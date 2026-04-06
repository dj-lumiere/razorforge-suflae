namespace SemanticAnalysis;

using Enums;
using Inference;
using Results;
using Scopes;
using Symbols;
using SyntaxTree;
using Diagnostics;
using TypeSymbol = Types.TypeInfo;

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
    private readonly TypeRegistry _registry;

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
    private RoutineInfo? _currentRoutine;

    /// <summary>Current type being analyzed (for me reference resolution).</summary>
    private TypeSymbol? _currentType;

    /// <summary>Danger block nesting depth (0 = not in danger block, >0 = inside danger block).</summary>
    private int _dangerBlockDepth;

    /// <summary>Gets whether we're currently inside a danger block.</summary>
    private bool InDangerBlock => _dangerBlockDepth > 0;

    /// <summary>Member variable names seen in the current type during body resolution (for duplicate detection).</summary>
    private HashSet<string>? _currentTypeMemberVariableNames;

    /// <summary>The source file path of the program being analyzed (for import resolution).</summary>
    private string _currentFilePath = string.Empty;

    /// <summary>The module name declared in the current file (from 'module' declaration).</summary>
    private string? _currentModuleName;

    /// <summary>Modules imported by the current file. Used for type resolution of non-Core types.</summary>
    private readonly HashSet<string> _importedModules =
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

    /// <summary>Whether the current routine is a generator (uses emit).</summary>
    private bool _currentRoutineIsGenerator;

    /// <summary>Routine declarations collected in Phase 1/2, pending resolution and registration in Phase 2.5.</summary>
    private readonly List<PendingRoutine> _pendingRoutines = [];

    /// <summary>Tracks lock policy per variable for lock policy validation (#19).</summary>
    private readonly Dictionary<string, string> _variableLockPolicies = [];

    /// <summary>Temporary: last share[Policy]() call info, propagated in variable declaration (#19).</summary>
    private (string SourceVar, string Policy)? _lastSharePolicy;

    /// <summary>Tracks (TypeName, ProtocolName) pairs added by implicit marker conformance, excluded from validation.</summary>
    private readonly HashSet<(string TypeName, string ProtocolName)>
        _implicitProtocolConformances = [];

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="SemanticAnalyzer"/> class.
    /// </summary>
    /// <param name="language">The language being analyzed (RazorForge or Suflae).</param>
    /// <param name="stdlibPath">Optional path to the stdlib directory.</param>
    public SemanticAnalyzer(Language language, string? stdlibPath = null)
    {
        _registry = new TypeRegistry(language: language, stdlibPath: stdlibPath);
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
        // Store file path for import resolution
        _currentFilePath = filePath ?? program.Location.FileName;
        _currentModuleName = null;
        _importedModules.Clear();
        _importedSymbolNames.Clear();

        // Phase 1: Collect all type and routine declarations (forward declarations)
        CollectDeclarations(program: program);

        // Phase 2: Resolve type bodies (member variables, method signatures)
        ResolveTypeBodies(program: program);

        // Phase 2.5: Resolve routine signatures and register (parameter types, protocol-as-type desugaring)
        ResolveAndRegisterPendingRoutines();
        ResolveExternalSignatures(program: program);

        // Phase 2.54: Apply implicit marker protocol conformance (record → RecordType, etc.)
        ApplyImplicitMarkerConformance();

        // Phase 2.55: Auto-register builder-generated member routines ($represent, $hash, $eq, etc.)
        AutoRegisterWiredRoutines();

        // Phase 2.6: Generate derived comparison operators ($ne from $eq, $lt/$le/$gt/$ge from $cmp)
        GenerateDerivedOperators();

        // Phase 2.7: Validate protocol implementations (ensure types implement all required protocol methods)
        ValidateProtocolImplementations();

        // Phase 3: Analyze routine bodies and expressions
        AnalyzeBodies(program: program);

        // Phase 3b: Analyze stdlib bodies to populate _routineBodies for variant generation.
        CollectStdlibBodiesForVariantGeneration();

        // Phase 4: Modification inference (call graph propagation)
        InferModificationCategories();

        // Phase 5: Error handling variant generation
        GenerateErrorHandlingVariants();

        // Phase 6: Finalize return types — any routine still null gets Blank
        FinalizeReturnTypes();

        return new AnalysisResult(Registry: _registry,
            Errors: _errors.AsReadOnly(),
            Warnings: _warnings.AsReadOnly(),
            ParsedLiterals: _parsedLiterals);
    }

    /// <summary>
    /// Validates routine bodies in the standard library.
    /// Analyzes all stdlib routine bodies that were registered during loading,
    /// catching type errors, unknown identifiers, and other semantic issues.
    /// </summary>
    /// <returns>List of errors found in stdlib routine bodies.</returns>
    public IReadOnlyList<SemanticError> ValidateStdlibBodies()
    {
        string previousFilePath = _currentFilePath;
        var previousImports = new HashSet<string>(collection: _importedModules,
            comparer: StringComparer.OrdinalIgnoreCase);
        int errorsBefore = _errors.Count;

        // Run global phases that stdlib body analysis depends on
        // (StdlibLoader registered types and routines, but these phases were not run)
        ApplyImplicitMarkerConformance();
        AutoRegisterWiredRoutines();
        GenerateDerivedOperators();

        foreach ((Program program, string filePath, string module) in _registry.StdlibPrograms)
        {
            _currentFilePath = filePath;
            _importedModules.Clear();

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
        }

        // Collect stdlib-specific errors
        var stdlibErrors = new List<SemanticError>();
        for (int i = errorsBefore; i < _errors.Count; i++)
        {
            stdlibErrors.Add(item: _errors[index: i]);
        }

        // Restore previous state
        _currentFilePath = previousFilePath;
        _importedModules.Clear();
        foreach (string ns in previousImports)
        {
            _importedModules.Add(item: ns);
        }

        return stdlibErrors;
    }

    /// <summary>
    /// Runs body analysis on all stdlib programs solely to populate <c>_routineBodies</c>
    /// for error-handling variant generation (Phase 5). Any errors produced are discarded —
    /// stdlib correctness is validated separately by the <c>ValidateStdlib</c> command.
    /// </summary>
    /// <summary>
    /// Lightweight scan of stdlib programs to populate <c>_routineBodies</c> for variant generation
    /// (Phase 5). Unlike <see cref="ValidateStdlibBodies"/>, this performs no type-checking —
    /// it only walks AST nodes looking for failable routines whose bodies contain throw/absent.
    /// This avoids corrupting SA state with stdlib-specific resolution errors.
    /// </summary>
    /// <remarks>
    /// HACK — tracked as S220. The real fix is to integrate stdlib body analysis into the normal
    /// Pass 3 pipeline so <c>_routineBodies</c> is always populated for all routines regardless
    /// of whether they come from stdlib or user files.
    /// </remarks>
    private void CollectStdlibBodiesForVariantGeneration()
    {
        if (_registry.StdlibPrograms.Count == 0)
        {
            return;
        }

        // TODO: This doesn't work for any modules beside Core.
        string previousFilePath = _currentFilePath;
        var previousImports = new HashSet<string>(collection: _importedModules,
            comparer: StringComparer.OrdinalIgnoreCase);
        string? previousModuleName = _currentModuleName;

        var scanner = new Inference.ErrorHandlingGenerator(registry: _registry);

        foreach ((Program program, string filePath, string module) in _registry.StdlibPrograms)
        {
            _currentFilePath = filePath;
            _currentModuleName = module;
            _importedModules.Clear();

            _importedModules.Add(item: "Core");
            if (!string.IsNullOrEmpty(value: module))
            {
                _importedModules.Add(item: module);
            }

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

            ScanFallibleBodiesForVariantGeneration(program: program, scanner: scanner);
        }

        // Restore state
        _currentFilePath = previousFilePath;
        _currentModuleName = previousModuleName;
        _importedModules.Clear();
        foreach (string ns in previousImports)
        {
            _importedModules.Add(item: ns);
        }
    }

    /// <summary>
    /// Scans a single program's failable routine declarations and stores those with
    /// throw/absent bodies in <c>_routineBodies</c> for error-handling variant generation.
    /// No type-checking is performed — only AST structure is examined.
    /// </summary>
    private void ScanFallibleBodiesForVariantGeneration(Program program,
        Inference.ErrorHandlingGenerator scanner)
    {
        // TODO: This should be done AFTER all the generic resolved routine has been added.
        foreach (IAstNode node in program.Declarations)
        {
            if (node is RoutineDeclaration routine && routine.IsFailable && routine.Body != null)
            {
                // Only store bodies that have throw/absent — LLVM IR routines don't
                if (!scanner.BodyHasThrowOrAbsent(body: routine.Body))
                {
                    continue;
                }

                // Compute registry key (same logic as AnalyzeFunctionBody)
                string baseName;
                if (routine.Name.Contains(value: '.'))
                {
                    int dotIndex = routine.Name.IndexOf(value: '.');
                    string typeName = routine.Name[..dotIndex];
                    string methodName = routine.Name[(dotIndex + 1)..];
                    string lookupName = typeName.Contains(value: '[')
                        ? typeName[..typeName.IndexOf(value: '[')]
                        : typeName;
                    TypeSymbol? ownerType = LookupTypeWithImports(name: lookupName);
                    baseName = ownerType != null
                        ? $"{ownerType.Name}.{methodName}"
                        : routine.Name;
                }
                else
                {
                    string? mod = GetCurrentModuleName();
                    baseName = string.IsNullOrEmpty(value: mod)
                        ? routine.Name
                        : $"{mod}.{routine.Name}";
                }

                RoutineInfo? routineInfo = _registry.LookupRoutine(fullName: baseName);
                if (routineInfo is { IsFailable: true })
                {
                    StoreRoutineBody(routine: routineInfo, body: routine.Body);
                }
            }
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
        // Snapshot storage: file path → imported modules after Phase 1
        var importSnapshots =
            new Dictionary<string, HashSet<string>>(comparer: StringComparer.OrdinalIgnoreCase);
        var symbolNameSnapshots =
            new Dictionary<string, HashSet<string>>(comparer: StringComparer.OrdinalIgnoreCase);
        var moduleNameSnapshots =
            new Dictionary<string, string?>(comparer: StringComparer.OrdinalIgnoreCase);

        // Pass 1: Collect declarations from ALL files (populates registry with all types/routines)
        foreach ((Program program, string filePath) in files)
        {
            _currentFilePath = filePath;
            _currentModuleName = null;
            _importedModules.Clear();
            _importedSymbolNames.Clear();

            CollectDeclarations(program: program);

            // Snapshot the imported modules and module name for this file
            importSnapshots[key: filePath] = new HashSet<string>(collection: _importedModules,
                comparer: StringComparer.OrdinalIgnoreCase);
            symbolNameSnapshots[key: filePath] =
                new HashSet<string>(collection: _importedSymbolNames,
                    comparer: StringComparer.Ordinal);
            moduleNameSnapshots[key: filePath] = _currentModuleName;
        }

        // Pass 2: Resolve type bodies across ALL files (members can reference types from other files)
        foreach ((Program program, string filePath) in files)
        {
            RestoreImportState(filePath: filePath,
                importSnapshots: importSnapshots,
                symbolNameSnapshots: symbolNameSnapshots,
                moduleNameSnapshots: moduleNameSnapshots);

            ResolveTypeBodies(program: program);
            ResolveAndRegisterPendingRoutines(filterFilePath: filePath);
            ResolveExternalSignatures(program: program);
        }

        // Global passes (once, registry-only — no per-file import scoping needed)
        ApplyImplicitMarkerConformance();
        AutoRegisterWiredRoutines();
        GenerateDerivedOperators();
        ValidateProtocolImplementations();

        // Pass 3: Analyze bodies per file (expressions need correct import scoping)
        foreach ((Program program, string filePath) in files)
        {
            RestoreImportState(filePath: filePath,
                importSnapshots: importSnapshots,
                symbolNameSnapshots: symbolNameSnapshots,
                moduleNameSnapshots: moduleNameSnapshots);

            AnalyzeBodies(program: program);
        }

        // Global passes (once, consume accumulated call graph + routine bodies)
        InferModificationCategories();

        // Pass 3b: Analyze stdlib bodies to populate _routineBodies for variant generation.
        // Stdlib routines ($next!, $getitem!, etc.) are failable and need try_/check_/lookup_
        // variants. Their bodies are not in 'files' (user code only), so we scan them separately
        // and discard any errors (stdlib errors are handled by the ValidateStdlib command).
        CollectStdlibBodiesForVariantGeneration();

        GenerateErrorHandlingVariants();

        // Phase 6: Finalize return types — any routine still null gets Blank
        FinalizeReturnTypes();

        return new AnalysisResult(Registry: _registry,
            Errors: _errors.AsReadOnly(),
            Warnings: _warnings.AsReadOnly(),
            ParsedLiterals: _parsedLiterals);
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
    private void ReportError(SemanticDiagnosticCode code, string message, SourceLocation location)
    {
        _errors.Add(item: new SemanticError(Code: code, Message: message, Location: location));
    }

    /// <summary>
    /// Reports a semantic warning with a diagnostic code.
    /// </summary>
    /// <param name="code">The diagnostic code for this warning.</param>
    /// <param name="message">The warning message.</param>
    /// <param name="location">The source location of the warning.</param>
    private void ReportWarning(SemanticWarningCode code, string message, SourceLocation location)
    {
        _warnings.Add(item: new SemanticWarning(Code: code, Message: message, Location: location));
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Walks the current scope chain and returns the fully-qualified module name
    /// for the scope being analyzed, or null if analysis is not inside any module scope.
    /// </summary>
    private string? GetCurrentModuleName()
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
    private sealed record PendingRoutine(
        RoutineDeclaration Declaration,
        TypeSymbol? OwnerType,
        RoutineKind Kind,
        string RoutineName,
        string? Module,
        string FilePath);

    #endregion
}
