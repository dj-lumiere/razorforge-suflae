using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

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
        AssertOutputEqual(fixtureName: fixtureName, expected: expected, actual: actual);
    }

    /// <summary>
    /// Compares fixture output and, on mismatch, throws with the COMPLETE expected and actual
    /// text (xUnit's <c>Assert.Equal</c> truncates long strings with <c>···</c>, which hides the
    /// real divergence). Also pinpoints the first differing line for a quick read.
    /// </summary>
    private static void AssertOutputEqual(string fixtureName, string expected, string actual)
    {
        if (expected == actual)
        {
            return;
        }

        string[] expLines = expected.Split('\n');
        string[] actLines = actual.Split('\n');
        var sb = new StringBuilder();
        sb.AppendLine($"Fixture output mismatch: {fixtureName}");

        int max = Math.Max(expLines.Length, actLines.Length);
        for (int i = 0; i < max; i++)
        {
            string e = i < expLines.Length ? expLines[i] : "<missing line>";
            string a = i < actLines.Length ? actLines[i] : "<missing line>";
            if (e != a)
            {
                sb.AppendLine($"First difference at line {i + 1}:");
                sb.AppendLine($"  expected: {e}");
                sb.AppendLine($"  actual:   {a}");
                break;
            }
        }

        sb.AppendLine($"({expLines.Length} expected lines, {actLines.Length} actual lines)");
        sb.AppendLine("================= FULL EXPECTED =================");
        sb.AppendLine(expected);
        sb.AppendLine("================= FULL ACTUAL ===================");
        sb.AppendLine(actual);
        sb.AppendLine("================================================");
        throw new Xunit.Sdk.XunitException(sb.ToString());
    }

    /// <summary>
    /// Builds and runs a fixture, returning its stdout. Retries a SPURIOUS external kill — a
    /// `buildandrun` process terminated mid-compile by the environment (SIGTERM/SIGKILL under
    /// transient memory/scheduling pressure during a long sequential run), recognisable by a
    /// non-zero exit with NO stdout AND NO stderr (no compiler diagnostic, no program output).
    /// Genuine failures are NOT retried: a real compile error prints diagnostics to stderr and a
    /// real runtime crash prints output / a trace, so they carry output and are deterministic — the
    /// retry gate (empty output) never matches them, so a broken fixture still fails on attempt 1.
    /// </summary>
    private const int MaxRunAttempts = 2;

    private static string RunFixture(string rfPath)
    {
        string fixture = Path.GetFileName(rfPath);
        FixtureRun last = default;

        for (int attempt = 1; attempt <= MaxRunAttempts; attempt++)
        {
            last = RunFixtureOnce(rfPath);

            if (last.TimedOut)
            {
                // A hang is a real defect, never an environmental blip — surface it immediately.
                throw new Xunit.Sdk.XunitException(
                    $"buildandrun timed out after {last.TimeoutMs / 1000}s for {fixture}.\n" +
                    $"--- stdout (partial) ---\n{last.Stdout}\n--- stderr (partial) ---\n{last.Stderr}");
            }

            if (last.ExitCode == 0)
            {
                return last.Stdout;
            }

            bool spuriousKill = last.Stdout.Length == 0 && last.Stderr.Length == 0;
            if (!spuriousKill)
            {
                // Real failure (compile diagnostic or runtime output present) — fail fast.
                throw new Xunit.Sdk.XunitException(
                    $"buildandrun failed for {fixture} (exit={last.ExitCode}).\n" +
                    $"--- stdout ---\n{last.Stdout}\n--- stderr ---\n{last.Stderr}");
            }

            // Silent external kill: log and retry — but only after a SUBSTANTIAL backoff. The kill
            // came from a transient memory spike; the killed process's pages are already reclaimed,
            // and waiting several seconds lets the spike fully clear before we re-spawn a heavy
            // compile. (A short backoff re-spawns INTO the live pressure and can tip the test host
            // over too — so the wait is load-relieving, not cosmetic.)
            Console.Error.WriteLine(
                $"[StdlibApiTests] {fixture}: spurious kill (exit={last.ExitCode}, no output) " +
                $"on attempt {attempt}/{MaxRunAttempts}; backing off then retrying.");
            if (attempt < MaxRunAttempts)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                System.Threading.Thread.Sleep(4000);
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"buildandrun for {fixture} was killed with no output (exit={last.ExitCode}) on all " +
            $"{MaxRunAttempts} attempts — likely sustained environmental resource pressure, not a " +
            $"fixture failure (it produced no compiler diagnostic and no program output).");
    }

    private readonly record struct FixtureRun(
        int ExitCode, string Stdout, string Stderr, bool TimedOut, int TimeoutMs);

    private static FixtureRun RunFixtureOnce(string rfPath)
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
        // Each buildandrun child compiles the entire (~180-file) stdlib and runs opt -O2/-O3 on a
        // large module — the heaviest memory user in the e2e suite. By default .NET uses SERVER GC,
        // which reserves a managed heap PER CORE; multiplied across ~150 sequential children plus the
        // test host, that peak exhausts memory on constrained machines and the OS OOM-kills processes
        // across the tree (the "exit=143, no output" spurious kills, and occasionally the test host
        // itself). Force WORKSTATION GC + aggressive memory conservation on the child to slash its
        // footprint; it stays single-fixture sequential, so the small GC-throughput trade is invisible.
        psi.Environment["DOTNET_gcServer"] = "0";
        psi.Environment["DOTNET_GCConserveMemory"] = "9";
        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        const int timeoutMs = 60_000;
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return new FixtureRun(ExitCode: -1, Stdout: stdoutTask.Result, Stderr: stderrTask.Result,
                TimedOut: true, TimeoutMs: timeoutMs);
        }

        return new FixtureRun(ExitCode: p.ExitCode, Stdout: stdoutTask.Result,
            Stderr: stderrTask.Result, TimedOut: false, TimeoutMs: timeoutMs);
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
