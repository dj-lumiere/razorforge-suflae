using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Compiler.Desugaring;
using Compiler.Diagnostics;
using Compiler.Instantiation;
using Compiler.Instantiation.Passes;
using Compiler.Postprocessing;
using Compiler.Postprocessing.Passes;
using Compiler.Resolution;
using Compiler.Synthesis;
using Compiler.Targeting;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Verification.Enums;
using Verification.Results;
using Verification.Scopes;

namespace Verification;

using TypeSymbol = TypeInfo;

/// <summary>
/// Semantic analyzer for RazorForge and Suflae programs.
/// Performs type checking, scope analysis, and inference for:
/// - Method modification (readonly/writable/migratable)
/// - Migratable modification tracking (buffer relocation detection)
/// - Error handling variant generation (try_/check_/lookup_)
/// </summary>
public sealed partial class SemanticVerifier
{
    #region Fields

    /// <summary>The type registry for storing and looking up types.</summary>
    internal readonly TypeRegistry _registry;

    /// <summary>Call graph for modification inference.</summary>
    private readonly CallGraph _callGraph = new();
    private MarkerProtocolDesugarPass? _markerPass;

    /// <summary>Errors collected during analysis (insertion order preserved; deduplicated).</summary>
    private readonly List<SemanticError> _errors = [];

    /// <summary>Warnings collected during analysis (insertion order preserved; deduplicated).</summary>
    private readonly List<SemanticWarning> _warnings = [];

    // The analyzer runs several passes and re-resolves expressions (post type/protocol
    // registration, monomorphization, etc.), so the SAME diagnostic can be produced more than
    // once for one source location. These sets dedup by value — SemanticError/SemanticWarning are
    // records whose equality is (Code, Message, Location) — so a re-reported diagnostic is dropped
    // while the List keeps first-seen order. All add-sites route through AddError/AddWarning.
    private readonly HashSet<SemanticError> _seenErrors = [];
    private readonly HashSet<SemanticWarning> _seenWarnings = [];

    /// <summary>Adds an error unless an identical one (same code/message/location) was already recorded.</summary>
    private void AddError(SemanticError error)
    {
        if (_seenErrors.Add(item: error))
            _errors.Add(item: error);
    }

    /// <summary>Adds a warning unless an identical one (same code/message/location) was already recorded.</summary>
    private void AddWarning(SemanticWarning warning)
    {
        if (_seenWarnings.Add(item: warning))
            _warnings.Add(item: warning);
    }

    /// <summary>
    /// Warnings visible to a user build: stdlib-internal warnings are excluded (surface them with
    /// the <c>validate-stdlib</c> verb instead). EVERY AnalysisResult must use this — passing the
    /// raw <c>_warnings</c> list leaks stdlib style warnings (e.g. RF-W258) into user output.
    /// </summary>
    private List<SemanticWarning> UserVisibleWarnings() =>
        _warnings
            .Where(predicate: w => !string.IsNullOrEmpty(value: w.Location.FileName)
                                && !IsStdlibFile(filePath: w.Location.FileName))
            .ToList();

    /// <summary>
    /// Parsed literal values for types requiring native library parsing.
    /// Keyed by source location for code generator lookup.
    /// </summary>
    private readonly Dictionary<SourceLocation, ParsedLiteral> _parsedLiterals = new();

    /// <summary>Current function being analyzed (for return type checking).</summary>
    internal RoutineInfo? _currentRoutine;

    /// <summary>Current type being analyzed (for me reference resolution).</summary>
    internal TypeSymbol? _currentType;

    /// <summary>Danger block nesting depth (0 = not in danger block, >0 = inside danger block).</summary>
    private int _dangerBlockDepth;

    /// <summary>Gets whether we're currently inside a danger block.</summary>
    private bool InDangerBlock => _dangerBlockDepth > 0;

    /// <summary>True while analyzing a compiler-generated body (variant or synthesized derived operator).
    /// Suppresses the wired-routine direct-call check so SA can fully annotate ResolvedType on
    /// all nodes -> errors are already discarded by AnalyzeCompilerGeneratedBody's error-count guard.</summary>
    internal bool _isInCompilerGeneratedBody;

    /// <summary>True while analyzing a synthesized derived-operator body (DerivedOperatorPass output).
    /// Instructs AnalyzeExpression to skip re-analysis of nodes that already have ResolvedType set,
    /// preserving the pre-annotations applied by DerivedOperatorPass.</summary>
    internal bool _preservePresetTypes;

    /// <summary>Member variable names seen in the current type during body resolution (for duplicate detection).</summary>
    internal HashSet<string>? _currentTypeMemberVariableNames;

    /// <summary>The source file path of the program being analyzed (for import resolution).</summary>
    internal string _currentFilePath = string.Empty;

    /// <summary>The module name declared in the current file (from 'module' declaration).</summary>
    internal string? _currentModuleName;

