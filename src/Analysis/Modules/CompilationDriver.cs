namespace Compilers.Analysis.Modules;

using Compilers.Analysis.Enums;
using Compilers.Analysis.Results;
using Compilers.RazorForge.Lexer;
using Compilers.RazorForge.Parser;
using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;
using Compilers.Suflae.Lexer;
using Compilers.Suflae.Parser;

/// <summary>
/// Result of compiling a single source file.
/// </summary>
/// <param name="FilePath">The path to the source file.</param>
/// <param name="Namespace">The namespace declared in the file (module path).</param>
/// <param name="Ast">The parsed AST.</param>
/// <param name="Imports">Import declarations found in the file.</param>
/// <param name="ParseWarnings">Warnings from parsing.</param>
public sealed record FileCompilationUnit(
    string FilePath,
    string? Namespace,
    Program Ast,
    IReadOnlyList<ImportDeclaration> Imports,
    IReadOnlyList<CompileWarning> ParseWarnings);

/// <summary>
/// Result of a complete multi-file compilation.
/// </summary>
/// <param name="Units">All successfully compiled file units.</param>
/// <param name="Errors">All errors encountered during compilation.</param>
/// <param name="Warnings">All warnings encountered during compilation.</param>
/// <param name="InitializationOrder">Modules in safe initialization order.</param>
public sealed record CompilationResult(
    IReadOnlyList<FileCompilationUnit> Units,
    IReadOnlyList<SemanticError> Errors,
    IReadOnlyList<CompileWarning> Warnings,
    IReadOnlyList<string> InitializationOrder);

/// <summary>
/// Coordinates multi-file compilation with circular import detection.
/// This is the entry point for compiling RazorForge/Suflae projects.
/// </summary>
public sealed class CompilationDriver
{
    private readonly ModuleDependencyGraph _dependencyGraph = new();
    private readonly ModuleResolver _resolver;
    private readonly Language _language;

    private readonly List<SemanticError> _errors = [];
    private readonly List<CompileWarning> _warnings = [];
    private readonly Dictionary<string, FileCompilationUnit> _compiledUnits = [];
    private readonly HashSet<string> _processingFiles = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="CompilationDriver"/> class.
    /// </summary>
    /// <param name="projectRoot">The root directory of the project.</param>
    /// <param name="stdlibRoot">The root directory of the standard library.</param>
    /// <param name="language">The language being compiled.</param>
    public CompilationDriver(string projectRoot, string stdlibRoot, Language language)
    {
        _resolver = new ModuleResolver(projectRoot: projectRoot, stdlibRoot: stdlibRoot);
        _language = language;
    }

    /// <summary>
    /// Compiles a single source file and all its dependencies.
    /// </summary>
    /// <param name="entryFile">The main source file to compile.</param>
    /// <returns>The compilation result with all units and errors.</returns>
    public CompilationResult CompileFile(string entryFile)
    {
        return CompileFiles(sourceFiles: [entryFile]);
    }

    /// <summary>
    /// Compiles multiple source files and all their dependencies.
    /// </summary>
    /// <param name="sourceFiles">The source files to compile.</param>
    /// <returns>The compilation result with all units and errors.</returns>
    public CompilationResult CompileFiles(IReadOnlyList<string> sourceFiles)
    {
        // Process each entry file
        foreach (string sourceFile in sourceFiles)
        {
            if (!File.Exists(path: sourceFile))
            {
                _errors.Add(item: new SemanticError(
                    Message: $"Source file not found: '{sourceFile}'",
                    Location: new SourceLocation(FileName: sourceFile, Line: 0, Column: 0, Position: 0)));
                continue;
            }

            ProcessFile(filePath: sourceFile, fromFile: null, importLocation: null);
        }

        // Collect errors from resolver and dependency graph
        _errors.AddRange(collection: _resolver.Errors);
        _errors.AddRange(collection: _dependencyGraph.Errors);

        // Get initialization order (if no cycles)
        IReadOnlyList<string> initOrder = [];
        if (_dependencyGraph.Errors.Count == 0)
        {
            try
            {
                initOrder = _dependencyGraph.GetInitializationOrder();
            }
            catch (InvalidOperationException ex)
            {
                _errors.Add(item: new SemanticError(
                    Message: ex.Message,
                    Location: new SourceLocation(FileName: "", Line: 0, Column: 0, Position: 0)));
            }
        }

        return new CompilationResult(
            Units: _compiledUnits.Values.ToList(),
            Errors: _errors,
            Warnings: _warnings,
            InitializationOrder: initOrder);
    }

