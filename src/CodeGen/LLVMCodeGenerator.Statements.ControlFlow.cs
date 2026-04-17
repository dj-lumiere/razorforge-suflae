using SemanticVerification.Symbols;

namespace Compiler.CodeGen;

using System.Text;
using SemanticVerification.Types;
using SyntaxTree;

public partial class LLVMCodeGenerator
{
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
                EmitLine(sb: sb, line: $"  unreachable");
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
    private readonly Stack<(string ContinueLabel, string BreakLabel, int UsingDepth)> _loopStack = new();

    /// <summary>
    /// Stack of active using-scope cleanups. Each entry holds the info needed to call $exit()
    /// on early exit (return, throw, break, continue, absent).
    /// </summary>
    private readonly Stack<(string ResourceValue, TypeInfo ResourceType, RoutineInfo ExitMethod)>
        _usingCleanupStack = new();

    /// <summary>
    /// Emits code for a while loop.
    /// </summary>
    private void EmitWhile(StringBuilder sb, WhileStatement whileStmt)
    {
        string condLabel = NextLabel(prefix: "while_cond");
        string bodyLabel = NextLabel(prefix: "while_body");
        string endLabel = NextLabel(prefix: "while_end");

        // Push loop labels for break/continue (record using-stack depth for scoped cleanup)
        _loopStack.Push(item: (condLabel, endLabel, _usingCleanupStack.Count));

        // Jump to condition
        EmitLine(sb: sb, line: $"  br label %{condLabel}");

        // Condition block
        EmitLine(sb: sb, line: $"{condLabel}:");
        string condition = EmitExpression(sb: sb, expr: whileStmt.Condition);
        EmitLine(sb: sb, line: $"  br i1 {condition}, label %{bodyLabel}, label %{endLabel}");

        // Body block
        EmitLine(sb: sb, line: $"{bodyLabel}:");
        bool bodyTerminated = EmitStatement(sb: sb, stmt: whileStmt.Body);
        if (!bodyTerminated)
        {
            EmitLine(sb: sb, line: $"  br label %{condLabel}");
        }

        // End block
        EmitLine(sb: sb, line: $"{endLabel}:");

        // Pop loop labels
        _loopStack.Pop();
    }

