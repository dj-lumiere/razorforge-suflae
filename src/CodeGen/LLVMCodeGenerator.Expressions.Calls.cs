namespace Compiler.CodeGen;

using System.Text;
using Compiler.Desugaring;
using SemanticVerification.Symbols;
using SemanticVerification.Types;
using SyntaxTree;

/// <summary>
/// Expression code generation for routine calls and compound assignment.
/// </summary>
public partial class LLVMCodeGenerator
{
    private static string UnquoteLlvmName(string name)
    {
        return name.Length >= 2 && name[0] == '"' && name[^1] == '"'
            ? name[1..^1]
            : name;
    }

    private string EmitAsyncCompletedTask(StringBuilder sb, RoutineInfo routine, string rawResult)
    {
        int kindValue = routine.AsyncStatus == AsyncStatus.Threaded
            ? 1
            : 0;

        string taskAddress = NextTemp();
        EmitLine(sb: sb, line: $"  {taskAddress} = call i64 @rf_task_create(i32 {kindValue})");
        string task = NextTemp();
        EmitLine(sb: sb, line: $"  {task} = inttoptr i64 {taskAddress} to ptr");
        EmitLine(sb: sb, line: $"  call void @rf_task_mark_running(i64 {taskAddress})");

        TypeInfo? returnType = routine.ReturnType;
        TypeInfo? blankType = _registry.LookupType(name: "Blank");

        if (returnType == null || blankType != null && returnType.Name == blankType.Name)
        {
            EmitLine(sb: sb, line: $"  call void @rf_task_complete_value(i64 {taskAddress}, i64 0)");
            return task;
        }

        if (returnType is EntityTypeInfo)
        {
            string entityAddress = NextTemp();
            EmitLine(sb: sb, line: $"  {entityAddress} = ptrtoint ptr {rawResult} to i64");
            EmitLine(sb: sb,
                line: $"  call void @rf_task_complete_value(i64 {taskAddress}, i64 {entityAddress})");
            return task;
        }

        string payloadType = GetLLVMType(type: returnType);
        int payloadSize = GetTypeSize(type: returnType);
        string payload = NextTemp();
        EmitLine(sb: sb,
            line: $"  {payload} = call ptr @rf_allocate_dynamic(i64 {payloadSize})");
        EmitLine(sb: sb, line: $"  store {payloadType} {rawResult}, ptr {payload}");
        string payloadAddress = NextTemp();
        EmitLine(sb: sb, line: $"  {payloadAddress} = ptrtoint ptr {payload} to i64");
        EmitLine(sb: sb,
            line: $"  call void @rf_task_complete_value(i64 {taskAddress}, i64 {payloadAddress})");
        return task;
    }

    private void EnsureThreadedWorkerDefinition(string workerName, string calleeName,
        RoutineInfo routine, IReadOnlyList<string> argTypes, IReadOnlyList<TypeInfo> argTypeInfos)
    {
        if (!_generatedThreadWorkerDefs.Add(item: workerName))
        {
            return;
        }

        _generatedFunctionDefs.Add(item: workerName);

        EmitLine(sb: _auxFunctionDefinitions, line: $"define void @{workerName}(ptr %task, ptr %userdata) {{");
        EmitLine(sb: _auxFunctionDefinitions, line: "entry:");
        string taskAddress = NextTemp();
        EmitLine(sb: _auxFunctionDefinitions, line: $"  {taskAddress} = ptrtoint ptr %task to i64");

        var loadedArgs = new List<string>();

        for (int i = 0; i < argTypes.Count; i++)
        {
            string slotPtr = NextTemp();
            EmitLine(sb: _auxFunctionDefinitions,
                line: $"  {slotPtr} = getelementptr ptr, ptr %userdata, i64 {i}");
            string slotValue = NextTemp();
            EmitLine(sb: _auxFunctionDefinitions, line: $"  {slotValue} = load ptr, ptr {slotPtr}");

            if (argTypes[i] == "ptr")
            {
                loadedArgs.Add(item: slotValue);
                continue;
            }

            string loaded = NextTemp();
            EmitLine(sb: _auxFunctionDefinitions,
                line: $"  {loaded} = load {argTypes[i]}, ptr {slotValue}");
            string slotAddress = NextTemp();
            EmitLine(sb: _auxFunctionDefinitions, line: $"  {slotAddress} = ptrtoint ptr {slotValue} to i64");
            EmitLine(sb: _auxFunctionDefinitions, line: $"  call void @rf_invalidate(i64 {slotAddress})");
            loadedArgs.Add(item: loaded);
        }

        if (argTypes.Count > 0)
        {
            string userdataAddress = NextTemp();
            EmitLine(sb: _auxFunctionDefinitions, line: $"  {userdataAddress} = ptrtoint ptr %userdata to i64");
            EmitLine(sb: _auxFunctionDefinitions, line: $"  call void @rf_invalidate(i64 {userdataAddress})");
        }

        string args = BuildCallArgs(types: argTypes.ToList(), values: loadedArgs);
        TypeInfo? blankType = _registry.LookupType(name: "Blank");
        TypeInfo? returnType = routine.ReturnType;
        string rawResult;

        if (returnType == null || blankType != null && returnType.Name == blankType.Name)
        {
            EmitLine(sb: _auxFunctionDefinitions,
                line: $"  call void @{calleeName}({args})");
            EmitLine(sb: _auxFunctionDefinitions,
                line: $"  call void @rf_task_complete_value(i64 {taskAddress}, i64 0)");
            EmitLine(sb: _auxFunctionDefinitions, line: "  ret void");
            EmitLine(sb: _auxFunctionDefinitions, line: "}");
            EmitLine(sb: _auxFunctionDefinitions, line: "");
            return;
        }

        string returnLlvmType = GetLLVMType(type: returnType);
        rawResult = NextTemp();
        EmitLine(sb: _auxFunctionDefinitions,
            line: $"  {rawResult} = call {returnLlvmType} @{calleeName}({args})");

        if (returnType is EntityTypeInfo)
        {
            string resultAddress = NextTemp();
            EmitLine(sb: _auxFunctionDefinitions, line: $"  {resultAddress} = ptrtoint ptr {rawResult} to i64");
            EmitLine(sb: _auxFunctionDefinitions,
                line: $"  call void @rf_task_complete_value(i64 {taskAddress}, i64 {resultAddress})");
            EmitLine(sb: _auxFunctionDefinitions, line: "  ret void");
            EmitLine(sb: _auxFunctionDefinitions, line: "}");
            EmitLine(sb: _auxFunctionDefinitions, line: "");
            return;
        }

        int payloadSize = GetTypeSize(type: returnType);
        string payload = NextTemp();
        EmitLine(sb: _auxFunctionDefinitions,
            line: $"  {payload} = call ptr @rf_allocate_dynamic(i64 {payloadSize})");
        EmitLine(sb: _auxFunctionDefinitions,
            line: $"  store {returnLlvmType} {rawResult}, ptr {payload}");
        string payloadAddress = NextTemp();
        EmitLine(sb: _auxFunctionDefinitions, line: $"  {payloadAddress} = ptrtoint ptr {payload} to i64");
        EmitLine(sb: _auxFunctionDefinitions,
            line: $"  call void @rf_task_complete_value(i64 {taskAddress}, i64 {payloadAddress})");
        EmitLine(sb: _auxFunctionDefinitions, line: "  ret void");
        EmitLine(sb: _auxFunctionDefinitions, line: "}");
        EmitLine(sb: _auxFunctionDefinitions, line: "");
    }

