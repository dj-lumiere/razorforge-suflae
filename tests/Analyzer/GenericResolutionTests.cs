using SemanticVerification.Results;
using SyntaxTree;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for generic type resolution bugs (S191, S192, S193).
/// </summary>
public class GenericResolutionTests
{
    #region S191 — Void return on generic method calls
    /// <summary>
    /// Tests Analyze_GenericVoidMethod_ReturnsBlank.
    /// </summary>

    [Fact]
    public void Analyze_GenericVoidMethod_ReturnsBlank()
    {
        // S191: Calling a void method on a generic resolution should return Blank, not <error>
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].clear()
                          return

                        routine test()
                          var b = Box[S32](value: 42)
                          b.clear()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region S192 — Double-generic method return type
    /// <summary>
    /// Tests Analyze_MethodLevelGenericReturnType_ResolvesCorrectly.
    /// </summary>

    [Fact]
    public void Analyze_MethodLevelGenericReturnType_ResolvesCorrectly()
    {
        // S192: Method-level generic params + owner-level generic params
        // convert[U](new_val: U) -> Box[U] should resolve Box[U] with the call-site type arg
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].convert[U](new_val: U) -> Box[U]
                          return Box[U](value: new_val)

                        routine test()
                          var b = Box[S32](value: 42)
                          var c: Box[Bool] = b.convert[Bool](true)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Tests Analyze_MethodLevelGenericReturnType_InfersWithoutAnnotation.
    /// </summary>

    [Fact]
    public void Analyze_MethodLevelGenericReturnType_InfersWithoutAnnotation()
    {
        // S192: Without explicit type annotation, var c should infer as Box[Bool]
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].convert[U](new_val: U) -> Box[U]
                          return Box[U](value: new_val)

                        routine test()
                          var b = Box[S32](value: 42)
                          var c = b.convert[Bool](true)
                          var d: Box[Bool] = c
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Tests Analyze_MethodLevelGenericDirectReturn_ResolvesCorrectly.
    /// </summary>

    [Fact]
    public void Analyze_MethodLevelGenericDirectReturn_ResolvesCorrectly()
    {
        // S192: Method returning U directly (not wrapped in owner type)
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].extract[U](val: U) -> U
                          return val

                        routine test()
                          var b = Box[S32](value: 42)
                          var x: Bool = b.extract[Bool](true)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region S193 — $eq on generic record types
    /// <summary>
    /// Tests Analyze_GenericRecordMethodLookup_WorksOnResolution.
    /// </summary>

