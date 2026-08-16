using System.Collections.Generic;
using System.Linq;
using Compiler.Resolution;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using TypeSymbol = TypeModel.Types.TypeInfo;

namespace Compiler.Synthesis;

/// <summary>
/// Phase D synthesizer: lazily generates transparent-forwarding routines on wrapper
/// types (T, Viewing[T], Modifying[T], etc.) when user code calls a method that
/// exists on the inner type T but not directly on the wrapper.
///
/// Synthesis anchors on the wrapper's generic definition (e.g. T) so that
/// monomorphization handles per-instance specialization.  The forwarder body is:
///
///   danger
///     var raw = Hijacked[T](me)
///     return raw.extract().method(arg1: arg1, ...)
///
/// where T is the wrapper's generic parameter.  When monomorphized with T->List[Byte],
/// the body's Hijacked[T] becomes Hijacked[List[Byte]], and expression types resolve
/// transitively through raw.extract() to the concrete inner type.
///
/// Policy:
///   - Read-only wrappers (Viewing, Inspecting) forward ONLY @readonly methods of T.
///   - All other wrappers forward any modification category.
///
/// Signature synthesis: params/return are taken from the inner method's signature
/// on the inner-generic-def type (e.g. List[T].getitem!).  GMP's
/// BuildConcreteRoutineInfo performs name-based substitution at monomorphization
/// time.  For methods whose return depends on the inner's generic param (e.g.
/// List[T].getitem! returning T), the forwarder is marked with
/// <see cref="RoutineInfo.WrapperForwarderInnerMethod"/> so GMP can re-resolve the
/// signature against the concrete inner type.
/// </summary>
internal sealed class WrapperForwardingPass
{
    private readonly TypeRegistry _registry;
    private readonly Dictionary<string, (RoutineInfo Routine, Statement Body)> _synthesizedBodies;
    private readonly HashSet<string> _synthesizedForwarderKeys;

    /// <summary>Synthetic source location used for compiler-generated AST nodes.</summary>
    private static readonly SourceLocation _synthLoc = new(FileName: "", Line: 0, Column: 0, Position: 0);

    /// <summary>
    /// All wrapper types recognized by the compiler for layout/dispatch purposes
    /// (codegen write-through, GMP body selection, auto-wired registration, etc.).
    /// </summary>
    private static readonly IReadOnlySet<string> WrapperTypes = RuntimeContract.WrapperTypes;

    /// <summary>
    /// Wrapper types that transparently forward inner-type methods. Hijacked[T] is the
    /// raw-pointer escape hatch — callers must explicitly use peek() / as_entity() — so
    /// it is excluded here even though it is a wrapper for layout purposes.
    /// </summary>
    private static readonly IReadOnlySet<string> ForwardingWrapperTypes =
        RuntimeContract.ForwardingWrapperTypes;

    /// <summary>
    /// Method names that codegen invokes implicitly on wrappers without going through
    /// semantic analysis (so the lazy synthesis path in member-access / call dispatch
    /// will not fire for them). RunEager seeds these for every concrete wrapper instance;
    /// all other methods are synthesized lazily on first reference.
    /// </summary>
    private static readonly HashSet<string> ImplicitlyInvokedMethods =
    [
        "destroy",
        // Operators/hashing/display: invoked from generic stdlib container
        // bodies after monomorphization, so they bypass SA's lazy synthesis
        // path. Wrappers do not define these themselves — they transparently
        // forward to inner T (e.g. Text.eq -> Text.eq).
        "eq",
        "ne",
        "cmp",
        "lt",
        "le",
        "gt",
        "ge",
        "hash",
        "represent",
        "diagnose"
    ];

    /// <summary>
    /// Exposes the wrapper type base names so GMP can detect wrapper methods during body selection.
    /// </summary>
    internal static IReadOnlySet<string> WrapperTypeNames => WrapperTypes;

    /// <summary>
    /// Read-only wrapper types that can only access @readonly methods.
    /// </summary>
    private static readonly IReadOnlySet<string> ReadOnlyWrapperTypes =
        RuntimeContract.ReadOnlyWrapperTypes;

    public WrapperForwardingPass(TypeRegistry registry,
        Dictionary<string, (RoutineInfo Routine, Statement Body)> synthesizedBodies,
        HashSet<string> synthesizedForwarderKeys)
    {
        _registry = registry;
        _synthesizedBodies = synthesizedBodies;
        _synthesizedForwarderKeys = synthesizedForwarderKeys;
    }

