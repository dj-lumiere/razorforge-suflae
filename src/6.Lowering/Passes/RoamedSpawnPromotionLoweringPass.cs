using Builder.LlvmEmit;
using Builder.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Stage 2b spawn-boundary <c>promote()</c> lowering, moved out of codegen. A <c>Roamed[T]</c>
/// argument crossing a <c>suspended</c>/<c>threaded</c> spawn boundary IS the escape event: on the
/// owner thread — before the task/coroutine is created — the handle must flip to ESCAPED (atomic
/// refcount + armed reentrant lock) so the flip happens-before the callee (which may run on another
/// worker) ever touches it.
///
/// <para>Previously codegen inserted this <c>call void @promote(handle)</c> itself right before the
/// spawn. This pass makes it a REAL AST rewrite instead: for every statement that contains a spawn
/// call, a <c>arg.promote()</c> <see cref="ExpressionStatement"/> is inserted immediately BEFORE that
/// statement — one per <c>Roamed[T]</c> argument, AND one per <c>Roamed[T]</c> transitively owned by a
/// record/tuple argument (reached as <c>arg.field.promote()</c>; see
/// <see cref="AppendPromotesForValue"/>). Because <c>promote</c> mutates the shared controller in place
/// (the handle pointer is unchanged and returns void), evaluating <c>arg.promote()</c> and then
/// spawning with the same handle is identical to the old codegen — codegen now just translates the real
/// call.</para>
///
/// <para>Reachability already seeds <c>promote</c> for every live <c>Roamed[T]</c> via
/// <c>ImplicitCallContract.ForLiveType</c>, so the target is live/monomorphized. Mirrors
/// <see cref="RoamedProjectionLoweringPass"/> (immutable node construction) and
/// <see cref="CancellationInstrumentationPass"/> (statement insertion around spawn sites). Runs after
/// <see cref="RoamedProjectionLoweringPass"/>.</para>
/// </summary>
internal sealed class RoamedSpawnPromotionLoweringPass(PostprocessingContext ctx)
{
    private TypeRegistry Registry => ctx.Registry;

    /// <summary>Inserts spawn-boundary promote calls across a whole program.</summary>
    public void Run(Program program)
    {
        foreach (SyntaxTree.Declaration decl in
                 program.Declarations.OfType<SyntaxTree.Declaration>())
        {
            LowerDeclaration(decl: decl);
        }
    }

