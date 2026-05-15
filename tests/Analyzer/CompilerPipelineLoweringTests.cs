using System;
using Compiler.CodeGen;
using Compiler.Diagnostics;
using Compiler.Instantiation;
using Compiler.Postprocessing;
using Verification;
using Verification.Results;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Reprs;
using TypeModel.Symbols;
using TypeModel.Types;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for compiler pipeline lowering.
/// </summary>
public class CompilerPipelineLoweringTests
{
    /// <summary>
    /// Verifies semantic analysis behavior for tuple literal is lowered to creator expression.
    /// </summary>
    [Fact]
    public void Analyze_TupleLiteral_IsLoweredToCreatorExpression()
    {
        string source = """
                        routine test()
                          var pair = (1_s32, 2_s32)
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        Assert.False(condition: ContainsTupleLiteral(program: program));
    }

    /// <summary>
    /// Verifies semantic analysis behavior for direct routine call attaches lowering kind.
    /// </summary>
    [Fact]
    public void Analyze_DirectRoutineCall_AttachesLoweringKind()
    {
        string source = """
                        routine helper(value: S32) -> S32
                          return value

                        routine test() -> S32
                          return helper(1_s32)
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        CallExpression call = GetReturnedCall(program: program, routineName: "test");
        Assert.Equal(expected: CallLoweringKind.DirectRoutine, actual: call.LoweringKind);
        Assert.NotNull(@object: call.ResolvedRoutine);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for direct routine call attaches scalar backend repr.
    /// </summary>
    [Fact]
    public void Analyze_DirectRoutineCall_AttachesScalarBackendRepr()
    {
        string source = """
                        routine helper(value: S32) -> S32
                          return value

                        routine test() -> S32
                          return helper(1_s32)
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        CallExpression call = GetReturnedCall(program: program, routineName: "test");
        Assert.NotNull(@object: call.ResolvedRepr);
        Assert.Equal(expected: BackendReprKind.Scalar, actual: call.ResolvedRepr!.Kind);
        Assert.Equal(expected: "i32", actual: call.ResolvedRepr.LlvmAbiType);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for type constructor call attaches lowering kind and constructed type.
    /// </summary>
    [Fact]
    public void Analyze_TypeConstructorCall_AttachesLoweringKindAndConstructedType()
    {
        string source = """
                        import Collections.BitList

                        routine test()
                          var bits = BitList()
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        CallExpression call = GetVariableInitializerCall(program: program, variableName: "bits");
        Assert.Equal(expected: CallLoweringKind.TypeConstructor, actual: call.LoweringKind);
        Assert.Equal(expected: "BitList", actual: call.ConstructedType?.Name);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for generic type constructor preserves lowering kind through lowering.
    /// </summary>
    [Fact]
    public void Analyze_GenericTypeConstructor_PreservesLoweringKindThroughLowering()
    {
        string source = """
                        routine test()
                          var maybe = Maybe[S32](present: true, value: 1_s32)
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        CreatorExpression creator = GetVariableInitializerCreator(program: program,
            variableName: "maybe");
        Assert.Equal(expected: CallLoweringKind.TypeConstructor, actual: creator.LoweringKind);
        Assert.Equal(expected: "Maybe[Core.S32]", actual: creator.ConstructedType?.Name);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for generic free routine lowers using resolved routine.
    /// </summary>
    [Fact]
    public void Analyze_GenericFreeRoutine_LowersUsingResolvedRoutine()
    {
        string source = """
                        routine test()
                          var ptr = hijacked_none[S32]()
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        CallExpression call = GetVariableInitializerCall(program: program, variableName: "ptr");
        Assert.Equal(expected: CallLoweringKind.DirectRoutine, actual: call.LoweringKind);
        Assert.NotNull(@object: call.ResolvedRoutine);
        Assert.Equal(expected: "hijacked_none", actual: call.ResolvedRoutine!.Name);
        Assert.False(condition: call.ResolvedRoutine.IsGenericDefinition);
        Assert.NotNull(@object: call.ResolvedRepr);
        Assert.Equal(expected: BackendReprKind.WrapperRef, actual: call.ResolvedRepr!.Kind);
        Assert.Equal(expected: PointerFlavor.Hijacked, actual: call.ResolvedRepr.PointerFlavor);
        Assert.Equal(expected: "ptr", actual: call.ResolvedRepr.LlvmAbiType);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for monomorphized generic body is concrete before backend entry.
    /// </summary>
    [Fact]
    public void Analyze_MonomorphizedGenericBody_IsConcreteBeforeBackendEntry()
    {
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].peek() -> T
                          return me.value

                        routine test() -> S32
                          var box = Box[S32](value: 7_s32)
                          return box.peek()
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        MonomorphizedBody body = Assert.Single(
            collection: result.InstantiatedGenericBodies.Values.Where(candidate =>
                candidate.Info.Name == "peek"));

        Assert.False(condition: ContainsGenericPlaceholder(type: body.Info.OwnerType));
        Assert.DoesNotContain(body.Info.Parameters,
            param => ContainsGenericPlaceholder(type: param.Type));
        Assert.False(condition: ContainsGenericPlaceholder(type: body.Info.ReturnType));
    }

    /// <summary>
    /// Verifies semantic analysis behavior for monomorphized constructor call binds concrete resolved routine.
    /// </summary>
    [Fact]
    public void Analyze_MonomorphizedConstructorCall_BindsConcreteResolvedRoutine()
    {
        string source = """
                        record Box[T]
                          value: T

                        dangerous routine Box[T].none_ptr() -> Hijacked[T]
                          return Hijacked[T](0_addr)

                        dangerous routine test() -> Hijacked[S32]
                          var box = Box[S32](value: 7_s32)
                          return box.none_ptr()
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        MonomorphizedBody body = Assert.Single(
            collection: result.InstantiatedGenericBodies.Values.Where(candidate =>
                candidate.Info.Name == "none_ptr"));

        CallExpression call = GetReturnedCall(body.Ast.Body);
        Assert.NotNull(@object: call.ResolvedRoutine);
        Assert.False(condition: call.ResolvedRoutine!.OwnerType?.IsGenericDefinition ?? true);
        Assert.False(condition: ContainsGenericPlaceholder(type: call.ResolvedRoutine.OwnerType));
        Assert.False(condition: ContainsGenericPlaceholder(type: call.ConstructedType));
    }

    /// <summary>
    /// Verifies semantic analysis behavior for monomorphized method call has concrete return metadata.
    /// </summary>
    [Fact]
    public void Analyze_MonomorphizedMethodCall_HasConcreteReturnMetadata()
    {
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].peek() -> T
                          return me.value

                        routine Box[T].copy_value() -> T
                          return me.peek()

                        routine test() -> S32
                          var box = Box[S32](value: 7_s32)
                          return box.copy_value()
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        MonomorphizedBody body = Assert.Single(
            collection: result.InstantiatedGenericBodies.Values.Where(candidate =>
                candidate.Info.Name == "copy_value"));

        CallExpression call = GetReturnedCall(body.Ast.Body);
        Assert.False(condition: ContainsGenericPlaceholder(type: call.ResolvedType));
    }

    /// <summary>
    /// Verifies semantic analysis behavior for when expression becomes is lowered before codegen.
    /// </summary>
    [Fact]
    public void Analyze_WhenExpressionBecomes_IsLoweredBeforeCodegen()
    {
        string source = """
                        routine test(value: S32) -> S32
                          var result = when value
                            == 1 =>
                              var doubled = value * 2_s32
                              becomes doubled
                            else => 0_s32
                          return result
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        Assert.False(condition: ContainsBecomes(program: program));
    }

    /// <summary>
    /// Verifies semantic analysis behavior for lambda expression is lifted before codegen.
    /// </summary>
    [Fact]
    public void Analyze_LambdaExpression_IsLiftedBeforeCodegen()
    {
        string source = """
                        routine test() -> S32
                          var double_it = x => x * 2_s32
                          return 0_s32
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        Assert.False(condition: ContainsLambda(program: program));
    }

    /// <summary>
    /// Verifies code generation behavior for tuple literal lowering and emits the expected IR.
    /// </summary>
    [Fact]
    public void Codegen_TupleLiteralLowering_GeneratesIr()
    {
        string source = """
                        routine test()
                          var pair = (1_s32, 2_s32)
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            stdlibPrograms: result.Registry.StdlibPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "insertvalue", actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for priority queue dict literal and emits the expected IR.
    /// </summary>
    [Fact]
    public void Codegen_PriorityQueueDictLiteral_GeneratesIr()
    {
        string source = """
                        routine test()
                          var items: PriorityQueue[S64, Text] = {1: "high", 10: "low"}
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            stdlibPrograms: result.Registry.StdlibPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "PriorityQueue", actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for nested owned list literal and emits the expected IR.
    /// </summary>
    [Fact]
    public void Codegen_NestedOwnedListLiteral_GeneratesIr()
    {
        string source = """
                        routine test()
                          var items = [[1_s64, 2_s64, 3_s64], [4_s64, 5_s64], [6_s64], []]
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        RoutineDeclaration routine = program.Declarations
                                            .OfType<RoutineDeclaration>()
                                            .Single(predicate: declaration =>
                                                 declaration.Name == "test");
        BlockStatement body = Assert.IsType<BlockStatement>(routine.Body);
        VariableDeclaration variable = body.Statements
                                           .OfType<DeclarationStatement>()
                                           .Select(selector: statement => statement.Declaration)
                                           .OfType<VariableDeclaration>()
                                           .Single(predicate: declaration =>
                                                declaration.Name == "items");

        Assert.Equal(expected: "Core.Owned[Core.List[Core.Owned[Core.List[Core.S64]]]]",
            actual: variable.Initializer?.ResolvedType?.FullName);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            stdlibPrograms: result.Registry.StdlibPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "Core.List[Core.Owned[Core.List[Core.S64]]].$create",
            actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for try floordiv variant uses failable operator symbols.
    /// </summary>
    [Fact]
    public void Codegen_TryFloordivVariant_UsesFailableOperatorSymbols()
    {
        string source = """
                        routine test()
                          var value = 7_s32.try_floordiv(2_s32)
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            stdlibPrograms: result.Registry.StdlibPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "\"Core.S32.$sub!\"", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "\"Core.S32.$add!\"", actualString: llvmIr);
        Assert.DoesNotContain(expectedSubstring: "declare void @Core.S32.$sub",
            actualString: llvmIr);
        Assert.DoesNotContain(expectedSubstring: "declare void @Core.S32.$add",
            actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for lambda lift and emits the expected IR.
    /// </summary>
    [Fact]
    public void Codegen_LambdaLift_GeneratesIr()
    {
        string source = """
                        var global_factor = 100_s32

                        routine test() -> S32
                          var scale = x => x * global_factor
                          return 0_s32
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        Assert.False(condition: ContainsLambda(program: program));

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            stdlibPrograms: result.Registry.StdlibPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "__lambda_", actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for stdlib bit list add last and emits the expected definition.
    /// </summary>
    [Fact]
    public void Codegen_StdlibBitListAddLast_IsDefined()
    {
        string source = """
                        import Collections.BitList

                        routine test()
                          var bits = BitList()
                          bits.add_last(true)
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            stdlibPrograms: result.Registry.StdlibPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "define void @Collections.BitList.add_last",
            actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for byte size allocator wrapper passes scalar abi to raw c function.
    /// </summary>
    [Fact]
    public void Codegen_ByteSizeAllocatorWrapper_PassesScalarAbiToRawCFunction()
    {
        string source = """
                        import Collections.List

                        routine test()
                          var items = List[S64]()
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            stdlibPrograms: result.Registry.StdlibPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "call i64 @rf_allocate_dynamic_uninit(i64 ",
            actualString: llvmIr);
        Assert.DoesNotContain(expectedSubstring: "call i64 @rf_allocate_dynamic_uninit({ i64 }",
            actualString: llvmIr);
        Assert.DoesNotContain(
            expectedSubstring: "call i64 @rf_allocate_dynamic_uninit(%Record.ByteSize",
            actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for concrete LLVM intrinsic call substitutes template type arguments.
    /// </summary>
    [Fact]
    public void Codegen_ConcreteLlvmIntrinsicCall_SubstitutesTemplateTypeArguments()
    {
        string source = """
                        routine test() -> S64
                          return 1_s64 +% 2_s64
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            stdlibPrograms: result.Registry.StdlibPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "add i64", actualString: llvmIr);
        Assert.DoesNotContain(expectedSubstring: "add {T}", actualString: llvmIr);
        Assert.DoesNotContain(expectedSubstring: "{From}", actualString: llvmIr);
        Assert.DoesNotContain(expectedSubstring: "{To}", actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for bit list to U8 uses concrete hijacked U64 extract.
    /// </summary>
    [Fact]
    public void Codegen_BitListToU8_UsesConcreteHijackedU64Extract()
    {
        string source = """
                        import Collections.BitList

                        routine test(bits: BitList) -> U8!
                          return bits.to_u8!()
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            stdlibPrograms: result.Registry.StdlibPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);

        string llvmIr = generator.Generate();
        string tryToU8Body = ExtractFunctionDefinition(llvmIr: llvmIr,
            functionMarker: "define %\"Record.Maybe[Core.U8]\" @Collections.BitList.try_to_u8");
        Assert.Contains(expectedSubstring: "call i64 @\"Core.Hijacked[Core.U64].extract\"",
            actualString: tryToU8Body);
        Assert.DoesNotContain(expectedSubstring: "@\"Core.Hijacked[Core.Bytes].extract\"",
            actualString: tryToU8Body);
        Assert.DoesNotContain(expectedSubstring: "@Core.Bytes.$bitand", actualString: tryToU8Body);
    }

    /// <summary>
    /// Verifies code generation behavior for overloaded try create variants get distinct mangled names.
    /// </summary>
    [Fact]
    public void Codegen_OverloadedTryCreateVariants_GetDistinctMangledNames()
    {
        string source = """
                        routine test()
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        TypeInfo? s64Type = result.Registry.LookupType(name: "S64");
        TypeInfo? s8Type = result.Registry.LookupType(name: "S8");
        TypeInfo? textType = result.Registry.LookupType(name: "Text");
        TypeInfo? maybeDef = result.Registry.LookupType(name: "Maybe");
        Assert.NotNull(@object: s64Type);
        Assert.NotNull(@object: s8Type);
        Assert.NotNull(@object: textType);
        Assert.NotNull(@object: maybeDef);
        TypeInfo maybeS64 = result.Registry.GetOrCreateResolution(genericDef: maybeDef,
            typeArguments: [s64Type]);

        string fromS8 = LlvmCodeGenerator.MangleRoutineName(new RoutineInfo(name: "try_create")
        {
            OwnerType = s64Type,
            Parameters = [new ParameterInfo("from", s8Type)],
            ReturnType = maybeS64,
            OriginalName = "$create",
            IsSynthesized = true
        });

        string fromText = LlvmCodeGenerator.MangleRoutineName(new RoutineInfo(name: "try_create")
        {
            OwnerType = s64Type,
            Parameters = [new ParameterInfo("from_text", textType)],
            ReturnType = maybeS64,
            OriginalName = "$create",
            IsSynthesized = true
        });

        Assert.Equal(expected: "\"Core.S64.try_create(Core.S8)\"", actual: fromS8);
        Assert.Equal(expected: "\"Core.S64.try_create(Core.Text)\"", actual: fromText);
        Assert.NotEqual(expected: fromS8, actual: fromText);
    }

    /// <summary>
    /// Verifies code generation behavior for generic hijack does not emit bare create symbol.
    /// </summary>
    [Fact]
    public void Codegen_GenericHijack_DoesNotEmitBareCreateSymbol()
    {
        string source = """
                        dangerous routine wrap[T](value: T) -> Hijacked[T]
                          return value.hijack()

                        dangerous routine test(value: S64) -> Hijacked[S64]
                          return wrap[S64](value)
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            stdlibPrograms: result.Registry.StdlibPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);

        string llvmIr = generator.Generate();
        Assert.DoesNotContain(expectedSubstring: "call void @$create", actualString: llvmIr);
        Assert.DoesNotContain(expectedSubstring: "call ptr @$create", actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for generic hijacked from does not emit bare module symbol.
    /// </summary>
    [Fact]
    public void Codegen_GenericHijackedFrom_DoesNotEmitBareModuleSymbol()
    {
        string source = """
                        dangerous routine test() -> Hijacked[S64]
                          return hijacked_from[S64](0_addr)
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            stdlibPrograms: result.Registry.StdlibPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);

        string llvmIr = generator.Generate();
        Assert.DoesNotContain(expectedSubstring: "call ptr @Core.hijacked_from(",
            actualString: llvmIr);
        Assert.Contains(expectedSubstring: "define ptr @\"Core.hijacked_from(S64)\"(i64 %addr)",
            actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for chained method call on call receiver and emits the expected IR.
    /// </summary>
    [Fact]
    public void Codegen_ChainedMethodCall_OnCallReceiver_GeneratesIr()
    {
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].peek() -> T
                          return me.value

                        routine make_box() -> Box[S32]
                          return Box[S32](value: 7_s32)

                        routine test() -> S32
                          return make_box().peek()
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            stdlibPrograms: result.Registry.StdlibPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "define i32 @test()", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "@\"Box[Core.S32].peek\"", actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for wrapper method call uses concrete generic return type.
    /// </summary>
    [Fact]
    public void Codegen_WrapperMethodCall_UsesConcreteGenericReturnType()
    {
        string source = """
                        dangerous routine test(ptr: Hijacked[S64]) -> S64
                          return ptr.extract()
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            stdlibPrograms: result.Registry.StdlibPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "define i64 @test(ptr %ptr)", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "@\"Core.Hijacked[Core.S64].extract\"",
            actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for owned wrapper forwarder uses concrete inner calls.
    /// </summary>
    [Fact]
    public void Codegen_OwnedWrapperForwarder_UsesConcreteInnerCalls()
    {
        string source = """
                        import Collections.SortedDict

                        routine test(node: Owned[BTreeDictNode[S64, S64]]) -> S64
                          return node.key_get(0_u64)
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            stdlibPrograms: result.Registry.StdlibPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);

        string llvmIr = generator.Generate();
        string forwarderBody = ExtractFunctionDefinition(llvmIr: llvmIr,
            functionMarker:
            "define i64 @\"Core.Owned[Collections.BTreeDictNode[Core.S64, Core.S64]].key_get\"");

        Assert.Contains(
            expectedSubstring:
            "@\"Core.Hijacked[Collections.BTreeDictNode[Core.S64, Core.S64]].reveal\"",
            actualString: forwarderBody);
        Assert.Contains(
            expectedSubstring: "@\"Collections.BTreeDictNode[Core.S64, Core.S64].key_get\"",
            actualString: forwarderBody);
        Assert.DoesNotContain(expectedSubstring: "@\"Core.Hijacked[T].reveal\"",
            actualString: forwarderBody);
        Assert.DoesNotContain(expectedSubstring: "@\"Collections.BTreeDictNode[K, V].key_get\"",
            actualString: forwarderBody);
    }

    /// <summary>
    /// Verifies code generation behavior for method conversion call uses semantic return type.
    /// </summary>
    [Fact]
    public void Codegen_MethodConversionCall_UsesSemanticReturnType()
    {
        string source = """
                        routine helper(value: S32) -> S32
                          return value

                        routine test(text: Text) -> S32
                          return helper(text.count().S32())
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            stdlibPrograms: result.Registry.StdlibPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "define i32 @test(", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "trunc i64", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "call i32 @helper(i32 ", actualString: llvmIr);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for stdlib variant bodies attach constructor metadata.
    /// </summary>
    [Fact]
    public void Analyze_StdlibVariantBodies_AttachConstructorMetadata()
    {
        string source = """
                        routine test() -> Blank
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var matchingBodies = result.SynthesizedBodies
                                   .Where(pair =>
                                        pair.Key.Contains("BytesUtf8Iterator.try_next",
                                            StringComparison.Ordinal) ||
                                        pair.Key.Contains("BytesUtf8Iterator.lookup_next",
                                            StringComparison.Ordinal))
                                   .ToList();

        Assert.NotEmpty(collection: matchingBodies);
        foreach ((string key, Statement body) in matchingBodies)
        {
            var ctorCalls = FindCalls(statement: body)
                           .Where(call => call.Callee is IdentifierExpression
                            {
                                Name: "Character"
                            })
                           .ToList();

            Assert.NotEmpty(collection: ctorCalls);
            Assert.All(ctorCalls,
                ctorCall =>
                {
                    Assert.True(
                        condition: ctorCall.LoweringKind == CallLoweringKind.TypeConstructor,
                        userMessage: $"{key} contains unclassified Character(...)");
                    Assert.NotNull(@object: ctorCall.ConstructedType);
                });
        }
    }

    /// <summary>
    /// Verifies code generation behavior for stdlib variant bodies do not warn about missing character constructor metadata.
    /// </summary>
    [Fact]
    public void Codegen_StdlibVariantBodies_DoNotWarnAboutMissingCharacterConstructorMetadata()
    {
        string source = """
                        routine helper(value: S32) -> S32
                          return value

                        routine test!(text: Text) -> S32
                          return helper(text.count().S32!())
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            stdlibPrograms: result.Registry.StdlibPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);

        TextWriter originalError = Console.Error;
        var errorWriter = new StringWriter();
        Console.SetError(newError: errorWriter);
        try
        {
            _ = generator.Generate();
        }
        finally
        {
            Console.SetError(newError: originalError);
        }

        string warnings = errorWriter.ToString();
        Assert.DoesNotContain(
            expectedSubstring:
            "Warning: Synthesized codegen failed for 'Core.BytesUtf8Iterator.try_next'",
            actualString: warnings);
        Assert.DoesNotContain(
            expectedSubstring:
            "Warning: Synthesized codegen failed for 'Core.BytesUtf8Iterator.lookup_next'",
            actualString: warnings);
    }

    /// <summary>
    /// Verifies backend entry validation behavior for entry validator rejects residual preset identifier.
    /// </summary>
    [Fact]
    public void BackendEntryValidator_RejectsResidualPresetIdentifier()
    {
        string source = """
                        preset LIMIT: S64 = 10_s64

                        routine test() -> S64
                          return LIMIT
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var leakedPreset = new ReturnStatement(
            Value: new IdentifierExpression(Name: "LIMIT", Location: program.Location)
            {
                ResolvedType = result.Registry.LookupType(name: "S64")
            },
            Location: program.Location);

        var validator = new BackendEntryValidator(registry: result.Registry);
        IReadOnlyList<SemanticError> errors = validator.ValidateStatement(statement: leakedPreset);
        Assert.Contains(collection: errors,
            filter: error => error.Code == SemanticDiagnosticCode.IllegalBackendPresetIdentifier);
    }

    /// <summary>
    /// Verifies backend entry validation behavior for entry validator rejects constructor like call without lowering metadata.
    /// </summary>
    [Fact]
    public void BackendEntryValidator_RejectsConstructorLikeCallWithoutLoweringMetadata()
    {
        string source = """
                        routine test() -> Character
                          return Character(65_u32)
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        TypeInfo characterType = Assert.IsType<RecordTypeInfo>(
            result.Registry.LookupType(name: "Character"));
        var leakedCall = new CallExpression(
            Callee: new IdentifierExpression(Name: "Character", Location: program.Location),
            Arguments:
            [
                new LiteralExpression(Value: "65_u32",
                    LiteralType: Compiler.Lexer.TokenType.U32Literal,
                    Location: program.Location)
            ],
            Location: program.Location) { ResolvedType = characterType };

        var leakedReturn = new ReturnStatement(Value: leakedCall, Location: program.Location);
        var validator = new BackendEntryValidator(registry: result.Registry);
        IReadOnlyList<SemanticError> errors = validator.ValidateStatement(statement: leakedReturn);
        Assert.Contains(collection: errors,
            filter: error => error.Code == SemanticDiagnosticCode.MissingCallLoweringMetadata);
    }

    /// <summary>
    /// Verifies backend entry validation behavior for entry validator rejects residual index without concrete type.
    /// </summary>
    [Fact]
    public void BackendEntryValidator_RejectsResidualIndexWithoutConcreteType()
    {
        string source = """
                        routine test() -> Blank
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var leakedIndex = new IndexExpression(
            Object: new IdentifierExpression(Name: "items", Location: program.Location),
            Index: new LiteralExpression(Value: "0_s64",
                LiteralType: Compiler.Lexer.TokenType.S64Literal,
                Location: program.Location),
            Location: program.Location) { ResolvedType = new GenericParameterTypeInfo(name: "T") };

        var leakedReturn = new ReturnStatement(Value: leakedIndex, Location: program.Location);
        var validator = new BackendEntryValidator(registry: result.Registry);
        IReadOnlyList<SemanticError> errors = validator.ValidateStatement(statement: leakedReturn);
        Assert.Contains(collection: errors,
            filter: error => error.Code == SemanticDiagnosticCode.UnresolvedBackendGeneric);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for dict index call attaches concrete value type.
    /// </summary>
    [Fact]
    public void Analyze_DictIndexCall_AttachesConcreteValueType()
    {
        string source = """
                        routine test(dict: Dict[S64, S64], key: S64) -> S64
                          return dict[key]
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        RoutineDeclaration testRoutine = program.Declarations
                                                .OfType<RoutineDeclaration>()
                                                .Single(predicate: declaration =>
                                                     declaration.Name == "test");
        var body = Assert.IsType<BlockStatement>(testRoutine.Body);
        var returnStatement = Assert.IsType<ReturnStatement>(body.Statements.Single());
        var call = Assert.IsType<CallExpression>(returnStatement.Value);
        TypeInfo resolvedType = call.ResolvedType!;
        Assert.NotNull(@object: resolvedType);
        Assert.Equal(expected: "S64", actual: resolvedType.Name);
        Assert.False(condition: ContainsGenericPlaceholder(type: resolvedType));
    }

    /// <summary>
    /// Verifies backend entry validation behavior for entry validator rejects direct routine call without resolved metadata.
    /// </summary>
    [Fact]
    public void BackendEntryValidator_RejectsDirectRoutineCallWithoutResolvedMetadata()
    {
        string source = """
                        routine helper(value: S32) -> S32
                          return value

                        routine test() -> S32
                          return helper(1_s32)
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var leakedCall = new CallExpression(
            Callee: new IdentifierExpression(Name: "helper", Location: program.Location),
            Arguments:
            [
                new LiteralExpression(Value: "1_s32",
                    LiteralType: Compiler.Lexer.TokenType.S32Literal,
                    Location: program.Location)
            ],
            Location: program.Location);

        var leakedReturn = new ReturnStatement(Value: leakedCall, Location: program.Location);
        var validator = new BackendEntryValidator(registry: result.Registry);
        IReadOnlyList<SemanticError> errors = validator.ValidateStatement(statement: leakedReturn);
        Assert.Contains(collection: errors,
            filter: error => error.Code == SemanticDiagnosticCode.MissingCallLoweringMetadata);
    }

    /// <summary>
    /// Verifies code generation behavior for const generic preset type argument uses resolved type expression metadata.
    /// </summary>
    [Fact]
    public void Codegen_ConstGenericPresetTypeArgument_UsesResolvedTypeExpressionMetadata()
    {
        string source = """
                        preset WIDTH: Address = 16addr

                        entity Buffer[T, N]
                        needs N is Address
                          data: T

                        routine Buffer[T, N].first() -> T
                          return me.data

                        routine test(buf: Buffer[U8, WIDTH]) -> U8
                          return buf.first()
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        RoutineDeclaration testRoutine = program.Declarations
                                                .OfType<RoutineDeclaration>()
                                                .Single(predicate: declaration =>
                                                     declaration.Name == "test");
        TypeExpression parameterType =
            Assert.IsType<TypeExpression>(testRoutine.Parameters[0].Type);
        TypeExpression widthArg =
            Assert.IsType<TypeExpression>(parameterType.GenericArguments![1]);
        ConstGenericValueTypeInfo resolvedWidth =
            Assert.IsType<ConstGenericValueTypeInfo>(widthArg.ResolvedType);
        Assert.Equal(expected: 16, actual: resolvedWidth.Value);
        Assert.Equal(expected: "Address", actual: resolvedWidth.ExplicitTypeName);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            stdlibPrograms: result.Registry.StdlibPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "define i8 @test(", actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for typewise builder service method uses semantic receiver type.
    /// </summary>
    [Fact]
    public void Codegen_TypewiseBuilderServiceMethod_UsesSemanticReceiverType()
    {
        string source = """
                        import BuilderService

                        routine test() -> ByteSize
                          return S64.data_size()
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            stdlibPrograms: result.Registry.StdlibPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "define %Record.ByteSize @test(", actualString: llvmIr);
    }

    /// <summary>
    /// Verifies code generation behavior for monomorphized generic body emits standalone generic helper definition.
    /// </summary>
    [Fact]
    public void Codegen_MonomorphizedGenericBody_EmitsStandaloneGenericHelperDefinition()
    {
        string source = """
                        dangerous routine wrap_addr[T](addr: Address) -> Hijacked[T]
                          return hijacked_from[T](addr)

                        dangerous routine test() -> Hijacked[S64]
                          return wrap_addr[S64](0_addr)
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            stdlibPrograms: result.Registry.StdlibPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "define ptr @\"wrap_addr(S64)\"(i64 %addr)",
            actualString: llvmIr);
        Assert.Contains(expectedSubstring: "define ptr @\"Core.hijacked_from(S64)\"(i64 %addr)",
            actualString: llvmIr);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for builder service universal methods do not explode wrapper targets.
    /// </summary>
    [Fact]
    public void Analyze_BuilderServiceUniversalMethods_DoNotExplodeWrapperTargets()
    {
        string source = """
                        import BuilderService

                        routine test() -> U64
                          return S64.routine_info().count()
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        Assert.DoesNotContain(result.Registry.AllConcreteGenericInstances,
            type => type.FullName.Contains(
                "Core.Hijacked[Core.Hijacked[Core.Hijacked[Core.Hijacked[",
                StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies semantic analysis behavior for universal owner method is monomorphized on demand.
    /// </summary>
    [Fact]
    public void Analyze_UniversalOwnerMethod_IsMonomorphizedOnDemand()
    {
        string source = """
                        dangerous routine test(value: S64) -> Hijacked[S64]
                          return value.hijack()
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        RoutineInfo resolvedHijack = Assert.Single(result.Registry.GetAllRoutineResolutions(),
            routine => routine.BaseName == "S64.hijack");
        Assert.Contains(result.InstantiatedGenericBodies.Values,
            body => body.Info.RegistryKey == resolvedHijack.RegistryKey &&
                    !ContainsGenericPlaceholder(type: body.Info.ReturnType));
    }

    private static bool ContainsTupleLiteral(Program program)
    {
        return program.Declarations
                      .OfType<RoutineDeclaration>()
                      .Any(predicate: routine => ContainsTupleLiteral(statement: routine.Body));
    }

    private static string ExtractFunctionDefinition(string llvmIr, string functionMarker)
    {
        int start =
            llvmIr.IndexOf(value: functionMarker, comparisonType: StringComparison.Ordinal);
        Assert.True(condition: start >= 0,
            userMessage: $"Function marker not found: {functionMarker}");

        int next = llvmIr.IndexOf(value: "\ndefine ",
            startIndex: start + functionMarker.Length,
            comparisonType: StringComparison.Ordinal);
        return next >= 0
            ? llvmIr[start..next]
            : llvmIr[start..];
    }

    private static CallExpression GetReturnedCall(Program program, string routineName)
    {
        RoutineDeclaration routine = program.Declarations
                                            .OfType<RoutineDeclaration>()
                                            .Single(predicate: declaration =>
                                                 declaration.Name == routineName);
        return GetReturnedCall(routine.Body);
    }

    private static CallExpression GetReturnedCall(Statement body)
    {
        BlockStatement block = Assert.IsType<BlockStatement>(body);
        ReturnStatement ret = Assert.IsType<ReturnStatement>(block.Statements.Last());
        return Assert.IsType<CallExpression>(ret.Value);
    }

    private static CallExpression GetVariableInitializerCall(Program program, string variableName)
    {
        RoutineDeclaration routine = program.Declarations
                                            .OfType<RoutineDeclaration>()
                                            .Single(predicate: declaration =>
                                                 declaration.Name == "test");
        BlockStatement block = Assert.IsType<BlockStatement>(routine.Body);
        VariableDeclaration variable = block.Statements
                                            .OfType<DeclarationStatement>()
                                            .Select(selector: declaration =>
                                                 declaration.Declaration)
                                            .OfType<VariableDeclaration>()
                                            .Single(predicate: declaration =>
                                                 declaration.Name == variableName);
        return Assert.IsType<CallExpression>(variable.Initializer);
    }

    private static CreatorExpression GetVariableInitializerCreator(Program program,
        string variableName)
    {
        RoutineDeclaration routine = program.Declarations
                                            .OfType<RoutineDeclaration>()
                                            .Single(predicate: declaration =>
                                                 declaration.Name == "test");
        BlockStatement block = Assert.IsType<BlockStatement>(routine.Body);
        VariableDeclaration variable = block.Statements
                                            .OfType<DeclarationStatement>()
                                            .Select(selector: declaration =>
                                                 declaration.Declaration)
                                            .OfType<VariableDeclaration>()
                                            .Single(predicate: declaration =>
                                                 declaration.Name == variableName);
        return Assert.IsType<CreatorExpression>(variable.Initializer);
    }

    private static bool ContainsTupleLiteral(Statement statement)
    {
        return statement switch
        {
            BlockStatement block => block.Statements.Any(ContainsTupleLiteral),
            DeclarationStatement { Declaration: VariableDeclaration { Initializer: { } init } } =>
                ContainsTupleLiteral(expression: init),
            AssignmentStatement assign => ContainsTupleLiteral(expression: assign.Target) ||
                                          ContainsTupleLiteral(expression: assign.Value),
            ReturnStatement { Value: { } value } => ContainsTupleLiteral(expression: value),
            ExpressionStatement exprStmt => ContainsTupleLiteral(expression: exprStmt.Expression),
            IfStatement ifs => ContainsTupleLiteral(expression: ifs.Condition) ||
                               ContainsTupleLiteral(statement: ifs.ThenStatement) ||
                               ifs.ElseStatement != null &&
                               ContainsTupleLiteral(statement: ifs.ElseStatement),
            LoopStatement loop => ContainsTupleLiteral(statement: loop.Body),
            WhenStatement whenStmt => ContainsTupleLiteral(expression: whenStmt.Expression) ||
                                      whenStmt.Clauses.Any(predicate: clause =>
                                          ContainsTupleLiteral(statement: clause.Body)),
            _ => false
        };
    }

    private static bool ContainsTupleLiteral(Expression expression)
    {
        return expression switch
        {
            TupleLiteralExpression => true,
            CreatorExpression creator => creator.MemberVariables.Any(predicate: mv =>
                ContainsTupleLiteral(expression: mv.Value)),
            CallExpression call => ContainsTupleLiteral(expression: call.Callee) ||
                                   call.Arguments.Any(ContainsTupleLiteral),
            BinaryExpression binary => ContainsTupleLiteral(expression: binary.Left) ||
                                       ContainsTupleLiteral(expression: binary.Right),
            UnaryExpression unary => ContainsTupleLiteral(expression: unary.Operand),
            MemberExpression member => ContainsTupleLiteral(expression: member.Object),
            ConditionalExpression conditional =>
                ContainsTupleLiteral(expression: conditional.Condition) ||
                ContainsTupleLiteral(expression: conditional.TrueExpression) ||
                ContainsTupleLiteral(expression: conditional.FalseExpression),
            _ => false
        };
    }

    private static bool ContainsBecomes(Program program)
    {
        return program.Declarations
                      .OfType<RoutineDeclaration>()
                      .Any(predicate: routine => ContainsBecomes(statement: routine.Body));
    }

    private static bool ContainsLambda(Program program)
    {
        return program.Declarations
                      .OfType<RoutineDeclaration>()
                      .Any(predicate: routine => ContainsLambda(statement: routine.Body));
    }

    private static bool ContainsBecomes(Statement statement)
    {
        return statement switch
        {
            BecomesStatement => true,
            BlockStatement block => block.Statements.Any(ContainsBecomes),
            IfStatement ifs => ContainsBecomes(statement: ifs.ThenStatement) ||
                               ifs.ElseStatement != null &&
                               ContainsBecomes(statement: ifs.ElseStatement),
            LoopStatement loop => ContainsBecomes(statement: loop.Body),
            WhenStatement whenStmt => whenStmt.Clauses.Any(predicate: clause =>
                ContainsBecomes(statement: clause.Body)),
            DangerStatement danger => ContainsBecomes(statement: danger.Body),
            UsingStatement usingStmt => ContainsBecomes(statement: usingStmt.Body),
            _ => false
        };
    }

    private static bool ContainsLambda(Statement statement)
    {
        return statement switch
        {
            BlockStatement block => block.Statements.Any(ContainsLambda),
            DeclarationStatement { Declaration: VariableDeclaration { Initializer: { } init } } =>
                ContainsLambda(expression: init),
            AssignmentStatement assign => ContainsLambda(expression: assign.Target) ||
                                          ContainsLambda(expression: assign.Value),
            ReturnStatement { Value: { } value } => ContainsLambda(expression: value),
            ExpressionStatement exprStmt => ContainsLambda(expression: exprStmt.Expression),
            IfStatement ifs => ContainsLambda(expression: ifs.Condition) ||
                               ContainsLambda(statement: ifs.ThenStatement) ||
                               ifs.ElseStatement != null &&
                               ContainsLambda(statement: ifs.ElseStatement),
            WhileStatement whileStmt => ContainsLambda(expression: whileStmt.Condition) ||
                                        ContainsLambda(statement: whileStmt.Body) ||
                                        whileStmt.ElseBranch != null &&
                                        ContainsLambda(statement: whileStmt.ElseBranch),
            LoopStatement loop => ContainsLambda(statement: loop.Body),
            ForStatement forStmt => ContainsLambda(expression: forStmt.Iterable) ||
                                    ContainsLambda(statement: forStmt.Body) ||
                                    forStmt.ElseBranch != null &&
                                    ContainsLambda(statement: forStmt.ElseBranch),
            WhenStatement whenStmt => ContainsLambda(expression: whenStmt.Expression) ||
                                      whenStmt.Clauses.Any(predicate: clause =>
                                          ContainsLambda(statement: clause.Body)),
            DangerStatement danger => ContainsLambda(statement: danger.Body),
            UsingStatement usingStmt => ContainsLambda(expression: usingStmt.Resource) ||
                                        ContainsLambda(statement: usingStmt.Body),
            DiscardStatement discard => ContainsLambda(expression: discard.Expression),
            ThrowStatement throwStmt => ContainsLambda(expression: throwStmt.Error),
            BecomesStatement becomes => ContainsLambda(expression: becomes.Value),
            _ => false
        };
    }

    private static bool ContainsLambda(Expression expression)
    {
        return expression switch
        {
            LambdaExpression => true,
            BinaryExpression binary => ContainsLambda(expression: binary.Left) ||
                                       ContainsLambda(expression: binary.Right),
            UnaryExpression unary => ContainsLambda(expression: unary.Operand),
            CallExpression call => ContainsLambda(expression: call.Callee) ||
                                   call.Arguments.Any(ContainsLambda),
            MemberExpression member => ContainsLambda(expression: member.Object),
            OptionalMemberExpression member => ContainsLambda(expression: member.Object),
            IndexExpression index => ContainsLambda(expression: index.Object) ||
                                     ContainsLambda(expression: index.Index),
            ConditionalExpression conditional =>
                ContainsLambda(expression: conditional.Condition) ||
                ContainsLambda(expression: conditional.TrueExpression) ||
                ContainsLambda(expression: conditional.FalseExpression),
            CreatorExpression creator => creator.MemberVariables.Any(predicate: mv =>
                ContainsLambda(expression: mv.Value)),
            GenericMethodCallExpression generic => ContainsLambda(expression: generic.Object) ||
                                                   generic.Arguments.Any(ContainsLambda),
            NamedArgumentExpression named => ContainsLambda(expression: named.Value),
            WithExpression withExpr => ContainsLambda(expression: withExpr.Base) ||
                                       withExpr.Updates.Any(predicate: update =>
                                           ContainsLambda(expression: update.Value) ||
                                           update.Index != null &&
                                           ContainsLambda(expression: update.Index)),
            ListLiteralExpression list => list.Elements.Any(ContainsLambda),
            SetLiteralExpression set => set.Elements.Any(ContainsLambda),
            DictLiteralExpression dict => dict.Pairs.Any(predicate: pair =>
                ContainsLambda(expression: pair.Key) || ContainsLambda(expression: pair.Value)),
            TupleLiteralExpression tuple => tuple.Elements.Any(ContainsLambda),
            TypeConversionExpression conversion => ContainsLambda(
                expression: conversion.Expression),
            ChainedComparisonExpression chained => chained.Operands.Any(ContainsLambda),
            BlockExpression block => ContainsLambda(expression: block.Value),
            DictEntryLiteralExpression dictEntry => ContainsLambda(expression: dictEntry.Key) ||
                                                    ContainsLambda(expression: dictEntry.Value),
            IsPatternExpression isPattern => ContainsLambda(expression: isPattern.Expression),
            FlagsTestExpression flagsTest => ContainsLambda(expression: flagsTest.Subject),
            InsertedTextExpression inserted => inserted.Parts
                                                       .OfType<ExpressionPart>()
                                                       .Any(predicate: part =>
                                                            ContainsLambda(
                                                                expression: part.Expression)),
            StealExpression steal => ContainsLambda(expression: steal.Operand),
            WaitforExpression waitfor => ContainsLambda(expression: waitfor.Operand) ||
                                         waitfor.Timeout != null &&
                                         ContainsLambda(expression: waitfor.Timeout),
            DependentWaitforExpression dependent =>
                ContainsLambda(expression: dependent.Operand) ||
                dependent.Dependencies.Any(predicate: dep =>
                    ContainsLambda(expression: dep.DependencyExpr)) || dependent.Timeout != null &&
                ContainsLambda(expression: dependent.Timeout),
            CarrierPayloadExpression payload => ContainsLambda(expression: payload.Carrier),
            BackIndexExpression backIndex => ContainsLambda(expression: backIndex.Operand),
            WhenExpression whenExpr => whenExpr.Expression != null &&
                                       ContainsLambda(expression: whenExpr.Expression) ||
                                       whenExpr.Clauses.Any(predicate: clause =>
                                           ContainsLambda(statement: clause.Body)),
            _ => false
        };
    }

    private static IEnumerable<CallExpression> FindCalls(Statement statement)
    {
        switch (statement)
        {
            case BlockStatement block:
                foreach (Statement inner in block.Statements)
                foreach (CallExpression call in FindCalls(inner))
                    yield return call;
                yield break;
            case ReturnStatement { Value: { } value }:
                foreach (CallExpression call in FindCalls(value))
                    yield return call;
                yield break;
            case VariantReturnStatement { Value: { } value }:
                foreach (CallExpression call in FindCalls(value))
                    yield return call;
                yield break;
            case DeclarationStatement
            {
                Declaration: VariableDeclaration { Initializer: { } init }
            }:
                foreach (CallExpression call in FindCalls(init))
                    yield return call;
                yield break;
            case ExpressionStatement exprStmt:
                foreach (CallExpression call in FindCalls(exprStmt.Expression))
                    yield return call;
                yield break;
            case IfStatement ifStmt:
                foreach (CallExpression call in FindCalls(ifStmt.Condition))
                    yield return call;
                foreach (CallExpression call in FindCalls(ifStmt.ThenStatement))
                    yield return call;
                if (ifStmt.ElseStatement != null)
                    foreach (CallExpression call in FindCalls(ifStmt.ElseStatement))
                        yield return call;
                yield break;
            case WhenStatement whenStmt:
                if (whenStmt.Expression != null)
                {
                    foreach (CallExpression call in FindCalls(whenStmt.Expression))
                    {
                        yield return call;
                    }
                }

                foreach (WhenClause clause in whenStmt.Clauses)
                {
                    foreach (CallExpression call in FindCalls(clause.Body))
                    {
                        yield return call;
                    }
                }

                yield break;
        }
    }

    private static IEnumerable<CallExpression> FindCalls(Expression expression)
    {
        switch (expression)
        {
            case CallExpression call:
                yield return call;
                foreach (CallExpression nested in FindCalls(call.Callee))
                {
                    yield return nested;
                }

                foreach (Expression arg in call.Arguments)
                {
                    foreach (CallExpression nested in FindCalls(arg))
                    {
                        yield return nested;
                    }
                }

                yield break;
            case MemberExpression member:
                foreach (CallExpression nested in FindCalls(member.Object))
                {
                    yield return nested;
                }

                yield break;
            case NamedArgumentExpression named:
                foreach (CallExpression nested in FindCalls(named.Value))
                {
                    yield return nested;
                }

                yield break;
            case BinaryExpression binary:
                foreach (CallExpression nested in FindCalls(binary.Left))
                {
                    yield return nested;
                }

                foreach (CallExpression nested in FindCalls(binary.Right))
                {
                    yield return nested;
                }

                yield break;
            case UnaryExpression unary:
                foreach (CallExpression nested in FindCalls(unary.Operand))
                {
                    yield return nested;
                }

                yield break;
            case ConditionalExpression conditional:
                foreach (CallExpression nested in FindCalls(conditional.Condition))
                {
                    yield return nested;
                }

                foreach (CallExpression nested in FindCalls(conditional.TrueExpression))
                {
                    yield return nested;
                }

                foreach (CallExpression nested in FindCalls(conditional.FalseExpression))
                {
                    yield return nested;
                }

                yield break;
            case CreatorExpression creator:
                foreach ((string _, Expression value) in creator.MemberVariables)
                foreach (CallExpression nested in FindCalls(value))
                {
                    yield return nested;
                }

                yield break;
        }
    }

    private static bool ContainsGenericPlaceholder(TypeInfo? type)
    {
        if (type == null)
        {
            return false;
        }

        if (type is GenericParameterTypeInfo or ProtocolSelfTypeInfo)
        {
            return true;
        }

        if (type.IsGenericDefinition && type.TypeArguments is not { Count: > 0 })
        {
            return true;
        }

        if (type.TypeArguments is { Count: > 0 } &&
            type.TypeArguments.Any(ContainsGenericPlaceholder))
        {
            return true;
        }

        return type switch
        {
            WrapperTypeInfo wrapper => ContainsGenericPlaceholder(type: wrapper.InnerType),
            TupleTypeInfo tuple => tuple.ElementTypes.Any(ContainsGenericPlaceholder),
            VariantTypeInfo variant => variant.Members.Any(member =>
                member.Type != null && ContainsGenericPlaceholder(type: member.Type)),
            _ => false
        };
    }

    private static IEnumerable<IndexExpression> FindIndexExpressions(ISyntaxTreeNode node)
    {
        if (node is IndexExpression index)
        {
            yield return index;
        }

        foreach (ISyntaxTreeNode child in EnumerateChildren(node))
        {
            foreach (IndexExpression nested in FindIndexExpressions(child))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<ISyntaxTreeNode> EnumerateChildren(ISyntaxTreeNode node)
    {
        foreach (var property in node.GetType()
                                     .GetProperties(System.Reflection.BindingFlags.Instance |
                                                    System.Reflection.BindingFlags.Public))
        {
            if (!property.CanRead || property.GetIndexParameters()
                                             .Length != 0 || property.Name ==
                nameof(ISyntaxTreeNode.Location))
            {
                continue;
            }

            object? value = property.GetValue(node);
            switch (value)
            {
                case null:
                    continue;
                case ISyntaxTreeNode child:
                    yield return child;
                    break;
                case IEnumerable enumerable:
                    foreach (object? item in enumerable)
                    {
                        if (item is ISyntaxTreeNode astNode)
                        {
                            yield return astNode;
                        }
                    }

                    break;
            }
        }
    }
}
