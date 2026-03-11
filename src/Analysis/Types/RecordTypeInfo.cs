namespace SemanticAnalysis.Types;

using Enums;
using Symbols;

/// <summary>
/// Type information for records (value types with copy semantics).
/// Includes "primitive-like" types (s32, bool, etc.) which are single-member-variable records
/// wrapping LLVM intrinsics.
/// </summary>
public sealed class RecordTypeInfo : TypeInfo
{
    public override TypeCategory Category => TypeCategory.Record;

    /// <summary>MemberVariables declared in this record.</summary>
    public IReadOnlyList<MemberVariableInfo> MemberVariables { get; init; } = [];

    /// <summary>Protocols this record implements (obeys).</summary>
    public IReadOnlyList<TypeInfo> ImplementedProtocols { get; init; } = [];

    /// <summary>
    /// Whether this is a single-member-variable record that wraps an intrinsic type.
    /// These records can be treated as their underlying LLVM type for operations.
    /// Examples: s32, bool, f64, uaddr
    /// </summary>
    public bool IsSingleMemberVariableWrapper => MemberVariables is [{ Type: IntrinsicTypeInfo }];

    /// <summary>
    /// For single-member-variable wrappers, gets the underlying intrinsic type.
    /// Returns null if not a single-member-variable wrapper.
    /// </summary>
    public IntrinsicTypeInfo? UnderlyingIntrinsic =>
        IsSingleMemberVariableWrapper ? MemberVariables[0].Type as IntrinsicTypeInfo : null;

    /// <summary>
    /// The LLVM type representation for this record.
    /// For single-member-variable wrappers, this is the intrinsic type (e.g., "i32").
    /// For multi-member-variable records, this is a struct type.
    /// </summary>
    public string LlvmType
    {
        get
        {
            if (UnderlyingIntrinsic != null)
            {
                return UnderlyingIntrinsic.LlvmType;
            }

            // Multi-member-variable record: struct type
            string memberVariableTypes = string.Join(separator: ", ",
                values: MemberVariables.Select(selector: GetLlvmTypeForMemberVariable));
            return $"{{ {memberVariableTypes} }}";
        }
    }

    /// <summary>
    /// For generic definitions, the original generic type this was resolved from.
    /// </summary>
    public RecordTypeInfo? GenericDefinition { get; init; }

    /// <summary>
    /// Looks up a member variable by name in this record.
    /// </summary>
    /// <param name="memberVariableName">The name of the member variable to look up.</param>
    /// <returns>The member variable info if found, null otherwise.</returns>
    public MemberVariableInfo? LookupMemberVariable(string memberVariableName)
    {
        return MemberVariables.FirstOrDefault(predicate: f => f.Name == memberVariableName);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordTypeInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the record type.</param>
    public RecordTypeInfo(string name) : base(name: name)
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
                message: $"Record '{Name}' is not a generic definition.");
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

        // Build resolved type name (e.g., "List[s32]")
        string resolvedName = $"{Name}[{string.Join(separator: ", ",
            values: typeArguments.Select(selector: t => t.Name))}]";

        return new RecordTypeInfo(name: resolvedName)
        {
            MemberVariables = substitutedMemberVariables,
            ImplementedProtocols = ImplementedProtocols, // TODO: substitute protocol type args
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
        // If it's a type parameter, substitute it
        if (substitution.TryGetValue(key: type.Name, value: out TypeInfo? substituted))
        {
            return substituted;
        }

        // If it's a generic resolution, recursively substitute
        if (!type.IsGenericResolution || type.TypeArguments == null)
        {
            return type;
        }

        var newArgs = type.TypeArguments
                          .Select(selector: arg => SubstituteType(type: arg, substitution: substitution))
                          .ToList();

        // Get the generic definition and create resolved instance with new args
        if (type is RecordTypeInfo { GenericDefinition: not null } recordType)
        {
            return recordType.GenericDefinition.CreateInstance(typeArguments: newArgs);
        }

        return type;
    }

    /// <summary>
    /// Gets the LLVM type string for a member variable.
    /// </summary>
    /// <param name="memberVariable">The member variable to get the LLVM type for.</param>
    /// <returns>The LLVM type string.</returns>
    private static string GetLlvmTypeForMemberVariable(MemberVariableInfo memberVariable)
    {
        return memberVariable.Type switch
        {
            IntrinsicTypeInfo intrinsic => intrinsic.LlvmType,
            RecordTypeInfo record => record.LlvmType,
            _ => "ptr" // Reference types are pointers
        };
    }
}
