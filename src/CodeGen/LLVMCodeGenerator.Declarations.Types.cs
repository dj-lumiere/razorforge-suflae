namespace Compiler.CodeGen;

using System.Text;
using SemanticVerification.Symbols;
using SemanticVerification.Types;

/// <summary>
/// Declaration code generation for LLVM types and routine signatures.
/// </summary>
public partial class LLVMCodeGenerator
{
    private void GenerateEntityType(EntityTypeInfo entity)
    {
        string typeName = GetEntityTypeName(entity: entity);

        // Skip if already generated
        if (_generatedTypes.Contains(item: typeName))
        {
            return;
        }

        _generatedTypes.Add(item: typeName);

        // For generic resolutions with stale empty member variables (created before the generic
        // definition's members were populated), re-create from the now-complete definition.
        if (entity is
            {
                IsGenericResolution: true, MemberVariables.Count: 0,
                GenericDefinition: { MemberVariables.Count: > 0 } genDef
            } && entity.TypeArguments != null)
        {
            var refreshed =
                genDef.CreateInstance(typeArguments: entity.TypeArguments) as EntityTypeInfo;
            if (refreshed != null && refreshed.MemberVariables.Count > 0)
            {
                entity = refreshed;
            }
        }

        // Recursively ensure struct types for member variable types are defined
        EnsureMemberVariableTypesGenerated(memberVariables: entity.MemberVariables);

        // Build the struct type
        var memberVariableTypes = new List<string>();
        foreach (MemberVariableInfo memberVariable in entity.MemberVariables)
        {
            string memberVariableType = GetLLVMType(type: memberVariable.Type);
            memberVariableTypes.Add(item: memberVariableType);
        }

        var decl = new StringBuilder();
        // Handle empty entities (no member variables)
        if (memberVariableTypes.Count == 0)
        {
            // Empty struct needs at least a dummy byte for addressability
            decl.AppendLine(value: $"{typeName} = type {{ i8 }}");
        }
        else
        {
            string memberVars = string.Join(separator: ", ", values: memberVariableTypes);
            decl.AppendLine(value: $"{typeName} = type {{ {memberVars} }}");
        }

        // Add member variable comment for documentation
        if (entity.MemberVariables.Count > 0)
        {
            decl.Append(handler: $"; {typeName} member variables: ");
            for (int i = 0; i < entity.MemberVariables.Count; i++)
            {
                if (i > 0) decl.Append(value: ", ");
                decl.Append(handler: $"{i}={entity.MemberVariables[index: i].Name}");
            }
            decl.AppendLine();
        }

        _typeDeclarationsEntity[key: typeName] = decl.ToString();
    }


    /// <summary>
    /// Generates the LLVM struct type for a crashable type.
    /// Crashable types have entity semantics (heap-allocated, pointer at usage sites).
    /// </summary>
    private void GenerateCrashableType(CrashableTypeInfo crashable)
    {
        string typeName = GetCrashableTypeName(crashable: crashable);

        if (_generatedTypes.Contains(item: typeName))
            return;

        _generatedTypes.Add(item: typeName);

        EnsureMemberVariableTypesGenerated(memberVariables: crashable.MemberVariables);

        var memberVariableTypes = new List<string>();
        foreach (MemberVariableInfo memberVariable in crashable.MemberVariables)
            memberVariableTypes.Add(item: GetLLVMType(type: memberVariable.Type));

        var decl = new StringBuilder();
        if (memberVariableTypes.Count == 0)
            decl.AppendLine(value: $"{typeName} = type {{ i8 }}");
        else
        {
            string memberVars = string.Join(separator: ", ", values: memberVariableTypes);
            decl.AppendLine(value: $"{typeName} = type {{ {memberVars} }}");
        }
        _typeDeclarationsCrashable[key: typeName] = decl.ToString();
    }

    /// <summary>
    /// Generates the LLVM struct type for a record.
    /// Record = value type, stack-allocated, copy semantics.
    /// Single-member-variable wrappers are unwrapped to their underlying intrinsic.
    /// </summary>
    /// <param name="record">The record type info.</param>
    private void GenerateRecordType(RecordTypeInfo record)
    {
        // Backend-annotated and single-member-variable wrappers don't need struct types
        if (record.HasDirectBackendType || record.IsSingleMemberVariableWrapper)
        {
            return;
        }

        string typeName = GetRecordTypeName(record: record);

        // Skip if already generated
        if (!_generatedTypes.Add(item: typeName))
        {
            return;
        }

        // For generic resolutions with stale empty member variables, re-create from the definition
        if (record is
            {
                IsGenericResolution: true, MemberVariables.Count: 0,
                GenericDefinition: { MemberVariables.Count: > 0 } genDef
            } && record.TypeArguments != null)
        {
            var refreshed =
                genDef.CreateInstance(typeArguments: record.TypeArguments) as RecordTypeInfo;
            if (refreshed != null && refreshed.MemberVariables.Count > 0)
            {
                record = refreshed;
            }
        }

        // Recursively ensure struct types for member variable types are defined
        EnsureMemberVariableTypesGenerated(memberVariables: record.MemberVariables);

        // Build the struct type
        var memberVariableTypes = new List<string>();
        foreach (MemberVariableInfo memberVariable in record.MemberVariables)
        {
            Console.Error.WriteLine(
                value: $"[CG-DBG] GenerateRecordType {record.FullName}: field '{memberVariable.Name}' type={memberVariable.Type?.GetType().Name}({memberVariable.Type?.FullName})");
            string memberVariableType = GetLLVMType(type: memberVariable.Type);
            memberVariableTypes.Add(item: memberVariableType);
        }

        var decl = new StringBuilder();
        // Handle empty records
        if (memberVariableTypes.Count == 0)
        {
            decl.AppendLine(value: $"{typeName} = type {{ }}");
        }
        else
        {
            string memberVars = string.Join(separator: ", ", values: memberVariableTypes);
            decl.AppendLine(value: $"{typeName} = type {{ {memberVars} }}");
        }

        // Add member variable comment
        if (record.MemberVariables.Count > 0)
        {
            decl.Append(handler: $"; {typeName} member variables: ");
            for (int i = 0; i < record.MemberVariables.Count; i++)
            {
                if (i > 0) decl.Append(value: ", ");
                decl.Append(handler: $"{i}={record.MemberVariables[index: i].Name}");
            }
            decl.AppendLine();
        }

        _typeDeclarationsRecord[key: typeName] = decl.ToString();
    }

