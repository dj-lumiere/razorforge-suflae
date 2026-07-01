using System.Collections.Generic;
using System.Linq;
using Compiler.Postprocessing;
using Compiler.Resolution;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using Verification;

namespace Compiler.Postprocessing.Passes;

/// <summary>
/// RAII teardown for owned <b>rvalue temporaries</b> — the heap-owning intermediate values that
/// <see cref="ScopeTeardownLoweringPass"/> cannot reach because it only tracks <i>named</i> bindings.
///
/// <para>Consider <c>x.$represent().count()</c>: <c>x.$represent()</c> mints a fresh owned
/// <c>Text</c> (an RC-record holding a heap buffer), <c>.count()</c> borrows it, and the <c>Text</c>
/// is then dropped on the floor — never destroyed. Inside a hot loop that leaks one buffer per
/// iteration. This pass spills such a temporary into a synthetic block-scoped local and emits its
/// <c>$destroy()</c> at end-of-statement, so the buffer is freed each time the statement runs.</para>
///
/// <para><b>Why a separate, late pass.</b> <see cref="ScopeTeardownLoweringPass"/> runs before
/// reachability and before user-code Phase 7 lowering — at that point a <c>var u = when … =>
/// x.$represent().count()</c> is still a <c>when</c>-<i>expression</i>, so the producing call is
/// buried in conditional arms with no statement to attach teardown to. This pass runs AFTER Phase 7
/// (when→if already lowered, so arms are real blocks) and emits its own <c>$destroy</c> calls;
/// codegen's emit-on-demand picks up the referenced concrete <c>$destroy</c>.</para>
///
/// <para><b>What is spilled (deliberately narrow — correctness over coverage, never a double-free).</b>
/// Exactly one shape: the <b>receiver of a method call where (a) the receiver is a fresh RC-record
/// producer and (b) the call result is not a borrow/view wrapper</b> (so it cannot alias the
/// receiver). <c>retain</c>/<c>track</c> verbs (which consume the receiver) are excluded. This covers
/// both <c>x.$represent().count()</c> (scalar result) and the intermediate <c>Text</c> of a
/// concatenation chain <c>a + "-" + b</c> (each <c>$add</c> result is the receiver of the next).</para>
///
/// <para><b>Why this is safe.</b> An RC-record <c>$destroy</c> releases a <i>refcounted</i>
/// controller, so a balanced release is harmless. A method's record/RC-record return is always
/// <i>independent</i> of the receiver — freshly allocated (string concat builds a new buffer) or a
/// retaining +1 copy (<see cref="ScopeTeardownLoweringPass"/>'s sibling RecordCopyLoweringPass injects
/// <c>$copy</c> on <c>me</c>/lvalue returns) — so freeing the receiver leaves the result valid. The
/// only alias hazard is a borrow/view result pointing into the receiver, which the guard excludes.</para>
///
/// <para>Crucially NOT spilled (each a real double-free / leak-vs-crash hazard): call
/// <b>arguments</b> (an rvalue arg is moved into the callee), <b>discarded</b> statement values (a
/// fluent <c>me</c>-returning call aliases an existing binding), <b>entities</b> (single-owner
/// lifetime + fluent returns are not provably alias-free here), non-scalar results, <c>var</c>
/// initializers / assignment RHS / return values (owned by the binding or moved out), and
/// <c>steal</c> operands.</para>
///
/// <para><b>Why a separate, late pass.</b> <see cref="ScopeTeardownLoweringPass"/> runs before
/// reachability and before user-code Phase 7 lowering — at that point a <c>var u = when … =>
/// x.$represent().count()</c> is still a <c>when</c>-expression with the producing call buried in
/// conditional arms. This pass runs AFTER Phase 7 (when→if lowered, arms are real blocks) and emits
/// its own <c>$destroy</c> calls; codegen's emit-on-demand resolves the concrete <c>$destroy</c>.</para>
///
/// <para><b>Known limitations</b> (leak preserved, never a crash): argument-position temporaries,
/// non-scalar-returning chains, entity temporaries, bare field-access objects, and owned temporaries
/// inside loop/if conditions are not freed. User-defined generic routine bodies monomorphized before
/// this pass also miss it.</para>
/// </summary>
internal sealed class TemporaryTeardownPass(PostprocessingContext ctx)
{
    private readonly TypeInfo? _blankType = ctx.Registry.LookupType(name: "Blank");
    private int _counter;

