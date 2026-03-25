using SemanticAnalysis.Symbols;

namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Enums;
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
                return EmitBlock(sb, block);

            case ExpressionStatement expr:
                EmitExpression(sb, expr.Expression);
                return false;

            case DeclarationStatement decl:
                EmitDeclarationStatement(sb, decl);
                return false;

            case AssignmentStatement assign:
                EmitAssignment(sb, assign);
                return false;

            case ReturnStatement ret:
                EmitReturn(sb, ret);
                return true; // Return terminates the block

            case IfStatement ifStmt:
                return EmitIf(sb, ifStmt);

            case WhileStatement whileStmt:
                EmitWhile(sb, whileStmt);
                return false;

            case ForStatement forStmt:
                EmitFor(sb, forStmt);
                return false;

            case BreakStatement:
                EmitBreak(sb);
                return true; // Break terminates the block

            case ContinueStatement:
                EmitContinue(sb);
                return true; // Continue terminates the block

            case PassStatement:
                // No-op, nothing to emit
                return false;

            case DangerStatement danger:
                // danger! block - just emit the body
                return EmitBlock(sb, danger.Body);

            case WhenStatement whenStmt:
                EmitWhen(sb, whenStmt);
                return false;

            case DiscardStatement discard:
                // Discard: evaluate the call expression and ignore the result
                // TODO: Maybe NOT evaluating if it is creator?
                EmitExpression(sb, discard.Expression);
                return false;

            case UsingStatement usingStmt:
                EmitUsing(sb, usingStmt);
                return false;

            case ThrowStatement throwStmt:
                EmitThrow(sb, throwStmt);
                return true; // Throw terminates the block

            case AbsentStatement:
                EmitAbsent(sb);
                return true; // Absent terminates the block

            case EmitStatement emitStmt:
                EmitEmit(sb, emitStmt);
                return false;

            case BecomesStatement becomesStmt:
                EmitBecomes(sb, becomesStmt);
                return false;

            default:
                throw new NotImplementedException($"Statement type not implemented: {stmt.GetType().Name}");
        }
    }

    /// <summary>
    /// Emits all statements in a block.
    /// Returns true if the block terminates (any statement is a terminator).
    /// </summary>
    private bool EmitBlock(StringBuilder sb, BlockStatement block)
    {
        foreach (var stmt in block.Statements)
        {
            if (EmitStatement(sb, stmt))
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
            EmitVariableDeclaration(sb, varDecl);
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
            varType = ResolveTypeExpression(varDecl.Type);
        }
        else if (varDecl.Initializer != null)
        {
            varType = GetExpressionType(varDecl.Initializer);
        }

        if (varType == null)
        {
            throw new InvalidOperationException($"Cannot determine type for variable '{varDecl.Name}'");
        }

        string llvmType = GetLLVMType(varType);

        // Generate unique LLVM name for this variable (handles shadowing/redeclaration)
        string uniqueName;
        if (_varNameCounts.TryGetValue(varDecl.Name, out int count))
        {
            _varNameCounts[varDecl.Name] = count + 1;
            uniqueName = $"{varDecl.Name}.{count + 1}";
        }
        else
        {
            _varNameCounts[varDecl.Name] = 1;
            uniqueName = varDecl.Name;
        }

        // Allocate stack space
        string varPtr = $"%{uniqueName}.addr";
        EmitLine(sb, $"  {varPtr} = alloca {llvmType}");

        // Register local variable for identifier lookup
        _localVariables[varDecl.Name] = varType;
        _localVarLLVMNames[varDecl.Name] = uniqueName;

        // Track entity variables for automatic cleanup at return points
        // Only track when initialized via constructor (actual heap allocation)
        if (varType is EntityTypeInfo && IsEntityConstructorCall(varDecl.Initializer))
        {
            _localEntityVars.Add((varDecl.Name, $"%{uniqueName}.addr"));
        }

        // Store initial value if present
        if (varDecl.Initializer != null)
        {
            string value = EmitExpression(sb, varDecl.Initializer);
            EmitLine(sb, $"  store {llvmType} {value}, ptr {varPtr}");
        }
    }

    /// <summary>
    /// Resolves a type expression to a TypeInfo.
    /// </summary>
    private TypeInfo? ResolveTypeExpression(TypeExpression typeExpr)
    {
        return _registry.LookupType(typeExpr.Name);
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
        string value = EmitExpression(sb, assign.Value);

        // Determine target type and emit store
        switch (assign.Target)
        {
            case IdentifierExpression id:
                EmitVariableAssignment(sb, id.Name, value);
                break;

            case MemberExpression member:
                EmitMemberVariableAssignment(sb, member, value);
                break;

            case IndexExpression index:
                EmitIndexAssignment(sb, index, value);
                break;

            default:
                throw new NotImplementedException($"Assignment target not implemented: {assign.Target.GetType().Name}");
        }
    }

    /// <summary>
    /// Emits a store to a local variable.
    /// </summary>
    private void EmitVariableAssignment(StringBuilder sb, string varName, string value)
    {
        if (!_localVariables.TryGetValue(varName, out var varType))
        {
            throw new InvalidOperationException($"Variable '{varName}' not found");
        }

        string llvmName = _localVarLLVMNames.TryGetValue(varName, out var unique) ? unique : varName;
        string llvmType = GetLLVMType(varType);
        EmitLine(sb, $"  store {llvmType} {value}, ptr %{llvmName}.addr");
    }

    /// <summary>
    /// Emits a store to a member variable.
    /// </summary>
    private void EmitMemberVariableAssignment(StringBuilder sb, MemberExpression member, string value)
    {
        // Evaluate the object
        string target = EmitExpression(sb, member.Object);
        TypeInfo? targetType = GetExpressionType(member.Object);

        if (targetType is EntityTypeInfo entity)
        {
            EmitEntityMemberVariableWrite(sb, target, entity, member.PropertyName, value);
        }
        // Wrapper type forwarding: Hijacked[T], Seized[T], etc. — write through to inner entity
        else if (targetType is RecordTypeInfo wrapperRecord
                 && wrapperRecord.Name.Contains('[')
                 && _wrapperTypeNames.Contains(wrapperRecord.Name[..wrapperRecord.Name.IndexOf('[')])
                 && wrapperRecord.TypeArguments is { Count: > 0 }
                 && wrapperRecord.TypeArguments[0] is EntityTypeInfo innerEntity)
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
            EmitEntityMemberVariableWrite(sb, innerPtr, innerEntity, member.PropertyName, value);
        }
        else
        {
            throw new InvalidOperationException($"Cannot assign to member variable on type: {targetType?.Name}");
        }
    }

    /// <summary>
    /// Emits a store to an indexed location.
    /// </summary>
    private void EmitIndexAssignment(StringBuilder sb, IndexExpression index, string value)
    {
        TypeInfo? targetType = GetExpressionType(index.Object);

        // Dispatch to __setitem__ if the type has one
        RoutineInfo? setItem = LookupSetItemMethod(index);
        if (setItem != null && targetType != null &&
            (!setItem.IsGenericDefinition || targetType.IsGenericResolution))
        {
            string receiver = EmitExpression(sb, index.Object);
            string indexValue = EmitExpression(sb, index.Index);
            TypeInfo? indexType = GetExpressionType(index.Index);

            // Build mangled name with proper monomorphization
            string mangledName;
            if (setItem.IsGenericDefinition && targetType.IsGenericResolution)
            {
                // Infer method-level type args from the explicit arguments (index, value)
                var concreteArgTypes = new List<TypeInfo>();
                if (indexType != null) concreteArgTypes.Add(indexType);
                var methodTypeArgs = InferMethodTypeArgs(setItem, concreteArgTypes);

                if (methodTypeArgs != null)
                {
                    var resolvedParamNames = new List<string> { targetType.Name };
                    if (indexType != null) resolvedParamNames.Add(indexType.Name);
                    // Resolve value param type
                    if (setItem.Parameters.Count >= 2)
                    {
                        var valParamType = ResolveSubstitutedType(setItem.Parameters[^1].Type, methodTypeArgs);
                        resolvedParamNames.Add(valParamType.Name);
                    }
                    string ownerName = setItem.OwnerType?.FullName ?? targetType.FullName;
                    mangledName = Q($"{ownerName}.{SanitizeLLVMName(setItem.Name)}#{string.Join(",", resolvedParamNames)}");
                    RecordMonomorphization(mangledName, setItem, targetType, methodTypeArgs);
                }
                else
                {
                    mangledName = Q($"{targetType.FullName}.{SanitizeLLVMName(setItem.Name)}");
                    RecordMonomorphization(mangledName, setItem, targetType);
                }
            }
            else if (targetType.IsGenericResolution && setItem.OwnerType is { IsGenericDefinition: true })
            {
                mangledName = Q($"{targetType.FullName}.{SanitizeLLVMName(setItem.Name)}");
                RecordMonomorphization(mangledName, setItem, targetType);
            }
            else
            {
                mangledName = MangleFunctionName(setItem);
            }

            GenerateFunctionDeclaration(setItem);

            // Build argument types — resolve from the method's substituted signature
            string receiverLlvm = GetParameterLLVMType(targetType);
            string indexLlvm = indexType != null ? GetLLVMType(indexType) : "i64";
            string valueLlvm = indexType != null ? GetLLVMType(indexType) : "i64"; // placeholder

            // Use type arguments to resolve the value parameter type
            if (targetType.TypeArguments is { Count: > 0 })
                valueLlvm = GetLLVMType(targetType.TypeArguments[0]); // T in List[T]
            else if (setItem.Parameters.Count >= 2)
                valueLlvm = GetLLVMType(setItem.Parameters[^1].Type);

            string args = $"{receiverLlvm} {receiver}, {indexLlvm} {indexValue}, {valueLlvm} {value}";
            EmitLine(sb, $"  call void @{mangledName}({args})");
            return;
        }

        // Fallback: raw GEP + store for pointer/contiguous-memory types
        string target = EmitExpression(sb, index.Object);
        string idxVal = EmitExpression(sb, index.Index);

        string elemType = targetType switch
        {
            RecordTypeInfo r when r.TypeArguments?.Count > 0 => GetLLVMType(r.TypeArguments[0]),
            EntityTypeInfo e when e.TypeArguments?.Count > 0 => GetLLVMType(e.TypeArguments[0]),
            _ => throw new InvalidOperationException(
                $"Cannot determine element type for index assignment on type: {targetType?.Name}")
        };

        string elemPtr = NextTemp();
        EmitLine(sb, $"  {elemPtr} = getelementptr {elemType}, ptr {target}, i64 {idxVal}");
        EmitLine(sb, $"  store {elemType} {value}, ptr {elemPtr}");
    }

    /// <summary>
    /// Looks up the __setitem__ method for an indexed target, handling failable names and generic types.
    /// </summary>
    private RoutineInfo? LookupSetItemMethod(IndexExpression index)
    {
        TypeInfo? targetType = GetExpressionType(index.Object);
        if (targetType == null) return null;

        RoutineInfo? setItem = _registry.LookupRoutine($"{targetType.Name}.__setitem__")
            ?? _registry.LookupRoutine($"{targetType.Name}.__setitem__!");

        // For generic resolutions (e.g., List[S64]), also try the generic definition name
        if (setItem == null && targetType.IsGenericResolution)
        {
            string? genDefName = targetType switch
            {
                EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition.Name,
                RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition.Name,
                _ => null
            };
            if (genDefName != null)
            {
                setItem = _registry.LookupRoutine($"{genDefName}.__setitem__")
                    ?? _registry.LookupRoutine($"{genDefName}.__setitem__!");
            }
        }

        return setItem;
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
                EmitLine(sb, $"  {loaded} = load {_emitSlotType}, ptr {_emitSlotAddr}");

                // Allocate a Snatched handle and store the value
                int size = _currentFunctionReturnType != null
                    ? GetTypeSize(_currentFunctionReturnType)
                    : GetTypeSizeFromLlvmType(_emitSlotType);
                string handle = NextTemp();
                EmitLine(sb, $"  {handle} = call ptr @rf_allocate_dynamic(i64 {size})");
                EmitLine(sb, $"  store {_emitSlotType} {loaded}, ptr {handle}");

                // Build { i64 1, ptr handle } — DataState.VALID
                string v0 = NextTemp();
                EmitLine(sb, $"  {v0} = insertvalue {{ i64, ptr }} undef, i64 1, 0");
                string v1 = NextTemp();
                EmitLine(sb, $"  {v1} = insertvalue {{ i64, ptr }} {v0}, ptr {handle}, 1");

                EmitEntityCleanup(sb, null);
                EmitLine(sb, "  call void @rf_trace_pop()");
                EmitLine(sb, $"  ret {{ i64, ptr }} {v1}");
            }
            else
            {
                EmitEntityCleanup(sb, null);
                EmitLine(sb, "  call void @rf_trace_pop()");
                EmitLine(sb, "  ret void");
            }
        }
        else
        {
            string value = EmitExpression(sb, ret.Value);
            // Prefer current function's return type (matches the 'define' header)
            // to avoid type mismatches between header and ret instruction
            TypeInfo? retType = _currentFunctionReturnType ?? GetExpressionType(ret.Value);
            if (retType == null)
            {
                throw new InvalidOperationException("Cannot determine return type for return statement");
            }
            string llvmType = GetLLVMType(retType);

            // Skip cleanup for the returned entity variable (ownership transfers to caller)
            string? returnedVarName = ret.Value is IdentifierExpression id
                && _localEntityVars.Any(e => e.Name == id.Name) ? id.Name : null;
            EmitEntityCleanup(sb, returnedVarName);

            EmitLine(sb, "  call void @rf_trace_pop()");
            EmitLine(sb, $"  ret {llvmType} {value}");
        }
    }

    /// <summary>
    /// Returns true if the expression is an entity constructor call (heap allocation).
    /// Matches both CreatorExpression and CallExpression that resolve to an entity type.
    /// </summary>
    private bool IsEntityConstructorCall(Expression? expr)
    {
        if (expr is CreatorExpression) return true;
        if (expr is ListLiteralExpression) return true;
        if (expr is CallExpression { Callee: IdentifierExpression id })
        {
            return _registry.LookupType(id.Name) is EntityTypeInfo;
        }
        return false;
    }

    /// <summary>
    /// Emits rf_invalidate calls for all locally-owned entity variables.
    /// Skips the variable being returned (ownership transfers to caller).
    /// </summary>
    private void EmitEntityCleanup(StringBuilder sb, string? returnedVarName)
    {
        foreach (var (name, llvmAddr) in _localEntityVars)
        {
            if (name == returnedVarName) continue;
            string loaded = NextTemp();
            EmitLine(sb, $"  {loaded} = load ptr, ptr {llvmAddr}");
            string asInt = NextTemp();
            EmitLine(sb, $"  {asInt} = ptrtoint ptr {loaded} to i64");
            EmitLine(sb, $"  call void @rf_invalidate(i64 {asInt})");
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
        string value = EmitExpression(sb, emitStmt.Expression);

        TypeInfo? valueType = GetExpressionType(emitStmt.Expression) ?? _currentFunctionReturnType;
        if (valueType == null)
        {
            throw new InvalidOperationException("Cannot determine type for emit expression");
        }
        string llvmType = GetLLVMType(valueType);

        // Tuple literals produce inline aggregates, not pointers — match EmitTupleLiteral's actual type
        if (llvmType == "ptr" && emitStmt.Expression is SyntaxTree.TupleLiteralExpression tupleEmitExpr)
        {
            var elemTypes = tupleEmitExpr.Elements.Select(e =>
            {
                TypeInfo? et = GetExpressionType(e);
                return et != null ? GetLLVMType(et) : "i64";
            });
            llvmType = $"{{ {string.Join(", ", elemTypes)} }}";
        }

        // Lazily create the emit slot alloca on first emit
        if (_emitSlotAddr == null)
        {
            _emitSlotAddr = "%emit.slot.addr";
            _emitSlotType = llvmType;
            EmitLine(sb, $"  {_emitSlotAddr} = alloca {llvmType}");
        }

        // Store the emitted value
        EmitLine(sb, $"  store {llvmType} {value}, ptr {_emitSlotAddr}");
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
        TypeInfo? errorType = GetExpressionType(throwStmt.Error);
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
            errorVal = EmitExpression(sb, throwStmt.Error);
        }

        // Try to call crash_message() on the error to get the message Text
        string dataPtr = "null";
        string msgLen = "0";
        string crashMethodName = $"{typeName}.crash_message";
        var crashMethod = _registry.LookupRoutine(crashMethodName);
        if (crashMethod != null)
        {
            // Ensure crash_message is declared/defined
            GenerateFunctionDeclaration(crashMethod);
            string mangledCrash = MangleFunctionName(crashMethod);

            string llvmReceiverType = GetLLVMType(errorType!);

            // Call crash_message(me) → returns ptr to Text entity
            string textPtr = NextTemp();
            EmitLine(sb, $"  {textPtr} = call ptr @{mangledCrash}({llvmReceiverType} {errorVal})");

            // Text entity = { ptr letters_list }
            // List[Letter] entity = { ptr data, i64 count, i64 capacity }
            string lettersPtr = NextTemp();
            EmitLine(sb, $"  {lettersPtr} = load ptr, ptr {textPtr}");
            string dataField = NextTemp();
            EmitLine(sb, $"  {dataField} = getelementptr {{ptr, i64, i64}}, ptr {lettersPtr}, i32 0, i32 0");
            dataPtr = NextTemp();
            EmitLine(sb, $"  {dataPtr} = load ptr, ptr {dataField}");
            string countField = NextTemp();
            EmitLine(sb, $"  {countField} = getelementptr {{ptr, i64, i64}}, ptr {lettersPtr}, i32 0, i32 1");
            msgLen = NextTemp();
            EmitLine(sb, $"  {msgLen} = load i64, ptr {countField}");
        }

        // Emit C string constants for type name and file, cast ptr → i64 (Address)
        string typeCStr = EmitCStringConstant(typeName);
        string fileCStr = EmitCStringConstant(throwStmt.Location.FileName);
        string typeNameAsInt = NextTemp();
        EmitLine(sb, $"  {typeNameAsInt} = ptrtoint ptr {typeCStr} to i64");
        string fileAsInt = NextTemp();
        EmitLine(sb, $"  {fileAsInt} = ptrtoint ptr {fileCStr} to i64");

        // Cast message data ptr → i64 (Address)
        string msgDataAsInt;
        if (dataPtr == "null")
        {
            msgDataAsInt = "0";
        }
        else
        {
            msgDataAsInt = NextTemp();
            EmitLine(sb, $"  {msgDataAsInt} = ptrtoint ptr {dataPtr} to i64");
        }

        // Call rf_crash — never returns
        EmitLine(sb, $"  call void @rf_crash(i64 {typeNameAsInt}, i64 {typeName.Length}, i64 {fileAsInt}, i64 {throwStmt.Location.FileName.Length}, i32 {throwStmt.Location.Line}, i32 {throwStmt.Location.Column}, i64 {msgDataAsInt}, i64 {msgLen})");
        EmitLine(sb, "  unreachable");
    }

    /// <summary>
    /// Emits code for an absent statement.
    /// Returns a { i64, ptr } with tag = 0 (ABSENT) and ptr = null.
    /// Used in try_* routines that return Maybe[T] or Lookup[T].
    /// </summary>
    private void EmitAbsent(StringBuilder sb)
    {
        // Build { i64 0, ptr null } — DataState.ABSENT
        string v0 = NextTemp();
        EmitLine(sb, $"  {v0} = insertvalue {{ i64, ptr }} undef, i64 0, 0");
        string v1 = NextTemp();
        EmitLine(sb, $"  {v1} = insertvalue {{ i64, ptr }} {v0}, ptr null, 1");
        EmitLine(sb, $"  ret {{ i64, ptr }} {v1}");
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
        EmitExpression(sb, becomesStmt.Value);
    }

    #endregion

    #region Control Flow - If/Else

    /// <summary>
    /// Emits code for an if statement with optional else branch.
    /// Returns true if both branches terminate (meaning the if as a whole terminates).
    /// </summary>
    private bool EmitIf(StringBuilder sb, IfStatement ifStmt)
    {
        string condition = EmitExpression(sb, ifStmt.Condition);

        string thenLabel = NextLabel("if_then");
        string endLabel = NextLabel("if_end");

        if (ifStmt.ElseBranch != null)
        {
            string elseLabel = NextLabel("if_else");
            EmitLine(sb, $"  br i1 {condition}, label %{thenLabel}, label %{elseLabel}");

            // Then branch
            EmitLine(sb, $"{thenLabel}:");
            bool thenTerminated = EmitStatement(sb, ifStmt.ThenBranch);
            if (!thenTerminated)
            {
                EmitLine(sb, $"  br label %{endLabel}");
            }

            // Else branch
            EmitLine(sb, $"{elseLabel}:");
            bool elseTerminated = EmitStatement(sb, ifStmt.ElseBranch);
            if (!elseTerminated)
            {
                EmitLine(sb, $"  br label %{endLabel}");
            }

            // If both branches terminated, the end block is unreachable
            // but we still need to emit it for LLVM (it will be dead code eliminated)
            if (thenTerminated && elseTerminated)
            {
                // Both branches return - the if statement as a whole terminates
                // Still emit end label but mark that we terminated
                EmitLine(sb, $"{endLabel}:");
                return true;
            }

            // End block is reachable from at least one branch
            EmitLine(sb, $"{endLabel}:");
            return false;
        }
        else
        {
            EmitLine(sb, $"  br i1 {condition}, label %{thenLabel}, label %{endLabel}");

            // Then branch
            EmitLine(sb, $"{thenLabel}:");
            bool thenTerminated = EmitStatement(sb, ifStmt.ThenBranch);
            if (!thenTerminated)
            {
                EmitLine(sb, $"  br label %{endLabel}");
            }

            // End block (always reachable via the else path, even if then returns)
            EmitLine(sb, $"{endLabel}:");
            return false; // If without else never fully terminates
        }
    }

    #endregion

    #region Control Flow - While Loop

    /// <summary>
    /// Stack of loop labels for break/continue.
    /// </summary>
    private readonly Stack<(string ContinueLabel, string BreakLabel)> _loopStack = new();

    /// <summary>
    /// Emits code for a while loop.
    /// </summary>
    private void EmitWhile(StringBuilder sb, WhileStatement whileStmt)
    {
        string condLabel = NextLabel("while_cond");
        string bodyLabel = NextLabel("while_body");
        string endLabel = NextLabel("while_end");

        // Push loop labels for break/continue
        _loopStack.Push((condLabel, endLabel));

        // Jump to condition
        EmitLine(sb, $"  br label %{condLabel}");

        // Condition block
        EmitLine(sb, $"{condLabel}:");
        string condition = EmitExpression(sb, whileStmt.Condition);
        EmitLine(sb, $"  br i1 {condition}, label %{bodyLabel}, label %{endLabel}");

        // Body block
        EmitLine(sb, $"{bodyLabel}:");
        bool bodyTerminated = EmitStatement(sb, whileStmt.Body);
        if (!bodyTerminated)
        {
            EmitLine(sb, $"  br label %{condLabel}");
        }

        // End block
        EmitLine(sb, $"{endLabel}:");

        // Pop loop labels
        _loopStack.Pop();
    }

    #endregion

    #region Control Flow - For Loop

    /// <summary>
    /// Emits code for a for loop.
    /// for x in iterable { body } becomes:
    ///   iterator = iterable.__iter__()
    ///   while iterator.__has_next__() { x = iterator.__next__(); body }
    /// </summary>
    private void EmitFor(StringBuilder sb, ForStatement forStmt)
    {
        // Fast path: range-based for loop (for x in (start to end) or (start to end by step))
        if (forStmt.Sequenceable is RangeExpression range)
        {
            EmitForRange(sb, forStmt, range);
            return;
        }

        // General iterator protocol: seq.__seq__() → emitter, emitter.__next__() → Maybe[T]
        // Maybe layout: { i64 (DataState), ptr (Snatched handle) }
        // DataState: VALID=1 → has value, ABSENT=0 → done

        string condLabel = NextLabel("for_cond");
        string bodyLabel = NextLabel("for_body");
        string endLabel = NextLabel("for_end");

        _loopStack.Push((condLabel, endLabel));

        // Evaluate the sequenceable expression and get its type
        string seqValue = EmitExpression(sb, forStmt.Sequenceable);
        TypeInfo? seqType = GetExpressionType(forStmt.Sequenceable);

        // Call __seq__() to get the emitter
        string emitterValue;
        TypeInfo? emitterType = null;

        if (seqType != null)
        {
            // Look up __seq__ method — LookupMethod handles generic type fallback
            RoutineInfo? seqMethod = _registry.LookupMethod(seqType, "__seq__");

            if (seqMethod != null)
            {
                // Handle monomorphization for generic types
                string seqMangled;
                if (seqMethod.OwnerType != null &&
                    (seqMethod.OwnerType.IsGenericDefinition || seqMethod.OwnerType is ProtocolTypeInfo) &&
                    seqType.IsGenericResolution)
                {
                    seqMangled = Q($"{seqType.FullName}.__seq__");
                    RecordMonomorphization(seqMangled, seqMethod, seqType);
                }
                else
                {
                    seqMangled = MangleFunctionName(seqMethod);
                }

                // Skip for protocol-owned methods — monomorphized version will declare itself
                if (seqMethod.OwnerType is not ProtocolTypeInfo)
                    GenerateFunctionDeclaration(seqMethod);

                // Resolve return type: substitute generic params (e.g., RangeEmitter[T] → RangeEmitter[S64])
                TypeInfo? resolvedReturnType = seqMethod.ReturnType;
                if (resolvedReturnType is GenericParameterTypeInfo && seqType is { IsGenericResolution: true, TypeArguments: not null })
                {
                    TypeInfo? ownerGenericDef = seqType switch
                    {
                        RecordTypeInfo r => r.GenericDefinition,
                        EntityTypeInfo e => e.GenericDefinition,
                        _ => null
                    };
                    if (ownerGenericDef?.GenericParameters != null)
                    {
                        int paramIndex = ownerGenericDef.GenericParameters.ToList().IndexOf(resolvedReturnType.Name);
                        if (paramIndex >= 0 && paramIndex < seqType.TypeArguments.Count)
                            resolvedReturnType = seqType.TypeArguments[paramIndex];
                    }
                }
                else if (resolvedReturnType is { IsGenericResolution: true, TypeArguments: not null } &&
                         seqType is { IsGenericResolution: true, TypeArguments: not null })
                {
                    // Nested resolution: RangeEmitter[T] → RangeEmitter[S64]
                    TypeInfo? ownerGenericDef = seqType switch
                    {
                        RecordTypeInfo r => r.GenericDefinition,
                        EntityTypeInfo e => e.GenericDefinition,
                        _ => null
                    };
                    if (ownerGenericDef?.GenericParameters != null)
                    {
                        bool anyChanged = false;
                        var substitutedArgs = new List<TypeInfo>();
                        foreach (var arg in resolvedReturnType.TypeArguments)
                        {
                            if (arg is GenericParameterTypeInfo gp)
                            {
                                int paramIndex = ownerGenericDef.GenericParameters.ToList().IndexOf(gp.Name);
                                if (paramIndex >= 0 && paramIndex < seqType.TypeArguments.Count)
                                {
                                    substitutedArgs.Add(seqType.TypeArguments[paramIndex]);
                                    anyChanged = true;
                                    continue;
                                }
                            }
                            substitutedArgs.Add(arg);
                        }
                        if (anyChanged)
                        {
                            string baseName = resolvedReturnType.Name.Contains('[')
                                ? resolvedReturnType.Name[..resolvedReturnType.Name.IndexOf('[')]
                                : resolvedReturnType.Name;
                            TypeInfo? genericBase = _registry.LookupType(baseName);
                            if (genericBase != null)
                                resolvedReturnType = _registry.GetOrCreateResolution(genericBase, substitutedArgs);
                        }
                    }
                }

                emitterType = resolvedReturnType;

                // Ensure entity type struct definition is emitted for resolved generic emitter types
                if (emitterType is EntityTypeInfo emitterEntityType)
                    GenerateEntityType(emitterEntityType);

                string receiverLlvm = GetParameterLLVMType(seqType);
                string emitterReturnType = emitterType != null ? GetLLVMType(emitterType) : "ptr";

                // Emit declaration for the monomorphized name
                if (!_generatedFunctions.Contains(seqMangled))
                {
                    EmitLine(_functionDeclarations, $"declare {emitterReturnType} @{seqMangled}({receiverLlvm})");
                    _generatedFunctions.Add(seqMangled);
                }

                string emitterTemp = NextTemp();
                EmitLine(sb, $"  {emitterTemp} = call {emitterReturnType} @{seqMangled}({receiverLlvm} {seqValue})");
                emitterValue = emitterTemp;
            }
            else
            {
                // No __seq__ found, use seqValue directly as the emitter
                emitterValue = seqValue;
                emitterType = seqType;
            }
        }
        else
        {
            emitterValue = seqValue;
        }

        // Store emitter in an alloca so we can reload it each iteration
        string emitterLlvmType = emitterType != null ? GetLLVMType(emitterType) : "ptr";
        string emitterAddr = NextTemp().Replace("%", "%emitter.");
        EmitLine(sb, $"  {emitterAddr} = alloca {emitterLlvmType}");
        EmitLine(sb, $"  store {emitterLlvmType} {emitterValue}, ptr {emitterAddr}");

        // Determine element type from __next__() return type (preferred) or emitter type arguments (fallback).
        // __next__() is preferred because the yielded type may differ from the emitter's type argument
        // (e.g., EnumerateEmitter[S64].__next__() yields Tuple[U64, S64], not S64).
        TypeInfo? elemType = null;
        if (emitterType != null)
        {
            RoutineInfo? nextLookup = _registry.LookupMethod(emitterType, "__next__");
            // Skip protocol-typed return values — they need further resolution
            if (nextLookup?.ReturnType is ErrorHandlingTypeInfo { ValueType: not null } errType
                && errType.ValueType is not ProtocolTypeInfo)
                elemType = errType.ValueType;
            else if (nextLookup?.ReturnType != null && nextLookup.ReturnType is not ProtocolTypeInfo)
                elemType = nextLookup.ReturnType;
        }

        // Fallback: use emitter's first type argument (works for simple SequenceEmitter[T])
        if (elemType == null && emitterType?.TypeArguments is { Count: > 0 })
        {
            elemType = emitterType.TypeArguments[0];
        }

        // Resolve generic parameters in element type using emitter's type arguments
        if (emitterType is { IsGenericResolution: true, TypeArguments: not null })
        {
            elemType = ResolveGenericElementType(elemType, emitterType);
        }

        string elemLlvmType = elemType != null ? GetLLVMType(elemType) : "i64";

        // Allocate loop variable(s)
        string? varAddr = null;
        if (forStmt.VariablePattern != null && elemType is TupleTypeInfo tupleElemType)
        {
            // Tuple destructuring: pre-allocate variables for each binding
            var bindings = forStmt.VariablePattern.Bindings;
            for (int i = 0; i < bindings.Count && i < tupleElemType.MemberVariables.Count; i++)
            {
                var binding = bindings[i];
                string bindName = binding.BindingName ?? binding.MemberVariableName ?? $"_destruct{i}";
                if (bindName == "_") continue;

                var memberVar = tupleElemType.MemberVariables[i];
                string memberLlvmType = GetLLVMType(memberVar.Type);
                EmitLine(sb, $"  %{bindName}.addr = alloca {memberLlvmType}");
                _localVariables[bindName] = memberVar.Type;
            }
        }
        else
        {
            string varName = forStmt.Variable ?? "_iter";
            varAddr = $"%{varName}.addr";
            EmitLine(sb, $"  {varAddr} = alloca {elemLlvmType}");

            if (elemType != null)
            {
                _localVariables[varName] = elemType;
            }
        }

        EmitLine(sb, $"  br label %{condLabel}");

        // Condition block: call __next__() → Maybe[T] = { i64, ptr }
        EmitLine(sb, $"{condLabel}:");
        string emitterLoad = NextTemp();
        EmitLine(sb, $"  {emitterLoad} = load {emitterLlvmType}, ptr {emitterAddr}");

        // Call __next__() on the emitter (emitting routine, always returns { i64, ptr })
        // LookupMethod handles generic type fallback (e.g., RangeEmitter[S64] → RangeEmitter[T])
        RoutineInfo? nextMethod = emitterType != null
            ? _registry.LookupMethod(emitterType, "__next__")
            : null;

        string maybeResult;

        if (nextMethod != null)
        {
            // Handle monomorphization for generic emitter types
            string nextMangled;
            if (nextMethod.OwnerType != null &&
                (nextMethod.OwnerType.IsGenericDefinition || nextMethod.OwnerType is ProtocolTypeInfo) &&
                emitterType != null && emitterType.IsGenericResolution)
            {
                nextMangled = Q($"{emitterType.FullName}.__next__");
                RecordMonomorphization(nextMangled, nextMethod, emitterType);
            }
            else
            {
                nextMangled = MangleFunctionName(nextMethod);
            }

            GenerateFunctionDeclaration(nextMethod);

            // Emitting routines always return { i64, ptr } at IR level
            string maybeRetType = "{ i64, ptr }";

            // Emit declaration for the monomorphized name
            if (!_generatedFunctions.Contains(nextMangled))
            {
                EmitLine(_functionDeclarations, $"declare {maybeRetType} @{nextMangled}({emitterLlvmType} {emitterLoad})");
                _generatedFunctions.Add(nextMangled);
            }

            maybeResult = NextTemp();
            EmitLine(sb, $"  {maybeResult} = call {maybeRetType} @{nextMangled}({emitterLlvmType} {emitterLoad})");
        }
        else
        {
            // Fallback: construct the call name from the type
            string fallbackName = emitterType != null
                ? Q($"{emitterType.FullName}.__next__")
                : "__next__";
            maybeResult = NextTemp();
            EmitLine(sb, $"  {maybeResult} = call {{ i64, ptr }} @{fallbackName}({emitterLlvmType} {emitterLoad})");
        }

        // Extract tag from Maybe result (field 0 = DataState i64)
        string maybeTagPtr = NextTemp();
        EmitLine(sb, $"  {maybeTagPtr} = alloca {{ i64, ptr }}");
        EmitLine(sb, $"  store {{ i64, ptr }} {maybeResult}, ptr {maybeTagPtr}");
        string tagFieldPtr = NextTemp();
        string tagVal = NextTemp();
        EmitLine(sb, $"  {tagFieldPtr} = getelementptr {{ i64, ptr }}, ptr {maybeTagPtr}, i32 0, i32 0");
        EmitLine(sb, $"  {tagVal} = load i64, ptr {tagFieldPtr}");

        // tag == 1 (VALID) → has value → body, else → end
        string hasValue = NextTemp();
        EmitLine(sb, $"  {hasValue} = icmp eq i64 {tagVal}, 1");
        EmitLine(sb, $"  br i1 {hasValue}, label %{bodyLabel}, label %{endLabel}");

        // Body block: extract payload (field 1 = ptr handle), store in loop var
        EmitLine(sb, $"{bodyLabel}:");
        string handlePtr = NextTemp();
        string handleVal = NextTemp();
        EmitLine(sb, $"  {handlePtr} = getelementptr {{ i64, ptr }}, ptr {maybeTagPtr}, i32 0, i32 1");
        EmitLine(sb, $"  {handleVal} = load ptr, ptr {handlePtr}");

        // Load element value from the Snatched handle pointer
        if (forStmt.VariablePattern != null && elemType is TupleTypeInfo bodyTupleType)
        {
            // Tuple destructuring: extract each field from the handle pointer
            var bindings = forStmt.VariablePattern.Bindings;
            string anonStructType = $"{{ {string.Join(", ", bodyTupleType.ElementTypes.Select(e => GetLLVMType(e)))} }}";
            for (int i = 0; i < bindings.Count && i < bodyTupleType.MemberVariables.Count; i++)
            {
                var binding = bindings[i];
                string bindName = binding.BindingName ?? binding.MemberVariableName ?? $"_destruct{i}";
                if (bindName == "_") continue;

                var memberVar = bodyTupleType.MemberVariables[i];
                string memberLlvmType = GetLLVMType(memberVar.Type);
                string memberPtr = NextTemp();
                EmitLine(sb, $"  {memberPtr} = getelementptr {anonStructType}, ptr {handleVal}, i32 0, i32 {i}");
                string memberVal = NextTemp();
                EmitLine(sb, $"  {memberVal} = load {memberLlvmType}, ptr {memberPtr}");
                EmitLine(sb, $"  store {memberLlvmType} {memberVal}, ptr %{bindName}.addr");
            }
        }
        else if (varAddr != null)
        {
            string elemVal = NextTemp();
            EmitLine(sb, $"  {elemVal} = load {elemLlvmType}, ptr {handleVal}");
            EmitLine(sb, $"  store {elemLlvmType} {elemVal}, ptr {varAddr}");
        }

        bool bodyTerminated = EmitStatement(sb, forStmt.Body);
        if (!bodyTerminated)
        {
            EmitLine(sb, $"  br label %{condLabel}");
        }

        // End block
        EmitLine(sb, $"{endLabel}:");
        _loopStack.Pop();
    }

    /// <summary>
    /// Resolves generic parameters within an element type using the emitter's concrete type arguments.
    /// Handles direct params (T → S64), tuple types (Tuple[U64, T] → Tuple[U64, S64]),
    /// and parameterized types (List[T] → List[S64]).
    /// </summary>
    private TypeInfo? ResolveGenericElementType(TypeInfo? elemType, TypeInfo emitterType)
    {
        if (elemType == null) return null;

        TypeInfo? emitterGenericDef = emitterType switch
        {
            EntityTypeInfo e => e.GenericDefinition,
            RecordTypeInfo r => r.GenericDefinition,
            _ => null
        };
        if (emitterGenericDef?.GenericParameters == null) return elemType;

        // Build param→concrete mapping from emitter (e.g., T → S64)
        var paramMap = new Dictionary<string, TypeInfo>();
        for (int i = 0; i < emitterGenericDef.GenericParameters.Count && i < emitterType.TypeArguments!.Count; i++)
            paramMap[emitterGenericDef.GenericParameters[i]] = emitterType.TypeArguments[i];

        return SubstituteTypeParams(elemType, paramMap);
    }

    /// <summary>
    /// Recursively substitutes generic type parameters in a type using the given mapping.
    /// </summary>
    private TypeInfo SubstituteTypeParams(TypeInfo type, Dictionary<string, TypeInfo> paramMap)
    {
        // Direct generic parameter (T → S64)
        if (type is GenericParameterTypeInfo && paramMap.TryGetValue(type.Name, out var sub))
            return sub;

        // Tuple with generic elements (Tuple[U64, T] → Tuple[U64, S64])
        if (type is TupleTypeInfo tuple)
        {
            bool anyChanged = false;
            var resolvedElems = new List<TypeInfo>();
            foreach (var elem in tuple.ElementTypes)
            {
                var resolved = SubstituteTypeParams(elem, paramMap);
                if (resolved != elem) anyChanged = true;
                resolvedElems.Add(resolved);
            }
            if (anyChanged)
            {
                return new TupleTypeInfo(resolvedElems);
            }
        }

        // Parameterized type with generic args (List[T] → List[S64])
        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            bool anyChanged = false;
            var resolvedArgs = new List<TypeInfo>();
            foreach (var ta in type.TypeArguments)
            {
                var resolved = SubstituteTypeParams(ta, paramMap);
                if (resolved != ta) anyChanged = true;
                resolvedArgs.Add(resolved);
            }
            if (anyChanged)
            {
                string baseName = type.Name.Contains('[') ? type.Name[..type.Name.IndexOf('[')] : type.Name;
                var genericBase = _registry.LookupType(baseName);
                if (genericBase != null)
                    return _registry.GetOrCreateResolution(genericBase, resolvedArgs);
            }
        }

        return type;
    }

    /// <summary>
    /// Emits a range-based for loop as a simple counter loop with start/end/step.
    /// </summary>
    private void EmitForRange(StringBuilder sb, ForStatement forStmt, RangeExpression range)
    {
        string condLabel = NextLabel("for_cond");
        string bodyLabel = NextLabel("for_body");
        string incrLabel = NextLabel("for_incr");
        string endLabel = NextLabel("for_end");

        _loopStack.Push((incrLabel, endLabel));

        // Infer element type from range bounds
        TypeInfo? elemType = GetExpressionType(range.Start) ?? GetExpressionType(range.End);
        string elemLlvmType = elemType != null ? GetLLVMType(elemType) : "i64";

        // Evaluate range bounds
        string start = EmitExpression(sb, range.Start);
        string end = EmitExpression(sb, range.End);
        string userStep = range.Step != null ? EmitExpression(sb, range.Step) : null;

        // Allocate loop variable (uniquify name to avoid conflicts with repeated loops)
        string varName = forStmt.Variable ?? "_iter";
        string uniqueVarName;
        if (_varNameCounts.TryGetValue(varName, out int count))
        {
            _varNameCounts[varName] = count + 1;
            uniqueVarName = $"{varName}.{count + 1}";
        }
        else
        {
            _varNameCounts[varName] = 1;
            uniqueVarName = varName;
        }
        _localVarLLVMNames[varName] = uniqueVarName;
        string varAddr = $"%{uniqueVarName}.addr";
        EmitLine(sb, $"  {varAddr} = alloca {elemLlvmType}");
        EmitLine(sb, $"  store {elemLlvmType} {start}, ptr {varAddr}");

        // Register loop variable type
        TypeInfo? loopVarType = elemType ?? _registry.LookupType("S64") ?? _registry.LookupType("@intrinsic.i64");
        if (loopVarType != null)
        {
            _localVariables[varName] = loopVarType;
        }

        bool isFloat = elemLlvmType is "half" or "float" or "double" or "fp128";

        // Compute direction at runtime: is_desc = start > end
        // For float ranges or explicitly descending (downto), keep compile-time behavior
        string? isDescReg = null;
        string step;
        if (isFloat || range.IsDescending)
        {
            step = userStep ?? "1";
        }
        else
        {
            // Runtime direction inference for integer to/til ranges
            isDescReg = NextTemp();
            EmitLine(sb, $"  {isDescReg} = icmp sgt {elemLlvmType} {start}, {end}");
            if (userStep != null)
            {
                // Negate step if descending: step = select is_desc, -userStep, userStep
                string negStep = NextTemp();
                EmitLine(sb, $"  {negStep} = sub {elemLlvmType} 0, {userStep}");
                step = NextTemp();
                EmitLine(sb, $"  {step} = select i1 {isDescReg}, {elemLlvmType} {negStep}, {elemLlvmType} {userStep}");
            }
            else
            {
                step = NextTemp();
                EmitLine(sb, $"  {step} = select i1 {isDescReg}, {elemLlvmType} -1, {elemLlvmType} 1");
            }
        }

        EmitLine(sb, $"  br label %{condLabel}");

        // Condition
        EmitLine(sb, $"{condLabel}:");
        string current = NextTemp();
        EmitLine(sb, $"  {current} = load {elemLlvmType}, ptr {varAddr}");
        string cmp;
        if (isFloat)
        {
            cmp = NextTemp();
            string fcmpOp = range.IsDescending ? "oge" : range.IsExclusive ? "olt" : "ole";
            EmitLine(sb, $"  {cmp} = fcmp {fcmpOp} {elemLlvmType} {current}, {end}");
        }
        else if (range.IsDescending)
        {
            // Explicit descending (downto): always use sge
            cmp = NextTemp();
            string icmpOp = range.IsExclusive ? "sgt" : "sge";
            EmitLine(sb, $"  {cmp} = icmp {icmpOp} {elemLlvmType} {current}, {end}");
        }
        else
        {
            // Runtime direction: emit both comparisons, select based on is_desc
            string ascCmp = NextTemp();
            string descCmp = NextTemp();
            string ascOp = range.IsExclusive ? "slt" : "sle";
            string descOp = range.IsExclusive ? "sgt" : "sge";
            EmitLine(sb, $"  {ascCmp} = icmp {ascOp} {elemLlvmType} {current}, {end}");
            EmitLine(sb, $"  {descCmp} = icmp {descOp} {elemLlvmType} {current}, {end}");
            cmp = NextTemp();
            EmitLine(sb, $"  {cmp} = select i1 {isDescReg}, i1 {descCmp}, i1 {ascCmp}");
        }
        EmitLine(sb, $"  br i1 {cmp}, label %{bodyLabel}, label %{endLabel}");

        // Body
        EmitLine(sb, $"{bodyLabel}:");
        bool bodyTerminated = EmitStatement(sb, forStmt.Body);
        if (!bodyTerminated)
        {
            EmitLine(sb, $"  br label %{incrLabel}");
        }

        // Increment: i += step (step is negative for descending)
        EmitLine(sb, $"{incrLabel}:");
        string curVal = NextTemp();
        EmitLine(sb, $"  {curVal} = load {elemLlvmType}, ptr {varAddr}");
        string nextVal = NextTemp();
        if (isFloat)
        {
            string fop = range.IsDescending ? "fsub" : "fadd";
            EmitLine(sb, $"  {nextVal} = {fop} {elemLlvmType} {curVal}, {userStep ?? "1"}");
        }
        else
        {
            // step already has correct sign from runtime select (or is positive for explicit descending sub)
            if (range.IsDescending)
                EmitLine(sb, $"  {nextVal} = sub {elemLlvmType} {curVal}, {userStep ?? "1"}");
            else
                EmitLine(sb, $"  {nextVal} = add {elemLlvmType} {curVal}, {step}");
        }
        EmitLine(sb, $"  store {elemLlvmType} {nextVal}, ptr {varAddr}");
        EmitLine(sb, $"  br label %{condLabel}");

        // End
        EmitLine(sb, $"{endLabel}:");
        _loopStack.Pop();
    }

    #endregion

    #region Control Flow - Break/Continue

    /// <summary>
    /// Emits code for a break statement.
    /// </summary>
    private void EmitBreak(StringBuilder sb)
    {
        if (_loopStack.Count == 0)
        {
            throw new InvalidOperationException("Break statement outside of loop");
        }

        var (_, breakLabel) = _loopStack.Peek();
        EmitLine(sb, $"  br label %{breakLabel}");
    }

    /// <summary>
    /// Emits code for a continue statement.
    /// </summary>
    private void EmitContinue(StringBuilder sb)
    {
        if (_loopStack.Count == 0)
        {
            throw new InvalidOperationException("Continue statement outside of loop");
        }

        var (continueLabel, _) = _loopStack.Peek();
        EmitLine(sb, $"  br label %{continueLabel}");
    }

    #endregion

    #region Control Flow - When (Pattern Matching)

    /// <summary>
    /// Emits code for a when statement (pattern matching).
    /// </summary>
    private void EmitWhen(StringBuilder sb, WhenStatement whenStmt)
    {
        // Evaluate the subject expression once
        string subject = EmitExpression(sb, whenStmt.Expression);
        TypeInfo? subjectType = GetExpressionType(whenStmt.Expression);
        string endLabel = NextLabel("when_end");

        // Generate labels for each clause
        var clauseLabels = new List<string>();
        for (int i = 0; i < whenStmt.Clauses.Count; i++)
        {
            clauseLabels.Add(NextLabel($"when_case{i}"));
        }

        // Jump to first clause
        if (clauseLabels.Count > 0)
        {
            EmitLine(sb, $"  br label %{clauseLabels[0]}");
        }
        else
        {
            EmitLine(sb, $"  br label %{endLabel}");
        }

        // Emit each clause
        for (int i = 0; i < whenStmt.Clauses.Count; i++)
        {
            var clause = whenStmt.Clauses[i];
            string currentLabel = clauseLabels[i];
            string nextLabel = i + 1 < clauseLabels.Count ? clauseLabels[i + 1] : endLabel;

            EmitLine(sb, $"{currentLabel}:");

            // Emit pattern matching code
            string bodyLabel = NextLabel($"when_body{i}");
            EmitPatternMatch(sb, subject, clause.Pattern, bodyLabel, nextLabel, subjectType);

            // Emit body
            EmitLine(sb, $"{bodyLabel}:");
            bool bodyTerminated = EmitStatement(sb, clause.Body);
            if (!bodyTerminated)
            {
                EmitLine(sb, $"  br label %{endLabel}");
            }
        }

        // End block
        EmitLine(sb, $"{endLabel}:");
    }

    /// <summary>
    /// Emits code for pattern matching.
    /// Branches to matchLabel if pattern matches, failLabel otherwise.
    /// </summary>
    private void EmitPatternMatch(StringBuilder sb, string subject, Pattern pattern, string matchLabel, string failLabel, TypeInfo? subjectType = null)
    {
        switch (pattern)
        {
            case LiteralPattern lit:
                EmitLiteralPatternMatch(sb, subject, lit, matchLabel, failLabel, subjectType);
                break;

            case WildcardPattern:
                // Always matches - unconditional branch
                EmitLine(sb, $"  br label %{matchLabel}");
                break;

            case ElsePattern elseP:
                // Always matches; optionally bind value to variable
                if (elseP.VariableName != null && subjectType != null)
                {
                    string elseType = GetLLVMType(subjectType);
                    string elseAddr = $"%{elseP.VariableName}.addr";
                    EmitLine(sb, $"  {elseAddr} = alloca {elseType}");
                    EmitLine(sb, $"  store {elseType} {subject}, ptr {elseAddr}");
                    _localVariables[elseP.VariableName] = subjectType;
                }
                EmitLine(sb, $"  br label %{matchLabel}");
                break;

            case IdentifierPattern id:
                EmitIdentifierPatternMatch(sb, subject, id, matchLabel, subjectType);
                break;

            case TypePattern typePattern:
                EmitTypePatternMatch(sb, subject, typePattern, matchLabel, failLabel, subjectType);
                break;

            case VariantPattern variant:
                EmitVariantPatternMatch(sb, subject, variant, matchLabel, failLabel, subjectType);
                break;

            case GuardPattern guardPattern:
                EmitGuardPatternMatch(sb, subject, guardPattern, matchLabel, failLabel, subjectType);
                break;

            case NonePattern:
                EmitNonePatternMatch(sb, subject, matchLabel, failLabel, subjectType);
                break;

            case CrashablePattern crashable:
                EmitCrashablePatternMatch(sb, subject, crashable, matchLabel, failLabel, subjectType);
                break;

            case ExpressionPattern exprPattern:
                // Expression pattern: evaluate condition directly
                string condition = EmitExpression(sb, exprPattern.Expression);
                EmitLine(sb, $"  br i1 {condition}, label %{matchLabel}, label %{failLabel}");
                break;

            case NegatedTypePattern negType:
                EmitNegatedTypePatternMatch(sb, subject, negType, matchLabel, failLabel, subjectType);
                break;

            case FlagsPattern flagsPattern:
                EmitFlagsPatternMatch(sb, subject, flagsPattern, matchLabel, failLabel, subjectType);
                break;

            case ComparisonPattern cmpPattern:
                EmitComparisonPatternMatch(sb, subject, cmpPattern, matchLabel, failLabel, subjectType);
                break;

            case DestructuringPattern destructPattern:
                EmitDestructuringPatternMatch(sb, subject, destructPattern, matchLabel, subjectType);
                break;

            case TypeDestructuringPattern typeDestructPattern:
                EmitTypeDestructuringPatternMatch(sb, subject, typeDestructPattern, matchLabel, failLabel, subjectType);
                break;

            default:
                throw new NotImplementedException(
                    $"Pattern type not implemented in codegen: {pattern.GetType().Name}");
        }
    }

    /// <summary>
    /// Emits code for literal pattern matching with correct type comparison.
    /// </summary>
    private void EmitLiteralPatternMatch(StringBuilder sb, string subject, LiteralPattern lit, string matchLabel, string failLabel, TypeInfo? subjectType)
    {
        string litValue = lit.Value?.ToString() ?? "0";
        string result = NextTemp();

        // Determine LLVM type and comparison from the literal's token type
        string llvmType = lit.LiteralType switch
        {
            Lexer.TokenType.S8Literal => "i8",
            Lexer.TokenType.S16Literal => "i16",
            Lexer.TokenType.S32Literal => "i32",
            Lexer.TokenType.S64Literal => "i64",
            Lexer.TokenType.S128Literal => "i128",
            Lexer.TokenType.U8Literal => "i8",
            Lexer.TokenType.U16Literal => "i16",
            Lexer.TokenType.U32Literal => "i32",
            Lexer.TokenType.U64Literal => "i64",
            Lexer.TokenType.U128Literal => "i128",
            Lexer.TokenType.F16Literal => "half",
            Lexer.TokenType.F32Literal => "float",
            Lexer.TokenType.F64Literal => "double",
            Lexer.TokenType.F128Literal => "fp128",
            Lexer.TokenType.True or Lexer.TokenType.False => "i1",
            _ => subjectType != null ? GetLLVMType(subjectType) : "i64"
        };

        bool isFloat = llvmType is "half" or "float" or "double" or "fp128";
        bool isText = lit.LiteralType == Lexer.TokenType.TextLiteral;

        if (isText)
        {
            // Text comparison via Text.__eq__(me, other) -> Bool (i1)
            RoutineInfo? textEq = _registry.LookupRoutine("Text.__eq__");
            string eqFuncName = textEq != null ? MangleFunctionName(textEq) : "Text___eq__";
            EmitLine(sb, $"  {result} = call i1 @{eqFuncName}(ptr {subject}, ptr {litValue})");
        }
        else if (isFloat)
        {
            litValue = lit.Value switch
            {
                float f => f.ToString("G9"),
                double d => d.ToString("G17"),
                _ => litValue
            };
            EmitLine(sb, $"  {result} = fcmp oeq {llvmType} {subject}, {litValue}");
        }
        else
        {
            if (lit.Value is bool b)
                litValue = b ? "true" : "false";
            EmitLine(sb, $"  {result} = icmp eq {llvmType} {subject}, {litValue}");
        }

        EmitLine(sb, $"  br i1 {result}, label %{matchLabel}, label %{failLabel}");
    }

    /// <summary>
    /// Emits code for identifier pattern: bind value to variable and always match.
    /// </summary>
    private void EmitIdentifierPatternMatch(StringBuilder sb, string subject, IdentifierPattern id, string matchLabel, TypeInfo? subjectType)
    {
        string llvmType = subjectType != null ? GetLLVMType(subjectType) : "i64";
        string varAddr = $"%{id.Name}.addr";

        EmitLine(sb, $"  {varAddr} = alloca {llvmType}");
        EmitLine(sb, $"  store {llvmType} {subject}, ptr {varAddr}");

        if (subjectType != null)
        {
            _localVariables[id.Name] = subjectType;
        }

        EmitLine(sb, $"  br label %{matchLabel}");
    }

    /// <summary>
    /// Emits code for type pattern matching.
    /// </summary>
    private void EmitTypePatternMatch(StringBuilder sb, string subject, TypePattern typePattern, string matchLabel, string failLabel, TypeInfo? subjectType)
    {
        // Resolve the target type
        TypeInfo? targetType = _registry.LookupType(typePattern.Type.Name);

        // Determine the actual target label — if we need to bind, use an extraction block
        bool needsBind = typePattern.VariableName != null && targetType != null;
        string branchTarget = needsBind ? NextLabel("type_bind") : matchLabel;

        if (subjectType is VariantTypeInfo variant && targetType != null)
        {
            // For variants, check if any member matches the target type
            VariantMemberInfo? matchedMember = null;

            // Check for None state
            if (targetType.Name == "None")
            {
                matchedMember = variant.Members.FirstOrDefault(m => m.IsNone);
            }
            else
            {
                matchedMember = variant.FindMember(targetType);
            }

            if (matchedMember != null)
            {
                string tagPtr = NextTemp();
                string tag = NextTemp();
                string variantTypeName = GetVariantTypeName(variant);
                EmitLine(sb, $"  {tagPtr} = getelementptr {variantTypeName}, ptr {subject}, i32 0, i32 0");
                EmitLine(sb, $"  {tag} = load i64, ptr {tagPtr}");
                string cmp = NextTemp();
                EmitLine(sb, $"  {cmp} = icmp eq i64 {tag}, {matchedMember.TagValue}");
                EmitLine(sb, $"  br i1 {cmp}, label %{branchTarget}, label %{failLabel}");
            }
            else
            {
                EmitLine(sb, $"  br label %{failLabel}");
            }
        }
        else
        {
            // For entities, compare vtable pointer or type tag
            if (subjectType != null && targetType != null && subjectType.Name == targetType.Name)
            {
                EmitLine(sb, $"  br label %{branchTarget}");
            }
            else if (subjectType is EntityTypeInfo && targetType is EntityTypeInfo
                     && subjectType.Name != targetType.Name)
            {
                // Known incompatible entity types — cannot match
                EmitLine(sb, $"  br label %{failLabel}");
            }
            else
            {
                // Cannot determine at compile time — fall through to match (optimistic)
                EmitLine(sb, $"  br label %{branchTarget}");
            }
        }

        // Bind to variable if specified — emit alloca+store in a dedicated block
        if (needsBind)
        {
            EmitLine(sb, $"{branchTarget}:");
            string bindType = GetLLVMType(targetType!);
            string varAddr = $"%{typePattern.VariableName}.addr";
            EmitLine(sb, $"  {varAddr} = alloca {bindType}");
            EmitLine(sb, $"  store {bindType} {subject}, ptr {varAddr}");
            _localVariables[typePattern.VariableName!] = targetType!;
            EmitLine(sb, $"  br label %{matchLabel}");
        }
    }

    /// <summary>
    /// Emits code for crashable pattern matching (error case of Result/Lookup/Maybe).
    /// </summary>
    private void EmitCrashablePatternMatch(StringBuilder sb, string subject, CrashablePattern crashable, string matchLabel, string failLabel, TypeInfo? subjectType)
    {
        if (subjectType is ErrorHandlingTypeInfo errorInfo)
        {
            // Maybe has no error case — CrashablePattern cannot match
            if (errorInfo.Kind == ErrorHandlingKind.Maybe)
            {
                EmitLine(sb, $"  br label %{failLabel}");
                return;
            }

            // Error handling layout: { i64 (DataState), ptr (Snatched handle) }
            // DataState: VALID=1, ABSENT=0, ERROR=-1
            // CrashablePattern matches the ERROR case (tag == -1)
            string tagPtr = NextTemp();
            string tag = NextTemp();
            EmitLine(sb, $"  {tagPtr} = getelementptr {{ i64, ptr }}, ptr {subject}, i32 0, i32 0");
            EmitLine(sb, $"  {tag} = load i64, ptr {tagPtr}");
            string cmp = NextTemp();
            EmitLine(sb, $"  {cmp} = icmp eq i64 {tag}, -1");

            // Bind error value to variable if specified
            if (crashable.VariableName != null)
            {
                string extractLabel = NextLabel("crash_extract");
                EmitLine(sb, $"  br i1 {cmp}, label %{extractLabel}, label %{failLabel}");

                EmitLine(sb, $"{extractLabel}:");
                // Extract ptr from field 1 (Snatched handle)
                string handlePtr = NextTemp();
                string handleVal = NextTemp();
                EmitLine(sb, $"  {handlePtr} = getelementptr {{ i64, ptr }}, ptr {subject}, i32 0, i32 1");
                EmitLine(sb, $"  {handleVal} = load ptr, ptr {handlePtr}");

                string varAddr = $"%{crashable.VariableName}.addr";
                EmitLine(sb, $"  {varAddr} = alloca ptr");
                EmitLine(sb, $"  store ptr {handleVal}, ptr {varAddr}");

                TypeInfo errorType = errorInfo.ErrorType ?? errorInfo;
                _localVariables[crashable.VariableName] = errorType;
                EmitLine(sb, $"  br label %{matchLabel}");
            }
            else
            {
                EmitLine(sb, $"  br i1 {cmp}, label %{matchLabel}, label %{failLabel}");
            }
        }
        else
        {
            // Not an error handling type — cannot match crashable pattern
            EmitLine(sb, $"  br label %{failLabel}");
        }
    }

    /// <summary>
    /// Emits code for None pattern matching.
    /// For error handling types, checks DataState tag == 0 (ABSENT).
    /// For other types, falls back to pointer null check.
    /// </summary>
    private void EmitNonePatternMatch(StringBuilder sb, string subject, string matchLabel, string failLabel, TypeInfo? subjectType)
    {
        if (subjectType is ErrorHandlingTypeInfo errorInfo)
        {
            // Result has no None case
            if (errorInfo.Kind == ErrorHandlingKind.Result)
            {
                EmitLine(sb, $"  br label %{failLabel}");
                return;
            }

            // Maybe and Lookup: check DataState tag == 0 (ABSENT)
            // Error handling layout: { i64 (DataState), ptr (Snatched handle) }
            string tagPtr = NextTemp();
            string tag = NextTemp();
            EmitLine(sb, $"  {tagPtr} = getelementptr {{ i64, ptr }}, ptr {subject}, i32 0, i32 0");
            EmitLine(sb, $"  {tag} = load i64, ptr {tagPtr}");
            string cmp = NextTemp();
            EmitLine(sb, $"  {cmp} = icmp eq i64 {tag}, 0");
            EmitLine(sb, $"  br i1 {cmp}, label %{matchLabel}, label %{failLabel}");
        }
        else
        {
            // Non-error-handling type: check for null pointer
            string cmp = NextTemp();
            EmitLine(sb, $"  {cmp} = icmp eq ptr {subject}, null");
            EmitLine(sb, $"  br i1 {cmp}, label %{matchLabel}, label %{failLabel}");
        }
    }

    /// <summary>
    /// Emits code for variant pattern matching (is MemberType payload).
    /// </summary>
    private void EmitVariantPatternMatch(StringBuilder sb, string subject, VariantPattern variant, string matchLabel, string failLabel, TypeInfo? subjectType = null)
    {
        // Determine variant type and struct name for GEP
        VariantTypeInfo? variantType = subjectType as VariantTypeInfo;
        if (variantType == null && variant.VariantType != null)
        {
            variantType = _registry.LookupType(variant.VariantType) as VariantTypeInfo;
        }

        string variantStructType = variantType != null ? GetVariantTypeName(variantType) : "{ i64 }";

        // Extract tag from variant (first field)
        string tagPtr = NextTemp();
        string tag = NextTemp();
        EmitLine(sb, $"  {tagPtr} = getelementptr {variantStructType}, ptr {subject}, i32 0, i32 0");
        EmitLine(sb, $"  {tag} = load i64, ptr {tagPtr}");

        // Look up member by case name (which is the type name)
        int expectedTag = 0;
        VariantMemberInfo? matchedMember = null;
        if (variantType != null)
        {
            matchedMember = variantType.Members.FirstOrDefault(m => m.Name == variant.CaseName);
            if (matchedMember != null)
                expectedTag = matchedMember.TagValue;
        }

        string cmp = NextTemp();
        EmitLine(sb, $"  {cmp} = icmp eq i64 {tag}, {expectedTag}");

        // If bindings are present, extract payload in the match block
        if (variant.Bindings is { Count: > 0 } && matchedMember is { IsNone: false })
        {
            string extractLabel = NextLabel("variant_extract");
            EmitLine(sb, $"  br i1 {cmp}, label %{extractLabel}, label %{failLabel}");

            EmitLine(sb, $"{extractLabel}:");
            // Extract payload (second field of variant struct)
            string payloadPtr = NextTemp();
            EmitLine(sb, $"  {payloadPtr} = getelementptr {variantStructType}, ptr {subject}, i32 0, i32 1");

            // Bind the first binding to the payload
            var binding = variant.Bindings[0];
            string bindName = binding.BindingName ?? binding.MemberVariableName ?? "_payload";

            TypeInfo? payloadType = matchedMember.Type;

            if (payloadType != null)
            {
                string payloadLlvm = GetLLVMType(payloadType);
                string payloadVal = NextTemp();
                EmitLine(sb, $"  {payloadVal} = load {payloadLlvm}, ptr {payloadPtr}");

                string bindAddr = $"%{bindName}.addr";
                EmitLine(sb, $"  {bindAddr} = alloca {payloadLlvm}");
                EmitLine(sb, $"  store {payloadLlvm} {payloadVal}, ptr {bindAddr}");
                _localVariables[bindName] = payloadType;
            }

            EmitLine(sb, $"  br label %{matchLabel}");
        }
        else
        {
            EmitLine(sb, $"  br i1 {cmp}, label %{matchLabel}, label %{failLabel}");
        }
    }

    /// <summary>
    /// Emits code for guard pattern matching (pattern if condition).
    /// </summary>
    private void EmitGuardPatternMatch(StringBuilder sb, string subject, GuardPattern guardPattern, string matchLabel, string failLabel, TypeInfo? subjectType = null)
    {
        // First check inner pattern
        string guardCheck = NextLabel("guard_check");
        EmitPatternMatch(sb, subject, guardPattern.InnerPattern, guardCheck, failLabel, subjectType);

        // Then check guard condition
        EmitLine(sb, $"{guardCheck}:");
        string guardResult = EmitExpression(sb, guardPattern.Guard);
        EmitLine(sb, $"  br i1 {guardResult}, label %{matchLabel}, label %{failLabel}");
    }

    /// <summary>
    /// Emits code for negated type pattern matching (isnot Type).
    /// Inverts the logic of TypePattern — branches to matchLabel when type does NOT match.
    /// </summary>
    private void EmitNegatedTypePatternMatch(StringBuilder sb, string subject, NegatedTypePattern negType, string matchLabel, string failLabel, TypeInfo? subjectType)
    {
        // Invert: match→fail, fail→match compared to regular TypePattern
        TypeInfo? targetType = _registry.LookupType(negType.Type.Name);

        if (subjectType is VariantTypeInfo variant && targetType != null)
        {
            VariantMemberInfo? matchedMember = targetType.Name == "None"
                ? variant.Members.FirstOrDefault(m => m.IsNone)
                : variant.FindMember(targetType);

            if (matchedMember != null)
            {
                string tagPtr = NextTemp();
                string tag = NextTemp();
                string variantTypeName = GetVariantTypeName(variant);
                EmitLine(sb, $"  {tagPtr} = getelementptr {variantTypeName}, ptr {subject}, i32 0, i32 0");
                EmitLine(sb, $"  {tag} = load i64, ptr {tagPtr}");
                string cmp = NextTemp();
                EmitLine(sb, $"  {cmp} = icmp ne i64 {tag}, {matchedMember.TagValue}");
                EmitLine(sb, $"  br i1 {cmp}, label %{matchLabel}, label %{failLabel}");
            }
            else
            {
                // No matching case — always matches the negation
                EmitLine(sb, $"  br label %{matchLabel}");
            }
        }
        else if (subjectType != null && targetType != null && subjectType.Name == targetType.Name)
        {
            // Known same type — negation always fails
            EmitLine(sb, $"  br label %{failLabel}");
        }
        else if (subjectType is EntityTypeInfo && targetType is EntityTypeInfo
                 && subjectType.Name != targetType.Name)
        {
            // Known different entity types — negation always matches
            EmitLine(sb, $"  br label %{matchLabel}");
        }
        else
        {
            // Cannot determine — fall through to match (optimistic for negation)
            EmitLine(sb, $"  br label %{matchLabel}");
        }
    }

    /// <summary>
    /// Emits code for flags pattern matching in when clauses.
    /// Reuses the same bitwise logic as EmitFlagsTest.
    /// </summary>
    private void EmitFlagsPatternMatch(StringBuilder sb, string subject, FlagsPattern flagsPattern, string matchLabel, string failLabel, TypeInfo? subjectType)
    {
        FlagsTypeInfo? flagsType = subjectType as FlagsTypeInfo;

        // Build the combined test mask
        ulong testMask = 0;
        foreach (string flagName in flagsPattern.FlagNames)
        {
            testMask |= ResolveFlagBit(flagName, flagsType);
        }

        // Build excluded mask
        ulong excludedMask = 0;
        if (flagsPattern.ExcludedFlags != null)
        {
            foreach (string flagName in flagsPattern.ExcludedFlags)
            {
                excludedMask |= ResolveFlagBit(flagName, flagsType);
            }
        }

        string maskStr = testMask.ToString();
        string result;

        if (flagsPattern.IsExact)
        {
            // isonly: x == mask
            result = NextTemp();
            EmitLine(sb, $"  {result} = icmp eq i64 {subject}, {maskStr}");
        }
        else
        {
            // is: check flags based on connective
            string andResult = NextTemp();
            EmitLine(sb, $"  {andResult} = and i64 {subject}, {maskStr}");

            if (flagsPattern.Connective == FlagsTestConnective.Or)
            {
                result = NextTemp();
                EmitLine(sb, $"  {result} = icmp ne i64 {andResult}, 0");
            }
            else
            {
                result = NextTemp();
                EmitLine(sb, $"  {result} = icmp eq i64 {andResult}, {maskStr}");
            }

            // Handle 'but' exclusion
            if (excludedMask > 0)
            {
                string exclAnd = NextTemp();
                EmitLine(sb, $"  {exclAnd} = and i64 {subject}, {excludedMask}");
                string exclCmp = NextTemp();
                EmitLine(sb, $"  {exclCmp} = icmp eq i64 {exclAnd}, 0");
                string combined = NextTemp();
                EmitLine(sb, $"  {combined} = and i1 {result}, {exclCmp}");
                result = combined;
            }
        }

        EmitLine(sb, $"  br i1 {result}, label %{matchLabel}, label %{failLabel}");
    }

    /// <summary>
    /// Emits code for comparison pattern matching (== value, != value, &lt; value, etc.).
    /// </summary>
    private void EmitComparisonPatternMatch(StringBuilder sb, string subject, ComparisonPattern cmpPattern, string matchLabel, string failLabel, TypeInfo? subjectType)
    {
        string rhs = EmitExpression(sb, cmpPattern.Value);
        string llvmType = subjectType != null ? GetLLVMType(subjectType) : "i64";
        bool isFloat = llvmType is "half" or "float" or "double" or "fp128";
        bool isPtr = llvmType == "ptr";

        string result = NextTemp();

        if (cmpPattern.Operator == Lexer.TokenType.ReferenceEqual)
        {
            EmitLine(sb, $"  {result} = icmp eq ptr {subject}, {rhs}");
        }
        else if (cmpPattern.Operator == Lexer.TokenType.ReferenceNotEqual)
        {
            EmitLine(sb, $"  {result} = icmp ne ptr {subject}, {rhs}");
        }
        else if (isFloat)
        {
            string fcmpOp = cmpPattern.Operator switch
            {
                Lexer.TokenType.Equal => "oeq",
                Lexer.TokenType.NotEqual => "one",
                Lexer.TokenType.Less => "olt",
                Lexer.TokenType.LessEqual => "ole",
                Lexer.TokenType.Greater => "ogt",
                Lexer.TokenType.GreaterEqual => "oge",
                _ => "oeq"
            };
            EmitLine(sb, $"  {result} = fcmp {fcmpOp} {llvmType} {subject}, {rhs}");
        }
        else if (isPtr)
        {
            string icmpOp = cmpPattern.Operator switch
            {
                Lexer.TokenType.Equal => "eq",
                Lexer.TokenType.NotEqual => "ne",
                _ => "eq"
            };
            EmitLine(sb, $"  {result} = icmp {icmpOp} ptr {subject}, {rhs}");
        }
        else
        {
            string icmpOp = cmpPattern.Operator switch
            {
                Lexer.TokenType.Equal => "eq",
                Lexer.TokenType.NotEqual => "ne",
                Lexer.TokenType.Less => "slt",
                Lexer.TokenType.LessEqual => "sle",
                Lexer.TokenType.Greater => "sgt",
                Lexer.TokenType.GreaterEqual => "sge",
                _ => "eq"
            };
            EmitLine(sb, $"  {result} = icmp {icmpOp} {llvmType} {subject}, {rhs}");
        }

        EmitLine(sb, $"  br i1 {result}, label %{matchLabel}, label %{failLabel}");
    }

    /// <summary>
    /// Emits code for destructuring pattern: extract fields and bind to variables.
    /// Always matches (destructuring is structural, not conditional).
    /// </summary>
    private void EmitDestructuringPatternMatch(StringBuilder sb, string subject, DestructuringPattern destructPattern, string matchLabel, TypeInfo? subjectType)
    {
        EmitDestructuringBindings(sb, subject, destructPattern.Bindings, subjectType);
        EmitLine(sb, $"  br label %{matchLabel}");
    }

    /// <summary>
    /// Emits code for type + destructuring pattern: type check then extract fields.
    /// </summary>
    private void EmitTypeDestructuringPatternMatch(StringBuilder sb, string subject, TypeDestructuringPattern pattern, string matchLabel, string failLabel, TypeInfo? subjectType)
    {
        TypeInfo? targetType = _registry.LookupType(pattern.Type.Name);

        // Type check first (same logic as TypePattern)
        string extractLabel = NextLabel("type_destruct");

        if (subjectType is VariantTypeInfo variant && targetType != null)
        {
            VariantMemberInfo? matchedMember = targetType.Name == "None"
                ? variant.Members.FirstOrDefault(m => m.IsNone)
                : variant.FindMember(targetType);

            if (matchedMember != null)
            {
                string tagPtr = NextTemp();
                string tag = NextTemp();
                string variantTypeName = GetVariantTypeName(variant);
                EmitLine(sb, $"  {tagPtr} = getelementptr {variantTypeName}, ptr {subject}, i32 0, i32 0");
                EmitLine(sb, $"  {tag} = load i64, ptr {tagPtr}");
                string cmp = NextTemp();
                EmitLine(sb, $"  {cmp} = icmp eq i64 {tag}, {matchedMember.TagValue}");
                EmitLine(sb, $"  br i1 {cmp}, label %{extractLabel}, label %{failLabel}");
            }
            else
            {
                EmitLine(sb, $"  br label %{failLabel}");
                EmitLine(sb, $"{extractLabel}:");
                EmitLine(sb, $"  br label %{matchLabel}");
                return;
            }
        }
        else if (subjectType != null && targetType != null && subjectType.Name == targetType.Name)
        {
            EmitLine(sb, $"  br label %{extractLabel}");
        }
        else if (subjectType is EntityTypeInfo && targetType is EntityTypeInfo
                 && subjectType.Name != targetType.Name)
        {
            EmitLine(sb, $"  br label %{failLabel}");
            EmitLine(sb, $"{extractLabel}:");
            EmitLine(sb, $"  br label %{matchLabel}");
            return;
        }
        else
        {
            EmitLine(sb, $"  br label %{extractLabel}");
        }

        // Extract and bind fields
        EmitLine(sb, $"{extractLabel}:");
        TypeInfo? bindType = targetType ?? subjectType;
        EmitDestructuringBindings(sb, subject, pattern.Bindings, bindType);
        EmitLine(sb, $"  br label %{matchLabel}");
    }

    /// <summary>
    /// Emits field extraction for destructuring bindings.
    /// Supports both positional and named bindings on records, entities, and tuples.
    /// </summary>
    private void EmitDestructuringBindings(StringBuilder sb, string subject, List<DestructuringBinding> bindings, TypeInfo? subjectType)
    {
        // Get the member variables from the subject type
        IReadOnlyList<MemberVariableInfo>? memberVariables = subjectType switch
        {
            RecordTypeInfo record => record.MemberVariables,
            EntityTypeInfo entity => entity.MemberVariables,
            TupleTypeInfo tuple => tuple.MemberVariables,
            _ => null
        };

        string structTypeName = subjectType switch
        {
            RecordTypeInfo record => record.HasDirectBackendType ? record.LlvmType
                : record.IsSingleMemberVariableWrapper ? GetLLVMType(record.UnderlyingIntrinsic!)
                : GetRecordTypeName(record),
            EntityTypeInfo entity => GetEntityTypeName(entity),
            TupleTypeInfo tuple => $"{{ {string.Join(", ", tuple.ElementTypes.Select(e => GetLLVMType(e)))} }}",
            _ => "{ }"
        };

        for (int i = 0; i < bindings.Count; i++)
        {
            var binding = bindings[i];
            string bindName = binding.BindingName ?? binding.MemberVariableName ?? $"_destruct{i}";

            if (bindName == "_") continue; // Wildcard, skip

            // Find the member variable by name or position
            int memberIdx = -1;
            MemberVariableInfo? memberVar = null;

            if (memberVariables != null)
            {
                if (binding.MemberVariableName != null)
                {
                    for (int j = 0; j < memberVariables.Count; j++)
                    {
                        if (memberVariables[j].Name == binding.MemberVariableName)
                        {
                            memberIdx = j;
                            memberVar = memberVariables[j];
                            break;
                        }
                    }
                }

                if (memberIdx < 0 && i < memberVariables.Count)
                {
                    memberIdx = i;
                    memberVar = memberVariables[i];
                }
            }

            if (memberIdx < 0 || memberVar == null) continue;

            string memberLlvmType = GetLLVMType(memberVar.Type);
            string memberPtr = NextTemp();
            EmitLine(sb, $"  {memberPtr} = getelementptr {structTypeName}, ptr {subject}, i32 0, i32 {memberIdx}");
            string memberVal = NextTemp();
            EmitLine(sb, $"  {memberVal} = load {memberLlvmType}, ptr {memberPtr}");

            string varAddr = $"%{bindName}.addr";
            EmitLine(sb, $"  {varAddr} = alloca {memberLlvmType}");
            EmitLine(sb, $"  store {memberLlvmType} {memberVal}, ptr {varAddr}");
            _localVariables[bindName] = memberVar.Type;
        }
    }

    #endregion

    #region Using Statement

    /// <summary>
    /// Emits code for a using statement.
    /// using name = resource { body }
    /// If the resource type has __enter__/__exit__, calls them around the body.
    /// Otherwise, just binds the resource to the name and emits the body (token path).
    /// </summary>
    private void EmitUsing(StringBuilder sb, UsingStatement usingStmt)
    {
        // Evaluate the resource expression
        string resourceValue = EmitExpression(sb, usingStmt.Resource);
        TypeInfo? resourceType = GetExpressionType(usingStmt.Resource);
        string llvmType = resourceType != null ? GetLLVMType(resourceType) : "ptr";

        // Check if __enter__ exists for this type
        string? enterMethodName = resourceType != null
            ? $"{resourceType.Name}.__enter__"
            : null;
        RoutineInfo? enterMethod = enterMethodName != null
            ? _registry.LookupRoutine(enterMethodName)
            : null;

        string boundValue;
        TypeInfo? boundType;

        if (enterMethod != null)
        {
            // Resource path: call __enter__(), bind result (or resource if void)
            string enterMangled = MangleFunctionName(enterMethod);
            string receiverType = GetParameterLLVMType(resourceType!);

            if (enterMethod.ReturnType != null && GetLLVMType(enterMethod.ReturnType) != "void")
            {
                string enterResult = NextTemp();
                string returnType = GetLLVMType(enterMethod.ReturnType);
                EmitLine(sb, $"  {enterResult} = call {returnType} @{enterMangled}({receiverType} {resourceValue})");
                boundValue = enterResult;
                boundType = enterMethod.ReturnType;
            }
            else
            {
                EmitLine(sb, $"  call void @{enterMangled}({receiverType} {resourceValue})");
                boundValue = resourceValue;
                boundType = resourceType;
            }
        }
        else
        {
            // Token path: just bind the resource directly
            boundValue = resourceValue;
            boundType = resourceType;
        }

        // Allocate and store the bound variable
        string bindLlvmType = boundType != null ? GetLLVMType(boundType) : llvmType;
        string varAddr = $"%{usingStmt.Name}.addr";
        EmitLine(sb, $"  {varAddr} = alloca {bindLlvmType}");
        EmitLine(sb, $"  store {bindLlvmType} {boundValue}, ptr {varAddr}");

        if (boundType != null)
        {
            _localVariables[usingStmt.Name] = boundType;
        }

        // Emit the body
        EmitStatement(sb, usingStmt.Body);

        // Call __exit__ if __enter__ was available
        if (enterMethod != null)
        {
            string exitMethodName = $"{resourceType!.Name}.__exit__";
            RoutineInfo? exitMethod = _registry.LookupRoutine(exitMethodName);
            if (exitMethod != null)
            {
                string exitMangled = MangleFunctionName(exitMethod);
                string receiverType = GetParameterLLVMType(resourceType);
                EmitLine(sb, $"  call void @{exitMangled}({receiverType} {resourceValue})");
            }
        }
    }

    #endregion
}