    /// <summary>Modules imported by the current file. Used for type resolution of non-Core types.</summary>
    internal readonly HashSet<string> _importedModules =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>Tracks imported symbol names for collision detection (#105).</summary>
    private readonly HashSet<string> _importedSymbolNames = new(comparer: StringComparer.Ordinal);

    /// <summary>Per-file import snapshots used when re-analyzing compiler-generated bodies.</summary>
    private readonly Dictionary<string, HashSet<string>> _importSnapshots =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-file imported symbol snapshots used when re-analyzing compiler-generated bodies.</summary>
    private readonly Dictionary<string, HashSet<string>> _symbolNameSnapshots =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-file module-name snapshots used when re-analyzing compiler-generated bodies.</summary>
    private readonly Dictionary<string, string?> _moduleNameSnapshots =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>Nesting depth for conditional expressions (for #145 deep nesting warning).</summary>
    private int _conditionalNestingDepth;

    /// <summary>Tracks the last variant variable declared, for immediate dismantling check (#58).</summary>
#pragma warning disable CS0414
    private (string Name, SourceLocation Location)? _lastDeclaredVariantVar;
#pragma warning restore CS0414

    /// <summary>
    /// When statements determined to be exhaustive (either via catch-all or full type coverage).
    /// Consulted by control-flow termination analysis so that an exhaustive `when` whose every
    /// arm terminates also terminates.
    /// </summary>
    private readonly HashSet<WhenStatement> _exhaustiveWhens = [];

    /// <summary>Tracks Lookup variables that must be dismantled before scope exit (#161).</summary>
    private readonly List<(string Name, SourceLocation Location)> _pendingLookupVars = [];

    /// <summary>Tracks variables invalidated by steal/ownership transfer (#11).</summary>
    private readonly HashSet<string> _deadrefVariables = [];

    /// <summary>Flags-context stack: when analyzing the RHS of `isonly` (and similar) on a flags
    /// LHS, bare identifiers are resolved against the flag members of the top type.</summary>
    private readonly Stack<TypeSymbol> _flagsContextStack = new();

    /// <summary>Tracks the current for-loop iteration variable names for migratable check (#22).</summary>
    private readonly HashSet<string> _activeIterationSources = [];

    /// <summary>Routine declarations collected in Phase 1/2, pending resolution and registration in Phase 2.5.</summary>
    internal readonly List<PendingRoutine> _pendingRoutines = [];

    /// <summary>Tracks lock policy per variable for lock policy validation (#19).</summary>
    private readonly Dictionary<string, string> _variableLockPolicies = [];

    /// <summary>The resource expression currently being analyzed as a `using` target, if any. A
    /// multi-threaded access token (Inspecting/Claiming) is only legal in this exact position —
    /// any other use is rejected (RF-S629) so its lock is always `using`-scoped.</summary>
    private ISyntaxTreeNode? _usingResourceNode;

    /// <summary>Stack of MT access holds (`inspect`/`claim`) live in the enclosing `using` scopes.
    /// Each hold records the syntactic handle path AND the controller-identity it resolves to (see
    /// <see cref="_sharedHandleIdentity"/>), so aliased handles (`s2 = s.share()`) conflict even
    /// though their names differ. Pushed on `using` entry, popped on exit, so a nested `using` sees
    /// the holds it overlaps — the basis of the readers-XOR-writer check (RF-S630).</summary>
    private readonly List<(string Handle, int Identity, bool IsWriter, SourceLocation Location)>
        _activeAccessHolds = [];

    /// <summary>Maps a Shared/Watched handle path (`s`, `s.a`) to the identity of the controller
    /// (the atomic Arc cell) it refers to. A fresh `T.share[P]()` mints a new identity;
    /// `.share()`/`.watch()` clones and plain copies INHERIT the source handle's identity, so all
    /// handles to one controller share an identity. Lets the readers-XOR-writer check key on the
    /// shared DATA rather than the variable name. Paths never bound to a tracked handle are
    /// lazily assigned a unique identity on first use (degrades to per-path = the old behaviour).</summary>
    private readonly Dictionary<string, int> _sharedHandleIdentity = new(StringComparer.Ordinal);

    /// <summary>Monotonic source of fresh controller identities for <see cref="_sharedHandleIdentity"/>.</summary>
    private int _nextSharedHandleIdentity;

    /// <summary>Temporary: last share[Policy]() call info, propagated in variable declaration (#19).</summary>
    private (string SourceVar, string Policy)? _lastSharePolicy;

    /// <summary>Tracks (TypeName, ProtocolName) pairs added by implicit marker conformance, excluded from validation.</summary>
    internal readonly HashSet<(string TypeName, string ProtocolName)>
        _implicitProtocolConformances = [];

    /// <summary>
    /// AST bodies synthesized for derived operators ($ne, $lt, $le, $gt, $ge, $notcontains).
    /// Keyed by RoutineInfo.RegistryKey. Analyzed in Phase 5 via AnalyzeSynthesizedBodies().
    /// </summary>
    private readonly Dictionary<string, (RoutineInfo Routine, Statement Body)> _synthesizedBodies =
        new();

    /// <summary>Handles resolution of type expressions (TypeResolution logic).</summary>
    internal TypeResolver _typeResolver;

    /// <summary>Handles resolution of type bodies (member variables, protocol conformances, etc.).</summary>
    internal TypeBodyResolver _typeBodyResolver;

    /// <summary>Handles resolution and registration of routine signatures.</summary>
    internal SignatureResolver _signatureResolver;

    /// <summary>Handles implicit marker protocol conformance application.</summary>
    internal ProtocolConformanceAnalyzer _conformanceAnalyzer;

    /// <summary>
    /// Pre-transformed bodies for error-handling variant routines (try_/check_/lookup_), produced
    /// by <see cref="ErrorHandlingVariantPass"/> during Phase 4 global desugaring.
    /// Merged into <c>SynthesizedBodies</c> when building the <see cref="AnalysisResult"/>.
    /// </summary>
    private Dictionary<string, Statement> _variantBodies = new();

    /// <summary>
    /// Concrete generic method bodies produced by <see cref="GenericMonomorphizationPass"/>.
    /// Captured from <see cref="DesugaringContext.InstantiatedGenericBodies"/> in
    /// <see cref="RunPhase4GlobalDesugaring"/> and forwarded to <see cref="AnalysisResult"/>.
    /// </summary>
    private IReadOnlyDictionary<string, MonomorphizedBody> _instantiatedGenericBodies =
        new Dictionary<string, MonomorphizedBody>();

    /// <summary>
    /// Reachable routine keys produced by <see cref="RoutineReachabilityPass"/>.
    /// Captured from <see cref="InstantiationContext.LiveRoutineKeys"/> after Phase 6.
    /// </summary>
    private IReadOnlyCollection<string> _liveRoutineKeys = Array.Empty<string>();
    private IReadOnlyCollection<string> _liveOwnerTypeNames = Array.Empty<string>();

    /// <summary>
    /// May-suspend routine keys from <see cref="MaySuspendAnalysis"/>, captured from
    /// <see cref="InstantiationContext.MaySuspendRoutineKeys"/> after Phase 6. Drives 5b-2
    /// cancellation instrumentation in codegen.
    /// </summary>
    private IReadOnlyCollection<string> _maySuspendRoutineKeys = Array.Empty<string>();

    #endregion

    #region Constructor

    /// <summary>
    /// Stores the target state used by this compiler phase.
    /// </summary>
    private readonly TargetConfig _target;
    /// <summary>
    /// Stores the build mode state used by this compiler phase.
    /// </summary>
    private readonly RfBuildMode _buildMode;

    /// <summary>
    /// True when this instance was constructed from a pre-analyzed stdlib snapshot.
    /// Causes Phase 3 to skip <c>PreRegisterStdlibVariants</c> (already registered in snapshot)
    /// and Phase 5 to skip <c>AnalyzeStdlibBodies</c> (already analyzed in snapshot).
    /// Only valid with <see cref="SaOnly"/> = true; the full pipeline re-runs stdlib lowering
    /// so it cannot safely reuse snapshot state.
    /// </summary>
    private readonly bool _snapshotMode;

    /// <summary>
    /// Initializes a new instance of the <see cref="SemanticVerifier"/> class.
    /// </summary>
    /// <param name="language">The language being analyzed (RazorForge or Suflae).</param>
    /// <param name="stdlibPath">Optional path to the stdlib directory.</param>
    /// <param name="target">Target platform -> drives BuilderService platform constants. Defaults to host.</param>
    /// <param name="buildMode">Build mode -> drives BuilderService.build_mode. Defaults to Debug.</param>
    public SemanticVerifier(Language language, string? stdlibPath = null,
        TargetConfig? target = null, RfBuildMode buildMode = RfBuildMode.Debug)
    {
        _registry = new TypeRegistry(language: language, stdlibPath: stdlibPath);
        _typeResolver = new TypeResolver(sa: this);
        _typeBodyResolver = new TypeBodyResolver(sa: this, typeResolver: _typeResolver);
        _signatureResolver = new SignatureResolver(sa: this, typeResolver: _typeResolver);
        _conformanceAnalyzer = new ProtocolConformanceAnalyzer(sa: this);
        _target = target ?? TargetConfig.ForCurrentHost();
        _buildMode = buildMode;
    }

    /// <summary>
    /// Constructs a <see cref="SemanticVerifier"/> pre-warmed from a stdlib snapshot.
    /// Stdlib loading, body analysis, and variant pre-registration are all skipped on the
    /// first <see cref="Analyze"/> call — use with <see cref="SaOnly"/> = true only.
    /// </summary>
    public SemanticVerifier(Language language, TypeRegistry.StdlibSnapshot snapshot,
        TargetConfig? target = null, RfBuildMode buildMode = RfBuildMode.Debug)
    {
        _registry = new TypeRegistry(language: language, snapshot: snapshot);
        _typeResolver = new TypeResolver(sa: this);
        _typeBodyResolver = new TypeBodyResolver(sa: this, typeResolver: _typeResolver);
        _signatureResolver = new SignatureResolver(sa: this, typeResolver: _typeResolver);
        _conformanceAnalyzer = new ProtocolConformanceAnalyzer(sa: this);
        _target = target ?? TargetConfig.ForCurrentHost();
        _buildMode = buildMode;
        _snapshotMode = true;
    }

    /// <summary>
    /// Captures a pre-analyzed stdlib snapshot for the given language.
    /// Runs a full SA initialization (including stdlib body analysis) on a minimal program,
    /// then returns the registry snapshot for fast-restore in subsequent test instances.
    /// </summary>
    public static TypeRegistry.StdlibSnapshot CaptureStdlibSnapshot(Language language)
    {
        var sa = new SemanticVerifier(language: language) { SaOnly = true };
        var tokens = new Compiler.Tokenizer.Tokenizer(
            source: "module __snapshot__",
            fileName: "__snapshot__",
            language: language).Tokenize();
        var parser = new Compiler.Parser.Parser(
            tokens: tokens,
            language: language,
            fileName: "__snapshot__");
        sa.Analyze(program: parser.Parse());
        return sa._registry.CaptureSnapshot();
    }

    /// <summary>
    /// When true, AnalyzeMultiple prints per-phase timings to stderr.
    /// Set from the manifest's <c>sa-timing</c> target field.
    /// </summary>
    public bool SaTiming { get; set; }

    /// <summary>
    /// When true, stops after Phase 5 (semantic verification) and skips Phase 4 global
    /// desugaring, Phase 6 instantiation, Phase 7 postprocessing, and Phase 5b checks.
    /// Use for tests that only assert on SA errors or type annotations — saves ~10× time
    /// by avoiding monomorphization and lowering passes.
    /// </summary>
    public bool SaOnly { get; set; }

    #endregion

    #region Public API

    /// <summary>
    /// Analyzes a complete program AST.
    /// </summary>
    /// <param name="program">The program to analyze.</param>
    /// <param name="filePath">Optional source file path for import resolution.</param>
    /// <returns>Analysis result containing errors, warnings, and the populated type registry.</returns>
    public AnalysisResult Analyze(Program program, string? filePath = null)
    {
        _importSnapshots.Clear();
        _symbolNameSnapshots.Clear();
        _moduleNameSnapshots.Clear();
        _currentFilePath = filePath ?? program.Location.FileName;
        _currentModuleName = null;
        _importedModules.Clear();
        _importedSymbolNames.Clear();

        bool saTiming = SaTiming;
        var swPhase = Stopwatch.StartNew();
        void Mark(string label)
        {
            if (!saTiming) return;
            swPhase.Stop();
            Console.Error.WriteLine(value: $"[SA] {label}: {swPhase.ElapsedMilliseconds} ms");
            swPhase.Restart();
        }

        RunPhase1Declaration(program: program);
        Mark(label: "Phase 1 Declaration");
        CaptureCurrentImportStateSnapshot(filePath: _currentFilePath);
        RunPhase2Resolution(program: program);
        Mark(label: "Phase 2 Resolution");
        RunPhase3Synthesis(program: program);
        Mark(label: "Phase 3 Synthesis");
        RunPhase3Desugaring(program: program);
        Mark(label: "Phase 3 Desugaring");
        RunPhase5Verification(program: program);
        Mark(label: "Phase 5 Verification");
        // Register user program before global desugaring so GenericMonomorphizationPass can
        // search user-program ASTs for generic routine bodies (like FindInStdlib does for stdlib).
        _registry.RegisterUserProgram(program: program,
            filePath: _currentFilePath,
            module: _currentModuleName ?? "");

        if (!SaOnly)
        {
            CollectStdlibBodiesForVariantGeneration();
            Mark(label: "CollectStdlibBodies");
            RunPhase4GlobalDesugaring();
            Mark(label: "Phase 4 GlobalDesugaring");
            RunPhase6Instantiation();
            Mark(label: "Phase 6 Instantiation");
            RunPhase7Postprocessing(program: program);
            Mark(label: "Phase 7 Postprocessing");
            SurveyMarkerProtocolLeaks();
            RunPhase5bPostDesugarChecks();
            Mark(label: "Phase 5b PostDesugarChecks");
            FinalizeReturnTypes();
            Mark(label: "FinalizeReturnTypes");
        }

        // Merge synthesized operator bodies and pre-transformed variant bodies
        var allSynthesized = _synthesizedBodies.ToDictionary(keySelector: kvp => kvp.Key,
            elementSelector: kvp => kvp.Value.Body);
        foreach ((string key, Statement variantBody) in _variantBodies)
        {
            allSynthesized[key] = variantBody;
        }

        return new AnalysisResult(Registry: _registry,
            Errors: _errors.ToList(),
            Warnings: UserVisibleWarnings(),
            ParsedLiterals: _parsedLiterals,
            SynthesizedBodies: allSynthesized,
            InstantiatedGenericBodies: _instantiatedGenericBodies,
            LiveRoutineKeys: _liveRoutineKeys,
            LiveOwnerTypeNames: _liveOwnerTypeNames,
            MaySuspendRoutineKeys: _maySuspendRoutineKeys);
    }

    /// <summary>Phase 1: Collect all type shapes and routine stubs -> no names resolved.</summary>
    private void RunPhase1Declaration(Program program)
    {
        CollectDeclarations(program: program);
    }

    /// <summary>Phase 2: Resolve all bare names to qualified types.</summary>
    private void RunPhase2Resolution(Program program)
    {
        _typeBodyResolver.ResolveTypeBodies(program: program);
        _signatureResolver.ResolveAndRegisterPendingRoutines();
        _signatureResolver.ResolveExternalSignatures(program: program);
        // Reject self-containing value records BEFORE conformance analysis, which computes
        // LlvmType/SizeBytes and would otherwise stack-overflow on the cycle.
        if (!ValidateNoRecursiveValueRecords())
            _conformanceAnalyzer.ApplyImplicitMarkerConformance();
    }

    /// <summary>
    /// Phase 3: Generate synthesized wired routines and derived operators.
    /// Structural routines ($represent/$hash/$eq/$diagnose) remain as IsSynthesized stubs.
    /// Derived operators ($ne/$lt/$le/$gt/$ge/$notcontains) have real AST bodies stored in _synthesizedBodies.
    /// </summary>
    private void RunPhase3Synthesis(Program program)
    {
        AutoRegisterWiredRoutines();
        GenerateDerivedOperators();
        ValidateProtocolImplementations();
        PreRegisterUserVariants(program: program);
        // Snapshot mode: stdlib variants are already registered in the restored registry.
        if (!_snapshotMode) PreRegisterStdlibVariants();
    }

    /// <summary>
    /// Phase 5: Type-annotate and verify all routine bodies.
    /// Runs before Phase 4 because desugaring needs type-annotated AST.
    /// </summary>
    private void RunPhase5Verification(Program program)
    {
        AnalyzeBodies(program: program);
        AnalyzeSynthesizedBodies();
        // M-0: Annotate stdlib expression types so desugaring passes can lower stdlib bodies
        // uniformly (OperatorLoweringPass, ExpressionLoweringPass, etc.).
        // Stdlib errors and warnings are suppressed from user-visible output -> use 'validate-stdlib' to surface them.
        // Snapshot mode: stdlib bodies were analyzed during snapshot capture — skip the repeat.
        if (!_snapshotMode)
        {
            int errorsBeforeStdlib = _errors.Count;
            int warningsBeforeStdlib = _warnings.Count;
            AnalyzeStdlibBodies();
            if (_errors.Count > errorsBeforeStdlib)
                _errors.RemoveRange(index: errorsBeforeStdlib,
                    count: _errors.Count - errorsBeforeStdlib);
            if (_warnings.Count > warningsBeforeStdlib)
                _warnings.RemoveRange(index: warningsBeforeStdlib,
                    count: _warnings.Count - warningsBeforeStdlib);
        }
        EagerSynthesizeAllWrapperForwarders();
    }

    /// <summary>
    /// Phase 4 (global): Runs registry-wide synthesis once after all Phase 5 analysis.
    /// Generates error-handling variants, wired routine bodies, prunes unused generics,
    /// then applies Phase 3 passes to generated variant bodies and stdlib programs.
    /// Immediately followed by Phase 7 global: lowers variant bodies and stdlib with type-aware passes.
    /// </summary>
    private void RunPhase4GlobalDesugaring()
    {
        var ctx = new DesugaringContext(registry: _registry,
            routineBodies: _routineBodies,
            target: _target,
            buildMode: _buildMode);
        new DesugaringPipeline(ctx: ctx).RunGlobal();
        // Capture variant bodies produced by ErrorHandlingVariantPass for codegen.
        _variantBodies = ctx.VariantBodies;
        AnalyzeVariantBodies();

        // Phase 7 global: lower variant bodies and stdlib programs with type-aware passes.
        // Also pass synthesized operator bodies so CallOverloadResolutionPass can classify
        // the CallExpression nodes inside them (LoweringKind = Unknown otherwise).
        var synthesizedBodyStatements = _synthesizedBodies
            .ToDictionary(keySelector: kvp => kvp.Key, elementSelector: kvp => kvp.Value.Body);
        // Phase 4.5: re-run wired-routine synthesis to catch tuple types (and any other
        // lazily-registered types) created during Phase 5 SA. The original Phase 4 sweep
        // could not see these because they did not yet exist in the registry.
        // Re-run AutoRegisterWiredRoutines first so user variants (registered in Phase 3
        // per-file via PreRegisterUserVariants, AFTER the Phase 3 global AutoRegister sweep)
        // get their $represent/$diagnose stubs registered before WiredRoutinePass synthesizes
        // bodies. MaybeRegisterWired is idempotent on existing methods.
        AutoRegisterWiredRoutines();
        var lateCtx = new DesugaringContext(registry: _registry,
            routineBodies: _routineBodies,
            target: _target,
            buildMode: _buildMode) { VariantBodies = _variantBodies };
        new WiredRoutinePass(ctx: lateCtx).RunGlobal();

        var p7ctx = new PostprocessingContext(registry: _registry,
            variantBodies: _variantBodies,
            synthesizedBodies: synthesizedBodyStatements,
            target: _target,
            buildMode: _buildMode);
        new PostprocessingPipeline(ctx: p7ctx).RunGlobal();
    }

    /// <summary>
    /// Phase 6: close reachable generic bodies up front so codegen no longer owns the
    /// common-case monomorphization entry point.
    /// </summary>
    private void RunPhase6Instantiation()
    {
        // Include wrapper forwarder bodies in variantBodies so GMP can rewrite them with
        // concrete type substitutions. Without this, GMP creates empty-body sentinels for
        // concrete forwarder instances instead of properly monomorphized bodies.
        var mergedVariantBodies = new Dictionary<string, Statement>(_variantBodies);
        foreach (var (key, pair) in _synthesizedBodies)
        {
            // Include wrapper forwarders AND derived operators on generic owner types.
            // GMP must monomorphize both; Phase C must not emit the generic-def version.
            if (pair.Routine.WrapperForwarderInnerMethod != null ||
                pair.Routine.OwnerType?.IsGenericDefinition == true)
                mergedVariantBodies[key] = pair.Body;
        }

        var ctx = new InstantiationContext(registry: _registry,
            userPrograms: _registry.UserPrograms,
            routineBodies: _routineBodies,
            variantBodies: mergedVariantBodies,
            instantiatedGenericBodies: _instantiatedGenericBodies is Dictionary<string, MonomorphizedBody> dict
                ? dict
                : _instantiatedGenericBodies.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            target: _target,
            buildMode: _buildMode) { SaTiming = SaTiming };

        // Rewrite Referring[T]/Controlling[T] params to inner T before reachability so
        // the resulting RegistryKeys / mangled names captured downstream match codegen.
        // Call-site $refer/$control coercion was already injected during SA argument binding.
        // Pass mergedVariantBodies (not _variantBodies) so the dict used by reachability/GMP
        // gets re-keyed to the post-mutation form.
        var markerCtx = new PostprocessingContext(registry: _registry,
            variantBodies: mergedVariantBodies,
            synthesizedBodies: _synthesizedBodies.ToDictionary(
                keySelector: kvp => kvp.Key,
                elementSelector: kvp => kvp.Value.Body),
            target: _target,
            buildMode: _buildMode);
        // Insert scope-exit `$destroy()` calls BEFORE reachability (so the calls drive liveness —
        // no manual seeding needed) and BEFORE the marker pass (so Referring[T]/Controlling[T]
        // params are still protocol-typed and excluded as access types, not yet stripped to the
        // inner entity). Generic bodies are processed here too, then monomorphized with the calls.
        var teardownPass = new Compiler.Postprocessing.Passes.ScopeTeardownLoweringPass(markerCtx);
        foreach ((Program program, _, _) in _registry.UserPrograms)
            teardownPass.Run(program: program);
        foreach ((Program program, _, _) in _registry.StdlibPrograms)
            teardownPass.Run(program: program);
        teardownPass.RunOnVariantBodies();

        // Tear down owned RVALUE temporaries (heap-owning receiver/discarded producers) that the
        // binding-only ScopeTeardownLoweringPass cannot see. Runs AFTER teardown so it never
        // double-frees the temps' bindings, and BEFORE reachability so its $destroy calls drive
        // liveness. Stdlib + variant bodies are already Phase-7 lowered here (when→if done); USER
        // programs are lowered later (Phase 7 per-file), so they get this pass in RunPhase7Postprocessing.
        var tempTeardownPass = new Compiler.Postprocessing.Passes.TemporaryTeardownPass(markerCtx);
        foreach ((Program program, _, _) in _registry.StdlibPrograms)
            tempTeardownPass.Run(program: program);
        tempTeardownPass.RunOnBodies(markerCtx.VariantBodies);

        var markerPass = new MarkerProtocolDesugarPass(markerCtx);
        _markerPass = markerPass;
        markerPass.RewriteAllSignatures();
        foreach ((Program program, _, _) in _registry.UserPrograms)
        {
            MarkerProtocolDesugarPass.RewriteAstSignatures(program);
            markerPass.Run(program);
        }
        foreach ((Program program, _, _) in _registry.StdlibPrograms)
        {
            MarkerProtocolDesugarPass.RewriteAstSignatures(program);
            markerPass.Run(program);
        }
        markerPass.RunOnVariantBodies();
        markerPass.RunOnSynthesizedBodies();

        // Expand `is Crashable err` clauses BEFORE reachability so that the new
        // per-crashable `err.crash_message()` calls participate in liveness analysis.
        // Without this, the fanout happens in Phase 7 and the crash_message method on
        // each concrete crashable is never marked reachable -> linker errors.
        {
            var crashablePass = new Compiler.Postprocessing.Passes.CrashableExpansionPass(markerCtx);
            foreach ((Program program, _, _) in _registry.UserPrograms)
                crashablePass.Run(program);
            foreach ((Program program, _, _) in _registry.StdlibPrograms)
                crashablePass.Run(program);
        }

        if (SaTiming)
        {
            var sw = Stopwatch.StartNew();
            void Step(string label)
            {
                sw.Stop();
                Console.Error.WriteLine(value: $"[SA]   Phase 6 sub - {label}: {sw.ElapsedMilliseconds} ms");
                sw.Restart();
            }
            new ReachableGenericCollectionPass(ctx: ctx).Run();
            Step(label: "ReachableGenericCollectionPass");
            new RoutineReachabilityPass(ctx: ctx).Run();
            Step(label: "RoutineReachabilityPass");
            new GenericClosurePass(ctx: ctx).Run();
            Step(label: "GenericClosurePass");
            GenericCanonicalizationPass.Run();
            Step(label: "GenericCanonicalizationPass");
        }
        else
        {
            new InstantiationPipeline(ctx: ctx).Run();
        }

        _variantBodies = ctx.VariantBodies;
        _instantiatedGenericBodies = ctx.InstantiatedGenericBodies;
        _liveRoutineKeys = ctx.LiveRoutineKeys.ToArray();
        _liveOwnerTypeNames = ctx.LiveOwnerTypeNames.ToArray();

        // v0.2.0 may-suspend effect analysis over the call graph RoutineReachabilityPass populated
        // (in either the timed or pipeline path above). Runs here — after both branches — so it is
        // computed exactly once regardless of SaTiming. Empty for any program that never reaches a
        // suspend primitive (all current code), so codegen instruments nothing extra.
        ComputeMaySuspend(ctx: ctx);

        // Classify call expressions (set LoweringKind) in rewritten instantiated generic bodies.
        // GenericAstRewriter preserves source-AST structure but doesn't re-classify try_next
        // and other wired calls — they stay Unknown and cause codegen exceptions if not fixed here.
        var classCtx = new PostprocessingContext(registry: _registry,
            variantBodies: _variantBodies,
            target: _target,
            buildMode: _buildMode);
        new CallOverloadResolutionPass(classCtx).RunOnStatements(
            _instantiatedGenericBodies.Values.Select(selector: b => b.Ast.Body));
    }

    /// <summary>
    /// Runs the v0.2.0 may-suspend fixpoint over the call graph that
    /// <see cref="RoutineReachabilityPass"/> populated, storing the result for codegen's 5b-2
    /// cancellation instrumentation. Optional <c>RF_MAYSUSPEND_DUMP</c> writes the set for probes.
    /// </summary>
    private void ComputeMaySuspend(InstantiationContext ctx)
    {
        IReadOnlyCollection<string> maySuspend =
            new MaySuspendAnalysis(callGraph: ctx.MaySuspendGraph).Compute().ToArray();
        foreach (string key in maySuspend) ctx.MaySuspendRoutineKeys.Add(item: key);
        _maySuspendRoutineKeys = maySuspend;

        string? dumpPath = Environment.GetEnvironmentVariable(variable: "RF_MAYSUSPEND_DUMP");
        if (!string.IsNullOrEmpty(value: dumpPath))
        {
            var lines = new List<string> { "=== MAY-SUSPEND ROUTINES ===" };
            lines.AddRange(collection: maySuspend.OrderBy(keySelector: s => s));
            System.IO.File.WriteAllLines(path: dumpPath, contents: lines);
        }
    }

    /// <summary>
    /// Phase 3 (per-file): Syntax-only lowering that requires no type information.
    /// Runs before SA annotates ResolvedType on expressions.
    /// </summary>
    private void RunPhase3Desugaring(Program program)
    {
        var ctx = new DesugaringContext(registry: _registry,
            routineBodies: _routineBodies,
            target: _target,
            buildMode: _buildMode);
        new DesugaringPipeline(ctx: ctx).Run(program: program);
    }

    /// <summary>
    /// Phase 7 (per-file): Type-aware lowering on a verified, type-annotated program.
    /// Runs after SA has annotated ResolvedType on all expressions.
    /// </summary>
    private void RunPhase7Postprocessing(Program program)
    {
        var ctx = new PostprocessingContext(registry: _registry,
            variantBodies: _variantBodies,
            target: _target,
            buildMode: _buildMode);
        new PostprocessingPipeline(ctx: ctx).Run(program: program);

        // Owned rvalue-temporary teardown for user code, now that Phase 7 has lowered when→if so the
        // producing calls sit in real statements. ScopeTeardownLoweringPass already ran (pre-lowering,
        // step 4) and will not revisit this program, so the temps' bindings are freed exactly once by
        // the $destroy calls this pass emits (codegen emit-on-demand resolves the concrete $destroy).
        new Compiler.Postprocessing.Passes.TemporaryTeardownPass(ctx).Run(program: program);
    }

    /// <summary>
    /// Diagnostic survey: after Phase 6/7 monomorphization, walks every routine in the
    /// registry and reports any RoutineInfo whose Parameters still contain
    /// Referring[T]/Controlling[T]. Such routines indicate a creation path that bypassed
    /// MarkerProtocolDesugarPass.RewriteAllSignatures — call-site mangling will then
    /// diverge from definition-site mangling and produce LINKERRs.
    /// </summary>
    private void SurveyMarkerProtocolLeaks()
    {
        static bool IsMarker(TypeInfo? t)
        {
            if (t is not ProtocolTypeInfo p) return false;
            string n = (p.GenericDefinition ?? p).Name;
            return n is "Referring" or "Controlling";
        }

        static bool ContainsMarker(TypeInfo? t, HashSet<TypeInfo> seen)
        {
            if (t == null) return false;
            if (!seen.Add(t)) return false;
            if (IsMarker(t)) return true;
            if (t.TypeArguments is { Count: > 0 } args)
                foreach (TypeInfo a in args)
                    if (ContainsMarker(a, seen)) return true;
            return false;
        }

        int leakCount = 0;
        void Check(IEnumerable<RoutineInfo> rs, string bucket)
        {
            foreach (RoutineInfo r in rs)
            {
                for (int i = 0; i < r.Parameters.Count; i++)
                {
                    if (ContainsMarker(r.Parameters[i].Type, new HashSet<TypeInfo>()))
                    {
                        leakCount++;
                        Console.Error.WriteLine(
                            $"[MARKER-LEAK] bucket={bucket} routine={r.RegistryKey} " +
                            $"param[{i}]={r.Parameters[i].Name}:{r.Parameters[i].Type?.FullName} " +
                            $"isGenericDef={r.IsGenericDefinition} owner={r.OwnerType?.FullName}");
                        break;
                    }
                }
            }
        }

        _markerPass?.RescanLateResolutions();

        // GMP creates body.Info entries in Phase 6 whose params still wear Referring/Controlling;
        // RescanLateResolutions cleans the registry but not the codegen-side instantiated-body cache.
        // Rewrite those param types and re-key the dict + live-set so definition emission and
        // call-site mangling agree.
        if (_markerPass != null
            && _instantiatedGenericBodies is Dictionary<string, Compiler.Instantiation.MonomorphizedBody> bodyDict)
        {
            Dictionary<string, string> bodyKeyMap = _markerPass.RewriteInstantiatedBodyInfos(bodyDict);
            if (bodyKeyMap.Count > 0)
            {
                _liveRoutineKeys = _liveRoutineKeys
                    .Select(selector: k => bodyKeyMap.TryGetValue(k, value: out string? newK) ? newK : k)
                    .ToArray();
            }
        }

        Check(_registry.GetAllRoutines(), "routines");
        Check(_registry.GetAllRoutineResolutions(), "resolutions");
        if (leakCount > 0)
            Console.Error.WriteLine($"[MARKER-LEAK] total={leakCount}");
    }

    /// <summary>
    /// Phase 5b: validates that postprocessing produced a backend-safe normalized AST.
    /// </summary>
    private void RunPhase5bPostDesugarChecks()
    {
        var reprPass = new BackendRepresentationPass(registry: _registry, target: _target);
        var validator = new BackendEntryValidator(registry: _registry);

        foreach ((Program program, _, _) in _registry.UserPrograms)
        {
            reprPass.Run(program: program);
            foreach (SemanticError error in validator.ValidateProgram(program: program))
            {
                AddError(error: error);
            }
        }

        foreach ((Program stdlibProgram, _, _) in _registry.StdlibPrograms)
        {
            reprPass.Run(program: stdlibProgram);
        }

        foreach ((string key, Statement body) in _variantBodies)
        {
            reprPass.Run(statement: body);
            foreach (SemanticError error in validator.ValidateStatement(statement: body))
            {
                AddError(error: error with
                {
                    Message = $"[{key}] {error.Message}"
                });
            }
        }

        foreach ((string key, MonomorphizedBody mono) in _instantiatedGenericBodies)
        {
            if (!mono.IsSynthesized)
            {
                reprPass.Run(statement: mono.Ast.Body);
            }

            foreach (SemanticError error in BackendEntryValidator.ValidateMonomorphizedBody(body: mono))
            {
                AddError(error: error with
                {
                    Message = $"[mono:{key}] {error.Message}"
                });
            }
        }

    }

    /// <summary>
    /// Validates routine bodies in the standard library and returns the full error list.
    /// Used by the <c>validate-stdlib</c> CLI subcommand to surface stdlib errors that the
    /// normal build pipeline suppresses. The main build pipeline calls
    /// <see cref="AnalyzeStdlibBodies"/> (via M-0) but discards its errors so they don't
    /// block user builds.
    /// </summary>
    /// <returns>List of errors found in stdlib routine bodies.</returns>
    public List<SemanticError> ValidateStdlibBodies()
    {
        int errorsBefore = _errors.Count;

        // Run global phases that stdlib body analysis depends on
        // (StdlibLoader registered types and routines, but these phases were not run)
        _conformanceAnalyzer.ApplyImplicitMarkerConformance();
        AutoRegisterWiredRoutines();
        GenerateDerivedOperators();
        AnalyzeSynthesizedBodies();

        // Pre-register try_/check_/lookup_ stubs for all failable stdlib routines so that
        // stdlib bodies that call try_X (e.g. try_get_by_rank) resolve during body analysis.
        // Uses AST-level detection — no full body analysis or expression lowering required.
        PreRegisterStdlibVariants();

        AnalyzeStdlibBodies();

        // Collect stdlib-specific errors
        var stdlibErrors = new List<SemanticError>();
        for (int i = errorsBefore; i < _errors.Count; i++)
        {
            stdlibErrors.Add(item: _errors[index: i]);
        }

        return stdlibErrors;
    }

    /// <summary>
    /// Runs per-program body analysis on every stdlib program registered via StdlibLoader.
    /// Sets up the correct module/import context for each file, calls <see cref="AnalyzeBodies"/>
    /// (which type-annotates expressions and populates <c>_routineBodies</c>), then restores state.
    ///
    /// Assumes the caller has already run the Phase 2/3 prerequisites
    /// (<c>ApplyImplicitMarkerConformance</c>, <see cref="AutoRegisterWiredRoutines"/>,
    /// <see cref="GenerateDerivedOperators"/>). Errors are appended to <c>_errors</c> ->
    /// callers that need to partition stdlib errors must snapshot <c>_errors.Count</c> themselves.
    /// </summary>
    private void AnalyzeStdlibBodies()
    {
        if (_registry.StdlibPrograms.Count == 0)
        {
            return;
        }

        // Mark the registry so that any concrete generic instances created as side-effects
        // of stdlib body analysis are tagged IsStdlibLazy and excluded from GMP iteration.
        // Types the user program actually needs will be materialized when user SA references them.
        _registry.BeginStdlibAnalysis();
        try
        {

        string previousFilePath = _currentFilePath;
        var previousImports = new HashSet<string>(collection: _importedModules,
            comparer: StringComparer.OrdinalIgnoreCase);
        string? previousModuleName = _currentModuleName;
        int stdlibIdx = 0;
        foreach ((Program program, string filePath, string module) in _registry.StdlibPrograms)
        {
            stdlibIdx++;
            _currentFilePath = filePath;
            _currentModuleName = module;
            _importedModules.Clear();
            _importedSymbolNames.Clear();

            // Core module types are auto-imported
            _importedModules.Add(item: "Core");

            // Add the file's own module so sibling types resolve
            if (!string.IsNullOrEmpty(value: module))
            {
                _importedModules.Add(item: module);
            }

            // Process import declarations for this stdlib file
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                if (node is not ImportDeclaration import)
                {
                    continue;
                }

                string importModule = import.ModulePath;
                int dotIdx = importModule.IndexOf(value: '.');
                if (dotIdx > 0)
                {
                    _importedModules.Add(item: importModule[..dotIdx]);
                }

                _importedModules.Add(item: importModule);
            }

            AnalyzeBodies(program: program);
        }

        _currentFilePath = previousFilePath;
        _currentModuleName = previousModuleName;
        _importedModules.Clear();
        foreach (string ns in previousImports)
        {
            _importedModules.Add(item: ns);
        }

        } // try
        finally
        {
            _registry.EndStdlibAnalysis();
        }
    }

    /// <summary>
    /// Analyzes multiple program ASTs from a multi-file build.
    /// Phases are split so per-file phases run with correct import scoping,
    /// while global phases run once across the combined registry.
    /// </summary>
    /// <param name="files">The programs and their file paths, in topological (dependency) order.</param>
    /// <returns>Analysis result containing errors, warnings, and the populated type registry.</returns>
    public AnalysisResult AnalyzeMultiple(List<(Program Program, string FilePath)> files)
    {
        _importSnapshots.Clear();
        _symbolNameSnapshots.Clear();
        _moduleNameSnapshots.Clear();
        bool saTiming = SaTiming;
        var swPhase = Stopwatch.StartNew();
        void Mark(string label)
        {
            if (!saTiming) return;
            swPhase.Stop();
            Console.Error.WriteLine(value: $"[SA] {label}: {swPhase.ElapsedMilliseconds} ms");
            swPhase.Restart();
        }

        // Snapshot storage: file path -> imported modules after Phase 1
        var importSnapshots =
            new Dictionary<string, HashSet<string>>(comparer: StringComparer.OrdinalIgnoreCase);
        var symbolNameSnapshots =
            new Dictionary<string, HashSet<string>>(comparer: StringComparer.OrdinalIgnoreCase);
        var moduleNameSnapshots =
            new Dictionary<string, string?>(comparer: StringComparer.OrdinalIgnoreCase);

        // Every file in the build graph contributes its declarations via Phase 1 below.
        // Pre-mark their declared modules as provided so `import` statements between them
        // record the module name instead of re-loading the file through StdlibLoader
        // (which would register every routine a second time -> duplicate-definition errors).
        foreach ((Program program, string filePath) in files)
        {
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                if (node is ModuleDeclaration moduleDecl)
                {
                    _registry.MarkModuleProvided(modulePath: moduleDecl.Path, filePath: filePath);
                    break;
                }
            }
        }

        // Phase 1: Collect declarations from ALL files (populates registry with all types/routines)
        foreach ((Program program, string filePath) in files)
        {
            _currentFilePath = filePath;
            _currentModuleName = null;
            _importedModules.Clear();
            _importedSymbolNames.Clear();

            RunPhase1Declaration(program: program);

            importSnapshots[key: filePath] = new HashSet<string>(collection: _importedModules,
                comparer: StringComparer.OrdinalIgnoreCase);
            symbolNameSnapshots[key: filePath] =
                new HashSet<string>(collection: _importedSymbolNames,
                    comparer: StringComparer.Ordinal);
            moduleNameSnapshots[key: filePath] = _currentModuleName;
            CaptureCurrentImportStateSnapshot(filePath: filePath);
        }
        Mark(label: "Phase 1 -> Declarations");

        // Phase 1b: Re-resolve record/entity `obeys` conformances now that ALL files' types AND
        // every referenced (lazily-loaded) protocol are registered. Initial per-file declaration
        // resolution can drop a protocol whose definition wasn't loaded yet — e.g. a user module
        // record obeying a Core protocol (FloorDivisible) registered before that protocol's file
        // loaded — leaving ImplementedProtocols short. Re-resolving here (before signature
        // resolution runs the generic-constraint checks, RF-S150) fills them in.
        foreach ((Program program, string _) in files)
        {
            Compiler.Declaration.StdlibLoader.ResolveProgramProtocolConformances(registry: _registry, program: program);
        }
        Mark(label: "Phase 1b -> re-resolve conformances");

        // Phase 2: Resolve type bodies across ALL files (members can reference types from other files)
        foreach ((Program program, string filePath) in files)
        {
            RestoreImportState(filePath: filePath,
                importSnapshots: importSnapshots,
                symbolNameSnapshots: symbolNameSnapshots,
                moduleNameSnapshots: moduleNameSnapshots);

            _typeBodyResolver.ResolveTypeBodies(program: program);
            _signatureResolver.ResolveAndRegisterPendingRoutines(filterFilePath: filePath);
            _signatureResolver.ResolveExternalSignatures(program: program);
        }
        Mark(label: "Phase 2 -> Type/signature resolution");

        // Reject self-containing value records (incl. cross-file mutual recursion) BEFORE conformance
        // analysis, which computes LlvmType/SizeBytes and would otherwise stack-overflow on the cycle.
        // Bail out entirely on a cycle — every downstream phase computes layout and would crash.
        if (ValidateNoRecursiveValueRecords())
        {
            return new AnalysisResult(Registry: _registry,
                Errors: _errors.ToList(),
                Warnings: UserVisibleWarnings(),
                ParsedLiterals: _parsedLiterals,
                SynthesizedBodies: new Dictionary<string, Statement>(),
                InstantiatedGenericBodies: _instantiatedGenericBodies,
                LiveRoutineKeys: _liveRoutineKeys,
                LiveOwnerTypeNames: _liveOwnerTypeNames,
            MaySuspendRoutineKeys: _maySuspendRoutineKeys);
        }

        // Phase 2 global: once, registry-only -> no per-file import scoping needed
        _conformanceAnalyzer.ApplyImplicitMarkerConformance();
        Mark(label: "Phase 2 global -> implicit marker conformance");

        // Phase 3 global: synthesized routines, derived operators, protocol validation
        AutoRegisterWiredRoutines();
        Mark(label: "Phase 3 global -> AutoRegisterWiredRoutines");
        GenerateDerivedOperators();
        Mark(label: "Phase 3 global -> GenerateDerivedOperators");
        ValidateProtocolImplementations();
        Mark(label: "Phase 3 global -> ValidateProtocolImplementations");

        // Phase 3 per-file: pre-register error handling variants before Phase 5 body analysis
        foreach ((Program program, string filePath) in files)
        {
            RestoreImportState(filePath: filePath,
                importSnapshots: importSnapshots,
                symbolNameSnapshots: symbolNameSnapshots,
                moduleNameSnapshots: moduleNameSnapshots);

            PreRegisterUserVariants(program: program);
        }
        Mark(label: "Phase 3 per-file -> PreRegisterUserVariants");

        // Phase 3 global: pre-register stdlib failable method variants (try_next, try_recover, etc.)
        // Must run before Phase 5 user body analysis and before Phase 3 per-file desugaring
        // (ControlFlowLoweringPass generates try_next calls that Phase 5 must resolve).
        PreRegisterStdlibVariants();
        Mark(label: "Phase 3 global -> PreRegisterStdlibVariants");

        // Phase 3 per-file: syntax-only lowering (no type info needed; runs before SA annotates types)
        foreach ((Program program, string filePath) in files)
        {
            RestoreImportState(filePath: filePath,
                importSnapshots: importSnapshots,
                symbolNameSnapshots: symbolNameSnapshots,
                moduleNameSnapshots: moduleNameSnapshots);

            RunPhase3Desugaring(program: program);
        }
        Mark(label: "Phase 3 per-file -> syntax-only desugaring");

        // Phase 5: Analyze bodies per file (expressions need correct import scoping)
        foreach ((Program program, string filePath) in files)
        {
            RestoreImportState(filePath: filePath,
                importSnapshots: importSnapshots,
                symbolNameSnapshots: symbolNameSnapshots,
                moduleNameSnapshots: moduleNameSnapshots);

            AnalyzeBodies(program: program);
        }
        Mark(label: "Phase 5 per-file -> AnalyzeBodies (user)");

        // Phase 5 global: synthesized body analysis, modification inference
        AnalyzeSynthesizedBodies();
        Mark(label: "Phase 5 global -> AnalyzeSynthesizedBodies");
        // M-0: Annotate stdlib expression types so desugaring passes can lower stdlib bodies
        // uniformly (OperatorLoweringPass, ExpressionLoweringPass, etc.).
        // Stdlib errors are suppressed from user-visible output -> use 'validate-stdlib' to surface them.
        int errorsBeforeStdlib = _errors.Count;
        AnalyzeStdlibBodies();
        Mark(label: "Phase 5 global -> AnalyzeStdlibBodies");
        if (_errors.Count > errorsBeforeStdlib)
            _errors.RemoveRange(index: errorsBeforeStdlib,
                count: _errors.Count - errorsBeforeStdlib);
        EagerSynthesizeAllWrapperForwarders();
        Mark(label: "Phase 5 global -> EagerSynthesizeAllWrapperForwarders");

        // If SA produced errors in user code, skip desugaring. Lowering passes over a broken
        // AST produce garbage types and can drive GenericMonomorphizationPass's fixed-point loop
        // with <error>-typed instances. The CLI driver aborts on any errors.
        if (_errors.Count > 0)
        {
            return new AnalysisResult(Registry: _registry,
                Errors: _errors.ToList(),
                Warnings: UserVisibleWarnings(),
                ParsedLiterals: _parsedLiterals,
                SynthesizedBodies: new Dictionary<string, Statement>(),
                InstantiatedGenericBodies: _instantiatedGenericBodies,
                LiveRoutineKeys: _liveRoutineKeys,
            LiveOwnerTypeNames: _liveOwnerTypeNames,
            MaySuspendRoutineKeys: _maySuspendRoutineKeys);
        }

        foreach ((Program program, string filePath) in files)
        {
            string moduleName = moduleNameSnapshots.GetValueOrDefault(key: filePath) ?? "";
            _registry.RegisterUserProgram(program: program, filePath: filePath, module: moduleName);
        }

        // Phase 5.5 global: compute type liveness — mark which concrete generic instances are
        // actually reachable from routine signatures.  Must run before Phase 4 synthesis so that
        // WiredRoutinePass and GMP only operate on live types, preventing phantom instantiations
        // (e.g. BTreeListNode[Blank]) from reaching codegen.
        new TypeLivenessPass(registry: _registry).Run();
        Mark(label: "Phase 5.5 global -> TypeLivenessPass");

        if (!SaOnly)
        {
            // Phase 4 global: error handling variants + future global passes (runs once)
            CollectStdlibBodiesForVariantGeneration();
            Mark(label: "Phase 4 global -> CollectStdlibBodiesForVariantGeneration");
            RunPhase4GlobalDesugaring();
            Mark(label: "Phase 4 global -> RunPhase4GlobalDesugaring");
            RunPhase6Instantiation();
            Mark(label: "Phase 6 -> RunPhase6Instantiation (monomorphization)");

            // Phase 7 per-file: type-aware lowering on verified, type-annotated AST
            foreach ((Program program, string filePath) in files)
            {
                RestoreImportState(filePath: filePath,
                    importSnapshots: importSnapshots,
                    symbolNameSnapshots: symbolNameSnapshots,
                    moduleNameSnapshots: moduleNameSnapshots);

                RunPhase7Postprocessing(program: program);
            }
            Mark(label: "Phase 7 per-file -> type-aware postprocessing");

            SurveyMarkerProtocolLeaks();
            RunPhase5bPostDesugarChecks();
            Mark(label: "Phase 5b -> PostDesugarChecks");
            FinalizeReturnTypes();
            Mark(label: "Phase 5b -> FinalizeReturnTypes");
        }

        // Merge synthesized operator bodies and pre-transformed variant bodies
        var allSynthesized2 = _synthesizedBodies.ToDictionary(keySelector: kvp => kvp.Key,
            elementSelector: kvp => kvp.Value.Body);
        foreach ((string key, Statement variantBody) in _variantBodies)
        {
            allSynthesized2[key] = variantBody;
        }

        return new AnalysisResult(Registry: _registry,
            Errors: _errors.ToList(),
            Warnings: UserVisibleWarnings(),
            ParsedLiterals: _parsedLiterals,
            SynthesizedBodies: allSynthesized2,
            InstantiatedGenericBodies: _instantiatedGenericBodies,
            LiveRoutineKeys: _liveRoutineKeys,
            LiveOwnerTypeNames: _liveOwnerTypeNames,
            MaySuspendRoutineKeys: _maySuspendRoutineKeys);
    }

