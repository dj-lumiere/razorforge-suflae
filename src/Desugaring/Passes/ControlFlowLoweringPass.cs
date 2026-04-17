using Compiler.Lexer;
using SyntaxTree;

namespace Compiler.Desugaring.Passes;

/// <summary>
/// Lowers all loop constructs to the <see cref="LoopStatement"/> primitive so the code
/// generator only needs to handle one loop form via <c>EmitLoop</c>.
///
/// <para><b>while</b> → loop+if-break:</para>
/// <code>
/// while cond { body }
///   ↓
/// loop { if !cond { break }; body }
/// </code>
///
/// <para><b>for v in iterable</b> → loop+when:</para>
/// <code>
/// {
///   var _lf_iter_N = iterable.$iter()
///   loop { when _lf_iter_N.try_next() { is None → break; else var v → body } }
/// }
/// </code>
///
/// <para><b>for (a, b) in pairs</b> (tuple destructuring) → same loop shape, else
/// body prepends positional member-access bindings:</para>
/// <code>
/// {
///   var _lf_iter_N = pairs.$iter()
///   loop {
///     when _lf_iter_N.try_next() {
///       is None → break
///       else var _lf_elem_M → { var a = _lf_elem_M.item0; var b = _lf_elem_M.item1; body }
///     }
///   }
/// }
/// </code>
///
/// <para><b>for x in iterable else { alt }</b> (for-else) → exhaustion flag:</para>
/// <code>
/// {
///   var _lf_exhausted_N: Bool = false
///   var _lf_iter_N = iterable.$iter()
///   loop {
///     when _lf_iter_N.try_next() {
///       is None → { _lf_exhausted_N = true; break }
///       else var x → body
///     }
///   }
///   if _lf_exhausted_N { alt }
/// }
/// </code>
///
/// <para>Range-based loops (<c>for x in 0 to n</c>) are also covered: the
/// <c>RangeExpression</c> iterable is converted to <c>Range[T](...)</c> by
/// <see cref="ExpressionLoweringPass"/> (which runs after this pass).</para>
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
        SourceLocation loc = forStmt.Location;
        int n = _iterCount++;
        string iterName = $"_lf_iter_{n}";

        // ─── Shared: var _lf_iter_N = iterable.$iter() ──────────────────────
        Statement iterVarStmt = new DeclarationStatement(
            Declaration: new VariableDeclaration(
                Name: iterName,
                Type: null,
                Initializer: new CallExpression(
                    Callee: new MemberExpression(
                        Object: forStmt.Iterable,
                        PropertyName: "$iter",
                        Location: loc),
                    Arguments: [],
                    Location: loc),
                Visibility: VisibilityModifier.Secret,
                Location: loc),
            Location: loc);

        // ─── Shared: _lf_iter_N.try_next() ──────────────────────────────────
        Expression tryNextCall = new CallExpression(
            Callee: new MemberExpression(
                Object: new IdentifierExpression(Name: iterName, Location: loc),
                PropertyName: "try_next",
                Location: loc),
            Arguments: [],
            Location: loc);

        // ─── Recursively lower the user body first ───────────────────────────
        Statement loweredBody = LowerStatement(stmt: forStmt.Body);

        // ─── Build the else-clause body ──────────────────────────────────────
        Statement elseBody;
        string? elseVarName;

        if (forStmt.VariablePattern != null)
        {
            // Tuple destructuring: else var _lf_elem_M → { var a = elem.item0; var b = elem.item1; ... body }
            string elemName = $"_lf_elem_{n}";
            elseVarName = elemName;

            // Prepend: var a = _lf_elem_M.item0, var b = _lf_elem_M.item1, …
            var bindStmts = new List<Statement>(capacity: forStmt.VariablePattern.Bindings.Count + 1);
            for (int i = 0; i < forStmt.VariablePattern.Bindings.Count; i++)
            {
                DestructuringBinding binding = forStmt.VariablePattern.Bindings[index: i];
                string bindName = binding.BindingName ?? binding.MemberVariableName ?? $"_lf_b{i}";
                if (bindName == "_") continue;

                bindStmts.Add(item: new DeclarationStatement(
                    Declaration: new VariableDeclaration(
                        Name: bindName,
                        Type: null,
                        Initializer: new MemberExpression(
                            Object: new IdentifierExpression(Name: elemName, Location: loc),
                            PropertyName: $"item{i}",
                            Location: loc),
                        Visibility: VisibilityModifier.Secret,
                        Location: loc),
                    Location: loc));
            }

            if (loweredBody is BlockStatement bodyBlock)
            {
                bindStmts.AddRange(collection: bodyBlock.Statements);
                elseBody = bodyBlock with { Statements = bindStmts };
            }
            else
            {
                bindStmts.Add(item: loweredBody);
                elseBody = new BlockStatement(Statements: bindStmts, Location: loc);
            }
        }
        else
        {
            // Simple variable or discard
            elseVarName = forStmt.Variable == "_" ? null : forStmt.Variable;
            elseBody    = loweredBody;
        }

        // ─── Build None and else clauses ─────────────────────────────────────
        Statement? elseBranchLowered = forStmt.ElseBranch != null
            ? LowerStatement(stmt: forStmt.ElseBranch)
            : null;

        Statement noneBody;
        if (elseBranchLowered != null)
        {
            // For-else: set exhausted flag, then break
            string exhaustedName = $"_lf_exhausted_{n}";
            noneBody = new BlockStatement(
                Statements:
                [
                    new AssignmentStatement(
                        Target: new IdentifierExpression(Name: exhaustedName, Location: loc),
                        Value: new LiteralExpression(Value: true, LiteralType: TokenType.True,
                            Location: loc),
                        Location: loc),
                    new BreakStatement(Location: loc)
                ],
                Location: loc);

            var noneClause = new WhenClause(Pattern: new NonePattern(Location: loc), Body: noneBody,
                Location: loc);
            var elseClause = new WhenClause(
                Pattern: new ElsePattern(VariableName: elseVarName, Location: loc),
                Body: elseBody, Location: loc);

            var whenStmt = new WhenStatement(Expression: tryNextCall,
                Clauses: [noneClause, elseClause], Location: loc);
            var loopStmt = new LoopStatement(
                Body: new BlockStatement(Statements: [whenStmt], Location: loc), Location: loc);

            // var _lf_exhausted_N: Bool = false
            Statement exhaustedVarStmt = new DeclarationStatement(
                Declaration: new VariableDeclaration(
                    Name: exhaustedName,
                    Type: new TypeExpression(Name: "Bool", GenericArguments: null, Location: loc),
                    Initializer: new LiteralExpression(Value: false, LiteralType: TokenType.False,
                        Location: loc),
                    Visibility: VisibilityModifier.Secret,
                    Location: loc),
                Location: loc);

            // if _lf_exhausted_N { alt }
            Statement exhaustionCheck = new IfStatement(
                Condition: new IdentifierExpression(Name: exhaustedName, Location: loc),
                ThenStatement: elseBranchLowered,
                ElseStatement: null,
                Location: loc);

            return new BlockStatement(
                Statements: [exhaustedVarStmt, iterVarStmt, loopStmt, exhaustionCheck],
                Location: loc);
        }
        else
        {
            // Plain for (no else branch)
            noneBody = new BlockStatement(
                Statements: [new BreakStatement(Location: loc)], Location: loc);

            var noneClause = new WhenClause(Pattern: new NonePattern(Location: loc), Body: noneBody,
                Location: loc);
            var elseClause = new WhenClause(
                Pattern: new ElsePattern(VariableName: elseVarName, Location: loc),
                Body: elseBody, Location: loc);

            var whenStmt = new WhenStatement(Expression: tryNextCall,
                Clauses: [noneClause, elseClause], Location: loc);
            var loopStmt = new LoopStatement(
                Body: new BlockStatement(Statements: [whenStmt], Location: loc), Location: loc);

            return new BlockStatement(Statements: [iterVarStmt, loopStmt], Location: loc);
        }
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