using System.Text;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// Declaration code generation for LLVM types and routine signatures.
/// </summary>
public partial class LlvmCodeGenerator
{
    private void GenerateEntityType(EntityTypeInfo entity)
    {
        string typeName = GetEntityTypeName(entity: entity);

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

        if (entity.MemberVariables.Count == 0 && !TryRebuildEntityMembersFromAst(entity: entity))
        {
            TypeInfo? refreshed = _registry.LookupType(name: entity.FullName) ??
                                  _registry.LookupType(name: entity.Name);
            if (refreshed is EntityTypeInfo resolvedEntity &&
                resolvedEntity.MemberVariables.Count > 0)
            {
                entity = resolvedEntity;
            }
        }

        // Skip if already generated
        if (!_generatedTypes.Add(item: typeName))
        {
            return;
        }

        // Recursively ensure struct types for member variable types are defined
        EnsureMemberVariableTypesGenerated(memberVariables: entity.MemberVariables);

        // Build the struct type
        var memberVariableTypes = new List<string>();
        foreach (MemberVariableInfo memberVariable in entity.MemberVariables)
        {
            string memberVariableType = GetLlvmType(type: memberVariable.Type);
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
        {
            memberVariableTypes.Add(item: GetLlvmType(type: memberVariable.Type));
        }

        var decl = new StringBuilder();
        if (memberVariableTypes.Count == 0)
        {
            decl.AppendLine(value: $"{typeName} = type {{ i8 }}");
        }
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
        // Backend-annotated records don't need struct types
        if (record.HasDirectBackendType)
        {
            return;
        }

        // Skip generic definitions and any type whose type arguments still contain unresolved
        // generic parameters. Such types produce invalid IR (e.g. { [{N} x {T}] }). This guard
        // fires both during GenerateTypeDeclarations and when called as a side-effect from body
        // emission, so it is defence-in-depth against partially-concrete types leaking through.
        if (record.IsGenericDefinition ||
            record.TypeArguments?.Any(predicate: t =>
                ContainsGenericParameter(t) || t is ErrorTypeInfo) == true)
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
            string memberVariableType = GetLlvmType(type: memberVariable.Type);
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
        var visited = new HashSet<string>();
        foreach (MemberVariableInfo mv in memberVariables)
        {
            EnsureTypeGenerated(type: mv.Type, visited: visited);
        }
    }

    // Recursively descends into a type's TypeArguments and wrapper inner types so that
    // concrete nested generics (e.g. Owned[BTreeDictNode[S64, S64]] inside a
    // Maybe[Owned[...]] field of SortedDict[S64, S64]) get their struct types emitted.
    private void EnsureTypeGenerated(TypeInfo? type, HashSet<string> visited)
    {
        if (type == null) return;
        if (!visited.Add(item: type.FullName)) return;

        // Skip not-yet-concrete generic resolutions. A `ListNode[T]` resolution (where T is
        // still `GenericParameterTypeInfo`) has IsGenericDefinition=false but its type
        // arguments include an unbound parameter; emitting its layout would force `T`
        // through GetLlvmType and crash. The same filter applies in GenerateTypeDeclarations
        // for top-level emission — replicate it here so it also gates recursive descent
        // into nested field types of monomorphized parents.
        bool hasUnboundTypeArg =
            type.TypeArguments is { Count: > 0 } args
            && args.Any(predicate: ContainsGenericParameter);

        switch (type)
        {
            case EntityTypeInfo { IsGenericDefinition: false } nestedEntity
                when !hasUnboundTypeArg:
                GenerateEntityType(entity: nestedEntity);
                break;
            case RecordTypeInfo
            {
                IsGenericDefinition: false, HasDirectBackendType: false
            } nestedRecord when !hasUnboundTypeArg:
                GenerateRecordType(record: nestedRecord);
                break;
        }

        if (type is WrapperTypeInfo wrapper)
        {
            EnsureTypeGenerated(type: wrapper.InnerType, visited: visited);
        }

        if (type.TypeArguments is { Count: > 0 } typeArgs)
        {
            foreach (TypeInfo ta in typeArgs)
            {
                EnsureTypeGenerated(type: ta, visited: visited);
            }
        }
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
        if (!_generatedTypes.Add(item: typeName))
        {
            return;
        }

        // Calculate max payload size
        int maxPayloadSize = 0;
        foreach (VariantMemberInfo member in variant.Members)
        {
            if (member is not { IsNone: false, Type: not null })
            {
                continue;
            }

            int payloadSize = GetTypeSize(type: member.Type);
            maxPayloadSize = Math.Max(val1: maxPayloadSize, val2: payloadSize);
        }

        var decl = new StringBuilder();
        // Variant is { i64 tag, [N x i8] payload }
        if (maxPayloadSize > 0)
        {
            decl.AppendLine(value: $"{typeName} = type {{ i64, [{maxPayloadSize} x i8] }}");
        }
        else
        {
            decl.AppendLine(value: $"{typeName} = type {{ i64 }}");
        }

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
}
