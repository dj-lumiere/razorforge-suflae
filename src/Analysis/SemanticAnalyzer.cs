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

    /// <summary>Modules imported by the current file. Used for type resolution of non-Core types.</summary>
    private readonly HashSet<string> _importedModules = new(StringComparer.OrdinalIgnoreCase);

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
        _importedModules.Clear();

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
            : null;
    }

    #endregion
}