    /// <summary>
    /// Processes a single file: parses it, extracts imports, and recursively processes dependencies.
    /// </summary>
    private void ProcessFile(string filePath, string? fromFile, SourceLocation? importLocation)
    {
        // Normalize the path
        filePath = Path.GetFullPath(path: filePath);

        // Skip if already compiled
        if (_compiledUnits.ContainsKey(key: filePath))
        {
            return;
        }

        // Detect re-entrant processing (shouldn't happen with proper cycle detection)
        if (_processingFiles.Contains(item: filePath))
        {
            return;
        }

        _processingFiles.Add(item: filePath);

        try
        {
            // Parse the file
            FileCompilationUnit? unit = ParseFile(filePath: filePath);
            if (unit == null)
            {
                return;
            }

            // Register the module
            string modulePath = unit.Namespace ?? Path.GetFileNameWithoutExtension(path: filePath);
            _dependencyGraph.GetOrCreateModule(modulePath: modulePath, sourceFile: filePath);

            // Track dependencies from imports
            if (fromFile != null && importLocation != null)
            {
                string fromModule = GetModuleForFile(filePath: fromFile);

                bool success = _dependencyGraph.AddDependency(
                    fromModule: fromModule,
                    toModule: modulePath,
                    importLocation: importLocation);

                if (!success)
                {
                    // Circular dependency detected - don't continue processing
                    return;
                }
            }

            // Store the unit
            _compiledUnits[key: filePath] = unit;
            _warnings.AddRange(collection: unit.ParseWarnings);

            // Process imports recursively
            foreach (ImportDeclaration import in unit.Imports)
            {
                string? resolvedPath = _resolver.ResolveImport(
                    importPath: import.ModulePath,
                    currentFile: filePath,
                    location: import.Location);

                if (resolvedPath != null)
                {
                    ProcessFile(
                        filePath: resolvedPath,
                        fromFile: filePath,
                        importLocation: import.Location);
                }
            }
        }
        finally
        {
            _processingFiles.Remove(item: filePath);
        }
    }

    /// <summary>
    /// Parses a single source file.
    /// </summary>
    private FileCompilationUnit? ParseFile(string filePath)
    {
        try
        {
            string code = File.ReadAllText(path: filePath);
            bool isSuflae = filePath.EndsWith(value: ".sf", comparisonType: StringComparison.OrdinalIgnoreCase);

            // Validate language consistency
            if (isSuflae && _language == Language.RazorForge)
            {
                _errors.Add(item: new SemanticError(
                    Message: $"Cannot import Suflae file '{filePath}' from RazorForge project.",
                    Location: new SourceLocation(FileName: filePath, Line: 1, Column: 1, Position: 0)));
                return null;
            }

            if (!isSuflae && _language == Language.Suflae)
            {
                _errors.Add(item: new SemanticError(
                    Message: $"Cannot import RazorForge file '{filePath}' from Suflae project.",
                    Location: new SourceLocation(FileName: filePath, Line: 1, Column: 1, Position: 0)));
                return null;
            }

            // Tokenize
            List<Token> tokens;
            if (isSuflae)
            {
                var tokenizer = new SuflaeTokenizer(code);
                tokens = tokenizer.Tokenize();
            }
            else
            {
                var tokenizer = new RazorForgeTokenizer(code);
                tokens = tokenizer.Tokenize();
            }

            // Parse
            Program ast;
            IReadOnlyList<CompileWarning> warnings;
            if (isSuflae)
            {
                var parser = new SuflaeParser(tokens: tokens, fileName: filePath);
                ast = parser.Parse();
                warnings = parser.GetWarnings();
            }
            else
            {
                var parser = new RazorForgeParser(tokens: tokens, fileName: filePath);
                ast = parser.Parse();
                warnings = parser.GetWarnings();
            }

            // Extract namespace and imports
            string? namespacePath = null;
            var imports = new List<ImportDeclaration>();

            foreach (IAstNode decl in ast.Declarations)
            {
                if (decl is NamespaceDeclaration ns)
                {
                    namespacePath = ns.Path;
                }
                else if (decl is ImportDeclaration import)
                {
                    imports.Add(item: import);
                }
            }

            return new FileCompilationUnit(
                FilePath: filePath,
                Namespace: namespacePath,
                Ast: ast,
                Imports: imports,
                ParseWarnings: warnings);
        }
        catch (ParseException ex)
        {
            _errors.Add(item: new SemanticError(
                Message: $"Parse error in '{filePath}': {ex.Message}",
                Location: new SourceLocation(FileName: filePath, Line: 1, Column: 1, Position: 0)));
            return null;
        }
        catch (Exception ex)
        {
            _errors.Add(item: new SemanticError(
                Message: $"Error processing '{filePath}': {ex.Message}",
                Location: new SourceLocation(FileName: filePath, Line: 1, Column: 1, Position: 0)));
            return null;
        }
    }

    /// <summary>
    /// Gets the module path for a file.
    /// </summary>
    private string GetModuleForFile(string filePath)
    {
        filePath = Path.GetFullPath(path: filePath);

        if (_compiledUnits.TryGetValue(key: filePath, value: out FileCompilationUnit? unit))
        {
            return unit.Namespace ?? Path.GetFileNameWithoutExtension(path: filePath);
        }

        return Path.GetFileNameWithoutExtension(path: filePath);
    }

    /// <summary>
    /// Gets the dependency graph for inspection.
    /// </summary>
    public ModuleDependencyGraph DependencyGraph => _dependencyGraph;
}