    [Fact]
    public void Analyze_GenericRecordMethodLookup_WorksOnResolution()
    {
        // S193: Methods registered on generic def (Wrapper) should be found for Wrapper[S32]
        // LookupMethod falls back from Wrapper[S32] → Wrapper to find user-defined methods
        string source = """
                        record Wrapper[T]
                          value: T

                        routine Wrapper[T].unwrap() -> T
                          return me.value

                        routine test()
                          var a = Wrapper[S32](value: 1)
                          var v: S32 = a.unwrap()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region LookupMethod fully-resolved results
    /// <summary>
    /// Tests Analyze_GenericOwnerMethod_ParamTypeSubstituted.
    /// </summary>

    [Fact]
    public void Analyze_GenericOwnerMethod_ParamTypeSubstituted()
    {
        // LookupMethod on List[S32].$add should return param type S32 (not T)
        // After fix, no manual SubstituteTypeParameters needed at call site
        string source = """
                        record Pair[T]
                          first: T
                          second: T

                        routine Pair[T].swap_first(value: T) -> T
                          return me.first

                        routine test()
                          var p = Pair[S32](first: 1, second: 2)
                          var old: S32 = p.swap_first(value: 3)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Tests Analyze_GenericOwnerMethod_ReturnTypeSubstituted.
    /// </summary>

    [Fact]
    public void Analyze_GenericOwnerMethod_ReturnTypeSubstituted()
    {
        // LookupMethod on generic owner should substitute T in return type
        string source = """
                        record Container[T]
                          item: T

                        routine Container[T].get() -> T
                          return me.item

                        routine test()
                          var c = Container[Bool](item: true)
                          var v: Bool = c.get()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Tests Analyze_GenericOwnerMethod_NestedGenericSubstitution.
    /// </summary>

    [Fact]
    public void Analyze_GenericOwnerMethod_NestedGenericSubstitution()
    {
        // Dict[Text, List[S32]].$getitem should return List[S32], not T
        string source = """
                        record Mapping[K, V]
                          key: K
                          val: V

                        routine Mapping[K, V].get_val() -> V
                          return me.val

                        routine test()
                          var inner = Mapping[S32, Bool](key: 1, val: true)
                          var m = Mapping[Bool, Mapping[S32, Bool]](key: false, val: inner)
                          var result: Mapping[S32, Bool] = m.get_val()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region P1 — ResolvedRoutine stored for member calls

    [Fact]
    public void Analyze_MemberCallOnGenericResolution_StoresResolvedRoutine()
    {
        // P1: Member call on a generic resolution should store ResolvedRoutine with substituted types
        string source = """
                        record Pair[T]
                          first: T
                          second: T

                        routine Pair[T].swap_first(value: T) -> T
                          return me.first

                        routine test()
                          var p = Pair[S32](first: 1, second: 2)
                          var old = p.swap_first(value: 3)
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerification.SemanticAnalyzer(
            language: SemanticVerification.Enums.Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);
        Assert.Empty(collection: result.Errors);

        // Find the 'test' routine, get its body, find the call to swap_first
        var testRoutine = program.Declarations.OfType<RoutineDeclaration>()
                                 .First(predicate: r => r.Name == "test");
        var body = (BlockStatement)testRoutine.Body;
        // Statement 1 (index 1): var old = p.swap_first(value: 3)
        var declStmt = (DeclarationStatement)body.Statements[index: 1];
        var varDecl = (VariableDeclaration)declStmt.Declaration;
        var call = (CallExpression)varDecl.Initializer!;

        Assert.NotNull(@object: call.ResolvedRoutine);
        Assert.Equal(expected: "swap_first", actual: call.ResolvedRoutine!.Name);
        // Return type should be substituted: T → S32
        Assert.NotNull(@object: call.ResolvedRoutine.ReturnType);
        Assert.Equal(expected: "S32", actual: call.ResolvedRoutine.ReturnType!.Name);
    }

    [Fact]
    public void Analyze_VoidMemberCallOnGenericResolution_StoresResolvedRoutine()
    {
        // P1: Void method on generic resolution should still store ResolvedRoutine
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].clear()
                          return

                        routine test()
                          var b = Box[S32](value: 42)
                          b.clear()
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerification.SemanticAnalyzer(
            language: SemanticVerification.Enums.Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);
        Assert.Empty(collection: result.Errors);

        var testRoutine = program.Declarations.OfType<RoutineDeclaration>()
                                 .First(predicate: r => r.Name == "test");
        var body = (BlockStatement)testRoutine.Body;
        // Statement 1 (index 1): b.clear()
        var exprStmt = (ExpressionStatement)body.Statements[index: 1];
        var call = (CallExpression)exprStmt.Expression;

        Assert.NotNull(@object: call.ResolvedRoutine);
        Assert.Equal(expected: "clear", actual: call.ResolvedRoutine!.Name);
    }

    [Fact]
    public void Analyze_GenericMethodCall_StoresResolvedRoutine()
    {
        // P1: Generic method call (obj.method[U](args)) stores ResolvedRoutine
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].convert[U](new_val: U) -> Box[U]
                          return Box[U](value: new_val)

                        routine test()
                          var b = Box[S32](value: 42)
                          var c = b.convert[Bool](true)
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerification.SemanticAnalyzer(
            language: SemanticVerification.Enums.Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);
        Assert.Empty(collection: result.Errors);

        var testRoutine = program.Declarations.OfType<RoutineDeclaration>()
                                 .First(predicate: r => r.Name == "test");
        var body = (BlockStatement)testRoutine.Body;
        // Statement 1 (index 1): var c = b.convert[Bool](true)
        // After GenericCallLoweringPass, the node is lowered to a CallExpression with TypeArguments.
        var declStmt = (DeclarationStatement)body.Statements[index: 1];
        var varDecl = (VariableDeclaration)declStmt.Declaration;
        var call = (CallExpression)varDecl.Initializer!;

        Assert.NotNull(@object: call.ResolvedRoutine);
        Assert.Equal(expected: "convert", actual: call.ResolvedRoutine!.Name);
        Assert.NotNull(@object: call.TypeArguments);
        Assert.Single(call.TypeArguments!);
    }

    #endregion

    #region P2 — GenericDefinition preserved after type updates
    /// <summary>
    /// Tests Analyze_GenericRecord_PreservesDefinitionAfterMemberUpdate.
    /// </summary>

    [Fact]
    public void Analyze_GenericRecord_PreservesDefinitionAfterMemberUpdate()
    {
        // P2: After UpdateRecordMemberVariables, GenericDefinition must survive
        // so that method lookup on the resolved type can substitute T → S32
        string source = """
                        record Cell[T]
                          data: T

                        routine Cell[T].extract() -> T
                          return me.data

                        routine test()
                          var c = Cell[S32](data: 42)
                          var v: S32 = c.extract()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Tests Analyze_GenericEntity_PreservesDefinitionAfterMemberUpdate.
    /// </summary>

    [Fact]
    public void Analyze_GenericEntity_PreservesDefinitionAfterMemberUpdate()
    {
        // P2: Same test for entity types — GenericDefinition must survive update
        string source = """
                        entity Node[T]
                          value: T

                        routine Node[T].get_value() -> T
                          return me.value

                        routine test()
                          var n = Node[Bool](value: true)
                          var v: Bool = n.get_value()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region P1 — Protocol method lookup on instantiated generic protocols

    [Fact]
    public void Analyze_GenericProtocolMethod_SubstitutesReturnType()
    {
        // P1: Calling a protocol method on a type that obeys a generic protocol
        // should substitute T in the return type
        string source = """
                        protocol Supplier[T]
                          @readonly
                          routine Me.supply() -> T

                        record IntSupplier obeys Supplier[S32]
                          value: S32

                        @readonly
                        routine IntSupplier.supply() -> S32
                          return me.value

                        routine test()
                          var s = IntSupplier(value: 42)
                          var v: S32 = s.supply()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_GenericProtocolMethod_SubstitutesParamType()
    {
        // P1: Protocol method with generic parameter type should substitute correctly
        string source = """
                        protocol Acceptor[T]
                          routine Me.accept(item: T) -> Blank

                        record S32Acceptor obeys Acceptor[S32]
                          count: S32

                        routine S32Acceptor.accept(item: S32) -> Blank
                          return

                        routine test()
                          var a = S32Acceptor(count: 0)
                          a.accept(item: 42)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_GenericProtocol_MultiParam_SubstitutesCorrectly()
    {
        // P1: Generic protocol with multiple type parameters — both K and V
        // should be substituted in method signatures
        string source = """
                        protocol Mapper[K, V]
                          @readonly
                          routine Me.map_value(key: K) -> V

                        record IntToBoMapper obeys Mapper[S32, Bool]
                          flag: Bool

                        @readonly
                        routine IntToBoMapper.map_value(key: S32) -> Bool
                          return me.flag

                        routine test()
                          var m = IntToBoMapper(flag: true)
                          var v: Bool = m.map_value(key: 1)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region P4 — Routine body matching by resolved signature

    [Fact]
    public void Analyze_OverloadedRoutines_DifferentGenericParams_MatchCorrectBodies()
    {
        // P4: Two overloads with different generic parameter types
        // must each match their own body via resolved RegistryKey
        string source = """
                        record Box[T]
                          value: T

                        routine process(item: Box[S32]) -> S32
                          return item.value

                        routine process(item: Box[Bool]) -> Bool
                          return item.value

                        routine test()
                          var a = Box[S32](value: 42)
                          var b = Box[Bool](value: true)
                          var x: S32 = process(item: a)
                          var y: Bool = process(item: b)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_ExtensionRoutine_GenericOwner_MatchesBody()
    {
        // P4: Extension-syntax routine with generic owner resolves its body
        string source = """
                        record Wrapper[T]
                          inner: T

                        routine Wrapper[T].get_inner() -> T
                          return me.inner

                        routine test()
                          var w = Wrapper[S32](inner: 10)
                          var v: S32 = w.get_inner()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_OverloadedRoutines_PlainVsGenericParam_MatchCorrectBodies()
    {
        // P4: Overloads where one takes a plain type and another takes a generic resolution
        string source = """
                        record Pair[T]
                          first: T
                          second: T

                        routine describe(item: S32) -> S32
                          return item

                        routine describe(item: Pair[S32]) -> S32
                          return item.first

                        routine test()
                          var p = Pair[S32](first: 1, second: 2)
                          var a: S32 = describe(item: 5)
                          var b: S32 = describe(item: p)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion
}
