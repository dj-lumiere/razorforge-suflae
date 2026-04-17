using Compiler.Synthesis;
using SemanticVerification.Symbols;

namespace Compiler.CodeGen;

using System.Text;
using SemanticVerification.Types;
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

            case LoopStatement loopStmt:
                EmitLoop(sb: sb, loopStmt: loopStmt);
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
                return EmitWhen(sb: sb, whenStmt: whenStmt);

            case DiscardStatement discard:
                // TODO(C43): for creator expressions, skip evaluation entirely — creators have no
                // observable side effects and their result is being discarded, so the allocation is wasted.
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

            case BecomesStatement becomesStmt:
                EmitBecomes(sb: sb, becomesStmt: becomesStmt);
                return false;

            case VariantReturnStatement variantRet:
                EmitVariantReturn(sb: sb, variantRet: variantRet);
                return true; // Always a terminator

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
    /// Global declarations (Storage == Global) are emitted at module level in
    /// GenerateGlobalVariableDeclarations and are skipped here.
    /// </summary>
    private void EmitVariableDeclaration(StringBuilder sb, VariableDeclaration varDecl)
    {
        // Global variables are declared at module level — no local alloca needed.
        if (varDecl.Storage == StorageClass.Global) return;

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

            // When the declaration has an explicit type annotation, the initializer may have a
            // different LLVM type (e.g., var e: U32 = exp where exp: S128 → trunc i128 to i32).
            // Emit an inline type cast so the store type always matches the alloca type.
            if (varDecl.Type != null)
            {
                TypeInfo? initType = GetExpressionType(expr: varDecl.Initializer);
                if (initType != null)
                {
                    string initLlvm = GetLLVMType(type: initType);
                    if (initLlvm != llvmType)
                        value = EmitPrimitiveCast(sb: sb, value: value, fromLlvm: initLlvm,
                            toLlvm: llvmType);
                }
            }

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
        // Check if this is a module-level global variable
        if (_globalVariables.TryGetValue(key: varName, value: out TypeInfo? globalType) &&
            _globalVariableLlvmNames.TryGetValue(key: varName, value: out string? globalLlvm))
        {
            string globalLlvmType = GetLLVMType(type: globalType);
            EmitLine(sb: sb, line: $"  store {globalLlvmType} {value}, ptr {globalLlvm}");
            return;
        }

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
                            _planner.ResolveSubstitutedType(type: setItem.Parameters[^1].Type,
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
            else if (targetType.IsGenericResolution)
            {
                mangledName =
                    Q(name: DecorateRoutineSymbolName(
                        baseName: $"{targetType.FullName}.{SanitizeLLVMName(name: setItem.Name)}",
                        isFailable: setItem.IsFailable));
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
            RecordTypeInfo { TypeArguments.Count: > 0 } r => GetLLVMType(
                type: r.TypeArguments[index: 0]),
            EntityTypeInfo { TypeArguments.Count: > 0 } e => GetLLVMType(
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
    /// </summary>
    private void EmitReturn(StringBuilder sb, ReturnStatement ret)
    {
        if (ret.Value == null)
        {
            EmitUsingCleanup(sb: sb);
            EmitRCRecordCleanup(sb: sb);
            EmitEntityCleanup(sb: sb, returnedVarName: null);
            if (_traceCurrentRoutine)
                EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");
            if (_currentFunctionReturnType == null)
                EmitLine(sb: sb, line: "  ret void");
            else
            {
                string retLlvmType = GetLLVMType(type: _currentFunctionReturnType);
                if (retLlvmType == "void")
                    EmitLine(sb: sb, line: "  ret void");
                else
                {
                    string retZero = GetZeroValue(type: _currentFunctionReturnType);
                    EmitLine(sb: sb, line: $"  ret {retLlvmType} {retZero}");
                }
            }
        }
        else
        {
            // Blank is llvm void — if the function returns void, skip evaluating the expression.
            // BlankReturnNormalizationPass fills bare `return` with IdentifierExpression("Blank");
            // since the return type maps to void, no LLVM value is needed.
            string earlyType = _currentFunctionReturnType != null
                ? GetLLVMType(type: _currentFunctionReturnType)
                : "void"; // this should throw error
            if (earlyType == "void")
            {
                EmitUsingCleanup(sb: sb);
                EmitRCRecordCleanup(sb: sb);
                EmitEntityCleanup(sb: sb, returnedVarName: null);
                if (_traceCurrentRoutine)
                    EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");
                EmitLine(sb: sb, line: "  ret void");
                return;
            }

            // If returning a crashable from a failable routine, treat as throw (RF idiom).
            // `return ErrorType(...)` in a failable function is a throw, not a normal return.
            TypeInfo? retValType = GetExpressionType(expr: ret.Value);
            if (retValType is CrashableTypeInfo && _currentRoutineIsFailable)
            {
                EmitThrow(sb: sb, throwStmt: new ThrowStatement(Error: ret.Value, Location: ret.Location));
                return;
            }

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

            if (_traceCurrentRoutine)
                EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");

            // Auto-wrap bare values in Maybe when function returns Maybe[T] but expression is T.
            // Record Maybe { i1 present, T value }: insertvalue i1 1 at 0, T at 1.
            // Entity Maybe { Snatched[T] }:         single ptr field — non-null ptr = present.
            if (IsMaybeType(type: retType) && value != "zeroinitializer")
            {
                TypeInfo? exprType = GetExpressionType(expr: ret.Value);
                if (exprType == null || !IsMaybeType(type: exprType))
                {
                    TypeInfo innerType = retType.TypeArguments is { Count: > 0 }
                        ? retType.TypeArguments[index: 0]
                        : retType;
                    string carrierType = GetLLVMType(type: retType);
                    if (innerType is EntityTypeInfo)
                    {
                        string v0 = NextTemp();
                        EmitLine(sb: sb,
                            line: $"  {v0} = insertvalue {carrierType} zeroinitializer, ptr {value}, 0");
                        EmitLine(sb: sb, line: $"  ret {carrierType} {v0}");
                    }
                    else
                    {
                        string innerLlvm = GetLLVMType(type: innerType);
                        string v0 = NextTemp();
                        EmitLine(sb: sb, line: $"  {v0} = insertvalue {carrierType} zeroinitializer, i1 1, 0");
                        string v1 = NextTemp();
                        EmitLine(sb: sb,
                            line: $"  {v1} = insertvalue {carrierType} {v0}, {innerLlvm} {value}, 1");
                        EmitLine(sb: sb, line: $"  ret {carrierType} {v1}");
                    }
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
        return expr switch
        {
            CreatorExpression or ListLiteralExpression or SetLiteralExpression
                or DictLiteralExpression => true,
            CallExpression { Callee: IdentifierExpression id } =>
                _registry.LookupType(name: id.Name) is EntityTypeInfo,
            _ => false
        };
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

    #region Throw / Absent / Becomes

    /// <summary>
    /// Emits code for a <see cref="VariantReturnStatement"/> inserted into error-handling variant
    /// bodies by <see cref="Desugaring.Passes.ErrorHandlingVariantPass"/>.
    /// Handles carrier construction for all (VariantKind, SiteKind) combinations without relying
    /// on mutable _currentVariantIs* flag fields.
    /// </summary>
    private void EmitVariantReturn(StringBuilder sb, VariantReturnStatement variantRet)
    {
        // ReturnType is either the carrier (Maybe[T]/Result[T]/Lookup[T]) or the inner T.
        // MonomorphizationPlanner strips the carrier for generic routines (ReturnType = T).
        // Non-generic stdlib variant routines keep the carrier as ReturnType.
        // Always extract inner T from the carrier when present.
        TypeInfo returnType = _currentEmittingRoutine!.ReturnType!;
        TypeInfo innerType = (variantRet.VariantKind == ErrorHandlingVariantKind.Try
                              || IsCarrierType(type: returnType))
            ? returnType.TypeArguments![index: 0]
            : returnType;

        switch (variantRet.VariantKind, variantRet.SiteKind)
        {
            // ── Try variant ────────────────────────────────────────────────────────────────
            case (ErrorHandlingVariantKind.Try, VariantSiteKind.FromThrow):
            {
                // Evaluate error expression for side effects (matches existing EmitThrow behavior).
                // Skip if errType is unknown (unregistered type) — cannot construct it safely.
                if (variantRet.Value != null)
                {
                    TypeInfo? errType = GetExpressionType(expr: variantRet.Value);
                    bool isEmptyRec = errType == null
                        || errType is RecordTypeInfo { MemberVariables.Count: 0 }
                        or CrashableTypeInfo { MemberVariables.Count: 0 };
                    if (!isEmptyRec)
                        EmitExpression(sb: sb, expr: variantRet.Value);
                }

                string tryCarrier = GetMaybeCarrierLLVMType(valueType: innerType);
                EmitUsingCleanup(sb: sb);
                EmitRCRecordCleanup(sb: sb);
                if (_traceCurrentRoutine)
                    EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");
                EmitLine(sb: sb, line: $"  ret {tryCarrier} zeroinitializer");
                break;
            }

            case (ErrorHandlingVariantKind.Try, VariantSiteKind.FromAbsent):
            {
                string tryCarrier = GetMaybeCarrierLLVMType(valueType: innerType);
                EmitUsingCleanup(sb: sb);
                EmitRCRecordCleanup(sb: sb);
                if (_traceCurrentRoutine)
                    EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");
                EmitLine(sb: sb, line: $"  ret {tryCarrier} zeroinitializer");
                break;
            }

            case (ErrorHandlingVariantKind.Try, VariantSiteKind.FromReturn):
            {
                // "return ErrorType(...)" in a failable routine is an error throw in RF.
                // The ErrorHandlingVariantPass creates FromReturn for ALL return statements;
                // detect the crashable case here and treat it as absent (zeroinitializer).
                TypeInfo? tryRetType = variantRet.Value != null
                    ? GetExpressionType(expr: variantRet.Value) : null;
                bool tryRetIsCrashable = tryRetType is CrashableTypeInfo;

                string? returnedVarName = variantRet.Value is IdentifierExpression tryId &&
                                          _localEntityVars.Any(predicate: e => e.Name == tryId.Name)
                    ? tryId.Name
                    : null;
                EmitUsingCleanup(sb: sb);
                EmitRCRecordCleanup(sb: sb);
                EmitEntityCleanup(sb: sb, returnedVarName: returnedVarName);
                if (_traceCurrentRoutine)
                    EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");

                if (tryRetIsCrashable)
                {
                    // Return of a crashable from a Try variant → absent in Maybe (error is dropped).
                    // Evaluate for side effects (allocates and immediately discards the crashable).
                    EmitExpression(sb: sb, expr: variantRet.Value!);
                    string tryCarrier = GetMaybeCarrierLLVMType(valueType: innerType);
                    EmitLine(sb: sb, line: $"  ret {tryCarrier} zeroinitializer");
                }
                else if (variantRet.Value == null)
                {
                    // Bare return in Try variant (Blank inner type): present with no payload.
                    // Record Maybe { i1, Blank }: set i1 tag to 1 (present), zeroinitializer for Blank payload.
                    string tryCarrier = GetMaybeCarrierLLVMType(valueType: innerType);
                    string tv0 = NextTemp();
                    EmitLine(sb: sb, line: $"  {tv0} = insertvalue {tryCarrier} zeroinitializer, i1 1, 0");
                    EmitLine(sb: sb, line: $"  ret {tryCarrier} {tv0}");
                }
                else
                {
                    // Present value: wrap in Maybe carrier.
                    // Record Maybe { i1 present, T value }: insertvalue i1 1 at 0, T at 1.
                    // Entity Maybe { Snatched[T] }:         single ptr field — non-null ptr = present.
                    string value = EmitExpression(sb: sb, expr: variantRet.Value);
                    string tryCarrier = GetMaybeCarrierLLVMType(valueType: innerType);
                    if (innerType is EntityTypeInfo)
                    {
                        string v0 = NextTemp();
                        EmitLine(sb: sb, line: $"  {v0} = insertvalue {tryCarrier} zeroinitializer, ptr {value}, 0");
                        EmitLine(sb: sb, line: $"  ret {tryCarrier} {v0}");
                    }
                    else
                    {
                        string innerLlvm = GetLLVMType(type: innerType);
                        string v0 = NextTemp();
                        EmitLine(sb: sb, line: $"  {v0} = insertvalue {tryCarrier} zeroinitializer, i1 1, 0");
                        string v1 = NextTemp();
                        EmitLine(sb: sb, line: $"  {v1} = insertvalue {tryCarrier} {v0}, {innerLlvm} {value}, 1");
                        EmitLine(sb: sb, line: $"  ret {tryCarrier} {v1}");
                    }
                }

                break;
            }

            // ── TryBool variant ────────────────────────────────────────────────────────────
            case (ErrorHandlingVariantKind.TryBool, VariantSiteKind.FromThrow):
            {
                // Evaluate error for side effects
                if (variantRet.Value != null)
                {
                    TypeInfo? errType = GetExpressionType(expr: variantRet.Value);
                    bool isEmptyRec = errType is RecordTypeInfo { MemberVariables.Count: 0 }
                        or CrashableTypeInfo { MemberVariables.Count: 0 };
                    if (!isEmptyRec)
                        EmitExpression(sb: sb, expr: variantRet.Value);
                }

                EmitUsingCleanup(sb: sb);
                EmitRCRecordCleanup(sb: sb);
                if (_traceCurrentRoutine)
                    EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");
                EmitLine(sb: sb, line: "  ret i1 false");
                break;
            }

            case (ErrorHandlingVariantKind.TryBool, VariantSiteKind.FromAbsent):
            {
                EmitUsingCleanup(sb: sb);
                EmitRCRecordCleanup(sb: sb);
                if (_traceCurrentRoutine)
                    EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");
                EmitLine(sb: sb, line: "  ret i1 false");
                break;
            }

            case (ErrorHandlingVariantKind.TryBool, VariantSiteKind.FromReturn):
            {
                EmitUsingCleanup(sb: sb);
                EmitRCRecordCleanup(sb: sb);
                EmitEntityCleanup(sb: sb, returnedVarName: null);
                if (_traceCurrentRoutine)
                    EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");
                EmitLine(sb: sb, line: "  ret i1 true");
                break;
            }

            // ── Lookup variant ─────────────────────────────────────────────────────────────
            case (ErrorHandlingVariantKind.Lookup, VariantSiteKind.FromThrow):
            {
                TypeInfo? errType = variantRet.Value != null
                    ? GetExpressionType(expr: variantRet.Value)
                    : null;
                string errTypeName = errType?.Name ?? "UnknownError";
                // Treat unknown type (errType == null) as empty — cannot construct safely.
                bool isEmptyRec = errType == null
                    || errType is RecordTypeInfo { MemberVariables.Count: 0 }
                    or CrashableTypeInfo { MemberVariables.Count: 0 };
                string errorVal = isEmptyRec || variantRet.Value == null
                    ? "zeroinitializer"
                    : EmitExpression(sb: sb, expr: variantRet.Value!);

                string lookupCarrier = GetLookupCarrierLLVMType(valueType: innerType);
                ulong errTypeId = errType != null
                    ? ComputeTypeId(fullName: errType.FullName)
                    : 0;
                string errDataAddr = EmitErrorDataAddress(sb: sb, errorType: errType ?? innerType,
                    errorVal: errorVal, isEmptyRecord: isEmptyRec);

                EmitUsingCleanup(sb: sb);
                EmitRCRecordCleanup(sb: sb);
                if (_traceCurrentRoutine)
                    EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");

                string lv0 = NextTemp();
                EmitLine(sb: sb, line: $"  {lv0} = insertvalue {lookupCarrier} zeroinitializer, i64 {errTypeId}, 0");
                string lv1 = NextTemp();
                EmitLine(sb: sb, line: $"  {lv1} = insertvalue {lookupCarrier} {lv0}, i64 {errDataAddr}, 1");
                EmitLine(sb: sb, line: $"  ret {lookupCarrier} {lv1}");
                break;
            }

            case (ErrorHandlingVariantKind.Lookup, VariantSiteKind.FromAbsent):
            {
                string lookupCarrier = GetLookupCarrierLLVMType(valueType: innerType);
                EmitUsingCleanup(sb: sb);
                EmitRCRecordCleanup(sb: sb);
                if (_traceCurrentRoutine)
                    EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");
                EmitLine(sb: sb, line: $"  ret {lookupCarrier} zeroinitializer");
                break;
            }

            case (ErrorHandlingVariantKind.Lookup, VariantSiteKind.FromReturn):
            {
                TypeInfo? lookupRetType = variantRet.Value != null
                    ? GetExpressionType(expr: variantRet.Value) : null;
                bool lookupRetIsCrashable = lookupRetType is CrashableTypeInfo;

                string? returnedVarName = variantRet.Value is IdentifierExpression lookupId &&
                                          _localEntityVars.Any(predicate: e => e.Name == lookupId.Name)
                    ? lookupId.Name
                    : null;
                EmitUsingCleanup(sb: sb);
                EmitRCRecordCleanup(sb: sb);
                EmitEntityCleanup(sb: sb, returnedVarName: returnedVarName);
                if (_traceCurrentRoutine)
                    EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");

                string lookupCarrier = GetLookupCarrierLLVMType(valueType: innerType);

                if (lookupRetIsCrashable)
                {
                    // "return ErrorType(...)" → store error in carrier field 1.
                    string errorVal = EmitExpression(sb: sb, expr: variantRet.Value!);
                    ulong errTypeId = ComputeTypeId(fullName: lookupRetType!.FullName);
                    string errDataAddr = EmitErrorDataAddress(sb: sb, errorType: lookupRetType,
                        errorVal: errorVal, isEmptyRecord: false);
                    string lev0 = NextTemp();
                    EmitLine(sb: sb,
                        line: $"  {lev0} = insertvalue {lookupCarrier} zeroinitializer, i64 {errTypeId}, 0");
                    string lev1 = NextTemp();
                    EmitLine(sb: sb, line: $"  {lev1} = insertvalue {lookupCarrier} {lev0}, i64 {errDataAddr}, 1");
                    EmitLine(sb: sb, line: $"  ret {lookupCarrier} {lev1}");
                }
                else if (variantRet.Value == null)
                {
                    EmitLine(sb: sb, line: $"  ret {lookupCarrier} zeroinitializer");
                }
                else
                {
                    string value = EmitExpression(sb: sb, expr: variantRet.Value);
                    ulong validId = ComputeTypeId(fullName: innerType.FullName);
                    string lrv0 = NextTemp();
                    EmitLine(sb: sb,
                        line: $"  {lrv0} = insertvalue {lookupCarrier} zeroinitializer, i64 {validId}, 0");
                    string lrv1 = NextTemp();
                    string lDataVal = EmitSuccessDataAddress(sb: sb, innerType: innerType, value: value);
                    EmitLine(sb: sb, line: $"  {lrv1} = insertvalue {lookupCarrier} {lrv0}, i64 {lDataVal}, 1");
                    EmitLine(sb: sb, line: $"  ret {lookupCarrier} {lrv1}");
                }

                break;
            }

            // ── Check variant ──────────────────────────────────────────────────────────────
            case (ErrorHandlingVariantKind.Check, VariantSiteKind.FromThrow):
            {
                TypeInfo? errType = variantRet.Value != null
                    ? GetExpressionType(expr: variantRet.Value)
                    : null;
                // Treat unknown type (errType == null) as empty — cannot construct safely.
                bool isEmptyRec = errType == null
                    || errType is RecordTypeInfo { MemberVariables.Count: 0 }
                    or CrashableTypeInfo { MemberVariables.Count: 0 };
                string errorVal = isEmptyRec || variantRet.Value == null
                    ? "zeroinitializer"
                    : EmitExpression(sb: sb, expr: variantRet.Value!);

                string checkCarrier = GetResultCarrierLLVMType(valueType: innerType);
                ulong errTypeId = errType != null
                    ? ComputeTypeId(fullName: errType.FullName)
                    : 0;
                string errDataAddr = EmitErrorDataAddress(sb: sb, errorType: errType ?? innerType,
                    errorVal: errorVal, isEmptyRecord: isEmptyRec);

                EmitUsingCleanup(sb: sb);
                EmitRCRecordCleanup(sb: sb);
                if (_traceCurrentRoutine)
                    EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");

                string cv0 = NextTemp();
                EmitLine(sb: sb, line: $"  {cv0} = insertvalue {checkCarrier} zeroinitializer, i64 {errTypeId}, 0");
                string cv1 = NextTemp();
                EmitLine(sb: sb, line: $"  {cv1} = insertvalue {checkCarrier} {cv0}, i64 {errDataAddr}, 1");
                EmitLine(sb: sb, line: $"  ret {checkCarrier} {cv1}");
                break;
            }

            case (ErrorHandlingVariantKind.Check, VariantSiteKind.FromAbsent):
            {
                string checkCarrier = GetResultCarrierLLVMType(valueType: innerType);
                EmitUsingCleanup(sb: sb);
                EmitRCRecordCleanup(sb: sb);
                if (_traceCurrentRoutine)
                    EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");
                EmitLine(sb: sb, line: $"  ret {checkCarrier} zeroinitializer");
                break;
            }

            case (ErrorHandlingVariantKind.Check, VariantSiteKind.FromReturn):
            {
                TypeInfo? checkRetType = variantRet.Value != null
                    ? GetExpressionType(expr: variantRet.Value) : null;
                bool checkRetIsCrashable = checkRetType is CrashableTypeInfo;

                string? returnedVarName = variantRet.Value is IdentifierExpression checkId &&
                                          _localEntityVars.Any(predicate: e => e.Name == checkId.Name)
                    ? checkId.Name
                    : null;
                EmitUsingCleanup(sb: sb);
                EmitRCRecordCleanup(sb: sb);
                EmitEntityCleanup(sb: sb, returnedVarName: returnedVarName);
                if (_traceCurrentRoutine)
                    EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");

                string checkCarrier = GetResultCarrierLLVMType(valueType: innerType);

                if (checkRetIsCrashable)
                {
                    // "return ErrorType(...)" → store error in carrier field 1.
                    string errorVal = EmitExpression(sb: sb, expr: variantRet.Value!);
                    ulong errTypeId = ComputeTypeId(fullName: checkRetType!.FullName);
                    string errDataAddr = EmitErrorDataAddress(sb: sb, errorType: checkRetType,
                        errorVal: errorVal, isEmptyRecord: false);
                    string cev0 = NextTemp();
                    EmitLine(sb: sb,
                        line: $"  {cev0} = insertvalue {checkCarrier} zeroinitializer, i64 {errTypeId}, 0");
                    string cev1 = NextTemp();
                    EmitLine(sb: sb, line: $"  {cev1} = insertvalue {checkCarrier} {cev0}, i64 {errDataAddr}, 1");
                    EmitLine(sb: sb, line: $"  ret {checkCarrier} {cev1}");
                }
                else if (variantRet.Value == null)
                {
                    EmitLine(sb: sb, line: $"  ret {checkCarrier} zeroinitializer");
                }
                else
                {
                    string value = EmitExpression(sb: sb, expr: variantRet.Value);
                    ulong validId = ComputeTypeId(fullName: innerType.FullName);
                    string crv0 = NextTemp();
                    EmitLine(sb: sb,
                        line: $"  {crv0} = insertvalue {checkCarrier} zeroinitializer, i64 {validId}, 0");
                    string crv1 = NextTemp();
                    string cDataVal = EmitSuccessDataAddress(sb: sb, innerType: innerType, value: value);
                    EmitLine(sb: sb, line: $"  {crv1} = insertvalue {checkCarrier} {crv0}, i64 {cDataVal}, 1");
                    EmitLine(sb: sb, line: $"  ret {checkCarrier} {crv1}");
                }

                break;
            }

            default:
                throw new InvalidOperationException(
                    message: $"Unhandled VariantReturnStatement: {variantRet.VariantKind}/{variantRet.SiteKind}");
        }
    }

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

        // Check if error type is an empty record (no member variables) — skip construction.
        // Crashable types are entity-like and always go through EmitExpression.
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
            // TODO: This should not be hardcoded
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
    /// Converts a success value of inner type T to the i64 representation stored in a Result/Lookup
    /// carrier's field 1. Handles entity (ptrtoint), primitive ≤ i64 (zext), and struct types
    /// (heap-allocate via rf_allocate_dynamic and return address as i64).
    /// </summary>
    private string EmitSuccessDataAddress(StringBuilder sb, TypeInfo innerType, string value)
    {
        if (innerType is EntityTypeInfo)
        {
            string asInt = NextTemp();
            EmitLine(sb: sb, line: $"  {asInt} = ptrtoint ptr {value} to i64");
            return asInt;
        }

        string innerLlvm = GetLLVMType(type: innerType);
        if (innerLlvm == "i64") return value;

        // Types that need heap allocation: structs (named %Type or inline { fields }), i128, fp128.
        // Any type that cannot be zero-extended to i64 must be heap-allocated.
        bool needsHeapAlloc = innerLlvm.StartsWith(value: "%")
                              || innerLlvm.StartsWith(value: "{")
                              || innerLlvm is "i128" or "fp128";
        if (needsHeapAlloc)
        {
            string sizePtr = NextTemp();
            string sizeVal = NextTemp();
            EmitLine(sb: sb, line: $"  {sizePtr} = getelementptr {innerLlvm}, ptr null, i32 1");
            EmitLine(sb: sb, line: $"  {sizeVal} = ptrtoint ptr {sizePtr} to i64");
            string heapPtr = NextTemp();
            EmitLine(sb: sb, line: $"  {heapPtr} = call ptr @rf_allocate_dynamic(i64 {sizeVal})");
            EmitLine(sb: sb, line: $"  store {innerLlvm} {value}, ptr {heapPtr}");
            string asInt = NextTemp();
            EmitLine(sb: sb, line: $"  {asInt} = ptrtoint ptr {heapPtr} to i64");
            return asInt;
        }

        // Float or small integer type: bitcast float to i64 or zero-extend integer to i64.
        if (innerLlvm is "double" or "float" or "half")
        {
            // Use same-width bitcast + zext/trunc to fit in i64
            int bits = innerLlvm switch { "double" => 64, "float" => 32, _ => 16 };
            string intType = $"i{bits}";
            string bc = NextTemp();
            EmitLine(sb: sb, line: $"  {bc} = bitcast {innerLlvm} {value} to {intType}");
            if (bits == 64) return bc;
            string ze = NextTemp();
            EmitLine(sb: sb, line: $"  {ze} = zext {intType} {bc} to i64");
            return ze;
        }

        // Small integer type (e.g., i1, i8, i16, i32): zero-extend to i64.
        string zext = NextTemp();
        EmitLine(sb: sb, line: $"  {zext} = zext {innerLlvm} {value} to i64");
        return zext;
    }

    /// <summary>
    /// Emits code to store a thrown error, returning its address as i64 for the carrier field.
    /// For empty records (no fields), returns "0" — crash_message ignores `me`, passing null is safe.
    /// For entity/crashable types, they are already heap-allocated — just convert the ptr to i64.
    /// For non-empty records, the data is heap-allocated via rf_allocate_dynamic so the
    /// address remains valid after the throwing function returns.
    /// </summary>
    private string EmitErrorDataAddress(StringBuilder sb, TypeInfo errorType, string errorVal,
        bool isEmptyRecord)
    {
        if (isEmptyRecord)
        {
            return "0";
        }

        // Entity and crashable types are already heap-allocated (their value IS a ptr).
        // Return the pointer itself as i64 — no extra allocation needed.
        // The protocol dispatch stub passes this ptr directly to crash_message(ptr %self).
        if (errorType is EntityTypeInfo or CrashableTypeInfo)
        {
            string addrInt = NextTemp();
            EmitLine(sb: sb, line: $"  {addrInt} = ptrtoint ptr {errorVal} to i64");
            return addrInt;
        }

        string llvmErrType = GetLLVMType(type: errorType);

        // Compute sizeof(RecordType) via GEP null trick: getelementptr T, null, 1 → byte past end
        string sizePtr = NextTemp();
        string sizeVal = NextTemp();
        EmitLine(sb: sb, line: $"  {sizePtr} = getelementptr {llvmErrType}, ptr null, i32 1");
        EmitLine(sb: sb, line: $"  {sizeVal} = ptrtoint ptr {sizePtr} to i64");

        // Heap-allocate storage for the error record (leaked, but errors are rare/terminal)
        string heapPtr = NextTemp();
        EmitLine(sb: sb, line: $"  {heapPtr} = call ptr @rf_allocate_dynamic(i64 {sizeVal})");
        EmitLine(sb: sb, line: $"  store {llvmErrType} {errorVal}, ptr {heapPtr}");

        // Return address as i64 for storage in the Result/Lookup carrier field 1
        string addrInt2 = NextTemp();
        EmitLine(sb: sb, line: $"  {addrInt2} = ptrtoint ptr {heapPtr} to i64");
        return addrInt2;
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

        // Return zeroinitializer — tag=0 means ABSENT for Maybe[T].
        // ReturnType is now the full Maybe[T] carrier (not the inner T), so use GetLLVMType directly.
        TypeInfo absentRetType = _currentEmittingRoutine!.ReturnType!;
        string absentCarrierType = GetLLVMType(type: absentRetType);
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
