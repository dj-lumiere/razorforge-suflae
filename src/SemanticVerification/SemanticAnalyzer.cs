using Compiler.Resolution;
using Compiler.Desugaring;
using Compiler.Instantiation;
using Compiler.Postprocessing;
using Compiler.Targeting;

namespace SemanticVerification;

using Enums;
using TypeModel.Enums;
using Results;
using Scopes;
using TypeModel.Symbols;
using SyntaxTree;
using Compiler.Diagnostics;
using TypeSymbol = TypeModel.Types.TypeInfo;

/// <summary>
/// Semantic analyzer for RazorForge and Suflae programs.
/// Performs type checking, scope analysis, and inference for:
/// - Method modification (readonly/writable/migratable)
/// - Migratable modification tracking (buffer relocation detection)
/// - Error handling variant generation (try_/check_/lookup_)
/// </summary>
public sealed partial class SemanticAnalyzer
{
    #region Fields

    /// <summary>The type registry for storing and looking up types.</summary>
    internal readonly TypeRegistry _registry;

    /// <summary>Call graph for modification inference.</summary>
    private readonly CallGraph _callGraph = new();

    /// <summary>Modification inference engine.</summary>
    private ModificationInference? _modificationInference;

    /// <summary>Current call graph node for the routine being analyzed.</summary>
    private CallGraphNode? _currentCallGraphNode;

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
    internal TypeResolver _typeResolver = null!;

    /// <summary>Handles resolution of type bodies (member variables, protocol conformances, etc.).</summary>
    internal TypeBodyResolver _typeBodyResolver = null!;

    /// <summary>Handles resolution and registration of routine signatures.</summary>
    internal SignatureResolver _signatureResolver = null!;

    /// <summary>Handles implicit marker protocol conformance application.</summary>
    internal ProtocolConformanceAnalyzer _conformanceAnalyzer = null!;

    /// <summary>
    /// Pre-transformed bodies for error-handling variant routines (try_/check_/lookup_), produced
    /// by <see cref="Synthesis.ErrorHandlingVariantPass"/> during Phase 4 global desugaring.
    /// Merged into <see cref="SynthesizedBodies"/> when building the <see cref="AnalysisResult"/>.
    /// </summary>
    private Dictionary<string, Statement> _variantBodies = new();

    /// <summary>
    /// Pre-rewritten generic method bodies produced by <see cref="Instantiation.Passes.GenericMonomorphizationPass"/>.
    /// Captured from <see cref="Desugaring.DesugaringContext.PreMonomorphizedBodies"/> in
    /// <see cref="RunPhase4GlobalDesugaring"/> and forwarded to <see cref="AnalysisResult"/>.
    /// </summary>
    private IReadOnlyDictionary<string, MonomorphizedBody> _preMonomorphizedBodies =
        new Dictionary<string, MonomorphizedBody>();

    #endregion

    #region Constructor

    private readonly TargetConfig _target;
    private readonly RfBuildMode _buildMode;

    /// <summary>
    /// Initializes a new instance of the <see cref="SemanticAnalyzer"/> class.
    /// </summary>
    /// <param name="language">The language being analyzed (RazorForge or Suflae).</param>
    /// <param name="stdlibPath">Optional path to the stdlib directory.</param>
    /// <param name="target">Target platform ??drives BuilderService platform constants. Defaults to host.</param>
    /// <param name="buildMode">Build mode ??drives BuilderService.build_mode. Defaults to Debug.</param>
    public SemanticAnalyzer(Language language, string? stdlibPath = null,
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
        _currentFilePath = filePath ?? program.Location.FileName;
        _currentModuleName = null;
        _importedModules.Clear();
        _importedSymbolNames.Clear();

        RunPhase1Declaration(program: program);
        RunPhase2Resolution(program: program);
        RunPhase3Synthesis(program: program);
        RunPhase3Desugaring(program: program);
        RunPhase5Verification(program: program);
        // Register user program before global desugaring so GenericMonomorphizationPass can
        // search user-program ASTs for generic routine bodies (like FindInStdlib does for stdlib).
        _registry.RegisterUserProgram(program: program,
            filePath: _currentFilePath,
            module: _currentModuleName ?? "");
        CollectStdlibBodiesForVariantGeneration();
        RunPhase4GlobalDesugaring();
        RunPhase6Instantiation();
        RunPhase7Postprocessing(program: program);
        RunPhase5bPostDesugarChecks();
        FinalizeReturnTypes();

        // Merge synthesized operator bodies and pre-transformed variant bodies
        var allSynthesized = _synthesizedBodies.ToDictionary(keySelector: kvp => kvp.Key,
            elementSelector: kvp => kvp.Value.Body);
        foreach ((string key, Statement variantBody) in _variantBodies)
        {
            allSynthesized[key] = variantBody;
        }

        return new AnalysisResult(Registry: _registry,
            Errors: _errors.AsReadOnly(),
            Warnings: _warnings.AsReadOnly(),
            ParsedLiterals: _parsedLiterals,
            SynthesizedBodies: allSynthesized,
            PreMonomorphizedBodies: _preMonomorphizedBodies);
    }

