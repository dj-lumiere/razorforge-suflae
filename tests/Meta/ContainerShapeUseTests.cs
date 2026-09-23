using System.Diagnostics;
using System.Text;

namespace RazorForge.Tests.Meta;

/// <summary>
/// End-to-end check of Suflae container shape use (the runtime IterGuard): an `each` loop marks its list's
/// shape as in use, and a change hidden behind a call (which Suflae cannot trace at build time, its list
/// being a shared <c>Roamed</c> handle) crashes with ReshapingWhileInUseError instead of leaving the loop
/// reading moved memory. RazorForge rejects the same program at build time (ShapeEffectTests). Builds and
/// runs <c>tests/Fixtures/ShapeUse/*.sf</c> through <c>buildandrun</c>. The paths that must NOT crash are
/// covered by the StdlibSf fixture <c>shape_use_sf.sf</c>.
/// </summary>
public sealed class ContainerShapeUseTests
{
    private static readonly string RepoRoot = LocateRepoRoot();

    private static readonly string CompilerDll =
        Path.Combine(path1: AppContext.BaseDirectory, path2: "RazorForge.dll");

    [Fact]
    public void IndirectReshapeDuringEach_CrashesWithReshapingWhileInUse()
    {
        (int exit, string stdout, string stderr) = RunFixture(fixture: "indirect_reshape_in_each.sf");

        Assert.True(condition: exit != 0,
            userMessage: $"expected a crash, got exit 0\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        Assert.Contains(expectedSubstring: "visiting 1", actualString: stdout);
        Assert.DoesNotContain(expectedSubstring: "not reached", actualString: stdout);
        Assert.Contains(expectedSubstring: "ReshapingWhileInUseError", actualString: stderr);
    }

    private static (int Exit, string Stdout, string Stderr) RunFixture(string fixture)
    {
        string rfPath = Path.Combine(paths: [RepoRoot, "tests", "Fixtures", "ShapeUse", fixture]);
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { CompilerDll, "buildandrun", rfPath },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            WorkingDirectory = RepoRoot,
            Environment =
            {
                [key: "DOTNET_gcServer"] = "0", [key: "DOTNET_GCConserveMemory"] = "9"
            }
        };
        using var p = Process.Start(startInfo: psi)!;
        Task<string> outTask = p.StandardOutput.ReadToEndAsync();
        Task<string> errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(milliseconds: 300_000))
        {
            try { p.Kill(entireProcessTree: true); }
            catch
            {
                /* best effort */
            }

            Assert.Fail(message: $"{fixture} did not finish.");
        }

        return (p.ExitCode, outTask.Result, errTask.Result);
    }

    private static string LocateRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(value: dir))
        {
            if (File.Exists(path: Path.Combine(path1: dir, path2: "RazorForge.csproj")))
            {
                return dir;
            }

            string? parent = Path.GetDirectoryName(path: dir);
            if (parent == null || parent == dir)
            {
                break;
            }

            dir = parent;
        }

        throw new InvalidOperationException(
            message: "Could not locate RazorForge.csproj walking up from test assembly directory.");
    }
}
