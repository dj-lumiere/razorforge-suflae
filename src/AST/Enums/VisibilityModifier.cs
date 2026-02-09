/// <summary>
/// Visibility modifiers controlling WHO can access declarations.
/// Orthogonal to StorageClass which controls WHERE a symbol lives.
/// </summary>
/// <remarks>
/// The visibility system is designed to be intuitive while providing precise control:
///
/// Both languages share the same visibility levels:
/// <list type="bullet">
/// <item>private - Only the declaring file can access</item>
/// <item>internal - Only code in the same module/package can access</item>
/// <item>published - Public read, private write (for fields)</item>
/// <item>public - Any code can access</item>
/// <item>imported - External/FFI linkage</item>
/// </list>
///
/// Visibility can combine with StorageClass (common/global):
/// <list type="bullet">
/// <item>public common routine Type.foo() - Anyone can call Type.foo()</item>
/// <item>private common routine Type.bar() - Same file can call Type.bar()</item>
/// </list>
///
/// Note: 'family' (protected) was removed since inheritance is not supported.
/// </remarks>
public enum VisibilityModifier
{
    /// <summary>Only accessible within the declaring file</summary>
    Private,

    /// <summary>Accessible within the same module</summary>
    Internal,

    /// <summary>Public read, private write (public getter, private setter)</summary>
    Published,

    /// <summary>Accessible from anywhere</summary>
    Public,

    /// <summary>External/FFI linkage</summary>
    Imported
}
