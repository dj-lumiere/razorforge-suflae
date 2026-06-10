using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Compiler.Diagnostics;
using Compiler.Tokenizer;
using Compiler.Parser;
using Verification.Results;
using SyntaxTree;
using TypeModel.Enums;

namespace Compiler.Declaration;

/// <summary>
/// Result of building a single source file.
/// </summary>
/// <param name="FilePath">The path to the source file.</param>
/// <param name="Module">The module declared in the file (module path).</param>
/// <param name="Ast">The parsed AST.</param>
/// <param name="Imports">Import declarations found in the file.</param>
/// <param name="ParseWarnings">Warnings from parsing.</param>
public sealed record FileBuildUnit(
    string FilePath,
    string? Module,
    Program Ast,
    List<ImportDeclaration> Imports,
    List<BuildWarning> ParseWarnings);

/// <summary>
/// Result of a complete multi-file build.
/// </summary>
/// <param name="Units">All successfully built file units.</param>
/// <param name="Errors">All errors encountered during building.</param>
/// <param name="Warnings">All warnings encountered during building.</param>
/// <param name="InitializationOrder">Modules in safe initialization order.</param>
public sealed record BuildResult(
    List<FileBuildUnit> Units,
    List<SemanticError> Errors,
    List<BuildWarning> Warnings,
    List<string> InitializationOrder);

/// <summary>
/// Coordinates multi-file building with circular import detection.
/// This is the entry point for building RazorForge/Suflae projects.
/// </summary>
public sealed class BuildDriver
{
    private readonly ModuleDependencyGraph _dependencyGraph = new();
    private readonly ModuleResolver _resolver;
    private readonly Language _language;
    private readonly string _stdlibRoot;

    private readonly List<SemanticError> _errors = [];
    private readonly List<BuildWarning> _warnings = [];
    private readonly Dictionary<string, FileBuildUnit> _compiledUnits = [];
    private readonly HashSet<string> _processingFiles = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="BuildDriver"/> class.
    /// </summary>
    /// <param name="projectRoot">The root directory of the project.</param>
    /// <param name="stdlibRoot">The root directory of the standard library.</param>
    /// <param name="language">The language being built.</param>
    public BuildDriver(string projectRoot, string stdlibRoot, Language language)
    {
        _resolver = new ModuleResolver(projectRoot: projectRoot, stdlibRoot: stdlibRoot);
        _stdlibRoot = stdlibRoot;
        _language = language;
    }

    /// <summary>
    /// Builds a single source file and all its dependencies.
    /// </summary>
    /// <param name="entryFile">The main source file to build.</param>
    /// <returns>The build result with all units and errors.</returns>
    public BuildResult CompileFile(string entryFile)
    {
        return CompileFiles(sourceFiles: [entryFile]);
    }

    /// <summary>
    /// Builds multiple source files and all their dependencies.
    /// </summary>
    /// <param name="sourceFiles">The source files to build.</param>
    /// <returns>The build result with all units and errors.</returns>
    public BuildResult CompileFiles(List<string> sourceFiles)
    {
        // Pre-register all stdlib files so imports resolve without filesystem probing.
        PreRegisterStdlib();

        // Process each entry file
        foreach (string sourceFile in sourceFiles)
        {
            if (!File.Exists(path: sourceFile))
            {
                _errors.Add(item: new SemanticError(
                    Code: SemanticDiagnosticCode.SourceFileNotFound,
                    Message: $"Source file not found: '{sourceFile}'",
                    Location: new SourceLocation(FileName: sourceFile,
                        Line: 0,
                        Column: 0,
                        Position: 0)));
                continue;
            }

            ProcessFile(filePath: sourceFile, fromFile: null, importLocation: null, importPathString: null);
        }

        // Collect errors from resolver and dependency graph
        _errors.AddRange(collection: _resolver.Errors);
        _errors.AddRange(collection: _dependencyGraph.Errors);

        // Get initialization order (if no cycles)
        List<string> initOrder = [];
        if (_dependencyGraph.Errors.Count == 0)
        {
            try
            {
                initOrder = _dependencyGraph.GetInitializationOrder();
            }
            catch (InvalidOperationException ex)
            {
                _errors.Add(item: new SemanticError(Code: SemanticDiagnosticCode.CircularImport,
                    Message: ex.Message,
                    Location: new SourceLocation(FileName: "",
                        Line: 0,
                        Column: 0,
                        Position: 0)));
            }
        }

        return new BuildResult(Units: _compiledUnits.Values.ToList(),
            Errors: _errors,
            Warnings: _warnings,
            InitializationOrder: initOrder);
    }