    /// <summary>The reference primitives whose result is a borrow of a referent owned elsewhere —
    /// a temporary produced by one of these owns nothing, so it must not be torn down. Mirrors
    /// <see cref="ScopeTeardownLoweringPass"/>'s view-verb exclusion.</summary>
    private static readonly HashSet<string> ViewVerbs =
        new(comparer: System.StringComparer.Ordinal) { "as_entity", "$refer", "$control" };

    /// <summary>Method verbs that consume their receiver (ownership moves into the RC controller),
    /// so the receiver must NOT be torn down here.</summary>
    private static readonly HashSet<string> ConsumingReceiverVerbs =
        new(comparer: System.StringComparer.Ordinal) { "retain", "track" };

    /// <summary>Borrow/view wrapper names whose value points INTO another value, so a method
    /// returning one may alias its receiver — freeing the receiver would then dangle it. The owning
    /// RC wrappers (Retained/Tracked/Shared/Watched) are NOT here: they carry a refcounted controller,
    /// so an aliasing owned result is balanced by refcount.</summary>
    private static readonly HashSet<string> BorrowWrapperNames =
        new(comparer: System.StringComparer.Ordinal)
            { "Viewing", "Modifying", "Inspecting", "Claiming", "Hijacked" };

    private sealed record Spill(string Name, TypeInfo Type, RoutineInfo Destroy, Expression Init);

