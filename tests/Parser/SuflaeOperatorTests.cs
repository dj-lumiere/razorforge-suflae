using Xunit;

namespace RazorForge.Tests.Parser;

using static TestHelpers;

/// <summary>
/// Tests for parsing operators in Suflae:
/// arithmetic, wrapping, saturating, checked, comparison, logical, bitwise, none coalescing.
/// Suflae uses the same operators as RazorForge with indentation-based syntax.
/// </summary>
public class SuflaeOperatorTests
{
    #region Standard Arithmetic Operators

    [Fact]
    public void ParseSuflae_Addition()
    {
        string source = """
                        routine test() -> Integer
                            return 1 + 2
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_Subtraction()
    {
        string source = """
                        routine test() -> Integer
                            return 5 - 3
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_Multiplication()
    {
        string source = """
                        routine test() -> Integer
                            return 4 * 5
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_Division()
    {
        string source = """
                        routine test() -> Decimal
                            return 10.0 / 3.0
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_FloorDivision()
    {
        string source = """
                        routine test() -> Integer
                            return 10 // 3
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_Remainder()
    {
        string source = """
                        routine test() -> Integer
                            return 10 % 3
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_Power()
    {
        string source = """
                        routine test() -> Integer
                            return 2 ** 10
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_Negation()
    {
        string source = """
                        routine test() -> Integer
                            return -42
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ChainedArithmetic()
    {
        string source = """
                        routine test() -> Integer
                            return 1 + 2 * 3 - 4 // 2
                        """;

        AssertParsesSuflae(source: source);
    }

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

    [Fact]
    public void ParseSuflae_WrappingAdd()
    {
        string source = """
                        routine test() -> U8
                            let a: U8 = 250
                            let b: U8 = 10
                            return a +% b
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WrappingSubtract()
    {
        string source = """
                        routine test() -> U8
                            let a: U8 = 5
                            let b: U8 = 10
                            return a -% b
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WrappingMultiply()
    {
        string source = """
                        routine test() -> U8
                            let a: U8 = 20
                            let b: U8 = 20
                            return a *% b
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Saturating Arithmetic Operators

    [Fact]
    public void ParseSuflae_SaturatingAdd()
    {
        string source = """
                        routine test() -> U8
                            let a: U8 = 250
                            let b: U8 = 10
                            return a +^ b
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_SaturatingSubtract()
    {
        string source = """
                        routine test() -> U8
                            let a: U8 = 5
                            let b: U8 = 10
                            return a -^ b
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Checked Arithmetic Operators

    [Fact]
    public void ParseSuflae_CheckedAdd()
    {
        string source = """
                        routine test() -> Integer?
                            let a: S32 = S32_MAX
                            let b: S32 = 1
                            return a +? b
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_CheckedDivision()
    {
        string source = """
                        routine test() -> Integer?
                            let a = 10
                            let b = 0
                            return a //? b
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Comparison Operators

    [Fact]
    public void ParseSuflae_Equal()
    {
        string source = """
                        routine test() -> bool
                            return a == b
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_NotEqual()
    {
        string source = """
                        routine test() -> bool
                            return a != b
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LessThan()
    {
        string source = """
                        routine test() -> bool
                            return a < b
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LessOrEqual()
    {
        string source = """
                        routine test() -> bool
                            return a <= b
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_GreaterThan()
    {
        string source = """
                        routine test() -> bool
                            return a > b
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_GreaterOrEqual()
    {
        string source = """
                        routine test() -> bool
                            return a >= b
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ChainedComparison()
    {
        string source = """
                        routine test() -> bool
                            return 0 <= index < length
                        """;

        AssertParsesSuflae(source: source);
    }

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

    [Fact]
    public void ParseSuflae_IdentityEqual()
    {
        string source = """
                        routine test() -> bool
                            return a === b
                        """;

        AssertParsesSuflae(source: source);
    }

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

    [Fact]
    public void ParseSuflae_LogicalAnd()
    {
        string source = """
                        routine test() -> bool
                            return a and b
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LogicalOr()
    {
        string source = """
                        routine test() -> bool
                            return a or b
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LogicalNot()
    {
        string source = """
                        routine test() -> bool
                            return not a
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ChainedLogical()
    {
        string source = """
                        routine test() -> bool
                            return a and b or c and not d
                        """;

        AssertParsesSuflae(source: source);
    }

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

    [Fact]
    public void ParseSuflae_BitwiseAnd()
    {
        string source = """
                        routine test() -> U32
                            return flags & mask
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_BitwiseOr()
    {
        string source = """
                        routine test() -> U32
                            return flags | mask
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_BitwiseXor()
    {
        string source = """
                        routine test() -> U32
                            return flags ^ mask
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_BitwiseNot()
    {
        string source = """
                        routine test() -> U32
                            return ~flags
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LeftShift()
    {
        string source = """
                        routine test() -> U32
                            return value << 4
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_RightShift()
    {
        string source = """
                        routine test() -> U32
                            return value >> 4
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LogicalLeftShift()
    {
        string source = """
                        routine test() -> U32
                            return value <<< 4
                        """;

        AssertParsesSuflae(source: source);
    }

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

    [Fact]
    public void ParseSuflae_NoneCoalescing()
    {
        string source = """
                        routine test() -> Integer
                            let value: Integer? = None
                            return value ?? 42
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ChainedNoneCoalescing()
    {
        string source = """
                        routine test() -> Integer
                            let a: Integer? = none
                            let b: Integer? = none
                            let c: Integer = 100
                            return a ?? b ?? c
                        """;

        AssertParsesSuflae(source: source);
    }

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

    [Fact]
    public void ParseSuflae_BitwiseAndAssignment()
    {
        string source = """
                        routine test()
                            var flags: U32 = 0xFF
                            flags &= 0x0F
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_BitwiseOrAssignment()
    {
        string source = """
                        routine test()
                            var flags: U32 = 0x00
                            flags |= 0x0F
                        """;

        AssertParsesSuflae(source: source);
    }

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

    [Fact]
    public void ParseSuflae_TextConcatenation()
    {
        string source = """
                        routine test() -> Text
                            return "Hello, " + "World!"
                        """;

        AssertParsesSuflae(source: source);
    }

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

    [Fact]
    public void ParseSuflae_InclusiveRange()
    {
        string source = """
                        routine test()
                            for i in 0 to 10
                                show(i)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_RangeWithStep()
    {
        string source = """
                        routine test()
                            for i in 0 to 100 by 5
                                show(i)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_DowntoRange()
    {
        string source = """
                        routine test()
                            for i in 10 downto 0
                                show(i)
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Complex Operator Expressions

    [Fact]
    public void ParseSuflae_ComplexExpression()
    {
        string source = """
                        routine test() -> Integer
                            return (a + b) * c - d // e % f ** g
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_MixedOperatorPrecedence()
    {
        string source = """
                        routine test() -> bool
                            return a + b > c * d and not (e == f or g != h)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_BitwiseWithComparison()
    {
        string source = """
                        routine test() -> bool
                            return (flags & mask) != 0
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion
}
