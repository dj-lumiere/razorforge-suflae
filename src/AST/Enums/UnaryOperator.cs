/// <summary>
/// Enumeration of unary operators that operate on a single operand.
/// Supports arithmetic, logical, and bitwise operations.
/// </summary>
/// <remarks>
/// Unary operators provide fundamental single-operand operations:
/// <list type="bullet">
/// <item>Arithmetic: -x (negation)</item>
/// <item>Logical: not condition</item>
/// <item>Bitwise: ~x (bitwise complement)</item>
/// </list>
/// </remarks>
public enum UnaryOperator
{
    /// <summary>Arithmetic negation operator (-x)</summary>
    Minus,

    /// <summary>Logical NOT operator (not x)</summary>
    Not,

    /// <summary>Bitwise NOT/complement operator (~x)</summary>
    BitwiseNot
}

internal static class UnaryOperatorExtensions
{
    extension(UnaryOperator op)
    {
        public string? ToStringRepresentation()
        {
            return op switch
            {
                UnaryOperator.Minus => "-",
                UnaryOperator.Not => "not",
                UnaryOperator.BitwiseNot => "~",
                _ => null
            };
        }
        /// <summary>
        /// Gets the dunder method name for operator overloading.
        /// Returns null if the operator is not overloadable.
        /// </summary>
        public string? GetMethodName()
        {
            return op switch
            {
                UnaryOperator.Minus => "__neg__",
                UnaryOperator.BitwiseNot => "__not__",
                _ => null
            };
        }
    }
}
