using Xunit;

namespace RazorForge.Tests.Parser;

using static TestHelpers;

/// <summary>
/// Tests for parsing expressions in Suflae:
/// method calls, field access, indexing, lambdas, closures, string interpolation.
/// Suflae uses indentation-based syntax.
/// </summary>
public class SuflaeExpressionTests
{
    #region Method Call Tests

    [Fact]
    public void ParseSuflae_SimpleMethodCall()
    {
        string source = """
                        routine test()
                          show("hello")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_MethodCallWithMultipleArgs()
    {
        string source = """
                        routine test()
                          compute(1, 2, 3)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_MethodCallWithNamedArgs()
    {
        string source = """
                        routine test()
                          create_user(name: "Alice", age: 30)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_MethodCallChain()
    {
        string source = """
                        routine test()
                          data.where().select().List()
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_MethodCallOnLiteral()
    {
        string source = """
                        routine test()
                          var len = "hello".count()
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_MethodCallWithConversion()
    {
        string source = """
                        routine test()
                          var result = "42".S32!()
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_StaticMethodCall()
    {
        string source = """
                        routine test()
                          var pi = Math.pi()
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Field Access Tests

    [Fact]
    public void ParseSuflae_SimpleFieldAccess()
    {
        string source = """
                        routine test()
                          var x = point.x
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ChainedFieldAccess()
    {
        string source = """
                        routine test()
                          var city = user.address.city
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_MixedFieldAndMethodAccess()
    {
        string source = """
                        routine test()
                          var result = user.name.to_upper().length()
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_MeFieldAccess()
    {
        string source = """
                        @readonly
                        routine Point.get_x() -> F32
                          return me.x
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Indexing Tests

    [Fact]
    public void ParseSuflae_ArrayIndexing()
    {
        string source = """
                        routine test()
                          var first = items[0]
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_MultiDimensionalIndexing()
    {
        string source = """
                        routine test()
                          var cell = matrix[i][j]
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_DictIndexing()
    {
        string source = """
                        routine test()
                          var value = dict["key"]
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_IndexAssignment()
    {
        string source = """
                        routine test()
                          var items = [1, 2, 3]
                          items[0] = 42
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void ParseSuflae_RecordConstructor()
    {
        string source = """
                        routine test()
                          var point = Point(x: 10.0, y: 20.0)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_EntityConstructor()
    {
        string source = """
                        routine test()
                          var user = User(name: "Alice", age: 30)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_NestedConstructor()
    {
        string source = """
                        routine test()
                          var circle = Circle(center: Point(x: 0.0, y: 0.0), radius: 10.0)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_GenericConstructor()
    {
        string source = """
                        routine test()
                          var container = Container[Integer](value: 42)
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Lambda Tests

    [Fact]
    public void ParseSuflae_SimpleLambda()
    {
        string source = """
                        routine test()
                          var add = (a, b) => a + b
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_SingleParamLambda()
    {
        string source = """
                        routine test()
                          var double = x => x * 2
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LambdaAsArgument()
    {
        string source = """
                        routine test()
                          items.select(x => x * 2)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LambdaWithCapture()
    {
        string source = """
                        routine test()
                          var multiplier = 10
                          var scale = x => x * multiplier
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_NoParamLambda()
    {
        string source = """
                        routine test()
                          var get_value = () => 42
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region String Interpolation Tests

    [Fact]
    public void ParseSuflae_SimpleInterpolation()
    {
        string source = """
                        routine test()
                          var msg = f"Hello, {name}!"
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_InterpolationWithExpression()
    {
        string source = """
                        routine test()
                          var msg = f"Sum: {a + b}"
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_MultipleInterpolations()
    {
        string source = """
                        routine test()
                          var msg = f"{first} + {second} = {first + second}"
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_InterpolationWithMethodCall()
    {
        string source = """
                        routine test()
                          var msg = f"Name: {user.name.to_upper()}"
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Type Conversion Tests

    [Fact]
    public void ParseSuflae_TypeConversionMethod()
    {
        string source = """
                        routine test()
                          var x = value.S64!()
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_TypeConversionFromLiteral()
    {
        string source = """
                        routine test()
                          var x = 42.F64()
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ChainedTypeConversion()
    {
        string source = """
                        routine test()
                          var result = "42".S32!().F64!()
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Parenthesized Expression Tests

    [Fact]
    public void ParseSuflae_ParenthesizedExpression()
    {
        string source = """
                        routine test()
                          var result = (a + b) * c
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_NestedParentheses()
    {
        string source = """
                        routine test()
                          var result = ((a + b) * (c - d)) / e
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Range Expression Tests

    [Fact]
    public void ParseSuflae_RangeExpression()
    {
        string source = """
                        routine test()
                          var range = 0 til 10
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_RangeExpressionWithStep()
    {
        string source = """
                        routine test()
                          var range = 0 til 100 by 5
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Complex Expression Tests

    [Fact]
    public void ParseSuflae_ComplexChainedExpression()
    {
        string source = """
                        routine test()
                          var result = items.where(x => x > 0).select(x => x * 2).take(10).to_list()
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ConditionalExpression()
    {
        string source = """
                        routine test()
                          var value = if condition then compute_a() else compute_b()
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenAsExpression()
    {
        string source = """
                        routine test()
                          var description = when status
                            is PENDING => "Waiting"
                            is ACTIVE => "Running"
                            else => "Unknown"
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_NoneCoalescingChain()
    {
        string source = """
                        routine test()
                          var value = first_option() ?? second_option() ?? default_value
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Variant Construction Tests

    [Fact]
    public void ParseSuflae_VariantConstruction()
    {
        string source = """
                        routine test()
                          var msg = Message.TEXT("Hello")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_VariantWithoutPayload()
    {
        string source = """
                        routine test()
                          var msg = Message.QUIT
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_VariantImmediatePatternMatch()
    {
        string source = """
                        routine test()
                          var result = parse_number("123")
                          when result
                            is SUCCESS value =>
                              show(f"Got: {value}")
                            is ERROR msg =>
                              show(f"Error: {msg}")
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Choice Value Tests

    [Fact]
    public void ParseSuflae_ChoiceValue()
    {
        string source = """
                        routine test()
                          var status = AppStatus.ACTIVE
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ChoiceMethodCall()
    {
        string source = """
                        routine test()
                          var dir = Direction.NORTH
                          var opposite = dir.opposite()
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ChoiceEquality()
    {
        string source = """
                        routine test()
                          var status = FileAccess.READ
                          if status == READ
                            show("Read access")
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Routine Type Tests

    [Fact]
    public void ParseSuflae_RoutineTypeVariable()
    {
        string source = """
                        routine test()
                          var add: Routine[S32, S32, S32] = (a, b) => a + b
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_RoutineTypeNoParams()
    {
        string source = """
                        routine test()
                          var get_value: Routine[S32] = () => 42
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Byte Literal Tests

    [Fact]
    public void ParseSuflae_ByteStringLiteral()
    {
        string source = """
                        routine test()
                          var data = b"hello"
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ByteCharLiteral()
    {
        string source = """
                        routine test()
                          var ch = b'A'
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ByteStringHexEscape()
    {
        string source = """
                        routine test()
                          var data = b"\x48\x65\x6C\x6C\x6F"
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void Tokenize_SuflaeByteStringLiteral_CorrectTokenType()
    {
        string source = """b"hello" """;
        List<Compiler.Lexer.Token> tokens = TokenizeSuflae(source: source);

        Assert.Equal(expected: Compiler.Lexer.TokenType.BytesLiteral,
            actual: tokens[index: 0].Type);
    }

    [Fact]
    public void Tokenize_SuflaeByteCharLiteral_CorrectTokenType()
    {
        string source = """b'A' """;
        List<Compiler.Lexer.Token> tokens = TokenizeSuflae(source: source);

        Assert.Equal(expected: Compiler.Lexer.TokenType.ByteLetterLiteral,
            actual: tokens[index: 0].Type);
    }

    [Fact]
    public void Tokenize_SuflaeByteStringNonAscii_ThrowsError()
    {
        string source = "b\"\uD55C\uAD6D\""; // b"한국" - non-ASCII

        Assert.ThrowsAny<Exception>(testCode: () => TokenizeSuflae(source: source));
    }

    [Fact]
    public void Tokenize_SuflaeByteStringUnicodeEscape_ThrowsError()
    {
        string source = """b"\u00E9" """;

        Assert.ThrowsAny<Exception>(testCode: () => TokenizeSuflae(source: source));
    }

    [Fact]
    public void Tokenize_SuflaeByteCharNonAscii_ThrowsError()
    {
        string source = "b'\u00E9'"; // b'é' - non-ASCII

        Assert.ThrowsAny<Exception>(testCode: () => TokenizeSuflae(source: source));
    }

    [Fact]
    public void Tokenize_SuflaeUnicodeEscape_Exactly6Digits()
    {
        // \u00004E is exactly 6 hex digits — valid
        string source = "\"\\u00004E\"";

        List<Compiler.Lexer.Token> tokens = TokenizeSuflae(source: source);
        Assert.Equal(expected: Compiler.Lexer.TokenType.TextLiteral,
            actual: tokens[index: 0].Type);
    }

    [Fact]
    public void Tokenize_SuflaeUnicodeEscape_TooFewDigits_ThrowsError()
    {
        // \u00E9 is only 4 hex digits — should fail (need exactly 6)
        string source = "\"\\u00E9\"";

        Assert.ThrowsAny<Exception>(testCode: () => TokenizeSuflae(source: source));
    }

    [Fact]
    public void Tokenize_SuflaeUnicodeEscape_TwoDigits_ThrowsError()
    {
        // \u41 is only 2 hex digits — should fail
        string source = "\"\\u41\"";

        Assert.ThrowsAny<Exception>(testCode: () => TokenizeSuflae(source: source));
    }

    #endregion

    #region With Expression Tests

    [Fact]
    public void ParseSuflae_WithFieldUpdate()
    {
        string source = """
                        routine test()
                          var p2 = p1 with .x = 5.0
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WithMultipleFields()
    {
        string source = """
                        routine test()
                          var p2 = p1 with .x = 5.0, .y = 3.0
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WithIndexUpdate()
    {
        string source = """
                        routine test()
                          var c2 = coords with [0] = 5.0
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WithNestedField()
    {
        string source = """
                        routine test()
                          var p2 = person with .address.city = "Shelbyville"
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WithMixedUpdates()
    {
        string source = """
                        routine test()
                          var p2 = data with .name = "test", [0] = 42
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WithExpression_ASTStructure()
    {
        string source = """
                        routine test()
                          var p2 = p1 with .x = 5.0, .y = 3.0
                        """;

        var ast = ParseSuflae(source: source);
        var routine = ast.Declarations.OfType<SyntaxTree.RoutineDeclaration>().First();
        var block = (SyntaxTree.BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<SyntaxTree.DeclarationStatement>().First();
        var withExpr = ((SyntaxTree.VariableDeclaration)varDecl.Declaration).Initializer
            as SyntaxTree.WithExpression;

        Assert.NotNull(withExpr);
        Assert.Equal(expected: 2, actual: withExpr!.Updates.Count);
        Assert.Equal(expected: "x", actual: withExpr.Updates[0].FieldPath![0]);
        Assert.Equal(expected: "y", actual: withExpr.Updates[1].FieldPath![0]);
    }

    [Fact]
    public void ParseSuflae_WithExpression_NestedFieldPath_ASTStructure()
    {
        string source = """
                        routine test()
                          var p2 = person with .address.city = "NYC"
                        """;

        var ast = ParseSuflae(source: source);
        var routine = ast.Declarations.OfType<SyntaxTree.RoutineDeclaration>().First();
        var block = (SyntaxTree.BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<SyntaxTree.DeclarationStatement>().First();
        var withExpr = ((SyntaxTree.VariableDeclaration)varDecl.Declaration).Initializer
            as SyntaxTree.WithExpression;

        Assert.NotNull(withExpr);
        Assert.Single(withExpr!.Updates);
        Assert.Equal(expected: 2, actual: withExpr.Updates[0].FieldPath!.Count);
        Assert.Equal(expected: "address", actual: withExpr.Updates[0].FieldPath![0]);
        Assert.Equal(expected: "city", actual: withExpr.Updates[0].FieldPath![1]);
    }

    #endregion

    #region Slice Expression Tests

    [Fact]
    public void ParseSuflae_SliceExpression()
    {
        string source = """
                        routine test()
                          var sub = list[0 til 5]
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_SliceExpressionWithBackIndex()
    {
        string source = """
                        routine test()
                          var sub = list[1 til ^1]
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_SliceExpression_ASTStructure()
    {
        string source = """
                        routine test()
                          var sub = list[0 til 5]
                        """;

        var ast = ParseSuflae(source: source);
        var routine = ast.Declarations.OfType<SyntaxTree.RoutineDeclaration>().First();
        var block = (SyntaxTree.BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<SyntaxTree.DeclarationStatement>().First();
        var initializer = ((SyntaxTree.VariableDeclaration)varDecl.Declaration).Initializer;

        Assert.IsType<SyntaxTree.SliceExpression>(initializer);
        var slice = (SyntaxTree.SliceExpression)initializer!;
        Assert.IsType<SyntaxTree.IdentifierExpression>(slice.Object);
        Assert.IsType<SyntaxTree.LiteralExpression>(slice.Start);
        Assert.IsType<SyntaxTree.LiteralExpression>(slice.End);
    }

    [Fact]
    public void ParseSuflae_SliceExpression_TilRange()
    {
        string source = """
                        routine test()
                          var sub = list[5 til 0]
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_SliceExpression_RejectsStep()
    {
        string source = """
                        routine test()
                          var sub = list[0 til 10 by 2]
                        """;

        (_, var parser) = ParseSuflaeWithErrors(source: source);
        Assert.True(condition: parser.HasErrors, userMessage: "Expected parse errors for 'by' step in slice syntax");
    }

    #endregion
}