    /// <summary>
    /// Eagerly synthesizes forwarders on all concrete wrapper-type instantiations for every
    /// method found on their inner type.  Called after stdlib body analysis so that wrapper
    /// methods used only implicitly (e.g. release() via scope cleanup) are still forwarded.
    /// </summary>
    public void RunEager()
    {
        // Collect from both resolution caches: RecordTypeInfo resolutions AND WrapperTypeInfo resolutions.
        var candidates =
            _registry.AllConcreteGenericInstances
                     .Where(predicate: IsWrapperType)
                     .Concat(_registry.AllConcreteWrapperInstances)
                     .Distinct()
                     .ToList();

        foreach (TypeSymbol wrapperType in candidates)
        {
            TypeSymbol? innerType = GetWrapperInnerType(wrapperType: wrapperType);
            if (innerType is null or GenericParameterTypeInfo)
                continue;

            TypeSymbol innerLookupType = innerType switch
            {
                RecordTypeInfo { GenericDefinition: { } d } => d,
                EntityTypeInfo { GenericDefinition: { } d } => d,
                _ => innerType
            };

            // Narrow to implicit-call methods only. User-visible calls hit the lazy path
            // (TrySynthesizeWrapperForwarder in member-access / call dispatch); only methods
            // codegen invokes without semantic analysis (scope cleanup, RC ops) need eager
            // seeding. This avoids fanning out a forwarder per (wrapper × every method of T).
            foreach (RoutineInfo innerMethod in _registry.GetMethodsForOwner(ownerType: innerLookupType))
            {
                if (!ImplicitlyInvokedMethods.Contains(item: innerMethod.Name))
                    continue;
                if (innerMethod.Annotations.Contains(value: "innate")) continue;
                TrySynthesize(wrapperType: wrapperType,
                    methodName: innerMethod.Name,
                    isFailable: innerMethod.IsFailable);
            }
        }
    }

