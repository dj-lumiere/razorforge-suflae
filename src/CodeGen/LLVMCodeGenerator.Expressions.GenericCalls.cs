namespace Compiler.CodeGen;

using System.Text;
using SemanticVerification.Symbols;
using SemanticVerification.Types;
using SyntaxTree;

/// <summary>
/// Expression code generation for generic routine and member routine calls.
/// </summary>
public partial class LLVMCodeGenerator
{
    private string EmitGenericMethodCall(StringBuilder sb, GenericMethodCallExpression generic)
    {
        // Check for generic type constructor call: TypeName[T](args)
        // e.g., Snatched[U8](ptr_value) → just pass through the argument
        if (generic.Object is IdentifierExpression id && id.Name == generic.MethodName)
        {
            // Only use id.ResolvedType as a type-constructor fallback when it is an actual
            // data type (entity, record, etc.).  RoutineTypeInfo (set when the identifier
            // resolves to a free function like ptrtoint) and ErrorTypeInfo must be excluded;
            // both would pass the "as TypeInfo" cast but do not represent constructable types.
            TypeInfo? calledType = LookupTypeInCurrentModule(name: id.Name) ??
                                   (id.ResolvedType is not RoutineTypeInfo and not ErrorTypeInfo
                                       ? id.ResolvedType as TypeInfo
                                       : null);
            if (calledType != null)
            {
                // Resolve type arguments (apply type substitutions for monomorphization)
                var resolvedTypeArgs = new List<TypeInfo>();
                foreach (TypeExpression ta in generic.TypeArguments)
                {
                    TypeInfo? resolved = ResolveTypeArgument(ta: ta);
                    if (resolved != null)
                    {
                        resolvedTypeArgs.Add(item: resolved);
                    }
                }

                // Fallback: if ResolveTypeArgument missed some args (e.g., the rewritten name
                // "DictEntry[Core.S64, Core.S64]" is not yet in _resolutions), use
                // _typeSubstitutions directly by matching generic parameter names to values.
                // This handles stdlib constructor calls like List[T](data:...) inside
                // List[T].$create monomorphized for a concrete T.
                if (calledType.IsGenericDefinition &&
                    resolvedTypeArgs.Count < calledType.GenericParameters!.Count &&
                    _typeSubstitutions != null &&
                    generic.TypeArguments.Count == calledType.GenericParameters.Count)
                {
                    resolvedTypeArgs.Clear();
                    foreach (string paramName in calledType.GenericParameters)
                    {
                        if (_typeSubstitutions.TryGetValue(key: paramName, value: out TypeInfo? sub))
                            resolvedTypeArgs.Add(item: sub);
                    }
                }

                // Resolve the full generic type (e.g., List + [Character] → List[Character])
                TypeInfo resolvedFullType = calledType;
                if (calledType.IsGenericDefinition &&
                    resolvedTypeArgs.Count == calledType.GenericParameters!.Count)
                {
                    resolvedFullType = _registry.GetOrCreateResolution(genericDef: calledType,
                        typeArguments: resolvedTypeArgs);
                }

                if (generic.ResolvedRoutine != null)
                {
                    return EmitFunctionCall(sb: sb,
                        functionName: generic.ResolvedRoutine.Name,
                        arguments: generic.Arguments,
                        resolvedRoutine: generic.ResolvedRoutine,
                        resolvedReturnType: resolvedFullType);
                }

                // Multi-field construction: Type[T](val1, val2, ...) or Type[T](f1: val1, f2: val2, ...)
                // ExpressionLoweringPass strips NamedArgumentExpression wrappers from generic
                // method call args, so we must NOT require All(NamedArg) here. Positional args
                // (after stripping) are fine — EmitEntityConstruction/EmitRecordConstruction both
                // accept positional as well as named (NamedArgumentExpression) expressions.
                if (generic.Arguments.Count > 1 &&
                    resolvedFullType is EntityTypeInfo resolvedEntity)
                {
                    return EmitEntityConstruction(sb: sb,
                        entity: resolvedEntity,
                        arguments: generic.Arguments
                                          .Cast<Expression>()
                                          .ToList());
                }

                if (generic.Arguments.Count > 1 &&
                    resolvedFullType is RecordTypeInfo resolvedRecord)
                {
                    return EmitRecordConstruction(sb: sb,
                        record: resolvedRecord,
                        arguments: generic.Arguments
                                          .Cast<Expression>()
                                          .ToList());
                }

                // Single-field named construction: Type[T](field: val)
                // Only when the single named arg matches the entity/record's single field
                if (generic.Arguments.Count == 1 &&
                    generic.Arguments[index: 0] is NamedArgumentExpression singleNamed)
                {
                    if (resolvedFullType is EntityTypeInfo singleEntity &&
                        singleEntity.MemberVariables.Count == 1 &&
                        singleEntity.MemberVariables[index: 0].Name == singleNamed.Name)
                    {
                        return EmitEntityConstruction(sb: sb,
                            entity: singleEntity,
                            arguments: generic.Arguments
                                              .Cast<Expression>()
                                              .ToList());
                    }

                    if (resolvedFullType is RecordTypeInfo singleRecord &&
                        singleRecord.MemberVariables.Count == 1 &&
                        singleRecord.MemberVariables[index: 0].Name == singleNamed.Name)
                    {
                        return EmitRecordConstruction(sb: sb,
                            record: singleRecord,
                            arguments: generic.Arguments
                                              .Cast<Expression>()
                                              .ToList());
                    }
                }

                // For single-arg generic type calls, try $create first (e.g., List[T](capacity))
                if (generic.Arguments.Count == 1)
                {
                    // Try $create overload (e.g., List[SortedDict[S64, Text]].$create(capacity: U64))
                    TypeInfo? singleArgType = GetExpressionType(expr: generic.Arguments[index: 0]);
                    if (singleArgType != null)
                    {
                        var singleArgTypes = new List<TypeInfo> { singleArgType };
                        string createNameFull = $"{resolvedFullType.Name}.$create";
                        RoutineInfo? creator =
                            _registry.LookupRoutineOverload(baseName: createNameFull,
                                argTypes: singleArgTypes);
                        // Fall back to generic definition's $create
                        if (creator == null && calledType.IsGenericDefinition)
                        {
                            string createNameBase = $"{calledType.Name}.$create";
                            creator = _registry.LookupRoutineOverload(baseName: createNameBase,
                                argTypes: singleArgTypes);
                        }

                        if (creator != null)
                        {
                            // Unwrap named argument for emission
                            Expression argExpr =
                                generic.Arguments[index: 0] is NamedArgumentExpression namedArg
                                    ? namedArg.Value
                                    : (Expression)generic.Arguments[index: 0];
                            string argVal = EmitExpression(sb: sb, expr: argExpr);
                            string argLlvm = GetLLVMType(type: singleArgType);

                            // Use resolved type name for generic resolutions (same as zero-arg path)
                            string funcName;
                            string firstParamType = creator.Parameters.Count > 0
                                ? creator.Parameters[index: 0].Type.Name
                                : "";
                            if (resolvedFullType.IsGenericResolution)
                            {
                                funcName =
                                    Q(name:
                                        $"{resolvedFullType.FullName}.$create({firstParamType})");
                                RecordMonomorphization(mangledName: funcName,
                                    genericMethod: creator,
                                    resolvedOwnerType: resolvedFullType);
                            }
                            else
                            {
                                funcName = MangleFunctionName(routine: creator);
                            }

                            // Ensure declared
                            string retType = resolvedFullType is EntityTypeInfo
                                ? "ptr"
                                :
                                creator.ReturnType != null
                                    ?
                                    GetLLVMType(
                                        type: ResolveTypeSubstitution(type: creator.ReturnType))
                                    : "ptr";
                            if (!_generatedFunctions.Contains(item: funcName))
                            {
                                GenerateFunctionDeclaration(routine: creator,
                                    nameOverride: funcName);
                            }

                            string createResult = NextTemp();
                            EmitLine(sb: sb,
                                line:
                                $"  {createResult} = call {retType} @{funcName}({argLlvm} {argVal})");
                            return createResult;
                        }
                    }

                    // For @llvm types (like Snatched[T] → ptr), the constructor is identity
                    string argValue = EmitExpression(sb: sb, expr: generic.Arguments[index: 0]);
                    string targetType = GetLLVMType(type: resolvedFullType);
                    TypeInfo? argTypeForCast =
                        GetExpressionType(expr: generic.Arguments[index: 0]);
                    string argLlvmType = argTypeForCast != null
                        ? GetLLVMType(type: argTypeForCast)
                        : targetType;

                    // If types match, identity. Otherwise, cast.
                    if (argLlvmType == targetType)
                    {
                        return argValue;
                    }

                    // Single-field record wrapping a ptr (e.g., Viewed[T] = { ptr })
                    // Use insertvalue to wrap the argument into the struct
                    if (targetType.StartsWith(value: "%Record.") && argLlvmType == "ptr")
                    {
                        string result = NextTemp();
                        EmitLine(sb: sb,
                            line:
                            $"  {result} = insertvalue {targetType} zeroinitializer, ptr {argValue}, 0");
                        return result;
                    }

                    // inttoptr / ptrtoint / bitcast as needed
                    string cast = NextTemp();
                    if (targetType == "ptr" && argLlvmType != "ptr")
                    {
                        EmitLine(sb: sb,
                            line: $"  {cast} = inttoptr {argLlvmType} {argValue} to ptr");
                    }
                    else if (targetType != "ptr" && argLlvmType == "ptr")
                    {
                        EmitLine(sb: sb,
                            line: $"  {cast} = ptrtoint ptr {argValue} to {targetType}");
                    }
                    else
                    {
                        EmitLine(sb: sb,
                            line: $"  {cast} = bitcast {argLlvmType} {argValue} to {targetType}");
                    }

                    return cast;
                }

                // Zero-arg constructor: look up $create() or return zeroinitializer
                if (generic.Arguments.Count == 0)
                {
                    // Use the already-resolved type from above
                    TypeInfo resolvedType = resolvedFullType;

                    // Try to find $create() — first on resolved type, then on generic definition
                    string createName = $"{resolvedType.Name}.$create";
                    RoutineInfo? creator = _registry.LookupRoutineOverload(baseName: createName,
                        argTypes: new List<TypeInfo>());
                    // If we got a non-zero-arg overload, it's not what we want for zero-arg construction
                    if (creator != null && creator.Parameters.Count > 0)
                    {
                        creator = null;
                    }

                    // Fall back to generic definition's $create
                    if (creator == null)
                    {
                        string genCreateName = $"{calledType.Name}.$create";
                        creator = _registry.LookupRoutineOverload(baseName: genCreateName,
                            argTypes: new List<TypeInfo>());
                        if (creator != null && creator.Parameters.Count > 0)
                        {
                            creator = null;
                        }
                    }

                    if (creator != null)
                    {
                        // For resolved generic types, use the resolved mangled name
                        string funcName;
                        if (resolvedType.IsGenericResolution)
                        {
                            funcName = Q(name: $"{resolvedType.FullName}.$create");
                            // Record for monomorphization
                            RecordMonomorphization(mangledName: funcName,
                                genericMethod: creator,
                                resolvedOwnerType: resolvedType);
                        }
                        else
                        {
                            funcName = MangleFunctionName(routine: creator);
                        }

                        // Ensure declared
                        string retType = creator.ReturnType != null
                            ? GetLLVMType(type: creator.ReturnType)
                            : "ptr";
                        if (!_generatedFunctions.Contains(item: funcName))
                        {
                            GenerateFunctionDeclaration(routine: creator,
                                nameOverride: funcName);
                        }

                        string result = NextTemp();
                        EmitLine(sb: sb, line: $"  {result} = call {retType} @{funcName}()");
                        return result;
                    }

                    return "zeroinitializer";
                }
            }
        }

        // Compiler intrinsics for generic standalone functions
        string baseName = generic.Object is IdentifierExpression baseId
            ? baseId.Name
            : generic.MethodName;
        string typeArgName = generic.TypeArguments.Count > 0
            ? generic.TypeArguments[index: 0].Name
            : "";
        // Resolve type arg — check type substitutions first (for monomorphization), then registry
        TypeInfo? typeArg = null;
        if (typeArgName.Length > 0)
        {
            if (_typeSubstitutions != null &&
                _typeSubstitutions.TryGetValue(key: typeArgName, value: out TypeInfo? sub))
            {
                typeArg = sub;
            }

            typeArg ??= _registry.LookupType(name: typeArgName);
        }

        // rf_invalidate[T](ptr) → free the memory at the pointer
        if (baseName == "rf_invalidate" && generic.Arguments.Count == 1)
        {
            string addr = EmitExpression(sb: sb, expr: generic.Arguments[index: 0]);
            TypeInfo? addrType = GetExpressionType(expr: generic.Arguments[index: 0]);
            string addrLlvm = addrType != null
                ? GetLLVMType(type: addrType)
                : "ptr";
            if (addrLlvm == "ptr")
            {
                EmitLine(sb: sb, line: $"  call void @rf_invalidate(ptr {addr})");
            }
            else
            {
                string asPtr = NextTemp();
                EmitLine(sb: sb, line: $"  {asPtr} = inttoptr {addrLlvm} {addr} to ptr");
                EmitLine(sb: sb, line: $"  call void @rf_invalidate(ptr {asPtr})");
            }

            return "undef";
        }

        // rf_address_of[T](entity) → ptrtoint ptr to Address (i64)
        if (baseName == "rf_address_of" && generic.Arguments.Count == 1)
        {
            string val = EmitExpression(sb: sb, expr: generic.Arguments[index: 0]);
            string result = NextTemp();
            EmitLine(sb: sb, line: $"  {result} = ptrtoint ptr {val} to i64");
            return result;
        }

        // snatched_none[T]() → null pointer
        if (baseName == "snatched_none" && generic.Arguments.Count == 0)
        {
            return "null";
        }

        // Resolve the receiver type and look up the method
        TypeInfo? receiverType = GetExpressionType(expr: generic.Object);

        // Try method lookup on receiver type, or standalone lookup for free functions
        RoutineInfo? method = receiverType != null
            ? _registry.LookupMethod(type: receiverType, methodName: generic.MethodName)
            : null;
        method ??= _registry.LookupRoutine(fullName: generic.MethodName);

        // If this is an LLVM intrinsic, emit directly as LLVM IR
        if (method is { CallingConvention: "llvm" })
        {
            return EmitLlvmIntrinsicGenericCall(sb: sb, generic: generic, method: method);
        }

        // Otherwise, emit as a regular generic method call
        return EmitRegularGenericMethodCall(sb: sb,
            generic: generic,
            method: method,
            receiverType: receiverType);
    }

