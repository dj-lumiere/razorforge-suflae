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
    private static readonly string CompilerDll = Path.Combine(AppContext.BaseDirectory, "RazorForge.dll");

    /// <summary>Enumerates .rf fixture files from the Stdlib fixtures directory.</summary>
    public static IEnumerable<object[]> Fixtures()
    {
        if (!Directory.Exists(FixturesDir)) yield break;
        foreach (string path in Directory.EnumerateFiles(FixturesDir, "*.rf").OrderBy(p => p))
        {
            yield return new object[] { Path.GetFileNameWithoutExtension(path) };
        }
    }

    /// <summary>Verifies that a stdlib fixture's output matches its expected snapshot file.</summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_OutputMatchesExpected(string fixtureName)
    {
        string rfPath = Path.Combine(FixturesDir, $"{fixtureName}.rf");
        string expectedPath = Path.Combine(FixturesDir, $"{fixtureName}.expected.txt");
        Assert.True(File.Exists(rfPath), $"Fixture .rf not found: {rfPath}");

        string actual = NormalizeForCompare(RunFixture(rfPath));

        // Bless mode: capture actual output as the new expected snapshot. Useful for
        // seeding new fixtures or refreshing after an intentional API/output change.
        // Set RF_TEST_BLESS=1 (or use the `bless-fixtures` helper) and re-run tests.
        // Write the normalized form (per-line-trimmed, LF) so blessed snapshots are stable
        // under editors that trim trailing whitespace on save.
        if (Environment.GetEnvironmentVariable("RF_TEST_BLESS") == "1")
        {
            File.WriteAllText(expectedPath, actual + "\n");
            return;
        }

        Assert.True(File.Exists(expectedPath),
            $"Expected snapshot not found: {expectedPath}. " +
            $"Run with RF_TEST_BLESS=1 to capture current output as the snapshot.");

        string expected = NormalizeForCompare(File.ReadAllText(expectedPath));
        Assert.Equal(expected, actual);
    }

    private static string RunFixture(string rfPath)
    {
        // Invoke the already-built compiler assembly directly. Using
        // `dotnet run --project …` here would re-evaluate the project file on
        // every fixture (~1-3s of SDK startup × ~150 fixtures = several minutes
        // of pure overhead).
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { CompilerDll, "buildandrun", rfPath },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoRoot
        };
        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        const int timeoutMs = 60_000;
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new Xunit.Sdk.XunitException(
                $"buildandrun timed out after {timeoutMs / 1000}s for {Path.GetFileName(rfPath)}.\n" +
                $"--- stdout (partial) ---\n{stdoutTask.Result}\n--- stderr (partial) ---\n{stderrTask.Result}");
        }
        string stdout = stdoutTask.Result;
        string stderr = stderrTask.Result;

        if (p.ExitCode != 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"buildandrun failed for {Path.GetFileName(rfPath)} (exit={p.ExitCode}).\n" +
                $"--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        }

        return stdout;
    }

    private static string NormalizeNewlines(string s) =>
        s.Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>
    /// Normalizes output for snapshot comparison: LF line endings (a snapshot checked out as CRLF on
    /// Windows must still match LF program output), trailing blank lines removed, and PER-LINE trailing
    /// whitespace stripped. The last is essential — editors trim trailing spaces on save, so a snapshot
    /// can't reliably hold them, yet programs legitimately emit them (e.g. <c>out + f"{x} "</c>).
    /// Trailing whitespace is never semantically meaningful for these stdlib fixtures, so trimming both
    /// sides avoids false mismatches while still catching every real content difference.
    /// </summary>
    private static string NormalizeForCompare(string s) =>
        string.Join("\n",
            NormalizeNewlines(s).TrimEnd('\n').Split('\n').Select(line => line.TrimEnd()));

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
