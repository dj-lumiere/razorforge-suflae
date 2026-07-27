using System.Collections.Generic;
using Compiler.Resolution;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Postprocessing.Passes;

/// <summary>
/// Suflae-only lowering: an SF <c>entity</c> is a <c>Roamed[E]</c> biased-refcounted handle, not a
/// bare single-owner entity. This pass runs at the START of Phase 6 (before
/// <see cref="Compiler.Instantiation.Passes.RoutineReachabilityPass"/>) so that once entity-typed
/// bindings carry a <c>Roamed[E]</c> resolved type, reachability seeds the roam/promote/lock/cycle
/// machinery off the live wrapper type (the existing <c>Roamed</c>-base seeding), and the Phase-7 RC
/// lifecycle passes (retain-on-copy, release-on-scope-exit) apply — with NO codegen-site changes.
///
/// <para>STAGE 1 (this cut): construction + aliasing + scope-exit release. For every SF construction
/// <c>E(...)</c> (a <see cref="CreatorExpression"/> whose resolved type is a bare
/// <see cref="EntityTypeInfo"/>) the value is rewritten to <c>E(...).roam()</c> and retyped to
/// <c>Roamed[E]</c>; locals bound to such a value (or aliased from one) are tracked so their
/// identifier references retype to <c>Roamed[E]</c>. The entity's own field layout and method
/// receivers (<c>me</c>) stay bare <c>E</c> — the controller is peeled by the wrapper forwarder.
/// Lock-wrapped access, promote-at-boundary, and cycle collection ride on later stages.</para>
/// </summary>
internal sealed class SuflaeEntityLoweringPass
{
    private readonly TypeRegistry _registry;

    // Per-routine scope: local name -> the Roamed[E] type it now carries. Reset per routine body.
    private readonly Dictionary<string, WrapperTypeInfo> _roamedLocals = new();

    public SuflaeEntityLoweringPass(TypeRegistry registry)
    {
        _registry = registry;
    }

