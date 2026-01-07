namespace Compilers.Analysis.Types;

using Compilers.Analysis.Enums;

/// <summary>
/// Singleton error type used when type resolution fails.
/// This is a compiler-internal sentinel, not a real user-visible type.
/// </summary>
public sealed class ErrorTypeInfo : TypeInfo
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.Error;

    /// <summary>
    /// Singleton instance of the error type.
    /// </summary>
    public static readonly ErrorTypeInfo Instance = new();

    private ErrorTypeInfo() : base(name: "<error>")
    {
    }

    /// <inheritdoc/>
    /// <returns>Always returns this instance.</returns>
    public override TypeInfo Instantiate(IReadOnlyList<TypeInfo> typeArguments)
    {
        return this;
    }
}
