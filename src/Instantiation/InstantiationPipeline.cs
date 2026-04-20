using Compiler.Instantiation.Passes;

namespace Compiler.Instantiation;

/// <summary>
/// Phase 6 pipeline boundary for generic closure.
/// This is intentionally separate from current codegen-era monomorphization logic.
/// </summary>
public sealed class InstantiationPipeline(InstantiationContext ctx)
{
    public void Run()
    {
        new ReachableGenericCollectionPass(ctx).Run();
        new GenericClosurePass(ctx).Run();
        new GenericCanonicalizationPass(ctx).Run();
    }
}
