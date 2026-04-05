using SemanticAnalysis.Symbols;

namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Types;
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
                // Still emit end label but mark that we terminated
                EmitLine(sb: sb, line: $"{endLabel}:");
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
    /// Emits code for a for loop.
    /// for x in iterable { body } becomes:
    ///   iterator = iterable.$iter()
    ///   while iterator.$has_next() { x = iterator.$next(); body }
    /// </summary>
    private void EmitFor(StringBuilder sb, ForStatement forStmt)
    {
        // Fast path: range-based for loop (for x in (start to end) or (start to end by step))
        if (forStmt.Iterable is RangeExpression range)
        {
            EmitForRange(sb: sb, forStmt: forStmt, range: range);
            return;
        }

        // General iterator protocol: seq.$iter() → emitter, emitter.$next() → Maybe[T]
        // Maybe layout: { i64 (DataState), ptr (Snatched handle) }
        // DataState: VALID=1 → has value, ABSENT=0 → done

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

        // Determine element type from $next() return type (preferred) or emitter type arguments (fallback).
        // $next() is preferred because the yielded type may differ from the emitter's type argument
        // (e.g., EnumerateEmitter[S64].$next() yields Tuple[U64, S64], not S64).
        TypeInfo? elemType = null;
        if (emitterType != null)
        {
            RoutineInfo? nextLookup =
                _registry.LookupMethod(type: emitterType, methodName: "$next");
            // Skip protocol-typed return values — they need further resolution
            if (nextLookup?.ReturnType is ErrorHandlingTypeInfo { ValueType: not null } errType &&
                errType.ValueType is not ProtocolTypeInfo)
            {
                elemType = errType.ValueType;
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

        // Condition block: call $next() → Maybe[T] = { i64, ptr }
        EmitLine(sb: sb, line: $"{condLabel}:");
        string emitterLoad = NextTemp();
        EmitLine(sb: sb, line: $"  {emitterLoad} = load {emitterLlvmType}, ptr {emitterAddr}");

        // Call $next() on the emitter (emitting routine, always returns { i64, ptr })
        // LookupMethod handles generic type fallback (e.g., RangeEmitter[S64] → RangeEmitter[T])
        RoutineInfo? nextMethod = emitterType != null
            ? _registry.LookupMethod(type: emitterType, methodName: "$next")
            : null;

        string maybeResult;

        // Emitting routines return a Maybe carrier — layout depends on element type
        string maybeRetType = elemType != null
            ? GetMaybeCarrierLLVMType(valueType: elemType)
            : "{ i1, ptr }";

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
                nextMangled = Q(name: $"{emitterType.FullName}.$next");
                RecordMonomorphization(mangledName: nextMangled,
                    genericMethod: nextMethod,
                    resolvedOwnerType: emitterType);
            }
            else
            {
                nextMangled = MangleFunctionName(routine: nextMethod);
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
                ? Q(name: $"{emitterType.FullName}.$next")
                : "$next";
            maybeResult = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {maybeResult} = call {maybeRetType} @{fallbackName}({emitterLlvmType} {emitterLoad})");
        }

        // Extract tag from Maybe result (field 0 = i1 for Maybe)
        string maybeTagPtr = NextTemp();
        EmitEntryAlloca(llvmName: maybeTagPtr, llvmType: maybeRetType);
        EmitLine(sb: sb, line: $"  store {maybeRetType} {maybeResult}, ptr {maybeTagPtr}");
        string tagFieldPtr = NextTemp();
        string tagVal = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {tagFieldPtr} = getelementptr {maybeRetType}, ptr {maybeTagPtr}, i32 0, i32 0");
        EmitLine(sb: sb, line: $"  {tagVal} = load i1, ptr {tagFieldPtr}");

        // tag == 1 (VALID) → has value → body, else → end
        string hasValue = NextTemp();
        EmitLine(sb: sb, line: $"  {hasValue} = icmp eq i1 {tagVal}, 1");
        EmitLine(sb: sb, line: $"  br i1 {hasValue}, label %{bodyLabel}, label %{endLabel}");

        // Body block: extract payload (field 1)
        EmitLine(sb: sb, line: $"{bodyLabel}:");
        string handlePtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {handlePtr} = getelementptr {maybeRetType}, ptr {maybeTagPtr}, i32 0, i32 1");

        string handleVal;
        if (elemType is EntityTypeInfo)
        {
            // Entity: field 1 is a ptr — load it directly
            handleVal = NextTemp();
            EmitLine(sb: sb, line: $"  {handleVal} = load ptr, ptr {handlePtr}");
        }
        else
        {
            // Record/value: field 1 IS the inline value — handlePtr is already the address
            handleVal = handlePtr;
        }

        // Load element value from the Snatched handle pointer
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
                // Entity: handleVal is the ptr — store it directly
                EmitLine(sb: sb, line: $"  store ptr {handleVal}, ptr {varAddr}");
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
    /// Emits a range-based for loop as a simple counter loop with start/end/step.
    /// </summary>
    private void EmitForRange(StringBuilder sb, ForStatement forStmt, RangeExpression range)
    {
        string condLabel = NextLabel(prefix: "for_cond");
        string bodyLabel = NextLabel(prefix: "for_body");
        string incrLabel = NextLabel(prefix: "for_incr");
        string endLabel = NextLabel(prefix: "for_end");

        _loopStack.Push(item: (incrLabel, endLabel, _usingCleanupStack.Count));

        // Infer element type from range bounds
        TypeInfo? elemType =
            GetExpressionType(expr: range.Start) ?? GetExpressionType(expr: range.End);
        string elemLlvmType = elemType != null
            ? GetLLVMType(type: elemType)
            : "i64";

        // Evaluate range bounds
        string start = EmitExpression(sb: sb, expr: range.Start);
        string end = EmitExpression(sb: sb, expr: range.End);
        string userStep = range.Step != null
            ? EmitExpression(sb: sb, expr: range.Step)
            : null;

        // Allocate loop variable (uniquify name to avoid conflicts with repeated loops)
        string varName = forStmt.Variable ?? "_iter";
        string uniqueVarName;
        if (_varNameCounts.TryGetValue(key: varName, value: out int count))
        {
            _varNameCounts[key: varName] = count + 1;
            uniqueVarName = $"{varName}.{count + 1}";
        }
        else
        {
            _varNameCounts[key: varName] = 1;
            uniqueVarName = varName;
        }

        _localVarLLVMNames[key: varName] = uniqueVarName;
        string varAddr = $"%{uniqueVarName}.addr";
        EmitEntryAlloca(llvmName: varAddr, llvmType: elemLlvmType);
        EmitLine(sb: sb, line: $"  store {elemLlvmType} {start}, ptr {varAddr}");

        // Register loop variable type
        TypeInfo? loopVarType = elemType ?? _registry.LookupType(name: "S64") ??
            _registry.LookupType(name: "@intrinsic.i64");
        if (loopVarType != null)
        {
            _localVariables[key: varName] = loopVarType;
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
            EmitLine(sb: sb, line: $"  {isDescReg} = icmp sgt {elemLlvmType} {start}, {end}");
            if (userStep != null)
            {
                // Negate step if descending: step = select is_desc, -userStep, userStep
                string negStep = NextTemp();
                EmitLine(sb: sb, line: $"  {negStep} = sub {elemLlvmType} 0, {userStep}");
                step = NextTemp();
                EmitLine(sb: sb,
                    line:
                    $"  {step} = select i1 {isDescReg}, {elemLlvmType} {negStep}, {elemLlvmType} {userStep}");
            }
            else
            {
                step = NextTemp();
                EmitLine(sb: sb,
                    line:
                    $"  {step} = select i1 {isDescReg}, {elemLlvmType} -1, {elemLlvmType} 1");
            }
        }

        EmitLine(sb: sb, line: $"  br label %{condLabel}");

        // Condition
        EmitLine(sb: sb, line: $"{condLabel}:");
        string current = NextTemp();
        EmitLine(sb: sb, line: $"  {current} = load {elemLlvmType}, ptr {varAddr}");
        string cmp;
        if (isFloat)
        {
            cmp = NextTemp();
            string fcmpOp = range.IsDescending ? "oge" : range.IsExclusive ? "olt" : "ole";
            EmitLine(sb: sb, line: $"  {cmp} = fcmp {fcmpOp} {elemLlvmType} {current}, {end}");
        }
        else if (range.IsDescending)
        {
            // Explicit descending (downto): always use sge
            cmp = NextTemp();
            string icmpOp = range.IsExclusive
                ? "sgt"
                : "sge";
            EmitLine(sb: sb, line: $"  {cmp} = icmp {icmpOp} {elemLlvmType} {current}, {end}");
        }
        else
        {
            // Runtime direction: emit both comparisons, select based on is_desc
            string ascCmp = NextTemp();
            string descCmp = NextTemp();
            string ascOp = range.IsExclusive
                ? "slt"
                : "sle";
            string descOp = range.IsExclusive
                ? "sgt"
                : "sge";
            EmitLine(sb: sb, line: $"  {ascCmp} = icmp {ascOp} {elemLlvmType} {current}, {end}");
            EmitLine(sb: sb, line: $"  {descCmp} = icmp {descOp} {elemLlvmType} {current}, {end}");
            cmp = NextTemp();
            EmitLine(sb: sb, line: $"  {cmp} = select i1 {isDescReg}, i1 {descCmp}, i1 {ascCmp}");
        }

        EmitLine(sb: sb, line: $"  br i1 {cmp}, label %{bodyLabel}, label %{endLabel}");

        // Body
        EmitLine(sb: sb, line: $"{bodyLabel}:");
        bool bodyTerminated = EmitStatement(sb: sb, stmt: forStmt.Body);
        if (!bodyTerminated)
        {
            EmitLine(sb: sb, line: $"  br label %{incrLabel}");
        }

        // Increment: i += step (step is negative for descending)
        EmitLine(sb: sb, line: $"{incrLabel}:");
        string curVal = NextTemp();
        EmitLine(sb: sb, line: $"  {curVal} = load {elemLlvmType}, ptr {varAddr}");
        string nextVal = NextTemp();
        if (isFloat)
        {
            string fop = range.IsDescending
                ? "fsub"
                : "fadd";
            EmitLine(sb: sb,
                line: $"  {nextVal} = {fop} {elemLlvmType} {curVal}, {userStep ?? "1"}");
        }
        else
        {
            // step already has correct sign from runtime select (or is positive for explicit descending sub)
            if (range.IsDescending)
            {
                EmitLine(sb: sb,
                    line: $"  {nextVal} = sub {elemLlvmType} {curVal}, {userStep ?? "1"}");
            }
            else
            {
                EmitLine(sb: sb, line: $"  {nextVal} = add {elemLlvmType} {curVal}, {step}");
            }
        }

        EmitLine(sb: sb, line: $"  store {elemLlvmType} {nextVal}, ptr {varAddr}");
        EmitLine(sb: sb, line: $"  br label %{condLabel}");

        // End
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
