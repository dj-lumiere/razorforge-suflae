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
        if (_typeSubstitutions != null && _typeSubstitutions.TryGetValue(type.Name, out var sub))
            return sub;
        return type;
    }

    private string GetLLVMType(TypeInfo type)
    {
        type = ResolveTypeSubstitution(type);
        return type switch
        {
            // Intrinsic types map directly to LLVM
            IntrinsicTypeInfo intrinsic => GetIntrinsicLLVMType(intrinsic),

            // Records with @llvm annotation → use backend type directly
            RecordTypeInfo { HasDirectBackendType: true } record => record.LlvmType,

            // Legacy single-member-variable wrappers → unwrap to underlying intrinsic
            RecordTypeInfo { IsSingleMemberVariableWrapper: true } record =>
                GetLLVMType(record.UnderlyingIntrinsic!),

            // Generic definition records (unresolved) → pointer fallback
            RecordTypeInfo { IsGenericDefinition: true } => "ptr",

            // Records with no fields — look up the registered definition (may have @llvm annotation)
            RecordTypeInfo { MemberVariables.Count: 0 } record when
                _registry.LookupType(record.Name) is RecordTypeInfo { HasDirectBackendType: true } llvmRecord
                => llvmRecord.LlvmType,

            // Records with no fields and generic base type has @llvm annotation
            RecordTypeInfo { MemberVariables.Count: 0 } record when record.Name.Contains('[') &&
                _registry.LookupType(record.Name[..record.Name.IndexOf('[')]) is RecordTypeInfo { HasDirectBackendType: true } baseRecord
                => baseRecord.LlvmType,

            // Multi-member-variable records → LLVM struct type
            RecordTypeInfo record => GetRecordTypeName(record),

            // Entities → pointer to LLVM struct
            EntityTypeInfo => "ptr",

            // Wrappers (Viewed, Hijacked, Snatched, etc.) → all pointers at LLVM level
            WrapperTypeInfo => "ptr",

            // Choices → underlying integer type (S64)
            ChoiceTypeInfo => "i64",

            // Tuples → always inline struct
            TupleTypeInfo tuple => GetTupleTypeName(tuple),

            // Variants → struct { tag, payload }
            VariantTypeInfo variant => GetVariantTypeName(variant),

            // Error handling types → struct { tag, payload }
            ErrorHandlingTypeInfo errorHandling => GetErrorHandlingTypeName(errorHandling),

            // Protocols → type-erased pointer (protocol-typed fields/params hold a handle to a concrete object)
            ProtocolTypeInfo => "ptr",

            // Generic parameters — use ptr as fallback (should be resolved before reaching codegen)
            GenericParameterTypeInfo => "ptr",

            // Error placeholder
            ErrorTypeInfo => throw new InvalidOperationException(
                "Error type found in codegen - semantic analysis should have caught this"),

            // Unknown
            _ => throw new InvalidOperationException(
                $"Unknown type category: {type.Category}")
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

            _ => throw new InvalidOperationException($"Unknown intrinsic type: {intrinsic.Name}")
        };
    }

    /// <summary>
    /// Gets the LLVM struct type name for a record.
    /// </summary>
    private static string GetRecordTypeName(RecordTypeInfo record)
    {
        return $"%{Q($"Record.{record.Name}")}";
    }

    /// <summary>
    /// Gets the LLVM struct type name for an entity.
    /// </summary>
    private static string GetEntityTypeName(EntityTypeInfo entity)
    {
        return $"%{Q($"Entity.{entity.Name}")}";
    }

    /// <summary>
    /// Gets the LLVM struct type name for a variant.
    /// </summary>
    private static string GetVariantTypeName(VariantTypeInfo variant)
    {
        return $"%{Q($"Variant.{variant.Name}")}";
    }

    /// <summary>
    /// Gets the LLVM struct type name for an error handling type.
    /// </summary>
    private static string GetErrorHandlingTypeName(ErrorHandlingTypeInfo errorType)
    {
        return $"%{Q($"ErrorHandling.{errorType.Name}")}";
    }

    /// <summary>
    /// Gets the LLVM struct type name for a choice.
    /// </summary>
    private static string GetChoiceTypeName(ChoiceTypeInfo choice)
    {
        return $"%{Q($"Choice.{choice.Name}")}";
    }

    /// <summary>
    /// Gets the LLVM struct type name for a tuple.
    /// </summary>
    private string GetTupleTypeName(TupleTypeInfo tuple)
    {
        // Build a name like %Tuple.S32_S64 from element types
        var parts = new List<string>();
        foreach (var elemType in tuple.ElementTypes)
        {
            parts.Add(elemType.Name);
        }
        string tupleName = $"Tuple.{string.Join("_", parts)}";
        return $"%{Q(tupleName)}";
    }

    /// <summary>
    /// Calculates the size of a tuple type (sum of element sizes with alignment).
    /// </summary>
    private int CalculateTupleSize(TupleTypeInfo tuple)
    {
        int size = 0;
        foreach (var elemType in tuple.ElementTypes)
        {
            int elemSize = GetTypeSize(elemType);
            size = AlignTo(size, Math.Min(elemSize, 8));
            size += elemSize;
        }
        return AlignTo(size, 8);
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
    /// LLVM allows any characters in quoted identifiers: @"Snatched[Point].__eq__", %"Record.Snatched[Point]".
    /// Unquoted identifiers only allow [a-zA-Z$._0-9-].
    /// </summary>
    private static string Q(string name)
    {
        foreach (char c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '$' && c != '.' && c != '_' && c != '-')
                return $"\"{name}\"";
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
        type = ResolveTypeSubstitution(type);
        return type switch
        {
            // Entities are always passed as pointers
            EntityTypeInfo => "ptr",

            // Other types use normal mapping
            _ => GetLLVMType(type)
        };
    }

    /// <summary>
    /// Gets the size in bytes for a type (for allocation).
    /// </summary>
    private int GetTypeSize(TypeInfo type)
    {
        return type switch
        {
            IntrinsicTypeInfo intrinsic => GetIntrinsicSize(intrinsic),
            RecordTypeInfo { HasDirectBackendType: true } record =>
                GetTypeSizeFromLlvmType(record.BackendType!),
            RecordTypeInfo { IsSingleMemberVariableWrapper: true } record =>
                GetTypeSize(record.UnderlyingIntrinsic!),
            RecordTypeInfo record => CalculateRecordSize(record),
            EntityTypeInfo entity => CalculateEntitySize(entity),
            TupleTypeInfo tuple => CalculateTupleSize(tuple),
            WrapperTypeInfo => 8, // Pointer size
            ChoiceTypeInfo => 8, // i64 tag
            VariantTypeInfo variant => CalculateVariantSize(variant),
            _ => 8 // Default to pointer size
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
            "@intrinsic.ptr" => 8,
            _ => 8
        };
    }

    /// <summary>
    /// Gets the size in bytes for an LLVM type string (from @llvm annotation).
    /// </summary>
    private static int GetTypeSizeFromLlvmType(string llvmType)
    {
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
            "ptr" => 8,
            _ => throw new InvalidOperationException($"Unknown LLVM type for size calculation: {llvmType}")
        };
    }

    /// <summary>
    /// Calculates the size of a record type (sum of member variable sizes with alignment).
    /// </summary>
    private int CalculateRecordSize(RecordTypeInfo record)
    {
        int size = 0;
        int maxAlignment = 1;
        foreach (var memberVariable in record.MemberVariables)
        {
            int memberVariableSize = GetTypeSize(memberVariable.Type);
            int alignment = Math.Max(Math.Min(memberVariableSize, 8), 1);
            maxAlignment = Math.Max(maxAlignment, alignment);
            size = AlignTo(size, alignment);
            size += memberVariableSize;
        }
        // Align struct to its natural alignment (max member alignment), matching LLVM layout
        return AlignTo(size, maxAlignment);
    }

    /// <summary>
    /// Calculates the size of an entity type.
    /// </summary>
    private int CalculateEntitySize(EntityTypeInfo entity)
    {
        int size = 0;
        foreach (var memberVariable in entity.MemberVariables)
        {
            int memberVariableSize = GetTypeSize(memberVariable.Type);
            int alignment = Math.Max(Math.Min(memberVariableSize, 8), 1);
            size = AlignTo(size, alignment);
            size += memberVariableSize;
        }
        return AlignTo(size, 8);
    }

    /// <summary>
    /// Calculates the size of a variant type (i64 tag + max payload).
    /// </summary>
    private int CalculateVariantSize(VariantTypeInfo variant)
    {
        int maxPayloadSize = 0;
        foreach (var member in variant.Members)
        {
            if (!member.IsNone && member.Type != null)
            {
                int payloadSize = GetTypeSize(member.Type);
                maxPayloadSize = Math.Max(maxPayloadSize, payloadSize);
            }
        }
        // Tag (i64 = 8 bytes) + payload
        return 8 + AlignTo(maxPayloadSize, 8);
    }

    /// <summary>
    /// Aligns a size to a given alignment.
    /// </summary>
    private static int AlignTo(int size, int alignment)
    {
        return (size + alignment - 1) / alignment * alignment;
    }

    #endregion
}
