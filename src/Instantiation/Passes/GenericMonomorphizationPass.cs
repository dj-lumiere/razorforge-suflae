using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Compiler.Desugaring;
using Compiler.Resolution;
using Compiler.Synthesis;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using Verification;

namespace Compiler.Instantiation.Passes;

/// <summary>
/// Phase 6 global pass that builds concrete generic method bodies before codegen.
/// so the code generator never needs to search programs or perform AST substitution
/// for the common case.
///
/// <para>
/// The pass iterates every concrete generic type instance recorded in the
/// <see cref="TypeRegistry"/> during Phase 5 (e.g., <c>List[S64]</c>, <c>Maybe[Text]</c>)
/// and generates <see cref="MonomorphizedBody"/> entries for each of the generic
/// definition's methods.  Bodies are sourced from three places:
/// <list type="bullet">
///   <item><see cref="DesugaringContext.VariantBodies"/> -> WiredRoutinePass-generated
///         bodies (<c>$represent</c>, <c>$diagnose</c>) and ErrorHandlingVariantPass
///         bodies (<c>try_next</c>, etc.).</item>
///   <item><c>Registry.StdlibPrograms</c> and <c>Registry.UserPrograms</c> AST declarations -> source bodies.</item>
/// </list>
/// Pure-synthesized methods (<see cref="RoutineInfo.IsSynthesized"/> = true with no
/// body anywhere) are skipped here; their AST bodies are produced by
/// <see cref="Compiler.Synthesis.WiredRoutinePass"/> and emitted via
/// <c>EmitSynthesizedBodyFromAst</c>.
/// </para>
///
/// <para>
/// Results are stored in <see cref="DesugaringContext.InstantiatedGenericBodies"/>,
/// keyed by the concrete routine's <see cref="RoutineInfo.RegistryKey"/>.
/// Codegen checks this map before doing its own AST search.
/// </para>
/// </summary>
public sealed class GenericMonomorphizationPass(DesugaringContext ctx)
{
    // Routine-declaration index

    // Key: routine name (e.g. "List[T].$getitem") -> list of matching declarations.
    // Built once in RunGlobal() before the fixed-point loop.
    private Dictionary<string, List<RoutineDeclaration>> _routineIndex = new();

    private void BuildRoutineIndex()
    {
        _routineIndex = new Dictionary<string, List<RoutineDeclaration>>();
        var allPrograms = ctx.Registry.StdlibPrograms.Concat(ctx.Registry.UserPrograms);
        foreach ((Program program, string _, string _) in allPrograms)
        {
            foreach (RoutineDeclaration decl in program.Declarations.OfType<RoutineDeclaration>())
            {
                if (!_routineIndex.TryGetValue(key: decl.Name, value: out List<RoutineDeclaration>? bucket))
                {
                    bucket = [];
                    _routineIndex[key: decl.Name] = bucket;
                }
                bucket.Add(item: decl);
            }
        }
    }
    // Public entry point

    /// <summary>
    /// Public entry point
    /// </summary>
    public void RunGlobal() // NOSONAR S3776
    {
        // Pre-build the routine-declaration index so FindInStdlib is O(1) per lookup.
        BuildRoutineIndex();

        // Process concrete generic instances. Start with the liveness-filtered set, then use
        // a push-based queue to pick up types discovered during body rewriting
        // (e.g. ListEmitter[Byte] registered by GenericAstRewriter when rewriting List[Byte].$iter).
        // Only types newly created by GetOrCreateResolution after tracking starts are enqueued —
        // pre-existing phantom types (BTreeDictNode stubs etc.) never enter the queue.
        bool timing = ctx.SaTiming;
        var sw = timing ? Stopwatch.StartNew() : null;

        var processedTypes = new HashSet<string>(StringComparer.Ordinal);

        // Enable push-based discovery before the first ProcessConcreteType call so any types
        // created during rewriting are captured immediately.
        ctx.Registry.StartGmpDiscoveryTracking();

        // Seed with liveness-filtered instances + wrapper instances.
        TypeInfo[] initialInstances = ctx.Registry.AllConcreteGenericInstances
            .Concat(second: ctx.Registry.AllConcreteWrapperInstances)
            .DistinctBy(type => type.FullName)
            .ToArray();
        foreach (TypeInfo concreteType in initialInstances)
        {
            processedTypes.Add(concreteType.FullName);
            ProcessConcreteType(concreteType);
        }

        // One-time pass over ALL concrete types in _resolutions that weren't in the liveness set.
        // Catches types created before GMP started (e.g. List[Bytes] resolved during SA
        // but not reached by the liveness walk). Self-nesting types (Hijacked^N) are blocked by
        // the guard in GetOrCreateResolution, so this scan terminates.
        // Materialize to a list first — ProcessConcreteType modifies _resolutions during iteration.
        foreach (TypeInfo preExisting in ctx.Registry.AllConcreteGenericInstancesUnfiltered.ToList())
        {
            if (!processedTypes.Add(preExisting.FullName)) continue;
            ProcessConcreteType(preExisting);
        }

        // Same for wrapper instances (Hijacked[T], T, etc.). Wrappers like Hijacked have
        // explicit method definitions in stdlib that need monomorphization for each concrete T,
        // but live in _wrapperResolutions and aren't enqueued by NotifyConcreteRegistration.
        foreach (TypeInfo preExisting in ctx.Registry.AllConcreteWrapperInstancesUnfiltered.ToList())
        {
            if (!processedTypes.Add(preExisting.FullName)) continue;
            ProcessConcreteType(preExisting);
        }

        // Fixed-point expansion: drain types created during body rewriting
        // (e.g. ListEmitter[Byte] registered when GenericAstRewriter rewrites List[Byte].$iter).
        // The self-nesting guard in GetOrCreateResolution prevents Hijacked^N infinite chains.
        List<TypeInfo> discovered;
        bool madeProgress;
        do
        {
            madeProgress = false;
            while ((discovered = ctx.Registry.DrainGmpDiscoveryQueue()).Count > 0)
            {
                foreach (TypeInfo newType in discovered)
                {
                    if (!processedTypes.Add(newType.FullName)) continue;
                    ProcessConcreteType(newType);
                    madeProgress = true;
                }
            }

            // Wrapper instances aren't enqueued by NotifyConcreteRegistration (it only handles
            // EntityTypeInfo/RecordTypeInfo). Re-scan _wrapperResolutions each round to pick up
            // new wrappers like Hijacked[Text] that GenericAstRewriter created while
            // rewriting List[Text] forwarder bodies.
            foreach (TypeInfo wrapper in ctx.Registry.AllConcreteWrapperInstancesUnfiltered.ToList())
            {
                if (!processedTypes.Add(wrapper.FullName)) continue;
                ProcessConcreteType(wrapper);
                madeProgress = true;
            }
        } while (madeProgress);

        if (timing)
        {
            sw!.Stop();
            Console.Error.WriteLine(value:
                $"[SA]     GMP initial reachable types: {initialInstances.Length} + {processedTypes.Count - initialInstances.Length} discovered ({sw.ElapsedMilliseconds} ms)");
        }

        // Scan built bodies for method-generic call sites (e.g. $getitem[U64]! called from
        // List[Bytes].$eq). SA only analyzes generic-def bodies, so these concrete
        // call sites are never registered in _routineResolutions. Register them now so
        // ProcessResolvedMethodGenericRoutines can build their bodies.
        ScanAndRegisterMethodGenericCallResolutions();

        ProcessResolvedMethodGenericRoutines();
        EmitGenericDefBuilderServiceBodies();
    }
    // Per-type processing

