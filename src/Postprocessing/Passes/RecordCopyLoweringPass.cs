using System.Collections.Generic;
using System.Linq;
using Compiler.Instantiation;
using Compiler.Resolution;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using Verification;

namespace Compiler.Postprocessing.Passes;

/// <summary>
/// Postprocessing pass that lowers two ownership-related constructs:
///
/// <list type="bullet">
/// <item><b>Steal preservation</b> -> keeps <see cref="StealExpression"/> wrappers
/// in the lowered AST so backend entry/codegen can observe explicit ownership
/// transfer sites while still emitting the operand value directly.</item>
/// <item><b>Record copy injection</b> -> rewrites <c>var r2 = r1</c> and <c>r2 = r1</c>
/// where <c>r1</c> is a "borrowed reference" (identifier or field access) of a
/// record type to <c>r1.store()</c>. Required for RC wrapper types
///  (<c>Retained[T]</c>, <c>Tracked[T]</c>, etc.) where a bit-for-bit struct copy
/// would not increment the reference count, causing a double-free bug.
/// For plain records (no RC fields) <c>$store()</c> is semantically identical to
/// a bit copy and is optimized away by LLVM inlining.</item>
/// </list>
///
/// <para>Runs last in the per-file desugaring pipeline (after <see cref="PatternLoweringPass"/>).
/// Needs <c>ResolvedType</c> to be set on all expressions (Phase 5 output).</para>
///
/// <para>Injection is limited to <em>borrowed-reference</em> expressions in assignment
/// positions: <see cref="IdentifierExpression"/> and <see cref="MemberExpression"/> with a
/// record <c>ResolvedType</c>. Fresh values (calls, constructors, arithmetic) are already
/// owned and do not need <c>$store()</c>.</para>
/// </summary>
internal sealed class RecordCopyLoweringPass(PostprocessingContext ctx)
{
    // True while lowering the body of a `$store` routine. Returning `me` there is the identity-copy
    // primitive itself, so it must NOT be rewritten to `me.store()` (that would recurse forever).
    private bool _inCopyRoutine;

    // The retaining-copy verbs: a `$store`, a variant deep `copy`, and every RC-wrapper refcount verb
    // (retain/track/share/watch/roam). Inside ANY of these bodies the bare `return me` is the identity-copy
    // primitive and must NOT be re-injected with a copy (that recurses infinitely). GetLifecycle now returns
    // the RC verb as a type's Copy, so — unlike before — a `retain`/`roam` body is itself a copy routine.
    private static readonly System.Collections.Generic.HashSet<string> RcCopyVerbs =
        [.. Compiler.Resolution.RuntimeContract.RcCopyVerb.Values];

    // True while lowering an RC-wrapper's refcount COPY VERB body (roam/retain/share/track/watch). These are
    // hand-written refcount primitives that reference `me` in many forms (receiver, ctor arg, temp spill) —
    // ANY retain-copy injection inside them makes the verb call itself → infinite recursion. So inside such a
    // body, suppress ALL copy injection (stronger than `_inCopyRoutine`, which only guards `return me`).
    private bool _inRcCopyVerb;

    // The method-name segment of a routine name/key: strips a leading `Owner.` qualifier and any
    // `(params)` / `[typeargs]` suffix. A stdlib generic DEF is keyed by its FULL name (e.g.
    // `Roamed[T].roam`), so a bare-name equality check would miss `roam` — extract the tail first.
    private static string MethodTail(string nameOrKey)
    {
        int lastDot = nameOrKey.LastIndexOf(value: '.');
        string tail = lastDot >= 0 ? nameOrKey[(lastDot + 1)..] : nameOrKey;
        int cut = tail.IndexOfAny(anyOf: ['(', '[']);
        return cut >= 0 ? tail[..cut] : tail;
    }

    // The OWNER type's base name of a `Owner.method` routine name/key (strips generic args + module path):
    // `Core.Roamed[Main.Box].roam` -> `Roamed`. Empty for a free routine.
    private static string OwnerBase(string nameOrKey)
    {
        int lastDot = nameOrKey.LastIndexOf(value: '.');
        if (lastDot < 0) return "";
        string owner = nameOrKey[..lastDot];
        int br = owner.IndexOf(value: '[');
        if (br >= 0) owner = owner[..br];
        int od = owner.LastIndexOf(value: '.');
        return od >= 0 ? owner[(od + 1)..] : owner;
    }

