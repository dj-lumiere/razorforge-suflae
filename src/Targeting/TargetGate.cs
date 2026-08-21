using System;
using System.IO;

namespace Compiler.Targeting;

/// <summary>
/// File-granularity conditional compilation for RazorForge. A <c>.rf</c> file may carry a leading
/// <c>#@target(...)</c> comment directive; when its conditions do not match the build target, the
/// file is EXCLUDED from the compile set before it is ever parsed.
///
/// <para>The directive is a Go-style build constraint: it lives inside an ordinary <c>#</c> comment,
/// so the tokenizer/parser never see it — only the build's file enumeration reads it. This keeps
/// conditional compilation at FILE granularity (a whole file is in or out), which avoids an in-file
/// preprocessor — those read badly in an indentation-based language, where <c>#if</c> blocks fight the
/// significant whitespace. Platform-specific code is split into separate files
/// (<c>foo_windows.rf</c> / <c>foo_linux.rf</c>) each guarded by its own directive.</para>
///
/// <para>RazorForge-only: <c>.sf</c> files are never gated (Suflae targets a REPL, not AOT
/// cross-builds). The directive must appear in the leading comment block, BEFORE any real code
/// (before <c>module</c>) — the enumerator stops scanning at the first non-comment line.</para>
///
/// <para>Syntax: <c>#@target(os: "windows")</c>, <c>#@target(os: "linux", arch: "arm64")</c>. Keys:
/// <c>os</c> (windows/linux/macos), <c>arch</c> (x64|x86_64, arm64|aarch64). Every named key must
/// match (AND); unknown keys are ignored for forward compatibility. The match is against a
/// <see cref="TargetConfig"/> — the host today, an explicit cross-target once cross-compilation
/// selects one.</para>
/// </summary>
public static class TargetGate
{
    private static readonly TargetConfig HostTarget = TargetConfig.ForCurrentHost();

    /// <summary>
    /// Returns false only when <paramref name="filePath"/> is a <c>.rf</c> file whose leading
    /// <c>#@target(...)</c> directive does not match <paramref name="target"/> (defaults to the host).
    /// Non-<c>.rf</c> files and files with no directive always compile.
    /// </summary>
    public static bool ShouldCompile(string filePath, TargetConfig? target = null)
    {
        if (!filePath.EndsWith(value: ".rf", comparisonType: StringComparison.OrdinalIgnoreCase))
            return true;

        string? inside = ReadTargetDirective(filePath: filePath);
        return inside == null || Matches(inside: inside, target: target ?? HostTarget);
    }

    /// <summary>
    /// Scans the file's leading comment block for a <c>#@target(...)</c> directive and returns the text
    /// inside the parentheses, or null if none precedes the first real line. Doc comments (<c>###</c>)
    /// and ordinary comments are skipped; the first non-comment, non-blank line ends the scan.
    /// </summary>
    private static string? ReadTargetDirective(string filePath)
    {
        foreach (string raw in File.ReadLines(path: filePath))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;               // blank line
            if (line.StartsWith(value: "###")) continue;  // doc comment — keep scanning the header
            if (line.StartsWith(value: "#"))
            {
                string body = line[1..].TrimStart();
                if (body.StartsWith(value: "@target(") && body.EndsWith(value: ")"))
                    return body["@target(".Length..^1];
                continue;                                  // ordinary comment — keep scanning
            }

            return null; // first real line (e.g. `module`) — the directive must precede it
        }

        return null;
    }

    private static bool Matches(string inside, TargetConfig target)
    {
        foreach (string part in inside.Split(separator: ','))
        {
            int colon = part.IndexOf(value: ':');
            if (colon < 0) continue;
            string key = part[..colon].Trim();
            string val = part[(colon + 1)..].Trim().Trim('"');

            bool ok = key switch
            {
                "os" => string.Equals(a: val, b: target.TargetOS,
                    comparisonType: StringComparison.OrdinalIgnoreCase),
                "arch" => string.Equals(a: NormalizeArch(arch: val),
                    b: NormalizeArch(arch: target.TargetArch),
                    comparisonType: StringComparison.OrdinalIgnoreCase),
                _ => true // unknown key: forward-compatible, ignore
            };
            if (!ok) return false;
        }

        return true;
    }

    /// <summary>Canonicalizes an architecture spelling so the directive's ergonomic aliases
    /// (<c>x64</c>/<c>arm64</c>) match <see cref="TargetConfig.TargetArch"/>'s LLVM names
    /// (<c>x86_64</c>/<c>aarch64</c>).</summary>
    private static string NormalizeArch(string arch) => arch.Trim().ToLowerInvariant() switch
    {
        "x64" or "amd64" or "x86_64" => "x86_64",
        "arm64" or "aarch64" => "aarch64",
        var other => other
    };
}