    private void ProcessConcreteType(TypeInfo concreteType) // NOSONAR S3776
    {
        // Strategy-B reachability gate at the type level: when LiveOwnerTypeNames is populated,
        // skip concrete instances that no reachable routine ever owned. This prevents
        // unreachable types like Array[BuildMode, 63] or BTreeListNode[Text] from emitting
        // try_next/$getitem!/etc. via the wired-routine bypass on the per-routine gate.
        if (ctx.LiveOwnerTypeNames.Count > 0
            && !ctx.LiveOwnerTypeNames.Contains(item: concreteType.FullName))
        {
            return;
        }

        TypeInfo? genDef = concreteType switch
        {
            EntityTypeInfo { GenericDefinition: { } d } => d,
            RecordTypeInfo { GenericDefinition: { } d } => d,
            WrapperTypeInfo wrapper => ctx.Registry.LookupType(name: wrapper.Name),
            _ => null
        };

        if (genDef?.GenericParameters == null || genDef.GenericParameters.Count == 0)
            return;

        List<TypeInfo>? typeArgs = concreteType.TypeArguments;
        if (typeArgs == null || typeArgs.Count != genDef.GenericParameters.Count)
            return;

        // Build type substitution maps.
        // stringSubs uses FullName (e.g. "T" ??"Core.S64") so rewritten AST type-expression
        // names are fully qualified. LookupType handles both "S64" and "Core.S64" via the
        // Core-prefix fallback, and GetOrCreateResolution stores types under both the FullName
        // key ("Hijacked[Core.Byte]") and the short-name alias ("Hijacked[Byte]").
        // typeSubs carries the resolved TypeInfo for ResolvedType annotation in GenericAstRewriter.
        var typeSubs = new Dictionary<string, TypeInfo>(capacity: genDef.GenericParameters.Count);
        var stringSubs = new Dictionary<string, string>(capacity: genDef.GenericParameters.Count);
        for (int i = 0; i < genDef.GenericParameters.Count; i++)
        {
            typeSubs[genDef.GenericParameters[i]] = typeArgs[i];
            stringSubs[genDef.GenericParameters[i]] = typeArgs[i].FullName;
        }

        foreach (RoutineInfo genMethod in ctx.Registry.GetMethodsForType(genDef))
        {
            RoutineInfo? concreteInfo = BuildConcreteRoutineInfo(
                genMethod: genMethod,
                concreteOwner: concreteType,
                typeSubs: typeSubs);

            // Null means the forwarder's inner type doesn't have this method — skip it.
            if (concreteInfo == null)
                continue;

            string key = concreteInfo.RegistryKey;
            if (ctx.InstantiatedGenericBodies.ContainsKey(key))
                continue;

            // Strategy-B reachability gate: when LiveRoutineKeys is populated, only emit bodies
            // for routines reachable from program entry points. Empty set disables the filter
            // (legacy fan-out behavior). RoutineReachabilityPass populates the set.
            // Wired routines bypass the gate: codegen/synthesis emit them unconditionally for
            // every live owner type. Types created during GMP body rewriting (e.g.,
            // ListEmitter[Byte] from List[Byte].$represent) post-date RoutineReachabilityPass
            // and so were never seeded — without this bypass their wired routines vanish.
            if (ctx.LiveRoutineKeys.Count > 0 && !ctx.LiveRoutineKeys.Contains(item: key)
                && !IsWiredRoutineName(genMethod.Name))
                continue;

            // Capability gate: skip wired comparison/containment/hashing routines whose
            // generic-def constraint (e.g. `needs T obeys Equatable`) is not satisfied by
            // this concrete owner. See RoutineApplicableToConcreteOwner.
            if (!RoutineApplicableToConcreteOwner(routine: concreteInfo, owner: concreteType))
                continue;

            MonomorphizedBody? body = BuildBody(
                genMethod: genMethod,
                concreteInfo: concreteInfo,
                genDef: genDef,
                typeSubs: typeSubs,
                stringSubs: stringSubs);

            if (body != null)
                ctx.InstantiatedGenericBodies[key] = body;
        }
    }

    /// Routines emitted for every live owner regardless of call-site reachability. Two sources:
    /// (1) the unified-teardown lifecycle routines (<c>$destroy</c>/<c>$copy</c>, from
    /// <see cref="Compiler.Resolution.WiredRoutineCatalog.AlwaysLiveNames"/>) — scope-exit teardown
    /// inserts <c>$destroy</c> calls that must always have a concrete body, and the matching
    /// retaining <c>$copy</c> likewise; (2) <c>try_next</c>, reachable only through synthesized
    /// for-loop iteration bodies whose owner type (ListEmitter[Byte], etc.) is created post-pass
    /// during GMP body rewriting, so ReachabilityPass cannot trace it. Kept narrow otherwise —
    /// broader sets cascade into derived-op chains ($ne->$eq) where the missing companion is the
    /// actual culprit.
    private static readonly HashSet<string> _gateBypassNames =
        new(collection: Compiler.Resolution.WiredRoutineCatalog.AlwaysLiveNames,
            comparer: StringComparer.Ordinal) { "try_next" };

    private static bool IsWiredRoutineName(string name) => _gateBypassNames.Contains(name);

    /// <summary>
    /// Returns false when this concrete instantiation does not actually have the wired
    /// routine. Example: `Array[T, N].$eq` is declared `needs T obeys Equatable` — for
    /// `T = X` (not equatable), the routine does not exist on this owner. Body
    /// emission must skip it so derived companions (`$ne`, `$notcontains`) don't reference
    /// a missing symbol downstream in codegen. The actual protocol-to-wired-routine map
    /// lives in <see cref="TypeRegistry"/> (single source of truth).
    /// </summary>
    private bool RoutineApplicableToConcreteOwner(RoutineInfo routine, TypeInfo owner)
        => ctx.Registry.TypeHasWiredRoutine(type: owner, wiredName: routine.Name);

