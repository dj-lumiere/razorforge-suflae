using Compiler.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Verification.Enums;

namespace Verification;

using TypeSymbol = TypeInfo;

public sealed partial class SemanticVerifier
{
    private TypeSymbol WrapAsyncRoutineReturnType(RoutineInfo routine, TypeSymbol returnType)
    {
        if (!routine.IsAsync)
        {
            return returnType;
        }

        TypeSymbol? taskDef = LookupTypeWithImports(name: "Task");
        if (taskDef is { IsGenericDefinition: true })
        {
            return _registry.GetOrCreateResolution(genericDef: taskDef, typeArguments: [returnType]);
        }

        return returnType;
    }

    private TypeSymbol AnalyzeCallExpression(CallExpression call)
    {
        switch (call.Callee)
        {
            // Get the callee type/routine
            case IdentifierExpression id:
            {
                bool isFailableCall = id.Name.EndsWith(value: '!');
                // Strip '!' suffix for failable calls (e.g., "stop!" → "stop")
                string callName = isFailableCall
                    ? id.Name[..^1]
                    : id.Name;
                TypeSymbol? callableType = LookupTypeWithImports(name: id.Name);
                if (callableType != null && call.TypeArguments is { Count: > 0 } typeArguments)
                {
                    var resolvedTypeArguments = new List<TypeSymbol>(capacity: typeArguments.Count);
                    foreach (TypeExpression typeArg in typeArguments)
                    {
                        resolvedTypeArguments.Add(item: ResolveType(typeExpr: typeArg));
                    }

                    if (callableType.IsGenericDefinition)
                    {
                        ValidateGenericConstraints(genericDef: callableType,
                            typeArgs: resolvedTypeArguments,
                            location: call.Location);
                        callableType = _registry.GetOrCreateResolution(genericDef: callableType,
                            typeArguments: resolvedTypeArguments.ToList());
                    }
                }

                // Wired routines ($-prefixed) cannot be called directly by user code, except
                // $represent and $diagnose which are composable for custom display implementations.
                if (callName.StartsWith(value: '$')
                    && callName != "$represent" && callName != "$diagnose")
                {
                    ReportError(code: SemanticDiagnosticCode.DirectWiredRoutineCall,
                        message: $"Wired routine '{callName}' cannot be called directly. " +
                                 "Use the corresponding language construct instead (e.g., '==' for $eq, 'for' for $iter).",
                        location: call.Location);
                    return ErrorTypeInfo.Instance;
                }

                RoutineInfo? routine = _registry.LookupRoutine(fullName: callName,
                    isFailable: isFailableCall);
                // Try current module prefix (e.g., "infinite_loop" → "HelloWorld.infinite_loop")
                if (routine == null && _currentModuleName != null && !callName.Contains(value: '.'))
                {
                    routine = _registry.LookupRoutine(fullName: $"{_currentModuleName}.{callName}",
                        isFailable: isFailableCall);
                }

                // Explicit type arguments on a generic routine call — monomorphize immediately so
                // that ResolvedType is concrete (e.g., signed_div[S32](...) → ReturnType = S32, not T).
                if (routine is { IsGenericDefinition: true } &&
                    call.TypeArguments is { Count: > 0 } routineExplicitTypeArgs &&
                    routine.GenericParameters?.Count == routineExplicitTypeArgs.Count)
                {
                    var resolvedTypeArguments = new List<TypeInfo>(capacity: routineExplicitTypeArgs.Count);
                    foreach (TypeExpression ta in routineExplicitTypeArgs)
                        resolvedTypeArguments.Add(item: ResolveType(typeExpr: ta));
                    RoutineInfo? monomorphized = _registry.GetOrCreateRoutineResolution(
                        genericDef: routine, typeArguments: resolvedTypeArguments);
                    if (monomorphized != null)
                        routine = monomorphized;
                }

                // Implicit type-argument inference for a generic routine call without explicit `[...]`.
                // Without this, callers like `set_byte_at(arr, 0, b)` keep the generic definition and
                // its return type stays `Array[Byte, N]`, breaking assignment/conversion checks.
                if (routine is { IsGenericDefinition: true } &&
                    (call.TypeArguments == null || call.TypeArguments.Count == 0) &&
                    routine.GenericParameters is { Count: > 0 } &&
                    call.Arguments.Count == routine.Parameters.Count)
                {
                    IReadOnlyList<TypeInfo>? inferred =
                        InferGenericTypeArguments(genericRoutine: routine,
                            arguments: call.Arguments);
                    if (inferred != null)
                    {
                        RoutineInfo? monomorphized = _registry.GetOrCreateRoutineResolution(
                            genericDef: routine, typeArguments: inferred);
                        if (monomorphized != null)
                            routine = monomorphized;
                    }
                }

                // Overload resolution: re-resolve when the initial lookup (first-wins by base name)
                // returns a routine with a different arity than the call site. This handles the case
                // where a zero-arg overload was registered first but the call has arguments, or where
                // a same-first-param overload was registered first but the call has different arity.
                if (routine != null && !routine.IsGenericDefinition && !routine.IsVariadic &&
                    call.Arguments.Count != routine.Parameters.Count)
                {
                    var arityArgTypes = new List<TypeSymbol>();
                    foreach (Expression arg in call.Arguments)
                    {
                        Expression actual = arg is NamedArgumentExpression nai ? nai.Value : arg;
                        TypeSymbol t = AnalyzeExpression(expression: actual);
                        if (t != ErrorTypeInfo.Instance) arityArgTypes.Add(item: t);
                    }
                    RoutineInfo? arityMatch =
                        _registry.LookupRoutineOverload(baseName: callName, argTypes: arityArgTypes);
                    if (arityMatch != null && arityMatch != routine)
                    {
                        routine = arityMatch;
                        call.ResolvedRoutine = routine;
                    }
                    else
                    {
                        RoutineInfo? generic =
                            _registry.LookupGenericOverload(name: callName,
                                preferredArity: call.Arguments.Count);
                        if (generic != null)
                        {
                            IReadOnlyList<TypeInfo>? inferred =
                                InferGenericTypeArguments(genericRoutine: generic,
                                    arguments: call.Arguments);
                            routine = inferred != null
                                ? generic.CreateInstance(typeArguments: inferred)
                                : generic;
                            call.ResolvedRoutine = routine;
                        }
                    }
                }

                // Overload resolution: if the found routine is non-generic and any
                // positional argument doesn't match the bound routine's parameter type,
                // try a specific or generic overload (e.g., show[T] or a ByteSize overload
                // when the U64 overload was first-bound).
                if (routine is { IsGenericDefinition: false } && call.Arguments.Count > 0 &&
                    routine.Parameters.Count == call.Arguments.Count)
                {
                    bool anyMismatch = false;
                    for (int i = 0; i < call.Arguments.Count; i++)
                    {
                        Expression argExpr = call.Arguments[index: i] is NamedArgumentExpression nax
                            ? nax.Value
                            : call.Arguments[index: i];
                        TypeSymbol at = AnalyzeExpression(expression: argExpr);
                        if (at == ErrorTypeInfo.Instance) continue;
                        TypeSymbol pt = routine.Parameters[index: i].Type;
                        if (at.FullName != pt.FullName && !IsAssignableTo(source: at, target: pt))
                        {
                            anyMismatch = true;
                            break;
                        }
                    }
                    if (anyMismatch)
                    {
                        // Collect all resolved arg types for better overload disambiguation
                        var resolvedArgTypes = new List<TypeSymbol>();
                        for (int i = 0; i < call.Arguments.Count; i++)
                        {
                            Expression actualArg =
                                call.Arguments[index: i] is NamedArgumentExpression nai
                                    ? nai.Value
                                    : call.Arguments[index: i];
                            TypeSymbol argType = AnalyzeExpression(expression: actualArg);
                            if (argType != ErrorTypeInfo.Instance)
                            {
                                resolvedArgTypes.Add(item: argType);
                            }
                        }

                        RoutineInfo? better =
                            _registry.LookupRoutineOverload(baseName: callName,
                                argTypes: resolvedArgTypes);
                        if (better != null && better != routine)
                        {
                            routine = better;
                            call.ResolvedRoutine = routine;
                        }
                        else
                        {
                            RoutineInfo? generic =
                                _registry.LookupGenericOverload(name: callName,
                                    preferredArity: call.Arguments.Count);
                            if (generic != null)
                            {
                                IReadOnlyList<TypeInfo>? inferred =
                                    InferGenericTypeArguments(genericRoutine: generic,
                                        arguments: call.Arguments);
                                routine = inferred != null
                                    ? generic.CreateInstance(typeArguments: inferred)
                                    : generic;
                                call.ResolvedRoutine = routine;
                            }
                        }
                    }
                }

                // Variadic fallback: if resolved routine is non-variadic but has too many args,
                // try a variadic generic overload (e.g., show("a","b","c") → show[T](values...: T))
                if (routine != null && !routine.IsVariadic &&
                    call.Arguments.Count > routine.Parameters.Count)
                {
                    RoutineInfo? variadicGeneric =
                        _registry.LookupVariadicGenericOverload(name: callName);
                    if (variadicGeneric != null)
                    {
                        IReadOnlyList<TypeInfo>? inferred =
                            InferGenericTypeArguments(genericRoutine: variadicGeneric,
                                arguments: call.Arguments);
                        routine = inferred != null
                            ? variadicGeneric.CreateInstance(typeArguments: inferred)
                            : variadicGeneric;
                        call.ResolvedRoutine = routine;
                    }
                }

                if (callableType != null && call.Arguments.Count > 0)
                {
                    var creatorArgTypes = new List<TypeSymbol>(capacity: call.Arguments.Count);
                    foreach (Expression arg in call.Arguments)
                    {
                        creatorArgTypes.Add(item: AnalyzeExpression(expression: arg));
                    }

                    RoutineInfo? creator = _registry.LookupMethodOverload(type: callableType,
                        methodName: "$create",
                        argTypes: creatorArgTypes);
                    creator ??= _registry.LookupRoutineOverload(
                        baseName: $"{callableType.FullName}.$create",
                        argTypes: creatorArgTypes);

                    if (creator != null && creator.Parameters.Count == creatorArgTypes.Count &&
                        !creator.Parameters.Any(predicate: p => p.IsVariadicParam))
                    {
                        call.ConstructedType = callableType;
                        call.LoweringKind = ClassifyConstruction(type: callableType,
                            isCollectionLiteral: call.IsCollectionLiteral);
                        call.ResolvedRoutine = creator;
                        return creator.ReturnType ?? callableType;
                    }
                }

                if (routine != null)
                {
                    call.ResolvedRoutine = routine;
                    call.LoweringKind = ClassifyStandaloneRoutineCall(routine: routine);

                    // Import-gating: BuilderService standalone routines require 'import BuilderService'
                    if (routine.IsSynthesized &&
                        BuilderInfoProvider.IsBuilderServiceStandalone(name: routine.Name) &&
                        !_importedModules.Contains(item: "BuilderService"))
                    {
                        ReportError(code: SemanticDiagnosticCode.BuilderServiceImportRequired,
                            message: $"'{routine.Name}()' requires 'import BuilderService'.",
                            location: call.Location);
                        return ErrorTypeInfo.Instance;
                    }

                    // Track failable calls for error handling variant generation
                    if (routine.IsFailable && _currentRoutine != null)
                    {
                        _currentRoutine.HasFailableCalls = true;

                        // Non-failable routine (except start/synthesized) cannot call failable routines
                        if (!_currentRoutine.IsFailable && _currentRoutine.Name != "start" &&
                            !_currentRoutine.IsSynthesized)
                        {
                            ReportWarning(code: SemanticWarningCode.UnhandledCrashableCall,
                                message:
                                $"Failable routine '{routine.Name}!' called without error handling. " +
                                "Use 'when' to match the result, '??' to provide a default, or make the enclosing routine failable (!).",
                                location: call.Location);
                        }
                    }

                    // Validate routine access
                    ValidateRoutineAccess(routine: routine, accessLocation: call.Location);

                    AnalyzeCallArguments(routine: routine,
                        arguments: call.Arguments,
                        location: call.Location);

                    // C29: Dispatch inference for varargs calls
                    call.ResolvedDispatch = InferDispatchStrategy(routine: routine, call: call);
                    if (call.ResolvedDispatch == DispatchStrategy.Runtime &&
                        _registry.Language == Language.RazorForge)
                    {
                        ReportError(code: SemanticDiagnosticCode.RuntimeDispatchNotSupported,
                            message: $"Runtime dispatch is not supported in RazorForge. " +
                                     $"All varargs arguments to '{routine.Name}' must be the same concrete type.",
                            location: call.Location);
                    }

                    // Validate exclusive token uniqueness (cannot pass same Grasped/Claimed twice)
                    ValidateExclusiveTokenUniqueness(arguments: call.Arguments,
                        location: call.Location);

                    // Return type is Blank if not specified (routines without explicit return type return Blank)
                    TypeSymbol returnType = routine.ReturnType ??
                                            _registry.LookupType(name: "Blank") ??
                                            ErrorTypeInfo.Instance;
                    return WrapAsyncRoutineReturnType(routine: routine, returnType: returnType);
                }

                // Could be a type creator
                TypeSymbol? type = callableType;
                if (type != null)
                {
                    call.ConstructedType = type;
                    call.LoweringKind = ClassifyConstruction(type: type,
                        isCollectionLiteral: call.IsCollectionLiteral);

                    // Analyze all arguments once before branching
                    var argTypes = new List<TypeSymbol>();
                    foreach (Expression arg in call.Arguments)
                    {
                        argTypes.Add(item: AnalyzeExpression(expression: arg));
                    }

                    // C95: Try $create overload match first
                    // e.g., BitList(capacity: 32u64) → BitList.$create(capacity: U64)
                    // e.g., BitList(32u64) → BitList.$create(capacity: U64) instead of collection literal
                    if (call.Arguments.Count > 0)
                    {
                        RoutineInfo? creator = _registry.LookupMethodOverload(type: type,
                            methodName: "$create",
                            argTypes: argTypes);
                        creator ??= _registry.LookupRoutineOverload(
                            baseName: $"{type.FullName}.$create",
                            argTypes: argTypes);

                        if (creator != null && creator.Parameters.Count == argTypes.Count &&
                            !creator.Parameters.Any(predicate: p => p.IsVariadicParam))
                        {
                            call.ResolvedRoutine = creator;
                            call.LoweringKind = ClassifyConstruction(type: type,
                                isCollectionLiteral: call.IsCollectionLiteral);
                            return creator.ReturnType ?? type;
                        }

                        // Entity types can only be constructed via $create — no fallback
                        if (type is EntityTypeInfo)
                        {
                            ReportError(code: SemanticDiagnosticCode.TypeNotCallable,
                                message: $"No matching '$create' overload found for entity type '{type.Name}' " +
                                         $"with {argTypes.Count} argument(s).",
                                location: call.Location);
                        }
                    }

                    // #115: Data boxing restrictions — certain types cannot be boxed to Data
                    if (id.Name == "Data" && argTypes.Count > 0)
                    {
                        TypeSymbol argType = argTypes[index: 0];
                        if ((IsCarrierType(type: argType) && !IsMaybeType(type: argType)) || argType is VariantTypeInfo
                                or WrapperTypeInfo { IsReadOnly: true } // Viewed, Inspected
                            || argType is WrapperTypeInfo wrapper && wrapper.InnerType != null &&
                            wrapper.Name is "Grasped" or "Claimed")
                        {
                            ReportError(code: SemanticDiagnosticCode.DataBoxingProhibited,
                                message: $"Type '{argType.Name}' cannot be boxed to Data. " +
                                         "Result, Lookup, variants, and access tokens (Viewed, Grasped, Inspected, Claimed) cannot be stored in Data.",
                                location: call.Location);
                        }

                        // #116: Nested Data flattening — Data(Data(x)) should warn
                        if (argType.Name == "Data")
                        {
                            ReportWarning(code: SemanticWarningCode.NestedDataWrapping,
                                message:
                                "Nested Data wrapping is redundant. Data(Data(x)) should be flattened to Data(x).",
                                location: call.Location);
                        }
                    }

                    // S510: Type creators with 3+ fields require all named arguments.
                    // W258: For 2 fields, naming is recommended but only emits a warning.
                    int memberCount = type switch
                    {
                        EntityTypeInfo e => e.MemberVariables.Count,
                        RecordTypeInfo r => r.MemberVariables.Count,
                        _ => 0
                    };
                    if (memberCount >= 3)
                    {
                        foreach (Expression arg in call.Arguments)
                        {
                            if (arg is not NamedArgumentExpression)
                            {
                                ReportError(code: SemanticDiagnosticCode.NamedArgumentRequired,
                                    message:
                                    $"Type '{id.Name}' has {memberCount} fields - all constructor arguments must be named.",
                                    location: arg.Location);
                            }
                        }
                    }
                    else if (memberCount == 2)
                    {
                        foreach (Expression arg in call.Arguments)
                        {
                            if (arg is not NamedArgumentExpression)
                            {
                                ReportWarning(code: SemanticWarningCode.NamedArgumentRecommended,
                                    message:
                                    $"Type '{id.Name}' has 2 fields - naming constructor arguments is recommended for clarity.",
                                    location: arg.Location);
                            }
                        }
                    }

                    ValidateExclusiveTokenUniqueness(arguments: call.Arguments,
                        location: call.Location);
                    return type;
                }

                // Try module-prefixed routine lookup (e.g., Core.normalize_duration)
                // This is done after type creator check to avoid shadowing type creators
                // with identically-named convenience functions (e.g., "routine U32(from: U8)")
                routine = LookupRoutineWithImports(name: callName);

                // Overload resolution for import-resolved routines (e.g., show[T] from IO/Console)
                if (routine != null && !routine.IsGenericDefinition && call.Arguments.Count > 0 &&
                    routine.Parameters.Count > 0)
                {
                    Expression firstArgImport =
                        call.Arguments[index: 0] is NamedArgumentExpression naImport
                            ? naImport.Value
                            : call.Arguments[index: 0];
                    TypeSymbol firstArgTypeImport = AnalyzeExpression(expression: firstArgImport);
                    TypeSymbol firstParamTypeImport = routine.Parameters[index: 0].Type;
                    if (firstArgTypeImport != ErrorTypeInfo.Instance &&
                        firstArgTypeImport.FullName != firstParamTypeImport.FullName &&
                        !IsAssignableTo(source: firstArgTypeImport, target: firstParamTypeImport))
                    {
                        // Collect all resolved arg types for better overload disambiguation
                        var resolvedArgTypesImport = new List<TypeSymbol> { firstArgTypeImport };
                        for (int i = 1; i < call.Arguments.Count; i++)
                        {
                            Expression actualArgImport =
                                call.Arguments[index: i] is NamedArgumentExpression naiImport
                                    ? naiImport.Value
                                    : call.Arguments[index: i];
                            TypeSymbol argTypeImport =
                                AnalyzeExpression(expression: actualArgImport);
                            if (argTypeImport != ErrorTypeInfo.Instance)
                            {
                                resolvedArgTypesImport.Add(item: argTypeImport);
                            }
                        }

                        // Try module-qualified specific overload (e.g., "IO.show#S64")
                        RoutineInfo? betterImport =
                            _registry.LookupRoutineOverload(baseName: routine.BaseName,
                                argTypes: resolvedArgTypesImport);
                        if (betterImport != null && betterImport != routine)
                        {
                            routine = betterImport;
                            call.ResolvedRoutine = routine;
                        }
                        else
                        {
                            RoutineInfo? genericImport =
                                _registry.LookupGenericOverload(name: callName);
                            if (genericImport != null)
                            {
                                IReadOnlyList<TypeInfo>? inferredImport =
                                    InferGenericTypeArguments(genericRoutine: genericImport,
                                        arguments: call.Arguments);
                                routine = inferredImport != null
                                    ? genericImport.CreateInstance(typeArguments: inferredImport)
                                    : genericImport;
                                call.ResolvedRoutine = routine;
                            }
                        }
                    }
                }

                // Variadic fallback for import-resolved routines
                if (routine != null && !routine.IsVariadic &&
                    call.Arguments.Count > routine.Parameters.Count)
                {
                    RoutineInfo? variadicGeneric =
                        _registry.LookupVariadicGenericOverload(name: callName);
                    if (variadicGeneric != null)
                    {
                        IReadOnlyList<TypeInfo>? inferred =
                            InferGenericTypeArguments(genericRoutine: variadicGeneric,
                                arguments: call.Arguments);
                        routine = inferred != null
                            ? variadicGeneric.CreateInstance(typeArguments: inferred)
                            : variadicGeneric;
                        call.ResolvedRoutine = routine;
                    }
                }

                if (routine != null)
                {
                    call.ResolvedRoutine = routine;
                    call.LoweringKind = ClassifyStandaloneRoutineCall(routine: routine);

                    // Import-gating: BuilderService standalone routines require 'import BuilderService'
                    if (routine.IsSynthesized &&
                        BuilderInfoProvider.IsBuilderServiceStandalone(name: routine.Name) &&
                        !_importedModules.Contains(item: "BuilderService"))
                    {
                        ReportError(code: SemanticDiagnosticCode.BuilderServiceImportRequired,
                            message: $"'{routine.Name}()' requires 'import BuilderService'.",
                            location: call.Location);
                        return ErrorTypeInfo.Instance;
                    }

                    // Track failable calls for error handling variant generation
                    if (routine.IsFailable && _currentRoutine != null)
                    {
                        _currentRoutine.HasFailableCalls = true;

                        if (!_currentRoutine.IsFailable && _currentRoutine.Name != "start" &&
                            !_currentRoutine.IsSynthesized)
                        {
                            ReportWarning(code: SemanticWarningCode.UnhandledCrashableCall,
                                message:
                                $"Failable routine '{routine.Name}!' called without error handling. " +
                                "Use 'when' to match the result, '??' to provide a default, or make the enclosing routine failable (!).",
                                location: call.Location);
                        }
                    }

                    ValidateRoutineAccess(routine: routine, accessLocation: call.Location);
                    AnalyzeCallArguments(routine: routine,
                        arguments: call.Arguments,
                        location: call.Location);

                    // C29: Dispatch inference for varargs calls
                    call.ResolvedDispatch = InferDispatchStrategy(routine: routine, call: call);
                    if (call.ResolvedDispatch == DispatchStrategy.Runtime &&
                        _registry.Language == Language.RazorForge)
                    {
                        ReportError(code: SemanticDiagnosticCode.RuntimeDispatchNotSupported,
                            message: $"Runtime dispatch is not supported in RazorForge. " +
                                     $"All varargs arguments to '{routine.Name}' must be the same concrete type.",
                            location: call.Location);
                    }

                    ValidateExclusiveTokenUniqueness(arguments: call.Arguments,
                        location: call.Location);

                    TypeSymbol returnType = routine.ReturnType ??
                                            _registry.LookupType(name: "Blank") ??
                                            ErrorTypeInfo.Instance;
                    return WrapAsyncRoutineReturnType(routine: routine, returnType: returnType);
                }

                break;
            }
            case MemberExpression member:
            {
                TypeSymbol objectType = AnalyzeExpression(expression: member.Object);

                // Choice types cannot use any operator wired methods
                if (objectType is ChoiceTypeInfo && IsOperatorWired(name: member.PropertyName))
                {
                    ReportError(code: SemanticDiagnosticCode.ArithmeticOnChoiceType,
                        message:
                        $"Operator '{member.PropertyName}' cannot be used with choice type '{objectType.Name}'. " +
                        "Choice types do not support operators. Use 'is' for case matching and regular methods for additional behavior.",
                        location: call.Location);
                    return ErrorTypeInfo.Instance;
                }

                // #134/#135: Flags types cannot use any operator wired methods
                if (objectType is FlagsTypeInfo && IsOperatorWired(name: member.PropertyName))
                {
                    ReportError(code: SemanticDiagnosticCode.ArithmeticOnFlagsType,
                        message:
                        $"Operator '{member.PropertyName}' cannot be used with flags type '{objectType.Name}'. " +
                        "Use 'but' to remove flags and 'is'/'isnot'/'isonly' to test flags.",
                        location: call.Location);
                    return ErrorTypeInfo.Instance;
                }

// #137: Nested grasping detection — checked before method resolution
                // since grasp() is generic extension T.grasp() that may not resolve by concrete type name
                if (member.PropertyName == "grasp" && IsNestedGrasping(source: member.Object))
                {
                    ReportError(code: SemanticDiagnosticCode.NestedHijackingNotAllowed,
                        message: "Cannot grasp a member of an already-grasped object. " +
                                 "Hijack the parent entity directly instead.",
                        location: call.Location);
                }

                bool isFailableMethodCall = member.PropertyName.EndsWith(value: '!');
                string callLookupName = isFailableMethodCall
                    ? member.PropertyName[..^1]
                    : member.PropertyName;
                TypeSymbol dispatchType = objectType;
                RoutineInfo? method =
                    _registry.LookupMethod(type: dispatchType,
                        methodName: callLookupName,
                        isFailable: isFailableMethodCall);

                // Phase D: Transparent wrapper forwarding — if the method isn't found directly on
                // the wrapper, synthesize a forwarder that delegates to the inner type's method
                // via `Hijacked[T](me).extract().method(...)`.
                if (method == null && IsWrapperType(type: dispatchType))
                {
                    method = TrySynthesizeWrapperForwarder(wrapperType: dispatchType,
                        methodName: callLookupName,
                        isFailable: isFailableMethodCall);
                }

                if (method == null &&
                    TryGetTransparentProtocolTarget(type: objectType, targetType: out TypeSymbol target))
                {
                    dispatchType = target;
                    method = _registry.LookupMethod(type: dispatchType,
                        methodName: callLookupName,
                        isFailable: isFailableMethodCall);
                }

                // Generic-parameter receiver: resolve via Obeys constraints from the current
                // routine and its owner type. e.g. `key.$hash()` where `K obeys Hashable`
                // dispatches through Hashable's protocol method.
                if (method == null && dispatchType is GenericParameterTypeInfo genParam)
                {
                    var constraints = ActiveConstraintsFor(paramName: genParam.Name).ToList();
                    method = _registry.LookupMethodViaConstraints(param: genParam,
                        methodName: callLookupName,
                        isFailable: isFailableMethodCall,
                        constraints: constraints);
                }

                if (method != null && !method.IsGenericDefinition && call.Arguments.Count > 0)
                {
                    var resolvedArgTypes = new List<TypeSymbol>(capacity: call.Arguments.Count);
                    foreach (Expression arg in call.Arguments)
                    {
                        Expression actualArg = arg is NamedArgumentExpression named ? named.Value : arg;
                        TypeSymbol argType = AnalyzeExpression(expression: actualArg);
                        if (argType != ErrorTypeInfo.Instance)
                        {
                            resolvedArgTypes.Add(item: argType);
                        }
                    }

                    bool arityMismatch = method.Parameters.Count != resolvedArgTypes.Count;
                    bool firstArgMismatch = !arityMismatch &&
                                            method.Parameters.Count > 0 &&
                                            resolvedArgTypes.Count > 0 &&
                                            !IsAssignableTo(source: resolvedArgTypes[0],
                                                target: method.Parameters[0].Type);

                    if (arityMismatch || firstArgMismatch)
                    {
                        RoutineInfo? betterMethod = _registry.LookupMethodOverload(type: dispatchType,
                            methodName: callLookupName,
                            argTypes: resolvedArgTypes);
                        if (betterMethod != null)
                        {
                            method = betterMethod;
                        }
                    }
                }

                if (method != null)
                {
                    call.LoweringKind = ClassifyMethodCall(method: method);

                    // Import-gating: BuilderService routines require 'import BuilderService'
                    if (method.IsSynthesized &&
                        BuilderInfoProvider.IsBuilderServiceRoutine(name: method.Name) &&
                        !_importedModules.Contains(item: "BuilderService"))
                    {
                        ReportError(code: SemanticDiagnosticCode.BuilderServiceImportRequired,
                            message: $"'{method.Name}()' requires 'import BuilderService'.",
                            location: call.Location);
                        return ErrorTypeInfo.Instance;
                    }

                    // Track failable calls for error handling variant generation
                    if (method.IsFailable && _currentRoutine != null)
                    {
                        _currentRoutine.HasFailableCalls = true;

                        if (!_currentRoutine.IsFailable && _currentRoutine.Name != "start" &&
                            !_currentRoutine.IsSynthesized)
                        {
                            ReportWarning(code: SemanticWarningCode.UnhandledCrashableCall,
                                message:
                                $"Failable routine '{method.Name}!' called without error handling. " +
                                "Use 'when' to match the result, '??' to provide a default, or make the enclosing routine failable (!).",
                                location: call.Location);
                        }
                    }

                    // #151: Static/instance mismatch — common routine called on instance.
                    // Generic type parameters (e.g., `T` inside `Dict[K, V]` body) are not
                    // registered as types but ARE valid receivers for common routines.
                    if (method.IsCommon && member.Object is IdentifierExpression instanceId &&
                        LookupTypeWithImports(name: instanceId.Name) == null &&
                        !IsGenericParameter(name: instanceId.Name))
                    {
                        ReportError(code: SemanticDiagnosticCode.CommonRoutineMismatch,
                            message:
                            $"Common routine '{method.Name}' must be called on the type '{objectType.Name}', not on an instance.",
                            location: call.Location);
                    }

                    // Validate method access
                    ValidateRoutineAccess(routine: method, accessLocation: call.Location);

                    if (!ReferenceEquals(objA: dispatchType, objB: objectType) &&
                        IsReadOnlyTransparentProtocol(type: objectType) && !method.IsReadOnly)
                    {
                        ReportError(code: SemanticDiagnosticCode.WritableMethodThroughReadOnlyWrapper,
                            message:
                            $"Cannot call writable method '{method.Name}' through read-only protocol '{objectType.Name}'. " +
                            "Use Controlling[T] or a writable token instead.",
                            location: call.Location);
                    }

                    // @readonly enforcement: cannot call modifying methods on 'me'
                    if (_currentRoutine is { IsReadOnly: true } &&
                        member.Object is IdentifierExpression { Name: "me" } && !method.IsReadOnly)
                    {
                        ReportError(code: SemanticDiagnosticCode.ModificationInReadonlyMethod,
                            message:
                            $"Cannot call non-readonly method '{method.Name}' on 'me' in a @readonly method. " +
                            "Mark the called method @readonly or use @migratable.",
                            location: call.Location);
                    }

                    // Preset enforcement: cannot call modifying methods on preset variables
                    if (member.Object is IdentifierExpression letTarget &&
                        method.ModificationCategory != ModificationCategory.Readonly)
                    {
                        VariableInfo? targetVar = _registry.LookupVariable(name: letTarget.Name);
                        if (targetVar is { IsModifiable: false })
                        {
                            ReportError(code: SemanticDiagnosticCode.ModifyingCallOnImmutable,
                                message:
                                $"Cannot call modifying method '{method.Name}' on preset variable '{letTarget.Name}'.",
                                location: call.Location);
                        }
                    }

                    AnalyzeCallArguments(routine: method,
                        arguments: call.Arguments,
                        location: call.Location,
                        callObjectType: dispatchType);

                    if (method.IsGenericDefinition)
                    {
                        IReadOnlyList<TypeInfo>? inferredMethodTypeArgs =
                            InferMethodGenericTypeArguments(genericMethod: method,
                                arguments: call.Arguments);
                        if (inferredMethodTypeArgs != null)
                        {
                            method = _registry.GetOrCreateRoutineResolution(genericDef: method,
                                typeArguments: inferredMethodTypeArgs);
                        }
                    }

                    // P1: Store fully resolved RoutineInfo (with owner-level generic substitution)
                    call.ResolvedRoutine = method;

                    // C29: Dispatch inference for varargs calls
                    call.ResolvedDispatch = InferDispatchStrategy(routine: method, call: call);
                    if (call.ResolvedDispatch == DispatchStrategy.Runtime &&
                        _registry.Language == Language.RazorForge)
                    {
                        ReportError(code: SemanticDiagnosticCode.RuntimeDispatchNotSupported,
                            message: $"Runtime dispatch is not supported in RazorForge. " +
                                     $"All varargs arguments to '{method.Name}' must be the same concrete type.",
                            location: call.Location);
                    }

                    // #68: Real-to-Complex promotion — only $add/$sub allow float↔complex cross-type
                    if (IsOperatorWired(name: member.PropertyName) &&
                        member.PropertyName is not ("$add" or "$sub" or "$iadd" or "$isub") &&
                        call.Arguments.Count > 0 && method.Parameters.Count > 0)
                    {
                        TypeSymbol argType = method.Parameters[index: 0].Type;
                        if (IsFloatType(type: objectType) && IsComplexType(type: argType) ||
                            IsComplexType(type: objectType) && IsFloatType(type: argType))
                        {
                            ReportError(code: SemanticDiagnosticCode.RealComplexPromotionInvalid,
                                message:
                                $"Operator '{member.PropertyName}' does not allow real↔complex promotion. " +
                                "Only '+' and '-' support implicit real-to-complex conversion. Use explicit conversion for other operators.",
                                location: call.Location);
                        }
                    }

                    // #12: Partial access rule — entity.field.view() is not allowed
                    if (member.PropertyName is "view" or "grasp" &&
                        member.Object is MemberExpression innerMember)
                    {
                        TypeSymbol innerObjectType =
                            innerMember.Object.ResolvedType ?? ErrorTypeInfo.Instance;
                        if (innerObjectType is EntityTypeInfo)
                        {
                            ReportError(code: SemanticDiagnosticCode.PartialAccessOnEntity,
                                message:
                                $"Cannot call '.{member.PropertyName}()' on entity member variable '{innerMember.PropertyName}'. " +
                                $"Access the entity directly instead of its individual member variables.",
                                location: call.Location);
                        }
                    }

                    // #137: Nested grasping detection
                    if (member.PropertyName == "grasp" && IsNestedGrasping(source: member.Object))
                    {
                        ReportError(code: SemanticDiagnosticCode.NestedHijackingNotAllowed,
                            message: "Cannot grasp a member of an already-grasped object. " +
                                     "Hijack the parent entity directly instead.",
                            location: call.Location);
                    }

                    // #92: Re-grasping prohibition — cannot grasp an already-grasped token
                    if (member.PropertyName == "grasp" && IsGraspedType(type: objectType))
                    {
                        ReportError(code: SemanticDiagnosticCode.ReHijackingProhibited,
                            message:
                            $"Cannot re-grasp an already-grasped token '{objectType.Name}'. " +
                            "The entity is already exclusively accessed.",
                            location: call.Location);
                    }

                    // #170: Downgrade prohibition — cannot call .view() on Grasped/Claimed
                    if (member.PropertyName == "view" && (IsGraspedType(type: objectType) ||
                                                          IsClaimedType(type: objectType)))
                    {
                        ReportError(code: SemanticDiagnosticCode.TokenDowngradeProhibited,
                            message: $"Cannot downgrade '{objectType.Name}' with '.view()'. " +
                                     "Grasped/Claimed tokens already have write access — use them directly.",
                            location: call.Location);
                    }

                    // #97: Hijacked[T] method calls require danger! block
                    if (IsHijacked(type: objectType) && !InDangerBlock)
                    {
                        ReportError(code: SemanticDiagnosticCode.HijackedRequiresDanger,
                            message:
                            "Method call on 'Hijacked[T]' type requires a 'danger!' block. " +
                            "Hijacked values bypass ownership safety checks.",
                            location: call.Location);
                    }

                    // #98: .hijack() on Shared/Marked requires danger! block
                    if (member.PropertyName == "hijack" && !InDangerBlock &&
                        (IsSharedType(type: objectType) || IsMarkedType(type: objectType)))
                    {
                        ReportError(code: SemanticDiagnosticCode.SnatchRequiresDanger,
                            message:
                            $"Calling '.hijack()' on '{objectType.Name}' requires a 'danger!' block. " +
                            "Snatching bypasses reference counting safety.",
                            location: call.Location);
                    }

                    // #100/#101: inspect!/claim! only valid on Shared entity handles
                    if (member.PropertyName is "inspect" or "claim" &&
                        !IsSharedType(type: objectType) && objectType is not ErrorTypeInfo)
                    {
                        ReportError(code: member.PropertyName == "inspect"
                                ? SemanticDiagnosticCode.InspectRequiresMultiRead
                                : SemanticDiagnosticCode.ReadOnlyRejectsLocking,
                            message: $"'{member.PropertyName}!()' is only valid on Shared handles. " +
                                     $"'{objectType.Name}' is not a Shared handle.",
                            location: call.Location);
                    }

                    // #19: Lock policy validation — inspect!/claim! must match the lock policy
                    if (member.PropertyName is "inspect" or "claim" &&
                        member.Object is IdentifierExpression lockPolicyTarget &&
                        _variableLockPolicies.TryGetValue(key: lockPolicyTarget.Name,
                            value: out string? policy))
                    {
                        if (member.PropertyName == "inspect" && policy == "Exclusive")
                        {
                            ReportError(code: SemanticDiagnosticCode.InspectRequiresMultiRead,
                                message:
                                $"Cannot use 'inspect!()' on '{lockPolicyTarget.Name}' — it uses Exclusive lock policy. " +
                                "Exclusive locks do not support concurrent readers. Use 'claim!()' instead.",
                                location: call.Location);
                        }

                        if (member.PropertyName == "claim" && policy == "ReadOnly")
                        {
                            ReportError(code: SemanticDiagnosticCode.ReadOnlyRejectsLocking,
                                message:
                                $"Cannot use 'claim!()' on '{lockPolicyTarget.Name}' — it uses ReadOnly lock policy. " +
                                "ReadOnly does not support exclusive write access. Use 'inspect!()' instead.",
                                location: call.Location);
                        }

                        if (member.PropertyName == "inspect" && policy == "ReadOnly")
                        {
                            ReportError(code: SemanticDiagnosticCode.ReadOnlyRejectsLocking,
                                message:
                                $"Cannot use 'inspect!()' on '{lockPolicyTarget.Name}' — it uses ReadOnly lock policy. " +
                                "ReadOnly data does not need locking — use '.view()' instead.",
                                location: call.Location);
                        }
                    }

                    // #22: Reject migratable operations on collection being iterated
                    if (member.Object is IdentifierExpression iterTarget &&
                        _activeIterationSources.Contains(item: iterTarget.Name) &&
                        method.ModificationCategory == ModificationCategory.Migratable)
                    {
                        ReportError(code: SemanticDiagnosticCode.MigratableDuringIteration,
                            message:
                            $"Cannot call migratable method '{method.Name}' on '{iterTarget.Name}' while iterating over it. " +
                            "Collect changes and apply them after the loop.",
                            location: call.Location);
                    }

                    // #47: .grasp() on @initonly record warns — record is frozen after construction
                    if (member.PropertyName == "grasp" && objectType is RecordTypeInfo)
                    {
                        // Check if the variable holding the record is @initonly bound
                        if (member.Object is IdentifierExpression graspTarget)
                        {
                            VariableInfo? targetVar =
                                _registry.LookupVariable(name: graspTarget.Name);
                            if (targetVar is { IsModifiable: false })
                            {
                                ReportWarning(code: SemanticWarningCode.HijackOnInitOnly,
                                    message:
                                    $"Calling '.grasp()' on @initonly-bound record '{graspTarget.Name}'. " +
                                    "The record is frozen after construction — grasping has no practical effect.",
                                    location: call.Location);
                            }
                        }
                    }

                    // #104/#23: Channel send() makes source variable a deadref
                    if (member.PropertyName == "send" &&
                        member.Object is IdentifierExpression sendSource)
                    {
                        string baseObjType = GetBaseTypeName(typeName: objectType.Name);
                        if (baseObjType == "Channel")
                        {
                            _deadrefVariables.Add(item: sendSource.Name);
                        }
                    }

                    // Validate exclusive token uniqueness (cannot pass same Grasped/Claimed twice)
                    ValidateExclusiveTokenUniqueness(arguments: call.Arguments,
                        location: call.Location);

                    // Return type is Blank if not specified
                    TypeSymbol? callReturnType = method.ReturnType;
                    if (callReturnType != null)
                    {
                        var substitutions = new Dictionary<string, TypeSymbol>();

                        // GenericParameterTypeInfo owner → map param name to receiver type
                        if (method.OwnerType is GenericParameterTypeInfo genParamOwner)
                        {
                            substitutions[key: genParamOwner.Name] = dispatchType;
                        }

                        // Protocol owner → map protocol generic params to receiver's type args
                        if (method.OwnerType is ProtocolTypeInfo protoOwner &&
                            dispatchType is { IsGenericResolution: true, TypeArguments: not null })
                        {
                            ProtocolTypeInfo protoGenDef = protoOwner.GenericDefinition ?? protoOwner;
                            if (protoGenDef.GenericParameters is { Count: > 0 })
                            {
                                for (int i = 0; i < protoGenDef.GenericParameters.Count &&
                                                i < dispatchType.TypeArguments.Count; i++)
                                {
                                    substitutions[key: protoGenDef.GenericParameters[index: i]] =
                                        dispatchType.TypeArguments[index: i];
                                }
                            }
                        }

                        if (substitutions.Count > 0)
                        {
                            callReturnType = SubstituteWithMapping(type: callReturnType,
                                substitutions: substitutions);
                        }
                    }

                    TypeSymbol returnType = callReturnType ??
                                            _registry.LookupType(name: "Blank") ??
                                            ErrorTypeInfo.Instance;
                    return WrapAsyncRoutineReturnType(routine: method, returnType: returnType);
                }

                // #78: Method-chain constructor — "42".S32!() → S32.$create!(from: "42")
                string propName = member.PropertyName;
                bool isFailable = propName.EndsWith(value: '!');
                string potentialTypeName = isFailable
                    ? propName[..^1]
                    : propName;

                TypeSymbol? targetType = LookupTypeWithImports(name: potentialTypeName);
                if (targetType != null)
                {
                    // Look up the creator on the target type, using overload resolution
                    // to match the object type (e.g., Text → S32.$create!(from_text: Text))
                    // Note: parser strips '!' from routine names — IsFailable is a separate flag.
                    // Always look up "$create" and check IsFailable on the result.
                    string creatorFullName = $"{targetType.FullName}.$create";
                    RoutineInfo? creator =
                        _registry.LookupRoutineOverload(baseName: creatorFullName,
                            argTypes: [objectType]);
                    // Fall back to default overload if no match by arg type
                    creator ??= _registry.LookupRoutine(fullName: creatorFullName);

                    if (creator != null)
                    {
                        call.ConstructedType = targetType;
                        call.LoweringKind = CallLoweringKind.TypeConstructor;

                        // Validate single non-me parameter
                        var nonMeParams = creator.Parameters
                                                 .Where(predicate: p => p.Name != "me")
                                                 .ToList();

                        if (nonMeParams.Count != 1)
                        {
                            ReportError(code: SemanticDiagnosticCode.MethodChainMultiArg,
                                message:
                                $"Method-chain constructor '{potentialTypeName}' requires exactly one non-'me' parameter, " +
                                $"but '$create' has {nonMeParams.Count}.",
                                location: call.Location);
                            return ErrorTypeInfo.Instance;
                        }

                        // Validate no extra args passed in the call
                        if (call.Arguments.Count > 0)
                        {
                            ReportError(code: SemanticDiagnosticCode.MethodChainMultiArg,
                                message:
                                $"Method-chain constructor '{potentialTypeName}' takes no additional arguments — " +
                                "the object itself is the argument.",
                                location: call.Location);
                            return ErrorTypeInfo.Instance;
                        }

                        // Type-check the object expression against the constructor parameter
                        if (!IsAssignableTo(source: objectType,
                                target: nonMeParams[index: 0].Type))
                        {
                            ReportError(code: SemanticDiagnosticCode.ArgumentTypeMismatch,
                                message:
                                $"Cannot convert '{objectType.Name}' to '{nonMeParams[index: 0].Type.Name}' " +
                                $"for method-chain constructor '{potentialTypeName}'.",
                                location: call.Location);
                        }

                        if (creator.IsFailable && _currentRoutine != null)
                        {
                            _currentRoutine.HasFailableCalls = true;

                            if (!_currentRoutine.IsFailable && _currentRoutine.Name != "start" &&
                                !_currentRoutine.IsSynthesized)
                            {
                                ReportWarning(code: SemanticWarningCode.UnhandledCrashableCall,
                                    message:
                                    $"Failable routine '{creator.Name}!' called without error handling. " +
                                    "Use 'when' to match the result, '??' to provide a default, or make the enclosing routine failable (!).",
                                    location: call.Location);
                            }
                        }

                        return targetType;
                    }
                }

                break;
            }
        }

        // Analyze callee expression (lambda or other callable)
        TypeSymbol calleeType = AnalyzeExpression(expression: call.Callee);

        // Analyze arguments
        foreach (Expression arg in call.Arguments)
        {
            AnalyzeExpression(expression: arg);
        }

        // Validate exclusive token uniqueness for dynamic calls too
        ValidateExclusiveTokenUniqueness(arguments: call.Arguments, location: call.Location);

        call.LoweringKind = CallLoweringKind.DynamicCall;

        // When the callee is a routine value (e.g. a parameter typed Routine[(T,T), Bool]),
        // the call's result type is the routine's return type, not the routine type itself.
        if (calleeType is RoutineTypeInfo routineType)
        {
            return routineType.ReturnType ?? _registry.LookupType(name: "Blank") ?? ErrorTypeInfo.Instance;
        }

        return calleeType;
    }

