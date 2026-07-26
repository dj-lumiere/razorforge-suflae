using System.Collections.Generic;

namespace Compiler.Resolution;

/// <summary>
/// Central inventory of the plain (non-<c>$</c>) stdlib routine names, stdlib type names, and native
/// runtime symbols that the compiler references by hard-coded string literal. This is the "gather in
/// one place" step of the compiler↔stdlib name-contract work
///
/// <para><b>Why this exists.</b> Renaming a stdlib routine (e.g. the <c>extract</c>/<c>inject</c> →
/// <c>peek</c>/<c>poke</c> rename, commit 1480acd) silently miscompiles: the compiler looks routines
/// up by literal (<c>LookupMethod(type, "peek")</c>) and sometimes changes teardown/codegen behavior
/// by matching a callee/type NAME. A rename compiles clean and breaks at runtime. Funnelling every
/// such literal through this one file makes the coupling visible and gives a single place to add the
/// <c>validate-stdlib</c> resolution check (Design 1 step 2, not yet wired).</para>
///
/// <para><b>Scope / non-goals (this step).</b> This file only <i>collects</i> the names as named
/// constants and grouped sets whose values are byte-identical to the current literals — no behavior
/// change. The call sites listed in each member's <c>&lt;remarks&gt;</c> still hold their own copies;
/// migrating them to reference these constants (and adding the resolution check) is the follow-up.
/// The <c>$</c>-wired routine names are intentionally NOT here — they already have a single source of
/// truth in <see cref="WiredRoutineCatalog"/>. The two marker-protocol verbs <c>$refer</c>/<c>$control</c>
/// and the iteration <c>try_next</c> are compiler-generated and appear as literals at lowering sites,
/// so they are cross-referenced here for completeness (their catalog entry, where one exists, stays
/// canonical).</para>
/// </summary>
public static class RuntimeContract
{
    // =====================================================================================
    // B-TIER — plain stdlib routine names looked up / property-matched by literal.
    // These are the rename-sensitive contract: an author renaming any of these must update
    // the compiler, and (once step 2 lands) validate-stdlib will fail loudly if they don't.
    // =====================================================================================

    /// <summary>Raw-pointer / entity-escape surface on <c>Hijacked[T]</c> and bare entities.</summary>
    /// <remarks>Sites: WrapperForwardingPass (LookupMethod/PropertyName), PatternLoweringPass,
    /// WiredRoutinePass, LLVMCodeGenerator.Expressions.Calls.</remarks>
    public static class RawPointer
    {
        /// <summary><c>Hijacked[T].peek()</c> — non-destructive read (<c>*ptr</c>).</summary>
        public const string Peek = "peek";
        /// <summary><c>Hijacked[T].poke(value:)</c> — store through the pointer.</summary>
        public const string Poke = "poke";
        /// <summary><c>Hijacked[T].as_entity()</c> — reinterpret the pointee as an owned entity (borrow view).</summary>
        public const string AsEntity = "as_entity";
        /// <summary>Null-pointer predicate on the raw-pointer surface.</summary>
        public const string IsNone = "is_none";
        /// <summary>Entity deallocation primitive.</summary>
        public const string Invalidate = "invalidate";
        /// <summary>Raw-pointer escape hatch that yields a <c>Hijacked[T]</c> (intercepted in codegen).</summary>
        public const string Hijack = "hijack";
    }

    /// <summary>Reference-counting controller surface (<c>RetainController[T]</c> and the RC wrappers).</summary>
    /// <remarks>Sites: WrapperForwardingPass, LLVMCodeGenerator.Statements (retain lookup + RcCopyVerb),
    /// ScopeTeardownLoweringPass / TemporaryTeardownPass (consuming-receiver heuristic).</remarks>
    public static class RefCount
    {
        /// <summary><c>RetainController[T].borrow_data()</c> — read the controlled payload.</summary>
        public const string BorrowData = "borrow_data";
        /// <summary>Strong-count increment (also the <c>Retained</c> copy verb).</summary>
        public const string Retain = "retain";
        /// <summary>Strong-count decrement.</summary>
        public const string Release = "release";
        /// <summary>Weak-count increment (also the <c>Tracked</c> copy verb).</summary>
        public const string Track = "track";
        /// <summary>Multi-threaded strong-count increment (the <c>Shared</c> copy verb).</summary>
        public const string Share = "share";
        /// <summary>Multi-threaded weak-count increment (the <c>Watched</c> copy verb).</summary>
        public const string Watch = "watch";
        /// <summary>Biased-refcount alias verb (the <c>Roamed</c> copy verb; the same name also
        /// constructs from an entity, mirroring <see cref="Retain"/>'s dual role).</summary>
        public const string Roam = "roam";
    }

