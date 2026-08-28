using System.Collections.Generic;
using System.Linq;
using Compiler.Resolution;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Postprocessing.Passes;

/// <summary>
/// Suflae <c>Roamed[E]</c> receiver-transparency lowering. A Suflae local promoted to
/// <c>Roamed[E]</c> (post-SA) reaches its inner value's memberRoutines through the wrapper handle:
/// operator-lowered calls (<c>x in d</c> -> <c>d.contains(x)</c>, <c>d[i]</c> -> <c>d.getitem(i)</c>,
/// <c>==</c>/<c>&lt;</c>) and f-string <c>represent</c>/<c>diagnose</c> arrive holding the
/// <c>RoamController</c> handle.
///
/// <para>This pass makes the transparency a REAL AST rewrite instead of a codegen-inserted call:
/// for every <see cref="CallExpression"/> whose callee is a <see cref="MemberExpression"/> on a
/// <c>Roamed[E]</c> receiver, <see cref="RoamedTransparency.Project"/> is the single decision point.
/// When it applies it (a) re-resolves a wrapper-shadowed <c>represent</c>/<c>diagnose</c> to the
/// inner value's routine (stamped onto <see cref="CallExpression.ResolvedRoutine"/>), and (b) for a
/// bare-<c>me</c> inner memberRoutine, rewrites the receiver to <c>receiver.raw_inner()</c> — projecting the
/// controller handle to the real inner pointer. Codegen then emits the already-projected,
/// already-inner-resolved call verbatim.</para>
///
/// <para>Runs after <see cref="OperatorLoweringPass"/> and <see cref="FStringLoweringPass"/> so the
/// operator/f-string-lowered Roamed calls are visible. Idempotent: an already-projected receiver is
/// inner-typed (not Roamed) so <c>Project</c> returns null on a second visit.</para>
/// </summary>
internal sealed class RoamedProjectionLoweringPass(PostprocessingContext ctx)
{
    private TypeRegistry Registry => ctx.Registry;

