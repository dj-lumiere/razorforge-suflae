namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Symbols;
using SemanticAnalysis.Types;

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

        // Handle empty entities (no member variables)
        if (memberVariableTypes.Count == 0)
        {
            // Empty struct needs at least a dummy byte for addressability
            EmitLine(sb: _typeDeclarations, line: $"{typeName} = type {{ i8 }}");
        }
        else
        {
            string memberVars = string.Join(separator: ", ", values: memberVariableTypes);
            EmitLine(sb: _typeDeclarations, line: $"{typeName} = type {{ {memberVars} }}");
        }

        // Add member variable comment for documentation
        if (entity.MemberVariables.Count > 0)
        {
            var sb = new StringBuilder();
            sb.Append(handler: $"; {typeName} member variables: ");
            for (int i = 0; i < entity.MemberVariables.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(value: ", ");
                }

                sb.Append(handler: $"{i}={entity.MemberVariables[index: i].Name}");
            }

            EmitLine(sb: _typeDeclarations, line: sb.ToString());
        }
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
            string memberVariableType = GetLLVMType(type: memberVariable.Type);
            memberVariableTypes.Add(item: memberVariableType);
        }

        // Handle empty records
        if (memberVariableTypes.Count == 0)
        {
            EmitLine(sb: _typeDeclarations, line: $"{typeName} = type {{ }}");
        }
        else
        {
            string memberVars = string.Join(separator: ", ", values: memberVariableTypes);
            EmitLine(sb: _typeDeclarations, line: $"{typeName} = type {{ {memberVars} }}");
        }

        // Add member variable comment
        if (record.MemberVariables.Count > 0)
        {
            var sb = new StringBuilder();
            sb.Append(handler: $"; {typeName} member variables: ");
            for (int i = 0; i < record.MemberVariables.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(value: ", ");
                }

                sb.Append(handler: $"{i}={record.MemberVariables[index: i].Name}");
            }

            EmitLine(sb: _typeDeclarations, line: sb.ToString());
        }
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

        // Choice is just an i32 (tag value)
        EmitLine(sb: _typeDeclarations, line: $"{typeName} = type {{ i32 }}");

        // Add case values as comments
        var sb = new StringBuilder();
        sb.Append(handler: $"; {typeName} cases: ");
        for (int i = 0; i < choice.Cases.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(value: ", ");
            }

            ChoiceCaseInfo c = choice.Cases[index: i];
            sb.Append(handler: $"{c.Name}={c.ComputedValue}");
        }

        EmitLine(sb: _typeDeclarations, line: sb.ToString());
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

        // Variant is { i64 tag, [N x i8] payload }
        if (maxPayloadSize > 0)
        {
            EmitLine(sb: _typeDeclarations,
                line: $"{typeName} = type {{ i64, [{maxPayloadSize} x i8] }}");
        }
        else
        {
            // No payloads (all None) - just the tag
            EmitLine(sb: _typeDeclarations, line: $"{typeName} = type {{ i64 }}");
        }

        // Add member info as comments
        var sb = new StringBuilder();
        sb.Append(handler: $"; {typeName} members: ");
        for (int i = 0; i < variant.Members.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(value: ", ");
            }

            VariantMemberInfo m = variant.Members[index: i];
            sb.Append(handler: $"{m.Name}={m.TagValue}");
        }

        EmitLine(sb: _typeDeclarations, line: sb.ToString());
    }
}