    // True when the routine is a METHOD of an RC wrapper (Retained/Tracked/Shared/Watched/Roamed). Inside ANY
    // such method `me` is the primitive handle — retain-copying it makes the method call the wrapper's copy verb
    // (`roam`), and the copy verb itself calls other wrapper methods (controller_address, …) which would ALSO
    // get `me.roam()` injected → mutual recursion (StackOverflow). So suppress all injection in EVERY RC-wrapper
    // method body, not just the copy verb. AST-declaration sites (Run / LowerMemberList) and variant-body keys
    // only carry the routine NAME string, so the owner is parsed out of it; the instantiated-generic site has a
    // structural `RoutineInfo.OwnerType` and uses `OwnerTypeIsRcWrapper` instead.
    private static bool OwnerNameIsRcWrapper(string nameOrKey) =>
        RuntimeContract.RcWrapperBaseNames.Contains(item: OwnerBase(nameOrKey: nameOrKey));

    // Structural form of the above: the routine's OwnerType is an RC wrapper record. Preferred wherever a
    // `RoutineInfo` is on hand — delegates to the same registry check as `IsRcWrapperType`, no name parsing.
    private static bool OwnerTypeIsRcWrapper(TypeInfo? owner) =>
        owner is not null && TypeRegistry.GetRcWrapperBaseName(type: owner) is not null;

    // True when the type is an RC wrapper record (Retained/Tracked/Shared/Watched/Roamed). A field of such a
    // type has its release-old/retain-new RC owned by codegen (isRoamedField), so the copy pass must NOT also
    // retain a field-write RHS of this type (double-count). Delegates to the registry's canonical
    // structural check (matches on GenericDefinition/WrapperTypeInfo) — no ad-hoc name parsing here.
    private static bool IsRcWrapperType(TypeInfo? type) =>
        type is not null && Compiler.Resolution.TypeRegistry.GetRcWrapperBaseName(type: type) is not null;

    private static bool NameIsCopyRoutine(string name)
    {
        string tail = MethodTail(nameOrKey: name);
        return tail == "store" || tail == "copy" || RcCopyVerbs.Contains(item: tail);
    }

    private static bool KeyIsCopyRoutine(string key)
    {
        if (key.Contains(value: "store") || key.Contains(value: ".copy")) return true;
        return RcCopyVerbs.Contains(item: MethodTail(nameOrKey: key));
    }

