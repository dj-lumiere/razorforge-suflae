namespace Builder;
/// <summary>
/// Describes the package metadata declared in <c>razorforge.toml</c>.
/// </summary>

public sealed class PackageInfo
{
    /// <summary>
    /// Gets the package name.
    /// </summary>
    public string Name { get; set; } = "";
    /// <summary>
    /// Gets the package version string.
    /// </summary>
    public string? Version { get; set; }
    /// <summary>
    /// Gets the list of package authors.
    /// </summary>
    public List<string>? Authors { get; set; }
    /// <summary>
    /// Gets the declared package license identifier or text.
    /// </summary>
    public string? License { get; set; }
    /// <summary>
    /// Gets the human-readable package description.
    /// </summary>
    public string? Description { get; set; }
    /// <summary>
    /// Gets the repository URL for the package.
    /// </summary>
    public string? Repository { get; set; }
    /// <summary>
    /// Gets the required RazorForge version constraint.
    /// </summary>
    public string? RazorForgeVersion { get; set; }
}
/// <summary>
/// Describes a build target declared in <c>razorforge.toml</c>.
/// </summary>

public sealed class TargetInfo
{
    /// <summary>
    /// Gets the target name.
    /// </summary>
    public string Name { get; set; } = "";
    /// <summary>
    /// Gets the target kind, such as <c>executable</c> or <c>library</c>.
    /// </summary>
    public string Type { get; set; } = "executable";
    /// <summary>
    /// Gets the resolved entry module or source path for the target.
    /// </summary>
    public string Entry { get; set; } = "";
    /// <summary>
    /// Gets the library output kind when <see cref="Type"/> is a library target.
    /// </summary>
    public string? LibType { get; set; }

    /// <summary>
    /// Gets the build mode for this target ("debug", "release", "release-time", "release-space").
    /// Defaults to "debug" when not specified.
    /// </summary>
    public string Mode { get; set; } = "debug";
}
/// <summary>
/// Represents the parsed contents of a project manifest file.
/// </summary>

public sealed class ProjectManifest
{
    /// <summary>
    /// Gets the package metadata section.
    /// </summary>
    public PackageInfo Package { get; set; } = new();
    /// <summary>
    /// Gets the declared build targets.
    /// </summary>
    public List<TargetInfo> Targets { get; set; } = [];
    /// <summary>
    /// Gets the directory containing the loaded manifest file.
    /// </summary>
    public string ManifestDirectory { get; set; } = "";
}
