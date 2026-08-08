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
/// Inserts explicit <c>local.destroy()</c> calls at scope exits — the unified teardown lowering.
/// After this pass codegen performs NO teardown of its own: every owned local/param is destroyed
/// by a real <see cref="CallExpression"/> in the AST, so reachability and monomorphization see the
/// calls naturally and codegen just emits them.
///
/// <para>Teardown is <b>block-scoped</b> (RAII): a local is destroyed at the end of (and on every
/// escape from) the block that declares it. Because control only reaches a block's end / escape
/// after the declaration executed, the local is always initialized at teardown — no null-guard or
/// zero-init dance is needed (unlike the prior function-scoped codegen teardown).</para>
///
/// <para>Insertion rules per exit (locals destroyed in reverse declaration order):</para>
/// <list type="bullet">
///   <item><see cref="ReturnStatement"/> / <see cref="AbsentStatement"/> / <see cref="ThrowStatement"/>
///         / <see cref="VariantReturnStatement"/> — destroy ALL live owned locals/params, except the
///         value being returned.</item>
///   <item><see cref="BreakStatement"/> / <see cref="ContinueStatement"/> — destroy owned locals
///         declared inside the enclosing loop.</item>
///   <item>Block fall-through end — destroy the locals declared in that block.</item>
/// </list>
///
/// <para>Move exclusion: a local that is moved anywhere in the routine (operand of <c>steal</c>, or
/// receiver of <c>.retain()</c>/<c>.track()</c>) is never destroyed — ownership left the binding.
/// This is flow-insensitive, mirroring the prior codegen behaviour.</para>
/// </summary>
internal sealed class ScopeTeardownLoweringPass(PostprocessingContext ctx)
{
    private readonly TypeInfo? _blankType = ctx.Registry.LookupType(name: "None");
    private int _spillCounter;

    /// <summary>A live owned binding: its name, type, and resolved <c>$destroy</c> routine.
    /// An entity binding always holds a valid owned allocation while live (a lateinit zeroed
    /// placeholder, the declaration initializer, or a later-assigned value).</summary>
    private readonly record struct Owned(string Name, TypeInfo Type, RoutineInfo Destroy);

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

