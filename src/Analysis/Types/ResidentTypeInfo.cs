namespace SemanticAnalysis.Types;

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

    /// <summary>MemberVariables declared in this resident.</summary>
    public IReadOnlyList<MemberVariableInfo> MemberVariables { get; set; } = [];

    /// <summary>
    /// Looks up a member variable by name.
    /// </summary>
    /// <param name="memberVariableName">The name of the member variable to look up.</param>
    /// <returns>The member variable info if found, null otherwise.</returns>
    public MemberVariableInfo? LookupMemberVariable(string memberVariableName)
    {
        return MemberVariables.FirstOrDefault(predicate: f => f.Name == memberVariableName);
    }

    /// <summary>Protocols this resident implements (obeys).</summary>
    public IReadOnlyList<TypeInfo> ImplementedProtocols { get; init; } = [];

    /// <summary>
    /// The fixed size of this resident in bytes.
    /// Residents have a build-time known size.
    /// </summary>
    public int FixedSize { get; init; }

    /// <summary>
    /// For generic definitions, the original generic type this was resolved from.
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
    public override TypeInfo CreateInstance(IReadOnlyList<TypeInfo> typeArguments)
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

        // Substitute types in member variables
        var substitutedMemberVariables = MemberVariables
            .Select(selector: f => SubstituteMemberVariableType(memberVariable: f, substitution: substitution))
            .ToList();

        // Build resolved type name
        string resolvedName = $"{Name}[{string.Join(separator: ", ",
            values: typeArguments.Select(selector: t => t.Name))}]";

        return new ResidentTypeInfo(name: resolvedName)
        {
            MemberVariables = substitutedMemberVariables,
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
    /// Substitutes the type in a member variable for generic resolution.
    /// </summary>
    /// <param name="memberVariable">The member variable to substitute.</param>
    /// <param name="substitution">The type parameter substitution map.</param>
    /// <returns>A new <see cref="MemberVariableInfo"/> with the substituted type.</returns>
    private static MemberVariableInfo SubstituteMemberVariableType(MemberVariableInfo memberVariable,
        Dictionary<string, TypeInfo> substitution)
    {
        TypeInfo substitutedType = SubstituteType(type: memberVariable.Type, substitution: substitution);
        return memberVariable.WithSubstitutedType(newType: substitutedType);
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

        if (!type.IsGenericResolution || type.TypeArguments == null)
        {
            return type;
        }

        var newArgs = type.TypeArguments
                          .Select(selector: arg => SubstituteType(type: arg, substitution: substitution))
                          .ToList();

        if (type is ResidentTypeInfo { GenericDefinition: not null } residentType)
        {
            return residentType.GenericDefinition.CreateInstance(typeArguments: newArgs);
        }

        if (type is EntityTypeInfo { GenericDefinition: not null } entityType)
        {
            return entityType.GenericDefinition.CreateInstance(typeArguments: newArgs);
        }

        if (type is RecordTypeInfo { GenericDefinition: not null } recordType)
        {
            return recordType.GenericDefinition.CreateInstance(typeArguments: newArgs);
        }

        return type;
    }
}
