using Builder.Instantiation;
using Builder.Declaration;
using Builder.Targeting;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;

namespace Builder.Verification;

public partial class SemanticVerifier
{
    /// <summary>
    /// Fully-processed stdlib state captured after a COMPLETE compile (Phases 1–7) of a minimal
    /// program. Restoring it lets a warm compile reuse the lowered / synthesized / monomorphized stdlib
    /// instead of reprocessing it every run (~5 s → ms). Holds the post-Phase-8 registry snapshot, the
    /// lowered stdlib program ASTs (read-only after lowering — safe to share across warm compiles), and
    /// the body dictionaries codegen consumes. This is the in-RAM state a compile daemon holds.
    /// </summary>
    public sealed class CompiledStdlibState
    {
        /// <summary>The language mode (RF or SF) the stdlib was compiled under.</summary>
        public required Language Language { get; init; }

        /// <summary>The post-SA type registry snapshot (all stdlib types and routines).</summary>
        public required TypeRegistry.StdlibSnapshot Registry { get; init; }

        /// <summary>The fully-lowered stdlib program ASTs, shared read-only across warm compiles.</summary>
        public required List<(Program Program, string FilePath, string Module)> StdlibPrograms
        {
            get;
            init;
        }

        /// <summary>Synthesized (wired/builder-generated) routine bodies keyed by registry key.</summary>
        public required Dictionary<string, (RoutineInfo Routine, Statement Body)> SynthesizedBodies
        {
            get;
            init;
        }

        /// <summary>Variant-generated routine bodies keyed by registry key.</summary>
        public required Dictionary<string, Statement> VariantBodies { get; init; }

        /// <summary>Monomorphized generic instantiation bodies keyed by instantiation key.</summary>
        public required Dictionary<string, MonomorphizedBody> InstantiatedGenericBodies
        {
            get;
            init;
        }

        /// <summary>All other stdlib routine bodies keyed by registry key.</summary>
        public required Dictionary<string, Statement> RoutineBodies { get; init; }

        /// <summary>
        /// Daemon-lifetime cache of per-body reachability scans, keyed by stdlib
        /// <see cref="RoutineDeclaration"/> reference. Shared by reference across every warm compile that
        /// restores from this state (the daemon reuses one <see cref="CompiledStdlibState"/>), so it grows
        /// as successive user programs reach more stdlib bodies and lets <c>RoutineReachabilityPass</c>
        /// skip re-walking them. Starts empty; safe because stdlib decls are stable across warm compiles.
        /// </summary>
        public Dictionary<RoutineDeclaration, RoutineBodyScan> BodyScanCache { get; init; } =
            new();

        /// <summary>
        /// Daemon-lifetime cache of per-file ON-DEMAND ANALYSIS results, keyed by stdlib file path. Under the
        /// demand flip the snapshot is captured from an `import EVERY module` program with no entry, so its
        /// demand collector reaches NOTHING — the per-file body SA + desugar + lower never runs at capture, and
        /// EVERY warm build re-runs it for each file its <c>start()</c> reaches (measured ~420 ms, the dominant
        /// warm dev-loop cost). This memo lets a build REUSE a file another build already analyzed: it holds the
        /// analyzed+lowered file program AST plus the variant/synth routine bodies that file's analysis produced.
        /// <para>PROGRAM-INDEPENDENT by construction: it caches the file's own TEMPLATE / non-generic bodies
        /// (a function of the file, not of any user program), NOT monomorphized generic INSTANCES — those stay
        /// per-build demand (rebuilt by the collector from <c>start()</c>'s reachability), so the define-set is
        /// still decided per build (warm/cold parity preserved) and there is no eager-instantiation runaway.</para>
        /// Grows as successive builds reach more files; shared by reference across every warm build served from
        /// this one state (the daemon holds ONE <see cref="CompiledStdlibState"/> per language, serving builds
        /// serially — no locking needed), exactly like <see cref="BodyScanCache"/>. Starts empty; warms over the
        /// daemon's lifetime.
        /// </summary>
        public Dictionary<string, CachedFileAnalysis> AnalyzedFileCache { get; init; } =
            new(comparer: StringComparer.Ordinal);

        /// <summary>Bumped on every write to <see cref="AnalyzedFileCache"/>, so a prepared restore can tell
        /// whether it still matches the cache.</summary>
        internal int AnalyzedFileCacheVersion { get; set; }

