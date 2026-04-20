namespace Compiler.Instantiation.Passes;

using Compiler.Desugaring;
using Compiler.Postprocessing.Passes;
using SyntaxTree;

/// <summary>
/// Phase 6 closure pass: reuse the existing generic monomorphization implementation, but run it
/// behind the explicit instantiation pipeline boundary instead of the old desugaring pipeline.
/// </summary>
internal sealed class GenericClosurePass(InstantiationContext ctx)
{
    public void Run()
    {
        var adapter = new DesugaringContext(registry: ctx.Registry,
            routineBodies: ctx.RoutineBodies,
            target: ctx.Target,
            buildMode: ctx.BuildMode);

        foreach ((string key, Statement body) in ctx.VariantBodies)
        {
            adapter.VariantBodies[key] = body;
        }

        foreach ((string key, MonomorphizedBody body) in ctx.PreMonomorphizedBodies)
        {
            adapter.PreMonomorphizedBodies[key] = body;
        }

        new GenericMonomorphizationPass(ctx: adapter).RunGlobal();
        new BuilderServiceInliningPass(ctx: adapter).RunOnPreMonomorphizedBodies();

        ctx.VariantBodies.Clear();
        foreach ((string key, Statement body) in adapter.VariantBodies)
        {
            ctx.VariantBodies[key] = body;
        }

        ctx.PreMonomorphizedBodies.Clear();
        foreach ((string key, MonomorphizedBody body) in adapter.PreMonomorphizedBodies)
        {
            ctx.PreMonomorphizedBodies[key] = body;
        }
    }
}