    /// <summary>Carrier record field names on <c>Maybe[T]</c>/<c>Result[T]</c>.</summary>
    /// <remarks>Sites: ExpressionLoweringPass (tuple synthesis), PatternLoweringPass, ErrorHandlingVariantPass,
    /// LLVMCodeGenerator.Statements (field lookup).</remarks>
    public static class Carrier
    {
        /// <summary>Presence flag field (<c>true</c> = value present / not-absent).</summary>
        public const string PresentField = "present";
        /// <summary>Wrapped-value field.</summary>
        public const string ValueField = "value";
    }

    /// <summary>Collection-shape routines resolved by literal during lowering / reachability.</summary>
    /// <remarks>Sites: OperatorLoweringPass, ExpressionLoweringPass, RoutineReachabilityPass,
    /// LLVMCodeGenerator.Expressions.Collections. <see cref="AddLast"/> vs <see cref="Add"/> is chosen
    /// by base-name (<c>List</c>/<c>Deque</c>/<c>BitList</c> → add_last, else add).</remarks>
    public static class Collection
    {
        /// <summary>Element count (see also the shipped <c>Sized.count()</c> protocol).</summary>
        public const string Count = "count";
        /// <summary>Unordered insert (Set/Dict).</summary>
        public const string Add = "add";
        /// <summary>Ordered append (List/Deque/BitList).</summary>
        public const string AddLast = "add_last";
        /// <summary>Element replacement.</summary>
        public const string Replace = "replace";
    }

    /// <summary><c>BackIndex.resolve(count:)</c> — resolve a back-index against a container size.</summary>
    /// <remarks>Sites: OperatorLoweringPass, RoutineReachabilityPass. Failable.</remarks>
    public const string Resolve = "resolve";

    /// <summary><c>data_size()</c> — per-type byte size (compile-time BuilderService intrinsic, folded not called).</summary>
    /// <remarks>Sites: BuilderInfoProvider, BuilderServiceInliningPass, GenericAstRewriter.</remarks>
    public const string DataSize = "data_size";

    /// <summary><c>crash_message()</c> on error types — extracts the diagnostic string on the throw path.</summary>
    /// <remarks>Sites: LLVMCodeGenerator.Statements.Returns, WiredRoutinePass, RoutineReachabilityPass.</remarks>
    public const string CrashMessage = "crash_message";

    /// <summary>Non-failable iterator step generated for <c>for</c>-lowering (the <c>try_</c> variant of
    /// failable <c>$next!</c>). Also carried by <see cref="WiredRoutineCatalog"/> (reachability seed);
    /// listed here because ControlFlowLoweringPass / IteratorInlineLoweringPass match it by literal.</summary>
    public const string TryNext = "try_next";

    // =====================================================================================
    // Marker-protocol verbs — compiler-generated $-names that are NOT in WiredRoutineCatalog
    // but are matched by literal at teardown/lowering sites (grouped with the view-verb sets).
    // =====================================================================================

    /// <summary><c>$refer</c> — the <c>Referring</c> marker-protocol coercion (yields a borrow view).</summary>
    public const string Refer = "$refer";
    /// <summary><c>$control</c> — the <c>Controlling</c> marker-protocol coercion (yields a borrow view).</summary>
    public const string Control = "$control";