    private static CallLoweringKind ClassifyStandaloneRoutineCall(RoutineInfo routine)
    {
        if (routine.LlvmIrTemplate != null)
            return CallLoweringKind.LlvmIntrinsic;

        if (routine.IsSynthesized && BuilderInfoProvider.IsBuilderServiceStandalone(name: routine.Name))
            return CallLoweringKind.BuilderIntrinsic;

        return CallLoweringKind.DirectRoutine;
    }

    private static CallLoweringKind ClassifyMethodCall(RoutineInfo method)
    {
        if (method.LlvmIrTemplate != null)
            return CallLoweringKind.LlvmIntrinsic;

        if (method.IsSynthesized && BuilderInfoProvider.IsBuilderServiceRoutine(name: method.Name))
            return CallLoweringKind.BuilderIntrinsic;

        if (method.OwnerType is ProtocolTypeInfo or GenericParameterTypeInfo)
            return CallLoweringKind.RuntimeDispatch;

        return CallLoweringKind.DirectMemberRoutine;
    }

    private static CallLoweringKind ClassifyConstruction(TypeInfo type, bool isCollectionLiteral)
    {
        if (isCollectionLiteral)
            return CallLoweringKind.CollectionConstruction;

        return type is WrapperTypeInfo
            ? CallLoweringKind.WrapperConstruction
            : CallLoweringKind.TypeConstructor;
    }

