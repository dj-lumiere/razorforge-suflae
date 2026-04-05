namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Enums;
using SemanticAnalysis.Symbols;
using SemanticAnalysis.Types;

public partial class LLVMCodeGenerator
{
    private void EmitSynthesizedTypeName(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);

        EmitLine(sb: _functionDefinitions, line: $"define ptr @{funcName}({meType} %me) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");
        string nameStr = EmitSynthesizedStringLiteral(value: routine.OwnerType.Name);
        EmitLine(sb: _functionDefinitions, line: $"  ret ptr {nameStr}");
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    /// <summary>
    /// Emits the body for a synthesized type_kind() routine.
    /// Returns the TypeKind choice ordinal as an i32 constant.
    /// </summary>
    private void EmitSynthesizedTypeKind(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);
        // Map TypeCategory enum → TypeKind choice ordinals (from BuilderService.rf)
        int kindValue = routine.OwnerType.Category switch
        {
            TypeCategory.Record => 0, // TypeKind.RECORD
            TypeCategory.Entity => 1, // TypeKind.ENTITY
            TypeCategory.Choice => 2, // TypeKind.CHOICE
            TypeCategory.Variant => 3, // TypeKind.VARIANT
            TypeCategory.Flags => 4, // TypeKind.FLAGS
            TypeCategory.Routine => 5, // TypeKind.ROUTINE
            TypeCategory.Protocol => 7, // TypeKind.PROTOCOL
            _ => 0 // Default RECORD for internal types (Tuple, Wrapper, etc.)
        };

        EmitLine(sb: _functionDefinitions, line: $"define i32 @{funcName}({meType} %me) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");
        EmitLine(sb: _functionDefinitions, line: $"  ret i32 {kindValue}");
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    /// <summary>
    /// Emits the body for a synthesized type_id() routine.
    /// Returns a unique build-time FNV-1a hash of the type's full name.
    /// </summary>
    private void EmitSynthesizedTypeId(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);
        ulong hash = ComputeTypeId(fullName: routine.OwnerType.FullName);

        EmitLine(sb: _functionDefinitions, line: $"define i64 @{funcName}({meType} %me) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");
        EmitLine(sb: _functionDefinitions, line: $"  ret i64 {unchecked((long)hash)}");
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    /// <summary>
    /// Emits the body for a synthesized module_name() routine.
    /// Returns the module where the type is defined as a Text constant.
    /// </summary>
    private void EmitSynthesizedModuleName(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);
        string moduleName = routine.OwnerType.Module ?? "";

        EmitLine(sb: _functionDefinitions, line: $"define ptr @{funcName}({meType} %me) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");
        string moduleStr = EmitSynthesizedStringLiteral(value: moduleName);
        EmitLine(sb: _functionDefinitions, line: $"  ret ptr {moduleStr}");
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    /// <summary>
    /// Emits the body for a synthesized full_type_name() routine.
    /// Returns "Module/Path.TypeName" as a Text constant.
    /// </summary>
    private void EmitSynthesizedFullTypeName(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);
        string moduleName = routine.OwnerType.Module ?? "";
        string typeName = routine.OwnerType.Name;
        string fullName = string.IsNullOrEmpty(value: moduleName)
            ? typeName
            : $"{moduleName}.{typeName}";

        EmitLine(sb: _functionDefinitions, line: $"define ptr @{funcName}({meType} %me) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");
        string fullNameStr = EmitSynthesizedStringLiteral(value: fullName);
        EmitLine(sb: _functionDefinitions, line: $"  ret ptr {fullNameStr}");
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    /// <summary>
    /// Emits the body for a synthesized field_count() routine.
    /// Returns the number of member variables as a U64 constant.
    /// </summary>
    private void EmitSynthesizedFieldCount(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);

        int count = routine.OwnerType switch
        {
            RecordTypeInfo rec => rec.MemberVariables.Count,
            EntityTypeInfo ent => ent.MemberVariables.Count,
            _ => 0
        };

