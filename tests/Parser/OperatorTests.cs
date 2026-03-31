using Xunit;

namespace RazorForge.Tests.Parser;

using static TestHelpers;

/// <summary>
/// Tests for parsing operators in RazorForge:
/// arithmetic, wrapping, clamping, checked, comparison, logical, bitwise, none coalescing.
/// </summary>
public class OperatorTests
{
    #region Standard Arithmetic Operators
    /// <summary>
    /// Tests Parse_Addition.
    /// </summary>

    [Fact]
    public void Parse_Addition()
    {
        string source = """
                        routine test() -> S32
                          return 1 + 2
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_Subtraction.
    /// </summary>

    [Fact]
    public void Parse_Subtraction()
    {
        string source = """
                        routine test() -> S32
                          return 5 - 3
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_Multiplication.
    /// </summary>

    [Fact]
    public void Parse_Multiplication()
    {
        string source = """
                        routine test() -> S32
                          return 4 * 5
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_Division.
    /// </summary>

    [Fact]
    public void Parse_Division()
    {
        string source = """
                        routine test() -> F32
                          return 10.0_f32 / 3.0_f32
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_FloorDivision.
    /// </summary>

    [Fact]
    public void Parse_FloorDivision()
    {
        string source = """
                        routine test() -> S32
                          return 10 // 3
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_Remainder.
    /// </summary>

    [Fact]
    public void Parse_Remainder()
    {
        string source = """
                        routine test() -> S32
                          return 10 % 3
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_Power.
    /// </summary>

    [Fact]
    public void Parse_Power()
    {
        string source = """
                        routine test() -> S32
                          return 2 ** 10
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_Negation.
    /// </summary>

    [Fact]
    public void Parse_Negation()
    {
        string source = """
                        routine test() -> S32
                          return -42
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_ChainedArithmetic.
    /// </summary>

    [Fact]
    public void Parse_ChainedArithmetic()
    {
        string source = """
                        routine test() -> S32
                          return 1 + 2 * 3 - 4 // 2
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_ParenthesizedArithmetic.
    /// </summary>

    [Fact]
    public void Parse_ParenthesizedArithmetic()
    {
        string source = """
                        routine test() -> S32
                          return (1 + 2) * (3 - 4)
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Wrapping Arithmetic Operators
    /// <summary>
    /// Tests Parse_WrappingAdd.
    /// </summary>

    [Fact]
    public void Parse_WrappingAdd()
    {
        string source = """
                        routine test() -> U8
                          var a: U8 = 250
                          var b: U8 = 10
                          return a +% b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WrappingSubtract.
    /// </summary>

    [Fact]
    public void Parse_WrappingSubtract()
    {
        string source = """
                        routine test() -> U8
                          var a: U8 = 5
                          var b: U8 = 10
                          return a -% b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WrappingMultiply.
    /// </summary>

    [Fact]
    public void Parse_WrappingMultiply()
    {
        string source = """
                        routine test() -> U8
                          var a: U8 = 20
                          var b: U8 = 20
                          return a *% b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WrappingPower.
    /// </summary>

    [Fact]
    public void Parse_WrappingPower()
    {
        string source = """
                        routine test() -> U8
                          var a: U8 = 2
                          var b: U8 = 10
                          return a **% b
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Clamping Arithmetic Operators
    /// <summary>
    /// Tests Parse_ClampingAdd.
    /// </summary>

    [Fact]
    public void Parse_ClampingAdd()
    {
        string source = """
                        routine test() -> U8
                          var a: U8 = 250
                          var b: U8 = 10
                          return a +^ b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_ClampingSubtract.
    /// </summary>

    [Fact]
    public void Parse_ClampingSubtract()
    {
        string source = """
                        routine test() -> U8
                          var a: U8 = 5
                          var b: U8 = 10
                          return a -^ b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_ClampingMultiply.
    /// </summary>

    [Fact]
    public void Parse_ClampingMultiply()
    {
        string source = """
                        routine test() -> U8
                          var a: U8 = 20
                          var b: U8 = 20
                          return a *^ b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_ClampingPower.
    /// </summary>

    [Fact]
    public void Parse_ClampingPower()
    {
        string source = """
                        routine test() -> U8
                          var a: U8 = 2
                          var b: U8 = 10
                          return a **^ b
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Comparison Operators
    /// <summary>
    /// Tests Parse_Equal.
    /// </summary>

    [Fact]
    public void Parse_Equal()
    {
        string source = """
                        routine test() -> bool
                          return a == b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_NotEqual.
    /// </summary>

    [Fact]
    public void Parse_NotEqual()
    {
        string source = """
                        routine test() -> bool
                          return a != b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_LessThan.
    /// </summary>

    [Fact]
    public void Parse_LessThan()
    {
        string source = """
                        routine test() -> bool
                          return a < b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_LessOrEqual.
    /// </summary>

    [Fact]
    public void Parse_LessOrEqual()
    {
        string source = """
                        routine test() -> bool
                          return a <= b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_GreaterThan.
    /// </summary>

    [Fact]
    public void Parse_GreaterThan()
    {
        string source = """
                        routine test() -> bool
                          return a > b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_GreaterOrEqual.
    /// </summary>

    [Fact]
    public void Parse_GreaterOrEqual()
    {
        string source = """
                        routine test() -> bool
                          return a >= b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_ChainedComparison.
    /// </summary>

    [Fact]
    public void Parse_ChainedComparison()
    {
        string source = """
                        routine test() -> bool
                          return 0 <= index < length
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_ChainedRangeComparison.
    /// </summary>

    [Fact]
    public void Parse_ChainedRangeComparison()
    {
        string source = """
                        routine test() -> bool
                          return min <= value <= max
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Identity Operators
    /// <summary>
    /// Tests Parse_IdentityEqual.
    /// </summary>

    [Fact]
    public void Parse_IdentityEqual()
    {
        string source = """
                        routine test() -> bool
                          return a === b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_IdentityNotEqual.
    /// </summary>

    [Fact]
    public void Parse_IdentityNotEqual()
    {
        string source = """
                        routine test() -> bool
                          return a !== b
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Logical Operators
    /// <summary>
    /// Tests Parse_LogicalAnd.
    /// </summary>

    [Fact]
    public void Parse_LogicalAnd()
    {
        string source = """
                        routine test() -> bool
                          return a and b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_LogicalOr.
    /// </summary>

    [Fact]
    public void Parse_LogicalOr()
    {
        string source = """
                        routine test() -> bool
                          return a or b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_LogicalNot.
    /// </summary>

    [Fact]
    public void Parse_LogicalNot()
    {
        string source = """
                        routine test() -> bool
                          return not a
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_ChainedLogical.
    /// </summary>

    [Fact]
    public void Parse_ChainedLogical()
    {
        string source = """
                        routine test() -> bool
                          return a and b or c and not d
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_LogicalWithComparison.
    /// </summary>

    [Fact]
    public void Parse_LogicalWithComparison()
    {
        string source = """
                        routine test() -> bool
                          return x > 0 and x < 100
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Bitwise Operators
    /// <summary>
    /// Tests Parse_BitwiseAnd.
    /// </summary>

    [Fact]
    public void Parse_BitwiseAnd()
    {
        string source = """
                        routine test() -> U32
                          return bits & mask
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_BitwiseOr.
    /// </summary>

    [Fact]
    public void Parse_BitwiseOr()
    {
        string source = """
                        routine test() -> U32
                          return bits | mask
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_BitwiseXor.
    /// </summary>

    [Fact]
    public void Parse_BitwiseXor()
    {
        string source = """
                        routine test() -> U32
                          return bits ^ mask
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_BitwiseNot.
    /// </summary>

    [Fact]
    public void Parse_BitwiseNot()
    {
        string source = """
                        routine test() -> U32
                          return ~bits
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_LeftShift.
    /// </summary>

    [Fact]
    public void Parse_LeftShift()
    {
        string source = """
                        routine test() -> U32
                          return value << 4
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_RightShift.
    /// </summary>

    [Fact]
    public void Parse_RightShift()
    {
        string source = """
                        routine test() -> U32
                          return value >> 4
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_LogicalLeftShift.
    /// </summary>

    [Fact]
    public void Parse_LogicalLeftShift()
    {
        string source = """
                        routine test() -> U32
                          return value <<< 4
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_LogicalRightShift.
    /// </summary>

    [Fact]
    public void Parse_LogicalRightShift()
    {
        string source = """
                        routine test() -> U32
                          return value >>> 4
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region None Coalescing Operator
    /// <summary>
    /// Tests Parse_NoneCoalescing.
    /// </summary>

    [Fact]
    public void Parse_NoneCoalescing()
    {
        string source = """
                        routine test() -> S32
                          var value: S32? = None
                          return value ?? 42
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_ChainedNoneCoalescing.
    /// </summary>

    [Fact]
    public void Parse_ChainedNoneCoalescing()
    {
        string source = """
                        routine test() -> S32
                          var a: S32? = None
                          var b: S32? = None
                          var c: S32 = 100
                          return a ?? b ?? c
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_NoneCoalescingWithMethodCall.
    /// </summary>

    [Fact]
    public void Parse_NoneCoalescingWithMethodCall()
    {
        string source = """
                        routine test() -> User
                          return try_get_user(id) ?? default_user()
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Assignment Operators
    /// <summary>
    /// Tests Parse_SimpleAssignment.
    /// </summary>

    [Fact]
    public void Parse_SimpleAssignment()
    {
        string source = """
                        routine test()
                          var x = 0
                          x = 42
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_AddAssignment.
    /// </summary>

    [Fact]
    public void Parse_AddAssignment()
    {
        string source = """
                        routine test()
                          var x = 0
                          x += 1
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_SubtractAssignment.
    /// </summary>

    [Fact]
    public void Parse_SubtractAssignment()
    {
        string source = """
                        routine test()
                          var x = 10
                          x -= 1
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_MultiplyAssignment.
    /// </summary>

    [Fact]
    public void Parse_MultiplyAssignment()
    {
        string source = """
                        routine test()
                          var x = 2
                          x *= 3
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_DivideAssignment.
    /// </summary>

    [Fact]
    public void Parse_DivideAssignment()
    {
        string source = """
                        routine test()
                          var x = 10.0_f32
                          x /= 2.0_f32
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_FloorDivideAssignment.
    /// </summary>

    [Fact]
    public void Parse_FloorDivideAssignment()
    {
        string source = """
                        routine test()
                          var x = 10
                          x //= 3
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_RemainderAssignment.
    /// </summary>

    [Fact]
    public void Parse_RemainderAssignment()
    {
        string source = """
                        routine test()
                          var x = 10
                          x %= 3
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_BitwiseAndAssignment.
    /// </summary>

    [Fact]
    public void Parse_BitwiseAndAssignment()
    {
        string source = """
                        routine test()
                          var bits: U32 = 0xFF
                          bits &= 0x0F
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_BitwiseOrAssignment.
    /// </summary>

    [Fact]
    public void Parse_BitwiseOrAssignment()
    {
        string source = """
                        routine test()
                          var bits: U32 = 0x00
                          bits |= 0x0F
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_BitwiseXorAssignment.
    /// </summary>

    [Fact]
    public void Parse_BitwiseXorAssignment()
    {
        string source = """
                        routine test()
                          var bits: U32 = 0xFF
                          bits ^= 0x0F
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_LeftShiftAssignment.
    /// </summary>

    [Fact]
    public void Parse_LeftShiftAssignment()
    {
        string source = """
                        routine test()
                          var x: U32 = 1
                          x <<= 4
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_RightShiftAssignment.
    /// </summary>

    [Fact]
    public void Parse_RightShiftAssignment()
    {
        string source = """
                        routine test()
                          var x: U32 = 16
                          x >>= 2
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Text Operators
    /// <summary>
    /// Tests Parse_TextConcatenation.
    /// </summary>

    [Fact]
    public void Parse_TextConcatenation()
    {
        string source = """
                        routine test() -> Text
                          return "Hello, " + "World!"
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_TextRepetition.
    /// </summary>

    [Fact]
    public void Parse_TextRepetition()
    {
        string source = """
                        routine test() -> Text
                          return "-" * 40
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Range Operators
    /// <summary>
    /// Tests Parse_InclusiveRange.
    /// </summary>

    [Fact]
    public void Parse_InclusiveRange()
    {
        string source = """
                        routine test()
                          for i in 0 til 10
                            show(i)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_RangeWithStep.
    /// </summary>

    [Fact]
    public void Parse_RangeWithStep()
    {
        string source = """
                        routine test()
                          for i in 0 til 100 by 5
                            show(i)
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Complex Operator Expressions
    /// <summary>
    /// Tests Parse_ComplexExpression.
    /// </summary>

    [Fact]
    public void Parse_ComplexExpression()
    {
        string source = """
                        routine test() -> S32
                          return (a + b) * c - d // e % f ** g
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_MixedOperatorPrecedence.
    /// </summary>

    [Fact]
    public void Parse_MixedOperatorPrecedence()
    {
        string source = """
                        routine test() -> bool
                          return a + b > c * d and not (e == f or g != h)
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_BitwiseWithComparison.
    /// </summary>

    [Fact]
    public void Parse_BitwiseWithComparison()
    {
        string source = """
                        routine test() -> bool
                          return (bits & mask) != 0
                        """;

        AssertParses(source: source);
    }

    #endregion
}
