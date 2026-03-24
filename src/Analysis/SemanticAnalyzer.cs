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
    private readonly HashSet<string> _importedModules = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tracks imported symbol names for collision detection (#105).</summary>
    private readonly HashSet<string> _importedSymbolNames = new(StringComparer.Ordinal);

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

    /// <summary>Tracks lock policy per variable for lock policy validation (#19).</summary>
    private readonly Dictionary<string, string> _variableLockPolicies = [];

    /// <summary>Temporary: last share[Policy]() call info, propagated in variable declaration (#19).</summary>
    private (string SourceVar, string Policy)? _lastSharePolicy;

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

        // Phase 2.5: Resolve routine signatures (parameter types, protocol-as-type desugaring)
        ResolveRoutineSignatures(program: program);

        // Phase 2.55: Auto-register builder-generated member routines (Text, hash, __eq__, etc.)
        AutoRegisterBuiltinRoutines();

        // Phase 2.6: Generate derived comparison operators (__ne__ from __eq__, __lt__/__le__/__gt__/__ge__ from __cmp__)
        GenerateDerivedOperators();

        // Phase 2.7: Validate protocol implementations (ensure types implement all required protocol methods)
        ValidateProtocolImplementations();

        // Phase 3: Analyze routine bodies and expressions
        AnalyzeBodies(program: program);

        // Phase 4: Modification inference (call graph propagation)
        InferModificationCategories();

        // Phase 5: Error handling variant generation
        GenerateErrorHandlingVariants();

        return new AnalysisResult(
            Registry: _registry,
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
        var previousImports = new HashSet<string>(_importedModules, StringComparer.OrdinalIgnoreCase);
        int errorsBefore = _errors.Count;

        // Run global phases that stdlib body analysis depends on
        // (StdlibLoader registered types and routines, but these phases were not run)
        AutoRegisterBuiltinRoutines();
        GenerateDerivedOperators();

        foreach (var (program, filePath, module) in _registry.StdlibPrograms)
        {
            _currentFilePath = filePath;
            _importedModules.Clear();

            // Core module types are auto-imported
            _importedModules.Add("Core");

            // Add the file's own module so sibling types resolve
            if (!string.IsNullOrEmpty(module))
            {
                _importedModules.Add(module);
            }

            // Process import declarations for this stdlib file
            foreach (var node in program.Declarations)
            {
                if (node is ImportDeclaration import)
                {
                    string importModule = import.ModulePath;
                    int dotIdx = importModule.IndexOf('.');
                    if (dotIdx > 0)
                    {
                        _importedModules.Add(importModule[..dotIdx]);
                    }
                    _importedModules.Add(importModule);
                }
            }

            AnalyzeBodies(program);
        }

        // Collect stdlib-specific errors
        var stdlibErrors = new List<SemanticError>();
        for (int i = errorsBefore; i < _errors.Count; i++)
        {
            stdlibErrors.Add(_errors[i]);
        }

        // Restore previous state
        _currentFilePath = previousFilePath;
        _importedModules.Clear();
        foreach (string ns in previousImports)
        {
            _importedModules.Add(ns);
        }

        return stdlibErrors;
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
        var importSnapshots = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var symbolNameSnapshots = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var moduleNameSnapshots = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: Collect declarations from ALL files (populates registry with all types/routines)
        foreach (var (program, filePath) in files)
        {
            _currentFilePath = filePath;
            _currentModuleName = null;
            _importedModules.Clear();
            _importedSymbolNames.Clear();

            CollectDeclarations(program: program);

            // Snapshot the imported modules and module name for this file
            importSnapshots[filePath] = new HashSet<string>(_importedModules, StringComparer.OrdinalIgnoreCase);
            symbolNameSnapshots[filePath] = new HashSet<string>(_importedSymbolNames, StringComparer.Ordinal);
            moduleNameSnapshots[filePath] = _currentModuleName;
        }

        // Pass 2: Resolve type bodies across ALL files (members can reference types from other files)
        foreach (var (program, filePath) in files)
        {
            RestoreImportState(filePath, importSnapshots, symbolNameSnapshots, moduleNameSnapshots);

            ResolveTypeBodies(program: program);
            ResolveRoutineSignatures(program: program);
        }

        // Global passes (once, registry-only — no per-file import scoping needed)
        AutoRegisterBuiltinRoutines();
        GenerateDerivedOperators();
        ValidateProtocolImplementations();

        // Pass 3: Analyze bodies per file (expressions need correct import scoping)
        foreach (var (program, filePath) in files)
        {
            RestoreImportState(filePath, importSnapshots, symbolNameSnapshots, moduleNameSnapshots);

            AnalyzeBodies(program: program);
        }

        // Global passes (once, consume accumulated call graph + routine bodies)
        InferModificationCategories();
        GenerateErrorHandlingVariants();

        return new AnalysisResult(
            Registry: _registry,
            Errors: _errors.AsReadOnly(),
            Warnings: _warnings.AsReadOnly(),
            ParsedLiterals: _parsedLiterals);
    }

    /// <summary>
    /// Restores per-file import state (_currentFilePath, _importedModules, _importedSymbolNames, _currentModuleName)
    /// from previously captured snapshots.
    /// </summary>
    private void RestoreImportState(
        string filePath,
        Dictionary<string, HashSet<string>> importSnapshots,
        Dictionary<string, HashSet<string>> symbolNameSnapshots,
        Dictionary<string, string?>? moduleNameSnapshots = null)
    {
        _currentFilePath = filePath;
        _importedModules.Clear();
        _importedSymbolNames.Clear();
        _currentModuleName = null;

        if (importSnapshots.TryGetValue(filePath, out var imports))
        {
            foreach (string module in imports)
                _importedModules.Add(module);
        }

        if (symbolNameSnapshots.TryGetValue(filePath, out var symbols))
        {
            foreach (string symbol in symbols)
                _importedSymbolNames.Add(symbol);
        }

        if (moduleNameSnapshots != null && moduleNameSnapshots.TryGetValue(filePath, out var moduleName))
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
}
