using Compiler.Postprocessing.Passes;

namespace Compiler.CodeGen;

using System.Text;
using TypeModel.Symbols;
using TypeModel.Types;
using SyntaxTree;

/// <summary>
/// Expression code generation for entity construction and entity member operations.
/// </summary>
public partial class LlvmCodeGenerator
{
    private string EmitEntityAllocation(StringBuilder sb, EntityTypeInfo entity,
        List<string>? memberVariableValues = null)
    {
        string typeName = GetEntityTypeName(entity: entity);
        int size = CalculateEntitySize(entity: entity);

        // Allocate memory
        // TODO(C41): route through a typed allocator abstraction rather than calling rf_allocate_dynamic directly.
        string rawPtr = NextTemp();
        EmitLine(sb: sb, line: $"  {rawPtr} = call ptr @rf_allocate_dynamic(i64 {size})");

        // Initialize member variables
        for (int i = 0; i < entity.MemberVariables.Count; i++)
        {
            MemberVariableInfo memberVariable = entity.MemberVariables[index: i];
            string memberVariableType = GetLlvmType(type: memberVariable.Type);

            // Get member variable pointer using GEP
            string memberVariablePtr = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {memberVariablePtr} = getelementptr {typeName}, ptr {rawPtr}, i32 0, i32 {i}");

            // Get value to store
            string value;
            if (memberVariableValues != null && i < memberVariableValues.Count)
            {
                value = memberVariableValues[index: i];
            }
            else
            {
                value = GetZeroValue(type: memberVariable.Type);
            }

            // Store the value
            EmitLine(sb: sb,
                line: $"  store {memberVariableType} {value}, ptr {memberVariablePtr}");
        }

        return rawPtr;
    }

    /// <summary>
    /// Generates code for a constructor call expression.
    /// </summary>
    /// <param name="sb">StringBuilder to emit code to.</param>
    /// <param name="expr">The constructor call expression.</param>
    /// <returns>The temporary variable holding the result.</returns>
    private string EmitConstructorCall(StringBuilder sb, CreatorExpression expr)
    {
        if (ResolveCreatorType(creator: expr) is TupleTypeInfo tupleType)
        {
            return EmitTupleConstruction(sb: sb, tuple: tupleType, expr: expr);
        }

        TypeInfo? type = ResolveCreatorType(creator: expr);
        if (type == null)
        {
            throw new InvalidOperationException(
                message: $"Unknown type in constructor: {expr.TypeName}");
        }

        return type switch
        {
            EntityTypeInfo entity => EmitEntityConstruction(sb: sb, entity: entity, expr: expr),
            RecordTypeInfo record => EmitRecordConstruction(sb: sb, record: record, expr: expr),
            // Crashable types are entity-like (heap-allocated, ptr semantics).
            CrashableTypeInfo crashable => EmitCrashableConstruction(
                sb: sb,
                crashable: crashable,
                arguments: expr.MemberVariables
                    .Select(mv => (Expression)new NamedArgumentExpression(
                        Name: mv.Name, Value: mv.Value, Location: expr.Location))
                    .ToList()),
            _ => throw new InvalidOperationException(
                message: $"Cannot construct type: {type.Category}")
        };
    }

    /// <summary>
    /// Generates code to construct an entity with member variable values.
    /// </summary>
    private string EmitEntityConstruction(StringBuilder sb, EntityTypeInfo entity,
        CreatorExpression expr)
    {
        // Evaluate all member variable value expressions first
        var memberVariableValues = new List<string>();
        foreach ((string _, Expression fieldExpr) in expr.MemberVariables)
        {
            string value = EmitExpression(sb: sb, expr: fieldExpr);
            memberVariableValues.Add(item: value);
        }

        // Allocate and initialize
        return EmitEntityAllocation(sb: sb,
            entity: entity,
            memberVariableValues: memberVariableValues);
    }