    /// <summary>
    /// Emits an LLVM intrinsic generic call by resolving type arguments to LLVM types
    /// and delegating to EmitLlvmInstruction.
    /// </summary>
    private string EmitLlvmIntrinsicGenericCall(StringBuilder sb,
        GenericMethodCallExpression generic, RoutineInfo method)
    {
        // Evaluate all arguments
        var args = new List<string>();
        foreach (Expression arg in generic.Arguments)
        {
            args.Add(item: EmitExpression(sb: sb, expr: arg));
        }

        // Also evaluate the receiver if this is a method call (it becomes 'me')
        string? receiver = null;
        if (generic.Object is not IdentifierExpression)
        {
            receiver = EmitExpression(sb: sb, expr: generic.Object);
        }

        // Resolve type arguments to LLVM types
        var llvmTypeArgs = new List<string>();
        foreach (TypeExpression typeArg in generic.TypeArguments)
        {
            llvmTypeArgs.Add(item: ResolveTypeExpressionToLLVM(typeExpr: typeArg));
        }

        if (llvmTypeArgs.Count == 0)
        {
            throw new InvalidOperationException(
                message: $"LLVM intrinsic call to '{generic.MethodName}' requires type arguments");
        }

        string llvmType = llvmTypeArgs[index: 0];

        // Get the LLVM instruction name (may differ from routine name via @llvm_ir annotation)
        string instrName = GetLlvmIrName(routine: method);

        // Build full arg list: receiver first if method call, then explicit args
        var allArgs = new List<string>();
        if (receiver != null)
        {
            allArgs.Add(item: receiver);
        }

        allArgs.AddRange(collection: args);

        // All LLVM intrinsics use template molds with {holes} for substitution
        return EmitFromTemplate(sb: sb,
            mold: instrName,
            method: method,
            llvmTypeArgs: llvmTypeArgs,
            args: allArgs);
    }

