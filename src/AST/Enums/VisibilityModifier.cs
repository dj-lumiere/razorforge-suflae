/// <summary>
/// Visibility modifiers controlling access to declarations.
/// Supports both Suflae's descriptive keywords and RazorForge's traditional modifiers.
/// </summary>
/// <remarks>
/// The visibility system is designed to be intuitive while providing precise control:
///
/// Both languages share the same visibility levels:
/// <list type="bullet">
/// <item>private - Only the declaring file can access</item>
/// <item>internal - Only code in the same module/package can access</item>
/// <item>public - Any code can access</item>
/// <item>common - Type-level static (class static equivalent)</item>
/// <item>global - File-level static, accessible without import</item>
/// <item>imported - External/FFI linkage</item>
/// </list>
///
/// Note: 'family' (protected) was removed since inheritance is not supported.
/// </remarks>
public enum VisibilityModifier
{
    /// <summary>Only accessible within the declaring file</summary>
    Private,

    /// <summary>Accessible within the same module/namespace</summary>
    Internal,

    /// <summary>Public read, private write (public getter, private setter)</summary>
    Published,

    /// <summary>Accessible from anywhere</summary>
    Public,

    /// <summary>Globally accessible without import</summary>
    Global,

    /// <summary>Type-level static (class static equivalent)</summary>
    Common,

    /// <summary>External/FFI linkage</summary>
    Imported
}
