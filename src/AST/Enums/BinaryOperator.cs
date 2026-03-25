/// <summary>
/// Enumeration of binary operators supported in binary expressions.
/// Includes arithmetic, comparison, logical, bitwise, and specialized operators.
/// </summary>
/// <remarks>
/// The operator categories provide comprehensive mathematical and logical operations:
/// <list type="bullet">
/// <item>Arithmetic: Basic math operations with overflow handling variants</item>
/// <item>Comparison: Standard comparisons plus membership and type testing</item>
/// <item>Logical: Boolean logic operations with short-circuit evaluation</item>
/// <item>Bitwise: Low-level bit manipulation operations</item>
/// <item>Assignment: Assignment operators when used in expression context</item>
/// </list>
/// Overflow handling variants allow precise control over integer arithmetic behavior.
/// </remarks>
public enum BinaryOperator
{
    #region Arithmetic - standard operations

    /// <summary>Standard addition operator (+)</summary>
    Add,

    /// <summary>Standard subtraction operator (-)</summary>
    Subtract,

    /// <summary>Standard multiplication operator (*)</summary>
    Multiply,

    /// <summary>Floating-point division operator (/)</summary>
    TrueDivide,

    /// <summary>Integer floor division operator (//)</summary>
    FloorDivide,

    /// <summary>Remainder/modulo operator (%)</summary>
    Modulo,

    /// <summary>Exponentiation operator (**)</summary>
    Power,

    #endregion

    #region Arithmetic with overflow handling variants - Wrap (wrapping on overflow)

    /// <summary>Addition with wrapping behavior on overflow (+%)</summary>
    AddWrap,

    /// <summary>Subtraction with wrapping behavior on overflow (-%)</summary>
    SubtractWrap,

    /// <summary>Multiplication with wrapping behavior on overflow (*%)</summary>
    MultiplyWrap,

    /// <summary>Exponentiation with wrapping behavior on overflow (**%)</summary>
    PowerWrap,

    #endregion

    #region Arithmetic with overflow handling variants - Clamping (clamp to min/max)

    /// <summary>Addition that clamps at the type's bounds on overflow (+^)</summary>
    AddClamp,

    /// <summary>Subtraction that clamps at the type's bounds on overflow (-^)</summary>
    SubtractClamp,

    /// <summary>Multiplication that clamps at the type's bounds on overflow (*^)</summary>
    MultiplyClamp,

    /// <summary>Division that clamps at the type's bounds on overflow (/^)</summary>
    TrueDivClamp,

    /// <summary>Exponentiation that clamps at the type's bounds on overflow (**^)</summary>
    PowerClamp,

    #endregion


    #region Comparison - equality, relational, and membership

    /// <summary>Equality comparison operator (==)</summary>
    Equal,

    /// <summary>Inequality comparison operator (!=)</summary>
    NotEqual,

    /// <summary>Strict identity/reference equality operator (===)</summary>
    Identical,

    /// <summary>Strict identity/reference inequality operator (!==)</summary>
    NotIdentical,

    /// <summary>Less than relational operator (&lt;)</summary>
    Less,

    /// <summary>Less than or equal relational operator (&lt;=)</summary>
    LessEqual,

    /// <summary>Greater than relational operator (&gt;)</summary>
    Greater,

    /// <summary>Greater than or equal relational operator (&gt;=)</summary>
    GreaterEqual,

    /// <summary>Three-way comparison (spaceship) operator (&lt;=&gt;)</summary>
    ThreeWayComparator,

    /// <summary>Collection membership operator (in)</summary>
    In,

    /// <summary>Negated collection membership operator (notin)</summary>
    NotIn,

    /// <summary>Type check or pattern matching operator (is)</summary>
    Is,

    /// <summary>Negated type check or pattern matching operator (isnot)</summary>
    IsNot,

    /// <summary>Protocol conformance check operator (obeys)</summary>
    Obeys,

