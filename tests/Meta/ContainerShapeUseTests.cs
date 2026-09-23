using System.Diagnostics;
using System.Text;

namespace RazorForge.Tests.Meta;

/// <summary>
/// End-to-end check of container shape use (the IterGuard): an `each` loop opens a shape use of its list,
/// and a change that could move the elements, hidden behind a call where the build-time checks cannot see
/// it, crashes with ReshapingWhileInUseError instead of leaving the loop reading moved memory. Builds and
/// runs <c>tests/Fixtures/ShapeUse/*.rf</c> through <c>buildandrun</c>. The paths that must NOT crash (after a
/// loop, after <c>break</c>, after a <c>return</c> out of a loop, around element calls) are covered by the
/// Stdlib fixture <c>container_shape_use.rf</c>.
/// </summary>
public sealed class ContainerShapeUseTests
{
    private static readonly string RepoRoot = LocateRepoRoot();

    private static readonly string CompilerDll =
        Path.Combine(path1: AppContext.BaseDirectory, path2: "RazorForge.dll");

    [Fact]
    public void IndirectReshapeDuringEach_CrashesWithReshapingWhileInUse()
    {
        (int exit, string stdout, string stderr) = RunFixture(fixture: "indirect_reshape_in_each.rf");

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
