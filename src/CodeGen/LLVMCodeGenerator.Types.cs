using SemanticAnalysis.Symbols;

namespace Compiler.CodeGen;

using SemanticAnalysis.Types;

/// <summary>
/// Type mapping: RazorForge/Suflae types → LLVM IR types.
/// </summary>
public partial class LLVMCodeGenerator
{
    #region Type Mapping

    /// <summary>
    /// Gets the LLVM type name for a TypeInfo.
    /// </summary>
    /// <param name="type">The type to convert.</param>
    /// <returns>The LLVM type string.</returns>
    private TypeInfo ResolveTypeSubstitution(TypeInfo type)
    {
        if (_typeSubstitutions == null)
        {
            return type;
        }

        // Direct generic parameter substitution (e.g., K → S64)
        if (_typeSubstitutions.TryGetValue(key: type.Name, value: out TypeInfo? sub))
        {
            return sub;
        }

        // Parameterized types with unresolved args (e.g., DictEntry[K, V] → DictEntry[S64, Text])
        if (type.TypeArguments is { Count: > 0 })
        {
            bool anyResolved = false;
            var resolvedArgs = new List<TypeInfo>();
            foreach (TypeInfo ta in type.TypeArguments)
            {
                TypeInfo resolved = ResolveTypeSubstitution(type: ta);
                resolvedArgs.Add(item: resolved);
                if (resolved != ta)
                {
                    anyResolved = true;
                }
            }

            if (anyResolved)
            {
                TypeInfo? genericBase = GetGenericBase(type: type);
                if (genericBase != null)
                {
                    return _registry.GetOrCreateResolution(genericDef: genericBase,
                        typeArguments: resolvedArgs);
                }
            }
        }

        return type;
    }

    private string GetLLVMType(TypeInfo type)
    {
        type = ResolveTypeSubstitution(type: type);
        return type switch
        {
            // Intrinsic types map directly to LLVM
            IntrinsicTypeInfo intrinsic => GetIntrinsicLLVMType(intrinsic: intrinsic),

            // Records with @llvm annotation → use backend type directly (skip generic definitions with template holes)
            RecordTypeInfo
            {
                HasDirectBackendType: true, IsGenericDefinition: false
            } record => record.LlvmType,

            // Legacy single-member-variable wrappers → unwrap to underlying intrinsic
            RecordTypeInfo { IsSingleMemberVariableWrapper: true } record => GetLLVMType(
                type: record.UnderlyingIntrinsic!),

            // Generic definition records (unresolved) → pointer fallback
            RecordTypeInfo { IsGenericDefinition: true } => "ptr",

            // Records with no fields — look up the registered definition (may have @llvm annotation)
            RecordTypeInfo { MemberVariables.Count: 0 } record when
                _registry.LookupType(name: record.Name) is RecordTypeInfo
                {
                    HasDirectBackendType: true
                } llvmRecord => llvmRecord.LlvmType,

            // Records with no fields and generic base type has @llvm annotation
            RecordTypeInfo
            {
                MemberVariables.Count: 0,
                GenericDefinition: { HasDirectBackendType: true } baseRecord
            } => baseRecord.LlvmType,

            // Error handling records (Maybe[T], Result[T], Lookup[T]) → always anonymous { i64, ptr }
            // This ensures consistency between RecordTypeInfo and ErrorHandlingTypeInfo representations.
            RecordTypeInfo record when GetGenericBaseName(type: record) is "Maybe" or "Result"
                or "Lookup" => "{ i64, ptr }",

            // Multi-member-variable records → LLVM struct type
            RecordTypeInfo record => GetRecordTypeName(record: record),

            // Entities → pointer to LLVM struct
            EntityTypeInfo => "ptr",

            // Wrappers (Viewed, Hijacked, Snatched, etc.) → all pointers at LLVM level
            WrapperTypeInfo => "ptr",

            // Choices → underlying integer type (S32)
            ChoiceTypeInfo => "i32",

            // Flags → underlying bitmask type (U64)
            FlagsTypeInfo => "i64",

            // Tuples → always inline struct
            TupleTypeInfo tuple => GetTupleTypeName(tuple: tuple),

            // Variants → struct { tag, payload }
            VariantTypeInfo variant => GetVariantTypeName(variant: variant),

            // Error handling types → struct { tag, payload }
            ErrorHandlingTypeInfo errorHandling => GetErrorHandlingTypeName(
                errorType: errorHandling),

            // Protocols → type-erased pointer (protocol-typed fields/params hold a handle to a concrete object)
            ProtocolTypeInfo => "ptr",

            // Const generic values — map to the underlying integer type
            ConstGenericValueTypeInfo => "i64",

            // Generic parameters — use ptr as fallback (should be resolved before reaching codegen)
            GenericParameterTypeInfo => "ptr",

            // Error placeholder
            ErrorTypeInfo => throw new InvalidOperationException(
                message:
                "Error type found in codegen - semantic analysis should have caught this"),

            // Unknown
            _ => throw new InvalidOperationException(
                message: $"Unknown type category: {type.Category}")
        };
    }