    private string EmitThreadedTaskSpawn(StringBuilder sb, RoutineInfo routine, string calleeName,
        IReadOnlyList<string> argValues, IReadOnlyList<string> argTypes, IReadOnlyList<TypeInfo> argTypeInfos)
    {
        string taskAddress = NextTemp();
        EmitLine(sb: sb, line: "  ; threaded routine spawn");
        EmitLine(sb: sb, line: $"  {taskAddress} = call i64 @rf_task_create(i32 1)");
        string task = NextTemp();
        EmitLine(sb: sb, line: $"  {task} = inttoptr i64 {taskAddress} to ptr");

        string userdata = "null";
        string userdataAddress = "0";
        if (argValues.Count > 0)
        {
            userdata = NextTemp();
            EmitLine(sb: sb,
                line: $"  {userdata} = call ptr @rf_allocate_dynamic(i64 {argValues.Count * _pointerSizeBytes})");
            userdataAddress = NextTemp();
            EmitLine(sb: sb, line: $"  {userdataAddress} = ptrtoint ptr {userdata} to i64");

            for (int i = 0; i < argValues.Count; i++)
            {
                string slotPtr = NextTemp();
                EmitLine(sb: sb,
                    line: $"  {slotPtr} = getelementptr ptr, ptr {userdata}, i64 {i}");

                if (argTypes[i] == "ptr")
                {
                    EmitLine(sb: sb, line: $"  store ptr {argValues[i]}, ptr {slotPtr}");
                    continue;
                }

                int payloadSize = GetTypeSize(type: argTypeInfos[i]);
                string boxed = NextTemp();
                EmitLine(sb: sb,
                    line: $"  {boxed} = call ptr @rf_allocate_dynamic(i64 {payloadSize})");
                EmitLine(sb: sb, line: $"  store {argTypes[i]} {argValues[i]}, ptr {boxed}");
                EmitLine(sb: sb, line: $"  store ptr {boxed}, ptr {slotPtr}");
            }
        }

        string workerBaseName = $"{UnquoteLlvmName(name: calleeName)}$thread_worker";
        string workerName = Q(name: workerBaseName);
        EnsureThreadedWorkerDefinition(workerName: workerName,
            calleeName: calleeName,
            routine: routine,
            argTypes: argTypes,
            argTypeInfos: argTypeInfos);

        string workerAddress = NextTemp();
        EmitLine(sb: sb, line: $"  {workerAddress} = ptrtoint ptr @{workerName} to i64");
        string spawnOk = NextTemp();
        EmitLine(sb: sb,
            line: $"  {spawnOk} = call i32 @rf_task_spawn_threaded(i64 {taskAddress}, i64 {workerAddress}, i64 {userdataAddress})");
        return task;
    }

    private string EmitWaitforExpression(StringBuilder sb, WaitforExpression waitfor)
    {
        string operandValue = EmitExpression(sb: sb, expr: waitfor.Operand);
        TypeInfo? operandType = GetExpressionType(expr: waitfor.Operand);
        if (operandType == null)
        {
            return operandValue;
        }

        if (operandType.Name == "Duration")
        {
            string durationLlvmType = GetLLVMType(type: operandType);
            string durationSeconds = NextTemp();
            EmitLine(sb: sb,
                line: $"  {durationSeconds} = extractvalue {durationLlvmType} {operandValue}, 0");
            string durationNanoseconds = NextTemp();
            EmitLine(sb: sb,
                line: $"  {durationNanoseconds} = extractvalue {durationLlvmType} {operandValue}, 1");
            EmitLine(sb: sb,
                line:
                $"  call void @rf_waitfor_duration(i64 {durationSeconds}, i32 {durationNanoseconds})");
            return "zeroinitializer";
        }

        if (GetGenericBaseName(type: operandType) != "Task" ||
            operandType.TypeArguments is not { Count: 1 })
        {
            return operandValue;
        }

        TypeInfo valueType = operandType.TypeArguments[index: 0];

        if (_declaredNativeFunctions.Add(item: "llvm.trap"))
        {
            EmitLine(sb: _functionDeclarations,
                line: "declare void @llvm.trap() noreturn nounwind");
        }

        string taskValue = operandValue;
        string taskAddress = NextTemp();
        EmitLine(sb: sb, line: $"  {taskAddress} = ptrtoint ptr {taskValue} to i64");
        string completionKind = NextTemp();
        if (waitfor.Timeout != null)
        {
            string timeoutValue = EmitExpression(sb: sb, expr: waitfor.Timeout);
            TypeInfo? timeoutType = GetExpressionType(expr: waitfor.Timeout) ??
                                    _registry.LookupType(name: "Duration");
            string timeoutLlvmType = timeoutType != null
                ? GetLLVMType(type: timeoutType)
                : "{ i64, i32 }";
            string timeoutSeconds = NextTemp();
            EmitLine(sb: sb,
                line: $"  {timeoutSeconds} = extractvalue {timeoutLlvmType} {timeoutValue}, 0");
            string timeoutNanoseconds = NextTemp();
            EmitLine(sb: sb,
                line: $"  {timeoutNanoseconds} = extractvalue {timeoutLlvmType} {timeoutValue}, 1");
            EmitLine(sb: sb,
                line:
                $"  {completionKind} = call i32 @rf_task_wait_within(i64 {taskAddress}, i64 {timeoutSeconds}, i32 {timeoutNanoseconds})");
        }
        else
        {
            EmitLine(sb: sb, line: $"  {completionKind} = call i32 @rf_task_wait(i64 {taskAddress})");
        }

        string isValue = NextTemp();
        EmitLine(sb: sb, line: $"  {isValue} = icmp eq i32 {completionKind}, 1");
        string okLabel = NextLabel(prefix: "waitfor_ok");
        string failLabel = NextLabel(prefix: "waitfor_fail");
        EmitLine(sb: sb, line: $"  br i1 {isValue}, label %{okLabel}, label %{failLabel}");

        EmitLine(sb: sb, line: $"{failLabel}:");
        EmitLine(sb: sb, line: "  call void @llvm.trap()");
        EmitLine(sb: sb, line: "  unreachable");

        EmitLine(sb: sb, line: $"{okLabel}:");

        if (valueType.Name == "Blank")
        {
            EmitLine(sb: sb, line: $"  call void @rf_task_mark_result_consumed(i64 {taskAddress})");
            return "zeroinitializer";
        }

        string payloadAddress = NextTemp();
        EmitLine(sb: sb,
            line: $"  {payloadAddress} = call i64 @rf_task_result_payload(i64 {taskAddress})");
        EmitLine(sb: sb, line: $"  call void @rf_task_mark_result_consumed(i64 {taskAddress})");
        string payload = NextTemp();
        EmitLine(sb: sb, line: $"  {payload} = inttoptr i64 {payloadAddress} to ptr");

        if (valueType is EntityTypeInfo)
        {
            return payload;
        }

        string llvmType = GetLLVMType(type: valueType);
        string loaded = NextTemp();
        EmitLine(sb: sb, line: $"  {loaded} = load {llvmType}, ptr {payload}");
        return loaded;
    }

