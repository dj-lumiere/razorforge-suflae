namespace TypeModel.Enums;

/// <summary>
/// The kind of routine (function, member routine, creator, etc.).
/// </summary>
public enum RoutineKind
{
    /// <summary>Free-standing function (not attached to a type).</summary>
    Function,

    /// <summary>Member routine in the same file as the type (routine Type.name()).</summary>
    MemberRoutine,

    /// <summary>Member routine in a different file from the type (routine Type.name()).</summary>
    ExternalMemberRoutine,

    /// <summary>Creator (create).</summary>
    Creator,

    // (FFI external-ness moved to RoutineInfo.Realm / RoutineRealm — a routine's FOREIGN realm
    //  is C/LLVM, orthogonal to its structural kind.)

    /// <summary>Operator overload (add, sub, etc.).</summary>
    Operator,

    /// <summary>Anonymous lambda / closure expression.</summary>
    Lambda
}
