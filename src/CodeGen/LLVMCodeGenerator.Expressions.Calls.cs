namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Symbols;
using SemanticAnalysis.Types;
using SyntaxTree;

/// <summary>
/// Expression code generation for routine calls and compound assignment.
/// </summary>
public partial class LLVMCodeGenerator
{
    private string EmitFunctionCall(StringBuilder sb, string functionName,
        List<Expression> arguments, RoutineInfo? resolvedRoutine = null)
    {
        // Strip failable '!' suffix — registry stores names without it
        if (functionName.EndsWith(value: '!'))
        {
            functionName = functionName[..^1];
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

        // Compiler intrinsics for heap memory management
        // TODO: This should be removed as they are going to be C native
        if (functionName is "heap_alloc" or "heap_free" or "heap_realloc")
        {
            var argVals = new List<string>();
            var argLlvmTypes = new List<string>();
            foreach (Expression arg in arguments)
            {
                argVals.Add(item: EmitExpression(sb: sb, expr: arg));
                TypeInfo? at = GetExpressionType(expr: arg);
                argLlvmTypes.Add(item: at != null
                    ? GetLLVMType(type: at)
                    : "i64");
            }

            switch (functionName)
            {
                case "heap_alloc":
                {
                    // heap_alloc(bytes) → rf_allocate_dynamic(i64 bytes) → returns ptr
                    string bytesVal = argVals[index: 0];
                    string result = NextTemp();
                    EmitLine(sb: sb,
                        line: $"  {result} = call ptr @rf_allocate_dynamic(i64 {bytesVal})");
                    // Convert ptr to i64 (Address) for caller
                    string asInt = NextTemp();
                    EmitLine(sb: sb, line: $"  {asInt} = ptrtoint ptr {result} to i64");
                    return asInt;
                }
                case "heap_free":
                {
                    // heap_free(ptr) → rf_invalidate(ptr)
                    string asPtr = NextTemp();
                    EmitLine(sb: sb, line: $"  {asPtr} = inttoptr i64 {argVals[index: 0]} to ptr");
                    EmitLine(sb: sb, line: $"  call void @rf_invalidate(ptr {asPtr})");
                    return "undef";
                }
                case "heap_realloc":
                {
                    // heap_realloc(ptr, new_size) → rf_reallocate_dynamic(ptr, i64)
                    string asPtr = NextTemp();
                    EmitLine(sb: sb, line: $"  {asPtr} = inttoptr i64 {argVals[index: 0]} to ptr");
                    string result = NextTemp();
                    EmitLine(sb: sb,
                        line:
                        $"  {result} = call ptr @rf_reallocate_dynamic(ptr {asPtr}, i64 {argVals[index: 1]})");
                    // Convert ptr back to i64 (Address)
                    string asInt = NextTemp();
                    EmitLine(sb: sb, line: $"  {asInt} = ptrtoint ptr {result} to i64");
                    return asInt;
                }
            }
        }

        // Use semantic analyzer's resolved routine if available (e.g., generic overload)
        // Otherwise look up the routine — try full name first, then short name fallback
        RoutineInfo? routine = resolvedRoutine ??
                               _registry.LookupRoutine(fullName: functionName) ??
                               _registry.LookupRoutineByName(name: functionName);

        // Generic free function monomorphization: when calling a generic instance (e.g., show[S64]),
        // record the monomorphization so the body is compiled with T=S64
        if (routine is { GenericDefinition: not null, TypeArguments: not null })
        {
            string monoName = MangleFunctionName(routine: routine);
            if (!_pendingMonomorphizations.ContainsKey(key: monoName))
            {
                RoutineInfo? genericDef = routine.GenericDefinition;
                var typeSubs = new Dictionary<string, TypeInfo>();
                for (int i = 0; i < genericDef.GenericParameters!.Count; i++)
                {
                    typeSubs[key: genericDef.GenericParameters[index: i]] =
                        (TypeInfo)routine.TypeArguments[index: i];
                }

                // Use [generic] marker so FindGenericAstRoutine skips non-generic overloads
                string genericAstName = $"{genericDef.Name}[generic]";
                _pendingMonomorphizations[key: monoName] = new MonomorphizationEntry(
                    GenericMethod: genericDef,
                    ResolvedOwnerType: null!,
                    TypeSubstitutions: typeSubs,
                    GenericAstName: genericAstName);
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
                    // (e.g., Letter(codepoint: val) where field is 'value')
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
            string expectedLlvm = GetLLVMType(type: routine.Parameters[index: 0].Type);
            if (argTypes[index: 0] != expectedLlvm)
            {
                RoutineInfo? genericOverload = _registry.LookupGenericOverload(name: routine.Name);
                if (genericOverload != null)
                {
                    TypeInfo? firstArgType = GetExpressionType(expr: arguments[index: 0]);
                    if (firstArgType != null && genericOverload.GenericParameters is
                            { Count: > 0 })
                    {
                        var typeArgs = new List<TypeInfo> { firstArgType };
                        // Pad with remaining generic params if needed (use same type)
                        for (int i = 1; i < genericOverload.GenericParameters.Count; i++)
                        {
                            typeArgs.Add(item: firstArgType);
                        }

                        routine = genericOverload.CreateInstance(typeArguments: typeArgs);

                        // Re-supply default arguments for the new routine
                        while (argValues.Count > arguments.Count)
                        {
                            argValues.RemoveAt(index: argValues.Count - 1);
                            argTypes.RemoveAt(index: argTypes.Count - 1);
                        }

                        for (int i = argValues.Count; i < routine.Parameters.Count; i++)
                        {
                            ParameterInfo param = routine.Parameters[index: i];
                            if (param.HasDefaultValue)
                            {
                                string value = EmitExpression(sb: sb, expr: param.DefaultValue!);
                                argValues.Add(item: value);
                                argTypes.Add(item: GetLLVMType(type: param.Type));
                            }
                        }

                        // Record monomorphization
                        string monoName = MangleFunctionName(routine: routine);
                        if (!_pendingMonomorphizations.ContainsKey(key: monoName))
                        {
                            var typeSubs = new Dictionary<string, TypeInfo>();
                            for (int i = 0; i < genericOverload.GenericParameters.Count; i++)
                            {
                                typeSubs[key: genericOverload.GenericParameters[index: i]] =
                                    (TypeInfo)routine.TypeArguments![index: i];
                            }

                            string genericAstName = $"{genericOverload.Name}[generic]";
                            _pendingMonomorphizations[key: monoName] = new MonomorphizationEntry(
                                GenericMethod: genericOverload,
                                ResolvedOwnerType: null!,
                                TypeSubstitutions: typeSubs,
                                GenericAstName: genericAstName);
                        }
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
            : SanitizeLLVMName(name: functionName);

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
                        ResolveSubstitutedType(type: routine.Parameters[index: 0].Type,
                            subs: _typeSubstitutions);
                    paramSuffix = $"#{resolvedParamType.Name}";
                }

                mangledName =
                    Q(name:
                        $"{resolvedOwnerName}.{SanitizeLLVMName(name: routine.Name)}{paramSuffix}");

                // Record monomorphization so the body gets generated
                if (!_pendingMonomorphizations.ContainsKey(key: mangledName) &&
                    !_generatedFunctionDefs.Contains(item: mangledName))
                {
                    string genericAstName = $"{routine.OwnerType.Name}.{routine.Name}";
                    _pendingMonomorphizations[key: mangledName] = new MonomorphizationEntry(
                        GenericMethod: routine,
                        ResolvedOwnerType: resolvedOwnerType,
                        TypeSubstitutions: _typeSubstitutions,
                        GenericAstName: genericAstName);
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

        string returnType = routine?.ReturnType != null
            ? GetLLVMType(type: routine.ReturnType)
            : "void";
        // Failable routines return Maybe[T] = { i64, ptr } at IR level
        bool isFailableCall = routine?.IsFailable == true;
        if (isFailableCall)
        {
            returnType = "{ i64, ptr }";
        }

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
            EmitLine(sb: sb, line: $"  {sretPtr} = alloca {returnType}");
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
            return "undef"; // No meaningful return value
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

            // Unwrap failable calls
            if (isFailableCall)
            {
                // Failable void routines: no value to unwrap
                if (routine?.ReturnType == null)
                {
                    return result;
                }

                return EmitEmittingCallUnwrap(sb: sb,
                    maybeResult: result,
                    valueType: routine?.ReturnType);
            }

            return result;
        }
    }

    /// <summary>
    /// Generates code for a method call on an object.
    /// The object becomes the implicit 'me' parameter.
    /// </summary>
    private string EmitMethodCall(StringBuilder sb, MemberExpression member,
        List<Expression> arguments, RoutineInfo? resolvedRoutine = null)
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
            throw new InvalidOperationException(
                message: "Cannot determine receiver type for method call");
        }

        // Look up the method — prefer resolved routine from semantic analysis (P1)
        // Strip '!' suffix from failable method calls (e.g., invalidate!() → invalidate)
        string methodName = member.PropertyName.EndsWith(value: '!')
            ? member.PropertyName[..^1]
            : member.PropertyName;
        string methodFullName = $"{receiverType.Name}.{methodName}";
        RoutineInfo? method = resolvedRoutine ?? _registry.LookupRoutine(fullName: methodFullName);
        // Also try with '!' suffix — failable methods may be registered as "name!" (e.g., $getitem!)
        if (method == null && !member.PropertyName.EndsWith(value: '!'))
        {
            method = _registry.LookupRoutine(fullName: $"{receiverType.Name}.{methodName}!");
        }

        if (method == null && member.PropertyName.EndsWith(value: '!'))
        {
            method = _registry.LookupRoutine(
                fullName: $"{receiverType.Name}.{member.PropertyName}");
        }

        // For generic type instances (e.g., List[Letter].count), try the generic base name
        if (method == null && GetGenericBaseName(type: receiverType) is { } baseName)
        {
            method = _registry.LookupRoutine(fullName: $"{baseName}.{methodName}") ??
                     _registry.LookupRoutine(fullName: $"{baseName}.{methodName}!");
        }

        // Fall back to LookupMethod which checks generic-parameter-owner methods (routine T.view())
        if (method == null)
        {
            method = _registry.LookupMethod(type: receiverType, methodName: methodName) ??
                     _registry.LookupMethod(type: receiverType, methodName: $"{methodName}!");
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
                        string creatorMangledName =
                            Q(name:
                                $"{creator.OwnerType?.FullName ?? conversionTypeName}.$create#{string.Join(separator: ",", values: resolvedParamNames)}");

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
                        return result;
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
                            string resolvedFuncName =
                                Q(name: $"{resolvedOwner.FullName}.$create#{receiverType.Name}");

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
                            return result2;
                        }
                    }
                }

                // Non-generic path
                string funcName = MangleFunctionName(routine: creator);
                GenerateFunctionDeclaration(routine: creator);
                string retType3 = creator.ReturnType != null
                    ? GetLLVMType(type: creator.ReturnType)
                    : "ptr";
                // Failable creators return Maybe[T] = { i64, ptr } at IR level
                if (creator.IsFailable)
                {
                    retType3 = "{ i64, ptr }";
                }

                string receiverLlvm3 = GetLLVMType(type: receiverType);
                string result3 = NextTemp();
                EmitLine(sb: sb,
                    line:
                    $"  {result3} = call {retType3} @{funcName}({receiverLlvm3} {receiver})");

                // Unwrap failable result (crash on failure in non-failable context)
                if (creator.IsFailable && creator.ReturnType != null)
                {
                    return EmitEmittingCallUnwrap(sb: sb,
                        maybeResult: result3,
                        valueType: creator.ReturnType);
                }

                return result3;
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
        // Infer type args from concrete argument types and monomorphize
        Dictionary<string, TypeInfo>? inferredMethodTypeArgs = null;
        if (method != null && method.IsGenericDefinition && arguments.Count > 0)
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

        // Build the call — for resolved generic types (e.g., List[Letter].add_last),
        // use the resolved type name even if the method was found via the base type
        string mangledName;
        if (method != null && inferredMethodTypeArgs != null)
        {
            // Method-level generics: use concrete param types in mangled name
            var resolvedParamNames = new List<string> { receiverType.Name };
            foreach (Expression arg in arguments)
            {
                TypeInfo? t = GetExpressionType(expr: arg);
                if (t != null)
                {
                    resolvedParamNames.Add(item: t.Name);
                }
            }

            string ownerName = method.OwnerType?.FullName ?? receiverType.FullName;
            mangledName =
                Q(name:
                    $"{ownerName}.{SanitizeLLVMName(name: method.Name)}#{string.Join(separator: ",", values: resolvedParamNames)}");
            RecordMonomorphization(mangledName: mangledName,
                genericMethod: method,
                resolvedOwnerType: receiverType,
                methodTypeArgs: inferredMethodTypeArgs);
        }
        else if (method != null && receiverType.IsGenericResolution && method.OwnerType != null &&
                 (method.OwnerType.IsGenericDefinition || method.OwnerType.IsGenericResolution))
        {
            mangledName =
                Q(name: $"{receiverType.FullName}.{SanitizeLLVMName(name: method.Name)}");
            // Record for monomorphization — will compile generic AST body with type substitutions
            RecordMonomorphization(mangledName: mangledName,
                genericMethod: method,
                resolvedOwnerType: receiverType);
        }
        else if (method != null && method.OwnerType is GenericParameterTypeInfo)
        {
            // Generic-parameter-owner methods (e.g., routine T.view() called on Point)
            // Monomorphize: Point.view with T=Point
            mangledName =
                Q(name: $"{receiverType.FullName}.{SanitizeLLVMName(name: method.Name)}");
            RecordMonomorphization(mangledName: mangledName,
                genericMethod: method,
                resolvedOwnerType: receiverType);
        }
        else
        {
            mangledName = method != null
                ? MangleFunctionName(routine: method)
                : Q(
                    name:
                    $"{receiverType.FullName}.{SanitizeLLVMName(name: member.PropertyName)}");
        }

        // Ensure the method is declared (so the multi-pass stdlib loop can compile its body)
        // Skip for protocol-owned methods — they can't be declared with protocol types in LLVM IR;
        // the monomorphized version (with concrete receiver type) will generate its own declaration.
        if (method != null && method.OwnerType is not ProtocolTypeInfo)
        {
            GenerateFunctionDeclaration(routine: method);
        }

        // Track protocol dispatch calls for stub generation
        if (method?.OwnerType is ProtocolTypeInfo protoDispatch)
        {
            _pendingProtocolDispatches.TryAdd(key: mangledName,
                value: new ProtocolDispatchInfo(Protocol: protoDispatch, MethodName: method.Name));
        }

        // Resolve return type — for generic resolutions, substitute type parameters (e.g., T → U8)
        TypeInfo? resolvedReturnType = method?.ReturnType;

        // For generic-parameter-owner methods (T.view() → Viewed[T]), substitute T with receiver type
        if (resolvedReturnType != null &&
            method?.OwnerType is GenericParameterTypeInfo genParamOwner)
        {
            resolvedReturnType = SubstituteGenericParamInType(type: resolvedReturnType,
                paramName: genParamOwner.Name,
                concreteType: receiverType);
        }

        // For protocol-owned methods (Iterable[T].enumerate() → EnumerateIterator[T]),
        // substitute protocol generic params using receiver's type arguments.
        // Use GenericDefinition to get the param names (resolved protocols have null GenericParameters).
        if (resolvedReturnType != null && method?.OwnerType is ProtocolTypeInfo protoOwner &&
            receiverType is { IsGenericResolution: true, TypeArguments: not null })
        {
            ProtocolTypeInfo protoGenDef = protoOwner.GenericDefinition ?? protoOwner;
            if (protoGenDef.GenericParameters is { Count: > 0 })
            {
                for (int pi = 0;
                     pi < protoGenDef.GenericParameters.Count &&
                     pi < receiverType.TypeArguments.Count;
                     pi++)
                {
                    resolvedReturnType = SubstituteGenericParamInType(type: resolvedReturnType,
                        paramName: protoGenDef.GenericParameters[index: pi],
                        concreteType: receiverType.TypeArguments[index: pi]);
                }
            }
        }

        if (resolvedReturnType is GenericParameterTypeInfo && receiverType is
                { IsGenericResolution: true, TypeArguments: not null })
        {
            TypeInfo? ownerGenericDef = receiverType switch
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
                if (paramIndex >= 0 && paramIndex < receiverType.TypeArguments.Count)
                {
                    resolvedReturnType = receiverType.TypeArguments[index: paramIndex];
                }
            }
        }

        // For return types that are generic resolutions with unresolved parameters
        // (e.g., ValueBitList[N] → ValueBitList[8]), substitute from the receiver's type arguments
        if (resolvedReturnType is { IsGenericResolution: true, TypeArguments: not null } &&
            receiverType is { IsGenericResolution: true, TypeArguments: not null })
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
                foreach (TypeInfo ta in resolvedReturnType.TypeArguments)
                {
                    if (ta is GenericParameterTypeInfo or ConstGenericValueTypeInfo)
                    {
                        int pi = ownerGenericDef.GenericParameters
                                                .ToList()
                                                .IndexOf(item: ta.Name);
                        if (pi >= 0 && pi < receiverType.TypeArguments.Count)
                        {
                            substArgs.Add(item: receiverType.TypeArguments[index: pi]);
                            needsSubst = true;
                            continue;
                        }
                    }

                    substArgs.Add(item: ta);
                }

                if (needsSubst)
                {
                    TypeInfo? retGenBase = GetGenericBase(type: resolvedReturnType);
                    if (retGenBase != null)
                    {
                        resolvedReturnType =
                            _registry.GetOrCreateResolution(genericDef: retGenBase,
                                typeArguments: substArgs);
                    }
                }
            }
        }

        // For resolved generic methods, also emit a declaration with the resolved name
        if (!_generatedFunctions.Contains(item: mangledName))
        {
            string retType = resolvedReturnType != null
                ? GetLLVMType(type: resolvedReturnType)
                : "void";
            // Emitting and failable routines return Maybe[T] = { i64, ptr } at IR level
            if (method?.AsyncStatus == AsyncStatus.Emitting || method?.IsFailable == true)
            {
                retType = "{ i64, ptr }";
            }

            EmitLine(sb: _functionDeclarations,
                line:
                $"declare {retType} @{mangledName}({string.Join(separator: ", ", values: argTypes)})");
            _generatedFunctions.Add(item: mangledName);
        }

        string returnType = resolvedReturnType != null
            ? GetLLVMType(type: resolvedReturnType)
            : "void";
        // Emitting and failable routines return Maybe[T] = { i64, ptr } at IR level
        bool isEmittingCall = method?.AsyncStatus == AsyncStatus.Emitting;
        bool isFailableCall = method?.IsFailable == true;
        if (isEmittingCall || isFailableCall)
        {
            returnType = "{ i64, ptr }";
        }

        if (returnType == "void")
        {
            string args = BuildCallArgs(types: argTypes, values: argValues);
            EmitLine(sb: sb, line: $"  call void @{mangledName}({args})");
            return "undef";
        }
        else
        {
            string result = NextTemp();
            string args = BuildCallArgs(types: argTypes, values: argValues);
            EmitLine(sb: sb, line: $"  {result} = call {returnType} @{mangledName}({args})");

            // Unwrap Maybe[T] from emitting/failable routine calls (C74).
            // EmitFor handles $next() unwrapping directly, so this only fires for
            // direct calls like `var item = me.source.$next()` inside emitting bodies.
            if (isEmittingCall || isFailableCall)
            {
                // Failable void routines: no value to unwrap, just discard the { i64, ptr }
                if (isFailableCall && resolvedReturnType == null)
                {
                    return result;
                }

                return EmitEmittingCallUnwrap(sb: sb,
                    maybeResult: result,
                    valueType: resolvedReturnType);
            }

            return result;
        }
    }

    /// <summary>
    /// Unwraps a Maybe[T] result from an emitting routine call (C74).
    /// Extracts the tag, propagates ABSENT if inside an emitting routine, and loads the value.
    /// </summary>
    private string EmitEmittingCallUnwrap(StringBuilder sb, string maybeResult,
        TypeInfo? valueType)
    {
        // Store Maybe { i64, ptr } to memory for field extraction
        string maybeAddr = NextTemp();
        EmitLine(sb: sb, line: $"  {maybeAddr} = alloca {{ i64, ptr }}");
        EmitLine(sb: sb, line: $"  store {{ i64, ptr }} {maybeResult}, ptr {maybeAddr}");

        // Extract tag (field 0 = DataState)
        string tagPtr = NextTemp();
        EmitLine(sb: sb,
            line: $"  {tagPtr} = getelementptr {{ i64, ptr }}, ptr {maybeAddr}, i32 0, i32 0");
        string tag = NextTemp();
        EmitLine(sb: sb, line: $"  {tag} = load i64, ptr {tagPtr}");

        // Branch: tag == 1 (VALID) → extract value, else → propagate ABSENT
        string isValid = NextTemp();
        EmitLine(sb: sb, line: $"  {isValid} = icmp eq i64 {tag}, 1");

        string validLabel = NextLabel(prefix: "emit_unwrap_valid");
        string absentLabel = NextLabel(prefix: "emit_unwrap_absent");
        EmitLine(sb: sb, line: $"  br i1 {isValid}, label %{validLabel}, label %{absentLabel}");

        // ABSENT branch: propagate absence or trap
        EmitLine(sb: sb, line: $"{absentLabel}:");
        if (_currentEmittingRoutine?.AsyncStatus == AsyncStatus.Emitting ||
            _currentRoutineIsFailable)
        {
            // Inside emitting or failable routine: propagate absence to caller
            EmitLine(sb: sb, line: "  call void @rf_trace_pop()");
            EmitLine(sb: sb, line: "  ret { i64, ptr } { i64 0, ptr null }");
        }
        else
        {
            // Non-failable caller: absence is a contract violation
            EmitLine(sb: sb, line: "  unreachable");
        }

        // VALID branch: extract handle pointer (field 1) and load the value
        EmitLine(sb: sb, line: $"{validLabel}:");
        _currentBlock = validLabel;

        string handlePtr = NextTemp();
        EmitLine(sb: sb,
            line: $"  {handlePtr} = getelementptr {{ i64, ptr }}, ptr {maybeAddr}, i32 0, i32 1");
        string handleVal = NextTemp();
        EmitLine(sb: sb, line: $"  {handleVal} = load ptr, ptr {handlePtr}");

        // For entity types, the value IS the pointer (no boxing) — return directly
        if (valueType is EntityTypeInfo)
        {
            return handleVal;
        }

        string unwrappedType = valueType != null
            ? GetLLVMType(type: valueType)
            : "i64";
        string unwrappedVal = NextTemp();
        EmitLine(sb: sb, line: $"  {unwrappedVal} = load {unwrappedType}, ptr {handleVal}");

        return unwrappedVal;
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