    /// <summary>
    /// Runs this compiler phase over its configured input.
    /// </summary>
    public void Run(Program program)
    {
        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[i])
            {
                case RoutineDeclaration r:
                {
                    _inCopyRoutine = NameIsCopyRoutine(name: r.Name); _inRcCopyVerb = OwnerNameIsRcWrapper(nameOrKey: r.Name);
                    Statement newBody = LowerStatement(stmt: r.Body);
                    if (!ReferenceEquals(newBody, r.Body))
                        program.Declarations[i] = r with { Body = newBody };
                    break;
                }

                case EntityDeclaration e:
                    LowerMemberList(members: e.Members);
                    break;

                case RecordDeclaration rec:
                    LowerMemberList(members: rec.Members);
                    break;

                case CrashableDeclaration cr:
                    LowerMemberList(members: cr.Members);
                    break;
            }
        }
    }

    /// <summary>
    /// Runs this compiler phase over its configured input.
    /// </summary>
    public void RunOnVariantBodies()
    {
        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            Statement body = ctx.VariantBodies[key];
            // A variant's deep `copy` body (BuildVariantCopyBody) has an `else => return me` arm for
            // its non-destructible (scalar/None) arms. Since a destructible-arm variant now carries a
            // GetLifecycle.Store, that bare `return me` would otherwise re-inject `me.copy()` → infinite
            // recursion. Treat the copy body like `$store`: its `return me` is the identity primitive.
            _inCopyRoutine = KeyIsCopyRoutine(key: key); _inRcCopyVerb = OwnerNameIsRcWrapper(nameOrKey: key);
            Statement lowered = LowerStatement(stmt: body);
            if (!ReferenceEquals(lowered, body))
                ctx.VariantBodies[key] = lowered;
        }
    }

    /// <summary>
    /// Injects retaining <c>$store</c> into instantiated generic routine bodies. Phase 6's
    /// <c>GenericMonomorphizationPass</c> populates <c>InstantiatedGenericBodies</c> AFTER the
    /// Phase 7 RunGlobal sweep, so those bodies miss the regular per-program copy-lowering. Without
    /// this, a monomorphized routine like <c>BTreeDictNode[Text, S64].entry_get</c> returns a
    /// <c>DictEntry</c> whose <c>Text</c> key is a non-retained alias — torn down per use, then
    /// freed again at container teardown (double-free). Mirrors
    /// <see cref="OperatorLoweringPass.RunOnInstantiatedGenericBodies"/>.
    /// Caller passes the map directly (PostprocessingContext doesn't hold it).
    /// </summary>
    public void RunOnInstantiatedGenericBodies(
        Dictionary<string, MonomorphizedBody> instantiatedGenericBodies)
    {
        foreach (string key in instantiatedGenericBodies.Keys.ToList())
        {
            MonomorphizedBody entry = instantiatedGenericBodies[key];
            if (entry.IsSynthesized) continue; // pure-synthesized: no AST to walk
            _inCopyRoutine = KeyIsCopyRoutine(key: key); _inRcCopyVerb = OwnerTypeIsRcWrapper(owner: entry.Info.OwnerType);
            Statement lowered = LowerStatement(stmt: entry.Ast.Body);
            if (!ReferenceEquals(lowered, entry.Ast.Body))
                instantiatedGenericBodies[key] = entry with
                {
                    Ast = entry.Ast with { Body = lowered }
                };
        }
    }

    /// <summary>
    /// Lower member list as part of this compiler phase.
    /// </summary>
    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i] is RoutineDeclaration mr)
            {
                _inCopyRoutine = NameIsCopyRoutine(name: mr.Name); _inRcCopyVerb = OwnerNameIsRcWrapper(nameOrKey: mr.Name);
                Statement newBody = LowerStatement(stmt: mr.Body);
                if (!ReferenceEquals(newBody, mr.Body))
                    members[i] = mr with { Body = newBody };
            }
        }
    }

    // Statement walker

    /// <summary>
    /// Lower statement as part of this compiler phase.
    /// </summary>
    private Statement LowerStatement(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement block:
            {
                bool changed = false;
                var newStmts = new List<Statement>(capacity: block.Statements.Count);
                foreach (Statement s in block.Statements)
                {
                    Statement ns = LowerStatement(stmt: s);
                    newStmts.Add(item: ns);
                    if (!ReferenceEquals(ns, s)) changed = true;
                }
                return changed ? block with { Statements = newStmts } : block;
            }

            case DeclarationStatement { Declaration: VariableDeclaration { Initializer: not null } vd } ds:
            {
                Expression lowered = LowerOwnership(expr: vd.Initializer, isReturn: false);
                if (ReferenceEquals(lowered, vd.Initializer)) return stmt;
                var newVd = vd with { Initializer = lowered };
                return ds with { Declaration = newVd };
            }

            case AssignmentStatement assign:
            {
                Expression lowered = LowerOwnership(expr: assign.Value, isReturn: false);
                return ReferenceEquals(lowered, assign.Value)
                    ? stmt
                    : assign with { Value = lowered };
            }

            case ReturnStatement { Value: not null } ret:
            {
                Expression lowered = LowerOwnership(expr: ret.Value, isReturn: true);
                return ReferenceEquals(lowered, ret.Value)
                    ? stmt
                    : ret with { Value = lowered };
            }

            case VariantReturnStatement { Value: not null } vrs:
            {
                Expression lowered = LowerOwnership(expr: vrs.Value, isReturn: true);
                return ReferenceEquals(lowered, vrs.Value)
                    ? stmt
                    : vrs with { Value = lowered };
            }

            case IfStatement ifStmt:
            {
                // The condition holds copy positions: `when`/`is` desugar to an if whose condition
                // is e.g. `(x.cmp(you: v)).eq(ME_SMALL)`, where `v` is a call argument needing a
                // retaining `$store`. Without lowering the condition, the callee `$destroy`s the
                // by-value param while the caller never copied it -> double-free of the reused source.
                Expression newCond = StripStealFromExpr(expr: ifStmt.Condition);
                Statement newThen = LowerStatement(stmt: ifStmt.ThenStatement);
                Statement? newElse = ifStmt.ElseStatement != null
                    ? LowerStatement(stmt: ifStmt.ElseStatement)
                    : null;
                bool changed = !ReferenceEquals(newCond, ifStmt.Condition) ||
                               !ReferenceEquals(newThen, ifStmt.ThenStatement) ||
                               !ReferenceEquals(newElse, ifStmt.ElseStatement);
                return changed
                    ? ifStmt with
                    {
                        Condition = newCond,
                        ThenStatement = newThen,
                        ElseStatement = newElse
                    }
                    : stmt;
            }

            case WhileStatement whileStmt:
            {
                // Same as IfStatement: the loop condition can carry copy positions.
                Expression newCond = StripStealFromExpr(expr: whileStmt.Condition);
                Statement newBody = LowerStatement(stmt: whileStmt.Body);
                bool changed = !ReferenceEquals(newCond, whileStmt.Condition) ||
                               !ReferenceEquals(newBody, whileStmt.Body);
                return changed
                    ? whileStmt with { Condition = newCond, Body = newBody }
                    : stmt;
            }

            case LoopStatement loopStmt:
            {
                Statement newBody = LowerStatement(stmt: loopStmt.Body);
                return ReferenceEquals(newBody, loopStmt.Body)
                    ? stmt
                    : loopStmt with { Body = newBody };
            }

            case EachStatement eachStmt:
            {
                Statement newBody = LowerStatement(stmt: eachStmt.Body);
                return ReferenceEquals(newBody, eachStmt.Body)
                    ? stmt
                    : eachStmt with { Body = newBody };
            }

            case WhenStatement whenStmt:
            {
                bool changed = false;
                // The subject holds copy positions too (e.g. `when x.cmp(you: v) is ...` — `v` is a
                // call argument that needs a retaining `$store`). Mirrors the WhenExpression case.
                Expression newSubject = StripStealFromExpr(expr: whenStmt.Expression);
                if (!ReferenceEquals(newSubject, whenStmt.Expression))
                    changed = true;

                var newClauses = new List<WhenClause>(capacity: whenStmt.Clauses.Count);
                foreach (WhenClause clause in whenStmt.Clauses)
                {
                    Statement newBody = LowerStatement(stmt: clause.Body);
                    if (!ReferenceEquals(newBody, clause.Body))
                    {
                        newClauses.Add(item: clause with { Body = newBody });
                        changed = true;
                    }
                    else
                    {
                        newClauses.Add(item: clause);
                    }
                }
                return changed
                    ? whenStmt with { Expression = newSubject, Clauses = newClauses }
                    : stmt;
            }

            case UsingStatement usingStmt:
            {
                Statement newBody = LowerStatement(stmt: usingStmt.Body);
                Statement? fb = usingStmt.FallbackBody != null
                    ? LowerStatement(stmt: usingStmt.FallbackBody)
                    : null;
                return ReferenceEquals(newBody, usingStmt.Body)
                       && ReferenceEquals(fb, usingStmt.FallbackBody)
                    ? stmt
                    : usingStmt with { Body = newBody, FallbackBody = fb };
            }

            case DangerStatement danger:
            {
                // A `danger` block is just a scoped block whose failable calls crash on failure.
                // Without recursing into its body, call arguments inside it never get their
                // retaining `$store` — so the callee's by-value param `$destroy` frees the CALLER's
                // value (e.g. `x.divmod!(other: b)` double-frees `b`). Recurse like any other block.
                Statement newBody = LowerStatement(stmt: danger.Body);
                return ReferenceEquals(newBody, danger.Body)
                    ? stmt
                    : danger with { Body = (BlockStatement)newBody };
            }

            case ExpressionStatement es:
            {
                Expression stripped = StripStealFromExpr(expr: es.Expression);
                return ReferenceEquals(stripped, es.Expression)
                    ? stmt
                    : es with { Expression = stripped };
            }

            case DiscardStatement ds:
            {
                // Lower discard to a plain expression statement -> the return value is
                // already dropped by not assigning it. The 'discard' keyword is codegen noise.
                Expression stripped = StripStealFromExpr(expr: ds.Expression);
                return new ExpressionStatement(Expression: stripped, Location: ds.Location);
            }

            default:
                return stmt;
        }
    }

    // Core lowering

    /// <summary>
    /// Lowers a single expression sitting in an ownership/copy position (var-binding RHS,
    /// assignment RHS, return value, call argument). A "borrowed reference" — an identifier or
    /// field read — of a type that carries a retaining <c>$store</c> (e.g. <c>Text</c>, whose
    /// <c>$store</c> bumps the controller refcount) is rewritten to <c>expr.store()</c> so the new
    /// owner holds its own reference, balancing the <c>$destroy</c> that scope-teardown inserts.
    /// Explicit <see cref="StealExpression"/> is preserved (an explicit move, no copy). Fresh
    /// values (calls, constructors, arithmetic) are already owned and are only recursed for
    /// nested copy positions.
    /// </summary>
    /// <param name="isReturn">
    /// True at a return position. Returning a bare local <see cref="IdentifierExpression"/>
    /// transfers ownership out (scope-teardown skips the returned local), so it must NOT be
    /// copied — that would leak the local. A returned field (<see cref="MemberExpression"/>) is
    /// still copied, since the aggregate keeps its own reference.
    /// </param>
    /// <param name="expr">The expression to inspect and potentially wrap in a copy call.</param>
    private Expression LowerOwnership(Expression expr, bool isReturn)
    {
        // Preserve explicit steal so later stages can observe the ownership transfer site.
        if (expr is StealExpression steal)
            return steal;

        // TemporaryTeardownPass move-temps (`__rv_*` = a spilled reassignment RHS, `__tt_*` = a
        // spilled owned receiver) hold a FRESH single-use owned value that is moved, never shared, so
        // it must NOT be retaining-copied. Normally that pass runs after this one, but instantiated
        // generic bodies are re-lowered here post-monomorphization (after the def already grew the
        // temps), and copying `target = __rv` would leak the un-freed `__rv` every iteration.
        if (expr is IdentifierExpression { Name: var tn }
            && (tn.StartsWith(value: "__rv_", comparisonType: StringComparison.Ordinal)
                || tn.StartsWith(value: "__tt_", comparisonType: StringComparison.Ordinal)))
            return expr;

        // Inside a copy-verb body (`$store` / variant `copy` / an RC-wrapper's refcount verb like `roam`),
        // `me` is the identity-copy primitive in EVERY position — not just `return me`. E.g. `Roamed.roam`
        // reinterprets `me` via `Hijacked[RoamController[T]](me)`; retain-copying that `me` argument would
        // make `roam` call `roam` → infinite recursion (StackOverflow). Never retain-copy `me` here.
        if (_inCopyRoutine && expr is IdentifierExpression { Name: "me" })
            return expr;

        if (!_inRcCopyVerb
            && expr is IdentifierExpression or MemberExpression
            && NeedsRetainingCopy(type: expr.ResolvedType, copyMethod: out RoutineInfo? copyMethod))
        {
            if (isReturn && expr is IdentifierExpression id)
            {
                // Returning the borrowed receiver `me` hands the caller an owned value, so it must
                // be copied (retained) — except inside `$store` itself, where `return me` is the
                // identity primitive. Any other bare identifier is an owned local/param being moved
                // out, so it is returned as-is.
                bool returningBorrowedReceiver = id.Name == "me" && !_inCopyRoutine;
                if (!returningBorrowedReceiver)
                    return expr;
            }
            return MakeCopyCall(expr: expr, copyMethod: copyMethod!);
        }

        // For complex expressions in ownership positions (calls, constructors, etc.),
        // recurse into argument positions (which are themselves copy positions).
        return StripStealFromExpr(expr: expr);
    }

    /// <summary>
    /// Recursively lowers nested expressions, preserving explicit <see cref="StealExpression"/>
    /// markers and injecting a retaining <c>$store</c> on borrowed-reference call arguments (each
    /// argument is a copy position — the callee's parameter is an independent owner that gets
    /// torn down at the callee's scope exit).
    /// </summary>
    private Expression StripStealFromExpr(Expression expr) // NOSONAR S3776
    {
        switch (expr)
        {
            case StealExpression steal:
                return steal;

            case CallExpression call:
            {
                bool changed = false;
                var args = new List<Expression>(capacity: call.Arguments.Count);
                foreach (Expression arg in call.Arguments)
                {
                    Expression s = LowerArgument(arg: arg);
                    args.Add(item: s);
                    if (!ReferenceEquals(s, arg)) changed = true;
                }

                // Recurse into the receiver chain so nested-call arguments get their
                // retain copies even when the inner call sits in *receiver* position.
                // e.g. f-string lowering turns `f"{c1 == c2}"` into
                // `(c1.eq(c2)).represent()`: the `$eq` whose argument `c2` must be
                // copied lives in `Callee.Object`, not in any argument list. We recurse
                // via StripStealFromExpr (not LowerOwnership) so the receiver itself
                // stays borrowed — only nested argument positions get a `$store`.
                Expression callee = call.Callee;
                if (callee is MemberExpression cm)
                {
                    Expression newObj = StripStealFromExpr(expr: cm.Object);
                    if (!ReferenceEquals(newObj, cm.Object))
                    {
                        callee = cm with { Object = newObj };
                        changed = true;
                    }
                }

                return changed ? call with { Arguments = args, Callee = callee } : call;
            }

            case CreatorExpression creator:
            {
                // A constructor's member-variable initializers are copy positions, exactly like call
                // arguments: each becomes an independent field of the new aggregate, so a borrowed
                // reference must be retained. Without this, `DictEntry(key: someText)` aliased the
                // source's controller and double-freed when the entry was later torn down.
                bool changed = false;
                var members = new List<(string Name, Expression Value)>(capacity: creator.MemberVariables.Count);
                foreach ((string Name, Expression Value) mv in creator.MemberVariables)
                {
                    Expression s = LowerOwnership(expr: mv.Value, isReturn: false);
                    members.Add(item: (mv.Name, s));
                    if (!ReferenceEquals(s, mv.Value)) changed = true;
                }

                return changed ? creator with { MemberVariables = members } : creator;
            }

            case GenericMethodCallExpression gmc:
            {
                bool changed = false;
                var args = new List<Expression>(capacity: gmc.Arguments.Count);
                foreach (Expression arg in gmc.Arguments)
                {
                    Expression s = LowerArgument(arg: arg);
                    args.Add(item: s);
                    if (!ReferenceEquals(s, arg)) changed = true;
                }

                // Same receiver-chain recursion as CallExpression (see note above).
                Expression newReceiver = StripStealFromExpr(expr: gmc.Object);
                if (!ReferenceEquals(newReceiver, gmc.Object)) changed = true;

                return changed ? gmc with { Arguments = args, Object = newReceiver } : gmc;
            }

            // User assignment `target = value` reaches here as a BinaryExpression(Assign) wrapped
            // in an ExpressionStatement (the AssignmentStatement node is only for synthesized
            // bodies). The RHS is a copy position exactly like a var-init or call argument: a
            // borrowed-reference value (e.g. a `Text` local) must be retained, otherwise
            // scope-teardown's `$destroy` of the source frees the buffer the target now aliases
            // (use-after-free). Mirrors the AssignmentStatement / DeclarationStatement handling.
            case BinaryExpression { Operator: BinaryOperator.Assign } bin:
            {
                // Field-write to an RC-wrapper / Roamed field: codegen owns the release-old + retain-new RC
                // (isRoamedField), so retaining the RHS here too DOUBLE-counts — an SF self-cycle
                // (`a.next = a`) then never reaches refcount 0 and cc_collect reclaims nothing. Recurse for
                // nested copies but do NOT retain the top-level RHS. Local / non-RC-field targets keep the
                // normal retain (managed leaves like Text ARE pass-owned).
                if (bin.Left is MemberExpression lhsm && IsRcWrapperType(type: lhsm.ResolvedType))
                    return bin with { Right = StripStealFromExpr(expr: bin.Right) };
                Expression newRight = LowerOwnership(expr: bin.Right, isReturn: false);
                return ReferenceEquals(newRight, bin.Right) ? bin : bin with { Right = newRight };
            }

            // A `when` used as an EXPRESSION (e.g. `acc +% when x.cmp(you: v) is ... => ...`).
            // The subject and the arm bodies hold copy positions (call arguments, RHS values) that
            // must be lowered — without this, an argument like `v` inside the subject call never
            // gets its retaining `$store`, yet the callee (e.g. `Integer.cmp`) still `$destroy`s the
            // by-value parameter, double-freeing the reused source on the next iteration.
            case WhenExpression whenExpr:
            {
                bool changed = false;
                Expression? newSubject = whenExpr.Expression is { } subj
                    ? StripStealFromExpr(expr: subj)
                    : null;
                if (newSubject is not null && !ReferenceEquals(newSubject, whenExpr.Expression))
                    changed = true;

                var newClauses = new List<WhenClause>(capacity: whenExpr.Clauses.Count);
                foreach (WhenClause clause in whenExpr.Clauses)
                {
                    Statement nb = LowerStatement(stmt: clause.Body);
                    if (!ReferenceEquals(nb, clause.Body))
                    {
                        newClauses.Add(item: clause with { Body = nb });
                        changed = true;
                    }
                    else
                    {
                        newClauses.Add(item: clause);
                    }
                }

                return changed
                    ? whenExpr with { Expression = newSubject, Clauses = newClauses }
                    : expr;
            }

            // Top-level conditional expression (`if c then a else b`). The branches are copy
            // positions (each can be the result value); the condition is recursed for nested args.
            case ConditionalExpression cond:
            {
                Expression newCond = StripStealFromExpr(expr: cond.Condition);
                Expression newTrue = LowerOwnership(expr: cond.TrueExpression, isReturn: false);
                Expression newFalse = LowerOwnership(expr: cond.FalseExpression, isReturn: false);
                return ReferenceEquals(newCond, cond.Condition)
                    && ReferenceEquals(newTrue, cond.TrueExpression)
                    && ReferenceEquals(newFalse, cond.FalseExpression)
                    ? expr
                    : cond with
                    {
                        Condition = newCond,
                        TrueExpression = newTrue,
                        FalseExpression = newFalse
                    };
            }

            // All other expression types: steal cannot appear as a direct child in practice
            // (steal only wraps identifier or member expressions).
            default:
                return expr;
        }
    }

    /// <summary>
    /// Lowers a single call argument as a copy position, transparently handling the
    /// <see cref="NamedArgumentExpression"/> wrapper required by named-argument calls (S510).
    /// </summary>
    private Expression LowerArgument(Expression arg)
    {
        if (arg is NamedArgumentExpression named)
        {
            Expression loweredValue = LowerOwnership(expr: named.Value, isReturn: false);
            return ReferenceEquals(loweredValue, named.Value) ? named : named with { Value = loweredValue };
        }
        return LowerOwnership(expr: arg, isReturn: false);
    }

    /// <summary>
    /// True when copying a value of <paramref name="type"/> must go through its <c>$store</c> rather
    /// than a bitwise duplicate: the type declares a non-synthesized (hand-written) <c>$store</c>,
    /// which is how leaf managed types like <c>Text</c> bump their refcount. Trivially-copyable
    /// records have only a synthesized identity <c>$store</c> and need no injection.
    /// </summary>
    private bool NeedsRetainingCopy(TypeInfo? type, out RoutineInfo? copyMethod)
    {
        copyMethod = null;
        if (type == null) return false;
        // Same unified decision the teardown pass uses (TypeRegistry.GetLifecycle): a retaining copy
        // is needed iff the type has a hand-written (non-synthesized) zero-arg `$store` — restricted to
        // records and excluding the borrow tier — resolved through GetOwnMethodsResolved so generic
        // resolutions (e.g. Maybe[Text]) agree with what teardown sees for the same type.
        TypeRegistry.Lifecycle lc = ctx.Registry.GetLifecycle(type: type);
        copyMethod = lc.Store;
        return !lc.IsBorrow && copyMethod != null;
    }

    /// <summary>
    /// Builds a fully resolved <c>expr.store()</c> call. The routine and lowering kind are stamped
    /// here (not left for a later pass) so codegen materializes the <c>$store</c> return value — an
    /// unresolved call is emitted as a discarded <c>void</c> call, dropping the retained copy and
    /// leaving the binding with a dangling reference.
    /// </summary>
    private static Expression MakeCopyCall(Expression expr, RoutineInfo copyMethod)
    {
        // Use the resolved method's own name for the property: records/managed-leaves retain via
        // `$store`, but a variant's deep copy is `copy` (BuildVariantCopyBody). Codegen dispatches on
        // ResolvedRoutine, but keeping the property name in sync avoids a misleading `$store` label on
        // a `copy` call and any name-based lookup drifting.
        var callee = new MemberExpression(Object: expr, MemberName: copyMethod.Name, Location: expr.Location)
            { ResolvedType = expr.ResolvedType };
        return new CallExpression(Callee: callee, Arguments: [], Location: expr.Location)
        {
            ResolvedRoutine = copyMethod,
            ResolvedType = expr.ResolvedType,
            LoweringKind = CallClassifier.ClassifyMethodCall(method: copyMethod)
        };
    }
}
