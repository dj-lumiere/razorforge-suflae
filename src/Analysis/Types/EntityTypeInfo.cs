namespace SemanticAnalysis.Types;

using Enums;
using Symbols;

/// <summary>
/// Type information for entities (reference types, heap-allocated).
/// Entity variables cannot be reassigned - they have stable identity.
/// </summary>
public sealed class EntityTypeInfo : TypeInfo
{
    public override TypeCategory Category => TypeCategory.Entity;

    /// <summary>MemberVariables declared in this entity.</summary>
    public IReadOnlyList<MemberVariableInfo> MemberVariables { get; set; } = [];

    /// <summary>Protocols this entity implements (obeys).</summary>
    public IReadOnlyList<TypeInfo> ImplementedProtocols { get; set; } = [];

    /// <summary>
    /// For generic definitions, the original generic type this was resolved from.
    /// </summary>
    public EntityTypeInfo? GenericDefinition { get; init; }

    /// <summary>
    /// Looks up a member variable by name in this entity.
    /// </summary>
    /// <param name="memberVariableName">The name of the member variable to look up.</param>
    /// <returns>The member variable info if found, null otherwise.</returns>
    public MemberVariableInfo? LookupMemberVariable(string memberVariableName)
    {
        return MemberVariables.FirstOrDefault(predicate: f => f.Name == memberVariableName);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EntityTypeInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the entity type.</param>
    public EntityTypeInfo(string name) : base(name: name)
    {
    }

    /// Tracks in-progress CreateInstance calls to break cycles from self-referential types
    /// (e.g., BTreeSetNode[T] containing Maybe[BTreeSetNode[T]]).
    [ThreadStatic]
    private static HashSet<string>? _creatingInstances;

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Thrown if this is not a generic definition.</exception>
    /// <exception cref="ArgumentException">Thrown if the number of type arguments doesn't match.</exception>
    public override TypeInfo CreateInstance(IReadOnlyList<TypeInfo> typeArguments)
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

        // Build resolved type name
        string resolvedName = $"{Name}[{string.Join(separator: ", ",
            values: typeArguments.Select(selector: t => t.Name))}]";

        // Detect cycles: if we're already creating this exact resolution, return a shell
        // to break infinite recursion. The shell is used only as a type reference in member
        // variable lists, not to access members directly.
        _creatingInstances ??= new HashSet<string>();
        if (!_creatingInstances.Add(item: resolvedName))
        {
            return new EntityTypeInfo(name: resolvedName)
            {
                MemberVariables = [],
                ImplementedProtocols = ImplementedProtocols,
                TypeArguments = typeArguments,
                GenericDefinition = this,
                Visibility = Visibility,
                Location = Location,
                Module = Module
            };
        }

        try
        {
            // Create type parameter substitution map
            var substitution = new Dictionary<string, TypeInfo>();
            for (int i = 0; i < GenericParameters.Count; i++)
            {
                substitution[key: GenericParameters[index: i]] = typeArguments[index: i];
            }

            // Substitute types in member variables
            var substitutedMemberVariables = MemberVariables
                                            .Select(selector: f =>
                                                 SubstituteMemberVariableType(memberVariable: f,
                                                     substitution: substitution))
                                            .ToList();

            return new EntityTypeInfo(name: resolvedName)
            {
                MemberVariables = substitutedMemberVariables,
                ImplementedProtocols = ImplementedProtocols,
                TypeArguments = typeArguments,
                GenericDefinition = this,
                Visibility = Visibility,
                Location = Location,
                Module = Module
            };
        }
        finally
        {
            _creatingInstances.Remove(item: resolvedName);
        }
    }

    /// <summary>
    /// Substitutes the type in a member variable for generic resolution.
    /// </summary>
    /// <param name="memberVariable">The member variable to substitute.</param>
    /// <param name="substitution">The type parameter substitution map.</param>
    /// <returns>A new <see cref="MemberVariableInfo"/> with the substituted type.</returns>
    private static MemberVariableInfo SubstituteMemberVariableType(
        MemberVariableInfo memberVariable, Dictionary<string, TypeInfo> substitution)
    {
        TypeInfo substitutedType =
            SubstituteType(type: memberVariable.Type, substitution: substitution);
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
                          .Select(selector: arg =>
                               SubstituteType(type: arg, substitution: substitution))
                          .ToList();

        if (type is EntityTypeInfo { GenericDefinition: not null } entityType)
        {
            return entityType.GenericDefinition.CreateInstance(typeArguments: newArgs);
        }

        if (type is RecordTypeInfo { GenericDefinition: not null } recordType)
        {
            return recordType.GenericDefinition.CreateInstance(typeArguments: newArgs);
        }

        if (type is ProtocolTypeInfo { GenericDefinition: not null } protocolType)
        {
            return protocolType.GenericDefinition.CreateInstance(typeArguments: newArgs);
        }

        if (type is ErrorHandlingTypeInfo { GenericDefinition: not null } errorType)
        {
            return errorType.GenericDefinition.CreateInstance(typeArguments: newArgs);
        }

        return type;
    }
}
