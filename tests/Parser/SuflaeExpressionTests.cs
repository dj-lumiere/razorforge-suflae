using Xunit;

namespace RazorForge.Tests.Parser;

using Compilers.Shared.AST;
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
                        routine test():
                            show("hello")
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_MethodCallWithMultipleArgs()
    {
        string source = """
                        routine test():
                            compute(1, 2, 3)
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_MethodCallWithNamedArgs()
    {
        string source = """
                        routine test():
                            create_user(name: "Alice", age: 30)
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_MethodCallChain()
    {
        string source = """
                        routine test():
                            data.where().select().to_list()
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_MethodCallOnLiteral()
    {
        string source = """
                        routine test():
                            let len = "hello".length()
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_MethodCallWithConversion()
    {
        string source = """
                        routine test():
                            let result = "42".S32!()
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_StaticMethodCall()
    {
        string source = """
                        routine test():
                            let pi = Math.pi()
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    #endregion

    #region Field Access Tests

    [Fact]
    public void ParseSuflae_SimpleFieldAccess()
    {
        string source = """
                        routine test():
                            let x = point.x
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ChainedFieldAccess()
    {
        string source = """
                        routine test():
                            let city = user.address.city
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_MixedFieldAndMethodAccess()
    {
        string source = """
                        routine test():
                            let result = user.name.to_upper().length()
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_MeFieldAccess()
    {
        string source = """
                        @readonly
                        routine Point.get_x() -> F32:
                            return me.x
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    #endregion

    #region Indexing Tests

    [Fact]
    public void ParseSuflae_ArrayIndexing()
    {
        string source = """
                        routine test():
                            let first = items[0]
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_MultiDimensionalIndexing()
    {
        string source = """
                        routine test():
                            let cell = matrix[i][j]
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_DictIndexing()
    {
        string source = """
                        routine test():
                            let value = dict["key"]
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_SliceExpression()
    {
        string source = """
                        routine test():
                            let slice = items[1..5]
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_SliceFromStart()
    {
        string source = """
                        routine test():
                            let first_five = items[..5]
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_SliceToEnd()
    {
        string source = """
                        routine test():
                            let rest = items[5..]
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_IndexAssignment()
    {
        string source = """
                        routine test():
                            var items = [1, 2, 3]
                            items[0] = 42
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void ParseSuflae_RecordConstructor()
    {
        string source = """
                        routine test():
                            let point = Point(x: 10.0, y: 20.0)
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_EntityConstructor()
    {
        string source = """
                        routine test():
                            let user = User(name: "Alice", age: 30)
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_NestedConstructor()
    {
        string source = """
                        routine test():
                            let circle = Circle(center: Point(x: 0.0, y: 0.0), radius: 10.0)
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_GenericConstructor()
    {
        string source = """
                        routine test():
                            let container = Container<Integer>(value: 42)
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    #endregion

    #region Lambda Tests

    [Fact]
    public void ParseSuflae_SimpleLambda()
    {
        string source = """
                        routine test():
                            let add = (a, b) => a + b
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_SingleParamLambda()
    {
        string source = """
                        routine test():
                            let double = x => x * 2
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LambdaAsArgument()
    {
        string source = """
                        routine test():
                            items.select(x => x * 2)
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LambdaWithCapture()
    {
        string source = """
                        routine test():
                            let multiplier = 10
                            let scale = x => x * multiplier
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_NoParamLambda()
    {
        string source = """
                        routine test():
                            let get_value = () => 42
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    #endregion

    #region String Interpolation Tests

    [Fact]
    public void ParseSuflae_SimpleInterpolation()
    {
        string source = """
                        routine test():
                            let msg = f"Hello, {name}!"
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_InterpolationWithExpression()
    {
        string source = """
                        routine test():
                            let msg = f"Sum: {a + b}"
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_MultipleInterpolations()
    {
        string source = """
                        routine test():
                            let msg = f"{first} + {second} = {first + second}"
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_InterpolationWithMethodCall()
    {
        string source = """
                        routine test():
                            let msg = f"Name: {user.name.to_upper()}"
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    #endregion

    #region Type Conversion Tests

    [Fact]
    public void ParseSuflae_TypeConversionMethod()
    {
        string source = """
                        routine test():
                            let x = value.S64!()
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_TypeConversionFromLiteral()
    {
        string source = """
                        routine test():
                            let x = 42.F64()
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ChainedTypeConversion()
    {
        string source = """
                        routine test():
                            let result = "42".S32!().F64!()
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    #endregion

    #region Parenthesized Expression Tests

    [Fact]
    public void ParseSuflae_ParenthesizedExpression()
    {
        string source = """
                        routine test():
                            let result = (a + b) * c
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_NestedParentheses()
    {
        string source = """
                        routine test():
                            let result = ((a + b) * (c - d)) / e
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    #endregion

    #region Range Expression Tests

    [Fact]
    public void ParseSuflae_RangeExpression()
    {
        string source = """
                        routine test():
                            let range = 0 to 10
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_RangeExpressionWithStep()
    {
        string source = """
                        routine test():
                            let range = 0 to 100 by 5
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    #endregion

    #region Complex Expression Tests

    [Fact]
    public void ParseSuflae_ComplexChainedExpression()
    {
        string source = """
                        routine test():
                            let result = items.where(x => x > 0).select(x => x * 2).take(10).to_list()
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ConditionalExpression()
    {
        string source = """
                        routine test():
                            let value = if condition then compute_a() else compute_b()
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenAsExpression()
    {
        string source = """
                        routine test():
                            let description = when status:
                                is PENDING => "Waiting"
                                is ACTIVE => "Running"
                                else => "Unknown"
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_NoneCoalescingChain()
    {
        string source = """
                        routine test():
                            let value = first_option() ?? second_option() ?? default_value
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    #endregion

    #region Variant Construction Tests

    [Fact]
    public void ParseSuflae_VariantConstruction()
    {
        string source = """
                        routine test():
                            let msg = Message.TEXT("Hello")
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_VariantWithoutPayload()
    {
        string source = """
                        routine test():
                            let msg = Message.QUIT
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_VariantImmediatePatternMatch()
    {
        string source = """
                        routine test():
                            let result = parse_number("123")
                            when result:
                                is SUCCESS value:
                                    show(f"Got: {value}")
                                is ERROR msg:
                                    show(f"Error: {msg}")
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    #endregion

    #region Choice Value Tests

    [Fact]
    public void ParseSuflae_ChoiceValue()
    {
        string source = """
                        routine test():
                            let status = AppStatus.ACTIVE
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ChoiceMethodCall()
    {
        string source = """
                        routine test():
                            let dir = Direction.NORTH
                            let opposite = dir.opposite()
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ChoiceEquality()
    {
        string source = """
                        routine test():
                            let status = FileAccess.READ
                            if status == READ:
                                show("Read access")
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    #endregion

    #region Routine Type Tests

    [Fact]
    public void ParseSuflae_RoutineTypeVariable()
    {
        string source = """
                        routine test():
                            let add: Routine<S32, S32, S32> = (a, b) => a + b
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_RoutineTypeNoParams()
    {
        string source = """
                        routine test():
                            let get_value: Routine<S32> = () => 42
                        """;

        Program program = AssertParsesSuflae(source: source);
    }

    #endregion
}