    /// <summary>
    /// Infers dispatch strategy for a call site with protocol-constrained varargs.
    /// Returns null for non-varargs routines (always buildtime, no annotation needed).
    /// </summary>
    private static DispatchStrategy? InferDispatchStrategy(RoutineInfo routine, CallExpression call)
    {
        if (!routine.IsVariadic)
        {
            return null;
        }

        // Find the varargs parameter
        ParameterInfo? varargsParam =
            routine.Parameters.FirstOrDefault(predicate: p => p.IsVariadicParam);
        if (varargsParam == null)
        {
            return null;
        }

        // Unwrap List[T] to get element type T
        TypeSymbol paramType = varargsParam.Type;
        if (paramType is not { IsGenericResolution: true, TypeArguments: [var elementType, ..] })
        {
            return null;
        }

        // Only protocol-constrained varargs need dispatch inference
        // Generic-constrained (GenericParameterTypeInfo) and concrete types are always buildtime
        if (elementType is not ProtocolTypeInfo)
        {
            return DispatchStrategy.Buildtime;
        }

        // Collect resolved types of all varargs arguments
        int varargsIndex = varargsParam.Index;
        var varargsArgTypes = new List<TypeSymbol>();
        for (int i = varargsIndex; i < call.Arguments.Count; i++)
        {
            TypeSymbol? argType = call.Arguments[index: i].ResolvedType;
            if (argType != null && argType is not ErrorTypeInfo)
            {
                varargsArgTypes.Add(item: argType);
            }
        }

        if (varargsArgTypes.Count == 0)
        {
            return DispatchStrategy.Buildtime;
        }

        // All same concrete type → buildtime; mixed → runtime
        TypeSymbol firstType = varargsArgTypes[index: 0];
        bool allSame = varargsArgTypes.All(predicate: t => t.Name == firstType.Name);

        return allSame
            ? DispatchStrategy.Buildtime
            : DispatchStrategy.Runtime;
    }
}
