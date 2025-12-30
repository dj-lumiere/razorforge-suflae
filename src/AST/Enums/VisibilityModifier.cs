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
    // RazorForge traditional visibility
    /// <summary>Only accessible within the declaring entity</summary>
    Private,

    /// <summary>Accessible within the same module/assembly</summary>
    Internal,

    /// <summary>
    ///
    /// </summary>
    InternalPrivateSet,

    /// <summary>Accessible from anywhere</summary>
    Public,
    /// <summary>
    ///
    /// </summary>
    PublicInternalSet,
    /// <summary>
    ///
    /// </summary>
    PublicPrivateSet,

    // Common to both languages
    /// <summary>Globally accessible without import (both languages: global)</summary>
    Global,
    /// <summary>
    ///
    /// </summary>
    Common,
    /// <summary>
    ///
    /// </summary>
    Imported
}