    /// <summary>
    /// Generates code to construct a record (value type).
    /// </summary>
    private string EmitRecordConstruction(StringBuilder sb, RecordTypeInfo record,
        CreatorExpression expr)
    {
        // Backend-annotated or single-member-variable wrapper: just return the inner value
        if ((record.HasDirectBackendType || record.IsSingleMemberVariableWrapper) &&
            expr.MemberVariables.Count <= 1)
        {
            string argValue = EmitExpression(sb: sb, expr: expr.MemberVariables[index: 0].Value);
            if (record.HasDirectBackendType)
            {
                string targetLlvm = GetLlvmType(type: record);
                TypeInfo? argType = GetExpressionType(expr: expr.MemberVariables[index: 0].Value);
                string argLlvm = argType != null
                    ? GetLlvmType(type: argType)
                    : targetLlvm;
                if (argLlvm != targetLlvm)
                {
                    string cast = NextTemp();
                    if (targetLlvm == "ptr" && argLlvm != "ptr")
                    {
                        EmitLine(sb: sb, line: $"  {cast} = inttoptr {argLlvm} {argValue} to ptr");
                    }
                    else if (targetLlvm != "ptr" && argLlvm == "ptr")
                    {
                        EmitLine(sb: sb,
                            line: $"  {cast} = ptrtoint ptr {argValue} to {targetLlvm}");
                    }
                    else
                    {
                        EmitLine(sb: sb,
                            line: $"  {cast} = bitcast {argLlvm} {argValue} to {targetLlvm}");
                    }

                    return cast;
                }
            }

            return argValue;
        }

        // Multi-member-variable record: build struct value
        string typeName = GetRecordTypeName(record: record);

        // Start with zeroinitializer and insert each member variable
        string result = "zeroinitializer";
        for (int i = 0; i < expr.MemberVariables.Count && i < record.MemberVariables.Count; i++)
        {
            string value = EmitExpression(sb: sb, expr: expr.MemberVariables[index: i].Value);
            string memberVariableType = GetLlvmType(type: record.MemberVariables[index: i].Type);

            string newResult = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {newResult} = insertvalue {typeName} {result}, {memberVariableType} {value}, {i}");
            result = newResult;
        }

        return result;
    }

    private string EmitTupleConstruction(StringBuilder sb, TupleTypeInfo tuple,
        CreatorExpression expr)
    {
        string typeName = GetTupleTypeName(tuple: tuple);
        string result = "zeroinitializer";

        for (int i = 0; i < tuple.ElementTypes.Count; i++)
        {
            Expression? element = TryGetTupleCreatorElement(creator: expr, index: i);
            if (element == null)
            {
                continue;
            }

            string value = EmitExpression(sb: sb, expr: element);
            string elementLlvmType = GetLlvmType(type: tuple.ElementTypes[index: i]);
            string next = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {next} = insertvalue {typeName} {result}, {elementLlvmType} {value}, {i}");
            result = next;
        }

        return result;
    }

