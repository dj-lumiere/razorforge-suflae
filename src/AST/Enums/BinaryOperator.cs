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
                _ => throw new ArgumentOutOfRangeException(paramName: nameof(op),
                    actualValue: op,
                    message: null)
            };
        }
        /// <summary>
        /// Gets the wired method name for operator overloading.
        /// Returns null if the operator is not overloadable.
        /// </summary>
        public string? GetMethodName()
        {
            return op switch
            {
                // Arithmetic
                BinaryOperator.Add => "$add",
                BinaryOperator.Subtract => "$sub",
                BinaryOperator.Multiply => "$mul",
                BinaryOperator.TrueDivide => "$truediv",
                BinaryOperator.FloorDivide => "$floordiv",
                BinaryOperator.Modulo => "$mod",
                BinaryOperator.Power => "$pow",

                // Wrapping arithmetic
                BinaryOperator.AddWrap => "$add_wrap",
                BinaryOperator.SubtractWrap => "$sub_wrap",
                BinaryOperator.MultiplyWrap => "$mul_wrap",
                BinaryOperator.PowerWrap => "$pow_wrap",

                // Clamping arithmetic
                BinaryOperator.AddClamp => "$add_clamp",
                BinaryOperator.SubtractClamp => "$sub_clamp",
                BinaryOperator.MultiplyClamp => "$mul_clamp",
                BinaryOperator.TrueDivClamp => "$truediv_clamp",
                BinaryOperator.PowerClamp => "$pow_clamp",

                // Ordering (overloadable)
                BinaryOperator.Equal => "$eq",
                BinaryOperator.NotEqual => "$ne",
                BinaryOperator.Less => "$lt",
                BinaryOperator.LessEqual => "$le",
                BinaryOperator.Greater => "$gt",
                BinaryOperator.GreaterEqual => "$ge",
                BinaryOperator.ThreeWayComparator => "$cmp",

                // Bitwise
                BinaryOperator.BitwiseAnd => "$bitand",
                BinaryOperator.BitwiseOr => "$bitor",
                BinaryOperator.BitwiseXor => "$bitxor",
                BinaryOperator.ArithmeticLeftShift => "$ashl",
                BinaryOperator.ArithmeticRightShift => "$ashr",
                BinaryOperator.LogicalLeftShift => "$lshl",
                BinaryOperator.LogicalRightShift => "$lshr",

                // Membership (note: operands are reversed in desugaring)
                // x in coll → coll.$contains(x)
                // x notin coll → coll.$notcontains(x)
                BinaryOperator.In => "$contains",
                BinaryOperator.NotIn => "$notcontains",

                // Unwrap operators
                BinaryOperator.NoneCoalesce => "$unwrap_or",

                // Non-overloadable operators return null
                _ => null
            };
        }

        /// <summary>
        /// Gets the in-place wired method name for compound assignment dispatch.
        /// Returns null if the operator has no in-place variant (overflow, comparison, etc.).
        /// In-place methods modify the receiver and return Blank.
        /// </summary>
        public string? GetInPlaceMethodName()
        {
            return op switch
            {
                // Arithmetic
                BinaryOperator.Add => "$iadd",
                BinaryOperator.Subtract => "$isub",
                BinaryOperator.Multiply => "$imul",
                BinaryOperator.TrueDivide => "$itruediv",
                BinaryOperator.FloorDivide => "$ifloordiv",
                BinaryOperator.Modulo => "$imod",
                BinaryOperator.Power => "$ipow",

                // Bitwise
                BinaryOperator.BitwiseAnd => "$ibitand",
                BinaryOperator.BitwiseOr => "$ibitor",
                BinaryOperator.BitwiseXor => "$ibitxor",

                // Shift
                BinaryOperator.ArithmeticLeftShift => "$iashl",
                BinaryOperator.ArithmeticRightShift => "$iashr",
                BinaryOperator.LogicalLeftShift => "$ilshl",
                BinaryOperator.LogicalRightShift => "$ilshr",

                // Overflow variants and other operators have no in-place form
                _ => null
            };
        }
    }
}