        /// <summary>The next build's cloned programs, prepared by the daemon between requests (see
        /// <see cref="PrepareNextRestore"/>). Taken by exactly one warm build.</summary>
        internal PreparedRestore? Prepared { get; set; }
    }

    /// <summary>One warm build's private copy of the stdlib programs (see the warm constructor) and the files
    /// restored already analyzed from the cache, built for <see cref="CacheVersion"/> of the analyzed-file cache.</summary>
    internal sealed record PreparedRestore(
        int CacheVersion,
        List<(Program Program, string FilePath, string Module)> Programs,
        List<string> CachedFiles,
        Dictionary<string, Statement> TemplateBodies);

    /// <summary>
    /// Builds the next warm build's program copies ahead of time. Cloning every stdlib program costs ~60-90 ms
    /// and its allocations trigger collections that land inside the following phases, so the daemon calls this
    /// after answering a request, while it waits for the next one. The warm constructor takes the result
    /// instead of cloning on the critical path. No-op when an up-to-date restore is already prepared.
    /// </summary>
    public static void PrepareNextRestore(CompiledStdlibState warm)
    {
        if (warm.Prepared is { } prepared && prepared.CacheVersion == warm.AnalyzedFileCacheVersion)
        {
            return;
        }

        warm.Prepared = BuildRestore(warm: warm);
    }

    /// <summary>Clones every stdlib program for one build: a file a prior build already analyzed on demand is
    /// cloned from its cached analyzed program and listed as cached, every other file from the snapshot.</summary>
    private static PreparedRestore BuildRestore(CompiledStdlibState warm)
    {
        var cachedFiles = new List<string>();
        var programs = new List<(Program Program, string FilePath, string Module)>(
            capacity: warm.StdlibPrograms.Count);
        foreach ((Program program, string filePath, string module) in warm.StdlibPrograms)
        {
            if (warm.AnalyzedFileCache.TryGetValue(key: filePath,
                    value: out CachedFileAnalysis? cachedFile))
            {
                cachedFiles.Add(item: filePath);
                programs.Add(item: (Builder.Instantiation.StdlibProgramBodyCloner.CloneBodies(
                    program: cachedFile.AnalyzedProgram), filePath, module));
                continue;
            }

            programs.Add(item: (Builder.Instantiation.StdlibProgramBodyCloner.CloneBodies(program: program),
                filePath, module));
        }

        // The stdlib routine bodies templates are cloned from (protocol-extension lowering, on-demand variant
        // synthesis) must be THIS build's program bodies, the nodes on-demand analysis annotates in place, as
        // in a cold build. The snapshot's own bodies were captured before anything was reached, so they are
        // raw: cloning `Iterable[T].first_or_default` from one lowered its `when` without analysis and returned
        // 0 instead of the default. A key with no declaration in the programs keeps the snapshot body.
        var templateBodies = new Dictionary<string, Statement>(dictionary: warm.RoutineBodies,
            comparer: StringComparer.Ordinal);
        foreach ((Program program, string _, string _) in programs)
        {
            foreach (RoutineDeclaration decl in DeclaredRoutines(program: program))
            {
                if (decl.ResolvedInfo is { } info && templateBodies.ContainsKey(key: info.RegistryKey))
                {
                    templateBodies[key: info.RegistryKey] = decl.Body;
                }
            }
        }

        return new PreparedRestore(CacheVersion: warm.AnalyzedFileCacheVersion,
            Programs: programs,
            CachedFiles: cachedFiles,
            TemplateBodies: templateBodies);
    }

    /// <summary>
    /// One stdlib file's cached on-demand analysis (see <see cref="CompiledStdlibState.AnalyzedFileCache"/>):
    /// the analyzed+lowered program AST plus the variant/synthesized routine bodies that file's analysis added,
    /// so a warm build reusing the file can replay them without re-running Phase-5 SA + desugar + lower.
    /// </summary>
    public sealed record CachedFileAnalysis(
        Program AnalyzedProgram,
        IReadOnlyList<KeyValuePair<string, Statement>> VariantBodies,
        IReadOnlyList<KeyValuePair<string, (RoutineInfo Routine, Statement Body)>> SynthBodies,
        IReadOnlyList<string> RoutineBodyKeys);