    public void Run(Program program)
    {
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

    /// <summary>Lowers free-standing bodies (variant / synthesized routine bodies) in place.</summary>
    public void RunOnBodies(Dictionary<string, Statement>? bodies)
    {
        if (bodies is null) return;
        foreach (string key in bodies.Keys.ToList())
            bodies[key] = TransformStatement(bodies[key]);
    }

    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int j = 0; j < members.Count; j++)
            if (members[j] is RoutineDeclaration m)
                members[j] = LowerRoutine(m);
    }

    private RoutineDeclaration LowerRoutine(RoutineDeclaration r)
    {
        Statement body = TransformStatement(r.Body);
        return r.Body == body ? r : r with { Body = body };
    }

    // ---------------------------------------------------------------------------------------------
    // Statement transform: descend into structure, spilling at leaf statements that bear expressions.
    // ---------------------------------------------------------------------------------------------

    private Statement TransformStatement(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement b:
            {
                List<Statement> stmts = b.Statements.Select(TransformStatement).ToList();
                return b with { Statements = stmts };
            }

            case IfStatement ifs:
            {
                // The condition is evaluated where the if sits (and re-evaluated each iteration when
                // the if is inside a loop body), so hoisting its temps just before the if — and
                // freeing them just after — is correct per-entry RAII.
                Statement then = TransformStatement(ifs.ThenStatement);
                Statement? elseS = ifs.ElseStatement != null
                    ? TransformStatement(ifs.ElseStatement)
                    : null;
                IfStatement rebuilt = ifs with { ThenStatement = then, ElseStatement = elseS };
                return SpillAround(rebuilt, ifs.Condition,
                    rebuildWithCondition: c => rebuilt with { Condition = c });
            }

            case LoopStatement loop:
                return loop with { Body = TransformStatement(loop.Body) };

            case WhileStatement w:
                // `while` desugars to LoopStatement before this pass; if one survives, only descend
                // into the body (hoisting a pre-checked condition's temps outside would change when
                // they evaluate). Condition temps in this rare case are left as-is.
                return w with { Body = TransformStatement(w.Body),
                    ElseBranch = w.ElseBranch != null ? TransformStatement(w.ElseBranch) : null };

            case ForStatement f:
                return f with { Body = TransformStatement(f.Body),
                    ElseBranch = f.ElseBranch != null ? TransformStatement(f.ElseBranch) : null };

            case DangerStatement d:
                return d with { Body = (BlockStatement)TransformStatement(d.Body) };

            case UsingStatement u:
                return u with
                {
                    Body = TransformStatement(u.Body),
                    FallbackBody = u.FallbackBody != null
                        ? TransformStatement(u.FallbackBody)
                        : null
                };

            case WhenStatement whenStmt:
            {
                // Should already be lowered to if-chains for the bodies codegen emits; handle
                // defensively by descending into clause bodies (guards left as-is).
                List<WhenClause> clauses = whenStmt.Clauses
                    .Select(c => c with { Body = TransformStatement(c.Body) }).ToList();
                return whenStmt with { Clauses = clauses };
            }

            // ---- Leaf statements that bear expressions ----

            case ExpressionStatement es:
            {
                // Operator-form assignment (`x = …`): recurse the RHS for spillable receivers, but the
                // RHS value itself is owned by the target — never spill the top.
                if (es.Expression is BinaryExpression { Operator: BinaryOperator.Assign,
                        Left: IdentifierExpression t1 } bin)
                    return LowerReassign(es, bin.Right, t1,
                        rebuild: rhs => es with { Expression = bin with { Right = rhs } });
                // A bare expression statement: recurse for receivers only. We do NOT spill the
                // discarded top value — a fluent `me`-returning call (e.g. `b.append(x)`) yields an
                // alias of an existing owned binding, so freeing it would double-free.
                return SpillAround(es, es.Expression,
                    rebuildWithCondition: e => es with { Expression = e });
            }

            case DeclarationStatement { Declaration: VariableDeclaration v } ds
                when v.Initializer != null:
                return SpillAround(ds, v.Initializer,
                    rebuildWithCondition: init =>
                        ds with { Declaration = v with { Initializer = init } });

            case AssignmentStatement { Target: IdentifierExpression t2 } a:
                return LowerReassign(a, a.Value, t2,
                    rebuild: val => a with { Value = val });

            case AssignmentStatement a:
                return SpillAround(a, a.Value,
                    rebuildWithCondition: val => a with { Value = val });

            case ReturnStatement { Value: { } rv } ret:
                return SpillAround(ret, rv,
                    rebuildWithCondition: val => ret with { Value = val });

            case VariantReturnStatement { Value: { } vrv } vret:
                return SpillAround(vret, vrv,
                    rebuildWithCondition: val => vret with { Value = val });

            case ThrowStatement th:
                return SpillAround(th, th.Error,
                    rebuildWithCondition: err => th with { Error = err });

            default:
                return stmt;
        }
    }

    /// <summary>
    /// Walks <paramref name="root"/> spilling owned receiver/discarded temporaries, then — if any
    /// were found — wraps <paramref name="owner"/> (rebuilt around the rewritten expression) in a
    /// block that declares the temps before it and destroys them (LIFO) after it.
    /// <paramref name="topOwning"/> is true when <paramref name="root"/> itself sits in an owning
    /// position (var init / assignment RHS / return value), so its top-level producer is left intact.
    /// </summary>
    private Statement SpillAround(Statement owner, Expression root,
        System.Func<Expression, Statement> rebuildWithCondition, bool topOwning = true)
    {
        var spills = new List<Spill>();
        Expression rewritten = Visit(root, objectPos: !topOwning, spills);
        if (spills.Count == 0)
            return owner;

        var stmts = new List<Statement>(capacity: spills.Count * 2 + 1);
        foreach (Spill s in spills)
            stmts.Add(new DeclarationStatement(
                Declaration: new VariableDeclaration(Name: s.Name, Type: null, Initializer: s.Init,
                    Visibility: VisibilityModifier.Secret, Location: owner.Location),
                Location: owner.Location));
        stmts.Add(rebuildWithCondition(rewritten));
        for (int i = spills.Count - 1; i >= 0; i--)
            stmts.Add(MakeDestroyStmt(spills[i], owner.Location));
        return new BlockStatement(Statements: stmts, Location: owner.Location);
    }

    /// <summary>
    /// Lowers an assignment to an identifier target. For a MANAGED-LEAF target (Text/Decimal-style
    /// record), the overwrite must first release the old value or it leaks — the dominant cost in
    /// string-building loops (<c>s = s + part</c>). The new RHS is already an independent owned value
    /// (RecordCopyLoweringPass, which has already run, turned any lvalue source into a fresh
    /// <c>$copy</c>; computed results are fresh), so the rewrite is:
    /// <code>var __rv = RHS ; target.$destroy() ; target = __rv</code>
    /// computing RHS (which may read the old target) BEFORE the destroy. For other targets the old
    /// value is released elsewhere (entities by ScopeTeardownLoweringPass; HasRCFields / RC-wrapper
    /// records by codegen's EmitVariableAssignment; scalars need nothing), so we only spill receivers.
    /// </summary>
    private Statement LowerReassign(Statement owner, Expression rhs, IdentifierExpression target,
        System.Func<Expression, Statement> rebuild)
    {
        if (!IsManagedLeafReassignTarget(target.ResolvedType))
            return SpillAround(owner, rhs, rebuildWithCondition: rebuild);

        TypeInfo t = target.ResolvedType!;
        RoutineInfo destroy = ctx.Registry.GetLifecycle(t).Destroy!;
        var spills = new List<Spill>();
        Expression rhs2 = Visit(rhs, objectPos: false, spills);

        var stmts = new List<Statement>(capacity: spills.Count * 2 + 3);
        foreach (Spill s in spills)
            stmts.Add(DeclStmt(s.Name, s.Init, owner.Location));
        string newName = $"__rv_{_counter++}";
        stmts.Add(DeclStmt(newName, rhs2, owner.Location));
        stmts.Add(MakeDestroyCall(target.Name, t, destroy, owner.Location));
        stmts.Add(rebuild(new IdentifierExpression(Name: newName, Location: owner.Location)
            { ResolvedType = t }));
        for (int i = spills.Count - 1; i >= 0; i--)
            stmts.Add(MakeDestroyStmt(spills[i], owner.Location));
        return new BlockStatement(Statements: stmts, Location: owner.Location);
    }

    private static DeclarationStatement DeclStmt(string name, Expression init, SourceLocation loc) =>
        new(Declaration: new VariableDeclaration(Name: name, Type: null, Initializer: init,
                Visibility: VisibilityModifier.Secret, Location: loc),
            Location: loc);

    /// <summary>RC-wrapper base names whose reassignment release is handled by codegen's
    /// EmitVariableAssignment (EmitRetainedVarRelease) — excluded so we never double-release.</summary>
    private static readonly HashSet<string> RcWrapperBaseNames =
        new(comparer: System.StringComparer.Ordinal) { "Retained", "Tracked", "Shared", "Watched" };

    /// <summary>True for a managed-leaf record target whose old value codegen does NOT release on
    /// reassignment: a record with a retaining <c>$copy</c> (Text/Decimal, or one carrying such a
    /// field) that is neither a <c>HasRCFields</c> record nor an RC wrapper (both released by
    /// codegen). Scalars (no retaining copy) and entities (handled by ScopeTeardownLoweringPass) are
    /// excluded.</summary>
    private bool IsManagedLeafReassignTarget(TypeInfo? t)
    {
        if (t is not RecordTypeInfo rec || rec.HasRCFields)
            return false;
        string baseName = rec.Name;
        int bracket = baseName.IndexOf(value: '[');
        if (bracket >= 0) baseName = baseName[..bracket];
        if (RcWrapperBaseNames.Contains(item: baseName))
            return false;
        TypeRegistry.Lifecycle lc = ctx.Registry.GetLifecycle(t);
        return !lc.IsBorrow && lc.Destroy != null && lc.Copy != null;
    }

    // ---------------------------------------------------------------------------------------------
    // Expression visitor: children first (post-order), so inner temps are declared before outer ones.
    // `objectPos` is true only for positions whose owned producer should be torn down here.
    // ---------------------------------------------------------------------------------------------

    private Expression Visit(Expression e, bool objectPos, List<Spill> spills)
    {
        switch (e)
        {
            case CallExpression { Callee: MemberExpression m } call:
            {
                // Recurse the receiver WITHOUT letting it self-spill (objectPos:false): receiver
                // teardown is decided HERE, where the enclosing call's result type is known, so the
                // aliasing guard can apply. Nested receivers (a.b().c()) are handled by this same
                // branch one level down, each guarded by its own call's result type.
                bool receiverConsumed = ConsumingReceiverVerbs.Contains(m.PropertyName);
                Expression newRecv = Visit(m.Object, objectPos: false, spills);

                // Spill the receiver iff it is a fresh heap-owning RC-record producer, the verb does
                // not consume it (retain/track move it into the RC controller), and the call result
                // cannot be a borrow/view aliasing it. An RC-record receiver is safe to free even when
                // the result is another owned value: a method's RC-record/record return is always
                // independent of the receiver — fresh (e.g. string concat allocates a new buffer) or a
                // retaining +1 copy (RecordCopyLoweringPass injects $copy on lvalue/`me` returns) — so
                // the controller refcount stays balanced. The only hazard is a borrow/view result
                // (Viewing/Modifying/…) pointing into the receiver, which the guard excludes.
                if (!receiverConsumed && IsSpillableProducer(newRecv)
                    && !ResultMayAliasReceiver(call.ResolvedType))
                    newRecv = MakeSpill(newRecv, spills);

                List<Expression> newArgs = call.Arguments
                    .Select(a => Visit(a, objectPos: false, spills)).ToList();
                Expression result = call with
                {
                    Callee = m with { Object = newRecv }, Arguments = newArgs
                };
                return MaybeSpillTop(result, objectPos, spills);
            }

            case CallExpression call:
            {
                Expression newCallee = Visit(call.Callee, objectPos: false, spills);
                List<Expression> newArgs = call.Arguments
                    .Select(a => Visit(a, objectPos: false, spills)).ToList();
                Expression result = call with { Callee = newCallee, Arguments = newArgs };
                return MaybeSpillTop(result, objectPos, spills);
            }

            case MemberExpression m:
            {
                // Field read / method-group object: descend (to catch nested call receivers) but do
                // not spill the object itself (v1 limitation — see class doc).
                Expression newObj = Visit(m.Object, objectPos: false, spills);
                return m with { Object = newObj };
            }

            case IndexExpression ix:
            {
                Expression newObj = Visit(ix.Object, objectPos: false, spills);
                Expression newIdx = Visit(ix.Index, objectPos: false, spills);
                return ix with { Object = newObj, Index = newIdx };
            }

            case NamedArgumentExpression na:
                return na with { Value = Visit(na.Value, objectPos: false, spills) };

            case BinaryExpression b:
                return b with
                {
                    Left = Visit(b.Left, objectPos: false, spills),
                    Right = Visit(b.Right, objectPos: false, spills)
                };

            case UnaryExpression u:
                return u with { Operand = Visit(u.Operand, objectPos: false, spills) };

            case StealExpression st:
                // `steal` is an explicit move — never tear down its operand.
                return st with { Operand = Visit(st.Operand, objectPos: false, spills) };

            default:
                // Identifiers, literals, and node forms not modeled here: leave untouched. A producer
                // sitting at the very top in a discarded position is still handled below.
                return MaybeSpillTop(e, objectPos, spills);
        }
    }

    /// <summary>Spills <paramref name="e"/> when it sits in a discard/borrow position and is a
    /// spillable owned producer (the discarded-value case — no aliasing concern since the value is
    /// not stored anywhere).</summary>
    private Expression MaybeSpillTop(Expression e, bool objectPos, List<Spill> spills)
    {
        if (objectPos && IsSpillableProducer(e))
            return MakeSpill(e, spills);
        return e;
    }

    private Expression MakeSpill(Expression producer, List<Spill> spills)
    {
        TypeInfo type = producer.ResolvedType!;
        RoutineInfo destroy = ctx.Registry.GetLifecycle(type).Destroy!;
        string name = $"__tt_{_counter++}";
        spills.Add(new Spill(Name: name, Type: type, Destroy: destroy, Init: producer));
        return new IdentifierExpression(Name: name, Location: producer.Location)
            { ResolvedType = type };
    }

    /// <summary>True for a fresh owned heap producer worth tearing down: a call/creator whose result
    /// is an entity or RC-record with a real (non-borrow) <c>$destroy</c>, excluding view-verb
    /// producers (which yield a borrow of a referent owned elsewhere).</summary>
    private bool IsSpillableProducer(Expression e)
    {
        if (e is not (CallExpression or CreatorExpression))
            return false;
        if (e is CallExpression { Callee: MemberExpression vm } && ViewVerbs.Contains(vm.PropertyName))
            return false;
        TypeInfo? t = e.ResolvedType;
        if (t is null)
            return false;
        TypeRegistry.Lifecycle lc = ctx.Registry.GetLifecycle(t);
        if (lc.IsBorrow || lc.Destroy is null)
            return false;
        // Only HEAP-owning RECORDS are spilled: a managed leaf with a retaining $copy (Text/Decimal)
        // or a record carrying RC-wrapper fields. Their $destroy releases a refcounted controller, so
        // an extra balanced release is always safe. Entities are deliberately excluded for now (their
        // single-owner lifetime and fluent `me` returns are trickier to prove alias-free); plain value
        // records / scalars have a no-op $destroy and would only bloat the IR.
        return t is RecordTypeInfo rec && (lc.Copy != null || rec.HasRCFields);
    }

    /// <summary>True when a call result MAY be a borrow/view pointing into the receiver, so freeing
    /// the receiver after the call could dangle it. Borrow/view wrappers and unknown/abstract results
    /// are treated as possibly-aliasing; scalars, value/RC records, RC wrappers, entities, and
    /// <c>Blank</c> are independent of an RC-record receiver and safe.</summary>
    private static bool ResultMayAliasReceiver(TypeInfo? resultType) =>
        resultType switch
        {
            null => true,
            GenericParameterTypeInfo => true,
            ProtocolTypeInfo => true,
            WrapperTypeInfo w => BorrowWrapperNames.Contains(w.Name),
            _ => false
        };

    private ExpressionStatement MakeDestroyStmt(Spill spill, SourceLocation loc) =>
        MakeDestroyCall(name: spill.Name, type: spill.Type, destroy: spill.Destroy, loc: loc);

    private ExpressionStatement MakeDestroyCall(string name, TypeInfo type, RoutineInfo destroy,
        SourceLocation loc)
    {
        var ident = new IdentifierExpression(Name: name, Location: loc) { ResolvedType = type };
        var callee = new MemberExpression(Object: ident, PropertyName: "$destroy", Location: loc)
            { ResolvedType = _blankType };
        var call = new CallExpression(Callee: callee, Arguments: [], Location: loc)
        {
            ResolvedRoutine = destroy,
            ResolvedType = _blankType,
            LoweringKind = CallClassifier.ClassifyMethodCall(method: destroy)
        };
        return new ExpressionStatement(Expression: call, Location: loc);
    }
}