    private static Expression? TryGetTupleCreatorElement(CreatorExpression creator, int index)
    {
        string expectedName = $"item{index}";
        foreach ((string name, Expression value) in creator.MemberVariables)
        {
            if (name == expectedName)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Constructs a record from a list of positional arguments (for TypeName(args...) calls).
    /// </summary>
    private string EmitRecordConstruction(StringBuilder sb, RecordTypeInfo record,
        List<Expression> arguments)
    {
        // Backend-annotated or single-member-variable wrapper: just return the inner value
        if ((record.HasDirectBackendType || record.IsSingleMemberVariableWrapper) &&
            arguments.Count <= 1)
        {
            string argValue = EmitExpression(sb: sb, expr: arguments[index: 0]);
            if (record.HasDirectBackendType)
            {
                string targetLlvm = GetLlvmType(type: record);
                TypeInfo? argType = GetExpressionType(expr: arguments[index: 0]);
                string argLlvm = argType != null
                    ? GetLlvmType(type: argType)
                    : targetLlvm;
                if (argLlvm != targetLlvm)
                {
                    string cast = NextTemp();
                    if (targetLlvm == "ptr" && argLlvm != "ptr")
                    {
                        EmitLine(sb: sb, line: $"  {cast} = inttoptr {argLlvm} {argValue} to ptr");
                    }
                    else if (targetLlvm != "ptr" && argLlvm == "ptr")
                    {
                        EmitLine(sb: sb,
                            line: $"  {cast} = ptrtoint ptr {argValue} to {targetLlvm}");
                    }
                    else
                    {
                        EmitLine(sb: sb,
                            line: $"  {cast} = bitcast {argLlvm} {argValue} to {targetLlvm}");
                    }

                    return cast;
                }
            }

            return argValue;
        }

        // Multi-member-variable record: build struct value
        string typeName = GetRecordTypeName(record: record);
        string result = "zeroinitializer";
        for (int i = 0; i < arguments.Count && i < record.MemberVariables.Count; i++)
        {
            // Unwrap NamedArgumentExpression if present
            Expression arg = arguments[index: i] is NamedArgumentExpression named
                ? named.Value
                : arguments[index: i];
            string value = EmitExpression(sb: sb, expr: arg);
            string memberVariableType = GetLlvmType(type: record.MemberVariables[index: i].Type);

            string newResult = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {newResult} = insertvalue {typeName} {result}, {memberVariableType} {value}, {i}");
            result = newResult;
        }

        return result;
    }

    /// <summary>
    /// Emits entity construction: heap-allocate and initialize fields.
    /// </summary>
    private string EmitEntityConstruction(StringBuilder sb, EntityTypeInfo entity,
        List<Expression> arguments)
    {
        string typeName = GetEntityTypeName(entity: entity);
        // Allocate entity on heap
        string sizeTemp = NextTemp();
        EmitLine(sb: sb, line: $"  {sizeTemp} = getelementptr {typeName}, ptr null, i32 1");
        string size = NextTemp();
        EmitLine(sb: sb, line: $"  {size} = ptrtoint ptr {sizeTemp} to i64");
        string entityPtr = NextTemp();
        EmitLine(sb: sb, line: $"  {entityPtr} = call ptr @rf_allocate_dynamic(i64 {size})");

        // Initialize fields
        for (int i = 0; i < arguments.Count && i < entity.MemberVariables.Count; i++)
        {
            Expression arg = arguments[index: i] is NamedArgumentExpression named
                ? named.Value
                : arguments[index: i];
            string value = EmitExpression(sb: sb, expr: arg);
            string fieldType = GetLlvmType(type: entity.MemberVariables[index: i].Type);
            string fieldPtr = NextTemp();
            EmitLine(sb: sb,
                line: $"  {fieldPtr} = getelementptr {typeName}, ptr {entityPtr}, i32 0, i32 {i}");
            EmitLine(sb: sb, line: $"  store {fieldType} {value}, ptr {fieldPtr}");
        }

        return entityPtr;
    }

    /// <summary>
    /// Emits crashable type construction: heap-allocate and initialize fields.
    /// Mirrors entity construction — crashable types have entity (ptr) semantics.
    /// </summary>
    private string EmitCrashableConstruction(StringBuilder sb, CrashableTypeInfo crashable,
        List<Expression> arguments)
    {
        string typeName = GetCrashableTypeName(crashable: crashable);
        string sizeTemp = NextTemp();
        EmitLine(sb: sb, line: $"  {sizeTemp} = getelementptr {typeName}, ptr null, i32 1");
        string size = NextTemp();
        EmitLine(sb: sb, line: $"  {size} = ptrtoint ptr {sizeTemp} to i64");
        string crashablePtr = NextTemp();
        EmitLine(sb: sb, line: $"  {crashablePtr} = call ptr @rf_allocate_dynamic(i64 {size})");

        for (int i = 0; i < arguments.Count && i < crashable.MemberVariables.Count; i++)
        {
            Expression arg = arguments[index: i] is NamedArgumentExpression named
                ? named.Value
                : arguments[index: i];
            string value = EmitExpression(sb: sb, expr: arg);
            string fieldType = GetLlvmType(type: crashable.MemberVariables[index: i].Type);
            string fieldPtr = NextTemp();
            EmitLine(sb: sb,
                line: $"  {fieldPtr} = getelementptr {typeName}, ptr {crashablePtr}, i32 0, i32 {i}");
            EmitLine(sb: sb, line: $"  store {fieldType} {value}, ptr {fieldPtr}");
        }

        return crashablePtr;
    }

    /// <summary>
    /// Generates code to read a member variable from an entity/record.
    /// For entities: GEP + load
    /// For records: extractvalue
    /// </summary>
    /// <param name="sb">StringBuilder to emit code to.</param>
    /// <param name="expr">The member access expression.</param>
    /// <returns>The temporary variable holding the member variable value.</returns>
    private string EmitMemberVariableAccess(StringBuilder sb, MemberExpression expr)
    {
        string propertyName = expr.PropertyName;

        // Choice/Flags member access: emit constant value directly (no target expression to evaluate)
        TypeInfo? earlyObjectType = GetExpressionType(expr: expr.Object);
        // Fallback: if SA didn't set ResolvedType (type-as-identifier), try type lookup by name
        if (earlyObjectType == null && expr.Object is IdentifierExpression typeId)
        {
            earlyObjectType = LookupTypeInCurrentModule(name: typeId.Name);
        }

        if (earlyObjectType is ChoiceTypeInfo choice)
        {
            ChoiceCaseInfo? caseInfo =
                choice.Cases.FirstOrDefault(predicate: c => c.Name == propertyName);
            if (caseInfo != null)
            {
                return caseInfo.ComputedValue.ToString();
            }
        }

        if (earlyObjectType is FlagsTypeInfo flags)
        {
            FlagsMemberInfo? memberInfo =
                flags.Members.FirstOrDefault(predicate: m => m.Name == propertyName);
            if (memberInfo != null)
            {
                return (1L << memberInfo.BitPosition).ToString();
            }
        }

        // Evaluate the target expression
        string target = EmitExpression(sb: sb, expr: expr.Object);

        // Get the target type
        TypeInfo? targetType = GetExpressionType(expr: expr.Object);
        if (targetType == null)
        {
            throw new InvalidOperationException(
                message: "Cannot determine type of member variable access target");
        }

        TryGetTransparentProtocolTarget(type: targetType, targetType: out TypeInfo? lookupType);
        targetType = lookupType ?? targetType;

        // Wrapper type forwarding: Viewed[T], Grasped[T], etc.
        // These are records wrapping a Hijacked[T] (ptr) — forward member access to the inner entity type
        if (targetType is RecordTypeInfo wrapperRecord &&
            GetGenericBaseName(type: wrapperRecord) is { } wrapBaseName &&
            WrapperTypeNames.Contains(item: wrapBaseName) &&
            wrapperRecord.TypeArguments is { Count: > 0 } &&
            wrapperRecord.TypeArguments[index: 0] is EntityTypeInfo innerEntity &&
            !wrapperRecord.MemberVariables.Any(predicate: mv => mv.Name == propertyName))
        {
            // For @llvm("ptr") wrappers, the value IS the pointer directly
            // For struct wrappers, extract the inner Hijacked[T] (ptr) from field 0
            string innerPtr;
            if (wrapperRecord.HasDirectBackendType)
            {
                innerPtr = target;
            }
            else
            {
                string recordTypeName = GetRecordTypeName(record: wrapperRecord);
                innerPtr = NextTemp();
                // Find the index of the Hijacked[T] field that holds the inner entity pointer.
                // (e.g. Retained[T] has controller=0, data=1; Inspected[T] has ptr=0)
                int dataFieldIndex = 0;
                for (int fi = 0; fi < wrapperRecord.MemberVariables.Count; fi++)
                {
                    if (wrapperRecord.MemberVariables[index: fi].Type is WrapperTypeInfo
                            { Name: "Hijacked" } hijacked
                        && hijacked.TypeArguments is { Count: > 0 }
                        && hijacked.TypeArguments[index: 0] is EntityTypeInfo fieldInner
                        && fieldInner.FullName == innerEntity.FullName)
                    {
                        dataFieldIndex = fi;
                        break;
                    }
                }
                EmitLine(sb: sb,
                    line: $"  {innerPtr} = extractvalue {recordTypeName} {target}, {dataFieldIndex}");
            }

            return EmitEntityMemberVariableRead(sb: sb,
                entityPtr: innerPtr,
                entity: innerEntity,
                memberVariableName: propertyName);
        }

        return targetType switch
        {
            EntityTypeInfo entity => EmitEntityMemberVariableRead(sb: sb,
                entityPtr: target,
                entity: entity,
                memberVariableName: propertyName),
            RecordTypeInfo record => EmitRecordMemberVariableRead(sb: sb,
                recordValue: target,
                record: record,
                memberVariableName: propertyName),
            CrashableTypeInfo crashable => EmitCrashableMemberVariableRead(sb: sb,
                crashablePtr: target,
                crashable: crashable,
                memberVariableName: propertyName),
            TupleTypeInfo tuple => EmitTupleMemberVariableRead(sb: sb,
                tupleValue: target,
                tuple: tuple,
                memberVariableName: propertyName),
            // Synthetic type_id access generated by PatternLoweringPass for variant subjects.
            VariantTypeInfo variant when propertyName == "type_id" =>
                EmitVariantTagAccess(sb: sb, variantValue: target, variant: variant),
            _ => throw new InvalidOperationException(
                message: $"Cannot access member variable '{propertyName}' on type: {targetType.Name} (category: {targetType.Category}), in routine: {_currentEmittingRoutine?.RegistryKey ?? "<unknown>"}")
        };
    }

    /// <summary>
    /// Generates code to read a member variable from an entity (pointer type).
    /// Uses GEP to get member variable address, then load.
    /// </summary>
    private string EmitEntityMemberVariableRead(StringBuilder sb, string entityPtr,
        EntityTypeInfo entity, string memberVariableName)
    {
        // Refresh stale generic resolutions (member variables may be empty or missing the target member)
        entity = RefreshEntityMemberVariables(entity: entity,
            memberVariableName: memberVariableName);

        // Ensure entity type struct definition exists in LLVM IR
        GenerateEntityType(entity: entity);

        // Find member variable index
        int memberVariableIndex = -1;
        MemberVariableInfo? memberVariable = null;
        for (int i = 0; i < entity.MemberVariables.Count; i++)
        {
            if (entity.MemberVariables[index: i].Name == memberVariableName)
            {
                memberVariableIndex = i;
                memberVariable = entity.MemberVariables[index: i];
                break;
            }
        }

        if (memberVariableIndex < 0 || memberVariable == null)
        {
            string memberList = string.Join(", ", entity.MemberVariables.Select(mv => mv.Name));
            string genDefName = entity.GenericDefinition?.FullName ?? "(null)";
            string genDefMembers = entity.GenericDefinition != null
                ? string.Join(", ", entity.GenericDefinition.MemberVariables.Select(mv => mv.Name))
                : "(null)";
            string typeArgNames = entity.TypeArguments != null
                ? string.Join(", ", entity.TypeArguments.Select(t => t.FullName))
                : "(null)";
            throw new InvalidOperationException(
                message:
                $"Member variable '{memberVariableName}' not found on entity '{entity.FullName}' (members: [{memberList}], GenericDef={genDefName}, GenericDefMembers=[{genDefMembers}], TypeArgs=[{typeArgNames}])");
        }

        string typeName = GetEntityTypeName(entity: entity);
        string memberVariableType = GetLlvmType(type: memberVariable.Type);

        // GEP to get member variable pointer
        string memberVariablePtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {memberVariablePtr} = getelementptr {typeName}, ptr {entityPtr}, i32 0, i32 {memberVariableIndex}");

        // Load the member variable value
        string value = NextTemp();
        EmitLine(sb: sb, line: $"  {value} = load {memberVariableType}, ptr {memberVariablePtr}");

        return value;
    }

    /// <summary>
    /// Generates code to read a member variable from a crashable type (heap-allocated, pointer).
    /// Uses GEP + load, same structural pattern as entities.
    /// </summary>
    private string EmitCrashableMemberVariableRead(StringBuilder sb, string crashablePtr,
        CrashableTypeInfo crashable, string memberVariableName)
    {
        int memberVariableIndex = -1;
        MemberVariableInfo? memberVariable = null;
        for (int i = 0; i < crashable.MemberVariables.Count; i++)
        {
            if (crashable.MemberVariables[index: i].Name == memberVariableName)
            {
                memberVariableIndex = i;
                memberVariable = crashable.MemberVariables[index: i];
                break;
            }
        }

        if (memberVariableIndex < 0 || memberVariable == null)
        {
            throw new InvalidOperationException(
                message:
                $"Member variable '{memberVariableName}' not found on crashable '{crashable.Name}'");
        }

        string typeName = GetCrashableTypeName(crashable: crashable);
        string memberVariableType = GetLlvmType(type: memberVariable.Type);

        string memberVariablePtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {memberVariablePtr} = getelementptr {typeName}, ptr {crashablePtr}, i32 0, i32 {memberVariableIndex}");

        string value = NextTemp();
        EmitLine(sb: sb, line: $"  {value} = load {memberVariableType}, ptr {memberVariablePtr}");

        return value;
    }

    /// <summary>
    /// Generates code to read a member variable from a record (value type).
    /// Uses extractvalue instruction.
    /// </summary>
    private string EmitRecordMemberVariableRead(StringBuilder sb, string recordValue,
        RecordTypeInfo record, string memberVariableName)
    {
        // Hijacked[T] (@llvm("ptr")): .address → ptrtoint ptr to i64
        if (record is { HasDirectBackendType: true, LlvmType: "ptr" } &&
            memberVariableName == "address")
        {
            string addr = NextTemp();
            EmitLine(sb: sb, line: $"  {addr} = ptrtoint ptr {recordValue} to i64");
            return addr;
        }

        // Backend-annotated or single-member-variable wrapper: the value IS the field
        if (record.HasDirectBackendType || record.IsSingleMemberVariableWrapper)
        {
            return recordValue;
        }

        // Find member variable index
        int memberVariableIndex = -1;
        MemberVariableInfo? memberVariable = null;
        for (int i = 0; i < record.MemberVariables.Count; i++)
        {
            if (record.MemberVariables[index: i].Name == memberVariableName)
            {
                memberVariableIndex = i;
                memberVariable = record.MemberVariables[index: i];
                break;
            }
        }

        if (memberVariableIndex < 0 || memberVariable == null)
        {
            throw new InvalidOperationException(
                message:
                $"Member variable '{memberVariableName}' not found on record '{record.FullName}'");
        }

        string typeName = GetRecordTypeName(record: record);
        string memberVariableType = GetLlvmType(type: memberVariable.Type);

        // Extract the member variable value
        string value = NextTemp();
        EmitLine(sb: sb,
            line: $"  {value} = extractvalue {typeName} {recordValue}, {memberVariableIndex}");

        return value;
    }


    /// <summary>
    /// Extracts the i64 type_id tag (field 0) from a variant struct value via <c>extractvalue</c>.
    /// Generated by <see cref="PatternLoweringPass"/> for variant <c>TypePattern</c> conditions.
    /// </summary>
    private string EmitVariantTagAccess(StringBuilder sb, string variantValue,
        VariantTypeInfo variant)
    {
        string typeName = GetVariantTypeName(variant: variant);
        string tag = NextTemp();
        EmitLine(sb: sb, line: $"  {tag} = extractvalue {typeName} {variantValue}, 0");
        return tag;
    }

    /// <summary>
    /// Generates code to read a field from a tuple value (value type — uses extractvalue).
    /// </summary>
    private string EmitTupleMemberVariableRead(StringBuilder sb, string tupleValue,
        TupleTypeInfo tuple, string memberVariableName)
    {
        // Field names are item0, item1, ... — parse the index directly
        if (!memberVariableName.StartsWith(value: "item", comparisonType: StringComparison.Ordinal) ||
            !int.TryParse(s: memberVariableName.AsSpan(start: 4), result: out int index) ||
            index < 0 || index >= tuple.ElementTypes.Count)
        {
            throw new InvalidOperationException(
                message: $"Member variable '{memberVariableName}' not found on tuple '{tuple.Name}'");
        }

        string tupleTypeName = GetTupleTypeName(tuple: tuple);
        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = extractvalue {tupleTypeName} {tupleValue}, {index}");
        return result;
    }

    /// <summary>
    /// Generates code to write a member variable on an entity.
    /// </summary>
    private void EmitEntityMemberVariableWrite(StringBuilder sb, string entityPtr,
        EntityTypeInfo entity, string memberVariableName, string value,
        TypeInfo? valueType = null)
    {
        // Refresh stale generic resolutions
        entity = RefreshEntityMemberVariables(entity: entity,
            memberVariableName: memberVariableName);

        // Find member variable index
        int memberVariableIndex = -1;
        MemberVariableInfo? memberVariable = null;
        for (int i = 0; i < entity.MemberVariables.Count; i++)
        {
            if (entity.MemberVariables[index: i].Name == memberVariableName)
            {
                memberVariableIndex = i;
                memberVariable = entity.MemberVariables[index: i];
                break;
            }
        }

        if (memberVariableIndex < 0 || memberVariable == null)
        {
            throw new InvalidOperationException(
                message:
                $"Member variable '{memberVariableName}' not found on entity '{entity.Name}'");
        }

        string typeName = GetEntityTypeName(entity: entity);
        string memberVariableType = GetLlvmType(type: memberVariable.Type);

        // Auto-wrap non-Maybe values into Maybe when assigning to nullable member variables.
        // Skip wrapping if: value is already a Maybe type, or value is zeroinitializer (None literal).
        // Maybe { i1 present, T value }: insertvalue i1 1 at 0, T at 1 (entity and record T, since C118).
        if (IsMaybeType(type: memberVariable.Type) &&
            !(valueType != null && IsMaybeType(type: valueType)) && value != "zeroinitializer")
        {
            string memberCarrierType = GetLlvmType(type: memberVariable.Type);
            // All Maybe[T] types are { i1, ptr } (C118: entity T is also 2-field, not single-ptr).
            string innerLlvm = memberVariable.Type.TypeArguments is { Count: 1 }
                ? GetLlvmType(type: memberVariable.Type.TypeArguments[index: 0])
                : "ptr";
            string wrapped = NextTemp();
            EmitLine(sb: sb,
                line: $"  {wrapped} = insertvalue {memberCarrierType} zeroinitializer, i1 1, 0");
            string wrapped2 = NextTemp();
            EmitLine(sb: sb,
                line: $"  {wrapped2} = insertvalue {memberCarrierType} {wrapped}, {innerLlvm} {value}, 1");
            value = wrapped2;
        }

        // GEP to get member variable pointer
        string memberVariablePtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {memberVariablePtr} = getelementptr {typeName}, ptr {entityPtr}, i32 0, i32 {memberVariableIndex}");

        // Store the value
        EmitLine(sb: sb, line: $"  store {memberVariableType} {value}, ptr {memberVariablePtr}");
    }

    /// <summary>
    /// Checks if a type is a Maybe/nullable type (Maybe[T], or error handling Maybe).
    /// </summary>
    /// <summary>
    /// Resolves a TypeExpression to a TypeInfo, handling parameterized types
    /// like SortedDict[K, V] by recursively resolving inner type arguments
    /// via _typeSubstitutions during monomorphization.
    /// </summary>
    private TypeInfo? ResolveTypeArgument(TypeExpression ta)
    {
        // Direct substitution (e.g., T → S64)
        if (_typeSubstitutions != null &&
            _typeSubstitutions.TryGetValue(key: ta.Name, value: out TypeInfo? sub))
        {
            return sub;
        }

        // Const generic literal values (e.g., 4, 8u64) used in types like ValueList[S64, 4].
        if (TryParseConstGenericLiteral(name: ta.Name,
                value: out long constValue,
                explicitType: out string? explicitType))
        {
            return new ConstGenericValueTypeInfo(literalText: ta.Name,
                value: constValue,
                explicitTypeName: explicitType);
        }

        TypeInfo? presetConst = ResolvePresetConstGenericType(name: ta.Name);
        if (presetConst != null)
        {
            return presetConst;
        }

        TypeInfo? tupleType = ResolveTupleTypeExpression(typeExpr: ta);
        if (tupleType != null)
        {
            return tupleType;
        }

        // If the type argument itself has generic arguments (e.g., SortedDict[K, V]),
        // resolve those recursively and create the concrete type
        if (ta.GenericArguments is { Count: > 0 })
        {
            TypeInfo? baseType = _registry.LookupType(name: ta.Name);
            if (baseType != null)
            {
                var innerArgs = new List<TypeInfo>();
                foreach (TypeExpression innerTa in ta.GenericArguments)
                {
                    TypeInfo? innerResolved = ResolveTypeArgument(ta: innerTa);
                    if (innerResolved != null)
                    {
                        innerArgs.Add(item: innerResolved);
                    }
                }

                if (innerArgs.Count == (baseType.GenericParameters?.Count ?? 0))
                {
                    return _registry.GetOrCreateResolution(genericDef: baseType,
                        typeArguments: innerArgs);
                }
            }
        }

        // Try module-qualified lookup and type substitution values
        // (handles rewritten names like "SortedDict[S64, S64]" from GenericAstRewriter)
        TypeInfo? fromModule = LookupTypeInCurrentModule(name: ta.Name);
        if (fromModule != null)
        {
            return fromModule;
        }

        if (_typeSubstitutions != null)
        {
            foreach (TypeInfo sub2 in _typeSubstitutions.Values)
            {
                if (sub2.Name == ta.Name)
                {
                    return sub2;
                }
            }
        }

        return _registry.LookupType(name: ta.Name);
    }

    private TypeInfo? ResolveTupleTypeExpression(TypeExpression typeExpr)
    {
        if (typeExpr.Name is not "Tuple" and not "ValueTuple")
        {
            return null;
        }

        if (typeExpr.GenericArguments is not { Count: > 0 } elementTypeExprs)
        {
            return null;
        }

        var elementTypes = new List<TypeInfo>(capacity: elementTypeExprs.Count);
        foreach (TypeExpression elementTypeExpr in elementTypeExprs)
        {
            TypeInfo? elementType = ResolveTypeArgument(ta: elementTypeExpr);
            if (elementType == null)
            {
                return null;
            }

            elementTypes.Add(item: elementType);
        }

        return _registry.GetOrCreateTupleType(elementTypes: elementTypes);
    }

    private static bool TryParseConstGenericLiteral(string name, out long value,
        out string? explicitType)
    {
        explicitType = null;

        if (long.TryParse(s: name, result: out value))
        {
            return true;
        }

        (string Suffix, string TypeName)[] integerSuffixes =
        [
            ("u8", "U8"), ("u16", "U16"), ("u32", "U32"), ("u64", "U64"), ("u128", "U128"),
            ("s8", "S8"), ("s16", "S16"), ("s32", "S32"), ("s64", "S64"), ("s128", "S128")
        ];

        foreach ((string suffix, string typeName) in integerSuffixes)
        {
            if (name.EndsWith(value: suffix, comparisonType: StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(s: name[..^suffix.Length], result: out value))
            {
                explicitType = typeName;
                return true;
            }
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// Refreshes entity member variables for resolved generic types that may have stale or empty members.
    /// Tries the generic definition first, then falls back to registry lookup.
    /// </summary>
    /// <param name="entity">The entity type to refresh.</param>
    /// <param name="memberVariableName">The member variable name being probed.</param>
    private EntityTypeInfo RefreshEntityMemberVariables(EntityTypeInfo entity,
        string memberVariableName)
    {
        if (entity.MemberVariables.Any(predicate: mv => mv.Name == memberVariableName))
        {
            return entity;
        }

        if (TryRebuildEntityMembersFromAst(entity: entity) &&
            entity.MemberVariables.Any(predicate: mv => mv.Name == memberVariableName))
        {
            return entity;
        }

        // Non-generic entities can also be observed before pass 1c repopulates their member list.
        TypeInfo? directLookup = _registry.LookupType(name: entity.FullName) ??
                                 LookupTypeInCurrentModule(name: entity.FullName) ??
                                 _registry.LookupType(name: entity.Name) ??
                                 LookupTypeInCurrentModule(name: entity.Name);
        if (directLookup is EntityTypeInfo directEntity &&
            directEntity.MemberVariables.Any(predicate: mv => mv.Name == memberVariableName))
        {
            return directEntity;
        }

        if (!entity.IsGenericResolution || entity.TypeArguments == null)
        {
            return entity;
        }

        // Try GenericDefinition if available
        if (entity.GenericDefinition is { MemberVariables.Count: > 0 } genDef)
        {
            var refreshed =
                genDef.CreateInstance(typeArguments: entity.TypeArguments) as EntityTypeInfo;
            if (refreshed != null &&
                refreshed.MemberVariables.Any(predicate: mv => mv.Name == memberVariableName))
            {
                return refreshed;
            }
        }

        // Fallback: look up the generic definition from the registry
        string baseName = GetGenericBaseName(type: entity) ?? entity.Name;
        var lookupDef = LookupTypeInCurrentModule(name: baseName) as EntityTypeInfo;
        if (lookupDef is { IsGenericDefinition: true, MemberVariables.Count: > 0 })
        {
            var refreshed =
                lookupDef.CreateInstance(typeArguments: entity.TypeArguments) as EntityTypeInfo;
            if (refreshed != null &&
                refreshed.MemberVariables.Any(predicate: mv => mv.Name == memberVariableName))
            {
                return refreshed;
            }
        }

        return entity;
    }

    private bool TryRebuildEntityMembersFromAst(EntityTypeInfo entity)
    {
        foreach ((Program program, _, string module) in _userPrograms.Concat(_stdlibPrograms))
        {
            if (!string.IsNullOrEmpty(entity.Module) &&
                !string.Equals(a: module, b: entity.Module, comparisonType: StringComparison.Ordinal))
            {
                continue;
            }

            EntityDeclaration? decl = program.Declarations
                .OfType<EntityDeclaration>()
                .FirstOrDefault(predicate: d => d.Name == entity.Name);
            if (decl == null)
            {
                continue;
            }

            var rebuilt = new List<MemberVariableInfo>();
            int index = 0;
            foreach (VariableDeclaration member in decl.Members.OfType<VariableDeclaration>())
            {
                if (member.Type == null)
                {
                    continue;
                }

                TypeInfo? memberType = ResolveEntityMemberTypeFromAst(typeExpr: member.Type,
                    moduleName: module,
                    genericParams: decl.GenericParameters);
                if (memberType == null)
                {
                    continue;
                }

                rebuilt.Add(item: new MemberVariableInfo(name: member.Name, type: memberType)
                {
                    Visibility = member.Visibility,
                    Index = index++,
                    HasDefaultValue = member.Initializer != null,
                    Location = member.Location,
                    Owner = entity
                });
            }

            if (rebuilt.Count > 0)
            {
                entity.MemberVariables = rebuilt;
                return true;
            }
        }

        return false;
    }

    private TypeInfo? ResolveEntityMemberTypeFromAst(TypeExpression typeExpr, string? moduleName,
        IReadOnlyList<string>? genericParams)
    {
        if (genericParams != null && genericParams.Any(predicate: gp => gp == typeExpr.Name))
        {
            return new GenericParameterTypeInfo(name: typeExpr.Name);
        }

        if (typeExpr.Name is "Tuple" or "ValueTuple" &&
            typeExpr.GenericArguments is { Count: > 0 } tupleArgs)
        {
            var elementTypes = new List<TypeInfo>(capacity: tupleArgs.Count);
            foreach (TypeExpression tupleArg in tupleArgs)
            {
                TypeInfo? elementType = ResolveEntityMemberTypeFromAst(typeExpr: tupleArg,
                    moduleName: moduleName,
                    genericParams: genericParams);
                if (elementType == null)
                {
                    return null;
                }

                elementTypes.Add(item: elementType);
            }

            return _registry.GetOrCreateTupleType(elementTypes: elementTypes);
        }

        if (typeExpr.GenericArguments is { Count: > 0 } genericArgs)
        {
            if (genericArgs.Count == 1 &&
                typeExpr.Name is "Hijacked" or "Viewed" or "Grasped" or "Inspected" or
                    "Claimed" or "Retained" or "Shared" or "Tracked" or "Marked" or "Owned")
            {
                TypeInfo? innerType = ResolveEntityMemberTypeFromAst(typeExpr: genericArgs[index: 0],
                    moduleName: moduleName,
                    genericParams: genericParams);
                if (innerType == null)
                {
                    return null;
                }

                bool isReadOnly = typeExpr.Name is "Viewed" or "Inspected";
                return _registry.GetOrCreateWrapperType(wrapperName: typeExpr.Name,
                    innerType: innerType,
                    isReadOnly: isReadOnly);
            }

            TypeInfo? genericDef = _registry.LookupType(name: typeExpr.Name) ??
                                   (moduleName != null
                                       ? _registry.LookupType(name: $"{moduleName}.{typeExpr.Name}")
                                       : null);
            if (genericDef is { IsGenericDefinition: true, GenericParameters: { } genParams } &&
                genParams.Count == genericArgs.Count)
            {
                var typeArgs = new List<TypeInfo>(capacity: genericArgs.Count);
                foreach (TypeExpression genericArg in genericArgs)
                {
                    TypeInfo? resolvedArg = ResolveEntityMemberTypeFromAst(typeExpr: genericArg,
                        moduleName: moduleName,
                        genericParams: genericParams);
                    if (resolvedArg == null)
                    {
                        return null;
                    }

                    typeArgs.Add(item: resolvedArg);
                }

                return _registry.GetOrCreateResolution(genericDef: genericDef,
                    typeArguments: typeArgs);
            }
        }

        return _registry.LookupType(name: typeExpr.Name) ??
               (moduleName != null
                   ? _registry.LookupType(name: $"{moduleName}.{typeExpr.Name}")
                   : null);
    }
}