    /// <summary>
    /// Runs one full compile of a minimal program to fully process the stdlib, then captures the
    /// result. Call once (e.g. at daemon startup); reuse via the restore constructor for warm compiles.
    /// (A "priming" snapshot that pre-instantiates the common generic surface was tried and measured NOT
    /// to help: the per-run cost is in GenericMonomorphizationPass's type-graph processing, whose
    /// per-instance working set resets each run and does not consult pre-seeded instantiation BODIES.
    /// Making it help requires seeding the type-level working sets, not the bodies — deferred.)
    /// </summary>
    public static CompiledStdlibState CaptureCompiledStdlib(Language language)
    {
        // Prime the snapshot with the WHOLE stdlib, not just Core: a daemon should hold the entire
        // analyzed stdlib resident in RAM. Without this, a warm compile of a program importing any non-Core
        // module (e.g. Collections.CircularList) finds it absent from _loadedModules and triggers a full
        // ScanStdlibFiles — re-parsing EVERY stdlib file, every run (~0.7 s, the Phase 3 cost). Importing
        // every module here makes the capture load+analyze+lower them once, so those imports short-circuit
        // on every warm run. The one-time capture cost grows; per-run latency drops.
        string stdlibPath = StdlibLoader.GetDefaultStdlibPath();
        var probe = new StdlibLoader(stdlibRoot: stdlibPath, language: language);
        var source = new System.Text.StringBuilder(value: "module __snapshot__\n");
        foreach (string moduleName in probe.ScanModuleNames())
        {
            source.Append(value: "import ")
                  .Append(value: moduleName.Replace(oldChar: '.', newChar: '/'))
                  .Append(value: '\n');
        }

        var sa = new SemanticVerifier(language: language);
        List<Token> tokens = new Builder.Tokenizer.Tokenizer(source: source.ToString(),
            fileName: "__snapshot__",
            language: language).Tokenize();
        var parser = new Builder.Parser.Parser(tokens: tokens,
            language: language,
            fileName: "__snapshot__");
        sa.Analyze(program: parser.Parse());
        // Fold every cross-module-lazy signature repair into the snapshot ONCE (all modules imported here, so
        // every referenced type resolves), so no warm build re-repairs per-file and every file is cacheable in
        // AnalyzedFileCache (see EagerlyRepairStdlibSignatures / [[daemon-analysis-cache]]).
        sa.EagerlyRepairStdlibSignatures();
        return sa.CaptureCompiledState();
    }

    private CompiledStdlibState CaptureCompiledState()
    {
        return new CompiledStdlibState
        {
            Language = _registry.Language,
            Registry = _registry.CaptureSnapshot(),
            StdlibPrograms =
                new List<(Program, string, string)>(collection: _registry.StdlibPrograms),
            SynthesizedBodies =
                new Dictionary<string, (RoutineInfo, Statement)>(dictionary: _synthesizedBodies),
            VariantBodies = new Dictionary<string, Statement>(dictionary: _variantBodies),
            // Capture an EMPTY instantiation set. The snapshot is analyzed from a throwaway `import EVERY module`
            // program, so its demand collector materializes that program's monomorphizations (Maybe[X].assign,
            // Atomic[X].destroy, …) — which are NOT what any real warm build reaches. Carrying them pollutes every
            // warm build: codegen (a dumb translator) emits the whole InstantiatedGenericBodies set, so a warm
            // build of `show("hi")` would emit hundreds of unrelated derives that the equivalent cold build prunes
            // (the cold/warm define-set divergence). The daemon's value is the cached ANALYZED stdlib (parsed
            // programs + resolved types/routines, captured above); monomorphization is per-build and the collector
            // demand-rebuilds it deterministically from that cache — identical to a cold build.
            InstantiatedGenericBodies =
                new Dictionary<string, MonomorphizedBody>(comparer: StringComparer.Ordinal),
            RoutineBodies = new Dictionary<string, Statement>(dictionary: _routineBodies)
        };
    }

    /// <summary>The resident warm state this verifier restored from (daemon-lifetime, shared across builds).
    /// Held so on-demand stdlib analysis can WRITE freshly-analyzed files back into
    /// <see cref="CompiledStdlibState.AnalyzedFileCache"/> for the next build to reuse. Null on the cold /
    /// snapshot-only ctors.</summary>
    private readonly CompiledStdlibState? _warmState;

