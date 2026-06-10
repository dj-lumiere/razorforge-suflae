using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        // [target] — the single build description: executable + external library deps.
        if (root.TryGetValue(key: "target", value: out object? targetObj) &&
            targetObj is TomlTable targetTable)
        {
            manifest.Target = ParseBuildTarget(table: targetTable, moduleIndex: moduleIndex);
        }
        else
        {
            throw new InvalidOperationException(
                message:
                $"{ManifestFileName}: missing [target] section. Declare what the package builds, e.g.\n" +
                "[target]\nexecutable = \"MainModule\"\nlibrary = [\"../shared-utils\"]\nmode = \"debug\"");
        }

        // Resolve external library dependency directories relative to the manifest.
        for (int i = 0; i < manifest.Target.Libraries.Count; i++)
        {
            string rawEntry = manifest.Target.Libraries[index: i];
            string resolved = Path.GetFullPath(path: Path.Combine(path1: manifestDir,
                path2: rawEntry));
            if (!Directory.Exists(path: resolved))
            {
                throw new InvalidOperationException(
                    message:
                    $"{ManifestFileName}: library dependency '{rawEntry}' not found (resolved to '{resolved}'). " +
                    "Library entries are directories containing RazorForge modules.");
            }

            manifest.Target.Libraries[index: i] = resolved;
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

    private static BuildTarget ParseBuildTarget(TomlTable table,
        Dictionary<string, string> moduleIndex)
    {
        var target = new BuildTarget
        {
            Executable = ReadRequiredString(table: table,
                key: "executable",
                context: "[target]")
        };

        // `library` = EXTERNAL dependency directories (requirements.txt-style), relative
        // to the manifest. Accept a single string or an array of strings.
        if (table.TryGetValue(key: "library", value: out object? libraryObj))
        {
            IEnumerable<string?> rawEntries = libraryObj switch
            {
                TomlArray array => array.Select(selector: item => item?.ToString()),
                _ => [libraryObj?.ToString()]
            };
            foreach (string? rawEntry in rawEntries)
            {
                if (string.IsNullOrWhiteSpace(value: rawEntry))
                {
                    continue;
                }

                target.Libraries.Add(item: rawEntry);
            }
        }

        if (table.TryGetValue(key: "mode", value: out object? mode) &&
            !string.IsNullOrWhiteSpace(value: mode?.ToString()))
        {
            target.Mode = mode!.ToString()!;
        }

        if (table.TryGetValue(key: "dump-ast", value: out object? dumpAst))
            target.DumpAst = dumpAst is bool and true;

        if (table.TryGetValue(key: "sa-timing", value: out object? saTiming))
            target.SaTiming = saTiming is bool and true;

        if (table.TryGetValue(key: "show-build-stages", value: out object? showStages))
            target.ShowBuildStages = showStages is bool and true;

        // Resolve the executable's module name to a file path
        if (!moduleIndex.TryGetValue(key: target.Executable, value: out string? resolvedFile))
        {
            string available = moduleIndex.Count > 0
                ? string.Join(separator: ", ",
                    values: moduleIndex.Keys.OrderBy(keySelector: k => k))
                : "(none found)";
            throw new InvalidOperationException(
                message:
                $"{ManifestFileName}: executable module '{target.Executable}' not found. Available modules: {available}");
        }

        target.Executable = resolvedFile;
        return target;
    }

    private static string ReadRequiredString(TomlTable table, string key, string context)
    {
        if (!table.TryGetValue(key: key, value: out object? raw))
        {
            throw new InvalidOperationException(
                message: $"{ManifestFileName}: {context} must define '{key}'.");
        }

        string? value = raw?.ToString();
        if (string.IsNullOrWhiteSpace(value: value))
        {
            throw new InvalidOperationException(
                message: $"{ManifestFileName}: {context} field '{key}' cannot be empty.");
        }

        return value;
    }

    /// <summary>
    /// Scans all .rf and .sf files under <paramref name="projectDir"/> and builds a
    /// map of module name -> file path by reading module declarations.
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
                    string fullPath = Path.GetFullPath(path: filePath);
                    if (!index.TryAdd(key: moduleName, value: fullPath))
                    {
                        throw new InvalidOperationException(
                            message: $"{ManifestFileName}: duplicate module name '{moduleName}' " +
                                     $"resolved to both '{index[moduleName]}' and '{fullPath}'.");
                    }
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
                    !trimmed.StartsWith('#') && !trimmed.StartsWith(value: "import "))
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                value: $"Warning: Could not read or parse '{filePath}' for module name extraction: {ex.Message}");
        }

        return null;
    }
}