    /// <summary>The routine-name contracts that MUST resolve to a real, declared stdlib routine —
    /// the rename-sensitive set that <c>validate-stdlib</c>'s <see cref="RuntimeContractCheck"/>
    /// asserts. Deliberately EXCLUDES compiler-generated / intrinsic names that have no stdlib
    /// routine body: <see cref="TryNext"/> (generated from <c>$next</c>), the marker verbs
    /// <see cref="Refer"/>/<see cref="Control"/>, <see cref="DataSize"/> + the BuilderService sets
    /// (folded intrinsics), and the native <see cref="Runtime"/> externs (link-checked C-ABI). The
    /// carrier FIELDS (<see cref="Carrier"/>) are member variables, not routines — checked separately.</summary>
    public static readonly IReadOnlyList<string> StdlibRoutineContracts =
    [
        RawPointer.Peek, RawPointer.Poke, RawPointer.AsEntity, RawPointer.IsNone,
        RawPointer.Invalidate, RawPointer.Hijack,
        RefCount.BorrowData, RefCount.Retain, RefCount.Release, RefCount.Track,
        RefCount.Share, RefCount.Watch,
        Collection.Count, Collection.Add, Collection.AddLast, Collection.Replace,
        Resolve, CrashMessage,
    ];

    /// <summary>Additional wrapper / marker-protocol TYPE-name contracts that must each resolve to a
    /// registered type (checked alongside <see cref="WrapperTypes"/> by <see cref="RuntimeContractCheck"/>).
    /// <see cref="Owned"/> is intentionally excluded — it is a compiler-internal wrapper name with no
    /// declared stdlib type, so it cannot be resolution-checked.</summary>
    public static readonly IReadOnlyList<string> StdlibTypeContracts =
    [
        Atomic, Controlling, Referring,
    ];

    // =====================================================================================
    // C-TIER — name→behavior heuristics. Teardown/codegen change behavior by matching these
    // name SETS. These stay name-based for now; the deep fix derives them from the signature
    // (Design 2B). Kept here so the sets have one definition to point every copy at.
    // =====================================================================================

    /// <summary>Store primitives: a call to one of these MOVES its argument into storage, so the
    /// source binding is not torn down at scope exit.</summary>
    /// <remarks>Sites: ScopeTeardownLoweringPass.StorePrimitives.</remarks>
    public static readonly IReadOnlySet<string> StorePrimitives =
        new HashSet<string>(comparer: System.StringComparer.Ordinal)
            { RawPointer.Poke, "store_element_ref", "store" };

    /// <summary>Verbs that CONSUME their (bare-entity) receiver — ownership moves into the RC
    /// controller, so the receiver is not torn down.</summary>
    /// <remarks>Sites: ScopeTeardownLoweringPass (retain/track pattern), TemporaryTeardownPass.ConsumingReceiverVerbs.</remarks>
    public static readonly IReadOnlySet<string> ConsumingReceiverVerbs =
        new HashSet<string>(comparer: System.StringComparer.Ordinal)
            { RefCount.Retain, RefCount.Track };

    /// <summary>Reference primitives whose result BORROWS a referent owned elsewhere — a binding or
    /// temporary initialized by one owns nothing and must not be torn down.</summary>
    /// <remarks>Sites: ScopeTeardownLoweringPass.ViewVerbs, TemporaryTeardownPass.ViewVerbs.</remarks>
    public static readonly IReadOnlySet<string> ViewVerbs =
        new HashSet<string>(comparer: System.StringComparer.Ordinal)
            { RawPointer.AsEntity, Refer, Control };

    // =====================================================================================
    // Wrapper TYPE names — genuine type-identity checks (legitimate to keep as checks, but the
    // string should be a symbol reference eventually — Design 2B). One definition per set here.
    // =====================================================================================

