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
        new StructuralLoweringPass(ctx).Run(program);
        new ExpressionLoweringPass(ctx).Run(program);
        new FStringLoweringPass(ctx).Run(program);
        new OperatorLoweringPass(ctx).Run(program);
        new CrashableExpansionPass(ctx).Run(program);
        new PatternLoweringPass(ctx).Run(program);
        new RecordCopyLoweringPass(ctx).Run(program);
        new BecomesLoweringPass(ctx).Run(program);
        new LambdaLiftingPass(ctx).Run(program);
        new AsyncLoweringPass(ctx).Run(program);
    }

    /// <summary>
    /// Runs Phase 7 lowering on variant bodies produced by Phase 4 synthesis,
    /// and on stdlib programs that bypass per-file <see cref="Run"/>.
    /// Must be called after <see cref="Run"/> has been applied to all user programs.
    /// </summary>
    public void RunGlobal()
    {
        new ExpressionLoweringPass(ctx).RunOnVariantBodies();
        new OperatorLoweringPass(ctx).RunOnVariantBodies();
        new RecordCopyLoweringPass(ctx).RunOnVariantBodies();

        foreach ((Program program, _, _) in ctx.Registry.StdlibPrograms)
            Run(program);
    }
}