    private void ProcessResolvedMethodGenericRoutines()
    {
        var processed = new HashSet<string>(StringComparer.Ordinal);
        bool discoveredNew;
        do
        {
            discoveredNew = false;
            foreach (RoutineInfo resolvedRoutine in ctx.Registry.GetAllRoutineResolutions().ToList())
            {
                if (!processed.Add(item: resolvedRoutine.RegistryKey))
                {
                    continue;
                }

                discoveredNew = true;
                if (resolvedRoutine.GenericDefinition == null ||
                    ctx.InstantiatedGenericBodies.ContainsKey(resolvedRoutine.RegistryKey) ||
                    ctx.VariantBodies.ContainsKey(resolvedRoutine.RegistryKey) ||
                    resolvedRoutine.OwnerType is ProtocolTypeInfo)
                {
                    continue;
                }

                // Reachability gate: SA's RoutineResolutions index includes routines that were
                // type-checked during analysis but never reached from program entry points.
                // ProcessConcreteType applies this gate at line ~222; this path historically
                // bypassed it, causing Phase B to emit unreachable bodies whose call sites
                // reference further unreachable routines (linker errors).
                if (ctx.LiveRoutineKeys.Count > 0
                    && !ctx.LiveRoutineKeys.Contains(item: resolvedRoutine.RegistryKey)
                    && !IsWiredRoutineName(resolvedRoutine.Name))
                {
                    continue;
                }

                if (resolvedRoutine.OwnerType is { } resolvedOwner
                    && !RoutineApplicableToConcreteOwner(routine: resolvedRoutine, owner: resolvedOwner))
                {
                    continue;
                }

                Dictionary<string, TypeInfo> typeSubs =
                    BuildResolvedRoutineTypeSubstitutions(resolvedRoutine);
                if (typeSubs.Count == 0 ||
                    typeSubs.Values.Any(predicate: HasUnresolvedTypeArgs))
                {
                    continue;
                }

                string astName = BuildAstNameForResolvedRoutine(resolvedRoutine);
                RoutineDeclaration? astDecl = FindInStdlib(
                    genericAstName: astName,
                    expectedParamCount: resolvedRoutine.GenericDefinition.Parameters.Count,
                    typeSubs: typeSubs,
                    expectedParamNames: resolvedRoutine.GenericDefinition.Parameters
                        .Select(static p => p.Name).ToList(),
                    expectedParamTypeNames: resolvedRoutine.GenericDefinition.Parameters
                        .Select(static p => (string?)p.Type?.Name).ToList());
                if (astDecl == null)
                {
                    // Check if the generic definition has a synthesized body in VariantBodies.
                    // ProcessConcreteType->BuildBody checks genMethod.RegistryKey (the generic def key),
                    // but this path only checked resolvedRoutine.RegistryKey (the concrete key).
                    // Example: List[T].$eq body is stored under "Core.List[T].$eq#Core.List[T]",
                    // but resolvedRoutine.RegistryKey is "Core.List[Core.Byte].$eq#Core.List[Core.Byte]".
                    string genDefRoutineKey = resolvedRoutine.GenericDefinition.RegistryKey;
                    if (ctx.VariantBodies.TryGetValue(key: genDefRoutineKey, out Statement? defVariantBody))
                    {
                        var stringSubs2 = typeSubs.ToDictionary(
                            keySelector: kvp => kvp.Key,
                            elementSelector: kvp => kvp.Value.FullName);
                        Statement rewritten = GenericAstRewriter.RewriteStatement(
                            defVariantBody, stringSubs2, typeSubs, ctx.Registry, resolvedRoutine);
                        ctx.InstantiatedGenericBodies[resolvedRoutine.RegistryKey] = new MonomorphizedBody(
                            Ast: WrapInShellDecl(name: resolvedRoutine.Name, body: rewritten,
                                info: resolvedRoutine),
                            Info: resolvedRoutine,
                            TypeSubs: typeSubs,
                            VariantStatus: null,
                            VariantInnerType: null,
                            IsSynthesized: true);
                        continue;
                    }
                    // Two-level generic wrapper forwarder: the generic-def body is stored under
                    // GenericDefinition.GenericDefinition (the T forwarder), not under
                    // GenericDefinition (the Text forwarder with method-level generic I).
                    // typeSubs already contains both T->Text (from owner) and I->U64 (from TypeArguments).
                    string? genDefGenDefKey = resolvedRoutine.GenericDefinition.GenericDefinition?.RegistryKey;
                    if (genDefGenDefKey != null &&
                        resolvedRoutine.GenericDefinition.WrapperForwarderInnerMethod != null &&
                        ctx.VariantBodies.TryGetValue(key: genDefGenDefKey, out Statement? genDefGenDefBody))
                    {
                        var stringSubs3 = typeSubs.ToDictionary(
                            keySelector: kvp => kvp.Key,
                            elementSelector: kvp => kvp.Value.FullName);
                        Statement rewritten3 = GenericAstRewriter.RewriteStatement(
                            genDefGenDefBody, stringSubs3, typeSubs, ctx.Registry, resolvedRoutine);
                        ctx.InstantiatedGenericBodies[resolvedRoutine.RegistryKey] = new MonomorphizedBody(
                            Ast: WrapInShellDecl(name: resolvedRoutine.Name, body: rewritten3,
                                info: resolvedRoutine),
                            Info: resolvedRoutine,
                            TypeSubs: typeSubs,
                            VariantStatus: null,
                            VariantInnerType: null,
                            IsSynthesized: true);
                        continue;
                    }

                    // Pure-synthesized resolved routines have no source AST. Add a sentinel
                    // MonomorphizedBody so EmitFromInstantiatedGenericBodies picks them up;
                    // the body comes from WiredRoutinePass via ctx.VariantBodies.
                    if (resolvedRoutine.IsSynthesized)
                    {
                        SourceLocation loc = resolvedRoutine.Location ??
                                             new SourceLocation("", 0, 0, 0);
                        ctx.InstantiatedGenericBodies[resolvedRoutine.RegistryKey] =
                            new MonomorphizedBody(
                                Ast: WrapInShellDecl(name: resolvedRoutine.Name,
                                    body: new BlockStatement(Statements: [], Location: loc),
                                    info: resolvedRoutine),
                                Info: resolvedRoutine,
                                TypeSubs: typeSubs,
                                VariantStatus: null,
                                VariantInnerType: null,
                                IsSynthesized: true);
                    }

                    continue;
                }

                var stringSubs = typeSubs.ToDictionary(
                    keySelector: kvp => kvp.Key,
                    elementSelector: kvp => kvp.Value.FullName);

                RoutineDeclaration rewrittenDecl =
                    GenericAstRewriter.Rewrite(
                        routine: astDecl,
                        subs: stringSubs,
                        typeSubs: typeSubs,
                        registry: ctx.Registry,
                        enclosingRoutine: resolvedRoutine);

                ctx.InstantiatedGenericBodies[resolvedRoutine.RegistryKey] = new MonomorphizedBody(
                    Ast: rewrittenDecl,
                    Info: resolvedRoutine,
                    TypeSubs: typeSubs,
                    VariantStatus: null,
                    VariantInnerType: null,
                    IsSynthesized: false);
            }
        } while (discoveredNew);
    }

    /// <summary>
    /// Emits generic-definition variant bodies for BuilderService routines (e.g. member_variable_count,
    /// type_name, is_generic) directly for the generic def owner. These bodies are safe to emit without
    /// type substitution because they return fixed literals and never reference the generic type parameter.
    ///
    /// Wrapper forwarders (e.g. Hijacked[T].type_name) call the inner type's method as T.method().
    /// When T is a generic def (e.g. BTreeDictNode[K,V]), the LLVM callee name is the generic def's
    /// mangled name (e.g. Collections.BTreeDictNode.type_name). This pass ensures that name has a
    /// definition so the linker does not fail.
    /// </summary>
    private void EmitGenericDefBuilderServiceBodies() // NOSONAR S3776
    {
        foreach (TypeInfo type in ctx.Registry.GetTypesWithMethods())
        {
            if (!type.IsGenericDefinition) continue;
            // Skip wrapper types: their concrete instances are handled by ProcessConcreteType
            // (which substitutes T with the concrete inner type). Emitting the wrapper generic-def
            // version would attempt to lower a body that still contains unresolved T references
            // (e.g. the as_entity() call), causing Phase B codegen failures.
            if (type is WrapperTypeInfo) continue;

            foreach (RoutineInfo routine in ctx.Registry.GetMethodsForType(type))
            {
                if (!BuilderInfoProvider.IsBuilderServiceRoutine(name: routine.Name)) continue;
                string key = routine.RegistryKey;
                if (ctx.InstantiatedGenericBodies.ContainsKey(key)) continue;
                if (!ctx.VariantBodies.TryGetValue(key: key, value: out Statement? body)) continue;

                ctx.InstantiatedGenericBodies[key] = new MonomorphizedBody(
                    Ast: WrapInShellDecl(name: routine.Name, body: body, info: routine),
                    Info: routine,
                    TypeSubs: new Dictionary<string, TypeInfo>(),
                    VariantStatus: null,
                    VariantInnerType: null,
                    IsSynthesized: true);
            }
        }
    }

    // Body construction

