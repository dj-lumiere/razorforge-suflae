using SyntaxTree;

namespace Compiler.Desugaring.Passes;

/// <summary>
/// Lowers for-loops to a loop + when form so the code generator handles all loops
/// uniformly via <c>EmitLoop</c> + <c>EmitWhen</c>.
///
/// <para>Transform rule for <c>for v in iterable { body }</c>:</para>
/// <code>
/// {
///   var _lf_iter_N = iterable.$iter()
///   loop
///     when _lf_iter_N.try_next()
///       is None
///         break
///       else var v
///         body
/// }
/// </code>
///
/// <para>Range-based loops (<c>for x in 0 to n</c>) are also lowered via this rule:
/// <c>RangeExpression</c> is passed as the iterable; <c>ExpressionLoweringPass</c> (which
/// runs after this pass) converts it to <c>Range[T](...)</c>, so <c>.$iter()</c> dispatches
/// to <c>Range[T].$iter()</c> → <c>RangeEmitter[T]</c> → <c>try_next()</c>.</para>
///
/// <para>The following forms are left unchanged for the code generator to handle:</para>
/// <list type="bullet">
///   <item>For-loops with tuple destructuring (<c>for (a, b) in pairs</c>)</item>
///   <item>For-loops with an else branch</item>
/// </list>
/// </summary>
internal sealed class ControlFlowLoweringPass(DesugaringContext ctx)
{
    private int _iterCount;

    public void Run(Program program)
    {
        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[i])
            {
                case RoutineDeclaration r:
                {
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

    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int j = 0; j < members.Count; j++)
        {
            if (members[j] is not RoutineDeclaration m) continue;
            Statement newBody = LowerStatement(stmt: m.Body);
            if (!ReferenceEquals(newBody, m.Body))
                members[j] = m with { Body = newBody };
        }
    }

    private Statement LowerStatement(Statement stmt)
    {
        switch (stmt)
        {
            case ForStatement f:
                return LowerFor(forStmt: f);

            case BlockStatement b:
            {
                bool changed = false;
                var stmts = new List<Statement>(capacity: b.Statements.Count);
                foreach (Statement s in b.Statements)
                {
                    Statement n = LowerStatement(stmt: s);
                    stmts.Add(item: n);
                    if (!ReferenceEquals(n, s)) changed = true;
                }
                return changed ? b with { Statements = stmts } : b;
            }

            case WhileStatement w:
                return LowerWhile(whileStmt: w);

            case LoopStatement loop:
            {
                Statement body = LowerStatement(stmt: loop.Body);
                if (ReferenceEquals(body, loop.Body)) return loop;
                return loop with { Body = body };
            }

            case IfStatement ifs:
            {
                Statement then = LowerStatement(stmt: ifs.ThenStatement);
                Statement? elseS = ifs.ElseStatement != null
                    ? LowerStatement(stmt: ifs.ElseStatement)
                    : null;
                bool tc = !ReferenceEquals(then, ifs.ThenStatement);
                bool ec = !ReferenceEquals(elseS, ifs.ElseStatement);
                return tc || ec ? ifs with { ThenStatement = then, ElseStatement = elseS } : ifs;
            }

            case WhenStatement w:
            {
                bool changed = false;
                var clauses = new List<WhenClause>(capacity: w.Clauses.Count);
                foreach (WhenClause c in w.Clauses)
                {
                    Statement body = LowerStatement(stmt: c.Body);
                    if (!ReferenceEquals(body, c.Body))
                    {
                        clauses.Add(item: c with { Body = body });
                        changed = true;
                    }
                    else
                    {
                        clauses.Add(item: c);
                    }
                }
                return changed ? w with { Clauses = clauses } : w;
            }

            case UsingStatement u:
            {
                Statement body = LowerStatement(stmt: u.Body);
                return !ReferenceEquals(body, u.Body) ? u with { Body = body } : u;
            }

            case DangerStatement d:
            {
                // DangerStatement.Body is BlockStatement; LowerStatement on BlockStatement
                // always returns a BlockStatement so the cast is safe.
                Statement lowered = LowerStatement(stmt: d.Body);
                return !ReferenceEquals(lowered, d.Body)
                    ? d with { Body = (BlockStatement)lowered }
                    : d;
            }

            default:
                return stmt;
        }
    }

