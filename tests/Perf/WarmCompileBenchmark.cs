using System;
using System.Diagnostics;
using Compiler.Resolution;
using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using Verification;
using Verification.Results;
using Xunit;
using Xunit.Abstractions;

namespace RazorForge.Tests.Perf;

/// <summary>
/// Milestone-1 de-risk: measures the compile-time ceiling when the stdlib is processed ONCE and
/// reused via the registry snapshot, versus the cold path that reprocesses the stdlib every run.
/// Not a correctness assertion — it prints timings via test output and always passes.
/// </summary>
public sealed class WarmCompileBenchmark
{
    private readonly ITestOutputHelper _out;
    public WarmCompileBenchmark(ITestOutputHelper output) => _out = output;

    private const string Trivial = """
                                   module Bench
                                   import IO/Console
                                   routine start()
                                     show("hi")
                                     return
                                   """;

    private static Program ParseTrivial()
    {
        var tokens = new Tokenizer(source: Trivial, fileName: "bench.rf",
            language: Language.RazorForge).Tokenize();
        return new Compiler.Parser.Parser(tokens: tokens, language: Language.RazorForge,
            fileName: "bench.rf").Parse();
    }

    private static double TimeMs(Action a)
    {
        var sw = Stopwatch.StartNew();
        a();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    [Fact]
    public void Ceiling_ColdVsWarm()
    {
        // COLD: full stdlib load + SA (this is what every CLI invocation pays today).
        TypeRegistry.StdlibSnapshot snap = null!;
        double cold = TimeMs(() => snap = SemanticVerifier.CaptureStdlibSnapshot(Language.RazorForge));
        _out.WriteLine($"COLD  capture stdlib snapshot (load+Phase1-5): {cold:N0} ms");

        // WARM (SA-only): restore snapshot + analyze the trivial user file, stopping at Phase 5.
        double warmSa = TimeMs(() =>
        {
            var sa = new SemanticVerifier(Language.RazorForge, snapshot: snap) { SaOnly = true };
            sa.Analyze(ParseTrivial());
        });
        _out.WriteLine($"WARM  restore + SA-only analyze(trivial):      {warmSa:N1} ms");

        // WARM (full pipeline): restore snapshot + full analyze (Phase 4/6/7). The restored registry
        // has no StdlibPrograms, so the stdlib desugar/monomorph loops are no-ops — this is the ceiling
        // for a full compile once the stdlib is not reprocessed.
        AnalysisResult warmResult = null!;
        double warmFull = TimeMs(() =>
        {
            var sa = new SemanticVerifier(Language.RazorForge, snapshot: snap);
            warmResult = sa.Analyze(ParseTrivial());
        });
        _out.WriteLine($"WARM  restore + FULL analyze(trivial):         {warmFull:N1} ms  " +
                       $"(errors={warmResult.Errors.Count})");

        // Second warm full to show steady-state (JIT/GC warmed), WITH per-phase timing to stderr.
        double warmFull2 = TimeMs(() =>
        {
            var sa = new SemanticVerifier(Language.RazorForge, snapshot: snap) { SaTiming = true };
            sa.Analyze(ParseTrivial());
        });
        _out.WriteLine($"WARM2 restore + FULL analyze(trivial):         {warmFull2:N1} ms");

        _out.WriteLine($"=> cold={cold:N0}ms  warm-full={warmFull2:N1}ms  " +
                       $"speedup≈{cold / Math.Max(warmFull2, 0.01):N0}x");

        // The ceiling claim: reusing the processed stdlib makes SA dramatically faster than the cold
        // load+analyze. (Generous bound so the guard is not machine-speed flaky.)
        Assert.True(warmSa < cold / 2.0,
            $"warm SA-only ({warmSa:N0}ms) should be far below cold snapshot capture ({cold:N0}ms)");
    }

    private static string PrintCodegenAst(AnalysisResult r) =>
        new Builder.RfSyntaxTreePrinter().PrintMultiProgram(
            programs: r.Registry.UserPrograms,
            synthesizedBodies: r.SynthesizedBodies,
            registry: r.Registry,
            stdlibPrograms: r.Registry.StdlibPrograms,
            instantiatedGenericBodies: r.InstantiatedGenericBodies);

    /// <summary>
    /// Correctness oracle for Milestone 1: a WARM compile (restored fully-processed stdlib) must feed
    /// codegen the IDENTICAL desugared AST as a COLD compile of the same source. Codegen is a pure
    /// translator, so identical input AST ⇒ identical .ll.
    ///
    /// WIP — does not pass yet: restoring StdlibPrograms makes RoutineReachabilityPass / GMP re-walk the
    /// stdlib (slow), and the synthesized-body set restored from the __snapshot__ capture differs from a
    /// cold trivial compile (missing @overridable derived routines). Next: gate reachability/GMP to reuse
    /// the captured live-set + monomorphizations, and capture the synthesized bodies consistently.
    /// </summary>
    [Fact(Skip = "WIP Milestone 1: warm codegen-equivalence needs reachability/GMP gating + synthesis consistency")]
    public void WarmCodegenAst_MatchesCold()
    {
        // COLD reference: a normal from-scratch compile of the trivial file.
        var cold = new SemanticVerifier(Language.RazorForge);
        AnalysisResult coldResult = cold.Analyze(ParseTrivial());
        string coldAst = PrintCodegenAst(coldResult);

        // WARM: capture the fully-processed stdlib once, then compile the SAME file from the restore.
        SemanticVerifier.CompiledStdlibState warm =
            SemanticVerifier.CaptureCompiledStdlib(Language.RazorForge);
        double warmMs = 0;
        AnalysisResult warmResult = null!;
        var sw = Stopwatch.StartNew();
        var warmSa = new SemanticVerifier(Language.RazorForge, warm);
        warmResult = warmSa.Analyze(ParseTrivial());
        warmMs = sw.Elapsed.TotalMilliseconds;
        string warmAst = PrintCodegenAst(warmResult);

        _out.WriteLine($"cold errors={coldResult.Errors.Count}  warm errors={warmResult.Errors.Count}");
        _out.WriteLine($"cold AST len={coldAst.Length}  warm AST len={warmAst.Length}");
        _out.WriteLine($"warm analyze: {warmMs:N1} ms");

        Assert.Empty(coldResult.Errors);
        Assert.Empty(warmResult.Errors);
        Assert.Equal(coldAst, warmAst);
    }
}
