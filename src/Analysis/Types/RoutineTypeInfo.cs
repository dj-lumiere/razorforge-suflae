namespace Compilers.Analysis.Types;

using Compilers.Analysis.Enums;

/// <summary>
/// Type information for first-class function types (lambdas, function references).
/// Represents types like (S32, S32) -> S32 or () -> Bool.
/// </summary>
public sealed class RoutineTypeInfo : TypeInfo
{
    public override TypeCategory Category => TypeCategory.Routine;

    /// <summary>Parameter types for this function type.</summary>
    public IReadOnlyList<TypeInfo> ParameterTypes { get; }

    /// <summary>Return type for this function type. Null means no return (Blank).</summary>
    public TypeInfo? ReturnType { get; }

    /// <summary>Whether this function type is failable (can throw/absent).</summary>
    public bool IsFailable { get; init; }

    /// <summary>
    /// Creates a new routine type with the given parameter and return types.
    /// </summary>
    /// <param name="parameterTypes">The parameter types.</param>
    /// <param name="returnType">The return type (null for Blank/void).</param>
    public RoutineTypeInfo(IReadOnlyList<TypeInfo> parameterTypes, TypeInfo? returnType)
        : base(name: BuildName(parameterTypes, returnType))
    {
        ParameterTypes = parameterTypes;
        ReturnType = returnType;
    }

    /// <summary>
    /// Builds the display name for a function type.
    /// Examples: "(S32, S32) -> S32", "() -> Bool", "(Text) -> Blank"
    /// </summary>
    private static string BuildName(IReadOnlyList<TypeInfo> parameterTypes, TypeInfo? returnType)
    {
        string paramList = string.Join(", ", parameterTypes.Select(p => p.Name));
        string returnName = returnType?.Name ?? "Blank";
        return $"({paramList}) -> {returnName}";
    }

    /// <summary>
    /// Checks if this function type is compatible with another function type.
    /// Function types are compatible if parameter types and return type match.
    /// </summary>
    /// <param name="other">The other function type to compare.</param>
    /// <returns>True if compatible, false otherwise.</returns>
    public bool IsCompatibleWith(RoutineTypeInfo other)
    {
        // Check parameter count
        if (ParameterTypes.Count != other.ParameterTypes.Count)
        {
            return false;
        }

        // Check parameter types (contravariant - other's params must be assignable to ours)
        for (int i = 0; i < ParameterTypes.Count; i++)
        {
            if (ParameterTypes[i].Name != other.ParameterTypes[i].Name)
            {
                return false;
            }
        }

        // Check return type (covariant - our return must be assignable to other's)
        if (ReturnType == null && other.ReturnType == null)
        {
            return true;
        }

        if (ReturnType == null || other.ReturnType == null)
        {
            return false;
        }

        return ReturnType.Name == other.ReturnType.Name;
    }

    /// <inheritdoc/>
    public override TypeInfo Instantiate(IReadOnlyList<TypeInfo> typeArguments)
    {
        // Function types don't have generic parameters in the traditional sense
        // But we might need to substitute type parameters in param/return types
        throw new NotSupportedException("Function types cannot be directly instantiated.");
    }

    /// <summary>
    /// Creates a new RoutineTypeInfo with substituted type parameters.
    /// </summary>
    /// <param name="substitution">Map from type parameter names to concrete types.</param>
    /// <returns>A new RoutineTypeInfo with substituted types.</returns>
    public RoutineTypeInfo Substitute(Dictionary<string, TypeInfo> substitution)
    {
        var substitutedParams = ParameterTypes
            .Select(p => SubstituteType(p, substitution))
            .ToList();

        TypeInfo? substitutedReturn = ReturnType != null
            ? SubstituteType(ReturnType, substitution)
            : null;

        return new RoutineTypeInfo(substitutedParams, substitutedReturn)
        {
            IsFailable = IsFailable
        };
    }

    /// <summary>
    /// Substitutes type parameters in a type.
    /// </summary>
    private static TypeInfo SubstituteType(TypeInfo type, Dictionary<string, TypeInfo> substitution)
    {
        if (substitution.TryGetValue(type.Name, out TypeInfo? substituted))
        {
            return substituted;
        }

        // For generic instantiations, recursively substitute
        if (type.IsGenericInstantiation && type.TypeArguments != null)
        {
            var newArgs = type.TypeArguments
                .Select(arg => SubstituteType(arg, substitution))
                .ToList();

            // Would need to reconstruct the instantiation - for now just return original
            // This is a limitation that can be addressed later
        }

        return type;
    }
}