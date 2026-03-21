namespace SemanticAnalysis.Enums;

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

    /// <summary>Creator (__create__).</summary>
    Creator,

    /// <summary>External FFI function.</summary>
    External,

    /// <summary>Operator overload (__add__, __sub__, etc.).</summary>
    Operator
}
