namespace Compilers.Analysis.Symbols;

using Compilers.Analysis.Enums;
using Compilers.Analysis.Types;
using Compilers.Shared.AST;
using TypeSymbol = Compilers.Analysis.Types.TypeInfo;

/// <summary>
/// Information about a routine (function, method, constructor).
/// </summary>
public sealed class RoutineInfo
{
    /// <summary>The name of the routine (without type prefix).</summary>
    public string Name { get; }

    /// <summary>The fully qualified name (e.g., "Circle.draw", "Math.abs").</summary>
    public string FullName
    {
        get
        {
            if (OwnerType != null)
            {
                return $"{OwnerType.Name}.{Name}";
            }

            return string.IsNullOrEmpty(value: Namespace)
                ? Name
                : $"{Namespace}.{Name}";
        }
    }

    /// <summary>The kind of routine.</summary>
    public RoutineKind Kind { get; init; } = RoutineKind.Function;

    /// <summary>The type that owns this routine (for methods/extension methods).</summary>
    public TypeSymbol? OwnerType { get; init; }

    /// <summary>Parameters of this routine.</summary>
    public IReadOnlyList<ParameterInfo> Parameters { get; init; } = Array.Empty<ParameterInfo>();

    /// <summary>Return type, or null for void.</summary>
    public TypeSymbol? ReturnType { get; init; }

    /// <summary>Whether this routine can fail (has ! suffix).</summary>
    public bool IsFailable { get; init; }

    /// <summary>Whether this routine contains throw statements.</summary>
    public bool HasThrow { get; set; }

    /// <summary>Whether this routine contains absent statements.</summary>
    public bool HasAbsent { get; set; }

    /// <summary>The declared mutation category for this routine (from source annotation).</summary>
    public MutationCategory DeclaredMutation { get; init; } = MutationCategory.Migratable;

    /// <summary>
    /// The inferred/final mutation category for this routine.
    /// Initially set to declared value, then updated by mutation inference.
    /// </summary>
    public MutationCategory MutationCategory { get; set; } = MutationCategory.Migratable;

    /// <summary>Generic type parameters, if any.</summary>
    public IReadOnlyList<string>? GenericParameters { get; init; }

    /// <summary>Generic constraints on type parameters.</summary>
    public IReadOnlyList<GenericConstraintDeclaration>? GenericConstraints { get; init; }

    /// <summary>Whether this is a generic routine definition.</summary>
    public bool IsGenericDefinition => GenericParameters is { Count: > 0 };

    /// <summary>For instantiated generics, the type arguments used.</summary>
    public IReadOnlyList<TypeSymbol>? TypeArguments { get; init; }

    /// <summary>Visibility modifier.</summary>
    public VisibilityModifier Visibility { get; init; } = VisibilityModifier.Public;

    /// <summary>Source location where this routine is defined.</summary>
    public SourceLocation? Location { get; init; }

    /// <summary>The namespace/module this routine belongs to.</summary>
    public string? Namespace { get; init; }

    /// <summary>Attributes on this routine (e.g., @readonly, @inline).</summary>
    public IReadOnlyList<string> Attributes { get; init; } = Array.Empty<string>();

    /// <summary>Whether this routine is marked @readonly (can be called through Viewed/Inspected).</summary>
    public bool IsReadOnly => Attributes.Contains(value: "readonly");

    /// <summary>For imported routines, the calling convention.</summary>
    public string? CallingConvention { get; init; }

    /// <summary>For imported routines, whether it's variadic.</summary>
    public bool IsVariadic { get; init; }

    /// <summary>Whether this routine was auto-generated (e.g., derived comparison operators).</summary>
    public bool IsSynthesized { get; init; }

    /// <summary>
    /// For generic definitions, the original generic routine this was instantiated from.
    /// </summary>
    public RoutineInfo? GenericDefinition { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="RoutineInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the routine.</param>
    public RoutineInfo(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Creates an instantiated version of this generic routine with the given type arguments.
    /// </summary>
    /// <param name="typeArguments">The type arguments to substitute for generic parameters.</param>
    /// <returns>A new <see cref="RoutineInfo"/> with types substituted.</returns>
    /// <exception cref="InvalidOperationException">Thrown if this is not a generic definition.</exception>
    /// <exception cref="ArgumentException">Thrown if the number of type arguments doesn't match.</exception>
    public RoutineInfo Instantiate(IReadOnlyList<TypeSymbol> typeArguments)
    {
        if (!IsGenericDefinition)
        {
            throw new InvalidOperationException(
                message: $"Routine '{Name}' is not a generic definition.");
        }

        if (typeArguments.Count != GenericParameters!.Count)
        {
            throw new ArgumentException(
                message:
                $"Expected {GenericParameters.Count} type arguments, got {typeArguments.Count}.");
        }

        // Create type parameter substitution map
        var substitution = new Dictionary<string, TypeSymbol>();
        for (int i = 0; i < GenericParameters.Count; i++)
        {
            substitution[key: GenericParameters[i]] = typeArguments[i];
        }

        // Substitute types in parameters
        var substitutedParams = Parameters
            .Select(selector: p => SubstituteParameterType(param: p, substitution: substitution))
            .ToList();

        // Substitute return type
        TypeSymbol? substitutedReturnType = ReturnType != null
            ? SubstituteType(type: ReturnType, substitution: substitution)
            : null;

        return new RoutineInfo(name: Name)
        {
            Kind = Kind,
            OwnerType = OwnerType,
            Parameters = substitutedParams,
            ReturnType = substitutedReturnType,
            IsFailable = IsFailable,
            DeclaredMutation = DeclaredMutation,
            MutationCategory = MutationCategory,
            TypeArguments = typeArguments,
            GenericDefinition = this,
            Visibility = Visibility,
            Location = Location,
            Namespace = Namespace,
            Attributes = Attributes,
            CallingConvention = CallingConvention,
            IsVariadic = IsVariadic,
            IsSynthesized = IsSynthesized
        };
    }

    /// <summary>
    /// Substitutes the type in a parameter for generic instantiation.
    /// </summary>
    /// <param name="param">The parameter to substitute.</param>
    /// <param name="substitution">The type parameter substitution map.</param>
    /// <returns>A new <see cref="ParameterInfo"/> with the substituted type.</returns>
    private static ParameterInfo SubstituteParameterType(ParameterInfo param,
        Dictionary<string, TypeSymbol> substitution)
    {
        TypeSymbol substitutedType = SubstituteType(type: param.Type, substitution: substitution);
        return param.WithSubstitutedType(newType: substitutedType);
    }

    /// <summary>
    /// Recursively substitutes type parameters in a type.
    /// </summary>
    /// <param name="type">The type to substitute.</param>
    /// <param name="substitution">The type parameter substitution map.</param>
    /// <returns>The substituted type, or the original if no substitution applies.</returns>
    private static TypeSymbol SubstituteType(TypeSymbol type,
        Dictionary<string, TypeSymbol> substitution)
    {
        if (substitution.TryGetValue(key: type.Name, value: out TypeSymbol? substituted))
        {
            return substituted;
        }

        if (type.IsGenericInstantiation && type.TypeArguments != null)
        {
            var newArgs = type.TypeArguments
                .Select(selector: arg => SubstituteType(type: arg, substitution: substitution))
                .ToList();

            return type.Instantiate(typeArguments: newArgs);
        }

        return type;
    }
}