    /// <summary>
    /// Gets the LLVM type for an intrinsic type.
    /// </summary>
    private string GetIntrinsicLLVMType(IntrinsicTypeInfo intrinsic)
    {
        return intrinsic.Name switch
        {
            // Integer types
            "@intrinsic.i1" => "i1",
            "@intrinsic.i8" => "i8",
            "@intrinsic.i16" => "i16",
            "@intrinsic.i32" => "i32",
            "@intrinsic.i64" => "i64",
            "@intrinsic.i128" => "i128",
            "@intrinsic.iptr" => $"i{_pointerBitWidth}",
            "@intrinsic.uptr" => $"i{_pointerBitWidth}",

            // Floating-point types
            "@intrinsic.f16" => "half",
            "@intrinsic.f32" => "float",
            "@intrinsic.f64" => "double",
            "@intrinsic.f128" => "fp128",

            // Pointer type
            "@intrinsic.ptr" => "ptr",

            // Void (for function returns)
            "@intrinsic.void" => "void",

            _ => throw new InvalidOperationException(
                message: $"Unknown intrinsic type: {intrinsic.Name}")
        };
    }

    /// <summary>
    /// Gets the LLVM struct type name for a record.
    /// </summary>
    private static string GetRecordTypeName(RecordTypeInfo record)
    {
        return $"%{Q(name: $"Record.{record.Name}")}";
    }

    /// <summary>
    /// Gets the LLVM struct type name for an entity.
    /// </summary>
    private static string GetEntityTypeName(EntityTypeInfo entity)
    {
        return $"%{Q(name: $"Entity.{entity.Name}")}";
    }

    /// <summary>
    /// Gets the LLVM struct type name for a variant.
    /// </summary>
    private static string GetVariantTypeName(VariantTypeInfo variant)
    {
        return $"%{Q(name: $"Variant.{variant.Name}")}";
    }

    /// <summary>
    /// Gets the LLVM struct type name for an error handling type.
    /// </summary>
    private static string GetErrorHandlingTypeName(ErrorHandlingTypeInfo errorType)
    {
        // All error handling types (Maybe, Result, Lookup) share the same { i64, ptr } layout.
        // Using the anonymous struct avoids naming conflicts between Record and ErrorHandling prefixes.
        return "{ i64, ptr }";
    }

    /// <summary>
    /// Gets the LLVM struct type name for a choice.
    /// </summary>
    private static string GetChoiceTypeName(ChoiceTypeInfo choice)
    {
        return $"%{Q(name: $"Choice.{choice.Name}")}";
    }

    /// <summary>
    /// Gets the LLVM struct type name for a tuple.
    /// </summary>
    private string GetTupleTypeName(TupleTypeInfo tuple)
    {
        // Tuples are inline LLVM structs: { i64, i64 } etc.
        // Resolve type substitutions for generic params (e.g., T → S64)
        var parts = new List<string>();
        foreach (TypeInfo elemType in tuple.ElementTypes)
        {
            TypeInfo resolved = ResolveTypeSubstitution(type: elemType);
            parts.Add(item: GetLLVMType(type: resolved));
        }

        return $"{{ {string.Join(separator: ", ", values: parts)} }}";
    }