    /// <summary>
    /// Analyzes all synthesized AST bodies (derived operators registered in _synthesizedBodies).
    /// Provides semantic validation for bodies produced by GenerateDerivedOperators.
    /// </summary>
    private void AnalyzeSynthesizedBodies()
    {
        foreach ((string _, (RoutineInfo Routine, Statement Body) pair) in _synthesizedBodies)
        {
            AnalyzeCompilerGeneratedBody(routineInfo: pair.Routine, body: pair.Body,
                preservePresetTypes: true);
        }
    }

    /// <summary>
    /// Analyzes all error-handling variant bodies in the context of their registered RoutineInfo.
    /// These bodies are compiler-generated, but they still need full semantic annotation before
    /// the type-aware postprocessing pipeline rewrites operators and expressions.
    /// </summary>
    private void AnalyzeVariantBodies()
    {
        foreach ((string key, Statement body) in _variantBodies)
        {
            RoutineInfo? routineInfo = _registry.LookupRoutine(fullName: key) ??
                _registry.GetAllRoutines()
                         .FirstOrDefault(predicate: r => r.RegistryKey == key);
            if (routineInfo == null)
            {
                continue;
            }

            AnalyzeCompilerGeneratedBody(routineInfo: routineInfo, body: body);
        }
    }

    /// <summary>
    /// Analyzes a single compiler-generated AST body in the context of its RoutineInfo.
    /// Sets up scope and parameters identically to AnalyzeFunctionBody, but skips
    /// validation that doesn't apply to compiler-generated code.
    /// </summary>
    private void AnalyzeCompilerGeneratedBody(RoutineInfo routineInfo, Statement body,
        bool preservePresetTypes = false)
    {
        string previousFilePath = _currentFilePath;
        var previousImports = new HashSet<string>(collection: _importedModules,
            comparer: StringComparer.OrdinalIgnoreCase);
        var previousSymbols = new HashSet<string>(collection: _importedSymbolNames,
            comparer: StringComparer.Ordinal);
        string? previousModuleName = _currentModuleName;

        bool importRestored = TryRestoreImportStateForRoutine(routineInfo: routineInfo);
        if (!importRestored)
        {
            // Single-file path: no snapshot for stdlib files -> set up a minimal import
            // state so SA can resolve Core type annotations (S128, U32, etc.) in variant bodies.
            _importedModules.Add(item: "Core");
            if (!string.IsNullOrEmpty(value: routineInfo.Module))
            {
                _importedModules.Add(item: routineInfo.Module);
                int dotIdx = routineInfo.Module.IndexOf('.');
                if (dotIdx > 0)
                    _importedModules.Add(item: routineInfo.Module[..dotIdx]);
            }
        }

        RoutineInfo? prevRoutine = _currentRoutine;
        TypeSymbol? prevType = _currentType;
        _currentRoutine = routineInfo;
        _currentType = routineInfo.OwnerType;

        _registry.EnterScope(kind: ScopeKind.Function, name: routineInfo.Name);

        foreach (ParameterInfo param in routineInfo.Parameters)
        {
            _registry.DeclareVariable(name: param.Name, type: param.Type);
        }

        // Suppress errors for synthesized bodies -> they are compiler-generated and correct by construction.
        // Any error indicates a compiler bug, not user code error, so we don't surface them.
        // _isInCompilerGeneratedBody bypasses the wired-routine direct-call guard so SA can fully
        // annotate ResolvedType on all nodes (needed by CallOverloadResolutionPass later).
        bool prevIsInCompilerGeneratedBody = _isInCompilerGeneratedBody;
        bool prevPreservePresetTypes = _preservePresetTypes;
        _isInCompilerGeneratedBody = true;
        _preservePresetTypes = preservePresetTypes;
        int errorsBefore = _errors.Count;
        AnalyzeStatement(statement: body);
        if (_errors.Count > errorsBefore)
        {
            _errors.RemoveRange(index: errorsBefore, count: _errors.Count - errorsBefore);
        }
        _isInCompilerGeneratedBody = prevIsInCompilerGeneratedBody;
        _preservePresetTypes = prevPreservePresetTypes;

        _registry.ExitScope();
        _currentRoutine = prevRoutine;
        _currentType = prevType;
        _currentFilePath = previousFilePath;
        _currentModuleName = previousModuleName;
        _importedModules.Clear();
        foreach (string ns in previousImports)
            _importedModules.Add(item: ns);
        _importedSymbolNames.Clear();
        foreach (string symbol in previousSymbols)
            _importedSymbolNames.Add(item: symbol);
    }