    private MonomorphizedBody? BuildBody(
        RoutineInfo genMethod,
        RoutineInfo concreteInfo,
        TypeInfo genDef,
        Dictionary<string, TypeInfo> typeSubs,
        Dictionary<string, string> stringSubs) // NOSONAR S3776
    {
        // Variant methods (try_/check_/lookup_)
        // These have OriginalName pointing back to the failable source routine.
        // Look for the body of that source routine (not the variant name).
        if (genMethod.OriginalName != null)
            return BuildVariantBody(
                genMethod: genMethod,
                concreteInfo: concreteInfo,
                genDef: genDef,
                typeSubs: typeSubs,
                stringSubs: stringSubs);
        // If the concrete method still has unresolved method-level generic parameters
        // (e.g. Text.$getitem! where index: I is still GenericParameterTypeInfo),
        // skip this body here. ProcessResolvedMethodGenericRoutines handles per-concrete-
        // index-type specialization once OperatorLoweringPass registers the resolutions.
        if (concreteInfo.Parameters.Any(static p => p.Type is GenericParameterTypeInfo))
            return null;
        // WiredRoutinePass / ErrorHandlingVariantPass body in VariantBodies
        bool skippedVariantBodyForStdlibFallback = false;
        if (ctx.VariantBodies.TryGetValue(key: genMethod.RegistryKey, out Statement? variantBody))
        {
            // Wrapper forwarder bodies call as_entity()/extract() on the inner type, which requires
            // T is EntityType (entity layout). For wrapper types (Hijacked, Retained, Owned, etc.)
            // with a non-entity concrete T, skip the variant body (which may be a forwarder) and
            // fall through to the stdlib AST lookup below — the RF source handles all T without
            // requiring entity layout.
            // NOTE: both the synthesized forwarder RoutineInfo and the stdlib RoutineInfo for
            // the same wrapper method share the same RegistryKey, so we check by wrapper type
            // name rather than genMethod.WrapperForwarderInnerMethod.
            bool isWrapperWithNonEntityInner =
                WrapperForwardingPass.WrapperTypeNames.Contains(genDef.Name) &&
                typeSubs.Count > 0 &&
                typeSubs.Values.First() is not EntityTypeInfo;
            if (!isWrapperWithNonEntityInner)
            {
                Statement rewritten = GenericAstRewriter.RewriteStatement(variantBody, stringSubs, typeSubs, ctx.Registry, concreteInfo);
                return new MonomorphizedBody(
                    Ast: WrapInShellDecl(name: concreteInfo.Name, body: rewritten, info: concreteInfo),
                    Info: concreteInfo,
                    TypeSubs: typeSubs,
                    VariantStatus: null,
                    VariantInnerType: null,
                    IsSynthesized: true);  // treat as synthesized so codegen uses EmitSynthesizedBodyFromAst
            }
            skippedVariantBodyForStdlibFallback = true;
        }
        // Pure synthesized: no AST body available.
        // If we skipped a variant body to force stdlib fallback (wrapper with non-entity T),
        // don't stop here — fall through to FindInStdlib below.
        if (genMethod.IsSynthesized && !skippedVariantBodyForStdlibFallback)
        {
            string ck = concreteInfo.RegistryKey;
            if (ck.Contains("List[Core.Owned") && (ck.Contains("$eq") || ck.Contains("$contains")))
                Console.Error.WriteLine($"[BuildBody-null-synth] gen={genMethod.RegistryKey} concrete={ck}");
            return null;
        }
        // Regular method: search stdlib + user program ASTs
        string astName = BuildAstName(genDef: genDef, routineName: genMethod.Name);
        var paramNames = genMethod.Parameters.Select(static p => p.Name).ToList();
        var paramTypeNames = genMethod.Parameters.Select(static p => (string?)p.Type?.Name).ToList();
        RoutineDeclaration? astDecl = FindInStdlib(
            genericAstName: astName,
            expectedParamCount: genMethod.Parameters.Count,
            typeSubs: typeSubs,
            expectedParamNames: paramNames,
            expectedParamTypeNames: paramTypeNames);

        if (astDecl == null)
        {
            string ck = concreteInfo.RegistryKey;
            if (ck.Contains("List[Core.Owned") && (ck.Contains("$eq") || ck.Contains("$contains")))
                Console.Error.WriteLine($"[BuildBody-stdlib-miss] gen={genMethod.RegistryKey} concrete={ck} astName={astName}");
            return null;
        }

        RoutineDeclaration rewrittenDecl =
            GenericAstRewriter.Rewrite(
                routine: astDecl,
                subs: stringSubs,
                typeSubs: typeSubs,
                registry: ctx.Registry,
                enclosingRoutine: concreteInfo);

        return new MonomorphizedBody(
            Ast: rewrittenDecl,
            Info: concreteInfo,
            TypeSubs: typeSubs,
            VariantStatus: null,
            VariantInnerType: null,
            IsSynthesized: false);
    }

    private MonomorphizedBody? BuildVariantBody(
        RoutineInfo genMethod,
        RoutineInfo concreteInfo,
        TypeInfo genDef,
        Dictionary<string, TypeInfo> typeSubs,
        Dictionary<string, string> stringSubs)
    {
        // Compute carrier-unwrapping metadata for instantiated variant bodies.
        AsyncStatus? variantStatus = null;
        TypeInfo? variantInnerType = null;

        if (concreteInfo.ReturnType?.TypeArguments is { Count: > 0 })
        {
            string? baseName = GetGenericBaseName(concreteInfo.ReturnType);
            if (baseName == "Lookup")
            {
                variantStatus = AsyncStatus.LookupVariant;
                variantInnerType = concreteInfo.ReturnType.TypeArguments[0];
            }
            else if (baseName == "Result")
            {
                variantStatus = AsyncStatus.CheckVariant;
                variantInnerType = concreteInfo.ReturnType.TypeArguments[0];
            }
        }
        // TryBool variants return Bool (no type args) — detect via AsyncStatus.
        if (variantStatus == null && concreteInfo.AsyncStatus == AsyncStatus.TryBoolVariant)
            variantStatus = AsyncStatus.TryBoolVariant;

        // When there is a carrier, the RoutineInfo.ReturnType is the inner type T,
        // not the carrier.
        RoutineInfo emitInfo = concreteInfo;
        if (variantStatus != null && variantInnerType != null)
        {
            emitInfo = new RoutineInfo(name: concreteInfo.Name)
            {
                Kind = concreteInfo.Kind,
                OwnerType = concreteInfo.OwnerType,
                Parameters = concreteInfo.Parameters,
                ReturnType = variantInnerType,
                IsFailable = concreteInfo.IsFailable,
                DeclaredModification = concreteInfo.DeclaredModification,
                ModificationCategory = concreteInfo.ModificationCategory,
                Visibility = concreteInfo.Visibility,
                Location = concreteInfo.Location,
                Module = concreteInfo.Module,
                Annotations = concreteInfo.Annotations,
                CallingConvention = concreteInfo.CallingConvention,
                IsVariadic = concreteInfo.IsVariadic,
                IsDangerous = concreteInfo.IsDangerous,
                Storage = concreteInfo.Storage,
                AsyncStatus = variantStatus.Value,
                OriginalName = concreteInfo.OriginalName
            };
        }

        // Pre-built variant body from ErrorHandlingVariantPass (keyed by generic method RegistryKey)
        if (ctx.VariantBodies.TryGetValue(key: genMethod.RegistryKey, out Statement? prebuiltVariant))
        {
            Statement rewritten = GenericAstRewriter.RewriteStatement(prebuiltVariant, stringSubs, typeSubs, ctx.Registry, emitInfo);
            return new MonomorphizedBody(
                Ast: WrapInShellDecl(name: emitInfo.Name, body: rewritten, info: emitInfo),
                Info: emitInfo,
                TypeSubs: typeSubs,
                VariantStatus: variantStatus,
                VariantInnerType: variantInnerType,
                IsSynthesized: false);
        }

        // Fallback: search for the original failable routine's AST and compile it as a variant
        string fallbackAstName = BuildAstName(genDef: genDef, routineName: genMethod.OriginalName!);
        RoutineDeclaration? astDecl = FindInStdlib(
            genericAstName: fallbackAstName,
            expectedParamCount: genMethod.Parameters.Count);

        if (astDecl == null)
            return null;

        RoutineDeclaration rewrittenDecl =
            GenericAstRewriter.Rewrite(
                routine: astDecl,
                subs: stringSubs,
                typeSubs: typeSubs,
                registry: ctx.Registry,
                enclosingRoutine: emitInfo);

        // The fallback body is the original failable AST (ReturnStatement nodes, not
        // VariantReturnStatement). Transform it so codegen emits carrier construction.
        if (variantStatus != null)
        {
            ErrorHandlingVariantKind kind = variantStatus switch
            {
                AsyncStatus.CheckVariant => ErrorHandlingVariantKind.Check,
                AsyncStatus.LookupVariant => ErrorHandlingVariantKind.Lookup,
                AsyncStatus.TryVariant => ErrorHandlingVariantKind.Try,
                AsyncStatus.TryBoolVariant => ErrorHandlingVariantKind.TryBool,
                _ => ErrorHandlingVariantKind.Try
            };
            Statement transformed = ErrorHandlingVariantPass.TransformBody(
                body: rewrittenDecl.Body, kind: kind);
            rewrittenDecl = rewrittenDecl with { Body = transformed };
        }

        return new MonomorphizedBody(
            Ast: rewrittenDecl,
            Info: emitInfo,
            TypeSubs: typeSubs,
            VariantStatus: variantStatus,
            VariantInnerType: variantInnerType,
            IsSynthesized: false);
    }
    // Helpers

