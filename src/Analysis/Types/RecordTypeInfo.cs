namespace Compilers.Analysis.Types;

using Compilers.Analysis.Enums;
using Compilers.Analysis.Symbols;

/// <summary>
/// Type information for records (value types with copy semantics).
/// Includes "primitive-like" types (s32, bool, etc.) which are single-field records
/// wrapping LLVM intrinsics.
/// </summary>
public sealed class RecordTypeInfo : TypeInfo
{
    public override TypeCategory Category => TypeCategory.Record;

    /// <summary>Fields declared in this record.</summary>
    public IReadOnlyList<FieldInfo> Fields { get; init; } = [];

    /// <summary>Protocols this record implements (follows).</summary>
    public IReadOnlyList<TypeInfo> ImplementedProtocols { get; init; } = [];

    /// <summary>
    /// Whether this is a single-field record that wraps an intrinsic type.
    /// These records can be treated as their underlying LLVM type for operations.
    /// Examples: s32, bool, f64, uaddr
    /// </summary>
    public bool IsSingleFieldWrapper => Fields is [{ Type: IntrinsicTypeInfo }];

    /// <summary>
    /// For single-field wrappers, gets the underlying intrinsic type.
    /// Returns null if not a single-field wrapper.
    /// </summary>
    public IntrinsicTypeInfo? UnderlyingIntrinsic =>
        IsSingleFieldWrapper ? Fields[0].Type as IntrinsicTypeInfo : null;

    /// <summary>
    /// The LLVM type representation for this record.
    /// For single-field wrappers, this is the intrinsic type (e.g., "i32").
    /// For multi-field records, this is a struct type.
    /// </summary>
    public string LlvmType
    {
        get
        {
            if (UnderlyingIntrinsic != null)
            {
                return UnderlyingIntrinsic.LlvmType;
            }

            // Multi-field record: struct type
            string fieldTypes = string.Join(separator: ", ",
                values: Fields.Select(selector: GetLlvmTypeForField));
            return $"{{ {fieldTypes} }}";
        }
    }

    /// <summary>
    /// For generic definitions, the original generic type this was instantiated from.
    /// </summary>
    public RecordTypeInfo? GenericDefinition { get; init; }

    /// <summary>
    /// Looks up a field by name in this record.
    /// </summary>
    /// <param name="fieldName">The name of the field to look up.</param>
    /// <returns>The field info if found, null otherwise.</returns>
    public FieldInfo? LookupField(string fieldName)
    {
        return Fields.FirstOrDefault(predicate: f => f.Name == fieldName);
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
    public override TypeInfo Instantiate(IReadOnlyList<TypeInfo> typeArguments)
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

        // Substitute types in fields
        var substitutedFields = Fields
            .Select(selector: f => SubstituteFieldType(field: f, substitution: substitution))
            .ToList();

        // Build instantiated type name (e.g., "List<s32>")
        string instantiatedName = $"{Name}<{string.Join(separator: ", ",
            values: typeArguments.Select(selector: t => t.Name))}>";

        return new RecordTypeInfo(name: instantiatedName)
        {
            Fields = substitutedFields,
            ImplementedProtocols = ImplementedProtocols, // TODO: substitute protocol type args
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
        // If it's a type parameter, substitute it
        if (substitution.TryGetValue(key: type.Name, value: out TypeInfo? substituted))
        {
            return substituted;
        }

        // If it's a generic instantiation, recursively substitute
        if (!type.IsGenericInstantiation || type.TypeArguments == null)
        {
            return type;
        }

        var newArgs = type.TypeArguments
                          .Select(selector: arg => SubstituteType(type: arg, substitution: substitution))
                          .ToList();

        // Get the generic definition and instantiate with new args
        if (type is RecordTypeInfo recordType && recordType.GenericDefinition != null)
        {
            return recordType.GenericDefinition.Instantiate(typeArguments: newArgs);
        }

        return type;
    }

    /// <summary>
    /// Gets the LLVM type string for a field.
    /// </summary>
    /// <param name="field">The field to get the LLVM type for.</param>
    /// <returns>The LLVM type string.</returns>
    private static string GetLlvmTypeForField(FieldInfo field)
    {
        return field.Type switch
        {
            IntrinsicTypeInfo intrinsic => intrinsic.LlvmType,
            RecordTypeInfo record => record.LlvmType,
            _ => "ptr" // Reference types are pointers
        };
    }
}