    /// <summary>
    /// Attempts to synthesize a forwarding routine on a wrapper type that delegates to
    /// a matching method on the wrapper's inner type T. Returns null if synthesis is
    /// not applicable (not a wrapper, no inner T, no matching inner method, or read-only
    /// wrapper rejecting a non-readonly inner method).
    /// </summary>
    public RoutineInfo? TrySynthesize(TypeSymbol wrapperType, string methodName, bool isFailable)
    {
        if (!IsWrapperType(type: wrapperType))
        {
            return null;
        }

        // create and destroy are type-lifecycle methods, not instance methods.
        // Forwarding them would generate `Hijacked[T](me)` in the body but `me` is
        // not set up for create (constructor) methods — skip unconditionally.
        if (methodName is "create" or "destroy")
            return null;

        TypeSymbol? wrapperDef = wrapperType switch
        {
            RecordTypeInfo { GenericDefinition: { } def } => def,
            EntityTypeInfo { GenericDefinition: { } def } => def,
            WrapperTypeInfo => _registry.LookupType(name: wrapperType.Name),
            _ => wrapperType
        };

        if (wrapperDef == null || !wrapperDef.IsGenericDefinition ||
            wrapperDef.GenericParameters is not { Count: 1 })
        {
            return null;
        }

        string genericParamName = wrapperDef.GenericParameters[index: 0];

        TypeSymbol? innerType = GetWrapperInnerType(wrapperType: wrapperType);
        if (innerType == null)
        {
            return null;
        }

        TypeSymbol innerLookupType = innerType switch
        {
            RecordTypeInfo { GenericDefinition: { } d } => d,
            EntityTypeInfo { GenericDefinition: { } d } => d,
            _ => innerType
        };

        // Resolve the method against the CONCRETE inner type first (e.g. `Box[S64]`, `List[S64]`) so a
        // generic entity's owner type parameters bind (`Box[T].get` → `Box[S64].get -> S64`). Falling
        // straight to the generic DEFINITION (innerLookupType = `Box[T]`) would return an
        // un-monomorphized `-> T` method, which codegen rejects ("no concrete SA-resolved return type").
        // Non-generic inners have innerType == innerLookupType, so this is a no-op there.
        RoutineInfo? innerMethod =
            _registry.LookupMethod(type: innerType, methodName: methodName, isFailable: isFailable)
            ?? _registry.LookupMethod(type: innerLookupType, methodName: methodName,
                isFailable: isFailable);
        if (innerMethod == null)
        {
            return null;
        }

        // Representation-unified Suflae entity method: its `me` is ALREADY `Roamed[E]` (SignatureResolver
        // sets MeType), so the "bare" method IS the Roamed method — it does its own lock_enter + project
        // through RoamController.data. Wrapping it in the projecting forwarder below would project the
        // controller to the entity and hand a BARE entity to a method that projects AGAIN → double
        // projection → the controller header is read as entity fields → crash. So resolve a `Roamed[E]`
        // receiver call straight to the inner method (passing the Roamed handle as `me`), exactly as a
        // receiver that was still bare at SA already does. No forwarder is registered.
        if (wrapperType.BareName == RuntimeContract.Roamed
            && innerMethod.MeType is RecordTypeInfo { GenericDefinition.Name: RuntimeContract.Roamed }
                                  or WrapperTypeInfo { Name: RuntimeContract.Roamed })
        {
            return innerMethod;
        }

        if (IsReadOnlyWrapper(type: wrapperType) && !innerMethod.IsReadOnly)
        {
            return null;
        }

        // Roamed failable forwarders: the `when`-re-propagation body IS built (see
        // BuildWrapperForwarderBody's isFailable path) but is currently GATED OFF — the synthesized
        // re-throw's `Core.Crashable.crash_message` gets reachability-pruned ("declared+called but never
        // defined"); the seed attempt in RoutineReachabilityPass (LookupType("Crashable")) did not
        // resolve it. Re-enable by fixing that seed (find the correct crash_message owner/lookup).
        if (wrapperType.BareName == RuntimeContract.Roamed
            && innerMethod.IsFailable)
        {
            return null;
        }


        string cacheKey = $"{wrapperDef.Name}.{methodName}#{(isFailable ? "!" : "")}";
        if (!_synthesizedForwarderKeys.Add(item: cacheKey))
        {
            var cached = _registry.LookupMethod(type: wrapperType,
                methodName: methodName,
                isFailable: isFailable) ??
                _registry.LookupMethod(type: wrapperDef,
                    methodName: methodName,
                    isFailable: isFailable);
            return cached;
        }

        // Don't overwrite a method already defined on the wrapper's generic def
        // (source-defined routines like represent, diagnose, destroy take precedence).
        var existingOnDef = _registry.LookupMethod(type: wrapperDef,
                methodName: methodName,
                isFailable: isFailable);
        if (existingOnDef != null)
        {
            return _registry.LookupMethod(type: wrapperType,
                methodName: methodName,
                isFailable: isFailable);
        }

        // Filter out owner-level generics from the inner method's GenericParameters.
        // `BTreeSetNode[T].keys_add_last(value: T)` registers a RoutineInfo whose
        // GenericParameters carries `T` (the owner-level param) — propagating that onto the
        // forwarder makes the forwarder look method-generic in T, so GMP later mangles it as
        // `Owned[BTreeSetNode[S64]].keys_add_last[S64]` while codegen call sites use the
        // un-suffixed `Owned[BTreeSetNode[S64]].keys_add_last`. Strip owner-level params so
        // only true method-level generics (e.g. `Hijacked[T].recast_as[U]` -> `[U]`) survive.
        List<string>? innerOwnerParams = innerLookupType.GenericParameters;
        List<string>? filteredGenericParams = innerMethod.GenericParameters;
        if (filteredGenericParams is { Count: > 0 } && innerOwnerParams is { Count: > 0 })
        {
            filteredGenericParams = filteredGenericParams
                .Where(predicate: gp => !innerOwnerParams.Contains(value: gp))
                .ToList();
            if (filteredGenericParams.Count == 0) filteredGenericParams = null;
        }
        List<GenericConstraintDeclaration>? filteredConstraints = innerMethod.GenericConstraints;
        if (filteredConstraints is { Count: > 0 } && innerOwnerParams is { Count: > 0 })
        {
            filteredConstraints = filteredConstraints
                .Where(predicate: c => !innerOwnerParams.Contains(value: c.ParameterName))
                .ToList();
            if (filteredConstraints.Count == 0) filteredConstraints = null;
        }

        // Resolve name collisions between the wrapper's generic params and the inner method's
        // owner-level generic params (both commonly use `T`). The forwarder's signature carries
        // the inner method's parameter types verbatim, which reference inner-T by name. Without
        // distinct names, GMP's typeSubs dict gets a single key `T` mapping to the wrapper's
        // inner instance (e.g. `T -> BTreeSetNode[S64]`) and never registers `inner-T -> S64`.
        // Result: a value-of-T parameter codegens as ptr instead of i64 → ABI mismatch.
        //
        // Rename colliding inner names so the forwarder's param/return types reference a
        // disambiguated GenericParameterTypeInfo with a `ForwarderOriginalName` marker.
        // Substitution sites (GenericAstRewriter.ResolveType, codegen's ResolveTypeSubstitution,
        // BuildResolvedRoutineTypeSubstitutions) check that property to recover the original
        // inner-param name — this avoids string-matching a `__rfwd_T__` sentinel everywhere.
        Dictionary<string, TypeInfo>? innerRename = null;
        if (innerOwnerParams is { Count: > 0 } &&
            wrapperDef.GenericParameters is { Count: > 0 } wrapperParams)
        {
            foreach (string ip in innerOwnerParams)
            {
                if (!wrapperParams.Contains(value: ip)) continue;
                innerRename ??= new Dictionary<string, TypeInfo>();
                // The Name still has to be unique vs the wrapper's own param so dict-keyed
                // lookups don't collide; the structural marker is `ForwarderOriginalName`.
                innerRename[ip] = new GenericParameterTypeInfo(name: $"__rfwd_{ip}__")
                {
                    ForwarderOriginalName = ip
                };
            }
        }

        List<ParameterInfo> forwarderParameters = innerMethod.Parameters;
        TypeSymbol? forwarderReturnType = innerMethod.ReturnType;
        if (innerRename is { Count: > 0 })
        {
            forwarderParameters = innerMethod.Parameters
                .Select(selector: p => p.WithSubstitutedType(
                    newType: RoutineInfo.SubstituteType(type: p.Type, substitution: innerRename)))
                .ToList();
            if (forwarderReturnType != null)
            {
                forwarderReturnType = RoutineInfo.SubstituteType(
                    type: forwarderReturnType, substitution: innerRename);
            }
        }

        var forwarder = new RoutineInfo(name: innerMethod.Name)
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = wrapperDef,
            Parameters = forwarderParameters,
            ReturnType = forwarderReturnType,
            IsFailable = innerMethod.IsFailable,
            DeclaredMutation = innerMethod.DeclaredMutation,
            MutationCategory = innerMethod.MutationCategory,
            Visibility = innerMethod.Visibility,
            Location = innerMethod.Location,
            Module = innerMethod.Module,
            Annotations = innerMethod.Annotations,
            IsSynthesized = true,
            WrapperForwarderInnerMethod = innerMethod,
            WrapperForwarderInnerGenericDef = innerLookupType,
            GenericParameters = filteredGenericParams,
            GenericConstraints = filteredConstraints,
        };

