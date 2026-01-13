/// <summary>
/// Enumeration of unary operators that operate on a single operand.
/// Supports arithmetic, logical, bitwise, and ownership operations.
/// </summary>
/// <remarks>
/// Unary operators provide fundamental single-operand operations:
/// <list type="bullet">
/// <item>Arithmetic: -x (negation)</item>
/// <item>Logical: not condition</item>
/// <item>Bitwise: ~x (bitwise complement)</item>
/// <item>Ownership: steal x (ownership transfer)</item>
/// </list>
/// </remarks>
public enum UnaryOperator
{
    /// <summary>Arithmetic negation operator (-x)</summary>
    Minus,

    /// <summary>Logical NOT operator (not x)</summary>
    Not,

    /// <summary>Bitwise NOT/complement operator (~x)</summary>
    BitwiseNot,

    /// <summary>
    /// Ownership transfer operator (steal x).
    /// Transfers ownership from the source variable to the destination.
    /// </summary>
    /// <remarks>
    /// The steal operator performs compile-time ownership tracking with runtime pass-through.
    /// After a steal, the source variable becomes a deadref and cannot be used.
    ///
    /// Stealable types:
    /// <list type="bullet">
    /// <item>Raw entities - ownership is transferred</item>
    /// <item>Shared&lt;T&gt; - reference count is transferred</item>
    /// <item>Tracked&lt;T&gt; - weak reference is transferred</item>
    /// </list>
    ///
    /// Non-stealable types (caught by semantic analyzer):
    /// <list type="bullet">
    /// <item>Scope-bound tokens (Viewed, Hijacked, Inspected, Seized)</item>
    /// <item>Snatched&lt;T&gt; - internal ownership type</item>
    /// </list>
    /// </remarks>
    Steal
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
                UnaryOperator.Steal => "steal",
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