    public void Run(Program program)
    {
        if (_registry.Language != Language.Suflae) return;

        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[i])
            {
                case RoutineDeclaration r:
                    program.Declarations[i] = LowerRoutine(r);
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

    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int i = 0; i < members.Count; i++)
            if (members[i] is RoutineDeclaration mr)
                members[i] = LowerRoutine(mr);
    }

    private RoutineDeclaration LowerRoutine(RoutineDeclaration r)
    {
        _roamedLocals.Clear();
        Statement newBody = LowerStatement(r.Body);
        return ReferenceEquals(newBody, r.Body) ? r : r with { Body = newBody };
    }

    // ---- Statements -----------------------------------------------------------------------------

    private Statement LowerStatement(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement block:
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

            case DeclarationStatement { Declaration: VariableDeclaration { Initializer: not null } vd } ds:
            {
                Expression init = MaybeRoamCopy(LowerExpression(vd.Initializer));
                // Track the local as Roamed[E] when its initializer resolved to a Roamed wrapper, so
                // later references (aliasing / access) retype consistently. `var` locals infer their
                // type from the initializer at codegen, so no declared-type rewrite is needed here.
                if (init.ResolvedType is WrapperTypeInfo { Name: RuntimeContract.Roamed } w)
                    _roamedLocals[vd.Name] = w;
                return ReferenceEquals(init, vd.Initializer)
                    ? stmt
                    : ds with { Declaration = vd with { Initializer = init } };
            }

            case AssignmentStatement assign:
            {
                Expression v = MaybeRoamCopy(LowerExpression(assign.Value));
                return ReferenceEquals(v, assign.Value) ? stmt : assign with { Value = v };
            }

            case ReturnStatement { Value: not null } ret:
            {
                Expression v = LowerExpression(ret.Value);
                return ReferenceEquals(v, ret.Value) ? stmt : ret with { Value = v };
            }

            case ExpressionStatement es:
            {
                Expression e = LowerExpression(es.Expression);
                return ReferenceEquals(e, es.Expression) ? stmt : es with { Expression = e };
            }

            case DiscardStatement dis:
            {
                Expression e = LowerExpression(dis.Expression);
                return ReferenceEquals(e, dis.Expression) ? stmt : dis with { Expression = e };
            }

            case IfStatement ifs:
            {
                Expression c = LowerExpression(ifs.Condition);
                Statement t = LowerStatement(ifs.ThenStatement);
                Statement? el = ifs.ElseStatement != null ? LowerStatement(ifs.ElseStatement) : null;
                return !ReferenceEquals(c, ifs.Condition) || !ReferenceEquals(t, ifs.ThenStatement)
                       || !ReferenceEquals(el, ifs.ElseStatement)
                    ? ifs with { Condition = c, ThenStatement = t, ElseStatement = el }
                    : stmt;
            }

            case WhileStatement w:
            {
                Expression c = LowerExpression(w.Condition);
                Statement b = LowerStatement(w.Body);
                return !ReferenceEquals(c, w.Condition) || !ReferenceEquals(b, w.Body)
                    ? w with { Condition = c, Body = b }
                    : stmt;
            }

            case LoopStatement loop:
            {
                Statement b = LowerStatement(loop.Body);
                return ReferenceEquals(b, loop.Body) ? stmt : loop with { Body = b };
            }

            case ForStatement f:
            {
                Statement b = LowerStatement(f.Body);
                return ReferenceEquals(b, f.Body) ? stmt : f with { Body = b };
            }

            case WhenStatement whenStmt:
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
                return changed ? whenStmt with { Expression = subj, Clauses = clauses } : stmt;
            }

            case UsingStatement u:
            {
                Statement b = LowerStatement(u.Body);
                Statement? fb = u.FallbackBody != null ? LowerStatement(u.FallbackBody) : null;
                return !ReferenceEquals(b, u.Body) || !ReferenceEquals(fb, u.FallbackBody)
                    ? u with { Body = b, FallbackBody = fb }
                    : stmt;
            }

            case DangerStatement d:
            {
                Statement b = LowerStatement(d.Body);
                return ReferenceEquals(b, d.Body) ? stmt : d with { Body = (BlockStatement)b };
            }

            default:
                return stmt;
        }
    }

    // ---- Expressions ----------------------------------------------------------------------------

    private Expression LowerExpression(Expression expr)
    {
        switch (expr)
        {
            // A creator that yields a bare SF entity -> `.roam()` : Roamed[E].
            case CreatorExpression creator when creator.ResolvedType is EntityTypeInfo ce:
                return WrapInRoam(inner: creator, entity: ce);

            // Reference to a local we've retyped to Roamed[E] -> flip its resolved type so aliasing
            // and access see the wrapper.
            case IdentifierExpression id
                when _roamedLocals.TryGetValue(id.Name, out WrapperTypeInfo? w)
                     && id.ResolvedType is EntityTypeInfo:
            {
                id.ResolvedType = w;
                return id;
            }

            case MemberExpression m:
            {
                Expression obj = LowerExpression(m.Object);
                return ReferenceEquals(obj, m.Object) ? m : m with { Object = obj };
            }

            // A call — INCLUDING a constructor call `E(...)`, which is a CallExpression (not a
            // CreatorExpression) at this phase — that produces a bare SF entity: recurse into its
            // parts, then `.roam()` the whole value.
            case CallExpression call:
            {
                Expression callee = LowerExpression(call.Callee);
                bool changed = !ReferenceEquals(callee, call.Callee);
                var args = new List<Expression>(capacity: call.Arguments.Count);
                foreach (Expression a in call.Arguments)
                {
                    Expression na = LowerExpression(a);
                    args.Add(na);
                    if (!ReferenceEquals(na, a)) changed = true;
                }
                // A construction `E(...)` stores its args into fields; a borrowed Roamed arg going into a
                // Roamed field must retain (else the field + the source local both release → double free).
                if (call.ResolvedType is EntityTypeInfo)
                {
                    for (int k = 0; k < args.Count; k++) args[k] = RetainConstructionArg(args[k]);
                    changed = true;
                }

                CallExpression lowered = changed ? call with { Callee = callee, Arguments = args } : call;

                // Method call whose receiver became `Roamed[E]`: SA baked the bare-entity method with the
                // Roamed handle as `me` (so `me.field` reads the RoamController). INTERIM FIX (LOCAL-only,
                // lock-SKIPPING): project the receiver via `.raw_inner()` so the bare method gets the real
                // entity. The proper fix is the Roamed[E] forwarder (lock_enter+project+call), which is
                // SA-synthesized — replace this once method calls resolve against Roamed[E] in SA.
                if (callee is MemberExpression meCall
                    && meCall.Object.ResolvedType is WrapperTypeInfo { Name: RuntimeContract.Roamed } roamedRecv
                    && lowered.ResolvedRoutine is { OwnerType: EntityTypeInfo })
                {
                    var rawInner = new CallExpression(
                        Callee: new MemberExpression(Object: meCall.Object, PropertyName: "raw_inner",
                            Location: meCall.Location) { ResolvedType = roamedRecv.InnerType },
                        Arguments: new List<Expression>(),
                        Location: meCall.Location) { ResolvedType = roamedRecv.InnerType };
                    return lowered with { Callee = meCall with { Object = rawInner } };
                }

                return call.ResolvedType is EntityTypeInfo callEntity
                    ? WrapInRoam(inner: lowered, entity: callEntity)
                    : lowered;
            }

            // f-string: recurse into each embedded `{ expr }` so entity references inside it retype
            // (else e.g. `f"{b.size}"` reads `b` as a bare entity — actually the RoamController — and
            // returns the refcount instead of the field).
            case InsertedTextExpression fstr:
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

            case BinaryExpression bin:
            {
                Expression l = LowerExpression(bin.Left);
                Expression r = LowerExpression(bin.Right);
                return !ReferenceEquals(l, bin.Left) || !ReferenceEquals(r, bin.Right)
                    ? bin with { Left = l, Right = r }
                    : bin;
            }

            case UnaryExpression un:
            {
                Expression o = LowerExpression(un.Operand);
                return ReferenceEquals(o, un.Operand) ? un : un with { Operand = o };
            }

            case NamedArgumentExpression namedArg:
            {
                Expression v = LowerExpression(namedArg.Value);
                return ReferenceEquals(v, namedArg.Value) ? namedArg : namedArg with { Value = v };
            }

            default:
                return expr;
        }
    }

    // A construction arg (a `NamedArgumentExpression` or bare value) whose value is a borrowed Roamed
    // reference must retain — it is stored into a Roamed field which the constructed entity now co-owns.
    private Expression RetainConstructionArg(Expression arg)
    {
        if (arg is NamedArgumentExpression na)
        {
            Expression v = MaybeRoamCopy(na.Value);
            return ReferenceEquals(v, na.Value) ? na : na with { Value = v };
        }
        return MaybeRoamCopy(arg);
    }

    // A borrowed reference (identifier / field read) to a Roamed value in a COPY position (var-init or
    // assignment RHS) must retain — bump the biased refcount via `.roam()` — so the new binding owns its
    // own reference; otherwise the shared controller is released twice (double free). Fresh values (a
    // construct `E(...).roam()`, a call) are already owned and are left alone.
    private Expression MaybeRoamCopy(Expression expr)
    {
        if (expr is (IdentifierExpression or MemberExpression)
            && expr.ResolvedType is WrapperTypeInfo { Name: RuntimeContract.Roamed } roamed)
        {
            return new CallExpression(
                Callee: new MemberExpression(Object: expr, PropertyName: RuntimeContract.RefCount.Roam,
                    Location: expr.Location) { ResolvedType = roamed },
                Arguments: new List<Expression>(),
                Location: expr.Location) { ResolvedType = roamed };
        }
        return expr;
    }

    // Wrap a bare-entity-valued expression in `<expr>.roam()`, retyped to Roamed[E].
    private Expression WrapInRoam(Expression inner, EntityTypeInfo entity)
    {
        WrapperTypeInfo roamed = _registry.GetOrCreateWrapperType(
            wrapperName: RuntimeContract.Roamed, innerType: entity, isReadOnly: false);
        return new CallExpression(
            Callee: new MemberExpression(Object: inner, PropertyName: RuntimeContract.RefCount.Roam,
                Location: inner.Location) { ResolvedType = roamed },
            Arguments: new List<Expression>(),
            Location: inner.Location) { ResolvedType = roamed };
    }
}
