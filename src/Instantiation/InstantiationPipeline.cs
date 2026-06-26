using System.Collections.Generic;
using System.Linq;
using Compiler.Instantiation.Passes;
using Verification;

namespace Compiler.Instantiation;

/// <summary>
/// Phase 6 pipeline boundary for generic closure.
/// This is intentionally separate from current codegen-era monomorphization logic.
/// </summary>
public sealed class InstantiationPipeline(InstantiationContext ctx)
{
    /// <summary>
    /// Runs generic reachability, closure expansion, and canonical body generation.
    /// </summary>
    public void Run()
    {
        new ReachableGenericCollectionPass(ctx).Run();
        new RoutineReachabilityPass(ctx).Run();
        ComputeMaySuspend();
        new GenericClosurePass(ctx).Run();
        GenericCanonicalizationPass.Run();
    }

    /// <summary>
    /// Runs the v0.2.0 may-suspend effect analysis over the call graph that
    /// <see cref="RoutineReachabilityPass"/> populated, storing the result on the context for the
    /// Phase 5 instrumentation pass to consume. For programs that never reach a suspend primitive
    /// (all current code) the result is empty and nothing downstream changes.
    /// </summary>
    private void ComputeMaySuspend()
    {
        IReadOnlySet<string> maySuspend = new MaySuspendAnalysis(callGraph: ctx.MaySuspendGraph).Compute();
        foreach (string key in maySuspend) ctx.MaySuspendRoutineKeys.Add(item: key);

        string? dumpPath = System.Environment.GetEnvironmentVariable(variable: "RF_MAYSUSPEND_DUMP");
        if (!string.IsNullOrEmpty(value: dumpPath))
        {
            var lines = new List<string> { "=== MAY-SUSPEND ROUTINES ===" };
            lines.AddRange(collection: ctx.MaySuspendRoutineKeys.OrderBy(keySelector: s => s));
            System.IO.File.WriteAllLines(path: dumpPath, contents: lines);
        }
    }
}
