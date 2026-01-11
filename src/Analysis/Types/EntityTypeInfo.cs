namespace Compilers.Analysis.Types;

using Enums;
using Symbols;

/// <summary>
/// Type information for entities (reference types, heap-allocated).
/// Entity variables cannot be reassigned - they have stable identity.
/// </summary>
public sealed class EntityTypeInfo : TypeInfo
{
    public override TypeCategory Category => TypeCategory.Entity;

    /// <summary>Fields declared in this entity.</summary>
    public IReadOnlyList<FieldInfo> Fields { get; init; } = [];

    /// <summary>Protocols this entity implements (follows).</summary>
    public IReadOnlyList<TypeInfo> ImplementedProtocols { get; init; } = [];

    /// <summary>
    /// For generic definitions, the original generic type this was instantiated from.
    /// </summary>
    public EntityTypeInfo? GenericDefinition { get; init; }

    /// <summary>
    /// Looks up a field by name in this entity.
    /// </summary>
    /// <param name="fieldName">The name of the field to look up.</param>
    /// <returns>The field info if found, null otherwise.</returns>
    public FieldInfo? LookupField(string fieldName)
    {
        return Fields.FirstOrDefault(predicate: f => f.Name == fieldName);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EntityTypeInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the entity type.</param>
    public EntityTypeInfo(string name) : base(name: name)
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
                message: $"Entity '{Name}' is not a generic definition.");
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

        // Substitute types in fields
        var substitutedFields = Fields
            .Select(selector: f => SubstituteFieldType(field: f, substitution: substitution))
            .ToList();

        // Build instantiated type name
        string instantiatedName = $"{Name}<{string.Join(separator: ", ",
            values: typeArguments.Select(selector: t => t.Name))}>";

        return new EntityTypeInfo(name: instantiatedName)
        {
            Fields = substitutedFields,
            ImplementedProtocols = ImplementedProtocols,
            TypeArguments = typeArguments,
            GenericDefinition = this,
            Visibility = Visibility,
            Location = Location,
            Namespace = Namespace
        };
    }

    /// <summary>
    /// Substitutes the type in a field for generic instantiation.
    /// </summary>
    /// <param name="field">The field to substitute.</param>
    /// <param name="substitution">The type parameter substitution map.</param>
    /// <returns>A new <see cref="FieldInfo"/> with the substituted type.</returns>
    private static FieldInfo SubstituteFieldType(FieldInfo field,
        Dictionary<string, TypeInfo> substitution)
    {
        TypeInfo substitutedType = SubstituteType(type: field.Type, substitution: substitution);
        return field.WithSubstitutedType(newType: substitutedType);
    }

    /// <summary>
    /// Recursively substitutes type parameters in a type.
    /// </summary>
    /// <param name="type">The type to substitute.</param>
    /// <param name="substitution">The type parameter substitution map.</param>
    /// <returns>The substituted type, or the original if no substitution applies.</returns>
    private static TypeInfo SubstituteType(TypeInfo type,
        Dictionary<string, TypeInfo> substitution)
    {
        if (substitution.TryGetValue(key: type.Name, value: out TypeInfo? substituted))
        {
            return substituted;
        }

        if (!type.IsGenericInstantiation || type.TypeArguments == null)
        {
            return type;
        }

        var newArgs = type.TypeArguments
                          .Select(selector: arg => SubstituteType(type: arg, substitution: substitution))
                          .ToList();

        if (type is EntityTypeInfo { GenericDefinition: not null } entityType)
        {
            return entityType.GenericDefinition.Instantiate(typeArguments: newArgs);
        }

        return type;
    }
}
