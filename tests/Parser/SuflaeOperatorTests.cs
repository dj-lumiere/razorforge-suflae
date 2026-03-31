using Xunit;

namespace RazorForge.Tests.Parser;

using static TestHelpers;

/// <summary>
/// Tests for parsing operators in Suflae:
/// arithmetic, wrapping, clamping, checked, comparison, logical, bitwise, none coalescing.
/// Suflae uses the same operators as RazorForge with indentation-based syntax.
/// </summary>
public class SuflaeOperatorTests
{
    #region Standard Arithmetic Operators
    /// <summary>
    /// Tests ParseSuflae_Addition.
    /// </summary>

    [Fact]
    public void ParseSuflae_Addition()
    {
        string source = """
                        routine test() -> Integer
                          return 1 + 2
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_Subtraction.
    /// </summary>

    [Fact]
    public void ParseSuflae_Subtraction()
    {
        string source = """
                        routine test() -> Integer
                          return 5 - 3
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_Multiplication.
    /// </summary>

    [Fact]
    public void ParseSuflae_Multiplication()
    {
        string source = """
                        routine test() -> Integer
                          return 4 * 5
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_Division.
    /// </summary>

    [Fact]
    public void ParseSuflae_Division()
    {
        string source = """
                        routine test() -> Decimal
                          return 10.0 / 3.0
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_FloorDivision.
    /// </summary>

    [Fact]
    public void ParseSuflae_FloorDivision()
    {
        string source = """
                        routine test() -> Integer
                          return 10 // 3
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_Remainder.
    /// </summary>

    [Fact]
    public void ParseSuflae_Remainder()
    {
        string source = """
                        routine test() -> Integer
                          return 10 % 3
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_Power.
    /// </summary>

    [Fact]
    public void ParseSuflae_Power()
    {
        string source = """
                        routine test() -> Integer
                          return 2 ** 10
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_Negation.
    /// </summary>

    [Fact]
    public void ParseSuflae_Negation()
    {
        string source = """
                        routine test() -> Integer
                          return -42
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_ChainedArithmetic.
    /// </summary>

    [Fact]
    public void ParseSuflae_ChainedArithmetic()
    {
        string source = """
                        routine test() -> Integer
                          return 1 + 2 * 3 - 4 // 2
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_ParenthesizedArithmetic.
    /// </summary>

    [Fact]
    public void ParseSuflae_ParenthesizedArithmetic()
    {
        string source = """
                        routine test() -> Integer
                          return (1 + 2) * (3 - 4)
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Wrapping Arithmetic Operators
    /// <summary>
    /// Tests ParseSuflae_WrappingAdd.
    /// </summary>

    [Fact]
    public void ParseSuflae_WrappingAdd()
    {
        string source = """
                        routine test() -> U8
                          var a: U8 = 250
                          var b: U8 = 10
                          return a +% b
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_WrappingSubtract.
    /// </summary>

    [Fact]
    public void ParseSuflae_WrappingSubtract()
    {
        string source = """
                        routine test() -> U8
                          var a: U8 = 5
                          var b: U8 = 10
                          return a -% b
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_WrappingMultiply.
    /// </summary>

    [Fact]
    public void ParseSuflae_WrappingMultiply()
    {
        string source = """
                        routine test() -> U8
                          var a: U8 = 20
                          var b: U8 = 20
                          return a *% b
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Clamping Arithmetic Operators
    /// <summary>
    /// Tests ParseSuflae_ClampingAdd.
    /// </summary>

    [Fact]
    public void ParseSuflae_ClampingAdd()
    {
        string source = """
                        routine test() -> U8
                          var a: U8 = 250
                          var b: U8 = 10
                          return a +^ b
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_ClampingSubtract.
    /// </summary>

    [Fact]
    public void ParseSuflae_ClampingSubtract()
    {
        string source = """
                        routine test() -> U8
                          var a: U8 = 5
                          var b: U8 = 10
                          return a -^ b
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Comparison Operators
    /// <summary>
    /// Tests ParseSuflae_Equal.
    /// </summary>

    [Fact]
    public void ParseSuflae_Equal()
    {
        string source = """
                        routine test() -> bool
                          return a == b
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_NotEqual.
    /// </summary>

    [Fact]
    public void ParseSuflae_NotEqual()
    {
        string source = """
                        routine test() -> bool
                          return a != b
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_LessThan.
    /// </summary>

    [Fact]
    public void ParseSuflae_LessThan()
    {
        string source = """
                        routine test() -> bool
                          return a < b
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_LessOrEqual.
    /// </summary>

    [Fact]
    public void ParseSuflae_LessOrEqual()
    {
        string source = """
                        routine test() -> bool
                          return a <= b
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_GreaterThan.
    /// </summary>

    [Fact]
    public void ParseSuflae_GreaterThan()
    {
        string source = """
                        routine test() -> bool
                          return a > b
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_GreaterOrEqual.
    /// </summary>

    [Fact]
    public void ParseSuflae_GreaterOrEqual()
    {
        string source = """
                        routine test() -> bool
                          return a >= b
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_ChainedComparison.
    /// </summary>

    [Fact]
    public void ParseSuflae_ChainedComparison()
    {
        string source = """
                        routine test() -> bool
                          return 0 <= index < length
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_ChainedRangeComparison.
    /// </summary>

    [Fact]
    public void ParseSuflae_ChainedRangeComparison()
    {
        string source = """
                        routine test() -> bool
                          return min <= value <= max
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Identity Operators
    /// <summary>
    /// Tests ParseSuflae_IdentityEqual.
    /// </summary>

    [Fact]
    public void ParseSuflae_IdentityEqual()
    {
        string source = """
                        routine test() -> bool
                          return a === b
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_IdentityNotEqual.
    /// </summary>

    [Fact]
    public void ParseSuflae_IdentityNotEqual()
    {
        string source = """
                        routine test() -> bool
                          return a !== b
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Logical Operators
    /// <summary>
    /// Tests ParseSuflae_LogicalAnd.
    /// </summary>

    [Fact]
    public void ParseSuflae_LogicalAnd()
    {
        string source = """
                        routine test() -> bool
                          return a and b
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_LogicalOr.
    /// </summary>

    [Fact]
    public void ParseSuflae_LogicalOr()
    {
        string source = """
                        routine test() -> bool
                          return a or b
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_LogicalNot.
    /// </summary>

    [Fact]
    public void ParseSuflae_LogicalNot()
    {
        string source = """
                        routine test() -> bool
                          return not a
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_ChainedLogical.
    /// </summary>

    [Fact]
    public void ParseSuflae_ChainedLogical()
    {
        string source = """
                        routine test() -> bool
                          return a and b or c and not d
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_LogicalWithComparison.
    /// </summary>

    [Fact]
    public void ParseSuflae_LogicalWithComparison()
    {
        string source = """
                        routine test() -> bool
                          return x > 0 and x < 100
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Bitwise Operators
    /// <summary>
    /// Tests ParseSuflae_BitwiseAnd.
    /// </summary>

    [Fact]
    public void ParseSuflae_BitwiseAnd()
    {
        string source = """
                        routine test() -> U32
                          return bits & mask
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_BitwiseOr.
    /// </summary>

    [Fact]
    public void ParseSuflae_BitwiseOr()
    {
        string source = """
                        routine test() -> U32
                          return bits | mask
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_BitwiseXor.
    /// </summary>

    [Fact]
    public void ParseSuflae_BitwiseXor()
    {
        string source = """
                        routine test() -> U32
                          return bits ^ mask
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_BitwiseNot.
    /// </summary>

    [Fact]
    public void ParseSuflae_BitwiseNot()
    {
        string source = """
                        routine test() -> U32
                          return ~bits
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_LeftShift.
    /// </summary>

    [Fact]
    public void ParseSuflae_LeftShift()
    {
        string source = """
                        routine test() -> U32
                          return value << 4
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_RightShift.
    /// </summary>

    [Fact]
    public void ParseSuflae_RightShift()
    {
        string source = """
                        routine test() -> U32
                          return value >> 4
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_LogicalLeftShift.
    /// </summary>

    [Fact]
    public void ParseSuflae_LogicalLeftShift()
    {
        string source = """
                        routine test() -> U32
                          return value <<< 4
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_LogicalRightShift.
    /// </summary>

    [Fact]
    public void ParseSuflae_LogicalRightShift()
    {
        string source = """
                        routine test() -> U32
                          return value >>> 4
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region None Coalescing Operator
    /// <summary>
    /// Tests ParseSuflae_NoneCoalescing.
    /// </summary>

    [Fact]
    public void ParseSuflae_NoneCoalescing()
    {
        string source = """
                        routine test() -> Integer
                          var value: Integer? = None
                          return value ?? 42
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_ChainedNoneCoalescing.
    /// </summary>

    [Fact]
    public void ParseSuflae_ChainedNoneCoalescing()
    {
        string source = """
                        routine test() -> Integer
                          var a: Integer? = none
                          var b: Integer? = none
                          var c: Integer = 100
                          return a ?? b ?? c
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_NoneCoalescingWithMethodCall.
    /// </summary>

    [Fact]
    public void ParseSuflae_NoneCoalescingWithMethodCall()
    {
        string source = """
                        routine test() -> User
                          return try_get_user(id) ?? default_user()
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Assignment Operators
    /// <summary>
    /// Tests ParseSuflae_SimpleAssignment.
    /// </summary>

    [Fact]
    public void ParseSuflae_SimpleAssignment()
    {
        string source = """
                        routine test()
                          var x = 0
                          x = 42
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_AddAssignment.
    /// </summary>

    [Fact]
    public void ParseSuflae_AddAssignment()
    {
        string source = """
                        routine test()
                          var x = 0
                          x += 1
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_SubtractAssignment.
    /// </summary>

    [Fact]
    public void ParseSuflae_SubtractAssignment()
    {
        string source = """
                        routine test()
                          var x = 10
                          x -= 1
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_MultiplyAssignment.
    /// </summary>

    [Fact]
    public void ParseSuflae_MultiplyAssignment()
    {
        string source = """
                        routine test()
                          var x = 2
                          x *= 3
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_BitwiseAndAssignment.
    /// </summary>

    [Fact]
    public void ParseSuflae_BitwiseAndAssignment()
    {
        string source = """
                        routine test()
                          var bits: U32 = 0xFF
                          bits &= 0x0F
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_BitwiseOrAssignment.
    /// </summary>

    [Fact]
    public void ParseSuflae_BitwiseOrAssignment()
    {
        string source = """
                        routine test()
                          var bits: U32 = 0x00
                          bits |= 0x0F
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_LeftShiftAssignment.
    /// </summary>

    [Fact]
    public void ParseSuflae_LeftShiftAssignment()
    {
        string source = """
                        routine test()
                          var x: U32 = 1
                          x <<= 4
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_RightShiftAssignment.
    /// </summary>

    [Fact]
    public void ParseSuflae_RightShiftAssignment()
    {
        string source = """
                        routine test()
                          var x: U32 = 16
                          x >>= 2
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Text Operators
    /// <summary>
    /// Tests ParseSuflae_TextConcatenation.
    /// </summary>

    [Fact]
    public void ParseSuflae_TextConcatenation()
    {
        string source = """
                        routine test() -> Text
                          return "Hello, " + "World!"
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_TextRepetition.
    /// </summary>

    [Fact]
    public void ParseSuflae_TextRepetition()
    {
        string source = """
                        routine test() -> Text
                          return "-" * 40
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Range Operators
    /// <summary>
    /// Tests ParseSuflae_InclusiveRange.
    /// </summary>

    [Fact]
    public void ParseSuflae_InclusiveRange()
    {
        string source = """
                        routine test()
                          for i in 0 til 10
                            show(i)
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_RangeWithStep.
    /// </summary>

    [Fact]
    public void ParseSuflae_RangeWithStep()
    {
        string source = """
                        routine test()
                          for i in 0 til 100 by 5
                            show(i)
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_DowntoRange.
    /// </summary>

    [Fact]
    public void ParseSuflae_DowntoRange()
    {
        string source = """
                        routine test()
                          for i in 10 til 0
                            show(i)
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Complex Operator Expressions
    /// <summary>
    /// Tests ParseSuflae_ComplexExpression.
    /// </summary>

    [Fact]
    public void ParseSuflae_ComplexExpression()
    {
        string source = """
                        routine test() -> Integer
                          return (a + b) * c - d // e % f ** g
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_MixedOperatorPrecedence.
    /// </summary>

    [Fact]
    public void ParseSuflae_MixedOperatorPrecedence()
    {
        string source = """
                        routine test() -> bool
                          return a + b > c * d and not (e == f or g != h)
                        """;

        AssertParsesSuflae(source: source);
    }
    /// <summary>
    /// Tests ParseSuflae_BitwiseWithComparison.
    /// </summary>

    [Fact]
    public void ParseSuflae_BitwiseWithComparison()
    {
        string source = """
                        routine test() -> bool
                          return (bits & mask) != 0
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion
}
