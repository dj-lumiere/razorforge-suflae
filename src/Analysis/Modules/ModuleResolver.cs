namespace Builder.Modules;

using SemanticAnalysis.Results;
using SyntaxTree;
using SemanticAnalysis.Diagnostics;

/// <summary>
/// Resolves import paths to actual source files.
/// Uses module-based resolution: import path matches the module declaration in the file.
/// </summary>
/// <remarks>
/// Import path resolution rules:
/// <list type="bullet">
/// <item>Module-based: import Core.Maybe → finds file declaring "module Core" with type "Maybe"</item>
/// <item>Relative imports: import ../Sibling/Module → relative to current file</item>
/// <item>Project imports: import MyModule/SubPath → project root</item>
/// </list>
/// </remarks>
public sealed class ModuleResolver
{
    /// <summary>The root directory of the project being built.</summary>
    public string ProjectRoot { get; }

    /// <summary>The standard library directory.</summary>
    public string StdlibRoot { get; }

    /// <summary>Errors collected during resolution.</summary>
    private readonly List<SemanticError> _errors = [];

    /// <summary>Gets all errors from module resolution.</summary>
    public IReadOnlyList<SemanticError> Errors => _errors;

    /// <summary>Cache of resolved module paths to source files.</summary>
    private readonly Dictionary<string, string?> _resolvedPaths = [];

    /// <summary>
    /// Module index: maps "Module.TypeName" to file path.
    /// Built by scanning stdlib files and reading their module declarations.
    /// </summary>
    private Dictionary<string, string>? _namespaceIndex;

    /// <summary>Cache of known stdlib directories (for fallback resolution).</summary>
    private HashSet<string>? _stdlibDirectories;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModuleResolver"/> class.
    /// </summary>
    /// <param name="projectRoot">The root directory of the project.</param>
    /// <param name="stdlibRoot">The root directory of the standard library.</param>
    public ModuleResolver(string projectRoot, string stdlibRoot)
    {
        ProjectRoot = projectRoot;
        StdlibRoot = stdlibRoot;
    }

    /// <summary>
    /// Resolves an import path to a source file.
    /// Uses module-based resolution: looks up "Module.Type" in the index.
    /// </summary>
    /// <param name="importPath">The import path (e.g., "Collections.List", "Core.Maybe").</param>
    /// <param name="currentFile">The file containing the import statement.</param>
    /// <param name="location">Source location for error reporting.</param>
    /// <returns>The resolved file path, or null if not found.</returns>
    public string? ResolveImport(string importPath, string currentFile, SourceLocation location)
    {
        // Check cache first
        if (_resolvedPaths.TryGetValue(key: importPath, value: out string? cached))
        {
            return cached;
        }

        string? resolved = null;

        // Handle relative imports
        if (importPath.StartsWith(value: "../") || importPath.StartsWith(value: "./"))
        {
            string normalizedPath = NormalizePath(importPath: importPath);
            resolved = ResolveRelativeImport(importPath: normalizedPath, currentFile: currentFile);
        }
        else
        {
            // Module-based resolution: look up in the index
            EnsureNamespaceIndexBuilt();

            if (_namespaceIndex != null &&
                _namespaceIndex.TryGetValue(key: importPath, value: out string? filePath))
            {
                resolved = filePath;
            }
            else
            {
                // Fallback: try file-path-based resolution for backwards compatibility
                string normalizedPath = NormalizePath(importPath: importPath);
                resolved = ResolveStdlibImport(importPath: normalizedPath) ??
                           ResolveProjectImport(importPath: normalizedPath);
            }
        }

        // Cache the result
        _resolvedPaths[key: importPath] = resolved;

        if (resolved == null)
        {
            _errors.Add(item: new SemanticError(Code: SemanticDiagnosticCode.ModuleNotFound,
                Message: $"Cannot resolve import '{importPath}'. Module not found.",
                Location: location));
        }

        return resolved;
    }

