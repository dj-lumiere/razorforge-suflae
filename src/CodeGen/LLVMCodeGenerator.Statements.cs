using System;
using System.Collections.Generic;
using System.Linq;
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
                    "UsingStatement reached codegen -> UsingLoweringPass must run before codegen.");

            case ThrowStatement throwStmt:
                EmitThrow(sb: sb, throwStmt: throwStmt);
                return true; // Throw terminates the block

            case AbsentStatement absentStmt:
                EmitAbsent(sb: sb, absentStmt: absentStmt);
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
    /// </summary>
    private void EmitVariableDeclaration(StringBuilder sb, VariableDeclaration varDecl) // NOSONAR S3776
    {
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
            // Track entity variables for automatic cleanup at return points.
            // Tracked when initialized via constructor (actual heap allocation) or as a
            // lateinit placeholder (allocated below, $create not run).
            case EntityTypeInfo when IsEntityConstructorCall(expr: varDecl.Initializer) ||
                                     (varDecl.IsLateInit && varDecl.Initializer == null):
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

            // Zero-init the alloca: if the declaration sits inside a conditional (e.g.
            // a `when`-arm binding `else r => ...`) that doesn't execute on the active
            // path, the alloca still exists and the function-level cleanup walks it.
            // Without zero-init, release() loads garbage and dereferences a junk
            // controller pointer → AV. RC wrappers are @llvm("ptr"), so null is a
            // safe sentinel and EmitRetainedVarRelease null-checks before releasing.
            EmitLine(sb: _currentRoutineEntryAllocas,
                line: $"  store {GetLlvmType(type: rcWrapRecord)} zeroinitializer, ptr {varPtr}");

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
            // `lateinit var x: T` — eager allocation, late initialization. Entities get a
            // real heap block ($create not run, no field stores) so the binding is
            // immediately valid and borrowable, and scope teardown frees a real allocation.
            // The block must come from the calloc-backed rf_allocate_dynamic (NOT the
            // _uninit variant): $destroy runs on the placeholder (scope exit, and on
            // reassignment) and walks its fields — zeroed fields are null-safe to free,
            // garbage fields are wild pointers. Value types are stored zeroed for the same
            // reason (RC-field release walks). Zeroed contents are teardown armor, not a
            // language guarantee — reading before assignment yields meaningless values.
            if (varDecl.IsLateInit)
            {
                if (varType is EntityTypeInfo lateInitEntity)
                {
                    int blockSize = lateInitEntity.HeapBlockSize(pointerSize: _pointerSizeBytes);
                    string placeholder = NextTemp();
                    EmitLine(sb: sb,
                        line: $"  {placeholder} = call ptr @rf_allocate_dynamic(i64 {blockSize})");
                    EmitLine(sb: sb, line: $"  store ptr {placeholder}, ptr {varPtr}");
                }
                else
                {
                    EmitLine(sb: sb,
                        line: $"  store {llvmType} {GetZeroValue(type: varType)}, ptr {varPtr}");
                }
            }

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
                // Primitive cast applies only between scalar @llvm-annotated records
                // (e.g. S128 -> U32). For aggregates (carriers like Maybe[T], variants,
                // multi-field records) the LLVM struct shape is identical on both sides
                // — no cast needed, and EmitPrimitiveCast would crash on the struct name.
                bool initIsScalar = initType is RecordTypeInfo { HasDirectBackendType: true };
                bool varIsScalar = varType is RecordTypeInfo { HasDirectBackendType: true };
                if (initLlvm != llvmType && initIsScalar && varIsScalar)
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

        // NOTE: no codegen strong-count bump for RC wrapper var bindings. Copying a Retained[T]/
        // Tracked[T] handle requires an explicit verb (`.retain()`/`.track()`) — implicit copy
        // (`var b = a`) is a COMPILE ERROR (ImplicitWrapperCopy; Retained/Tracked don't obey
        // Assignable). So an init is always either a fresh handle from `.retain()`/`.track()`
        // (already count=1) or a creator `Retained[T](ctrl)` (count=1) — never an implicit copy
        // needing balance. The old bump (fired on `is not CallExpression`) wrongly counted the
        // teardown return-spill `var __td_ret = Retained[T](ctrl)` (a CreatorExpression) as a copy,
        // injecting a spurious retain → strong 1→2 → double-free at scope exit. Removed.

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

        // Fall back to the call's explicit generic-return-type resolution only when the
        // inferred varType is null or unresolved-generic. The earlier "ptr-typed" heuristic
        // was too loose — for `var x = entity.retain()`, the initializer's ResolvedType is
        // the fully-substituted `Retained[Entity[S64]]`, but the underlying routine's
        // declared ReturnType is the universal-method-baked `Retained[Entity]` (with the
        // inner type-arg lost). TryResolveExplicitGenericCallReturnType reads
        // `routine.ReturnType` directly and would overwrite our correct varType with the
        // bare form. Only re-resolve when the existing varType is missing or still has
        // unresolved generic parameters.
        bool varTypeIsUnresolved = varType is null
            || varType is ErrorTypeInfo
            || varType is GenericParameterTypeInfo
            || ContainsGenericParameter(varType);
        if (varDecl.Initializer is CallExpression genericCallInit && varTypeIsUnresolved)
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

        if (routine is { IsGenericDefinition: true, GenericParameters: { Count: > 0 } genericParams } &&
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

        // NOTE: no codegen strong-count bump for RC wrapper reassignment. Same reasoning as the
        // var-binding site — implicit copy of a Retained/Tracked handle is a compile error, so the
        // RHS is always a fresh count=1 handle (explicit `.retain()`/`.track()` or a creator), never
        // an implicit copy needing balance. The old-value release above stays (reassignment-overwrite
        // is not a scope exit, so ScopeTeardownLoweringPass does not cover it).
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
        // Wrapper-of-record field write: Modifying[Record] etc. The wrapper is `@llvm("ptr")`
        // and the pointer addresses a record value in memory. GEP into the record at the
        // field index and store. (Record-inner branch must come before the entity-inner one
        // since RecordTypeInfo and EntityTypeInfo are distinct AST nodes.)
        else if (targetType is RecordTypeInfo wrapperRecOfRec &&
                 GetGenericBaseName(type: wrapperRecOfRec) is { } wrapRecBaseName &&
                 WrapperTypeNames.Contains(item: wrapRecBaseName) &&
                 wrapperRecOfRec is { HasDirectBackendType: true, TypeArguments.Count: > 0 } &&
                 wrapperRecOfRec.TypeArguments[index: 0] is RecordTypeInfo innerRecord &&
                 !wrapperRecOfRec.MemberVariables.Any(predicate: mv => mv.Name == member.PropertyName))
        {
            int fieldIndex = -1;
            MemberVariableInfo? fieldInfo = null;
            for (int i = 0; i < innerRecord.MemberVariables.Count; i++)
            {
                if (innerRecord.MemberVariables[index: i].Name == member.PropertyName)
                {
                    fieldIndex = i;
                    fieldInfo = innerRecord.MemberVariables[index: i];
                    break;
                }
            }

            if (fieldIndex < 0 || fieldInfo == null)
            {
                throw new InvalidOperationException(
                    message:
                    $"Member '{member.PropertyName}' not found on inner record '{innerRecord.Name}'");
            }

            string innerRecordTypeName = GetRecordTypeName(record: innerRecord);
            string fieldPtr = NextTemp();
            EmitLine(sb: sb,
                line: $"  {fieldPtr} = getelementptr {innerRecordTypeName}, ptr {target}, i32 0, i32 {fieldIndex}");
            EmitLine(sb: sb,
                line: $"  store {GetLlvmType(type: fieldInfo.Type)} {value}, ptr {fieldPtr}");
        }
        // Wrapper type forwarding: Modifying[T], Claiming[T], etc. -> write through to inner entity
        else if (targetType is RecordTypeInfo wrapperRecord &&
                 GetGenericBaseName(type: wrapperRecord) is { } wrapBaseName &&
                 WrapperTypeNames.Contains(item: wrapBaseName) &&
                 wrapperRecord.TypeArguments is { Count: > 0 } &&
                 wrapperRecord.TypeArguments[index: 0] is EntityTypeInfo innerEntity)
        {
            // For @llvm("ptr") wrappers, the value IS the pointer directly
            // For struct wrappers, extract the inner Hijacked[T] (ptr) from field 0
            string innerPtr;
            // Retained[T] / Tracked[T] are `@llvm("ptr")` but the pointer targets a
            // RetainController[T] struct, NOT the entity directly. The entity ptr lives in the
            // controller's `data` field. Mirrors the read-path handling at
            // LLVMCodeGenerator.Expressions.Entities.cs:441-465 — without this branch, writes
            // to `me.head!!.prev = ...` etc. on Retained/Tracked would store into the
            // controller's strong_count slot instead of the wrapped entity's field.
            if (wrapperRecord.HasDirectBackendType &&
                (wrapBaseName == "Retained" || wrapBaseName == "Tracked"))
            {
                TypeInfo? controllerType = _registry.LookupType(
                    name: $"RetainController[{innerEntity.FullName}]")
                    ?? _registry.LookupType(name: $"Core.RetainController[{innerEntity.FullName}]");
                if (controllerType is EntityTypeInfo controllerEntity)
                {
                    innerPtr = EmitEntityMemberVariableRead(sb: sb,
                        entityPtr: target,
                        entity: controllerEntity,
                        memberVariableName: "data");
                }
                else
                {
                    innerPtr = target;
                }
            }
            else if (wrapperRecord.HasDirectBackendType)
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
                            Name: "Hijacked", TypeArguments.Count: > 0
                        } hijacked &&
                        hijacked.TypeArguments![index: 0] is EntityTypeInfo fieldInner &&
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
        else if (targetType is RecordTypeInfo { HasDirectBackendType: false } plainRecord &&
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
    private void EmitIndexAssignment(StringBuilder sb, IndexExpression index, Expression rhs) // NOSONAR S3776
    {
        // TODO: Record setitem is a hack and should be following $setitem member routine.
        // TODO: Also, the $setitem routine should be just called through anyway and handled not in here.
        TypeInfo? targetType = GetExpressionType(expr: index.Object);
        TryGetTransparentProtocolTarget(type: targetType, targetType: out TypeInfo? lookupType);
        targetType = lookupType ?? targetType;

        RoutineInfo? setItem = LookupSetItemMethod(index: index);

        // Wrapper-record detection: if the resolved $setitem's value-param type doesn't match
        // the target's last type-argument, the lookup unwrapped through a wrapper (e.g.
        // Owned[List[S64]] -> inner List[S64].$setitem!(i64)). The inline mangled-name path
        // would emit a call to the wrapper's symbol which doesn't exist, so escape to the
        // standard method-dispatch path that handles wrapper forwarding correctly.
        // Skip when the last type-arg is a const-generic value (e.g. BitArray[N] where N=8),
        // since const-generic owners are never wrapper forwarders.
        bool isWrapperForwardingSetItem =
            setItem is { Parameters.Count: >= 2 } &&
            targetType?.TypeArguments is [not ConstGenericValueTypeInfo] &&
            setItem.Parameters[^1].Type.FullName != targetType.TypeArguments[^1].FullName;

        // Record $setitem!: the receiver must be the alloca pointer so mutations persist in the
        // caller's frame. EmitMemberRoutineCall evaluates the receiver as a loaded value, which would
        // discard writes -> so keep the pointer-based dispatch inline for this case.
        if (setItem != null && targetType is RecordTypeInfo &&
            setItem.Name.Contains(value: "$setitem") &&
            index.Object is IdentifierExpression recId &&
            !isWrapperForwardingSetItem &&
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

            string mangledName = MangleRoutineName(routine: setItem);

            GenerateRoutineDeclaration(routine: setItem);

            string indexLlvm = indexType != null
                ? GetLlvmType(type: indexType)
                : "i64";
            string valueLlvm;
            // Prefer the resolved $setitem's value param type — that's what the call signature
            // actually expects. Only fall back to TypeArguments[^1] when the param is still an
            // unresolved generic parameter (rare; should not happen for IsGenericResolution targets).
            // Falling through to TypeArguments[^1] is wrong for single-arg wrappers like
            // Owned[List[S64]], where the last type-arg is List[S64], not the element type S64.
            if (setItem.Parameters is [.., _, { Type: not GenericParameterTypeInfo }])
            {
                valueLlvm = GetLlvmType(type: setItem.Parameters[^1].Type);
            }
            else if (targetType.TypeArguments is { Count: > 0 })
            {
                valueLlvm = GetLlvmType(type: targetType.TypeArguments[^1]);
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
            // Failability is a property, not part of the name — use the bare `$setitem`. Codegen
            // dispatches via ResolvedRoutine (dispatchSetItem), which carries IsFailable.
            var member = new MemberExpression(Object: index.Object,
                PropertyName: "$setitem",
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
                type: r.TypeArguments![index: 0]!),
            EntityTypeInfo { TypeArguments.Count: > 0 } e => GetLlvmType(
                type: e.TypeArguments![index: 0]!),
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

    /// <summary>RC wrapper base names that require copy/release on var binding.</summary>
    private static readonly HashSet<string> RcWrapperBaseNames =
        ["Retained", "Tracked"];

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

        // For Maybe[T] carriers, the `value` field (RC wrapper) is uninitialized when
        // present=false. Calling release on a garbage controller AVs. Gate the entire
        // field-release walk on the present flag.
        string? skipLabel = null;
        if (IsMaybeType(type: recordType))
        {
            MemberVariableInfo? presentField = recordType.MemberVariables
                .FirstOrDefault(f => f.Name == "present");
            if (presentField != null)
            {
                string presentVal = NextTemp();
                EmitLine(sb: sb,
                    line: $"  {presentVal} = extractvalue {llvmType} {loaded}, {presentField.Index}");
                string doLabel = NextLabel(prefix: "rcrel_do");
                skipLabel = NextLabel(prefix: "rcrel_skip");
                EmitLine(sb: sb,
                    line: $"  br i1 {presentVal}, label %{doLabel}, label %{skipLabel}");
                EmitLine(sb: sb, line: $"{doLabel}:");
            }
        }

        foreach (MemberVariableInfo field in recordType.MemberVariables)
        {
            if (field.Type is not WrapperTypeInfo w || !RcWrapperBaseNames.Contains(item: w.Name))
            {
                continue;
            }

            string fieldVal = NextTemp();
            EmitLine(sb: sb,
                line: $"  {fieldVal} = extractvalue {llvmType} {loaded}, {field.Index}");

            // Unified teardown: tear the RC-wrapper field down via its `$destroy` (which forwards
            // to `release`→controller), not `release` directly — keeps every teardown on one verb.
            RoutineInfo? destroyMethod = _registry.LookupMethod(type: w, methodName: "$destroy");
            if (destroyMethod == null)
            {
                continue;
            }

            GenerateRoutineDeclaration(routine: destroyMethod);
            string mangled = MangleRoutineName(routine: destroyMethod);
            string fieldLlvm = GetParameterLlvmType(type: w);
            EmitLine(sb: sb, line: $"  call void @{mangled}({fieldLlvm} {fieldVal})");
        }

        if (skipLabel != null)
        {
            EmitLine(sb: sb, line: $"  br label %{skipLabel}");
            EmitLine(sb: sb, line: $"{skipLabel}:");
        }
    }

    /// <summary>
    /// Emits release calls for all tracked RC record variables at scope exit.
    /// Called at return, throw, and absent -> before EmitEntityCleanup.
    /// </summary>
    private void EmitRcRecordCleanup(StringBuilder sb)
    {
        // Teardown is now lowered into the AST as explicit `local.$destroy()` calls by
        // ScopeTeardownLoweringPass (Phase 7) — RC wrapper vars and RC-field records get their
        // `$destroy` (which forwards to `release`) inserted there. Codegen emits no teardown.
        _ = sb;
    }

    /// <summary>Copy verb per RC wrapper (the method that bumps the appropriate count).</summary>
    private static string? RcCopyVerb(string wrapperBase) => wrapperBase switch
    {
        "Retained" => "retain",
        "Tracked" => "track",
        _ => null
    };

    /// <summary>
    /// Bumps the count for an RC wrapper variable by calling its copy verb.
    /// Retained → retain (strong), Tracked → track (weak). Other wrappers skip.
    /// </summary>
    private void EmitRetainedVarRetain(StringBuilder sb, string llvmAddr,
        RecordTypeInfo recordType)
    {
        if (GetGenericBaseName(type: recordType) is not { } baseName ||
            RcCopyVerb(wrapperBase: baseName) is not { } verb)
        {
            return;
        }

        RoutineInfo? copyMethod = _registry.LookupMethod(type: recordType, methodName: verb);
        if (copyMethod == null)
        {
            return;
        }

        string llvmType = GetLlvmType(type: recordType);
        string loaded = NextTemp();
        EmitLine(sb: sb, line: $"  {loaded} = load {llvmType}, ptr {llvmAddr}");

        GenerateRoutineDeclaration(routine: copyMethod);
        string mangled = MangleRoutineName(routine: copyMethod);
        string rcLlvm = GetParameterLlvmType(type: recordType);
        EmitLine(sb: sb, line: $"  {NextTemp()} = call {rcLlvm} @{mangled}({rcLlvm} {loaded})");
    }

    /// <summary>
    /// Tears down an RC wrapper variable at scope exit by calling its <c>$destroy()</c> (which
    /// forwards to <c>release()</c>→controller). Both Retained and Tracked expose <c>$destroy</c>.
    /// </summary>
    private void EmitRetainedVarRelease(StringBuilder sb, string llvmAddr,
        RecordTypeInfo recordType)
    {
        if (GetGenericBaseName(type: recordType) is not { } baseName ||
            RcCopyVerb(wrapperBase: baseName) is null)
        {
            return;
        }

        RoutineInfo? releaseMethod =
            _registry.LookupMethod(type: recordType, methodName: "$destroy");
        if (releaseMethod == null)
        {
            return;
        }

        string llvmType = GetLlvmType(type: recordType);
        string loaded = NextTemp();
        EmitLine(sb: sb, line: $"  {loaded} = load {llvmType}, ptr {llvmAddr}");

        GenerateRoutineDeclaration(routine: releaseMethod);
        string mangled = MangleRoutineName(routine: releaseMethod);
        string rcLlvm = GetParameterLlvmType(type: recordType);

        // Null-check guard: conditionally-declared RC wrapper bindings (e.g. a
        // `when`-arm `else r => ...`) have hoisted, zero-inited allocas that the
        // function-exit cleanup walks even when their arm never ran. Skip teardown
        // when the controller pointer is null.
        string isNull = NextTemp();
        string skipLabel = NextLabel(prefix: "rcwrap_rel_skip");
        string doLabel = NextLabel(prefix: "rcwrap_rel_do");
        EmitLine(sb: sb, line: $"  {isNull} = icmp eq {llvmType} {loaded}, null");
        EmitLine(sb: sb, line: $"  br i1 {isNull}, label %{skipLabel}, label %{doLabel}");
        EmitLine(sb: sb, line: $"{doLabel}:");
        EmitLine(sb: sb, line: $"  call void @{mangled}({rcLlvm} {loaded})");
        EmitLine(sb: sb, line: $"  br label %{skipLabel}");
        EmitLine(sb: sb, line: $"{skipLabel}:");
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