        EmitLine(sb: _functionDefinitions, line: $"define i64 @{funcName}({meType} %me) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");
        EmitLine(sb: _functionDefinitions, line: $"  ret i64 {count}");
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    /// <summary>
    /// Emits the body for a synthesized is_generic() routine.
    /// Returns true if the type has generic parameters.
    /// </summary>
    private void EmitSynthesizedIsGeneric(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);
        string value = routine.OwnerType.IsGenericDefinition
            ? "true"
            : "false";

        EmitLine(sb: _functionDefinitions, line: $"define i1 @{funcName}({meType} %me) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");
        EmitLine(sb: _functionDefinitions, line: $"  ret i1 {value}");
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    /// <summary>
    /// Computes a unique type ID using FNV-1a hash of the full type name.
    /// </summary>
    private static ulong ComputeTypeId(string fullName)
    {
        // Blank is the unit type; type_id 0 is reserved for it (represents "absent" in carriers)
        if (fullName is "Blank" || fullName.EndsWith(value: ".Blank"))
            return 0UL;

        ulong hash = 14695981039346656037UL; // FNV-1a offset basis
        foreach (byte b in Encoding.UTF8.GetBytes(s: fullName))
        {
            hash ^= b;
            hash *= 1099511628211UL; // FNV-1a prime
        }

        return hash;
    }

    /// <summary>
    /// Emits a List[Text] constant containing the given string values.
    /// Layout: { ptr data, i64 count, i64 capacity } matching List[T] entity field order.
    /// </summary>
    private void EmitSynthesizedStringList(string funcName, string meType,
        IReadOnlyList<string> values)
    {
        int count = values.Count;
        StringBuilder sb = _functionDefinitions;

        EmitLine(sb: sb, line: $"define ptr @{funcName}({meType} %me) {{");
        EmitLine(sb: sb, line: "entry:");

        // Allocate list header: { ptr data, i64 count, i64 capacity }
        string listPtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {listPtr} = call ptr @rf_allocate_dynamic(i64 {_collectionHeaderSizeBytes})");

        // Allocate data array (array of pointers)
        string dataPtr = NextTemp();
        EmitLine(sb: sb,
            line: $"  {dataPtr} = call ptr @rf_allocate_dynamic(i64 {count * _pointerSizeBytes})");

        // Store data pointer (field 0)
        string dataPtrSlot = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {dataPtrSlot} = getelementptr {{ ptr, i64, i64 }}, ptr {listPtr}, i32 0, i32 0");
        EmitLine(sb: sb, line: $"  store ptr {dataPtr}, ptr {dataPtrSlot}");

        // Store count (field 1)
        string countPtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {countPtr} = getelementptr {{ ptr, i64, i64 }}, ptr {listPtr}, i32 0, i32 1");
        EmitLine(sb: sb, line: $"  store i64 {count}, ptr {countPtr}");

        // Store capacity (field 2)
        string capPtr = NextTemp();
        EmitLine(sb: sb,
            line: $"  {capPtr} = getelementptr {{ ptr, i64, i64 }}, ptr {listPtr}, i32 0, i32 2");
        EmitLine(sb: sb, line: $"  store i64 {count}, ptr {capPtr}");

        // Store each string pointer into the data array
        for (int i = 0; i < count; i++)
        {
            string strConst = EmitSynthesizedStringLiteral(value: values[index: i]);
            string elemPtr = NextTemp();
            EmitLine(sb: sb, line: $"  {elemPtr} = getelementptr ptr, ptr {dataPtr}, i64 {i}");
            EmitLine(sb: sb, line: $"  store ptr {strConst}, ptr {elemPtr}");
        }

        EmitLine(sb: sb, line: $"  ret ptr {listPtr}");
        EmitLine(sb: sb, line: "}");
        EmitLine(sb: sb, line: "");
    }

    /// <summary>
    /// Emits the body for a synthesized protocols() routine.
    /// Returns a List[Text] of protocol names this type obeys.
    /// </summary>
    private void EmitSynthesizedProtocols(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);

        IReadOnlyList<TypeInfo>? protocols = routine.OwnerType switch
        {
            RecordTypeInfo rec => rec.ImplementedProtocols,
            EntityTypeInfo ent => ent.ImplementedProtocols,
            _ => null
        };