    /// <summary>
    /// Emits code for a loop statement (infinite loop primitive).
    /// Unconditional back-edge: continue → loop header, break → end.
    /// </summary>
    private void EmitLoop(StringBuilder sb, LoopStatement loopStmt)
    {
        string bodyLabel = NextLabel(prefix: "loop_body");
        string endLabel = NextLabel(prefix: "loop_end");

        // Push loop labels: continue → body header, break → end
        _loopStack.Push(item: (bodyLabel, endLabel, _usingCleanupStack.Count));

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
    /// Emits code for a for loop.
    /// for x in iterable { body } becomes:
    ///   iterator = iterable.$iter()
    ///   while iterator.$has_next() { x = iterator.$next!(); body }
    /// </summary>
    private void EmitFor(StringBuilder sb, ForStatement forStmt)
    {
        // Range-based for loops are desugared by ControlFlowLoweringPass to loop+when via
        // Range[T].$iter() / RangeEmitter[T].try_next() — they never reach here as ForStatement.
        // Only deferred forms (tuple destructuring, for-else) reach codegen as ForStatement.

        // General iterator protocol: seq.$iter() → iterator, iterator.try_next() → Maybe[T]
        // try_next() returns Maybe[T]; $next!() crashes when exhausted.
        // Record Maybe[T] layout: { i1 present, T value }  — Bool tag + inline value.
        // Entity Maybe[T] layout: { Snatched[T] value }    — single ptr; null = absent.

        string condLabel = NextLabel(prefix: "for_cond");
        string bodyLabel = NextLabel(prefix: "for_body");
        string endLabel = NextLabel(prefix: "for_end");

        _loopStack.Push(item: (condLabel, endLabel, _usingCleanupStack.Count));

        // Evaluate the iterable expression and get its type
        string iterValue = EmitExpression(sb: sb, expr: forStmt.Iterable);
        TypeInfo? iterType = GetExpressionType(expr: forStmt.Iterable);

        // Call $iter() to get the emitter
        string emitterValue;
        TypeInfo? emitterType = null;

        if (iterType != null)
        {
            // Look up $iter method — LookupMethod handles generic type fallback
            RoutineInfo? iterMethod = _registry.LookupMethod(type: iterType, methodName: "$iter");

            if (iterMethod != null)
            {
                // Handle monomorphization for generic types
                string iterMangled;
                if (iterMethod.OwnerType != null && (iterMethod.OwnerType.IsGenericDefinition ||
                                                     iterMethod.OwnerType.IsGenericResolution ||
                                                     iterMethod.OwnerType is ProtocolTypeInfo
                                                         or GenericParameterTypeInfo) &&
                    iterType.IsGenericResolution)
                {
                    iterMangled = Q(name: $"{iterType.FullName}.$iter");
                    RecordMonomorphization(mangledName: iterMangled,
                        genericMethod: iterMethod,
                        resolvedOwnerType: iterType);
                }
                else
                {
                    iterMangled = MangleFunctionName(routine: iterMethod);
                }

                // Skip for protocol-owned methods — monomorphized version will declare itself
                if (iterMethod.OwnerType is not ProtocolTypeInfo)
                {
                    GenerateFunctionDeclaration(routine: iterMethod);
                }

                // Resolve return type: substitute generic params (e.g., RangeEmitter[T] → RangeEmitter[S64])
                TypeInfo? resolvedReturnType = iterMethod.ReturnType;
                if (resolvedReturnType is GenericParameterTypeInfo && iterType is
                        { IsGenericResolution: true, TypeArguments: not null })
                {
                    TypeInfo? ownerGenericDef = iterType switch
                    {
                        RecordTypeInfo r => r.GenericDefinition,
                        EntityTypeInfo e => e.GenericDefinition,
                        _ => null
                    };
                    if (ownerGenericDef?.GenericParameters != null)
                    {
                        int paramIndex = ownerGenericDef.GenericParameters
                                                        .ToList()
                                                        .IndexOf(item: resolvedReturnType.Name);
                        if (paramIndex >= 0 && paramIndex < iterType.TypeArguments.Count)
                        {
                            resolvedReturnType = iterType.TypeArguments[index: paramIndex];
                        }
                    }
                }
                else if (resolvedReturnType is
                             { IsGenericResolution: true, TypeArguments: not null } && iterType is
                             { IsGenericResolution: true, TypeArguments: not null })
                {
                    // Nested resolution: RangeEmitter[T] → RangeEmitter[S64]
                    TypeInfo? ownerGenericDef = iterType switch
                    {
                        RecordTypeInfo r => r.GenericDefinition,
                        EntityTypeInfo e => e.GenericDefinition,
                        _ => null
                    };
                    if (ownerGenericDef?.GenericParameters != null)
                    {
                        bool anyChanged = false;
                        var substitutedArgs = new List<TypeInfo>();
                        foreach (TypeInfo arg in resolvedReturnType.TypeArguments)
                        {
                            if (arg is GenericParameterTypeInfo gp)
                            {
                                int paramIndex = ownerGenericDef.GenericParameters
                                                                .ToList()
                                                                .IndexOf(item: gp.Name);
                                if (paramIndex >= 0 && paramIndex < iterType.TypeArguments.Count)
                                {
                                    substitutedArgs.Add(
                                        item: iterType.TypeArguments[index: paramIndex]);
                                    anyChanged = true;
                                    continue;
                                }
                            }

                            substitutedArgs.Add(item: arg);
                        }

                        if (anyChanged)
                        {
                            TypeInfo? genericBase = GetGenericBase(type: resolvedReturnType) ??
                                                    LookupTypeInCurrentModule(
                                                        name: resolvedReturnType.Name);
                            if (genericBase != null)
                            {
                                resolvedReturnType =
                                    _registry.GetOrCreateResolution(genericDef: genericBase,
                                        typeArguments: substitutedArgs);
                            }
                        }
                    }
                }

                emitterType = resolvedReturnType;

                // Ensure entity type struct definition is emitted for resolved generic emitter types
                if (emitterType is EntityTypeInfo emitterEntityType)
                {
                    GenerateEntityType(entity: emitterEntityType);
                }

                string receiverLlvm = GetParameterLLVMType(type: iterType);
                string emitterReturnType = emitterType != null
                    ? GetLLVMType(type: emitterType)
                    : "ptr";

                // Emit declaration for the monomorphized name
                if (!_generatedFunctions.Contains(item: iterMangled))
                {
                    EmitLine(sb: _functionDeclarations,
                        line: $"declare {emitterReturnType} @{iterMangled}({receiverLlvm})");
                    _generatedFunctions.Add(item: iterMangled);
                }

                string emitterTemp = NextTemp();
                EmitLine(sb: sb,
                    line:
                    $"  {emitterTemp} = call {emitterReturnType} @{iterMangled}({receiverLlvm} {iterValue})");
                emitterValue = emitterTemp;
            }
            else
            {
                // No $iter found, use seqValue directly as the emitter
                emitterValue = iterValue;
                emitterType = iterType;
            }
        }
        else
        {
            emitterValue = iterValue;
        }

        // Store emitter in an alloca so we can reload it each iteration
        string emitterLlvmType = emitterType != null
            ? GetLLVMType(type: emitterType)
            : "ptr";
        string emitterAddr = NextTemp()
           .Replace(oldValue: "%", newValue: "%emitter.");
        EmitEntryAlloca(llvmName: emitterAddr, llvmType: emitterLlvmType);
        EmitLine(sb: sb, line: $"  store {emitterLlvmType} {emitterValue}, ptr {emitterAddr}");

        // Determine element type from try_next() return type (preferred) or iterator type arguments (fallback).
        // try_next is preferred because the yielded type may differ from the iterator's type argument
        // (e.g., EnumerateIterator[S64].$next!() yields Tuple[U64, S64], not S64).
        // All iterators have try_next registered (failable → via ErrorHandlingGenerator). try_next always returns Maybe[T].
        TypeInfo? elemType = null;
        if (emitterType != null)
        {
            RoutineInfo? nextLookup =
                _registry.LookupMethod(type: emitterType, methodName: "try_next");
            // Skip protocol-typed return values — they need further resolution
            if (nextLookup?.ReturnType != null &&
                IsCarrierType(type: nextLookup.ReturnType) &&
                nextLookup.ReturnType.TypeArguments is { Count: > 0 } &&
                nextLookup.ReturnType.TypeArguments[index: 0] is not ProtocolTypeInfo)
            {
                elemType = nextLookup.ReturnType.TypeArguments[index: 0];
            }
            else if (nextLookup?.ReturnType != null &&
                     nextLookup.ReturnType is not ProtocolTypeInfo)
            {
                elemType = nextLookup.ReturnType;
            }
        }

        // Fallback: use emitter's first type argument (works for simple Iterator[T])
        if (elemType == null && emitterType?.TypeArguments is { Count: > 0 })
        {
            elemType = emitterType.TypeArguments[index: 0];
        }

        // Resolve generic parameters in element type using emitter's type arguments
        if (emitterType is { IsGenericResolution: true, TypeArguments: not null })
        {
            elemType = ResolveGenericElementType(elemType: elemType, emitterType: emitterType);
        }

        string elemLlvmType = elemType != null
            ? GetLLVMType(type: elemType)
            : "i64";

        // Allocate loop variable(s)
        string? varAddr = null;
        if (forStmt.VariablePattern != null && elemType is TupleTypeInfo tupleElemType)
        {
            // Tuple destructuring: pre-allocate variables for each binding
            List<DestructuringBinding> bindings = forStmt.VariablePattern.Bindings;
            for (int i = 0; i < bindings.Count && i < tupleElemType.MemberVariables.Count; i++)
            {
                DestructuringBinding binding = bindings[index: i];
                string bindName = binding.BindingName ??
                                  binding.MemberVariableName ?? $"_destruct{i}";
                if (bindName == "_")
                {
                    continue;
                }

                MemberVariableInfo memberVar = tupleElemType.MemberVariables[index: i];
                string memberLlvmType = GetLLVMType(type: memberVar.Type);
                string uniqueName;
                if (_varNameCounts.TryGetValue(key: bindName, value: out int bindCount))
                {
                    _varNameCounts[key: bindName] = bindCount + 1;
                    uniqueName = $"{bindName}.{bindCount + 1}";
                }
                else
                {
                    _varNameCounts[key: bindName] = 1;
                    uniqueName = bindName;
                }

                EmitEntryAlloca(llvmName: $"%{uniqueName}.addr", llvmType: memberLlvmType);
                _localVariables[key: bindName] = memberVar.Type;
                _localVarLLVMNames[key: bindName] = uniqueName;
            }
        }
        else
        {
            string varName = forStmt.Variable ?? "_iter";
            string uniqueName;
            if (_varNameCounts.TryGetValue(key: varName, value: out int count))
            {
                _varNameCounts[key: varName] = count + 1;
                uniqueName = $"{varName}.{count + 1}";
            }
            else
            {
                _varNameCounts[key: varName] = 1;
                uniqueName = varName;
            }

            varAddr = $"%{uniqueName}.addr";
            EmitEntryAlloca(llvmName: varAddr, llvmType: elemLlvmType);

            if (elemType != null)
            {
                _localVariables[key: varName] = elemType;
                _localVarLLVMNames[key: varName] = uniqueName;
            }
        }

        EmitLine(sb: sb, line: $"  br label %{condLabel}");

        // Condition block: call try_next() → Maybe[T]
        // try_next returns Maybe[T]; $next! is generated from it (crashes when None).
        EmitLine(sb: sb, line: $"{condLabel}:");
        string emitterLoad = NextTemp();
        EmitLine(sb: sb, line: $"  {emitterLoad} = load {emitterLlvmType}, ptr {emitterAddr}");

        // Call try_next() on the iterator — returns Maybe[T] carrier.
        // All iterators have try_next registered (failable via ErrorHandlingGenerator).
        // LookupMethod handles generic type fallback.
        RoutineInfo? nextMethod = emitterType != null
            ? _registry.LookupMethod(type: emitterType, methodName: "try_next")
            : null;


        string maybeResult;

        // Iterator try_next returns a Maybe carrier — layout depends on element type
        string maybeRetType = GetMaybeCarrierLLVMType(valueType: elemType!);

        if (nextMethod != null)
        {
            // Handle monomorphization for generic emitter types
            string nextMangled;
            if (nextMethod.OwnerType != null &&
                (nextMethod.OwnerType.IsGenericDefinition ||
                 nextMethod.OwnerType.IsGenericResolution ||
                 nextMethod.OwnerType is ProtocolTypeInfo or GenericParameterTypeInfo) &&
                emitterType != null && emitterType.IsGenericResolution)
            {
                nextMangled = Q(name: $"{emitterType.FullName}.{nextMethod.Name}");
                RecordMonomorphization(mangledName: nextMangled,
                    genericMethod: nextMethod,
                    resolvedOwnerType: emitterType);
            }
            else
            {
                nextMangled = MangleFunctionName(routine: nextMethod);
                // Non-generic iterator type with generated try_next — record for body compilation.
                // e.g., BitListIterator.try_next must compile the $next! body with try wrapping.
                if (nextMethod.OriginalName != null && emitterType != null)
                {
                    RecordMonomorphization(mangledName: nextMangled,
                        genericMethod: nextMethod,
                        resolvedOwnerType: emitterType);
                }
            }

            GenerateFunctionDeclaration(routine: nextMethod);

            // Emit declaration for the monomorphized name
            if (!_generatedFunctions.Contains(item: nextMangled))
            {
                EmitLine(sb: _functionDeclarations,
                    line:
                    $"declare {maybeRetType} @{nextMangled}({emitterLlvmType} {emitterLoad})");
                _generatedFunctions.Add(item: nextMangled);
            }

            maybeResult = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {maybeResult} = call {maybeRetType} @{nextMangled}({emitterLlvmType} {emitterLoad})");
        }
        else
        {
            // Fallback: construct the call name from the type
            string fallbackName = emitterType != null
                ? Q(name: $"{emitterType.FullName}.try_next")
                : "try_next";
            maybeResult = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {maybeResult} = call {maybeRetType} @{fallbackName}({emitterLlvmType} {emitterLoad})");
        }

        // Store Maybe carrier to an alloca so we can GEP into it.
        // Record Maybe[T] layout: { i1 present, T value }  — tag = field 0, value = field 1.
        // Entity Maybe[T] layout: { Snatched[T] value }    — single ptr field; null = absent.
        string maybeTagPtr = NextTemp();
        EmitEntryAlloca(llvmName: maybeTagPtr, llvmType: maybeRetType);
        EmitLine(sb: sb, line: $"  store {maybeRetType} {maybeResult}, ptr {maybeTagPtr}");
        string tagFieldPtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {tagFieldPtr} = getelementptr {maybeRetType}, ptr {maybeTagPtr}, i32 0, i32 0");

        string hasValue;
        if (elemType is EntityTypeInfo)
        {
            // Entity Maybe { Snatched[T] }: field 0 is ptr; null = absent.
            string ptrVal = NextTemp();
            EmitLine(sb: sb, line: $"  {ptrVal} = load ptr, ptr {tagFieldPtr}");
            hasValue = NextTemp();
            EmitLine(sb: sb, line: $"  {hasValue} = icmp ne ptr {ptrVal}, null");
        }
        else
        {
            // Record Maybe { i1, T }: field 0 is Bool tag.
            string tagVal = NextTemp();
            EmitLine(sb: sb, line: $"  {tagVal} = load i1, ptr {tagFieldPtr}");
            hasValue = NextTemp();
            EmitLine(sb: sb, line: $"  {hasValue} = icmp eq i1 {tagVal}, 1");
        }

        EmitLine(sb: sb, line: $"  br i1 {hasValue}, label %{bodyLabel}, label %{endLabel}");

        // Body block: extract payload.
        // Record Maybe: value is at field 1.
        // Entity Maybe: the Snatched ptr at field 0 IS the entity ptr (no field 1 exists).
        EmitLine(sb: sb, line: $"{bodyLabel}:");

        string handleVal;
        if (elemType is EntityTypeInfo)
        {
            // Entity Maybe: field 0 is the Snatched[T] ptr — use it directly.
            handleVal = tagFieldPtr;
        }
        else
        {
            // Record/value Maybe: value is at field 1.
            string handlePtr = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {handlePtr} = getelementptr {maybeRetType}, ptr {maybeTagPtr}, i32 0, i32 1");
            handleVal = handlePtr;
        }

        // Load element value from the handle pointer
        if (forStmt.VariablePattern != null && elemType is TupleTypeInfo bodyTupleType)
        {
            // Tuple destructuring: extract each field from the handle pointer
            List<DestructuringBinding> bindings = forStmt.VariablePattern.Bindings;
            string anonStructType =
                $"{{ {string.Join(separator: ", ", values: bodyTupleType.ElementTypes.Select(selector: e => GetLLVMType(type: e)))} }}";
            for (int i = 0; i < bindings.Count && i < bodyTupleType.MemberVariables.Count; i++)
            {
                DestructuringBinding binding = bindings[index: i];
                string bindName = binding.BindingName ??
                                  binding.MemberVariableName ?? $"_destruct{i}";
                if (bindName == "_")
                {
                    continue;
                }

                MemberVariableInfo memberVar = bodyTupleType.MemberVariables[index: i];
                string memberLlvmType = GetLLVMType(type: memberVar.Type);
                string memberPtr = NextTemp();
                EmitLine(sb: sb,
                    line:
                    $"  {memberPtr} = getelementptr {anonStructType}, ptr {handleVal}, i32 0, i32 {i}");
                string memberVal = NextTemp();
                EmitLine(sb: sb, line: $"  {memberVal} = load {memberLlvmType}, ptr {memberPtr}");
                string bindLlvmName =
                    _localVarLLVMNames.TryGetValue(key: bindName, value: out string? unique)
                        ? unique
                        : bindName;
                EmitLine(sb: sb,
                    line: $"  store {memberLlvmType} {memberVal}, ptr %{bindLlvmName}.addr");
            }
        }
        else if (varAddr != null)
        {
            if (elemType is EntityTypeInfo)
            {
                // Entity Maybe { Snatched[T] }: field 0 ptr IS the entity — load and store.
                // TODO(C87): RC-bump the entity here (call rf_retain) before storing into the loop
                // variable. Currently the loop variable borrows the entity without incrementing its
                // reference count, which is unsound if the source collection is mutated during iteration.
                string entityPtr = NextTemp();
                EmitLine(sb: sb, line: $"  {entityPtr} = load ptr, ptr {handleVal}");
                EmitLine(sb: sb, line: $"  store ptr {entityPtr}, ptr {varAddr}");
            }
            else
            {
                // Record/value: handleVal is ptr to inline T — load then store
                string elemVal = NextTemp();
                EmitLine(sb: sb, line: $"  {elemVal} = load {elemLlvmType}, ptr {handleVal}");
                EmitLine(sb: sb, line: $"  store {elemLlvmType} {elemVal}, ptr {varAddr}");
            }
        }

        bool bodyTerminated = EmitStatement(sb: sb, stmt: forStmt.Body);
        if (!bodyTerminated)
        {
            EmitLine(sb: sb, line: $"  br label %{condLabel}");
        }

        // End block
        EmitLine(sb: sb, line: $"{endLabel}:");
        _loopStack.Pop();
    }

    /// <summary>
    /// Resolves generic parameters within an element type using the emitter's concrete type arguments.
    /// Handles direct params (T → S64), tuple types (Tuple[U64, T] → Tuple[U64, S64]),
    /// and parameterized types (List[T] → List[S64]).
    /// </summary>
    private TypeInfo? ResolveGenericElementType(TypeInfo? elemType, TypeInfo emitterType)
    {
        if (elemType == null)
        {
            return null;
        }

        TypeInfo? emitterGenericDef = emitterType switch
        {
            EntityTypeInfo e => e.GenericDefinition,
            RecordTypeInfo r => r.GenericDefinition,
            _ => null
        };
        if (emitterGenericDef?.GenericParameters == null)
        {
            return elemType;
        }

        // Build param→concrete mapping from emitter (e.g., T → S64)
        var paramMap = new Dictionary<string, TypeInfo>();
        for (int i = 0;
             i < emitterGenericDef.GenericParameters.Count && i < emitterType.TypeArguments!.Count;
             i++)
        {
            paramMap[key: emitterGenericDef.GenericParameters[index: i]] =
                emitterType.TypeArguments[index: i];
        }

        return SubstituteTypeParamsByParamMap(type: elemType, paramMap: paramMap);
    }

    /// <summary>
    /// Recursively substitutes generic type parameters in a type using the given mapping.
    /// </summary>
    private TypeInfo SubstituteTypeParamsByParamMap(TypeInfo type, Dictionary<string, TypeInfo> paramMap)
    {
        // Direct generic parameter (T → S64)
        if (type is GenericParameterTypeInfo &&
            paramMap.TryGetValue(key: type.Name, value: out TypeInfo? sub))
        {
            return sub;
        }

        // Tuple with generic elements (Tuple[U64, T] → Tuple[U64, S64])
        if (type is TupleTypeInfo tuple)
        {
            bool anyChanged = false;
            var resolvedElems = new List<TypeInfo>();
            foreach (TypeInfo elem in tuple.ElementTypes)
            {
                TypeInfo resolved = SubstituteTypeParamsByParamMap(type: elem, paramMap: paramMap);
                if (resolved != elem)
                {
                    anyChanged = true;
                }

                resolvedElems.Add(item: resolved);
            }

            if (anyChanged)
            {
                return new TupleTypeInfo(elementTypes: resolvedElems);
            }
        }

        // Parameterized type with generic args (List[T] → List[S64])
        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            bool anyChanged = false;
            var resolvedArgs = new List<TypeInfo>();
            foreach (TypeInfo ta in type.TypeArguments)
            {
                TypeInfo resolved = SubstituteTypeParamsByParamMap(type: ta, paramMap: paramMap);
                if (resolved != ta)
                {
                    anyChanged = true;
                }

                resolvedArgs.Add(item: resolved);
            }

            if (anyChanged)
            {
                TypeInfo? genericBase = GetGenericBase(type: type) ??
                                        _registry.LookupType(name: type.Name);
                if (genericBase != null)
                {
                    return _registry.GetOrCreateResolution(genericDef: genericBase,
                        typeArguments: resolvedArgs);
                }
            }
        }

        return type;
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

        (_, string breakLabel, int usingDepth) = _loopStack.Peek();
        // Clean up using scopes pushed inside this loop only (not outer scopes)
        EmitUsingCleanup(sb: sb, untilDepth: usingDepth);
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

        (string continueLabel, _, int usingDepth) = _loopStack.Peek();
        // Clean up using scopes pushed inside this loop only (not outer scopes)
        EmitUsingCleanup(sb: sb, untilDepth: usingDepth);
        EmitLine(sb: sb, line: $"  br label %{continueLabel}");
    }
}