    /// <summary>
    /// Calculates the size of a tuple type (sum of element sizes with alignment).
    /// </summary>
    private int CalculateTupleSize(TupleTypeInfo tuple)
    {
        int size = 0;
        int maxAlignment = 1;
        foreach (TypeInfo elemType in tuple.ElementTypes)
        {
            int elemSize = GetTypeSize(type: elemType);
            int alignment = Math.Max(val1: Math.Min(val1: elemSize, val2: 16), val2: 1);
            maxAlignment = Math.Max(val1: maxAlignment, val2: alignment);
            size = AlignTo(size: size, alignment: alignment);
            size += elemSize;
        }

        return AlignTo(size: size, alignment: maxAlignment);
    }

    /// <summary>
    /// Returns the name unchanged — LLVM quoted identifiers handle special characters.
    /// </summary>
    private static string MangleTypeName(string name)
    {
        return name;
    }

    /// <summary>
    /// Quotes an LLVM identifier if it contains characters that require quoting.
    /// LLVM allows any characters in quoted identifiers: @"Snatched[Point].$eq", %"Record.Snatched[Point]".
    /// Unquoted identifiers only allow [a-zA-Z$._0-9-].
    /// </summary>
    private static string Q(string name)
    {
        foreach (char c in name)
        {
            if (!char.IsLetterOrDigit(c: c) && c != '$' && c != '.' && c != '_' && c != '-')
            {
                return $"\"{name}\"";
            }
        }

        return name;
    }

    /// <summary>
    /// Gets the LLVM type for a function parameter or return type.
    /// For entities, this returns ptr (all entities are pointers).
    /// For records, this returns the struct type (passed by value).
    /// </summary>
    private string GetParameterLLVMType(TypeInfo type)
    {
        type = ResolveTypeSubstitution(type: type);
        return type switch
        {
            // Entities are always passed as pointers
            EntityTypeInfo => "ptr",

            // Other types use normal mapping
            _ => GetLLVMType(type: type)
        };
    }

    /// <summary>
    /// Gets the size in bytes for a type (for allocation).
    /// </summary>
    private int GetTypeSize(TypeInfo type)
    {
        return type switch
        {
            IntrinsicTypeInfo intrinsic => GetIntrinsicSize(intrinsic: intrinsic),
            RecordTypeInfo { HasDirectBackendType: true, IsGenericDefinition: false } record =>
                GetTypeSizeFromLlvmType(llvmType: record.BackendType!),
            RecordTypeInfo { IsSingleMemberVariableWrapper: true } record => GetTypeSize(
                type: record.UnderlyingIntrinsic!),
            RecordTypeInfo record => CalculateRecordSize(record: record),
            EntityTypeInfo => _pointerSizeBytes, // Entities are heap-allocated, stored as pointers
            TupleTypeInfo tuple => CalculateTupleSize(tuple: tuple),
            WrapperTypeInfo => _pointerSizeBytes, // Pointer size
            ChoiceTypeInfo => 4, // i32 tag
            VariantTypeInfo variant => CalculateVariantSize(variant: variant),
            _ => _pointerSizeBytes // Default to pointer size
        };
    }

    /// <summary>
    /// Gets the size in bytes for an intrinsic type.
    /// </summary>
    private int GetIntrinsicSize(IntrinsicTypeInfo intrinsic)
    {
        return intrinsic.Name switch
        {
            "@intrinsic.i1" => 1,
            "@intrinsic.i8" => 1,
            "@intrinsic.i16" => 2,
            "@intrinsic.i32" => 4,
            "@intrinsic.i64" => 8,
            "@intrinsic.i128" => 16,
            "@intrinsic.iptr" => _pointerBitWidth / 8,
            "@intrinsic.uptr" => _pointerBitWidth / 8,
            "@intrinsic.f16" => 2,
            "@intrinsic.f32" => 4,
            "@intrinsic.f64" => 8,
            "@intrinsic.f128" => 16,
            "@intrinsic.ptr" => _pointerSizeBytes,
            _ => _pointerSizeBytes
        };
    }

