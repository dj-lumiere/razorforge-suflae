namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Symbols;
using SemanticAnalysis.Types;
using SyntaxTree;

/// <summary>
/// Expression code generation for conditional and flow-oriented expressions.
/// </summary>
public partial class LLVMCodeGenerator
{
    private string EmitConditional(StringBuilder sb, ConditionalExpression cond)
    {
        string condition = EmitExpression(sb: sb, expr: cond.Condition);

        string thenLabel = NextLabel(prefix: "cond_then");
        string elseLabel = NextLabel(prefix: "cond_else");
        string endLabel = NextLabel(prefix: "cond_end");

        EmitLine(sb: sb, line: $"  br i1 {condition}, label %{thenLabel}, label %{elseLabel}");

        // Then branch
        EmitLine(sb: sb, line: $"{thenLabel}:");
        string thenValue = EmitExpression(sb: sb, expr: cond.TrueExpression);
        EmitLine(sb: sb, line: $"  br label %{endLabel}");

        // Else branch
        EmitLine(sb: sb, line: $"{elseLabel}:");
        string elseValue = EmitExpression(sb: sb, expr: cond.FalseExpression);
        EmitLine(sb: sb, line: $"  br label %{endLabel}");

        // Merge with phi
        EmitLine(sb: sb, line: $"{endLabel}:");
        string result = NextTemp();
        TypeInfo? resultType = GetExpressionType(expr: cond.TrueExpression);
        if (resultType == null)
        {
            throw new InvalidOperationException(
                message: "Cannot determine type for conditional expression");
        }

        string llvmType = GetLLVMType(type: resultType);
        EmitLine(sb: sb,
            line:
            $"  {result} = phi {llvmType} [ {thenValue}, %{thenLabel} ], [ {elseValue}, %{elseLabel} ]");

        return result;
    }

