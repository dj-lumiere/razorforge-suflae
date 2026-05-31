using System.Collections.Generic;
using System.Linq;
using Compiler.Postprocessing;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using Verification;

namespace Compiler.Postprocessing.Passes;

/// <summary>
/// Inserts explicit <c>local.$destroy()</c> calls at scope exits — the unified teardown lowering.
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
    private readonly TypeInfo? _blankType = ctx.Registry.LookupType(name: "Blank");
    private int _spillCounter;

    /// <summary>A live owned binding: its name, type, and resolved <c>$destroy</c> routine.</summary>
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

    private readonly HashSet<string> _movedNames = new(comparer: System.StringComparer.Ordinal);

    private RoutineDeclaration LowerRoutine(RoutineDeclaration r)
    {
        _movedNames.Clear();
        CollectMovedNames(r.Body);

        // Consuming entity parameters are owned by the routine and torn down at every exit, exactly
        // like a top-level local. (Borrows arrive as Referring/Controlling/Viewed/Grasped wrappers,
        // never as bare EntityTypeInfo, so they are correctly excluded.)
        var paramLive = new List<Owned>();
        foreach (Parameter p in r.Parameters)
        {
            if (p.Name == "me") continue;
            if (_movedNames.Contains(item: p.Name)) continue;
            TypeInfo? pt = p.Type?.ResolvedType;
            if (pt != null && TryResolveDestroy(type: pt, out RoutineInfo? d) && d != null)
                paramLive.Add(item: new Owned(Name: p.Name, Type: pt, Destroy: d));
        }

        Statement newBody = LowerStatement(r.Body, paramLive, loopBoundary: 0);
        return r.Body == newBody && paramLive.Count == 0 ? r : r with { Body = newBody };
    }

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
                return u with { Body = LowerStatement(u.Body, Copy(live), loopBoundary) };

            case ReturnStatement or AbsentStatement or ThrowStatement or VariantReturnStatement:
                return PrefixDestroys(stmt, live, from: 0, skip: ReturnedName(stmt));

            case BreakStatement or ContinueStatement:
                return PrefixDestroys(stmt, live, from: loopBoundary, skip: null);

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
                TypeInfo? t = v.Type?.ResolvedType ?? v.Initializer?.ResolvedType;
                if (t != null && !_movedNames.Contains(item: v.Name) && !IsUsingBinding(v: v)
                    && TryResolveDestroy(type: t, out RoutineInfo? d) && d != null)
                {
                    live.Add(item: new Owned(Name: v.Name, Type: t, Destroy: d));
                }
                continue;
            }

            stmts.Add(item: LowerStatement(s, live, loopBoundary));
        }

        // Fall-through end-of-block: destroy this block's own locals in declaration order. Codegen
        // drops these as dead code if the block always terminates (same as UsingLoweringPass's
        // normal-exit exit).
        for (int i = blockStart; i < live.Count; i++)
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

        for (int i = from; i < live.Count; i++)
        {
            if (skip != null && live[index: i].Name == skip) continue;
            stmts.Add(item: MakeDestroyStmt(live[index: i], exit.Location));
        }
        if (stmts.Count == 0) return exit;
        stmts.Add(item: finalExit);
        return new BlockStatement(Statements: stmts, Location: exit.Location);
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
        var callee = new MemberExpression(Object: ident, PropertyName: "$destroy", Location: loc)
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
    /// Resolves the OWN <c>$destroy</c> of a teardown-eligible type via GetMethodsForType (NOT
    /// LookupMethod, which would surface the no-owner universal <c>T.$destroy</c> stub — that mangles
    /// to a symbol nobody emits → undefined at link). Prefers the user-written destructor.
    /// </summary>
    private bool TryResolveDestroy(TypeInfo type, out RoutineInfo? destroy)
    {
        destroy = null;
        if (!NeedsTeardown(type: type)) return false;

        List<RoutineInfo> candidates = ctx.Registry.GetMethodsForType(type: type)
            .Where(predicate: m => m.Name == "$destroy" && m.Parameters.Count == 0)
            .ToList();
        destroy = candidates.FirstOrDefault(predicate: m => !m.IsSynthesized)
                  ?? candidates.FirstOrDefault();
        return destroy != null;
    }

    /// <summary>
    /// Every type has a `$destroy`, so by default it's called at scope exit (the per-type
    /// `$destroy` is a cheap no-op when there's nothing to free). The ONLY exclusions are the
    /// access/borrow tier — `Viewed`/`Grasped`/`Inspected`/`Claimed` views, the `Referring`/
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
    /// user's `var x = __uf_N.$enter()` / `var x = __uf_N`). Their lifetime is governed by the
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

    // -----------------------------------------------------------------------------
    // Move pre-scan: a binding whose ownership leaves the routine is never torn down here.
    // A binding is "moved" when it is: stolen; consumed by `.retain()`/`.track()` (bare entity)
    // written into storage by a store primitive (`inject`/`set_element_at`/`store`); or assigned
    // into another binding/field. These are all unambiguous ownership transfers — unlike general
    // argument passing (which is usually a borrow), so we do NOT treat plain call args as moves.
    // -----------------------------------------------------------------------------

    private static readonly System.Collections.Generic.HashSet<string> StorePrimitives =
        new(comparer: System.StringComparer.Ordinal) { "inject", "set_element_at", "store" };

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
                        PropertyName: "retain" or "track",
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
            }
        });
    }

    private static Expression Unwrap(Expression e) =>
        e is NamedArgumentExpression n ? n.Value : e;

    private static string? CalleeName(Expression callee) => callee switch
    {
        MemberExpression m => m.PropertyName,
        IdentifierExpression id => id.Name,
        _ => null
    };
}
