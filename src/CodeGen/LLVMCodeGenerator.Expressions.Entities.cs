namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Enums;
using SemanticAnalysis.Symbols;
using SemanticAnalysis.Types;
using SyntaxTree;

/// <summary>
/// Expression code generation for entity construction and entity member operations.
/// </summary>
public partial class LLVMCodeGenerator
{
    private string EmitEntityAllocation(StringBuilder sb, EntityTypeInfo entity,
        List<string>? memberVariableValues = null)
    {
        string typeName = GetEntityTypeName(entity: entity);
        int size = CalculateEntitySize(entity: entity);

        // Allocate memory
        string rawPtr = NextTemp();
        EmitLine(sb: sb, line: $"  {rawPtr} = call ptr @rf_allocate_dynamic(i64 {size})");

        // Initialize member variables
        for (int i = 0; i < entity.MemberVariables.Count; i++)
        {
            MemberVariableInfo memberVariable = entity.MemberVariables[index: i];
            string memberVariableType = GetLLVMType(type: memberVariable.Type);

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
        // Look up the type (try module-qualified name for user types)
        TypeInfo? type = LookupTypeInCurrentModule(name: expr.TypeName);
        if (type == null)
        {
            throw new InvalidOperationException(
                message: $"Unknown type in constructor: {expr.TypeName}");
        }

        return type switch
        {
            EntityTypeInfo entity => EmitEntityConstruction(sb: sb, entity: entity, expr: expr),
            RecordTypeInfo record => EmitRecordConstruction(sb: sb, record: record, expr: expr),
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
                string targetLlvm = GetLLVMType(type: record);
                TypeInfo? argType = GetExpressionType(expr: expr.MemberVariables[index: 0].Value);
                string argLlvm = argType != null
                    ? GetLLVMType(type: argType)
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

        // Start with undef and insert each member variable
        string result = "undef";
        for (int i = 0; i < expr.MemberVariables.Count && i < record.MemberVariables.Count; i++)
        {
            string value = EmitExpression(sb: sb, expr: expr.MemberVariables[index: i].Value);
            string memberVariableType = GetLLVMType(type: record.MemberVariables[index: i].Type);

            string newResult = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {newResult} = insertvalue {typeName} {result}, {memberVariableType} {value}, {i}");
            result = newResult;
        }

        return result;
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
                string targetLlvm = GetLLVMType(type: record);
                TypeInfo? argType = GetExpressionType(expr: arguments[index: 0]);
                string argLlvm = argType != null
                    ? GetLLVMType(type: argType)
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
        string result = "undef";
        for (int i = 0; i < arguments.Count && i < record.MemberVariables.Count; i++)
        {
            // Unwrap NamedArgumentExpression if present
            Expression arg = arguments[index: i] is NamedArgumentExpression named
                ? named.Value
                : arguments[index: i];
            string value = EmitExpression(sb: sb, expr: arg);
            string memberVariableType = GetLLVMType(type: record.MemberVariables[index: i].Type);

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
            string fieldType = GetLLVMType(type: entity.MemberVariables[index: i].Type);
            string fieldPtr = NextTemp();
            EmitLine(sb: sb,
                line: $"  {fieldPtr} = getelementptr {typeName}, ptr {entityPtr}, i32 0, i32 {i}");
            EmitLine(sb: sb, line: $"  store {fieldType} {value}, ptr {fieldPtr}");
        }

        return entityPtr;
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
        // Handle force-unwrap suffix: me.root! → load me.root (Maybe), then unwrap
        bool isForceUnwrap = expr.PropertyName.EndsWith(value: '!');
        string propertyName = isForceUnwrap
            ? expr.PropertyName[..^1]
            : expr.PropertyName;

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

        // Wrapper type forwarding: Viewed[T], Hijacked[T], etc.
        // These are records wrapping a Snatched[T] (ptr) — forward member access to the inner entity type
        if (targetType is RecordTypeInfo wrapperRecord &&
            GetGenericBaseName(type: wrapperRecord) is { } wrapBaseName &&
            _wrapperTypeNames.Contains(item: wrapBaseName) &&
            wrapperRecord.TypeArguments is { Count: > 0 } &&
            wrapperRecord.TypeArguments[index: 0] is EntityTypeInfo innerEntity &&
            !wrapperRecord.MemberVariables.Any(predicate: mv => mv.Name == propertyName))
        {
            // For @llvm("ptr") wrappers, the value IS the pointer directly
            // For struct wrappers, extract the inner Snatched[T] (ptr) from field 0
            string innerPtr;
            if (wrapperRecord.HasDirectBackendType)
            {
                innerPtr = target;
            }
            else
            {
                string recordTypeName = GetRecordTypeName(record: wrapperRecord);
                innerPtr = NextTemp();
                EmitLine(sb: sb,
                    line: $"  {innerPtr} = extractvalue {recordTypeName} {target}, 0");
            }

            string result = EmitEntityMemberVariableRead(sb: sb,
                entityPtr: innerPtr,
                entity: innerEntity,
                memberVariableName: propertyName);
            return isForceUnwrap
                ? EmitMemberForceUnwrap(sb: sb,
                    maybeValue: result,
                    ownerType: targetType,
                    memberName: propertyName)
                : result;
        }

        string value = targetType switch
        {
            EntityTypeInfo entity => EmitEntityMemberVariableRead(sb: sb,
                entityPtr: target,
                entity: entity,
                memberVariableName: propertyName),
            RecordTypeInfo record => EmitRecordMemberVariableRead(sb: sb,
                recordValue: target,
                record: record,
                memberVariableName: propertyName),
            _ => throw new InvalidOperationException(
                message: $"Cannot access member variable on type: {targetType.Category}")
        };

        return isForceUnwrap
            ? EmitMemberForceUnwrap(sb: sb,
                maybeValue: value,
                ownerType: targetType,
                memberName: propertyName)
            : value;
    }

    /// <summary>
    /// Force-unwraps a Maybe value from a member variable access (me.field!).
    /// The value is a { i64, ptr } where tag=0 is None, tag=1 is valid.
    /// </summary>
    private string EmitMemberForceUnwrap(StringBuilder sb, string maybeValue, TypeInfo ownerType,
        string memberName)
    {
        // Find the member variable type to determine the unwrapped type
        MemberVariableInfo? memberVar = ownerType switch
        {
            EntityTypeInfo e => e.LookupMemberVariable(memberVariableName: memberName),
            RecordTypeInfo r => r.LookupMemberVariable(memberVariableName: memberName),
            _ => null
        };

        TypeInfo? memberType = memberVar?.Type;
        if (memberType != null && ownerType is
                { IsGenericResolution: true, TypeArguments: not null })
        {
            memberType = ResolveGenericMemberType(memberType: memberType, ownerType: ownerType);
        }

        // Determine the wrapper LLVM type (Maybe[T]) and inner value type
        string wrapperLlvmType = memberType != null
            ? GetLLVMType(type: memberType)
            : "{ i1, ptr }";
        TypeInfo? valueType = memberType switch
        {
            ErrorHandlingTypeInfo eh => eh.ValueType,
            RecordTypeInfo r when GetGenericBaseName(type: r) == "Maybe" &&
                                  r.TypeArguments is { Count: > 0 } => r.TypeArguments[index: 0],
            _ => null
        };

        // Declare llvm.trap if not already declared
        if (_declaredNativeFunctions.Add(item: "llvm.trap"))
        {
            EmitLine(sb: _functionDeclarations,
                line: "declare void @llvm.trap() noreturn nounwind");
        }

        // Alloca and store the Maybe value using its proper LLVM type
        string allocaPtr = NextTemp();
        EmitEntryAlloca(llvmName: allocaPtr, llvmType: wrapperLlvmType);
        EmitLine(sb: sb, line: $"  store {wrapperLlvmType} {maybeValue}, ptr {allocaPtr}");

        // Extract tag (i1 for Maybe)
        string tagPtr = NextTemp();
        string tag = NextTemp();
        EmitLine(sb: sb,
            line: $"  {tagPtr} = getelementptr {wrapperLlvmType}, ptr {allocaPtr}, i32 0, i32 0");
        EmitLine(sb: sb, line: $"  {tag} = load i1, ptr {tagPtr}");

        string isValid = NextTemp();
        EmitLine(sb: sb, line: $"  {isValid} = icmp eq i1 {tag}, 1");

        string okLabel = NextLabel(prefix: "unwrap_ok");
        string failLabel = NextLabel(prefix: "unwrap_fail");
        EmitLine(sb: sb, line: $"  br i1 {isValid}, label %{okLabel}, label %{failLabel}");

        // Fail: trap
        EmitLine(sb: sb, line: $"{failLabel}:");
        _currentBlock = failLabel;
        EmitLine(sb: sb, line: "  call void @llvm.trap()");
        EmitLine(sb: sb, line: "  unreachable");

        // OK: extract value from field 1
        EmitLine(sb: sb, line: $"{okLabel}:");
        _currentBlock = okLabel;
        string handlePtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {handlePtr} = getelementptr {wrapperLlvmType}, ptr {allocaPtr}, i32 0, i32 1");

        // Entity: field 1 is ptr — return it directly
        if (valueType is EntityTypeInfo)
        {
            string entityPtr = NextTemp();
            EmitLine(sb: sb, line: $"  {entityPtr} = load ptr, ptr {handlePtr}");
            return entityPtr;
        }

        // Record: field 1 is inline T — load from handlePtr
        string llvmValueType = valueType != null
            ? GetLLVMType(type: valueType)
            : "ptr";
        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = load {llvmValueType}, ptr {handlePtr}");
        return result;
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
            throw new InvalidOperationException(
                message:
                $"Member variable '{memberVariableName}' not found on entity '{entity.Name}'");
        }

        string typeName = GetEntityTypeName(entity: entity);
        string memberVariableType = GetLLVMType(type: memberVariable.Type);

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
    /// Generates code to read a member variable from a record (value type).
    /// Uses extractvalue instruction.
    /// </summary>
    private string EmitRecordMemberVariableRead(StringBuilder sb, string recordValue,
        RecordTypeInfo record, string memberVariableName)
    {
        // Snatched[T] (@llvm("ptr")): .address → ptrtoint ptr to i64
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
                $"Member variable '{memberVariableName}' not found on record '{record.Name}'");
        }

        string typeName = GetRecordTypeName(record: record);
        string memberVariableType = GetLLVMType(type: memberVariable.Type);

        // Extract the member variable value
        string value = NextTemp();
        EmitLine(sb: sb,
            line: $"  {value} = extractvalue {typeName} {recordValue}, {memberVariableIndex}");

        return value;
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
        string memberVariableType = GetLLVMType(type: memberVariable.Type);

        // Auto-wrap non-Maybe values into Maybe when assigning to nullable member variables.
        // Skip wrapping if: value is already a Maybe type, or value is zeroinitializer (None literal).
        if (IsMaybeType(type: memberVariable.Type) &&
            !(valueType != null && IsMaybeType(type: valueType)) && value != "zeroinitializer")
        {
            string memberCarrierType = GetLLVMType(type: memberVariable.Type);
            string wrapped = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {wrapped} = insertvalue {memberCarrierType} undef, i1 1, 0");
            string wrapped2 = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {wrapped2} = insertvalue {memberCarrierType} {wrapped}, ptr {value}, 1");
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

    private static bool IsMaybeType(TypeInfo type)
    {
        return type is ErrorHandlingTypeInfo { Kind: ErrorHandlingKind.Maybe } ||
               type is RecordTypeInfo r && GetGenericBaseName(type: r) == "Maybe";
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
        if (!entity.IsGenericResolution || entity.TypeArguments == null)
        {
            return entity;
        }

        if (entity.MemberVariables.Any(predicate: mv => mv.Name == memberVariableName))
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
}