    public void RunOnVariantBodies()
    {
        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            // Variant/synthesized bodies have no parameter list available here; teardown applies to
            // their block-local owned vars only.
            _movedNames.Clear();
            CollectMovedNames(ctx.VariantBodies[key]);
            var live = new List<Owned>();
            Statement lowered = LowerStatement(ctx.VariantBodies[key], live, loopBoundary: 0);
            ctx.VariantBodies[key] = lowered;
        }
    }

    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int j = 0; j < members.Count; j++)
        {
            if (members[j] is RoutineDeclaration m)
                members[j] = LowerRoutine(m);
        }
    }

    private readonly HashSet<string> _movedNames = new(comparer: StringComparer.Ordinal);

    private RoutineDeclaration LowerRoutine(RoutineDeclaration r)
    {
        _movedNames.Clear();
        CollectMovedNames(r.Body);
        // Merge SA's authoritative per-routine "stolen / out of scope" record. `steal` takes a
        // binding out of scope (ownership moves to the callee, which destroys the content), but the
        // `steal` AST wrapper is normalized away during arg lowering (e.g. `Text(from_list: steal
        // digits)` → `Text(digits)`), so the AST move pre-scan above can miss it. SA recorded it via
        // deadref tracking; trust that so a stolen binding is never torn down here (double-free).
        if (r.StolenVariableNames is { Count: > 0 } stolen)
            _movedNames.UnionWith(other: stolen);

        // Consuming entity parameters are owned by the routine and torn down at every exit, exactly
        // like a top-level local. (Borrows arrive as Referring/Controlling/Viewing/Modifying wrappers,
        // never as bare EntityTypeInfo, so they are correctly excluded.)
        //
        // Suflae exception: an entity parameter is a BORROWED handle — the caller keeps ownership, so
        // destroying it here double-frees the caller's live entity. After representation unification
        // (SignatureResolver.MaybeRoamSuflaeEntity) an SF entity param resolves to `Roamed[E]` (a
        // RecordTypeInfo), so we skip THAT; a bare `EntityTypeInfo` param is still skipped for the older
        // interim `.raw_inner()`-projected shape (both are borrows the callee must not release).
        bool isSuflae = ctx.Registry.Language == TypeModel.Enums.Language.Suflae;
        var paramLive = new List<Owned>();
        foreach (Parameter p in r.Parameters)
        {
            if (p.Name == "me") continue;
            if (_movedNames.Contains(item: p.Name)) continue;
            TypeInfo? pt = p.Type?.ResolvedType;
            if (isSuflae && (pt is EntityTypeInfo || IsRoamedRecord(pt))) continue;
            if (pt != null && TryResolveDestroy(type: pt, out RoutineInfo? d) && d != null)
                paramLive.Add(item: new Owned(Name: p.Name, Type: pt, Destroy: d));
        }

        Statement newBody = LowerStatement(r.Body, paramLive, loopBoundary: 0);
        return r.Body == newBody && paramLive.Count == 0 ? r : r with { Body = newBody };
    }

    // True for a `Roamed[E]` handle in either representation the pipeline produces (the wrapper form
    // from SuflaeEntityLoweringPass or the RecordTypeInfo form from the resolver's GetOrCreateResolution).
    private static bool IsRoamedRecord(TypeInfo? t) =>
        t is RecordTypeInfo { GenericDefinition.Name: RuntimeContract.Roamed }
          or WrapperTypeInfo { Name: RuntimeContract.Roamed };

    /// <summary>
    /// Lowers a statement. <paramref name="live"/> is the ordered list of owned bindings live on
    /// entry (params + enclosing-block locals). <paramref name="loopBoundary"/> is the index in
    /// <paramref name="live"/> marking the nearest enclosing loop's entry (locals at index &gt;=
    /// boundary were declared inside the loop). The caller owns <paramref name="live"/>; this method
    /// copies before recursing into nested scopes.
    /// </summary>
    private Statement LowerStatement(Statement stmt, List<Owned> live, int loopBoundary)
    {
        switch (stmt)
        {
            case BlockStatement b:
                return LowerBlock(b, live, loopBoundary);

            case IfStatement ifs:
            {
                Statement then = LowerStatement(ifs.ThenStatement, Copy(live), loopBoundary);
                Statement? elseS = ifs.ElseStatement != null
                    ? LowerStatement(ifs.ElseStatement, Copy(live), loopBoundary)
                    : null;
                return ifs with { ThenStatement = then, ElseStatement = elseS };
            }

            case WhenStatement w:
            {
                var clauses = w.Clauses
                    .Select(selector: c => c with { Body = LowerStatement(c.Body, Copy(live), loopBoundary) })
                    .ToList();
                return w with { Clauses = clauses };
            }

            case LoopStatement loop:
            {
                // Locals declared inside the loop body sit at index >= live.Count on entry, so the
                // new boundary is the current live count.
                Statement body = LowerStatement(loop.Body, Copy(live), loopBoundary: live.Count);
                return loop with { Body = body };
            }

            case DangerStatement d:
                return d with { Body = (BlockStatement)LowerStatement(d.Body, Copy(live), loopBoundary) };

            // At Phase 6 a user `using` is still a UsingStatement (UsingLoweringPass runs later in
            // Phase 7). Recurse into the body so inner owned locals are torn down, but leave the
            // resource itself alone — its lifetime is governed by the `$enter`/`$exit` that
            // UsingLoweringPass injects. (In already-lowered stdlib bodies the `using` is gone; its
            // `__uf_` temporaries are skipped by IsUsingBinding instead.)
            case UsingStatement u:
                return u with
                {
                    Body = LowerStatement(u.Body, Copy(live), loopBoundary),
                    FallbackBody = u.FallbackBody != null
                        ? LowerStatement(u.FallbackBody, Copy(live), loopBoundary)
                        : null
                };

            case ReturnStatement or AbsentStatement or ThrowStatement or VariantReturnStatement:
                return PrefixDestroys(stmt, live, from: 0, skip: ReturnedName(stmt));

            case BreakStatement or ContinueStatement:
                return PrefixDestroys(stmt, live, from: loopBoundary, skip: null);

            // An owned entity local always holds a valid owned allocation at a reassignment —
            // a `lateinit` binding holds the zeroed placeholder or a prior value, a plain
            // binding was initialized at declaration (block-scoped teardown guarantees the
            // declaration executed). So reassignment must destroy the current content first
            // or it leaks: the branch-init pattern (`lateinit var x: T` then `x = T(...)` per
            // branch) would leak a placeholder per run, and `var r = T(...) ; r = T(...)`
            // would leak the first allocation. Bindings excluded from `live` (stolen, view,
            // using, no $destroy) are correctly skipped — same trust as scope-exit teardown.
            // The RHS is spilled to a temp before the destroy so it may still read the
            // old value; a bare-identifier RHS is a move and needs no spill. User assignments
            // are still in operator form here (ExpressionStatement of `=` BinaryExpression —
            // codegen's EmitBinaryAssign); AssignmentStatement appears in synthesized bodies.
            case AssignmentStatement a when a.Target is IdentifierExpression target &&
                                            FindOwnedEntity(live, target.Name) is { } owned:
                return LowerEntityReassign(original: a, rhs: a.Value,
                    rebuild: rhs => a with { Value = rhs }, owned: owned);

            case ExpressionStatement
            {
                Expression: BinaryExpression
                {
                    Operator: BinaryOperator.Assign, Left: IdentifierExpression target
                } bin
            } es when FindOwnedEntity(live, target.Name) is { } owned:
                return LowerEntityReassign(original: es, rhs: bin.Right,
                    rebuild: rhs => es with { Expression = bin with { Right = rhs } },
                    owned: owned);

            default:
                return stmt;
        }
    }

    private BlockStatement LowerBlock(BlockStatement block, List<Owned> outerLive, int loopBoundary)
    {
        // `live` grows as this block's own declarations are seen; nested scopes get a copy.
        var live = Copy(outerLive);
        int blockStart = live.Count;
        var stmts = new List<Statement>(capacity: block.Statements.Count + 2);

        foreach (Statement s in block.Statements)
        {
            if (s is DeclarationStatement { Declaration: VariableDeclaration v })
            {
                stmts.Add(item: s);

                // A `var buf = _lit_N` binding takes over ownership of a hoisted collection-literal
                // temporary (ExpressionLoweringPass lowers `var buf = []` to
                // `var _lit_N = List(); _lit_N.add(...); var buf = _lit_N`). `buf` and `_lit_N` alias
                // the SAME object, so only ONE may be torn down. When this pass runs AFTER that lowering
                // (stdlib + synthesized variant bodies — user programs are lowered later), both would be
                // live and BOTH destroyed → double-free. Treat the move as consuming the temp: drop it
                // from `live` and record it moved so no scope-exit/return teardown frees it.
                if (v.Initializer is IdentifierExpression { Name: var srcName }
                    && srcName.StartsWith(value: "_lit_", comparisonType: StringComparison.Ordinal))
                {
                    _movedNames.Add(item: srcName);
                    int srcIdx = live.FindLastIndex(match: o => o.Name == srcName);
                    if (srcIdx >= 0) live.RemoveAt(index: srcIdx);
                }

                TypeInfo? t = v.Type?.ResolvedType ?? v.Initializer?.ResolvedType;
                if (t != null && !_movedNames.Contains(item: v.Name) && !IsUsingBinding(v: v)
                    && !IsViewBinding(v: v)
                    && TryResolveDestroy(type: t, out RoutineInfo? d) && d != null)
                {
                    live.Add(item: new Owned(Name: v.Name, Type: t, Destroy: d));
                }
                continue;
            }

            stmts.Add(item: LowerStatement(s, live, loopBoundary));
        }

        // Fall-through end-of-block: destroy this block's own locals in REVERSE declaration order
        // (LIFO) — a later-declared local may reference an earlier one, so destroying dependents
        // before dependencies is the safe RAII order (and matches this pass's documented contract).
        // Codegen drops these as dead code if the block always terminates (same as
        // UsingLoweringPass's normal-exit exit).
        for (int i = live.Count - 1; i >= blockStart; i--)
            stmts.Add(item: MakeDestroyStmt(live[index: i], block.Location));

        return block with { Statements = stmts };
    }

    /// <summary>
    /// Wraps <paramref name="exit"/> in a block that destroys live owned bindings at indices
    /// <c>[from, live.Count)</c>, skipping <paramref name="skip"/> (a returned bare local, which is
    /// moved out).
    ///
    /// <para>When the exit returns a non-trivial EXPRESSION (e.g. <c>return me.concat(other)</c>),
    /// that expression may still read owned locals/params. Destroying them first would free memory
    /// the expression then reads (use-after-free). So the return value is evaluated into a temp
    /// BEFORE the destroys, and the temp — which now owns the result — is returned and excluded from
    /// teardown:
    /// <code>var __td_ret = EXPR ; &lt;destroys&gt; ; return __td_ret</code></para>
    /// </summary>
    private Statement PrefixDestroys(Statement exit, List<Owned> live, int from, string? skip)
    {
        var stmts = new List<Statement>();
        Statement finalExit = exit;

        // Spill a non-trivial returned expression so it's computed while its locals are still live.
        // (A bare-identifier return is a move — `skip` already excludes it — and needs no spill.)
        Expression? retVal = exit switch
        {
            ReturnStatement r => r.Value,
            VariantReturnStatement vr => vr.Value,
            _ => null
        };
        if (retVal is not null and not IdentifierExpression && WillDestroyAny(live, from, skip))
        {
            string tmp = $"__td_ret_{_spillCounter++}";
            // Leave the slot type to be inferred from EXPR — codegen emits the spilled value with its
            // own resolved type, so the slot must match THAT, not the declared routine return type
            // (they can disagree, e.g. a synthesized $diagnose whose AST return type lags the body, or
            // a return that codegen wraps). The failable-passthrough case (node says S64, emits
            // Maybe[S64]) is handled at the source: ErrorHandlingVariantPass stamps the passthrough
            // call's ResolvedType with the variant carrier, so EXPR inference already sees Maybe[S64].
            var decl = new VariableDeclaration(Name: tmp, Type: null, Initializer: retVal,
                Visibility: VisibilityModifier.Secret, Location: exit.Location);
            stmts.Add(item: new DeclarationStatement(Declaration: decl, Location: exit.Location));
            var tmpRef = new IdentifierExpression(Name: tmp, Location: exit.Location)
                { ResolvedType = retVal.ResolvedType };
            finalExit = exit switch
            {
                ReturnStatement r => r with { Value = tmpRef },
                VariantReturnStatement vr => vr with { Value = tmpRef },
                _ => exit
            };
            skip = tmp; // the spilled value is moved out — never tear it down
        }

        // Destroy in REVERSE declaration order (LIFO) — the safe RAII order, matching this pass's
        // documented contract and the fall-through end-of-block teardown above.
        for (int i = live.Count - 1; i >= from; i--)
        {
            if (skip != null && live[index: i].Name == skip) continue;
            stmts.Add(item: MakeDestroyStmt(live[index: i], exit.Location));
        }
        if (stmts.Count == 0) return exit;
        stmts.Add(item: finalExit);
        return new BlockStatement(Statements: stmts, Location: exit.Location);
    }

    /// <summary>
    /// Finds the innermost live binding named <paramref name="name"/> and returns it only if it
    /// is an owned bare-entity local (lateinit or plain) — the bindings whose reassignment must
    /// tear down the old content. A shadowing non-entity binding correctly yields null.
    /// </summary>
    private static Owned? FindOwnedEntity(List<Owned> live, string name)
    {
        for (int i = live.Count - 1; i >= 0; i--)
        {
            if (live[index: i].Name != name) continue;
            return live[index: i].Type is EntityTypeInfo ? live[index: i] : null;
        }
        return null;
    }

    /// <summary>
    /// Rewrites <c>x = EXPR</c> on an owned entity local to
    /// <c>var __li_N = EXPR ; x.destroy() ; x = __li_N</c> so the current content (placeholder
    /// or prior value) is freed exactly once and the RHS still sees the old value.
    /// <paramref name="rebuild"/> reconstructs the assignment statement (whichever AST form it
    /// has) around the spilled RHS.
    /// </summary>
    private Statement LowerEntityReassign(Statement original, Expression rhs,
        Func<Expression, Statement> rebuild, Owned owned)
    {
        var stmts = new List<Statement>();
        Expression finalRhs = rhs;

        // A bare-identifier RHS is a move of an existing binding — no spill needed.
        if (rhs is not IdentifierExpression)
        {
            string tmp = $"__li_{_spillCounter++}";
            var decl = new VariableDeclaration(Name: tmp, Type: null, Initializer: rhs,
                Visibility: VisibilityModifier.Secret, Location: original.Location);
            stmts.Add(item: new DeclarationStatement(Declaration: decl, Location: original.Location));
            finalRhs = new IdentifierExpression(Name: tmp, Location: original.Location)
                { ResolvedType = rhs.ResolvedType };
        }

        stmts.Add(item: MakeDestroyStmt(owned, original.Location));
        stmts.Add(item: rebuild(finalRhs));
        return new BlockStatement(Statements: stmts, Location: original.Location);
    }

    private bool WillDestroyAny(List<Owned> live, int from, string? skip)
    {
        for (int i = from; i < live.Count; i++)
            if (skip == null || live[index: i].Name != skip)
                return true;
        return false;
    }

    private ExpressionStatement MakeDestroyStmt(Owned owned, SourceLocation loc)
    {
        var ident = new IdentifierExpression(Name: owned.Name, Location: loc) { ResolvedType = owned.Type };
        var callee = new MemberExpression(Object: ident, MemberName: "destroy", Location: loc)
            { ResolvedType = _blankType };
        var call = new CallExpression(Callee: callee, Arguments: [], Location: loc)
        {
            ResolvedRoutine = owned.Destroy,
            ResolvedType = _blankType,
            LoweringKind = CallClassifier.ClassifyMethodCall(method: owned.Destroy)
        };
        return new ExpressionStatement(Expression: call, Location: loc);
    }

    /// <summary>
    /// Resolves the <c>$destroy</c> to call at scope exit via the unified
    /// <see cref="TypeRegistry.GetLifecycle"/> — the SAME decision the copy pass (<c>NeedsRetainingCopy</c>)
    /// drives off, so a value is either both retaining-copied and balanced-destroyed or neither (the
    /// asymmetry that double-freed before). <c>GetLifecycle</c> resolves through
    /// <c>GetOwnMethodsResolved</c> (own-methods-only, generic-resolution aware), so it finds e.g.
    /// <c>Retained[Tracer].destroy</c> without surfacing the no-owner universal <c>T.destroy</c> stub,
    /// and excludes the abstract tier.
    /// </summary>
    private bool TryResolveDestroy(TypeInfo type, out RoutineInfo? destroy)
    {
        TypeRegistry.Lifecycle lc = ctx.Registry.GetLifecycle(type: type);
        destroy = lc.Destroy;
        if (lc.IsBorrow || destroy == null)
            return false;
        // Elide the teardown of a trivially-destructible value (a pure-scalar record/tuple with no
        // user destroy): its $destroy is a transitive chain of `ret void`s that the optimizer can't
        // strip (external linkage) and that pins the value's alloca, blocking SROA. Skipping the call
        // lets the value scalarize — e.g. `record R { inner: S64 }` collapses to a bare `i64`.
        if (ctx.Registry.IsTriviallyDestructible(type: type))
        {
            destroy = null;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Every type has a `$destroy`, so by default it's called at scope exit (the per-type
    /// `$destroy` is a cheap no-op when there's nothing to free). The ONLY exclusions are the
    /// access/borrow tier — `Viewing`/`Modifying`/`Inspecting`/`Claiming` views, the `Referring`/
    /// `Controlling` access protocols, and the unmanaged `Hijacked` pointer — whose referent is
    /// owned elsewhere, so destroying them here would free a caller's value. Abstract types
    /// (generic params, protocols) likewise have no concrete destructor to call.
    /// </summary>
    internal static bool NeedsTeardown(TypeInfo type)
    {
        return type is not (GenericParameterTypeInfo or ProtocolTypeInfo);
    }

    private static string? ReturnedName(Statement stmt) => stmt switch
    {
        ReturnStatement { Value: IdentifierExpression id } => id.Name,
        VariantReturnStatement { Value: IdentifierExpression id } => id.Name,
        _ => null
    };

    private static List<Owned> Copy(List<Owned> live) => [.. live];

    /// <summary>
    /// True for the synthetic bindings UsingLoweringPass emits (`var __uf_N = resource` and the
    /// user's `var x = __uf_N.enter()` / `var x = __uf_N`). Their lifetime is governed by the
    /// injected `$enter`/`$exit`, so they must NOT also be torn down here (that would double-free
    /// the single underlying entity).
    /// </summary>
    private static bool IsUsingBinding(VariableDeclaration v)
    {
        if (v.Name.StartsWith(value: "__uf_")) return true;
        return v.Initializer switch
        {
            IdentifierExpression id => id.Name.StartsWith(value: "__uf_"),
            CallExpression { Callee: MemberExpression { Object: IdentifierExpression o } } =>
                o.Name.StartsWith(value: "__uf_"),
            _ => false
        };
    }

    /// <summary>
    /// The reference primitives that yield an in-flight <c>T</c> view of a referent owned elsewhere:
    /// <c>Hijacked[T].as_entity()</c> and the <c>$refer</c>/<c>$control</c> marker-protocol coercions.
    /// A binding initialized by one of these owns nothing and must NOT be torn down.
    /// </summary>
    private static readonly IReadOnlySet<string> ViewVerbs = RuntimeContract.ViewVerbs;

    /// <summary>
    /// True for a binding that holds a borrowed <c>T</c> view — <c>var ctrl = ptr.as_entity()</c>,
    /// <c>var x = h.refer()</c>, <c>var x = h.control()</c>. These pervade the RC wrapper bodies
    /// (e.g. <c>var ctrl = Hijacked[RetainController[T]](me).as_entity()</c> in <c>Retained.release</c>).
    /// The binding's static type is the bare referent (<c>T</c>), so <c>GetLifecycle</c> would resolve
    /// the referent's real <c>$destroy</c> and free a value owned elsewhere — hence we key on the
    /// initializer VERB (a reference primitive), per the four-routine governance model, not the type.
    /// </summary>
    private static bool IsViewBinding(VariableDeclaration v) =>
        (v.Initializer is CallExpression { Callee: MemberExpression m } &&
         ViewVerbs.Contains(item: m.MemberName))
        // A variant when-pattern payload binding (`when me is Arm as v: …`, lowered by
        // PatternLoweringPass to `var v = <CarrierPayloadExpression on me>`) is a BORROW/view into the
        // matched variant's payload — the variant still owns it. Tearing `v` down frees the variant's
        // payload out from under it: a read-only `$represent` would then corrupt `me`, and the
        // auto-synthesized variant `$destroy` (explicit `v.destroy()`) would double-free. So exclude it.
        || v.Initializer is CarrierPayloadExpression;

    // -----------------------------------------------------------------------------
    // Move pre-scan: a binding whose ownership leaves the routine is never torn down here.
    // A binding is "moved" when it is: stolen; consumed by `.retain()`/`.track()` (bare entity)
    // written into storage by a store primitive (`poke`/`store_element_ref`/`store`); or assigned
    // into another binding/field. These are all unambiguous ownership transfers — unlike general
    // argument passing (which is usually a borrow), so we do NOT treat plain call args as moves.
    // (`load_element_ref` is a READ, not a store, so it is excluded.)
    // -----------------------------------------------------------------------------

    private static readonly IReadOnlySet<string> StorePrimitives = RuntimeContract.StorePrimitives;

    private void CollectMovedNames(Statement stmt)
    {
        AstWalker.Walk(root: stmt, visit: node =>
        {
            switch (node)
            {
                case StealExpression { Operand: IdentifierExpression id }:
                    _movedNames.Add(item: id.Name);
                    break;
                // `T.retain()` / `T.track()` on a bare ENTITY consumes it (ownership moves into the
                // RC controller). But `Retained.retain()` / `Tracked.track()` only mint another
                // handle — the receiver is NOT consumed and must still be released at scope exit.
                // So only treat the receiver as moved when it is a bare entity.
                case CallExpression
                {
                    Callee: MemberExpression
                    {
                        MemberName: RuntimeContract.RefCount.Retain or RuntimeContract.RefCount.Track,
                        Object: IdentifierExpression { ResolvedType: EntityTypeInfo } recv
                    }
                }:
                    _movedNames.Add(item: recv.Name);
                    break;
                // Store primitives write their argument(s) into memory/storage — the source binding
                // is moved into the container, not dropped at scope exit.
                case CallExpression call when CalleeName(call.Callee) is { } n && StorePrimitives.Contains(item: n):
                    foreach (Expression arg in call.Arguments)
                        if (Unwrap(arg) is IdentifierExpression a)
                            _movedNames.Add(item: a.Name);
                    break;
                // `target = source` / `me.field = source` moves `source` into the target.
                case AssignmentStatement assign when Unwrap(assign.Value) is IdentifierExpression rhs:
                    _movedNames.Add(item: rhs.Name);
                    break;
                // An explicit `v.destroy()` consumes `v` — it must NOT then be torn down again at
                // scope exit. This is the auto-synthesized variant `$destroy` shape (`when me is Arm
                // as v: v.destroy()`): without this the pattern-bound heap payload is destroyed by the
                // explicit call AND by binding teardown → double free (scalar arms hid it: no-op $destroy).
                case CallExpression
                {
                    Callee: MemberExpression
                    {
                        MemberName: "destroy", Object: IdentifierExpression dv
                    }
                }:
                    _movedNames.Add(item: dv.Name);
                    break;
                // Constructing a VARIANT boxes (takes ownership of) its single payload — the source
                // binding is moved into the variant, not dropped at scope exit. Without this, a heap
                // payload boxed into a returned variant (e.g. a synthesized `serialize()` returning
                // `SerialValue.Dict(<hoisted dict temp>)`) is BOTH boxed and torn down → double free.
                // (Harmless for scalar arms: scalars have no `$destroy`. Entity/record field moves are
                // handled via `steal`; variant/carrier boxing has no steal, so mark it here.)
                case CreatorExpression creator
                    when (creator.ConstructedType ?? creator.ResolvedType) is VariantTypeInfo:
                    foreach ((_, Expression val) in creator.MemberVariables)
                        if (Unwrap(val) is IdentifierExpression a)
                            _movedNames.Add(item: a.Name);
                    break;
            }
        });
    }

    private static Expression Unwrap(Expression e) =>
        e is NamedArgumentExpression n ? n.Value : e;

    private static string? CalleeName(Expression callee) => callee switch
    {
        MemberExpression m => m.MemberName,
        IdentifierExpression id => id.Name,
        _ => null
    };
}
