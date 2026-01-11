namespace Compilers.Analysis.Types;

using Enums;

/// <summary>
/// Placeholder type used for unbound generic type parameters.
/// </summary>
public sealed class TypeParameterPlaceholder : TypeInfo
{
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Always thrown as type parameters are not concrete types.</exception>
    public override TypeCategory Category => throw new InvalidOperationException(
        message: $"Type parameter '{Name}' is not a concrete type.");

    /// <summary>
    /// Initializes a new instance of the <see cref="TypeParameterPlaceholder"/> class.
    /// </summary>
    /// <param name="name">The name of the type parameter.</param>
    public TypeParameterPlaceholder(string name) : base(name: name)
    {
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Always thrown as type parameters cannot be instantiated.</exception>
    public override TypeInfo Instantiate(IReadOnlyList<TypeInfo> typeArguments)
    {
        throw new InvalidOperationException(
            message: $"Type parameter '{Name}' cannot be instantiated.");
    }
}