    /// <summary>
    /// Generates code for an index access expression (e.g., list[i]).
    /// </summary>
    private string EmitIndexAccess(StringBuilder sb, IndexExpression index)
    {
        // Resolve target type to decide dispatch strategy
        TypeInfo? targetType = GetExpressionType(expr: index.Object);

        // If the type has a $getitem method, dispatch to it
        if (targetType != null)
        {
            RoutineInfo? getItem = _registry.LookupMethod(type: targetType, methodName: "$getitem");

            if (getItem != null)
            {
                // Emit as method call: obj.$getitem(index) or obj.$getitem!(index)
                // Use the actual method name (may be failable with ! suffix).
                // For generic definitions on generic resolution types (e.g., List[S64].$getitem!),
                // EmitMethodCall handles monomorphization automatically.
                var member = new MemberExpression(Object: index.Object,
                    PropertyName: getItem.Name,
                    Location: index.Location);
                return EmitMethodCall(sb: sb,
                    member: member,
                    arguments: new List<Expression> { index.Index });
            }

            // For entity types with a list-like first field (e.g., Text has letters: List[Letter]),
            // inline $getitem as: load list ptr → GEP data → load element
            if (getItem != null && targetType is EntityTypeInfo entity &&
                entity.MemberVariables.Count > 0)
            {
                MemberVariableInfo firstField = entity.MemberVariables[index: 0];
                if (firstField.Type is EntityTypeInfo listType &&
                    listType.Name.StartsWith(value: "List["))
                {
                    string target = EmitExpression(sb: sb, expr: index.Object);
                    string indexValue = EmitExpression(sb: sb, expr: index.Index);

                    // GEP to get the list pointer field
                    string entityTypeName = GetEntityTypeName(entity: entity);
                    string listFieldPtr = NextTemp();
                    EmitLine(sb: sb,
                        line:
                        $"  {listFieldPtr} = getelementptr {entityTypeName}, ptr {target}, i32 0, i32 0");
                    string listPtr = NextTemp();
                    EmitLine(sb: sb, line: $"  {listPtr} = load ptr, ptr {listFieldPtr}");

                    // GEP into list's data (field 0 of list entity)
                    string listEntityType = GetEntityTypeName(entity: listType);
                    string dataFieldPtr = NextTemp();
                    EmitLine(sb: sb,
                        line:
                        $"  {dataFieldPtr} = getelementptr {listEntityType}, ptr {listPtr}, i32 0, i32 0");
                    string dataBase = NextTemp();
                    EmitLine(sb: sb, line: $"  {dataBase} = load ptr, ptr {dataFieldPtr}");

                    // Load the element
                    TypeInfo? elemType = GetListElementType(listEntity: listType);
                    string elemLlvm = elemType != null
                        ? GetLLVMType(type: elemType)
                        : "i32";
                    string elemPtr = NextTemp();
                    EmitLine(sb: sb,
                        line:
                        $"  {elemPtr} = getelementptr {elemLlvm}, ptr {dataBase}, i64 {indexValue}");
                    string loaded = NextTemp();
                    EmitLine(sb: sb, line: $"  {loaded} = load {elemLlvm}, ptr {elemPtr}");
                    return loaded;
                }
            }
        }

        // For CStr indexing: pointer + offset → load byte
        if (targetType is RecordTypeInfo { Name: "CStr" })
        {
            string cstrVal = EmitExpression(sb: sb, expr: index.Object);
            string idxVal = EmitExpression(sb: sb, expr: index.Index);
            string ptr = NextTemp();
            EmitLine(sb: sb, line: $"  {ptr} = extractvalue %Record.CStr {cstrVal}, 0");
            string addr = NextTemp();
            EmitLine(sb: sb, line: $"  {addr} = add i64 {ptr}, {idxVal}");
            string realPtr = NextTemp();
            EmitLine(sb: sb, line: $"  {realPtr} = inttoptr i64 {addr} to ptr");
            string loaded = NextTemp();
            EmitLine(sb: sb, line: $"  {loaded} = load i8, ptr {realPtr}");
            return loaded;
        }

        // Fallback: raw GEP + load for pointer/contiguous-memory types
        string fallbackTarget = EmitExpression(sb: sb, expr: index.Object);
        string fallbackIndex = EmitExpression(sb: sb, expr: index.Index);

        string fallbackElemType = targetType switch
        {
            RecordTypeInfo r when r.TypeArguments is { Count: > 0 } => GetLLVMType(
                type: r.TypeArguments[index: 0]),
            EntityTypeInfo e when e.TypeArguments is { Count: > 0 } => GetLLVMType(
                type: e.TypeArguments[index: 0]),
            _ => throw new InvalidOperationException(
                message: $"Cannot determine element type for indexing on type: {targetType?.Name}")
        };

        string fallbackElemPtr = NextTemp();
        string fallbackResult = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {fallbackElemPtr} = getelementptr {fallbackElemType}, ptr {fallbackTarget}, i64 {fallbackIndex}");
        EmitLine(sb: sb,
            line: $"  {fallbackResult} = load {fallbackElemType}, ptr {fallbackElemPtr}");

        return fallbackResult;
    }

    /// <summary>
    /// Generates code for a slice expression: obj[start to end] → $getslice(start, end)
    /// </summary>
    private string EmitSliceAccess(StringBuilder sb, SliceExpression slice)
    {
        string target = EmitExpression(sb: sb, expr: slice.Object);
        string start = EmitExpression(sb: sb, expr: slice.Start);
        string end = EmitExpression(sb: sb, expr: slice.End);

        TypeInfo? targetType = GetExpressionType(expr: slice.Object);
        if (targetType == null)
        {
            throw new InvalidOperationException(message: "Cannot determine type for slice target");
        }

        RoutineInfo? method = _registry.LookupMethod(type: targetType, methodName: "$getslice");

        var argValues = new List<string> { target, start, end };
        var argTypes = new List<string> { GetParameterLLVMType(type: targetType), "i64", "i64" };

        string mangledName = method != null
            ? MangleFunctionName(routine: method)
            : Q(name: $"{targetType.Name}.$getslice");

        string returnType = method?.ReturnType != null
            ? GetLLVMType(type: method.ReturnType)
            : GetParameterLLVMType(type: targetType);

        string result = NextTemp();
        string args = BuildCallArgs(types: argTypes, values: argValues);
        EmitLine(sb: sb, line: $"  {result} = call {returnType} @{mangledName}({args})");
        return result;
    }

