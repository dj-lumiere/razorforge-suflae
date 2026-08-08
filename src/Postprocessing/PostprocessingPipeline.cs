using Compiler.Desugaring.Passes;
using Compiler.Postprocessing.Passes;
using SyntaxTree;

namespace Compiler.Postprocessing;

/// <summary>
/// Phase 7 pipeline: type-aware lowering on already-verified AST.
/// Runs after Phase 5 (semantic analysis) and Phase 4 synthesis.
/// </summary>
public sealed class PostprocessingPipeline(PostprocessingContext ctx)
{
    /// <summary>
    /// Runs all Phase 7 lowering passes on a single program (user file or stdlib file).
    /// Must be called after SA has annotated ResolvedType on all expressions.
    /// </summary>
    public void Run(Program program)
    {
        new LiteralLoweringPass(ctx).Run(program);
        new BuilderServiceInliningPass(ctx.Registry, ctx.VariantBodies).Run(program);
        new GenericCallLoweringPass(ctx.Registry, ctx.VariantBodies).Run(program);
        StructuralLoweringPass.Run(program);
        new FStringLoweringPass(ctx).Run(program);
        new CrashableExpansionPass(ctx).Run(program);
        // CallOverloadResolutionPass runs after FStringLoweringPass so that the represent/diagnose
        // calls it synthesizes are visible and can be classified before reaching codegen.
        new CallOverloadResolutionPass(ctx).Run(program);
        // PatternLowering before ExpressionLowering: PLP introduces UnaryExpression(Not)
        // when lowering WhenStatement -> IfStatement chains; ELP must see those new nodes.
        // OLP runs after ELP so chained comparisons are already split into BinaryExpressions.
        new PatternLoweringPass(ctx).Run(program);
        new ExpressionLoweringPass(ctx).Run(program);
        // ExpressionLoweringPass synthesizes WhenStatements with NonePattern / TypePattern("None")
        // when lowering `??` and `?.` (see MakeAbsencePattern in ExpressionLoweringPass). Those
        // would-be-lowered patterns are inserted AFTER the first PatternLoweringPass run, so
        // re-run PLP here to fold them into the if/else chains codegen expects. The second PLP
        // run can introduce UnaryExpression(Not) (e.g. `not present`) on Maybe[T record] absence
        // checks, so re-run ELP after it to lower those into ConditionalExpression form.
        new PatternLoweringPass(ctx).Run(program);
        new ExpressionLoweringPass(ctx).Run(program);
        new OperatorLoweringPass(ctx).Run(program);
        // RoamedProjectionLoweringPass runs after OperatorLoweringPass and FStringLoweringPass so it
        // sees the operator/f-string-lowered Roamed receiver calls; it rewrites the codegen-side
        // raw_inner() projection into a real AST call (+ inner represent/diagnose re-resolution).
        new RoamedProjectionLoweringPass(ctx).Run(program);
        // Moves the Stage 2b spawn-boundary `promote()` out of codegen into a real AST call: for each
        // Roamed[T] argument of a suspended/threaded spawn, inserts `arg.promote()` before the spawn.
        new RoamedSpawnPromotionLoweringPass(ctx).Run(program);
        new RecordCopyLoweringPass(ctx).Run(program);
        // Moves the implicit RC-wrapper copy-verb bump (retain/track/share/watch/roam) out of codegen
        // into a real AST call: one bump per RC field of a record copy, one roam per Roamed entity-
        // field write, inserted right after the store. Runs after RecordCopyLoweringPass (store).
        new RcRetainLoweringPass(ctx).Run(program);
        // Moves the Roamed[E] access-lock bracket (lock_enter/lock_exit around a direct entity-field
        // read/write) out of codegen into real AST calls: one enter before / one exit after each
        // statement that touches a Roamed field. Runs after the other Roamed passes so field accesses
        // are in final form.
        new RoamedLockBracketLoweringPass(ctx).Run(program);
        new BecomesLoweringPass(ctx).Run(program);
        new UsingLoweringPass(ctx).Run(program);
        new LambdaLiftingPass(ctx).Run(program);
    }

    /// <summary>
    /// Runs Phase 7 lowering on variant bodies produced by Phase 4 synthesis,
    /// and on stdlib programs that bypass per-file <see cref="Run"/>.
    /// Must be called after <see cref="Run"/> has been applied to all user programs.
    /// </summary>
    public void RunGlobal()
    {
        new LiteralLoweringPass(ctx).RunOnVariantBodies();
        new BuilderServiceInliningPass(ctx.Registry, ctx.VariantBodies).RunOnVariantBodies();
        new GenericCallLoweringPass(ctx.Registry, ctx.VariantBodies).RunOnVariantBodies();
        // Expand `is Crashable` clauses (synthesized by non-tail propagation in check_/lookup_
        // variants) into per-type TypePatterns before PatternLowering can lower them. No-op for
        // variant bodies that contain no CrashablePattern.
        new CrashableExpansionPass(ctx).RunOnVariantBodies();
        // PatternLowering runs before ExpressionLowering so that when-clauses with
        // ChainedComparison patterns are converted to IfStatement chains first, allowing
        // ExpressionLowering to correctly lower And/Or in the resulting if-conditions.
        new PatternLoweringPass(ctx).RunOnVariantBodies();
        new ExpressionLoweringPass(ctx).RunOnVariantBodies();
        // Second pass to fold NonePattern/None-TypePattern WhenStatements that
        // ExpressionLoweringPass synthesized for `??` / `?.`. PLP's lowering may
        // introduce UnaryExpression(Not), so re-run ELP afterwards.
        new PatternLoweringPass(ctx).RunOnVariantBodies();
        new ExpressionLoweringPass(ctx).RunOnVariantBodies();
        new FStringLoweringPass(ctx).RunOnVariantBodies();
        new OperatorLoweringPass(ctx).RunOnVariantBodies();
        // See the per-program Run(): rewrite the Roamed raw_inner() projection into a real AST call
        // after operator/f-string lowering so it is visible in synthesized variant bodies too.
        new RoamedProjectionLoweringPass(ctx).RunOnVariantBodies();
        // See per-program Run(): move the spawn-boundary promote() into a real AST call in
        // synthesized variant bodies too.
        new RoamedSpawnPromotionLoweringPass(ctx).RunOnVariantBodies();
        new RecordCopyLoweringPass(ctx).RunOnVariantBodies();
        // See per-program Run(): move the RC-wrapper copy-verb bump into a real AST call in
        // synthesized variant bodies too.
        new RcRetainLoweringPass(ctx).RunOnVariantBodies();
        // See per-program Run(): move the Roamed field-access lock bracket into real AST calls in
        // synthesized variant bodies too.
        new RoamedLockBracketLoweringPass(ctx).RunOnVariantBodies();
        new UsingLoweringPass(ctx).RunOnVariantBodies();
        // CallOverloadResolutionPass runs last so it sees all CallExpression nodes introduced
        // by FStringLoweringPass (represent/diagnose/add), OperatorLoweringPass (wired ops),
        // and RecordCopyLoweringPass (store). Also classifies synthesized derived-operator bodies
        // (ne/lt/le/gt/ge/notcontains) which bypass per-program Run() entirely.
        new CallOverloadResolutionPass(ctx).RunOnVariantBodies();
        new CallOverloadResolutionPass(ctx).RunOnSynthesizedBodies();

        foreach ((Program program, _, _) in ctx.Registry.StdlibPrograms)
            Run(program);
    }
}
