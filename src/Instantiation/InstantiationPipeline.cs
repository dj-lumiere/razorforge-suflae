using Compiler.Instantiation.Passes;

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
        new GenericClosurePass(ctx).Run();
        GenericCanonicalizationPass.Run();
    }
}