    /// <summary>
    /// Generates code for a range expression.
    /// </summary>
    private string EmitRange(StringBuilder sb, RangeExpression range)
    {
        // Emit start, end, step expressions
        string start = EmitExpression(sb: sb, expr: range.Start);
        string end = EmitExpression(sb: sb, expr: range.End);
        string step = range.Step != null
            ? EmitExpression(sb: sb, expr: range.Step)
            : "1";
        // IsDescending is confusingly named: false = 'to' (inclusive), true = 'til' (exclusive)
        // Range record field 3 is 'inclusive': true for 'to', false for 'til'
        string isInclusive = range.IsDescending
            ? "false"
            : "true";

        // Infer element type from start/end expressions (Range[T] is generic)
        TypeInfo? elemType =
            GetExpressionType(expr: range.Start) ?? GetExpressionType(expr: range.End);
        string elemLlvmType = elemType != null
            ? GetLLVMType(type: elemType)
            : "i64";

        // Try to use registered Range type, resolved with element type
        TypeInfo? rangeGenericDef = _registry.LookupType(name: "Range");
        string structType;
        if (rangeGenericDef != null && elemType != null)
        {
            TypeInfo resolvedRange = _registry.GetOrCreateResolution(genericDef: rangeGenericDef,
                typeArguments: new List<TypeInfo> { elemType });
            structType = resolvedRange is RecordTypeInfo resolvedRecord
                ? GetRecordTypeName(record: resolvedRecord)
                : $"{{ {elemLlvmType}, {elemLlvmType}, {elemLlvmType}, i1 }}";
        }
        else if (rangeGenericDef is RecordTypeInfo rangeRecord)
        {
            structType = GetRecordTypeName(record: rangeRecord);
        }
        else
        {
            structType = $"{{ {elemLlvmType}, {elemLlvmType}, {elemLlvmType}, i1 }}";
        }

        // Build struct via insertvalue chain: { start, end, step, inclusive }
        string v0 = NextTemp();
        EmitLine(sb: sb,
            line: $"  {v0} = insertvalue {structType} undef, {elemLlvmType} {start}, 0");
        string v1 = NextTemp();
        EmitLine(sb: sb, line: $"  {v1} = insertvalue {structType} {v0}, {elemLlvmType} {end}, 1");
        string v2 = NextTemp();
        EmitLine(sb: sb,
            line: $"  {v2} = insertvalue {structType} {v1}, {elemLlvmType} {step}, 2");
        string v3 = NextTemp();
        EmitLine(sb: sb, line: $"  {v3} = insertvalue {structType} {v2}, i1 {isInclusive}, 3");

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
    /// Non-stealable types (caught by semantic analyzer):
    /// - All wrappers and tokens (Viewed, Hijacked, Retained, Tracked, Shared, Marked, Inspected, Seized)
    /// - Snatched[T] (internal ownership type)
    /// </remarks>
    private string EmitSteal(StringBuilder sb, StealExpression steal)
    {
        // Steal just evaluates the operand and passes the value through.
        // The semantic analyzer has already validated that:
        // 1. The operand is a raw entity type
        // 2. The source will be marked as deadref after this point
        return EmitExpression(sb: sb, expr: steal.Operand);
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
        foreach (Expression element in tuple.Elements)
        {
            elemValues.Add(item: EmitExpression(sb: sb, expr: element));
            TypeInfo? elemType = GetExpressionType(expr: element);
            elemLLVMTypes.Add(item: elemType != null
                ? GetLLVMType(type: elemType)
                : "i64");
        }

        // Resolve tuple type from semantic analysis
        var tupleType = tuple.ResolvedType as TupleTypeInfo;

        string structType;
        if (tupleType != null)
        {
            structType = GetTupleTypeName(tuple: tupleType);
        }
        else
        {
            // Fall back to anonymous struct type
            structType = $"{{ {string.Join(separator: ", ", values: elemLLVMTypes)} }}";
        }

        string result = "undef";
        for (int i = 0; i < elemValues.Count; i++)
        {
            string newResult = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {newResult} = insertvalue {structType} {result}, {elemLLVMTypes[index: i]} {elemValues[index: i]}, {i}");
            result = newResult;
        }

        return result;
    }


    /// <summary>
    /// Emits the ?? (none coalesce) operator.
    /// If the left operand (ErrorHandlingTypeInfo) is VALID, extracts its value.
    /// Otherwise evaluates and returns the right operand as default.
    /// </summary>
    private string EmitNoneCoalesce(StringBuilder sb, BinaryExpression binary)
    {
        string left = EmitExpression(sb: sb, expr: binary.Left);
        TypeInfo? leftType = GetExpressionType(expr: binary.Left);

        if (leftType is not ErrorHandlingTypeInfo errorType)
        {
            throw new InvalidOperationException(
                message: "'??' operator requires ErrorHandlingTypeInfo on the left");
        }

        // Use proper LLVM type for the maybe/error-handling value
        string maybeType = GetLLVMType(type: leftType);

        // Alloca and store the maybe value
        string allocaPtr = NextTemp();
        EmitEntryAlloca(llvmName: allocaPtr, llvmType: maybeType);
        EmitLine(sb: sb, line: $"  store {maybeType} {left}, ptr {allocaPtr}");

        // Extract tag (field 0)
        string tagPtr = NextTemp();
        string tag = NextTemp();
        EmitLine(sb: sb,
            line: $"  {tagPtr} = getelementptr {maybeType}, ptr {allocaPtr}, i32 0, i32 0");
        EmitLine(sb: sb, line: $"  {tag} = load i64, ptr {tagPtr}");

        // Check tag == 1 (VALID)
        string isValid = NextTemp();
        EmitLine(sb: sb, line: $"  {isValid} = icmp eq i64 {tag}, 1");

        string valLabel = NextLabel(prefix: "coalesce_val");
        string rhsLabel = NextLabel(prefix: "coalesce_rhs");
        string endLabel = NextLabel(prefix: "coalesce_end");
        string leftBlock = _currentBlock;

        EmitLine(sb: sb, line: $"  br i1 {isValid}, label %{valLabel}, label %{rhsLabel}");

        // Valid path: extract the value from the handle
        EmitLine(sb: sb, line: $"{valLabel}:");
        _currentBlock = valLabel;
        string handlePtr = NextTemp();
        string handleVal = NextTemp();
        EmitLine(sb: sb,
            line: $"  {handlePtr} = getelementptr {maybeType}, ptr {allocaPtr}, i32 0, i32 1");
        EmitLine(sb: sb, line: $"  {handleVal} = load ptr, ptr {handlePtr}");

        // Determine the value type T
        TypeInfo valueType = errorType.ValueType;
        string llvmValueType = GetLLVMType(type: valueType);

        // Load T from the handle pointer
        string validValue = NextTemp();
        EmitLine(sb: sb, line: $"  {validValue} = load {llvmValueType}, ptr {handleVal}");
        string valBlock = _currentBlock;
        EmitLine(sb: sb, line: $"  br label %{endLabel}");

        // RHS path: evaluate the default expression
        EmitLine(sb: sb, line: $"{rhsLabel}:");
        _currentBlock = rhsLabel;
        string rhsValue = EmitExpression(sb: sb, expr: binary.Right);
        string rhsBlock = _currentBlock;
        EmitLine(sb: sb, line: $"  br label %{endLabel}");

        // Merge with PHI
        EmitLine(sb: sb, line: $"{endLabel}:");
        _currentBlock = endLabel;
        string result = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {result} = phi {llvmValueType} [ {validValue}, %{valBlock} ], [ {rhsValue}, %{rhsBlock} ]");

        return result;
    }

    /// <summary>
    /// Emits the !! (force unwrap) operator.
    /// If the operand (ErrorHandlingTypeInfo) is VALID, extracts its value.
    /// Otherwise traps (crashes the program).
    /// </summary>
    private string EmitForceUnwrap(StringBuilder sb, UnaryExpression unary)
    {
        string operand = EmitExpression(sb: sb, expr: unary.Operand);
        TypeInfo? operandType = GetExpressionType(expr: unary.Operand);

        // Determine the value type inside the Maybe/ErrorHandling wrapper
        TypeInfo? valueType = null;
        if (operandType is ErrorHandlingTypeInfo errorType)
        {
            valueType = errorType.ValueType;
        }
        else if (operandType != null && GetGenericBaseName(type: operandType) == "Maybe" &&
                 operandType.TypeArguments is { Count: 1 })
        {
            // Handle Maybe types that were resolved as RecordTypeInfo by the registry cache
            valueType = operandType.TypeArguments[index: 0];
        }

        if (valueType == null)
        {
            throw new InvalidOperationException(
                message:
                $"'!!' operator requires Maybe/ErrorHandling operand, got {operandType?.GetType().Name ?? "null"}: {operandType?.Name ?? "null"}");
        }

        // Declare llvm.trap if not already declared
        if (_declaredNativeFunctions.Add(item: "llvm.trap"))
        {
            EmitLine(sb: _functionDeclarations,
                line: "declare void @llvm.trap() noreturn nounwind");
        }

        // Alloca and store the maybe/error-handling value using its proper LLVM type
        string maybeType = operandType != null
            ? GetLLVMType(type: operandType)
            : "{ i64, ptr }";
        string allocaPtr = NextTemp();
        EmitEntryAlloca(llvmName: allocaPtr, llvmType: maybeType);
        EmitLine(sb: sb, line: $"  store {maybeType} {operand}, ptr {allocaPtr}");

        // Extract tag (field 0)
        string tagPtr = NextTemp();
        string tag = NextTemp();
        EmitLine(sb: sb,
            line: $"  {tagPtr} = getelementptr {maybeType}, ptr {allocaPtr}, i32 0, i32 0");
        EmitLine(sb: sb, line: $"  {tag} = load i64, ptr {tagPtr}");

        // Check tag == 1 (VALID)
        string isValid = NextTemp();
        EmitLine(sb: sb, line: $"  {isValid} = icmp eq i64 {tag}, 1");

        string okLabel = NextLabel(prefix: "unwrap_ok");
        string failLabel = NextLabel(prefix: "unwrap_fail");

        EmitLine(sb: sb, line: $"  br i1 {isValid}, label %{okLabel}, label %{failLabel}");

        // Fail path: trap
        EmitLine(sb: sb, line: $"{failLabel}:");
        _currentBlock = failLabel;
        EmitLine(sb: sb, line: $"  call void @llvm.trap()");
        EmitLine(sb: sb, line: $"  unreachable");

        // OK path: extract the value from the handle
        EmitLine(sb: sb, line: $"{okLabel}:");
        _currentBlock = okLabel;
        string handlePtr = NextTemp();
        string handleVal = NextTemp();
        EmitLine(sb: sb,
            line: $"  {handlePtr} = getelementptr {maybeType}, ptr {allocaPtr}, i32 0, i32 1");
        EmitLine(sb: sb, line: $"  {handleVal} = load ptr, ptr {handlePtr}");

        // For entity types, the value IS the pointer (no boxing) — return directly
        if (valueType is EntityTypeInfo)
        {
            return handleVal;
        }

        // For value types, load T from the handle pointer (heap-allocated storage)
        string llvmValueType = GetLLVMType(type: valueType);
        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = load {llvmValueType}, ptr {handleVal}");

        return result;
    }


    /// <summary>
    /// Emits optional member access (?.): obj?.field
    /// If obj is null/none, produces a zero/null value. Otherwise performs normal member access.
    /// </summary>
    private string EmitOptionalMemberAccess(StringBuilder sb, OptionalMemberExpression optMember)
    {
        string obj = EmitExpression(sb: sb, expr: optMember.Object);
        TypeInfo? objType = GetExpressionType(expr: optMember.Object);

        if (objType is ErrorHandlingTypeInfo errorType)
        {
            return EmitOptionalChainErrorHandling(sb: sb,
                obj: obj,
                errorType: errorType,
                propertyName: optMember.PropertyName);
        }

        // Entity (pointer): null check
        return EmitOptionalChainPointer(sb: sb,
            obj: obj,
            objType: objType,
            propertyName: optMember.PropertyName);
    }

    /// <summary>
    /// Optional chaining on a pointer-based type (entity): null check → member access or zero.
    /// </summary>
    private string EmitOptionalChainPointer(StringBuilder sb, string obj, TypeInfo? objType,
        string propertyName)
    {
        string nonNullLabel = NextLabel(prefix: "optchain_nonnull");
        string nullLabel = NextLabel(prefix: "optchain_null");
        string endLabel = NextLabel(prefix: "optchain_end");
        string entryBlock = _currentBlock;

        // Null check
        string isNull = NextTemp();
        EmitLine(sb: sb, line: $"  {isNull} = icmp eq ptr {obj}, null");
        EmitLine(sb: sb, line: $"  br i1 {isNull}, label %{nullLabel}, label %{nonNullLabel}");

        // Non-null path: do normal member access
        EmitLine(sb: sb, line: $"{nonNullLabel}:");
        _currentBlock = nonNullLabel;
        string memberValue = EmitMemberAccessOnType(sb: sb,
            value: obj,
            type: objType,
            propertyName: propertyName);
        string memberBlock = _currentBlock;

        // Determine result type from member access
        TypeInfo? resultType =
            GetMemberTypeFromOwner(ownerType: objType, memberName: propertyName);
        string llvmResultType = resultType != null
            ? GetLLVMType(type: resultType)
            : "ptr";
        string zeroValue = resultType != null
            ? GetZeroValue(type: resultType)
            : "null";

        EmitLine(sb: sb, line: $"  br label %{endLabel}");

        // Null path: return zero/null
        EmitLine(sb: sb, line: $"{nullLabel}:");
        _currentBlock = nullLabel;
        EmitLine(sb: sb, line: $"  br label %{endLabel}");

        // Merge
        EmitLine(sb: sb, line: $"{endLabel}:");
        _currentBlock = endLabel;
        string result = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {result} = phi {llvmResultType} [ {memberValue}, %{memberBlock} ], [ {zeroValue}, %{nullLabel} ]");

        return result;
    }

    /// <summary>
    /// Optional chaining on an ErrorHandlingTypeInfo: check VALID → extract value → member access, or zero.
    /// </summary>
    private string EmitOptionalChainErrorHandling(StringBuilder sb, string obj,
        ErrorHandlingTypeInfo errorType, string propertyName)
    {
        // Alloca and store the { i64, ptr } value
        string allocaPtr = NextTemp();
        EmitEntryAlloca(llvmName: allocaPtr, llvmType: "{ i64, ptr }");
        EmitLine(sb: sb, line: $"  store {{ i64, ptr }} {obj}, ptr {allocaPtr}");

        // Extract tag
        string tagPtr = NextTemp();
        string tag = NextTemp();
        EmitLine(sb: sb,
            line: $"  {tagPtr} = getelementptr {{ i64, ptr }}, ptr {allocaPtr}, i32 0, i32 0");
        EmitLine(sb: sb, line: $"  {tag} = load i64, ptr {tagPtr}");

        string isValid = NextTemp();
        EmitLine(sb: sb, line: $"  {isValid} = icmp eq i64 {tag}, 1");

        string validLabel = NextLabel(prefix: "optchain_valid");
        string invalidLabel = NextLabel(prefix: "optchain_invalid");
        string endLabel = NextLabel(prefix: "optchain_end");

        EmitLine(sb: sb, line: $"  br i1 {isValid}, label %{validLabel}, label %{invalidLabel}");

        // Valid path: extract value and do member access
        EmitLine(sb: sb, line: $"{validLabel}:");
        _currentBlock = validLabel;
        string handlePtr = NextTemp();
        string handleVal = NextTemp();
        EmitLine(sb: sb,
            line: $"  {handlePtr} = getelementptr {{ i64, ptr }}, ptr {allocaPtr}, i32 0, i32 1");
        EmitLine(sb: sb, line: $"  {handleVal} = load ptr, ptr {handlePtr}");

        TypeInfo valueType = errorType.ValueType;
        string llvmValueType = GetLLVMType(type: valueType);
        string innerValue = NextTemp();
        EmitLine(sb: sb, line: $"  {innerValue} = load {llvmValueType}, ptr {handleVal}");

        // Now do member access on the extracted value
        string memberValue = EmitMemberAccessOnType(sb: sb,
            value: innerValue,
            type: valueType,
            propertyName: propertyName);
        string validBlock = _currentBlock;

        // Determine result type
        TypeInfo? resultType =
            GetMemberTypeFromOwner(ownerType: valueType, memberName: propertyName);
        string llvmResultType = resultType != null
            ? GetLLVMType(type: resultType)
            : "ptr";
        string zeroValue = resultType != null
            ? GetZeroValue(type: resultType)
            : "null";

        EmitLine(sb: sb, line: $"  br label %{endLabel}");

        // Invalid path
        EmitLine(sb: sb, line: $"{invalidLabel}:");
        _currentBlock = invalidLabel;
        EmitLine(sb: sb, line: $"  br label %{endLabel}");

        // Merge
        EmitLine(sb: sb, line: $"{endLabel}:");
        _currentBlock = endLabel;
        string result = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {result} = phi {llvmResultType} [ {memberValue}, %{validBlock} ], [ {zeroValue}, %{invalidLabel} ]");

        return result;
    }

    /// <summary>
    /// Performs member access on a value given its type, reusing existing member read logic.
    /// </summary>
    private string EmitMemberAccessOnType(StringBuilder sb, string value, TypeInfo? type,
        string propertyName)
    {
        return type switch
        {
            EntityTypeInfo entity => EmitEntityMemberVariableRead(sb: sb,
                entityPtr: value,
                entity: entity,
                memberVariableName: propertyName),
            RecordTypeInfo record => EmitRecordMemberVariableRead(sb: sb,
                recordValue: value,
                record: record,
                memberVariableName: propertyName),
            _ => throw new InvalidOperationException(
                message: $"Cannot access member on type: {type?.Name}")
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

        if (members == null)
        {
            return null;
        }

        foreach (MemberVariableInfo m in members)
        {
            if (m.Name == memberName)
            {
                TypeInfo memberType = m.Type;
                if (ownerType is { IsGenericResolution: true, TypeArguments: not null })
                {
                    memberType =
                        ResolveGenericMemberType(memberType: memberType, ownerType: ownerType);
                }

                return memberType;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves generic type parameters in a member's type using the owner's type arguments.
    /// Builds a substitution map from the owner and delegates to SubstituteTypeParams.
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
        {
            return memberType;
        }

        var subs = new Dictionary<string, TypeInfo>();
        for (int i = 0;
             i < ownerGenericDef.GenericParameters.Count && i < ownerType.TypeArguments.Count;
             i++)
        {
            subs[key: ownerGenericDef.GenericParameters[index: i]] =
                ownerType.TypeArguments[index: i];
        }

        if (subs.Count == 0)
        {
            return memberType;
        }

        return SubstituteTypeParams(type: memberType, substitutions: subs);
    }
}
