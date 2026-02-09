/// <summary>
/// Storage class modifiers controlling where a symbol lives.
/// Orthogonal to visibility modifiers (public/internal/private).
/// </summary>
/// <remarks>
/// Storage class determines the lifetime and association of a symbol:
/// <list type="bullet">
/// <item>None - Instance member (default for fields/methods) or module-level (for routines)</item>
/// <item>Common - Type-level static, accessed via Type.member()</item>
/// <item>Global - File-scope static variable, not valid for routines</item>
/// </list>
///
/// Storage class can combine with visibility:
/// <list type="bullet">
/// <item>public common routine Type.foo() - Anyone can call Type.foo()</item>
/// <item>internal common routine Type.bar() - Same module can call Type.bar()</item>
/// <item>private common routine Type.baz() - Same file can call Type.baz()</item>
/// </list>
/// </remarks>
public enum StorageClass
{
    /// <summary>Default storage: instance member or module-level</summary>
    None,

    /// <summary>Type-level static (like static in C#/Java), accessed via Type.member()</summary>
    Common,

    /// <summary>File-scope static variable, not valid for routines</summary>
    Global
}