using System.Text;
using Compiler.Postprocessing;
using Compiler.Synthesis;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// Statement code generation: control flow, assignments, declarations, returns.
/// </summary>
public partial class LlvmCodeGenerator
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

            case ExpressionStatement exprStmt:
                EmitExpression(sb: sb, expr: exprStmt.Expression);
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

            case LoopStatement loopStmt:
                EmitLoop(sb: sb, loopStmt: loopStmt);
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
                // TODO(C43): for creator expressions, skip evaluation entirely -> creators have no
                // observable side effects and their result is being discarded, so the allocation is wasted.
                EmitExpression(sb: sb, expr: discard.Expression);
                return false;

            case UsingStatement:
                throw new InvalidOperationException(
                    "UsingStatement reached codegen ??UsingLoweringPass must run before codegen.");

            case ThrowStatement throwStmt:
                EmitThrow(sb: sb, throwStmt: throwStmt);
                return true; // Throw terminates the block

            case AbsentStatement:
                EmitAbsent(sb: sb);
                return true; // Absent terminates the block

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
        // Global variables are declared at module level -> no local alloca needed.
        if (varDecl.Storage == StorageClass.Global) return;

        // Determine the type
        TypeInfo? varType = ResolveVariableDeclType(varDecl: varDecl);

        if (varType == null)
        {
            string typeText = "<null>";
            if (varDecl.Type != null)
            {
                typeText = varDecl.Type.Name;
                if (varDecl.Type.GenericArguments is { Count: > 0 } args)
                {
                    typeText += $"[{string.Join(", ", args.Select(a => a.Name))}]";
                }
            }

            string initializerText = varDecl.Initializer?.GetType()
                                            .Name ?? "<null>";
            throw new InvalidOperationException(
                message:
                $"Cannot determine type for variable '{varDecl.Name}' (declared type: {typeText}, initializer: {initializerText})");
        }

        string llvmType = GetLlvmType(type: varType);

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
        _localVarLlvmNames[key: varDecl.Name] = uniqueName;

        switch (varType)
        {
            // Track entity variables for automatic cleanup at return points
            // Only track when initialized via constructor (actual heap allocation)
            case EntityTypeInfo when IsEntityConstructorCall(expr: varDecl.Initializer):
                _localEntityVars.Add(item: (varDecl.Name, $"%{uniqueName}.addr"));
                // Zero-init the alloca in the entry block: if the declaration sits inside a
                // conditional that doesn't execute on the active path, the alloca still exists
                // and the function-level cleanup walks it. Without zero-init the load returns
                // uninitialized stack memory → rf_invalidate frees a garbage pointer → heap
                // corruption. rf_invalidate is null-safe so zero-init makes the cleanup a no-op.
                EmitLine(sb: _currentRoutineEntryAllocas, line: $"  store ptr null, ptr {varPtr}");
                break;
            // Track record variables with RC wrapper fields for retain/release
            case RecordTypeInfo { HasRCFields: true } rcRecord:
                _localRcRecordVars.Add(item: (varDecl.Name, $"%{uniqueName}.addr", rcRecord));
                break;
        }

        // Track variables whose type IS an RC wrapper (Retained[T], Shared[T], etc.)
        if (varType is RecordTypeInfo rcWrapRecord &&
            GetGenericBaseName(type: rcWrapRecord) is { } rcWrapBase &&
            RcWrapperBaseNames.Contains(item: rcWrapBase))
        {
            _localRetainedVars.Add(item: (varDecl.Name, $"%{uniqueName}.addr", rcWrapRecord));

            // Move semantics: if the initializer is entity.retain(), the RC wrapper
            // now manages the entity's lifetime. Remove it from scope-exit entity cleanup to prevent
            // double-free (rc.release() already frees the entity when count reaches zero).
            if (varDecl.Initializer is CallExpression
                {
                    Callee: MemberExpression
                    {
                        PropertyName: "retain",
                        Object: IdentifierExpression { Name: var srcEntityName }
                    }
                })
            {
                _localEntityVars.RemoveAll(match: e => e.Name == srcEntityName);
            }
        }

        // Store initial value if present
        if (varDecl.Initializer == null)
        {
            return;
        }

        // `var x: T = uninit` — alloca only, no store. Reading without prior write is UB.
        if (varDecl.Initializer is UninitExpression)
        {
            return;
        }

        string value = EmitExpression(sb: sb, expr: varDecl.Initializer);

        // When the declaration has an explicit type annotation, the initializer may have a
        // different LLVM type (e.g., var e: U32 = exp where exp: S128 -> trunc i128 to i32).
        // Emit an inline type cast so the store type always matches the alloca type.
        if (varDecl.Type != null)
        {
            TypeInfo? initType = GetExpressionType(expr: varDecl.Initializer);
            if (initType != null)
            {
                string initLlvm = GetLlvmType(type: initType);
                if (initLlvm != llvmType)
                    value = EmitPrimitiveCast(sb: sb,
                        value: value,
                        fromLlvm: initLlvm,
                        toLlvm: llvmType);
            }
        }

        EmitLine(sb: sb, line: $"  store {llvmType} {value}, ptr {varPtr}");

        // Retain RC fields on initial copy
        if (varType is RecordTypeInfo { HasRCFields: true } rcRecordInit)
        {
            EmitRcRecordRetain(sb: sb, llvmAddr: varPtr, recordType: rcRecordInit);
        }

        // For RC wrapper vars copied from another variable/field, bump the strong count.
        // Calls that return Retained[T] (e.g. .retain()) already set count=1 for us.
        if (varType is RecordTypeInfo rcWrapInit &&
            GetGenericBaseName(type: rcWrapInit) is { } rcWrapInitBase &&
            RcWrapperBaseNames.Contains(item: rcWrapInitBase) &&
            varDecl.Initializer is not CallExpression)
        {
            EmitRetainedVarRetain(sb: sb, llvmAddr: varPtr, recordType: rcWrapInit);
        }

        ConsumeTransferredLocalOwnership(expr: varDecl.Initializer);
    }

    /// <summary>
    /// Resolves the variable decl type from semantic compiler state.
    /// </summary>
    private TypeInfo? ResolveVariableDeclType(VariableDeclaration varDecl)
    {
        TypeInfo? varType = null;
        if (varDecl.Type != null)
            varType = ResolveTypeExpression(typeExpr: varDecl.Type);
        else if (varDecl.Initializer != null)
            varType = GetExpressionType(expr: varDecl.Initializer);

        if (varDecl.Initializer is CallExpression genericCallInit &&
            (varType == null || GetLlvmType(type: varType) == "ptr"))
        {
            TypeInfo? explicitGenericReturn =
                TryResolveExplicitGenericCallReturnType(call: genericCallInit);
            if (explicitGenericReturn != null)
                varType = explicitGenericReturn;
        }

        if (varType == null && varDecl.Initializer is CallExpression
            {
                ConstructedType: { } constructedType
            })
            varType = constructedType;

        // Fallback: constructor-style call (e.g., `var x = TypeName(...)`) -> look up by callee name.
        // Fixes "Cannot determine type" when type inference doesn't propagate the return type
        // (common for generic constructors and stdlib intrinsic-wrapped calls).
        if (varType != null || varDecl.Initializer is not CallExpression callInit)
        {
            return varType;
        }

        string? typeName = callInit.Callee switch
        {
            IdentifierExpression idc => idc.Name,
            GenericMemberExpression gmc => gmc.MemberName,
            MemberExpression mc => mc.PropertyName,
            _ => null
        };
        if (typeName != null)
        {
            varType = _registry.LookupType(name: typeName) ??
                      _registry.LookupType(name: $"Core.{typeName}") ?? _registry.GetAllTypes()
                         .FirstOrDefault(predicate: t =>
                              t.Name == typeName || t.FullName == typeName ||
                              t.FullName.EndsWith(value: "." + typeName));
        }

        return varType;
    }

    /// <summary>
    /// Resolves a type expression to a TypeInfo.
    /// </summary>
    private TypeInfo? ResolveTypeExpression(TypeExpression typeExpr)
    {
        return ResolveTypeArgument(ta: typeExpr);
    }

    /// <summary>
    /// Attempts to resolve explicit generic call return type and reports whether it succeeded.
    /// </summary>
    private TypeInfo? TryResolveExplicitGenericCallReturnType(CallExpression call)
    {
        if (call.ConstructedType is not null and not ErrorTypeInfo)
        {
            return call.ConstructedType;
        }

        RoutineInfo? routine = call.ResolvedRoutine;
        if (routine == null && call.Callee is IdentifierExpression id)
        {
            routine = _registry.LookupRoutine(fullName: id.Name) ??
                      _registry.LookupRoutineByName(name: id.Name);
        }

        if (routine == null || call.TypeArguments is not { Count: > 0 } explicitTypeArgs)
        {
            return routine?.ReturnType;
        }

        if (routine.IsGenericDefinition &&
            routine.GenericParameters is { Count: > 0 } genericParams &&
            explicitTypeArgs.Count == genericParams.Count)
        {
            var resolvedTypeArgs = explicitTypeArgs
                                  .Select(selector => ResolveTypeExpression(typeExpr: selector))
                                  .Where(predicate: t => t != null)
                                  .Cast<TypeInfo>()
                                  .ToList();
            if (resolvedTypeArgs.Count == explicitTypeArgs.Count)
            {
                routine = _registry.GetOrCreateRoutineResolution(genericDef: routine,
                    typeArguments: resolvedTypeArgs);
            }
        }

        return routine.ReturnType;
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
                ConsumeTransferredLocalOwnership(expr: assign.Value);
                break;

            case IndexExpression index:
                EmitIndexAssignment(sb: sb, index: index, rhs: assign.Value);
                break;

            default:
                throw new NotImplementedException(
                    message: $"Assignment target not implemented: {assign.Target.GetType().Name}");
        }
    }

    /// <summary>
    /// Performs the consume transferred local ownership step for this compiler phase.
    /// </summary>
    private void ConsumeTransferredLocalOwnership(Expression expr)
    {
        // `$copy` synthesis is gone — borrowed-reference values reach here as bare
        // identifiers / member accesses or wrapped in `steal`. Both are handled below.
        // Named arguments wrap their value (`value: steal new_node` → NamedArgumentExpression);
        // peek through the wrapper to reach the underlying identifier.
        Expression unwrapped = expr is NamedArgumentExpression named ? named.Value : expr;
        string? sourceName = unwrapped switch
        {
            StealExpression
            {
                Operand: IdentifierExpression { Name: var stolenName }
            } => stolenName,
            IdentifierExpression { Name: var identifierName } => identifierName,
            _ => null
        };

        if (sourceName == null)
        {
            return;
        }

        _localEntityVars.RemoveAll(match: e => e.Name == sourceName);

        if (expr is StealExpression)
        {
            _localRetainedVars.RemoveAll(match: e => e.Name == sourceName);
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
            string globalLlvmType = GetLlvmType(type: globalType);
            EmitLine(sb: sb, line: $"  store {globalLlvmType} {value}, ptr {globalLlvm}");
            return;
        }

        if (!_localVariables.TryGetValue(key: varName, value: out TypeInfo? varType))
        {
            throw new InvalidOperationException(message: $"Variable '{varName}' not found");
        }

        string llvmName = _localVarLlvmNames.TryGetValue(key: varName, value: out string? unique)
            ? unique
            : varName;
        string llvmType = GetLlvmType(type: varType);
        string varPtr = $"%{llvmName}.addr";

        // Release old value's RC fields before overwrite
        if (varType is RecordTypeInfo { HasRCFields: true } rcRecord)
        {
            EmitRcRecordRelease(sb: sb, llvmAddr: varPtr, recordType: rcRecord);
        }

        // Release old RC wrapper value before overwrite
        if (varType is RecordTypeInfo rcWrapOld &&
            GetGenericBaseName(type: rcWrapOld) is { } rcWrapOldBase &&
            RcWrapperBaseNames.Contains(item: rcWrapOldBase))
        {
            EmitRetainedVarRelease(sb: sb, llvmAddr: varPtr, recordType: rcWrapOld);
        }

        EmitLine(sb: sb, line: $"  store {llvmType} {value}, ptr {varPtr}");

        // Retain new value's RC fields
        if (varType is RecordTypeInfo { HasRCFields: true } rcRecordNew)
        {
            EmitRcRecordRetain(sb: sb, llvmAddr: varPtr, recordType: rcRecordNew);
        }

        // Retain new RC wrapper value (call already returned with count set, but copies need bump)
        if (varType is RecordTypeInfo rcWrapNew &&
            GetGenericBaseName(type: rcWrapNew) is { } rcWrapNewBase &&
            RcWrapperBaseNames.Contains(item: rcWrapNewBase))
        {
            EmitRetainedVarRetain(sb: sb, llvmAddr: varPtr, recordType: rcWrapNew);
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
        TryGetTransparentProtocolTarget(type: targetType, targetType: out TypeInfo? lookupType);
        targetType = lookupType ?? targetType;

        if (targetType is EntityTypeInfo entity)
        {
            EmitEntityMemberVariableWrite(sb: sb,
                entityPtr: target,
                entity: entity,
                memberVariableName: member.PropertyName,
                value: value,
                valueType: valueType);
        }
        // Wrapper type forwarding: Grasped[T], Claimed[T], etc. -> write through to inner entity
        else if (targetType is RecordTypeInfo wrapperRecord &&
                 GetGenericBaseName(type: wrapperRecord) is { } wrapBaseName &&
                 WrapperTypeNames.Contains(item: wrapBaseName) &&
                 wrapperRecord.TypeArguments is { Count: > 0 } &&
                 wrapperRecord.TypeArguments[index: 0] is EntityTypeInfo innerEntity)
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
                // Find the Hijacked[T] field holding the inner entity pointer.
                // e.g. Retained[T] has controller=0, data=1 -> must use data index.
                int dataFieldIndex = 0;
                for (int fi = 0; fi < wrapperRecord.MemberVariables.Count; fi++)
                {
                    if (wrapperRecord.MemberVariables[index: fi].Type is WrapperTypeInfo
                        {
                            Name: "Hijacked"
                        } hijacked && hijacked.TypeArguments is { Count: > 0 } &&
                        hijacked.TypeArguments[index: 0] is EntityTypeInfo fieldInner &&
                        fieldInner.FullName == innerEntity.FullName)
                    {
                        dataFieldIndex = fi;
                        break;
                    }
                }

                EmitLine(sb: sb,
                    line:
                    $"  {innerPtr} = extractvalue {recordTypeName} {target}, {dataFieldIndex}");
            }

            EmitEntityMemberVariableWrite(sb: sb,
                entityPtr: innerPtr,
                entity: innerEntity,
                memberVariableName: member.PropertyName,
                value: value,
                valueType: valueType);
        }
        // Plain record field write: load current value, insertvalue, store back to alloca
        else if (targetType is RecordTypeInfo plainRecord && !plainRecord.HasDirectBackendType &&
                 member.Object is IdentifierExpression recIdExpr)
        {
            string llvmName =
                _localVarLlvmNames.TryGetValue(key: recIdExpr.Name, value: out string? recUnique)
                    ? recUnique
                    : recIdExpr.Name;
            string recAllocaPtr = $"%{llvmName}.addr";

            int fieldIndex = -1;
            MemberVariableInfo? fieldInfo = null;
            for (int i = 0; i < plainRecord.MemberVariables.Count; i++)
            {
                if (plainRecord.MemberVariables[index: i].Name == member.PropertyName)
                {
                    fieldIndex = i;
                    fieldInfo = plainRecord.MemberVariables[index: i];
                    break;
                }
            }

            if (fieldIndex < 0 || fieldInfo == null)
            {
                throw new InvalidOperationException(
                    message:
                    $"Member variable '{member.PropertyName}' not found on record '{plainRecord.Name}'");
            }

            string recTypeName = GetRecordTypeName(record: plainRecord);
            string loaded = NextTemp();
            EmitLine(sb: sb, line: $"  {loaded} = load {recTypeName}, ptr {recAllocaPtr}");
            string updated = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {updated} = insertvalue {recTypeName} {loaded}, {GetLlvmType(type: fieldInfo.Type)} {value}, {fieldIndex}");
            EmitLine(sb: sb, line: $"  store {recTypeName} {updated}, ptr {recAllocaPtr}");
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
    private void EmitIndexAssignment(StringBuilder sb, IndexExpression index, Expression rhs)
    {
        // TODO: Record setitem is a hack and should be following $setitem member routine.
        // TODO: Also, the $setitem routine should be just called through anyway and handled not in here.
        TypeInfo? targetType = GetExpressionType(expr: index.Object);
        TryGetTransparentProtocolTarget(type: targetType, targetType: out TypeInfo? lookupType);
        targetType = lookupType ?? targetType;

        RoutineInfo? setItem = LookupSetItemMethod(index: index);

        // Record $setitem!: the receiver must be the alloca pointer so mutations persist in the
        // caller's frame. EmitMemberRoutineCall evaluates the receiver as a loaded value, which would
        // discard writes -> so keep the pointer-based dispatch inline for this case.
        if (setItem != null && targetType is RecordTypeInfo &&
            setItem.Name.Contains(value: "$setitem") &&
            index.Object is IdentifierExpression recId &&
            (!setItem.IsGenericDefinition || targetType.IsGenericResolution))
        {
            string value = EmitExpression(sb: sb, expr: rhs);
            string llvmName =
                _localVarLlvmNames.TryGetValue(key: recId.Name, value: out string? unique)
                    ? unique
                    : recId.Name;
            string receiver = $"%{llvmName}.addr";
            string indexValue = EmitExpression(sb: sb, expr: index.Index);
            TypeInfo? indexType = GetExpressionType(expr: index.Index);

            string mangledName = targetType.IsGenericResolution
                ? Q(name: DecorateRoutineSymbolName(
                    baseName: $"{targetType.FullName}.{SanitizeLlvmName(name: setItem.Name)}",
                    isFailable: setItem.IsFailable))
                : MangleRoutineName(routine: setItem);

            GenerateRoutineDeclaration(routine: setItem);

            string indexLlvm = indexType != null
                ? GetLlvmType(type: indexType)
                : "i64";
            string valueLlvm;
            if (targetType.TypeArguments is { Count: > 0 })
            {
                valueLlvm = GetLlvmType(type: targetType.TypeArguments[^1]);
            }
            else if (setItem.Parameters.Count >= 2)
            {
                valueLlvm = GetLlvmType(type: setItem.Parameters[^1].Type);
            }
            else
            {
                valueLlvm = "i64";
            }

            EmitLine(sb: sb,
                line:
                $"  call void @{mangledName}(ptr {receiver}, {indexLlvm} {indexValue}, {valueLlvm} {value})");
            return;
        }

        // Entity/generic dispatch: synthesize `obj.$setitem[!](index, rhs)` and delegate to
        // EmitMemberRoutineCall. This reuses the full owner-level + method-level generic monomorphization
        // machinery (e.g. BitList.$setitem![I] -> BitList.$setitem![S64]) without duplicating it.
        // OperatorLoweringPass annotates `index.ResolvedSetItem` with the method-generic-resolved
        // routine; prefer it over a fresh lookup so codegen bypasses the generic-definition guard.
        RoutineInfo? dispatchSetItem = index.ResolvedSetItem ?? setItem;
        if (dispatchSetItem != null)
        {
            string propName = dispatchSetItem.IsFailable
                ? "$setitem!"
                : "$setitem";
            var member = new MemberExpression(Object: index.Object,
                PropertyName: propName,
                Location: index.Location);
            var call = new CallExpression(Callee: member,
                Arguments: [index.Index, rhs],
                Location: index.Location) { ResolvedRoutine = dispatchSetItem };
            // Result is void -> discard
            EmitExpression(sb: sb, expr: call);
            return;
        }

        // Fallback: raw GEP + store for pointer/contiguous-memory types with no $setitem
        string rawValue = EmitExpression(sb: sb, expr: rhs);
        string target = EmitExpression(sb: sb, expr: index.Object);
        string idxVal = EmitExpression(sb: sb, expr: index.Index);

        string elemType = targetType switch
        {
            RecordTypeInfo { TypeArguments.Count: > 0 } r => GetLlvmType(
                type: r.TypeArguments[index: 0]),
            EntityTypeInfo { TypeArguments.Count: > 0 } e => GetLlvmType(
                type: e.TypeArguments[index: 0]),
            _ => throw new InvalidOperationException(
                message:
                $"Cannot determine element type for index assignment on type: {targetType?.Name}")
        };

        string elemPtr = NextTemp();
        EmitLine(sb: sb,
            line: $"  {elemPtr} = getelementptr {elemType}, ptr {target}, i64 {idxVal}");
        EmitLine(sb: sb, line: $"  store {elemType} {rawValue}, ptr {elemPtr}");
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

        TryGetTransparentProtocolTarget(type: targetType, targetType: out TypeInfo? lookupType);
        targetType = lookupType ?? targetType;

        return _registry.LookupMethod(type: targetType, methodName: "$setitem");
    }

    #endregion

    #region RC Record Cleanup

    /// <summary>RC wrapper base names that require retain/release.</summary>
    private static readonly HashSet<string> RcWrapperBaseNames =
        ["Retained", "Shared", "Tracked", "Marked"];

    /// <summary>
    /// Emits retain calls for all RC wrapper fields in a record.
    /// Called when a record with RC fields is copied into a new variable.
    /// </summary>
    private void EmitRcRecordRetain(StringBuilder sb, string llvmAddr, RecordTypeInfo recordType)
    {
        string llvmType = GetLlvmType(type: recordType);
        string loaded = NextTemp();
        EmitLine(sb: sb, line: $"  {loaded} = load {llvmType}, ptr {llvmAddr}");

        foreach (MemberVariableInfo field in recordType.MemberVariables)
        {
            if (field.Type is not WrapperTypeInfo w || !RcWrapperBaseNames.Contains(item: w.Name))
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

            GenerateRoutineDeclaration(routine: retainMethod);
            string mangled = MangleRoutineName(routine: retainMethod);
            string fieldLlvm = GetParameterLlvmType(type: w);
            EmitLine(sb: sb,
                line: $"  {NextTemp()} = call {fieldLlvm} @{mangled}({fieldLlvm} {fieldVal})");
        }
    }

    /// <summary>
    /// Emits release calls for all RC wrapper fields in a record.
    /// Called before overwriting a record variable or at scope exit.
    /// </summary>
    private void EmitRcRecordRelease(StringBuilder sb, string llvmAddr, RecordTypeInfo recordType)
    {
        string llvmType = GetLlvmType(type: recordType);
        string loaded = NextTemp();
        EmitLine(sb: sb, line: $"  {loaded} = load {llvmType}, ptr {llvmAddr}");

        foreach (MemberVariableInfo field in recordType.MemberVariables)
        {
            if (field.Type is not WrapperTypeInfo w || !RcWrapperBaseNames.Contains(item: w.Name))
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

            GenerateRoutineDeclaration(routine: releaseMethod);
            string mangled = MangleRoutineName(routine: releaseMethod);
            string fieldLlvm = GetParameterLlvmType(type: w);
            EmitLine(sb: sb, line: $"  call void @{mangled}({fieldLlvm} {fieldVal})");
        }
    }

    /// <summary>
    /// Emits release calls for all tracked RC record variables at scope exit.
    /// Called at return, throw, and absent -> before EmitEntityCleanup.
    /// </summary>
    private void EmitRcRecordCleanup(StringBuilder sb)
    {
        foreach ((string _, string llvmAddr, RecordTypeInfo recordType) in _localRcRecordVars)
        {
            EmitRcRecordRelease(sb: sb, llvmAddr: llvmAddr, recordType: recordType);
        }

        foreach ((string _, string llvmAddr, RecordTypeInfo recordType) in _localRetainedVars)
        {
            EmitRetainedVarRelease(sb: sb, llvmAddr: llvmAddr, recordType: recordType);
        }
    }

    /// <summary>
    /// Bumps the strong count for an RC wrapper variable by calling retain() on it.
    /// Used when copying an existing Retained[T] into a new variable (not from a .retain() call).
    /// </summary>
    private void EmitRetainedVarRetain(StringBuilder sb, string llvmAddr,
        RecordTypeInfo recordType)
    {
        string llvmType = GetLlvmType(type: recordType);
        string loaded = NextTemp();
        EmitLine(sb: sb, line: $"  {loaded} = load {llvmType}, ptr {llvmAddr}");

        RoutineInfo? retainMethod = _registry.LookupMethod(type: recordType, methodName: "retain");
        if (retainMethod == null)
        {
            return;
        }

        GenerateRoutineDeclaration(routine: retainMethod);
        string mangled = MangleRoutineName(routine: retainMethod);
        string rcLlvm = GetParameterLlvmType(type: recordType);
        // retain() returns Retained[T] (same struct value); discard -> heap mutation already done
        EmitLine(sb: sb, line: $"  {NextTemp()} = call {rcLlvm} @{mangled}({rcLlvm} {loaded})");
    }

    /// <summary>
    /// Decrements the strong count for an RC wrapper variable by calling release() on it.
    /// Potentially deallocates the inner data if strong count reaches zero.
    /// </summary>
    private void EmitRetainedVarRelease(StringBuilder sb, string llvmAddr,
        RecordTypeInfo recordType)
    {
        string llvmType = GetLlvmType(type: recordType);
        string loaded = NextTemp();
        EmitLine(sb: sb, line: $"  {loaded} = load {llvmType}, ptr {llvmAddr}");

        RoutineInfo? releaseMethod =
            _registry.LookupMethod(type: recordType, methodName: "release");
        if (releaseMethod == null)
        {
            return;
        }

        GenerateRoutineDeclaration(routine: releaseMethod);
        string mangled = MangleRoutineName(routine: releaseMethod);
        string rcLlvm = GetParameterLlvmType(type: recordType);
        EmitLine(sb: sb, line: $"  call void @{mangled}({rcLlvm} {loaded})");
    }

    #endregion

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Emit if as part of this compiler phase.
    /// </summary>
    private bool EmitIf(StringBuilder sb, IfStatement ifStmt)
    {
        string condition = EmitExpression(sb: sb, expr: ifStmt.Condition);

        string thenLabel = NextLabel(prefix: "if_then");
        string endLabel = NextLabel(prefix: "if_end");

        if (ifStmt.ElseBranch != null)
        {
            string elseLabel = NextLabel(prefix: "if_else");
            EmitLine(sb: sb, line: $"  br i1 {condition}, label %{thenLabel}, label %{elseLabel}");

            // Then branch
            EmitLine(sb: sb, line: $"{thenLabel}:");
            bool thenTerminated = EmitStatement(sb: sb, stmt: ifStmt.ThenBranch);
            if (!thenTerminated)
            {
                EmitLine(sb: sb, line: $"  br label %{endLabel}");
            }

            // Else branch
            EmitLine(sb: sb, line: $"{elseLabel}:");
            bool elseTerminated = EmitStatement(sb: sb, stmt: ifStmt.ElseBranch);
            if (!elseTerminated)
            {
                EmitLine(sb: sb, line: $"  br label %{endLabel}");
            }

            // If both branches terminated, the end block is unreachable
            // but we still need to emit it for LLVM (it will be dead code eliminated)
            if (thenTerminated && elseTerminated)
            {
                // Both branches return - the if statement as a whole terminates
                // Emit end label + unreachable (dead block must still have a terminator)
                EmitLine(sb: sb, line: $"{endLabel}:");
                EmitLine(sb: sb, line: "  unreachable");
                return true;
            }

            // End block is reachable from at least one branch
            EmitLine(sb: sb, line: $"{endLabel}:");
            return false;
        }
        else
        {
            EmitLine(sb: sb, line: $"  br i1 {condition}, label %{thenLabel}, label %{endLabel}");

            // Then branch
            EmitLine(sb: sb, line: $"{thenLabel}:");
            bool thenTerminated = EmitStatement(sb: sb, stmt: ifStmt.ThenBranch);
            if (!thenTerminated)
            {
                EmitLine(sb: sb, line: $"  br label %{endLabel}");
            }

            // End block (always reachable via the else path, even if then returns)
            EmitLine(sb: sb, line: $"{endLabel}:");
            return false; // If without else never fully terminates
        }
    }

    /// <summary>
    /// Stack of loop labels for break/continue.
    /// </summary>
    private readonly Stack<(string ContinueLabel, string BreakLabel)> _loopStack = new();

    /// <summary>
    /// Emits code for a loop statement (infinite loop primitive).
    /// Unconditional back-edge: continue -> loop header, break -> end.
    /// </summary>
    private void EmitLoop(StringBuilder sb, LoopStatement loopStmt)
    {
        string bodyLabel = NextLabel(prefix: "loop_body");
        string endLabel = NextLabel(prefix: "loop_end");

        // Push loop labels: continue -> body header, break -> end
        _loopStack.Push(item: (bodyLabel, endLabel));

        // Jump to body
        EmitLine(sb: sb, line: $"  br label %{bodyLabel}");

        // Body block
        EmitLine(sb: sb, line: $"{bodyLabel}:");
        bool bodyTerminated = EmitStatement(sb: sb, stmt: loopStmt.Body);
        if (!bodyTerminated)
        {
            EmitLine(sb: sb, line: $"  br label %{bodyLabel}");
        }

        // End block
        EmitLine(sb: sb, line: $"{endLabel}:");

        _loopStack.Pop();
    }


    /// <summary>
    /// Emits code for a break statement.
    /// </summary>
    private void EmitBreak(StringBuilder sb)
    {
        if (_loopStack.Count == 0)
        {
            throw new InvalidOperationException(message: "Break statement outside of loop");
        }

        (_, string breakLabel) = _loopStack.Peek();
        EmitLine(sb: sb, line: $"  br label %{breakLabel}");
    }

    /// <summary>
    /// Emits code for a continue statement.
    /// </summary>
    private void EmitContinue(StringBuilder sb)
    {
        if (_loopStack.Count == 0)
        {
            throw new InvalidOperationException(message: "Continue statement outside of loop");
        }

        (string continueLabel, _) = _loopStack.Peek();
        EmitLine(sb: sb, line: $"  br label %{continueLabel}");
    }
}
