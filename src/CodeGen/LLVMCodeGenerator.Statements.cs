using SemanticAnalysis.Symbols;

namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Types;
using SyntaxTree;

/// <summary>
/// Statement code generation: control flow, assignments, declarations, returns.
/// </summary>
public partial class LLVMCodeGenerator
{
    #region Statement Dispatch

    /// <summary>
    /// Main statement dispatch - generates code for any statement type.
    /// Returns true if the statement is a terminator (return, break, continue, throw).
    /// </summary>
    /// <param name="sb">StringBuilder to emit code to.</param>
    /// <param name="stmt">The statement to generate code for.</param>
    /// <returns>True if the statement terminates the current block.</returns>
    private bool EmitStatement(StringBuilder sb, Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement block:
                return EmitBlock(sb: sb, block: block);

            case ExpressionStatement expr:
                EmitExpression(sb: sb, expr: expr.Expression);
                return false;

            case DeclarationStatement decl:
                EmitDeclarationStatement(sb: sb, decl: decl);
                return false;

            case AssignmentStatement assign:
                EmitAssignment(sb: sb, assign: assign);
                return false;

            case ReturnStatement ret:
                EmitReturn(sb: sb, ret: ret);
                return true; // Return terminates the block

            case IfStatement ifStmt:
                return EmitIf(sb: sb, ifStmt: ifStmt);

            case WhileStatement whileStmt:
                EmitWhile(sb: sb, whileStmt: whileStmt);
                return false;

            case ForStatement forStmt:
                EmitFor(sb: sb, forStmt: forStmt);
                return false;

            case BreakStatement:
                EmitBreak(sb: sb);
                return true; // Break terminates the block

            case ContinueStatement:
                EmitContinue(sb: sb);
                return true; // Continue terminates the block

            case PassStatement:
                // No-op, nothing to emit
                return false;

            case DangerStatement danger:
                // danger! block - just emit the body
                return EmitBlock(sb: sb, block: danger.Body);

            case WhenStatement whenStmt:
                EmitWhen(sb: sb, whenStmt: whenStmt);
                return false;

            case DiscardStatement discard:
                // Discard: evaluate the expression and ignore the result (including creators, for side effects)
                EmitExpression(sb: sb, expr: discard.Expression);
                return false;

            case UsingStatement usingStmt:
                EmitUsing(sb: sb, usingStmt: usingStmt);
                return false;

            case ThrowStatement throwStmt:
                EmitThrow(sb: sb, throwStmt: throwStmt);
                return true; // Throw terminates the block

            case AbsentStatement:
                EmitAbsent(sb: sb);
                return true; // Absent terminates the block

            case EmitStatement emitStmt:
                EmitEmit(sb: sb, emitStmt: emitStmt);
                return false;

            case BecomesStatement becomesStmt:
                EmitBecomes(sb: sb, becomesStmt: becomesStmt);
                return false;

            default:
                throw new NotImplementedException(
                    message: $"Statement type not implemented: {stmt.GetType().Name}");
        }
    }

    /// <summary>
    /// Emits all statements in a block.
    /// Returns true if the block terminates (any statement is a terminator).
    /// </summary>
    private bool EmitBlock(StringBuilder sb, BlockStatement block)
    {
        foreach (Statement stmt in block.Statements)
        {
            if (EmitStatement(sb: sb, stmt: stmt))
            {
                return true; // Block terminated early
            }
        }

        return false;
    }

    #endregion

    #region Variable Declarations

    /// <summary>
    /// Emits code for a declaration statement.
    /// Handles variable declarations with alloca + store.
    /// </summary>
    private void EmitDeclarationStatement(StringBuilder sb, DeclarationStatement decl)
    {
        if (decl.Declaration is VariableDeclaration varDecl)
        {
            EmitVariableDeclaration(sb: sb, varDecl: varDecl);
        }
        // Other declaration types (function, type) are handled at module level
    }

    /// <summary>
    /// Emits code for a variable declaration.
    /// Creates stack allocation and optionally stores initial value.
    /// </summary>
    private void EmitVariableDeclaration(StringBuilder sb, VariableDeclaration varDecl)
    {
        // Determine the type
        TypeInfo? varType = null;
        if (varDecl.Type != null)
        {
            varType = ResolveTypeExpression(typeExpr: varDecl.Type);
        }
        else if (varDecl.Initializer != null)
        {
            varType = GetExpressionType(expr: varDecl.Initializer);
        }

        if (varType == null)
        {
            throw new InvalidOperationException(
                message: $"Cannot determine type for variable '{varDecl.Name}'");
        }

        string llvmType = GetLLVMType(type: varType);

        // Generate unique LLVM name for this variable (handles shadowing/redeclaration)
        string uniqueName;
        if (_varNameCounts.TryGetValue(key: varDecl.Name, value: out int count))
        {
            _varNameCounts[key: varDecl.Name] = count + 1;
            uniqueName = $"{varDecl.Name}.{count + 1}";
        }
        else
        {
            _varNameCounts[key: varDecl.Name] = 1;
            uniqueName = varDecl.Name;
        }

        // Allocate stack space
        string varPtr = $"%{uniqueName}.addr";
        EmitEntryAlloca(llvmName: varPtr, llvmType: llvmType);

        // Register local variable for identifier lookup
        _localVariables[key: varDecl.Name] = varType;
        _localVarLLVMNames[key: varDecl.Name] = uniqueName;

        // Track entity variables for automatic cleanup at return points
        // Only track when initialized via constructor (actual heap allocation)
        if (varType is EntityTypeInfo && IsEntityConstructorCall(expr: varDecl.Initializer))
        {
            _localEntityVars.Add(item: (varDecl.Name, $"%{uniqueName}.addr"));
        }

        // Track record variables with RC wrapper fields for retain/release
        if (varType is RecordTypeInfo { HasRCFields: true } rcRecord)
        {
            _localRCRecordVars.Add(item: (varDecl.Name, $"%{uniqueName}.addr", rcRecord));
        }

        // Store initial value if present
        if (varDecl.Initializer != null)
        {
            string value = EmitExpression(sb: sb, expr: varDecl.Initializer);
            EmitLine(sb: sb, line: $"  store {llvmType} {value}, ptr {varPtr}");

            // Retain RC fields on initial copy
            if (varType is RecordTypeInfo { HasRCFields: true } rcRecordInit)
            {
                EmitRCRecordRetain(sb: sb, llvmAddr: varPtr, recordType: rcRecordInit);
            }
        }
    }

    /// <summary>
    /// Resolves a type expression to a TypeInfo.
    /// </summary>
    private TypeInfo? ResolveTypeExpression(TypeExpression typeExpr)
    {
        return ResolveTypeArgument(ta: typeExpr);
    }

    #endregion

    #region Assignments

    /// <summary>
    /// Emits code for an assignment statement.
    /// Handles simple variable assignment and member variable assignment.
    /// </summary>
    private void EmitAssignment(StringBuilder sb, AssignmentStatement assign)
    {
        // Evaluate the value first
        string value = EmitExpression(sb: sb, expr: assign.Value);

        // Determine target type and emit store
        switch (assign.Target)
        {
            case IdentifierExpression id:
                EmitVariableAssignment(sb: sb, varName: id.Name, value: value);
                break;

            case MemberExpression member:
                EmitMemberVariableAssignment(sb: sb,
                    member: member,
                    value: value,
                    valueType: GetExpressionType(expr: assign.Value));
                break;

            case IndexExpression index:
                EmitIndexAssignment(sb: sb, index: index, value: value);
                break;

            default:
                throw new NotImplementedException(
                    message: $"Assignment target not implemented: {assign.Target.GetType().Name}");
        }
    }

    /// <summary>
    /// Emits a store to a local variable.
    /// For RC record variables, releases old value's RC fields and retains new value's RC fields.
    /// </summary>
    private void EmitVariableAssignment(StringBuilder sb, string varName, string value)
    {
        if (!_localVariables.TryGetValue(key: varName, value: out TypeInfo? varType))
        {
            throw new InvalidOperationException(message: $"Variable '{varName}' not found");
        }

        string llvmName = _localVarLLVMNames.TryGetValue(key: varName, value: out string? unique)
            ? unique
            : varName;
        string llvmType = GetLLVMType(type: varType);
        string varPtr = $"%{llvmName}.addr";

        // Release old value's RC fields before overwrite
        if (varType is RecordTypeInfo { HasRCFields: true } rcRecord)
        {
            EmitRCRecordRelease(sb: sb, llvmAddr: varPtr, recordType: rcRecord);
        }

        EmitLine(sb: sb, line: $"  store {llvmType} {value}, ptr {varPtr}");

        // Retain new value's RC fields
        if (varType is RecordTypeInfo { HasRCFields: true } rcRecordNew)
        {
            EmitRCRecordRetain(sb: sb, llvmAddr: varPtr, recordType: rcRecordNew);
        }
    }

    /// <summary>
    /// Emits a store to a member variable.
    /// </summary>
    private void EmitMemberVariableAssignment(StringBuilder sb, MemberExpression member,
        string value, TypeInfo? valueType = null)
    {
        // Evaluate the object
        string target = EmitExpression(sb: sb, expr: member.Object);
        TypeInfo? targetType = GetExpressionType(expr: member.Object);

        if (targetType is EntityTypeInfo entity)
        {
            EmitEntityMemberVariableWrite(sb: sb,
                entityPtr: target,
                entity: entity,
                memberVariableName: member.PropertyName,
                value: value,
                valueType: valueType);
        }
        // Wrapper type forwarding: Hijacked[T], Seized[T], etc. — write through to inner entity
        else if (targetType is RecordTypeInfo wrapperRecord &&
                 GetGenericBaseName(type: wrapperRecord) is { } wrapBaseName &&
                 _wrapperTypeNames.Contains(item: wrapBaseName) &&
                 wrapperRecord.TypeArguments is { Count: > 0 } &&
                 wrapperRecord.TypeArguments[index: 0] is EntityTypeInfo innerEntity)
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

            EmitEntityMemberVariableWrite(sb: sb,
                entityPtr: innerPtr,
                entity: innerEntity,
                memberVariableName: member.PropertyName,
                value: value,
                valueType: valueType);
        }
        else
        {
            throw new InvalidOperationException(
                message: $"Cannot assign to member variable on type: {targetType?.Name}");
        }
    }

    /// <summary>
    /// Emits a store to an indexed location.
    /// </summary>
    private void EmitIndexAssignment(StringBuilder sb, IndexExpression index, string value)
    {
        TypeInfo? targetType = GetExpressionType(expr: index.Object);

        // Dispatch to $setitem if the type has one
        RoutineInfo? setItem = LookupSetItemMethod(index: index);
        if (setItem != null && targetType != null &&
            (!setItem.IsGenericDefinition || targetType.IsGenericResolution))
        {
            // For record $setitem!: pass alloca pointer so mutations propagate
            string receiver;
            string receiverLlvm;
            bool isRecordSetItem = targetType is RecordTypeInfo &&
                                   setItem.Name.Contains(value: "$setitem");
            if (isRecordSetItem && index.Object is IdentifierExpression recId)
            {
                string llvmName =
                    _localVarLLVMNames.TryGetValue(key: recId.Name, value: out string? unique)
                        ? unique
                        : recId.Name;
                receiver = $"%{llvmName}.addr";
                receiverLlvm = "ptr";
            }
            else
            {
                receiver = EmitExpression(sb: sb, expr: index.Object);
                receiverLlvm = GetParameterLLVMType(type: targetType);
            }

            string indexValue = EmitExpression(sb: sb, expr: index.Index);
            TypeInfo? indexType = GetExpressionType(expr: index.Index);

            // Build mangled name with proper monomorphization
            string mangledName;
            if (setItem.IsGenericDefinition && targetType.IsGenericResolution)
            {
                // Infer method-level type args from the explicit arguments (index, value)
                var concreteArgTypes = new List<TypeInfo>();
                if (indexType != null)
                {
                    concreteArgTypes.Add(item: indexType);
                }

                Dictionary<string, TypeInfo>? methodTypeArgs =
                    InferMethodTypeArgs(genericMethod: setItem, argTypes: concreteArgTypes);

                if (methodTypeArgs != null)
                {
                    var resolvedParamNames = new List<string> { targetType.Name };
                    if (indexType != null)
                    {
                        resolvedParamNames.Add(item: indexType.Name);
                    }

                    // Resolve value param type
                    if (setItem.Parameters.Count >= 2)
                    {
                        TypeInfo valParamType =
                            ResolveSubstitutedType(type: setItem.Parameters[^1].Type,
                                subs: methodTypeArgs);
                        resolvedParamNames.Add(item: valParamType.Name);
                    }

                    string ownerName = setItem.OwnerType?.FullName ?? targetType.FullName;
                    mangledName =
                        Q(name: DecorateRoutineSymbolName(
                            baseName: $"{ownerName}.{SanitizeLLVMName(name: setItem.Name)}({string.Join(separator: ",", values: resolvedParamNames)})",
                            isFailable: setItem.IsFailable));
                    RecordMonomorphization(mangledName: mangledName,
                        genericMethod: setItem,
                        resolvedOwnerType: targetType,
                        methodTypeArgs: methodTypeArgs);
                }
                else
                {
                    mangledName =
                        Q(name: $"{targetType.FullName}.{SanitizeLLVMName(name: setItem.Name)}");
                    RecordMonomorphization(mangledName: mangledName,
                        genericMethod: setItem,
                        resolvedOwnerType: targetType);
                }
            }
            else if (targetType.IsGenericResolution &&
                     setItem.OwnerType is { IsGenericDefinition: true })
            {
                mangledName =
                    Q(name: $"{targetType.FullName}.{SanitizeLLVMName(name: setItem.Name)}");
                RecordMonomorphization(mangledName: mangledName,
                    genericMethod: setItem,
                    resolvedOwnerType: targetType);
            }
            else
            {
                mangledName = MangleFunctionName(routine: setItem);
            }

            GenerateFunctionDeclaration(routine: setItem);

            // Build argument types
            string indexLlvm = indexType != null
                ? GetLLVMType(type: indexType)
                : "i64";
            string valueLlvm = indexType != null
                ? GetLLVMType(type: indexType)
                : "i64"; // placeholder

            // Use type arguments to resolve the value parameter type
            // For List[T]: value is TypeArguments[0]; for Dict[K, V]: value is TypeArguments[^1]
            if (targetType.TypeArguments is { Count: > 0 })
            {
                valueLlvm = GetLLVMType(type: targetType.TypeArguments[^1]);
            }
            else if (setItem.Parameters.Count >= 2)
            {
                valueLlvm = GetLLVMType(type: setItem.Parameters[^1].Type);
            }

            string args =
                $"{receiverLlvm} {receiver}, {indexLlvm} {indexValue}, {valueLlvm} {value}";
            EmitLine(sb: sb, line: $"  call void @{mangledName}({args})");
            return;
        }

        // Fallback: raw GEP + store for pointer/contiguous-memory types
        string target = EmitExpression(sb: sb, expr: index.Object);
        string idxVal = EmitExpression(sb: sb, expr: index.Index);

        string elemType = targetType switch
        {
            RecordTypeInfo r when r.TypeArguments?.Count > 0 => GetLLVMType(
                type: r.TypeArguments[index: 0]),
            EntityTypeInfo e when e.TypeArguments?.Count > 0 => GetLLVMType(
                type: e.TypeArguments[index: 0]),
            _ => throw new InvalidOperationException(
                message:
                $"Cannot determine element type for index assignment on type: {targetType?.Name}")
        };

        string elemPtr = NextTemp();
        EmitLine(sb: sb,
            line: $"  {elemPtr} = getelementptr {elemType}, ptr {target}, i64 {idxVal}");
        EmitLine(sb: sb, line: $"  store {elemType} {value}, ptr {elemPtr}");
    }

    /// <summary>
    /// Looks up the $setitem method for an indexed target, handling failable names and generic types.
    /// </summary>
    private RoutineInfo? LookupSetItemMethod(IndexExpression index)
    {
        TypeInfo? targetType = GetExpressionType(expr: index.Object);
        if (targetType == null)
        {
            return null;
        }

        return _registry.LookupMethod(type: targetType, methodName: "$setitem");
    }

    #endregion

    #region Return Statements

    /// <summary>
    /// Emits code for a return statement.
    /// In emitting routines, a bare return (no value) after an emit delivers the stored value
    /// wrapped in a Snatched handle: { i64 1, ptr handle }.
    /// </summary>
    private void EmitReturn(StringBuilder sb, ReturnStatement ret)
    {
        if (ret.Value == null)
        {
            // Bare return in emitting routine: deliver the emit slot value
            if (_emitSlotAddr != null && _emitSlotType != null)
            {
                // Load the stored emit value
                string loaded = NextTemp();
                EmitLine(sb: sb, line: $"  {loaded} = load {_emitSlotType}, ptr {_emitSlotAddr}");

                TypeInfo? emitElemType = _currentEmittingRoutine?.ReturnType;
                string emitCarrierType = emitElemType != null
                    ? GetMaybeCarrierLLVMType(valueType: emitElemType)
                    : "{ i1, ptr }";

                // Build { i1 1, <value> } — VALID
                string v0 = NextTemp();
                EmitLine(sb: sb, line: $"  {v0} = insertvalue {emitCarrierType} undef, i1 1, 0");
                string v1 = NextTemp();
                if (emitElemType is EntityTypeInfo)
                    EmitLine(sb: sb,
                        line: $"  {v1} = insertvalue {emitCarrierType} {v0}, ptr {loaded}, 1");
                else
                    EmitLine(sb: sb,
                        line: $"  {v1} = insertvalue {emitCarrierType} {v0}, {_emitSlotType} {loaded}, 1");

                EmitUsingCleanup(sb: sb);
                EmitRCRecordCleanup(sb: sb);
                EmitEntityCleanup(sb: sb, returnedVarName: null);
                if (ShouldEmitTrace)
                    EmitLine(sb: sb, line: "  call void @rf_trace_pop()");
                EmitLine(sb: sb, line: $"  ret {emitCarrierType} {v1}");
            }
            else
            {
                EmitUsingCleanup(sb: sb);
                EmitRCRecordCleanup(sb: sb);
                EmitEntityCleanup(sb: sb, returnedVarName: null);
                if (ShouldEmitTrace)
                    EmitLine(sb: sb, line: "  call void @rf_trace_pop()");
                EmitLine(sb: sb, line: "  ret void");
            }
        }
        else
        {
            string value = EmitExpression(sb: sb, expr: ret.Value);
            // Prefer current function's return type (matches the 'define' header)
            // to avoid type mismatches between header and ret instruction
            TypeInfo? retType = _currentFunctionReturnType ?? GetExpressionType(expr: ret.Value);
            if (retType == null)
            {
                throw new InvalidOperationException(
                    message: "Cannot determine return type for return statement");
            }

            string llvmType = GetLLVMType(type: retType);

            // Skip cleanup for the returned entity variable (ownership transfers to caller)
            string? returnedVarName = ret.Value is IdentifierExpression id &&
                                      _localEntityVars.Any(predicate: e => e.Name == id.Name)
                ? id.Name
                : null;
            EmitUsingCleanup(sb: sb);
            EmitRCRecordCleanup(sb: sb);
            EmitEntityCleanup(sb: sb, returnedVarName: returnedVarName);

            if (ShouldEmitTrace)
                EmitLine(sb: sb, line: "  call void @rf_trace_pop()");

            // Auto-wrap bare values in Maybe when function returns Maybe[T] but expression is T
            else if (IsMaybeType(type: retType) && value != "zeroinitializer")
            {
                TypeInfo? exprType = GetExpressionType(expr: ret.Value);
                if (exprType == null || !IsMaybeType(type: exprType))
                {
                    TypeInfo innerType = retType is ErrorHandlingTypeInfo eh ? eh.ValueType :
                        retType.TypeArguments is { Count: > 0 } ? retType.TypeArguments[index: 0] :
                        retType;
                    string carrierType = GetLLVMType(type: retType);
                    string v0 = NextTemp();
                    EmitLine(sb: sb, line: $"  {v0} = insertvalue {carrierType} undef, i1 1, 0");
                    string v1 = NextTemp();
                    if (innerType is EntityTypeInfo)
                    {
                        // Entity types are already pointers — insert directly
                        EmitLine(sb: sb,
                            line: $"  {v1} = insertvalue {carrierType} {v0}, ptr {value}, 1");
                    }
                    else
                    {
                        string innerLlvm = GetLLVMType(type: innerType);
                        EmitLine(sb: sb,
                            line: $"  {v1} = insertvalue {carrierType} {v0}, {innerLlvm} {value}, 1");
                    }

                    EmitLine(sb: sb, line: $"  ret {carrierType} {v1}");
                }
                else
                {
                    EmitLine(sb: sb, line: $"  ret {llvmType} {value}");
                }
            }
            else
            {
                EmitLine(sb: sb, line: $"  ret {llvmType} {value}");
            }
        }
    }

    /// <summary>
    /// Returns true if the expression is an entity constructor call (heap allocation).
    /// Matches both CreatorExpression and CallExpression that resolve to an entity type.
    /// </summary>
    private bool IsEntityConstructorCall(Expression? expr)
    {
        if (expr is CreatorExpression)
        {
            return true;
        }

        if (expr is ListLiteralExpression)
        {
            return true;
        }

        if (expr is SetLiteralExpression)
        {
            return true;
        }

        if (expr is DictLiteralExpression)
        {
            return true;
        }

        if (expr is CallExpression { Callee: IdentifierExpression id })
        {
            return _registry.LookupType(name: id.Name) is EntityTypeInfo;
        }

        return false;
    }

    /// <summary>
    /// Emits rf_invalidate calls for all locally-owned entity variables.
    /// Skips the variable being returned (ownership transfers to caller).
    /// </summary>
    private void EmitEntityCleanup(StringBuilder sb, string? returnedVarName)
    {
        foreach ((string name, string llvmAddr) in _localEntityVars)
        {
            if (name == returnedVarName)
            {
                continue;
            }

            string loaded = NextTemp();
            EmitLine(sb: sb, line: $"  {loaded} = load ptr, ptr {llvmAddr}");
            string asInt = NextTemp();
            EmitLine(sb: sb, line: $"  {asInt} = ptrtoint ptr {loaded} to i64");
            EmitLine(sb: sb, line: $"  call void @rf_invalidate(i64 {asInt})");
        }
    }

    /// <summary>
    /// Emits $exit() calls for all active using scopes (innermost first).
    /// Called before early exits (return, throw, break, continue, absent).
    /// </summary>
    private void EmitUsingCleanup(StringBuilder sb)
    {
        foreach ((string resourceValue, TypeInfo resourceType, RoutineInfo exitMethod) in
                 _usingCleanupStack)
        {
            string exitMangled = MangleFunctionName(routine: exitMethod);
            string receiverType = GetParameterLLVMType(type: resourceType);
            EmitLine(sb: sb, line: $"  call void @{exitMangled}({receiverType} {resourceValue})");
        }
    }

    /// <summary>
    /// Emits $exit() calls for using scopes pushed after <paramref name="untilDepth"/>.
    /// Used by break/continue to clean up only scopes inside the current loop,
    /// leaving outer scopes for their own normal-exit path.
    /// </summary>
    private void EmitUsingCleanup(StringBuilder sb, int untilDepth)
    {
        int toClean = _usingCleanupStack.Count - untilDepth;
        int cleaned = 0;
        foreach ((string resourceValue, TypeInfo resourceType, RoutineInfo exitMethod) in
                 _usingCleanupStack)
        {
            if (cleaned >= toClean) break;
            string exitMangled = MangleFunctionName(routine: exitMethod);
            string receiverType = GetParameterLLVMType(type: resourceType);
            EmitLine(sb: sb, line: $"  call void @{exitMangled}({receiverType} {resourceValue})");
            cleaned++;
        }
    }

    #endregion

    #region Emit Statement

    /// <summary>
    /// Emits code for an emit statement in an emitting routine.
    /// Stores the value in a hidden return slot. Does not terminate the block —
    /// code after emit does bookkeeping, then a bare return delivers the value.
    /// </summary>
    private void EmitEmit(StringBuilder sb, EmitStatement emitStmt)
    {
        string value = EmitExpression(sb: sb, expr: emitStmt.Expression);

        TypeInfo? valueType =
            GetExpressionType(expr: emitStmt.Expression) ?? _currentFunctionReturnType;
        if (valueType == null)
        {
            throw new InvalidOperationException(
                message: "Cannot determine type for emit expression");
        }

        string llvmType = GetLLVMType(type: valueType);

        // Tuple literals produce inline aggregates, not pointers — match EmitTupleLiteral's actual type
        if (llvmType == "ptr" && emitStmt.Expression is TupleLiteralExpression tupleEmitExpr)
        {
            IEnumerable<string> elemTypes = tupleEmitExpr.Elements.Select(selector: e =>
            {
                TypeInfo? et = GetExpressionType(expr: e);
                return et != null
                    ? GetLLVMType(type: et)
                    : "i64";
            });
            llvmType = $"{{ {string.Join(separator: ", ", values: elemTypes)} }}";
        }

        // Lazily create the emit slot alloca on first emit
        if (_emitSlotAddr == null)
        {
            _emitSlotAddr = "%emit.slot.addr";
            _emitSlotType = llvmType;
            EmitEntryAlloca(llvmName: _emitSlotAddr, llvmType: llvmType);
        }

        // Store the emitted value
        EmitLine(sb: sb, line: $"  store {llvmType} {value}, ptr {_emitSlotAddr}");
    }

    #endregion

    #region Throw / Absent / Becomes

    /// <summary>
    /// Emits code for a throw statement.
    /// Evaluates the crashable expression, calls crash_message(), and invokes rf_crash
    /// with the error type name, message, and source location for full error reporting.
    /// </summary>
    private void EmitThrow(StringBuilder sb, ThrowStatement throwStmt)
    {
        // rf_crash is declared via NativeDeclarations.rf registry

        // Get the error type info
        TypeInfo? errorType = GetExpressionType(expr: throwStmt.Error);
        string typeName = errorType?.Name ?? "UnknownError";

        // Check if error type is an empty record (no member variables) — skip construction
        bool isEmptyRecord = errorType is RecordTypeInfo { MemberVariables.Count: 0 };
        string errorVal;
        if (isEmptyRecord)
        {
            // Empty record — no need to construct, use zeroinitializer
            errorVal = "zeroinitializer";
        }
        else
        {
            errorVal = EmitExpression(sb: sb, expr: throwStmt.Error);
        }

        // Try to call crash_message() on the error to get the message Text
        string dataPtr = "null";
        string msgLen = "0";
        ResolvedMethod? resolvedCrash = errorType != null
            ? ResolveMethod(receiverType: errorType, methodName: "crash_message")
            : null;
        if (resolvedCrash != null)
        {
            // Ensure crash_message is declared/defined
            GenerateFunctionDeclaration(routine: resolvedCrash.Routine);
            string mangledCrash = resolvedCrash.MangledName;

            string llvmReceiverType = GetLLVMType(type: errorType!);

            // Call crash_message(me) → returns ptr to Text entity
            string textPtr = NextTemp();
            EmitLine(sb: sb,
                line: $"  {textPtr} = call ptr @{mangledCrash}({llvmReceiverType} {errorVal})");

            // Text entity = { ptr letters_list }
            // List[Character] entity = { ptr data, i64 count, i64 capacity }
            string lettersPtr = NextTemp();
            EmitLine(sb: sb, line: $"  {lettersPtr} = load ptr, ptr {textPtr}");
            string dataField = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {dataField} = getelementptr {{ptr, i64, i64}}, ptr {lettersPtr}, i32 0, i32 0");
            dataPtr = NextTemp();
            EmitLine(sb: sb, line: $"  {dataPtr} = load ptr, ptr {dataField}");
            string countField = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {countField} = getelementptr {{ptr, i64, i64}}, ptr {lettersPtr}, i32 0, i32 1");
            msgLen = NextTemp();
            EmitLine(sb: sb, line: $"  {msgLen} = load i64, ptr {countField}");
        }

        // Emit C string constants for type name and file, cast ptr → i64 (Address)
        string typeCStr = EmitCStringConstant(value: typeName);
        string fileCStr = EmitCStringConstant(value: throwStmt.Location.FileName);
        string typeNameAsInt = NextTemp();
        EmitLine(sb: sb, line: $"  {typeNameAsInt} = ptrtoint ptr {typeCStr} to i64");
        string fileAsInt = NextTemp();
        EmitLine(sb: sb, line: $"  {fileAsInt} = ptrtoint ptr {fileCStr} to i64");

        // Cast message data ptr → i64 (Address)
        string msgDataAsInt;
        if (dataPtr == "null")
        {
            msgDataAsInt = "0";
        }
        else
        {
            msgDataAsInt = NextTemp();
            EmitLine(sb: sb, line: $"  {msgDataAsInt} = ptrtoint ptr {dataPtr} to i64");
        }

        // Clean up active scopes before crashing
        EmitUsingCleanup(sb: sb);
        EmitRCRecordCleanup(sb: sb);

        // Call rf_crash — never returns
        EmitLine(sb: sb,
            line:
            $"  call void @rf_crash(i64 {typeNameAsInt}, i64 {typeName.Length}, i64 {fileAsInt}, i64 {throwStmt.Location.FileName.Length}, i32 {throwStmt.Location.Line}, i32 {throwStmt.Location.Column}, i64 {msgDataAsInt}, i64 {msgLen})");
        EmitLine(sb: sb, line: "  unreachable");
    }

    /// <summary>
    /// Emits code for an absent statement.
    /// Returns a { i64, ptr } with tag = 0 (ABSENT) and ptr = null.
    /// Used in try_* routines that return Maybe[T] or Lookup[T].
    /// </summary>
    private void EmitAbsent(StringBuilder sb)
    {
        // In a failable routine, `absent` is a contract violation — crash
        if (_currentRoutineIsFailable)
        {
            EmitUsingCleanup(sb: sb);
            EmitRCRecordCleanup(sb: sb);
            EmitLine(sb: sb,
                line: "  call void @rf_crash(i64 0, i64 0, i64 0, i64 0, i32 0, i32 0, i64 0, i64 0)");
            EmitLine(sb: sb, line: "  unreachable");
            return;
        }

        // Return zeroinitializer — tag=0 means ABSENT for any carrier layout
        TypeInfo? absentElemType = _currentEmittingRoutine?.ReturnType;
        string absentCarrierType = absentElemType != null
            ? GetMaybeCarrierLLVMType(valueType: absentElemType)
            : "{ i1, ptr }";
        // Clean up active scopes before returning absent
        EmitUsingCleanup(sb: sb);
        EmitRCRecordCleanup(sb: sb);
        EmitLine(sb: sb, line: $"  ret {absentCarrierType} zeroinitializer");
    }

    /// <summary>
    /// Emits code for a becomes statement (result value in multi-statement when arms).
    /// Evaluates the expression — the value is used as the arm's result.
    /// </summary>
    private void EmitBecomes(StringBuilder sb, BecomesStatement becomesStmt)
    {
        // Evaluate the becomes expression. In the current when statement codegen,
        // this is equivalent to an expression statement — the value flows through
        // the block's last expression. Future: store to a when-result alloca and branch to merge.
        EmitExpression(sb: sb, expr: becomesStmt.Value);
    }

    #endregion

    #region RC Record Cleanup

    /// <summary>RC wrapper base names that require retain/release.</summary>
    private static readonly HashSet<string> _rcWrapperBaseNames =
        ["Retained", "Shared", "Tracked", "Marked"];

    /// <summary>
    /// Emits retain calls for all RC wrapper fields in a record.
    /// Called when a record with RC fields is copied into a new variable.
    /// </summary>
    private void EmitRCRecordRetain(StringBuilder sb, string llvmAddr, RecordTypeInfo recordType)
    {
        string llvmType = GetLLVMType(type: recordType);
        string loaded = NextTemp();
        EmitLine(sb: sb, line: $"  {loaded} = load {llvmType}, ptr {llvmAddr}");

        foreach (MemberVariableInfo field in recordType.MemberVariables)
        {
            if (field.Type is not WrapperTypeInfo w || !_rcWrapperBaseNames.Contains(item: w.Name))
            {
                continue;
            }

            string fieldVal = NextTemp();
            EmitLine(sb: sb,
                line: $"  {fieldVal} = extractvalue {llvmType} {loaded}, {field.Index}");

            RoutineInfo? retainMethod = _registry.LookupMethod(type: w, methodName: "retain");
            if (retainMethod == null)
            {
                continue;
            }

            GenerateFunctionDeclaration(routine: retainMethod);
            string mangled = MangleFunctionName(routine: retainMethod);
            string fieldLlvm = GetParameterLLVMType(type: w);
            EmitLine(sb: sb,
                line: $"  {NextTemp()} = call {fieldLlvm} @{mangled}({fieldLlvm} {fieldVal})");
        }
    }

    /// <summary>
    /// Emits release calls for all RC wrapper fields in a record.
    /// Called before overwriting a record variable or at scope exit.
    /// </summary>
    private void EmitRCRecordRelease(StringBuilder sb, string llvmAddr, RecordTypeInfo recordType)
    {
        string llvmType = GetLLVMType(type: recordType);
        string loaded = NextTemp();
        EmitLine(sb: sb, line: $"  {loaded} = load {llvmType}, ptr {llvmAddr}");

        foreach (MemberVariableInfo field in recordType.MemberVariables)
        {
            if (field.Type is not WrapperTypeInfo w || !_rcWrapperBaseNames.Contains(item: w.Name))
            {
                continue;
            }

            string fieldVal = NextTemp();
            EmitLine(sb: sb,
                line: $"  {fieldVal} = extractvalue {llvmType} {loaded}, {field.Index}");

            RoutineInfo? releaseMethod = _registry.LookupMethod(type: w, methodName: "release");
            if (releaseMethod == null)
            {
                continue;
            }

            GenerateFunctionDeclaration(routine: releaseMethod);
            string mangled = MangleFunctionName(routine: releaseMethod);
            string fieldLlvm = GetParameterLLVMType(type: w);
            EmitLine(sb: sb, line: $"  call void @{mangled}({fieldLlvm} {fieldVal})");
        }
    }

    /// <summary>
    /// Emits release calls for all tracked RC record variables at scope exit.
    /// Called at return, throw, absent — after EmitUsingCleanup, before EmitEntityCleanup.
    /// </summary>
    private void EmitRCRecordCleanup(StringBuilder sb)
    {
        foreach ((string _, string llvmAddr, RecordTypeInfo recordType) in _localRCRecordVars)
        {
            EmitRCRecordRelease(sb: sb, llvmAddr: llvmAddr, recordType: recordType);
        }
    }

    #endregion
}