    /// <summary>
    /// Emits LLVM IR from a template mold string with {hole} substitution.
    /// Supports multi-line templates (for overflow intrinsics, etc.).
    /// </summary>
    /// <param name="sb">StringBuilder to emit code to.</param>
    /// <param name="mold">The template mold string with {holes}.</param>
    /// <param name="method">The routine info for generic parameter name resolution.</param>
    /// <param name="llvmTypeArgs">Resolved LLVM type arguments.</param>
    /// <param name="args">Emitted argument values.</param>
    /// <returns>The last {result} temp, or args[0] if no {result} in any line.</returns>
    private string EmitFromTemplate(StringBuilder sb, string mold, RoutineInfo method,
        List<string> llvmTypeArgs, List<string> args)
    {
        string[] lines =
            mold.Split(separator: '\n', options: StringSplitOptions.RemoveEmptyEntries);
        string? lastResult = null;
        string? prevResult = null;
        string? firstResult = null;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string currentResult = NextTemp();
            bool hasResult = line.Contains(value: "{result}");

            // Perform substitutions
            string substituted = line;

            // {result} → current SSA temp
            substituted = substituted.Replace(oldValue: "{result}", newValue: currentResult);

            // {prev} → previous line's {result}
            if (prevResult != null)
            {
                substituted = substituted.Replace(oldValue: "{prev}", newValue: prevResult);
            }

            // {first} → first line's {result} (useful for alloca patterns)
            if (firstResult != null)
            {
                substituted = substituted.Replace(oldValue: "{first}", newValue: firstResult);
            }

            // Named type parameters from GenericParameters: {T}, {From}, {To}, etc.
            // Must be done before parameter names to avoid collisions (e.g. {T} vs {type})
            if (method.GenericParameters != null)
            {
                for (int i = 0; i < method.GenericParameters.Count && i < llvmTypeArgs.Count; i++)
                {
                    string paramName = method.GenericParameters[index: i];
                    substituted = substituted.Replace(oldValue: $"{{{paramName}}}",
                        newValue: llvmTypeArgs[index: i]);

                    // {sizeof T} → byte size (only compute when pattern present)
                    string sizeofPattern = $"{{sizeof {paramName}}}";
                    if (substituted.Contains(value: sizeofPattern))
                    {
                        substituted = substituted.Replace(oldValue: sizeofPattern,
                            newValue: (GetTypeBitWidth(llvmType: llvmTypeArgs[index: i]) / 8)
                           .ToString());
                    }
                }
            }

            // Named parameter substitution: {paramName} → args[i]
            // Parameter names come from method.Parameters, args are positional
            for (int i = 0; i < method.Parameters.Count && i < args.Count; i++)
            {
                string paramName = method.Parameters[index: i].Name;
                substituted = substituted.Replace(oldValue: $"{{{paramName}}}",
                    newValue: args[index: i]);
            }

            EmitLine(sb: sb, line: $"  {substituted}");

            if (hasResult)
            {
                firstResult ??= currentResult;
                prevResult = currentResult;
                lastResult = currentResult;
            }
        }

