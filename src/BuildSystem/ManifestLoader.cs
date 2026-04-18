using Tomlyn;
using Tomlyn.Model;

namespace Builder;
/// <summary>
/// Loads and validates RazorForge project manifest files.
/// </summary>

public static class ManifestLoader
{
    /// <summary>
    /// Gets the canonical file name for a RazorForge project manifest.
    /// </summary>
    public const string ManifestFileName = "razorforge.toml";

    /// <summary>
    /// Walks up from <paramref name="startDir"/> looking for a razorforge.toml file.
    /// Returns the full path to the manifest, or null if not found.
    /// </summary>
    public static string? FindManifest(string startDir)
    {
        string? dir = Path.GetFullPath(path: startDir);
        while (dir != null)
        {
            string candidate = Path.Combine(path1: dir, path2: ManifestFileName);
            if (File.Exists(path: candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(path: dir);
        }

        return null;
    }

    /// <summary>
    /// Parses a razorforge.toml file and returns a <see cref="ProjectManifest"/>.
    /// Validates that required fields are present and resolves entry modules to files.
    /// </summary>
    public static ProjectManifest Load(string tomlPath)
    {
        string fullPath = Path.GetFullPath(path: tomlPath);
        string manifestDir = Path.GetDirectoryName(path: fullPath)!;
        string content = File.ReadAllText(path: fullPath);

        TomlTable root = Toml.ToModel(text: content);

        var manifest = new ProjectManifest { ManifestDirectory = manifestDir };

        // [package]
        if (root.TryGetValue(key: "package", value: out object? packageObj) &&
            packageObj is TomlTable packageTable)
        {
            manifest.Package = ParsePackage(table: packageTable);
        }
        else
        {
            throw new InvalidOperationException(
                message: $"{ManifestFileName}: missing [package] section.");
        }

        if (string.IsNullOrWhiteSpace(value: manifest.Package.Name))
        {
            throw new InvalidOperationException(
                message: $"{ManifestFileName}: package.name is required.");
        }

        // Build module index for resolving entry modules
        Dictionary<string, string> moduleIndex = BuildModuleIndex(projectDir: manifestDir);

        // [targets.*]
        if (root.TryGetValue(key: "targets", value: out object? targetsObj) &&
            targetsObj is TomlTable targetsTable)
        {
            foreach ((string name, object value) in targetsTable)
            {
                if (value is TomlTable targetTable)
                {
                    TargetInfo target = ParseTarget(name: name,
                        table: targetTable,
                        moduleIndex: moduleIndex);
                    manifest.Targets.Add(item: target);
                }
            }
        }

        if (manifest.Targets.Count == 0)
        {
            throw new InvalidOperationException(
                message: $"{ManifestFileName}: at least one target is required.");
        }

        return manifest;
    }

    private static PackageInfo ParsePackage(TomlTable table)
    {
        var pkg = new PackageInfo();

        if (table.TryGetValue(key: "name", value: out object? name))
        {
            pkg.Name = name?.ToString() ?? "";
        }

        if (table.TryGetValue(key: "version", value: out object? version))
        {
            pkg.Version = version?.ToString();
        }

        if (table.TryGetValue(key: "license", value: out object? license))
        {
            pkg.License = license?.ToString();
        }

        if (table.TryGetValue(key: "description", value: out object? description))
        {
            pkg.Description = description?.ToString();
        }

        if (table.TryGetValue(key: "authors", value: out object? authorsObj) &&
            authorsObj is TomlArray authorsArray)
        {
            pkg.Authors = authorsArray.Select(selector: a => a?.ToString() ?? "")
                                      .ToList();
        }

        if (table.TryGetValue(key: "repository", value: out object? repository))
        {
            pkg.Repository = repository?.ToString();
        }

        if (table.TryGetValue(key: "razorforge-version", value: out object? rfVersion))
        {
            pkg.RazorForgeVersion = rfVersion?.ToString();
        }

        return pkg;
    }

    private static TargetInfo ParseTarget(string name, TomlTable table,
        Dictionary<string, string> moduleIndex)
    {
        var target = new TargetInfo { Name = name };

        if (table.TryGetValue(key: "type", value: out object? type))
        {
            target.Type = type?.ToString() ?? "executable";
        }

        if (table.TryGetValue(key: "entry", value: out object? entry))
        {
            target.Entry = entry?.ToString() ?? "";
        }

        if (table.TryGetValue(key: "lib_type", value: out object? libType))
        {
            target.LibType = libType?.ToString();
        }

        if (table.TryGetValue(key: "mode", value: out object? mode))
        {
            target.Mode = mode?.ToString() ?? "debug";
        }

        if (table.TryGetValue(key: "dump-ast", value: out object? dumpAst))
            target.DumpAst = dumpAst is bool b && b;

        if (string.IsNullOrWhiteSpace(value: target.Entry))
        {
            throw new InvalidOperationException(
                message: $"{ManifestFileName}: target '{name}' must have an 'entry' field.");
        }

        // Resolve module name to file path
        if (!moduleIndex.TryGetValue(key: target.Entry, value: out string? resolvedFile))
        {
            string available = moduleIndex.Count > 0
                ? string.Join(separator: ", ",
                    values: moduleIndex.Keys.OrderBy(keySelector: k => k))
                : "(none found)";
            throw new InvalidOperationException(
                message:
                $"{ManifestFileName}: target '{name}' entry module '{target.Entry}' not found. Available modules: {available}");
        }

        target.Entry = resolvedFile;
        return target;
    }

    /// <summary>
    /// Scans all .rf and .sf files under <paramref name="projectDir"/> and builds a
    /// map of module name → file path by reading module declarations.
    /// </summary>
    private static Dictionary<string, string> BuildModuleIndex(string projectDir)
    {
        var index = new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(path: projectDir))
        {
            return index;
        }

        string[] extensions =
        [
            "*.rf",
            "*.sf"
        ];
        foreach (string pattern in extensions)
        {
            foreach (string filePath in Directory.GetFiles(path: projectDir,
                         searchPattern: pattern,
                         searchOption: SearchOption.AllDirectories))
            {
                // Skip debug AST dump files — they share the module name with the real source
                if (filePath.EndsWith(value: ".rf.desugared",
                        comparisonType: StringComparison.OrdinalIgnoreCase))
                    continue;

                string? moduleName = ExtractModuleName(filePath: filePath);
                if (moduleName != null)
                {
                    // First file wins for a given module name
                    index.TryAdd(key: moduleName, value: Path.GetFullPath(path: filePath));
                }
            }
        }

        return index;
    }

    /// <summary>
    /// Reads the first "module X" declaration from a source file.
    /// </summary>
    private static string? ExtractModuleName(string filePath)
    {
        try
        {
            foreach (string line in File.ReadLines(path: filePath))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith(value: "module "))
                {
                    string name = trimmed["module ".Length..]
                       .Trim();
                    int commentIdx = name.IndexOf(value: '#');
                    if (commentIdx >= 0)
                    {
                        name = name[..commentIdx]
                           .Trim();
                    }

                    return name;
                }

                // Skip comments, empty lines, and imports — stop at first real declaration
                if (!string.IsNullOrWhiteSpace(value: trimmed) &&
                    !trimmed.StartsWith(value: "#") && !trimmed.StartsWith(value: "import "))
                {
                    break;
                }
            }
        }
        catch
        {
            // Skip unreadable files
        }

        return null;
    }
}
