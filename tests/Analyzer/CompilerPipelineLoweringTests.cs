using SemanticVerification;
using TypeModel.Enums;
using SemanticVerification.Results;
using Xunit;
using Compiler.CodeGen;
using Compiler.Postprocessing;
using Compiler.Targeting;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;
using SyntaxTree;

public class CompilerPipelineLoweringTests
{
    [Fact]
    public void Analyze_TupleLiteral_IsLoweredToCreatorExpression()
    {
        string source = """
                        routine test()
                          var pair = (1_s32, 2_s32)
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticAnalyzer(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        Assert.False(condition: ContainsTupleLiteral(program: program));
    }

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
        var analyzer = new SemanticAnalyzer(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        RunPostprocessing(program: program, result: result);
        Assert.False(condition: ContainsBecomes(program: program));
    }

    [Fact]
    public void Analyze_LambdaExpression_IsLiftedBeforeCodegen()
    {
        string source = """
                        routine test() -> S32
                          var double_it = x => x * 2_s32
                          return 0_s32
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticAnalyzer(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        RunPostprocessing(program: program, result: result);
        Assert.False(condition: ContainsLambda(program: program));
    }

    [Fact]
    public void Codegen_TupleLiteralLowering_GeneratesIr()
    {
        string source = """
                        routine test()
                          var pair = (1_s32, 2_s32)
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticAnalyzer(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        RunPostprocessing(program: program, result: result);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            stdlibPrograms: result.Registry.StdlibPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "insertvalue", actualString: llvmIr);
    }

    [Fact]
    public void Codegen_PriorityQueueDictLiteral_GeneratesIr()
    {
        string source = """
                        routine test()
                          var items: PriorityQueue[S64, Text] = {1: "high", 10: "low"}
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticAnalyzer(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        RunPostprocessing(program: program, result: result);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            stdlibPrograms: result.Registry.StdlibPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "PriorityQueue", actualString: llvmIr);
    }

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
        var analyzer = new SemanticAnalyzer(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        RunPostprocessing(program: program, result: result);
        Assert.False(condition: ContainsLambda(program: program));

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            stdlibPrograms: result.Registry.StdlibPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "__lambda_", actualString: llvmIr);
    }

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
        var analyzer = new SemanticAnalyzer(language: Language.RazorForge);
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);
        RunPostprocessing(program: program, result: result);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            stdlibPrograms: result.Registry.StdlibPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);

        string llvmIr = generator.Generate();
        Assert.Contains(expectedSubstring: "define void @Collections.BitList.add_last",
            actualString: llvmIr);
    }

    private static void RunPostprocessing(Program program, AnalysisResult result)
    {
        var pipeline = new PostprocessingPipeline(new PostprocessingContext(
            registry: result.Registry,
            target: TargetConfig.ForCurrentHost(),
            buildMode: RfBuildMode.Debug));
        pipeline.Run(program);
    }

    private static bool ContainsTupleLiteral(Program program)
    {
        return program.Declarations.OfType<RoutineDeclaration>()
            .Any(predicate: routine => ContainsTupleLiteral(statement: routine.Body));
    }

    private static bool ContainsTupleLiteral(Statement statement)
    {
        return statement switch
        {
            BlockStatement block => block.Statements.Any(ContainsTupleLiteral),
            DeclarationStatement { Declaration: VariableDeclaration { Initializer: { } init } } =>
                ContainsTupleLiteral(expression: init),
            AssignmentStatement assign =>
                ContainsTupleLiteral(expression: assign.Target) ||
                ContainsTupleLiteral(expression: assign.Value),
            ReturnStatement { Value: { } value } => ContainsTupleLiteral(expression: value),
            ExpressionStatement exprStmt => ContainsTupleLiteral(expression: exprStmt.Expression),
            IfStatement ifs =>
                ContainsTupleLiteral(expression: ifs.Condition) ||
                ContainsTupleLiteral(statement: ifs.ThenStatement) ||
                ifs.ElseStatement != null && ContainsTupleLiteral(statement: ifs.ElseStatement),
            LoopStatement loop => ContainsTupleLiteral(statement: loop.Body),
            WhenStatement whenStmt =>
                ContainsTupleLiteral(expression: whenStmt.Expression) ||
                whenStmt.Clauses.Any(predicate: clause => ContainsTupleLiteral(statement: clause.Body)),
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
            CallExpression call =>
                ContainsTupleLiteral(expression: call.Callee) ||
                call.Arguments.Any(ContainsTupleLiteral),
            BinaryExpression binary =>
                ContainsTupleLiteral(expression: binary.Left) ||
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
        return program.Declarations.OfType<RoutineDeclaration>()
            .Any(predicate: routine => ContainsBecomes(statement: routine.Body));
    }

    private static bool ContainsLambda(Program program)
    {
        return program.Declarations.OfType<RoutineDeclaration>()
            .Any(predicate: routine => ContainsLambda(statement: routine.Body));
    }

    private static bool ContainsBecomes(Statement statement)
    {
        return statement switch
        {
            BecomesStatement => true,
            BlockStatement block => block.Statements.Any(ContainsBecomes),
            IfStatement ifs =>
                ContainsBecomes(statement: ifs.ThenStatement) ||
                ifs.ElseStatement != null && ContainsBecomes(statement: ifs.ElseStatement),
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
            AssignmentStatement assign =>
                ContainsLambda(expression: assign.Target) ||
                ContainsLambda(expression: assign.Value),
            ReturnStatement { Value: { } value } => ContainsLambda(expression: value),
            ExpressionStatement exprStmt => ContainsLambda(expression: exprStmt.Expression),
            IfStatement ifs =>
                ContainsLambda(expression: ifs.Condition) ||
                ContainsLambda(statement: ifs.ThenStatement) ||
                ifs.ElseStatement != null && ContainsLambda(statement: ifs.ElseStatement),
            WhileStatement whileStmt =>
                ContainsLambda(expression: whileStmt.Condition) ||
                ContainsLambda(statement: whileStmt.Body) ||
                whileStmt.ElseBranch != null && ContainsLambda(statement: whileStmt.ElseBranch),
            LoopStatement loop => ContainsLambda(statement: loop.Body),
            ForStatement forStmt =>
                ContainsLambda(expression: forStmt.Iterable) ||
                ContainsLambda(statement: forStmt.Body) ||
                forStmt.ElseBranch != null && ContainsLambda(statement: forStmt.ElseBranch),
            WhenStatement whenStmt =>
                ContainsLambda(expression: whenStmt.Expression) ||
                whenStmt.Clauses.Any(predicate: clause => ContainsLambda(statement: clause.Body)),
            DangerStatement danger => ContainsLambda(statement: danger.Body),
            UsingStatement usingStmt =>
                ContainsLambda(expression: usingStmt.Resource) ||
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
            BinaryExpression binary =>
                ContainsLambda(expression: binary.Left) ||
                ContainsLambda(expression: binary.Right),
            UnaryExpression unary => ContainsLambda(expression: unary.Operand),
            CallExpression call =>
                ContainsLambda(expression: call.Callee) ||
                call.Arguments.Any(ContainsLambda),
            MemberExpression member => ContainsLambda(expression: member.Object),
            OptionalMemberExpression member => ContainsLambda(expression: member.Object),
            IndexExpression index =>
                ContainsLambda(expression: index.Object) ||
                ContainsLambda(expression: index.Index),
            ConditionalExpression conditional =>
                ContainsLambda(expression: conditional.Condition) ||
                ContainsLambda(expression: conditional.TrueExpression) ||
                ContainsLambda(expression: conditional.FalseExpression),
            CreatorExpression creator =>
                creator.MemberVariables.Any(predicate: mv => ContainsLambda(expression: mv.Value)),
            GenericMethodCallExpression generic =>
                ContainsLambda(expression: generic.Object) ||
                generic.Arguments.Any(ContainsLambda),
            NamedArgumentExpression named => ContainsLambda(expression: named.Value),
            WithExpression withExpr =>
                ContainsLambda(expression: withExpr.Base) ||
                withExpr.Updates.Any(predicate: update =>
                    ContainsLambda(expression: update.Value) ||
                    update.Index != null && ContainsLambda(expression: update.Index)),
            ListLiteralExpression list => list.Elements.Any(ContainsLambda),
            SetLiteralExpression set => set.Elements.Any(ContainsLambda),
            DictLiteralExpression dict =>
                dict.Pairs.Any(predicate: pair =>
                    ContainsLambda(expression: pair.Key) ||
                    ContainsLambda(expression: pair.Value)),
            TupleLiteralExpression tuple => tuple.Elements.Any(ContainsLambda),
            TypeConversionExpression conversion => ContainsLambda(expression: conversion.Expression),
            ChainedComparisonExpression chained => chained.Operands.Any(ContainsLambda),
            BlockExpression block => ContainsLambda(expression: block.Value),
            DictEntryLiteralExpression dictEntry =>
                ContainsLambda(expression: dictEntry.Key) ||
                ContainsLambda(expression: dictEntry.Value),
            IsPatternExpression isPattern => ContainsLambda(expression: isPattern.Expression),
            FlagsTestExpression flagsTest => ContainsLambda(expression: flagsTest.Subject),
            InsertedTextExpression inserted => inserted.Parts
                .OfType<ExpressionPart>()
                .Any(predicate: part => ContainsLambda(expression: part.Expression)),
            StealExpression steal => ContainsLambda(expression: steal.Operand),
            WaitforExpression waitfor =>
                ContainsLambda(expression: waitfor.Operand) ||
                waitfor.Timeout != null && ContainsLambda(expression: waitfor.Timeout),
            DependentWaitforExpression dependent =>
                ContainsLambda(expression: dependent.Operand) ||
                dependent.Dependencies.Any(predicate: dep =>
                    ContainsLambda(expression: dep.DependencyExpr)) ||
                dependent.Timeout != null && ContainsLambda(expression: dependent.Timeout),
            CarrierPayloadExpression payload => ContainsLambda(expression: payload.Carrier),
            BackIndexExpression backIndex => ContainsLambda(expression: backIndex.Operand),
            WhenExpression whenExpr =>
                whenExpr.Expression != null && ContainsLambda(expression: whenExpr.Expression) ||
                whenExpr.Clauses.Any(predicate: clause => ContainsLambda(statement: clause.Body)),
            _ => false
        };
    }
}