        // RC record wrappers (Retained[T], Tracked[T]) are structs with a `data: Hijacked[T]`
        // field — cannot cast `me` to a pointer. Pointer wrappers use Hijacked[T](me) directly.
        // Detect by checking for an actual `data` member on the record def.
        string? dataFieldName = wrapperDef is RecordTypeInfo recDef &&
                                recDef.LookupMemberVariable(memberVariableName: "data") != null
            ? "data"
            : null;

        Statement body = BuildWrapperForwarderBody(
            wrapperType: wrapperDef,
            innerMethod: innerMethod,
            genericParamName: genericParamName,
            methodName: innerMethod.Name,
            isFailable: innerMethod.IsFailable,
            parameters: innerMethod.Parameters,
            hasReturnValue: innerMethod.ReturnType != null &&
                innerMethod.ReturnType.Name != "None",
            dataFieldName: dataFieldName,
            innerIsEntity: innerType is EntityTypeInfo);

        _registry.RegisterRoutine(routine: forwarder);
        _synthesizedBodies[key: forwarder.RegistryKey] = (forwarder, body);

        return _registry.LookupMethod(type: wrapperType,
            methodName: methodName,
            isFailable: isFailable) ?? forwarder;
    }

    /// <summary>
    /// Builds the AST body:
    ///
    ///   Pointer wrappers (dataFieldName == null):
    ///     danger
    ///       var raw = Hijacked[T](me)
    ///       [return] raw.extract().methodName(param1: param1, ...)
    ///
    ///   Record-struct wrappers (dataFieldName == "data"):
    ///     danger
    ///       [return] me.data.extract().methodName(param1: param1, ...)
    ///
    /// where T is the wrapper's generic parameter name.
    /// </summary>
    private DangerStatement BuildWrapperForwarderBody(TypeSymbol wrapperType, RoutineInfo innerMethod,
        string genericParamName,
        string methodName, bool isFailable, List<ParameterInfo> parameters,
        bool hasReturnValue, string? dataFieldName = null, bool innerIsEntity = false) // NOSONAR S3776
    {
        // The forwarded call's name is always bare; its failability is carried structurally on the
        // callee MemberExpression (IsFailable), never appended to the name.
        string callPropertyName = methodName;
        TypeSymbol innerType = wrapperType.TypeArguments is { Count: > 0 }
            ? wrapperType.TypeArguments[0]
            : new GenericParameterTypeInfo(name: genericParamName);
        TypeInfo? wrapperDataType = dataFieldName != null
            ? (wrapperType as RecordTypeInfo)?.LookupMemberVariable(memberVariableName: dataFieldName)?.Type
            : null;
        var forwardedArgs = new List<Expression>();
        foreach (ParameterInfo p in parameters)
        {
            if (p.Name == "me")
                continue;
            forwardedArgs.Add(item: new NamedArgumentExpression(
                Name: p.Name,
                Value: new IdentifierExpression(Name: p.Name, Location: _synthLoc),
                Location: _synthLoc));
        }

        List<Statement> innerStatements;

        if (dataFieldName != null)
        {
            // Record-struct wrapper: me.data.peek().method(...)
            // Skip the `raw` variable entirely — no type inference needed.
            var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
                { ResolvedType = wrapperType };
            var dataAccess = new MemberExpression(
                Object: meRef,
                MemberName: dataFieldName,
                Location: _synthLoc)
            {
                ResolvedType = wrapperDataType
            };
            RoutineInfo? extractMethod = wrapperDataType != null
                ? _registry.LookupMethod(type: wrapperDataType, methodName: RuntimeContract.RawPointer.Peek)
                : null;
            var readCall = new CallExpression(
                Callee: new MemberExpression(
                    Object: dataAccess,
                    MemberName: RuntimeContract.RawPointer.Peek,
                    Location: _synthLoc),
                Arguments: [],
                Location: _synthLoc)
            {
                ResolvedRoutine = extractMethod,
                ResolvedType = innerType
            };
            var innerCall = new CallExpression(
                Callee: new MemberExpression(
                    Object: readCall,
                    MemberName: callPropertyName,
                    Location: _synthLoc) { IsFailable = isFailable },
                Arguments: forwardedArgs,
                Location: _synthLoc)
            {
                // ResolvedRoutine intentionally left null: this forwarder is generated once
                // per wrapperDef and reused across all inner T. Baking innerMethod here would
                // freeze the call to whichever inner type was resolved first (e.g. binding
                // to BTreeListNode.keys_add_last forever, even when monomorphized for
                // Modifying[BTreeSetNode[S64]]). Leaving it null lets RoutineReachabilityPass
                // re-resolve the call from the substituted receiver type at monomorphization.
                ResolvedType = innerMethod.ReturnType
            };
            Statement callStmt = hasReturnValue
                ? new ReturnStatement(Value: innerCall, Location: _synthLoc)
                : new ExpressionStatement(Expression: innerCall, Location: _synthLoc);
            innerStatements = [callStmt];
        }
        else if (wrapperType.BareName is RuntimeContract.Retained or RuntimeContract.Tracked or RuntimeContract.Roamed)
        {
            // RC wrappers: `me` is a ptr to `RetainController[T]`, NOT to T directly. Reaching
            // T requires double-indirection through the controller's `data: Hijacked[T]` field:
            //
            //   danger
            //     var raw  = Hijacked[RetainController[T]](me)
            //     var ctrl = raw.as_entity()              # RetainController[T] ptr
            //     [return] ctrl.borrow_data().as_entity().method(args...)
            //
            // Without this branch, the pointer-wrapper branch below would emit
            // `Hijacked[T](me).as_entity().method(...)`, treating the controller's strong+weak
            // counts (first 8 bytes) as if they were T's first 8 bytes.
            // A `Roaming` guard indirects through `RoamController.data_ptr()`; Retained/Tracked through
            // `RetainController.borrow_data()`. Both just reach the inner entity — for `Roaming` the
            // lock is already held by the enclosing `using` (enter), so the forwarder only reaches +
            // calls (release happens at exit on every path).
            bool isRoamed = wrapperType.BareName == RuntimeContract.Roamed;
            bool viaRoamController = isRoamed;
            string controllerName = viaRoamController ? "RoamController" : "RetainController";
            string dataRevealName = viaRoamController
                ? "data_ptr"
                : RuntimeContract.RefCount.BorrowData;
            var controllerTypeExpr = new TypeExpression(
                Name: controllerName,
                GenericArguments:
                [
                    new TypeExpression(Name: genericParamName, GenericArguments: null,
                        Location: _synthLoc)
                ],
                Location: _synthLoc);
            var hijackedCtrlCtor = new CreatorExpression(
                TypeName: RuntimeContract.Hijacked,
                TypeArguments: [controllerTypeExpr],
                MemberVariables:
                    [("", new IdentifierExpression(Name: "me", Location: _synthLoc))],
                Location: _synthLoc);
            var rawDecl = new DeclarationStatement(
                Declaration: new VariableDeclaration(
                    Name: "raw",
                    Type: null,
                    Initializer: hijackedCtrlCtor,
                    Visibility: VisibilityModifier.Open,
                    Location: _synthLoc),
                Location: _synthLoc);
            // Build TypeInfo annotations so codegen's type-resolution gate accepts the
            // synthesized AST. Mirror the pointer-wrapper branch below: ResolvedType on
            // each `raw`/`ctrl` identifier and ResolvedRoutine + ResolvedType on each Call.
            // The inner T may still be a GenericParameterTypeInfo at synth time; codegen's
            // ApplyTypeSubstitutions substitutes T at monomorphization.
            //
            // Annotate with the OPEN instantiation RetainController[T] (T = the wrapper's
            // param), never the bare generic def. The shared synth body is re-resolved by
            // later consumers (SA lazy analysis, GMP rewrite, codegen re-lookup) under a
            // substitution keyed on T; the open form is idempotent there — the same shape
            // SA bakes into Retained.rf's source bodies — while a bare def gets freshly
            // instantiated with whatever binding is at hand, double-wrapping the controller
            // (RetainController[RetainController[X]]) and killing forwarder body emission
            // (undefined symbol at link).
            TypeSymbol? retainControllerDef = _registry.LookupType(name: controllerName);
            TypeSymbol? retainControllerType = retainControllerDef is { IsGenericDefinition: true }
                ? _registry.GetOrCreateResolution(genericDef: retainControllerDef,
                    typeArguments: [innerType])
                : retainControllerDef;
            TypeSymbol hijackedCtrlType = new WrapperTypeInfo(
                wrapperName: RuntimeContract.Hijacked,
                innerType: retainControllerType ?? innerType,
                isReadOnly: false);
            TypeSymbol hijackedInnerType = new WrapperTypeInfo(
                wrapperName: RuntimeContract.Hijacked,
                innerType: innerType,
                isReadOnly: false);
            RoutineInfo? ctrlRevealMethod = _registry.LookupMethod(
                type: hijackedCtrlType, methodName: RuntimeContract.RawPointer.AsEntity);
            RoutineInfo? borrowDataMethod = retainControllerType != null
                ? _registry.LookupMethod(type: retainControllerType, methodName: dataRevealName)
                : null;
            RoutineInfo? innerRevealMethod = _registry.LookupMethod(
                type: hijackedInnerType, methodName: RuntimeContract.RawPointer.AsEntity);

            var ctrlCall = new CallExpression(
                Callee: new MemberExpression(
                    Object: new IdentifierExpression(Name: "raw", Location: _synthLoc)
                        { ResolvedType = hijackedCtrlType },
                    MemberName: RuntimeContract.RawPointer.AsEntity,
                    Location: _synthLoc),
                Arguments: [],
                Location: _synthLoc)
            {
                ResolvedRoutine = ctrlRevealMethod,
                ResolvedType = retainControllerType
            };
            var ctrlDecl = new DeclarationStatement(
                Declaration: new VariableDeclaration(
                    Name: "ctrl",
                    Type: null,
                    Initializer: ctrlCall,
                    Visibility: VisibilityModifier.Open,
                    Location: _synthLoc),
                Location: _synthLoc);
            var borrowCall = new CallExpression(
                Callee: new MemberExpression(
                    Object: new IdentifierExpression(Name: "ctrl", Location: _synthLoc)
                        { ResolvedType = retainControllerType },
                    MemberName: RuntimeContract.RefCount.BorrowData,
                    Location: _synthLoc),
                Arguments: [],
                Location: _synthLoc)
            {
                ResolvedRoutine = borrowDataMethod,
                ResolvedType = hijackedInnerType
            };
            var innerRevealCall = new CallExpression(
                Callee: new MemberExpression(
                    Object: borrowCall,
                    MemberName: RuntimeContract.RawPointer.AsEntity,
                    Location: _synthLoc),
                Arguments: [],
                Location: _synthLoc)
            {
                ResolvedRoutine = innerRevealMethod,
                ResolvedType = innerType
            };
            var innerCall = new CallExpression(
                Callee: new MemberExpression(
                    Object: innerRevealCall,
                    MemberName: callPropertyName,
                    Location: _synthLoc) { IsFailable = isFailable },
                Arguments: forwardedArgs,
                Location: _synthLoc)
            {
                // ResolvedRoutine intentionally null — see record-struct branch for reasoning.
                ResolvedType = innerMethod.ReturnType
            };
            if (isRoamed)
            {
                // Mode-checked lock, released EXPLICITLY (synthesized forwarder bodies are not run
                // through ScopeTeardownLoweringPass, so an owned-guard destroy would never be inserted).
                RoutineInfo? lockEnter = _registry.LookupMethod(type: wrapperType, methodName: "lock_enter");
                RoutineInfo? lockExit = _registry.LookupMethod(type: wrapperType, methodName: "lock_exit");
                ExpressionStatement MkLock(RoutineInfo? m, string verb) => new ExpressionStatement(
                    Expression: new CallExpression(
                        Callee: new MemberExpression(
                            Object: new IdentifierExpression(Name: "me", Location: _synthLoc) { ResolvedType = wrapperType },
                            MemberName: verb, Location: _synthLoc),
                        Arguments: [], Location: _synthLoc) { ResolvedRoutine = m },
                    Location: _synthLoc);

                if (isFailable)
                {
                    // Failable: call the throw-based `check_` variant (non-propagating carrier), then a
                    // `when` re-propagates AFTER releasing the lock in each arm — mirrors
                    // ErrorHandlingVariantPass.BuildCarrierPropagationWhen, but with lock_exit inserted
                    // so the lock is freed on BOTH the failure (throw) and success paths.
                    TypeSymbol innerDef = innerType switch
                    {
                        EntityTypeInfo { GenericDefinition: { } ed } => ed,
                        RecordTypeInfo { GenericDefinition: { } rd } => rd,
                        _ => innerType
                    };
                    RoutineInfo? checkM = _registry.LookupMethod(type: innerDef,
                        methodName: "check_" + methodName, isFailable: false);
                    var checkSubject = new CallExpression(
                        Callee: new MemberExpression(Object: innerRevealCall,
                            MemberName: "check_" + methodName, Location: _synthLoc),
                        Arguments: forwardedArgs, Location: _synthLoc)
                    { ResolvedType = checkM?.ReturnType };
                    var whenStmt = new WhenStatement(
                        Expression: checkSubject,
                        Clauses:
                        [
                            new WhenClause(
                                Pattern: new CrashablePattern(ErrorType: null, VariableName: "__rf_e", Location: _synthLoc),
                                Body: new BlockStatement(
                                    Statements: [MkLock(lockExit, "lock_exit"),
                                        new ThrowStatement(Error: new IdentifierExpression(Name: "__rf_e", Location: _synthLoc), Location: _synthLoc)],
                                    Location: _synthLoc),
                                Location: _synthLoc),
                            new WhenClause(
                                Pattern: new ElsePattern(VariableName: "__rf_v", Location: _synthLoc),
                                Body: new BlockStatement(
                                    Statements: [MkLock(lockExit, "lock_exit"),
                                        new ReturnStatement(Value: new IdentifierExpression(Name: "__rf_v", Location: _synthLoc), Location: _synthLoc)],
                                    Location: _synthLoc),
                                Location: _synthLoc)
                        ],
                        Location: _synthLoc);
                    innerStatements = [MkLock(lockEnter, "lock_enter"), rawDecl, ctrlDecl, whenStmt];
                }
                else if (hasReturnValue)
                {
                    Statement resultDecl = new DeclarationStatement(
                        Declaration: new VariableDeclaration(Name: "__rf_locked", Type: null,
                            Initializer: innerCall, Visibility: VisibilityModifier.Open, Location: _synthLoc),
                        Location: _synthLoc);
                    Statement retStmt = new ReturnStatement(
                        Value: new IdentifierExpression(Name: "__rf_locked", Location: _synthLoc)
                            { ResolvedType = innerMethod.ReturnType },
                        Location: _synthLoc);
                    innerStatements = [MkLock(lockEnter, "lock_enter"), rawDecl, ctrlDecl, resultDecl, MkLock(lockExit, "lock_exit"), retStmt];
                }
                else
                {
                    innerStatements = [MkLock(lockEnter, "lock_enter"), rawDecl, ctrlDecl,
                        new ExpressionStatement(Expression: innerCall, Location: _synthLoc), MkLock(lockExit, "lock_exit")];
                }
            }
            else
            {
                Statement callStmt = hasReturnValue
                    ? new ReturnStatement(Value: innerCall, Location: _synthLoc)
                    : new ExpressionStatement(Expression: innerCall, Location: _synthLoc);
                innerStatements = [rawDecl, ctrlDecl, callStmt];
            }
        }
        else
        {
            // Pointer wrapper: var raw = Hijacked[T](me); raw.as_entity()/peek().method(...)
            // Entity inner types: as_entity() reinterprets the ptr directly as T (no dereference)
            //   — correct for T where me IS the entity ptr, not a slot holding one.
            // Record inner types: peek() dereferences the ptr to load the value — correct
            //   for Hijacked[RecordType] where the ptr points to a heap/stack slot.
            // innerIsEntity is determined from the concrete inner type at the call site so
            //   generic-def forwarder bodies (where innerType is GenericParameterTypeInfo)
            //   get the correct access method even before T is substituted.
            string accessMethodName = innerIsEntity ? RuntimeContract.RawPointer.AsEntity : RuntimeContract.RawPointer.Peek;
            var hijackedCall = new CreatorExpression(
                TypeName: RuntimeContract.Hijacked,
                TypeArguments:
                [
                    new TypeExpression(Name: genericParamName, GenericArguments: null,
                        Location: _synthLoc)
                ],
                MemberVariables:
                    [("", new IdentifierExpression(Name: "me", Location: _synthLoc))],
                Location: _synthLoc);
            var rawDecl = new DeclarationStatement(
                Declaration: new VariableDeclaration(
                    Name: "raw",
                    Type: null,
                    Initializer: hijackedCall,
                    Visibility: VisibilityModifier.Open,
                    Location: _synthLoc),
                Location: _synthLoc);
            TypeSymbol hijackedInnerType = new WrapperTypeInfo(
                wrapperName: RuntimeContract.Hijacked,
                innerType: innerType,
                isReadOnly: false);
            RoutineInfo? accessMethod = _registry.LookupMethod(type: hijackedInnerType,
                methodName: accessMethodName);
            var readCall = new CallExpression(
                Callee: new MemberExpression(
                    Object: new IdentifierExpression(Name: "raw", Location: _synthLoc)
                        { ResolvedType = hijackedInnerType },
                    MemberName: accessMethodName,
                    Location: _synthLoc),
                Arguments: [],
                Location: _synthLoc)
            {
                ResolvedRoutine = accessMethod,
                ResolvedType = innerType
            };
            var innerCall = new CallExpression(
                Callee: new MemberExpression(
                    Object: readCall,
                    MemberName: callPropertyName,
                    Location: _synthLoc) { IsFailable = isFailable },
                Arguments: forwardedArgs,
                Location: _synthLoc)
            {
                // ResolvedRoutine intentionally left null — see the record-struct branch above
                // for the full reasoning. Same issue applies to pointer wrappers.
                ResolvedType = innerMethod.ReturnType
            };
            Statement callStmt = hasReturnValue
                ? new ReturnStatement(Value: innerCall, Location: _synthLoc)
                : new ExpressionStatement(Expression: innerCall, Location: _synthLoc);
            innerStatements = [rawDecl, callStmt];
        }

        return new DangerStatement(
            Body: new BlockStatement(Statements: innerStatements, Location: _synthLoc),
            Location: _synthLoc);
    }

    /// <summary>
    /// Checks if a type is a forwarding wrapper (Viewing, Modifying, Shared, etc.).
    /// Hijacked is intentionally excluded — its API is the explicit extract/as_entity/inject
    /// surface, not transparent forwarding of T's methods.
    /// </summary>
    private static bool IsWrapperType(TypeSymbol type)
    {
        string baseName = type.BareName;
        return ForwardingWrapperTypes.Contains(value: baseName);
    }

    /// <summary>
    /// Checks if a wrapper type is read-only (Viewing, Inspecting).
    /// </summary>
    private static bool IsReadOnlyWrapper(TypeSymbol type)
    {
        string baseName = type.BareName;
        return ReadOnlyWrapperTypes.Contains(value: baseName);
    }

    /// <summary>
    /// Gets the inner type from a wrapper type (e.g., T from Viewing&lt;T&gt;).
    /// </summary>
    private TypeSymbol? GetWrapperInnerType(TypeSymbol wrapperType)
    {
        if (!IsWrapperType(type: wrapperType))
        {
            return null;
        }

        // Wrapper types have their inner type as the first type argument
        if (wrapperType.TypeArguments is { Count: > 0 })
        {
            return wrapperType.TypeArguments[index: 0];
        }

        return null;
    }
}
