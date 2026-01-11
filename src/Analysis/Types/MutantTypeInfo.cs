namespace Compilers.Analysis.Types;

using Enums;

/// <summary>
/// Type information for mutants (untagged unions / raw memory unions).
/// Mutants require danger! block for access, mutable, no methods.
/// Cases use SCREAMING_SNAKE_CASE with single-type payloads.
/// </summary>
public sealed class MutantTypeInfo : TypeInfo
{
    public override TypeCategory Category => TypeCategory.Mutant;

    /// <summary>The cases of this mutant.</summary>
    public IReadOnlyList<VariantCaseInfo> Cases { get; init; } = [];

    /// <summary>
    /// For generic definitions, the original generic type this was instantiated from.
    /// </summary>
    public MutantTypeInfo? GenericDefinition { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MutantTypeInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the mutant type.</param>
    public MutantTypeInfo(string name) : base(name: name)
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
                message: $"Mutant '{Name}' is not a generic definition.");
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

        // Substitute types in cases
        var substitutedCases = Cases
            .Select(selector: c => SubstituteCaseType(caseInfo: c, substitution: substitution))
            .ToList();

        // Build instantiated type name
        string instantiatedName = $"{Name}<{string.Join(separator: ", ",
            values: typeArguments.Select(selector: t => t.Name))}>";

        return new MutantTypeInfo(name: instantiatedName)
        {
            Cases = substitutedCases,
            TypeArguments = typeArguments,
            GenericDefinition = this,
            Visibility = Visibility,
            Location = Location,
            Namespace = Namespace
        };
    }

    /// <summary>
    /// Substitutes the payload type in a case for generic instantiation.
    /// </summary>
    /// <param name="caseInfo">The case to substitute.</param>
    /// <param name="substitution">The type parameter substitution map.</param>
    /// <returns>A new <see cref="VariantCaseInfo"/> with the substituted type, or the original if no payload.</returns>
    private static VariantCaseInfo SubstituteCaseType(VariantCaseInfo caseInfo,
        Dictionary<string, TypeInfo> substitution)
    {
        if (caseInfo.PayloadType == null)
        {
            return caseInfo;
        }

        TypeInfo substitutedType =
            SubstituteType(type: caseInfo.PayloadType, substitution: substitution);
        return caseInfo.WithSubstitutedType(newPayloadType: substitutedType);
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

        if (type is { IsGenericInstantiation: true, TypeArguments: not null })
        {
            var newArgs = type.TypeArguments
                .Select(selector: arg => SubstituteType(type: arg, substitution: substitution))
                .ToList();

            if (type is MutantTypeInfo { GenericDefinition: not null } mutantType)
            {
                return mutantType.GenericDefinition.Instantiate(typeArguments: newArgs);
            }
        }

        return type;
    }
}
