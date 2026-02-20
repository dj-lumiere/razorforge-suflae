namespace Compilers.Analysis.Enums;

/// <summary>
/// The kind of routine (function, method, constructor, etc.).
/// </summary>
public enum RoutineKind
{
    /// <summary>Free-standing function (not attached to a type).</summary>
    Function,

    /// <summary>Extension method in the same file as the type (routine Type.name()).</summary>
    Method,

    /// <summary>Extension method in a different file from the type (routine Type.name()).</summary>
    ExtensionMethod,

    /// <summary>Constructor (__create__).</summary>
    Constructor,

    /// <summary>External FFI function.</summary>
    External,

    /// <summary>Operator overload (__add__, __sub__, etc.).</summary>
    Operator
}
