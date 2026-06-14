using System.Collections.Generic;
using System.Linq;
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
    private const string StartRoutineName = "start";
    private const string UseWhenHint = "Use 'when' to match the result, '??' to provide a default, or make the enclosing routine failable (!).";
    private const string BlankMemberName = "Blank";
    private const string ModifyMethodName = "modify";
    private const string InspectMethodName = "inspect";

    private TypeSymbol AnalyzeCallExpression(CallExpression call)
    {
        switch (call.Callee)
        {
            // Get the callee type/routine
            case IdentifierExpression id:
            {
                bool isFailableCall = id.Name.EndsWith(value: '!');
                // Strip '!' suffix for failable calls (e.g., "stop!" -> "stop")
                string callName = isFailableCall
                    ? id.Name[..^1]
                    : id.Name;
                // Look up the type with `!` stripped — `U32!(level)` is a failable type
                // constructor call routing to `U32.$create!(from: U64)`. Without stripping,
                // `LookupTypeWithImports("U32!")` returns null and the call falls through to
                // non-creator paths, eventually mis-picking a non-failable overload by name.
                TypeSymbol? callableType = LookupTypeWithImports(name: callName);
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

                // Display-routine desugaring (phase 1): `show(x)` / `alert(x)` where x is a
                // copy-restricted wrapper becomes `show(x.$represent())` / `alert(x.$diagnose())`
                // BEFORE overload resolution. The rewrite turns the call into a Text-typed
                // argument, so overload resolution picks the `show(value: Referring[Text])`
                // / `alert(value: Referring[Text])` overload instead of the generic-T variant
                // that would either trigger S420 (implicit copy of the wrapper) or — worse —
                // bind to the wrong overload and emit a garbage call at runtime.
                if (_registry.Language == Language.RazorForge)
                {
                    RewriteDisplayRoutineWrapperArgs(callName: callName,
                        arguments: call.Arguments);
                }

                RoutineInfo? routine = _registry.LookupRoutine(fullName: callName,
                    isFailable: isFailableCall);
                // Try current module prefix (e.g., "infinite_loop" -> "HelloWorld.infinite_loop")
                if (routine == null && _currentModuleName != null && !callName.Contains(value: '.'))
                {
                    routine = _registry.LookupRoutine(fullName: $"{_currentModuleName}.{callName}",
                        isFailable: isFailableCall);
                }

                // Explicit type arguments on a generic routine call — monomorphize immediately so
                // that ResolvedType is concrete (e.g., signed_div[S32](...) -> ReturnType = S32, not T).
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
                    List<TypeInfo>? inferred =
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
                if (routine is { IsGenericDefinition: false, IsVariadic: false } &&
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
                        _registry.LookupRoutineOverload(baseName: callName, argTypes: arityArgTypes)
                        ?? _registry.LookupRoutineOverload(baseName: routine.BaseName, argTypes: arityArgTypes);
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
                            List<TypeInfo>? inferred =
                                InferGenericTypeArguments(genericRoutine: generic,
                                    arguments: call.Arguments);
                            routine = inferred != null
                                ? _registry.GetOrCreateRoutineResolution(
                                    genericDef: generic, typeArguments: inferred)
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

                        // Bare callName misses module-qualified overloads (the routines register
                        // under `Module.name#params`). Fall back to the resolved routine's qualified
                        // BaseName so overload resolution finds sibling overloads in the same module.
                        RoutineInfo? better =
                            _registry.LookupRoutineOverload(baseName: callName,
                                argTypes: resolvedArgTypes)
                            ?? _registry.LookupRoutineOverload(baseName: routine.BaseName,
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
                                List<TypeInfo>? inferred =
                                    InferGenericTypeArguments(genericRoutine: generic,
                                        arguments: call.Arguments);
                                // Use GetOrCreateRoutineResolution so the monomorphisation lands
                                // in `_routineResolutions`. CreateInstance alone produced a stray
                                // instance that codegen mangled to `show(Point)` but
                                // ProcessResolvedMethodGenericRoutines never picked up — no body
                                // emitted, link errors followed.
                                routine = inferred != null
                                    ? _registry.GetOrCreateRoutineResolution(
                                        genericDef: generic, typeArguments: inferred)
                                    : generic;
                                call.ResolvedRoutine = routine;
                            }
                        }
                    }
                }

                // Variadic fallback: if resolved routine is non-variadic but has too many args,
                // try a variadic generic overload (e.g., show("a","b","c") -> show[T](values...: T))
                if (routine is { IsVariadic: false } &&
                    call.Arguments.Count > routine.Parameters.Count)
                {
                    RoutineInfo? variadicGeneric =
                        _registry.LookupVariadicGenericOverload(name: callName);
                    if (variadicGeneric != null)
                    {
                        List<TypeInfo>? inferred =
                            InferGenericTypeArguments(genericRoutine: variadicGeneric,
                                arguments: call.Arguments);
                        routine = inferred != null
                            ? _registry.GetOrCreateRoutineResolution(
                                genericDef: variadicGeneric, typeArguments: inferred)
                            : variadicGeneric;
                        call.ResolvedRoutine = routine;
                    }
                }

                if (callableType != null && call.Arguments.Count > 0)
                {
                    // Variant construction auto-wraps the argument into the variant (e.g.
                    // `Inner(7_s32)` -> Inner's S32 arm, `Inner(none)` -> Inner's None arm), so the
                    // argument's contextual type is the variant itself. Without this, a bare `none`
                    // argument has no expected type and errors S016.
                    TypeSymbol? variantArgContext = callableType is VariantTypeInfo ? callableType : null;
                    var creatorArgTypes = new List<TypeSymbol>(capacity: call.Arguments.Count);
                    foreach (Expression arg in call.Arguments)
                    {
                        creatorArgTypes.Add(item: AnalyzeExpression(expression: arg, expectedType: variantArgContext));
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

                        // `Type(fields)` written *inside* Type's own `$create` is the memberwise
                        // primitive base case — it must inline, never route back to `$create`
                        // (infinite recursion). Mirrors the CreatorExpression path's guard.
                        bool insideOwnCreate =
                            _currentRoutine is { Name: "$create" or "$create!" } currentCreate
                            && currentCreate.OwnerType != null
                            && (currentCreate.OwnerType.FullName == callableType.FullName
                                || currentCreate.OwnerType.Name == callableType.Name);

                        // Route through a *user-declared* `$create` so its body/side-effects run.
                        // The synthesized memberwise creator (IsSynthesized) is pure field-init and
                        // is left to inline construction in codegen. A user `$create` whose params
                        // match the fields only by name but differ by type (e.g.
                        // `Resource.$create(tag: S32)` over field `tag: S64`) is the real
                        // constructor and is selected by arg type via LookupMethodOverload above.
                        if (!insideOwnCreate && !creator.IsSynthesized)
                        {
                            call.ResolvedRoutine = creator;

                            // Failability propagation for failable constructors (e.g. `U32!(x)`
                            // routing to `U32.$create!(from: U64)`).
                            if (creator.IsFailable && _currentRoutine != null)
                            {
                                _currentRoutine.HasFailableCalls = true;
                                _currentRoutine.FailableCallees.Add(creator);
                                if (!_currentRoutine.IsFailable &&
                                    _currentRoutine.Name != StartRoutineName &&
                                    !_currentRoutine.IsSynthesized)
                                {
                                    ReportWarning(code: SemanticWarningCode.UnhandledCrashableCall,
                                        message:
                                        $"Failable constructor '{callableType.Name}!' called without error handling. " +
                                        UseWhenHint,
                                        location: call.Location);
                                }
                            }
                        }

                        call.IsInFlight = creator.IsInFlightReturn;
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
                        _currentRoutine.FailableCallees.Add(routine);

                        // Non-failable routine (except start/synthesized) cannot call failable routines
                        if (!_currentRoutine.IsFailable && _currentRoutine.Name != StartRoutineName &&
                            !_currentRoutine.IsSynthesized)
                        {
                            ReportWarning(code: SemanticWarningCode.UnhandledCrashableCall,
                                message:
                                $"Failable routine '{routine.Name}!' called without error handling. " +
                                UseWhenHint,
                                location: call.Location);
                        }
                    }

                    // Validate routine access
                    ValidateRoutineAccess(routine: routine, accessLocation: call.Location);

                    AnalyzeCallArguments(routine: routine,
                        arguments: call.Arguments,
                        location: call.Location);

                    // Validate exclusive token uniqueness (cannot pass same Modifying/Claiming twice)
                    ValidateExclusiveTokenUniqueness(arguments: call.Arguments,
                        location: call.Location);

                    // Return type is Blank if not specified (routines without explicit return type return Blank)
                    TypeSymbol returnType = routine.ReturnType ??
                                            _registry.LookupType(name: BlankMemberName) ??
                                            ErrorTypeInfo.Instance;
                    call.IsInFlight = routine.IsInFlightReturn;

                    // A `threaded routine` call spawns an OS thread and yields a `Task[T]`
                    // handle (T = the routine's own return type). The handle is awaited via the
                    // stdlib `Task[T].retrieve!()` / `.waitfor(deadline)` methods. (v0.1.)
                    if (routine.AsyncStatus == AsyncStatus.Threaded)
                    {
                        TypeSymbol? taskDef = _registry.LookupType(name: "Task");
                        return taskDef != null
                            ? _registry.GetOrCreateResolution(genericDef: taskDef,
                                typeArguments: [returnType])
                            : returnType;
                    }

                    return returnType;
                }

                // Could be a type creator
                TypeSymbol? type = callableType;
                if (type != null)
                {
                    call.ConstructedType = type;
                    call.LoweringKind = ClassifyConstruction(type: type,
                        isCollectionLiteral: call.IsCollectionLiteral);

                    // Analyze all arguments once before branching. Variant construction auto-wraps
                    // the argument into the variant, so its contextual type is the variant itself
                    // (lets a bare `none` argument resolve to the variant's None arm).
                    TypeSymbol? variantArgContext = type is VariantTypeInfo ? type : null;
                    var argTypes = new List<TypeSymbol>();
                    foreach (Expression arg in call.Arguments)
                    {
                        argTypes.Add(item: AnalyzeExpression(expression: arg, expectedType: variantArgContext));
                    }

                    // C95: Try $create overload match first
                    // e.g., BitList(capacity: 32u64) -> BitList.$create(capacity: U64)
                    // e.g., BitList(32u64) -> BitList.$create(capacity: U64) instead of collection literal
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
                            call.IsInFlight = creator.IsInFlightReturn;
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
                    if (type is TypeInfo ti)
                        call.IsInFlight = ti.ImplicitConstructorReturnsInFlight;
                    return type;
                }

                // Try module-prefixed routine lookup (e.g., Core.normalize_duration)
                // This is done after type creator check to avoid shadowing type creators
                // with identically-named convenience functions (e.g., "routine U32(from: U8)")
                routine = LookupRoutineWithImports(name: callName);

                // Overload resolution for import-resolved routines (e.g., show[T] from IO/Console)
                if (routine is { IsGenericDefinition: false } && call.Arguments.Count > 0 &&
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
                                List<TypeInfo>? inferredImport =
                                    InferGenericTypeArguments(genericRoutine: genericImport,
                                        arguments: call.Arguments);
                                routine = inferredImport != null
                                    ? _registry.GetOrCreateRoutineResolution(
                                        genericDef: genericImport, typeArguments: inferredImport)
                                    : genericImport;
                                call.ResolvedRoutine = routine;
                            }
                        }
                    }
                }

                // Variadic fallback for import-resolved routines
                if (routine is { IsVariadic: false } &&
                    call.Arguments.Count > routine.Parameters.Count)
                {
                    RoutineInfo? variadicGeneric =
                        _registry.LookupVariadicGenericOverload(name: callName);
                    if (variadicGeneric != null)
                    {
                        List<TypeInfo>? inferred =
                            InferGenericTypeArguments(genericRoutine: variadicGeneric,
                                arguments: call.Arguments);
                        routine = inferred != null
                            ? _registry.GetOrCreateRoutineResolution(
                                genericDef: variadicGeneric, typeArguments: inferred)
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
                        _currentRoutine.FailableCallees.Add(routine);

                        if (!_currentRoutine.IsFailable && _currentRoutine.Name != StartRoutineName &&
                            !_currentRoutine.IsSynthesized)
                        {
                            ReportWarning(code: SemanticWarningCode.UnhandledCrashableCall,
                                message:
                                $"Failable routine '{routine.Name}!' called without error handling. " +
                                UseWhenHint,
                                location: call.Location);
                        }
                    }

                    ValidateRoutineAccess(routine: routine, accessLocation: call.Location);
                    AnalyzeCallArguments(routine: routine,
                        arguments: call.Arguments,
                        location: call.Location);

                    ValidateExclusiveTokenUniqueness(arguments: call.Arguments,
                        location: call.Location);

                    TypeSymbol returnType = routine.ReturnType ??
                                            _registry.LookupType(name: BlankMemberName) ??
                                            ErrorTypeInfo.Instance;
                    call.IsInFlight = routine.IsInFlightReturn;
                    return returnType;
                }

                break;
            }
            case MemberExpression member:
            {
                TypeSymbol objectType = AnalyzeExpression(expression: member.Object);

                // $iter / $refer / $control are dunder-private to their protocols — only the
                // corresponding lowering passes may emit them (for-loop → $iter; argument
                // coercion → $refer/$control). Forbidding user calls prevents storing the
                // result in a variable, which would let a borrow / iterator outlive its source.
                // Stdlib is exempt — its iterator implementations and wrapper bodies chain these
                // dunders directly (e.g., `me.source.$iter()`, wrapper `$refer` forwarders).
                if ((member.PropertyName == "$iter"
                     || member.PropertyName == "$refer"
                     || member.PropertyName == "$control")
                    && !call.IsSynthesizedLowering
                    && !IsStdlibFile(filePath: call.Location.FileName))
                {
                    string hint = member.PropertyName == "$iter"
                        ? "use a 'for' loop or iterable combinators (skip, take, map, etc.) instead."
                        : "pass the value to a routine whose parameter is typed " +
                          "Referring[T] / Controlling[T] — the compiler coerces it for you.";
                    ReportError(code: SemanticDiagnosticCode.DirectWiredRoutineCall,
                        message: $"Method '{member.PropertyName}' is internal to the compiler — {hint}",
                        location: call.Location);
                    return ErrorTypeInfo.Instance;
                }

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
                // since modify() is generic extension T.modify() that may not resolve by concrete type name
                if (member.PropertyName == ModifyMethodName && IsNestedModifying(source: member.Object))
                {
                    ReportError(code: SemanticDiagnosticCode.NestedHijackingNotAllowed,
                        message: "Cannot modify a member of an already-modified object. " +
                                 "Modify the parent entity directly instead.",
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

                // Named-argument overload disambiguation. LookupMethod returns one overload by name;
                // when the call supplies a named argument that overload lacks — e.g.
                // `get_count(predicate: …)` resolving to the zero-arg `get_count()` — prefer the
                // overload whose parameters cover every named argument. This MUST run before the
                // arguments are analyzed below: otherwise a callback argument is analyzed against a
                // missing/wrong parameter type, collapses to <error>, and the later type-based
                // overload retry can no longer recover the right method.
                if (method != null && dispatchType != null && call.Arguments.Count > 0
                    && call.Arguments.Any(predicate: a => a is NamedArgumentExpression))
                {
                    var providedNames = call.Arguments
                        .OfType<NamedArgumentExpression>()
                        .Select(selector: n => n.Name)
                        .ToList();
                    bool methodCoversNames = providedNames.All(predicate: n =>
                        method.Parameters.Any(predicate: p => p.Name == n));
                    if (!methodCoversNames)
                    {
                        var candidates = new List<RoutineInfo>();
                        _registry.CollectMemberRoutineCandidates(type: dispatchType,
                            methodName: callLookupName, candidates: candidates);
                        RoutineInfo? byName = candidates.FirstOrDefault(predicate: c =>
                            c.Parameters.Count == call.Arguments.Count
                            && providedNames.All(predicate: n =>
                                c.Parameters.Any(predicate: p => p.Name == n)));
                        if (byName != null)
                            method = byName;
                    }
                }

                if (method is { IsGenericDefinition: false } && call.Arguments.Count > 0)
                {
                    var resolvedArgTypes = new List<TypeSymbol>(capacity: call.Arguments.Count);
                    int posIdx = 0;
                    foreach (Expression arg in call.Arguments)
                    {
                        Expression actualArg = arg is NamedArgumentExpression named ? named.Value : arg;
                        TypeSymbol? expectedParamType = null;
                        if (arg is NamedArgumentExpression namedArg)
                        {
                            ParameterInfo? p = method.Parameters
                                .FirstOrDefault(predicate: pp => pp.Name == namedArg.Name);
                            if (p != null) expectedParamType = p.Type;
                        }
                        else if (posIdx < method.Parameters.Count)
                        {
                            expectedParamType = method.Parameters[index: posIdx].Type;
                        }
                        if (expectedParamType != null && dispatchType != null
                            && method.OwnerType is { IsGenericDefinition: true })
                        {
                            expectedParamType =
                                SubstituteOwnerGenerics(paramType: expectedParamType,
                                    lookupType: dispatchType,
                                    ownerType: method.OwnerType) ?? expectedParamType;
                        }
                        TypeSymbol argType = AnalyzeExpression(expression: actualArg,
                            expectedType: expectedParamType);
                        if (argType != ErrorTypeInfo.Instance)
                        {
                            resolvedArgTypes.Add(item: argType);
                        }
                        posIdx++;
                    }

                    bool arityMismatch = method.Parameters.Count != resolvedArgTypes.Count;
                    bool firstArgMismatch = !arityMismatch &&
                                            method.Parameters.Count > 0 &&
                                            resolvedArgTypes.Count > 0 &&
                                            !IsAssignableTo(source: resolvedArgTypes[0],
                                                target: method.Parameters[0].Type);

                    if (arityMismatch || firstArgMismatch)
                    {
                        RoutineInfo? betterMethod = _registry.LookupMethodOverload(type: dispatchType!,
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
                        _currentRoutine.FailableCallees.Add(method);

                        if (!_currentRoutine.IsFailable && _currentRoutine.Name != StartRoutineName &&
                            !_currentRoutine.IsSynthesized)
                        {
                            ReportWarning(code: SemanticWarningCode.UnhandledCrashableCall,
                                message:
                                $"Failable routine '{method.Name}!' called without error handling. " +
                                UseWhenHint,
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

                    // @readonly enforcement: cannot call mutating methods on 'me'
                    if (_currentRoutine is { IsReadOnly: true } &&
                        member.Object is IdentifierExpression { Name: "me" } && !method.IsReadOnly)
                    {
                        ReportError(code: SemanticDiagnosticCode.MutationInReadonlyMethod,
                            message:
                            $"Cannot call non-readonly method '{method.Name}' on 'me' in a @readonly method. " +
                            "Mark the called method @readonly or use @migratable.",
                            location: call.Location);
                    }

                    // Preset enforcement: cannot call mutating methods on preset variables
                    if (member.Object is IdentifierExpression letTarget &&
                        method.MutationCategory != MutationCategory.Readonly)
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
                        List<TypeInfo>? inferredMethodTypeArgs =
                            InferMethodGenericTypeArguments(genericMethod: method,
                                arguments: call.Arguments);
                        if (inferredMethodTypeArgs != null)
                        {
                            method = _registry.GetOrCreateRoutineResolution(genericDef: method,
                                typeArguments: inferredMethodTypeArgs);
                            // AnalyzeCallArguments above ran against the still-generic signature, so a
                            // lambda argument whose parameter binds a method-level generic kept it
                            // unresolved (e.g. `acc` in `accumulate[U](combiner: Routine[(U,T),U])`
                            // stayed `U`). Now that the method generics are bound, re-analyze the
                            // lambda arguments against the resolved parameter types so their
                            // parameters become concrete — otherwise the lifted lambda mangles with an
                            // unbound generic (`[lambda]...(U,S64)`) and codegen cannot emit it.
                            ReanalyzeLambdaArguments(resolvedMethod: method,
                                arguments: call.Arguments,
                                callObjectType: dispatchType);
                        }
                    }

                    // P1: Store fully resolved RoutineInfo (with owner-level generic substitution)
                    call.ResolvedRoutine = method;

                    // Move-on-consume: `a.retain()` / `a.track()` on an `T` receiver
                    // transfers ownership into the RC handle; the source variable is dead after.
                    // `.retain()` on an already-RC handle (Retained / Shared / ...) is a refcount
                    // bump that leaves the source intact, so we only mark deadref for Owned sources.
                    // `T` is declared as a `record` in stdlib (not WrapperTypeInfo), so we
                    // check the base name rather than the runtime category.
                    // Move-on-consume: `a.retain()` / `a.track()` transfers ownership into a
                    // new RC handle when the receiver is a raw entity (the canonical fresh
                    // form) or an `T`. After the call the source name is dead — any
                    // later use is a hard error (UseAfterSteal). `.retain()` on an existing
                    // RC handle (`Retained[T]`, `Shared[T]`, ...) is a refcount bump and the
                    // source remains valid.
                    if (member.PropertyName is "retain" or "track" &&
                        member.Object is IdentifierExpression consumedId)
                    {
                        string baseName = GetBaseTypeName(typeName: objectType.Name);
                        bool consumesSource = baseName == "Owned" || objectType is EntityTypeInfo;
                        if (consumesSource)
                        {
                            _deadrefVariables.Add(item: consumedId.Name);
                        }
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
                    if (member.PropertyName is "view" or ModifyMethodName &&
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
                    if (member.PropertyName == ModifyMethodName && IsNestedModifying(source: member
                        .Object))
                    {
                        ReportError(code: SemanticDiagnosticCode.NestedHijackingNotAllowed,
                            message: "Cannot grasp a member of an already-grasped object. " +
                                     "Hijack the parent entity directly instead.",
                            location: call.Location);
                    }

                    // #92: Re-grasping prohibition — cannot grasp an already-grasped token
                    if (member.PropertyName == ModifyMethodName && IsModifyingType(type: objectType))
                    {
                        ReportError(code: SemanticDiagnosticCode.ReHijackingProhibited,
                            message:
                            $"Cannot re-modify an already-modified token '{objectType.Name}'. " +
                            "The entity is already exclusively accessed.",
                            location: call.Location);
                    }

                    // #170: Downgrade prohibition — cannot call .view() on Modifying/Claiming
                    if (member.PropertyName == "view" && (IsModifyingType(type: objectType) ||
                                                          IsClaimingType(type: objectType)))
                    {
                        ReportError(code: SemanticDiagnosticCode.TokenDowngradeProhibited,
                            message: $"Cannot downgrade '{objectType.Name}' with '.view()'. " +
                                     "Modifying/Claiming tokens already have write access — use them directly.",
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

                    // #98: .hijack() on Shared/Watched requires danger! block
                    if (member.PropertyName == "hijack" && !InDangerBlock &&
                        (IsSharedType(type: objectType) || IsWatchedType(type: objectType)))
                    {
                        ReportError(code: SemanticDiagnosticCode.SnatchRequiresDanger,
                            message:
                            $"Calling '.hijack()' on '{objectType.Name}' requires a 'danger!' block. " +
                            "Hijacked values bypasses reference counting safety.",
                            location: call.Location);
                    }

                    // NOTE: `inspect()` / `claim()` are ordinary `Shared[T, P]` methods now —
                    // resolution + the `needs P in [...]` type-equality constraint (RF-S160) enforce
                    // policy legality (inspect not on Exclusive, claim not on ReadOnly), and the
                    // scoped-token / `using`-binding rules enforce lifetime. The earlier ad-hoc
                    // inspect!/claim! validation (a variable→policy side-table that did not recognize
                    // the 2-arg `Shared[T, P]`) was removed in favour of the type system.

                    // Enforce a method's `needs P in [...]` (TypeEquality) constraint when the
                    // constrained parameter is INHERITED FROM THE RECEIVER (e.g.
                    // `Shared[T, P].claim() needs P in [Exclusive, MultiRead]`, with P bound by the
                    // receiver `Shared[Counter, ReadOnly]`). The general constraint validator only
                    // fires for explicitly-instantiated generics, so a receiver-bound param — which
                    // carries no explicit type args at the call site — is validated here instead.
                    ValidateReceiverInheritedTypeEqualityConstraints(method: method,
                        receiverType: objectType, member: member, location: call.Location);

                    // A multi-threaded access token (Inspecting/Claiming, produced by
                    // inspect()/claim()) is only legal as the immediate resource of a `using` block,
                    // so its lock spans exactly that scope. Reject every other position — inline use,
                    // a function argument, an unbound statement — with RF-S629. (The "cannot bind to a
                    // var" half is already enforced for inline-only tokens at var-declaration sites.)
                    if (method.ReturnType is { } mtReturn &&
                        GetBaseTypeName(typeName: mtReturn.Name) is "Inspecting" or "Claiming" &&
                        !ReferenceEquals(objA: call, objB: _usingResourceNode))
                    {
                        ReportError(code: SemanticDiagnosticCode.MtTokenRequiresUsing,
                            message:
                            $"'{member.PropertyName}()' returns a scope-bound access token and must be " +
                            $"opened with 'using' (e.g. 'using …{member.PropertyName}() as v'). It " +
                            "cannot be used inline, passed as an argument, or stored.",
                            location: call.Location);
                    }

                    // #22: Reject migratable operations on collection being iterated
                    if (member.Object is IdentifierExpression iterTarget &&
                        _activeIterationSources.Contains(item: iterTarget.Name) &&
                        method.MutationCategory == MutationCategory.Migratable)
                    {
                        ReportError(code: SemanticDiagnosticCode.MigratableDuringIteration,
                            message:
                            $"Cannot call migratable method '{method.Name}' on '{iterTarget.Name}' while iterating over it. " +
                            "Collect changes and apply them after the loop.",
                            location: call.Location);
                    }

                    // #47: .grasp() on @initonly record warns — record is frozen after construction
                    // Check if the variable holding the record is @initonly bound
                    if (member.PropertyName == ModifyMethodName && objectType is RecordTypeInfo &&
                        member.Object is IdentifierExpression graspTarget)
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

                    // #104/#23: Channel send() makes source variable a deadref
                    if (member is { PropertyName: "send", Object: IdentifierExpression sendSource })
                    {
                        string baseObjType = GetBaseTypeName(typeName: objectType.Name);
                        if (baseObjType == "Channel")
                        {
                            _deadrefVariables.Add(item: sendSource.Name);
                        }
                    }

                    // Validate exclusive token uniqueness (cannot pass same Modifying/Claiming twice)
                    ValidateExclusiveTokenUniqueness(arguments: call.Arguments,
                        location: call.Location);

                    // Return type is Blank if not specified
                    TypeSymbol? callReturnType = method.ReturnType;
                    if (callReturnType != null)
                    {
                        var substitutions = new Dictionary<string, TypeSymbol>();

                        // GenericParameterTypeInfo owner -> map param name to receiver type
                        if (method.OwnerType is GenericParameterTypeInfo genParamOwner)
                        {
                            substitutions[key: genParamOwner.Name] = dispatchType!;
                        }

                        // Protocol owner -> map protocol generic params to receiver's type args
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

                        // `Me` (ProtocolSelf, Name "Me") in a return type always denotes the
                        // receiver — e.g. `Iterable[T].enumerate() -> ?EnumerateIterator[T, Me]`.
                        // Bind it to the concrete receiver so the call's return type is the concrete
                        // adapter (`EnumerateIterator[Text, List[Text]]`). Unconditional: the
                        // protocol-extension method is re-homed onto the implementer (owner =
                        // List[Text], not the protocol), so an owner-is-protocol gate would miss it;
                        // for non-protocol methods no return type contains `Me`, so this is a no-op.
                        substitutions[key: "Me"] = dispatchType!;

                        // Protocol method resolved through a generic param's `obeys` constraint
                        // (e.g. `r.$iter()` where `r: __T0 obeys Iterable[S64]`). The resolved method
                        // is homed on the bare generic param, and its signature carries the PROTOCOL's
                        // own element param (`Iterator[T]`), which is distinct from `__T0` and so isn't
                        // bound by the branches above. Bind each obeys-constraint protocol's params from
                        // the constraint's type args (`Iterable[S64]` ⇒ T=S64) so a return type like
                        // `Iterator[T]` resolves to `Iterator[S64]` instead of leaking the element param
                        // into the monomorphized body (`GenericParameterTypeInfo 'T' reached GetLlvmType`).
                        if (dispatchType is GenericParameterTypeInfo dispatchParam)
                        {
                            foreach (GenericConstraintDeclaration gc in
                                     ActiveConstraintsFor(paramName: dispatchParam.Name))
                            {
                                if (gc is not { ConstraintType: ConstraintKind.Obeys, ConstraintTypes: not null })
                                    continue;
                                foreach (TypeExpression ce in gc.ConstraintTypes)
                                {
                                    TypeSymbol resolvedConstraint = _typeResolver.ResolveType(typeExpr: ce);
                                    if (resolvedConstraint is not ProtocolTypeInfo rcProto ||
                                        rcProto.TypeArguments is not { Count: > 0 } cArgs)
                                        continue;
                                    ProtocolTypeInfo rcDef = rcProto.GenericDefinition ?? rcProto;
                                    if (rcDef.GenericParameters is not { Count: > 0 } cParams) continue;
                                    for (int i = 0; i < cParams.Count && i < cArgs.Count; i++)
                                        substitutions[key: cParams[index: i]] = cArgs[index: i];
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
                                            _registry.LookupType(name: BlankMemberName) ??
                                            ErrorTypeInfo.Instance;
                    call.IsInFlight = method.IsInFlightReturn;
                    return returnType;
                }

                // #78: Method-chain constructor — "42".S32!() -> S32.$create!(from: "42")
                string propName = member.PropertyName;
                bool isFailable = propName.EndsWith(value: '!');
                string potentialTypeName = isFailable
                    ? propName[..^1]
                    : propName;

                TypeSymbol? targetType = LookupTypeWithImports(name: potentialTypeName);
                if (targetType != null)
                {
                    // Look up the creator on the target type, using method-overload resolution
                    // to match the object type (e.g., Text -> S32.$create!(from_text: Text)).
                    // Note: parser strips '!' from routine names — IsFailable is a separate flag.
                    // Always look up "$create" and check IsFailable on the result.
                    // $create is owner-scoped, so LookupMethodOverload (not LookupRoutineOverload)
                    // is the right entry point — the latter only indexes free functions.
                    RoutineInfo? creator =
                        _registry.LookupMethodOverload(type: targetType,
                            methodName: "$create",
                            argTypes: [objectType]);
                    // Fall back to default overload if no match by arg type
                    string creatorFullName = $"{targetType.FullName}.$create";
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
                            _currentRoutine.FailableCallees.Add(creator);

                            if (!_currentRoutine.IsFailable && _currentRoutine.Name != StartRoutineName &&
                                !_currentRoutine.IsSynthesized)
                            {
                                ReportWarning(code: SemanticWarningCode.UnhandledCrashableCall,
                                    message:
                                    $"Failable routine '{creator.Name}!' called without error handling. " +
                                    UseWhenHint,
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
            return routineType.ReturnType ?? _registry.LookupType(name: BlankMemberName) ?? ErrorTypeInfo.Instance;
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
    /// Validates a called method's <c>needs P in [...]</c> (<see cref="ConstraintKind.TypeEquality"/>)
    /// constraints when the constrained parameter is inherited from the receiver type rather than
    /// supplied as an explicit type argument — e.g. <c>Shared[T, P].claim() needs P in [Exclusive,
    /// MultiRead]</c> called on a <c>Shared[Counter, ReadOnly]</c>. The standard constraint validator
    /// (<c>TypeResolver.ValidateTypeEqualityConstraint</c>) only fires when a generic type/method is
    /// explicitly instantiated, so receiver-bound parameters would otherwise go unchecked.
    /// </summary>
    private void ValidateReceiverInheritedTypeEqualityConstraints(RoutineInfo method,
        TypeSymbol receiverType, MemberExpression member, SourceLocation location)
    {
        if (method.GenericConstraints is not { Count: > 0 } constraints)
            return;

        // Map the receiver's generic parameter names to its bound type arguments. The names live on
        // the generic definition; the bindings on the resolved instance.
        List<string>? paramNames = receiverType.GenericParameters
            ?? (receiverType as RecordTypeInfo)?.GenericDefinition?.GenericParameters;
        List<TypeInfo>? boundArgs = receiverType.TypeArguments;
        if (paramNames is not { Count: > 0 } || boundArgs is not { Count: > 0 })
            return;

        foreach (GenericConstraintDeclaration constraint in constraints)
        {
            if (constraint.ConstraintType != ConstraintKind.TypeEquality ||
                constraint.ConstraintTypes is not { Count: > 0 } allowed)
                continue;

            int paramIndex = paramNames.IndexOf(item: constraint.ParameterName);
            if (paramIndex < 0 || paramIndex >= boundArgs.Count)
                continue;

            TypeInfo bound = boundArgs[index: paramIndex];
            string boundBase = GetBaseTypeName(typeName: bound.Name);
            string boundShort = boundBase.Contains(value: '.')
                ? boundBase[(boundBase.LastIndexOf(value: '.') + 1)..]
                : boundBase;

            bool inSet = allowed.Any(predicate: ce =>
                ce.Name == bound.Name || ce.Name == boundBase || ce.Name == boundShort);
            if (inSet)
                continue;

            string allowedList = string.Join(separator: ", ",
                values: allowed.Select(selector: t => t.Name));
            ReportError(code: SemanticDiagnosticCode.TypeEqualityConstraintViolation,
                message:
                $"'{member.PropertyName}()' is not available on '{receiverType.Name}': " +
                $"'{boundShort}' is not in [{allowedList}] " +
                $"(constraint on '{constraint.ParameterName}').",
                location: location);
        }
    }

}