    /// <summary>Read-only single-threaded borrow token.</summary>
    public const string Viewing = "Viewing";
    /// <summary>Exclusive-write single-threaded borrow token.</summary>
    public const string Modifying = "Modifying";
    /// <summary>Read-only multi-threaded borrow token.</summary>
    public const string Inspecting = "Inspecting";
    /// <summary>Exclusive-write multi-threaded borrow token.</summary>
    public const string Claiming = "Claiming";
    /// <summary>Reference-counted single-threaded handle.</summary>
    public const string Retained = "Retained";
    /// <summary>Weak-reference single-threaded handle.</summary>
    public const string Tracked = "Tracked";
    /// <summary>Reference-counted multi-threaded handle.</summary>
    public const string Shared = "Shared";
    /// <summary>Weak-reference multi-threaded handle.</summary>
    public const string Watched = "Watched";
    /// <summary>Unmanaged raw-pointer handle.</summary>
    public const string Hijacked = "Hijacked";
    /// <summary>Biased-reference-counted, auto-promoting handle (Suflae `entity` backing). Registered
    /// as an RC wrapper for lifetime (retain/release), but deliberately NOT in the forwarding /
    /// read-only / coercion sets: access is compiler-inserted lock-wrapping, never <c>$refer</c>/
    /// <c>$control</c> (which would hand out a lock-bypassing raw reference).</summary>
    public const string Roamed = "Roamed";
    /// <summary>Scope-bound access guard over a <see cref="Roamed"/> (the `Roamed` analogue of
    /// <see cref="Claiming"/>): <c>Enterable</c> + <c>Controlling</c>, produced by
    /// <c>Roamed.claim_roam()</c>. Its <c>$enter</c>/<c>$exit</c> take/release the mode-checked
    /// reentrant lock so member access is lock-wrapped on every exit path.</summary>
    public const string Roaming = "Roaming";

    // Related wrapper / marker-protocol type names that appear in the same type-identity checks as
    // the nine borrow wrappers above, but are NOT part of the borrow-wrapper contract sets.
    /// <summary>Owning value wrapper (compiler-internal; not a declared stdlib type).</summary>
    public const string Owned = "Owned";
    /// <summary>Atomic value wrapper.</summary>
    public const string Atomic = "Atomic";
    /// <summary>Marker protocol whose coercion mints a controlling borrow (<see cref="Control"/>).</summary>
    public const string Controlling = "Controlling";
    /// <summary>Marker protocol whose coercion mints a referring borrow (<see cref="Refer"/>).</summary>
    public const string Referring = "Referring";

    /// <summary>All wrapper types recognized for layout/dispatch. Mirrors WrapperForwardingPass.WrapperTypes
    /// and LLVMCodeGenerator.WrapperTypeNames.</summary>
    public static readonly IReadOnlySet<string> WrapperTypes =
        new HashSet<string>(comparer: System.StringComparer.Ordinal)
            { Viewing, Modifying, Inspecting, Claiming, Shared, Watched, Retained, Tracked, Hijacked, Roaming };

    /// <summary>Wrapper types that transparently forward inner-type methods — every wrapper EXCEPT
    /// <see cref="Hijacked"/> (the raw-pointer escape hatch). Mirrors WrapperForwardingPass.ForwardingWrapperTypes.</summary>
    public static readonly IReadOnlySet<string> ForwardingWrapperTypes =
        new HashSet<string>(comparer: System.StringComparer.Ordinal)
            { Viewing, Modifying, Inspecting, Claiming, Shared, Watched, Retained, Tracked };

    /// <summary>Read-only borrow tokens (only <c>@readonly</c> methods reachable). Mirrors
    /// WrapperForwardingPass.ReadOnlyWrapperTypes.</summary>
    public static readonly IReadOnlySet<string> ReadOnlyWrapperTypes =
        new HashSet<string>(comparer: System.StringComparer.Ordinal) { Viewing, Inspecting };

    /// <summary>Borrow/view wrappers whose value points INTO another value, so a method returning one
    /// may ALIAS its receiver. Mirrors TemporaryTeardownPass.BorrowWrapperNames.</summary>
    public static readonly IReadOnlySet<string> BorrowWrapperNames =
        new HashSet<string>(comparer: System.StringComparer.Ordinal)
            { Viewing, Modifying, Inspecting, Claiming, Hijacked, Roaming };

    /// <summary>RC-wrapper base names whose refcount release is owned by codegen. Mirrors
    /// TemporaryTeardownPass.RcWrapperBaseNames.</summary>
    public static readonly IReadOnlySet<string> RcWrapperBaseNames =
        new HashSet<string>(comparer: System.StringComparer.Ordinal)
            { Retained, Tracked, Shared, Watched, Roamed };

