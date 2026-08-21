using System.Collections.Generic;

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
/// Describes the single <c>[target]</c> section of <c>razorforge.toml</c> — what this
/// package builds and what it depends on. There is no target selection (and no CLI
/// flags): the manifest IS the build configuration.
/// <code>
/// [target]
/// executable = "MainModule"
/// library = ["../shared-utils", "libs/json-helpers"]
/// mode = "debug"
/// </code>
/// </summary>
public sealed class BuildTarget
{
    /// <summary>
    /// The executable's entry module (resolved to a file path at load time).
    /// </summary>
    public string Executable { get; set; } = "";

    /// <summary>
    /// EXTERNAL library dependencies (requirements.txt-style): directories — relative to
    /// the manifest — whose modules join the import search space between the project and
    /// the stdlib. Resolved to absolute paths at load time.
    /// FUTURE (post-v0.0.1a package manager): entries will name packages on the package
    /// site with a version (e.g. <c>"json-utils@1.2.0"</c>); the fetch step resolves each
    /// into a cache directory and the build consumes it exactly like a local entry here.
    /// Local directory paths remain supported as the offline/vendored form.
    /// </summary>
    public List<string> Libraries { get; set; } = [];

    /// <summary>
    /// External C libraries to link (the <c>-l</c> names, e.g. <c>"SDL2"</c>). Names only — the platform
    /// resolves each to <c>libSDL2.so</c> / <c>SDL2.lib</c> / <c>libSDL2.dylib</c> at link time via the
    /// bundled clang/lld driver. Search directories come from <see cref="LibraryPaths"/>.
    /// </summary>
    public List<string> CLibraries { get; set; } = [];

    /// <summary>
    /// Additional library search directories (the <c>-L</c> paths) for resolving <see cref="CLibraries"/>.
    /// Relative entries are resolved against the manifest directory at load time.
    /// </summary>
    public List<string> LibraryPaths { get; set; } = [];

    /// <summary>
    /// Build mode for the whole build: "debug" (default), "release", "release-time",
    /// "release-space".
    /// </summary>
    public string Mode { get; set; } = "debug";

    /// <summary>
    /// When true, writes the fully post-desugared AST to a .rf.desugared file alongside the .ll output.
    /// Controlled by the <c>dump-ast</c> field.
    /// </summary>
    public bool DumpAst { get; set; }

    /// <summary>
    /// When true, prints per-phase semantic-analysis timings to stderr.
    /// Controlled by the <c>sa-timing</c> field.
    /// </summary>
    public bool SaTiming { get; set; }

    /// <summary>
    /// When true, prints build-stage banners ("=== SEMANTIC ANALYSIS ===", "Build successful!",
    /// "=== EXECUTION ===", etc.) during build/buildandrun. Default is false: only errors and
    /// warnings are printed, and the program's own stdout passes through unframed.
    /// Controlled by the <c>show-build-stages</c> field.
    /// </summary>
    public bool ShowBuildStages { get; set; }
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
    /// Gets the single build target declared by the <c>[target]</c> section.
    /// </summary>
    public BuildTarget Target { get; set; } = new();
    /// <summary>
    /// Gets the directory containing the loaded manifest file.
    /// </summary>
    public string ManifestDirectory { get; set; } = "";
}
