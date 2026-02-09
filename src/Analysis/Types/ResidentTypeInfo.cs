namespace Compilers.Analysis.Types;

using Enums;
using Symbols;

/// <summary>
/// Type information for residents (fixed-size reference types in persistent memory).
/// RazorForge only - Suflae does not have residents.
/// Resident variables cannot be reassigned - they have stable identity.
/// </summary>
public sealed class ResidentTypeInfo : TypeInfo
{
    public override TypeCategory Category => TypeCategory.Resident;

    /// <summary>Fields declared in this resident.</summary>
    public IReadOnlyList<FieldInfo> Fields { get; init; } = [];

    /// <summary>
    /// Looks up a field by name.
    /// </summary>
    /// <param name="fieldName">The name of the field to look up.</param>
    /// <returns>The field info if found, null otherwise.</returns>
    public FieldInfo? LookupField(string fieldName)
    {
        return Fields.FirstOrDefault(predicate: f => f.Name == fieldName);
    }

    /// <summary>Protocols this resident implements (follows).</summary>
    public IReadOnlyList<TypeInfo> ImplementedProtocols { get; init; } = [];

    /// <summary>
    /// The fixed size of this resident in bytes.
    /// Residents have a compile-time known size.
    /// </summary>
    public int FixedSize { get; init; }

    /// <summary>
    /// For generic definitions, the original generic type this was instantiated from.
    /// </summary>
    public ResidentTypeInfo? GenericDefinition { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ResidentTypeInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the resident type.</param>
    public ResidentTypeInfo(string name) : base(name: name)
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
                message: $"Resident '{Name}' is not a generic definition.");
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

        return new ResidentTypeInfo(name: instantiatedName)
        {
            Fields = substitutedFields,
            ImplementedProtocols = ImplementedProtocols,
            FixedSize = FixedSize, // May need recalculation based on type args
            TypeArguments = typeArguments,
            GenericDefinition = this,
            Visibility = Visibility,
            Location = Location,
            Module = Module
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

        if (type is ResidentTypeInfo { GenericDefinition: not null } residentType)
        {
            return residentType.GenericDefinition.Instantiate(typeArguments: newArgs);
        }

        return type;
    }
}
