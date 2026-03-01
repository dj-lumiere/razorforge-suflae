using Xunit;

namespace RazorForge.Tests.Parser;

using static TestHelpers;

/// <summary>
/// Tests for parsing expressions in RazorForge:
/// method calls, field access, indexing, lambdas, closures, string interpolation.
/// </summary>
public class ExpressionTests
{
    #region Method Call Tests

    [Fact]
    public void Parse_SimpleMethodCall()
    {
        string source = """
                        routine test()
                          show("hello")
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_MethodCallWithMultipleArgs()
    {
        string source = """
                        routine test()
                          compute(1, 2, 3)
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_MethodCallWithNamedArgs()
    {
        string source = """
                        routine test()
                          create_user(name: "Alice", age: 30)
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_MethodCallChain()
    {
        string source = """
                        routine test()
                          data.where().select().to_list()
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_MethodCallOnLiteral()
    {
        string source = """
                        routine test()
                          var len = "hello".length()
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_MethodCallWithConversion()
    {
        string source = """
                        routine test()
                          var result = "42".S32!()
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_StaticMethodCall()
    {
        string source = """
                        routine test()
                          var pi = Math.pi()
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Field Access Tests

    [Fact]
    public void Parse_SimpleFieldAccess()
    {
        string source = """
                        routine test()
                          var x = point.x
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_ChainedFieldAccess()
    {
        string source = """
                        routine test()
                          var city = user.address.city
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_MixedFieldAndMethodAccess()
    {
        string source = """
                        routine test()
                          var result = user.name.to_upper().length()
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_MeFieldAccess()
    {
        string source = """
                        @readonly
                        routine Point.get_x() -> F32
                          return me.x
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Indexing Tests

    [Fact]
    public void Parse_ArrayIndexing()
    {
        string source = """
                        routine test()
                          var first = items[0]
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_MultiDimensionalIndexing()
    {
        string source = """
                        routine test()
                          var cell = matrix[i][j]
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_DictIndexing()
    {
        string source = """
                        routine test()
                          var value = dict["key"]
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_IndexAssignment()
    {
        string source = """
                        routine test()
                          var items = [1, 2, 3]
                          items[0] = 42
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void Parse_RecordConstructor()
    {
        string source = """
                        routine test()
                          var point = Point(x: 10.0, y: 20.0)
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_EntityConstructor()
    {
        string source = """
                        routine test()
                          var user = User(name: "Alice", age: 30)
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_NestedConstructor()
    {
        string source = """
                        routine test()
                          var circle = Circle(center: Point(x: 0.0, y: 0.0), radius: 10.0)
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_GenericConstructor()
    {
        string source = """
                        routine test()
                          var container = Container[S32](value: 42)
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Lambda and Closure Tests

    [Fact]
    public void Parse_SimpleLambda()
    {
        string source = """
                        routine test()
                          var add = (a, b) => a + b
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_SingleParamLambda()
    {
        string source = """
                        routine test()
                          var double = x => x * 2
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LambdaAsArgument()
    {
        string source = """
                        routine test()
                          items.select(x => x * 2)
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    // TODO: This should NOT parse.
    public void Parse_LambdaWithCapture()
    {
        string source = """
                        routine test()
                          var multiplier = 10
                          var scale = x => x * multiplier
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LambdaWithGivenClause_SingleCapture()
    {
        string source = """
                        routine test()
                          var lo = 0
                          var hi = 100
                          var in_range = x given lo => lo <= x
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LambdaWithGivenClause_MultipleCaptures()
    {
        string source = """
                        routine test()
                          var lo = 0
                          var hi = 100
                          var in_range = x given (lo, hi) => lo <= x and x < hi
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LambdaWithGivenClause_ZeroParams()
    {
        string source = """
                        routine test()
                          var value = 42
                          var getter = () given value => value
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LambdaWithGivenClause_ParenthesizedParams()
    {
        string source = """
                        routine test()
                          var scale = 2
                          var multiply = (x, y) given scale => (x + y) * scale
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LambdaInvalid_CommaBeforeGiven()
    {
        // x, given y => x + y - invalid, comma before 'given' breaks parsing
        string source = """
                        routine test()
                          var f = x, given y => x + y
                          return
                        """;

        AssertParseError(source: source);
    }

    [Fact]
    public void Parse_LambdaInvalid_TrailingCommaInGiven()
    {
        // x given y, => x + y - invalid, trailing comma after capture
        string source = """
                        routine test()
                          var f = x given y, => x + y
                          return
                        """;

        AssertParseError(source: source);
    }

    [Fact]
    public void Parse_LambdaInvalid_MultipleUnparenthesizedCaptures()
    {
        // x given y,z => x+y+z - invalid, multiple captures need parentheses
        string source = """
                        routine test()
                          var f = x given y,z => x + y + z
                          return
                        """;

        AssertParseError(source: source);
    }

    [Fact]
    public void Parse_LambdaInvalid_MultipleUnparenthesizedParams()
    {
        // x, y given z => x+y+z - invalid, multiple params need parentheses
        string source = """
                        routine test()
                          var f = x, y given z => x + y + z
                          return
                        """;

        AssertParseError(source: source);
    }

    #endregion

    #region String Interpolation Tests

    [Fact]
    public void Parse_SimpleInterpolation()
    {
        string source = """
                        routine test()
                          var msg = f"Hello, {name}!"
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_InterpolationWithExpression()
    {
        string source = """
                        routine test()
                          var msg = f"Sum: {a + b}"
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_MultipleInterpolations()
    {
        string source = """
                        routine test()
                          var msg = f"{first} + {second} = {first + second}"
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_InterpolationWithMethodCall()
    {
        string source = """
                        routine test()
                          var msg = f"Name: {user.name.to_upper()}"
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_InterpolationWithFormatting()
    {
        string source = """
                        routine test()
                          var msg = f"Value: {value:0.2f}"
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Type Conversion Tests

    [Fact]
    public void Parse_TypeConversionMethod()
    {
        string source = """
                        routine test()
                          var x = value.S64!()
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_TypeConversionFromLiteral()
    {
        string source = """
                        routine test()
                          var x = 42.F64()
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Parenthesized Expression Tests

    [Fact]
    public void Parse_ParenthesizedExpression()
    {
        string source = """
                        routine test()
                          var result = (a + b) * c
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_NestedParentheses()
    {
        string source = """
                        routine test()
                          var result = ((a + b) * (c - d)) / e
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Range Expression Tests

    [Fact]
    public void Parse_RangeExpression()
    {
        string source = """
                        routine test()
                          var range = 0 til 10
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_RangeExpressionWithStep()
    {
        string source = """
                        routine test()
                          var range = 0 til 100 by 5
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Complex Expression Tests

    [Fact]
    public void Parse_ComplexChainedExpression()
    {
        string source = """
                        routine test()
                          var result = items
                          .where(x => x > 0)
                          .select(x => x * 2)
                          .take(10)
                          .to_list()
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_ConditionalExpression()
    {
        string source = """
                        routine test()
                          var value = if condition then compute_a() else compute_b()
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WhenAsExpression()
    {
        string source = """
                        routine test()
                          var description = when status
                            is PENDING => "Waiting"
                            is ACTIVE => "Running"
                            else => "Unknown"
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_NoneCoalescingChain()
    {
        string source = """
                        routine test()
                          var value = first_option() ?? second_option() ?? default_value
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Variant Construction Tests

    [Fact]
    public void Parse_VariantConstruction()
    {
        string source = """
                        routine test()
                          var msg = Message.TEXT("Hello")
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_VariantWithoutPayload()
    {
        string source = """
                        routine test()
                          var msg = Message.QUIT
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Choice Value Tests

    [Fact]
    public void Parse_ChoiceValue()
    {
        string source = """
                        routine test()
                          var status = Status.ACTIVE
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Unary vs Binary Operator Tests

    /// <summary>
    /// Verifies that binary subtraction is parsed correctly (not as unary minus).
    /// 3 - 2 should be BinaryExpression(3, Subtract, 2), NOT "3" and "-2".
    /// </summary>
    [Fact]
    public void Parse_BinarySubtraction_NotUnaryMinus()
    {
        string source = """
                        routine test()
                          var result = 3 - 2
                          return
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<SyntaxTree.RoutineDeclaration>().First();
        var block = (SyntaxTree.BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<SyntaxTree.DeclarationStatement>().First();
        var varDeclTyped = (SyntaxTree.VariableDeclaration)varDecl.Declaration;
        var binary = varDeclTyped.Initializer as SyntaxTree.BinaryExpression;

        Assert.NotNull(binary);
        Assert.Equal(BinaryOperator.Subtract, binary!.Operator);

        // Left operand should be "3", not "-3"
        var left = binary.Left as SyntaxTree.LiteralExpression;
        Assert.NotNull(left);
        Assert.Equal("3", left!.Value);

        // Right operand should be "2", not "-2"
        var right = binary.Right as SyntaxTree.LiteralExpression;
        Assert.NotNull(right);
        Assert.Equal("2", right!.Value);
    }

    /// <summary>
    /// Verifies that unary minus on a literal is parsed correctly.
    /// -2 should be a single literal "-2".
    /// </summary>
    [Fact]
    public void Parse_UnaryMinus_OnLiteral()
    {
        string source = """
                        routine test()
                          var result = -2
                          return
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<SyntaxTree.RoutineDeclaration>().First();
        var block = (SyntaxTree.BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<SyntaxTree.DeclarationStatement>().First();
        var literal = ((SyntaxTree.VariableDeclaration)varDecl.Declaration).Initializer as SyntaxTree.LiteralExpression;

        Assert.NotNull(literal);
        Assert.Equal("-2", literal!.Value);
    }

    /// <summary>
    /// Verifies that binary subtraction with imaginary literals is parsed correctly.
    /// 3 - 2j should be BinaryExpression(3, Subtract, 2j), NOT "3" and "-2j".
    /// </summary>
    [Fact]
    public void Parse_BinarySubtraction_WithImaginaryLiteral()
    {
        string source = """
                        routine test()
                          var result = 3 - 2j
                          return
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<SyntaxTree.RoutineDeclaration>().First();
        var block = (SyntaxTree.BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<SyntaxTree.DeclarationStatement>().First();
        var binary = ((SyntaxTree.VariableDeclaration)varDecl.Declaration).Initializer as SyntaxTree.BinaryExpression;

        Assert.NotNull(binary);
        Assert.Equal(BinaryOperator.Subtract, binary!.Operator);

        // Left operand should be "3"
        var left = binary.Left as SyntaxTree.LiteralExpression;
        Assert.NotNull(left);
        Assert.Equal("3", left!.Value);

        // Right operand should be "2j", NOT "-2j"
        var right = binary.Right as SyntaxTree.LiteralExpression;
        Assert.NotNull(right);
        Assert.Equal("2j", right!.Value);
    }

    /// <summary>
    /// Verifies complex expression with both unary minus and binary subtraction.
    /// -3 - 2 should be BinaryExpression(-3, Subtract, 2).
    /// </summary>
    [Fact]
    public void Parse_UnaryMinusAndBinarySubtraction()
    {
        string source = """
                        routine test()
                          var result = -3 - 2
                          return
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<SyntaxTree.RoutineDeclaration>().First();
        var block = (SyntaxTree.BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<SyntaxTree.DeclarationStatement>().First();
        var binary = ((SyntaxTree.VariableDeclaration)varDecl.Declaration).Initializer as SyntaxTree.BinaryExpression;

        Assert.NotNull(binary);
        Assert.Equal(BinaryOperator.Subtract, binary!.Operator);

        // Left operand should be "-3" (unary minus applied til literal)
        var left = binary.Left as SyntaxTree.LiteralExpression;
        Assert.NotNull(left);
        Assert.Equal("-3", left!.Value);

        // Right operand should be "2", NOT "-2"
        var right = binary.Right as SyntaxTree.LiteralExpression;
        Assert.NotNull(right);
        Assert.Equal("2", right!.Value);
    }

    /// <summary>
    /// Verifies binary addition is not affected by unary minus handling.
    /// 3 + 2 should be BinaryExpression(3, Add, 2).
    /// </summary>
    [Fact]
    public void Parse_BinaryAddition_NotAffectedByUnaryHandling()
    {
        string source = """
                        routine test()
                          var result = 3 + 2
                          return
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<SyntaxTree.RoutineDeclaration>().First();
        var block = (SyntaxTree.BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<SyntaxTree.DeclarationStatement>().First();
        var binary = ((SyntaxTree.VariableDeclaration)varDecl.Declaration).Initializer as SyntaxTree.BinaryExpression;

        Assert.NotNull(binary);
        Assert.Equal(BinaryOperator.Add, binary!.Operator);

        // Left operand should be "3"
        var left = binary.Left as SyntaxTree.LiteralExpression;
        Assert.NotNull(left);
        Assert.Equal("3", left!.Value);

        // Right operand should be "2"
        var right = binary.Right as SyntaxTree.LiteralExpression;
        Assert.NotNull(right);
        Assert.Equal("2", right!.Value);
    }

    #endregion

    #region Byte Literal Tests

    [Fact]
    public void Parse_ByteStringLiteral()
    {
        string source = """
                        routine test()
                          var data = b"hello"
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_ByteCharLiteral()
    {
        string source = """
                        routine test()
                          var ch = b'A'
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_ByteStringHexEscape()
    {
        string source = """
                        routine test()
                          var data = b"\x48\x65\x6C\x6C\x6F"
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_ByteRawStringLiteral()
    {
        string source = """
                        routine test()
                          var data = br"no escapes here \n"
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Tokenize_ByteStringLiteral_CorrectTokenType()
    {
        string source = """b"hello" """;
        List<Compiler.Lexer.Token> tokens = Tokenize(source: source);

        Assert.Equal(expected: Compiler.Lexer.TokenType.BytesLiteral,
            actual: tokens[index: 0].Type);
    }

    [Fact]
    public void Tokenize_ByteCharLiteral_CorrectTokenType()
    {
        string source = """b'A' """;
        List<Compiler.Lexer.Token> tokens = Tokenize(source: source);

        Assert.Equal(expected: Compiler.Lexer.TokenType.ByteLetterLiteral,
            actual: tokens[index: 0].Type);
    }

    [Fact]
    public void Tokenize_ByteStringNonAscii_ThrowsError()
    {
        string source = "b\"\uD55C\uAD6D\""; // b"한국" - non-ASCII

        Assert.ThrowsAny<Exception>(testCode: () => Tokenize(source: source));
    }

    [Fact]
    public void Tokenize_ByteStringUnicodeEscape_ThrowsError()
    {
        string source = """b"\u00E9" """;

        Assert.ThrowsAny<Exception>(testCode: () => Tokenize(source: source));
    }

    [Fact]
    public void Tokenize_ByteCharNonAscii_ThrowsError()
    {
        string source = "b'\u00E9'"; // b'é' - non-ASCII

        Assert.ThrowsAny<Exception>(testCode: () => Tokenize(source: source));
    }

    [Fact]
    public void Tokenize_UnicodeEscape_Exactly6Digits()
    {
        // \u00004E is exactly 6 hex digits — valid
        string source = "\"\\u00004E\"";

        List<Compiler.Lexer.Token> tokens = Tokenize(source: source);
        Assert.Equal(expected: Compiler.Lexer.TokenType.TextLiteral,
            actual: tokens[index: 0].Type);
    }

    [Fact]
    public void Tokenize_UnicodeEscape_TooFewDigits_ThrowsError()
    {
        // \u00E9 is only 4 hex digits — should fail (need exactly 6)
        string source = "\"\\u00E9\"";

        Assert.ThrowsAny<Exception>(testCode: () => Tokenize(source: source));
    }

    [Fact]
    public void Tokenize_UnicodeEscape_TwoDigits_ThrowsError()
    {
        // \u41 is only 2 hex digits — should fail
        string source = "\"\\u41\"";

        Assert.ThrowsAny<Exception>(testCode: () => Tokenize(source: source));
    }

    #endregion

    #region With Expression Tests

    [Fact]
    public void Parse_WithFieldUpdate()
    {
        string source = """
                        routine test()
                          var p2 = p1 with .x = 5.0
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WithMultipleFields()
    {
        string source = """
                        routine test()
                          var p2 = p1 with .x = 5.0, .y = 3.0
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WithIndexUpdate()
    {
        string source = """
                        routine test()
                          var c2 = coords with [0] = 5.0
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WithNestedField()
    {
        string source = """
                        routine test()
                          var p2 = person with .address.city = "Shelbyville"
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WithMixedUpdates()
    {
        string source = """
                        routine test()
                          var p2 = data with .name = "test", [0] = 42
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WithExpression_ASTStructure()
    {
        string source = """
                        routine test()
                          var p2 = p1 with .x = 5.0, .y = 3.0
                          return
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<SyntaxTree.RoutineDeclaration>().First();
        var block = (SyntaxTree.BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<SyntaxTree.DeclarationStatement>().First();
        var withExpr = ((SyntaxTree.VariableDeclaration)varDecl.Declaration).Initializer
            as SyntaxTree.WithExpression;

        Assert.NotNull(withExpr);
        Assert.Equal(expected: 2, actual: withExpr!.Updates.Count);
        Assert.Equal(expected: "x", actual: withExpr.Updates[0].FieldPath![0]);
        Assert.Equal(expected: "y", actual: withExpr.Updates[1].FieldPath![0]);
        Assert.Null(withExpr.Updates[0].Index);
        Assert.Null(withExpr.Updates[1].Index);
    }

    [Fact]
    public void Parse_WithExpression_NestedFieldPath_ASTStructure()
    {
        string source = """
                        routine test()
                          var p2 = person with .address.city = "NYC"
                          return
                        """;

        var ast = Parse(source: source);
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

    [Fact]
    public void Parse_WithExpression_IndexUpdate_ASTStructure()
    {
        string source = """
                        routine test()
                          var c2 = coords with [0] = 5.0
                          return
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<SyntaxTree.RoutineDeclaration>().First();
        var block = (SyntaxTree.BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<SyntaxTree.DeclarationStatement>().First();
        var withExpr = ((SyntaxTree.VariableDeclaration)varDecl.Declaration).Initializer
            as SyntaxTree.WithExpression;

        Assert.NotNull(withExpr);
        Assert.Single(withExpr!.Updates);
        Assert.Null(withExpr.Updates[0].FieldPath);
        Assert.NotNull(withExpr.Updates[0].Index);
    }

    #endregion

    #region Slice Expression Tests

    [Fact]
    public void Parse_SliceExpression()
    {
        string source = """
                        routine test()
                          var sub = list[0 til 5]
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_SliceExpressionWithBackIndex()
    {
        string source = """
                        routine test()
                          var sub = list[1 til ^1]
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_SliceExpression_ASTStructure()
    {
        string source = """
                        routine test()
                          var sub = list[0 til 5]
                          return
                        """;

        var ast = Parse(source: source);
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
    public void Parse_SliceExpression_TilRange()
    {
        // til is supported in slices (exclusive end)
        string source = """
                        routine test()
                          var sub = list[5 til 0]
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_SliceExpression_RejectsStep()
    {
        string source = """
                        routine test()
                          var sub = list[0 til 10 by 2]
                          return
                        """;

        AssertParseError(source: source);
    }

    [Fact]
    public void Parse_RegularIndexExpression_StillWorks()
    {
        string source = """
                        routine test()
                          var item = list[5]
                          return
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<SyntaxTree.RoutineDeclaration>().First();
        var block = (SyntaxTree.BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<SyntaxTree.DeclarationStatement>().First();
        var initializer = ((SyntaxTree.VariableDeclaration)varDecl.Declaration).Initializer;

        Assert.IsType<SyntaxTree.IndexExpression>(initializer);
    }

    #endregion
}