    /// <summary>
    /// Builds a concrete <see cref="RoutineInfo"/> for an instantiated generic body by
    /// substituting owner and method type parameters.
    /// </summary>
    private RoutineInfo? BuildConcreteRoutineInfo(
        RoutineInfo genMethod,
        TypeInfo concreteOwner,
        Dictionary<string, TypeInfo> typeSubs)
    {
        // Wrapper-forwarder special case: the generic forwarder's signature came from the
        // inner-generic-def method (e.g. List[T].$getitem! returning T). Naive name-based
        // substitution using the wrapper's typeSubs would map List[T]'s T to the wrapper's
        // T-substitution (the whole inner type), not the inner's own T. Re-resolve the
        // signature against the concrete inner method instead.
        if (genMethod is { IsSynthesized: true, WrapperForwarderInnerMethod: { } innerGenMethod }
            && concreteOwner.TypeArguments is { Count: 1 } wrapperArgs)
        {
            TypeInfo concreteInner = wrapperArgs[0];
            RoutineInfo? concreteInnerMethod = ctx.Registry.LookupMethod(
                type: concreteInner,
                methodName: innerGenMethod.Name,
                isFailable: innerGenMethod.IsFailable);
            if (concreteInnerMethod != null)
            {
                var fwdParams = concreteInnerMethod.Parameters
                    .Select(p => p.Name == "me"
                        ? p.WithSubstitutedType(newType: concreteOwner)
                        : p)
                    .ToList();
                return new RoutineInfo(name: genMethod.Name)
                {
                    Kind = genMethod.Kind,
                    OwnerType = concreteOwner,
                    Parameters = fwdParams,
                    ReturnType = concreteInnerMethod.ReturnType,
                    IsFailable = genMethod.IsFailable,
                    DeclaredModification = genMethod.DeclaredModification,
                    ModificationCategory = genMethod.ModificationCategory,
                    Visibility = genMethod.Visibility,
                    Location = genMethod.Location,
                    Module = genMethod.Module,
                    Annotations = genMethod.Annotations,
                    CallingConvention = genMethod.CallingConvention,
                    IsVariadic = genMethod.IsVariadic,
                    IsDangerous = genMethod.IsDangerous,
                    IsSynthesized = true,
                    WrapperForwarderInnerMethod = concreteInnerMethod,
                    WrapperForwarderInnerGenericDef = genMethod.WrapperForwarderInnerGenericDef,
                    Storage = genMethod.Storage,
                    AsyncStatus = genMethod.AsyncStatus,
                    OriginalName = genMethod.OriginalName
                };
            }

            // The concrete inner type does not have this forwarded method — skip it.
            return null;
        }

        var resolvedParams = genMethod.Parameters
            .Select(p =>
            {
                TypeInfo resolved = ResolveSubstitutedType(p.Type, typeSubs);
                // Final sweep: if ResolveSubstitutedType couldn't resolve a generic parameter
                // (e.g., TryGetResolution returned null for a wrapper type), fall back to a direct
                // name-based lookup in typeSubs. Post-GMP there must be no GenericParameterTypeInfo.
                if (resolved is GenericParameterTypeInfo gp &&
                    typeSubs.TryGetValue(key: gp.Name, value: out TypeInfo? directSub))
                    resolved = directSub;
                return p.WithSubstitutedType(newType: resolved);
            })
            .ToList();

        TypeInfo? resolvedReturn = genMethod.ReturnType != null
            ? ResolveSubstitutedType(genMethod.ReturnType, typeSubs)
            : null;
        if (resolvedReturn is GenericParameterTypeInfo retGp &&
            typeSubs.TryGetValue(key: retGp.Name, value: out TypeInfo? directRetSub))
            resolvedReturn = directRetSub;

        return new RoutineInfo(name: genMethod.Name)
        {
            Kind = genMethod.Kind,
            OwnerType = concreteOwner,
            Parameters = resolvedParams,
            ReturnType = resolvedReturn,
            IsFailable = genMethod.IsFailable,
            DeclaredModification = genMethod.DeclaredModification,
            ModificationCategory = genMethod.ModificationCategory,
            Visibility = genMethod.Visibility,
            Location = genMethod.Location,
            Module = genMethod.Module,
            Annotations = genMethod.Annotations,
            CallingConvention = genMethod.CallingConvention,
            IsVariadic = genMethod.IsVariadic,
            IsDangerous = genMethod.IsDangerous,
            Storage = genMethod.Storage,
            AsyncStatus = genMethod.AsyncStatus,
            OriginalName = genMethod.OriginalName
        };
    }

    /// <summary>
    /// Resolves a type by applying generic substitutions.
    /// Also converts <see cref="WrapperTypeInfo"/> to the concrete <see cref="RecordTypeInfo"/>
    /// so method lookup and LLVM name mangling use the correct module-qualified type name.
    /// </summary>
    private TypeInfo ResolveSubstitutedType(TypeInfo type, Dictionary<string, TypeInfo> subs)
    {
        if (subs.TryGetValue(key: type.Name, value: out TypeInfo? sub))
            return sub;

        // WrapperTypeInfo (e.g., Hijacked[T] or Hijacked[Core.Byte]) must always be resolved
        // to the real RecordTypeInfo so LookupMethod and LLVM mangled names work correctly.
        // Use TryGetResolution (lookup-only) -> GMP must not grow AllConcreteGenericInstances.
        // Any unresolved generic parameter left in a RoutineInfo should not reach codegen —
        // GenericAstRewriter resolves expression ResolvedType before codegen entry.
        if (type is WrapperTypeInfo wrapper)
        {
            TypeInfo? wrapperDef = ctx.Registry.LookupType(name: wrapper.Name);
            if (wrapperDef is { IsGenericDefinition: true } &&
                wrapper.TypeArguments is { Count: > 0 })
            {
                var resolvedInnerArgs = wrapper.TypeArguments
                    .Select(a => ResolveSubstitutedType(a, subs))
                    .ToList();
                return ctx.Registry.TryGetResolution(genericDef: wrapperDef,
                    typeArguments: resolvedInnerArgs) ?? type;
            }
        }

        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            bool anySubstituted = false;
            var substitutedArgs = new List<TypeInfo>();
            foreach (TypeInfo arg in type.TypeArguments)
            {
                TypeInfo resolved = ResolveSubstitutedType(arg, subs);
                substitutedArgs.Add(resolved);
                if (!ReferenceEquals(resolved, arg)) anySubstituted = true;
            }
            if (anySubstituted)
            {
                TypeInfo? genericBase = GetGenericBase(type);
                if (genericBase != null)
                {
                    TypeInfo? alreadyResolved = ctx.Registry.TryGetResolution(
                        genericDef: genericBase,
                        typeArguments: substitutedArgs);
                    if (alreadyResolved != null) return alreadyResolved;
                    // Not yet registered — create it so the concrete signature reaches codegen
                    // without unresolved GenericParameterTypeInfo. Safe here because wrapper types
                    // are intercepted above (the WrapperTypeInfo branch) and never reach this path.
                    // GetOrCreateResolution also enqueues the new type for ProcessConcreteType.
                    if (substitutedArgs.All(predicate: a => a is not ErrorTypeInfo))
                        return ctx.Registry.GetOrCreateResolution(
                            genericDef: genericBase,
                            typeArguments: substitutedArgs);
                }
            }
        }

