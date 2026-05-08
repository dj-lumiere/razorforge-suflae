using Compiler.Desugaring;
using Compiler.Desugaring.Passes;
using Compiler.Postprocessing.Passes;
using SyntaxTree;

namespace Compiler.Instantiation.Passes;

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
            buildMode: ctx.BuildMode) { SaTiming = ctx.SaTiming };

        foreach ((string key, Statement body) in ctx.VariantBodies)
        {
            adapter.VariantBodies[key] = body;
        }

        foreach ((string key, MonomorphizedBody body) in ctx.InstantiatedGenericBodies)
        {
            adapter.InstantiatedGenericBodies[key] = body;
        }

        foreach (string key in ctx.LiveRoutineKeys)
        {
            adapter.LiveRoutineKeys.Add(item: key);
        }

        foreach (string typeName in ctx.LiveOwnerTypeNames)
        {
            adapter.LiveOwnerTypeNames.Add(item: typeName);
        }

        new GenericMonomorphizationPass(ctx: adapter).RunGlobal();
        new GenericCallLoweringPass(ctx: adapter).RunOnInstantiatedGenericBodies();
        new BuilderServiceInliningPass(ctx: adapter).RunOnInstantiatedGenericBodies();

        ctx.VariantBodies.Clear();
        foreach ((string key, Statement body) in adapter.VariantBodies)
        {
            ctx.VariantBodies[key] = body;
        }

        ctx.InstantiatedGenericBodies.Clear();
        foreach ((string key, MonomorphizedBody body) in adapter.InstantiatedGenericBodies)
        {
            ctx.InstantiatedGenericBodies[key] = body;
        }
    }
}