    /// <summary>
    /// Recursively ensures struct type definitions exist for member variable types.
    /// Handles nested generic resolutions that may not be in the registry (e.g.,
    /// Maybe[BTreeSetNode[S64]] created during member variable substitution).
    /// </summary>
    private void EnsureMemberVariableTypesGenerated(
        IReadOnlyList<MemberVariableInfo> memberVariables)
    {
        foreach (MemberVariableInfo mv in memberVariables)
        {
            switch (mv.Type)
            {
                case EntityTypeInfo { IsGenericDefinition: false } nestedEntity:
                    GenerateEntityType(entity: nestedEntity);
                    break;
                case RecordTypeInfo
                {
                    IsGenericDefinition: false, HasDirectBackendType: false,
                    IsSingleMemberVariableWrapper: false
                } nestedRecord:
                    GenerateRecordType(record: nestedRecord);
                    break;
            }
        }
    }


    /// <summary>
    /// Generates the LLVM type for a choice (enum).
    /// Choice = record with single integer member variable (tag value).
    /// </summary>
    /// <param name="choice">The choice type info.</param>
    private void GenerateChoiceType(ChoiceTypeInfo choice)
    {
        string typeName = GetChoiceTypeName(choice: choice);

        // Skip if already generated
        if (_generatedTypes.Contains(item: typeName))
        {
            return;
        }

        _generatedTypes.Add(item: typeName);

        var decl = new StringBuilder();
        decl.AppendLine(value: $"{typeName} = type {{ i32 }}");

        // Add case values as comment
        decl.Append(handler: $"; {typeName} cases: ");
        for (int i = 0; i < choice.Cases.Count; i++)
        {
            if (i > 0) decl.Append(value: ", ");
            ChoiceCaseInfo c = choice.Cases[index: i];
            decl.Append(handler: $"{c.Name}={c.ComputedValue}");
        }
        decl.AppendLine();

        _typeDeclarationsChoice[key: typeName] = decl.ToString();
    }


    /// <summary>
    /// Generates the LLVM type for a variant (type-based tagged union).
    /// Variant = { i64 tag, [N x i8] payload } where N = max member size.
    /// </summary>
    /// <param name="variant">The variant type info.</param>
    private void GenerateVariantType(VariantTypeInfo variant)
    {
        string typeName = GetVariantTypeName(variant: variant);

        // Skip if already generated
        if (_generatedTypes.Contains(item: typeName))
        {
            return;
        }

        _generatedTypes.Add(item: typeName);

        // Calculate max payload size
        int maxPayloadSize = 0;
        foreach (VariantMemberInfo member in variant.Members)
        {
            if (!member.IsNone && member.Type != null)
            {
                int payloadSize = GetTypeSize(type: member.Type);
                maxPayloadSize = Math.Max(val1: maxPayloadSize, val2: payloadSize);
            }
        }

        var decl = new StringBuilder();
        // Variant is { i64 tag, [N x i8] payload }
        if (maxPayloadSize > 0)
            decl.AppendLine(value: $"{typeName} = type {{ i64, [{maxPayloadSize} x i8] }}");
        else
            decl.AppendLine(value: $"{typeName} = type {{ i64 }}");

        // Add member info as comment
        decl.Append(handler: $"; {typeName} members: ");
        for (int i = 0; i < variant.Members.Count; i++)
        {
            if (i > 0) decl.Append(value: ", ");
            VariantMemberInfo m = variant.Members[index: i];
            decl.Append(handler: $"{m.Name}={m.TagValue}");
        }
        decl.AppendLine();

        _typeDeclarationsVariant[key: typeName] = decl.ToString();
    }

    /// <summary>
    /// Generates the LLVM named type alias for a flags type.
    /// Flags are backed by i64; the declaration exists for IR readability only.
    /// </summary>
    private void GenerateFlagsType(FlagsTypeInfo flags)
    {
        string typeName = $"%{Q(name: $"Flags.{flags.Name}")}";

        if (!_generatedTypes.Add(item: typeName))
            return;

        var decl = new StringBuilder();
        decl.AppendLine(value: $"{typeName} = type {{ i64 }}");

        // Add flag members as comment (name=bitmask)
        decl.Append(handler: $"; {typeName} flags: ");
        for (int i = 0; i < flags.Members.Count; i++)
        {
            if (i > 0) decl.Append(value: ", ");
            FlagsMemberInfo m = flags.Members[index: i];
            decl.Append(handler: $"{m.Name}=0x{1UL << m.BitPosition:X}");
        }
        decl.AppendLine();

        _typeDeclarationsFlags[key: typeName] = decl.ToString();
    }
}
