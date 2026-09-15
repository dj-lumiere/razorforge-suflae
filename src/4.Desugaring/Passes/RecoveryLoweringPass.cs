using Builder.Lowering;
using SyntaxTree;

namespace Builder.Desugaring.Passes;

/// <summary>
/// Lowers a <c>try</c>/<c>grab</c>/<c>lookup</c> <see cref="RecoveryExpression"/> to a call to the matching
/// compiler-generated recovery variant, by prepending the variant prefix to the wrapped call's callee name:
/// <list type="bullet">
///   <item><c>try foo(a)</c> → <c>try_foo(a)</c> (Maybe[T])</item>
///   <item><c>grab x.foo()</c> → <c>x.check_foo()</c> (Check[T])</item>
///   <item><c>lookup foo()</c> → <c>lookup_foo()</c> (Lookup[T])</item>
/// </list>
/// Runs PRE-analysis (in the <see cref="DesugaringPipeline"/>, before <see cref="ErrorHandlingVariantPass"/>
/// registers the variants) so the variant name resolves in SA. The recovery variant is total (returns a
/// carrier), so the wrapped call's failable <c>!</c> marker is cleared.
///
/// <para>MILESTONE: single wrapped call only — the outermost call of the wrapped expression is redirected to
/// its variant. Whole-expression monadic short-circuit composition (<c>try a(b(c()))</c> recovering every
/// nested failable call) is a later SA-aware lowering; the parser already guarantees the inner is a call.</para>
/// </summary>
internal sealed class RecoveryLoweringPass : AstRewriter
{
    /// <summary>Rewrites every recovery expression in every routine/member body of <paramref name="program"/>.</summary>
    public void Run(Program program)
    {
        BodyDispatch.RunOnProgram(program: program,
            lower: r => VisitStatement(stmt: r.Body));
    }

    /// <inheritdoc/>
    protected override Expression VisitRecovery(RecoveryExpression e)
    {
        // Lower any nested recovery first, then redirect the (guaranteed-call) inner to its variant.
        Expression inner = VisitExpression(expr: e.Inner);
        string variantPrefix = e.Kind switch
        {
            RecoveryKind.Grab => "check_",
            RecoveryKind.Lookup => "lookup_",
            _ => "try_"
        };

        return inner switch
        {
            CallExpression { Callee: IdentifierExpression id } freeCall => freeCall with
            {
                Callee = id with { Name = variantPrefix + id.Name },
                IsFailable = false
            },
            CallExpression { Callee: MemberExpression member } memberCall => memberCall with
            {
                Callee = member with { MemberName = variantPrefix + member.MemberName },
                IsFailable = false
            },
            // Parser guarantees a CallExpression; anything else means the inner lowered to a non-call
            // (e.g. a nested recovery already redirected) — leave it for SA to reject.
            _ => e with { Inner = inner }
        };
    }
}
