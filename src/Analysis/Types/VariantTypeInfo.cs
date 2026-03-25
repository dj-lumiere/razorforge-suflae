namespace SemanticAnalysis.Types;

using Enums;

/// <summary>
/// Type information for variants (type-based tagged unions).
/// Variants are local-only and unmodifiable with no methods.
/// Members are types — the type IS the tag. No named cases.
/// </summary>
public sealed class VariantTypeInfo : TypeInfo
{
    public override TypeCategory Category => TypeCategory.Variant;

    /// <summary>The member types of this variant.</summary>
    public IReadOnlyList<VariantMemberInfo> Members { get; init; } = [];

    /// <summary>
    /// For generic definitions, the original generic type this was resolved from.
    /// </summary>
    public VariantTypeInfo? GenericDefinition { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="VariantTypeInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the variant type.</param>
    public VariantTypeInfo(string name) : base(name: name)
    {
    }

    /// <summary>
    /// Finds a member by its type name.
    /// </summary>
    /// <param name="type">The type to look up.</param>
    /// <returns>The matching member info, or null if not found.</returns>
    public VariantMemberInfo? FindMember(TypeInfo type)
    {
        foreach (var member in Members)
        {
            if (member.Type.Name == type.Name)
                return member;
        }
        return null;
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Thrown if this is not a generic definition.</exception>
    /// <exception cref="ArgumentException">Thrown if the number of type arguments doesn't match.</exception>
    public override TypeInfo CreateInstance(IReadOnlyList<TypeInfo> typeArguments)
    {
        if (!IsGenericDefinition)
        {
            throw new InvalidOperationException(
                message: $"Variant '{Name}' is not a generic definition.");
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

        // Substitute types in members
        var substitutedMembers = Members
            .Select(selector: m => SubstituteMemberType(memberInfo: m, substitution: substitution))
            .ToList();

        // Build resolved type name
        string resolvedName = $"{Name}[{string.Join(separator: ", ",
            values: typeArguments.Select(selector: t => t.Name))}]";

        return new VariantTypeInfo(name: resolvedName)
        {
            Members = substitutedMembers,
            TypeArguments = typeArguments,
            GenericDefinition = this,
            Visibility = Visibility,
            Location = Location,
            Module = Module
        };
    }

    /// <summary>
    /// Substitutes the type in a member for generic resolution.
    /// </summary>
    private static VariantMemberInfo SubstituteMemberType(VariantMemberInfo memberInfo,
        Dictionary<string, TypeInfo> substitution)
    {
        if (memberInfo.IsNone)
            return memberInfo; // None state has no type to substitute

        TypeInfo substitutedType =
            SubstituteType(type: memberInfo.Type!, substitution: substitution);
        if (substitutedType == memberInfo.Type)
            return memberInfo;
        return memberInfo.WithSubstitutedType(newType: substitutedType);
    }

    /// <summary>
    /// Recursively substitutes type parameters in a type.
    /// </summary>
    private static TypeInfo SubstituteType(TypeInfo type,
        Dictionary<string, TypeInfo> substitution)
    {
        if (substitution.TryGetValue(key: type.Name, value: out TypeInfo? substituted))
        {
            return substituted;
        }

        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            var newArgs = type.TypeArguments
                .Select(selector: arg => SubstituteType(type: arg, substitution: substitution))
                .ToList();

            if (type is VariantTypeInfo { GenericDefinition: not null } variantType)
            {
                return variantType.GenericDefinition.CreateInstance(typeArguments: newArgs);
            }
        }

        return type;
    }
}
