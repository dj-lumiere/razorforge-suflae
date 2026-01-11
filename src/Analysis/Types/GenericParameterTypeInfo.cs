namespace Compilers.Analysis.Types;

using Enums;

/// <summary>
/// Represents a generic type parameter (like T in List&lt;T&gt;).
/// This is an unbound placeholder that gets replaced during generic instantiation.
/// </summary>
public sealed class GenericParameterTypeInfo : TypeInfo
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.TypeParameter;

    /// <summary>
    /// Initializes a new instance of the <see cref="GenericParameterTypeInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the generic type parameter.</param>
    public GenericParameterTypeInfo(string name) : base(name: name)
    {
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Always thrown as generic parameters cannot be instantiated.</exception>
    public override TypeInfo Instantiate(IReadOnlyList<TypeInfo> typeArguments)
    {
        throw new InvalidOperationException(
            message: "Cannot instantiate a generic type parameter.");
    }
}
