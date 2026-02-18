namespace Compilers.CodeGen;

using Analysis.Types;

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
    private string GetLLVMType(TypeInfo type)
    {
        return type switch
        {
            // Intrinsic types map directly to LLVM
            IntrinsicTypeInfo intrinsic => GetIntrinsicLLVMType(intrinsic),

            // Single-field record wrappers → unwrap to underlying intrinsic
            RecordTypeInfo { IsSingleFieldWrapper: true } record =>
                GetLLVMType(record.UnderlyingIntrinsic!),

            // Multi-field records → LLVM struct type
            RecordTypeInfo record => GetRecordTypeName(record),

            // Entities → pointer to LLVM struct
            EntityTypeInfo => "ptr",

            // Residents → pointer to LLVM struct (same as entity at IR level)
            ResidentTypeInfo => "ptr",

            // Choices → underlying integer type (S64)
            ChoiceTypeInfo => "i64",

            // Variants → struct { tag, payload }
            VariantTypeInfo variant => GetVariantTypeName(variant),

            // Error handling types → struct { tag, payload }
            ErrorHandlingTypeInfo errorHandling => GetErrorHandlingTypeName(errorHandling),

            // Protocols → not directly representable (used for constraints only)
            ProtocolTypeInfo => throw new InvalidOperationException(
                "Protocol types cannot be used directly in codegen"),

            // Generic parameters should be resolved by this point
            GenericParameterTypeInfo param => throw new InvalidOperationException(
                $"Unresolved generic parameter '{param.Name}' in codegen"),

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
    private static string GetIntrinsicLLVMType(IntrinsicTypeInfo intrinsic)
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
            "@intrinsic.iptr" => "i64", // TODO: Target-dependent (i32 on 32-bit)
            "@intrinsic.uptr" => "i64", // TODO: Target-dependent

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
        // Mangle the name to be LLVM-compatible
        string mangledName = MangleTypeName(record.Name);
        return $"%Record.{mangledName}";
    }

    /// <summary>
    /// Gets the LLVM struct type name for an entity.
    /// </summary>
    private static string GetEntityTypeName(EntityTypeInfo entity)
    {
        string mangledName = MangleTypeName(entity.Name);
        return $"%Entity.{mangledName}";
    }

    /// <summary>
    /// Gets the LLVM struct type name for a resident.
    /// </summary>
    private static string GetResidentTypeName(ResidentTypeInfo resident)
    {
        string mangledName = MangleTypeName(resident.Name);
        return $"%Resident.{mangledName}";
    }

    /// <summary>
    /// Gets the LLVM struct type name for a variant.
    /// </summary>
    private static string GetVariantTypeName(VariantTypeInfo variant)
    {
        string mangledName = MangleTypeName(variant.Name);
        return $"%Variant.{mangledName}";
    }

    /// <summary>
    /// Gets the LLVM struct type name for an error handling type.
    /// </summary>
    private static string GetErrorHandlingTypeName(ErrorHandlingTypeInfo errorType)
    {
        string mangledName = MangleTypeName(errorType.Name);
        return $"%ErrorHandling.{mangledName}";
    }

    /// <summary>
    /// Gets the LLVM struct type name for a choice.
    /// </summary>
    private static string GetChoiceTypeName(ChoiceTypeInfo choice)
    {
        string mangledName = MangleTypeName(choice.Name);
        return $"%Choice.{mangledName}";
    }

    /// <summary>
    /// Mangles a type name to be LLVM-compatible.
    /// Replaces angle brackets, commas, and spaces with underscores.
    /// </summary>
    private static string MangleTypeName(string name)
    {
        return name
            .Replace("<", "_")
            .Replace(">", "_")
            .Replace(", ", "_")
            .Replace(",", "_")
            .Replace(" ", "_");
    }

    /// <summary>
    /// Gets the LLVM type for a function parameter or return type.
    /// For entities, this returns ptr (all entities are pointers).
    /// For records, this returns the struct type (passed by value).
    /// </summary>
    private string GetParameterLLVMType(TypeInfo type)
    {
        return type switch
        {
            // Entities are always passed as pointers
            EntityTypeInfo => "ptr",
            ResidentTypeInfo => "ptr",

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
            RecordTypeInfo { IsSingleFieldWrapper: true } record =>
                GetTypeSize(record.UnderlyingIntrinsic!),
            RecordTypeInfo record => CalculateRecordSize(record),
            EntityTypeInfo entity => CalculateEntitySize(entity),
            ResidentTypeInfo resident => CalculateResidentSize(resident),
            ChoiceTypeInfo => 8, // i64 tag
            VariantTypeInfo variant => CalculateVariantSize(variant),
            _ => 8 // Default to pointer size
        };
    }

    /// <summary>
    /// Gets the size in bytes for an intrinsic type.
    /// </summary>
    private static int GetIntrinsicSize(IntrinsicTypeInfo intrinsic)
    {
        return intrinsic.Name switch
        {
            "@intrinsic.i1" => 1,
            "@intrinsic.i8" => 1,
            "@intrinsic.i16" => 2,
            "@intrinsic.i32" => 4,
            "@intrinsic.i64" => 8,
            "@intrinsic.i128" => 16,
            "@intrinsic.iptr" => 8,
            "@intrinsic.uptr" => 8,
            "@intrinsic.f16" => 2,
            "@intrinsic.f32" => 4,
            "@intrinsic.f64" => 8,
            "@intrinsic.f128" => 16,
            "@intrinsic.ptr" => 8,
            _ => 8
        };
    }

    /// <summary>
    /// Calculates the size of a record type (sum of field sizes with alignment).
    /// </summary>
    private int CalculateRecordSize(RecordTypeInfo record)
    {
        int size = 0;
        foreach (var field in record.Fields)
        {
            int fieldSize = GetTypeSize(field.Type);
            // Align to field size (simplified - real alignment is more complex)
            size = AlignTo(size, Math.Min(fieldSize, 8));
            size += fieldSize;
        }
        return AlignTo(size, 8); // Align struct to 8 bytes
    }

    /// <summary>
    /// Calculates the size of an entity type.
    /// </summary>
    private int CalculateEntitySize(EntityTypeInfo entity)
    {
        int size = 0;
        foreach (var field in entity.Fields)
        {
            int fieldSize = GetTypeSize(field.Type);
            size = AlignTo(size, Math.Min(fieldSize, 8));
            size += fieldSize;
        }
        return AlignTo(size, 8);
    }

    /// <summary>
    /// Calculates the size of a resident type.
    /// </summary>
    private int CalculateResidentSize(ResidentTypeInfo resident)
    {
        int size = 0;
        foreach (var field in resident.Fields)
        {
            int fieldSize = GetTypeSize(field.Type);
            size = AlignTo(size, Math.Min(fieldSize, 8));
            size += fieldSize;
        }
        return AlignTo(size, 8);
    }

    /// <summary>
    /// Calculates the size of a variant type (tag + max payload).
    /// </summary>
    private int CalculateVariantSize(VariantTypeInfo variant)
    {
        int maxPayloadSize = 0;
        foreach (var variantCase in variant.Cases)
        {
            if (variantCase.PayloadType != null)
            {
                int payloadSize = GetTypeSize(variantCase.PayloadType);
                maxPayloadSize = Math.Max(maxPayloadSize, payloadSize);
            }
        }
        // Tag (i32 = 4 bytes) + padding + payload
        return AlignTo(4, 8) + AlignTo(maxPayloadSize, 8);
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
