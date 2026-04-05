namespace SemanticAnalysis;

using Enums;
using Symbols;
using Types;
using SyntaxTree;
using Diagnostics;
using TypeSymbol = Types.TypeInfo;

public sealed partial class SemanticAnalyzer
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
        // Get the callee type/routine
        if (call.Callee is IdentifierExpression id)
        {
            bool isFailableCall = id.Name.EndsWith(value: '!');
            // Strip '!' suffix for failable calls (e.g., "stop!" → "stop")
            string callName = isFailableCall
                ? id.Name[..^1]
                : id.Name;

            // Wired routines ($-prefixed) cannot be called directly by user code
            if (callName.StartsWith(value: '$'))
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

            // Overload resolution: if the found routine is non-generic and the first
            // argument doesn't match, try a specific or generic overload (e.g., show[T])
            if (routine != null && !routine.IsGenericDefinition && call.Arguments.Count > 0 &&
                routine.Parameters.Count > 0)
            {
                Expression firstArg = call.Arguments[index: 0] is NamedArgumentExpression na
                    ? na.Value
                    : call.Arguments[index: 0];
                TypeSymbol firstArgType = AnalyzeExpression(expression: firstArg);
                TypeSymbol firstParamType = routine.Parameters[index: 0].Type;
                if (firstArgType != ErrorTypeInfo.Instance &&
                    firstArgType.FullName != firstParamType.FullName &&
                    !IsAssignableTo(source: firstArgType, target: firstParamType))
                {
                    // Collect all resolved arg types for better overload disambiguation
                    var resolvedArgTypes = new List<TypeSymbol> { firstArgType };
                    for (int i = 1; i < call.Arguments.Count; i++)
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
                        RoutineInfo? generic = _registry.LookupGenericOverload(name: callName);
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

            if (routine != null)
            {
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

                    // Non-failable routine (except start) cannot call failable routines
                    if (!_currentRoutine.IsFailable && _currentRoutine.Name != "start")
                    {
                        ReportError(code: SemanticDiagnosticCode.UnhandledCrashableCall,
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

                // Validate exclusive token uniqueness (cannot pass same Hijacked/Seized twice)
                ValidateExclusiveTokenUniqueness(arguments: call.Arguments,
                    location: call.Location);

                // Return type is Blank if not specified (routines without explicit return type return Blank)
                TypeSymbol returnType = routine.ReturnType ??
                                        _registry.LookupType(name: "Blank") ??
                                        ErrorTypeInfo.Instance;
                return WrapAsyncRoutineReturnType(routine: routine, returnType: returnType);
            }

            // Could be a type creator
            TypeSymbol? type = LookupTypeWithImports(name: id.Name);
            if (type != null)
            {
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
                    RoutineInfo? creator = _registry.LookupRoutineOverload(
                        baseName: $"{type.Name}.$create",
                        argTypes: argTypes);

                    // Fall back to generic definition's $create for resolved generic types
                    if ((creator == null || creator.Parameters.Count != argTypes.Count) &&
                        type.IsGenericResolution)
                    {
                        TypeSymbol? genDef = type switch
                        {
                            EntityTypeInfo e => e.GenericDefinition,
                            RecordTypeInfo r => r.GenericDefinition,
                            _ => null
                        };
                        if (genDef != null)
                        {
                            RoutineInfo? genCreator = _registry.LookupRoutineOverload(
                                baseName: $"{genDef.Name}.$create",
                                argTypes: argTypes);
                            if (genCreator != null &&
                                genCreator.Parameters.Count == argTypes.Count)
                            {
                                creator = genCreator;
                            }
                        }
                    }

                    if (creator != null && creator.Parameters.Count == argTypes.Count &&
                        !creator.Parameters.Any(predicate: p => p.IsVariadicParam))
                    {
                        call.ResolvedRoutine = creator;
                        return creator.ReturnType ?? type;
                    }
                }

                // #115: Data boxing restrictions — certain types cannot be boxed to Data
                if (id.Name == "Data" && argTypes.Count > 0)
                {
                    TypeSymbol argType = argTypes[index: 0];
                    if ((IsCarrierType(type: argType) && !IsMaybeType(type: argType)) || argType is VariantTypeInfo
                            or WrapperTypeInfo { IsReadOnly: true } // Viewed, Inspected
                        || argType is WrapperTypeInfo wrapper && wrapper.InnerType != null &&
                        wrapper.Name is "Hijacked" or "Seized")
                    {
                        ReportError(code: SemanticDiagnosticCode.DataBoxingProhibited,
                            message: $"Type '{argType.Name}' cannot be boxed to Data. " +
                                     "Result, Lookup, variants, and access tokens (Viewed, Hijacked, Inspected, Seized) cannot be stored in Data.",
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

                // S510: Type creators with 2+ fields require all named arguments
                int memberCount = type switch
                {
                    EntityTypeInfo e => e.MemberVariables.Count,
                    RecordTypeInfo r => r.MemberVariables.Count,
                    _ => 0
                };
                if (memberCount >= 2)
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

                    if (!_currentRoutine.IsFailable && _currentRoutine.Name != "start")
                    {
                        ReportError(code: SemanticDiagnosticCode.UnhandledCrashableCall,
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
        }

        if (call.Callee is MemberExpression member)
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

            // Wired routines ($-prefixed) cannot be called directly by user code
            if (member.PropertyName.StartsWith(value: '$'))
            {
                ReportError(code: SemanticDiagnosticCode.DirectWiredRoutineCall,
                    message: $"Wired routine '{member.PropertyName}' cannot be called directly. " +
                             "Use the corresponding language construct instead (e.g., '==' for $eq, 'for' for $iter).",
                    location: call.Location);
                return ErrorTypeInfo.Instance;
            }

            // #137: Nested hijacking detection — checked before method resolution
            // since hijack() is generic extension T.hijack() that may not resolve by concrete type name
            if (member.PropertyName == "hijack" && IsNestedHijacking(source: member.Object))
            {
                ReportError(code: SemanticDiagnosticCode.NestedHijackingNotAllowed,
                    message: "Cannot hijack a member of an already-hijacked object. " +
                             "Hijack the parent entity directly instead.",
                    location: call.Location);
            }

            bool isFailableMethodCall = member.PropertyName.EndsWith(value: '!');
            string callLookupName = isFailableMethodCall
                ? member.PropertyName[..^1]
                : member.PropertyName;
            RoutineInfo? method =
                _registry.LookupMethod(type: objectType,
                    methodName: callLookupName,
                    isFailable: isFailableMethodCall);

            if (method != null)
            {
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

                    if (!_currentRoutine.IsFailable && _currentRoutine.Name != "start")
                    {
                        ReportError(code: SemanticDiagnosticCode.UnhandledCrashableCall,
                            message:
                            $"Failable routine '{method.Name}!' called without error handling. " +
                            "Use 'when' to match the result, '??' to provide a default, or make the enclosing routine failable (!).",
                            location: call.Location);
                    }
                }

                // #151: Static/instance mismatch — common routine called on instance
                if (method.IsCommon && member.Object is IdentifierExpression instanceId &&
                    LookupTypeWithImports(name: instanceId.Name) == null)
                {
                    ReportError(code: SemanticDiagnosticCode.CommonRoutineMismatch,
                        message:
                        $"Common routine '{method.Name}' must be called on the type '{objectType.Name}', not on an instance.",
                        location: call.Location);
                }

                // Validate method access
                ValidateRoutineAccess(routine: method, accessLocation: call.Location);

                // @readonly enforcement: cannot call modifying methods on 'me'
                if (_currentRoutine is { IsReadOnly: true } &&
                    member.Object is IdentifierExpression { Name: "me" } && !method.IsReadOnly)
                {
                    ReportError(code: SemanticDiagnosticCode.ModificationInReadonlyMethod,
                        message:
                        $"Cannot call non-readonly method '{method.Name}' on 'me' in a @readonly method. " +
                        "Mark the called method @readonly or use @writable/@migratable.",
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
                    callObjectType: objectType);

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
                if (member.PropertyName is "view" or "hijack" &&
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

                // #137: Nested hijacking detection
                if (member.PropertyName == "hijack" && IsNestedHijacking(source: member.Object))
                {
                    ReportError(code: SemanticDiagnosticCode.NestedHijackingNotAllowed,
                        message: "Cannot hijack a member of an already-hijacked object. " +
                                 "Hijack the parent entity directly instead.",
                        location: call.Location);
                }

                // #92: Re-hijacking prohibition — cannot hijack an already-hijacked token
                if (member.PropertyName == "hijack" && IsHijackedType(type: objectType))
                {
                    ReportError(code: SemanticDiagnosticCode.ReHijackingProhibited,
                        message:
                        $"Cannot re-hijack an already-hijacked token '{objectType.Name}'. " +
                        "The entity is already exclusively accessed.",
                        location: call.Location);
                }

                // #170: Downgrade prohibition — cannot call .view() on Hijacked/Seized
                if (member.PropertyName == "view" && (IsHijackedType(type: objectType) ||
                                                      IsSeizedType(type: objectType)))
                {
                    ReportError(code: SemanticDiagnosticCode.TokenDowngradeProhibited,
                        message: $"Cannot downgrade '{objectType.Name}' with '.view()'. " +
                                 "Hijacked/Seized tokens already have write access — use them directly.",
                        location: call.Location);
                }

                // #97: Snatched[T] method calls require danger! block
                if (IsSnatched(type: objectType) && !InDangerBlock)
                {
                    ReportError(code: SemanticDiagnosticCode.SnatchedRequiresDanger,
                        message:
                        $"Method call on 'Snatched[T]' type requires a 'danger!' block. " +
                        "Snatched values bypass ownership safety checks.",
                        location: call.Location);
                }

                // #98: .snatch() on Shared/Marked requires danger! block
                if (member.PropertyName == "snatch" && !InDangerBlock &&
                    (IsSharedType(type: objectType) || IsMarkedType(type: objectType)))
                {
                    ReportError(code: SemanticDiagnosticCode.SnatchRequiresDanger,
                        message:
                        $"Calling '.snatch()' on '{objectType.Name}' requires a 'danger!' block. " +
                        "Snatching bypasses reference counting safety.",
                        location: call.Location);
                }

                // #100/#101: inspect!/seize! only valid on Shared entity handles
                if (member.PropertyName is "inspect" or "seize" &&
                    !IsSharedType(type: objectType) && objectType is not ErrorTypeInfo)
                {
                    ReportError(code: member.PropertyName == "inspect"
                            ? SemanticDiagnosticCode.InspectRequiresMultiRead
                            : SemanticDiagnosticCode.ReadOnlyRejectsLocking,
                        message: $"'{member.PropertyName}!()' is only valid on Shared handles. " +
                                 $"'{objectType.Name}' is not a Shared handle.",
                        location: call.Location);
                }

                // #19: Lock policy validation — inspect!/seize! must match the lock policy
                if (member.PropertyName is "inspect" or "seize" &&
                    member.Object is IdentifierExpression lockPolicyTarget &&
                    _variableLockPolicies.TryGetValue(key: lockPolicyTarget.Name,
                        value: out string? policy))
                {
                    if (member.PropertyName == "inspect" && policy == "Exclusive")
                    {
                        ReportError(code: SemanticDiagnosticCode.InspectRequiresMultiRead,
                            message:
                            $"Cannot use 'inspect!()' on '{lockPolicyTarget.Name}' — it uses Exclusive lock policy. " +
                            "Exclusive locks do not support concurrent readers. Use 'seize!()' instead.",
                            location: call.Location);
                    }

                    if (member.PropertyName == "seize" && policy == "ReadOnly")
                    {
                        ReportError(code: SemanticDiagnosticCode.ReadOnlyRejectsLocking,
                            message:
                            $"Cannot use 'seize!()' on '{lockPolicyTarget.Name}' — it uses ReadOnly lock policy. " +
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
                    method.ModificationCategory != ModificationCategory.Readonly)
                {
                    ReportError(code: SemanticDiagnosticCode.MigratableDuringIteration,
                        message:
                        $"Cannot call modifying method '{method.Name}' on '{iterTarget.Name}' while iterating over it. " +
                        "Collect changes and apply them after the loop.",
                        location: call.Location);
                }

                // #47: .hijack() on @initonly record warns — record is frozen after construction
                if (member.PropertyName == "hijack" && objectType is RecordTypeInfo)
                {
                    // Check if the variable holding the record is @initonly bound
                    if (member.Object is IdentifierExpression hijackTarget)
                    {
                        VariableInfo? targetVar =
                            _registry.LookupVariable(name: hijackTarget.Name);
                        if (targetVar is { IsModifiable: false })
                        {
                            ReportWarning(code: SemanticWarningCode.HijackOnInitOnly,
                                message:
                                $"Calling '.hijack()' on @initonly-bound record '{hijackTarget.Name}'. " +
                                "The record is frozen after construction — hijacking has no practical effect.",
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

                // Validate exclusive token uniqueness (cannot pass same Hijacked/Seized twice)
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
                        substitutions[key: genParamOwner.Name] = objectType;
                    }

                    // Protocol owner → map protocol generic params to receiver's type args
                    if (method.OwnerType is ProtocolTypeInfo protoOwner &&
                        objectType is { IsGenericResolution: true, TypeArguments: not null })
                    {
                        ProtocolTypeInfo protoGenDef = protoOwner.GenericDefinition ?? protoOwner;
                        if (protoGenDef.GenericParameters is { Count: > 0 })
                        {
                            for (int i = 0; i < protoGenDef.GenericParameters.Count &&
                                            i < objectType.TypeArguments.Count; i++)
                            {
                                substitutions[key: protoGenDef.GenericParameters[index: i]] =
                                    objectType.TypeArguments[index: i];
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
            else
            {
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
                    string creatorFullName = $"{potentialTypeName}.$create";
                    RoutineInfo? creator =
                        _registry.LookupRoutineOverload(baseName: creatorFullName,
                            argTypes: [objectType]);
                    // Fall back to default overload if no match by arg type
                    creator ??= _registry.LookupRoutine(fullName: creatorFullName);

                    if (creator != null)
                    {
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

                            if (!_currentRoutine.IsFailable && _currentRoutine.Name != "start")
                            {
                                ReportError(code: SemanticDiagnosticCode.UnhandledCrashableCall,
                                    message:
                                    $"Failable routine '{creator.Name}!' called without error handling. " +
                                    "Use 'when' to match the result, '??' to provide a default, or make the enclosing routine failable (!).",
                                    location: call.Location);
                            }
                        }

                        return targetType;
                    }
                }
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

        return calleeType;
    }

    /// <summary>
    /// Infers dispatch strategy for a call site with protocol-constrained varargs.
    /// Returns null for non-varargs routines (always buildtime, no annotation needed).
    /// </summary>
    private DispatchStrategy? InferDispatchStrategy(RoutineInfo routine, CallExpression call)
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
