/// <summary>
/// Storage class modifiers controlling where a symbol lives.
/// Orthogonal to visibility modifiers (public/internal/private).
/// </summary>
public enum StorageClass
{
    /// <summary>Default storage: instance member or module-level</summary>
    None,

    /// <summary>Type-level static (like static in C#/Java), accessed via Type.member()</summary>
    Common
}