        // Return last {result} temp, or first arg if no {result} in template
        return lastResult ?? (args.Count > 0
            ? args[index: 0]
            : "undef");
    }

    /// <summary>
    /// Emits a regular (non-LLVM-intrinsic) generic method call.
    /// Handles both single-generic (owner-level T) and double-generic (method-level U) calls.
    /// </summary>
    private string EmitRegularGenericMethodCall(StringBuilder sb,
        GenericMethodCallExpression generic, RoutineInfo? method, TypeInfo? receiverType)
    {
        // Evaluate the receiver
        string receiver = EmitExpression(sb: sb, expr: generic.Object);

        // Build argument list: receiver first (becomes 'me'), then explicit arguments
        var argValues = new List<string> { receiver };
        var argTypes = new List<string>
        {
            receiverType != null
                ? GetParameterLLVMType(type: receiverType)
                : "ptr"
        };

        foreach (Expression arg in generic.Arguments)
        {
            string value = EmitExpression(sb: sb, expr: arg);
            argValues.Add(item: value);

            TypeInfo? argType = GetExpressionType(expr: arg);
            if (argType == null)
            {
                throw new InvalidOperationException(
                    message:
                    $"Cannot determine type for argument in generic method call to '{generic.MethodName}'");
            }

            argTypes.Add(item: GetLLVMType(type: argType));
        }

        // Resolve method-level type arguments (e.g., U → S32)
        // method.GenericParameters may include both owner-level (T) and method-level (U) params.
        // generic.TypeArguments only contains method-level type args from the call site.
        // Skip owner-level params to match positionally with the call-site type args.
        var resolvedMethodTypeArgNames = new List<string>();
        Dictionary<string, TypeInfo>? methodTypeArgs = null;
        if (method?.GenericParameters != null && generic.TypeArguments.Count > 0)
        {
            // Determine which generic params are owner-level (from the type definition)
            var ownerGenericParams = new HashSet<string>();
            if (method.OwnerType?.GenericParameters != null)
            {
                foreach (string gp in method.OwnerType.GenericParameters)
                {
                    ownerGenericParams.Add(item: gp);
                }
            }

            // Only method-level params (not owner-level) should match the call-site type args
            var methodLevelParams = method.GenericParameters
                                          .Where(predicate: gp =>
                                               !ownerGenericParams.Contains(item: gp))
                                          .ToList();

            methodTypeArgs = new Dictionary<string, TypeInfo>();
            for (int i = 0; i < methodLevelParams.Count && i < generic.TypeArguments.Count; i++)
            {
                string taName = generic.TypeArguments[index: i].Name;
                TypeInfo? resolved = null;
                if (_typeSubstitutions != null &&
                    _typeSubstitutions.TryGetValue(key: taName, value: out TypeInfo? sub))
                {
                    resolved = sub;
                }

                resolved ??= _registry.LookupType(name: taName);
                if (resolved != null)
                {
                    methodTypeArgs[key: methodLevelParams[index: i]] = resolved;
                    resolvedMethodTypeArgNames.Add(item: resolved.Name);
                }
                else
                {
                    resolvedMethodTypeArgNames.Add(item: taName);
                }
            }
        }

        // Build the mangled name — include method type args for double-generic calls
        string methodNamePart = SanitizeLLVMName(name: generic.MethodName);
        if (resolvedMethodTypeArgNames.Count > 0)
        {
            methodNamePart +=
                $"[{string.Join(separator: ", ", values: resolvedMethodTypeArgNames)}]";
        }

        string mangledName;
        if (receiverType != null)
        {
            mangledName = Q(name: $"{receiverType.FullName}.{methodNamePart}");
        }
        else
        {
            mangledName = Q(name: methodNamePart);
        }

        // Record monomorphization for generic resolution types
        if (receiverType != null && method != null && (receiverType.IsGenericResolution ||
                                                       method.OwnerType is
                                                           GenericParameterTypeInfo))
        {
            RecordMonomorphization(mangledName: mangledName,
                genericMethod: method,
                resolvedOwnerType: receiverType,
                methodTypeArgs: methodTypeArgs);
        }

        // Use the semantic-layer-resolved return type.
        // During monomorphization, apply _typeSubstitutions for stale AST metadata.
        TypeInfo? resolvedReturnType = method?.ReturnType;
        if (resolvedReturnType != null)
        {
            resolvedReturnType = ApplyTypeSubstitutions(type: resolvedReturnType);
        }

        string returnType = resolvedReturnType != null
            ? GetLLVMType(type: resolvedReturnType)
            : "void";

        // Ensure the function is declared
        if (!_generatedFunctions.Contains(item: mangledName))
        {
            _rfFunctionDeclarations[key: mangledName] =
                $"declare {returnType} @{mangledName}({string.Join(separator: ", ", values: argTypes)})";
            _generatedFunctions.Add(item: mangledName);
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
            return result;
        }
    }

    /// <summary>
    /// Gets the LLVM IR instruction name for a routine, checking for @llvm_ir("name") annotation.
    /// Falls back to the routine name if no annotation is present.
    /// </summary>
    private static string GetLlvmIrName(RoutineInfo routine)
    {
        return routine.LlvmIrTemplate ?? routine.Name;
    }

    /// <summary>
    /// Resolves a TypeExpression (AST node) to its LLVM type string.
    /// </summary>
    private string ResolveTypeExpressionToLLVM(TypeExpression typeExpr)
    {
        // Apply type substitutions first (e.g., T → Character during monomorphization)
        if (_typeSubstitutions != null &&
            _typeSubstitutions.TryGetValue(key: typeExpr.Name, value: out TypeInfo? sub))
        {
            return GetLLVMType(type: sub);
        }

        // Look up the type in the registry
        TypeInfo? type = _registry.LookupType(name: typeExpr.Name);
        if (type != null)
        {
            // If this is a generic definition with generic arguments, resolve them
            if (type.IsGenericDefinition && typeExpr.GenericArguments is { Count: > 0 })
            {
                // Try full-name lookup first (handles const generic args like ValueBitList[8])
                string fullName =
                    $"{typeExpr.Name}[{string.Join(separator: ", ", values: typeExpr.GenericArguments.Select(selector: g => g.Name))}]";
                TypeInfo? fullType = _registry.LookupType(name: fullName);
                if (fullType != null)
                {
                    return GetLLVMType(type: fullType);
                }

                var resolvedArgs = new List<TypeInfo>();
                foreach (TypeExpression ga in typeExpr.GenericArguments)
                {
                    TypeInfo? resolved = null;
                    if (_typeSubstitutions != null &&
                        _typeSubstitutions.TryGetValue(key: ga.Name, value: out TypeInfo? gaSub))
                    {
                        resolved = gaSub;
                    }

                    resolved ??= _registry.LookupType(name: ga.Name);
                    if (resolved != null)
                    {
                        resolvedArgs.Add(item: resolved);
                    }
                }

                if (resolvedArgs.Count == type.GenericParameters!.Count)
                {
                    TypeInfo resolvedType =
                        _registry.GetOrCreateResolution(genericDef: type,
                            typeArguments: resolvedArgs);
                    return GetLLVMType(type: resolvedType);
                }
            }

            return GetLLVMType(type: type);
        }

        // Try module-qualified lookup (e.g., SortedDict → Collections.SortedDict)
        type = LookupTypeInCurrentModule(name: typeExpr.Name);
        if (type != null)
        {
            return GetLLVMType(type: type);
        }

        // During monomorphization, the AST rewriter may produce rewritten type names
        // (e.g., SortedDict[S64, S64]) that match a type substitution value
        if (_typeSubstitutions != null)
        {
            foreach (TypeInfo sub2 in _typeSubstitutions.Values)
            {
                if (sub2.Name == typeExpr.Name)
                {
                    return GetLLVMType(type: sub2);
                }
            }
        }

        // Fall back: return the name as-is (assumes it's already an LLVM type name)
        return typeExpr.Name;
    }

    /// <summary>
    /// Gets the return type of a generic method call expression.
    /// </summary>
    private TypeInfo? GetGenericMethodCallReturnType(GenericMethodCallExpression generic)
    {
        // Check for generic type constructor call: TypeName[T](args)
        // Parser produces GenericMethodCallExpression where Object and MethodName
        // are both the type name (e.g., Snatched[U8](x) → Object=Snatched, MethodName=Snatched)
        if (generic.Object is IdentifierExpression id && id.Name == generic.MethodName)
        {
            TypeInfo? calledType = LookupTypeInCurrentModule(name: id.Name);
            if (calledType != null)
            {
                // Resolve generic type arguments to get the concrete type (e.g., List[Character])
                if (calledType.IsGenericDefinition && generic.TypeArguments.Count > 0)
                {
                    var typeArgs = new List<TypeInfo>();
                    foreach (TypeExpression ta in generic.TypeArguments)
                    {
                        TypeInfo? resolved = ResolveTypeArgument(ta: ta);
                        resolved ??= _registry.LookupType(name: ta.Name);
                        if (resolved != null)
                        {
                            typeArgs.Add(item: resolved);
                        }
                    }

                    if (typeArgs.Count == calledType.GenericParameters!.Count)
                    {
                        calledType = _registry.GetOrCreateResolution(genericDef: calledType,
                            typeArguments: typeArgs);
                    }
                }

                return calledType;
            }
        }

        TypeInfo? receiverType = GetExpressionType(expr: generic.Object);

        RoutineInfo? method = receiverType != null
            ? _registry.LookupMethod(type: receiverType, methodName: generic.MethodName)
            : null;
        method ??= _registry.LookupRoutine(fullName: generic.MethodName);

        // Representable pattern: obj.TypeName[Args]() → type conversion returning the resolved generic type
        if (method == null)
        {
            TypeInfo? representableGenType = _registry.LookupType(name: generic.MethodName);
            if (representableGenType != null && representableGenType.IsGenericDefinition &&
                generic.TypeArguments.Count > 0)
            {
                var typeArgs = new List<TypeInfo>();
                foreach (TypeExpression ta in generic.TypeArguments)
                {
                    TypeInfo? resolved = null;
                    if (_typeSubstitutions != null &&
                        _typeSubstitutions.TryGetValue(key: ta.Name, value: out TypeInfo? sub))
                    {
                        resolved = sub;
                    }

                    resolved ??= _registry.LookupType(name: ta.Name);
                    if (resolved != null)
                    {
                        typeArgs.Add(item: resolved);
                    }
                }

                if (typeArgs.Count == representableGenType.GenericParameters!.Count)
                {
                    return _registry.GetOrCreateResolution(genericDef: representableGenType,
                        typeArguments: typeArgs);
                }
            }

            if (representableGenType != null)
            {
                return representableGenType;
            }

            return null;
        }

        // Build substitution map for generic parameters from call-site type args
        TypeInfo? returnType = method.ReturnType;
        if (returnType != null && method.GenericParameters is { Count: > 0 })
        {
            var callSubstitutions = new Dictionary<string, TypeInfo>();

            // Separate owner-level from method-level generic params
            var ownerGenericParams = new HashSet<string>();
            if (method.OwnerType?.GenericParameters != null)
            {
                foreach (string gp in method.OwnerType.GenericParameters)
                {
                    ownerGenericParams.Add(item: gp);
                }
            }

            var methodLevelParams = method.GenericParameters
                                          .Where(predicate: gp =>
                                               !ownerGenericParams.Contains(item: gp))
                                          .ToList();

            // Map method-level params to call-site type args
            for (int i = 0; i < methodLevelParams.Count && i < generic.TypeArguments.Count; i++)
            {
                string taName = generic.TypeArguments[index: i].Name;
                TypeInfo? resolved = null;
                if (_typeSubstitutions != null &&
                    _typeSubstitutions.TryGetValue(key: taName, value: out TypeInfo? sub))
                {
                    resolved = sub;
                }

                resolved ??= _registry.LookupType(name: taName);
                if (resolved != null)
                {
                    callSubstitutions[key: methodLevelParams[index: i]] = resolved;
                }
            }

            // Include owner-level substitutions from _typeSubstitutions
            if (_typeSubstitutions != null)
            {
                foreach ((string key, TypeInfo value) in _typeSubstitutions)
                {
                    callSubstitutions.TryAdd(key: key, value: value);
                }
            }

            // Also include owner-level substitutions from receiver type arguments
            if (receiverType is { IsGenericResolution: true, TypeArguments: not null })
            {
                TypeInfo? ownerGenericDef = receiverType switch
                {
                    RecordTypeInfo r => r.GenericDefinition,
                    EntityTypeInfo e => e.GenericDefinition,

                    _ => null
                };
                if (ownerGenericDef?.GenericParameters != null)
                {
                    for (int i = 0;
                         i < ownerGenericDef.GenericParameters.Count &&
                         i < receiverType.TypeArguments.Count;
                         i++)
                    {
                        callSubstitutions.TryAdd(key: ownerGenericDef.GenericParameters[index: i],
                            value: receiverType.TypeArguments[index: i]);
                    }
                }
            }

            // Case 1: Return type is directly a generic parameter (e.g., U → S64)
            if (callSubstitutions.TryGetValue(key: returnType.Name,
                    value: out TypeInfo? directSub))
            {
                returnType = directSub;
            }
            // Case 2: Return type is a generic resolution with unresolved params (e.g., Snatched[U])
            else if (returnType.IsGenericResolution && returnType.TypeArguments != null)
            {
                bool needsResolution = false;
                var resolvedArgs = new List<TypeInfo>();
                foreach (TypeInfo ta in returnType.TypeArguments)
                {
                    if (callSubstitutions.TryGetValue(key: ta.Name, value: out TypeInfo? argSub))
                    {
                        resolvedArgs.Add(item: argSub);
                        needsResolution = true;
                    }
                    else
                    {
                        resolvedArgs.Add(item: ta);
                    }
                }

                if (needsResolution)
                {
                    TypeInfo? genericBase = GetGenericBase(type: returnType);
                    if (genericBase != null)
                    {
                        returnType = _registry.GetOrCreateResolution(genericDef: genericBase,
                            typeArguments: resolvedArgs);
                    }
                }
            }
        }

        return returnType;
    }


    /// <summary>
    /// Resolves unsubstituted generic parameters in a type through _typeSubstitutions.
    /// E.g., during monomorphization with {U→S64}: Snatched[U] → Snatched[S64], U → S64.
    /// Also unconditionally converts WrapperTypeInfo to RecordTypeInfo so that LookupMethod
    /// and LLVM name mangling work correctly even in non-generic (non-monomorphized) contexts.
    /// </summary>
    private TypeInfo ApplyTypeSubstitutions(TypeInfo type)
    {
        // WrapperTypeInfo (e.g., Snatched[Byte]) must always be converted to the real RecordTypeInfo
        // (e.g., Core.Snatched[Byte]) so LookupMethod uses the right key and the LLVM mangled name
        // uses "Core.Snatched[Byte]" rather than "Snatched[Core.Byte]". This is needed even in
        // non-generic contexts (_typeSubstitutions == null) because the SA stores WrapperTypeInfo
        // as ResolvedType on call expressions even when the inner type is already concrete.
        if (type is WrapperTypeInfo wrapper)
        {
            TypeInfo? wrapperRecordDef = _registry.LookupType(name: wrapper.Name);
            if (wrapperRecordDef is { IsGenericDefinition: true } &&
                wrapper.TypeArguments is { Count: > 0 })
            {
                var resolvedArgs = _typeSubstitutions != null
                    ? wrapper.TypeArguments
                               .Select(selector: a => SubstituteTypeParams(type: a,
                                   substitutions: _typeSubstitutions))
                               .ToList()
                    : [.. wrapper.TypeArguments];
                return _registry.GetOrCreateResolution(genericDef: wrapperRecordDef,
                    typeArguments: resolvedArgs);
            }
        }

        if (_typeSubstitutions == null)
        {
            return type;
        }

        return SubstituteTypeParams(type: type, substitutions: _typeSubstitutions);
    }

    /// <summary>
    /// Recursively substitutes generic type parameters in a type using the given substitution map.
    /// Handles direct params (T → S64), generic resolutions (Snatched[T] → Snatched[S64]),
    /// nested generics (BTreeListNode[T] → BTreeListNode[S64]), and bare generic definitions
    /// (Snatched → Snatched[S64]).
    /// </summary>
    private TypeInfo SubstituteTypeParams(TypeInfo type, Dictionary<string, TypeInfo> substitutions)
    {
        // Direct generic parameter substitution (e.g., U → S64)
        if (substitutions.TryGetValue(key: type.Name, value: out TypeInfo? sub))
        {
            return sub;
        }

        // Generic resolution with unresolved params (e.g., Snatched[U] → Snatched[S64])
        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            bool needsResolution = false;
            var resolvedArgs = new List<TypeInfo>();
            foreach (TypeInfo ta in type.TypeArguments)
            {
                if (substitutions.TryGetValue(key: ta.Name, value: out TypeInfo? argSub))
                {
                    resolvedArgs.Add(item: argSub);
                    needsResolution = true;
                }
                else if (ta is { IsGenericResolution: true, TypeArguments: not null })
                {
                    // Recursively resolve nested generics (e.g., BTreeListNode[T] → BTreeListNode[S64])
                    TypeInfo innerResolved = SubstituteTypeParams(type: ta, substitutions: substitutions);
                    resolvedArgs.Add(item: innerResolved);
                    if (innerResolved != ta)
                    {
                        needsResolution = true;
                    }
                }
                else if (ta is { IsGenericDefinition: true, GenericParameters: not null }
                         and not EntityTypeInfo)
                {
                    // Generic definition with resolvable params (e.g., DictEntry in List[DictEntry]
                    // when substitutions has {K: S64, V: Text} → resolve to DictEntry[S64, Text]).
                    // Skip entity types — they are always ptr and resolution is unnecessary.
                    bool canResolve = true;
                    var innerArgs = new List<TypeInfo>();
                    foreach (string param in ta.GenericParameters)
                    {
                        if (substitutions.TryGetValue(key: param,
                                value: out TypeInfo? paramSub))
                        {
                            innerArgs.Add(item: paramSub);
                        }
                        else
                        {
                            canResolve = false;
                            break;
                        }
                    }

                    if (canResolve)
                    {
                        resolvedArgs.Add(item: _registry.GetOrCreateResolution(genericDef: ta,
                            typeArguments: innerArgs));
                        needsResolution = true;
                    }
                    else
                    {
                        resolvedArgs.Add(item: ta);
                    }
                }
                else
                {
                    resolvedArgs.Add(item: ta);
                }
            }

            if (needsResolution)
            {
                TypeInfo? genericBase = GetGenericBase(type: type);
                if (genericBase != null)
                {
                    return _registry.GetOrCreateResolution(genericDef: genericBase,
                        typeArguments: resolvedArgs);
                }
            }
        }

        // WrapperTypeInfo (e.g., Snatched[T] → Snatched[S64] when T→S64, or Snatched[S64] stays
        // Snatched[S64]). WrapperTypeInfo is used for member variable types when the stdlib loader
        // resolves Snatched[T] as a wrapper rather than the RecordTypeInfo generic resolution.
        // Always resolve to the real RecordTypeInfo so LookupMethod works correctly and the LLVM
        // mangled name uses "Core.Snatched[S64]" (from RecordTypeInfo.FullName) not
        // "Snatched[Core.S64]" (from WrapperTypeInfo.FullName with Module=null).
        if (type is WrapperTypeInfo wrapper)
        {
            TypeInfo resolvedInner = SubstituteTypeParams(type: wrapper.InnerType, substitutions: substitutions);
            TypeInfo? wrapperRecordDef = _registry.LookupType(name: wrapper.Name);
            if (wrapperRecordDef is { IsGenericDefinition: true })
            {
                return _registry.GetOrCreateResolution(genericDef: wrapperRecordDef,
                    typeArguments: new List<TypeInfo> { resolvedInner });
            }

            if (!ReferenceEquals(resolvedInner, wrapper.InnerType))
            {
                return new WrapperTypeInfo(wrapperName: wrapper.Name, innerType: resolvedInner,
                    isReadOnly: wrapper.IsReadOnly);
            }
        }

        // Generic definition with resolvable params at top level (e.g., List when substitutions
        // has {T: S64} → List[S64]). This happens when the semantic analyzer assigns a bare generic
        // definition as a variable's type instead of a generic resolution (e.g., return type of
        // conversion constructor in a generic body).
        if (type is { IsGenericDefinition: true, GenericParameters: not null })
        {
            bool canResolve = true;
            var resolvedArgs = new List<TypeInfo>();
            foreach (string param in type.GenericParameters)
            {
                if (substitutions.TryGetValue(key: param, value: out TypeInfo? paramSub))
                {
                    resolvedArgs.Add(item: paramSub);
                }
                else
                {
                    canResolve = false;
                    break;
                }
            }

            if (canResolve && resolvedArgs.Count > 0)
            {
                return _registry.GetOrCreateResolution(genericDef: type,
                    typeArguments: resolvedArgs);
            }
        }

        // Tuple types with unresolved generic params in element types (e.g., Tuple[U64, T] → Tuple[U64, S64])
        if (type is TupleTypeInfo tuple)
        {
            bool anyChanged = false;
            var resolvedElems = new List<TypeInfo>();
            foreach (TypeInfo elem in tuple.ElementTypes)
            {
                TypeInfo resolved = SubstituteTypeParams(type: elem, substitutions: substitutions);
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

        return type;
    }

}
