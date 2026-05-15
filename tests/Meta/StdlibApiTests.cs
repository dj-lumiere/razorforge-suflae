using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace RazorForge.Tests.Meta;

/// <summary>
/// Runs each <c>tests/Fixtures/Stdlib/*.rf</c> fixture through <c>buildandrun</c> and
/// diffs its stdout against the sibling <c>.expected.txt</c> snapshot. Provides
/// end-to-end behavior coverage for stdlib types/routines that <c>validate-stdlib</c>
/// (parse-and-typecheck only) does not catch.
/// </summary>
public sealed class StdlibApiTests
{
    private static readonly string RepoRoot = LocateRepoRoot();
    private static readonly string FixturesDir = Path.Combine(RepoRoot, "tests", "Fixtures", "Stdlib");
    private static readonly string ProjectFile = Path.Combine(RepoRoot, "RazorForge.csproj");

    public static IEnumerable<object[]> Fixtures()
    {
        if (!Directory.Exists(FixturesDir)) yield break;
        foreach (string path in Directory.EnumerateFiles(FixturesDir, "*.rf").OrderBy(p => p))
        {
            yield return new object[] { Path.GetFileNameWithoutExtension(path) };
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_OutputMatchesExpected(string fixtureName)
    {
        string rfPath = Path.Combine(FixturesDir, $"{fixtureName}.rf");
        string expectedPath = Path.Combine(FixturesDir, $"{fixtureName}.expected.txt");
        Assert.True(File.Exists(rfPath), $"Fixture .rf not found: {rfPath}");

        string actual = NormalizeNewlines(RunFixture(rfPath));

        // Bless mode: capture actual output as the new expected snapshot. Useful for
        // seeding new fixtures or refreshing after an intentional API/output change.
        // Set RF_TEST_BLESS=1 (or use the `bless-fixtures` helper) and re-run tests.
        if (Environment.GetEnvironmentVariable("RF_TEST_BLESS") == "1")
        {
            File.WriteAllText(expectedPath, actual);
            return;
        }

        Assert.True(File.Exists(expectedPath),
            $"Expected snapshot not found: {expectedPath}. " +
            $"Run with RF_TEST_BLESS=1 to capture current output as the snapshot.");

        string expected = NormalizeNewlines(File.ReadAllText(expectedPath));
        Assert.Equal(expected.TrimEnd(), actual.TrimEnd());
    }

    private static string RunFixture(string rfPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { "run", "--project", ProjectFile, "--no-build", "--",
                "buildandrun", rfPath },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoRoot
        };
        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();

        // buildandrun emits a banner before the user program's output and a build summary
        // after. The user-visible run starts after "=== EXECUTION ===" and ends at the
        // next blank section header (or EOF). Strip the framing.
        return ExtractExecutionSection(stdout);
    }

    private static string ExtractExecutionSection(string output)
    {
        const string marker = "=== EXECUTION ===";
        int idx = output.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return output;
        int start = idx + marker.Length;
        // Skip the newline after the marker.
        while (start < output.Length && (output[start] == '\r' || output[start] == '\n')) start++;
        return output[start..];
    }

    private static string NormalizeNewlines(string s) =>
        s.Replace("\r\n", "\n").Replace("\r", "\n");

    private static string LocateRepoRoot()
    {
        // Walk up from the test assembly until we find RazorForge.csproj.
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "RazorForge.csproj"))) return dir;
            string? parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir) break;
            dir = parent;
        }
        throw new InvalidOperationException(
            "Could not locate RazorForge.csproj walking up from test assembly directory.");
    }
}
