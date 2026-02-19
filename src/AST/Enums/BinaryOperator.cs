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

    #region Arithmetic with overflow handling variants - Saturate (clamp to min/max)

    /// <summary>Addition that saturates at the type's bounds on overflow (+^)</summary>
    AddSaturate,

    /// <summary>Subtraction that saturates at the type's bounds on overflow (-^)</summary>
    SubtractSaturate,

    /// <summary>Multiplication that saturates at the type's bounds on overflow (*^)</summary>
    MultiplySaturate,

    /// <summary>Exponentiation that saturates at the type's bounds on overflow (**^)</summary>
    PowerSaturate,

    #endregion

    #region Arithmetic with overflow handling variants - Checked (throw on overflow)

    /// <summary>Addition that throws or traps on overflow (+?)</summary>
    AddChecked,

    /// <summary>Subtraction that throws or traps on overflow (-?)</summary>
    SubtractChecked,

    /// <summary>Multiplication that throws or traps on overflow (*?)</summary>
    MultiplyChecked,

    /// <summary>Floor division that throws or traps on overflow (//?)</summary>
    FloorDivideChecked,

    /// <summary>Modulo that throws or traps on overflow (%?)</summary>
    ModuloChecked,

    /// <summary>Exponentiation that throws or traps on overflow (**?)</summary>
    PowerChecked,

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

    /// <summary>Protocol conformance check operator (follows)</summary>
    Follows,

    /// <summary>Negated protocol conformance check operator (notfollows)</summary>
    NotFollows,

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

    /// <summary>Checked arithmetic left shift operator (&lt;&lt;?)</summary>
    ArithmeticLeftShiftChecked,

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

                BinaryOperator.AddSaturate => "+^",
                BinaryOperator.SubtractSaturate => "-^",
                BinaryOperator.MultiplySaturate => "*^",
                BinaryOperator.PowerSaturate => "**^",

                BinaryOperator.AddChecked => "+?",
                BinaryOperator.SubtractChecked => "-?",
                BinaryOperator.MultiplyChecked => "*?",
                BinaryOperator.FloorDivideChecked => "//?",
                BinaryOperator.ModuloChecked => "%?",
                BinaryOperator.PowerChecked => "**?",

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
                BinaryOperator.Follows => "follows",
                BinaryOperator.NotFollows => "notfollows",

                BinaryOperator.And => "and",
                BinaryOperator.Or => "or",
                BinaryOperator.But => "but",

                BinaryOperator.BitwiseAnd => "&",
                BinaryOperator.BitwiseOr => "|",
                BinaryOperator.BitwiseXor => "^",
                BinaryOperator.ArithmeticLeftShift => "<<",
                BinaryOperator.ArithmeticLeftShiftChecked => "<<?",
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

                // Saturating arithmetic
                BinaryOperator.AddSaturate => "__add_sat__",
                BinaryOperator.SubtractSaturate => "__sub_sat__",
                BinaryOperator.MultiplySaturate => "__mul_sat__",
                BinaryOperator.PowerSaturate => "__pow_sat__",

                // Checked arithmetic
                BinaryOperator.AddChecked => "__add_checked__",
                BinaryOperator.SubtractChecked => "__sub_checked__",
                BinaryOperator.MultiplyChecked => "__mul_checked__",
                BinaryOperator.FloorDivideChecked => "__floordiv_checked__",
                BinaryOperator.ModuloChecked => "__mod_checked__",
                BinaryOperator.PowerChecked => "__pow_checked__",

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
                BinaryOperator.ArithmeticLeftShiftChecked => "__ashl_checked__",
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
        /// In-place methods mutate the receiver and return Blank.
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
