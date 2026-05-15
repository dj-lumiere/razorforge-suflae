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

    /// <summary>Modification inference engine.</summary>
    private ModificationInference? _modificationInference;

    /// <summary>Errors collected during analysis.</summary>
    private readonly List<SemanticError> _errors = [];

    /// <summary>Warnings collected during analysis.</summary>
    private readonly List<SemanticWarning> _warnings = [];

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
    private (string Name, SourceLocation Location)? _lastDeclaredVariantVar;

    /// <summary>Tracks Lookup variables that must be dismantled before scope exit (#161).</summary>
    private readonly List<(string Name, SourceLocation Location)> _pendingLookupVars = [];

    /// <summary>Tracks variables invalidated by steal/ownership transfer (#11).</summary>
    private readonly HashSet<string> _deadrefVariables = [];

    /// <summary>Tracks the current for-loop iteration variable names for migratable check (#22).</summary>
    private readonly HashSet<string> _activeIterationSources = [];

    /// <summary>Routine declarations collected in Phase 1/2, pending resolution and registration in Phase 2.5.</summary>
    internal readonly List<PendingRoutine> _pendingRoutines = [];

    /// <summary>Tracks lock policy per variable for lock policy validation (#19).</summary>
    private readonly Dictionary<string, string> _variableLockPolicies = [];

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
    /// runtime dispatch stubs pre-registered by Phase 6b
    /// <see cref="Compiler.Postprocessing.Passes.RuntimeDispatchRegistrationPass"/>.
    /// Forwarded to <see cref="AnalysisResult"/> and then to codegen.
    /// </summary>
    private IReadOnlyDictionary<string, RuntimeDispatchEntry> _pendingRuntimeDispatches =
        new Dictionary<string, RuntimeDispatchEntry>();

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
        var tokens = new Compiler.Lexer.Tokenizer(
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

        var userWarnings = _warnings
            .Where(predicate: w => !string.IsNullOrEmpty(value: w.Location.FileName)
                               && !IsStdlibFile(filePath: w.Location.FileName))
            .ToList();
        return new AnalysisResult(Registry: _registry,
            Errors: _errors.AsReadOnly(),
            Warnings: userWarnings.AsReadOnly(),
            ParsedLiterals: _parsedLiterals,
            SynthesizedBodies: allSynthesized,
            InstantiatedGenericBodies: _instantiatedGenericBodies,
            PendingRuntimeDispatches: _pendingRuntimeDispatches,
            LiveRoutineKeys: _liveRoutineKeys,
            LiveOwnerTypeNames: _liveOwnerTypeNames);
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
            new GenericCanonicalizationPass(ctx: ctx).Run();
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
            _errors.AddRange(collection: validator.ValidateProgram(program: program));
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
                _errors.Add(item: error with
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
                _errors.Add(item: error with
                {
                    Message = $"[mono:{key}] {error.Message}"
                });
            }
        }

        // Phase 6b: pre-register all runtime dispatch stubs so codegen never discovers them lazily.
        _pendingRuntimeDispatches = new RuntimeDispatchRegistrationPass(registry: _registry)
            .Run(userPrograms: _registry.UserPrograms,
                variantBodies: _variantBodies,
                instantiatedGenericBodies: _instantiatedGenericBodies);
    }

    /// <summary>
    /// Validates routine bodies in the standard library and returns the full error list.
    /// Used by the <c>validate-stdlib</c> CLI subcommand to surface stdlib errors that the
    /// normal build pipeline suppresses. The main build pipeline calls
    /// <see cref="AnalyzeStdlibBodies"/> (via M-0) but discards its errors so they don't
    /// block user builds.
    /// </summary>
    /// <returns>List of errors found in stdlib routine bodies.</returns>
    public IReadOnlyList<SemanticError> ValidateStdlibBodies()
    {
        int errorsBefore = _errors.Count;

        // Run global phases that stdlib body analysis depends on
        // (StdlibLoader registered types and routines, but these phases were not run)
        _conformanceAnalyzer.ApplyImplicitMarkerConformance();
        AutoRegisterWiredRoutines();
        GenerateDerivedOperators();
        AnalyzeSynthesizedBodies();

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
    public AnalysisResult AnalyzeMultiple(IReadOnlyList<(Program Program, string FilePath)> files)
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
                Errors: _errors.AsReadOnly(),
                Warnings: _warnings.AsReadOnly(),
                ParsedLiterals: _parsedLiterals,
                SynthesizedBodies: new Dictionary<string, Statement>(),
                InstantiatedGenericBodies: _instantiatedGenericBodies,
                PendingRuntimeDispatches: _pendingRuntimeDispatches,
                LiveRoutineKeys: _liveRoutineKeys,
            LiveOwnerTypeNames: _liveOwnerTypeNames);
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
            Errors: _errors.AsReadOnly(),
            Warnings: _warnings.AsReadOnly(),
            ParsedLiterals: _parsedLiterals,
            SynthesizedBodies: allSynthesized2,
            InstantiatedGenericBodies: _instantiatedGenericBodies,
            PendingRuntimeDispatches: _pendingRuntimeDispatches,
            LiveRoutineKeys: _liveRoutineKeys,
            LiveOwnerTypeNames: _liveOwnerTypeNames);
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
    public IReadOnlyList<SemanticError> Errors => _errors;

    /// <summary>
    /// Gets all warnings collected during analysis.
    /// </summary>
    public IReadOnlyList<SemanticWarning> Warnings => _warnings;

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
        _errors.Add(item: new SemanticError(Code: code, Message: message, Location: location));
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
        _warnings.Add(item: new SemanticWarning(Code: code, Message: message, Location: location));
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
