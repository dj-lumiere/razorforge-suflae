namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Enums;
using SemanticAnalysis.Symbols;
using SemanticAnalysis.Types;
using SyntaxTree;

/// <summary>
/// Expression code generation: allocation, member variable access, method calls, operators.
/// </summary>
public partial class LLVMCodeGenerator
{
    #region Entity Allocation

    /// <summary>
    /// Generates code to allocate a new entity instance.
    /// Entity allocation:
    /// 1. Call rf_alloc(size) to get heap memory
    /// 2. Initialize all member variables to zero/default values
    /// 3. Return pointer to the entity
    /// </summary>
    /// <param name="sb">StringBuilder to emit code to.</param>
    /// <param name="entity">The entity type to allocate.</param>
    /// <param name="memberVariableValues">Optional field initializer values (in member variable order).</param>
    /// <returns>The temporary variable holding the entity pointer.</returns>
    private string EmitEntityAllocation(StringBuilder sb, EntityTypeInfo entity, List<string>? memberVariableValues = null)
    {
        string typeName = GetEntityTypeName(entity);
        int size = CalculateEntitySize(entity);

        // Allocate memory
        string rawPtr = NextTemp();
        EmitLine(sb, $"  {rawPtr} = call ptr @rf_allocate_dynamic(i64 {size})");

        // Initialize member variables
        for (int i = 0; i < entity.MemberVariables.Count; i++)
        {
            var memberVariable = entity.MemberVariables[i];
            string memberVariableType = GetLLVMType(memberVariable.Type);

            // Get member variable pointer using GEP
            string memberVariablePtr = NextTemp();
            EmitLine(sb, $"  {memberVariablePtr} = getelementptr {typeName}, ptr {rawPtr}, i32 0, i32 {i}");

            // Get value to store
            string value;
            if (memberVariableValues != null && i < memberVariableValues.Count)
            {
                value = memberVariableValues[i];
            }
            else
            {
                value = GetZeroValue(memberVariable.Type);
            }

            // Store the value
            EmitLine(sb, $"  store {memberVariableType} {value}, ptr {memberVariablePtr}");
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
        TypeInfo? type = LookupTypeInCurrentModule(expr.TypeName);
        if (type == null)
        {
            throw new InvalidOperationException($"Unknown type in constructor: {expr.TypeName}");
        }

        return type switch
        {
            EntityTypeInfo entity => EmitEntityConstruction(sb, entity, expr),
            RecordTypeInfo record => EmitRecordConstruction(sb, record, expr),
            _ => throw new InvalidOperationException($"Cannot construct type: {type.Category}")
        };
    }

    /// <summary>
    /// Generates code to construct an entity with member variable values.
    /// </summary>
    private string EmitEntityConstruction(StringBuilder sb, EntityTypeInfo entity, CreatorExpression expr)
    {
        // Evaluate all member variable value expressions first
        var memberVariableValues = new List<string>();
        foreach (var (_, fieldExpr) in expr.MemberVariables)
        {
            string value = EmitExpression(sb, fieldExpr);
            memberVariableValues.Add(value);
        }

        // Allocate and initialize
        return EmitEntityAllocation(sb, entity, memberVariableValues);
    }

    /// <summary>
    /// Generates code to construct a record (value type).
    /// </summary>
    private string EmitRecordConstruction(StringBuilder sb, RecordTypeInfo record, CreatorExpression expr)
    {
        // Backend-annotated or single-member-variable wrapper: just return the inner value
        if ((record.HasDirectBackendType || record.IsSingleMemberVariableWrapper) && expr.MemberVariables.Count <= 1)
        {
            string argValue = EmitExpression(sb, expr.MemberVariables[0].Value);
            if (record.HasDirectBackendType)
            {
                string targetLlvm = GetLLVMType(record);
                TypeInfo? argType = GetExpressionType(expr.MemberVariables[0].Value);
                string argLlvm = argType != null ? GetLLVMType(argType) : targetLlvm;
                if (argLlvm != targetLlvm)
                {
                    string cast = NextTemp();
                    if (targetLlvm == "ptr" && argLlvm != "ptr")
                        EmitLine(sb, $"  {cast} = inttoptr {argLlvm} {argValue} to ptr");
                    else if (targetLlvm != "ptr" && argLlvm == "ptr")
                        EmitLine(sb, $"  {cast} = ptrtoint ptr {argValue} to {targetLlvm}");
                    else
                        EmitLine(sb, $"  {cast} = bitcast {argLlvm} {argValue} to {targetLlvm}");
                    return cast;
                }
            }
            return argValue;
        }

        // Multi-member-variable record: build struct value
        string typeName = GetRecordTypeName(record);

        // Start with undef and insert each member variable
        string result = "undef";
        for (int i = 0; i < expr.MemberVariables.Count && i < record.MemberVariables.Count; i++)
        {
            string value = EmitExpression(sb, expr.MemberVariables[i].Value);
            string memberVariableType = GetLLVMType(record.MemberVariables[i].Type);

            string newResult = NextTemp();
            EmitLine(sb, $"  {newResult} = insertvalue {typeName} {result}, {memberVariableType} {value}, {i}");
            result = newResult;
        }

        return result;
    }

    /// <summary>
    /// Constructs a record from a list of positional arguments (for TypeName(args...) calls).
    /// </summary>
    private string EmitRecordConstruction(StringBuilder sb, RecordTypeInfo record, List<Expression> arguments)
    {
        // Backend-annotated or single-member-variable wrapper: just return the inner value
        if ((record.HasDirectBackendType || record.IsSingleMemberVariableWrapper) && arguments.Count <= 1)
        {
            string argValue = EmitExpression(sb, arguments[0]);
            if (record.HasDirectBackendType)
            {
                string targetLlvm = GetLLVMType(record);
                TypeInfo? argType = GetExpressionType(arguments[0]);
                string argLlvm = argType != null ? GetLLVMType(argType) : targetLlvm;
                if (argLlvm != targetLlvm)
                {
                    string cast = NextTemp();
                    if (targetLlvm == "ptr" && argLlvm != "ptr")
                        EmitLine(sb, $"  {cast} = inttoptr {argLlvm} {argValue} to ptr");
                    else if (targetLlvm != "ptr" && argLlvm == "ptr")
                        EmitLine(sb, $"  {cast} = ptrtoint ptr {argValue} to {targetLlvm}");
                    else
                        EmitLine(sb, $"  {cast} = bitcast {argLlvm} {argValue} to {targetLlvm}");
                    return cast;
                }
            }
            return argValue;
        }

        // Multi-member-variable record: build struct value
        string typeName = GetRecordTypeName(record);
        string result = "undef";
        for (int i = 0; i < arguments.Count && i < record.MemberVariables.Count; i++)
        {
            // Unwrap NamedArgumentExpression if present
            var arg = arguments[i] is NamedArgumentExpression named ? named.Value : arguments[i];
            string value = EmitExpression(sb, arg);
            string memberVariableType = GetLLVMType(record.MemberVariables[i].Type);

            string newResult = NextTemp();
            EmitLine(sb, $"  {newResult} = insertvalue {typeName} {result}, {memberVariableType} {value}, {i}");
            result = newResult;
        }
        return result;
    }

    /// <summary>
    /// Emits entity construction: heap-allocate and initialize fields.
    /// </summary>
    private string EmitEntityConstruction(StringBuilder sb, EntityTypeInfo entity, List<Expression> arguments)
    {
        string typeName = GetEntityTypeName(entity);
        // Allocate entity on heap
        string sizeTemp = NextTemp();
        EmitLine(sb, $"  {sizeTemp} = getelementptr {typeName}, ptr null, i32 1");
        string size = NextTemp();
        EmitLine(sb, $"  {size} = ptrtoint ptr {sizeTemp} to i64");
        string entityPtr = NextTemp();
        EmitLine(sb, $"  {entityPtr} = call ptr @rf_allocate_dynamic(i64 {size})");

        // Initialize fields
        for (int i = 0; i < arguments.Count && i < entity.MemberVariables.Count; i++)
        {
            var arg = arguments[i] is NamedArgumentExpression named ? named.Value : arguments[i];
            string value = EmitExpression(sb, arg);
            string fieldType = GetLLVMType(entity.MemberVariables[i].Type);
            string fieldPtr = NextTemp();
            EmitLine(sb, $"  {fieldPtr} = getelementptr {typeName}, ptr {entityPtr}, i32 0, i32 {i}");
            EmitLine(sb, $"  store {fieldType} {value}, ptr {fieldPtr}");
        }

        return entityPtr;
    }

    #endregion

    #region Field Access

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
        bool isForceUnwrap = expr.PropertyName.EndsWith('!');
        string propertyName = isForceUnwrap ? expr.PropertyName[..^1] : expr.PropertyName;

        // Choice/Flags member access: emit constant value directly (no target expression to evaluate)
        TypeInfo? earlyObjectType = GetExpressionType(expr.Object);
        // Fallback: if SA didn't set ResolvedType (type-as-identifier), try type lookup by name
        if (earlyObjectType == null && expr.Object is IdentifierExpression typeId)
            earlyObjectType = LookupTypeInCurrentModule(typeId.Name);
        if (earlyObjectType is ChoiceTypeInfo choice)
        {
            ChoiceCaseInfo? caseInfo = choice.Cases.FirstOrDefault(c => c.Name == propertyName);
            if (caseInfo != null)
                return caseInfo.ComputedValue.ToString();
        }
        if (earlyObjectType is FlagsTypeInfo flags)
        {
            FlagsMemberInfo? memberInfo = flags.Members.FirstOrDefault(m => m.Name == propertyName);
            if (memberInfo != null)
                return (1L << memberInfo.BitPosition).ToString();
        }

        // Evaluate the target expression
        string target = EmitExpression(sb, expr.Object);

        // Get the target type
        TypeInfo? targetType = GetExpressionType(expr.Object);
        if (targetType == null)
        {
            throw new InvalidOperationException("Cannot determine type of member variable access target");
        }

        // Wrapper type forwarding: Viewed[T], Hijacked[T], etc.
        // These are records wrapping a Snatched[T] (ptr) — forward member access to the inner entity type
        if (targetType is RecordTypeInfo wrapperRecord
            && wrapperRecord.Name.Contains('[')
            && _wrapperTypeNames.Contains(wrapperRecord.Name[..wrapperRecord.Name.IndexOf('[')])
            && wrapperRecord.TypeArguments is { Count: > 0 }
            && wrapperRecord.TypeArguments[0] is EntityTypeInfo innerEntity
            && !wrapperRecord.MemberVariables.Any(mv => mv.Name == propertyName))
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
                string recordTypeName = GetRecordTypeName(wrapperRecord);
                innerPtr = NextTemp();
                EmitLine(sb, $"  {innerPtr} = extractvalue {recordTypeName} {target}, 0");
            }
            string result = EmitEntityMemberVariableRead(sb, innerPtr, innerEntity, propertyName);
            return isForceUnwrap ? EmitMemberForceUnwrap(sb, result, targetType, propertyName) : result;
        }

        string value = targetType switch
        {
            EntityTypeInfo entity => EmitEntityMemberVariableRead(sb, target, entity, propertyName),
            RecordTypeInfo record => EmitRecordMemberVariableRead(sb, target, record, propertyName),
            _ => throw new InvalidOperationException($"Cannot access member variable on type: {targetType.Category}")
        };

        return isForceUnwrap ? EmitMemberForceUnwrap(sb, value, targetType, propertyName) : value;
    }

    /// <summary>
    /// Force-unwraps a Maybe value from a member variable access (me.field!).
    /// The value is a { i64, ptr } where tag=0 is None, tag=1 is valid.
    /// </summary>
    private string EmitMemberForceUnwrap(StringBuilder sb, string maybeValue, TypeInfo ownerType, string memberName)
    {
        // Find the member variable type to determine the unwrapped type
        MemberVariableInfo? memberVar = ownerType switch
        {
            EntityTypeInfo e => e.LookupMemberVariable(memberName),
            RecordTypeInfo r => r.LookupMemberVariable(memberName),
            _ => null
        };

        TypeInfo? memberType = memberVar?.Type;
        if (memberType != null && ownerType is { IsGenericResolution: true, TypeArguments: not null })
            memberType = ResolveGenericMemberType(memberType, ownerType);

        // Determine the wrapper LLVM type (Maybe[T]) and inner value type
        string wrapperLlvmType = memberType != null ? GetLLVMType(memberType) : "{ i64, ptr }";
        TypeInfo? valueType = memberType switch
        {
            ErrorHandlingTypeInfo eh => eh.ValueType,
            RecordTypeInfo r when r.Name.StartsWith("Maybe[") && r.TypeArguments is { Count: > 0 } => r.TypeArguments[0],
            _ => null
        };

        // Declare llvm.trap if not already declared
        if (_declaredNativeFunctions.Add("llvm.trap"))
            EmitLine(_functionDeclarations, "declare void @llvm.trap() noreturn nounwind");

        // Alloca and store the Maybe value using its proper LLVM type
        string allocaPtr = NextTemp();
        EmitLine(sb, $"  {allocaPtr} = alloca {wrapperLlvmType}");
        EmitLine(sb, $"  store {wrapperLlvmType} {maybeValue}, ptr {allocaPtr}");

        // Extract tag
        string tagPtr = NextTemp();
        string tag = NextTemp();
        EmitLine(sb, $"  {tagPtr} = getelementptr {wrapperLlvmType}, ptr {allocaPtr}, i32 0, i32 0");
        EmitLine(sb, $"  {tag} = load i64, ptr {tagPtr}");

        string isValid = NextTemp();
        EmitLine(sb, $"  {isValid} = icmp eq i64 {tag}, 1");

        string okLabel = NextLabel("unwrap_ok");
        string failLabel = NextLabel("unwrap_fail");
        EmitLine(sb, $"  br i1 {isValid}, label %{okLabel}, label %{failLabel}");

        // Fail: trap
        EmitLine(sb, $"{failLabel}:");
        _currentBlock = failLabel;
        EmitLine(sb, "  call void @llvm.trap()");
        EmitLine(sb, "  unreachable");

        // OK: extract value from handle
        EmitLine(sb, $"{okLabel}:");
        _currentBlock = okLabel;
        string handlePtr = NextTemp();
        string handleVal = NextTemp();
        EmitLine(sb, $"  {handlePtr} = getelementptr {wrapperLlvmType}, ptr {allocaPtr}, i32 0, i32 1");
        EmitLine(sb, $"  {handleVal} = load ptr, ptr {handlePtr}");

        // For entity types, the value IS the pointer; for records, load from the pointer
        if (valueType is EntityTypeInfo)
        {
            return handleVal;
        }

        string llvmValueType = valueType != null ? GetLLVMType(valueType) : "ptr";
        string result = NextTemp();
        EmitLine(sb, $"  {result} = load {llvmValueType}, ptr {handleVal}");
        return result;
    }

    /// <summary>
    /// Generates code to read a member variable from an entity (pointer type).
    /// Uses GEP to get member variable address, then load.
    /// </summary>
    private string EmitEntityMemberVariableRead(StringBuilder sb, string entityPtr, EntityTypeInfo entity, string memberVariableName)
    {
        // Refresh stale generic resolutions (member variables may be empty or missing the target member)
        entity = RefreshEntityMemberVariables(entity, memberVariableName);

        // Ensure entity type struct definition exists in LLVM IR
        GenerateEntityType(entity);

        // Find member variable index
        int memberVariableIndex = -1;
        MemberVariableInfo? memberVariable = null;
        for (int i = 0; i < entity.MemberVariables.Count; i++)
        {
            if (entity.MemberVariables[i].Name == memberVariableName)
            {
                memberVariableIndex = i;
                memberVariable = entity.MemberVariables[i];
                break;
            }
        }

        if (memberVariableIndex < 0 || memberVariable == null)
        {
            throw new InvalidOperationException($"Member variable '{memberVariableName}' not found on entity '{entity.Name}'");
        }

        string typeName = GetEntityTypeName(entity);
        string memberVariableType = GetLLVMType(memberVariable.Type);

        // GEP to get member variable pointer
        string memberVariablePtr = NextTemp();
        EmitLine(sb, $"  {memberVariablePtr} = getelementptr {typeName}, ptr {entityPtr}, i32 0, i32 {memberVariableIndex}");

        // Load the member variable value
        string value = NextTemp();
        EmitLine(sb, $"  {value} = load {memberVariableType}, ptr {memberVariablePtr}");

        return value;
    }

    /// <summary>
    /// Generates code to read a member variable from a record (value type).
    /// Uses extractvalue instruction.
    /// </summary>
    private string EmitRecordMemberVariableRead(StringBuilder sb, string recordValue, RecordTypeInfo record, string memberVariableName)
    {
        // Snatched[T] (@llvm("ptr")): .address → ptrtoint ptr to i64
        if (record is { HasDirectBackendType: true, LlvmType: "ptr" } && memberVariableName == "address")
        {
            string addr = NextTemp();
            EmitLine(sb, $"  {addr} = ptrtoint ptr {recordValue} to i64");
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
            if (record.MemberVariables[i].Name == memberVariableName)
            {
                memberVariableIndex = i;
                memberVariable = record.MemberVariables[i];
                break;
            }
        }

        if (memberVariableIndex < 0 || memberVariable == null)
        {
            throw new InvalidOperationException($"Member variable '{memberVariableName}' not found on record '{record.Name}'");
        }

        string typeName = GetRecordTypeName(record);
        string memberVariableType = GetLLVMType(memberVariable.Type);

        // Extract the member variable value
        string value = NextTemp();
        EmitLine(sb, $"  {value} = extractvalue {typeName} {recordValue}, {memberVariableIndex}");

        return value;
    }

    #endregion

    #region Field Write

    /// <summary>
    /// Generates code to write a member variable on an entity.
    /// </summary>
    private void EmitEntityMemberVariableWrite(StringBuilder sb, string entityPtr, EntityTypeInfo entity, string memberVariableName, string value, TypeInfo? valueType = null)
    {
        // Refresh stale generic resolutions
        entity = RefreshEntityMemberVariables(entity, memberVariableName);

        // Find member variable index
        int memberVariableIndex = -1;
        MemberVariableInfo? memberVariable = null;
        for (int i = 0; i < entity.MemberVariables.Count; i++)
        {
            if (entity.MemberVariables[i].Name == memberVariableName)
            {
                memberVariableIndex = i;
                memberVariable = entity.MemberVariables[i];
                break;
            }
        }

        if (memberVariableIndex < 0 || memberVariable == null)
        {
            throw new InvalidOperationException($"Member variable '{memberVariableName}' not found on entity '{entity.Name}'");
        }

        string typeName = GetEntityTypeName(entity);
        string memberVariableType = GetLLVMType(memberVariable.Type);

        // Auto-wrap non-Maybe values into Maybe when assigning to nullable member variables.
        // Skip wrapping if: value is already a Maybe type, or value is zeroinitializer (None literal).
        if (IsMaybeType(memberVariable.Type)
            && !(valueType != null && IsMaybeType(valueType))
            && value != "zeroinitializer")
        {
            string wrapped = NextTemp();
            EmitLine(sb, $"  {wrapped} = insertvalue {{ i64, ptr }} {{ i64 1, ptr null }}, ptr {value}, 1");
            value = wrapped;
        }

        // GEP to get member variable pointer
        string memberVariablePtr = NextTemp();
        EmitLine(sb, $"  {memberVariablePtr} = getelementptr {typeName}, ptr {entityPtr}, i32 0, i32 {memberVariableIndex}");

        // Store the value
        EmitLine(sb, $"  store {memberVariableType} {value}, ptr {memberVariablePtr}");
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
        if (_typeSubstitutions != null && _typeSubstitutions.TryGetValue(ta.Name, out var sub))
            return sub;

        // If the type argument itself has generic arguments (e.g., SortedDict[K, V]),
        // resolve those recursively and create the concrete type
        if (ta.GenericArguments is { Count: > 0 })
        {
            TypeInfo? baseType = _registry.LookupType(ta.Name);
            if (baseType != null)
            {
                var innerArgs = new List<TypeInfo>();
                foreach (var innerTa in ta.GenericArguments)
                {
                    TypeInfo? innerResolved = ResolveTypeArgument(innerTa);
                    if (innerResolved != null)
                        innerArgs.Add(innerResolved);
                }
                if (innerArgs.Count == (baseType.GenericParameters?.Count ?? 0))
                    return _registry.GetOrCreateResolution(baseType, innerArgs);
            }
        }

        // Try module-qualified lookup and type substitution values
        // (handles rewritten names like "SortedDict[S64, S64]" from GenericAstRewriter)
        var fromModule = LookupTypeInCurrentModule(ta.Name);
        if (fromModule != null) return fromModule;

        if (_typeSubstitutions != null)
        {
            foreach (var sub2 in _typeSubstitutions.Values)
            {
                if (sub2.Name == ta.Name) return sub2;
            }
        }

        return _registry.LookupType(ta.Name);
    }

    private static bool IsMaybeType(TypeInfo type)
    {
        return type is ErrorHandlingTypeInfo { Kind: ErrorHandlingKind.Maybe }
            || (type is RecordTypeInfo r && r.Name.StartsWith("Maybe["));
    }

    #endregion

    #region Expression Dispatch

    /// <summary>
    /// Main expression dispatch - generates code for any expression type.
    /// </summary>
    /// <param name="sb">StringBuilder to emit code to.</param>
    /// <summary>
    /// Refreshes entity member variables for resolved generic types that may have stale/empty members.
    /// Tries GenericDefinition first, then falls back to registry lookup.
    /// </summary>
    private EntityTypeInfo RefreshEntityMemberVariables(EntityTypeInfo entity, string memberVariableName)
    {
        if (!entity.IsGenericResolution || entity.TypeArguments == null)
            return entity;
        if (entity.MemberVariables.Any(mv => mv.Name == memberVariableName))
            return entity;

        // Try GenericDefinition if available
        if (entity.GenericDefinition is { MemberVariables.Count: > 0 } genDef)
        {
            var refreshed = genDef.CreateInstance(entity.TypeArguments) as EntityTypeInfo;
            if (refreshed != null && refreshed.MemberVariables.Any(mv => mv.Name == memberVariableName))
                return refreshed;
        }

        // Fallback: look up the generic definition from the registry
        string baseName = entity.Name.Contains('[') ? entity.Name[..entity.Name.IndexOf('[')] : entity.Name;
        var lookupDef = LookupTypeInCurrentModule(baseName) as EntityTypeInfo;
        if (lookupDef is { IsGenericDefinition: true, MemberVariables.Count: > 0 })
        {
            var refreshed = lookupDef.CreateInstance(entity.TypeArguments) as EntityTypeInfo;
            if (refreshed != null && refreshed.MemberVariables.Any(mv => mv.Name == memberVariableName))
                return refreshed;
        }

        return entity;
    }

    /// <param name="expr">The expression to generate code for.</param>
    /// <returns>The temporary variable holding the expression result.</returns>
    private string EmitExpression(StringBuilder sb, Expression expr)
    {
        return expr switch
        {
            LiteralExpression literal => EmitLiteral(sb, literal),
            IdentifierExpression identifier => EmitIdentifier(sb, identifier),
            MemberExpression memberAccess => EmitMemberVariableAccess(sb, memberAccess),
            OptionalMemberExpression optMember => EmitOptionalMemberAccess(sb, optMember),
            CreatorExpression constructor => EmitConstructorCall(sb, constructor),
            CallExpression call => EmitCall(sb, call),
            BinaryExpression binary => EmitBinaryOp(sb, binary),
            UnaryExpression unary => EmitUnaryOp(sb, unary),
            ConditionalExpression cond => EmitConditional(sb, cond),
            IndexExpression index => EmitIndexAccess(sb, index),
            SliceExpression slice => EmitSliceAccess(sb, slice),
            RangeExpression range => EmitRange(sb, range),
            StealExpression steal => EmitSteal(sb, steal),
            TupleLiteralExpression tuple => EmitTupleLiteral(sb, tuple),
            GenericMethodCallExpression generic => EmitGenericMethodCall(sb, generic),
            InsertedTextExpression inserted => EmitInsertedText(sb, inserted),
            ListLiteralExpression list => EmitListLiteral(sb, list),
            SetLiteralExpression set => EmitSetLiteral(sb, set),
            DictLiteralExpression dict => EmitDictLiteral(sb, dict),
            FlagsTestExpression flagsTest => EmitFlagsTest(sb, flagsTest),
            ChainedComparisonExpression chain => EmitChainedComparison(sb, chain),
            CompoundAssignmentExpression compound => EmitCompoundAssignment(sb, compound),
            IsPatternExpression isPattern => EmitIsPattern(sb, isPattern),
            NamedArgumentExpression named => EmitExpression(sb, named.Value),
            DictEntryLiteralExpression dictEntry => EmitDictEntryLiteral(sb, dictEntry),
            GenericMemberExpression gme => EmitGenericMemberExpression(sb, gme),
            _ => throw new NotImplementedException($"Expression type not implemented: {expr.GetType().Name}")
        };
    }

    /// <summary>
    /// Generates code for a literal expression.
    /// </summary>
    private string EmitLiteral(StringBuilder sb, LiteralExpression literal)
    {
        // Numeric literals are stored as strings by the parser (e.g., "1_s32", "3.14_f32").
        // Check LiteralType first to handle them as numbers, not string constants.
        if (literal.Value is char ch)
            return EmitLetterLiteral(sb, ch.ToString());
        if (literal.Value is string s)
        {
            if (IsIntegerLiteralType(literal.LiteralType))
                return StripNumericSuffix(s);
            if (IsFloatLiteralType(literal.LiteralType))
                return EmitFloatLiteral(StripNumericSuffix(s), literal.LiteralType);
            if (IsDecimalFloatLiteralType(literal.LiteralType))
                return EmitDecimalFloatLiteral(sb, StripNumericSuffix(s), literal.LiteralType);
            if (IsByteSizeLiteralType(literal.LiteralType))
                return EmitByteSizeLiteral(sb, s);
            if (literal.LiteralType == Lexer.TokenType.LetterLiteral)
                return EmitLetterLiteral(sb, s);
            if (literal.LiteralType == Lexer.TokenType.ByteLetterLiteral)
                return EmitByteLetterLiteral(sb, s);
            if (literal.LiteralType == Lexer.TokenType.BytesLiteral)
                return EmitBytesLiteral(sb, s);
            // Actual string literal
            return EmitStringLiteral(sb, s);
        }

        // None literal → emit zeroinitializer for Maybe types ({ i64, ptr } with tag=0)
        if (literal.LiteralType == Lexer.TokenType.None)
            return "zeroinitializer";

        return literal.Value switch
        {
            int i => i.ToString(),
            long l => l.ToString(),
            double d => $"0x{BitConverter.DoubleToInt64Bits(d):X16}",
            float f => $"0x{BitConverter.DoubleToInt64Bits((double)f):X16}",
            bool b => b ? "true" : "false",
            null => "null",
            _ => literal.Value.ToString() ?? "0"
        };
    }

    /// <summary>
    /// Checks if a token type represents an integer literal.
    /// </summary>
    private static bool IsIntegerLiteralType(Lexer.TokenType type)
    {
        return type is Lexer.TokenType.S8Literal or Lexer.TokenType.S16Literal
            or Lexer.TokenType.S32Literal or Lexer.TokenType.S64Literal
            or Lexer.TokenType.S128Literal
            or Lexer.TokenType.U8Literal or Lexer.TokenType.U16Literal
            or Lexer.TokenType.U32Literal or Lexer.TokenType.U64Literal
            or Lexer.TokenType.U128Literal or Lexer.TokenType.AddressLiteral;
    }

    /// <summary>
    /// Checks if a token type represents a floating-point literal.
    /// </summary>
    private static bool IsFloatLiteralType(Lexer.TokenType type)
    {
        return type is Lexer.TokenType.F16Literal or Lexer.TokenType.F32Literal
            or Lexer.TokenType.F64Literal or Lexer.TokenType.F128Literal;
    }

    private static bool IsDecimalFloatLiteralType(Lexer.TokenType type)
    {
        return type is Lexer.TokenType.D32Literal or Lexer.TokenType.D64Literal
            or Lexer.TokenType.D128Literal;
    }

    private static bool IsByteSizeLiteralType(Lexer.TokenType type)
    {
        return type is Lexer.TokenType.ByteLiteral or Lexer.TokenType.KilobyteLiteral
            or Lexer.TokenType.KibibyteLiteral or Lexer.TokenType.MegabyteLiteral
            or Lexer.TokenType.MebibyteLiteral or Lexer.TokenType.GigabyteLiteral
            or Lexer.TokenType.GibibyteLiteral;
    }

    private static readonly (string suffix, ulong multiplier)[] ByteSizeSuffixes =
    [
        ("gib", 1_073_741_824UL),
        ("mib", 1_048_576UL),
        ("kib", 1_024UL),
        ("gb", 1_000_000_000UL),
        ("mb", 1_000_000UL),
        ("kb", 1_000UL),
        ("b", 1UL)
    ];

    private string EmitByteSizeLiteral(StringBuilder sb, string text)
    {
        // Compute the byte value from the literal text + unit suffix.
        ulong bytes = 0;
        string lower = text.ToLowerInvariant();
        foreach (var (suffix, multiplier) in ByteSizeSuffixes)
        {
            if (lower.EndsWith(suffix))
            {
                string numPart = text[..^suffix.Length].TrimEnd('_').Replace("_", "");
                if (ulong.TryParse(numPart, out ulong value))
                    bytes = value * multiplier;
                break;
            }
        }

        // ByteSize is %Record.ByteSize = type { i64 } — construct the aggregate.
        TypeInfo? bsType = _registry.LookupType("ByteSize");
        string llvmType = bsType != null ? GetLLVMType(bsType) : "%Record.ByteSize";

        // If ByteSize resolves to a struct, use insertvalue; if it's a plain i64, return directly
        if (llvmType.StartsWith("%"))
        {
            string result = NextTemp();
            EmitLine(sb, $"  {result} = insertvalue {llvmType} undef, i64 {bytes}, 0");
            return result;
        }
        return bytes.ToString();
    }

    /// <summary>
    /// Emits a Letter literal as a %Record.Letter aggregate with the Unicode codepoint.
    /// </summary>
    private string EmitLetterLiteral(StringBuilder sb, string text)
    {
        int codepoint = text.Length > 0 ? char.ConvertToUtf32(text, 0) : 0;

        TypeInfo? letterType = _registry.LookupType("Letter");
        string llvmType = letterType != null ? GetLLVMType(letterType) : "%\"Record.Letter\"";

        if (llvmType.StartsWith("%"))
        {
            string result = NextTemp();
            EmitLine(sb, $"  {result} = insertvalue {llvmType} undef, i32 {codepoint}, 0");
            return result;
        }
        return codepoint.ToString();
    }

    private string EmitByteLetterLiteral(StringBuilder sb, string text)
    {
        int byteValue = text.Length > 0 ? (int)text[0] & 0xFF : 0;

        TypeInfo? byteType = _registry.LookupType("Byte");
        string llvmType = byteType != null ? GetLLVMType(byteType) : "%\"Record.Byte\"";

        if (llvmType.StartsWith("%"))
        {
            string result = NextTemp();
            EmitLine(sb, $"  {result} = insertvalue {llvmType} undef, i8 {byteValue}, 0");
            return result;
        }
        return byteValue.ToString();
    }

    /// <summary>
    /// Emits a Bytes literal (b"...") as a constant Bytes entity.
    /// Bytes is entity { letters: List[Byte] } where List is entity { data: ptr, count: U64, capacity: U64 }
    /// and Byte is an i8. Returns a pointer to the Bytes struct.
    /// </summary>
    private string EmitBytesLiteral(StringBuilder sb, string value)
    {
        int idx = _stringCounter++;
        string constName = $"@.bytes.{idx}";

        // Collect ASCII byte values
        var bytes = new List<int>();
        foreach (char c in value)
        {
            bytes.Add((int)c & 0xFF);
        }

        int count = bytes.Count;

        // Layer 1: raw byte data array [N x i8]
        string dataName = $"@.bytes.data.{idx}";
        string byteValues = string.Join(", ", bytes.Select(b => $"i8 {b}"));
        if (count > 0)
            EmitLine(_globalDeclarations, $"{dataName} = private unnamed_addr constant [{count} x i8] [{byteValues}]");
        else
            EmitLine(_globalDeclarations, $"{dataName} = private unnamed_addr constant [0 x i8] zeroinitializer");

        // Layer 2: List[Byte] struct { ptr data, i64 count, i64 capacity }
        string listName = $"@.bytes.list.{idx}";
        EmitLine(_globalDeclarations, $"{listName} = private unnamed_addr constant {{ ptr, i64, i64 }} {{ ptr {dataName}, i64 {count}, i64 {count} }}");

        // Layer 3: Bytes entity struct { ptr letters }
        EmitLine(_globalDeclarations, $"{constName} = private unnamed_addr constant {{ ptr }} {{ ptr {listName} }}");

        return constName;
    }

    /// <summary>
    /// Strips the type suffix from a numeric literal string (e.g., "1_s32" → "1", "3_14_f64" → "3_14")
    /// and removes digit separator underscores.
    /// </summary>
    /// <summary>
    /// Converts a prefixed literal (0x hex, 0b binary, 0o octal) to decimal for LLVM IR.
    /// Hex floats (containing '.' or 'p') are passed through for EmitFloatLiteral.
    /// </summary>
    private static string ConvertPrefixedToDecimal(string value)
    {
        if (value.Length > 2)
        {
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                // Don't convert hex floats — they go through EmitFloatLiteral
                if (value.IndexOfAny(['.', 'p', 'P'], 2) >= 0)
                    return value;
                if (ulong.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out ulong hexVal))
                    return hexVal.ToString();
            }
            else if (value.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                try { return Convert.ToUInt64(value[2..], 2).ToString(); }
                catch { /* fall through */ }
            }
            else if (value.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
            {
                try { return Convert.ToUInt64(value[2..], 8).ToString(); }
                catch { /* fall through */ }
            }
        }
        return value;
    }

    private static readonly string[] NumericSuffixes =
    [
        "addr", "s128", "u128", "s64", "u64", "s32", "u32",
        "s16", "u16", "s8", "u8", "f128", "f64", "f32", "f16",
        "d128", "d64", "d32"
    ];

    private static string StripNumericSuffix(string text)
    {
        // First try: underscore-separated suffix (e.g., "1_s32" → "1")
        for (int i = text.Length - 1; i >= 0; i--)
        {
            if (text[i] == '_' && i + 1 < text.Length && char.IsLetter(text[i + 1]))
            {
                return ConvertPrefixedToDecimal(text[..i].Replace("_", ""));
            }
        }

        // Second try: direct suffix without underscore (e.g., "0u64" → "0", "0x7Fu32" → "127")
        string lower = text.ToLowerInvariant();
        foreach (string suffix in NumericSuffixes)
        {
            if (lower.EndsWith(suffix))
            {
                string numPart = text[..^suffix.Length].Replace("_", "");
                return ConvertPrefixedToDecimal(numPart);
            }
        }

        // No suffix found — just remove underscores
        return ConvertPrefixedToDecimal(text.Replace("_", ""));
    }

    /// <summary>
    /// Emits a float literal in LLVM IR format.
    /// LLVM requires specific formats for different float types.
    /// </summary>
    private static string EmitFloatLiteral(string numericValue, Lexer.TokenType literalType)
    {
        // F128: use native parser for full 128-bit precision
        if (literalType == Lexer.TokenType.F128Literal)
        {
            var f128 = SemanticAnalysis.Native.NumericLiteralParser.ParseF128(numericValue);
            // LLVM fp128 hex format: 0xL<Lo16hex><Hi16hex> (low bits first)
            return $"0xL{f128.Lo:X16}{f128.Hi:X16}";
        }

        // Try hex float format first (0x1.ABCDp5)
        if (TryParseHexFloat(numericValue, out double hexFloatVal))
        {
            return EmitDoubleAsLlvmHex(hexFloatVal, literalType);
        }

        if (double.TryParse(numericValue, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double d))
        {
            return EmitDoubleAsLlvmHex(d, literalType);
        }
        return numericValue;
    }

    private static string EmitDoubleAsLlvmHex(double d, Lexer.TokenType literalType)
    {
        if (literalType == Lexer.TokenType.F32Literal)
        {
            // F32: promote to double for LLVM's float hex format
            float f = (float)d;
            long bits = BitConverter.DoubleToInt64Bits((double)f);
            return $"0x{bits:X16}";
        }
        else
        {
            long bits = BitConverter.DoubleToInt64Bits(d);
            return $"0x{bits:X16}";
        }
    }

    /// <summary>
    /// Parses C99 hex float format: 0x1.ABCDp5 = (hex mantissa) × 2^(exponent).
    /// </summary>
    private static bool TryParseHexFloat(string value, out double result)
    {
        result = 0;
        if (!value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || value.Length <= 2)
            return false;

        string body = value[2..];
        int pIndex = body.IndexOfAny(['p', 'P']);
        if (pIndex < 0) return false;

        string mantissaStr = body[..pIndex];
        string exponentStr = body[(pIndex + 1)..];

        if (!int.TryParse(exponentStr, out int exponent))
            return false;

        double mantissa = 0;
        int dotIndex = mantissaStr.IndexOf('.');

        if (dotIndex >= 0)
        {
            string intPart = mantissaStr[..dotIndex];
            string fracPart = mantissaStr[(dotIndex + 1)..];

            if (intPart.Length > 0 &&
                ulong.TryParse(intPart, System.Globalization.NumberStyles.HexNumber, null, out ulong intVal))
                mantissa = intVal;

            double scale = 1.0 / 16;
            foreach (char c in fracPart)
            {
                int digit = c switch
                {
                    >= '0' and <= '9' => c - '0',
                    >= 'a' and <= 'f' => c - 'a' + 10,
                    >= 'A' and <= 'F' => c - 'A' + 10,
                    _ => 0
                };
                mantissa += digit * scale;
                scale /= 16;
            }
        }
        else
        {
            if (!ulong.TryParse(mantissaStr, System.Globalization.NumberStyles.HexNumber, null, out ulong intVal))
                return false;
            mantissa = intVal;
        }

        result = Math.ScaleB(mantissa, exponent);
        return !double.IsNaN(result) && !double.IsInfinity(result);
    }

    /// <summary>
    /// Emits a decimal floating-point literal (D32, D64, D128) as raw integer bits.
    /// D32/D64 return scalar values. D128 emits insertvalue instructions and returns a temp.
    /// </summary>
    private string EmitDecimalFloatLiteral(StringBuilder sb, string numericValue, Lexer.TokenType literalType)
    {
        switch (literalType)
        {
            case Lexer.TokenType.D32Literal:
                return SemanticAnalysis.Native.NumericLiteralParser.ParseD32(numericValue).Value.ToString();
            case Lexer.TokenType.D64Literal:
                return SemanticAnalysis.Native.NumericLiteralParser.ParseD64(numericValue).Value.ToString();
            case Lexer.TokenType.D128Literal:
            {
                var d128 = SemanticAnalysis.Native.NumericLiteralParser.ParseD128(numericValue);
                string tmp1 = NextTemp();
                string tmp2 = NextTemp();
                EmitLine(sb, $"  {tmp1} = insertvalue %Record.D128 undef, i64 {d128.Lo}, 0");
                EmitLine(sb, $"  {tmp2} = insertvalue %Record.D128 {tmp1}, i64 {d128.Hi}, 1");
                return tmp2;
            }
            default:
                return numericValue;
        }
    }

    /// <summary>
    /// Generates code for a string literal.
    /// Emits a Text string literal as a UTF-32 constant.
    /// Text is entity { letters: List[Letter] } where List is entity { data: ptr, count: U64, capacity: U64 }
    /// and Letter is a U32 codepoint. Returns a pointer to the Text struct.
    /// </summary>
    private string EmitStringLiteral(StringBuilder sb, string value)
    {
        // Check if we've already emitted this string
        if (_stringConstants.TryGetValue(value, out string? existingName))
        {
            return existingName;
        }

        int idx = _stringCounter++;
        string constName = $"@.str.{idx}";
        _stringConstants[value] = constName;

        // Collect Unicode codepoints (UTF-32)
        var codepoints = new List<int>();
        foreach (var rune in value.EnumerateRunes())
        {
            codepoints.Add(rune.Value);
        }

        int count = codepoints.Count;

        // Layer 1: raw codepoint data array [N x i32]
        string dataName = $"@.str.data.{idx}";
        string cpValues = string.Join(", ", codepoints.Select(cp => $"i32 {cp}"));
        if (count > 0)
            EmitLine(_globalDeclarations, $"{dataName} = private unnamed_addr constant [{count} x i32] [{cpValues}]");
        else
            EmitLine(_globalDeclarations, $"{dataName} = private unnamed_addr constant [0 x i32] zeroinitializer");

        // Layer 2: List[Letter] struct { ptr data, i64 count, i64 capacity }
        string listName = $"@.str.list.{idx}";
        EmitLine(_globalDeclarations, $"{listName} = private unnamed_addr constant {{ ptr, i64, i64 }} {{ ptr {dataName}, i64 {count}, i64 {count} }}");

        // Layer 3: Text struct { ptr letters }
        EmitLine(_globalDeclarations, $"{constName} = private unnamed_addr constant {{ ptr }} {{ ptr {listName} }}");

        return constName;
    }

    /// <summary>
    /// Generates code for an identifier expression (variable reference).
    /// </summary>
    private string EmitIdentifier(StringBuilder sb, IdentifierExpression identifier)
    {
        // Check if this is a const generic value substituted during monomorphization
        // After GenericAstRewriter replaces N→"4", the identifier name is a numeric literal
        if (identifier.Name.Length > 0 && char.IsDigit(identifier.Name[0]))
        {
            // Parse the numeric value from the literal text (handles "4", "8u64", etc.)
            if (long.TryParse(identifier.Name, out _))
                return identifier.Name;
            // Try stripping type suffix for typed literals
            foreach (string suffix in new[] { "u64", "s64", "u32", "s32", "u16", "s16", "u8", "s8", "u128", "s128", "addr" })
            {
                if (identifier.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(identifier.Name[..^suffix.Length], out long val))
                    return val.ToString();
            }
        }

        // Check if this is a choice case (e.g., ME_SMALL, NORTH)
        var choiceCase = _registry.LookupChoiceCase(identifier.Name);
        if (choiceCase != null)
        {
            return choiceCase.Value.CaseInfo.ComputedValue.ToString();
        }

        // Check if this is a preset (module-level constant, e.g., SET_INITIAL_BUCKETS)
        var presetVar = _registry.LookupVariable(identifier.Name);
        if (presetVar is { IsPreset: true })
        {
            // Find the preset's value expression from stdlib/user programs
            foreach (var (program, _, _) in _stdlibPrograms.Concat(_userPrograms))
            {
                foreach (var decl in program.Declarations)
                {
                    if (decl is PresetDeclaration preset && preset.Name == identifier.Name)
                        return EmitExpression(sb, preset.Value);
                }
            }
        }

        // Look up the variable in local variables first
        if (!_localVariables.TryGetValue(identifier.Name, out var varType))
        {
            throw new InvalidOperationException($"Unknown identifier '{identifier.Name}'");
        }

        // Variables are stored in allocas (%name.addr), need to load them
        // Use unique LLVM name to handle shadowing
        string llvmName = _localVarLLVMNames.TryGetValue(identifier.Name, out var unique) ? unique : identifier.Name;
        string llvmType = GetLLVMType(varType);
        string tmp = NextTemp();
        EmitLine(sb, $"  {tmp} = load {llvmType}, ptr %{llvmName}.addr");
        return tmp;
    }

    /// <summary>
    /// Generates code for a function/method call.
    /// Handles both standalone function calls and method calls on objects.
    /// </summary>
    private string EmitCall(StringBuilder sb, CallExpression call)
    {
        // C29: Safety guard — semantic analyzer already errors on runtime dispatch in RF mode,
        // but if we somehow reach codegen with Runtime dispatch, trap instead of emitting bad code
        if (call.ResolvedDispatch == DispatchStrategy.Runtime)
        {
            EmitLine(sb, "  call void @llvm.trap()");
            EmitLine(sb, "  unreachable");
            return "undef";
        }

        // Collection literal constructor: List(1, 2, 3), Set(1, 2), Dict(1:2, 3:4), etc.
        if (call.IsCollectionLiteral && call.ResolvedType != null)
            return EmitCollectionLiteralConstructor(sb, call.ResolvedType, call.Arguments);

        // Intercept source location routines — emit constants from call site, no actual call
        if (call.Callee is IdentifierExpression { Name: var name } && IsSourceLocationRoutine(name))
            return EmitSourceLocationInline(sb, name, call.Location);

        return call.Callee switch
        {
            // Determine if this is a method call (callee is MemberExpression) or standalone function call
            MemberExpression member => EmitMethodCall(sb, member, call.Arguments),
            IdentifierExpression id => EmitFunctionCall(sb, id.Name, call.Arguments, call.ResolvedRoutine),
            _ => throw new NotImplementedException(
                $"Cannot emit call for callee type: {call.Callee.GetType().Name}")
        };
    }

    /// <summary>
    /// Returns true if the function name is a source location routine that should be inlined at call site.
    /// </summary>
    private static bool IsSourceLocationRoutine(string name) =>
        name is "source_file" or "source_line" or "source_column" or "source_routine" or "source_module"
            or "source_text" or "caller_file" or "caller_line" or "caller_routine";

    /// <summary>
    /// Emits a source location routine inline as a constant from the call site location.
    /// No actual function call is generated — the value is injected directly.
    /// </summary>
    private string EmitSourceLocationInline(StringBuilder sb, string routineName, SourceLocation location)
    {
        return routineName switch
        {
            "source_file" => EmitSynthesizedStringLiteral(location.FileName),
            "source_line" => $"{location.Line}",
            "source_column" => $"{location.Column}",
            "source_routine" => EmitSynthesizedStringLiteral(
                _currentEmittingRoutine?.Name ?? "<unknown>"),
            "source_module" => EmitSynthesizedStringLiteral(
                _currentEmittingRoutine?.OwnerType?.Module
                ?? _currentEmittingRoutine?.Module
                ?? "<unknown>"),
            "source_text" => EmitSynthesizedStringLiteral("<expr>"),
            "caller_file" => EmitSynthesizedStringLiteral(location.FileName),
            "caller_line" => $"{location.Line}",
            "caller_routine" => EmitSynthesizedStringLiteral(
                _currentEmittingRoutine?.Name ?? "<unknown>"),
            _ => "undef"
        };
    }

    /// <summary>
    /// Emits an inline primitive type conversion (trunc/zext/sext/fpcast) for @llvm types.
    /// Used for calls like U8(val), S32(val), F64(val), etc.
    /// </summary>
    private string EmitPrimitiveTypeConversion(StringBuilder sb, string targetTypeName, Expression arg, TypeInfo targetType)
    {
        string argValue = EmitExpression(sb, arg);
        TypeInfo? argType = GetExpressionType(arg);
        string targetLlvm = GetLLVMType(targetType);
        string sourceLlvm = argType != null ? GetLLVMType(argType) : targetLlvm;

        if (sourceLlvm == targetLlvm) return argValue;

        bool sourceIsFloat = sourceLlvm is "half" or "float" or "double" or "fp128";
        bool targetIsFloat = targetLlvm is "half" or "float" or "double" or "fp128";
        bool targetUnsigned = targetTypeName is "U8" or "U16" or "U32" or "U64" or "U128" or "Address";

        string cast = NextTemp();
        if (sourceIsFloat && targetIsFloat)
        {
            string op = GetTypeBitWidth(sourceLlvm) > GetTypeBitWidth(targetLlvm) ? "fptrunc" : "fpext";
            EmitLine(sb, $"  {cast} = {op} {sourceLlvm} {argValue} to {targetLlvm}");
        }
        else if (sourceIsFloat)
        {
            string op = targetUnsigned ? "fptoui" : "fptosi";
            EmitLine(sb, $"  {cast} = {op} {sourceLlvm} {argValue} to {targetLlvm}");
        }
        else if (targetIsFloat)
        {
            bool sourceUnsigned = argType?.Name is "U8" or "U16" or "U32" or "U64" or "U128" or "Address";
            string op = sourceUnsigned ? "uitofp" : "sitofp";
            EmitLine(sb, $"  {cast} = {op} {sourceLlvm} {argValue} to {targetLlvm}");
        }
        else
        {
            int srcBits = GetTypeBitWidth(sourceLlvm);
            int dstBits = GetTypeBitWidth(targetLlvm);
            if (srcBits > dstBits)
                EmitLine(sb, $"  {cast} = trunc {sourceLlvm} {argValue} to {targetLlvm}");
            else if (targetUnsigned)
                EmitLine(sb, $"  {cast} = zext {sourceLlvm} {argValue} to {targetLlvm}");
            else
                EmitLine(sb, $"  {cast} = sext {sourceLlvm} {argValue} to {targetLlvm}");
        }
        return cast;
    }

    /// <summary>
    /// Generates code for a standalone function call.
    /// </summary>
    private string EmitFunctionCall(StringBuilder sb, string functionName, List<Expression> arguments,
        RoutineInfo? resolvedRoutine = null)
    {
        // Strip failable '!' suffix — registry stores names without it
        if (functionName.EndsWith('!'))
            functionName = functionName[..^1];

        // Primitive type conversion: U8(val), S32(val), F64(val), etc.
        // For @llvm types with a single argument, emit trunc/zext/sext/fpcast inline.
        // This must be checked BEFORE routine lookup, as stdlib defines conversion routines
        // (e.g., "routine U8(from: S8) -> U8") that we want to inline instead of calling.
        if (arguments.Count == 1)
        {
            TypeInfo? calledType = LookupTypeInCurrentModule(functionName);
            if (calledType is RecordTypeInfo { HasDirectBackendType: true })
            {
                return EmitPrimitiveTypeConversion(sb, functionName, arguments[0], calledType);
            }
        }

        // Compiler intrinsics for heap memory management
        // TODO: This should be removed as they are going to be C native
        if (functionName is "heap_alloc" or "heap_free" or "heap_realloc")
        {
            var argVals = new List<string>();
            var argLlvmTypes = new List<string>();
            foreach (var arg in arguments)
            {
                argVals.Add(EmitExpression(sb, arg));
                TypeInfo? at = GetExpressionType(arg);
                argLlvmTypes.Add(at != null ? GetLLVMType(at) : "i64");
            }

            switch (functionName)
            {
                case "heap_alloc":
                {
                    // heap_alloc(bytes) → rf_allocate_dynamic(i64 bytes) → returns ptr
                    string bytesVal = argVals[0];
                    string result = NextTemp();
                    EmitLine(sb, $"  {result} = call ptr @rf_allocate_dynamic(i64 {bytesVal})");
                    // Convert ptr to i64 (Address) for caller
                    string asInt = NextTemp();
                    EmitLine(sb, $"  {asInt} = ptrtoint ptr {result} to i64");
                    return asInt;
                }
                case "heap_free":
                {
                    // heap_free(ptr) → rf_invalidate(ptr)
                    string asPtr = NextTemp();
                    EmitLine(sb, $"  {asPtr} = inttoptr i64 {argVals[0]} to ptr");
                    EmitLine(sb, $"  call void @rf_invalidate(ptr {asPtr})");
                    return "undef";
                }
                case "heap_realloc":
                {
                    // heap_realloc(ptr, new_size) → rf_reallocate_dynamic(ptr, i64)
                    string asPtr = NextTemp();
                    EmitLine(sb, $"  {asPtr} = inttoptr i64 {argVals[0]} to ptr");
                    string result = NextTemp();
                    EmitLine(sb, $"  {result} = call ptr @rf_reallocate_dynamic(ptr {asPtr}, i64 {argVals[1]})");
                    // Convert ptr back to i64 (Address)
                    string asInt = NextTemp();
                    EmitLine(sb, $"  {asInt} = ptrtoint ptr {result} to i64");
                    return asInt;
                }
            }
        }

        // Use semantic analyzer's resolved routine if available (e.g., generic overload)
        // Otherwise look up the routine — try full name first, then short name fallback
        RoutineInfo? routine = resolvedRoutine
                               ?? _registry.LookupRoutine(functionName)
                               ?? _registry.LookupRoutineByName(functionName);

        // Generic free function monomorphization: when calling a generic instance (e.g., show[S64]),
        // record the monomorphization so the body is compiled with T=S64
        if (routine is { GenericDefinition: not null, TypeArguments: not null })
        {
            string monoName = MangleFunctionName(routine);
            if (!_pendingMonomorphizations.ContainsKey(monoName))
            {
                var genericDef = routine.GenericDefinition;
                var typeSubs = new Dictionary<string, TypeInfo>();
                for (int i = 0; i < genericDef.GenericParameters!.Count; i++)
                    typeSubs[genericDef.GenericParameters[i]] = (TypeInfo)routine.TypeArguments[i];

                // Use [generic] marker so FindGenericAstRoutine skips non-generic overloads
                string genericAstName = $"{genericDef.Name}[generic]";
                _pendingMonomorphizations[monoName] = new MonomorphizationEntry(
                    genericDef, null!, typeSubs, genericAstName);
            }
        }

        // If not found as a routine, check if it's a type name
        bool isCreatorCall = false;
        if (routine == null)
        {
            TypeInfo? calledType = LookupTypeInCurrentModule(functionName);
            if (calledType != null)
            {
                // Direct named-field construction: when all arg names match field names exactly,
                // emit struct construction directly (avoids $create infinite recursion).
                // e.g., CStr(ptr: from_ptr) inside CStr.$create body
                if (calledType is RecordTypeInfo record && record.MemberVariables.Count > 0
                    && arguments.Count == record.MemberVariables.Count
                    && arguments.All(a => a is NamedArgumentExpression named &&
                        record.MemberVariables.Any(mv => mv.Name == named.Name)))
                {
                    return EmitRecordConstruction(sb, record, arguments);
                }

                if (calledType is EntityTypeInfo entity && entity.MemberVariables.Count > 0
                    && arguments.Count == entity.MemberVariables.Count
                    && arguments.All(a => a is NamedArgumentExpression named2 &&
                        entity.MemberVariables.Any(mv => mv.Name == named2.Name)))
                {
                    return EmitEntityConstruction(sb, entity, arguments);
                }

                // Zero-arg entity construction → try $create() first, then null
                if (calledType is EntityTypeInfo && arguments.Count == 0)
                {
                    string createName = $"{calledType.Name}.$create";
                    var creator = _registry.LookupRoutineOverload(createName, new List<TypeInfo>());
                    if (creator != null && creator.Parameters.Count == 0)
                    {
                        routine = creator;
                        isCreatorCall = true;
                    }
                    else
                    {
                        return "null";
                    }
                }

                // Try $create overload — this covers conversion constructors
                // (e.g., CStr(from: text) → CStr.$create(from: Text))
                var semanticArgTypes = new List<TypeInfo>();
                foreach (var arg in arguments)
                {
                    TypeInfo? t = GetExpressionType(arg);
                    if (t != null) semanticArgTypes.Add(t);
                }
                routine = _registry.LookupRoutineOverload($"{calledType.Name}.$create", semanticArgTypes);
                if (routine != null)
                {
                    isCreatorCall = true;
                }
                else
                {
                    // For single-field records where arg name doesn't match field name
                    // (e.g., Letter(codepoint: val) where field is 'value')
                    if (calledType is RecordTypeInfo singleRecord && singleRecord.MemberVariables.Count == 1
                        && arguments.Count == 1 && arguments[0] is NamedArgumentExpression)
                    {
                        return EmitRecordConstruction(sb, singleRecord, arguments);
                    }

                    isCreatorCall = true;
                }
            }
        }

        // Evaluate all arguments
        var argValues = new List<string>();
        var argTypes = new List<string>();

        foreach (var arg in arguments)
        {
            string value = EmitExpression(sb, arg);
            argValues.Add(value);

            TypeInfo? argType = GetExpressionType(arg);
            if (argType == null)
            {
                throw new InvalidOperationException($"Cannot determine type for argument in function call to '{functionName}'");
            }
            argTypes.Add(GetLLVMType(argType));
        }

        // Supply default arguments for parameters not covered by explicit arguments
        if (routine != null)
        {
            for (int i = argValues.Count; i < routine.Parameters.Count; i++)
            {
                var param = routine.Parameters[i];
                if (param.HasDefaultValue)
                {
                    string value = EmitExpression(sb, param.DefaultValue!);
                    argValues.Add(value);
                    argTypes.Add(GetLLVMType(param.Type));
                }
            }
        }

        // Inside monomorphized body: if the resolved routine's param types don't match
        // the actual arg types, look for a matching generic overload and instantiate it.
        // This handles cases like show(v, end: "") inside monomorphized show[T=S64] body,
        // where registry lookup finds concrete show(Text,Text) instead of show[T](T,Text).
        if (_typeSubstitutions != null && routine != null
            && routine.GenericDefinition == null && !routine.IsGenericDefinition
            && argValues.Count > 0 && routine.Parameters.Count > 0)
        {
            string expectedLlvm = GetLLVMType(routine.Parameters[0].Type);
            if (argTypes[0] != expectedLlvm)
            {
                var genericOverload = _registry.LookupGenericOverload(routine.Name);
                if (genericOverload != null)
                {
                    TypeInfo? firstArgType = GetExpressionType(arguments[0]);
                    if (firstArgType != null && genericOverload.GenericParameters is { Count: > 0 })
                    {
                        var typeArgs = new List<TypeInfo> { firstArgType };
                        // Pad with remaining generic params if needed (use same type)
                        for (int i = 1; i < genericOverload.GenericParameters.Count; i++)
                            typeArgs.Add(firstArgType);
                        routine = genericOverload.CreateInstance(typeArgs);

                        // Re-supply default arguments for the new routine
                        while (argValues.Count > arguments.Count)
                        {
                            argValues.RemoveAt(argValues.Count - 1);
                            argTypes.RemoveAt(argTypes.Count - 1);
                        }
                        for (int i = argValues.Count; i < routine.Parameters.Count; i++)
                        {
                            var param = routine.Parameters[i];
                            if (param.HasDefaultValue)
                            {
                                string value = EmitExpression(sb, param.DefaultValue!);
                                argValues.Add(value);
                                argTypes.Add(GetLLVMType(param.Type));
                            }
                        }

                        // Record monomorphization
                        string monoName = MangleFunctionName(routine);
                        if (!_pendingMonomorphizations.ContainsKey(monoName))
                        {
                            var typeSubs = new Dictionary<string, TypeInfo>();
                            for (int i = 0; i < genericOverload.GenericParameters.Count; i++)
                                typeSubs[genericOverload.GenericParameters[i]] = (TypeInfo)routine.TypeArguments![i];
                            string genericAstName = $"{genericOverload.Name}[generic]";
                            _pendingMonomorphizations[monoName] = new MonomorphizationEntry(
                                genericOverload, null!, typeSubs, genericAstName);
                        }
                    }
                }
            }
        }

        // C105: Pack variadic arguments into List[T]
        if (routine is { IsVariadic: true })
            PackVariadicArgs(sb, routine, arguments, ref argValues, ref argTypes, argOffset: 0);

        // Build the call
        string mangledName = routine != null
            ? MangleFunctionName(routine)
            : SanitizeLLVMName(functionName);

        // Inside monomorphized bodies, calls to routines on generic types need the resolved owner type.
        // e.g., Snatched[T].$create(from: addr) inside Snatched[T].offset → when T=SortedDict[S64,S64],
        // the call should target Snatched[SortedDict[S64,S64]].$create#Address, not Snatched.$create#Address.
        if (_typeSubstitutions != null && routine?.OwnerType is { IsGenericDefinition: true, GenericParameters: not null })
        {
            var resolvedArgs = new List<TypeInfo>();
            foreach (var param in routine.OwnerType.GenericParameters)
            {
                if (_typeSubstitutions.TryGetValue(param, out var sub))
                    resolvedArgs.Add(sub);
                else
                    break;
            }
            if (resolvedArgs.Count == routine.OwnerType.GenericParameters.Count)
            {
                var resolvedOwnerType = _registry.GetOrCreateResolution(routine.OwnerType, resolvedArgs);
                string resolvedOwnerName = resolvedOwnerType.FullName;
                string paramSuffix = "";
                if (routine.Name == "$create" && routine.Parameters.Count > 0)
                {
                    TypeInfo resolvedParamType = ResolveSubstitutedType(routine.Parameters[0].Type, _typeSubstitutions);
                    paramSuffix = $"#{resolvedParamType.Name}";
                }
                mangledName = Q($"{resolvedOwnerName}.{SanitizeLLVMName(routine.Name)}{paramSuffix}");

                // Record monomorphization so the body gets generated
                if (!_pendingMonomorphizations.ContainsKey(mangledName) && !_generatedFunctionDefs.Contains(mangledName))
                {
                    var genericAstName = $"{routine.OwnerType.Name}.{routine.Name}";
                    _pendingMonomorphizations[mangledName] = new MonomorphizationEntry(
                        routine, resolvedOwnerType, _typeSubstitutions, genericAstName);
                }
            }
        }

        // Ensure the function is declared (generates 'declare' and tracks in _generatedFunctions)
        if (routine != null)
        {
            GenerateFunctionDeclaration(routine);
        }
        else
        {
            _generatedFunctions.Add(mangledName);
        }

        // For external("C") functions, F16 (half) params must be bitcast to i16 (C ABI)
        bool isCExtern = routine is { CallingConvention: "C" };
        if (isCExtern)
        {
            for (int i = 0; i < argTypes.Count; i++)
            {
                if (argTypes[i] == "half")
                {
                    string bits = NextTemp();
                    EmitLine(sb, $"  {bits} = bitcast half {argValues[i]} to i16");
                    argValues[i] = bits;
                    argTypes[i] = "i16";
                }
            }
        }

        string returnType = routine?.ReturnType != null
            ? GetLLVMType(routine.ReturnType)
            : "void";
        // Failable routines return Maybe[T] = { i64, ptr } at IR level
        bool isFailableCall = routine?.IsFailable == true;
        if (isFailableCall)
            returnType = "{ i64, ptr }";
        string callReturnType = isCExtern && returnType == "half" ? "i16" : returnType;

        // On Windows x64 MSVC ABI, external("C") functions returning structs > 8 bytes
        // use a hidden sret pointer as the first parameter. We must match this convention.
        bool needsSret = isCExtern && routine != null && NeedsCExternSret(routine);
        if (needsSret)
        {
            // Allocate space for the result, pass as sret pointer, call as void, then load
            string sretPtr = NextTemp();
            EmitLine(sb, $"  {sretPtr} = alloca {returnType}");
            // Insert sret pointer as first argument
            argTypes.Insert(0, $"ptr sret({returnType})");
            argValues.Insert(0, sretPtr);
            string args = BuildCallArgs(argTypes, argValues);
            EmitLine(sb, $"  call void @{mangledName}({args})");
            // Load the result from the sret allocation
            string result = NextTemp();
            EmitLine(sb, $"  {result} = load {returnType}, ptr {sretPtr}");
            return result;
        }
        else if (callReturnType == "void")
        {
            // Void return - no result
            string args = BuildCallArgs(argTypes, argValues);
            EmitLine(sb, $"  call void @{mangledName}({args})");
            return "undef"; // No meaningful return value
        }
        else
        {
            // Has return value
            string result = NextTemp();
            string args = BuildCallArgs(argTypes, argValues);
            EmitLine(sb, $"  {result} = call {callReturnType} @{mangledName}({args})");
            // For external("C") F16 return, bitcast i16 back to half
            if (isCExtern && returnType == "half" && callReturnType == "i16")
            {
                string halfResult = NextTemp();
                EmitLine(sb, $"  {halfResult} = bitcast i16 {result} to half");
                return halfResult;
            }
            // Unwrap failable calls
            if (isFailableCall)
            {
                // Failable void routines: no value to unwrap
                if (routine?.ReturnType == null)
                    return result;
                return EmitEmittingCallUnwrap(sb, result, routine?.ReturnType);
            }
            return result;
        }
    }

    /// <summary>
    /// Generates code for a method call on an object.
    /// The object becomes the implicit 'me' parameter.
    /// </summary>
    private string EmitMethodCall(StringBuilder sb, MemberExpression member, List<Expression> arguments)
    {
        // Intercept var_name() — inline the variable name from the receiver expression
        if (member.PropertyName == "var_name" && arguments.Count == 0)
        {
            string varName = member.Object is IdentifierExpression varId ? varId.Name : "<expr>";
            return EmitSynthesizedStringLiteral(varName);
        }

        // Evaluate the receiver (becomes 'me' parameter)
        // Handle type-level method calls (e.g., S64.data_size() from monomorphized T.data_size()).
        // After GenericAstRewriter substitutes T→S64 in IdentifierExpression, the type name
        // appears as a receiver but has no runtime value — use a dummy zeroinitializer.
        string receiver;
        TypeInfo? receiverType;
        if (member.Object is IdentifierExpression typeId
            && !_localVariables.ContainsKey(typeId.Name)
            && ResolveTypeNameAsReceiver(typeId.Name) is { } typeAsReceiver)
        {
            receiverType = typeAsReceiver;
            string llvmType = GetLLVMType(receiverType);
            receiver = llvmType.StartsWith('%') || llvmType.StartsWith('{') ? "zeroinitializer"
                     : llvmType == "ptr" ? "null" : "0";
        }
        else
        {
            receiver = EmitExpression(sb, member.Object);
            receiverType = GetExpressionType(member.Object);
        }

        if (receiverType == null)
        {
            throw new InvalidOperationException("Cannot determine receiver type for method call");
        }

        // Look up the method — try full name, then generic base name
        // Strip '!' suffix from failable method calls (e.g., invalidate!() → invalidate)
        string methodName = member.PropertyName.EndsWith('!') ? member.PropertyName[..^1] : member.PropertyName;
        string methodFullName = $"{receiverType.Name}.{methodName}";
        RoutineInfo? method = _registry.LookupRoutine(methodFullName);
        // Also try with '!' suffix — failable methods may be registered as "name!" (e.g., $getitem!)
        if (method == null && !member.PropertyName.EndsWith('!'))
            method = _registry.LookupRoutine($"{receiverType.Name}.{methodName}!");
        if (method == null && member.PropertyName.EndsWith('!'))
            method = _registry.LookupRoutine($"{receiverType.Name}.{member.PropertyName}");
        // For generic type instances (e.g., List[Letter].count), try the generic base name
        if (method == null && receiverType.Name.Contains('['))
        {
            string baseName = receiverType.Name[..receiverType.Name.IndexOf('[')];
            method = _registry.LookupRoutine($"{baseName}.{methodName}")
                ?? _registry.LookupRoutine($"{baseName}.{methodName}!");
        }

        // Fall back to LookupMethod which checks generic-parameter-owner methods (routine T.view())
        if (method == null)
        {
            method = _registry.LookupMethod(receiverType, methodName)
                ?? _registry.LookupMethod(receiverType, $"{methodName}!");
        }

        // Representable pattern: obj.Text() → Text.$create(from: obj)
        // When the method name matches a registered type and no direct method exists,
        // route to TypeName.$create(from: receiver).
        // Strip '!' suffix for failable conversions (e.g., index.U64!() → U64)
        string conversionTypeName = member.PropertyName.EndsWith('!') ? member.PropertyName[..^1] : member.PropertyName;
        if (method == null && arguments.Count == 0 && _registry.LookupType(conversionTypeName) != null)
        {
            // For @llvm primitive types, emit inline conversion (trunc/zext/sext/fpcast)
            // instead of a function call. e.g., val.Address() → inline zext/trunc.
            TypeInfo? targetType = _registry.LookupType(conversionTypeName);
            if (targetType is RecordTypeInfo { HasDirectBackendType: true })
            {
                return EmitPrimitiveTypeConversion(sb, conversionTypeName, member.Object, targetType);
            }

            string creatorName = $"{conversionTypeName}.$create";
            var argTypes2 = new List<TypeInfo> { receiverType };
            RoutineInfo? creator = _registry.LookupRoutineOverload(creatorName, argTypes2);
            if (creator != null)
            {
                // Method-level generics: infer T from argument types and monomorphize
                if (creator.IsGenericDefinition)
                {
                    var inferred = InferMethodTypeArgs(creator, argTypes2);
                    if (inferred != null)
                    {
                        var resolvedParamNames = argTypes2.Select(t => t.Name);
                        string creatorMangledName = Q($"{creator.OwnerType?.FullName ?? conversionTypeName}.$create#{string.Join(",", resolvedParamNames)}");

                        GenerateFunctionDeclaration(creator);
                        RecordMonomorphization(creatorMangledName, creator, creator.OwnerType ?? receiverType, inferred);

                        string retType = creator.ReturnType != null ? GetLLVMType(creator.ReturnType) : "ptr";
                        string receiverLlvm = GetLLVMType(receiverType);
                        string result = NextTemp();
                        EmitLine(sb, $"  {result} = call {retType} @{creatorMangledName}({receiverLlvm} {receiver})");
                        return result;
                    }
                }

                // Owner-level generics: resolve from argument types and monomorphize.
                // e.g., List[T].$create(from: SortedSet[T]) called with SortedSet[S64]
                //        → infer T=S64, emit List[S64].$create#SortedSet[S64]
                if (creator.OwnerType is { IsGenericDefinition: true, GenericParameters: not null }
                    && receiverType.IsGenericResolution && receiverType.TypeArguments != null)
                {
                    // Match the argument's type args against the creator param's type args to infer owner generics
                    TypeInfo paramType = creator.Parameters[0].Type;
                    if (paramType is { IsGenericResolution: true, TypeArguments: not null }
                        && paramType.TypeArguments.Count == receiverType.TypeArguments.Count)
                    {
                        var ownerSubs = new Dictionary<string, TypeInfo>();
                        for (int i = 0; i < paramType.TypeArguments.Count; i++)
                        {
                            if (paramType.TypeArguments[i] is GenericParameterTypeInfo gp)
                                ownerSubs[gp.Name] = receiverType.TypeArguments[i];
                        }

                        // Resolve the owner type (e.g., List[T] → List[S64])
                        var resolvedOwnerArgs = new List<TypeInfo>();
                        foreach (string gp in creator.OwnerType.GenericParameters)
                        {
                            if (ownerSubs.TryGetValue(gp, out var resolved))
                                resolvedOwnerArgs.Add(resolved);
                            else
                                break;
                        }

                        if (resolvedOwnerArgs.Count == creator.OwnerType.GenericParameters.Count)
                        {
                            var resolvedOwner = _registry.GetOrCreateResolution(creator.OwnerType, resolvedOwnerArgs);
                            string resolvedFuncName = Q($"{resolvedOwner.FullName}.$create#{receiverType.Name}");

                            GenerateFunctionDeclaration(creator);
                            RecordMonomorphization(resolvedFuncName, creator, resolvedOwner);

                            string retType2 = "ptr";
                            string receiverLlvm2 = GetLLVMType(receiverType);
                            string result2 = NextTemp();
                            EmitLine(sb, $"  {result2} = call {retType2} @{resolvedFuncName}({receiverLlvm2} {receiver})");
                            return result2;
                        }
                    }
                }

                // Non-generic path
                string funcName = MangleFunctionName(creator);
                GenerateFunctionDeclaration(creator);
                string retType3 = creator.ReturnType != null ? GetLLVMType(creator.ReturnType) : "ptr";
                string receiverLlvm3 = GetLLVMType(receiverType);
                string result3 = NextTemp();
                EmitLine(sb, $"  {result3} = call {retType3} @{funcName}({receiverLlvm3} {receiver})");
                return result3;
            }
        }

        // For zero-argument methods on entity/record types, if the method name matches a field,
        // emit as a direct field access (common pattern: List[T].count() returns me.count)
        // Also applies when method is a generic definition that can't be monomorphized.
        if ((method == null || method.IsGenericDefinition) && arguments.Count == 0)
        {
            if (receiverType is EntityTypeInfo entity &&
                entity.MemberVariables.Any(mv => mv.Name == member.PropertyName))
            {
                return EmitEntityMemberVariableRead(sb, receiver, entity, member.PropertyName);
            }
            if (receiverType is RecordTypeInfo record &&
                record.MemberVariables.Any(mv => mv.Name == member.PropertyName))
            {
                return EmitRecordMemberVariableRead(sb, receiver, record, member.PropertyName);
            }
        }

        // Build argument list: receiver first, then explicit arguments
        var argValues = new List<string> { receiver };
        var argTypes = new List<string> { GetParameterLLVMType(receiverType) };

        foreach (var arg in arguments)
        {
            string value = EmitExpression(sb, arg);
            argValues.Add(value);

            TypeInfo? argType = GetExpressionType(arg);
            if (argType == null)
            {
                throw new InvalidOperationException($"Cannot determine type for argument in method call to '{member.PropertyName}'");
            }
            argTypes.Add(GetLLVMType(argType));
        }

        // C105: Pack variadic arguments into List[T]
        if (method is { IsVariadic: true })
            PackVariadicArgs(sb, method, arguments, ref argValues, ref argTypes, argOffset: 1);

        // Method-level generics on regular method calls (e.g., method has [T] on itself, not the owner)
        // Infer type args from concrete argument types and monomorphize
        Dictionary<string, TypeInfo>? inferredMethodTypeArgs = null;
        if (method != null && method.IsGenericDefinition && arguments.Count > 0)
        {
            // Only pass explicit argument types — method.Parameters excludes implicit 'me',
            // so including receiverType would cause an off-by-one mismatch
            var concreteArgTypes = new List<TypeInfo>();
            foreach (var arg in arguments)
            {
                var t = GetExpressionType(arg);
                if (t != null) concreteArgTypes.Add(t);
            }
            inferredMethodTypeArgs = InferMethodTypeArgs(method, concreteArgTypes);
        }

        // Build the call — for resolved generic types (e.g., List[Letter].add_last),
        // use the resolved type name even if the method was found via the base type
        string mangledName;
        if (method != null && inferredMethodTypeArgs != null)
        {
            // Method-level generics: use concrete param types in mangled name
            var resolvedParamNames = new List<string> { receiverType.Name };
            foreach (var arg in arguments)
            {
                var t = GetExpressionType(arg);
                if (t != null) resolvedParamNames.Add(t.Name);
            }
            string ownerName = method.OwnerType?.FullName ?? receiverType.FullName;
            mangledName = Q($"{ownerName}.{SanitizeLLVMName(method.Name)}#{string.Join(",", resolvedParamNames)}");
            RecordMonomorphization(mangledName, method, receiverType, inferredMethodTypeArgs);
        }
        else if (method != null && receiverType.IsGenericResolution &&
            method.OwnerType != null && method.OwnerType.IsGenericDefinition)
        {
            mangledName = Q($"{receiverType.FullName}.{SanitizeLLVMName(method.Name)}");
            // Record for monomorphization — will compile generic AST body with type substitutions
            RecordMonomorphization(mangledName, method, receiverType);
        }
        else if (method != null && method.OwnerType is GenericParameterTypeInfo)
        {
            // Generic-parameter-owner methods (e.g., routine T.view() called on Point)
            // Monomorphize: Point.view with T=Point
            mangledName = Q($"{receiverType.FullName}.{SanitizeLLVMName(method.Name)}");
            RecordMonomorphization(mangledName, method, receiverType);
        }
        else
        {
            mangledName = method != null
                ? MangleFunctionName(method)
                : Q($"{receiverType.FullName}.{SanitizeLLVMName(member.PropertyName)}");
        }

        // Ensure the method is declared (so the multi-pass stdlib loop can compile its body)
        // Skip for protocol-owned methods — they can't be declared with protocol types in LLVM IR;
        // the monomorphized version (with concrete receiver type) will generate its own declaration.
        if (method != null && method.OwnerType is not ProtocolTypeInfo)
        {
            GenerateFunctionDeclaration(method);
        }

        // Track protocol dispatch calls for stub generation
        if (method?.OwnerType is ProtocolTypeInfo protoDispatch)
        {
            _pendingProtocolDispatches.TryAdd(mangledName,
                new ProtocolDispatchInfo(protoDispatch, method.Name));
        }

        // Resolve return type — for generic resolutions, substitute type parameters (e.g., T → U8)
        TypeInfo? resolvedReturnType = method?.ReturnType;

        // For generic-parameter-owner methods (T.view() → Viewed[T]), substitute T with receiver type
        if (resolvedReturnType != null && method?.OwnerType is GenericParameterTypeInfo genParamOwner)
        {
            resolvedReturnType = SubstituteGenericParamInType(resolvedReturnType, genParamOwner.Name, receiverType);
        }

        // For protocol-owned methods (Iterable[T].enumerate() → EnumerateSequence[T]),
        // substitute protocol generic params using receiver's type arguments.
        // Use GenericDefinition to get the param names (resolved protocols have null GenericParameters).
        if (resolvedReturnType != null && method?.OwnerType is ProtocolTypeInfo protoOwner &&
            receiverType is { IsGenericResolution: true, TypeArguments: not null })
        {
            var protoGenDef = protoOwner.GenericDefinition ?? protoOwner;
            if (protoGenDef.GenericParameters is { Count: > 0 })
            {
                for (int pi = 0; pi < protoGenDef.GenericParameters.Count && pi < receiverType.TypeArguments.Count; pi++)
                    resolvedReturnType = SubstituteGenericParamInType(resolvedReturnType,
                        protoGenDef.GenericParameters[pi], receiverType.TypeArguments[pi]);
            }
        }

        if (resolvedReturnType is GenericParameterTypeInfo && receiverType is { IsGenericResolution: true, TypeArguments: not null })
        {
            TypeInfo? ownerGenericDef = receiverType switch
            {
                RecordTypeInfo r => r.GenericDefinition,
                EntityTypeInfo e => e.GenericDefinition,

                _ => null
            };
            if (ownerGenericDef?.GenericParameters != null)
            {
                int paramIndex = ownerGenericDef.GenericParameters.ToList().IndexOf(resolvedReturnType.Name);
                if (paramIndex >= 0 && paramIndex < receiverType.TypeArguments.Count)
                    resolvedReturnType = receiverType.TypeArguments[paramIndex];
            }
        }

        // For return types that are generic resolutions with unresolved parameters
        // (e.g., ValueBitList[N] → ValueBitList[8]), substitute from the receiver's type arguments
        if (resolvedReturnType is { IsGenericResolution: true, TypeArguments: not null }
            && receiverType is { IsGenericResolution: true, TypeArguments: not null })
        {
            TypeInfo? ownerGenericDef = receiverType switch
            {
                RecordTypeInfo r => r.GenericDefinition,
                EntityTypeInfo e => e.GenericDefinition,
                _ => null
            };
            if (ownerGenericDef?.GenericParameters != null)
            {
                bool needsSubst = false;
                var substArgs = new List<TypeInfo>();
                foreach (var ta in resolvedReturnType.TypeArguments)
                {
                    if (ta is GenericParameterTypeInfo or ConstGenericValueTypeInfo)
                    {
                        int pi = ownerGenericDef.GenericParameters.ToList().IndexOf(ta.Name);
                        if (pi >= 0 && pi < receiverType.TypeArguments.Count)
                        {
                            substArgs.Add(receiverType.TypeArguments[pi]);
                            needsSubst = true;
                            continue;
                        }
                    }
                    substArgs.Add(ta);
                }
                if (needsSubst)
                {
                    TypeInfo? retGenBase = resolvedReturnType switch
                    {
                        RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition,
                        EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition,
                        _ => null
                    };
                    retGenBase ??= resolvedReturnType.Name.Contains('[')
                        ? _registry.LookupType(resolvedReturnType.Name[..resolvedReturnType.Name.IndexOf('[')])
                        : null;
                    if (retGenBase != null)
                        resolvedReturnType = _registry.GetOrCreateResolution(retGenBase, substArgs);
                }
            }
        }

        // For resolved generic methods, also emit a declaration with the resolved name
        if (!_generatedFunctions.Contains(mangledName))
        {
            string retType = resolvedReturnType != null ? GetLLVMType(resolvedReturnType) : "void";
            // Emitting and failable routines return Maybe[T] = { i64, ptr } at IR level
            if (method?.AsyncStatus == AsyncStatus.Emitting || method?.IsFailable == true)
                retType = "{ i64, ptr }";
            EmitLine(_functionDeclarations, $"declare {retType} @{mangledName}({string.Join(", ", argTypes)})");
            _generatedFunctions.Add(mangledName);
        }

        string returnType = resolvedReturnType != null
            ? GetLLVMType(resolvedReturnType)
            : "void";
        // Emitting and failable routines return Maybe[T] = { i64, ptr } at IR level
        bool isEmittingCall = method?.AsyncStatus == AsyncStatus.Emitting;
        bool isFailableCall = method?.IsFailable == true;
        if (isEmittingCall || isFailableCall)
            returnType = "{ i64, ptr }";

        if (returnType == "void")
        {
            string args = BuildCallArgs(argTypes, argValues);
            EmitLine(sb, $"  call void @{mangledName}({args})");
            return "undef";
        }
        else
        {
            string result = NextTemp();
            string args = BuildCallArgs(argTypes, argValues);
            EmitLine(sb, $"  {result} = call {returnType} @{mangledName}({args})");

            // Unwrap Maybe[T] from emitting/failable routine calls (C74).
            // EmitFor handles $next() unwrapping directly, so this only fires for
            // direct calls like `var item = me.source.$next()` inside emitting bodies.
            if (isEmittingCall || isFailableCall)
            {
                // Failable void routines: no value to unwrap, just discard the { i64, ptr }
                if (isFailableCall && resolvedReturnType == null)
                    return result;
                return EmitEmittingCallUnwrap(sb, result, resolvedReturnType);
            }

            return result;
        }
    }

    /// <summary>
    /// Unwraps a Maybe[T] result from an emitting routine call (C74).
    /// Extracts the tag, propagates ABSENT if inside an emitting routine, and loads the value.
    /// </summary>
    private string EmitEmittingCallUnwrap(StringBuilder sb, string maybeResult, TypeInfo? valueType)
    {
        // Store Maybe { i64, ptr } to memory for field extraction
        string maybeAddr = NextTemp();
        EmitLine(sb, $"  {maybeAddr} = alloca {{ i64, ptr }}");
        EmitLine(sb, $"  store {{ i64, ptr }} {maybeResult}, ptr {maybeAddr}");

        // Extract tag (field 0 = DataState)
        string tagPtr = NextTemp();
        EmitLine(sb, $"  {tagPtr} = getelementptr {{ i64, ptr }}, ptr {maybeAddr}, i32 0, i32 0");
        string tag = NextTemp();
        EmitLine(sb, $"  {tag} = load i64, ptr {tagPtr}");

        // Branch: tag == 1 (VALID) → extract value, else → propagate ABSENT
        string isValid = NextTemp();
        EmitLine(sb, $"  {isValid} = icmp eq i64 {tag}, 1");

        string validLabel = NextLabel("emit_unwrap_valid");
        string absentLabel = NextLabel("emit_unwrap_absent");
        EmitLine(sb, $"  br i1 {isValid}, label %{validLabel}, label %{absentLabel}");

        // ABSENT branch: propagate absence or trap
        EmitLine(sb, $"{absentLabel}:");
        if (_currentEmittingRoutine?.AsyncStatus == AsyncStatus.Emitting || _currentRoutineIsFailable)
        {
            // Inside emitting or failable routine: propagate absence to caller
            EmitLine(sb, "  call void @rf_trace_pop()");
            EmitLine(sb, "  ret { i64, ptr } { i64 0, ptr null }");
        }
        else
        {
            // Non-failable caller: absence is a contract violation
            EmitLine(sb, "  unreachable");
        }

        // VALID branch: extract handle pointer (field 1) and load the value
        EmitLine(sb, $"{validLabel}:");
        _currentBlock = validLabel;

        string handlePtr = NextTemp();
        EmitLine(sb, $"  {handlePtr} = getelementptr {{ i64, ptr }}, ptr {maybeAddr}, i32 0, i32 1");
        string handleVal = NextTemp();
        EmitLine(sb, $"  {handleVal} = load ptr, ptr {handlePtr}");

        // For entity types, the value IS the pointer (no boxing) — return directly
        if (valueType is EntityTypeInfo)
        {
            return handleVal;
        }

        string unwrappedType = valueType != null ? GetLLVMType(valueType) : "i64";
        string unwrappedVal = NextTemp();
        EmitLine(sb, $"  {unwrappedVal} = load {unwrappedType}, ptr {handleVal}");

        return unwrappedVal;
    }

    /// <summary>
    /// Substitutes a generic parameter name with a concrete type in a type expression.
    /// Handles both direct substitution (T → Point) and nested resolution (Viewed[T] → Viewed[Point]).
    /// </summary>
    private TypeInfo SubstituteGenericParamInType(TypeInfo type, string paramName, TypeInfo concreteType)
    {
        // Direct match: T → Point
        if (type.Name == paramName || (type is GenericParameterTypeInfo gp && gp.Name == paramName))
            return concreteType;

        // Nested resolution: Viewed[T] → Viewed[Point]
        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            bool anyChanged = false;
            var substitutedArgs = new List<TypeInfo>();
            foreach (var arg in type.TypeArguments)
            {
                var substituted = SubstituteGenericParamInType(arg, paramName, concreteType);
                substitutedArgs.Add(substituted);
                if (!ReferenceEquals(substituted, arg)) anyChanged = true;
            }
            if (anyChanged)
            {
                TypeInfo? genericBase = type switch
                {
                    RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition,
                    EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition,
                    ErrorHandlingTypeInfo { GenericDefinition: not null } eh => eh.GenericDefinition,
                    ProtocolTypeInfo { GenericDefinition: not null } p => p.GenericDefinition,
                    _ => null
                };
                if (genericBase == null)
                {
                    string baseName = type.Name.Contains('[') ? type.Name[..type.Name.IndexOf('[')] : type.Name;
                    genericBase = _registry.LookupType(baseName);
                }
                if (genericBase != null)
                    return _registry.GetOrCreateResolution(genericBase, substitutedArgs);
            }
        }

        return type;
    }

    /// <summary>
    /// Builds a comma-separated argument list for a call instruction.
    /// </summary>
    private static string BuildCallArgs(List<string> types, List<string> values)
    {
        if (types.Count != values.Count || types.Count == 0)
        {
            return "";
        }
        return string.Join(", ", types.Select((t, i) => $"{t} {values[i]}"));
    }

    /// <summary>
    /// Generates code for compound assignment (e.g., x += 1).
    /// Desugars to: x = x op value, then stores back and returns the result.
    /// </summary>
    private string EmitCompoundAssignment(StringBuilder sb, CompoundAssignmentExpression compound)
    {
        // Synthesize a BinaryExpression for the operation
        var binaryExpr = new BinaryExpression(compound.Target, compound.Operator, compound.Value, compound.Location);
        string result = EmitBinaryOp(sb, binaryExpr);

        // Store the result back to the target
        switch (compound.Target)
        {
            case IdentifierExpression id:
                EmitVariableAssignment(sb, id.Name, result);
                break;
            case MemberExpression member:
                EmitMemberVariableAssignment(sb, member, result);
                break;
            case IndexExpression index:
                EmitIndexAssignment(sb, index, result);
                break;
        }

        return result;
    }

    /// <summary>
    /// Generates code for a binary operation.
    /// Only handles operators that are NOT desugared to method calls by the parser.
    /// Arithmetic/comparison/bitwise operators are desugared to $add, $eq, etc.
    /// </summary>
    private string EmitBinaryOp(StringBuilder sb, BinaryExpression binary)
    {
        return binary.Operator switch
        {
            BinaryOperator.And => IsFlagsBinaryOp(binary) ? EmitFlagsCombine(sb, binary) : EmitShortCircuitAnd(sb, binary),
            BinaryOperator.Or => EmitShortCircuitOr(sb, binary),
            BinaryOperator.Identical => EmitIdentityComparison(sb, binary, "eq"),
            BinaryOperator.NotIdentical => EmitIdentityComparison(sb, binary, "ne"),
            BinaryOperator.Assign => EmitBinaryAssign(sb, binary),
            BinaryOperator.But => EmitBitClear(sb, binary),
            BinaryOperator.In => EmitContainsCall(sb, binary, "$contains"),
            BinaryOperator.NotIn => EmitContainsCall(sb, binary, "$notcontains"),
            BinaryOperator.Is => EmitChoiceIs(sb, binary, "eq"),
            BinaryOperator.IsNot => EmitChoiceIs(sb, binary, "ne"),
            BinaryOperator.Obeys => EmitCompileTimeConstant("true"),
            BinaryOperator.Disobeys => EmitCompileTimeConstant("false"),
            BinaryOperator.NoneCoalesce => EmitNoneCoalesce(sb, binary),
            // Arithmetic, comparison, bitwise operators — normally desugared to method
            // calls by the semantic analyzer, but stdlib bodies are raw AST, so we
            // handle them directly for primitive types.
            _ => EmitPrimitiveBinaryOp(sb, binary)
        };
    }

    /// <summary>
    /// Emits a primitive binary operation (arithmetic, comparison, bitwise, shift)
    /// directly as LLVM instructions. Used for stdlib bodies where operator
    /// desugaring to method calls hasn't been applied.
    /// </summary>
    private string EmitPrimitiveBinaryOp(StringBuilder sb, BinaryExpression binary)
    {
        // TODO: This is going to be stdlib operator overloading and thus does not need hardcoding
        string left = EmitExpression(sb, binary.Left);
        string right = EmitExpression(sb, binary.Right);
        TypeInfo? leftType = GetExpressionType(binary.Left);
        string llvmType = leftType != null ? GetLLVMType(leftType) : "i64";
        string typeName = leftType?.Name ?? "";
        bool isUnsigned = typeName is "U8" or "U16" or "U32" or "U64" or "U128" or "Address";
        bool isFloat = llvmType is "half" or "float" or "double" or "fp128";
        bool isSoftwareType = llvmType.StartsWith("%Record.") || llvmType.StartsWith("%Tuple.") || llvmType.StartsWith("{") || llvmType == "ptr";

        // For comparison operators that return i1 (Bool)
        string? cmpInstr = isSoftwareType ? null : binary.Operator switch
        {
            BinaryOperator.Equal when isFloat => "fcmp oeq",
            BinaryOperator.Equal => "icmp eq",
            BinaryOperator.NotEqual when isFloat => "fcmp une",
            BinaryOperator.NotEqual => "icmp ne",
            BinaryOperator.Less when isFloat => "fcmp olt",
            BinaryOperator.Less when isUnsigned => "icmp ult",
            BinaryOperator.Less => "icmp slt",
            BinaryOperator.LessEqual when isFloat => "fcmp ole",
            BinaryOperator.LessEqual when isUnsigned => "icmp ule",
            BinaryOperator.LessEqual => "icmp sle",
            BinaryOperator.Greater when isFloat => "fcmp ogt",
            BinaryOperator.Greater when isUnsigned => "icmp ugt",
            BinaryOperator.Greater => "icmp sgt",
            BinaryOperator.GreaterEqual when isFloat => "fcmp oge",
            BinaryOperator.GreaterEqual when isUnsigned => "icmp uge",
            BinaryOperator.GreaterEqual => "icmp sge",
            _ => null
        };

        if (cmpInstr != null)
        {
            string cmpResult = NextTemp();
            EmitLine(sb, $"  {cmpResult} = {cmpInstr} {llvmType} {left}, {right}");
            return cmpResult;
        }

        // Arithmetic and bitwise operators (skip for software-implemented types like D32, D64, D128)
        string? arithInstr = isSoftwareType ? null : binary.Operator switch
        {
            BinaryOperator.Add when isFloat => "fadd",
            BinaryOperator.Add => "add",
            BinaryOperator.Subtract when isFloat => "fsub",
            BinaryOperator.Subtract => "sub",
            BinaryOperator.Multiply when isFloat => "fmul",
            BinaryOperator.Multiply => "mul",
            BinaryOperator.TrueDivide when isFloat => "fdiv",
            BinaryOperator.TrueDivide when isUnsigned => "udiv",
            BinaryOperator.TrueDivide => "sdiv",
            BinaryOperator.FloorDivide when isUnsigned => "udiv",
            BinaryOperator.FloorDivide => "sdiv",
            BinaryOperator.Modulo when isFloat => "frem",
            BinaryOperator.Modulo when isUnsigned => "urem",
            BinaryOperator.Modulo => "srem",
            BinaryOperator.AddWrap => "add",
            BinaryOperator.SubtractWrap => "sub",
            BinaryOperator.MultiplyWrap => "mul",
            BinaryOperator.BitwiseAnd => "and",
            BinaryOperator.BitwiseOr => "or",
            BinaryOperator.BitwiseXor => "xor",
            BinaryOperator.ArithmeticLeftShift => "shl",
            BinaryOperator.ArithmeticRightShift when isUnsigned => "lshr",
            BinaryOperator.ArithmeticRightShift => "ashr",
            BinaryOperator.LogicalLeftShift => "shl",
            BinaryOperator.LogicalRightShift => "lshr",
            _ => null
        };

        if (arithInstr != null)
        {
            // Ensure right operand matches left operand's type width
            TypeInfo? rightType = GetExpressionType(binary.Right);
            string rightLlvmType = rightType != null ? GetLLVMType(rightType) : llvmType;
            if (rightLlvmType != llvmType)
            {
                // Truncate or extend right operand to match left type
                string cast = NextTemp();
                int leftBits = GetTypeBitWidth(llvmType);
                int rightBits = GetTypeBitWidth(rightLlvmType);
                if (rightBits > leftBits)
                    EmitLine(sb, $"  {cast} = trunc {rightLlvmType} {right} to {llvmType}");
                else if (isUnsigned)
                    EmitLine(sb, $"  {cast} = zext {rightLlvmType} {right} to {llvmType}");
                else
                    EmitLine(sb, $"  {cast} = sext {rightLlvmType} {right} to {llvmType}");
                right = cast;
            }
            string result = NextTemp();
            EmitLine(sb, $"  {result} = {arithInstr} {llvmType} {left}, {right}");
            return result;
        }

        // Fall back to method call for types with software-implemented arithmetic (e.g., D32, D64, D128)
        string? methodName = binary.Operator switch
        {
            BinaryOperator.Add => "$add",
            BinaryOperator.Subtract => "$sub",
            BinaryOperator.Multiply => "$mul",
            BinaryOperator.TrueDivide => "$truediv",
            BinaryOperator.FloorDivide => "$floordiv",
            BinaryOperator.Modulo => "$mod",
            BinaryOperator.Equal => "$eq",
            BinaryOperator.NotEqual => "$ne",
            BinaryOperator.Less => "$lt",
            BinaryOperator.LessEqual => "$le",
            BinaryOperator.Greater => "$gt",
            BinaryOperator.GreaterEqual => "$ge",
            _ => null
        };
        if (methodName != null && leftType != null)
        {
            var method = _registry.LookupMethod(leftType, methodName);
            if (method != null)
            {
                // For generic resolutions (e.g., Snatched[Point].$eq), use the concrete type name
                // and record monomorphization so the specialized function body gets generated
                string funcName;
                if (leftType.IsGenericResolution && method.OwnerType is { IsGenericDefinition: true })
                {
                    funcName = Q($"{leftType.FullName}.{SanitizeLLVMName(methodName)}");
                    RecordMonomorphization(funcName, method, leftType);
                }
                else
                {
                    funcName = MangleFunctionName(method);
                }
                GenerateFunctionDeclaration(method, funcName);
                string retType = method.ReturnType != null ? GetLLVMType(method.ReturnType) : llvmType;
                string result = NextTemp();
                EmitLine(sb, $"  {result} = call {retType} @{funcName}({llvmType} {left}, {llvmType} {right})");
                return result;
            }
        }

        throw new NotImplementedException(
            $"Binary operator '{binary.Operator}' not supported for type '{typeName}' " +
            $"(left={binary.Left.GetType().Name}, right={binary.Right.GetType().Name}, loc={binary.Location})");
    }

    /// <summary>
    /// Emits an 'is' pattern expression (e.g., 'value is None', 'value isnot None').
    /// For Maybe/ErrorHandling types, checks the tag field.
    /// </summary>
    /// <summary>
    /// Handles GenericMemberExpression — the parser produces this for ambiguous expr.member[args]
    /// patterns. In most cases this is actually member access followed by indexing (e.g.,
    /// me.buckets[i].clear()), not a generic type reference. Desugar to member access + index access.
    /// </summary>
    private string EmitGenericMemberExpression(StringBuilder sb, GenericMemberExpression gme)
    {
        // Build the member access: gme.Object.gme.MemberName
        var memberExpr = new MemberExpression(
            Object: gme.Object,
            PropertyName: gme.MemberName,
            Location: gme.Location);

        // The "type arguments" are actually index expressions. Use the first one.
        if (gme.TypeArguments.Count > 0)
        {
            var indexExpr = new IdentifierExpression(
                Name: gme.TypeArguments[0].Name,
                Location: gme.TypeArguments[0].Location);

            var indexAccess = new IndexExpression(
                Object: memberExpr,
                Index: indexExpr,
                Location: gme.Location);

            return EmitIndexAccess(sb, indexAccess);
        }

        // No type arguments — just emit as member access
        return EmitMemberVariableAccess(sb, memberExpr);
    }

    private string EmitIsPattern(StringBuilder sb, IsPatternExpression isPattern)
    {
        string operand = EmitExpression(sb, isPattern.Expression);

        // Handle NonePattern or TypePattern with type name "None" — check Maybe tag == 0
        bool isNoneCheck = isPattern.Pattern is NonePattern
            || (isPattern.Pattern is TypePattern tp && tp.Type.Name == "None");

        if (isNoneCheck)
        {
            // For Maybe types, check tag == 0 (ABSENT/None)
            // Use the proper LLVM type for the operand (named or anonymous)
            TypeInfo? operandType = GetExpressionType(isPattern.Expression);
            string maybeType = operandType != null ? GetLLVMType(operandType) : "{ i64, ptr }";

            string allocaPtr = NextTemp();
            EmitLine(sb, $"  {allocaPtr} = alloca {maybeType}");
            EmitLine(sb, $"  store {maybeType} {operand}, ptr {allocaPtr}");

            string tagPtr = NextTemp();
            string tag = NextTemp();
            EmitLine(sb, $"  {tagPtr} = getelementptr {maybeType}, ptr {allocaPtr}, i32 0, i32 0");
            EmitLine(sb, $"  {tag} = load i64, ptr {tagPtr}");

            string result = NextTemp();
            if (isPattern.IsNegated)
                EmitLine(sb, $"  {result} = icmp ne i64 {tag}, 0"); // isnot None → tag != 0
            else
                EmitLine(sb, $"  {result} = icmp eq i64 {tag}, 0"); // is None → tag == 0
            return result;
        }

        throw new NotImplementedException($"IsPatternExpression pattern type not implemented: {isPattern.Pattern.GetType().Name}");
    }

    /// <summary>
    /// Emits a chained comparison (e.g., 0xC0u8 &lt;= b0 &lt;= 0xDFu8).
    /// Evaluates middle operands once and ANDs all pairwise comparisons.
    /// </summary>
    private string EmitChainedComparison(StringBuilder sb, ChainedComparisonExpression chain)
    {
        // Evaluate all operands (middle ones evaluated only once)
        var values = new List<string>();
        foreach (var operand in chain.Operands)
            values.Add(EmitExpression(sb, operand));

        // Emit pairwise comparisons, AND results together
        string result = "";
        for (int i = 0; i < chain.Operators.Count; i++)
        {
            TypeInfo? leftType = GetExpressionType(chain.Operands[i]);
            string llvmType = leftType != null ? GetLLVMType(leftType) : "i64";
            string typeName = leftType?.Name ?? "";
            bool isUnsigned = typeName is "U8" or "U16" or "U32" or "U64" or "U128" or "Address";
            bool isFloat = llvmType is "half" or "float" or "double" or "fp128";

            string cmpInstr = chain.Operators[i] switch
            {
                BinaryOperator.Less when isFloat => "fcmp olt",
                BinaryOperator.Less when isUnsigned => "icmp ult",
                BinaryOperator.Less => "icmp slt",
                BinaryOperator.LessEqual when isFloat => "fcmp ole",
                BinaryOperator.LessEqual when isUnsigned => "icmp ule",
                BinaryOperator.LessEqual => "icmp sle",
                BinaryOperator.Greater when isFloat => "fcmp ogt",
                BinaryOperator.Greater when isUnsigned => "icmp ugt",
                BinaryOperator.Greater => "icmp sgt",
                BinaryOperator.GreaterEqual when isFloat => "fcmp oge",
                BinaryOperator.GreaterEqual when isUnsigned => "icmp uge",
                BinaryOperator.GreaterEqual => "icmp sge",
                BinaryOperator.Equal => isFloat ? "fcmp oeq" : "icmp eq",
                BinaryOperator.NotEqual => isFloat ? "fcmp une" : "icmp ne",
                _ => throw new InvalidOperationException($"Unsupported chained comparison operator: {chain.Operators[i]}")
            };

            string cmp = NextTemp();
            EmitLine(sb, $"  {cmp} = {cmpInstr} {llvmType} {values[i]}, {values[i + 1]}");

            if (result == "")
                result = cmp;
            else
            {
                string andResult = NextTemp();
                EmitLine(sb, $"  {andResult} = and i1 {result}, {cmp}");
                result = andResult;
            }
        }

        return result;
    }

    /// <summary>
    /// Emits choice case comparison (is / isnot).
    /// Left operand is a choice value (i32 tag), right operand is a choice case identifier.
    /// </summary>
    private string EmitChoiceIs(StringBuilder sb, BinaryExpression binary, string cmpOp)
    {
        string left = EmitExpression(sb, binary.Left);

        // Try to resolve RHS as a known choice case identifier
        if (binary.Right is IdentifierExpression id)
        {
            var choiceCase = _registry.LookupChoiceCase(id.Name);
            if (choiceCase != null)
            {
                string result = NextTemp();
                EmitLine(sb, $"  {result} = icmp {cmpOp} i32 {left}, {choiceCase.Value.CaseInfo.ComputedValue}");
                return result;
            }
        }

        // Fallback: evaluate RHS as an expression (e.g., qualified access Direction.NORTH)
        string right = EmitExpression(sb, binary.Right);
        string fallbackResult = NextTemp();
        EmitLine(sb, $"  {fallbackResult} = icmp {cmpOp} i32 {left}, {right}");
        return fallbackResult;
    }

    /// <summary>
    /// Emits short-circuit AND: evaluate left, branch, phi merge.
    /// </summary>
    private string EmitShortCircuitAnd(StringBuilder sb, BinaryExpression binary)
    {
        string left = EmitExpression(sb, binary.Left);

        string rhsLabel = NextLabel("and_rhs");
        string endLabel = NextLabel("and_end");
        string leftBlock = _currentBlock;

        EmitLine(sb, $"  br i1 {left}, label %{rhsLabel}, label %{endLabel}");

        EmitLine(sb, $"{rhsLabel}:");
        _currentBlock = rhsLabel;
        string right = EmitExpression(sb, binary.Right);
        string rightBlock = _currentBlock;
        EmitLine(sb, $"  br label %{endLabel}");

        EmitLine(sb, $"{endLabel}:");
        _currentBlock = endLabel;
        string result = NextTemp();
        EmitLine(sb, $"  {result} = phi i1 [ false, %{leftBlock} ], [ {right}, %{rightBlock} ]");
        return result;
    }

    /// <summary>
    /// Emits short-circuit OR: evaluate left, branch, phi merge.
    /// </summary>
    private string EmitShortCircuitOr(StringBuilder sb, BinaryExpression binary)
    {
        string left = EmitExpression(sb, binary.Left);

        string rhsLabel = NextLabel("or_rhs");
        string endLabel = NextLabel("or_end");
        string leftBlock = _currentBlock;

        EmitLine(sb, $"  br i1 {left}, label %{endLabel}, label %{rhsLabel}");

        EmitLine(sb, $"{rhsLabel}:");
        _currentBlock = rhsLabel;
        string right = EmitExpression(sb, binary.Right);
        string rightBlock = _currentBlock;
        EmitLine(sb, $"  br label %{endLabel}");

        EmitLine(sb, $"{endLabel}:");
        _currentBlock = endLabel;
        string result = NextTemp();
        EmitLine(sb, $"  {result} = phi i1 [ true, %{leftBlock} ], [ {right}, %{rightBlock} ]");
        return result;
    }

    /// <summary>
    /// Emits identity comparison (=== / !==) using pointer comparison.
    /// </summary>
    private string EmitIdentityComparison(StringBuilder sb, BinaryExpression binary, string cmpOp)
    {
        string left = EmitExpression(sb, binary.Left);
        string right = EmitExpression(sb, binary.Right);
        string result = NextTemp();
        EmitLine(sb, $"  {result} = icmp {cmpOp} ptr {left}, {right}");
        return result;
    }

    /// <summary>
    /// Emits assignment as an expression (evaluates right, stores into left's alloca).
    /// </summary>
    private string EmitBinaryAssign(StringBuilder sb, BinaryExpression binary)
    {
        string value = EmitExpression(sb, binary.Right);

        if (binary.Left is IdentifierExpression id)
        {
            EmitVariableAssignment(sb, id.Name, value);
        }
        else if (binary.Left is MemberExpression member)
        {
            EmitMemberVariableAssignment(sb, member, value, GetExpressionType(binary.Right));
        }
        else if (binary.Left is IndexExpression index)
        {
            EmitIndexAssignment(sb, index, value);
        }
        else
        {
            throw new NotImplementedException(
                $"Assignment target not implemented for expression type: {binary.Left.GetType().Name}");
        }

        return value;
    }

    /// <summary>
    /// Emits bit clear: left &amp; ~right (flags 'but' operator).
    /// </summary>
    private string EmitBitClear(StringBuilder sb, BinaryExpression binary)
    {
        string left = EmitExpression(sb, binary.Left);
        string right = EmitExpression(sb, binary.Right);

        TypeInfo? type = GetExpressionType(binary.Left);
        string llvmType = type != null ? GetLLVMType(type) : "i64";

        string inverted = NextTemp();
        EmitLine(sb, $"  {inverted} = xor {llvmType} {right}, -1");
        string result = NextTemp();
        EmitLine(sb, $"  {result} = and {llvmType} {left}, {inverted}");
        return result;
    }

    /// <summary>
    /// Checks whether a binary expression is a flags combination (both operands are FlagsTypeInfo).
    /// </summary>
    private bool IsFlagsBinaryOp(BinaryExpression binary)
    {
        return GetExpressionType(binary.Left) is FlagsTypeInfo;
    }

    /// <summary>
    /// Emits flags combination: left | right (bitwise OR of two flags values).
    /// </summary>
    private string EmitFlagsCombine(StringBuilder sb, BinaryExpression binary)
    {
        string left = EmitExpression(sb, binary.Left);
        string right = EmitExpression(sb, binary.Right);
        string result = NextTemp();
        EmitLine(sb, $"  {result} = or i64 {left}, {right}");
        return result;
    }

    /// <summary>
    /// Emits 'in' / 'notin' by calling the right operand's $contains / $notcontains method.
    /// </summary>
    private string EmitContainsCall(StringBuilder sb, BinaryExpression binary, string methodName)
    {
        // 'x in collection' → collection.$contains(x)
        string collection = EmitExpression(sb, binary.Right);
        string element = EmitExpression(sb, binary.Left);

        TypeInfo? collectionType = GetExpressionType(binary.Right);
        if (collectionType == null)
        {
            throw new InvalidOperationException("Cannot determine collection type for 'in'/'notin' operator");
        }

        string methodFullName = $"{collectionType.Name}.{methodName}";
        RoutineInfo? method = _registry.LookupRoutine(methodFullName);
        // For generic type instances (e.g., Set[S64]), try the generic base name
        if (method == null && collectionType.Name.Contains('['))
        {
            string baseName = collectionType.Name[..collectionType.Name.IndexOf('[')];
            method = _registry.LookupRoutine($"{baseName}.{methodName}");
        }
        // Fall back to LookupMethod which handles module-qualified and protocol lookups
        method ??= _registry.LookupMethod(collectionType, methodName);

        var argValues = new List<string> { collection, element };
        var argTypes = new List<string> { GetParameterLLVMType(collectionType) };

        TypeInfo? elemType = GetExpressionType(binary.Left);
        argTypes.Add(elemType != null ? GetLLVMType(elemType) : "i64");

        string mangledName;
        if (method != null && collectionType.IsGenericResolution &&
            method.OwnerType != null && method.OwnerType.IsGenericDefinition)
        {
            mangledName = Q($"{collectionType.FullName}.{SanitizeLLVMName(method.Name)}");
            RecordMonomorphization(mangledName, method, collectionType);
        }
        else
        {
            mangledName = method != null
                ? MangleFunctionName(method)
                : Q($"{collectionType.FullName}.{SanitizeLLVMName(methodName)}");
        }

        if (method != null)
            GenerateFunctionDeclaration(method);

        string result = NextTemp();
        string args = BuildCallArgs(argTypes, argValues);
        EmitLine(sb, $"  {result} = call i1 @{mangledName}({args})");
        return result;
    }

    /// <summary>
    /// Returns a compile-time constant value.
    /// </summary>
    private static string EmitCompileTimeConstant(string value)
    {
        return value;
    }

    /// <summary>
    /// Generates code for a unary operation.
    /// Minus and BitwiseNot are emitted as method calls to $neg / $bitnot
    /// so the stdlib bodies (which call LLVM intrinsics) do the actual work.
    /// </summary>
    private string EmitUnaryOp(StringBuilder sb, UnaryExpression unary)
    {
        return unary.Operator switch
        {
            UnaryOperator.Not => EmitLogicalNot(sb, unary),
            UnaryOperator.Minus => EmitUnaryMethodCall(sb, unary, "$neg"),
            UnaryOperator.BitwiseNot => EmitBitwiseNot(sb, unary),
            UnaryOperator.Steal => EmitExpression(sb, unary.Operand),
            UnaryOperator.ForceUnwrap => EmitForceUnwrap(sb, unary),
            _ => throw new NotImplementedException(
                $"Unary operator '{unary.Operator}' codegen not implemented")
        };
    }

    /// <summary>
    /// Emits logical not: xor i1 %val, true.
    /// </summary>
    private string EmitLogicalNot(StringBuilder sb, UnaryExpression unary)
    {
        string operand = EmitExpression(sb, unary.Operand);
        string result = NextTemp();
        EmitLine(sb, $"  {result} = xor i1 {operand}, true");
        return result;
    }

    /// <summary>
    /// Emits bitwise NOT: xor %val, -1 for integer types, falls back to method call for others.
    /// </summary>
    private string EmitBitwiseNot(StringBuilder sb, UnaryExpression unary)
    {
        string operand = EmitExpression(sb, unary.Operand);
        TypeInfo? operandType = GetExpressionType(unary.Operand);
        if (operandType != null)
        {
            string llvmType = GetLLVMType(operandType);
            if (llvmType.StartsWith("i") && llvmType != "i1")
            {
                string result = NextTemp();
                EmitLine(sb, $"  {result} = xor {llvmType} {operand}, -1");
                return result;
            }
        }
        return EmitUnaryMethodCall(sb, unary, "$bitnot");
    }

    /// <summary>
    /// Emits a unary operator as a method call (e.g., -x → x.$neg(), ~x → x.$bitnot()).
    /// </summary>
    private string EmitUnaryMethodCall(StringBuilder sb, UnaryExpression unary, string methodName)
    {
        string operand = EmitExpression(sb, unary.Operand);
        TypeInfo? operandType = GetExpressionType(unary.Operand);

        if (operandType == null)
        {
            throw new InvalidOperationException(
                $"Cannot determine operand type for unary operator '{unary.Operator}'");
        }

        string methodFullName = $"{operandType.Name}.{methodName}";
        RoutineInfo? method = _registry.LookupRoutine(methodFullName);

        // Build call: receiver (me) is the only argument
        var argValues = new List<string> { operand };
        var argTypes = new List<string> { GetParameterLLVMType(operandType) };

        string mangledName = method != null
            ? MangleFunctionName(method)
            : Q($"{operandType.Name}.{SanitizeLLVMName(methodName)}");

        string returnType = method?.ReturnType != null
            ? GetLLVMType(method.ReturnType)
            : GetLLVMType(operandType);

        string result = NextTemp();
        string args = BuildCallArgs(argTypes, argValues);
        EmitLine(sb, $"  {result} = call {returnType} @{mangledName}({args})");
        return result;
    }

    #region Generic Method Calls (C21)

    /// <summary>
    /// Generates code for a generic method call expression.
    /// Handles LLVM intrinsic routines (CallingConvention == "llvm") by emitting
    /// LLVM IR instructions directly, and regular generic calls by resolving type
    /// arguments and calling the mangled function.
    /// </summary>
    private string EmitGenericMethodCall(StringBuilder sb, GenericMethodCallExpression generic)
    {
        // Check for generic type constructor call: TypeName[T](args)
        // e.g., Snatched[U8](ptr_value) → just pass through the argument
        if (generic.Object is IdentifierExpression id && id.Name == generic.MethodName)
        {
            TypeInfo? calledType = LookupTypeInCurrentModule(id.Name)
                                  ?? (id.ResolvedType as TypeInfo);
            if (calledType != null)
            {
                // Resolve type arguments (apply type substitutions for monomorphization)
                var resolvedTypeArgs = new List<TypeInfo>();
                foreach (var ta in generic.TypeArguments)
                {
                    TypeInfo? resolved = ResolveTypeArgument(ta);
                    if (resolved != null) resolvedTypeArgs.Add(resolved);
                }

                // Resolve the full generic type (e.g., List + [Letter] → List[Letter])
                TypeInfo resolvedFullType = calledType;
                if (calledType.IsGenericDefinition && resolvedTypeArgs.Count == calledType.GenericParameters!.Count)
                    resolvedFullType = _registry.GetOrCreateResolution(calledType, resolvedTypeArgs);

                // Collection literal constructor: List[S64](1, 2, 3), Dict[S64, Text](1:"a"), etc.
                // Prefer SA-resolved type (handles const generics like ValueList[S64, 4] correctly)
                if (generic.IsCollectionLiteral)
                {
                    TypeInfo collType = (generic.ResolvedType as TypeInfo) ?? resolvedFullType;
                    return EmitCollectionLiteralConstructor(sb, collType, generic.Arguments);
                }

                // Named-field construction: Type[T](field1: val1, field2: val2, ...)
                // e.g., List[T](data: ..., count: ..., capacity: ...) during monomorphization
                if (generic.Arguments.Count > 1 && resolvedFullType is EntityTypeInfo resolvedEntity)
                {
                    return EmitEntityConstruction(sb, resolvedEntity, generic.Arguments.Cast<Expression>().ToList());
                }
                if (generic.Arguments.Count > 1 && resolvedFullType is RecordTypeInfo resolvedRecord)
                {
                    return EmitRecordConstruction(sb, resolvedRecord, generic.Arguments.Cast<Expression>().ToList());
                }

                // Single-field named construction: Type[T](field: val)
                // Only when the single named arg matches the entity/record's single field
                if (generic.Arguments.Count == 1 && generic.Arguments[0] is NamedArgumentExpression singleNamed)
                {
                    if (resolvedFullType is EntityTypeInfo singleEntity
                        && singleEntity.MemberVariables.Count == 1
                        && singleEntity.MemberVariables[0].Name == singleNamed.Name)
                    {
                        return EmitEntityConstruction(sb, singleEntity, generic.Arguments.Cast<Expression>().ToList());
                    }
                    if (resolvedFullType is RecordTypeInfo singleRecord
                        && singleRecord.MemberVariables.Count == 1
                        && singleRecord.MemberVariables[0].Name == singleNamed.Name)
                    {
                        return EmitRecordConstruction(sb, singleRecord, generic.Arguments.Cast<Expression>().ToList());
                    }
                }

                // For single-arg generic type calls, try $create first (e.g., List[T](capacity))
                if (generic.Arguments.Count == 1)
                {
                    // Try $create overload (e.g., List[SortedDict[S64, Text]].$create(capacity: U64))
                    TypeInfo? singleArgType = GetExpressionType(generic.Arguments[0]);
                    if (singleArgType != null)
                    {
                        var singleArgTypes = new List<TypeInfo> { singleArgType };
                        string createNameFull = $"{resolvedFullType.Name}.$create";
                        RoutineInfo? creator = _registry.LookupRoutineOverload(createNameFull, singleArgTypes);
                        // Fall back to generic definition's $create
                        if (creator == null && calledType.IsGenericDefinition)
                        {
                            string createNameBase = $"{calledType.Name}.$create";
                            creator = _registry.LookupRoutineOverload(createNameBase, singleArgTypes);
                        }
                        if (creator != null)
                        {
                            // Unwrap named argument for emission
                            var argExpr = generic.Arguments[0] is NamedArgumentExpression namedArg
                                ? namedArg.Value : (Expression)generic.Arguments[0];
                            string argVal = EmitExpression(sb, argExpr);
                            string argLlvm = GetLLVMType(singleArgType);

                            // Use resolved type name for generic resolutions (same as zero-arg path)
                            string funcName;
                            string firstParamType = creator.Parameters.Count > 0 ? creator.Parameters[0].Type.Name : "";
                            if (resolvedFullType.IsGenericResolution)
                            {
                                funcName = Q($"{resolvedFullType.FullName}.$create#{firstParamType}");
                                RecordMonomorphization(funcName, creator, resolvedFullType);
                            }
                            else
                            {
                                funcName = MangleFunctionName(creator);
                            }

                            // Ensure declared
                            string retType = resolvedFullType is EntityTypeInfo ? "ptr" :
                                (creator.ReturnType != null ? GetLLVMType(ResolveTypeSubstitution(creator.ReturnType)) : "ptr");
                            if (!_generatedFunctions.Contains(funcName))
                            {
                                EmitLine(_functionDeclarations, $"declare {retType} @{funcName}({argLlvm})");
                                _generatedFunctions.Add(funcName);
                            }
                            string createResult = NextTemp();
                            EmitLine(sb, $"  {createResult} = call {retType} @{funcName}({argLlvm} {argVal})");
                            return createResult;
                        }
                    }

                    // For @llvm types (like Snatched[T] → ptr), the constructor is identity
                    string argValue = EmitExpression(sb, generic.Arguments[0]);
                    string targetType = GetLLVMType(resolvedFullType);
                    TypeInfo? argTypeForCast = GetExpressionType(generic.Arguments[0]);
                    string argLlvmType = argTypeForCast != null ? GetLLVMType(argTypeForCast) : targetType;

                    // If types match, identity. Otherwise, cast.
                    if (argLlvmType == targetType)
                        return argValue;

                    // Single-field record wrapping a ptr (e.g., Viewed[T] = { ptr })
                    // Use insertvalue to wrap the argument into the struct
                    if (targetType.StartsWith("%Record.") && argLlvmType == "ptr")
                    {
                        string result = NextTemp();
                        EmitLine(sb, $"  {result} = insertvalue {targetType} undef, ptr {argValue}, 0");
                        return result;
                    }

                    // inttoptr / ptrtoint / bitcast as needed
                    string cast = NextTemp();
                    if (targetType == "ptr" && argLlvmType != "ptr")
                        EmitLine(sb, $"  {cast} = inttoptr {argLlvmType} {argValue} to ptr");
                    else if (targetType != "ptr" && argLlvmType == "ptr")
                        EmitLine(sb, $"  {cast} = ptrtoint ptr {argValue} to {targetType}");
                    else
                        EmitLine(sb, $"  {cast} = bitcast {argLlvmType} {argValue} to {targetType}");
                    return cast;
                }

                // Zero-arg constructor: look up $create() or return zeroinitializer
                if (generic.Arguments.Count == 0)
                {
                    // Use the already-resolved type from above
                    TypeInfo resolvedType = resolvedFullType;

                    // Try to find $create() — first on resolved type, then on generic definition
                    string createName = $"{resolvedType.Name}.$create";
                    RoutineInfo? creator = _registry.LookupRoutineOverload(createName, new List<TypeInfo>());
                    // If we got a non-zero-arg overload, it's not what we want for zero-arg construction
                    if (creator != null && creator.Parameters.Count > 0)
                        creator = null;
                    // Fall back to generic definition's $create
                    if (creator == null)
                    {
                        string genCreateName = $"{calledType.Name}.$create";
                        creator = _registry.LookupRoutineOverload(genCreateName, new List<TypeInfo>());
                        if (creator != null && creator.Parameters.Count > 0)
                            creator = null;
                    }
                    if (creator != null)
                    {
                        // For resolved generic types, use the resolved mangled name
                        string funcName;
                        if (resolvedType.IsGenericResolution)
                        {
                            funcName = Q($"{resolvedType.FullName}.$create");
                            // Record for monomorphization
                            RecordMonomorphization(funcName, creator, resolvedType);
                        }
                        else
                        {
                            funcName = MangleFunctionName(creator);
                        }
                        // Ensure declared
                        string retType = creator.ReturnType != null ? GetLLVMType(creator.ReturnType) : "ptr";
                        if (!_generatedFunctions.Contains(funcName))
                        {
                            EmitLine(_functionDeclarations, $"declare {retType} @{funcName}()");
                            _generatedFunctions.Add(funcName);
                        }
                        string result = NextTemp();
                        EmitLine(sb, $"  {result} = call {retType} @{funcName}()");
                        return result;
                    }
                    return "zeroinitializer";
                }
            }
        }

        // Compiler intrinsics for generic standalone functions
        string baseName = generic.Object is IdentifierExpression baseId ? baseId.Name : generic.MethodName;
        string typeArgName = generic.TypeArguments.Count > 0 ? generic.TypeArguments[0].Name : "";
        // Resolve type arg — check type substitutions first (for monomorphization), then registry
        TypeInfo? typeArg = null;
        if (typeArgName.Length > 0)
        {
            if (_typeSubstitutions != null && _typeSubstitutions.TryGetValue(typeArgName, out var sub))
                typeArg = sub;
            typeArg ??= _registry.LookupType(typeArgName);
        }

        // rf_invalidate[T](ptr) → free the memory at the pointer
        if (baseName == "rf_invalidate" && generic.Arguments.Count == 1)
        {
            string addr = EmitExpression(sb, generic.Arguments[0]);
            TypeInfo? addrType = GetExpressionType(generic.Arguments[0]);
            string addrLlvm = addrType != null ? GetLLVMType(addrType) : "ptr";
            if (addrLlvm == "ptr")
            {
                EmitLine(sb, $"  call void @rf_invalidate(ptr {addr})");
            }
            else
            {
                string asPtr = NextTemp();
                EmitLine(sb, $"  {asPtr} = inttoptr {addrLlvm} {addr} to ptr");
                EmitLine(sb, $"  call void @rf_invalidate(ptr {asPtr})");
            }
            return "undef";
        }

        // rf_address_of[T](entity) → ptrtoint ptr to Address (i64)
        if (baseName == "rf_address_of" && generic.Arguments.Count == 1)
        {
            string val = EmitExpression(sb, generic.Arguments[0]);
            string result = NextTemp();
            EmitLine(sb, $"  {result} = ptrtoint ptr {val} to i64");
            return result;
        }

        // snatched_none[T]() → null pointer
        if (baseName == "snatched_none" && generic.Arguments.Count == 0)
            return "null";

        // Resolve the receiver type and look up the method
        TypeInfo? receiverType = GetExpressionType(generic.Object);

        // Try method lookup: "Type.MethodName" for methods, or standalone "MethodName"
        string methodFullName = receiverType != null
            ? $"{receiverType.Name}.{generic.MethodName}"
            : generic.MethodName;

        RoutineInfo? method = _registry.LookupRoutine(methodFullName)
                              ?? _registry.LookupRoutine(generic.MethodName);

        // For generic type instances (e.g., Snatched[Point].obtain_as), try the generic base name
        if (method == null && receiverType != null && receiverType.Name.Contains('['))
        {
            string genericBase = receiverType.Name[..receiverType.Name.IndexOf('[')];
            method = _registry.LookupRoutine($"{genericBase}.{generic.MethodName}");
        }
        if (method == null && receiverType != null)
        {
            method = _registry.LookupMethod(receiverType, generic.MethodName);
        }

        // If this is an LLVM intrinsic, emit directly as LLVM IR
        if (method is { CallingConvention: "llvm" })
        {
            return EmitLlvmIntrinsicGenericCall(sb, generic, method);
        }

        // Otherwise, emit as a regular generic method call
        return EmitRegularGenericMethodCall(sb, generic, method, receiverType);
    }

    /// <summary>
    /// Emits an LLVM intrinsic generic call by resolving type arguments to LLVM types
    /// and delegating to EmitLlvmInstruction.
    /// </summary>
    private string EmitLlvmIntrinsicGenericCall(StringBuilder sb, GenericMethodCallExpression generic, RoutineInfo method)
    {
        // Evaluate all arguments
        var args = new List<string>();
        foreach (var arg in generic.Arguments)
        {
            args.Add(EmitExpression(sb, arg));
        }

        // Also evaluate the receiver if this is a method call (it becomes 'me')
        string? receiver = null;
        if (generic.Object is not IdentifierExpression)
        {
            receiver = EmitExpression(sb, generic.Object);
        }

        // Resolve type arguments to LLVM types
        var llvmTypeArgs = new List<string>();
        foreach (var typeArg in generic.TypeArguments)
        {
            llvmTypeArgs.Add(ResolveTypeExpressionToLLVM(typeArg));
        }

        if (llvmTypeArgs.Count == 0)
        {
            throw new InvalidOperationException(
                $"LLVM intrinsic call to '{generic.MethodName}' requires type arguments");
        }

        string llvmType = llvmTypeArgs[0];

        // Get the LLVM instruction name (may differ from routine name via @llvm_ir annotation)
        string instrName = GetLlvmIrName(method);

        // Build full arg list: receiver first if method call, then explicit args
        var allArgs = new List<string>();
        if (receiver != null)
        {
            allArgs.Add(receiver);
        }
        allArgs.AddRange(args);

        // All LLVM intrinsics use template molds with {holes} for substitution
        return EmitFromTemplate(sb, instrName, method, llvmTypeArgs, allArgs);
    }

    /// <summary>
    /// Emits LLVM IR from a template mold string with {hole} substitution.
    /// Supports multi-line templates (for overflow intrinsics, etc.).
    /// </summary>
    /// <param name="sb">StringBuilder to emit code to.</param>
    /// <param name="mold">The template mold string with {holes}.</param>
    /// <param name="method">The routine info for generic parameter name resolution.</param>
    /// <param name="llvmTypeArgs">Resolved LLVM type arguments.</param>
    /// <param name="args">Emitted argument values.</param>
    /// <returns>The last {result} temp, or args[0] if no {result} in any line.</returns>
    private string EmitFromTemplate(StringBuilder sb, string mold, RoutineInfo method,
        List<string> llvmTypeArgs, List<string> args)
    {
        string[] lines = mold.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string? lastResult = null;
        string? prevResult = null;
        string? firstResult = null;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            string currentResult = NextTemp();
            bool hasResult = line.Contains("{result}");

            // Perform substitutions
            string substituted = line;

            // {result} → current SSA temp
            substituted = substituted.Replace("{result}", currentResult);

            // {prev} → previous line's {result}
            if (prevResult != null)
                substituted = substituted.Replace("{prev}", prevResult);

            // {first} → first line's {result} (useful for alloca patterns)
            if (firstResult != null)
                substituted = substituted.Replace("{first}", firstResult);

            // Named type parameters from GenericParameters: {T}, {From}, {To}, etc.
            // Must be done before parameter names to avoid collisions (e.g. {T} vs {type})
            if (method.GenericParameters != null)
            {
                for (int i = 0; i < method.GenericParameters.Count && i < llvmTypeArgs.Count; i++)
                {
                    string paramName = method.GenericParameters[i];
                    substituted = substituted.Replace($"{{{paramName}}}", llvmTypeArgs[i]);

                    // {sizeof T} → byte size (only compute when pattern present)
                    string sizeofPattern = $"{{sizeof {paramName}}}";
                    if (substituted.Contains(sizeofPattern))
                        substituted = substituted.Replace(sizeofPattern,
                            (GetTypeBitWidth(llvmTypeArgs[i]) / 8).ToString());
                }
            }

            // Named parameter substitution: {paramName} → args[i]
            // Parameter names come from method.Parameters, args are positional
            for (int i = 0; i < method.Parameters.Count && i < args.Count; i++)
            {
                string paramName = method.Parameters[i].Name;
                substituted = substituted.Replace($"{{{paramName}}}", args[i]);
            }

            EmitLine(sb, $"  {substituted}");

            if (hasResult)
            {
                firstResult ??= currentResult;
                prevResult = currentResult;
                lastResult = currentResult;
            }
        }

        // Return last {result} temp, or first arg if no {result} in template
        return lastResult ?? (args.Count > 0 ? args[0] : "undef");
    }

    /// <summary>
    /// Emits a regular (non-LLVM-intrinsic) generic method call.
    /// Handles both single-generic (owner-level T) and double-generic (method-level U) calls.
    /// </summary>
    private string EmitRegularGenericMethodCall(StringBuilder sb, GenericMethodCallExpression generic,
        RoutineInfo? method, TypeInfo? receiverType)
    {
        // Evaluate the receiver
        string receiver = EmitExpression(sb, generic.Object);

        // Build argument list: receiver first (becomes 'me'), then explicit arguments
        var argValues = new List<string> { receiver };
        var argTypes = new List<string> { receiverType != null ? GetParameterLLVMType(receiverType) : "ptr" };

        foreach (var arg in generic.Arguments)
        {
            string value = EmitExpression(sb, arg);
            argValues.Add(value);

            TypeInfo? argType = GetExpressionType(arg);
            if (argType == null)
            {
                throw new InvalidOperationException(
                    $"Cannot determine type for argument in generic method call to '{generic.MethodName}'");
            }
            argTypes.Add(GetLLVMType(argType));
        }

        // Resolve method-level type arguments (e.g., U → S32)
        // method.GenericParameters may include both owner-level (T) and method-level (U) params.
        // generic.TypeArguments only contains method-level type args from the call site.
        // Skip owner-level params to match positionally with the call-site type args.
        var resolvedMethodTypeArgNames = new List<string>();
        Dictionary<string, TypeInfo>? methodTypeArgs = null;
        if (method?.GenericParameters != null && generic.TypeArguments.Count > 0)
        {
            // Determine which generic params are owner-level (from the type definition)
            var ownerGenericParams = new HashSet<string>();
            if (method.OwnerType?.GenericParameters != null)
            {
                foreach (var gp in method.OwnerType.GenericParameters)
                    ownerGenericParams.Add(gp);
            }

            // Only method-level params (not owner-level) should match the call-site type args
            var methodLevelParams = method.GenericParameters
                .Where(gp => !ownerGenericParams.Contains(gp))
                .ToList();

            methodTypeArgs = new Dictionary<string, TypeInfo>();
            for (int i = 0; i < methodLevelParams.Count && i < generic.TypeArguments.Count; i++)
            {
                string taName = generic.TypeArguments[i].Name;
                TypeInfo? resolved = null;
                if (_typeSubstitutions != null && _typeSubstitutions.TryGetValue(taName, out var sub))
                    resolved = sub;
                resolved ??= _registry.LookupType(taName);
                if (resolved != null)
                {
                    methodTypeArgs[methodLevelParams[i]] = resolved;
                    resolvedMethodTypeArgNames.Add(resolved.Name);
                }
                else
                {
                    resolvedMethodTypeArgNames.Add(taName);
                }
            }
        }

        // Build the mangled name — include method type args for double-generic calls
        string methodNamePart = SanitizeLLVMName(generic.MethodName);
        if (resolvedMethodTypeArgNames.Count > 0)
        {
            methodNamePart += $"[{string.Join(", ", resolvedMethodTypeArgNames)}]";
        }

        string mangledName;
        if (receiverType != null)
        {
            mangledName = Q($"{receiverType.FullName}.{methodNamePart}");
        }
        else
        {
            mangledName = Q(methodNamePart);
        }

        // Record monomorphization for generic resolution types
        if (receiverType != null && method != null &&
            (receiverType.IsGenericResolution || method.OwnerType is GenericParameterTypeInfo))
        {
            RecordMonomorphization(mangledName, method, receiverType, methodTypeArgs);
        }

        // Resolve return type — substitute both owner-level and method-level type params
        TypeInfo? resolvedReturnType = method?.ReturnType;
        if (resolvedReturnType != null && methodTypeArgs != null)
        {
            // Direct method-level substitution (e.g., return type U → S32)
            if (methodTypeArgs.TryGetValue(resolvedReturnType.Name, out var retSub))
                resolvedReturnType = retSub;
            // Generic resolution return type with method-level type args (e.g., Snatched[U] → Snatched[S32])
            else if (resolvedReturnType is { IsGenericDefinition: true, GenericParameters: not null })
            {
                var typeArgs = resolvedReturnType.GenericParameters
                    .Select(gp =>
                    {
                        if (methodTypeArgs.TryGetValue(gp, out var s)) return s;
                        if (_typeSubstitutions != null && _typeSubstitutions.TryGetValue(gp, out var s2)) return s2;
                        return _registry.LookupType(gp);
                    })
                    .Where(t => t != null)
                    .ToList();
                if (typeArgs.Count == resolvedReturnType.GenericParameters.Count)
                    resolvedReturnType = _registry.GetOrCreateResolution(resolvedReturnType, typeArgs!);
            }
            else if (resolvedReturnType is { IsGenericResolution: true, TypeArguments: not null })
            {
                bool anySubstituted = false;
                var substitutedArgs = new List<TypeInfo>();
                foreach (var arg in resolvedReturnType.TypeArguments)
                {
                    if (methodTypeArgs.TryGetValue(arg.Name, out var argSub))
                    {
                        substitutedArgs.Add(argSub);
                        anySubstituted = true;
                    }
                    else if (_typeSubstitutions != null && _typeSubstitutions.TryGetValue(arg.Name, out var argSub2))
                    {
                        substitutedArgs.Add(argSub2);
                        anySubstituted = true;
                    }
                    else
                    {
                        substitutedArgs.Add(arg);
                    }
                }
                if (anySubstituted)
                {
                    TypeInfo? genericBase = resolvedReturnType switch
                    {
                        RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition,
                        EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition,

                        _ => null
                    };
                    if (genericBase == null)
                    {
                        string baseName = resolvedReturnType.Name.Contains('[')
                            ? resolvedReturnType.Name[..resolvedReturnType.Name.IndexOf('[')]
                            : resolvedReturnType.Name;
                        genericBase = _registry.LookupType(baseName);
                    }
                    if (genericBase != null)
                        resolvedReturnType = _registry.GetOrCreateResolution(genericBase, substitutedArgs);
                }
            }
        }
        // Also handle owner-level substitution for return type
        if (resolvedReturnType is GenericParameterTypeInfo && receiverType is { IsGenericResolution: true, TypeArguments: not null })
        {
            TypeInfo? ownerGenericDef = receiverType switch
            {
                RecordTypeInfo r => r.GenericDefinition,
                EntityTypeInfo e => e.GenericDefinition,

                _ => null
            };
            if (ownerGenericDef?.GenericParameters != null)
            {
                int paramIndex = ownerGenericDef.GenericParameters.ToList().IndexOf(resolvedReturnType.Name);
                if (paramIndex >= 0 && paramIndex < receiverType.TypeArguments.Count)
                    resolvedReturnType = receiverType.TypeArguments[paramIndex];
            }
        }

        string returnType = resolvedReturnType != null
            ? GetLLVMType(resolvedReturnType)
            : "void";

        // Ensure the function is declared
        if (!_generatedFunctions.Contains(mangledName))
        {
            string declRetType = returnType;
            EmitLine(_functionDeclarations, $"declare {declRetType} @{mangledName}({string.Join(", ", argTypes)})");
            _generatedFunctions.Add(mangledName);
        }

        if (returnType == "void")
        {
            string args = BuildCallArgs(argTypes, argValues);
            EmitLine(sb, $"  call void @{mangledName}({args})");
            return "undef";
        }
        else
        {
            string result = NextTemp();
            string args = BuildCallArgs(argTypes, argValues);
            EmitLine(sb, $"  {result} = call {returnType} @{mangledName}({args})");
            return result;
        }
    }

    /// <summary>
    /// Gets the LLVM IR instruction name for a routine, checking for @llvm_ir("name") annotation.
    /// Falls back to the routine name if no annotation is present.
    /// </summary>
    private static string GetLlvmIrName(RoutineInfo routine)
    {
        return routine.LlvmIrTemplate ?? routine.Name;
    }

    /// <summary>
    /// Resolves a TypeExpression (AST node) to its LLVM type string.
    /// </summary>
    private string ResolveTypeExpressionToLLVM(TypeExpression typeExpr)
    {
        // Apply type substitutions first (e.g., T → Letter during monomorphization)
        if (_typeSubstitutions != null && _typeSubstitutions.TryGetValue(typeExpr.Name, out var sub))
        {
            return GetLLVMType(sub);
        }

        // Look up the type in the registry
        TypeInfo? type = _registry.LookupType(typeExpr.Name);
        if (type != null)
        {
            // If this is a generic definition with generic arguments, resolve them
            if (type.IsGenericDefinition && typeExpr.GenericArguments is { Count: > 0 })
            {
                // Try full-name lookup first (handles const generic args like ValueBitList[8])
                string fullName = $"{typeExpr.Name}[{string.Join(", ", typeExpr.GenericArguments.Select(g => g.Name))}]";
                TypeInfo? fullType = _registry.LookupType(fullName);
                if (fullType != null)
                    return GetLLVMType(fullType);

                var resolvedArgs = new List<TypeInfo>();
                foreach (var ga in typeExpr.GenericArguments)
                {
                    TypeInfo? resolved = null;
                    if (_typeSubstitutions != null && _typeSubstitutions.TryGetValue(ga.Name, out var gaSub))
                        resolved = gaSub;
                    resolved ??= _registry.LookupType(ga.Name);
                    if (resolved != null) resolvedArgs.Add(resolved);
                }
                if (resolvedArgs.Count == type.GenericParameters!.Count)
                {
                    var resolvedType = _registry.GetOrCreateResolution(type, resolvedArgs);
                    return GetLLVMType(resolvedType);
                }
            }
            return GetLLVMType(type);
        }

        // Try module-qualified lookup (e.g., SortedDict → Collections.SortedDict)
        type = LookupTypeInCurrentModule(typeExpr.Name);
        if (type != null)
            return GetLLVMType(type);

        // During monomorphization, the AST rewriter may produce rewritten type names
        // (e.g., SortedDict[S64, S64]) that match a type substitution value
        if (_typeSubstitutions != null)
        {
            foreach (var sub2 in _typeSubstitutions.Values)
            {
                if (sub2.Name == typeExpr.Name)
                    return GetLLVMType(sub2);
            }
        }

        // Fall back: return the name as-is (assumes it's already an LLVM type name)
        return typeExpr.Name;
    }

    /// <summary>
    /// Gets the return type of a generic method call expression.
    /// </summary>
    private TypeInfo? GetGenericMethodCallReturnType(GenericMethodCallExpression generic)
    {
        // Check for generic type constructor call: TypeName[T](args)
        // Parser produces GenericMethodCallExpression where Object and MethodName
        // are both the type name (e.g., Snatched[U8](x) → Object=Snatched, MethodName=Snatched)
        if (generic.Object is IdentifierExpression id && id.Name == generic.MethodName)
        {
            TypeInfo? calledType = LookupTypeInCurrentModule(id.Name);
            if (calledType != null)
            {
                // Resolve generic type arguments to get the concrete type (e.g., List[Letter])
                if (calledType.IsGenericDefinition && generic.TypeArguments.Count > 0)
                {
                    var typeArgs = new List<TypeInfo>();
                    foreach (var ta in generic.TypeArguments)
                    {
                        TypeInfo? resolved = ResolveTypeArgument(ta);
                        resolved ??= _registry.LookupType(ta.Name);
                        if (resolved != null) typeArgs.Add(resolved);
                    }
                    if (typeArgs.Count == calledType.GenericParameters!.Count)
                        calledType = _registry.GetOrCreateResolution(calledType, typeArgs);
                }
                return calledType;
            }
        }

        TypeInfo? receiverType = GetExpressionType(generic.Object);

        string methodFullName = receiverType != null
            ? $"{receiverType.Name}.{generic.MethodName}"
            : generic.MethodName;

        RoutineInfo? method = _registry.LookupRoutine(methodFullName)
                              ?? _registry.LookupRoutine(generic.MethodName);

        // For generic type instances, try the generic base name
        if (method == null && receiverType != null && receiverType.Name.Contains('['))
        {
            string genericBase = receiverType.Name[..receiverType.Name.IndexOf('[')];
            method = _registry.LookupRoutine($"{genericBase}.{generic.MethodName}");
        }
        if (method == null && receiverType != null)
        {
            method = _registry.LookupMethod(receiverType, generic.MethodName);
        }

        // Representable pattern: obj.TypeName[Args]() → type conversion returning the resolved generic type
        if (method == null)
        {
            TypeInfo? representableGenType = _registry.LookupType(generic.MethodName);
            if (representableGenType != null && representableGenType.IsGenericDefinition &&
                generic.TypeArguments.Count > 0)
            {
                var typeArgs = new List<TypeInfo>();
                foreach (var ta in generic.TypeArguments)
                {
                    TypeInfo? resolved = null;
                    if (_typeSubstitutions != null && _typeSubstitutions.TryGetValue(ta.Name, out var sub))
                        resolved = sub;
                    resolved ??= _registry.LookupType(ta.Name);
                    if (resolved != null) typeArgs.Add(resolved);
                }
                if (typeArgs.Count == representableGenType.GenericParameters!.Count)
                    return _registry.GetOrCreateResolution(representableGenType, typeArgs);
            }
            if (representableGenType != null) return representableGenType;
            return null;
        }

        // Build substitution map for generic parameters from call-site type args
        TypeInfo? returnType = method.ReturnType;
        if (returnType != null && method.GenericParameters is { Count: > 0 })
        {
            var callSubstitutions = new Dictionary<string, TypeInfo>();

            // Separate owner-level from method-level generic params
            var ownerGenericParams = new HashSet<string>();
            if (method.OwnerType?.GenericParameters != null)
                foreach (var gp in method.OwnerType.GenericParameters)
                    ownerGenericParams.Add(gp);

            var methodLevelParams = method.GenericParameters
                .Where(gp => !ownerGenericParams.Contains(gp))
                .ToList();

            // Map method-level params to call-site type args
            for (int i = 0; i < methodLevelParams.Count && i < generic.TypeArguments.Count; i++)
            {
                string taName = generic.TypeArguments[i].Name;
                TypeInfo? resolved = null;
                if (_typeSubstitutions != null && _typeSubstitutions.TryGetValue(taName, out var sub))
                    resolved = sub;
                resolved ??= _registry.LookupType(taName);
                if (resolved != null)
                    callSubstitutions[methodLevelParams[i]] = resolved;
            }

            // Include owner-level substitutions from _typeSubstitutions
            if (_typeSubstitutions != null)
                foreach (var (key, value) in _typeSubstitutions)
                    callSubstitutions.TryAdd(key, value);

            // Also include owner-level substitutions from receiver type arguments
            if (receiverType is { IsGenericResolution: true, TypeArguments: not null })
            {
                TypeInfo? ownerGenericDef = receiverType switch
                {
                    RecordTypeInfo r => r.GenericDefinition,
                    EntityTypeInfo e => e.GenericDefinition,

                    _ => null
                };
                if (ownerGenericDef?.GenericParameters != null)
                {
                    for (int i = 0; i < ownerGenericDef.GenericParameters.Count && i < receiverType.TypeArguments.Count; i++)
                        callSubstitutions.TryAdd(ownerGenericDef.GenericParameters[i], receiverType.TypeArguments[i]);
                }
            }

            // Case 1: Return type is directly a generic parameter (e.g., U → S64)
            if (callSubstitutions.TryGetValue(returnType.Name, out var directSub))
            {
                returnType = directSub;
            }
            // Case 2: Return type is a generic resolution with unresolved params (e.g., Snatched[U])
            else if (returnType.IsGenericResolution && returnType.TypeArguments != null)
            {
                bool needsResolution = false;
                var resolvedArgs = new List<TypeInfo>();
                foreach (var ta in returnType.TypeArguments)
                {
                    if (callSubstitutions.TryGetValue(ta.Name, out var argSub))
                    {
                        resolvedArgs.Add(argSub);
                        needsResolution = true;
                    }
                    else
                    {
                        resolvedArgs.Add(ta);
                    }
                }
                if (needsResolution)
                {
                    TypeInfo? genericBase = returnType switch
                    {
                        RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition,
                        EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition,

                        _ => null
                    };
                    genericBase ??= returnType.Name.Contains('[')
                        ? _registry.LookupType(returnType.Name[..returnType.Name.IndexOf('[')])
                        : null;
                    if (genericBase != null)
                        returnType = _registry.GetOrCreateResolution(genericBase, resolvedArgs);
                }
            }
        }

        return returnType;
    }

    #endregion

    /// <summary>
    /// Resolves unsubstituted generic parameters in a type through _typeSubstitutions.
    /// E.g., during monomorphization with {U→S64}: Snatched[U] → Snatched[S64], U → S64.
    /// </summary>
    private TypeInfo ApplyTypeSubstitutions(TypeInfo type)
    {
        if (_typeSubstitutions == null) return type;

        // Direct generic parameter substitution (e.g., U → S64)
        if (_typeSubstitutions.TryGetValue(type.Name, out var sub))
            return sub;

        // Generic resolution with unresolved params (e.g., Snatched[U] → Snatched[S64])
        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            bool needsResolution = false;
            var resolvedArgs = new List<TypeInfo>();
            foreach (var ta in type.TypeArguments)
            {
                if (_typeSubstitutions.TryGetValue(ta.Name, out var argSub))
                {
                    resolvedArgs.Add(argSub);
                    needsResolution = true;
                }
                else if (ta is { IsGenericResolution: true, TypeArguments: not null })
                {
                    // Recursively resolve nested generics (e.g., BTreeListNode[T] → BTreeListNode[S64])
                    var innerResolved = ApplyTypeSubstitutions(ta);
                    resolvedArgs.Add(innerResolved);
                    if (innerResolved != ta) needsResolution = true;
                }
                else if (ta is { IsGenericDefinition: true, GenericParameters: not null } and not EntityTypeInfo)
                {
                    // Generic definition with resolvable params (e.g., DictEntry in List[DictEntry]
                    // when _typeSubstitutions has {K: S64, V: Text} → resolve to DictEntry[S64, Text]).
                    // Skip entity types — they are always ptr and resolution is unnecessary.
                    bool canResolve = true;
                    var innerArgs = new List<TypeInfo>();
                    foreach (var param in ta.GenericParameters)
                    {
                        if (_typeSubstitutions.TryGetValue(param, out var paramSub))
                            innerArgs.Add(paramSub);
                        else
                        {
                            canResolve = false;
                            break;
                        }
                    }
                    if (canResolve)
                    {
                        resolvedArgs.Add(_registry.GetOrCreateResolution(ta, innerArgs));
                        needsResolution = true;
                    }
                    else
                    {
                        resolvedArgs.Add(ta);
                    }
                }
                else
                {
                    resolvedArgs.Add(ta);
                }
            }
            if (needsResolution)
            {
                TypeInfo? genericBase = type switch
                {
                    RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition,
                    EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition,
                    ErrorHandlingTypeInfo { GenericDefinition: not null } eh => eh.GenericDefinition,
                    ProtocolTypeInfo { GenericDefinition: not null } p => p.GenericDefinition,
                    _ => null
                };
                genericBase ??= type.Name.Contains('[')
                    ? _registry.LookupType(type.Name[..type.Name.IndexOf('[')])
                    : null;
                if (genericBase != null)
                    return _registry.GetOrCreateResolution(genericBase, resolvedArgs);
            }
        }

        // Generic definition with resolvable params at top level (e.g., List when _typeSubstitutions
        // has {T: S64} → List[S64]). This happens when the semantic analyzer assigns a bare generic
        // definition as a variable's type instead of a generic resolution (e.g., return type of
        // conversion constructor in a generic body).
        if (type is { IsGenericDefinition: true, GenericParameters: not null })
        {
            bool canResolve = true;
            var resolvedArgs = new List<TypeInfo>();
            foreach (var param in type.GenericParameters)
            {
                if (_typeSubstitutions.TryGetValue(param, out var paramSub))
                    resolvedArgs.Add(paramSub);
                else
                {
                    canResolve = false;
                    break;
                }
            }
            if (canResolve && resolvedArgs.Count > 0)
                return _registry.GetOrCreateResolution(type, resolvedArgs);
        }

        // Tuple types with unresolved generic params in element types (e.g., Tuple[U64, T] → Tuple[U64, S64])
        if (type is TupleTypeInfo tuple)
        {
            bool needsResolution = false;
            var resolvedElems = new List<TypeInfo>();
            foreach (var elem in tuple.ElementTypes)
            {
                if (_typeSubstitutions.TryGetValue(elem.Name, out var elemSub))
                {
                    resolvedElems.Add(elemSub);
                    needsResolution = true;
                }
                else
                {
                    resolvedElems.Add(elem);
                }
            }
            if (needsResolution)
            {
                return new TupleTypeInfo(resolvedElems);
            }
        }

        return type;
    }

    /// <summary>
    /// Resolves the type of an identifier expression, handling const generic values
    /// (numeric identifiers from monomorphization like "8" from N→8).
    /// </summary>
    private TypeInfo? ResolveIdentifierType(IdentifierExpression id)
    {
        if (_localVariables.TryGetValue(id.Name, out var varType))
            return ApplyTypeSubstitutions(varType);
        var regVar = _registry.LookupVariable(id.Name);
        if (regVar != null)
            return ApplyTypeSubstitutions(regVar.Type);
        // Const generic values after AST rewriting: "8", "4u64", etc.
        if (id.Name.Length > 0 && char.IsDigit(id.Name[0])
            && _typeSubstitutions != null)
        {
            foreach (var sub in _typeSubstitutions.Values)
            {
                if (sub is ConstGenericValueTypeInfo constVal && constVal.Name == id.Name)
                    return ResolveConstGenericUnderlyingType(constVal);
            }
        }
        return null;
    }

    /// <summary>
    /// Resolves a <see cref="ConstGenericValueTypeInfo"/> to its underlying primitive type
    /// for method dispatch. E.g., a const generic value "8" with constraint "N is U64"
    /// resolves to the U64 type so that method calls like N.$represent() work correctly.
    /// </summary>
    private TypeInfo ResolveConstGenericUnderlyingType(ConstGenericValueTypeInfo constVal)
    {
        // Use explicit type if available (e.g., "4u64" → U64)
        string typeName = constVal.ExplicitTypeName ?? "U64"; // Default to U64 for untyped integer const generics
        return _registry.LookupType(typeName) ?? constVal;
    }

    /// <summary>
    /// Resolves a type name that appears as a method receiver after monomorphization.
    /// E.g., after GenericAstRewriter substitutes T→S64, "S64.data_size()" needs to resolve "S64" as a type.
    /// Checks: registry lookup, module-qualified lookup, and type substitution values.
    /// </summary>
    private TypeInfo? ResolveTypeNameAsReceiver(string name)
    {
        // Direct registry lookup (handles simple names like S64, Text, Letter)
        var type = LookupTypeInCurrentModule(name);
        if (type != null) return type;

        // During monomorphization, check if this name matches any substituted type
        // (handles generic instances like SortedDict[S64, S64] that may not be in the registry by bare name)
        if (_typeSubstitutions != null)
        {
            foreach (var sub in _typeSubstitutions.Values)
            {
                // Skip const generic values — they are runtime values, not types for method dispatch
                if (sub is ConstGenericValueTypeInfo) continue;
                if (sub.Name == name) return sub;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the type of an expression (from semantic analysis metadata).
    /// </summary>
    private TypeInfo? GetExpressionType(Expression expr)
    {
        // For index expressions during monomorphization, prefer structure-based inference.
        // ResolvedType may contain ambiguous generic params (e.g., T) that map to the wrong
        // substitution when the collection's element type differs from the outer type param.
        if (expr is IndexExpression indexExpr && _typeSubstitutions != null)
        {
            var structureType = GetIndexReturnType(indexExpr);
            if (structureType != null)
                return structureType;
        }

        // First, check if the semantic analyzer has already resolved the type
        if (expr.ResolvedType != null)
        {
            // During monomorphization, resolve unsubstituted generic params (e.g., Snatched[U] → Snatched[S64])
            var resolved = ApplyTypeSubstitutions(expr.ResolvedType);
            // If the type is still an unresolved generic parameter, fall through to the
            // expression-specific resolution which can use call-site type arguments
            if (resolved is not GenericParameterTypeInfo)
            {
                // Const generic values resolve to their underlying primitive type for method dispatch
                if (resolved is ConstGenericValueTypeInfo constVal)
                    return ResolveConstGenericUnderlyingType(constVal);
                return resolved;
            }
        }

        // Fall back to inferring from the expression structure
        return expr switch
        {
            LiteralExpression literal => GetLiteralType(literal),
            IdentifierExpression id => ResolveIdentifierType(id),
            MemberExpression member => GetMemberType(member),
            CreatorExpression ctor => LookupTypeInCurrentModule(ctor.TypeName),
            BinaryExpression binary => GetBinaryExpressionType(binary),
            ChainedComparisonExpression => _registry.LookupType("Bool"), // Comparisons return Bool
            UnaryExpression unary => GetUnaryExpressionType(unary),
            CallExpression call => GetCallReturnType(call),
            GenericMethodCallExpression generic => GetGenericMethodCallReturnType(generic),
            IndexExpression index => GetIndexReturnType(index),
            NamedArgumentExpression named => GetExpressionType(named.Value),
            DictEntryLiteralExpression dictEntry => dictEntry.ResolvedType,
            ConditionalExpression cond => GetExpressionType(cond.TrueExpression),
            GenericMemberExpression gme => GetGenericMemberExpressionType(gme),
            _ => null
        };
    }

    /// <summary>
    /// Gets the return type of an index expression by looking up $getitem on the target type.
    /// </summary>
    private TypeInfo? GetUnaryExpressionType(UnaryExpression unary)
    {
        TypeInfo? operandType = GetExpressionType(unary.Operand);
        if (unary.Operator == UnaryOperator.ForceUnwrap && operandType != null)
        {
            // Force-unwrap: return the value type inside the Maybe/ErrorHandling wrapper
            if (operandType is ErrorHandlingTypeInfo eh)
                return eh.ValueType;
            if (operandType.Name.StartsWith("Maybe[") && operandType.TypeArguments is { Count: 1 })
                return operandType.TypeArguments[0];
        }
        return operandType;
    }

    private TypeInfo? GetBinaryExpressionType(BinaryExpression binary)
    {
        return binary.Operator is BinaryOperator.Equal or BinaryOperator.NotEqual
            or BinaryOperator.Less or BinaryOperator.LessEqual
            or BinaryOperator.Greater or BinaryOperator.GreaterEqual
            or BinaryOperator.And or BinaryOperator.Or
            or BinaryOperator.Identical or BinaryOperator.NotIdentical
            or BinaryOperator.In or BinaryOperator.NotIn
            ? _registry.LookupType("Bool")
            : GetExpressionType(binary.Left);
    }

    /// <summary>
    /// Gets the type of a GenericMemberExpression (member access + indexing).
    /// </summary>
    private TypeInfo? GetGenericMemberExpressionType(GenericMemberExpression gme)
    {
        // Get the type of the object
        TypeInfo? objType = GetExpressionType(gme.Object);
        if (objType == null) return null;

        // Find the member variable
        IReadOnlyList<MemberVariableInfo>? memberVars = objType switch
        {
            EntityTypeInfo e => e.MemberVariables,
            RecordTypeInfo r => r.MemberVariables,
            _ => null
        };
        var memberVar = memberVars?.FirstOrDefault(mv => mv.Name == gme.MemberName);
        if (memberVar?.Type == null) return null;

        // The member's type has type arguments — the first one is the element type
        TypeInfo memberType = memberVar.Type;
        if (memberType.TypeArguments is { Count: > 0 })
            return memberType.TypeArguments[0];

        // Try $getitem on the member type
        RoutineInfo? getItem = _registry.LookupRoutine($"{memberType.Name}.$getitem")
            ?? _registry.LookupRoutine($"{memberType.Name}.$getitem!");
        return getItem?.ReturnType;
    }

    /// <summary>
    /// Gets the return type of an index expression by looking up $getitem on the target type.
    /// </summary>
    private TypeInfo? GetIndexReturnType(IndexExpression index)
    {
        TypeInfo? targetType = GetExpressionType(index.Object);
        if (targetType == null) return null;

        // Try exact name, then with ! suffix, then generic base name
        string typeName = targetType.Name;
        RoutineInfo? getItem = _registry.LookupRoutine($"{typeName}.$getitem")
            ?? _registry.LookupRoutine($"{typeName}.$getitem!");
        if (getItem == null && typeName.Contains('['))
        {
            string baseName = typeName[..typeName.IndexOf('[')];
            getItem = _registry.LookupRoutine($"{baseName}.$getitem")
                ?? _registry.LookupRoutine($"{baseName}.$getitem!");
        }

        if (getItem?.ReturnType == null) return null;

        // Substitute generic return type params with concrete types from the target.
        // Prefer target type arguments over _typeSubstitutions to avoid ambiguous param names
        // (e.g., List[BTreeListNode[S64]].$getitem returns T, but the outer T maps to S64 —
        //  the correct resolution is BTreeListNode[S64] from the list's type args, not S64).
        TypeInfo returnType = getItem.ReturnType;
        if (targetType.TypeArguments is { Count: > 0 } && getItem.OwnerType?.GenericParameters is { Count: > 0 })
        {
            // Map generic params to concrete args (e.g., T → BTreeListNode[S64] for List[BTreeListNode[S64]].$getitem)
            var genParams = getItem.OwnerType.GenericParameters;
            for (int i = 0; i < genParams.Count && i < targetType.TypeArguments.Count; i++)
            {
                if (returnType.Name == genParams[i])
                    return targetType.TypeArguments[i];
            }
        }
        // Fallback: use _typeSubstitutions for simple generic params
        if (_typeSubstitutions != null && _typeSubstitutions.TryGetValue(returnType.Name, out var sub))
            return sub;
        return returnType;
    }

    /// <summary>
    /// Gets the return type of a call expression.
    /// </summary>
    private TypeInfo? GetCallReturnType(CallExpression call)
    {
        switch (call.Callee)
        {
            case MemberExpression member:
            {
                // Qualified method call: resolve receiver type, look up Type.method
                TypeInfo? receiverType = GetExpressionType(member.Object);
                if (receiverType != null)
                {
                    string methodFullName = $"{receiverType.Name}.{member.PropertyName}";
                    var method = _registry.LookupRoutine(methodFullName);
                    // For generic resolutions (e.g., Snatched[Letter].offset), try base name
                    if (method == null && receiverType.Name.Contains('['))
                    {
                        string baseName = receiverType.Name[..receiverType.Name.IndexOf('[')];
                        method = _registry.LookupRoutine($"{baseName}.{member.PropertyName}");
                    }
                    // Fallback: LookupMethod handles generic-param-owner methods (e.g., T.get_address)
                    if (method == null)
                    {
                        method = _registry.LookupMethod(receiverType, member.PropertyName);
                    }
                    if (method?.ReturnType != null)
                    {
                        // Substitute generic type params in return type (e.g., T → Letter)
                        if (_typeSubstitutions != null && _typeSubstitutions.TryGetValue(method.ReturnType.Name, out var sub))
                            return sub;

                        // For generic resolution receivers (e.g., Snatched[U8].read() → T should become U8),
                        // substitute using the receiver's type arguments when no _typeSubstitutions available
                        if (receiverType is { IsGenericResolution: true, TypeArguments: not null }
                            && method.ReturnType is GenericParameterTypeInfo)
                        {
                            // Find the generic parameter index in the owner type's generic parameters
                            TypeInfo? ownerGenericDef = receiverType switch
                            {
                                RecordTypeInfo r => r.GenericDefinition,
                                EntityTypeInfo e => e.GenericDefinition,

                                _ => null
                            };
                            if (ownerGenericDef?.GenericParameters != null)
                            {
                                int paramIndex = ownerGenericDef.GenericParameters.ToList().IndexOf(method.ReturnType.Name);
                                if (paramIndex >= 0 && paramIndex < receiverType.TypeArguments.Count)
                                    return receiverType.TypeArguments[paramIndex];
                            }
                        }

                        // For parameterized return types (e.g., Snatched[T] → Snatched[Letter]),
                        // resolve through receiver's type arguments even without _typeSubstitutions
                        if (receiverType is { IsGenericResolution: true, TypeArguments: not null }
                            && method.ReturnType is { IsGenericResolution: true, TypeArguments: not null })
                        {
                            TypeInfo? ownerGenericDef = receiverType switch
                            {
                                RecordTypeInfo r => r.GenericDefinition,
                                EntityTypeInfo e => e.GenericDefinition,

                                _ => null
                            };
                            if (ownerGenericDef?.GenericParameters != null)
                            {
                                var paramSubs = new Dictionary<string, TypeInfo>();
                                for (int i = 0; i < ownerGenericDef.GenericParameters.Count && i < receiverType.TypeArguments.Count; i++)
                                    paramSubs[ownerGenericDef.GenericParameters[i]] = receiverType.TypeArguments[i];

                                bool anyResolved = false;
                                var resolvedArgs = new List<TypeInfo>();
                                foreach (var ta in method.ReturnType.TypeArguments)
                                {
                                    if (paramSubs.TryGetValue(ta.Name, out var resolved))
                                    {
                                        resolvedArgs.Add(resolved);
                                        anyResolved = true;
                                    }
                                    else
                                    {
                                        resolvedArgs.Add(ta);
                                    }
                                }
                                if (anyResolved)
                                {
                                    string baseName = method.ReturnType.Name;
                                    int bracketIdx = baseName.IndexOf('[');
                                    if (bracketIdx > 0) baseName = baseName[..bracketIdx];
                                    var genericDef = _registry.LookupType(baseName);
                                    if (genericDef != null)
                                        return _registry.GetOrCreateResolution(genericDef, resolvedArgs);
                                }
                            }
                        }

                        // Fallback: substitute type arguments using _typeSubstitutions
                        if (_typeSubstitutions != null && method.ReturnType.IsGenericResolution
                            && method.ReturnType.TypeArguments != null)
                        {
                            var substitutedArgs = new List<TypeInfo>();
                            bool anySubstituted = false;
                            foreach (var typeArg in method.ReturnType.TypeArguments)
                            {
                                if (_typeSubstitutions.TryGetValue(typeArg.Name, out var resolvedArg))
                                {
                                    substitutedArgs.Add(resolvedArg);
                                    anySubstituted = true;
                                }
                                else
                                {
                                    substitutedArgs.Add(typeArg);
                                }
                            }
                            if (anySubstituted)
                            {
                                string baseName = method.ReturnType.Name;
                                int bracketIdx = baseName.IndexOf('[');
                                if (bracketIdx > 0) baseName = baseName[..bracketIdx];
                                var genericDef = _registry.LookupType(baseName);
                                if (genericDef != null)
                                    return _registry.GetOrCreateResolution(genericDef, substitutedArgs);
                            }
                        }
                        return method.ReturnType;
                    }
                }
                // Representable pattern: obj.TypeName() → TypeName.$create(from: obj)
                // If the method name matches a registered type, the return type is that type.
                // Strip '!' suffix for failable conversions (e.g., index.U64!() → U64)
                string conversionLookup = member.PropertyName.EndsWith('!') ? member.PropertyName[..^1] : member.PropertyName;
                TypeInfo? representableType = _registry.LookupType(conversionLookup);
                if (representableType != null)
                    return representableType;

                // Fall back to unqualified lookup
                var fallback = _registry.LookupRoutine(member.PropertyName);
                return fallback?.ReturnType;
            }
            case IdentifierExpression id:
            {
                // Strip failable '!' suffix for lookup
                string callName = id.Name.EndsWith('!') ? id.Name[..^1] : id.Name;
                // Try direct routine lookup first
                var routine = _registry.LookupRoutine(callName)
                              ?? _registry.LookupRoutineByName(callName);
                if (routine?.ReturnType != null)
                    return routine.ReturnType;
                // If name matches a type, it's a creator call — returns that type
                TypeInfo? calledType = LookupTypeInCurrentModule(id.Name);
                if (calledType != null)
                {
                    var creator = _registry.LookupRoutine($"{calledType.Name}.$create");
                    return creator?.ReturnType ?? calledType;
                }
                return null;
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Gets the type of a literal expression from its token type.
    /// </summary>
    private TypeInfo? GetLiteralType(LiteralExpression literal)
    {
        string? typeName = literal.LiteralType switch
        {
            Lexer.TokenType.S8Literal => "S8",
            Lexer.TokenType.S16Literal => "S16",
            Lexer.TokenType.S32Literal => "S32",
            Lexer.TokenType.S64Literal => "S64",
            Lexer.TokenType.S128Literal => "S128",
            Lexer.TokenType.U8Literal => "U8",
            Lexer.TokenType.U16Literal => "U16",
            Lexer.TokenType.U32Literal => "U32",
            Lexer.TokenType.U64Literal => "U64",
            Lexer.TokenType.U128Literal => "U128",
            Lexer.TokenType.F16Literal => "F16",
            Lexer.TokenType.F32Literal => "F32",
            Lexer.TokenType.F64Literal => "F64",
            Lexer.TokenType.F128Literal => "F128",
            Lexer.TokenType.D32Literal => "D32",
            Lexer.TokenType.D64Literal => "D64",
            Lexer.TokenType.D128Literal => "D128",
            Lexer.TokenType.True or Lexer.TokenType.False => "Bool",
            Lexer.TokenType.TextLiteral => "Text",
            Lexer.TokenType.LetterLiteral => "Letter",
            Lexer.TokenType.ByteLetterLiteral => "Byte",
            _ => null
        };

        return typeName != null ? _registry.LookupType(typeName) : null;
    }

    /// <summary>
    /// Gets the type of a member access expression.
    /// </summary>
    private TypeInfo? GetMemberType(MemberExpression member)
    {
        TypeInfo? targetType = GetExpressionType(member.Object);
        // Fallback: if SA didn't set ResolvedType (type-as-identifier), try type lookup by name
        if (targetType == null && member.Object is IdentifierExpression typeId)
            targetType = LookupTypeInCurrentModule(typeId.Name);
        if (targetType == null) return null;

        // Refresh stale generic entity resolutions for member variable lookup
        if (targetType is EntityTypeInfo { IsGenericResolution: true, MemberVariables.Count: 0, GenericDefinition: { MemberVariables.Count: > 0 } genDef } staleEntity
            && staleEntity.TypeArguments != null)
        {
            var refreshed = genDef.CreateInstance(staleEntity.TypeArguments) as EntityTypeInfo;
            if (refreshed != null && refreshed.MemberVariables.Count > 0)
                targetType = refreshed;
        }

        // Choice/Flags member access returns the type itself
        if (targetType is ChoiceTypeInfo or FlagsTypeInfo)
            return targetType;

        MemberVariableInfo? memberVariable = targetType switch
        {
            EntityTypeInfo e => e.LookupMemberVariable(member.PropertyName),
            RecordTypeInfo r => r.LookupMemberVariable(member.PropertyName),
            _ => null
        };

        TypeInfo? memberType = memberVariable?.Type;
        if (memberType != null && targetType is { IsGenericResolution: true, TypeArguments: not null })
            memberType = ResolveGenericMemberType(memberType, targetType);
        return memberType;
    }

    #endregion

    /// <summary>
    /// Gets the bit width of an LLVM type.
    /// </summary>
    /// <summary>
    /// Gets the element type from a List[T] entity by parsing the type parameter.
    /// </summary>
    private TypeInfo? GetListElementType(EntityTypeInfo listEntity)
    {
        string name = listEntity.Name;
        int bracketStart = name.IndexOf('[');
        int bracketEnd = name.LastIndexOf(']');
        if (bracketStart < 0 || bracketEnd <= bracketStart) return null;
        string elemTypeName = name[(bracketStart + 1)..bracketEnd];
        return _registry.LookupType(elemTypeName);
    }

    private int GetTypeBitWidth(string llvmType)
    {
        return llvmType switch
        {
            "i1" => 1,
            "i8" => 8,
            "i16" => 16,
            "i32" => 32,
            "i64" => 64,
            "i128" => 128,
            "half" => 16,
            "float" => 32,
            "double" => 64,
            "fp128" => 128,
            "ptr" => _pointerBitWidth,
            _ => throw new InvalidOperationException($"Unknown LLVM type for bitwidth: {llvmType}")
        };
    }

    #region Additional Expression Types

    /// <summary>
    /// Generates code for a conditional (ternary) expression.
    /// </summary>
    private string EmitConditional(StringBuilder sb, ConditionalExpression cond)
    {
        string condition = EmitExpression(sb, cond.Condition);

        string thenLabel = NextLabel("cond_then");
        string elseLabel = NextLabel("cond_else");
        string endLabel = NextLabel("cond_end");

        EmitLine(sb, $"  br i1 {condition}, label %{thenLabel}, label %{elseLabel}");

        // Then branch
        EmitLine(sb, $"{thenLabel}:");
        string thenValue = EmitExpression(sb, cond.TrueExpression);
        EmitLine(sb, $"  br label %{endLabel}");

        // Else branch
        EmitLine(sb, $"{elseLabel}:");
        string elseValue = EmitExpression(sb, cond.FalseExpression);
        EmitLine(sb, $"  br label %{endLabel}");

        // Merge with phi
        EmitLine(sb, $"{endLabel}:");
        string result = NextTemp();
        TypeInfo? resultType = GetExpressionType(cond.TrueExpression);
        if (resultType == null)
        {
            throw new InvalidOperationException("Cannot determine type for conditional expression");
        }
        string llvmType = GetLLVMType(resultType);
        EmitLine(sb, $"  {result} = phi {llvmType} [ {thenValue}, %{thenLabel} ], [ {elseValue}, %{elseLabel} ]");

        return result;
    }

    /// <summary>
    /// Generates code for an index access expression (e.g., list[i]).
    /// </summary>
    private string EmitIndexAccess(StringBuilder sb, IndexExpression index)
    {
        // Resolve target type to decide dispatch strategy
        TypeInfo? targetType = GetExpressionType(index.Object);

        // If the type has a $getitem method, dispatch to it
        if (targetType != null)
        {
            string methodFullName = $"{targetType.Name}.$getitem";
            RoutineInfo? getItem = _registry.LookupRoutine(methodFullName)
                ?? _registry.LookupRoutine($"{targetType.Name}.$getitem!");

            // For generic resolutions (e.g., List[S64]), also try the generic definition name
            if (getItem == null && targetType.IsGenericResolution)
            {
                string? genDefName = targetType switch
                {
                    EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition.Name,
                    RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition.Name,
                    _ => null
                };
                if (genDefName != null)
                {
                    getItem = _registry.LookupRoutine($"{genDefName}.$getitem")
                        ?? _registry.LookupRoutine($"{genDefName}.$getitem!");
                }
            }
            if (getItem != null)
            {
                // Emit as method call: obj.$getitem(index) or obj.$getitem!(index)
                // Use the actual method name (may be failable with ! suffix).
                // For generic definitions on generic resolution types (e.g., List[S64].$getitem!),
                // EmitMethodCall handles monomorphization automatically.
                var member = new MemberExpression(
                    Object: index.Object,
                    PropertyName: getItem.Name,
                    Location: index.Location);
                return EmitMethodCall(sb, member, new List<Expression> { index.Index });
            }

            // For entity types with a list-like first field (e.g., Text has letters: List[Letter]),
            // inline $getitem as: load list ptr → GEP data → load element
            if (getItem != null && targetType is EntityTypeInfo entity && entity.MemberVariables.Count > 0)
            {
                var firstField = entity.MemberVariables[0];
                if (firstField.Type is EntityTypeInfo listType && listType.Name.StartsWith("List["))
                {
                    string target = EmitExpression(sb, index.Object);
                    string indexValue = EmitExpression(sb, index.Index);

                    // GEP to get the list pointer field
                    string entityTypeName = GetEntityTypeName(entity);
                    string listFieldPtr = NextTemp();
                    EmitLine(sb, $"  {listFieldPtr} = getelementptr {entityTypeName}, ptr {target}, i32 0, i32 0");
                    string listPtr = NextTemp();
                    EmitLine(sb, $"  {listPtr} = load ptr, ptr {listFieldPtr}");

                    // GEP into list's data (field 0 of list entity)
                    string listEntityType = GetEntityTypeName(listType);
                    string dataFieldPtr = NextTemp();
                    EmitLine(sb, $"  {dataFieldPtr} = getelementptr {listEntityType}, ptr {listPtr}, i32 0, i32 0");
                    string dataBase = NextTemp();
                    EmitLine(sb, $"  {dataBase} = load ptr, ptr {dataFieldPtr}");

                    // Load the element
                    TypeInfo? elemType = GetListElementType(listType);
                    string elemLlvm = elemType != null ? GetLLVMType(elemType) : "i32";
                    string elemPtr = NextTemp();
                    EmitLine(sb, $"  {elemPtr} = getelementptr {elemLlvm}, ptr {dataBase}, i64 {indexValue}");
                    string loaded = NextTemp();
                    EmitLine(sb, $"  {loaded} = load {elemLlvm}, ptr {elemPtr}");
                    return loaded;
                }
            }
        }

        // For CStr indexing: pointer + offset → load byte
        if (targetType is RecordTypeInfo { Name: "CStr" })
        {
            string cstrVal = EmitExpression(sb, index.Object);
            string idxVal = EmitExpression(sb, index.Index);
            string ptr = NextTemp();
            EmitLine(sb, $"  {ptr} = extractvalue %Record.CStr {cstrVal}, 0");
            string addr = NextTemp();
            EmitLine(sb, $"  {addr} = add i64 {ptr}, {idxVal}");
            string realPtr = NextTemp();
            EmitLine(sb, $"  {realPtr} = inttoptr i64 {addr} to ptr");
            string loaded = NextTemp();
            EmitLine(sb, $"  {loaded} = load i8, ptr {realPtr}");
            return loaded;
        }

        // Fallback: raw GEP + load for pointer/contiguous-memory types
        string fallbackTarget = EmitExpression(sb, index.Object);
        string fallbackIndex = EmitExpression(sb, index.Index);

        string fallbackElemType = targetType switch
        {
            RecordTypeInfo r when r.TypeArguments is { Count: > 0 } => GetLLVMType(r.TypeArguments[0]),
            EntityTypeInfo e when e.TypeArguments is { Count: > 0 } => GetLLVMType(e.TypeArguments[0]),
            _ => throw new InvalidOperationException(
                $"Cannot determine element type for indexing on type: {targetType?.Name}")
        };

        string fallbackElemPtr = NextTemp();
        string fallbackResult = NextTemp();
        EmitLine(sb, $"  {fallbackElemPtr} = getelementptr {fallbackElemType}, ptr {fallbackTarget}, i64 {fallbackIndex}");
        EmitLine(sb, $"  {fallbackResult} = load {fallbackElemType}, ptr {fallbackElemPtr}");

        return fallbackResult;
    }

    /// <summary>
    /// Generates code for a slice expression: obj[start to end] → $getslice(start, end)
    /// </summary>
    private string EmitSliceAccess(StringBuilder sb, SliceExpression slice)
    {
        string target = EmitExpression(sb, slice.Object);
        string start = EmitExpression(sb, slice.Start);
        string end = EmitExpression(sb, slice.End);

        TypeInfo? targetType = GetExpressionType(slice.Object);
        if (targetType == null)
        {
            throw new InvalidOperationException("Cannot determine type for slice target");
        }

        string methodFullName = $"{targetType.Name}.$getslice";
        RoutineInfo? method = _registry.LookupRoutine(methodFullName);

        var argValues = new List<string> { target, start, end };
        var argTypes = new List<string> { GetParameterLLVMType(targetType), "i64", "i64" };

        string mangledName = method != null
            ? MangleFunctionName(method)
            : Q($"{targetType.Name}.$getslice");

        string returnType = method?.ReturnType != null
            ? GetLLVMType(method.ReturnType)
            : GetParameterLLVMType(targetType);

        string result = NextTemp();
        string args = BuildCallArgs(argTypes, argValues);
        EmitLine(sb, $"  {result} = call {returnType} @{mangledName}({args})");
        return result;
    }

    /// <summary>
    /// Generates code for a range expression.
    /// </summary>
    private string EmitRange(StringBuilder sb, RangeExpression range)
    {
        // Emit start, end, step expressions
        string start = EmitExpression(sb, range.Start);
        string end = EmitExpression(sb, range.End);
        string step = range.Step != null ? EmitExpression(sb, range.Step) : "1";
        // IsDescending is confusingly named: false = 'to' (inclusive), true = 'til' (exclusive)
        // Range record field 3 is 'inclusive': true for 'to', false for 'til'
        string isInclusive = range.IsDescending ? "false" : "true";

        // Infer element type from start/end expressions (Range[T] is generic)
        TypeInfo? elemType = GetExpressionType(range.Start) ?? GetExpressionType(range.End);
        string elemLlvmType = elemType != null ? GetLLVMType(elemType) : "i64";

        // Try to use registered Range type, resolved with element type
        TypeInfo? rangeGenericDef = _registry.LookupType("Range");
        string structType;
        if (rangeGenericDef != null && elemType != null)
        {
            TypeInfo resolvedRange = _registry.GetOrCreateResolution(rangeGenericDef, new List<TypeInfo> { elemType });
            structType = resolvedRange is RecordTypeInfo resolvedRecord
                ? GetRecordTypeName(resolvedRecord)
                : $"{{ {elemLlvmType}, {elemLlvmType}, {elemLlvmType}, i1 }}";
        }
        else if (rangeGenericDef is RecordTypeInfo rangeRecord)
        {
            structType = GetRecordTypeName(rangeRecord);
        }
        else
        {
            structType = $"{{ {elemLlvmType}, {elemLlvmType}, {elemLlvmType}, i1 }}";
        }

        // Build struct via insertvalue chain: { start, end, step, inclusive }
        string v0 = NextTemp();
        EmitLine(sb, $"  {v0} = insertvalue {structType} undef, {elemLlvmType} {start}, 0");
        string v1 = NextTemp();
        EmitLine(sb, $"  {v1} = insertvalue {structType} {v0}, {elemLlvmType} {end}, 1");
        string v2 = NextTemp();
        EmitLine(sb, $"  {v2} = insertvalue {structType} {v1}, {elemLlvmType} {step}, 2");
        string v3 = NextTemp();
        EmitLine(sb, $"  {v3} = insertvalue {structType} {v2}, i1 {isInclusive}, 3");

        return v3;
    }

    /// <summary>
    /// Generates code for a steal expression (ownership transfer).
    /// </summary>
    /// <remarks>
    /// The steal keyword transfers ownership from the source to the destination.
    /// At runtime, this is essentially a pass-through - the ownership tracking
    /// is handled at compile time by the semantic analyzer, which marks the
    /// source as a deadref after the steal.
    ///
    /// Stealable types:
    /// - Raw entities (ownership transferred)
    /// - Shared[T] (reference count transferred)
    /// - Tracked[T] (weak reference transferred)
    ///
    /// Non-stealable types (caught by semantic analyzer):
    /// - Scope-bound tokens (Viewed, Hijacked, Inspected, Seized)
    /// - Snatched[T] (internal ownership type)
    /// </remarks>
    private string EmitSteal(StringBuilder sb, StealExpression steal)
    {
        // Steal just evaluates the operand and passes the value through.
        // The semantic analyzer has already validated that:
        // 1. The operand is a stealable type
        // 2. The source will be marked as deadref after this point
        return EmitExpression(sb, steal.Operand);
    }

    /// <summary>
    /// Generates code for a tuple literal expression.
    /// Tuples are always inline LLVM structs built via insertvalue chain.
    /// </summary>
    private string EmitTupleLiteral(StringBuilder sb, TupleLiteralExpression tuple)
    {
        // Evaluate all element expressions
        var elemValues = new List<string>();
        var elemLLVMTypes = new List<string>();
        foreach (var element in tuple.Elements)
        {
            elemValues.Add(EmitExpression(sb, element));
            TypeInfo? elemType = GetExpressionType(element);
            elemLLVMTypes.Add(elemType != null ? GetLLVMType(elemType) : "i64");
        }

        // Resolve tuple type from semantic analysis
        TupleTypeInfo? tupleType = tuple.ResolvedType as TupleTypeInfo;

        string structType;
        if (tupleType != null)
        {
            structType = GetTupleTypeName(tupleType);
        }
        else
        {
            // Fall back to anonymous struct type
            structType = $"{{ {string.Join(", ", elemLLVMTypes)} }}";
        }

        string result = "undef";
        for (int i = 0; i < elemValues.Count; i++)
        {
            string newResult = NextTemp();
            EmitLine(sb, $"  {newResult} = insertvalue {structType} {result}, {elemLLVMTypes[i]} {elemValues[i]}, {i}");
            result = newResult;
        }

        return result;
    }

    #endregion

    #region Error Handling Operators

    /// <summary>
    /// Emits the ?? (none coalesce) operator.
    /// If the left operand (ErrorHandlingTypeInfo) is VALID, extracts its value.
    /// Otherwise evaluates and returns the right operand as default.
    /// </summary>
    private string EmitNoneCoalesce(StringBuilder sb, BinaryExpression binary)
    {
        string left = EmitExpression(sb, binary.Left);
        TypeInfo? leftType = GetExpressionType(binary.Left);

        if (leftType is not ErrorHandlingTypeInfo errorType)
        {
            throw new InvalidOperationException("'??' operator requires ErrorHandlingTypeInfo on the left");
        }

        // Use proper LLVM type for the maybe/error-handling value
        string maybeType = GetLLVMType(leftType);

        // Alloca and store the maybe value
        string allocaPtr = NextTemp();
        EmitLine(sb, $"  {allocaPtr} = alloca {maybeType}");
        EmitLine(sb, $"  store {maybeType} {left}, ptr {allocaPtr}");

        // Extract tag (field 0)
        string tagPtr = NextTemp();
        string tag = NextTemp();
        EmitLine(sb, $"  {tagPtr} = getelementptr {maybeType}, ptr {allocaPtr}, i32 0, i32 0");
        EmitLine(sb, $"  {tag} = load i64, ptr {tagPtr}");

        // Check tag == 1 (VALID)
        string isValid = NextTemp();
        EmitLine(sb, $"  {isValid} = icmp eq i64 {tag}, 1");

        string valLabel = NextLabel("coalesce_val");
        string rhsLabel = NextLabel("coalesce_rhs");
        string endLabel = NextLabel("coalesce_end");
        string leftBlock = _currentBlock;

        EmitLine(sb, $"  br i1 {isValid}, label %{valLabel}, label %{rhsLabel}");

        // Valid path: extract the value from the handle
        EmitLine(sb, $"{valLabel}:");
        _currentBlock = valLabel;
        string handlePtr = NextTemp();
        string handleVal = NextTemp();
        EmitLine(sb, $"  {handlePtr} = getelementptr {maybeType}, ptr {allocaPtr}, i32 0, i32 1");
        EmitLine(sb, $"  {handleVal} = load ptr, ptr {handlePtr}");

        // Determine the value type T
        TypeInfo valueType = errorType.ValueType;
        string llvmValueType = GetLLVMType(valueType);

        // Load T from the handle pointer
        string validValue = NextTemp();
        EmitLine(sb, $"  {validValue} = load {llvmValueType}, ptr {handleVal}");
        string valBlock = _currentBlock;
        EmitLine(sb, $"  br label %{endLabel}");

        // RHS path: evaluate the default expression
        EmitLine(sb, $"{rhsLabel}:");
        _currentBlock = rhsLabel;
        string rhsValue = EmitExpression(sb, binary.Right);
        string rhsBlock = _currentBlock;
        EmitLine(sb, $"  br label %{endLabel}");

        // Merge with PHI
        EmitLine(sb, $"{endLabel}:");
        _currentBlock = endLabel;
        string result = NextTemp();
        EmitLine(sb, $"  {result} = phi {llvmValueType} [ {validValue}, %{valBlock} ], [ {rhsValue}, %{rhsBlock} ]");

        return result;
    }

    /// <summary>
    /// Emits the !! (force unwrap) operator.
    /// If the operand (ErrorHandlingTypeInfo) is VALID, extracts its value.
    /// Otherwise traps (crashes the program).
    /// </summary>
    private string EmitForceUnwrap(StringBuilder sb, UnaryExpression unary)
    {
        string operand = EmitExpression(sb, unary.Operand);
        TypeInfo? operandType = GetExpressionType(unary.Operand);

        // Determine the value type inside the Maybe/ErrorHandling wrapper
        TypeInfo? valueType = null;
        if (operandType is ErrorHandlingTypeInfo errorType)
        {
            valueType = errorType.ValueType;
        }
        else if (operandType != null && operandType.Name.StartsWith("Maybe[") && operandType.TypeArguments is { Count: 1 })
        {
            // Handle Maybe types that were resolved as RecordTypeInfo by the registry cache
            valueType = operandType.TypeArguments[0];
        }

        if (valueType == null)
        {
            throw new InvalidOperationException($"'!!' operator requires Maybe/ErrorHandling operand, got {operandType?.GetType().Name ?? "null"}: {operandType?.Name ?? "null"}");
        }

        // Declare llvm.trap if not already declared
        if (_declaredNativeFunctions.Add("llvm.trap"))
        {
            EmitLine(_functionDeclarations, "declare void @llvm.trap() noreturn nounwind");
        }

        // Alloca and store the maybe/error-handling value using its proper LLVM type
        string maybeType = operandType != null ? GetLLVMType(operandType) : "{ i64, ptr }";
        string allocaPtr = NextTemp();
        EmitLine(sb, $"  {allocaPtr} = alloca {maybeType}");
        EmitLine(sb, $"  store {maybeType} {operand}, ptr {allocaPtr}");

        // Extract tag (field 0)
        string tagPtr = NextTemp();
        string tag = NextTemp();
        EmitLine(sb, $"  {tagPtr} = getelementptr {maybeType}, ptr {allocaPtr}, i32 0, i32 0");
        EmitLine(sb, $"  {tag} = load i64, ptr {tagPtr}");

        // Check tag == 1 (VALID)
        string isValid = NextTemp();
        EmitLine(sb, $"  {isValid} = icmp eq i64 {tag}, 1");

        string okLabel = NextLabel("unwrap_ok");
        string failLabel = NextLabel("unwrap_fail");

        EmitLine(sb, $"  br i1 {isValid}, label %{okLabel}, label %{failLabel}");

        // Fail path: trap
        EmitLine(sb, $"{failLabel}:");
        _currentBlock = failLabel;
        EmitLine(sb, $"  call void @llvm.trap()");
        EmitLine(sb, $"  unreachable");

        // OK path: extract the value from the handle
        EmitLine(sb, $"{okLabel}:");
        _currentBlock = okLabel;
        string handlePtr = NextTemp();
        string handleVal = NextTemp();
        EmitLine(sb, $"  {handlePtr} = getelementptr {maybeType}, ptr {allocaPtr}, i32 0, i32 1");
        EmitLine(sb, $"  {handleVal} = load ptr, ptr {handlePtr}");

        // For entity types, the value IS the pointer (no boxing) — return directly
        if (valueType is EntityTypeInfo)
        {
            return handleVal;
        }

        // For value types, load T from the handle pointer (heap-allocated storage)
        string llvmValueType = GetLLVMType(valueType);
        string result = NextTemp();
        EmitLine(sb, $"  {result} = load {llvmValueType}, ptr {handleVal}");

        return result;
    }

    #endregion

    #region Optional Chaining

    /// <summary>
    /// Emits optional member access (?.): obj?.field
    /// If obj is null/none, produces a zero/null value. Otherwise performs normal member access.
    /// </summary>
    private string EmitOptionalMemberAccess(StringBuilder sb, OptionalMemberExpression optMember)
    {
        string obj = EmitExpression(sb, optMember.Object);
        TypeInfo? objType = GetExpressionType(optMember.Object);

        if (objType is ErrorHandlingTypeInfo errorType)
        {
            return EmitOptionalChainErrorHandling(sb, obj, errorType, optMember.PropertyName);
        }

        // Entity (pointer): null check
        return EmitOptionalChainPointer(sb, obj, objType, optMember.PropertyName);
    }

    /// <summary>
    /// Optional chaining on a pointer-based type (entity): null check → member access or zero.
    /// </summary>
    private string EmitOptionalChainPointer(StringBuilder sb, string obj, TypeInfo? objType, string propertyName)
    {
        string nonNullLabel = NextLabel("optchain_nonnull");
        string nullLabel = NextLabel("optchain_null");
        string endLabel = NextLabel("optchain_end");
        string entryBlock = _currentBlock;

        // Null check
        string isNull = NextTemp();
        EmitLine(sb, $"  {isNull} = icmp eq ptr {obj}, null");
        EmitLine(sb, $"  br i1 {isNull}, label %{nullLabel}, label %{nonNullLabel}");

        // Non-null path: do normal member access
        EmitLine(sb, $"{nonNullLabel}:");
        _currentBlock = nonNullLabel;
        string memberValue = EmitMemberAccessOnType(sb, obj, objType, propertyName);
        string memberBlock = _currentBlock;

        // Determine result type from member access
        TypeInfo? resultType = GetMemberTypeFromOwner(objType, propertyName);
        string llvmResultType = resultType != null ? GetLLVMType(resultType) : "ptr";
        string zeroValue = resultType != null ? GetZeroValue(resultType) : "null";

        EmitLine(sb, $"  br label %{endLabel}");

        // Null path: return zero/null
        EmitLine(sb, $"{nullLabel}:");
        _currentBlock = nullLabel;
        EmitLine(sb, $"  br label %{endLabel}");

        // Merge
        EmitLine(sb, $"{endLabel}:");
        _currentBlock = endLabel;
        string result = NextTemp();
        EmitLine(sb, $"  {result} = phi {llvmResultType} [ {memberValue}, %{memberBlock} ], [ {zeroValue}, %{nullLabel} ]");

        return result;
    }

    /// <summary>
    /// Optional chaining on an ErrorHandlingTypeInfo: check VALID → extract value → member access, or zero.
    /// </summary>
    private string EmitOptionalChainErrorHandling(StringBuilder sb, string obj, ErrorHandlingTypeInfo errorType, string propertyName)
    {
        // Alloca and store the { i64, ptr } value
        string allocaPtr = NextTemp();
        EmitLine(sb, $"  {allocaPtr} = alloca {{ i64, ptr }}");
        EmitLine(sb, $"  store {{ i64, ptr }} {obj}, ptr {allocaPtr}");

        // Extract tag
        string tagPtr = NextTemp();
        string tag = NextTemp();
        EmitLine(sb, $"  {tagPtr} = getelementptr {{ i64, ptr }}, ptr {allocaPtr}, i32 0, i32 0");
        EmitLine(sb, $"  {tag} = load i64, ptr {tagPtr}");

        string isValid = NextTemp();
        EmitLine(sb, $"  {isValid} = icmp eq i64 {tag}, 1");

        string validLabel = NextLabel("optchain_valid");
        string invalidLabel = NextLabel("optchain_invalid");
        string endLabel = NextLabel("optchain_end");

        EmitLine(sb, $"  br i1 {isValid}, label %{validLabel}, label %{invalidLabel}");

        // Valid path: extract value and do member access
        EmitLine(sb, $"{validLabel}:");
        _currentBlock = validLabel;
        string handlePtr = NextTemp();
        string handleVal = NextTemp();
        EmitLine(sb, $"  {handlePtr} = getelementptr {{ i64, ptr }}, ptr {allocaPtr}, i32 0, i32 1");
        EmitLine(sb, $"  {handleVal} = load ptr, ptr {handlePtr}");

        TypeInfo valueType = errorType.ValueType;
        string llvmValueType = GetLLVMType(valueType);
        string innerValue = NextTemp();
        EmitLine(sb, $"  {innerValue} = load {llvmValueType}, ptr {handleVal}");

        // Now do member access on the extracted value
        string memberValue = EmitMemberAccessOnType(sb, innerValue, valueType, propertyName);
        string validBlock = _currentBlock;

        // Determine result type
        TypeInfo? resultType = GetMemberTypeFromOwner(valueType, propertyName);
        string llvmResultType = resultType != null ? GetLLVMType(resultType) : "ptr";
        string zeroValue = resultType != null ? GetZeroValue(resultType) : "null";

        EmitLine(sb, $"  br label %{endLabel}");

        // Invalid path
        EmitLine(sb, $"{invalidLabel}:");
        _currentBlock = invalidLabel;
        EmitLine(sb, $"  br label %{endLabel}");

        // Merge
        EmitLine(sb, $"{endLabel}:");
        _currentBlock = endLabel;
        string result = NextTemp();
        EmitLine(sb, $"  {result} = phi {llvmResultType} [ {memberValue}, %{validBlock} ], [ {zeroValue}, %{invalidLabel} ]");

        return result;
    }

    /// <summary>
    /// Performs member access on a value given its type, reusing existing member read logic.
    /// </summary>
    private string EmitMemberAccessOnType(StringBuilder sb, string value, TypeInfo? type, string propertyName)
    {
        return type switch
        {
            EntityTypeInfo entity => EmitEntityMemberVariableRead(sb, value, entity, propertyName),
            RecordTypeInfo record => EmitRecordMemberVariableRead(sb, value, record, propertyName),
            _ => throw new InvalidOperationException($"Cannot access member on type: {type?.Name}")
        };
    }

    /// <summary>
    /// Gets the type of a member variable from the owning type.
    /// </summary>
    private TypeInfo? GetMemberTypeFromOwner(TypeInfo? ownerType, string memberName)
    {
        IReadOnlyList<MemberVariableInfo>? members = ownerType switch
        {
            EntityTypeInfo entity => entity.MemberVariables,
            RecordTypeInfo record => record.MemberVariables,
            _ => null
        };

        if (members == null) return null;

        foreach (var m in members)
        {
            if (m.Name == memberName)
            {
                TypeInfo memberType = m.Type;
                if (ownerType is { IsGenericResolution: true, TypeArguments: not null })
                    memberType = ResolveGenericMemberType(memberType, ownerType);
                return memberType;
            }
        }
        return null;
    }

    /// <summary>
    /// Resolves generic type parameters in a member's type using the owner's type arguments.
    /// Handles direct params (T → Letter), parameterized types (Snatched[T] → Snatched[Letter]),
    /// and generic definitions (Snatched → Snatched[Letter]).
    /// </summary>
    private TypeInfo ResolveGenericMemberType(TypeInfo memberType, TypeInfo ownerType)
    {
        TypeInfo? ownerGenericDef = ownerType switch
        {
            RecordTypeInfo r => r.GenericDefinition,
            EntityTypeInfo e => e.GenericDefinition,
            _ => null
        };
        if (ownerGenericDef?.GenericParameters == null || ownerType.TypeArguments == null)
            return memberType;

        var subs = new Dictionary<string, TypeInfo>();
        for (int i = 0; i < ownerGenericDef.GenericParameters.Count && i < ownerType.TypeArguments.Count; i++)
            subs[ownerGenericDef.GenericParameters[i]] = ownerType.TypeArguments[i];

        // Direct parameter: memberType is T → substitute to Letter
        if (memberType is GenericParameterTypeInfo && subs.TryGetValue(memberType.Name, out var directSub))
            return directSub;

        // Parameterized: memberType is Snatched[T] or Maybe[BTreeListNode[T]] → recursively substitute
        if (memberType is { IsGenericResolution: true, TypeArguments: not null })
        {
            bool anySubstituted = false;
            var substitutedArgs = new List<TypeInfo>();
            foreach (var ta in memberType.TypeArguments)
            {
                if (subs.TryGetValue(ta.Name, out var resolved))
                {
                    substitutedArgs.Add(resolved);
                    anySubstituted = true;
                }
                else if (ta is { IsGenericResolution: true, TypeArguments: not null })
                {
                    // Recursively substitute nested generics (e.g., BTreeListNode[T] → BTreeListNode[S64])
                    var innerResolved = ResolveGenericMemberType(ta, ownerType);
                    substitutedArgs.Add(innerResolved);
                    if (innerResolved != ta) anySubstituted = true;
                }
                else
                {
                    substitutedArgs.Add(ta);
                }
            }
            if (anySubstituted)
            {
                string baseName = memberType.Name;
                int bracketIdx = baseName.IndexOf('[');
                if (bracketIdx > 0) baseName = baseName[..bracketIdx];
                var genericDef = _registry.LookupType(baseName);
                if (genericDef != null)
                    return _registry.GetOrCreateResolution(genericDef, substitutedArgs);
            }
        }

        // Generic definition: memberType is Snatched with GenericParameters → create resolution
        if (memberType is { IsGenericDefinition: true, GenericParameters: not null })
        {
            var typeArgs = memberType.GenericParameters
                .Select(gp => subs.TryGetValue(gp, out var s) ? s : _registry.LookupType(gp))
                .Where(t => t != null)
                .ToList();
            if (typeArgs.Count == memberType.GenericParameters.Count)
                return _registry.GetOrCreateResolution(memberType, typeArgs!);
        }

        return memberType;
    }

    #endregion

    #region Text Insertion (F-Strings)

    /// <summary>
    /// Emits an f-string (InsertedTextExpression).
    /// Concatenates text and expression parts via Text.$create and Text.concat calls.
    /// </summary>
    private string EmitInsertedText(StringBuilder sb, InsertedTextExpression inserted)
    {
        if (inserted.Parts.Count == 0)
        {
            return EmitStringLiteral(sb, "");
        }

        // Convert each part to a ptr (Text value)
        var partValues = new List<string>();
        foreach (var part in inserted.Parts)
        {
            switch (part)
            {
                case TextPart textPart:
                    partValues.Add(EmitStringLiteral(sb, textPart.Text));
                    break;
                case ExpressionPart exprPart:
                    partValues.Add(EmitInsertedTextPart(sb, exprPart));
                    break;
            }
        }

        if (partValues.Count == 1)
        {
            return partValues[0];
        }

        // Chain concat calls via native rf_text_concat
        if (_declaredNativeFunctions.Add("rf_text_concat"))
            EmitLine(_functionDeclarations, "declare ptr @rf_text_concat(ptr, ptr)");

        string accumulator = partValues[0];
        for (int i = 1; i < partValues.Count; i++)
        {
            string concatResult = NextTemp();
            EmitLine(sb, $"  {concatResult} = call ptr @rf_text_concat(ptr {accumulator}, ptr {partValues[i]})");
            accumulator = concatResult;
        }

        return accumulator;
    }

    /// <summary>
    /// Emits a single expression part of an f-string, handling format specifiers.
    /// Valid specifiers: null (default → $represent), "=" (name prefix + $represent),
    /// "?" ($diagnose), "=?" (name prefix + $diagnose).
    /// </summary>
    private string EmitInsertedTextPart(StringBuilder sb, ExpressionPart exprPart)
    {
        string exprValue = EmitExpression(sb, exprPart.Expression);
        TypeInfo? exprType = GetExpressionType(exprPart.Expression);
        string? formatSpec = exprPart.FormatSpec;

        bool hasName = formatSpec != null && formatSpec.Contains('=');
        bool hasDiagnose = formatSpec != null && formatSpec.Contains('?');

        // Resolve the text value via $diagnose or $represent
        string valueText = hasDiagnose
            ? EmitDiagnoseCall(sb, exprValue, exprType)
            : EmitRepresentCall(sb, exprValue, exprType);

        // Prepend "name=" prefix if = specifier is present
        if (hasName)
        {
            string varName = exprPart.Expression is IdentifierExpression id ? id.Name : "expr";
            string prefix = EmitStringLiteral(sb, $"{varName}=");

            if (_declaredNativeFunctions.Add("rf_text_concat"))
                EmitLine(_functionDeclarations, "declare ptr @rf_text_concat(ptr, ptr)");
            string result = NextTemp();
            EmitLine(sb, $"  {result} = call ptr @rf_text_concat(ptr {prefix}, ptr {valueText})");
            return result;
        }

        return valueText;
    }

    /// <summary>
    /// Emits a call to Type.$represent() to convert a value to Text.
    /// </summary>
    private string EmitRepresentCall(StringBuilder sb, string value, TypeInfo? type)
    {
        // If already Text, return directly
        if (type?.Name == "Text")
            return value;

        string typeName = type?.Name ?? "Data";
        string representName = $"{typeName}.$represent";
        RoutineInfo? representMethod = _registry.LookupRoutine(representName);
        // For generic resolutions (e.g., ValueBitList[8]), try the generic base name
        if (representMethod == null && typeName.Contains('['))
        {
            string baseName = typeName[..typeName.IndexOf('[')];
            representMethod = _registry.LookupRoutine($"{baseName}.$represent");
        }
        if (representMethod != null)
            GenerateFunctionDeclaration(representMethod);

        string mangledName = representMethod != null
            ? MangleFunctionName(representMethod)
            : Q($"{typeName}.$represent");

        // For monomorphized methods, use the resolved type name in the mangled function name
        if (representMethod != null && representMethod.IsGenericDefinition && typeName != representMethod.OwnerType?.Name)
        {
            string module = representMethod.OwnerType?.Module ?? representMethod.Module ?? "";
            string prefix = module != "" ? $"{module}." : "";
            mangledName = Q($"{prefix}{typeName}.$represent");
        }

        // Ensure the monomorphized body is compiled for generic types
        if (type != null && representMethod != null && type.IsGenericResolution)
            RecordMonomorphization(mangledName, representMethod, type);

        string argType = type != null ? GetParameterLLVMType(type) : "i64";
        string result = NextTemp();
        EmitLine(sb, $"  {result} = call ptr @{mangledName}({argType} {value})");
        return result;
    }

    /// <summary>
    /// Emits a call to Type.$diagnose() to convert a value to its diagnostic Text.
    /// </summary>
    private string EmitDiagnoseCall(StringBuilder sb, string value, TypeInfo? type)
    {
        string typeName = type?.Name ?? "Data";
        string diagnoseName = $"{typeName}.$diagnose";
        RoutineInfo? diagnoseMethod = _registry.LookupRoutine(diagnoseName);
        // For generic resolutions (e.g., ValueBitList[8]), try the generic base name
        if (diagnoseMethod == null && typeName.Contains('['))
        {
            string baseName = typeName[..typeName.IndexOf('[')];
            diagnoseMethod = _registry.LookupRoutine($"{baseName}.$diagnose");
        }
        if (diagnoseMethod != null)
            GenerateFunctionDeclaration(diagnoseMethod);

        string mangledName = diagnoseMethod != null
            ? MangleFunctionName(diagnoseMethod)
            : Q($"{typeName}.$diagnose");

        // For monomorphized methods, use the resolved type name in the mangled function name
        if (diagnoseMethod != null && diagnoseMethod.IsGenericDefinition && typeName != diagnoseMethod.OwnerType?.Name)
        {
            string module = diagnoseMethod.OwnerType?.Module ?? diagnoseMethod.Module ?? "";
            string prefix = module != "" ? $"{module}." : "";
            mangledName = Q($"{prefix}{typeName}.$diagnose");
        }

        // Ensure the monomorphized body is compiled for generic types
        if (type != null && diagnoseMethod != null && type.IsGenericResolution)
            RecordMonomorphization(mangledName, diagnoseMethod, type);

        string argType = type != null ? GetParameterLLVMType(type) : "i64";
        string result = NextTemp();
        EmitLine(sb, $"  {result} = call ptr @{mangledName}({argType} {value})");
        return result;
    }

    /// <summary>
    /// Converts a value to Text using $represent. Used by non-f-text contexts.
    /// </summary>
    private string EmitValueToText(StringBuilder sb, string value, TypeInfo? type, string? formatSpec)
    {
        return EmitRepresentCall(sb, value, type);
    }

    #endregion

    #region List Literals

    /// <summary>
    /// Emits a list literal expression: [1, 2, 3]
    /// Allocates a List entity and adds each element via add_last.
    /// </summary>
    private string EmitListLiteral(StringBuilder sb, ListLiteralExpression list)
    {
        // Determine element type from ResolvedType or first element
        TypeInfo? listType = list.ResolvedType;
        TypeInfo? elemType = null;

        if (listType is EntityTypeInfo entity && entity.TypeArguments.Count > 0)
        {
            elemType = entity.TypeArguments[0];
        }
        else if (list.Elements.Count > 0)
        {
            elemType = GetExpressionType(list.Elements[0]);
        }

        string elemLLVMType = elemType != null ? GetLLVMType(elemType) : "i64";

        // Look up List type and its constructor/add_last method
        string listTypeName = listType != null ? listType.Name : $"List[{elemType?.Name ?? "S64"}]";
        // mangledListType is no longer needed — list type name used directly via Q()

        // Allocate the list via constructor or rf_alloc
        // Try to find a $create or use a fallback allocation
        string createName = $"{listTypeName}.$create";
        RoutineInfo? createMethod = _registry.LookupRoutine(createName);
        string entityTypeName = listType is EntityTypeInfo eti ? GetEntityTypeName(eti) : $"%\"Entity.{listTypeName}\"";

        string listPtr;
        if (createMethod != null)
        {
            string mangledCreate = MangleFunctionName(createMethod);
            listPtr = NextTemp();
            EmitLine(sb, $"  {listPtr} = call ptr @{mangledCreate}()");
        }
        else
        {
            // Fallback: allocate via rf_alloc with a reasonable default size
            // List entity needs at least: count (i64) + capacity (i64) + data ptr
            listPtr = NextTemp();
            EmitLine(sb, $"  {listPtr} = call ptr @rf_allocate_dynamic(i64 24)");

            // Initialize: data = alloc(n * elem_size), count = 0, capacity = element count
            // Entity layout is { ptr (data), i64 (count), i64 (capacity) }
            int elemSize = elemType != null ? GetTypeSize(elemType) : 8;
            long capacity = Math.Max(list.Elements.Count, 4);
            string dataPtr = NextTemp();
            EmitLine(sb, $"  {dataPtr} = call ptr @rf_allocate_dynamic(i64 {capacity * elemSize})");
            string dataPtrSlot = NextTemp();
            EmitLine(sb, $"  {dataPtrSlot} = getelementptr {entityTypeName}, ptr {listPtr}, i32 0, i32 0");
            EmitLine(sb, $"  store ptr {dataPtr}, ptr {dataPtrSlot}");

            string countPtr = NextTemp();
            EmitLine(sb, $"  {countPtr} = getelementptr {entityTypeName}, ptr {listPtr}, i32 0, i32 1");
            EmitLine(sb, $"  store i64 0, ptr {countPtr}");

            string capPtr = NextTemp();
            EmitLine(sb, $"  {capPtr} = getelementptr {entityTypeName}, ptr {listPtr}, i32 0, i32 2");
            EmitLine(sb, $"  store i64 {capacity}, ptr {capPtr}");
        }

        // Add each element via add_last or direct store
        string addLastName = $"{listTypeName}.add_last";
        RoutineInfo? addLastMethod = _registry.LookupRoutine(addLastName);

        if (addLastMethod != null)
        {
            string mangledAddLast = MangleFunctionName(addLastMethod);
            foreach (var elem in list.Elements)
            {
                string elemValue = EmitExpression(sb, elem);
                EmitLine(sb, $"  call void @{mangledAddLast}(ptr {listPtr}, {elemLLVMType} {elemValue})");
            }
        }
        else
        {
            // Fallback: direct store into data buffer
            // Load data ptr (field 0), then GEP + store for each element
            string dataPtrSlot2 = NextTemp();
            EmitLine(sb, $"  {dataPtrSlot2} = getelementptr {entityTypeName}, ptr {listPtr}, i32 0, i32 0");
            string dataBase = NextTemp();
            EmitLine(sb, $"  {dataBase} = load ptr, ptr {dataPtrSlot2}");

            for (int i = 0; i < list.Elements.Count; i++)
            {
                string elemValue = EmitExpression(sb, list.Elements[i]);
                string elemPtr = NextTemp();
                EmitLine(sb, $"  {elemPtr} = getelementptr {elemLLVMType}, ptr {dataBase}, i64 {i}");
                EmitLine(sb, $"  store {elemLLVMType} {elemValue}, ptr {elemPtr}");
            }

            // Update count (field 1)
            string countPtr2 = NextTemp();
            EmitLine(sb, $"  {countPtr2} = getelementptr {entityTypeName}, ptr {listPtr}, i32 0, i32 1");
            EmitLine(sb, $"  store i64 {list.Elements.Count}, ptr {countPtr2}");
        }

        return listPtr;
    }

    private string EmitSetLiteral(StringBuilder sb, SetLiteralExpression set)
    {
        TypeInfo? setType = set.ResolvedType;
        string setTypeName = setType?.Name ?? "Set";

        string setPtr = EmitCollectionCreate(sb, setType, setTypeName);

        // Add each element via .add(element)
        foreach (var elem in set.Elements)
        {
            string elemValue = EmitExpression(sb, elem);
            TypeInfo? elemType = GetExpressionType(elem);
            string elemLLVMType = elemType != null ? GetLLVMType(elemType) : "i64";

            string addName = $"{setTypeName}.add";
            RoutineInfo? addMethod = _registry.LookupRoutine(addName);
            if (addMethod != null)
            {
                string mangledAdd = MangleFunctionName(addMethod);
                EmitLine(sb, $"  call i1 @{mangledAdd}(ptr {setPtr}, {elemLLVMType} {elemValue})");
            }
        }

        return setPtr;
    }

    private string EmitDictLiteral(StringBuilder sb, DictLiteralExpression dict)
    {
        TypeInfo? dictType = dict.ResolvedType;
        string dictTypeName = dictType?.Name ?? "Dict";

        string dictPtr = EmitCollectionCreate(sb, dictType, dictTypeName);

        // Add each pair via .add(key, value)
        foreach (var (key, value) in dict.Pairs)
        {
            string keyValue = EmitExpression(sb, key);
            string valValue = EmitExpression(sb, value);

            TypeInfo? keyType = GetExpressionType(key);
            TypeInfo? valueType = GetExpressionType(value);
            string keyLLVMType = keyType != null ? GetLLVMType(keyType) : "i64";
            string valueLLVMType = valueType != null ? GetLLVMType(valueType) : "i64";

            string addName = $"{dictTypeName}.add";
            RoutineInfo? addMethod = _registry.LookupRoutine(addName);
            if (addMethod != null)
            {
                string mangledAdd = MangleFunctionName(addMethod);
                EmitLine(sb, $"  call i1 @{mangledAdd}(ptr {dictPtr}, {keyLLVMType} {keyValue}, {valueLLVMType} {valValue})");
            }
        }

        return dictPtr;
    }

    /// <summary>
    /// C105: Packs variadic arguments into a List[T] at the call site.
    /// Modifies argValues/argTypes, replacing individual variadic args with a single list pointer.
    /// </summary>
    private void PackVariadicArgs(StringBuilder sb, RoutineInfo routine,
        List<Expression> arguments, ref List<string> argValues, ref List<string> argTypes,
        int argOffset)
    {
        ParameterInfo? varParam = routine.Parameters.FirstOrDefault(p => p.IsVariadicParam);
        if (varParam == null) return;

        int varIndex = varParam.Index;
        int packStart = varIndex + argOffset;

        // Determine element type from actual arguments or parameter type
        TypeInfo? elemType = null;
        int firstVarArgIdx = varIndex; // index into explicit arguments (not argValues)
        if (firstVarArgIdx < arguments.Count)
            elemType = GetExpressionType(arguments[firstVarArgIdx]);

        // Fall back: extract T from List[T] parameter type
        if (elemType == null && varParam.Type is { TypeArguments: { Count: > 0 } })
        {
            elemType = varParam.Type.TypeArguments[0];
            if (elemType is GenericParameterTypeInfo && _typeSubstitutions != null
                && _typeSubstitutions.TryGetValue(elemType.Name, out var resolved))
                elemType = resolved;
        }

        if (elemType == null) return;

        // Resolve List[elemType]
        TypeInfo? listGenDef = _registry.LookupType("List");
        if (listGenDef == null) return;
        TypeInfo listType = _registry.GetOrCreateResolution(listGenDef, [elemType]);

        // Create empty List[elemType]
        string listPtr = EmitCollectionCreate(sb, listType, listType.Name);

        // Look up add_last on List generic definition
        RoutineInfo? addLast = _registry.LookupRoutine($"{listType.Name}.add_last")
                             ?? _registry.LookupRoutine("List.add_last");
        if (addLast == null) return;

        // Handle monomorphization
        string mangledAdd;
        if (listType.IsGenericResolution)
        {
            mangledAdd = Q($"{listType.FullName}.add_last");
            RecordMonomorphization(mangledAdd, addLast, listType);
        }
        else
        {
            mangledAdd = MangleFunctionName(addLast);
        }

        // Declare add_last if needed
        string elemLlvm = GetLLVMType(elemType);
        if (!_generatedFunctions.Contains(mangledAdd))
        {
            EmitLine(_functionDeclarations, $"declare void @{mangledAdd}(ptr, {elemLlvm})");
            _generatedFunctions.Add(mangledAdd);
        }

        // Determine how many trailing (non-variadic) params follow the varargs param.
        // Named args matching trailing params are NOT varargs.
        int trailingParamCount = routine.Parameters.Count - varIndex - 1;
        var trailingParamNames = new HashSet<string>();
        for (int i = varIndex + 1; i < routine.Parameters.Count; i++)
            trailingParamNames.Add(routine.Parameters[i].Name);

        // Count explicit named args that match trailing params
        int namedTrailingCount = arguments.Count(a =>
            a is NamedArgumentExpression na && trailingParamNames.Contains(na.Name));
        int packEnd = argValues.Count - namedTrailingCount;

        // Emit add_last for each variadic argument
        for (int i = packStart; i < packEnd; i++)
            EmitLine(sb, $"  call void @{mangledAdd}(ptr {listPtr}, {elemLlvm} {argValues[i]})");

        // Build new argValues: [pre-varargs] + [listPtr] + [trailing defaults]
        var newArgValues = argValues.Take(packStart).ToList();
        newArgValues.Add(listPtr);
        var newArgTypes = argTypes.Take(packStart).ToList();
        newArgTypes.Add("ptr");

        // Append trailing args if they were already supplied
        for (int i = packEnd; i < argValues.Count; i++)
        {
            newArgValues.Add(argValues[i]);
            newArgTypes.Add(argTypes[i]);
        }

        // Supply defaults for trailing params not yet covered
        int suppliedTrailing = argValues.Count - packEnd;
        for (int i = varIndex + 1 + suppliedTrailing; i < routine.Parameters.Count; i++)
        {
            var param = routine.Parameters[i];
            if (param.HasDefaultValue)
            {
                string value = EmitExpression(sb, param.DefaultValue!);
                newArgValues.Add(value);
                newArgTypes.Add(GetLLVMType(param.Type));
            }
        }

        argValues = newArgValues;
        argTypes = newArgTypes;
    }

    /// <summary>
    /// Emits a zero-arg $create() call for a collection type, handling monomorphization.
    /// </summary>
    private string EmitCollectionCreate(StringBuilder sb, TypeInfo? resolvedType, string typeName)
    {
        if (resolvedType == null)
            return "null";

        // Look up $create on the resolved type first, then fall back to generic definition
        string createName = $"{resolvedType.Name}.$create";
        RoutineInfo? creator = _registry.LookupRoutineOverload(createName, new List<TypeInfo>());
        if (creator != null && creator.Parameters.Count > 0)
            creator = null;

        // Fall back to generic definition's $create
        if (creator == null)
        {
            TypeInfo? genericDef = resolvedType switch
            {
                EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition,
                RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition,
                _ => null
            };
            if (genericDef != null)
            {
                string genCreateName = $"{genericDef.Name}.$create";
                creator = _registry.LookupRoutineOverload(genCreateName, new List<TypeInfo>());
                if (creator != null && creator.Parameters.Count > 0)
                    creator = null;
            }
        }

        if (creator != null)
        {
            string funcName;
            if (resolvedType.IsGenericResolution)
            {
                funcName = Q($"{resolvedType.FullName}.$create");
                RecordMonomorphization(funcName, creator, resolvedType);
            }
            else
            {
                funcName = MangleFunctionName(creator);
            }

            // Ensure declared
            if (!_generatedFunctions.Contains(funcName))
            {
                EmitLine(_functionDeclarations, $"declare ptr @{funcName}()");
                _generatedFunctions.Add(funcName);
            }
            string result = NextTemp();
            EmitLine(sb, $"  {result} = call ptr @{funcName}()");
            return result;
        }

        // Last resort: direct allocation with zeroed fields
        string ptr = NextTemp();
        EmitLine(sb, $"  {ptr} = call ptr @rf_allocate_dynamic(i64 24)");
        return ptr;
    }

    /// <summary>
    /// Emits a standalone DictEntry literal (key:value used outside a collection constructor).
    /// Constructs a DictEntry[K, V] record via insertvalue.
    /// </summary>
    private string EmitDictEntryLiteral(StringBuilder sb, DictEntryLiteralExpression dictEntry)
    {
        string keyVal = EmitExpression(sb, dictEntry.Key);
        string valVal = EmitExpression(sb, dictEntry.Value);

        TypeInfo? entryType = dictEntry.ResolvedType;
        if (entryType == null)
            return "undef";

        string llvmType = GetLLVMType(entryType);
        string tmp1 = NextTemp();
        string tmp2 = NextTemp();
        TypeInfo? keyType = GetExpressionType(dictEntry.Key);
        TypeInfo? valueType = GetExpressionType(dictEntry.Value);
        string keyLlvm = keyType != null ? GetLLVMType(keyType) : "i64";
        string valLlvm = valueType != null ? GetLLVMType(valueType) : "i64";

        EmitLine(sb, $"  {tmp1} = insertvalue {llvmType} undef, {keyLlvm} {keyVal}, 0");
        EmitLine(sb, $"  {tmp2} = insertvalue {llvmType} {tmp1}, {valLlvm} {valVal}, 1");
        return tmp2;
    }

    /// <summary>
    /// Emits a collection literal constructor: List(1, 2, 3), Set(1, 2), Dict(1:2), etc.
    /// Creates collection via $create(), then adds elements via add/add_last.
    /// For ValueList/ValueBitList, uses insertvalue for inline array construction.
    /// </summary>
    private string EmitCollectionLiteralConstructor(StringBuilder sb, TypeInfo resolvedType, List<Expression> arguments)
    {
        string typeName = resolvedType.Name;
        // Extract base type name (e.g., "List" from "List[S64]")
        string baseName = typeName.Contains('[') ? typeName[..typeName.IndexOf('[')] : typeName;

        // ValueList[T, N]: inline array construction via insertvalue
        if (baseName == "ValueList")
        {
            string llvmType = GetLLVMType(resolvedType);
            string current = "zeroinitializer";
            for (int i = 0; i < arguments.Count; i++)
            {
                string elemVal = EmitExpression(sb, arguments[i]);
                TypeInfo? elemType = GetExpressionType(arguments[i]);
                string elemLlvm = elemType != null ? GetLLVMType(elemType) : "i64";
                string next = NextTemp();
                EmitLine(sb, $"  {next} = insertvalue {llvmType} {current}, {elemLlvm} {elemVal}, {i}");
                current = next;
            }
            return current;
        }

        // ValueBitList[N]: inline bit-packed array construction
        if (baseName == "ValueBitList")
        {
            string llvmType = GetLLVMType(resolvedType);
            // Calculate byte count from number of bits
            int bitCount = arguments.Count;
            int byteCount = (bitCount + 7) / 8;

            // Pack bits into bytes, then insertvalue each byte
            string current = "zeroinitializer";
            for (int byteIdx = 0; byteIdx < byteCount; byteIdx++)
            {
                int byteVal = 0;
                for (int bitIdx = 0; bitIdx < 8 && byteIdx * 8 + bitIdx < bitCount; bitIdx++)
                {
                    // Evaluate the boolean argument
                    // For literal bools, we can read the value directly
                    if (arguments[byteIdx * 8 + bitIdx] is LiteralExpression { Value: true })
                    {
                        byteVal |= (1 << bitIdx);
                    }
                    else if (arguments[byteIdx * 8 + bitIdx] is LiteralExpression { Value: false })
                    {
                        // bit is already 0
                    }
                    else
                    {
                        // Non-literal: evaluate at runtime and OR into byte
                        // For simplicity, fall back to runtime construction for non-literal bools
                        return EmitValueBitListRuntime(sb, resolvedType, arguments);
                    }
                }
                string next = NextTemp();
                EmitLine(sb, $"  {next} = insertvalue {llvmType} {current}, i8 {byteVal}, {byteIdx}");
                current = next;
            }
            return current;
        }

        // PriorityQueue[TPriority, TElement]: $create() + add(element, priority) from tuple args
        if (baseName == "PriorityQueue")
        {
            return EmitPriorityQueueLiteral(sb, resolvedType, typeName, arguments);
        }

        // Entity collections: $create() + add/add_last calls
        string collectionPtr = EmitCollectionCreate(sb, resolvedType, typeName);

        // Determine the add method name
        string addMethodName;
        bool isMapType = baseName is "Dict" or "SortedDict";
        bool isSequenceType = baseName is "List" or "Deque" or "BitList";

        if (isSequenceType)
            addMethodName = "add_last";
        else
            addMethodName = "add";

        // Look up the add method
        string fullAddName = $"{typeName}.{addMethodName}";
        RoutineInfo? addMethod = _registry.LookupRoutine(fullAddName);

        // Fall back to generic definition
        if (addMethod == null)
        {
            TypeInfo? genericDef = resolvedType switch
            {
                EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition,
                RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition,
                _ => null
            };
            if (genericDef != null)
            {
                string genAddName = $"{genericDef.Name}.{addMethodName}";
                addMethod = _registry.LookupRoutine(genAddName);
            }
        }

        if (addMethod == null)
            return collectionPtr;

        // Determine mangled name and handle monomorphization
        string mangledAdd;
        if (resolvedType.IsGenericResolution)
        {
            mangledAdd = Q($"{resolvedType.FullName}.{addMethodName}");
            RecordMonomorphization(mangledAdd, addMethod, resolvedType);
        }
        else
        {
            mangledAdd = MangleFunctionName(addMethod);
        }

        // Emit add calls for each element
        if (isMapType)
        {
            // Dict/SortedDict: extract key and value from DictEntry arguments
            foreach (var arg in arguments)
            {
                if (arg is DictEntryLiteralExpression entry)
                {
                    string keyVal = EmitExpression(sb, entry.Key);
                    string valVal = EmitExpression(sb, entry.Value);
                    TypeInfo? keyType = GetExpressionType(entry.Key);
                    TypeInfo? valueType = GetExpressionType(entry.Value);
                    string keyLlvm = keyType != null ? GetLLVMType(keyType) : "i64";
                    string valLlvm = valueType != null ? GetLLVMType(valueType) : "i64";

                    // Declare the add function if not yet declared
                    if (!_generatedFunctions.Contains(mangledAdd))
                    {
                        EmitLine(_functionDeclarations, $"declare i1 @{mangledAdd}(ptr, {keyLlvm}, {valLlvm})");
                        _generatedFunctions.Add(mangledAdd);
                    }
                    EmitLine(sb, $"  call i1 @{mangledAdd}(ptr {collectionPtr}, {keyLlvm} {keyVal}, {valLlvm} {valVal})");
                }
            }
        }
        else
        {
            // Sequence/unique: add each element
            foreach (var arg in arguments)
            {
                string elemVal = EmitExpression(sb, arg);
                TypeInfo? elemType = GetExpressionType(arg);
                string elemLlvm = elemType != null ? GetLLVMType(elemType) : "i64";

                // Declare the add function if not yet declared
                if (!_generatedFunctions.Contains(mangledAdd))
                {
                    // add returns Bool for Set/SortedSet, void for others
                    string retType = baseName is "Set" or "SortedSet" ? "i1" : "void";
                    EmitLine(_functionDeclarations, $"declare {retType} @{mangledAdd}(ptr, {elemLlvm})");
                    _generatedFunctions.Add(mangledAdd);
                }

                string retType2 = baseName is "Set" or "SortedSet" ? "i1" : "void";
                string callPrefix = retType2 == "void" ? "" : $"  {NextTemp()} = ";
                // For void returns, we just call. For non-void, we discard the result.
                if (retType2 == "void")
                    EmitLine(sb, $"  call void @{mangledAdd}(ptr {collectionPtr}, {elemLlvm} {elemVal})");
                else
                    EmitLine(sb, $"  call i1 @{mangledAdd}(ptr {collectionPtr}, {elemLlvm} {elemVal})");
            }
        }

        return collectionPtr;
    }

    /// <summary>
    /// Runtime fallback for ValueBitList construction when arguments are non-literal.
    /// Evaluates each bool at runtime and packs into bytes.
    /// </summary>
    private string EmitValueBitListRuntime(StringBuilder sb, TypeInfo resolvedType, List<Expression> arguments)
    {
        string llvmType = GetLLVMType(resolvedType);
        int bitCount = arguments.Count;
        int byteCount = (bitCount + 7) / 8;

        string current = "zeroinitializer";
        for (int byteIdx = 0; byteIdx < byteCount; byteIdx++)
        {
            // Start with 0, OR in each bit
            string byteAccum = "0";
            for (int bitIdx = 0; bitIdx < 8 && byteIdx * 8 + bitIdx < bitCount; bitIdx++)
            {
                string boolVal = EmitExpression(sb, arguments[byteIdx * 8 + bitIdx]);
                // zext i1 to i8, shift left by bitIdx, OR into accumulator
                string extended = NextTemp();
                EmitLine(sb, $"  {extended} = zext i1 {boolVal} to i8");
                if (bitIdx > 0)
                {
                    string shifted = NextTemp();
                    EmitLine(sb, $"  {shifted} = shl i8 {extended}, {bitIdx}");
                    extended = shifted;
                }
                string ored = NextTemp();
                EmitLine(sb, $"  {ored} = or i8 {byteAccum}, {extended}");
                byteAccum = ored;
            }
            string next = NextTemp();
            EmitLine(sb, $"  {next} = insertvalue {llvmType} {current}, i8 {byteAccum}, {byteIdx}");
            current = next;
        }
        return current;
    }

    /// <summary>
    /// Emits PriorityQueue literal constructor: PriorityQueue[S32, Text]((1, "high"), (10, "low"))
    /// Each argument is a tuple (priority, element) unpacked into add(element: element, priority: priority).
    /// </summary>
    private string EmitPriorityQueueLiteral(StringBuilder sb, TypeInfo resolvedType, string typeName, List<Expression> arguments)
    {
        string pqPtr = EmitCollectionCreate(sb, resolvedType, typeName);

        // Look up add(element, priority) method
        string addName = $"{typeName}.add";
        RoutineInfo? addMethod = _registry.LookupRoutine(addName);

        if (addMethod == null)
        {
            TypeInfo? genericDef = resolvedType switch
            {
                EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition,
                _ => null
            };
            if (genericDef != null)
            {
                string genAddName = $"{genericDef.Name}.add";
                addMethod = _registry.LookupRoutine(genAddName);
            }
        }

        if (addMethod == null)
            return pqPtr;

        string mangledAdd;
        if (resolvedType.IsGenericResolution)
        {
            mangledAdd = Q($"{resolvedType.FullName}.add");
            RecordMonomorphization(mangledAdd, addMethod, resolvedType);
        }
        else
        {
            mangledAdd = MangleFunctionName(addMethod);
        }

        // Resolve priority and element types from type arguments
        // PriorityQueue[TPriority, TElement] — args are (priority, element) tuples
        TypeInfo? priorityType = resolvedType.TypeArguments?.Count >= 1 ? resolvedType.TypeArguments[0] : null;
        TypeInfo? elementType = resolvedType.TypeArguments?.Count >= 2 ? resolvedType.TypeArguments[1] : null;
        string priorityLlvm = priorityType != null ? GetLLVMType(priorityType) : "i32";
        string elementLlvm = elementType != null ? GetLLVMType(elementType) : "ptr";

        foreach (var arg in arguments)
        {
            string? priorityVal = null;
            string? elementVal = null;

            // DictEntryLiteralExpression: PriorityQueue(1: "high", 2: "low")
            if (arg is DictEntryLiteralExpression dictEntry)
            {
                priorityVal = EmitExpression(sb, dictEntry.Key);
                elementVal = EmitExpression(sb, dictEntry.Value);
            }
            // TupleLiteralExpression: PriorityQueue((1, "high"), (2, "low"))
            else if (arg is TupleLiteralExpression tuple && tuple.Elements.Count == 2)
            {
                priorityVal = EmitExpression(sb, tuple.Elements[0]);
                elementVal = EmitExpression(sb, tuple.Elements[1]);
            }

            if (priorityVal != null && elementVal != null)
            {
                // add(element: TElement, priority: TPriority)
                if (!_generatedFunctions.Contains(mangledAdd))
                {
                    EmitLine(_functionDeclarations, $"declare void @{mangledAdd}(ptr, {elementLlvm}, {priorityLlvm})");
                    _generatedFunctions.Add(mangledAdd);
                }
                EmitLine(sb, $"  call void @{mangledAdd}(ptr {pqPtr}, {elementLlvm} {elementVal}, {priorityLlvm} {priorityVal})");
            }
        }

        return pqPtr;
    }

    #endregion

    #region Flags Tests

    /// <summary>
    /// Emits a flags test expression: x is FLAG, x isnot FLAG, x isonly FLAG.
    /// FlagsTestExpression has Subject, Kind, TestFlags, Connective, and ExcludedFlags.
    /// </summary>
    private string EmitFlagsTest(StringBuilder sb, FlagsTestExpression flagsTest)
    {
        string subject = EmitExpression(sb, flagsTest.Subject);
        TypeInfo? subjectType = GetExpressionType(flagsTest.Subject);

        FlagsTypeInfo? flagsType = subjectType as FlagsTypeInfo;

        // Build the combined test mask from TestFlags
        ulong testMask = 0;
        foreach (string flagName in flagsTest.TestFlags)
        {
            testMask |= ResolveFlagBit(flagName, flagsType);
        }

        // Build the excluded mask from ExcludedFlags (if present)
        ulong excludedMask = 0;
        if (flagsTest.ExcludedFlags != null)
        {
            foreach (string flagName in flagsTest.ExcludedFlags)
            {
                excludedMask |= ResolveFlagBit(flagName, flagsType);
            }
        }

        string maskStr = testMask.ToString();

        return flagsTest.Kind switch
        {
            FlagsTestKind.Is => EmitFlagsIsTest(sb, subject, maskStr, flagsTest.Connective, excludedMask),
            FlagsTestKind.IsNot => EmitFlagsIsNotTest(sb, subject, maskStr),
            FlagsTestKind.IsOnly => EmitFlagsIsOnlyTest(sb, subject, maskStr),
            _ => throw new InvalidOperationException($"Unknown flags test kind: {flagsTest.Kind}")
        };
    }

    /// <summary>
    /// Resolves a flag member name to its bit value (1UL &lt;&lt; BitPosition).
    /// Falls back to 0 if not found.
    /// </summary>
    private static ulong ResolveFlagBit(string flagName, FlagsTypeInfo? flagsType)
    {
        if (flagsType == null) return 0;
        foreach (var member in flagsType.Members)
        {
            if (member.Name == flagName)
            {
                return 1UL << member.BitPosition;
            }
        }
        return 0;
    }

    /// <summary>
    /// x is A and B → (x &amp; mask) == mask (all flags set)
    /// x is A or B  → (x &amp; mask) != 0 (any flag set)
    /// x is A and B but C → ((x &amp; mask) == mask) &amp;&amp; ((x &amp; excludedMask) == 0)
    /// </summary>
    private string EmitFlagsIsTest(StringBuilder sb, string subject, string mask, FlagsTestConnective connective, ulong excludedMask)
    {
        string andResult = NextTemp();
        EmitLine(sb, $"  {andResult} = and i64 {subject}, {mask}");

        string cmpResult;
        if (connective == FlagsTestConnective.Or)
        {
            // Any flag set: (subject & mask) != 0
            cmpResult = NextTemp();
            EmitLine(sb, $"  {cmpResult} = icmp ne i64 {andResult}, 0");
        }
        else
        {
            // All flags set: (subject & mask) == mask
            cmpResult = NextTemp();
            EmitLine(sb, $"  {cmpResult} = icmp eq i64 {andResult}, {mask}");
        }

        // Handle 'but' exclusion
        if (excludedMask > 0)
        {
            string exclAnd = NextTemp();
            EmitLine(sb, $"  {exclAnd} = and i64 {subject}, {excludedMask}");
            string exclCmp = NextTemp();
            EmitLine(sb, $"  {exclCmp} = icmp eq i64 {exclAnd}, 0");
            // Combined: cmpResult && exclCmp
            string combined = NextTemp();
            EmitLine(sb, $"  {combined} = and i1 {cmpResult}, {exclCmp}");
            return combined;
        }

        return cmpResult;
    }

    /// <summary>
    /// x isnot A → (x &amp; mask) != mask (flag not fully set)
    /// </summary>
    private string EmitFlagsIsNotTest(StringBuilder sb, string subject, string mask)
    {
        string andResult = NextTemp();
        EmitLine(sb, $"  {andResult} = and i64 {subject}, {mask}");
        string result = NextTemp();
        EmitLine(sb, $"  {result} = icmp ne i64 {andResult}, {mask}");
        return result;
    }

    /// <summary>
    /// x isonly A and B → x == mask (exact match)
    /// </summary>
    private string EmitFlagsIsOnlyTest(StringBuilder sb, string subject, string mask)
    {
        string result = NextTemp();
        EmitLine(sb, $"  {result} = icmp eq i64 {subject}, {mask}");
        return result;
    }

    #endregion
}