    /// <summary>
    /// Processes a single file: parses it, extracts imports, and recursively processes dependencies.
    /// </summary>
    private void ProcessFile(string filePath, string? fromFile, SourceLocation? importLocation, string? importPathString)
    {
        // Normalize the path
        filePath = Path.GetFullPath(path: filePath);

        // Skip if already built
        if (_compiledUnits.ContainsKey(key: filePath))
        {
            // Even if already built, validate the `/`-form import path against the file's actual module.
            if (fromFile != null && importPathString != null && importLocation != null
                && _compiledUnits.TryGetValue(key: filePath, value: out FileBuildUnit? existing))
            {
                ValidateSlashFormImport(
                    importPathString: importPathString,
                    actualModule: existing.Module ?? Path.GetFileNameWithoutExtension(path: filePath),
                    importLocation: importLocation);
            }
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
            FileBuildUnit? unit = ParseFile(filePath: filePath);
            if (unit == null)
            {
                return;
            }

            // Register the module
            string modulePath = unit.Module ?? Path.GetFileNameWithoutExtension(path: filePath);
            _dependencyGraph.GetOrCreateModule(modulePath: modulePath, sourceFile: filePath);

            // Validate that `/`-form imports refer to an actual module of that path.
            // `import Collections/BitList` is an error if the file declares `module Collections`
            // (BitList is a type, not a submodule).
            if (importPathString != null && importLocation != null)
            {
                ValidateSlashFormImport(
                    importPathString: importPathString,
                    actualModule: modulePath,
                    importLocation: importLocation);
            }

            // Track dependencies from imports.
            // Skip self-dependencies for member imports (`.` form) and Core auto-imports — they're no-ops.
            if (fromFile != null && importLocation != null)
            {
                string fromModule = GetModuleForFile(filePath: fromFile);

                bool isMemberImport = importPathString != null && importPathString.Contains(value: '.');
                bool isSelfMemberImport = isMemberImport && fromModule == modulePath;
                bool isCoreAutoImport = modulePath == "Core";

                if (!isSelfMemberImport && !isCoreAutoImport)
                {
                    bool success = _dependencyGraph.AddDependency(fromModule: fromModule,
                        toModule: modulePath,
                        importLocation: importLocation);

                    if (!success)
                    {
                        // Circular dependency detected - don't continue processing
                        return;
                    }
                }
            }

            // Store the unit
            _compiledUnits[key: filePath] = unit;
            _warnings.AddRange(collection: unit.ParseWarnings);

            // Register into the resolver index so later imports of this file resolve correctly.
            _resolver.RegisterFile(filePath: filePath, moduleName: modulePath, ast: unit.Ast);

            // Process imports recursively
            foreach (ImportDeclaration import in unit.Imports)
            {
                string? resolvedPath = _resolver.ResolveImport(importPath: import.ModulePath,
                    currentFile: filePath,
                    location: import.Location);

                if (resolvedPath != null)
                {
                    ProcessFile(filePath: resolvedPath,
                        fromFile: filePath,
                        importLocation: import.Location,
                        importPathString: import.ModulePath);
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
    private FileBuildUnit? ParseFile(string filePath)
    {
        try
        {
            string code = File.ReadAllText(path: filePath);
            bool isSuflae = filePath.EndsWith(value: ".sf",
                comparisonType: StringComparison.OrdinalIgnoreCase);

            // Validate language consistency
            if (isSuflae && _language == Language.RazorForge)
            {
                _errors.Add(item: new SemanticError(Code: SemanticDiagnosticCode.LanguageMismatch,
                    Message: $"Cannot import Suflae file '{filePath}' from RazorForge project.",
                    Location: new SourceLocation(FileName: filePath,
                        Line: 1,
                        Column: 1,
                        Position: 0)));
                return null;
            }

            if (!isSuflae && _language == Language.Suflae)
            {
                _errors.Add(item: new SemanticError(Code: SemanticDiagnosticCode.LanguageMismatch,
                    Message: $"Cannot import RazorForge file '{filePath}' from Suflae project.",
                    Location: new SourceLocation(FileName: filePath,
                        Line: 1,
                        Column: 1,
                        Position: 0)));
                return null;
            }

            // Tokenize
            Language language = isSuflae
                ? Language.Suflae
                : Language.RazorForge;
            var tokenizer = new Tokenizer.Tokenizer(source: code, fileName: filePath, language: language);
            List<Token> tokens = tokenizer.Tokenize();

            // Parse
            var parser = new Parser.Parser(tokens: tokens, language: language, fileName: filePath);
            Program ast = parser.Parse();
            List<BuildWarning> warnings = parser.GetWarnings();

            // Extract module and imports
            string? modulePath = null;
            var imports = new List<ImportDeclaration>();

            foreach (ISyntaxTreeNode decl in ast.Declarations)
            {
                if (decl is ModuleDeclaration ns)
                {
                    modulePath = ns.Path;
                }
                else if (decl is ImportDeclaration import)
                {
                    imports.Add(item: import);
                }
            }

            return new FileBuildUnit(FilePath: filePath,
                Module: modulePath,
                Ast: ast,
                Imports: imports,
                ParseWarnings: warnings);
        }
        catch (GrammarException ex)
        {
            // Preserve the REAL error position (not 1:1) so the caret renderer points at the
            // offending token, and embed only the grammar code + raw text — the SemanticError
            // envelope supplies its own `error[…]: file:line:col:` prefix.
            _errors.Add(item: new SemanticError(Code: SemanticDiagnosticCode.ParseError,
                Message: $"[{ex.Code.ToCodeString(language: ex.Language)}] {ex.RawMessage}",
                Location: new SourceLocation(FileName: ex.FileName,
                    Line: ex.Line > 0 ? ex.Line : 1,
                    Column: ex.Column > 0 ? ex.Column : 1,
                    Position: 0)));
            return null;
        }
        catch (Exception ex)
        {
            _errors.Add(item: new SemanticError(Code: SemanticDiagnosticCode.CompilationError,
                Message: $"Error processing '{filePath}': {ex.Message}",
                Location: new SourceLocation(FileName: filePath,
                    Line: 1,
                    Column: 1,
                    Position: 0)));
            return null;
        }
    }

    /// <summary>
    /// Validates a `/`-form import path against the actual module declared by the resolved file.
    /// `import Foo/Bar` requires the file to declare `module Foo/Bar`. If the file declares a
    /// different module (e.g. `module Foo` because `Bar` is a type in that module), emit an error —
    /// the caller should have used `import Foo.Bar` instead.
    /// Member-form imports (paths containing `.`) skip this check.
    /// </summary>
    private void ValidateSlashFormImport(string importPathString, string actualModule, SourceLocation importLocation)
    {
        // Member-form imports are not subject to module-path equality.
        if (importPathString.Contains(value: '.'))
        {
            return;
        }

        if (importPathString == actualModule)
        {
            return;
        }

        _errors.Add(item: new SemanticError(
            Code: SemanticDiagnosticCode.ModuleNotFound,
            Message: $"No module '{importPathString}'. The file resolved to module '{actualModule}'. " +
                     $"Use 'import {actualModule}.{importPathString[(importPathString.LastIndexOf(value: '/') + 1)..]}' if you meant to import a member.",
            Location: importLocation));
    }

    /// <summary>
    /// Gets the module path for a file.
    /// </summary>
    private string GetModuleForFile(string filePath)
    {
        filePath = Path.GetFullPath(path: filePath);

        if (_compiledUnits.TryGetValue(key: filePath, value: out FileBuildUnit? unit))
        {
            return unit.Module ?? Path.GetFileNameWithoutExtension(path: filePath);
        }

        return Path.GetFileNameWithoutExtension(path: filePath);
    }

    /// <summary>
    /// Gets the dependency graph for inspection.
    /// </summary>
    public ModuleDependencyGraph DependencyGraph => _dependencyGraph;

    /// <summary>
    /// Parses all stdlib files and registers them in the resolver index.
    /// This replaces the old text-scanning approach with correct AST-based extraction.
    /// </summary>
    private void PreRegisterStdlib()
    {
        RegisterStdlibDirectory(subdirectory: "RazorForge", extension: "*.rf");
        RegisterStdlibDirectory(subdirectory: "Suflae", extension: "*.sf");
    }

    private void RegisterStdlibDirectory(string subdirectory, string extension)
    {
        string dirPath = Path.Combine(path1: _stdlibRoot, path2: subdirectory);
        if (!Directory.Exists(path: dirPath))
        {
            return;
        }

        foreach (string filePath in Directory.GetFiles(path: dirPath,
                     searchPattern: extension,
                     searchOption: SearchOption.AllDirectories))
        {
            Program? ast = ParseAstOnly(filePath: filePath);
            if (ast is null)
            {
                continue;
            }

            string? moduleName = null;
            foreach (ISyntaxTreeNode node in ast.Declarations)
            {
                if (node is ModuleDeclaration md)
                {
                    moduleName = md.Path;
                    break;
                }
            }

            moduleName ??= DeriveModuleNameFromPath(filePath: filePath, languageSubdir: subdirectory);
            _resolver.RegisterFile(filePath: filePath, moduleName: moduleName, ast: ast);
        }
    }

    /// <summary>
    /// Parses a file to an AST without language-consistency validation or error accumulation.
    /// Used only for index pre-registration; actual compilation errors surface via <see cref="ParseFile"/>.
    /// </summary>
    private static Program? ParseAstOnly(string filePath)
    {
        try
        {
            string code = File.ReadAllText(path: filePath);
            bool isSuflae = filePath.EndsWith(value: ".sf",
                comparisonType: StringComparison.OrdinalIgnoreCase);
            Language language = isSuflae ? Language.Suflae : Language.RazorForge;
            var tokenizer = new Tokenizer.Tokenizer(source: code, fileName: filePath, language: language);
            List<Token> tokens = tokenizer.Tokenize();
            var parser = new Parser.Parser(tokens: tokens, language: language, fileName: filePath);
            return parser.Parse();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Derives a module name from a file path for stdlib files that lack an explicit module declaration.
    /// Uses the directory path relative to the language subdirectory, with '/' as hierarchy separator.
    /// Files directly in the language root use the filename as their module name.
    /// </summary>
    private string DeriveModuleNameFromPath(string filePath, string languageSubdir)
    {
        try
        {
            string languagePath = Path.GetFullPath(path: Path.Combine(path1: _stdlibRoot,
                path2: languageSubdir));
            string fileDir = Path.GetFullPath(path: Path.GetDirectoryName(path: filePath) ?? "");

            if (!fileDir.StartsWith(value: languagePath,
                    comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFileNameWithoutExtension(path: filePath);
            }

            string relativeDir = fileDir[languagePath.Length..]
               .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.IsNullOrEmpty(value: relativeDir)
                ? Path.GetFileNameWithoutExtension(path: filePath)
                : relativeDir.Replace(oldChar: Path.DirectorySeparatorChar, newChar: '/')
                             .Replace(oldChar: Path.AltDirectorySeparatorChar, newChar: '/');
        }
        catch
        {
            return Path.GetFileNameWithoutExtension(path: filePath);
        }
    }
}