    /// <summary>
    /// Builds the module index by scanning all stdlib files.
    /// Maps "Module.TypeName" to file path based on module declarations in files.
    /// Scans both razorforge/ and suflae/ subdirectories.
    /// </summary>
    private void EnsureNamespaceIndexBuilt()
    {
        if (_namespaceIndex != null)
        {
            return;
        }

        _namespaceIndex =
            new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(path: StdlibRoot))
        {
            return;
        }

        // Scan all .rf files in Standard/RazorForge recursively
        string razorforgePath = Path.Combine(path1: StdlibRoot, path2: "RazorForge");
        if (Directory.Exists(path: razorforgePath))
        {
            foreach (string filePath in Directory.GetFiles(path: razorforgePath,
                         searchPattern: "*.rf",
                         searchOption: SearchOption.AllDirectories))
            {
                IndexFile(filePath: filePath, languagePrefix: "RazorForge");
            }
        }

        // Scan all .sf files in Standard/Suflae recursively
        string suflaePath = Path.Combine(path1: StdlibRoot, path2: "Suflae");
        if (Directory.Exists(path: suflaePath))
        {
            foreach (string filePath in Directory.GetFiles(path: suflaePath,
                         searchPattern: "*.sf",
                         searchOption: SearchOption.AllDirectories))
            {
                IndexFile(filePath: filePath, languagePrefix: "Suflae");
            }
        }
    }

    /// <summary>
    /// Indexes a single file by reading its module and type declarations.
    /// </summary>
    /// <param name="filePath">The file path to index.</param>
    /// <param name="languagePrefix">The language prefix (razorforge or suflae) for cross-language imports.</param>
    private void IndexFile(string filePath, string languagePrefix)
    {
        try
        {
            string content = File.ReadAllText(path: filePath);
            string? fileNamespace = ExtractModule(content: content);
            List<string> typeNames = ExtractTypeNames(content: content);

            // Use file name as type name if no type declarations found
            if (typeNames.Count == 0)
            {
                string fileName = Path.GetFileNameWithoutExtension(path: filePath);
                typeNames.Add(item: fileName);
            }

            // Default module based on directory structure
            if (fileNamespace == null)
            {
                fileNamespace =
                    DeriveModuleNameFromPath(filePath: filePath, languagePrefix: languagePrefix);
            }

            // Register each type under its module
            // Keys use "Module.TypeName" format to match parser output
            // e.g., "Errors.Common", "Collections.List"
            foreach (string typeName in typeNames)
            {
                string key = $"{fileNamespace}.{typeName}";
                // First registration wins (don't overwrite)
                _namespaceIndex!.TryAdd(key: key, value: filePath);

                // Also register with language prefix for cross-language imports
                // e.g., "RazorForge/Numeric.Integer"
                string crossLangKey = $"{languagePrefix}/{fileNamespace}.{typeName}";
                _namespaceIndex!.TryAdd(key: crossLangKey, value: filePath);
            }
        }
        catch
        {
            // Skip files that can't be read/parsed
        }
    }

    /// <summary>
    /// Extracts module declaration from file content.
    /// Looks for "module Foo.Bar" pattern.
    /// </summary>
    private static string? ExtractModule(string content)
    {
        // Simple regex-free parsing for "module X" or "module X.Y.Z"
        foreach (string line in content.Split(separator: '\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith(value: "module "))
            {
                string ns = trimmed["module ".Length..]
                   .Trim();
                // Remove any trailing comments or whitespace
                int commentIdx = ns.IndexOf(value: '#');
                if (commentIdx >= 0)
                {
                    ns = ns[..commentIdx]
                       .Trim();
                }

                return ns;
            }

            // Skip comments and empty lines, but stop at first non-comment declaration
            if (!string.IsNullOrWhiteSpace(value: trimmed) && !trimmed.StartsWith(value: "#") &&
                !trimmed.StartsWith(value: "import "))
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts type declaration names from file content.
    /// Looks for record, entity, choice, variant, mutant, protocol declarations.
    /// </summary>
    private static List<string> ExtractTypeNames(string content)
    {
        var types = new List<string>();
        string[] keywords =
        [
            "record ", "entity ", "choice ", "variant ", "mutant ", "protocol "
        ];

        foreach (string line in content.Split(separator: '\n'))
        {
            string trimmed = line.Trim();

            // Skip visibility modifiers
            if (trimmed.StartsWith(value: "public ") || trimmed.StartsWith(value: "private ") ||
                trimmed.StartsWith(value: "internal ") || trimmed.StartsWith(value: "protected "))
            {
                trimmed = trimmed[(trimmed.IndexOf(value: ' ') + 1)..]
                   .Trim();
            }

            foreach (string keyword in keywords)
            {
                if (!trimmed.StartsWith(value: keyword))
                {
                    continue;
                }

                string rest = trimmed[keyword.Length..]
                   .Trim();
                // Extract type name (before < or { or space)
                int endIdx = rest.IndexOfAny(anyOf: ['[', '{', ' ', '(']);
                string typeName = endIdx >= 0
                    ? rest[..endIdx]
                    : rest;
                if (!string.IsNullOrEmpty(value: typeName))
                {
                    types.Add(item: typeName);
                }

                break;
            }
        }

        return types;
    }

    /// <summary>
    /// Derives module name from file path relative to stdlib root.
    /// </summary>
    /// <param name="filePath">The file path to derive module name from.</param>
    /// <param name="languagePrefix">The language prefix to skip (razorforge or suflae).</param>
    private string DeriveModuleNameFromPath(string filePath, string languagePrefix)
    {
        try
        {
            string? fileDir = Path.GetDirectoryName(path: filePath);
            if (fileDir == null)
            {
                return "Core";
            }

            string normalizedFileDir = Path.GetFullPath(path: fileDir);
            string languageStdlibPath =
                Path.GetFullPath(path: Path.Combine(path1: StdlibRoot, path2: languagePrefix));

            if (!normalizedFileDir.StartsWith(value: languageStdlibPath,
                    comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                return "Core";
            }

            string relativePath = normalizedFileDir[languageStdlibPath.Length..]
               .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.IsNullOrEmpty(value: relativePath))
            {
                return "Core";
            }

            return relativePath.Replace(oldChar: Path.DirectorySeparatorChar, newChar: '.')
                               .Replace(oldChar: Path.AltDirectorySeparatorChar, newChar: '.');
        }
        catch
        {
            return "Core";
        }
    }

    /// <summary>
    /// Normalizes an import path by converting module separators and removing file extensions.
    /// Handles both dot notation (Collections.List) and slash notation (Collections/List).
    /// </summary>
    private static string NormalizePath(string importPath)
    {
        // Convert dots to path separators (module-based imports)
        // e.g., "Collections.List" -> "Collections/List"
        string normalized = importPath.Replace(oldChar: '.', newChar: Path.DirectorySeparatorChar);

        // Also convert forward slashes to OS-specific separators (for backward compatibility)
        normalized = normalized.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar);

        // Remove file extensions if present
        if (normalized.EndsWith(value: $"{Path.DirectorySeparatorChar}rf") ||
            normalized.EndsWith(value: $"{Path.DirectorySeparatorChar}sf"))
        {
            // This was a file extension that got converted, restore it
            // e.g., "Module.rf" -> "Module/rf" -> should be "Module"
            normalized = normalized[..^(Path.DirectorySeparatorChar.ToString()
                                            .Length + 2)];
        }

        return normalized;
    }

    /// <summary>
    /// Checks if an import path refers to the standard library.
    /// Dynamically discovers stdlib directories on first access.
    /// </summary>
    private bool IsStdlibImport(string importPath)
    {
        // Lazily discover stdlib directories
        if (_stdlibDirectories == null)
        {
            _stdlibDirectories = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(path: StdlibRoot))
            {
                foreach (string dir in Directory.GetDirectories(path: StdlibRoot))
                {
                    _stdlibDirectories.Add(item: Path.GetFileName(path: dir));
                }

                // Also add stdlib root-level .rf files as potential module sources
                foreach (string file in
                         Directory.GetFiles(path: StdlibRoot, searchPattern: "*.rf"))
                {
                    _stdlibDirectories.Add(item: Path.GetFileNameWithoutExtension(path: file));
                }
            }
        }

        string firstSegment = importPath.Split(separator: Path.DirectorySeparatorChar)[0];
        return _stdlibDirectories.Contains(item: firstSegment);
    }

    /// <summary>
    /// Resolves a relative import path.
    /// </summary>
    private string? ResolveRelativeImport(string importPath, string currentFile)
    {
        string? currentDir = Path.GetDirectoryName(path: currentFile);
        if (currentDir == null)
        {
            return null;
        }

        string fullPath =
            Path.GetFullPath(path: Path.Combine(path1: currentDir, path2: importPath));

        return TryFindSourceFile(basePath: fullPath);
    }

    /// <summary>
    /// Resolves a standard library import.
    /// Handles cross-language imports like "razorforge/Numeric/Integer".
    /// </summary>
    private string? ResolveStdlibImport(string importPath)
    {
        // First try direct path (may be a cross-language import like "razorforge/Numeric/Integer")
        string fullPath = Path.Combine(path1: StdlibRoot, path2: importPath);
        string? result = TryFindSourceFile(basePath: fullPath);
        if (result != null)
        {
            return result;
        }

        // Try in RazorForge subdirectory
        string razorforgePath =
            Path.Combine(path1: StdlibRoot, path2: "RazorForge", path3: importPath);
        result = TryFindSourceFile(basePath: razorforgePath);
        if (result != null)
        {
            return result;
        }

        // Try in Suflae subdirectory
        string suflaePath = Path.Combine(path1: StdlibRoot, path2: "Suflae", path3: importPath);
        return TryFindSourceFile(basePath: suflaePath);
    }

    /// <summary>
    /// Resolves a project-level import.
    /// </summary>
    private string? ResolveProjectImport(string importPath)
    {
        string fullPath = Path.Combine(path1: ProjectRoot, path2: importPath);
        return TryFindSourceFile(basePath: fullPath);
    }

    /// <summary>
    /// Tries to find a source file at the given base path.
    /// Checks for .rf and .sf extensions, and directory/index patterns.
    /// </summary>
    private static string? TryFindSourceFile(string basePath)
    {
        // Try exact file with extensions
        string rfPath = basePath + ".rf";
        if (File.Exists(path: rfPath))
        {
            return rfPath;
        }

        string sfPath = basePath + ".sf";
        if (File.Exists(path: sfPath))
        {
            return sfPath;
        }

        // Try as directory with index file
        if (Directory.Exists(path: basePath))
        {
            string indexRf = Path.Combine(path1: basePath, path2: "index.rf");
            if (File.Exists(path: indexRf))
            {
                return indexRf;
            }

            string indexSf = Path.Combine(path1: basePath, path2: "index.sf");
            if (File.Exists(path: indexSf))
            {
                return indexSf;
            }
        }

        // Try with the module name as the filename (common pattern)
        // e.g., import Collections/List -> Collections/List.rf or Collections/List/List.rf
        string moduleName = Path.GetFileName(path: basePath);
        string? parentDir = Path.GetDirectoryName(path: basePath);
        if (parentDir != null && Directory.Exists(path: basePath))
        {
            string moduleFile = Path.Combine(path1: basePath, path2: moduleName + ".rf");
            if (File.Exists(path: moduleFile))
            {
                return moduleFile;
            }

            moduleFile = Path.Combine(path1: basePath, path2: moduleName + ".sf");
            if (File.Exists(path: moduleFile))
            {
                return moduleFile;
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the module path from an import path.
    /// The module path is the import path converted to dot notation.
    /// </summary>
    /// <param name="importPath">The import path (e.g., "Collections/List").</param>
    /// <returns>The module path (e.g., "Collections.List").</returns>
    public static string GetNamespaceFromImport(string importPath)
    {
        return importPath.Replace(oldChar: '/', newChar: '.')
                         .Replace(oldChar: Path.DirectorySeparatorChar, newChar: '.');
    }

    /// <summary>
    /// Clears the resolution cache. Used when files change.
    /// </summary>
    public void ClearCache()
    {
        _resolvedPaths.Clear();
        _errors.Clear();
    }
}