    /// <summary>
    /// Constructs a verifier pre-warmed from a full compiled-stdlib snapshot. Restores the lowered
    /// stdlib programs + body dicts and marks the registry to SKIP stdlib reprocessing, so a subsequent
    /// full <see cref="Analyze"/> only processes the user program (+ its incremental instantiations) and
    /// can codegen without redoing the ~5 s of stdlib desugaring/verification/monomorphization.
    /// </summary>
    /// <param name="language">The source language (RazorForge or Suflae) this build targets.</param>
    /// <param name="warm">The resident compiled-stdlib snapshot to restore from.</param>
    /// <param name="target">Optional target configuration; null uses the host default.</param>
    /// <param name="buildMode">The RazorForge build mode (optimization level).</param>
    public SemanticVerifier(Language language, CompiledStdlibState warm,
        TargetConfig? target = null, RfBuildMode buildMode = RfBuildMode.Debug)
    {
        _warmState = warm;
        _registry = new TypeRegistry(language: language, snapshot: warm.Registry);
        // PER-BUILD ISOLATION: the captured stdlib program ASTs are shared BY REFERENCE across every warm build
        // the daemon serves from this one snapshot. On-demand analysis + monomorphization + the collector lower
        // routine bodies IN PLACE (a routine not reached at capture is still un-lowered and gets lowered on the
        // first build that reaches it; call nodes get resolution annotations), so a shared body would leak one
        // build's lowering into the next — the warm/cold define-set divergence (a spurious extra define, e.g.
        // Range[U64]'s creator, appearing on the 2nd+ warm build). Give THIS build its own programs with freshly
        // cloned routine bodies (every other node + each RoutineDeclaration.ResolvedInfo stays shared), so all
        // per-build lowering lands on build-local ASTs and the shared snapshot graph is never mutated. Every
        // reader — on-demand analysis, GMP's FindInStdlib template index, and codegen — resolves through the SAME
        // cloned program (StdlibPrograms serves this list), so there is no shared-vs-clone split.
        // CACHE-AWARE restore: if a prior build already analyzed this file on demand, clone the CACHED
        // analyzed+lowered program (annotations preserved by the cloner — they point at the shared snapshot
        // routine/type objects, valid across builds) instead of the snapshot's un-body-analyzed one, and list
        // the file as cached so AnalyzeStdlibProgramOnDemand, on first reach, replays what its analysis would
        // have left behind (ReplayCachedFileAnalysis) instead of re-running SA+lower + repair.
        // Installing an analyzed program for a file THIS build never reaches is harmless — codegen only emits
        // REACHED bodies (liveness), same as the un-analyzed programs already in StdlibPrograms.
        // The daemon usually prepared this copy between requests (PrepareNextRestore). A stale or missing one
        // (the cache changed since, or the first build) is built here. Either way the copy is this build's own.
        PreparedRestore restore = warm.Prepared is { } prepared &&
                                  prepared.CacheVersion == warm.AnalyzedFileCacheVersion
            ? prepared
            : BuildRestore(warm: warm);
        warm.Prepared = null;
        _registry.RestoreStdlibPrograms(programs: restore.Programs);
        foreach (string filePath in restore.CachedFiles)
        {
            _warmCachedFiles.Add(item: filePath);
        }
        // Re-lazy the primed whole-stdlib instance closure so this warm compile re-discovers only what the
        // USER program reaches (like cold), instead of GMP re-processing all ~638 primed instances (cold
        // reaches ~378, codegen keeps ~122). User-reachability un-lazies via MaterializeIfLazy. The lazy set is
        // per-registry (not a flag on the shared TypeInfo), so this marks instances lazy for THIS build only —
        // the snapshot graph is never mutated.
        _registry.RelazyStdlibConcreteInstances();

        _typeResolver = new TypeResolver(sa: this);
        _typeBodyResolver = new TypeBodyResolver(sa: this, typeResolver: _typeResolver);
        _signatureResolver = new SignatureResolver(sa: this, typeResolver: _typeResolver);
        _conformanceAnalyzer = new ProtocolConformanceAnalyzer(sa: this);
        _target = target ?? TargetConfig.ForCurrentHost();
        _buildMode = buildMode;

        // Seed the codegen-consumed body dicts from the captured (already-lowered/analyzed) stdlib.
        // NOTE: _routineBodies is deliberately NOT seeded — it is the synthesis working set that drives
        // ErrorHandlingVariantPass / WiredRoutinePass; seeding it makes them REGENERATE + re-analyze all
        // stdlib variant/wired bodies (the 3.6 s AnalyzeVariantBodies cost). CollectStdlibBodiesForVariant-
        // Generation is gated on SkipStdlibReprocessing so stdlib routines stay out of _routineBodies and
        // those passes only process USER routines; the stdlib variant/synthesized bodies come from here.
        // Keep the captured stdlib routine bodies as a LOOKUP-ONLY source (NOT merged into
        // `_routineBodies`, which must stay the user-only variant-generation working set). Protocol
        // default-impl lowering needs the stdlib extension templates (e.g. `Iterable[Text].join`) to
        // recognize + clone them per implementer; without this a warm compile can't specialize them and
        // the generic-def reaches codegen unresolved ("Unresolved generic member routine …join").
        foreach (KeyValuePair<string, (RoutineInfo Routine, Statement Body)> kv in warm
                    .SynthesizedBodies)
        {
            _synthesizedBodies[key: kv.Key] = kv.Value;
        }

        _variantBodies = new Dictionary<string, Statement>(dictionary: warm.VariantBodies);

        // Replay the variant/synthesized bodies each cached file's on-demand analysis produced. A file's
        // Phase-5 analysis drains its own failables' try_/check_/lookup_ variants into _variantBodies and
        // synthesizes their represent/diagnose/derives into _synthesizedBodies; the snapshot (no reachability
        // at capture) never generated them, so a build that REUSES the cached file (skipping its analysis)
        // must have these replayed or the variant call site link-fails ("declared+called but never defined").
        // Keyed by RegistryKey; merging a not-reached file's bodies is harmless (codegen gates on liveness).
        foreach (CachedFileAnalysis cachedFile in warm.AnalyzedFileCache.Values)
        {
            foreach (KeyValuePair<string, Statement> vb in cachedFile.VariantBodies)
            {
                _variantBodies[key: vb.Key] = vb.Value;
            }

            foreach (KeyValuePair<string, (RoutineInfo Routine, Statement Body)> sb in cachedFile
                        .SynthBodies)
            {
                _synthesizedBodies[key: sb.Key] = sb.Value;
            }
        }
        // Skip restoring EMPTY synthesized sentinels that have NO matching variant body. The stdlib
        // snapshot captures a placeholder body for a resolved routine whose owner was not live in the
        // stdlib-only snapshot program (e.g. `DictEmittable[Text,SerialValue].try_emit` — no stdlib code
        // iterates a `Dict[Text,SerialValue]`, so its emit variant was never materialized, only a
        // `new BlockStatement([])` stub). Restoring such a stub is HARMFUL: its key enters
        // `_restoredInstantiationKeys`, so GMP treats it as already-built and skips re-monomorphization
        // when the USER program legitimately reaches it — leaving the empty body for codegen to skip
        // (Phase B), which surfaces as the "declared+called but never defined" over-prune. Dropping the
        // placeholder lets the warm build re-materialize the real body from the user's live reachability,
        // exactly as a cold compile does. A sentinel WITH a matching variant body is real (Phase C emits
        // it) and is kept.
        var restoredInst = new Dictionary<string, MonomorphizedBody>();
        foreach (KeyValuePair<string, MonomorphizedBody> kv in warm.InstantiatedGenericBodies)
        {
            bool emptySentinel = kv.Value is
                { IsSynthesized: true, Ast.Body: BlockStatement { Statements.Count: 0 } };
            if (emptySentinel && !warm.VariantBodies.ContainsKey(key: kv.Key))
            {
                continue; // broken placeholder — let the warm build rebuild it
            }

            restoredInst[key: kv.Key] = kv.Value;
        }

        _instantiatedGenericBodies = restoredInst;

        // Keep the captured stdlib routine bodies as a LOOKUP-ONLY source (see the seeding comment above),
        // plus the already-analyzed variant/instantiation keys, in ONE immutable memo consumed read-only
        // downstream.
        _memo = new StdlibMemo(
            IsWarm: true,
            // From _variantBodies (NOT warm.VariantBodies): it now also holds the replayed cached-file variant
            // bodies, and RestoredVariantKeys must cover them so the demand path treats them as already-built.
            RestoredVariantKeys: new HashSet<string>(collection: _variantBodies.Keys,
                comparer: StringComparer.Ordinal),
            RestoredInstantiationKeys: new HashSet<string>(collection: restoredInst.Keys,
                comparer: StringComparer.Ordinal),
            WarmStdlibRoutineBodies: restore.TemplateBodies);
        if (Diagnostics.DiagnosticFlags.PhaseTiming)
        {
            Console.Error.WriteLine(
                value:
                $"[warm-restore] seeded instantiations={_instantiatedGenericBodies.Count} variants={_variantBodies.Count} synth={_synthesizedBodies.Count}");
        }
    }

}
