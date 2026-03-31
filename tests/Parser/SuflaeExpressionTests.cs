using Xunit;
using SyntaxTree;

namespace RazorForge.Tests.Parser;

using static TestHelpers;

/// <summary>
/// Tests for parsing expressions in Suflae:
/// method calls, member variable access, indexing, lambdas, closures, string interpolation.
/// Suflae uses indentation-based syntax.
/// </summary>
public class SuflaeExpressionTests
{
    #region Method Call Tests
    /// <summary>
    /// Tests ParseSuflae_SimpleMethodCall.
    /// </summary>

    [Fact]
    public void ParseSuflae_SimpleMethodCall()
    {
        string source = """
                        routine test()
                          show("hello")
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_MethodCallWithMultipleArgs.
    /// </summary>

    [Fact]
    public void ParseSuflae_MethodCallWithMultipleArgs()
    {
        string source = """
                        routine test()
                          compute(1, 2, 3)
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_MethodCallWithNamedArgs.
    /// </summary>

    [Fact]
    public void ParseSuflae_MethodCallWithNamedArgs()
    {
        string source = """
                        routine test()
                          create_user(name: "Alice", age: 30)
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_MethodCallChain.
    /// </summary>

    [Fact]
    public void ParseSuflae_MethodCallChain()
    {
        string source = """
                        routine test()
                          data.where().select().List()
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_MethodCallOnLiteral.
    /// </summary>

    [Fact]
    public void ParseSuflae_MethodCallOnLiteral()
    {
        string source = """
                        routine test()
                          var len = "hello".count()
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_MethodCallWithConversion.
    /// </summary>

    [Fact]
    public void ParseSuflae_MethodCallWithConversion()
    {
        string source = """
                        routine test()
                          var result = "42".S32!()
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_StaticMethodCall.
    /// </summary>

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
    /// <summary>
    /// Tests ParseSuflae_SimpleMemberVariableAccess.
    /// </summary>

    [Fact]
    public void ParseSuflae_SimpleMemberVariableAccess()
    {
        string source = """
                        routine test()
                          var x = point.x
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_ChainedMemberVariableAccess.
    /// </summary>

    [Fact]
    public void ParseSuflae_ChainedMemberVariableAccess()
    {
        string source = """
                        routine test()
                          var city = user.address.city
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_MixedMemberVariableAndMethodAccess.
    /// </summary>

    [Fact]
    public void ParseSuflae_MixedMemberVariableAndMethodAccess()
    {
        string source = """
                        routine test()
                          var result = user.name.to_upper().length()
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_MeMemberVariableAccess.
    /// </summary>

    [Fact]
    public void ParseSuflae_MeMemberVariableAccess()
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
    /// <summary>
    /// Tests ParseSuflae_ArrayIndexing.
    /// </summary>

    [Fact]
    public void ParseSuflae_ArrayIndexing()
    {
        string source = """
                        routine test()
                          var first = items[0]
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_MultiDimensionalIndexing.
    /// </summary>

    [Fact]
    public void ParseSuflae_MultiDimensionalIndexing()
    {
        string source = """
                        routine test()
                          var cell = matrix[i][j]
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_DictIndexing.
    /// </summary>

    [Fact]
    public void ParseSuflae_DictIndexing()
    {
        string source = """
                        routine test()
                          var value = dict["key"]
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_IndexAssignment.
    /// </summary>

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
    /// <summary>
    /// Tests ParseSuflae_RecordConstructor.
    /// </summary>

    [Fact]
    public void ParseSuflae_RecordConstructor()
    {
        string source = """
                        routine test()
                          var point = Point(x: 10.0, y: 20.0)
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_EntityConstructor.
    /// </summary>

    [Fact]
    public void ParseSuflae_EntityConstructor()
    {
        string source = """
                        routine test()
                          var user = User(name: "Alice", age: 30)
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_NestedConstructor.
    /// </summary>

    [Fact]
    public void ParseSuflae_NestedConstructor()
    {
        string source = """
                        routine test()
                          var circle = Circle(center: Point(x: 0.0, y: 0.0), radius: 10.0)
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_GenericConstructor.
    /// </summary>

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
    /// <summary>
    /// Tests ParseSuflae_SimpleLambda.
    /// </summary>

    [Fact]
    public void ParseSuflae_SimpleLambda()
    {
        string source = """
                        routine test()
                          var add = (a, b) => a + b
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_SingleParamLambda.
    /// </summary>

    [Fact]
    public void ParseSuflae_SingleParamLambda()
    {
        string source = """
                        routine test()
                          var double = x => x * 2
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_LambdaAsArgument.
    /// </summary>

    [Fact]
    public void ParseSuflae_LambdaAsArgument()
    {
        string source = """
                        routine test()
                          items.select(x => x * 2)
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_LambdaWithCapture.
    /// </summary>

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
    /// <summary>
    /// Tests ParseSuflae_NoParamLambda.
    /// </summary>

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
    /// <summary>
    /// Tests ParseSuflae_SimpleInterpolation.
    /// </summary>

    [Fact]
    public void ParseSuflae_SimpleInterpolation()
    {
        string source = """
                        routine test()
                          var msg = f"Hello, {name}!"
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_InterpolationWithExpression.
    /// </summary>

    [Fact]
    public void ParseSuflae_InterpolationWithExpression()
    {
        string source = """
                        routine test()
                          var msg = f"Sum: {a + b}"
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_MultipleInterpolations.
    /// </summary>

    [Fact]
    public void ParseSuflae_MultipleInterpolations()
    {
        string source = """
                        routine test()
                          var msg = f"{first} + {second} = {first + second}"
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_InterpolationWithMethodCall.
    /// </summary>

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

    #region Inserted Text AST Tests

    /// <summary>
    /// Helper to extract InsertedTextExpression from f-string assignment in a Suflae routine.
    /// </summary>
    private static InsertedTextExpression GetInsertedTextSuflae(string source)
    {
        Program program = AssertParsesSuflae(source: source);
        var routine = (RoutineDeclaration)program.Declarations[0];
        var block = (BlockStatement)routine.Body;
        var declStmt = (DeclarationStatement)block.Statements[0];
        var varDecl = (VariableDeclaration)declStmt.Declaration;
        return (InsertedTextExpression)varDecl.Initializer!;
    }
    /// <summary>
    /// Tests ParseSuflae_InsertedText_SimpleInterpolation_HasThreeParts.
    /// </summary>

    [Fact]
    public void ParseSuflae_InsertedText_SimpleInterpolation_HasThreeParts()
    {
        string source = """
                        routine test()
                          var msg = f"Hello, {name}!"
                        """;

        InsertedTextExpression expr = GetInsertedTextSuflae(source: source);
        Assert.Equal(expected: 3, actual: expr.Parts.Count);
        Assert.IsType<TextPart>(expr.Parts[0]);
        Assert.Equal(expected: "Hello, ", actual: ((TextPart)expr.Parts[0]).Text);
        Assert.IsType<ExpressionPart>(expr.Parts[1]);
        Assert.IsType<TextPart>(expr.Parts[2]);
        Assert.Equal(expected: "!", actual: ((TextPart)expr.Parts[2]).Text);
    }
    /// <summary>
    /// Tests ParseSuflae_InsertedText_MultipleInsertions_HasFiveParts.
    /// </summary>

    [Fact]
    public void ParseSuflae_InsertedText_MultipleInsertions_HasFiveParts()
    {
        string source = """
                        routine test()
                          var msg = f"{a} + {b} = {a + b}"
                        """;

        InsertedTextExpression expr = GetInsertedTextSuflae(source: source);
        Assert.Equal(expected: 5, actual: expr.Parts.Count);
        Assert.IsType<ExpressionPart>(expr.Parts[0]);
        Assert.IsType<TextPart>(expr.Parts[1]);
        Assert.IsType<ExpressionPart>(expr.Parts[2]);
        Assert.IsType<TextPart>(expr.Parts[3]);
        Assert.IsType<ExpressionPart>(expr.Parts[4]);
        Assert.IsType<BinaryExpression>(((ExpressionPart)expr.Parts[4]).Expression);
    }
    /// <summary>
    /// Tests ParseSuflae_InsertedText_EscapedBraces_SingleTextPart.
    /// </summary>

    [Fact]
    public void ParseSuflae_InsertedText_EscapedBraces_SingleTextPart()
    {
        string source = """
                        routine test()
                          var msg = f"Set: {{1, 2}}"
                        """;

        InsertedTextExpression expr = GetInsertedTextSuflae(source: source);
        Assert.Single(collection: expr.Parts);
        Assert.IsType<TextPart>(expr.Parts[0]);
        Assert.Equal(expected: "Set: {1, 2}", actual: ((TextPart)expr.Parts[0]).Text);
    }
    /// <summary>
    /// Tests ParseSuflae_InsertedText_FormatSpec.
    /// </summary>

    [Fact]
    public void ParseSuflae_InsertedText_FormatSpec()
    {
        string source = """
                        routine test()
                          var msg = f"{value:D2}"
                        """;

        InsertedTextExpression expr = GetInsertedTextSuflae(source: source);
        Assert.Single(collection: expr.Parts);
        var exprPart = Assert.IsType<ExpressionPart>(expr.Parts[0]);
        Assert.Equal(expected: "D2", actual: exprPart.FormatSpec);
    }
    /// <summary>
    /// Tests ParseSuflae_InsertedText_NestedBrackets_IndexAccess.
    /// </summary>

    [Fact]
    public void ParseSuflae_InsertedText_NestedBrackets_IndexAccess()
    {
        string source = """
                        routine test()
                          var msg = f"{list[0]}"
                        """;

        InsertedTextExpression expr = GetInsertedTextSuflae(source: source);
        Assert.Single(collection: expr.Parts);
        var exprPart = Assert.IsType<ExpressionPart>(expr.Parts[0]);
        Assert.IsType<IndexExpression>(exprPart.Expression);
    }
    /// <summary>
    /// Tests ParseSuflae_InsertedText_NoInsertions_SingleTextPart.
    /// </summary>

    [Fact]
    public void ParseSuflae_InsertedText_NoInsertions_SingleTextPart()
    {
        string source = """
                        routine test()
                          var msg = f"plain text"
                        """;

        InsertedTextExpression expr = GetInsertedTextSuflae(source: source);
        Assert.Single(collection: expr.Parts);
        Assert.IsType<TextPart>(expr.Parts[0]);
        Assert.Equal(expected: "plain text", actual: ((TextPart)expr.Parts[0]).Text);
    }
    /// <summary>
    /// Tests ParseSuflae_InsertedText_AdjacentInsertions.
    /// </summary>

    [Fact]
    public void ParseSuflae_InsertedText_AdjacentInsertions()
    {
        string source = """
                        routine test()
                          var msg = f"{x}{y}"
                        """;

        InsertedTextExpression expr = GetInsertedTextSuflae(source: source);
        Assert.Equal(expected: 2, actual: expr.Parts.Count);
        Assert.IsType<ExpressionPart>(expr.Parts[0]);
        Assert.IsType<ExpressionPart>(expr.Parts[1]);
    }
    /// <summary>
    /// Tests ParseSuflae_InsertedText_RawFormatted.
    /// </summary>

    [Fact]
    public void ParseSuflae_InsertedText_RawFormatted()
    {
        string source = """
                        routine test()
                          var msg = rf"path: {dir}\file"
                        """;

        InsertedTextExpression expr = GetInsertedTextSuflae(source: source);
        Assert.True(condition: expr.IsRaw);
        Assert.Equal(expected: 3, actual: expr.Parts.Count);
        Assert.IsType<TextPart>(expr.Parts[0]);
        Assert.IsType<ExpressionPart>(expr.Parts[1]);
        Assert.IsType<TextPart>(expr.Parts[2]);
        Assert.Contains(expectedSubstring: "\\", actualString: ((TextPart)expr.Parts[2]).Text);
    }

    #endregion

    #region Type Conversion Tests
    /// <summary>
    /// Tests ParseSuflae_TypeConversionMethod.
    /// </summary>

    [Fact]
    public void ParseSuflae_TypeConversionMethod()
    {
        string source = """
                        routine test()
                          var x = value.S64!()
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_TypeConversionFromLiteral.
    /// </summary>

    [Fact]
    public void ParseSuflae_TypeConversionFromLiteral()
    {
        string source = """
                        routine test()
                          var x = 42.F64()
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_ChainedTypeConversion.
    /// </summary>

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
    /// <summary>
    /// Tests ParseSuflae_ParenthesizedExpression.
    /// </summary>

    [Fact]
    public void ParseSuflae_ParenthesizedExpression()
    {
        string source = """
                        routine test()
                          var result = (a + b) * c
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_NestedParentheses.
    /// </summary>

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
    /// <summary>
    /// Tests ParseSuflae_RangeExpression.
    /// </summary>

    [Fact]
    public void ParseSuflae_RangeExpression()
    {
        string source = """
                        routine test()
                          var range = 0 til 10
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_RangeExpressionWithStep.
    /// </summary>

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
    /// <summary>
    /// Tests ParseSuflae_ComplexChainedExpression.
    /// </summary>

    [Fact]
    public void ParseSuflae_ComplexChainedExpression()
    {
        string source = """
                        routine test()
                          var result = items.where(x => x > 0).select(x => x * 2).take(10).to_list()
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_ConditionalExpression.
    /// </summary>

    [Fact]
    public void ParseSuflae_ConditionalExpression()
    {
        string source = """
                        routine test()
                          var value = if condition then compute_a() else compute_b()
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_WhenAsExpression.
    /// </summary>

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
    /// <summary>
    /// Tests ParseSuflae_NoneCoalescingChain.
    /// </summary>

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
    /// <summary>
    /// Tests ParseSuflae_VariantConstruction.
    /// </summary>

    [Fact]
    public void ParseSuflae_VariantConstruction()
    {
        string source = """
                        routine test()
                          var msg = Message.TEXT("Hello")
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_VariantWithoutPayload.
    /// </summary>

    [Fact]
    public void ParseSuflae_VariantWithoutPayload()
    {
        string source = """
                        routine test()
                          var msg = Message.QUIT
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_VariantImmediatePatternMatch.
    /// </summary>

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
    /// <summary>
    /// Tests ParseSuflae_ChoiceValue.
    /// </summary>

    [Fact]
    public void ParseSuflae_ChoiceValue()
    {
        string source = """
                        routine test()
                          var status = AppStatus.ACTIVE
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_ChoiceMethodCall.
    /// </summary>

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
    /// <summary>
    /// Tests ParseSuflae_ChoiceEquality.
    /// </summary>

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
    /// <summary>
    /// Tests ParseSuflae_RoutineTypeVariable.
    /// </summary>

    [Fact]
    public void ParseSuflae_RoutineTypeVariable()
    {
        string source = """
                        routine test()
                          var add: Routine[S32, S32, S32] = (a, b) => a + b
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_RoutineTypeNoParams.
    /// </summary>

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
    /// <summary>
    /// Tests ParseSuflae_ByteStringLiteral.
    /// </summary>

    [Fact]
    public void ParseSuflae_ByteStringLiteral()
    {
        string source = """
                        routine test()
                          var data = b"hello"
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_ByteCharLiteral.
    /// </summary>

    [Fact]
    public void ParseSuflae_ByteCharLiteral()
    {
        string source = """
                        routine test()
                          var ch = b'A'
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_ByteStringHexEscape.
    /// </summary>

    [Fact]
    public void ParseSuflae_ByteStringHexEscape()
    {
        string source = """
                        routine test()
                          var data = b"\x48\x65\x6C\x6C\x6F"
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests Tokenize_SuflaeByteStringLiteral_CorrectTokenType.
    /// </summary>

    [Fact]
    public void Tokenize_SuflaeByteStringLiteral_CorrectTokenType()
    {
        string source = """b"hello" """;
        List<Compiler.Lexer.Token> tokens = TokenizeSuflae(source: source);

        Assert.Equal(expected: Compiler.Lexer.TokenType.BytesLiteral,
            actual: tokens[index: 0].Type);
    }
    /// <summary>
    /// Tests Tokenize_SuflaeByteCharLiteral_CorrectTokenType.
    /// </summary>

    [Fact]
    public void Tokenize_SuflaeByteCharLiteral_CorrectTokenType()
    {
        string source = """b'A' """;
        List<Compiler.Lexer.Token> tokens = TokenizeSuflae(source: source);

        Assert.Equal(expected: Compiler.Lexer.TokenType.ByteLetterLiteral,
            actual: tokens[index: 0].Type);
    }
    /// <summary>
    /// Tests Tokenize_SuflaeByteStringNonAscii_ThrowsError.
    /// </summary>

    [Fact]
    public void Tokenize_SuflaeByteStringNonAscii_ThrowsError()
    {
        string source = "b\"\uD55C\uAD6D\""; // b"한국" - non-ASCII

        Assert.ThrowsAny<Exception>(testCode: () => TokenizeSuflae(source: source));
    }
    /// <summary>
    /// Tests Tokenize_SuflaeByteStringUnicodeEscape_ThrowsError.
    /// </summary>

    [Fact]
    public void Tokenize_SuflaeByteStringUnicodeEscape_ThrowsError()
    {
        string source = """b"\u00E9" """;

        Assert.ThrowsAny<Exception>(testCode: () => TokenizeSuflae(source: source));
    }
    /// <summary>
    /// Tests Tokenize_SuflaeByteCharNonAscii_ThrowsError.
    /// </summary>

    [Fact]
    public void Tokenize_SuflaeByteCharNonAscii_ThrowsError()
    {
        string source = "b'\u00E9'"; // b'é' - non-ASCII

        Assert.ThrowsAny<Exception>(testCode: () => TokenizeSuflae(source: source));
    }
    /// <summary>
    /// Tests Tokenize_SuflaeUnicodeEscape_Exactly6Digits.
    /// </summary>

    [Fact]
    public void Tokenize_SuflaeUnicodeEscape_Exactly6Digits()
    {
        // \u00004E is exactly 6 hex digits — valid
        string source = "\"\\u00004E\"";

        List<Compiler.Lexer.Token> tokens = TokenizeSuflae(source: source);
        Assert.Equal(expected: Compiler.Lexer.TokenType.TextLiteral,
            actual: tokens[index: 0].Type);
    }
    /// <summary>
    /// Tests Tokenize_SuflaeUnicodeEscape_TooFewDigits_ThrowsError.
    /// </summary>

    [Fact]
    public void Tokenize_SuflaeUnicodeEscape_TooFewDigits_ThrowsError()
    {
        // \u00E9 is only 4 hex digits — should fail (need exactly 6)
        string source = "\"\\u00E9\"";

        Assert.ThrowsAny<Exception>(testCode: () => TokenizeSuflae(source: source));
    }
    /// <summary>
    /// Tests Tokenize_SuflaeUnicodeEscape_TwoDigits_ThrowsError.
    /// </summary>

    [Fact]
    public void Tokenize_SuflaeUnicodeEscape_TwoDigits_ThrowsError()
    {
        // \u41 is only 2 hex digits — should fail
        string source = "\"\\u41\"";

        Assert.ThrowsAny<Exception>(testCode: () => TokenizeSuflae(source: source));
    }

    #endregion

    #region With Expression Tests
    /// <summary>
    /// Tests ParseSuflae_WithMemberVariableUpdate.
    /// </summary>

    [Fact]
    public void ParseSuflae_WithMemberVariableUpdate()
    {
        string source = """
                        routine test()
                          var p2 = p1 with .x = 5.0
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_WithMultipleFields.
    /// </summary>

    [Fact]
    public void ParseSuflae_WithMultipleFields()
    {
        string source = """
                        routine test()
                          var p2 = p1 with .x = 5.0, .y = 3.0
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_WithIndexUpdate.
    /// </summary>

    [Fact]
    public void ParseSuflae_WithIndexUpdate()
    {
        string source = """
                        routine test()
                          var c2 = coords with [0] = 5.0
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_WithNestedMemberVariable.
    /// </summary>

    [Fact]
    public void ParseSuflae_WithNestedMemberVariable()
    {
        string source = """
                        routine test()
                          var p2 = person with .address.city = "Shelbyville"
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_WithMixedUpdates.
    /// </summary>

    [Fact]
    public void ParseSuflae_WithMixedUpdates()
    {
        string source = """
                        routine test()
                          var p2 = data with .name = "test", [0] = 42
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_WithExpression_ASTStructure.
    /// </summary>

    [Fact]
    public void ParseSuflae_WithExpression_ASTStructure()
    {
        string source = """
                        routine test()
                          var p2 = p1 with .x = 5.0, .y = 3.0
                        """;

        var ast = ParseSuflae(source: source);
        var routine = ast.Declarations.OfType<RoutineDeclaration>().First();
        var block = (BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<DeclarationStatement>().First();
        var withExpr = ((VariableDeclaration)varDecl.Declaration).Initializer
            as WithExpression;

        Assert.NotNull(withExpr);
        Assert.Equal(expected: 2, actual: withExpr!.Updates.Count);
        Assert.Equal(expected: "x", actual: withExpr.Updates[0].MemberVariablePath![0]);
        Assert.Equal(expected: "y", actual: withExpr.Updates[1].MemberVariablePath![0]);
    }
    /// <summary>
    /// Tests ParseSuflae_WithExpression_NestedMemberVariablePath_ASTStructure.
    /// </summary>

    [Fact]
    public void ParseSuflae_WithExpression_NestedMemberVariablePath_ASTStructure()
    {
        string source = """
                        routine test()
                          var p2 = person with .address.city = "NYC"
                        """;

        var ast = ParseSuflae(source: source);
        var routine = ast.Declarations.OfType<RoutineDeclaration>().First();
        var block = (BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<DeclarationStatement>().First();
        var withExpr = ((VariableDeclaration)varDecl.Declaration).Initializer
            as WithExpression;

        Assert.NotNull(withExpr);
        Assert.Single(withExpr!.Updates);
        Assert.Equal(expected: 2, actual: withExpr.Updates[0].MemberVariablePath!.Count);
        Assert.Equal(expected: "address", actual: withExpr.Updates[0].MemberVariablePath![0]);
        Assert.Equal(expected: "city", actual: withExpr.Updates[0].MemberVariablePath![1]);
    }

    #endregion

    #region Slice Expression Tests
    /// <summary>
    /// Tests ParseSuflae_SliceExpression.
    /// </summary>

    [Fact]
    public void ParseSuflae_SliceExpression()
    {
        string source = """
                        routine test()
                          var sub = list[0 til 5]
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_SliceExpressionWithBackIndex.
    /// </summary>

    [Fact]
    public void ParseSuflae_SliceExpressionWithBackIndex()
    {
        string source = """
                        routine test()
                          var sub = list[1 til ^1]
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_SliceExpression_ASTStructure.
    /// </summary>

    [Fact]
    public void ParseSuflae_SliceExpression_ASTStructure()
    {
        string source = """
                        routine test()
                          var sub = list[0 til 5]
                        """;

        var ast = ParseSuflae(source: source);
        var routine = ast.Declarations.OfType<RoutineDeclaration>().First();
        var block = (BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<DeclarationStatement>().First();
        var initializer = ((VariableDeclaration)varDecl.Declaration).Initializer;

        Assert.IsType<SliceExpression>(initializer);
        var slice = (SliceExpression)initializer!;
        Assert.IsType<IdentifierExpression>(slice.Object);
        Assert.IsType<LiteralExpression>(slice.Start);
        Assert.IsType<LiteralExpression>(slice.End);
    }
    /// <summary>
    /// Tests ParseSuflae_SliceExpression_TilRange.
    /// </summary>

    [Fact]
    public void ParseSuflae_SliceExpression_TilRange()
    {
        string source = """
                        routine test()
                          var sub = list[5 til 0]
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_SliceExpression_RejectsStep.
    /// </summary>

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