    /// <summary>Negated protocol conformance check operator (disobeys)</summary>
    Disobeys,

    #endregion

    #region Logical - boolean operations with short-circuit evaluation

    /// <summary>Short-circuiting logical AND operator (and)</summary>
    And,

    /// <summary>Short-circuiting logical OR operator (or)</summary>
    Or,

    /// <summary>Flags removal operator (but) — removes flags from a value</summary>
    But,

    #endregion

    #region Bitwise - low-level bit manipulation

    /// <summary>Bitwise AND operator (&amp;)</summary>
    BitwiseAnd,

    /// <summary>Bitwise OR operator (|)</summary>
    BitwiseOr,

    /// <summary>Bitwise XOR operator (^)</summary>
    BitwiseXor,

    /// <summary>Arithmetic left shift operator (&lt;&lt;)</summary>
    ArithmeticLeftShift,

    /// <summary>Arithmetic right shift operator (&gt;&gt;)</summary>
    ArithmeticRightShift,

    /// <summary>Logical left shift operator (&lt;&lt;&lt;)</summary>
    LogicalLeftShift,

    /// <summary>Logical right shift operator (&gt;&gt;&gt;)</summary>
    LogicalRightShift,

    #endregion

    #region Assignment - when assignment is used as expression

    /// <summary>Simple assignment operator (=)</summary>
    Assign,

    #endregion

    #region None coalescing - returns left if not None/Error, otherwise right

    /// <summary>None-coalescing operator (??)</summary>
    NoneCoalesce

    #endregion
}

