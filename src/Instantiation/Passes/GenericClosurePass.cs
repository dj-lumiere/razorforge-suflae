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

        // Lower protocol-default-impl routines (e.g. Iterable[Text].join) to per-implementer
        // routines BEFORE GMP, so the synthesized implementer-owned bodies flow through the
        // normal monomorphization machinery.
        new ProtocolDefaultImplLoweringPass(ctx: ctx).Run();
        // Re-sync adapter with newly-synthesized entries.
        foreach ((string key, MonomorphizedBody body) in ctx.InstantiatedGenericBodies)
        {
            adapter.InstantiatedGenericBodies[key] = body;
        }
        foreach (string key in ctx.LiveRoutineKeys)
        {
            adapter.LiveRoutineKeys.Add(item: key);
        }

        new GenericMonomorphizationPass(ctx: adapter).RunGlobal();
        // ControlFlowLowering for instantiated bodies: protocol-default-impl clones (from
        // ProtocolDefaultImplLoweringPass above) carry raw `for` loops from the stdlib AST
        // that never went through Phase 4 desugaring. Lower them before subsequent passes.
        new Compiler.Desugaring.Passes.ControlFlowLoweringPass(ctx: adapter)
            .RunOnInstantiatedGenericBodies(adapter.InstantiatedGenericBodies);
        new GenericCallLoweringPass(ctx: adapter).RunOnInstantiatedGenericBodies();
        new BuilderServiceInliningPass(ctx: adapter).RunOnInstantiatedGenericBodies();
        // Operator lowering for instantiated bodies: GMP's clones inherit unlowered
        // BinaryExpression/UnaryExpression nodes from the generic-def AST (the Phase 7
        // RunGlobal sweep finished before GMP populated the InstantiatedGenericBodies
        // map). Without this, `me.size = me.size + 1_u64` in a monomorphized routine
        // reaches codegen as a raw `BinaryExpression(Add)` and trips the codegen guard.
        var postCtx = new Compiler.Postprocessing.PostprocessingContext(
            registry: ctx.Registry,
            variantBodies: ctx.VariantBodies,
            target: ctx.Target,
            buildMode: ctx.BuildMode);
        // FStringLoweringPass runs BEFORE OperatorLoweringPass (per the per-file pipeline order);
        // monomorphized $represent/$diagnose bodies need f-strings lowered to $represent/$diagnose
        // method calls + Text.$add before operator lowering can fold the `+` chain.
        new Compiler.Postprocessing.Passes.FStringLoweringPass(ctx: postCtx)
            .RunOnInstantiatedGenericBodies(adapter.InstantiatedGenericBodies);
        // ExpressionLoweringPass: handles RangeExpression, UnaryExpression(Not), pattern lowering
        // etc. Must run before OperatorLoweringPass — operator lowering folds the BinaryExpressions
        // ExpressionLowering produces (e.g. `1 til n` -> a range record with `+ 1` / `< n` checks).
        // Mirror the per-file PLP → ELP → PLP → ELP cycle: the first PLP folds Maybe/Result/Lookup
        // when-chains (introducing UnaryExpression(Not)); ELP lowers those Not nodes; a second
        // PLP catches the WhenStatements that ELP synthesized for `??` / `?.`; final ELP lowers
        // any Not nodes the second PLP added. Without PLP here, `when subj is None => … else x =>`
        // over `Maybe[Wrapper[T]]` reaches codegen as raw TypePattern/ElsePattern; the codegen
        // TypePattern path falls through to an unconditional match → the first arm always wins.
        new Compiler.Postprocessing.Passes.PatternLoweringPass(ctx: postCtx)
            .RunOnInstantiatedGenericBodies(adapter.InstantiatedGenericBodies);
        new Compiler.Postprocessing.Passes.ExpressionLoweringPass(ctx: postCtx)
            .RunOnInstantiatedGenericBodies(adapter.InstantiatedGenericBodies);
        new Compiler.Postprocessing.Passes.PatternLoweringPass(ctx: postCtx)
            .RunOnInstantiatedGenericBodies(adapter.InstantiatedGenericBodies);
        new Compiler.Postprocessing.Passes.ExpressionLoweringPass(ctx: postCtx)
            .RunOnInstantiatedGenericBodies(adapter.InstantiatedGenericBodies);
        new Compiler.Postprocessing.Passes.OperatorLoweringPass(ctx: postCtx)
            .RunOnInstantiatedGenericBodies(adapter.InstantiatedGenericBodies);
        // Copy lowering for instantiated bodies: at generic-def time a field of generic type T looks
        // borrow-tier (no retaining $copy), so a monomorphized body that returns/stores a value with
        // a now-concrete refcounted field (e.g. DictEntry[Text, S64] from entry_get) never retained
        // it — torn down per use then freed again at container teardown. Re-run here, post-mono, so
        // GetLifecycle sees the concrete field types and injects the balancing $copy.
        new Compiler.Postprocessing.Passes.RecordCopyLoweringPass(ctx: postCtx)
            .RunOnInstantiatedGenericBodies(adapter.InstantiatedGenericBodies);

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
