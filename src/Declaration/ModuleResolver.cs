namespace Compiler.Declaration;

using SemanticVerification.Results;
using SyntaxTree;
using Diagnostics;

/// <summary>
/// Resolves import paths to source files using a pre-built index from parsed ASTs.
/// </summary>
public sealed class ModuleResolver
{
    private readonly string _projectRoot;
    private readonly string _stdlibRoot;

    private readonly List<SemanticError> _errors = [];

    /// <summary>Gets all errors from module resolution.</summary>
    public IReadOnlyList<SemanticError> Errors => _errors;

    /// <summary>
    /// Index mapping "ModuleName" or "ModuleName.SymbolName" to the source file that declares it.
    /// Keys use '/' for module hierarchy and '.' for module-symbol separation, matching the parser's
    /// ImportDeclaration.ModulePath format.
    /// </summary>
    private readonly Dictionary<string, string> _index =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <param name="projectRoot">Project root directory, used as a filesystem fallback for project files.</param>
    /// <param name="stdlibRoot">Standard library root, used as a filesystem fallback when AST pre-registration fails.</param>
    public ModuleResolver(string projectRoot, string stdlibRoot)
    {
        _projectRoot = projectRoot;
        _stdlibRoot = stdlibRoot;
    }

    /// <summary>
    /// Registers a parsed file into the import index.
    /// Called by <see cref="BuildDriver"/> after parsing each file (stdlib pre-scan + project files).
    /// </summary>
    /// <param name="filePath">Absolute path to the source file.</param>
    /// <param name="moduleName">The module name — either from a ModuleDeclaration or derived from the file path.</param>
    /// <param name="ast">The parsed program AST to extract exported symbol names from.</param>
    public void RegisterFile(string filePath, string moduleName, Program ast)
    {
        // Register the module itself for bare imports: `import Module`
        _index.TryAdd(key: moduleName, value: filePath);

        foreach (IAstNode node in ast.Declarations)
        {
            switch (node)
            {
                case RecordDeclaration { Name: var n }:
                    _index.TryAdd(key: $"{moduleName}.{n}", value: filePath);
                    break;
                case EntityDeclaration { Name: var n }:
                    _index.TryAdd(key: $"{moduleName}.{n}", value: filePath);
                    break;
                case ChoiceDeclaration { Name: var n }:
                    _index.TryAdd(key: $"{moduleName}.{n}", value: filePath);
                    break;
                case FlagsDeclaration { Name: var n }:
                    _index.TryAdd(key: $"{moduleName}.{n}", value: filePath);
                    break;
                case VariantDeclaration { Name: var n }:
                    _index.TryAdd(key: $"{moduleName}.{n}", value: filePath);
                    break;
                case CrashableDeclaration { Name: var n }:
                    _index.TryAdd(key: $"{moduleName}.{n}", value: filePath);
                    break;
                case ProtocolDeclaration { Name: var n }:
                    _index.TryAdd(key: $"{moduleName}.{n}", value: filePath);
                    break;
                case DefineDeclaration { OldName: var original, NewName: var alias }:
                    _index.TryAdd(key: $"{moduleName}.{original}", value: filePath);
                    _index.TryAdd(key: $"{moduleName}.{alias}", value: filePath);
                    break;
                case PresetDeclaration { Name: var n }:
                    _index.TryAdd(key: $"{moduleName}.{n}", value: filePath);
                    break;
                case RoutineDeclaration { Name: var n } when !n.Contains(value: '.'):
                    // Module-level routines only; member routines have "Type.name" as their Name
                    _index.TryAdd(key: $"{moduleName}.{n}", value: filePath);
                    break;
            }
        }
    }

    /// <summary>
    /// Resolves an import path to a source file path.
    /// Checks the pre-built AST index first; falls back to filesystem probing for project files.
    /// </summary>
    /// <param name="importPath">
    /// The import path as parsed — '/' for module hierarchy, '.' for symbol access.
    /// Examples: "Collections.List", "IO/Console.show", "Collections", "IO/Console".
    /// </param>
    /// <param name="currentFile">The file containing the import statement (unused, kept for API stability).</param>
    /// <param name="location">Source location for error reporting.</param>
    public string? ResolveImport(string importPath, string currentFile, SourceLocation location)
    {
        if (_index.TryGetValue(key: importPath, value: out string? resolved))
        {
            return resolved;
        }

        resolved = TryFilesystemFallback(importPath: importPath);

        if (resolved is null)
        {
            _errors.Add(item: new SemanticError(
                Code: SemanticDiagnosticCode.ModuleNotFound,
                Message: $"Cannot resolve import '{importPath}'. Module not found.",
                Location: location));
        }

        return resolved;
    }

    /// <summary>
    /// Filesystem fallback for imports not yet registered in the AST index.
    /// Covers both project files (discovered organically) and stdlib files whose AST parsing failed
    /// silently during pre-registration. Only converts '/' (module hierarchy separator) to OS path
    /// separators; the '.' module-symbol separator is stripped before constructing file paths.
    /// </summary>
    private string? TryFilesystemFallback(string importPath)
    {
        // Split on the last '.' to separate module path from symbol name.
        // "Collections.List"   -> module="Collections",  symbol="List"
        // "IO/Console.show"    -> module="IO/Console",   symbol="show"
        // "Collections"        -> module="Collections",  symbol=null
        int lastDot = importPath.LastIndexOf(value: '.');
        string modulePart = lastDot >= 0 ? importPath[..lastDot] : importPath;
        string? symbolPart = lastDot >= 0 ? importPath[(lastDot + 1)..] : null;

        // Only '/' is a hierarchy separator; convert to OS path separator.
        string relPath = modulePart.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar);

        // Search roots in priority order: project, then stdlib RazorForge, then stdlib Suflae.
        string[] roots =
        [
            _projectRoot,
            Path.Combine(path1: _stdlibRoot, path2: "RazorForge"),
            Path.Combine(path1: _stdlibRoot, path2: "Suflae"),
        ];

        foreach (string root in roots)
        {
            // Try: root/module.rf  or  .sf
            string modRf = Path.Combine(path1: root, path2: relPath + ".rf");
            if (File.Exists(path: modRf)) return modRf;

            string modSf = Path.Combine(path1: root, path2: relPath + ".sf");
            if (File.Exists(path: modSf)) return modSf;

            // Try: root/module/symbol.rf  (type-per-file convention)
            if (symbolPart is not null)
            {
                string symRf = Path.Combine(path1: root, path2: relPath,
                    path3: symbolPart + ".rf");
                if (File.Exists(path: symRf)) return symRf;

                string symSf = Path.Combine(path1: root, path2: relPath,
                    path3: symbolPart + ".sf");
                if (File.Exists(path: symSf)) return symSf;
            }

            // Try: root/module/index.rf
            string idxRf = Path.Combine(path1: root, path2: relPath, path3: "index.rf");
            if (File.Exists(path: idxRf)) return idxRf;

            // Try: root/module/module.rf (same-name-as-directory convention, e.g., BuilderService/BuilderService.rf)
            string dirName = Path.GetFileName(path: relPath);
            if (!string.IsNullOrEmpty(value: dirName))
            {
                string sameNameRf = Path.Combine(path1: root, path2: relPath,
                    path3: dirName + ".rf");
                if (File.Exists(path: sameNameRf)) return sameNameRf;

                string sameNameSf = Path.Combine(path1: root, path2: relPath,
                    path3: dirName + ".sf");
                if (File.Exists(path: sameNameSf)) return sameNameSf;
            }
        }

        return null;
    }
}