    /// <summary>
    /// Lowers <c>while cond { body }</c> to <c>loop { if !cond { break } body }</c>.
    /// The else branch (if present) is dropped — while-else is not yet fully implemented.
    /// </summary>
    private Statement LowerWhile(WhileStatement whileStmt)
    {
        SourceLocation loc = whileStmt.Location;
        Statement loweredBody = LowerStatement(stmt: whileStmt.Body);

        // Build: if !cond { break }
        Expression negCond = new UnaryExpression(
            Operator: UnaryOperator.Not,
            Operand: whileStmt.Condition,
            Location: loc);
        Statement guardBreak = new IfStatement(
            Condition: negCond,
            ThenStatement: new BlockStatement(
                Statements: [new BreakStatement(Location: loc)],
                Location: loc),
            ElseStatement: null,
            Location: loc);

        // Build: loop { if !cond { break }; body }
        Statement loopBody = loweredBody is BlockStatement block
            ? block with { Statements = [guardBreak, .. block.Statements] }
            : new BlockStatement(Statements: [guardBreak, loweredBody], Location: loc);

        return new LoopStatement(Body: loopBody, Location: loc);
    }

    private Statement LowerFor(ForStatement forStmt)
    {
        // Defer tuple destructuring (for (a, b) in pairs)
        if (forStmt.VariablePattern != null) return forStmt;
        // Defer for-else (requires _lf_broke tracking to preserve semantics)
        if (forStmt.ElseBranch != null) return forStmt;

        SourceLocation loc = forStmt.Location;
        string iterName = $"_lf_iter_{_iterCount++}";

        // "_" is the discard identifier — map to null so ElsePattern doesn't bind a variable
        string? varName = forStmt.Variable == "_" ? null : forStmt.Variable;

        // ─── Build: var _lf_iter_N = iterable.$iter() ───────────────────────
        Expression iterCallExpr = new CallExpression(
            Callee: new MemberExpression(
                Object: forStmt.Iterable,
                PropertyName: "$iter",
                Location: loc),
            Arguments: [],
            Location: loc);

        Statement iterVarStmt = new DeclarationStatement(
            Declaration: new VariableDeclaration(
                Name: iterName,
                Type: null,
                Initializer: iterCallExpr,
                Visibility: VisibilityModifier.Secret,
                Location: loc),
            Location: loc);

        // ─── Build: _lf_iter_N.try_next() ───────────────────────────────────
        Expression tryNextCallExpr = new CallExpression(
            Callee: new MemberExpression(
                Object: new IdentifierExpression(Name: iterName, Location: loc),
                PropertyName: "try_next",
                Location: loc),
            Arguments: [],
            Location: loc);

        // Recursively lower nested for-loops inside the body before embedding it
        Statement loweredBody = LowerStatement(stmt: forStmt.Body);

        // ─── Build: is None → break ──────────────────────────────────────────
        var noneClause = new WhenClause(
            Pattern: new NonePattern(Location: loc),
            Body: new BlockStatement(
                Statements: [new BreakStatement(Location: loc)],
                Location: loc),
            Location: loc);

        // ─── Build: else var v → body ─────────────────────────────────────────
        var elseClause = new WhenClause(
            Pattern: new ElsePattern(VariableName: varName, Location: loc),
            Body: loweredBody,
            Location: loc);

        // ─── Build: when _lf_iter_N.try_next() { is None → break; else var v → body } ──
        var whenStmt = new WhenStatement(
            Expression: tryNextCallExpr,
            Clauses: [noneClause, elseClause],
            Location: loc);

        // ─── Build: loop { when ... } ────────────────────────────────────────
        var loopStmt = new LoopStatement(
            Body: new BlockStatement(
                Statements: [whenStmt],
                Location: loc),
            Location: loc);

        // ─── Final: { var _lf_iter_N = ...; loop { ... } } ──────────────────
        return new BlockStatement(
            Statements: [iterVarStmt, loopStmt],
            Location: loc);
    }

    /// <summary>
    /// Lowers control flow in all synthesized variant bodies in
    /// <see cref="DesugaringContext.VariantBodies"/>.
    /// Called from <see cref="DesugaringPipeline.RunGlobal"/> after variant bodies are generated,
    /// so that bodies copied from unlowered stdlib originals get their WhileStatement nodes
    /// converted to LoopStatement before OperatorLoweringPass and codegen see them.
    /// </summary>
    public void RunOnVariantBodies()
    {
        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            Statement body = ctx.VariantBodies[key];
            Statement lowered = LowerStatement(stmt: body);
            if (!ReferenceEquals(lowered, body))
                ctx.VariantBodies[key] = lowered;
        }
    }
}