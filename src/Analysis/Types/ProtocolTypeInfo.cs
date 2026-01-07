namespace Compilers.Analysis.Types;

using Compilers.Analysis.Enums;

/// <summary>
/// Type information for protocols (interface/trait definitions).
/// Protocols define contracts that types can implement via the `follows` keyword.
/// </summary>
public sealed class ProtocolTypeInfo : TypeInfo
{
    public override TypeCategory Category => TypeCategory.Protocol;

    /// <summary>Method signatures defined by this protocol.</summary>
    public IReadOnlyList<ProtocolMethodInfo> Methods { get; init; } =
        Array.Empty<ProtocolMethodInfo>();

    /// <summary>Parent protocols that this protocol extends.</summary>
    public IReadOnlyList<ProtocolTypeInfo> ParentProtocols { get; init; } =
        Array.Empty<ProtocolTypeInfo>();

    /// <summary>
    /// For generic definitions, the original generic type this was instantiated from.
    /// </summary>
    public ProtocolTypeInfo? GenericDefinition { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolTypeInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the protocol.</param>
    public ProtocolTypeInfo(string name) : base(name: name)
    {
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Thrown if this is not a generic definition.</exception>
    /// <exception cref="ArgumentException">Thrown if the number of type arguments doesn't match.</exception>
    public override TypeInfo Instantiate(IReadOnlyList<TypeInfo> typeArguments)
    {
        if (!IsGenericDefinition)
        {
            throw new InvalidOperationException(
                message: $"Protocol '{Name}' is not a generic definition.");
        }

        if (typeArguments.Count != GenericParameters!.Count)
        {
            throw new ArgumentException(
                message:
                $"Expected {GenericParameters.Count} type arguments, got {typeArguments.Count}.");
        }

        // Create type parameter substitution map
        var substitution = new Dictionary<string, TypeInfo>();
        for (int i = 0; i < GenericParameters.Count; i++)
        {
            substitution[key: GenericParameters[i]] = typeArguments[i];
        }

        // Build instantiated type name
        string instantiatedName = $"{Name}<{string.Join(separator: ", ",
            values: typeArguments.Select(selector: t => t.Name))}>";

        // TODO: Substitute types in method signatures

        return new ProtocolTypeInfo(name: instantiatedName)
        {
            Methods = Methods,
            ParentProtocols = ParentProtocols,
            TypeArguments = typeArguments,
            GenericDefinition = this,
            Visibility = Visibility,
            Location = Location,
            Namespace = Namespace
        };
    }
}
