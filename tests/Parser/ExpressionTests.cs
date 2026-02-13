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
                        routine test() {
                            show("hello")
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_MethodCallWithMultipleArgs()
    {
        string source = """
                        routine test() {
                            compute(1, 2, 3)
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_MethodCallWithNamedArgs()
    {
        string source = """
                        routine test() {
                            create_user(name: "Alice", age: 30)
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_MethodCallChain()
    {
        string source = """
                        routine test() {
                            data.where().select().to_list()
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_MethodCallOnLiteral()
    {
        string source = """
                        routine test() {
                            let len = "hello".length()
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_MethodCallWithConversion()
    {
        string source = """
                        routine test() {
                            let result = "42".S32!()
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_StaticMethodCall()
    {
        string source = """
                        routine test() {
                            let pi = Math.pi()
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Field Access Tests

    [Fact]
    public void Parse_SimpleFieldAccess()
    {
        string source = """
                        routine test() {
                            let x = point.x
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_ChainedFieldAccess()
    {
        string source = """
                        routine test() {
                            let city = user.address.city
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_MixedFieldAndMethodAccess()
    {
        string source = """
                        routine test() {
                            let result = user.name.to_upper().length()
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_MeFieldAccess()
    {
        string source = """
                        @readonly
                        routine Point.get_x() -> F32 {
                            return me.x
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Indexing Tests

    [Fact]
    public void Parse_ArrayIndexing()
    {
        string source = """
                        routine test() {
                            let first = items[0]
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_MultiDimensionalIndexing()
    {
        string source = """
                        routine test() {
                            let cell = matrix[i][j]
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_DictIndexing()
    {
        string source = """
                        routine test() {
                            let value = dict["key"]
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_IndexAssignment()
    {
        string source = """
                        routine test() {
                            var items = [1, 2, 3]
                            items[0] = 42
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void Parse_RecordConstructor()
    {
        string source = """
                        routine test() {
                            let point = Point(x: 10.0, y: 20.0)
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_EntityConstructor()
    {
        string source = """
                        routine test() {
                            let user = User(name: "Alice", age: 30)
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_NestedConstructor()
    {
        string source = """
                        routine test() {
                            let circle = Circle(center: Point(x: 0.0, y: 0.0), radius: 10.0)
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_GenericConstructor()
    {
        string source = """
                        routine test() {
                            let container = Container<S32>(value: 42)
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Lambda and Closure Tests

    [Fact]
    public void Parse_SimpleLambda()
    {
        string source = """
                        routine test() {
                            let add = (a, b) => a + b
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_SingleParamLambda()
    {
        string source = """
                        routine test() {
                            let double = x => x * 2
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LambdaAsArgument()
    {
        string source = """
                        routine test() {
                            items.select(x => x * 2)
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    // TODO: This should NOT parse.
    public void Parse_LambdaWithCapture()
    {
        string source = """
                        routine test() {
                            let multiplier = 10
                            let scale = x => x * multiplier
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LambdaWithGivenClause_SingleCapture()
    {
        string source = """
                        routine test() {
                            let lo = 0
                            let hi = 100
                            let in_range = x given lo => lo <= x
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LambdaWithGivenClause_MultipleCaptures()
    {
        string source = """
                        routine test() {
                            let lo = 0
                            let hi = 100
                            let in_range = x given (lo, hi) => lo <= x and x < hi
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LambdaWithGivenClause_ZeroParams()
    {
        string source = """
                        routine test() {
                            let value = 42
                            let getter = () given value => value
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LambdaWithGivenClause_ParenthesizedParams()
    {
        string source = """
                        routine test() {
                            let scale = 2
                            let multiply = (x, y) given scale => (x + y) * scale
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LambdaInvalid_CommaBeforeGiven()
    {
        // x, given y => x + y - invalid, comma before 'given' breaks parsing
        string source = """
                        routine test() {
                            let f = x, given y => x + y
                        }
                        """;

        AssertParseError(source: source);
    }

    [Fact]
    public void Parse_LambdaInvalid_TrailingCommaInGiven()
    {
        // x given y, => x + y - invalid, trailing comma after capture
        string source = """
                        routine test() {
                            let f = x given y, => x + y
                        }
                        """;

        AssertParseError(source: source);
    }

    [Fact]
    public void Parse_LambdaInvalid_MultipleUnparenthesizedCaptures()
    {
        // x given y,z => x+y+z - invalid, multiple captures need parentheses
        string source = """
                        routine test() {
                            let f = x given y,z => x + y + z
                        }
                        """;

        AssertParseError(source: source);
    }

    [Fact]
    public void Parse_LambdaInvalid_MultipleUnparenthesizedParams()
    {
        // x, y given z => x+y+z - invalid, multiple params need parentheses
        string source = """
                        routine test() {
                            let f = x, y given z => x + y + z
                        }
                        """;

        AssertParseError(source: source);
    }

    #endregion

    #region String Interpolation Tests

    [Fact]
    public void Parse_SimpleInterpolation()
    {
        string source = """
                        routine test() {
                            let msg = f"Hello, {name}!"
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_InterpolationWithExpression()
    {
        string source = """
                        routine test() {
                            let msg = f"Sum: {a + b}"
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_MultipleInterpolations()
    {
        string source = """
                        routine test() {
                            let msg = f"{first} + {second} = {first + second}"
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_InterpolationWithMethodCall()
    {
        string source = """
                        routine test() {
                            let msg = f"Name: {user.name.to_upper()}"
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_InterpolationWithFormatting()
    {
        string source = """
                        routine test() {
                            let msg = f"Value: {value:0.2f}"
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Type Conversion Tests

    [Fact]
    public void Parse_TypeConversionMethod()
    {
        string source = """
                        routine test() {
                            let x = value.S64!()
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_TypeConversionFromLiteral()
    {
        string source = """
                        routine test() {
                            let x = 42.F64()
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Parenthesized Expression Tests

    [Fact]
    public void Parse_ParenthesizedExpression()
    {
        string source = """
                        routine test() {
                            let result = (a + b) * c
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_NestedParentheses()
    {
        string source = """
                        routine test() {
                            let result = ((a + b) * (c - d)) / e
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Range Expression Tests

    [Fact]
    public void Parse_RangeExpression()
    {
        string source = """
                        routine test() {
                            let range = 0 to 10
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_RangeExpressionWithStep()
    {
        string source = """
                        routine test() {
                            let range = 0 to 100 by 5
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Complex Expression Tests

    [Fact]
    public void Parse_ComplexChainedExpression()
    {
        string source = """
                        routine test() {
                            let result = items
                                .where(x => x > 0)
                                .select(x => x * 2)
                                .take(10)
                                .to_list()
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_ConditionalExpression()
    {
        string source = """
                        routine test() {
                            let value = if condition then compute_a() else compute_b()
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WhenAsExpression()
    {
        string source = """
                        routine test() {
                            let description = when status {
                                is PENDING => "Waiting"
                                is ACTIVE => "Running"
                                else => "Unknown"
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_NoneCoalescingChain()
    {
        string source = """
                        routine test() {
                            let value = first_option() ?? second_option() ?? default_value
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Variant Construction Tests

    [Fact]
    public void Parse_VariantConstruction()
    {
        string source = """
                        routine test() {
                            let msg = Message.TEXT("Hello")
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_VariantWithoutPayload()
    {
        string source = """
                        routine test() {
                            let msg = Message.QUIT
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Choice Value Tests

    [Fact]
    public void Parse_ChoiceValue()
    {
        string source = """
                        routine test() {
                            let status = Status.ACTIVE
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Unary vs Binary Operator Tests

    /// <summary>
    /// Verifies that binary subtraction is parsed correctly (not as unary minus).
    /// 3 - 2 should be desugared to 3.__sub__(2), NOT "3" and "-2".
    /// Note: Arithmetic operators are desugared to method calls for operator overloading.
    /// </summary>
    [Fact]
    public void Parse_BinarySubtraction_NotUnaryMinus()
    {
        string source = """
                        routine test() {
                            let result = 3 - 2
                        }
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<Compilers.Shared.AST.RoutineDeclaration>().First();
        var block = (Compilers.Shared.AST.BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<Compilers.Shared.AST.DeclarationStatement>().First();
        var varDeclTyped = (Compilers.Shared.AST.VariableDeclaration)varDecl.Declaration;
        var call = varDeclTyped.Initializer as Compilers.Shared.AST.CallExpression;

        // Arithmetic operators are desugared: 3 - 2 => 3.__sub__(2)
        Assert.NotNull(call);
        var memberExpr = call!.Callee as Compilers.Shared.AST.MemberExpression;
        Assert.NotNull(memberExpr);
        Assert.Equal("__sub__", memberExpr!.PropertyName);

        // Left operand (receiver) should be "3", not "-3"
        var left = memberExpr.Object as Compilers.Shared.AST.LiteralExpression;
        Assert.NotNull(left);
        Assert.Equal("3", left!.Value);

        // Right operand (argument) should be "2", not "-2"
        Assert.Single(call.Arguments);
        var right = call.Arguments[0] as Compilers.Shared.AST.LiteralExpression;
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
                        routine test() {
                            let result = -2
                        }
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<Compilers.Shared.AST.RoutineDeclaration>().First();
        var block = (Compilers.Shared.AST.BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<Compilers.Shared.AST.DeclarationStatement>().First();
        var literal = ((Compilers.Shared.AST.VariableDeclaration)varDecl.Declaration).Initializer as Compilers.Shared.AST.LiteralExpression;

        Assert.NotNull(literal);
        Assert.Equal("-2", literal!.Value);
    }

    /// <summary>
    /// Verifies that binary subtraction with imaginary literals is parsed correctly.
    /// 3 - 2j should be desugared to 3.__sub__(2j), NOT "3" and "-2j".
    /// </summary>
    [Fact]
    public void Parse_BinarySubtraction_WithImaginaryLiteral()
    {
        string source = """
                        routine test() {
                            let result = 3 - 2j
                        }
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<Compilers.Shared.AST.RoutineDeclaration>().First();
        var block = (Compilers.Shared.AST.BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<Compilers.Shared.AST.DeclarationStatement>().First();
        var call = ((Compilers.Shared.AST.VariableDeclaration)varDecl.Declaration).Initializer as Compilers.Shared.AST.CallExpression;

        // Arithmetic operators are desugared: 3 - 2j => 3.__sub__(2j)
        Assert.NotNull(call);
        var memberExpr = call!.Callee as Compilers.Shared.AST.MemberExpression;
        Assert.NotNull(memberExpr);
        Assert.Equal("__sub__", memberExpr!.PropertyName);

        // Left operand (receiver) should be "3"
        var left = memberExpr.Object as Compilers.Shared.AST.LiteralExpression;
        Assert.NotNull(left);
        Assert.Equal("3", left!.Value);

        // Right operand (argument) should be "2j", NOT "-2j"
        Assert.Single(call.Arguments);
        var right = call.Arguments[0] as Compilers.Shared.AST.LiteralExpression;
        Assert.NotNull(right);
        Assert.Equal("2j", right!.Value);
    }

    /// <summary>
    /// Verifies complex expression with both unary minus and binary subtraction.
    /// -3 - 2 should be desugared to (-3).__sub__(2).
    /// </summary>
    [Fact]
    public void Parse_UnaryMinusAndBinarySubtraction()
    {
        string source = """
                        routine test() {
                            let result = -3 - 2
                        }
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<Compilers.Shared.AST.RoutineDeclaration>().First();
        var block = (Compilers.Shared.AST.BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<Compilers.Shared.AST.DeclarationStatement>().First();
        var call = ((Compilers.Shared.AST.VariableDeclaration)varDecl.Declaration).Initializer as Compilers.Shared.AST.CallExpression;

        Assert.NotNull(call);
        var memberExpr = call!.Callee as Compilers.Shared.AST.MemberExpression;
        Assert.NotNull(memberExpr);
        Assert.Equal("__sub__", memberExpr!.PropertyName);

        // Left operand (receiver) should be "-3" (unary minus applied to literal)
        var left = memberExpr.Object as Compilers.Shared.AST.LiteralExpression;
        Assert.NotNull(left);
        Assert.Equal("-3", left!.Value);

        // Right operand (argument) should be "2", NOT "-2"
        Assert.Single(call.Arguments);
        var right = call.Arguments[0] as Compilers.Shared.AST.LiteralExpression;
        Assert.NotNull(right);
        Assert.Equal("2", right!.Value);
    }

    /// <summary>
    /// Verifies binary addition is not affected by unary minus handling.
    /// 3 + 2 should be desugared to 3.__add__(2).
    /// </summary>
    [Fact]
    public void Parse_BinaryAddition_NotAffectedByUnaryHandling()
    {
        string source = """
                        routine test() {
                            let result = 3 + 2
                        }
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<Compilers.Shared.AST.RoutineDeclaration>().First();
        var block = (Compilers.Shared.AST.BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<Compilers.Shared.AST.DeclarationStatement>().First();
        var call = ((Compilers.Shared.AST.VariableDeclaration)varDecl.Declaration).Initializer as Compilers.Shared.AST.CallExpression;

        Assert.NotNull(call);
        var memberExpr = call!.Callee as Compilers.Shared.AST.MemberExpression;
        Assert.NotNull(memberExpr);
        Assert.Equal("__add__", memberExpr!.PropertyName);

        // Left operand should be "3"
        var left = memberExpr.Object as Compilers.Shared.AST.LiteralExpression;
        Assert.NotNull(left);
        Assert.Equal("3", left!.Value);

        // Right operand should be "2"
        Assert.Single(call.Arguments);
        var right = call.Arguments[0] as Compilers.Shared.AST.LiteralExpression;
        Assert.NotNull(right);
        Assert.Equal("2", right!.Value);
    }

    #endregion

    #region Byte Literal Tests

    [Fact]
    public void Parse_ByteStringLiteral()
    {
        string source = """
                        routine test() {
                            let data = b"hello"
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_ByteCharLiteral()
    {
        string source = """
                        routine test() {
                            let ch = b'A'
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_ByteStringHexEscape()
    {
        string source = """
                        routine test() {
                            let data = b"\x48\x65\x6C\x6C\x6F"
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_ByteRawStringLiteral()
    {
        string source = """
                        routine test() {
                            let data = br"no escapes here \n"
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Tokenize_ByteStringLiteral_CorrectTokenType()
    {
        string source = """b"hello" """;
        List<Compilers.Shared.Lexer.Token> tokens = Tokenize(source: source);

        Assert.Equal(expected: Compilers.Shared.Lexer.TokenType.BytesLiteral,
            actual: tokens[index: 0].Type);
    }

    [Fact]
    public void Tokenize_ByteCharLiteral_CorrectTokenType()
    {
        string source = """b'A' """;
        List<Compilers.Shared.Lexer.Token> tokens = Tokenize(source: source);

        Assert.Equal(expected: Compilers.Shared.Lexer.TokenType.ByteLetterLiteral,
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

        List<Compilers.Shared.Lexer.Token> tokens = Tokenize(source: source);
        Assert.Equal(expected: Compilers.Shared.Lexer.TokenType.TextLiteral,
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
                        routine test() {
                            let p2 = p1 with .x = 5.0
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WithMultipleFields()
    {
        string source = """
                        routine test() {
                            let p2 = p1 with .x = 5.0, .y = 3.0
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WithIndexUpdate()
    {
        string source = """
                        routine test() {
                            let c2 = coords with [0] = 5.0
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WithNestedField()
    {
        string source = """
                        routine test() {
                            let p2 = person with .address.city = "Shelbyville"
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WithMixedUpdates()
    {
        string source = """
                        routine test() {
                            let p2 = data with .name = "test", [0] = 42
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WithExpression_ASTStructure()
    {
        string source = """
                        routine test() {
                            let p2 = p1 with .x = 5.0, .y = 3.0
                        }
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<Compilers.Shared.AST.RoutineDeclaration>().First();
        var block = (Compilers.Shared.AST.BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<Compilers.Shared.AST.DeclarationStatement>().First();
        var withExpr = ((Compilers.Shared.AST.VariableDeclaration)varDecl.Declaration).Initializer
            as Compilers.Shared.AST.WithExpression;

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
                        routine test() {
                            let p2 = person with .address.city = "NYC"
                        }
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<Compilers.Shared.AST.RoutineDeclaration>().First();
        var block = (Compilers.Shared.AST.BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<Compilers.Shared.AST.DeclarationStatement>().First();
        var withExpr = ((Compilers.Shared.AST.VariableDeclaration)varDecl.Declaration).Initializer
            as Compilers.Shared.AST.WithExpression;

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
                        routine test() {
                            let c2 = coords with [0] = 5.0
                        }
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<Compilers.Shared.AST.RoutineDeclaration>().First();
        var block = (Compilers.Shared.AST.BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<Compilers.Shared.AST.DeclarationStatement>().First();
        var withExpr = ((Compilers.Shared.AST.VariableDeclaration)varDecl.Declaration).Initializer
            as Compilers.Shared.AST.WithExpression;

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
                        routine test() {
                            let sub = list[0 to 5]
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_SliceExpressionWithBackIndex()
    {
        string source = """
                        routine test() {
                            let sub = list[1 to ^1]
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_SliceExpression_ASTStructure()
    {
        string source = """
                        routine test() {
                            let sub = list[0 to 5]
                        }
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<Compilers.Shared.AST.RoutineDeclaration>().First();
        var block = (Compilers.Shared.AST.BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<Compilers.Shared.AST.DeclarationStatement>().First();
        var initializer = ((Compilers.Shared.AST.VariableDeclaration)varDecl.Declaration).Initializer;

        Assert.IsType<Compilers.Shared.AST.SliceExpression>(initializer);
        var slice = (Compilers.Shared.AST.SliceExpression)initializer!;
        Assert.IsType<Compilers.Shared.AST.IdentifierExpression>(slice.Object);
        Assert.IsType<Compilers.Shared.AST.LiteralExpression>(slice.Start);
        Assert.IsType<Compilers.Shared.AST.LiteralExpression>(slice.End);
    }

    [Fact]
    public void Parse_SliceExpression_RejectsDownto()
    {
        string source = """
                        routine test() {
                            let sub = list[5 downto 0]
                        }
                        """;

        AssertParseError(source: source);
    }

    [Fact]
    public void Parse_SliceExpression_RejectsStep()
    {
        string source = """
                        routine test() {
                            let sub = list[0 to 10 by 2]
                        }
                        """;

        AssertParseError(source: source);
    }

    [Fact]
    public void Parse_RegularIndexExpression_StillWorks()
    {
        string source = """
                        routine test() {
                            let item = list[5]
                        }
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<Compilers.Shared.AST.RoutineDeclaration>().First();
        var block = (Compilers.Shared.AST.BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<Compilers.Shared.AST.DeclarationStatement>().First();
        var initializer = ((Compilers.Shared.AST.VariableDeclaration)varDecl.Declaration).Initializer;

        Assert.IsType<Compilers.Shared.AST.IndexExpression>(initializer);
    }

    #endregion
}