    /// <summary>Phase 1: Collect all type shapes and routine stubs ??no names resolved.</summary>
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
        // Stdlib errors are suppressed from user-visible output ??use 'validate-stdlib' to surface them.
        int errorsBeforeStdlib = _errors.Count;
        AnalyzeStdlibBodies();
        if (_errors.Count > errorsBeforeStdlib)
            _errors.RemoveRange(index: errorsBeforeStdlib,
                count: _errors.Count - errorsBeforeStdlib);
        EagerSynthesizeAllWrapperForwarders();
        InferModificationCategories();
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

        // Phase 7 global: lower variant bodies and stdlib programs with type-aware passes.
        var p7ctx = new PostprocessingContext(registry: _registry,
            variantBodies: _variantBodies,
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
        var ctx = new InstantiationContext(registry: _registry,
            userPrograms: _registry.UserPrograms,
            routineBodies: _routineBodies,
            variantBodies: _variantBodies,
            preMonomorphizedBodies: _preMonomorphizedBodies is Dictionary<string, MonomorphizedBody> dict
                ? dict
                : _preMonomorphizedBodies.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            target: _target,
            buildMode: _buildMode);
        new InstantiationPipeline(ctx: ctx).Run();
        _variantBodies = ctx.VariantBodies;
        _preMonomorphizedBodies = ctx.PreMonomorphizedBodies;
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

    /// <summary>Phase 5b: Placeholder for verification passes on desugared AST.</summary>
    private void RunPhase5bPostDesugarChecks()
    {
        // Populated in follow-up sessions: memory safety, module referencing, etc.
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
    /// (<see cref="ApplyImplicitMarkerConformance"/>, <see cref="AutoRegisterWiredRoutines"/>,
    /// <see cref="GenerateDerivedOperators"/>). Errors are appended to <c>_errors</c> ??
    /// callers that need to partition stdlib errors must snapshot <c>_errors.Count</c> themselves.
    /// </summary>
    private void AnalyzeStdlibBodies()
    {
        if (_registry.StdlibPrograms.Count == 0)
        {
            return;
        }

        string previousFilePath = _currentFilePath;
        var previousImports = new HashSet<string>(collection: _importedModules,
            comparer: StringComparer.OrdinalIgnoreCase);
        string? previousModuleName = _currentModuleName;

        int stdlibTotal = _registry.StdlibPrograms.Count;
        int stdlibIdx = 0;
        foreach ((Program program, string filePath, string module) in _registry.StdlibPrograms)
        {
            stdlibIdx++;
            string shortName = Path.GetFileName(path: filePath);
            // Console.Error.WriteLine(
            //     value: $"[SA] stdlib {stdlibIdx}/{stdlibTotal}: {shortName}");

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
            foreach (IAstNode node in program.Declarations)
            {
                if (node is ImportDeclaration import)
                {
                    string importModule = import.ModulePath;
                    int dotIdx = importModule.IndexOf(value: '.');
                    if (dotIdx > 0)
                    {
                        _importedModules.Add(item: importModule[..dotIdx]);
                    }

                    _importedModules.Add(item: importModule);
                }
            }

            AnalyzeBodies(program: program);
            // Console.Error.WriteLine(value: $"[SA] done    {stdlibIdx}/{stdlibTotal}: " + $"{shortName}");
        }

        _currentFilePath = previousFilePath;
        _currentModuleName = previousModuleName;
        _importedModules.Clear();
        foreach (string ns in previousImports)
        {
            _importedModules.Add(item: ns);
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
        // Snapshot storage: file path ??imported modules after Phase 1
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
        }

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

        // Phase 2 global: once, registry-only ??no per-file import scoping needed
        _conformanceAnalyzer.ApplyImplicitMarkerConformance();

        // Phase 3 global: synthesized routines, derived operators, protocol validation
        AutoRegisterWiredRoutines();
        GenerateDerivedOperators();
        ValidateProtocolImplementations();

        // Phase 3 per-file: pre-register error handling variants before Phase 5 body analysis
        foreach ((Program program, string filePath) in files)
        {
            RestoreImportState(filePath: filePath,
                importSnapshots: importSnapshots,
                symbolNameSnapshots: symbolNameSnapshots,
                moduleNameSnapshots: moduleNameSnapshots);

            PreRegisterUserVariants(program: program);
        }

        // Phase 3 per-file: syntax-only lowering (no type info needed; runs before SA annotates types)
        foreach ((Program program, string filePath) in files)
        {
            RestoreImportState(filePath: filePath,
                importSnapshots: importSnapshots,
                symbolNameSnapshots: symbolNameSnapshots,
                moduleNameSnapshots: moduleNameSnapshots);

            RunPhase3Desugaring(program: program);
        }

        // Phase 5: Analyze bodies per file (expressions need correct import scoping)
        foreach ((Program program, string filePath) in files)
        {
            RestoreImportState(filePath: filePath,
                importSnapshots: importSnapshots,
                symbolNameSnapshots: symbolNameSnapshots,
                moduleNameSnapshots: moduleNameSnapshots);

            AnalyzeBodies(program: program);
        }

        // Phase 5 global: synthesized body analysis, modification inference
        AnalyzeSynthesizedBodies();
        InferModificationCategories();
        // M-0: Annotate stdlib expression types so desugaring passes can lower stdlib bodies
        // uniformly (OperatorLoweringPass, ExpressionLoweringPass, etc.).
        // Stdlib errors are suppressed from user-visible output ??use 'validate-stdlib' to surface them.
        int errorsBeforeStdlib = _errors.Count;
        AnalyzeStdlibBodies();
        if (_errors.Count > errorsBeforeStdlib)
            _errors.RemoveRange(index: errorsBeforeStdlib,
                count: _errors.Count - errorsBeforeStdlib);
        EagerSynthesizeAllWrapperForwarders();

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
                PreMonomorphizedBodies: _preMonomorphizedBodies);
        }

        foreach ((Program program, string filePath) in files)
        {
            string moduleName = moduleNameSnapshots.GetValueOrDefault(key: filePath) ?? "";
            _registry.RegisterUserProgram(program: program, filePath: filePath, module: moduleName);
        }

        // Phase 4 global: error handling variants + future global passes (runs once)
        CollectStdlibBodiesForVariantGeneration();
        RunPhase4GlobalDesugaring();
        RunPhase6Instantiation();

        // Phase 7 per-file: type-aware lowering on verified, type-annotated AST
        foreach ((Program program, string filePath) in files)
        {
            RestoreImportState(filePath: filePath,
                importSnapshots: importSnapshots,
                symbolNameSnapshots: symbolNameSnapshots,
                moduleNameSnapshots: moduleNameSnapshots);

            RunPhase7Postprocessing(program: program);
        }

        RunPhase5bPostDesugarChecks();
        FinalizeReturnTypes();

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
            PreMonomorphizedBodies: _preMonomorphizedBodies);
    }

    /// <summary>
    /// Analyzes all synthesized AST bodies (derived operators registered in _synthesizedBodies).
    /// Provides semantic validation for bodies produced by GenerateDerivedOperators.
    /// </summary>
    private void AnalyzeSynthesizedBodies()
    {
        foreach ((string _, (RoutineInfo Routine, Statement Body) pair) in _synthesizedBodies)
        {
            AnalyzeSynthesizedBody(routineInfo: pair.Routine, body: pair.Body);
        }
    }

    /// <summary>
    /// Analyzes a single synthesized AST body in the context of its RoutineInfo.
    /// Sets up scope and parameters identically to AnalyzeFunctionBody, but skips
    /// validation that doesn't apply to compiler-generated code.
    /// </summary>
    private void AnalyzeSynthesizedBody(RoutineInfo routineInfo, Statement body)
    {
        RoutineInfo? prevRoutine = _currentRoutine;
        TypeSymbol? prevType = _currentType;
        _currentRoutine = routineInfo;
        _currentType = routineInfo.OwnerType;

        _registry.EnterScope(kind: ScopeKind.Function, name: routineInfo.Name);

        foreach (ParameterInfo param in routineInfo.Parameters)
        {
            _registry.DeclareVariable(name: param.Name, type: param.Type);
        }

        // Suppress errors for synthesized bodies ??they are compiler-generated and correct by construction.
        // Any error indicates a compiler bug, not user code error, so we don't surface them.
        int errorsBefore = _errors.Count;
        AnalyzeStatement(statement: body);
        if (_errors.Count > errorsBefore)
        {
            _errors.RemoveRange(index: errorsBefore, count: _errors.Count - errorsBefore);
        }

        _registry.ExitScope();
        _currentRoutine = prevRoutine;
        _currentType = prevType;
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
        _warnings.Add(item: new SemanticWarning(Code: code, Message: message, Location: location));
    }

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