    /// <summary>Per RC-wrapper base name, the copy verb that bumps the appropriate count. Mirrors
    /// LLVMCodeGenerator.Statements.RcCopyVerb.</summary>
    public static readonly IReadOnlyDictionary<string, string> RcCopyVerb =
        new Dictionary<string, string>(comparer: System.StringComparer.Ordinal)
        {
            [Retained] = RefCount.Retain,
            [Tracked] = RefCount.Track,
            [Shared] = RefCount.Share,
            [Watched] = RefCount.Watch,
            [Roamed] = RefCount.Roam,
        };

    // =====================================================================================
    // BuilderService intrinsic names — reflection-style routines folded at compile time
    // (Axis-2 intrinsics: no linkable body, no user-import dependency). Mirrors
    // BuilderInfoProvider.PerTypeRoutines / .StandaloneRoutines.
    // =====================================================================================

    /// <summary>Per-type BuilderService member routines (require <c>import BuilderService</c>).</summary>
    public static readonly IReadOnlySet<string> BuilderPerTypeRoutines =
        new HashSet<string>(comparer: System.StringComparer.Ordinal)
        {
            "type_name", "type_kind", "type_id", "module_name", "is_generic", "is_in_flight",
            "generic_args", "member_variable_count", "member_variable_info", "protocols",
            "protocol_info", "routine_names", "routine_info", "annotations", DataSize,
            "full_type_name", "dependencies", "member_type_id",
        };

    /// <summary>Standalone BuilderService routines (require <c>import BuilderService</c>).</summary>
    public static readonly IReadOnlySet<string> BuilderStandaloneRoutines =
        new HashSet<string>(comparer: System.StringComparer.Ordinal)
        {
            "source_file", "source_line", "source_column", "source_routine", "source_module",
            "source_text", "caller_file", "caller_line", "caller_routine", "target_os",
            "target_arch", "builder_version", "build_mode", "build_timestamp", "page_size",
            "cache_line", "word_size",
        };

    // =====================================================================================
    // Native runtime externs — C-ABI symbols emitted directly into the module by codegen.
    // These are matched against the native runtime library (native/runtime/*.c), NOT stdlib
    // .rf, so they are a DIFFERENT contract (a rename here means editing the C side too, and
    // validate-stdlib cannot check them). Collected here so codegen has one name table.
    // Sites: LLVMCodeGenerator.Expressions (declarations + call sites).
    // =====================================================================================

    /// <summary>Native runtime function symbols referenced by codegen. Names must match
    /// <c>native/runtime/razorforge_runtime.h</c>.
    ///
    /// <para>INVENTORY-ONLY: unlike the stdlib names above, codegen still emits these inline in its
    /// IR-template strings (<c>declare .. @rf_allocate_dynamic ..</c> / <c>call .. @rf_...</c>). A
    /// rename here breaks LOUDLY at link (undefined symbol), so it needs no silent-break guard and is
    /// deliberately not funnelled through these consts — they exist to document the one name table.</para></summary>
    public static class Runtime
    {
        /// <summary>Zero-initialized heap allocation.</summary>
        public const string AllocateDynamic = "rf_allocate_dynamic";
        /// <summary>Uninitialized heap allocation.</summary>
        public const string AllocateDynamicUninit = "rf_allocate_dynamic_uninit";
        /// <summary>Entity invalidation / free.</summary>
        public const string Invalidate = "rf_invalidate";
        /// <summary>Trace: update current source location.</summary>
        public const string TraceUpdateLoc = "_rf_trace_update_loc";
        /// <summary>Agent/task: create.</summary>
        public const string TaskCreate = "rf_task_create";
        /// <summary>Agent/task: spawn on a dedicated thread.</summary>
        public const string TaskSpawnThreaded = "rf_task_spawn_threaded";
        /// <summary>Agent/task: complete with a value payload.</summary>
        public const string TaskCompleteValue = "rf_task_complete_value";
        /// <summary>Coroutine: create.</summary>
        public const string CoroCreate = "rf_coro_create";
        /// <summary>Coroutine: push a cancellation frame.</summary>
        public const string CoroCfPush = "rf_coro_cf_push";
        /// <summary>Coroutine: pop a cancellation frame.</summary>
        public const string CoroCfPop = "rf_coro_cf_pop";
        /// <summary>Scheduler: spawn onto the default scheduler.</summary>
        public const string SchedSpawnDefault = "rf_sched_spawn_default";
    }
}