    /// <summary>Inserts spawn-boundary promote calls in synthesized variant bodies.</summary>
    public void RunOnVariantBodies()
    {
        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            LowerBody(body: ctx.VariantBodies[key: key]);
        }
    }

    private void LowerDeclaration(SyntaxTree.Declaration decl)
    {
        switch (decl)
        {
            case RoutineDeclaration r:
                LowerBody(body: r.Body);
                break;
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

    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        foreach (SyntaxTree.Declaration m in members)
        {
            if (m is RoutineDeclaration mr)
            {
                LowerBody(body: mr.Body);
            }
        }
    }

    private void LowerBody(Statement body)
    {
        if (body is BlockStatement block)
        {
            LowerBlock(block: block);
        }
    }

    // ---- Block rewrite (in place, mirroring CancellationInstrumentationPass) ---------------------

    private void LowerBlock(BlockStatement block)
    {
        var rewritten = new List<Statement>(capacity: block.Statements.Count);
        foreach (Statement stmt in block.Statements)
        {
            RecurseInto(stmt: stmt);
            rewritten.AddRange(collection: CollectPromotes(stmt: stmt));
            rewritten.Add(item: stmt);

            // A Suflae `global` whose storage (transitively) owns a Roamed[T] handle is reachable from
            // every task, so it must be ESCAPED (armed lock) for the per-statement access-lock brackets
            // to serialize concurrent mutation. Promote it right AFTER its init assignment — descending
            // into a record/tuple global the same way spawn args do. Idempotent + void.
            if (stmt is AssignmentStatement { IsGlobalInit: true } gi)
            {
                AppendPromotesForValue(root: gi.Target, promotes: rewritten);
            }
        }

        block.Statements.Clear();
        block.Statements.AddRange(collection: rewritten);
    }

    // Gathers a promote() statement for every Roamed[T] transitively owned by an argument of every
    // spawn call that appears in `stmt`'s OWN expressions (see AppendPromotesForValue for the descent).
    // Spawns nested inside child statements/blocks are handled when the
    // recursion reaches those blocks, so this only walks expression nodes and does NOT descend into
    // nested Statements (which would double-count them).
    private List<Statement> CollectPromotes(Statement stmt)
    {
        var promotes = new List<Statement>();
        foreach (Expression e in DirectExpressions(stmt: stmt))
        {
            AstWalker.WalkExpressions(root: e,
                visit: n =>
                {
                    if (n is CallExpression call && IsSpawnCall(call: call))
                    {
                        AppendPromotesFor(spawn: call, promotes: promotes);
                    }
                });
        }

        return promotes;
    }

    // The expressions directly owned by a statement (its condition/value/target), excluding any
    // nested Statement bodies — those are visited separately by the block recursion.
    private static IEnumerable<Expression> DirectExpressions(Statement stmt)
    {
        switch (stmt)
        {
            case ExpressionStatement es:
                yield return es.Expression;
                break;
            case DiscardStatement ds:
                yield return ds.Expression;
                break;
            case ReturnStatement { Value: not null } s: yield return s.Value; break;
            case VariantReturnStatement { Value: not null } s: yield return s.Value; break;
            case BecomesStatement s: yield return s.Value; break;
            case ThrowStatement s: yield return s.Error; break;
            case AssignmentStatement s:
                yield return s.Target;
                yield return s.Value;
                break;
            case DestructuringStatement s: yield return s.Initializer; break;
            case DeclarationStatement
            {
                Declaration: VariableDeclaration { Initializer: not null } v
            }:
                yield return v.Initializer; break;
            case IfStatement s: yield return s.Condition; break;
            case WhileStatement s: yield return s.Condition; break;
            case EachStatement s: yield return s.Iterable; break;
            case WhenStatement s: yield return s.Expression; break;
            case UsingStatement s: yield return s.Resource; break;
        }
    }

    private void AppendPromotesFor(CallExpression spawn, List<Statement> promotes)
    {
        foreach (Expression arg in spawn.Arguments)
        {
            AppendPromotesForValue(root: Unwrap(arg: arg), promotes: promotes);
        }
    }

    // Emits a `promote()` for `root` if it is itself a Roamed[T], otherwise DESCENDS into its record
    // fields / tuple elements to promote every transitively-owned Roamed[T]. Crossing a spawn boundary
    // by copy shares the SAME controller for each nested handle (the copy is a refcount bump, not a
    // deep clone), so promoting a nested handle ONCE on the owner side — via `root.field.promote()` —
    // arms the shared controller for the callee's copy too, identical to a top-level Roamed arg. The
    // walk STOPS at a Roamed leaf (its inner value lives behind an opaque controller, not inline) and
    // at any OTHER wrapper (a non-Roamed handle is copied by its own share/refcount semantics and its
    // payload is not `.field`-reachable). @llvm inline-storage records (Array[Roamed[E],N]) have no AST
    // fields to address, so an owned Roamed there is NOT reached — a known gap, not silently claimed.
    private void AppendPromotesForValue(Expression root, List<Statement> promotes)
    {
        if (TryMakePromote(handle: root) is { } promote)
        {
            promotes.Add(item: promote);
            return;
        }

        // A non-Roamed wrapper is an opaque leaf — do not descend into its internal fields (which a
        // RecordTypeSymbol match below would otherwise do, since wrappers ARE records).
        if (root.ResolvedType is { } rt &&
            LlvmEmitter.GetGenericBaseNameStatic(type: rt) is { } rtBase &&
            RuntimeContract.WrapperTypes.Contains(item: rtBase))
        {
            return;
        }

        switch (root.ResolvedType)
        {
            case RecordTypeSymbol { IsGenericDefinition: false } record
                when record.MemberVariables is { Count: > 0 }:
                foreach (MemberVariableInfo field in record.MemberVariables)
                {
                    if (!OwnsRoamed(type: field.Type))
                    {
                        continue;
                    }

                    var access = new MemberExpression(Object: root, MemberName: field.Name,
                        Location: root.Location) { ResolvedType = field.Type };
                    AppendPromotesForValue(root: access, promotes: promotes);
                }

                break;
            case TupleTypeSymbol tuple:
                for (int i = 0; i < tuple.ElementTypes.Count; i++)
                {
                    if (!OwnsRoamed(type: tuple.ElementTypes[index: i]))
                    {
                        continue;
                    }

                    var access = new MemberExpression(Object: root, MemberName: $"item{i}",
                        Location: root.Location) { ResolvedType = tuple.ElementTypes[index: i] };
                    AppendPromotesForValue(root: access, promotes: promotes);
                }

                break;
        }
    }

    // True iff `type` transitively owns a Roamed[T] REACHABLE by field/element access from a copied
    // value: it IS a Roamed, or a plain record (with AST fields) / tuple that owns one. STOPS at a
    // non-Roamed wrapper (opaque, copied by its own semantics) — mirrors the descent in
    // AppendPromotesForValue exactly, so the prune never skips a path the descent could promote nor
    // admits one it cannot. Cannot cycle: record fields / tuple elements are owned BY VALUE (finite
    // size), and the walk stops at every wrapper.
    private static bool OwnsRoamed(TypeSymbol type)
    {
        string? baseName = LlvmEmitter.GetGenericBaseNameStatic(type: type);
        if (baseName == RuntimeContract.Roamed)
        {
            return true;
        }

        if (baseName is not null && RuntimeContract.WrapperTypes.Contains(item: baseName))
        {
            return false;
        }

        return type switch
        {
            TupleTypeSymbol t => t.ElementTypes.Any(predicate: OwnsRoamed),
            RecordTypeSymbol { IsGenericDefinition: false } r when r.MemberVariables is { Count: > 0 }
                => r.MemberVariables.Any(predicate: m => OwnsRoamed(type: m.Type)),
            _ => false
        };
    }

    private static Expression Unwrap(Expression arg)
    {
        return arg is NamedArgumentExpression na
            ? na.Value
            : arg;
    }

    private static bool IsSpawnCall(CallExpression call)
    {
        return call.ResolvedRoutine is
            { AsyncStatus: AsyncStatus.Suspended or AsyncStatus.Threaded };
    }

    // Builds `handle.promote()` as an ExpressionStatement when `handle` is a Roamed[T]. promote
    // returns void and mutates in place, so the statement is a pure side effect before the spawn.
    private ExpressionStatement? TryMakePromote(Expression handle)
    {
        if (handle.ResolvedType is not RecordTypeSymbol rec ||
            LlvmEmitter.GetGenericBaseNameStatic(type: rec) != RuntimeContract.Roamed)
        {
            return null;
        }

        RoutineInfo? promote = Registry.LookupMemberRoutine(type: rec,
            memberRoutineName: RuntimeContract.RoamedMemberRoutine.Promote);
        if (promote is null)
        {
            return null;
        }

        var callee = new MemberExpression(Object: handle,
            MemberName: RuntimeContract.RoamedMemberRoutine.Promote,
            Location: handle.Location) { ResolvedType = rec };
        var call =
            new CallExpression(Callee: callee,
                Arguments: new List<Expression>(),
                Location: handle.Location)
            {
                ResolvedRoutine = promote, ResolvedType = promote.ReturnType
            };
        return new ExpressionStatement(Expression: call, Location: handle.Location);
    }

    // ---- Nested-block recursion (mirrors CancellationInstrumentationPass) ------------------------

    private void RecurseInto(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement b:
                LowerBlock(block: b);
                break;
            case IfStatement i:
                RecurseStmt(stmt: i.ThenStatement);
                if (i.ElseStatement != null)
                {
                    RecurseStmt(stmt: i.ElseStatement);
                }

                break;
            case WhileStatement w:
                RecurseStmt(stmt: w.Body);
                if (w.ElseBranch != null)
                {
                    RecurseStmt(stmt: w.ElseBranch);
                }

                break;
            case LoopStatement l:
                RecurseStmt(stmt: l.Body);
                break;
            case EachStatement f:
                RecurseStmt(stmt: f.Body);
                if (f.ElseBranch != null)
                {
                    RecurseStmt(stmt: f.ElseBranch);
                }

                break;
            case DangerStatement d:
                LowerBlock(block: d.Body);
                break;
            case UsingStatement u:
                RecurseStmt(stmt: u.Body);
                if (u.FallbackBody != null)
                {
                    RecurseStmt(stmt: u.FallbackBody);
                }

                break;
            case WhenStatement whenStmt:
                foreach (WhenClause clause in whenStmt.Clauses)
                {
                    RecurseStmt(stmt: clause.Body);
                }

                break;
        }
    }

    private void RecurseStmt(Statement stmt)
    {
        if (stmt is BlockStatement b)
        {
            LowerBlock(block: b);
        }
        else
        {
            RecurseInto(stmt: stmt);
        }
    }
}
