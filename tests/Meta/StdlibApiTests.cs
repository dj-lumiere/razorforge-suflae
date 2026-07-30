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

    private static readonly string FixturesDir = Path.Combine(RepoRoot,
        "tests",
        "Fixtures",
        "Stdlib");

    private static readonly string CompilerDll =
        Path.Combine(AppContext.BaseDirectory, "RazorForge.dll");

    // The per-fixture `Fixture_OutputMatchesExpected` [Theory] (one full-stdlib compile per fixture,
    // ~165× ≈ 15 min, load-flaky) was replaced by the single-compile harness below.
    private static readonly string HarnessDir =
        Path.Combine(RepoRoot, "tests", "Fixtures", "StdlibHarness");

    private const string HarnessModule = "StdlibHarness";

    private static readonly System.Text.RegularExpressions.Regex ModuleRe =
        new(@"^\s*module\s+(\S+)\s*$");

    /// <summary>
    /// Single-compile stdlib e2e test: generates <c>all_stdlib.rf</c> + <c>razorforge.toml</c> from
    /// every <c>Stdlib/*.rf</c> fixture (importing each by its declared module and calling its
    /// <c>start()</c> via the module leaf), compiles + runs the ONE program, splits the combined
    /// stdout on the <c>##### fixture #####</c> delimiters, and diffs each section against its
    /// <c>.expected.txt</c>. Compiles the stdlib + all fixtures ONCE instead of ~165 times.
    /// </summary>
    [Fact]
    public void StdlibHarness_AllFixturesOutputMatchExpected()
    {
        // 1) Generate the harness program + manifest from the fixtures.
        var entries = new List<(string Stem, string Module, string Leaf)>();
        var leaves = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        foreach (string rf in Directory.EnumerateFiles(FixturesDir, "*.rf")
                                       .OrderBy(keySelector: p => p, comparer: StringComparer.Ordinal))
        {
            string? module = ReadDeclaredModule(rfPath: rf);
            if (module == null) continue;
            string leaf = module.Contains('/') ? module[(module.LastIndexOf('/') + 1)..] : module;
            if (leaves.TryGetValue(leaf, out string? other))
            {
                throw new Xunit.Sdk.XunitException(
                    $"Harness leaf collision '{leaf}': {other} vs {module} — module-qualified leaf calls " +
                    "would be ambiguous (RF-S513). Rename one fixture's module leaf.");
            }
            leaves[leaf] = module;
            entries.Add((Path.GetFileNameWithoutExtension(rf), module, leaf));
        }

        Directory.CreateDirectory(HarnessDir);
        var lines = new List<string> { $"module {HarnessModule}", "", "import IO/Console" };
        lines.AddRange(entries.Select(selector: e => $"import {e.Module}"));
        lines.Add("");
        lines.Add("routine start()");
        foreach ((string stem, _, string leaf) in entries)
        {
            lines.Add($"  show(\"##### {stem} #####\")");
            lines.Add($"  {leaf}.start()");
        }
        lines.Add("  return");
        lines.Add("");
        string harnessRf = Path.Combine(HarnessDir, "all_stdlib.rf");
        File.WriteAllText(harnessRf, string.Join("\n", lines));

        string manifest =
            $"[package]\nname = \"stdlib-harness\"\nversion = \"0.0.1\"\nrazorforge-version = \"0.1.0\"\n\n" +
            $"[target]\nexecutable = \"{HarnessModule}\"\nmode = \"debug\"\nlibrary = [\"../Stdlib\"]\n";
        File.WriteAllText(Path.Combine(HarnessDir, "razorforge.toml"), manifest);

        // 2) Compile + run the ONE program (cwd = harness dir so razorforge.toml is discovered).
        FixtureRun run = RunHarness(harnessRf: harnessRf);
        Assert.True(run is { ExitCode: 0, TimedOut: false },
            $"Harness buildandrun failed (exit={run.ExitCode}, timedOut={run.TimedOut}).\n" +
            $"--- stdout ---\n{run.Stdout}\n--- stderr ---\n{run.Stderr}");

        // 3) Split combined output on the delimiter lines.
        Dictionary<string, string> sections = SplitHarnessOutput(stdout: run.Stdout);

        // 4) Compare each fixture's section to its snapshot.
        var mismatches = new List<string>();
        foreach ((string stem, _, _) in entries)
        {
            string expectedPath = Path.Combine(FixturesDir, $"{stem}.expected.txt");
            if (!File.Exists(expectedPath)) continue;
            string expected = NormalizeForCompare(s: File.ReadAllText(expectedPath));
            string actual = NormalizeForCompare(s: sections.GetValueOrDefault(stem, "<no output section emitted>"));
            if (expected != actual) mismatches.Add(item: stem);
        }

        if (mismatches.Count > 0)
        {
            // Surface the first mismatch in full for a quick read.
            string first = mismatches[0];
            AssertOutputEqual(fixtureName: first,
                expected: NormalizeForCompare(s: File.ReadAllText(Path.Combine(FixturesDir, $"{first}.expected.txt"))),
                actual: NormalizeForCompare(s: sections.GetValueOrDefault(first, "<no output section emitted>")));
            throw new Xunit.Sdk.XunitException(
                $"{mismatches.Count} harness fixture(s) mismatched: {string.Join(", ", mismatches)}");
        }
    }

    /// <summary>Reads the declared <c>module</c> path of a fixture (utf-8-sig for BOM), or null.</summary>
    private static string? ReadDeclaredModule(string rfPath)
    {
        foreach (string line in File.ReadLines(rfPath))
        {
            System.Text.RegularExpressions.Match m = ModuleRe.Match(line);
            if (m.Success) return m.Groups[1].Value;
            string stripped = line.Trim();
            if (stripped.Length > 0 && !stripped.StartsWith('#')) return null;
        }
        return null;
    }

    /// <summary>Splits harness stdout into per-fixture sections keyed by the <c>##### stem #####</c> delimiters.</summary>
    private static Dictionary<string, string> SplitHarnessOutput(string stdout)
    {
        var sections = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        string? current = null;
        var buf = new StringBuilder();
        foreach (string rawLine in NormalizeNewlines(s: stdout).Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("##### ") && line.EndsWith(" #####"))
            {
                if (current != null) sections[current] = buf.ToString();
                current = line[6..^6].Trim();
                buf.Clear();
                continue;
            }
            if (current != null) buf.Append(rawLine).Append('\n');
        }
        if (current != null) sections[current] = buf.ToString();
        return sections;
    }

    /// <summary>Runs <c>buildandrun</c> on the harness program with the harness dir as cwd.</summary>
    private static FixtureRun RunHarness(string harnessRf)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { CompilerDll, "buildandrun", harnessRf },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = HarnessDir,
            Environment = { ["DOTNET_gcServer"] = "0", ["DOTNET_GCConserveMemory"] = "9" }
        };
        using var p = Process.Start(psi)!;
        System.Threading.Tasks.Task<string> outTask = p.StandardOutput.ReadToEndAsync();
        System.Threading.Tasks.Task<string> errTask = p.StandardError.ReadToEndAsync();
        const int timeoutMs = 300_000;
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return new FixtureRun(ExitCode: -1, Stdout: outTask.Result, Stderr: errTask.Result,
                TimedOut: true, TimeoutMs: timeoutMs);
        }
        return new FixtureRun(ExitCode: p.ExitCode, Stdout: outTask.Result, Stderr: errTask.Result,
            TimedOut: false, TimeoutMs: timeoutMs);
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
            string e = i < expLines.Length
                ? expLines[i]
                : "<missing line>";
            string a = i < actLines.Length
                ? actLines[i]
                : "<missing line>";
            if (e == a)
            {
                continue;
            }

            sb.AppendLine($"First difference at line {i + 1}:");
            sb.AppendLine($"  expected: {e}");
            sb.AppendLine($"  actual:   {a}");
            break;
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
            if (attempt >= MaxRunAttempts)
            {
                continue;
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            System.Threading.Thread.Sleep(4000);
        }

        throw new Xunit.Sdk.XunitException(
            $"buildandrun for {fixture} was killed with no output (exit={last.ExitCode}) on all " +
            $"{MaxRunAttempts} attempts — likely sustained environmental resource pressure, not a " +
            $"fixture failure (it produced no compiler diagnostic and no program output).");
    }

    private readonly record struct FixtureRun(
        int ExitCode,
        string Stdout,
        string Stderr,
        bool TimedOut,
        int TimeoutMs);

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
            WorkingDirectory = RepoRoot,
            // Each buildandrun child compiles the entire (~180-file) stdlib and runs opt -O2/-O3 on a
            // large module — the heaviest memory user in the e2e suite. By default .NET uses SERVER GC,
            // which reserves a managed heap PER CORE; multiplied across ~150 sequential children plus the
            // test host, that peak exhausts memory on constrained machines and the OS OOM-kills processes
            // across the tree (the "exit=143, no output" spurious kills, and occasionally the test host
            // itself). Force WORKSTATION GC + aggressive memory conservation on the child to slash its
            // footprint; it stays single-fixture sequential, so the small GC-throughput trade is invisible.
            Environment = { ["DOTNET_gcServer"] = "0", ["DOTNET_GCConserveMemory"] = "9" }
        };
        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        const int timeoutMs = 60_000;
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); }
            catch { }

            return new FixtureRun(ExitCode: -1,
                Stdout: stdoutTask.Result,
                Stderr: stderrTask.Result,
                TimedOut: true,
                TimeoutMs: timeoutMs);
        }

        return new FixtureRun(ExitCode: p.ExitCode,
            Stdout: stdoutTask.Result,
            Stderr: stderrTask.Result,
            TimedOut: false,
            TimeoutMs: timeoutMs);
    }

    private static string NormalizeNewlines(string s) =>
        s.Replace("\r\n", "\n")
         .Replace("\r", "\n");

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
            NormalizeNewlines(s)
               .TrimEnd('\n')
               .Split('\n')
               .Select(line => line.TrimEnd()));

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
