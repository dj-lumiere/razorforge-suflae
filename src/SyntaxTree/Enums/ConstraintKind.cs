namespace SyntaxTree;

/// <summary>
/// Types of generic constraints
/// </summary>
public enum ConstraintKind
{
    /// <summary>Protocol/interface implementation (T obeys Comparable)</summary>
    Obeys,

    /// <summary>Value type constraint (where T is record)</summary>
    ValueType,

    /// <summary>Reference type constraint (where T is entity)</summary>
    ReferenceType,

    /// <summary>Routine/function type constraint (where T is routine)</summary>
    RoutineType,

    /// <summary>Choice type constraint (where T is choice)</summary>
    ChoiceType,

    /// <summary>Flags type constraint (where T is flags)</summary>
    FlagsType,

    /// <summary>Variant type constraint (where T is variant)</summary>
    VariantType,

    /// <summary>Tuple type constraint (where T is TupleType)</summary>
    TupleType,

    /// <summary>Zero-member-variable constraint (where T is ZeroMemvarType) — a type whose
    /// `memvarof` is empty: a field-less record, or a scalar kind (choice/flags) that carries no
    /// member variables. Lets a derive specialize the degenerate empty-field-walk case.</summary>
    ZeroMemvarType,

    /// <summary>Splittable constraint (where T is SplittableType) — a trivially-destructible element
    /// type whose footprint reduces to `@llvm` primitives + raw pointers with no custom store/destroy,
    /// so its member-variable columns are memcpy-movable with no per-element teardown. The eligibility
    /// gate for the SoA collections `SplitArray[T, N]` / `SplitList[T]`.</summary>
    Splittable,

    /// <summary>Const generic type constraint (where N is Address)</summary>
    ConstGeneric,

    /// <summary>Type equality constraint (where T in [s32, u8])</summary>
    TypeEquality,

    /// <summary>Crashable type constraint (where T is crashable).</summary>
    Crashable
}