        List<string> names = protocols?.Select(selector: p => p.Name)
                                       .ToList() ?? [];
        EmitSynthesizedStringList(funcName: funcName, meType: meType, values: names);
    }

    /// <summary>
    /// Emits the body for a synthesized routine_names() routine.
    /// Returns a List[Text] of all member routine names for this type.
    /// </summary>
    private void EmitSynthesizedRoutineNames(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);

        var routineNames = _registry.GetMethodsForType(type: routine.OwnerType)
                                    .Select(selector: r => r.Name)
                                    .Distinct()
                                    .ToList();

        EmitSynthesizedStringList(funcName: funcName, meType: meType, values: routineNames);
    }

    /// <summary>
    /// Emits the body for a synthesized annotations() routine.
    /// Returns a List[Text] of build-time annotations on this type.
    /// Currently returns an empty list (type-level annotations not yet tracked on TypeInfo).
    /// </summary>
    private void EmitSynthesizedAnnotations(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);

        // Type-level annotations are not yet tracked on TypeInfo — return empty list
        EmitSynthesizedStringList(funcName: funcName, meType: meType, values: []);
    }

    /// <summary>
    /// Emits the body for a synthesized data_size() routine.
    /// Returns the byte size of the type's data layout.
    /// </summary>
    private void EmitSynthesizedDataSize(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);
        long size = ComputeDataSize(type: routine.OwnerType);

        EmitLine(sb: _functionDefinitions, line: $"define i64 @{funcName}({meType} %me) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");
        EmitLine(sb: _functionDefinitions, line: $"  ret i64 {size}");
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    /// <summary>
    /// Emits the body for a synthesized member_variable_info() routine.
    /// Returns a List[FieldInfo] where FieldInfo = { ptr name, ptr type_name, i64 visibility, i64 offset }.
    /// </summary>
    private void EmitSynthesizedMemberVariableInfo(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);

        IReadOnlyList<MemberVariableInfo>? fields = routine.OwnerType switch
        {
            RecordTypeInfo rec => rec.MemberVariables,
            EntityTypeInfo ent => ent.MemberVariables,
            _ => null
        };

        fields ??= [];
        int count = fields.Count;

        // FieldInfo layout: { ptr name, ptr type_name, i64 visibility, i64 offset }
        // Each element is 32 bytes (8 + 8 + 8 + 8)
        int elemSize = 32;

        StringBuilder sb = _functionDefinitions;
        EmitLine(sb: sb, line: $"define ptr @{funcName}({meType} %me) {{");
        EmitLine(sb: sb, line: "entry:");

        // Allocate list header: { ptr data, i64 count, i64 capacity }
        string listPtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {listPtr} = call ptr @rf_allocate_dynamic(i64 {_collectionHeaderSizeBytes})");

        // Allocate data array
        string dataPtr = NextTemp();
        EmitLine(sb: sb,
            line: $"  {dataPtr} = call ptr @rf_allocate_dynamic(i64 {count * elemSize})");

        // Store data pointer (field 0)
        string dataPtrSlot = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {dataPtrSlot} = getelementptr {{ ptr, i64, i64 }}, ptr {listPtr}, i32 0, i32 0");
        EmitLine(sb: sb, line: $"  store ptr {dataPtr}, ptr {dataPtrSlot}");

        // Store count (field 1)
        string countPtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {countPtr} = getelementptr {{ ptr, i64, i64 }}, ptr {listPtr}, i32 0, i32 1");
        EmitLine(sb: sb, line: $"  store i64 {count}, ptr {countPtr}");

        // Store capacity (field 2)
        string capPtr = NextTemp();
        EmitLine(sb: sb,
            line: $"  {capPtr} = getelementptr {{ ptr, i64, i64 }}, ptr {listPtr}, i32 0, i32 2");
        EmitLine(sb: sb, line: $"  store i64 {count}, ptr {capPtr}");

        // Compute field byte offsets
        List<long> fieldOffsets = ComputeFieldOffsets(type: routine.OwnerType);

        // Store each FieldInfo { ptr name, ptr type_name, i64 visibility, i64 offset }
        for (int i = 0; i < count; i++)
        {
            MemberVariableInfo field = fields[index: i];
            string nameStr = EmitSynthesizedStringLiteral(value: field.Name);
            string typeNameStr = EmitSynthesizedStringLiteral(value: field.Type.Name);
            long visibility = (long)field.Visibility;
            long offset = i < fieldOffsets.Count
                ? fieldOffsets[index: i]
                : 0;

            // GEP to element i in the data array (each element is { ptr, ptr, i64, i64 })
            string elemPtr = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {elemPtr} = getelementptr {{ ptr, ptr, i64, i64 }}, ptr {dataPtr}, i64 {i}");

            // Store name (field 0)
            string nameSlot = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {nameSlot} = getelementptr {{ ptr, ptr, i64, i64 }}, ptr {elemPtr}, i32 0, i32 0");
            EmitLine(sb: sb, line: $"  store ptr {nameStr}, ptr {nameSlot}");

            // Store type_name (field 1)
            string typeSlot = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {typeSlot} = getelementptr {{ ptr, ptr, i64, i64 }}, ptr {elemPtr}, i32 0, i32 1");
            EmitLine(sb: sb, line: $"  store ptr {typeNameStr}, ptr {typeSlot}");

            // Store visibility (field 2)
            string visSlot = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {visSlot} = getelementptr {{ ptr, ptr, i64, i64 }}, ptr {elemPtr}, i32 0, i32 2");
            EmitLine(sb: sb, line: $"  store i64 {visibility}, ptr {visSlot}");

            // Store offset (field 3)
            string offSlot = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {offSlot} = getelementptr {{ ptr, ptr, i64, i64 }}, ptr {elemPtr}, i32 0, i32 3");
            EmitLine(sb: sb, line: $"  store i64 {offset}, ptr {offSlot}");
        }

        EmitLine(sb: sb, line: $"  ret ptr {listPtr}");
        EmitLine(sb: sb, line: "}");
        EmitLine(sb: sb, line: "");
    }

    /// <summary>
    /// Emits the body for a synthesized all_fields() routine.
    /// Returns a Dict[Text, Data] with all field names mapped to their boxed values.
    /// </summary>
    private void EmitSynthesizedAllFields(RoutineInfo routine, string funcName)
    {
        EmitSynthesizedFieldsDict(routine: routine, funcName: funcName, openOnly: false);
    }

    /// <summary>
    /// Emits the body for a synthesized open_fields() routine.
    /// Returns a Dict[Text, Data] with only open (public) field names mapped to their boxed values.
    /// </summary>
    private void EmitSynthesizedOpenFields(RoutineInfo routine, string funcName)
    {
        EmitSynthesizedFieldsDict(routine: routine, funcName: funcName, openOnly: true);
    }

    /// <summary>
    /// Shared implementation for all_fields() and open_fields().
    /// Allocates a Dict[Text, Data], extracts field values from %me, boxes each to Data,
    /// and inserts into the dict.
    /// </summary>
    private void EmitSynthesizedFieldsDict(RoutineInfo routine, string funcName, bool openOnly)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        StringBuilder sb = _functionDefinitions;
        string meType = GetParameterLLVMType(type: routine.OwnerType);

        // Get fields for this type
        IReadOnlyList<MemberVariableInfo> allFields = routine.OwnerType switch
        {
            RecordTypeInfo rec => rec.MemberVariables,
            EntityTypeInfo ent => ent.MemberVariables,
            _ => []
        };

        List<MemberVariableInfo> fields = openOnly
            ? allFields.Where(predicate: f => f.Visibility == VisibilityModifier.Open)
                       .ToList()
            : allFields.ToList();

        // Look up Dict[Text, Data] creator and set method
        TypeInfo? dictDef = _registry.LookupType(name: "Dict");
        TypeInfo? textType = _registry.LookupType(name: "Text");
        TypeInfo? dataType = _registry.LookupType(name: "Data");

        if (dictDef == null || textType == null || dataType == null)
        {
            // Dict or Data not available — emit a function that returns null
            EmitLine(sb: sb, line: $"define ptr @{funcName}({meType} %me) {{");
            EmitLine(sb: sb, line: "entry:");
            EmitLine(sb: sb, line: "  ret ptr null");
            EmitLine(sb: sb, line: "}");
            EmitLine(sb: sb, line: "");
            return;
        }

        TypeInfo dictTextData = _registry.GetOrCreateResolution(
            genericDef: dictDef,
            typeArguments: [textType, dataType]);

        // Build names for Dict[Text, Data].$create() and set(key, value)
        string dictCreateName = Q(name: $"{dictTextData.Name}.$create");
        string dictSetName = Q(name: $"{dictTextData.Name}.set");

        // Data entity struct type for inline boxing
        string dataStructType = dataType is EntityTypeInfo dataEntity
            ? GetEntityTypeName(entity: dataEntity)
            : "%Entity.Data";

        EmitLine(sb: sb, line: $"define ptr @{funcName}({meType} %me) {{");
        EmitLine(sb: sb, line: "entry:");

        // Allocate empty dict: %dict = call ptr @Dict_Text_Data_.$create()
        string dictPtr = NextTemp();
        EmitLine(sb: sb, line: $"  {dictPtr} = call ptr @{dictCreateName}()");

        // Get the owner struct type name for GEP (not GetLLVMType, which returns "ptr" for entities)
        string structType = routine.OwnerType switch
        {
            EntityTypeInfo ent => GetEntityTypeName(entity: ent),
            RecordTypeInfo rec => GetRecordTypeName(record: rec),
            _ => GetLLVMType(type: routine.OwnerType)
        };

        // For records (pass-by-value), we need a pointer to GEP into.
        // Alloca the value and store %me into it.
        bool isRecord = routine.OwnerType is RecordTypeInfo;
        bool hasBackendType = routine.OwnerType is RecordTypeInfo { HasDirectBackendType: true };
        string mePtr = "%me";
        if (isRecord)
        {
            string allocaType = hasBackendType
                ? meType
                : structType;
            string allocaPtr = NextTemp();
            EmitLine(sb: sb, line: $"  {allocaPtr} = alloca {allocaType}");
            EmitLine(sb: sb, line: $"  store {meType} %me, ptr {allocaPtr}");
            mePtr = allocaPtr;
        }

        // For each field: extract value, inline-box to Data, insert into dict
        for (int i = 0; i < fields.Count; i++)
        {
            MemberVariableInfo field = fields[index: i];
            string fieldLlvmType = GetLLVMType(type: field.Type);
            string nameStr = EmitSynthesizedStringLiteral(value: field.Name);

            // Extract field value from %me (use pointer for GEP)
            string fieldPtr;
            if (hasBackendType)
            {
                // Backend-typed records (Bool, S32, etc.) — the value IS the field, just use the alloca pointer
                fieldPtr = mePtr;
            }
            else
            {
                fieldPtr = NextTemp();
                EmitLine(sb: sb,
                    line:
                    $"  {fieldPtr} = getelementptr {structType}, ptr {mePtr}, i32 0, i32 {field.Index}");
            }

            string fieldVal = NextTemp();
            EmitLine(sb: sb, line: $"  {fieldVal} = load {fieldLlvmType}, ptr {fieldPtr}");

            // Inline Data boxing (avoids overload collision on Data_$create)
            ulong fieldTypeId = ComputeTypeId(fullName: field.Type.FullName);
            long fieldDataSize = ComputeDataSize(type: field.Type);

            // Allocate Data entity: { i64 type_id, ptr data_ptr, i64 data_size }
            string boxed = NextTemp();
            EmitLine(sb: sb,
                line: $"  {boxed} = call ptr @rf_allocate_dynamic(i64 {_dataEntitySizeBytes})");

            // Store type_id
            string tidSlot = NextTemp();
            EmitLine(sb: sb,
                line: $"  {tidSlot} = getelementptr {dataStructType}, ptr {boxed}, i32 0, i32 0");
            EmitLine(sb: sb, line: $"  store i64 {fieldTypeId}, ptr {tidSlot}");

            // Store size
            string sizeSlot = NextTemp();
            EmitLine(sb: sb,
                line: $"  {sizeSlot} = getelementptr {dataStructType}, ptr {boxed}, i32 0, i32 1");
            EmitLine(sb: sb, line: $"  store i64 {fieldDataSize}, ptr {sizeSlot}");

            // Allocate and store boxed value
            string valBox = NextTemp();
            EmitLine(sb: sb,
                line: $"  {valBox} = call ptr @rf_allocate_dynamic(i64 {fieldDataSize})");
            EmitLine(sb: sb, line: $"  store {fieldLlvmType} {fieldVal}, ptr {valBox}");

            // Store data_ptr
            string dptrSlot = NextTemp();
            EmitLine(sb: sb,
                line: $"  {dptrSlot} = getelementptr {dataStructType}, ptr {boxed}, i32 0, i32 2");
            EmitLine(sb: sb, line: $"  store ptr {valBox}, ptr {dptrSlot}");

            // Insert into dict: call dict.set(name, boxed)
            EmitLine(sb: sb,
                line: $"  call void @{dictSetName}(ptr {dictPtr}, ptr {nameStr}, ptr {boxed})");
        }

        EmitLine(sb: sb, line: $"  ret ptr {dictPtr}");
        EmitLine(sb: sb, line: "}");
        EmitLine(sb: sb, line: "");
    }

    /// <summary>
    /// Computes the byte size of a type's data layout using field sizes with alignment padding.
    /// </summary>
    private long ComputeDataSize(TypeInfo type)
    {
        // Entities are always pointer-referenced — their storage size is pointer size,
        // not the entity struct size. This matters for collections (e.g., List[Entity])
        // where elements are stored as pointers in the data buffer.
        if (type is EntityTypeInfo)
        {
            return _pointerBitWidth / 8;
        }

        IReadOnlyList<MemberVariableInfo>? fields = type switch
        {
            RecordTypeInfo { HasDirectBackendType: true } rec => null, // Use backend type size
            RecordTypeInfo { IsSingleMemberVariableWrapper: true } => null, // Use underlying size
            RecordTypeInfo rec => rec.MemberVariables,
            _ => null
        };

        // Simple types with known sizes
        if (fields == null)
        {
            return type switch
            {
                RecordTypeInfo { HasDirectBackendType: true } rec => LlvmTypeSizeBytes(
                    llvmType: rec.LlvmType),
                RecordTypeInfo { IsSingleMemberVariableWrapper: true } rec => LlvmTypeSizeBytes(
                    llvmType: rec.LlvmType),
                ChoiceTypeInfo => 4, // i32
                FlagsTypeInfo => 8, // i64
                _ => _pointerBitWidth / 8 // Default to pointer size
            };
        }

        if (fields.Count == 0)
        {
            return 1; // Empty struct = 1 byte (for addressability)
        }

        // Compute struct layout with alignment padding
        long offset = 0;
        long maxAlign = 1;

        foreach (MemberVariableInfo field in fields)
        {
            long fieldSize = GetFieldByteSize(type: field.Type);
            long fieldAlign = GetFieldAlignment(type: field.Type);
            maxAlign = Math.Max(val1: maxAlign, val2: fieldAlign);

            // Align offset
            offset = (offset + fieldAlign - 1) / fieldAlign * fieldAlign;
            offset += fieldSize;
        }

        // Pad to struct alignment
        offset = (offset + maxAlign - 1) / maxAlign * maxAlign;
        return offset;
    }

    /// <summary>
    /// Computes the alignment requirement of a type.
    /// </summary>
    private long ComputeAlignSize(TypeInfo type)
    {
        IReadOnlyList<MemberVariableInfo>? fields = type switch
        {
            RecordTypeInfo { HasDirectBackendType: true } => null,
            RecordTypeInfo { IsSingleMemberVariableWrapper: true } => null,
            RecordTypeInfo rec => rec.MemberVariables,
            EntityTypeInfo ent => ent.MemberVariables,
            _ => null
        };

        if (fields == null)
        {
            return type switch
            {
                RecordTypeInfo { HasDirectBackendType: true } rec => LlvmTypeAlignment(
                    llvmType: rec.LlvmType),
                RecordTypeInfo { IsSingleMemberVariableWrapper: true } rec => LlvmTypeAlignment(
                    llvmType: rec.LlvmType),
                ChoiceTypeInfo => 4,
                FlagsTypeInfo => 8,
                _ => _pointerBitWidth / 8
            };
        }

        if (fields.Count == 0)
        {
            return 1;
        }

        long maxAlign = 1;
        foreach (MemberVariableInfo field in fields)
        {
            maxAlign = Math.Max(val1: maxAlign, val2: GetFieldAlignment(type: field.Type));
        }

        return maxAlign;
    }

    /// <summary>
    /// Gets the byte size of a field's type for layout computation.
    /// </summary>
    private long GetFieldByteSize(TypeInfo type)
    {
        string llvmType = GetLLVMType(type: type);
        return LlvmTypeSizeBytes(llvmType: llvmType);
    }

    /// <summary>
    /// Gets the alignment of a field's type for layout computation.
    /// </summary>
    private long GetFieldAlignment(TypeInfo type)
    {
        string llvmType = GetLLVMType(type: type);
        return LlvmTypeAlignment(llvmType: llvmType);
    }

    /// <summary>
    /// Maps an LLVM type string to its byte size.
    /// </summary>
    private long LlvmTypeSizeBytes(string llvmType)
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
            "ptr" => _pointerBitWidth / 8,
            _ => _pointerBitWidth / 8 // Default to pointer size for struct/unknown types
        };
    }

    /// <summary>
    /// Maps an LLVM type string to its natural alignment.
    /// </summary>
    private long LlvmTypeAlignment(string llvmType)
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
            "ptr" => _pointerBitWidth / 8,
            _ => _pointerBitWidth / 8
        };
    }

    /// <summary>
    /// Computes byte offsets for each field in a type (for FieldInfo.offset).
    /// </summary>
    private List<long> ComputeFieldOffsets(TypeInfo type)
    {
        IReadOnlyList<MemberVariableInfo>? fields = type switch
        {
            RecordTypeInfo rec => rec.MemberVariables,
            EntityTypeInfo ent => ent.MemberVariables,
            _ => null
        };

        if (fields == null || fields.Count == 0)
        {
            return [];
        }

        var offsets = new List<long>();
        long currentOffset = 0;

        foreach (MemberVariableInfo field in fields)
        {
            long fieldSize = ComputeDataSize(type: field.Type);
            long fieldAlign = ComputeAlignSize(type: field.Type);
            if (fieldAlign > 0)
            {
                long remainder = currentOffset % fieldAlign;
                if (remainder != 0)
                {
                    currentOffset += fieldAlign - remainder;
                }
            }

            offsets.Add(item: currentOffset);
            currentOffset += fieldSize;
        }

        return offsets;
    }

    /// <summary>
    /// Emits the body for a synthesized generic_args() routine.
    /// Returns a List[Text] of generic type argument names.
    /// </summary>
    private void EmitSynthesizedGenericArgs(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);

        List<string> args = routine.OwnerType
                                   .TypeArguments
                                  ?.Select(selector: t => t.Name)
                                   .ToList() ?? routine.OwnerType.GenericParameters?.ToList() ??
            [];
        EmitSynthesizedStringList(funcName: funcName, meType: meType, values: args);
    }

    /// <summary>
    /// Emits the body for a synthesized protocol_info() routine.
    /// Returns a List[ProtocolInfo] where ProtocolInfo = { ptr name, ptr routine_names_list, i1 is_generated }.
    /// Currently emits a simplified version returning an empty list (full entity allocation deferred).
    /// </summary>
    private void EmitSynthesizedProtocolInfo(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);

        // For now, emit an empty list — full ProtocolInfo entity allocation is complex
        // and will be implemented when the BuilderService stdlib types are fully parsed
        EmitSynthesizedStringList(funcName: funcName, meType: meType, values: []);
    }

    /// <summary>
    /// Emits the body for a synthesized routine_info() routine.
    /// Returns a List[RoutineInfo] — currently emits empty list (entity allocation deferred).
    /// </summary>
    private void EmitSynthesizedRoutineInfoList(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);

        // For now, emit an empty list — full RoutineInfo entity allocation is complex
        // and will be implemented when the BuilderService stdlib types are fully parsed
        EmitSynthesizedStringList(funcName: funcName, meType: meType, values: []);
    }

    /// <summary>
    /// Emits the body for a synthesized dependencies() routine.
    /// Returns a List[Text] of module dependencies — currently empty.
    /// </summary>
    private void EmitSynthesizedDependencies(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);
        EmitSynthesizedStringList(funcName: funcName, meType: meType, values: []);
    }

    /// <summary>
    /// Emits the body for a synthesized member_type_id(member_name: Text) routine.
    /// Returns the type ID (FNV-1a hash) for a named member variable, or 0 if not found.
    /// </summary>
    private void EmitSynthesizedMemberTypeId(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);

        IReadOnlyList<MemberVariableInfo>? fields = routine.OwnerType switch
        {
            RecordTypeInfo rec => rec.MemberVariables,
            EntityTypeInfo ent => ent.MemberVariables,
            _ => null
        };
        fields ??= [];

        StringBuilder sb = _functionDefinitions;
        EmitLine(sb: sb, line: $"define i64 @{funcName}({meType} %me, ptr %member_name) {{");
        EmitLine(sb: sb, line: "entry:");

        if (fields.Count == 0)
        {
            EmitLine(sb: sb, line: "  ret i64 0");
        }
        else
        {
            // Ensure Text.$eq is available for string comparison
            TypeInfo? textType = _registry.LookupType(name: "Text");
            RoutineInfo? textEq = textType != null
                ? _registry.LookupMethod(type: textType, methodName: "$eq")
                : null;
            string eqFuncName = textEq != null
                ? MangleFunctionName(routine: textEq)
                : "Text$_eq";
            // Add to _generatedFunctions so the synthesized pass emits its body
            _generatedFunctions.Add(item: eqFuncName);

            // For each field, compare the member_name string and return the type ID
            for (int i = 0; i < fields.Count; i++)
            {
                MemberVariableInfo field = fields[index: i];
                string nameStr = EmitSynthesizedStringLiteral(value: field.Name);
                ulong typeId = ComputeTypeId(fullName: field.Type.FullName);

                string cmpResult = NextTemp();
                EmitLine(sb: sb,
                    line:
                    $"  {cmpResult} = call i1 @{eqFuncName}(ptr %member_name, ptr {nameStr})");
                string nextLabel = i < fields.Count - 1
                    ? $"check_{i + 1}"
                    : "not_found";
                EmitLine(sb: sb,
                    line: $"  br i1 {cmpResult}, label %found_{i}, label %{nextLabel}");

                EmitLine(sb: sb, line: $"found_{i}:");
                EmitLine(sb: sb, line: $"  ret i64 {unchecked((long)typeId)}");

                if (i < fields.Count - 1)
                {
                    EmitLine(sb: sb, line: $"check_{i + 1}:");
                }
            }

            EmitLine(sb: sb, line: "not_found:");
            EmitLine(sb: sb, line: "  ret i64 0");
        }

        EmitLine(sb: sb, line: "}");
        EmitLine(sb: sb, line: "");
    }

    /// <summary>
    /// Emits a synthesized routine that returns a Text constant (no owner type required).
    /// Used for var_name() fallback.
    /// </summary>
    private void EmitSynthesizedBuilderServiceTextNoOwner(RoutineInfo routine, string funcName,
        string value)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLLVMType(type: routine.OwnerType);
        string strConst = EmitSynthesizedStringLiteral(value: value);

        EmitLine(sb: _functionDefinitions, line: $"define ptr @{funcName}({meType} %me) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");
        EmitLine(sb: _functionDefinitions, line: $"  ret ptr {strConst}");
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }
}
