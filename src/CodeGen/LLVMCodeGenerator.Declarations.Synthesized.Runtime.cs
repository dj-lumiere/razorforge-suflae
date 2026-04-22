using Compiler.Postprocessing;

namespace Compiler.CodeGen;

using System.Text;
using Desugaring;
using TypeModel.Symbols;
using TypeModel.Types;

/// <summary>
/// Declaration code generation for synthesized runtime-support routines.
/// </summary>
public partial class LlvmCodeGenerator
{
    private void EmitSynthesizedHash(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        string meType = GetParameterLlvmType(type: routine.OwnerType);

        EmitLine(sb: _functionDefinitions, line: $"define i64 @{funcName}({meType} %me) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");

        switch (routine.OwnerType)
        {
            case ChoiceTypeInfo:
            {
                // Choice is i32 — extend to i64 for hash, then hash = me * 2654435761
                string ext = NextTemp();
                EmitLine(sb: _functionDefinitions, line: $"  {ext} = sext i32 %me to i64");
                string result = NextTemp();
                EmitLine(sb: _functionDefinitions,
                    line: $"  {result} = mul i64 {ext}, 2654435761");
                EmitLine(sb: _functionDefinitions, line: $"  ret i64 {result}");
                break;
            }
            case FlagsTypeInfo:
            {
                // hash = me * 2654435761 (Knuth multiplicative hash constant)
                string result = NextTemp();
                EmitLine(sb: _functionDefinitions, line: $"  {result} = mul i64 %me, 2654435761");
                EmitLine(sb: _functionDefinitions, line: $"  ret i64 {result}");
                break;
            }
            case RecordTypeInfo { HasDirectBackendType: true }:
            case RecordTypeInfo { IsSingleMemberVariableWrapper: true }:
            {
                // Single-value: call hash on the value directly
                string hashName = Q(name: $"{routine.OwnerType.Name}.$hash");
                // For primitive wrappers, hash the underlying value
                string result = NextTemp();
                EmitLine(sb: _functionDefinitions, line: $"  {result} = mul i64 %me, 2654435761");
                EmitLine(sb: _functionDefinitions, line: $"  ret i64 {result}");
                break;
            }
            case RecordTypeInfo record:
            {
                if (record.MemberVariables.Count == 0)
                {
                    EmitLine(sb: _functionDefinitions, line: "  ret i64 0");
                    break;
                }

                string typeName = GetRecordTypeName(record: record);
                string accum = "0";
                for (int i = 0; i < record.MemberVariables.Count; i++)
                {
                    MemberVariableInfo mv = record.MemberVariables[index: i];
                    string fieldType = GetLlvmType(type: mv.Type);
                    string field = NextTemp();
                    EmitLine(sb: _functionDefinitions,
                        line: $"  {field} = extractvalue {typeName} %me, {i}");

                    // Call field.hash()
                    string fieldHashName = Q(name: $"{mv.Type.Name}.$hash");
                    string fieldHash = NextTemp();
                    EmitLine(sb: _functionDefinitions,
                        line: $"  {fieldHash} = call i64 @{fieldHashName}({fieldType} {field})");

                    if (accum == "0")
                    {
                        accum = fieldHash;
                    }
                    else
                    {
                        string xorResult = NextTemp();
                        EmitLine(sb: _functionDefinitions,
                            line: $"  {xorResult} = xor i64 {accum}, {fieldHash}");
                        accum = xorResult;
                    }
                }

                EmitLine(sb: _functionDefinitions, line: $"  ret i64 {accum}");
                break;
            }
            default:
                EmitLine(sb: _functionDefinitions, line: "  ret i64 0");
                break;
        }

        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    /// <summary>
    /// Emits the body for a synthesized numeric creator from a Flags or Choice type.
    /// U64.$create(from: FlagsType) — identity cast (i64 → i64).
    /// S64.$create(from: ChoiceType) — sign-extend (i32 → i64).
    /// </summary>
    private void EmitSynthesizedNumericCreate(RoutineInfo routine, string funcName)
    {
        if (routine.ReturnType == null || routine.Parameters.Count != 1) return;

        string paramLlvmType = GetParameterLlvmType(type: routine.Parameters[0].Type);
        string retLlvmType = GetLlvmType(type: routine.ReturnType);

        EmitLine(sb: _functionDefinitions,
            line: $"define {retLlvmType} @{funcName}({paramLlvmType} %from) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");

        if (paramLlvmType == retLlvmType)
        {
            EmitLine(sb: _functionDefinitions, line: $"  ret {retLlvmType} %from");
        }
        else
        {
            // Choice is i32, S64 return is i64 — sign-extend
            string ext = NextTemp();
            EmitLine(sb: _functionDefinitions,
                line: $"  {ext} = sext {paramLlvmType} %from to {retLlvmType}");
            EmitLine(sb: _functionDefinitions, line: $"  ret {retLlvmType} {ext}");
        }

        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    /// <summary>
    /// Emits the body for a synthesized id() routine on entities.
    /// Returns the pointer cast to i64.
    /// </summary>
    private void EmitSynthesizedId(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        EmitLine(sb: _functionDefinitions, line: $"define i64 @{funcName}(ptr %me) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");
        string result = NextTemp();
        EmitLine(sb: _functionDefinitions, line: $"  {result} = ptrtoint ptr %me to i64");
        EmitLine(sb: _functionDefinitions, line: $"  ret i64 {result}");
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    /// <summary>
    /// Emits the body for a synthesized copy!() routine on entities.
    /// Allocates new memory and copies all fields via GEP load/store.
    /// </summary>
    private void EmitSynthesizedCopy(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType is not EntityTypeInfo entity)
        {
            return;
        }

        string typeName = GetEntityTypeName(entity: entity);
        int size = CalculateEntitySize(entity: entity);

        EmitLine(sb: _functionDefinitions, line: $"define ptr @{funcName}(ptr %me) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");

        // Allocate new entity
        string newPtr = NextTemp();
        EmitLine(sb: _functionDefinitions,
            line: $"  {newPtr} = call ptr @rf_allocate_dynamic(i64 {size})");

        // Copy each field
        for (int i = 0; i < entity.MemberVariables.Count; i++)
        {
            MemberVariableInfo mv = entity.MemberVariables[index: i];
            string fieldType = GetLlvmType(type: mv.Type);

            // Load from source
            string srcFp = NextTemp();
            EmitLine(sb: _functionDefinitions,
                line: $"  {srcFp} = getelementptr {typeName}, ptr %me, i32 0, i32 {i}");
            string val = NextTemp();
            EmitLine(sb: _functionDefinitions, line: $"  {val} = load {fieldType}, ptr {srcFp}");

            // Store to dest
            string dstFp = NextTemp();
            EmitLine(sb: _functionDefinitions,
                line: $"  {dstFp} = getelementptr {typeName}, ptr {newPtr}, i32 0, i32 {i}");
            EmitLine(sb: _functionDefinitions, line: $"  store {fieldType} {val}, ptr {dstFp}");
        }

        EmitLine(sb: _functionDefinitions, line: $"  ret ptr {newPtr}");
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    /// <summary>
    /// Emits the body for a synthesized Data.$create(from: T) routine.
    /// Boxes a value into a Data entity (type_id, size, data_ptr).
    /// </summary>
    private void EmitSynthesizedDataCreate(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null || routine.Parameters.Count == 0)
        {
            return;
        }

        TypeInfo paramType = routine.Parameters[index: 0].Type;
        string paramLlvmType = GetParameterLlvmType(type: paramType);
        string paramName = routine.Parameters[index: 0].Name;

        // Data entity layout: { i64 type_id, i64 size, ptr data_ptr } = 24 bytes
        string dataStructType = GetEntityTypeName(entity: (EntityTypeInfo)routine.OwnerType);

        ulong typeId = TypeIdHelper.ComputeTypeId(fullName: paramType.FullName);
        long dataSize = ComputeDataSize(type: paramType);

        EmitLine(sb: _functionDefinitions,
            line: $"define ptr @{funcName}({paramLlvmType} %{paramName}) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");

        // Allocate Data entity: { i64 type_id, ptr data_ptr, i64 data_size }
        string dataPtr = NextTemp();
        EmitLine(sb: _functionDefinitions,
            line: $"  {dataPtr} = call ptr @rf_allocate_dynamic(i64 {_dataEntitySizeBytes})");

        // Store type_id (compile-time FNV-1a hash)
        string tidPtr = NextTemp();
        EmitLine(sb: _functionDefinitions,
            line: $"  {tidPtr} = getelementptr {dataStructType}, ptr {dataPtr}, i32 0, i32 0");
        EmitLine(sb: _functionDefinitions, line: $"  store i64 {typeId}, ptr {tidPtr}");

        // Store size
        string sizePtr = NextTemp();
        EmitLine(sb: _functionDefinitions,
            line: $"  {sizePtr} = getelementptr {dataStructType}, ptr {dataPtr}, i32 0, i32 1");
        EmitLine(sb: _functionDefinitions, line: $"  store i64 {dataSize}, ptr {sizePtr}");

        // Allocate memory for boxed value and copy
        string box = NextTemp();
        EmitLine(sb: _functionDefinitions,
            line: $"  {box} = call ptr @rf_allocate_dynamic(i64 {dataSize})");
        EmitLine(sb: _functionDefinitions,
            line: $"  store {paramLlvmType} %{paramName}, ptr {box}");

        // Store data_ptr
        string dptrPtr = NextTemp();
        EmitLine(sb: _functionDefinitions,
            line: $"  {dptrPtr} = getelementptr {dataStructType}, ptr {dataPtr}, i32 0, i32 2");
        EmitLine(sb: _functionDefinitions, line: $"  store ptr {box}, ptr {dptrPtr}");

        EmitLine(sb: _functionDefinitions, line: $"  ret ptr {dataPtr}");
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    /// <summary>
    /// Emits the body for a synthesized all_cases() routine on flags/choice types.
    /// Returns a List[Me] containing all cases.
    /// Layout: { ptr data, i64 count, i64 capacity } matching List[T] entity field order.
    /// </summary>
    private void EmitSynthesizedAllCases(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null)
        {
            return;
        }

        // Collect all case values as constants
        var caseValues = new List<long>();
        bool isChoice = routine.OwnerType is ChoiceTypeInfo;

        if (routine.OwnerType is ChoiceTypeInfo choiceType)
        {
            foreach (ChoiceCaseInfo c in choiceType.Cases)
            {
                caseValues.Add(item: c.ComputedValue);
            }
        }
        else if (routine.OwnerType is FlagsTypeInfo flagsType)
        {
            foreach (FlagsMemberInfo member in flagsType.Members)
            {
                caseValues.Add(item: (long)(1UL << member.BitPosition));
            }
        }
        else
        {
            return;
        }

        int count = caseValues.Count;
        int elemSize = isChoice
            ? 4
            : 8; // i32 for choice, i64 for flags
        string elemType = isChoice
            ? "i32"
            : "i64";

        StringBuilder sb = _functionDefinitions;
        EmitLine(sb: sb, line: $"define ptr @{funcName}() {{");
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

        // Store each case value into the data array
        for (int i = 0; i < count; i++)
        {
            string elemPtr = NextTemp();
            EmitLine(sb: sb,
                line: $"  {elemPtr} = getelementptr {elemType}, ptr {dataPtr}, i64 {i}");
            EmitLine(sb: sb, line: $"  store {elemType} {caseValues[index: i]}, ptr {elemPtr}");
        }

        EmitLine(sb: sb, line: $"  ret ptr {listPtr}");
        EmitLine(sb: sb, line: "}");
        EmitLine(sb: sb, line: "");
    }
}
