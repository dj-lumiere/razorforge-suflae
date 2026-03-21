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

    /// <summary>Resident type constraint (where T is resident)</summary>
    ResidentType,

    /// <summary>Routine/function type constraint (where T is routine)</summary>
    RoutineType,

    /// <summary>Choice type constraint (where T is choice)</summary>
    ChoiceType,

    /// <summary>Flags type constraint (where T is flags)</summary>
    FlagsType,

    /// <summary>Variant type constraint (where T is variant)</summary>
    VariantType,

    /// <summary>Const generic type constraint (where N is uaddr)</summary>
    ConstGeneric,

    /// <summary>Type equality constraint (where T in [s32, u8])</summary>
    TypeEquality
}
