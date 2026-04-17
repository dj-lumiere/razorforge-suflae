namespace SemanticVerification.Types;

using Enums;

/// <summary>
/// Type information for protocols (interface/trait definitions).
/// Protocols define contracts that types can implement via the `obeys` keyword.
/// </summary>
public sealed class ProtocolTypeInfo : TypeInfo
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.Protocol;

    /// <summary>Method signatures defined by this protocol.</summary>
    public IReadOnlyList<ProtocolMethodInfo> Methods { get; set; } = [];

    /// <summary>Parent protocols that this protocol extends.</summary>
    public IReadOnlyList<ProtocolTypeInfo> ParentProtocols { get; init; } = [];

    /// <summary>
    /// For generic definitions, the original generic type this was resolved from.
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
    public override TypeInfo CreateInstance(IReadOnlyList<TypeInfo> typeArguments)
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
            substitution[key: GenericParameters[index: i]] = typeArguments[index: i];
        }

        // Build resolved type name
        string resolvedName = $"{Name}[{string.Join(separator: ", ",
            values: typeArguments.Select(selector: t => t.Name))}]";

        var substitutedMethods = Methods
            .Select(selector: m => new ProtocolMethodInfo(name: m.Name)
            {
                IsInstanceMethod = m.IsInstanceMethod,
                Modification = m.Modification,
                ParameterTypes = m.ParameterTypes
                    .Select(selector: t => RecordTypeInfo.SubstituteType(type: t, substitution: substitution))
                    .ToList(),
                ParameterNames = m.ParameterNames,
                ReturnType = m.ReturnType != null
                    ? RecordTypeInfo.SubstituteType(type: m.ReturnType, substitution: substitution)
                    : null,
                IsFailable = m.IsFailable,
                GenerationKind = m.GenerationKind,
                HasDefaultImplementation = m.HasDefaultImplementation,
                Location = m.Location
            })
            .ToList();

        var substitutedParentProtocols = ParentProtocols
            .Select(selector: p =>
                (ProtocolTypeInfo)RecordTypeInfo.SubstituteType(type: p, substitution: substitution))
            .ToList();

        return new ProtocolTypeInfo(name: resolvedName)
        {
            Methods = substitutedMethods,
            ParentProtocols = substitutedParentProtocols,
            TypeArguments = typeArguments,
            GenericDefinition = this,
            Visibility = Visibility,
            Location = Location,
            Module = Module
        };
    }
}