    /// <summary>Lowers Roamed receiver projections across a whole program.</summary>
    public void Run(Program program)
    {
        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[i])
            {
                case RoutineDeclaration r:
                    Statement nb = LowerStatement(r.Body);
                    if (!ReferenceEquals(nb, r.Body)) program.Declarations[i] = r with { Body = nb };
                    break;
                case EntityDeclaration e:
                    LowerMemberList(e.Members);
                    break;
                case RecordDeclaration rec:
                    LowerMemberList(rec.Members);
                    break;
                case CrashableDeclaration cr:
                    LowerMemberList(cr.Members);
                    break;
            }
        }
    }

    /// <summary>Lowers Roamed receiver projections in synthesized variant bodies.</summary>
    public void RunOnVariantBodies()
    {
        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            Statement body = ctx.VariantBodies[key];
            Statement lowered = LowerStatement(body);
            if (!ReferenceEquals(lowered, body)) ctx.VariantBodies[key] = lowered;
        }
    }

    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i] is not RoutineDeclaration mr) continue;
            Statement nb = LowerStatement(mr.Body);
            if (!ReferenceEquals(nb, mr.Body)) members[i] = mr with { Body = nb };
        }
    }

    // ---- Statements -----------------------------------------------------------------------------

    private Statement LowerStatement(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement block:
                return LowerBlock(block);
            case DeclarationStatement { Declaration: VariableDeclaration { Initializer: not null } vd } ds:
                return RebuildDecl(ds, vd, LowerExpression(vd.Initializer));
            case AssignmentStatement assign:
                return Rebuilt(assign.Value, LowerExpression(assign.Value), v => assign with { Value = v }, stmt);
            case ReturnStatement { Value: not null } ret:
                return Rebuilt(ret.Value, LowerExpression(ret.Value), v => ret with { Value = v }, stmt);
            case VariantReturnStatement { Value: not null } vrs:
                return Rebuilt(vrs.Value, LowerExpression(vrs.Value), v => vrs with { Value = v }, stmt);
            case ExpressionStatement es:
                return Rebuilt(es.Expression, LowerExpression(es.Expression), v => es with { Expression = v }, stmt);
            case DiscardStatement dis:
                return Rebuilt(dis.Expression, LowerExpression(dis.Expression), v => dis with { Expression = v }, stmt);
            case IfStatement ifs:
                return LowerIf(ifs);
            case WhileStatement w:
                return LowerWhile(w);
            case LoopStatement loop:
                return RebuiltBody(loop.Body, LowerStatement(loop.Body), b => loop with { Body = b }, stmt);
            case EachStatement f:
                return RebuiltBody(f.Body, LowerStatement(f.Body), b => f with { Body = b }, stmt);
            case WhenStatement whenStmt:
                return LowerWhen(whenStmt);
            case UsingStatement u:
                return LowerUsing(u);
            case DangerStatement d:
                return RebuiltBody(d.Body, LowerStatement(d.Body), b => d with { Body = (BlockStatement)b }, stmt);
            default:
                return stmt;
        }
    }

    private Statement LowerBlock(BlockStatement block)
    {
        bool changed = false;
        var list = new List<Statement>(capacity: block.Statements.Count);
        foreach (Statement s in block.Statements)
        {
            Statement ns = LowerStatement(s);
            list.Add(ns);
            if (!ReferenceEquals(ns, s)) changed = true;
        }
        return changed ? block with { Statements = list } : block;
    }

    private Statement LowerIf(IfStatement ifs)
    {
        Expression c = LowerExpression(ifs.Condition);
        Statement t = LowerStatement(ifs.ThenStatement);
        Statement? el = ifs.ElseStatement != null ? LowerStatement(ifs.ElseStatement) : null;
        return !ReferenceEquals(c, ifs.Condition) || !ReferenceEquals(t, ifs.ThenStatement)
               || !ReferenceEquals(el, ifs.ElseStatement)
            ? ifs with { Condition = c, ThenStatement = t, ElseStatement = el }
            : ifs;
    }

    private Statement LowerWhile(WhileStatement w)
    {
        Expression c = LowerExpression(w.Condition);
        Statement b = LowerStatement(w.Body);
        return !ReferenceEquals(c, w.Condition) || !ReferenceEquals(b, w.Body)
            ? w with { Condition = c, Body = b }
            : w;
    }

    private Statement LowerWhen(WhenStatement whenStmt)
    {
        bool changed = false;
        Expression subj = LowerExpression(whenStmt.Expression);
        if (!ReferenceEquals(subj, whenStmt.Expression)) changed = true;
        var clauses = new List<WhenClause>(capacity: whenStmt.Clauses.Count);
        foreach (WhenClause cl in whenStmt.Clauses)
        {
            Statement nb = LowerStatement(cl.Body);
            clauses.Add(ReferenceEquals(nb, cl.Body) ? cl : cl with { Body = nb });
            if (!ReferenceEquals(nb, cl.Body)) changed = true;
        }
        return changed ? whenStmt with { Expression = subj, Clauses = clauses } : whenStmt;
    }

    private Statement LowerUsing(UsingStatement u)
    {
        Statement b = LowerStatement(u.Body);
        Statement? fb = u.FallbackBody != null ? LowerStatement(u.FallbackBody) : null;
        return !ReferenceEquals(b, u.Body) || !ReferenceEquals(fb, u.FallbackBody)
            ? u with { Body = b, FallbackBody = fb }
            : u;
    }

    private static Statement Rebuilt(Expression old, Expression low,
        System.Func<Expression, Statement> build, Statement original) =>
        ReferenceEquals(low, old) ? original : build(low);

    private static Statement RebuiltBody(Statement old, Statement low,
        System.Func<Statement, Statement> build, Statement original) =>
        ReferenceEquals(low, old) ? original : build(low);

    private static Statement RebuildDecl(DeclarationStatement ds, VariableDeclaration vd, Expression low) =>
        ReferenceEquals(low, vd.Initializer) ? ds : ds with { Declaration = vd with { Initializer = low } };

    // ---- Expressions ----------------------------------------------------------------------------

    private Expression LowerExpression(Expression expr)
    {
        switch (expr)
        {
            case CallExpression call:
                return LowerCall(call);
            case MemberExpression m:
                return Rebuild(m.Object, LowerExpression(m.Object), o => m with { Object = o }, m);
            case BinaryExpression bin:
                return LowerBinary(bin);
            case UnaryExpression un:
                return Rebuild(un.Operand, LowerExpression(un.Operand), o => un with { Operand = o }, un);
            case NamedArgumentExpression na:
                return Rebuild(na.Value, LowerExpression(na.Value), v => na with { Value = v }, na);
            case InsertedTextExpression fstr:
                return LowerFString(fstr);
            default:
                return expr;
        }
    }

    private Expression LowerBinary(BinaryExpression bin)
    {
        Expression l = LowerExpression(bin.Left);
        Expression r = LowerExpression(bin.Right);
        return !ReferenceEquals(l, bin.Left) || !ReferenceEquals(r, bin.Right)
            ? bin with { Left = l, Right = r }
            : bin;
    }

    private Expression LowerFString(InsertedTextExpression fstr)
    {
        bool changed = false;
        var parts = new List<InsertedTextPart>(capacity: fstr.Parts.Count);
        foreach (InsertedTextPart part in fstr.Parts)
        {
            if (part is ExpressionPart ep)
            {
                Expression ne = LowerExpression(ep.Expression);
                parts.Add(ReferenceEquals(ne, ep.Expression) ? ep : ep with { Expression = ne });
                if (!ReferenceEquals(ne, ep.Expression)) changed = true;
            }
            else { parts.Add(part); }
        }
        return changed ? fstr with { Parts = parts } : fstr;
    }

    // Recurse into the call's parts, then apply the Roamed transparency projection when the callee is
    // a member call on a Roamed[E] receiver.
    private Expression LowerCall(CallExpression call)
    {
        Expression callee = LowerExpression(call.Callee);
        var args = new List<Expression>(capacity: call.Arguments.Count);
        bool changed = !ReferenceEquals(callee, call.Callee);
        foreach (Expression a in call.Arguments)
        {
            Expression na = LowerExpression(a);
            args.Add(na);
            if (!ReferenceEquals(na, a)) changed = true;
        }
        CallExpression lowered = changed ? call with { Callee = callee, Arguments = args } : call;
        return ProjectRoamedReceiver(lowered);
    }

    // The core rewrite: when the callee is a member call on a Roamed[E] receiver and
    // RoamedTransparency.Project applies, stamp the inner memberRoutine as ResolvedRoutine and — for a
    // bare-`me` inner memberRoutine — rewrite the receiver to `receiver.raw_inner()`.
    private CallExpression ProjectRoamedReceiver(CallExpression call)
    {
        if (call.Callee is not MemberExpression member) return call;
        TypeInfo? receiverType = member.Object.ResolvedType;
        if (receiverType is null) return call;

        // The initial memberRoutine: the SA/operator-stamped routine, or (when unresolved on a Roamed
        // receiver — an operator-lowered `d[i]`/`x in d` whose owner has no such memberRoutine) a transparent
        // lookup on the wrapper, matching the codegen path this pass replaces.
        RoutineInfo? memberRoutine = call.ResolvedRoutine
            ?? Registry.LookupMemberRoutine(type: receiverType, memberRoutineName: member.MemberName);

        RoamedTransparency.Projection? proj = RoamedTransparency.Project(receiverType: receiverType,
            memberRoutine: memberRoutine, memberName: member.MemberName, registry: Registry);
        if (proj is not { } roamProj) return call;

        // Stamp the inner memberRoutine (represent/diagnose shadowed by the wrapper → inner's; a null-stamped
        // operator call → the transparently-resolved inner memberRoutine) so codegen emits it directly.
        call.ResolvedRoutine = roamProj.MemberRoutine;
        call.LoweringKind = Verification.CallClassifier.ClassifyMemberRoutineCall(memberRoutine: roamProj.MemberRoutine);
        if (!roamProj.ProjectToInner) return call;

        Expression innerRecv = MakeControlCall(member.Object, receiverType, roamProj.InnerType);
        if (ReferenceEquals(innerRecv, member.Object)) return call;
        return call with { Callee = member with { Object = innerRecv } };
    }

    // Build `receiver.control()` : the inner entity, via the Controlling marker-protocol deref (Roamed
    // obeys Controlling[T]). Stamps the resolved control routine and inner type so codegen emits it as a
    // real, already-resolved call. Reachability seeds control via ImplicitCallContract.ForLiveType, so
    // the target is live/monomorphized. The access lock is applied around the enclosing statement by
    // RoamedLockBracketLoweringPass (which recognizes this control() coercion), so the deref is safe.
    private Expression MakeControlCall(Expression receiver, TypeInfo receiverType, TypeInfo innerType)
    {
        RoutineInfo? control = Registry.LookupMemberRoutine(type: receiverType,
            memberRoutineName: RuntimeContract.Control);
        if (control is null) return receiver;

        var callee = new MemberExpression(Object: receiver,
            MemberName: RuntimeContract.Control, Location: receiver.Location)
        {
            ResolvedType = innerType
        };
        return new CallExpression(Callee: callee, Arguments: new List<Expression>(),
            Location: receiver.Location)
        {
            ResolvedRoutine = control,
            ResolvedType = innerType
        };
    }

    private static Expression Rebuild(Expression old, Expression low,
        System.Func<Expression, Expression> build, Expression original) =>
        ReferenceEquals(low, old) ? original : build(low);
}