        if (type is { IsGenericDefinition: true, GenericParameters: not null, TypeArguments: null })
        {
            var typeArgs = type.GenericParameters
                .Select(gp => subs.TryGetValue(key: gp, value: out TypeInfo? s)
                    ? s
                    : ctx.Registry.LookupType(name: gp))
                .Where(t => t != null)
                .ToList();
            if (typeArgs.Count == type.GenericParameters.Count)
                return ctx.Registry.TryGetResolution(
                    genericDef: type,
                    typeArguments: typeArgs!) ?? type;
        }

        return type;
    }

    /// <summary>Builds the expected AST name for a routine on a generic type definition.</summary>
    private static string BuildAstName(TypeInfo genDef, string routineName)
    {
        if (genDef.GenericParameters is { Count: > 0 })
        {
            string paramList = string.Join(", ", genDef.GenericParameters);
            return $"{genDef.Name}[{paramList}].{routineName}";
        }
        return $"{genDef.Name}.{routineName}";
    }

    private string BuildAstNameForResolvedRoutine(RoutineInfo resolvedRoutine)
    {
        string astName;
        if (resolvedRoutine.OwnerType != null)
        {
            string ownerAstName;
            if (resolvedRoutine.GenericDefinition?.OwnerType is GenericParameterTypeInfo universalOwner)
            {
                ownerAstName = universalOwner.Name;
            }
            else
            {
                TypeInfo ownerType = resolvedRoutine.OwnerType;
                TypeInfo? ownerGenericDef = GetGenericBase(ownerType)
                    ?? (ownerType is WrapperTypeInfo ? ctx.Registry.LookupType(name: ownerType.Name) : null);
                if (ownerGenericDef?.GenericParameters is { Count: > 0 } gdParams)
                    ownerAstName = $"{ownerGenericDef.Name}[{string.Join(", ", gdParams)}]";
                else if (ownerType.IsGenericDefinition && ownerType.GenericParameters is { Count: > 0 } ownParams)
                    ownerAstName = $"{ownerType.Name}[{string.Join(", ", ownParams)}]";
                else
                    ownerAstName = ownerType.Name;
            }
            astName = $"{ownerAstName}.{resolvedRoutine.Name}";
        }
        else
        {
            astName = resolvedRoutine.Name;
        }

        if (resolvedRoutine.GenericDefinition?.IsGenericDefinition == true)
        {
            astName += "[generic]";
        }

        return astName;
    }

    private Dictionary<string, TypeInfo> BuildResolvedRoutineTypeSubstitutions(
        RoutineInfo resolvedRoutine)
    {
        var typeSubs = new Dictionary<string, TypeInfo>();

        if (resolvedRoutine.GenericDefinition?.OwnerType is GenericParameterTypeInfo universalOwner &&
            resolvedRoutine.OwnerType != null)
        {
            typeSubs[universalOwner.Name] = resolvedRoutine.OwnerType;
        }

        if (resolvedRoutine.OwnerType is { TypeArguments: { Count: > 0 } } ownerType)
        {
            TypeInfo? ownerGenericDef = GetGenericBase(ownerType)
                ?? (ownerType is WrapperTypeInfo ? ctx.Registry.LookupType(name: ownerType.Name) : null);
            if (ownerGenericDef?.GenericParameters is { Count: > 0 })
            {
                for (int i = 0;
                     i < ownerGenericDef.GenericParameters.Count && i < ownerType.TypeArguments.Count;
                     i++)
                {
                    string paramName = ownerGenericDef.GenericParameters[index: i];
                    // Don't overwrite a universal-owner mapping (e.g. T->BTreeListNode[Byte] from
                    // case 1) with the owner's own type argument (e.g. T->Byte from BTreeListNode[T]).
                    // These are different uses of the same name T: the method's universal-owner T
                    // refers to the whole owner type, not to the owner's element type.
                    if (!typeSubs.ContainsKey(key: paramName))
                        typeSubs[paramName] = ownerType.TypeArguments[index: i];
                }
            }

            // Wrapper forwarder over a generic inner type: the forwarder's parameters and body
            // reference the inner type's generic parameters (e.g. `Owned[BTreeDictNode[K,V]]`'s
            // synthesized `entries_add_last(k: K, v: V)` carries `K, V` from BTreeDictNode, not
            // from Owned). The outer ownerGenericDef.GenericParameters only knows Owned's `T`
            // — propagate the inner type's K, V substitutions too so the body rewriter can
            // resolve them when monomorphizing per concrete inner type.
            if (resolvedRoutine.WrapperForwarderInnerGenericDef is { GenericParameters: { Count: > 0 } } innerGenDef
                && ownerType.TypeArguments[index: 0] is { TypeArguments: { Count: > 0 } } innerInstance)
            {
                // Register the inner instance's type-args under their original inner-param
                // names. WrapperForwardingPass sets `ForwarderOriginalName = innerParamName`
                // on the disambiguated GenericParameterTypeInfo, and downstream substitution
                // sites (GenericAstRewriter.ResolveType, codegen.ResolveTypeSubstitution) look
                // up by that name when the disambiguated Name miss. So the map only needs the
                // original-name key — no `__rfwd_` sentinel.
                for (int i = 0;
                     i < innerGenDef.GenericParameters.Count && i < innerInstance.TypeArguments.Count;
                     i++)
                {
                    string innerParamName = innerGenDef.GenericParameters[index: i];
                    if (!typeSubs.ContainsKey(key: innerParamName))
                        typeSubs[innerParamName] = innerInstance.TypeArguments[index: i];
                }
            }
        }

        if (resolvedRoutine.GenericDefinition?.GenericParameters is { Count: > 0 } methodParams &&
            resolvedRoutine.TypeArguments is { Count: > 0 } methodTypeArgs)
        {
            for (int i = 0; i < methodParams.Count && i < methodTypeArgs.Count; i++)
            {
                string pName = methodParams[index: i];
                TypeInfo pVal = methodTypeArgs[index: i];
                if (typeSubs.TryGetValue(key: pName, value: out TypeInfo? existingOwnerValue))
                {
                    // Name collision: the owner already maps pName -> some type.
                    // When existingOwnerValue is a generic definition (TypeArguments=null),
                    // SubstituteType cannot recurse into it and returns it unchanged.
                    // Explicitly instantiate the generic def with pVal so we get a concrete type
                    // (e.g. T->BTreeListNode[T] + method T->BuildMode -> T->BTreeListNode[BuildMode]).
                    TypeInfo newVal;
                    if (existingOwnerValue is { IsGenericDefinition: true, GenericParameters: { Count: > 0 } innerParams } &&
                        innerParams.Any(predicate: p => p == pName))
                    {
                        var newArgs = innerParams
                            .Select(selector: p => p == pName ? pVal : (TypeInfo)new GenericParameterTypeInfo(name: p))
                            .ToList();
                        newVal = newArgs.All(predicate: a => a is not GenericParameterTypeInfo)
                            ? ctx.Registry.GetOrCreateResolution(genericDef: existingOwnerValue,
                                typeArguments: newArgs)
                            : existingOwnerValue;
                    }
                    else
                    {
                        var innerSub = new Dictionary<string, TypeInfo> { [pName] = pVal };
                        newVal = RoutineInfo.SubstituteType(type: existingOwnerValue,
                            substitution: innerSub);
                    }
                    typeSubs[pName] = newVal;
                }
                else
                {
                    typeSubs[pName] = pVal;
                }
            }
        }

        return typeSubs;
    }

    /// <summary>
    /// Searches all stdlib and user program ASTs for a routine declaration matching the given name.
    /// Uses a pre-built index for O(1) name lookup.
    /// When <paramref name="typeSubs"/> is provided, routines whose generic constraints are not
    /// satisfied by the concrete type arguments are skipped -> this prevents the record-layout
    /// overload of e.g. <c>Maybe[T]</c> from being selected when T is an entity type.
    /// </summary>
    private RoutineDeclaration? FindInStdlib(string genericAstName, int expectedParamCount = -1,
        Dictionary<string, TypeInfo>? typeSubs = null, List<string>? expectedParamNames = null,
        List<string?>? expectedParamTypeNames = null)
    {
        bool requireGenericSuffix = genericAstName.EndsWith("[generic]");
        string baseName = requireGenericSuffix
            ? genericAstName[..genericAstName.IndexOf("[generic]", StringComparison.Ordinal)]
            : genericAstName;

        if (!_routineIndex.TryGetValue(key: baseName, value: out List<RoutineDeclaration>? candidates))
        {
            // Failable methods are indexed under their name WITHOUT '!' (parser strips it,
            // sets IsFailable=true). When baseName ends with '!', retry without it and
            // keep only the failable overloads.
            if (!baseName.EndsWith('!') ||
                !_routineIndex.TryGetValue(key: baseName[..^1], value: out candidates))
                return null;
            candidates = candidates!.Where(d => d.IsFailable).ToList();
            if (candidates.Count == 0) return null;
        }

        RoutineDeclaration? countOnlyMatch = null;
        RoutineDeclaration? firstMatch = null;
        foreach (RoutineDeclaration decl in candidates)
        {
            if (requireGenericSuffix && decl.GenericParameters is not { Count: > 0 }) continue;
            if (!ConstraintsSatisfied(routine: decl, subs: typeSubs)) continue;

            if (expectedParamCount >= 0 && decl.Parameters.Count != expectedParamCount)
            {
                firstMatch ??= decl;
                continue;
            }

            // Count matches. If we also have expected param names, prefer the overload whose
            // param names match exactly — this disambiguates overloads like $create(capacity: U64)
            // vs $create(from: SortedList[T]) which both have 1 parameter.
            if (expectedParamNames != null && decl.Parameters.Count == expectedParamNames.Count)
            {
                bool namesMatch = true;
                for (int i = 0; i < expectedParamNames.Count; i++)
                {
                    if (decl.Parameters[i].Name != expectedParamNames[i])
                    {
                        namesMatch = false;
                        break;
                    }
                }
                if (namesMatch)
                {
                    // Param names alone don't disambiguate same-name-different-type overloads
                    // (e.g. `$create(from: Set[T])` vs `$create(from: FastSet[T])`). When type
                    // names are supplied, require those to match too. Without this gate the
                    // first-declared overload wins by source order, and Set's body ends up
                    // mounted under FastSet's mangled signature (LINKERR on $iter mismatch).
                    if (expectedParamTypeNames != null &&
                        decl.Parameters.Count == expectedParamTypeNames.Count)
                    {
                        bool typesMatch = true;
                        for (int i = 0; i < expectedParamTypeNames.Count; i++)
                        {
                            string? expected = expectedParamTypeNames[i];
                            if (expected == null) continue;
                            string? actual = decl.Parameters[i].Type?.Name;
                            if (actual == null) continue;
                            // Compare by base name (strip [T]/[K,V]) so `Set[T]` matches `Set`
                            // and `FastSet[T]` matches `FastSet` regardless of generic-arg form.
                            string expectedBase = StripGenericSuffix(expected);
                            string actualBase = StripGenericSuffix(actual);
                            if (expectedBase != actualBase)
                            {
                                typesMatch = false;
                                break;
                            }
                        }
                        if (!typesMatch)
                        {
                            countOnlyMatch ??= decl;
                            continue;
                        }
                    }
                    return decl;
                }
                countOnlyMatch ??= decl;
                continue;
            }

            return decl;
        }

        return countOnlyMatch ?? firstMatch;
    }

    private static string StripGenericSuffix(string typeName)
    {
        int bracket = typeName.IndexOf('[');
        return bracket >= 0 ? typeName[..bracket] : typeName;
    }

    /// <summary>
    /// Returns true if all explicit generic constraints on <paramref name="routine"/> are
    /// satisfied by the concrete type substitutions in <paramref name="subs"/>.
    /// </summary>
    private static bool ConstraintsSatisfied(RoutineDeclaration routine,
        Dictionary<string, TypeInfo>? subs)
    {
        if (routine.GenericConstraints is not { Count: > 0 }) return true;
        if (subs == null || subs.Count == 0) return true;

        foreach (GenericConstraintDeclaration c in routine.GenericConstraints)
        {
            if (!subs.TryGetValue(key: c.ParameterName, value: out TypeInfo? actual)) continue;
            bool ok = c.ConstraintType switch
            {
                ConstraintKind.ValueType     => IsRecordLike(actual),
                ConstraintKind.ReferenceType => actual is EntityTypeInfo,
                ConstraintKind.ChoiceType    => actual is ChoiceTypeInfo,
                ConstraintKind.FlagsType     => actual is FlagsTypeInfo,
                ConstraintKind.VariantType   => actual is VariantTypeInfo,
                ConstraintKind.Crashable     => actual is CrashableTypeInfo,
                ConstraintKind.ConstGeneric  => CheckStructuralConstGeneric(c: c, actual: actual),
                _                            => true, // Obeys/TypeEquality: trust SA
            };
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// Checks a <see cref="ConstraintKind.ConstGeneric"/> constraint that may encode a structural
    /// type-category requirement.
    /// </summary>
    private static bool CheckStructuralConstGeneric(GenericConstraintDeclaration c, TypeInfo actual)
    {
        string? typeName = c.ConstraintTypes is { Count: > 0 } ? c.ConstraintTypes[0].Name : null;
        return typeName switch
        {
            "RecordType"  => IsRecordLike(actual),
            "EntityType"  => actual is EntityTypeInfo,
            "ChoiceType"  => actual is ChoiceTypeInfo,
            "FlagsType"   => actual is FlagsTypeInfo,
            "VariantType" => actual is VariantTypeInfo,
            "Crashable"   => actual is CrashableTypeInfo,
            _             => true
        };
    }

    /// Wrappers (Owned, Retained, Hijacked, ...) are declared with `record` syntax and behave as
    /// record-category types for constraint purposes; they are tracked as WrapperTypeInfo for layout
    /// reasons but a `needs T is RecordType` constraint must still accept them.
    private static bool IsRecordLike(TypeInfo type) =>
        type is RecordTypeInfo || type is WrapperTypeInfo;

    /// <summary>Wraps a pre-built body statement in a minimal shell RoutineDeclaration.</summary>
    private static RoutineDeclaration WrapInShellDecl(
        string name, Statement body, RoutineInfo info)
    {
        return new RoutineDeclaration(
            Name: name,
            Parameters: [],
            ReturnType: null,
            Body: body,
            Visibility: VisibilityModifier.Open,
            Annotations: [],
            Location: info.Location ?? new SourceLocation("", 0, 0, 0));
    }
    // Type helpers (no codegen dependency)

    /// <summary>
    /// Returns true when a type contains unresolved generic parameters at any nesting depth,
    /// or is itself a generic definition (free type params, no TypeArguments).
    /// Used to skip wrapper instances whose inner type still has free type params.
    /// </summary>
    private static bool HasUnresolvedTypeArgs(TypeInfo t)
    {
        if (t is GenericParameterTypeInfo or ErrorTypeInfo) return true;
        if (t.IsGenericDefinition) return true;
        if (t.TypeArguments is not { Count: > 0 } args) return false;
        return args.Any(predicate: HasUnresolvedTypeArgs);
    }

    private static TypeInfo? GetGenericBase(TypeInfo type) => type switch
    {
        RecordTypeInfo { GenericDefinition: { } d } => d,
        EntityTypeInfo { GenericDefinition: { } d } => d,
        ProtocolTypeInfo { GenericDefinition: { } d } => d,
        VariantTypeInfo { GenericDefinition: { } d } => d,
        _ => null
    };

    private static string? GetGenericBaseName(TypeInfo type) => GetGenericBase(type)?.Name;

    // Method-generic call scanning

    /// <summary>
    /// Walks all built InstantiatedGenericBodies and registers method-level generic
    /// call-site resolutions (e.g. $getitem[U64]! called from List[Bytes].$eq).
    /// SA only analyzes generic-def bodies, so these concrete resolutions are never in
    /// _routineResolutions. Registering them here lets ProcessResolvedMethodGenericRoutines
    /// build the bodies before codegen runs.
    /// </summary>
    private void ScanAndRegisterMethodGenericCallResolutions()
    {
        foreach ((string _, MonomorphizedBody body) in ctx.InstantiatedGenericBodies.ToList())
        {
            if (body.Ast?.Body == null) continue;
            ScanStatementForMethodGenericCalls(body.Ast.Body);
        }
    }

    private void ScanStatementForMethodGenericCalls(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement b:
                foreach (Statement s in b.Statements) ScanStatementForMethodGenericCalls(s);
                break;
            case IfStatement ifs:
                ScanExprForMethodGenericCalls(ifs.Condition);
                ScanStatementForMethodGenericCalls(ifs.ThenStatement);
                if (ifs.ElseStatement != null) ScanStatementForMethodGenericCalls(ifs.ElseStatement);
                break;
            case WhileStatement w:
                ScanExprForMethodGenericCalls(w.Condition);
                ScanStatementForMethodGenericCalls(w.Body);
                break;
            case LoopStatement loop:
                ScanStatementForMethodGenericCalls(loop.Body);
                break;
            case ForStatement f:
                ScanExprForMethodGenericCalls(f.Iterable);
                ScanStatementForMethodGenericCalls(f.Body);
                break;
            case WhenStatement ws:
                ScanExprForMethodGenericCalls(ws.Expression);
                foreach (WhenClause c in ws.Clauses) ScanStatementForMethodGenericCalls(c.Body);
                break;
            case ReturnStatement { Value: { } rv }:
                ScanExprForMethodGenericCalls(rv);
                break;
            case AssignmentStatement assign:
                ScanExprForMethodGenericCalls(assign.Target);
                ScanExprForMethodGenericCalls(assign.Value);
                break;
            case DeclarationStatement { Declaration: VariableDeclaration { Initializer: { } vi } }:
                ScanExprForMethodGenericCalls(vi);
                break;
            case ExpressionStatement es:
                ScanExprForMethodGenericCalls(es.Expression);
                break;
            case DiscardStatement ds:
                ScanExprForMethodGenericCalls(ds.Expression);
                break;
            case ThrowStatement ts:
                ScanExprForMethodGenericCalls(ts.Error);
                break;
            case VariantReturnStatement { Value: { } vv }:
                ScanExprForMethodGenericCalls(vv);
                break;
            case BecomesStatement bst:
                ScanExprForMethodGenericCalls(bst.Value);
                break;
            case DangerStatement danger:
                ScanStatementForMethodGenericCalls(danger.Body);
                break;
        }
    }

    private void ScanExprForMethodGenericCalls(Expression expr) // NOSONAR S3776
    {
        switch (expr)
        {
            case LiteralExpression or IdentifierExpression or TypeIdExpression:
                return;

            case CallExpression call:
            {
                // Check if this call targets a method with unresolved method-level generic params
                if (call.ResolvedRoutine is { IsGenericDefinition: true, GenericParameters: { Count: > 0 } genParams } method)
                {
                    TypeInfo?[] inferred = InferMethodTypeArgsFromCall(method, genParams, call);
                    if (inferred.Length == genParams.Count && inferred.All(t => t != null))
                    {
                        ctx.Registry.GetOrCreateRoutineResolution(
                            genericDef: method,
                            typeArguments: inferred.Cast<TypeInfo>().ToList());
                    }
                }
                ScanExprForMethodGenericCalls(call.Callee);
                foreach (Expression arg in call.Arguments) ScanExprForMethodGenericCalls(arg);
                break;
            }
            case BinaryExpression bin:
                ScanExprForMethodGenericCalls(bin.Left);
                ScanExprForMethodGenericCalls(bin.Right);
                break;
            case UnaryExpression un:
                ScanExprForMethodGenericCalls(un.Operand);
                break;
            case MemberExpression mem:
                ScanExprForMethodGenericCalls(mem.Object);
                break;
            case NamedArgumentExpression named:
                ScanExprForMethodGenericCalls(named.Value);
                break;
            case TypeConversionExpression conv:
                ScanExprForMethodGenericCalls(conv.Expression);
                break;
            case IndexExpression idx:
                ScanExprForMethodGenericCalls(idx.Object);
                ScanExprForMethodGenericCalls(idx.Index);
                // Register method-generic $getitem!/$setitem! resolutions from IndexExpression nodes.
                // OperatorLoweringPass doesn't run on InstantiatedGenericBodies, so $getitem/$setitem
                // calls in those bodies are still IndexExpression rather than CallExpression.
                if (idx.Object.ResolvedType is { } idxObjType &&
                    idx.Index.ResolvedType is { } idxIdxType and not GenericParameterTypeInfo)
                {
                    RoutineInfo? getItem = ctx.Registry.LookupMethod(
                        type: idxObjType, methodName: "$getitem", isFailable: true);
                    if (getItem is { IsGenericDefinition: true, GenericParameters.Count: > 0 })
                    {
                        ctx.Registry.GetOrCreateRoutineResolution(
                            genericDef: getItem,
                            typeArguments: [idxIdxType]);
                    }
                    RoutineInfo? setItem = ctx.Registry.LookupMethod(
                        type: idxObjType, methodName: "$setitem", isFailable: true);
                    if (setItem is { IsGenericDefinition: true, GenericParameters.Count: > 0 })
                    {
                        ctx.Registry.GetOrCreateRoutineResolution(
                            genericDef: setItem,
                            typeArguments: [idxIdxType]);
                    }
                }
                break;
            case CreatorExpression creator:
                foreach (var (_, v) in creator.MemberVariables) ScanExprForMethodGenericCalls(v);
                break;
            case ConditionalExpression cond:
                ScanExprForMethodGenericCalls(cond.Condition);
                ScanExprForMethodGenericCalls(cond.TrueExpression);
                ScanExprForMethodGenericCalls(cond.FalseExpression);
                break;
        }
    }

    /// <summary>
    /// Infers method-level type arguments by matching GenericParameterTypeInfo params
    /// against the corresponding call-site argument ResolvedTypes.
    /// </summary>
    private static TypeInfo?[] InferMethodTypeArgsFromCall(
        RoutineInfo method, List<string> genParams, CallExpression call) // NOSONAR S3776
    {
        var result = new TypeInfo?[genParams.Count];

        foreach (ParameterInfo param in method.Parameters)
        {
            if (param.Name == "me") continue;
            if (param.Type is not GenericParameterTypeInfo gp) continue;

            int idx = -1;
            for (int i = 0; i < genParams.Count; i++)
            {
                if (genParams[i] == gp.Name) { idx = i; break; }
            }
            if (idx < 0 || result[idx] != null) continue;

            // Find the matching argument by name
            foreach (Expression arg in call.Arguments)
            {
                Expression argValue = arg is NamedArgumentExpression nae && nae.Name == param.Name
                    ? nae.Value
                    : arg;
                TypeInfo? argType = argValue.ResolvedType;
                if (argType != null && argType is not GenericParameterTypeInfo)
                {
                    result[idx] = argType;
                    break;
                }
            }
        }

        return result;
    }
}
