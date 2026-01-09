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
}
