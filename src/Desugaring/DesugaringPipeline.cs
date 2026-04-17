using Compiler.Synthesis;
using Compiler.Desugaring.Passes;
using SyntaxTree;

namespace Compiler.Desugaring;

/// <summary>
/// Phase 6 — Postprocessing. Orchestrates all type-aware lowering passes.
/// Called after Phase 5 (Verification) has set <c>ResolvedType</c> on all expressions
/// and validated exhaustiveness against the original type structure.
/// Passes run in order; each transforms the program AST in-place.
/// </summary>
public sealed class DesugaringPipeline(DesugaringContext ctx)
{
    /// <summary>
    /// Runs per-file desugaring passes (called once per source file).
    /// Pass order:
    /// <list type="number">
    ///   <item><see cref="BlankReturnNormalizationPass"/> — fills null return types and bare returns with <c>Blank</c>.</item>
    ///   <item><see cref="PresetInliningPass"/> — substitutes preset identifiers with their literal values.</item>
    ///   <item><see cref="StructuralLoweringPass"/> — lowers choice/flags/variant/crashable (Step 3, stub).</item>
    ///   <item><see cref="ControlFlowLoweringPass"/> — lowers iterator for-loops to while+when.</item>
    ///   <item><see cref="ExpressionLoweringPass"/> — lowers <c>??</c>, <c>?.</c>, chained comparisons,
    ///         compound assignments, flags tests, and range expressions. (<c>!!</c> is handled by
    ///         <see cref="OperatorLoweringPass"/> so stdlib bodies are also covered.)</item>
    ///   <item><see cref="FStringLoweringPass"/> — lowers <see cref="InsertedTextExpression"/> f-strings to
    ///         <c>$represent</c>/<c>$diagnose</c> + <c>Text.$add</c> chains.</item>
    ///   <item><see cref="OperatorLoweringPass"/> — lowers <c>IndexExpression</c> → <c>$getitem!</c>,
    ///         <c>SliceExpression</c> → <c>$getslice</c>, <c>GenericMemberExpression</c> → member + index.</item>
    ///   <item><see cref="GenericCallLoweringPass"/> — lowers <c>GenericMethodCallExpression</c> →
    ///         <c>CallExpression</c> where <c>ResolvedRoutine != null</c> and not an LLVM intrinsic.</item>
    ///   <item><see cref="CrashableExpansionPass"/> — expands <c>is Crashable e</c> in Result/Lookup whens to per-type clauses.
    ///         Runs after <see cref="RunGlobal"/> so <c>ThrowableTypes</c> are populated.</item>
    ///   <item><see cref="PatternLoweringPass"/> — lowers simple-pattern WhenStatements to if/else chains.</item>
    ///   <item><see cref="RecordCopyLoweringPass"/> — strips <c>steal</c> and injects <c>$copy()</c> for record assignments.</item>
    ///   <item><see cref="BuilderServiceInliningPass"/> — folds compile-time-constant BuilderService calls
    ///         (data_size, type_id, type_name, etc.) to literal expressions.</item>
    /// </list>
    /// </summary>
    public void Run(Program program)
    {
        new BlankReturnNormalizationPass(ctx).Run(program);
        new PresetInliningPass(ctx).Run(program);
        new StructuralLoweringPass(ctx).Run(program);
        new ControlFlowLoweringPass(ctx).Run(program);
        new ExpressionLoweringPass(ctx).Run(program);
        new FStringLoweringPass(ctx).Run(program);
        new OperatorLoweringPass(ctx).Run(program);
        new GenericCallLoweringPass(ctx).Run(program);
        new CrashableExpansionPass(ctx).Run(program);
        new PatternLoweringPass(ctx).Run(program);
        new RecordCopyLoweringPass(ctx).Run(program);
        new BuilderServiceInliningPass(ctx).Run(program);
    }

    /// <summary>
    /// Runs global desugaring passes (called once after all per-file Phase 5 analysis is done).
    /// These passes operate on the whole registry rather than a single file's AST.
    /// </summary>
    public void RunGlobal()
    {
        new ErrorHandlingVariantPass(ctx).RunGlobal();
        new WiredRoutinePass(ctx).RunGlobal();
        ctx.Registry.PruneUnusedGenericRoutines();

        // Also evict variant bodies for pruned generic routines so they don't appear in the dump.
        // RegistryKey format: "BaseName#ParamTypes" — base is everything before the first '#'.
        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            string baseName = key.Contains(value: '#') ? key[..key.IndexOf(value: '#')] : key;
            if (ctx.Registry.IsRoutinePruned(baseName: baseName))
                ctx.VariantBodies.Remove(key: key);
        }

        new PresetInliningPass(ctx).RunOnVariantBodies();
        new ControlFlowLoweringPass(ctx).RunOnVariantBodies();
        new ExpressionLoweringPass(ctx).RunOnVariantBodies();
        new OperatorLoweringPass(ctx).RunOnVariantBodies();
        new RecordCopyLoweringPass(ctx).RunOnVariantBodies();
        new BuilderServiceInliningPass(ctx).RunOnVariantBodies();

        // Stdlib programs bypass per-file desugaring (they are pre-loaded by StdlibLoader).
        // Stdlib bodies are now type-annotated by SemanticAnalyzer.AnalyzeStdlibBodies(), so
        // we can run the full type-aware lowering pipeline on them here — same set of passes
        // as DesugaringPipeline.Run(program), but orchestrated globally because stdlib programs
        // aren't processed by the per-file driver.
        foreach ((Program program, _, _) in ctx.Registry.StdlibPrograms)
        {
            new BlankReturnNormalizationPass(ctx).Run(program);
            new PresetInliningPass(ctx).Run(program);
            new StructuralLoweringPass(ctx).Run(program);
            new ControlFlowLoweringPass(ctx).Run(program);
            new ExpressionLoweringPass(ctx).Run(program);
            new FStringLoweringPass(ctx).Run(program);
            new OperatorLoweringPass(ctx).Run(program);
            new GenericCallLoweringPass(ctx).Run(program);
            new CrashableExpansionPass(ctx).Run(program);
            new PatternLoweringPass(ctx).Run(program);
            new RecordCopyLoweringPass(ctx).Run(program);
            new BuilderServiceInliningPass(ctx).Run(program);
        }

        // Pre-rewrite all generic method bodies for known concrete instantiations.
        // Runs after stdlib OperatorLoweringPass so bodies have IndexExpression lowered
        // to $getitem! calls before rewriting. Also runs after WiredRoutinePass so
        // $represent/$diagnose bodies are available in VariantBodies.
        new GenericMonomorphizationPass(ctx).RunGlobal();

        // Fold any remaining BuilderService constant calls in pre-monomorphized bodies.
        // GenericAstRewriter folds T.data_size() during monomorphization substitution, but this
        // pass handles any cases where the receiver was already a concrete type name before GMP
        // ran (e.g., calls written as Byte.data_size() rather than T.data_size()), and provides
        // a unified second-pass sweep over all pre-built bodies.
        new BuilderServiceInliningPass(ctx).RunOnPreMonomorphizedBodies();
    }
}