    private string EmitFunctionCall(StringBuilder sb, string functionName,
        List<Expression> arguments, RoutineInfo? resolvedRoutine = null,
        TypeInfo? resolvedReturnType = null)
    {
        bool isFailableCallSyntax = functionName.EndsWith(value: '!');
        // Strip failable '!' suffix — registry stores names without it
        if (isFailableCallSyntax)
        {
            functionName = functionName[..^1];
        }

        // Indirect call through a local function-pointer variable (e.g., compare(a: x, b: y)
        // where 'compare' is a parameter of type Routine[(T, T), Bool]).
        if (_localVariables.TryGetValue(key: functionName, value: out TypeInfo? localType) &&
            localType is RoutineTypeInfo routineTypeInfo)
        {
            string llvmName =
                _localVarLLVMNames.TryGetValue(key: functionName, value: out string? unique)
                    ? unique
                    : functionName;
            string fpVal = NextTemp();
            EmitLine(sb: sb, line: $"  {fpVal} = load ptr, ptr %{llvmName}.addr");

            var fpArgValues = new List<string>();
            var fpArgTypes = new List<string>();
            foreach (Expression arg in arguments)
            {
                string v = EmitExpression(sb: sb, expr: arg);
                fpArgValues.Add(item: v);
                TypeInfo? argType = GetExpressionType(expr: arg);
                fpArgTypes.Add(item: argType != null ? GetLLVMType(type: argType) : "i64");
            }

            string retLlvm = routineTypeInfo.ReturnType != null
                ? GetLLVMType(type: routineTypeInfo.ReturnType)
                : "void";

            string callArgs = BuildCallArgs(types: fpArgTypes, values: fpArgValues);
            if (retLlvm == "void")
            {
                EmitLine(sb: sb, line: $"  call void {fpVal}({callArgs})");
                return "undef";
            }
            else
            {
                string result = NextTemp();
                EmitLine(sb: sb,
                    line: $"  {result} = call {retLlvm} {fpVal}({callArgs})");
                return result;
            }
        }

        // Primitive type conversion: U8(val), S32(val), F64(val), etc.
        // For @llvm types with a single argument, emit trunc/zext/sext/fpcast inline.
        // This must be checked BEFORE routine lookup, as stdlib defines conversion routines
        // (e.g., "routine U8(from: S8) -> U8") that we want to inline instead of calling.
        if (arguments.Count == 1)
        {
            TypeInfo? calledType = LookupTypeInCurrentModule(name: functionName);
            if (calledType is RecordTypeInfo { HasDirectBackendType: true })
            {
                return EmitPrimitiveTypeConversion(sb: sb,
                    targetTypeName: functionName,
                    arg: arguments[index: 0],
                    targetType: calledType);
            }
        }

        // Record/entity direct field construction — check BEFORE routine lookup so a user-defined
        // 0-arg routine with the same name doesn't shadow the constructor call.
        // e.g., NotImplementedError(feature: "warp drive") where NotImplementedError() also exists.
        {
            TypeInfo? ctorType = LookupTypeInCurrentModule(name: functionName);
            if (ctorType is RecordTypeInfo ctorRecord && ctorRecord.MemberVariables.Count > 0 &&
                arguments.Count == ctorRecord.MemberVariables.Count && arguments.All(
                    predicate: a =>
                        a is NamedArgumentExpression namedArg &&
                        ctorRecord.MemberVariables.Any(
                            predicate: mv => mv.Name == namedArg.Name)))
            {
                return EmitRecordConstruction(sb: sb, record: ctorRecord, arguments: arguments);
            }

            if (ctorType is EntityTypeInfo ctorEntity && ctorEntity.MemberVariables.Count > 0 &&
                arguments.Count == ctorEntity.MemberVariables.Count && arguments.All(
                    predicate: a =>
                        a is NamedArgumentExpression namedArg &&
                        ctorEntity.MemberVariables.Any(
                            predicate: mv => mv.Name == namedArg.Name)))
            {
                return EmitEntityConstruction(sb: sb, entity: ctorEntity, arguments: arguments);
            }

            if (ctorType is CrashableTypeInfo ctorCrashable &&
                arguments.Count == ctorCrashable.MemberVariables.Count && arguments.All(
                    predicate: a => ctorCrashable.MemberVariables.Count == 0 ||
                                    a is NamedArgumentExpression namedArg &&
                                    ctorCrashable.MemberVariables.Any(
                                        predicate: mv => mv.Name == namedArg.Name)))
            {
                return EmitCrashableConstruction(sb: sb, crashable: ctorCrashable,
                    arguments: arguments);
            }
        }

        // Use semantic analyzer's resolved routine if available (e.g., generic overload)
        // Otherwise look up the routine — try full name first, then short name fallback
        RoutineInfo? routine = resolvedRoutine ??
                               _registry.LookupRoutine(fullName: functionName,
                                   isFailable: isFailableCallSyntax) ??
                               _registry.LookupRoutineByName(name: functionName,
                                   isFailable: isFailableCallSyntax);

        // Generic free function monomorphization: when calling a generic instance (e.g., show[S64]),
        // record the monomorphization so the body is compiled with T=S64
        if (routine is { GenericDefinition: not null, TypeArguments: not null })
        {
            string monoName = MangleFunctionName(routine: routine);
            if (!_planner.HasEntry(mangledName: monoName))
            {
                RoutineInfo? genericDef = routine.GenericDefinition;
                var typeSubs = new Dictionary<string, TypeInfo>();
                for (int i = 0; i < genericDef.GenericParameters!.Count; i++)
                {
                    typeSubs[key: genericDef.GenericParameters[index: i]] =
                        (TypeInfo)routine.TypeArguments[index: i];
                }

                // Use [generic] marker so FindAstRoutine skips non-generic overloads
                string genericAstName = $"{genericDef.Name}[generic]";
                _planner.AddDirectEntry(mangledName: monoName, entry: new MonomorphizationEntry(
                    GenericMethod: genericDef,
                    ResolvedOwnerType: null!,
                    TypeSubstitutions: typeSubs,
                    GenericAstName: genericAstName));
            }
        }

        // If not found as a routine, check if it's a type name
        bool isCreatorCall = false;
        if (routine == null)
        {
            TypeInfo? calledType = LookupTypeInCurrentModule(name: functionName);
            if (calledType != null)
            {
                // Direct named-field construction: when all arg names match field names exactly,
                // emit struct construction directly (avoids $create infinite recursion).
                // e.g., CStr(ptr: from_ptr) inside CStr.$create body
                if (calledType is RecordTypeInfo record && record.MemberVariables.Count > 0 &&
                    arguments.Count == record.MemberVariables.Count && arguments.All(
                        predicate: a =>
                            a is NamedArgumentExpression named &&
                            record.MemberVariables.Any(predicate: mv => mv.Name == named.Name)))
                {
                    return EmitRecordConstruction(sb: sb, record: record, arguments: arguments);
                }

                if (calledType is EntityTypeInfo entity && entity.MemberVariables.Count > 0 &&
                    arguments.Count == entity.MemberVariables.Count && arguments.All(
                        predicate: a =>
                            a is NamedArgumentExpression named2 &&
                            entity.MemberVariables.Any(predicate: mv => mv.Name == named2.Name)))
                {
                    return EmitEntityConstruction(sb: sb, entity: entity, arguments: arguments);
                }

                if (calledType is CrashableTypeInfo crashable &&
                    arguments.Count == crashable.MemberVariables.Count && arguments.All(
                        predicate: a => crashable.MemberVariables.Count == 0 ||
                                        a is NamedArgumentExpression named3 &&
                                        crashable.MemberVariables.Any(
                                            predicate: mv => mv.Name == named3.Name)))
                {
                    return EmitCrashableConstruction(sb: sb, crashable: crashable,
                        arguments: arguments);
                }

                // Zero-arg entity construction → try $create() first, then null
                if (calledType is EntityTypeInfo && arguments.Count == 0)
                {
                    string createName = $"{calledType.Name}.$create";
                    RoutineInfo? creator = _registry.LookupRoutineOverload(baseName: createName,
                        argTypes: new List<TypeInfo>());
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
                foreach (Expression arg in arguments)
                {
                    TypeInfo? t = GetExpressionType(expr: arg);
                    if (t != null)
                    {
                        semanticArgTypes.Add(item: t);
                    }
                }

                routine = _registry.LookupRoutineOverload(baseName: $"{calledType.Name}.$create",
                    argTypes: semanticArgTypes);
                if (routine != null)
                {
                    isCreatorCall = true;
                }
                else
                {
                    // For single-field records where arg name doesn't match field name
                    // (e.g., Character(codepoint: val) where field is 'value')
                    if (calledType is RecordTypeInfo singleRecord &&
                        singleRecord.MemberVariables.Count == 1 && arguments.Count == 1 &&
                        arguments[index: 0] is NamedArgumentExpression)
                    {
                        return EmitRecordConstruction(sb: sb,
                            record: singleRecord,
                            arguments: arguments);
                    }

                    isCreatorCall = true;
                }
            }
        }

        // Evaluate all arguments
        var argValues = new List<string>();
        var argTypes = new List<string>();
        var argTypeInfos = new List<TypeInfo>();

        foreach (Expression arg in arguments)
        {
            string value = EmitExpression(sb: sb, expr: arg);
            argValues.Add(item: value);

            TypeInfo? argType = GetExpressionType(expr: arg);
            if (argType == null)
            {
                throw new InvalidOperationException(
                    message:
                    $"Cannot determine type for argument in function call to '{functionName}'");
            }

            argTypeInfos.Add(item: argType);
            argTypes.Add(item: GetLLVMType(type: argType));
        }

        // Supply default arguments for parameters not covered by explicit arguments
        if (routine != null)
        {
            for (int i = argValues.Count; i < routine.Parameters.Count; i++)
            {
                ParameterInfo param = routine.Parameters[index: i];
                if (param.HasDefaultValue)
                {
                    string value = EmitExpression(sb: sb, expr: param.DefaultValue!);
                    argValues.Add(item: value);
                    argTypeInfos.Add(item: param.Type);
                    argTypes.Add(item: GetLLVMType(type: param.Type));
                }
            }
        }

        // Inside monomorphized body: if the resolved routine's param types don't match
        // the actual arg types, look for a matching generic overload and instantiate it.
        // This handles cases like show(v, end: "") inside monomorphized show[T=S64] body,
        // where registry lookup finds concrete show(Text,Text) instead of show[T](T,Text).
        if (_typeSubstitutions != null && routine != null && routine.GenericDefinition == null &&
            !routine.IsGenericDefinition && argValues.Count > 0 && routine.Parameters.Count > 0)
        {
            TypeInfo? expectedType = routine.Parameters[index: 0].Type;
            if (expectedType != null)
            {
                expectedType = ApplyTypeSubstitutions(type: expectedType);
            }

            TypeInfo? actualArgType = GetExpressionType(expr: arguments[index: 0]);

            if (actualArgType != null && expectedType != null &&
                actualArgType.FullName != expectedType.FullName)
            {
                RoutineInfo? genericOverload = _registry.LookupGenericOverload(name: routine.Name);
                if (genericOverload?.GenericParameters is { Count: > 0 })
                {
                    // Infer type args from ALL argument types
                    var inferredArgTypeInfos = arguments
                                               .Select(selector: a => GetExpressionType(expr: a))
                                               .Where(predicate: t => t != null)
                                               .Cast<TypeInfo>()
                                               .ToList();

                    Dictionary<string, TypeInfo>? inferred =
                        InferMethodTypeArgs(genericMethod: genericOverload,
                            argTypes: inferredArgTypeInfos);

                    // Build type args list from inferred dict, falling back to actual arg types
                    var typeArgs = new List<TypeInfo>();
                    for (int i = 0; i < genericOverload.GenericParameters.Count; i++)
                    {
                        string paramName = genericOverload.GenericParameters[index: i];
                        if (inferred != null &&
                            inferred.TryGetValue(key: paramName, value: out TypeInfo? t))
                        {
                            typeArgs.Add(item: t);
                        }
                        else if (i < inferredArgTypeInfos.Count)
                        {
                            typeArgs.Add(item: inferredArgTypeInfos[index: i]);
                        }
                        else
                        {
                            typeArgs.Add(item: inferredArgTypeInfos[index: 0]); // last resort fallback
                        }
                    }

                    routine = genericOverload.CreateInstance(typeArguments: typeArgs);

                    // Re-supply default arguments for the new routine
                    while (argValues.Count > arguments.Count)
                    {
                        argValues.RemoveAt(index: argValues.Count - 1);
                        argTypeInfos.RemoveAt(index: argTypeInfos.Count - 1);
                        argTypes.RemoveAt(index: argTypes.Count - 1);
                    }

                    for (int i = argValues.Count; i < routine.Parameters.Count; i++)
                    {
                        ParameterInfo param = routine.Parameters[index: i];
                        if (param.HasDefaultValue)
                        {
                            string value = EmitExpression(sb: sb, expr: param.DefaultValue!);
                            argValues.Add(item: value);
                            argTypeInfos.Add(item: param.Type);
                            argTypes.Add(item: GetLLVMType(type: param.Type));
                        }
                    }

                    // Record monomorphization
                    string monoName = MangleFunctionName(routine: routine);
                    if (!_planner.HasEntry(mangledName: monoName))
                    {
                        var typeSubs = new Dictionary<string, TypeInfo>();
                        for (int i = 0; i < genericOverload.GenericParameters.Count; i++)
                        {
                            typeSubs[key: genericOverload.GenericParameters[index: i]] =
                                (TypeInfo)routine.TypeArguments![index: i];
                        }

                        string genericAstName = $"{genericOverload.Name}[generic]";
                        _planner.AddDirectEntry(mangledName: monoName, entry: new MonomorphizationEntry(
                            GenericMethod: genericOverload,
                            ResolvedOwnerType: null!,
                            TypeSubstitutions: typeSubs,
                            GenericAstName: genericAstName));
                    }
                }
            }
        }

        // C105: Pack variadic arguments into List[T]
        if (routine is { IsVariadic: true })
        {
            PackVariadicArgs(sb: sb,
                routine: routine,
                arguments: arguments,
                argValues: ref argValues,
                argTypes: ref argTypes,
                argOffset: 0);
        }

        // Build the call
        string mangledName = routine != null
            ? MangleFunctionName(routine: routine)
            : DecorateRoutineSymbolName(baseName: SanitizeLLVMName(name: functionName),
                isFailable: isFailableCallSyntax);

        // Inside monomorphized bodies, calls to routines on generic types need the resolved owner type.
        // e.g., Snatched[T].$create(from: addr) inside Snatched[T].offset → when T=SortedDict[S64,S64],
        // the call should target Snatched[SortedDict[S64,S64]].$create#Address, not Snatched.$create#Address.
        if (_typeSubstitutions != null && routine?.OwnerType is
                { IsGenericDefinition: true, GenericParameters: not null })
        {
            var resolvedArgs = new List<TypeInfo>();
            foreach (string param in routine.OwnerType.GenericParameters)
            {
                if (_typeSubstitutions.TryGetValue(key: param, value: out TypeInfo? sub))
                {
                    resolvedArgs.Add(item: sub);
                }
                else
                {
                    break;
                }
            }

            if (resolvedArgs.Count == routine.OwnerType.GenericParameters.Count)
            {
                TypeInfo resolvedOwnerType =
                    _registry.GetOrCreateResolution(genericDef: routine.OwnerType,
                        typeArguments: resolvedArgs);
                string resolvedOwnerName = resolvedOwnerType.FullName;
                string paramSuffix = "";
                if (routine.Name == "$create" && routine.Parameters.Count > 0)
                {
                    TypeInfo resolvedParamType =
                        _planner.ResolveSubstitutedType(type: routine.Parameters[index: 0].Type,
                            subs: _typeSubstitutions!);
                    paramSuffix = $"({resolvedParamType.Name})";
                }

                mangledName =
                    Q(name: DecorateRoutineSymbolName(
                        baseName:
                        $"{resolvedOwnerName}.{SanitizeLLVMName(name: routine.Name)}{paramSuffix}",
                        isFailable: routine.IsFailable));

                // Record monomorphization so the body gets generated
                if (!_planner.HasEntry(mangledName: mangledName) &&
                    !_generatedFunctionDefs.Contains(item: mangledName))
                {
                    string genericAstName = $"{routine.OwnerType.Name}.{routine.Name}";
                    _planner.AddDirectEntry(mangledName: mangledName, entry: new MonomorphizationEntry(
                        GenericMethod: routine,
                        ResolvedOwnerType: resolvedOwnerType,
                        TypeSubstitutions: _typeSubstitutions!,
                        GenericAstName: genericAstName));
                }
            }
        }

        // Fallback for lowered generic-type constructor calls (e.g., List[U64](cap) lowered to
        // CallExpression("$create", ResolvedType=Core.List[Core.U64]) by GenericCallLoweringPass).
        // The block above can't resolve the owner when the owner's generic param (T) is not
        // directly in _typeSubstitutions (e.g., Dict[K,V] body calling Snatched[K] — T ∉ subs,
        // but K ∈ subs, and resolvedReturnType.TypeArguments[0] = GenericParameterTypeInfo("K")).
        // Reconstruct the concrete owner type by resolving all type args through substitutions.
        if (routine?.Name == "$create"
            && routine.OwnerType is { IsGenericDefinition: true, GenericParameters: not null }
            && resolvedReturnType is { IsGenericResolution: true, TypeArguments: { Count: > 0 } }
            && routine.OwnerType.GenericParameters.Count == resolvedReturnType.TypeArguments.Count)
        {
            // Resolve each type arg: if it's a GenericParameterTypeInfo, look up in
            // _typeSubstitutions (handles Snatched[K] where K=S64 from Dict body).
            // If already concrete, use as-is (handles List[U64] case).
            var resolvedOwnerArgs = new List<TypeInfo>();
            foreach (TypeInfo ta in resolvedReturnType.TypeArguments)
            {
                if (ta is GenericParameterTypeInfo gpArg && _typeSubstitutions != null
                    && _typeSubstitutions.TryGetValue(key: gpArg.Name, value: out TypeInfo? subArg))
                {
                    resolvedOwnerArgs.Add(item: subArg);
                }
                else if (ta is not GenericParameterTypeInfo)
                {
                    resolvedOwnerArgs.Add(item: ta); // already concrete
                }
                else
                {
                    break; // unresolvable generic param — give up
                }
            }

            if (resolvedOwnerArgs.Count == routine.OwnerType.GenericParameters.Count)
            {
                TypeInfo resolvedOwnerType2 = _registry.GetOrCreateResolution(
                    genericDef: routine.OwnerType, typeArguments: resolvedOwnerArgs);

                string paramSuffix2 = routine.Parameters.Count > 0
                    // Use .Name (short) for param type — consistent with EmitGenericMethodCall
                    // and the block above (both use .Name, not .FullName).
                    ? $"({routine.Parameters[index: 0].Type.Name})"
                    : "";

                string recoveredName = Q(name: DecorateRoutineSymbolName(
                    baseName:
                    $"{resolvedOwnerType2.FullName}.{SanitizeLLVMName(name: routine.Name)}{paramSuffix2}",
                    isFailable: routine.IsFailable));

                // Only use the recovered name if it differs from the current (wrong) one.
                if (recoveredName != mangledName)
                {
                    mangledName = recoveredName;

                    // Record monomorphization so the body gets generated.
                    if (!_planner.HasEntry(mangledName: mangledName) &&
                        !_generatedFunctionDefs.Contains(item: mangledName))
                    {
                        string genericAstName = $"{routine.OwnerType.Name}.{routine.Name}";
                        var typeSubs2 = new Dictionary<string, TypeInfo>();
                        for (int i = 0; i < routine.OwnerType.GenericParameters.Count; i++)
                        {
                            typeSubs2[key: routine.OwnerType.GenericParameters[index: i]] =
                                resolvedOwnerArgs[index: i];
                        }

                        _planner.AddDirectEntry(mangledName: mangledName,
                            entry: new MonomorphizationEntry(
                                GenericMethod: routine,
                                ResolvedOwnerType: resolvedOwnerType2,
                                TypeSubstitutions: typeSubs2,
                                GenericAstName: genericAstName));
                    }
                }
            }
        }

        // Ensure the function is declared (generates 'declare' and tracks in _generatedFunctions)
        if (routine != null)
        {
            GenerateFunctionDeclaration(routine: routine);
        }
        else
        {
            _generatedFunctions.Add(item: mangledName);
        }

        // For external("C") functions, F16 (half) params must be bitcast to i16 (C ABI)
        bool isCExtern = routine is { CallingConvention: "C" };
        if (isCExtern)
        {
            for (int i = 0; i < argTypes.Count; i++)
            {
                if (argTypes[index: i] == "half")
                {
                    string bits = NextTemp();
                    EmitLine(sb: sb,
                        line: $"  {bits} = bitcast half {argValues[index: i]} to i16");
                    argValues[index: i] = bits;
                    argTypes[index: i] = "i16";
                }
            }
        }

        if (routine?.AsyncStatus == AsyncStatus.Threaded)
        {
            return EmitThreadedTaskSpawn(sb: sb,
                routine: routine,
                calleeName: mangledName,
                argValues: argValues,
                argTypes: argTypes,
                argTypeInfos: argTypeInfos);
        }

        string returnType = routine?.ReturnType != null
            ? GetLLVMType(type: routine.ReturnType)
            : "void";
        // Failable routines return T directly — they crash on failure, no carrier needed

        string callReturnType = isCExtern && returnType == "half"
            ? "i16"
            : returnType;

        // On Windows x64 MSVC ABI, external("C") functions returning structs > 8 bytes
        // use a hidden sret pointer as the first parameter. We must match this convention.
        bool needsSret = isCExtern && routine != null && NeedsCExternSret(routine: routine);
        if (needsSret)
        {
            // Allocate space for the result, pass as sret pointer, call as void, then load
            string sretPtr = NextTemp();
            EmitEntryAlloca(llvmName: sretPtr, llvmType: returnType);
            // Insert sret pointer as first argument
            argTypes.Insert(index: 0, item: $"ptr sret({returnType})");
            argValues.Insert(index: 0, item: sretPtr);
            string args = BuildCallArgs(types: argTypes, values: argValues);
            EmitLine(sb: sb, line: $"  call void @{mangledName}({args})");
            // Load the result from the sret allocation
            string result = NextTemp();
            EmitLine(sb: sb, line: $"  {result} = load {returnType}, ptr {sretPtr}");
            return result;
        }
        else if (callReturnType == "void")
        {
            // Void return - no result
            string args = BuildCallArgs(types: argTypes, values: argValues);
            EmitLine(sb: sb, line: $"  call void @{mangledName}({args})");
            return routine?.IsAsync == true
                ? EmitAsyncCompletedTask(sb: sb, routine: routine, rawResult: "undef")
                : "undef"; // No meaningful return value
        }
        else
        {
            // Has return value
            string result = NextTemp();
            string args = BuildCallArgs(types: argTypes, values: argValues);
            EmitLine(sb: sb, line: $"  {result} = call {callReturnType} @{mangledName}({args})");
            // For external("C") F16 return, bitcast i16 back to half
            if (isCExtern && returnType == "half" && callReturnType == "i16")
            {
                string halfResult = NextTemp();
                EmitLine(sb: sb, line: $"  {halfResult} = bitcast i16 {result} to half");
                return halfResult;
            }

            return routine?.IsAsync == true
                ? EmitAsyncCompletedTask(sb: sb, routine: routine, rawResult: result)
                : result;
        }
    }

    /// <summary>
    /// Generates code for a method call on an object.
    /// The object becomes the implicit 'me' parameter.
    /// </summary>
    private string EmitMethodCall(StringBuilder sb, MemberExpression member,
        List<Expression> arguments, RoutineInfo? resolvedRoutine = null,
        IReadOnlyList<TypeExpression>? typeArguments = null)
    {
        // Intercept var_name() — inline the variable name from the receiver expression
        if (member.PropertyName == "var_name" && arguments.Count == 0)
        {
            string varName = member.Object is IdentifierExpression varId
                ? varId.Name
                : "<expr>";
            return EmitSynthesizedStringLiteral(value: varName);
        }

        // Evaluate the receiver (becomes 'me' parameter)
        // Handle type-level method calls (e.g., S64.data_size() from monomorphized T.data_size()).
        // After GenericAstRewriter substitutes T→S64 in IdentifierExpression, the type name
        // appears as a receiver but has no runtime value — use a dummy zeroinitializer.
        string receiver;
        TypeInfo? receiverType;
        if (member.Object is IdentifierExpression typeId &&
            !_localVariables.ContainsKey(key: typeId.Name) &&
            ResolveTypeNameAsReceiver(name: typeId.Name) is { } typeAsReceiver)
        {
            receiverType = typeAsReceiver;
            string llvmType = GetLLVMType(type: receiverType);
            receiver = llvmType.StartsWith(value: '%') || llvmType.StartsWith(value: '{')
                ?
                "zeroinitializer"
                : llvmType == "ptr"
                    ? "null"
                    : "0";
        }
        else
        {
            receiver = EmitExpression(sb: sb, expr: member.Object);
            receiverType = GetExpressionType(expr: member.Object);
        }

        if (receiverType == null)
        {
            string objDesc = member.Object switch
            {
                IdentifierExpression id => $"identifier '{id.Name}'",
                CallExpression c when c.Callee is MemberExpression m2 =>
                    $"call .{m2.PropertyName}() (ResolvedType={c.ResolvedType?.Name ?? "null"})",
                _ => member.Object.GetType().Name
            };
            throw new InvalidOperationException(
                message: $"Cannot determine receiver type for method call .{member.PropertyName} on {objDesc}");
        }

        // WrapperTypeInfo (e.g., Snatched[Byte]) has FullName="Snatched[Core.Byte]" (Module=null,
        // inner FullName used for type args) which LookupMethod can't resolve and emits a wrong
        // mangled name. Always normalize to the real RecordTypeInfo (FullName="Core.Snatched[Byte]")
        // so both LookupMethod and LLVM name mangling work correctly.
        if (receiverType is WrapperTypeInfo wrapperReceiver)
        {
            TypeInfo? wrapperDef = _registry.LookupType(name: wrapperReceiver.Name);
            if (wrapperDef is { IsGenericDefinition: true } &&
                wrapperReceiver.TypeArguments is { Count: > 0 })
            {
                receiverType = _registry.GetOrCreateResolution(genericDef: wrapperDef,
                    typeArguments: wrapperReceiver.TypeArguments);
            }
        }

        // Transparent protocol (e.g. Referring[Text] with no declared methods): dispatch through
        // the first concrete type argument T. Both representations are ptr in LLVM, so no cast needed.
        if (receiverType is ProtocolTypeInfo transparentProto &&
            transparentProto.Methods.Count == 0 && transparentProto.TypeArguments is { Count: > 0 })
        {
            receiverType = transparentProto.TypeArguments[index: 0];
        }

        // Look up the method — prefer resolved routine from semantic analysis (P1)
        // Strip '!' suffix from failable method calls (e.g., invalidate!() → invalidate)
        bool isFailableMethodCall = member.PropertyName.EndsWith(value: '!');
        string methodName = isFailableMethodCall
            ? member.PropertyName[..^1]
            : member.PropertyName;
        RoutineInfo? method = resolvedRoutine ??
                              _registry.LookupMethod(type: receiverType,
                                  methodName: methodName,
                                  isFailable: isFailableMethodCall);

        // Fallback: if the method name has no '!' but the registry only has a failable variant
        // (e.g., OperatorLoweringPass emitted "$add" for a type whose method is "$add!"),
        // retry with isFailable:null to find it regardless of failable flag.
        if (method == null && !isFailableMethodCall)
        {
            method = _registry.LookupMethod(type: receiverType,
                methodName: methodName,
                isFailable: null);
        }

        // Representable pattern: obj.Text() → Text.$create(from: obj)
        // When the method name matches a registered type and no direct method exists,
        // route to TypeName.$create(from: receiver).
        // Strip '!' suffix for failable conversions (e.g., index.U64!() → U64)
        string conversionTypeName = member.PropertyName.EndsWith(value: '!')
            ? member.PropertyName[..^1]
            : member.PropertyName;
        if (method == null && arguments.Count == 0 &&
            _registry.LookupType(name: conversionTypeName) != null)
        {
            // For @llvm primitive types where the source is also primitive,
            // emit inline conversion (trunc/zext/sext/fpcast) instead of a function call.
            // e.g., val.Address() → inline zext/trunc.
            // Non-primitive sources (e.g., Text.S32!()) must go through $create.
            TypeInfo? targetType = _registry.LookupType(name: conversionTypeName);
            if (targetType is RecordTypeInfo { HasDirectBackendType: true } &&
                receiverType is RecordTypeInfo { HasDirectBackendType: true })
            {
                return EmitPrimitiveTypeConversion(sb: sb,
                    targetTypeName: conversionTypeName,
                    arg: member.Object,
                    targetType: targetType);
            }

            string creatorName = $"{conversionTypeName}.$create";
            var argTypes2 = new List<TypeInfo> { receiverType };
            RoutineInfo? creator =
                _registry.LookupRoutineOverload(baseName: creatorName, argTypes: argTypes2);
            if (creator != null)
            {
                // Method-level generics: infer T from argument types and monomorphize
                if (creator.IsGenericDefinition)
                {
                    Dictionary<string, TypeInfo>? inferred =
                        InferMethodTypeArgs(genericMethod: creator, argTypes: argTypes2);
                    if (inferred != null)
                    {
                        IEnumerable<string> resolvedParamNames =
                            argTypes2.Select(selector: t => t.Name);
                        string creatorMangledName = Q(name: DecorateRoutineSymbolName(
                            baseName:
                            $"{creator.OwnerType?.FullName ?? conversionTypeName}.$create({string.Join(separator: ",", values: resolvedParamNames)})",
                            isFailable: creator.IsFailable));

                        GenerateFunctionDeclaration(routine: creator);
                        RecordMonomorphization(mangledName: creatorMangledName,
                            genericMethod: creator,
                            resolvedOwnerType: creator.OwnerType ?? receiverType,
                            methodTypeArgs: inferred);

                        string retType = creator.ReturnType != null
                            ? GetLLVMType(type: creator.ReturnType)
                            : "ptr";
                        string receiverLlvm = GetLLVMType(type: receiverType);
                        string result = NextTemp();
                        EmitLine(sb: sb,
                            line:
                            $"  {result} = call {retType} @{creatorMangledName}({receiverLlvm} {receiver})");
                        if (creator.AsyncStatus == AsyncStatus.Threaded)
                        {
                            return EmitThreadedTaskSpawn(sb: sb,
                                routine: creator,
                                calleeName: creatorMangledName,
                                argValues: [receiver],
                                argTypes: [receiverLlvm],
                                argTypeInfos: [receiverType]);
                        }

                        return creator.IsAsync
                            ? EmitAsyncCompletedTask(sb: sb, routine: creator, rawResult: result)
                            : result;
                    }
                }

                // Owner-level generics: resolve from argument types and monomorphize.
                // e.g., List[T].$create(from: SortedSet[T]) called with SortedSet[S64]
                //        → infer T=S64, emit List[S64].$create#SortedSet[S64]
                if (creator.OwnerType is
                        { IsGenericDefinition: true, GenericParameters: not null } &&
                    receiverType.IsGenericResolution && receiverType.TypeArguments != null)
                {
                    // Match the argument's type args against the creator param's type args to infer owner generics
                    TypeInfo paramType = creator.Parameters[index: 0].Type;
                    if (paramType is { IsGenericResolution: true, TypeArguments: not null } &&
                        paramType.TypeArguments.Count == receiverType.TypeArguments.Count)
                    {
                        var ownerSubs = new Dictionary<string, TypeInfo>();
                        for (int i = 0; i < paramType.TypeArguments.Count; i++)
                        {
                            if (paramType.TypeArguments[index: i] is GenericParameterTypeInfo gp)
                            {
                                ownerSubs[key: gp.Name] = receiverType.TypeArguments[index: i];
                            }
                        }

                        // Resolve the owner type (e.g., List[T] → List[S64])
                        var resolvedOwnerArgs = new List<TypeInfo>();
                        foreach (string gp in creator.OwnerType.GenericParameters)
                        {
                            if (ownerSubs.TryGetValue(key: gp, value: out TypeInfo? resolved))
                            {
                                resolvedOwnerArgs.Add(item: resolved);
                            }
                            else
                            {
                                break;
                            }
                        }

                        if (resolvedOwnerArgs.Count == creator.OwnerType.GenericParameters.Count)
                        {
                            TypeInfo resolvedOwner =
                                _registry.GetOrCreateResolution(genericDef: creator.OwnerType,
                                    typeArguments: resolvedOwnerArgs);
                            string resolvedFuncName = Q(name: DecorateRoutineSymbolName(
                                baseName: $"{resolvedOwner.FullName}.$create({receiverType.Name})",
                                isFailable: creator.IsFailable));

                            GenerateFunctionDeclaration(routine: creator);
                            RecordMonomorphization(mangledName: resolvedFuncName,
                                genericMethod: creator,
                                resolvedOwnerType: resolvedOwner);

                            string retType2 = "ptr";
                            string receiverLlvm2 = GetLLVMType(type: receiverType);
                            string result2 = NextTemp();
                            EmitLine(sb: sb,
                                line:
                                $"  {result2} = call {retType2} @{resolvedFuncName}({receiverLlvm2} {receiver})");
                            if (creator.AsyncStatus == AsyncStatus.Threaded)
                            {
                                return EmitThreadedTaskSpawn(sb: sb,
                                    routine: creator,
                                    calleeName: resolvedFuncName,
                                    argValues: [receiver],
                                    argTypes: [receiverLlvm2],
                                    argTypeInfos: [receiverType]);
                            }

                            return creator.IsAsync
                                ? EmitAsyncCompletedTask(sb: sb,
                                    routine: creator,
                                    rawResult: result2)
                                : result2;
                        }
                    }
                }

                // Non-generic path
                string funcName = MangleFunctionName(routine: creator);
                GenerateFunctionDeclaration(routine: creator);
                string retType3 = creator.ReturnType != null
                    ? GetLLVMType(type: creator.ReturnType)
                    : "ptr";

                string receiverLlvm3 = GetLLVMType(type: receiverType);
                string result3 = NextTemp();
                EmitLine(sb: sb,
                    line:
                    $"  {result3} = call {retType3} @{funcName}({receiverLlvm3} {receiver})");

                if (creator.AsyncStatus == AsyncStatus.Threaded)
                {
                    return EmitThreadedTaskSpawn(sb: sb,
                        routine: creator,
                        calleeName: funcName,
                        argValues: [receiver],
                        argTypes: [receiverLlvm3],
                        argTypeInfos: [receiverType]);
                }

                return creator.IsAsync
                    ? EmitAsyncCompletedTask(sb: sb, routine: creator, rawResult: result3)
                    : result3;
            }
        }

        // For zero-argument methods on entity/record types, if the method name matches a field,
        // emit as a direct field access (common pattern: List[T].count() returns me.count)
        // Also applies when method is a generic definition that can't be monomorphized.
        if ((method == null || method.IsGenericDefinition) && arguments.Count == 0)
        {
            if (receiverType is EntityTypeInfo entity &&
                entity.MemberVariables.Any(predicate: mv => mv.Name == member.PropertyName))
            {
                return EmitEntityMemberVariableRead(sb: sb,
                    entityPtr: receiver,
                    entity: entity,
                    memberVariableName: member.PropertyName);
            }

            if (receiverType is RecordTypeInfo record &&
                record.MemberVariables.Any(predicate: mv => mv.Name == member.PropertyName))
            {
                return EmitRecordMemberVariableRead(sb: sb,
                    recordValue: receiver,
                    record: record,
                    memberVariableName: member.PropertyName);
            }
        }

        // Build argument list: receiver first, then explicit arguments
        var argValues = new List<string> { receiver };
        var argTypes = new List<string> { GetParameterLLVMType(type: receiverType) };
        var argTypeInfos = new List<TypeInfo> { receiverType };

        foreach (Expression arg in arguments)
        {
            string value = EmitExpression(sb: sb, expr: arg);
            argValues.Add(item: value);

            TypeInfo? argType = GetExpressionType(expr: arg);
            if (argType == null)
            {
                throw new InvalidOperationException(
                    message:
                    $"Cannot determine type for argument in method call to '{member.PropertyName}'");
            }

            argTypeInfos.Add(item: argType);
            argTypes.Add(item: GetLLVMType(type: argType));
        }

        // C105: Pack variadic arguments into List[T]
        if (method is { IsVariadic: true })
        {
            PackVariadicArgs(sb: sb,
                routine: method,
                arguments: arguments,
                argValues: ref argValues,
                argTypes: ref argTypes,
                argOffset: 1);
        }

        // Method-level generics on regular method calls (e.g., method has [T] on itself, not the owner)
        // Infer type args from concrete argument types and monomorphize.
        // Skip when typeArguments != null — caller provided explicit type args (from GenericCallLoweringPass).
        Dictionary<string, TypeInfo>? inferredMethodTypeArgs = null;
        if (method != null && method.IsGenericDefinition && arguments.Count > 0 && typeArguments == null)
        {
            // Only pass explicit argument types — method.Parameters excludes implicit 'me',
            // so including receiverType would cause an off-by-one mismatch
            var concreteArgTypes = new List<TypeInfo>();
            foreach (Expression arg in arguments)
            {
                TypeInfo? t = GetExpressionType(expr: arg);
                if (t != null)
                {
                    concreteArgTypes.Add(item: t);
                }
            }

            inferredMethodTypeArgs =
                InferMethodTypeArgs(genericMethod: method, argTypes: concreteArgTypes);
        }

        // Build the call — for resolved generic types (e.g., List[Character].add_last),
        // use the resolved type name even if the method was found via the base type
        string mangledName;
        if (typeArguments is { Count: > 0 } && method != null)
        {
            // Explicit type-argument generic call (lowered from GenericMethodCallExpression by
            // GenericCallLoweringPass). Mirror EmitRegularGenericMethodCall mangling:
            // "ReceiverType.MethodName[T1, T2]" — no parameter suffix, no '!' in name part.
            var ownerParams = new HashSet<string>(
                method.OwnerType?.GenericParameters ?? (IEnumerable<string>)[]);
            var methodLevelParams = method.GenericParameters?
                                          .Where(predicate: gp => !ownerParams.Contains(item: gp))
                                          .ToList() ?? [];

            var resolvedTypeArgNames = new List<string>();
            var methodTypeArgMap = new Dictionary<string, TypeInfo>();
            for (int i = 0; i < methodLevelParams.Count && i < typeArguments.Count; i++)
            {
                string taName = typeArguments[i].Name;
                TypeInfo? resolved = null;
                if (_typeSubstitutions?.TryGetValue(key: taName, value: out TypeInfo? sub) == true)
                    resolved = sub;
                resolved ??= _registry.LookupType(name: taName);
                if (resolved != null)
                {
                    methodTypeArgMap[key: methodLevelParams[index: i]] = resolved;
                    resolvedTypeArgNames.Add(item: resolved.Name);
                }
                else
                {
                    resolvedTypeArgNames.Add(item: taName);
                }
            }

            string typeArgsSuffix = resolvedTypeArgNames.Count > 0
                ? $"[{string.Join(separator: ", ", values: resolvedTypeArgNames)}]"
                : "";
            string methodNamePart = SanitizeLLVMName(name: member.PropertyName) + typeArgsSuffix;
            mangledName = Q(name: $"{receiverType.FullName}.{methodNamePart}");

            if (receiverType.IsGenericResolution || method.OwnerType is GenericParameterTypeInfo)
            {
                RecordMonomorphization(mangledName: mangledName,
                    genericMethod: method,
                    resolvedOwnerType: receiverType,
                    methodTypeArgs: methodTypeArgMap.Count > 0 ? methodTypeArgMap : null);
            }
        }
        else if (method != null && inferredMethodTypeArgs != null)
        {
            // Method-level generics: use concrete explicit argument types in mangled name
            // (receiver is already encoded in ownerName — do NOT include it in the param suffix)
            var resolvedParamNames = new List<string>();
            foreach (Expression arg in arguments)
            {
                TypeInfo? t = GetExpressionType(expr: arg);
                if (t != null)
                {
                    resolvedParamNames.Add(item: t.Name);
                }
            }

            string ownerName = method.OwnerType?.FullName ?? receiverType.FullName;
            mangledName = Q(name: DecorateRoutineSymbolName(
                baseName:
                $"{ownerName}.{SanitizeLLVMName(name: method.Name)}({string.Join(separator: ",", values: resolvedParamNames)})",
                isFailable: method.IsFailable));
            RecordMonomorphization(mangledName: mangledName,
                genericMethod: method,
                resolvedOwnerType: receiverType,
                methodTypeArgs: inferredMethodTypeArgs);
        }
        else if (method != null)
        {
            // Delegate to ResolveMethod for generic resolution, generic-param owner, and standard cases
            ResolvedMethod? resolved = ResolveMethod(receiverType: receiverType,
                methodName: method.Name,
                isFailable: method.IsFailable);
            mangledName = resolved?.MangledName ??
                Q(name: DecorateRoutineSymbolName(
                    baseName: $"{receiverType.FullName}.{SanitizeLLVMName(name: member.PropertyName)}",
                    isFailable: method.IsFailable));
        }
        else
        {
            mangledName = Q(name: DecorateRoutineSymbolName(
                baseName: $"{receiverType.FullName}.{SanitizeLLVMName(name: member.PropertyName)}",
                isFailable: isFailableMethodCall));
        }

        // Ensure the method is declared (so the multi-pass stdlib loop can compile its body)
        // Skip for protocol-owned methods — they can't be declared with protocol types in LLVM IR;
        // the monomorphized version (with concrete receiver type) will generate its own declaration.
        if (method != null && method.OwnerType is not ProtocolTypeInfo)
        {
            GenerateFunctionDeclaration(routine: method);
        }

        if (method?.AsyncStatus == AsyncStatus.Threaded)
        {
            return EmitThreadedTaskSpawn(sb: sb,
                routine: method,
                calleeName: mangledName,
                argValues: argValues,
                argTypes: argTypes,
                argTypeInfos: argTypeInfos);
        }

        // Track protocol dispatch calls for stub generation
        if (method?.OwnerType is ProtocolTypeInfo protoDispatch)
        {
            _pendingProtocolDispatches.TryAdd(key: mangledName,
                value: new ProtocolDispatchInfo(Protocol: protoDispatch, MethodName: method.Name));

            // Append the runtime type_id as the last argument so the dispatch stub can
            // switch to the right concrete implementation.
            // For "when is Protocol var" bindings, the type_id was saved in a dedicated alloca.
            // For other protocol-typed receivers, we fall back to 0 (unknown → unreachable).
            string typeIdToPass;
            if (member.Object is IdentifierExpression protocolVarExpr &&
                _protocolTypeIdAllocas.TryGetValue(key: protocolVarExpr.Name,
                    value: out string? typeIdAlloca))
            {
                string typeIdTemp = NextTemp();
                EmitLine(sb: sb, line: $"  {typeIdTemp} = load i64, ptr {typeIdAlloca}");
                typeIdToPass = typeIdTemp;
            }
            else
            {
                typeIdToPass = "0";
            }

            argValues.Add(item: typeIdToPass);
            argTypes.Add(item: "i64");
        }

        // Use the semantic-layer-resolved return type.
        // During monomorphization, apply _typeSubstitutions for stale AST metadata.
        TypeInfo? resolvedReturnType = method?.ReturnType;
        if (resolvedReturnType != null)
        {
            resolvedReturnType = ApplyTypeSubstitutions(type: resolvedReturnType);
        }

        // Universal method (OwnerType = GenericParameterTypeInfo "T"): substitute T → receiverType
        // in the return type (e.g., T.retain() -> Retained[T] with receiverType=Node → Retained[Node]).
        if (method?.OwnerType is GenericParameterTypeInfo universalOwnerParam &&
            resolvedReturnType is { IsGenericResolution: true, TypeArguments: not null })
        {
            resolvedReturnType = SubstituteGenericParamInType(
                type: resolvedReturnType,
                paramName: universalOwnerParam.Name,
                concreteType: receiverType);
        }

        // For resolved generic methods, also emit a declaration with the resolved name
        if (!_generatedFunctions.Contains(item: mangledName))
        {
            if (method != null)
            {
                GenerateFunctionDeclaration(routine: method, nameOverride: mangledName);
                // Protocol dispatch stubs take an extra i64 type_id parameter at the end.
                // GenerateFunctionDeclaration only uses the method's own Parameters, so the
                // stored declaration omits the type_id. Overwrite it with the full argTypes
                // (which already includes the appended i64) so declare and define match.
                if (method.OwnerType is ProtocolTypeInfo)
                {
                    string retType2 = resolvedReturnType != null
                        ? GetLLVMType(type: resolvedReturnType) : "void";
                    _rfFunctionDeclarations[key: mangledName] =
                        $"declare {retType2} @{mangledName}({string.Join(separator: ", ", values: argTypes)})";
                }
            }
            else
            {
                string retType = resolvedReturnType != null
                    ? GetLLVMType(type: resolvedReturnType)
                    : "void";
                _rfFunctionDeclarations[key: mangledName] =
                    $"declare {retType} @{mangledName}({string.Join(separator: ", ", values: argTypes)})";
                _generatedFunctions.Add(item: mangledName);
            }
        }

        string returnType = resolvedReturnType != null
            ? GetLLVMType(type: resolvedReturnType)
            : "void";

        if (returnType == "void")
        {
            string args = BuildCallArgs(types: argTypes, values: argValues);
            EmitLine(sb: sb, line: $"  call void @{mangledName}({args})");
            return method?.IsAsync == true
                ? EmitAsyncCompletedTask(sb: sb, routine: method, rawResult: "undef")
                : "undef";
        }
        else
        {
            string result = NextTemp();
            string args = BuildCallArgs(types: argTypes, values: argValues);
            EmitLine(sb: sb, line: $"  {result} = call {returnType} @{mangledName}({args})");

            return method?.IsAsync == true
                ? EmitAsyncCompletedTask(sb: sb, routine: method, rawResult: result)
                : result;
        }
    }

    /// <summary>
    /// Substitutes a generic parameter name with a concrete type in a type expression.
    /// Handles both direct substitution (T → Point) and nested resolution (Viewed[T] → Viewed[Point]).
    /// </summary>
    private TypeInfo SubstituteGenericParamInType(TypeInfo type, string paramName,
        TypeInfo concreteType)
    {
        // Direct match: T → Point
        if (type.Name == paramName || type is GenericParameterTypeInfo gp && gp.Name == paramName)
        {
            return concreteType;
        }

        // Nested resolution: Viewed[T] → Viewed[Point]
        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            bool anyChanged = false;
            var substitutedArgs = new List<TypeInfo>();
            foreach (TypeInfo arg in type.TypeArguments)
            {
                TypeInfo substituted = SubstituteGenericParamInType(type: arg,
                    paramName: paramName,
                    concreteType: concreteType);
                substitutedArgs.Add(item: substituted);
                if (!ReferenceEquals(objA: substituted, objB: arg))
                {
                    anyChanged = true;
                }
            }

            if (anyChanged)
            {
                TypeInfo? genericBase = GetGenericBase(type: type);
                if (genericBase != null)
                {
                    return _registry.GetOrCreateResolution(genericDef: genericBase,
                        typeArguments: substitutedArgs);
                }

                // WrapperTypeInfo has no GenericDefinition — resolve via RecordTypeInfo def.
                if (type is WrapperTypeInfo)
                {
                    TypeInfo? wrapperRecordDef = _registry.LookupType(name: type.Name);
                    if (wrapperRecordDef is { IsGenericDefinition: true })
                        return _registry.GetOrCreateResolution(genericDef: wrapperRecordDef,
                            typeArguments: substitutedArgs);
                }
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

        return string.Join(separator: ", ",
            values: types.Select(selector: (t, i) => $"{t} {values[index: i]}"));
    }

}
