namespace Compilers.Analysis.Modules;

using Compilers.Analysis.Results;
using Compilers.Shared.AST;

/// <summary>
/// Resolves import paths to actual source files.
/// Uses namespace-based resolution: import path matches the namespace declaration in the file.
/// </summary>
/// <remarks>
/// Import path resolution rules:
/// <list type="bullet">
/// <item>Namespace-based: import Core.Maybe → finds file declaring "namespace Core" with type "Maybe"</item>
/// <item>Relative imports: import ../Sibling/Module → relative to current file</item>
/// <item>Project imports: import MyModule/SubPath → project root</item>
/// </list>
/// </remarks>
public sealed class ModuleResolver
{
    /// <summary>The root directory of the project being compiled.</summary>
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
    /// Namespace index: maps "Namespace.TypeName" to file path.
    /// Built by scanning stdlib files and reading their namespace declarations.
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
    /// Uses namespace-based resolution: looks up "Namespace.Type" in the index.
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
            resolved = ResolveRelativeImport(
                importPath: normalizedPath,
                currentFile: currentFile);
        }
        else
        {
            // Namespace-based resolution: look up in the index
            EnsureNamespaceIndexBuilt();

            if (_namespaceIndex != null && _namespaceIndex.TryGetValue(importPath, out string? filePath))
            {
                resolved = filePath;
            }
            else
            {
                // Fallback: try file-path-based resolution for backwards compatibility
                string normalizedPath = NormalizePath(importPath: importPath);
                resolved = ResolveStdlibImport(importPath: normalizedPath)
                        ?? ResolveProjectImport(importPath: normalizedPath);
            }
        }

        // Cache the result
        _resolvedPaths[key: importPath] = resolved;

        if (resolved == null)
        {
            _errors.Add(item: new SemanticError(
                Message: $"Cannot resolve import '{importPath}'. Module not found.",
                Location: location));
        }

        return resolved;
    }

    /// <summary>
    /// Builds the namespace index by scanning all stdlib files.
    /// Maps "Namespace.TypeName" to file path based on namespace declarations in files.
    /// </summary>
    private void EnsureNamespaceIndexBuilt()
    {
        if (_namespaceIndex != null)
        {
            return;
        }

        _namespaceIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(StdlibRoot))
        {
            return;
        }

        // Scan all .rf files in stdlib recursively
        foreach (string filePath in Directory.GetFiles(StdlibRoot, "*.rf", SearchOption.AllDirectories))
        {
            IndexFile(filePath);
        }
    }

    /// <summary>
    /// Indexes a single file by reading its namespace and type declarations.
    /// </summary>
    private void IndexFile(string filePath)
    {
        try
        {
            string content = File.ReadAllText(filePath);
            string? fileNamespace = ExtractNamespace(content);
            List<string> typeNames = ExtractTypeNames(content);

            // Use file name as type name if no type declarations found
            if (typeNames.Count == 0)
            {
                string fileName = Path.GetFileNameWithoutExtension(filePath);
                typeNames.Add(fileName);
            }

            // Default namespace based on directory structure
            if (fileNamespace == null)
            {
                fileNamespace = DeriveNamespaceFromPath(filePath);
            }

            // Register each type under its namespace
            foreach (string typeName in typeNames)
            {
                string key = $"{fileNamespace}.{typeName}";
                // First registration wins (don't overwrite)
                _namespaceIndex!.TryAdd(key, filePath);
            }
        }
        catch
        {
            // Skip files that can't be read/parsed
        }
    }

    /// <summary>
    /// Extracts namespace declaration from file content.
    /// Looks for "namespace Foo.Bar" pattern.
    /// </summary>
    private static string? ExtractNamespace(string content)
    {
        // Simple regex-free parsing for "namespace X" or "namespace X.Y.Z"
        foreach (string line in content.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("namespace "))
            {
                string ns = trimmed.Substring("namespace ".Length).Trim();
                // Remove any trailing comments or whitespace
                int commentIdx = ns.IndexOf('#');
                if (commentIdx >= 0)
                {
                    ns = ns.Substring(0, commentIdx).Trim();
                }
                return ns;
            }
            // Skip comments and empty lines, but stop at first non-comment declaration
            if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith("#") && !trimmed.StartsWith("import "))
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
        string[] keywords = { "record ", "entity ", "choice ", "variant ", "mutant ", "protocol ", "resident " };

        foreach (string line in content.Split('\n'))
        {
            string trimmed = line.Trim();

            // Skip visibility modifiers
            if (trimmed.StartsWith("public ") || trimmed.StartsWith("private ") ||
                trimmed.StartsWith("internal ") || trimmed.StartsWith("protected "))
            {
                trimmed = trimmed.Substring(trimmed.IndexOf(' ') + 1).Trim();
            }

            foreach (string keyword in keywords)
            {
                if (trimmed.StartsWith(keyword))
                {
                    string rest = trimmed.Substring(keyword.Length).Trim();
                    // Extract type name (before < or { or space)
                    int endIdx = rest.IndexOfAny(['<', '{', ' ', '(']);
                    string typeName = endIdx >= 0 ? rest.Substring(0, endIdx) : rest;
                    if (!string.IsNullOrEmpty(typeName))
                    {
                        types.Add(typeName);
                    }
                    break;
                }
            }
        }

        return types;
    }

    /// <summary>
    /// Derives namespace from file path relative to stdlib root.
    /// </summary>
    private string DeriveNamespaceFromPath(string filePath)
    {
        try
        {
            string? fileDir = Path.GetDirectoryName(filePath);
            if (fileDir == null) return "Core";

            string normalizedFileDir = Path.GetFullPath(fileDir);
            string normalizedStdlibPath = Path.GetFullPath(StdlibRoot);

            if (!normalizedFileDir.StartsWith(normalizedStdlibPath, StringComparison.OrdinalIgnoreCase))
            {
                return "Core";
            }

            string relativePath = normalizedFileDir.Substring(normalizedStdlibPath.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.IsNullOrEmpty(relativePath))
            {
                return "Core";
            }

            // NativeDataTypes -> Core
            if (relativePath.Equals("NativeDataTypes", StringComparison.OrdinalIgnoreCase))
            {
                return "Core";
            }

            return relativePath.Replace(Path.DirectorySeparatorChar, '.')
                              .Replace(Path.AltDirectorySeparatorChar, '.');
        }
        catch
        {
            return "Core";
        }
    }

    /// <summary>
    /// Normalizes an import path by converting namespace separators and removing file extensions.
    /// Handles both dot notation (Collections.List) and slash notation (Collections/List).
    /// </summary>
    private static string NormalizePath(string importPath)
    {
        // Convert dots to path separators (namespace-based imports)
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
            normalized = normalized[..^(Path.DirectorySeparatorChar.ToString().Length + 2)];
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
            _stdlibDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(StdlibRoot))
            {
                foreach (string dir in Directory.GetDirectories(StdlibRoot))
                {
                    _stdlibDirectories.Add(Path.GetFileName(dir));
                }
                // Also add stdlib root-level .rf files as potential module sources
                foreach (string file in Directory.GetFiles(StdlibRoot, "*.rf"))
                {
                    _stdlibDirectories.Add(Path.GetFileNameWithoutExtension(file));
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

        string fullPath = Path.GetFullPath(path: Path.Combine(path1: currentDir, path2: importPath));

        return TryFindSourceFile(basePath: fullPath);
    }

    /// <summary>
    /// Resolves a standard library import.
    /// </summary>
    private string? ResolveStdlibImport(string importPath)
    {
        string fullPath = Path.Combine(path1: StdlibRoot, path2: importPath);
        return TryFindSourceFile(basePath: fullPath);
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
    /// Extracts the module namespace from an import path.
    /// The namespace is the import path converted to dot notation.
    /// </summary>
    /// <param name="importPath">The import path (e.g., "Collections/List").</param>
    /// <returns>The namespace (e.g., "Collections.List").</returns>
    public static string GetNamespaceFromImport(string importPath)
    {
        return importPath
            .Replace(oldChar: '/', newChar: '.')
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