    /// <summary>
    /// Phase 6: Sets ReturnType = Blank for every routine still carrying null after all analysis.
    /// Null is a transient "not yet inferred" state. Stdlib routines without a return type
    /// annotation never go through AnalyzeFunctionBody, so they keep null permanently unless
    /// this pass runs.
    /// </summary>
    private void FinalizeReturnTypes()
    {
        TypeSymbol? blank = _registry.LookupType(name: "Blank");
        if (blank == null)
        {
            return;
        }

        foreach (RoutineInfo routine in _registry.GetAllRoutines())
        {
            routine.ReturnType ??= blank;
        }
    }

    /// <summary>
    /// Restores per-file import state (_currentFilePath, _importedModules, _importedSymbolNames, _currentModuleName)
    /// from previously captured snapshots.
    /// </summary>
    private void RestoreImportState(string filePath,
        Dictionary<string, HashSet<string>> importSnapshots,
        Dictionary<string, HashSet<string>> symbolNameSnapshots,
        Dictionary<string, string?>? moduleNameSnapshots = null)
    {
        _currentFilePath = filePath;
        _importedModules.Clear();
        _importedSymbolNames.Clear();
        _currentModuleName = null;

        if (importSnapshots.TryGetValue(key: filePath, value: out HashSet<string>? imports))
        {
            foreach (string module in imports)
            {
                _importedModules.Add(item: module);
            }
        }

        if (symbolNameSnapshots.TryGetValue(key: filePath, value: out HashSet<string>? symbols))
        {
            foreach (string symbol in symbols)
            {
                _importedSymbolNames.Add(item: symbol);
            }
        }

        if (moduleNameSnapshots != null &&
            moduleNameSnapshots.TryGetValue(key: filePath, value: out string? moduleName))
        {
            _currentModuleName = moduleName;
        }
    }