    /// <summary>
    /// Gets the size in bytes for an LLVM type string (from @llvm annotation).
    /// </summary>
    private int GetTypeSizeFromLlvmType(string llvmType)
    {
        // Array types: [N x elemType]
        if (llvmType.StartsWith(value: "[") && llvmType.Contains(value: " x "))
        {
            string[] parts = llvmType[1..^1]
               .Split(separator: " x ", count: 2);
            int count = int.Parse(s: parts[0]
               .Trim());
            int elemSize = GetTypeSizeFromLlvmType(llvmType: parts[1]
               .Trim());
            return count * elemSize;
        }

        return llvmType switch
        {
            "i1" => 1,
            "i8" => 1,
            "i16" => 2,
            "i32" => 4,
            "i64" => 8,
            "i128" => 16,
            "half" => 2,
            "float" => 4,
            "double" => 8,
            "fp128" => 16,
            "ptr" => _pointerSizeBytes,
            _ => throw new InvalidOperationException(
                message: $"Unknown LLVM type for size calculation: {llvmType}")
        };
    }

    /// <summary>
    /// Calculates the size of a record type (sum of member variable sizes with alignment).
    /// </summary>
    private int CalculateRecordSize(RecordTypeInfo record)
    {
        int size = 0;
        int maxAlignment = 1;
        foreach (MemberVariableInfo memberVariable in record.MemberVariables)
        {
            int memberVariableSize = GetTypeSize(type: memberVariable.Type);
            int alignment = Math.Max(val1: Math.Min(val1: memberVariableSize, val2: 16), val2: 1);
            maxAlignment = Math.Max(val1: maxAlignment, val2: alignment);
            size = AlignTo(size: size, alignment: alignment);
            size += memberVariableSize;
        }

        // Align struct to its natural alignment (max member alignment), matching LLVM layout
        return AlignTo(size: size, alignment: maxAlignment);
    }

    /// <summary>
    /// Calculates the size of an entity type.
    /// </summary>
    private int CalculateEntitySize(EntityTypeInfo entity)
    {
        int size = 0;
        int maxAlignment = 1;
        foreach (MemberVariableInfo memberVariable in entity.MemberVariables)
        {
            int memberVariableSize = GetTypeSize(type: memberVariable.Type);
            int alignment = Math.Max(val1: Math.Min(val1: memberVariableSize, val2: 16), val2: 1);
            maxAlignment = Math.Max(val1: maxAlignment, val2: alignment);
            size = AlignTo(size: size, alignment: alignment);
            size += memberVariableSize;
        }

        return AlignTo(size: size, alignment: maxAlignment);
    }

    /// <summary>
    /// Calculates the size of a variant type (i64 tag + max payload).
    /// </summary>
    private int CalculateVariantSize(VariantTypeInfo variant)
    {
        int maxPayloadSize = 0;
        int maxPayloadAlignment = 1;
        foreach (VariantMemberInfo member in variant.Members)
        {
            if (!member.IsNone && member.Type != null)
            {
                int payloadSize = GetTypeSize(type: member.Type);
                int payloadAlignment =
                    Math.Max(val1: Math.Min(val1: payloadSize, val2: 16), val2: 1);
                maxPayloadSize = Math.Max(val1: maxPayloadSize, val2: payloadSize);
                maxPayloadAlignment = Math.Max(val1: maxPayloadAlignment, val2: payloadAlignment);
            }
        }

        // Tag (i64 = 8 bytes) + padding + payload, aligned to max of tag and payload alignment
        int tagSize = 8;
        int structAlignment = Math.Max(val1: tagSize, val2: maxPayloadAlignment);
        int size = AlignTo(size: tagSize, alignment: maxPayloadAlignment) + maxPayloadSize;
        return AlignTo(size: size, alignment: structAlignment);
    }

    /// <summary>
    /// Aligns a size to a given alignment.
    /// </summary>
    private static int AlignTo(int size, int alignment)
    {
        return (size + alignment - 1) / alignment * alignment;
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> is an unsigned integer type.
    /// Uses protocol conformance: unsigned types obey <c>UnsignedIntegral</c>.
    /// </summary>
    private static bool IsUnsignedIntegerType(TypeInfo? type)
    {
        return type is RecordTypeInfo record &&
               record.ImplementedProtocols.Any(p => p.Name == "UnsignedIntegral");
    }

    #endregion
}