/// <summary>
/// Extension methods for BinaryOperator enum
/// </summary>
public static class BinaryOperatorExtensions
{
    extension(BinaryOperator op)
    {
        /// <summary>
        /// Converts a BinaryOperator to its string representation
        /// </summary>
        public string ToStringRepresentation()
        {
            return op switch
            {
                BinaryOperator.Add => "+",
                BinaryOperator.Subtract => "-",
                BinaryOperator.Multiply => "*",
                BinaryOperator.TrueDivide => "/",
                BinaryOperator.FloorDivide => "//",
                BinaryOperator.Modulo => "%",
                BinaryOperator.Power => "**",

                BinaryOperator.AddWrap => "+%",
                BinaryOperator.SubtractWrap => "-%",
                BinaryOperator.MultiplyWrap => "*%",
                BinaryOperator.PowerWrap => "**%",

                BinaryOperator.AddClamp => "+^",
                BinaryOperator.SubtractClamp => "-^",
                BinaryOperator.MultiplyClamp => "*^",
                BinaryOperator.TrueDivClamp => "/^",
                BinaryOperator.PowerClamp => "**^",

                BinaryOperator.Equal => "==",
                BinaryOperator.NotEqual => "!=",
                BinaryOperator.Identical => "===",
                BinaryOperator.NotIdentical => "!==",
                BinaryOperator.Less => "<",
                BinaryOperator.LessEqual => "<=",
                BinaryOperator.Greater => ">",
                BinaryOperator.GreaterEqual => ">=",
                BinaryOperator.ThreeWayComparator => "<=>",

                BinaryOperator.In => "in",
                BinaryOperator.NotIn => "notin",
                BinaryOperator.Is => "is",
                BinaryOperator.IsNot => "isnot",
                BinaryOperator.Obeys => "obeys",
                BinaryOperator.Disobeys => "disobeys",

                BinaryOperator.And => "and",
                BinaryOperator.Or => "or",
                BinaryOperator.But => "but",

                BinaryOperator.BitwiseAnd => "&",
                BinaryOperator.BitwiseOr => "|",
                BinaryOperator.BitwiseXor => "^",
                BinaryOperator.ArithmeticLeftShift => "<<",
                BinaryOperator.ArithmeticRightShift => ">>",
                BinaryOperator.LogicalLeftShift => "<<<",
                BinaryOperator.LogicalRightShift => ">>>",

                BinaryOperator.Assign => "=",
                BinaryOperator.NoneCoalesce => "??",
                _ => throw new ArgumentOutOfRangeException(nameof(op), op, null)
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
                // Arithmetic
                BinaryOperator.Add => "__add__",
                BinaryOperator.Subtract => "__sub__",
                BinaryOperator.Multiply => "__mul__",
                BinaryOperator.TrueDivide => "__truediv__",
                BinaryOperator.FloorDivide => "__floordiv__",
                BinaryOperator.Modulo => "__mod__",
                BinaryOperator.Power => "__pow__",

                // Wrapping arithmetic
                BinaryOperator.AddWrap => "__add_wrap__",
                BinaryOperator.SubtractWrap => "__sub_wrap__",
                BinaryOperator.MultiplyWrap => "__mul_wrap__",
                BinaryOperator.PowerWrap => "__pow_wrap__",

                // Clamping arithmetic
                BinaryOperator.AddClamp => "__add_clamp__",
                BinaryOperator.SubtractClamp => "__sub_clamp__",
                BinaryOperator.MultiplyClamp => "__mul_clamp__",
                BinaryOperator.TrueDivClamp => "__truediv_clamp__",
                BinaryOperator.PowerClamp => "__pow_clamp__",

                // Ordering (overloadable)
                BinaryOperator.Equal => "__eq__",
                BinaryOperator.NotEqual => "__ne__",
                BinaryOperator.Less => "__lt__",
                BinaryOperator.LessEqual => "__le__",
                BinaryOperator.Greater => "__gt__",
                BinaryOperator.GreaterEqual => "__ge__",
                BinaryOperator.ThreeWayComparator => "__cmp__",

                // Bitwise
                BinaryOperator.BitwiseAnd => "__and__",
                BinaryOperator.BitwiseOr => "__or__",
                BinaryOperator.BitwiseXor => "__xor__",
                BinaryOperator.ArithmeticLeftShift => "__ashl__",
                BinaryOperator.ArithmeticRightShift => "__ashr__",
                BinaryOperator.LogicalLeftShift => "__lshl__",
                BinaryOperator.LogicalRightShift => "__lshr__",

                // Membership (note: operands are reversed in desugaring)
                // x in coll → coll.__contains__(x)
                // x notin coll → coll.__notcontains__(x)
                BinaryOperator.In => "__contains__",
                BinaryOperator.NotIn => "__notcontains__",

                // Non-overloadable operators return null
                _ => null
            };
        }

        /// <summary>
        /// Gets the in-place dunder method name for compound assignment dispatch.
        /// Returns null if the operator has no in-place variant (overflow, comparison, etc.).
        /// In-place methods modify the receiver and return Blank.
        /// </summary>
        public string? GetInPlaceMethodName()
        {
            return op switch
            {
                // Arithmetic
                BinaryOperator.Add => "__iadd__",
                BinaryOperator.Subtract => "__isub__",
                BinaryOperator.Multiply => "__imul__",
                BinaryOperator.TrueDivide => "__itruediv__",
                BinaryOperator.FloorDivide => "__ifloordiv__",
                BinaryOperator.Modulo => "__imod__",
                BinaryOperator.Power => "__ipow__",

                // Bitwise
                BinaryOperator.BitwiseAnd => "__iand__",
                BinaryOperator.BitwiseOr => "__ior__",
                BinaryOperator.BitwiseXor => "__ixor__",

                // Shift
                BinaryOperator.ArithmeticLeftShift => "__iashl__",
                BinaryOperator.ArithmeticRightShift => "__iashr__",
                BinaryOperator.LogicalLeftShift => "__ilshl__",
                BinaryOperator.LogicalRightShift => "__ilshr__",

                // Overflow variants and other operators have no in-place form
                _ => null
            };
        }
    }
}
