using Tomlyn;
using Tomlyn.Model;

namespace Builder;

public static class ManifestLoader
{
    public const string ManifestFileName = "razorforge.toml";

    /// <summary>
    /// Walks up from <paramref name="startDir"/> looking for a razorforge.toml file.
    /// Returns the full path to the manifest, or null if not found.
    /// </summary>
    public static string? FindManifest(string startDir)
    {
        string? dir = Path.GetFullPath(startDir);
        while (dir != null)
        {
            string candidate = Path.Combine(dir, ManifestFileName);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>
    /// Parses a razorforge.toml file and returns a <see cref="ProjectManifest"/>.
    /// Validates that required fields are present and resolves entry modules to files.
    /// </summary>
    public static ProjectManifest Load(string tomlPath)
    {
        string fullPath = Path.GetFullPath(tomlPath);
        string manifestDir = Path.GetDirectoryName(fullPath)!;
        string content = File.ReadAllText(fullPath);

        TomlTable root = Toml.ToModel(content);

        var manifest = new ProjectManifest { ManifestDirectory = manifestDir };

        // [package]
        if (root.TryGetValue("package", out object? packageObj) && packageObj is TomlTable packageTable)
        {
            manifest.Package = ParsePackage(packageTable);
        }
        else
        {
            throw new InvalidOperationException($"{ManifestFileName}: missing [package] section.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Package.Name))
            throw new InvalidOperationException($"{ManifestFileName}: package.name is required.");

        // Build module index for resolving entry modules
        var moduleIndex = BuildModuleIndex(manifestDir);

        // [targets.*]
        if (root.TryGetValue("targets", out object? targetsObj) && targetsObj is TomlTable targetsTable)
        {
            foreach (var (name, value) in targetsTable)
            {
                if (value is TomlTable targetTable)
                {
                    var target = ParseTarget(name, targetTable, moduleIndex);
                    manifest.Targets.Add(target);
                }
            }
        }

        if (manifest.Targets.Count == 0)
            throw new InvalidOperationException($"{ManifestFileName}: at least one target is required.");

        return manifest;
    }

    private static PackageInfo ParsePackage(TomlTable table)
    {
        var pkg = new PackageInfo();

        if (table.TryGetValue("name", out object? name))
            pkg.Name = name?.ToString() ?? "";
        if (table.TryGetValue("version", out object? version))
            pkg.Version = version?.ToString();
        if (table.TryGetValue("license", out object? license))
            pkg.License = license?.ToString();
        if (table.TryGetValue("description", out object? description))
            pkg.Description = description?.ToString();
        if (table.TryGetValue("authors", out object? authorsObj) && authorsObj is TomlArray authorsArray)
            pkg.Authors = authorsArray.Select(a => a?.ToString() ?? "").ToList();
        if (table.TryGetValue("repository", out object? repository))
            pkg.Repository = repository?.ToString();
        if (table.TryGetValue("razorforge-version", out object? rfVersion))
            pkg.RazorForgeVersion = rfVersion?.ToString();

        return pkg;
    }

    private static TargetInfo ParseTarget(string name, TomlTable table, Dictionary<string, string> moduleIndex)
    {
        var target = new TargetInfo { Name = name };

        if (table.TryGetValue("type", out object? type))
            target.Type = type?.ToString() ?? "executable";
        if (table.TryGetValue("entry", out object? entry))
            target.Entry = entry?.ToString() ?? "";
        if (table.TryGetValue("lib_type", out object? libType))
            target.LibType = libType?.ToString();

        if (string.IsNullOrWhiteSpace(target.Entry))
            throw new InvalidOperationException($"{ManifestFileName}: target '{name}' must have an 'entry' field.");

        // Resolve module name to file path
        if (!moduleIndex.TryGetValue(target.Entry, out string? resolvedFile))
        {
            string available = moduleIndex.Count > 0
                ? string.Join(", ", moduleIndex.Keys.OrderBy(k => k))
                : "(none found)";
            throw new InvalidOperationException(
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
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(projectDir))
            return index;

        var extensions = new[] { "*.rf", "*.sf" };
        foreach (string pattern in extensions)
        {
            foreach (string filePath in Directory.GetFiles(projectDir, pattern, SearchOption.AllDirectories))
            {
                string? moduleName = ExtractModuleName(filePath);
                if (moduleName != null)
                {
                    // First file wins for a given module name
                    index.TryAdd(moduleName, Path.GetFullPath(filePath));
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
            foreach (string line in File.ReadLines(filePath))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("module "))
                {
                    string name = trimmed["module ".Length..].Trim();
                    int commentIdx = name.IndexOf('#');
                    if (commentIdx >= 0)
                        name = name[..commentIdx].Trim();
                    return name;
                }
                // Skip comments, empty lines, and imports — stop at first real declaration
                if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith("#") && !trimmed.StartsWith("import "))
                    break;
            }
        }
        catch
        {
            // Skip unreadable files
        }
        return null;
    }
}