    /// <summary>
    /// Performs the capture current import state snapshot step for this compiler phase.
    /// </summary>
    private void CaptureCurrentImportStateSnapshot(string filePath)
    {
        _importSnapshots[filePath] = new HashSet<string>(collection: _importedModules,
            comparer: StringComparer.OrdinalIgnoreCase);
        _symbolNameSnapshots[filePath] = new HashSet<string>(collection: _importedSymbolNames,
            comparer: StringComparer.Ordinal);
        _moduleNameSnapshots[filePath] = _currentModuleName;
    }

    /// <summary>
    /// Attempts to restore import state for routine and reports whether it succeeded.
    /// </summary>
    private bool TryRestoreImportStateForRoutine(RoutineInfo routineInfo)
    {
        string? locationFile = routineInfo.Location?.FileName;
        if (string.IsNullOrWhiteSpace(locationFile))
            return false;

        string? matchedFilePath = ResolveSnapshotFilePath(locationFile: locationFile);
        if (matchedFilePath == null)
            return false;

        RestoreImportState(filePath: matchedFilePath,
            importSnapshots: _importSnapshots,
            symbolNameSnapshots: _symbolNameSnapshots,
            moduleNameSnapshots: _moduleNameSnapshots);
        return true;
    }

    /// <summary>
    /// Resolves the snapshot file path from semantic compiler state.
    /// </summary>
    private string? ResolveSnapshotFilePath(string locationFile)
    {
        if (_importSnapshots.ContainsKey(locationFile))
        {
            return locationFile;
        }

        string locationFileName = Path.GetFileName(path: locationFile);
        return _importSnapshots.Keys.FirstOrDefault(candidate =>
            string.Equals(a: Path.GetFileName(path: candidate),
                b: locationFileName,
                comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the type registry after analysis.
    /// </summary>
    public TypeRegistry Registry => _registry;

    /// <summary>
    /// Gets all errors collected during analysis.
    /// </summary>
    public List<SemanticError> Errors => _errors;

    /// <summary>
    /// Gets all warnings collected during analysis.
    /// </summary>
    public List<SemanticWarning> Warnings => _warnings;

    #endregion

    #region Error Reporting

    /// <summary>
    /// Reports a semantic error with a diagnostic code.
    /// </summary>
    /// <param name="code">The diagnostic code for this error.</param>
    /// <param name="message">The error message.</param>
    /// <param name="location">The source location of the error.</param>
    internal void ReportError(SemanticDiagnosticCode code, string message, SourceLocation location)
    {
        AddError(error: new SemanticError(Code: code, Message: message, Location: location));
    }

    /// <summary>
    /// Reports a semantic warning with a diagnostic code.
    /// </summary>
    /// <param name="code">The diagnostic code for this warning.</param>
    /// <param name="message">The warning message.</param>
    /// <param name="location">The source location of the warning.</param>
    internal void ReportWarning(SemanticWarningCode code, string message, SourceLocation location)
    {
        if (SuppressedWarnings.Contains(item: code)) return;
        AddWarning(warning: new SemanticWarning(Code: code, Message: message, Location: location));
    }

    private static readonly HashSet<SemanticWarningCode> SuppressedWarnings = new()
    {
        SemanticWarningCode.UnusedRoutineReturnValue,
        SemanticWarningCode.UnhandledCrashableCall,
    };

    #endregion

    #region Type Resolution Delegation Stubs

    /// <summary>Resolves a type expression. Delegates to <see cref="TypeResolver"/>.</summary>
    public TypeSymbol ResolveType(TypeExpression? typeExpr) =>
        _typeResolver.ResolveType(typeExpr: typeExpr);

    /// <summary>Looks up a type by name, searching imported modules. Delegates to <see cref="TypeResolver"/>.</summary>
    internal TypeSymbol? LookupTypeWithImports(string name) =>
        _typeResolver.LookupTypeWithImports(name: name);

    /// <summary>Returns true if name is a generic type parameter in the current context. Delegates to <see cref="TypeResolver"/>.</summary>
    internal bool IsGenericParameter(string name) =>
        _typeResolver.IsGenericParameter(name: name);

    /// <summary>Resolves a type expression in a protocol context (handles 'Me'). Delegates to <see cref="TypeResolver"/>.</summary>
    internal TypeSymbol ResolveProtocolType(TypeExpression? typeExpr) =>
        _typeResolver.ResolveProtocolType(typeExpr: typeExpr);

    /// <summary>Looks up a routine by name, searching Core and imported modules. Delegates to <see cref="TypeResolver"/>.</summary>
    internal RoutineInfo? LookupRoutineWithImports(string name) =>
        _typeResolver.LookupRoutineWithImports(name: name);

    /// <summary>Validates that type arguments satisfy generic constraints. Delegates to <see cref="TypeResolver"/>.</summary>
    internal void ValidateGenericConstraints(TypeSymbol genericDef, List<TypeSymbol> typeArgs,
        SourceLocation location) =>
        _typeResolver.ValidateGenericConstraints(genericDef: genericDef,
            typeArgs: typeArgs,
            location: location);

    #endregion

    #region Helper Methods

    /// <summary>
    /// Walks the current scope chain and returns the fully-qualified module name
    /// for the scope being analyzed, or null if analysis is not inside any module scope.
    /// </summary>
    internal string? GetCurrentModuleName()
    {
        Scope? current = _registry.CurrentScope;
        var namespaces = new List<string>();

        while (current != null)
        {
            if (current is { Kind: ScopeKind.Module, Name: not null })
            {
                namespaces.Insert(index: 0, item: current.Name);
            }

            current = current.Parent;
        }

        return namespaces.Count > 0
            ? string.Join(separator: ".", values: namespaces)
            : _currentModuleName;
    }

    #endregion

    #region Pending Routine

    /// <summary>
    /// A routine declaration collected in Phase 1/2, pending resolution and registration in Phase 2.5.
    /// </summary>
    internal sealed record PendingRoutine(
        RoutineDeclaration Declaration,
        TypeSymbol? OwnerType,
        RoutineKind Kind,
        string RoutineName,
        string? Module,
        string FilePath);

    #endregion
}
