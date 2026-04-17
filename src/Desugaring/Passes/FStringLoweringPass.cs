using Compiler.Lexer;
using SemanticVerification.Types;
using SyntaxTree;

namespace Compiler.Desugaring.Passes;

/// <summary>
/// Lowers <see cref="InsertedTextExpression"/> f-strings to <c>$represent</c>/<c>$diagnose</c>
/// method calls folded with <c>Text.$add</c>.
/// Runs after <see cref="ExpressionLoweringPass"/> and before <see cref="OperatorLoweringPass"/>
/// in the per-file desugaring pipeline.
///
/// <para>Part conversion:</para>
/// <list type="bullet">
///   <item><c>TextPart("text")</c> → <c>LiteralExpression("text")</c></item>
///   <item><c>ExpressionPart(e, null)</c> → <c>e.$represent()</c></item>
///   <item><c>ExpressionPart(e, "?")</c> → <c>e.$diagnose()</c></item>
///   <item><c>ExpressionPart(e, "=")</c> → <c>"name=" + e.$represent()</c></item>
///   <item><c>ExpressionPart(e, "=?")</c> → <c>"name=" + e.$diagnose()</c></item>
/// </list>
///
/// <para>Scope: per-file user code only. WiredRoutinePass-generated f-string bodies
/// (stored in <c>ctx.VariantBodies</c>) are not processed here — codegen's
/// <c>EmitInsertedText</c> handles those.</para>
/// </summary>
internal sealed class FStringLoweringPass(DesugaringContext ctx)
{
    public void Run(Program program)
    {
        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[i])
            {
                case RoutineDeclaration r:
                {
                    Statement newBody = LowerStatement(r.Body);
                    if (!ReferenceEquals(newBody, r.Body))
                        program.Declarations[i] = r with { Body = newBody };
                    break;
                }

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
        for (int j = 0; j < members.Count; j++)
        {
            if (members[j] is not RoutineDeclaration m) continue;
            Statement newBody = LowerStatement(m.Body);
            if (!ReferenceEquals(newBody, m.Body))
                members[j] = m with { Body = newBody };
        }
    }

    // ── Statement lowering ────────────────────────────────────────────────────

    private Statement LowerStatement(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement b:
            {
                List<Statement> stmts = LowerStatementList(b.Statements);
                return ReferenceEquals(stmts, b.Statements) ? stmt : b with { Statements = stmts };
            }

            case IfStatement ifs:
            {
                Expression cond = LowerExpression(ifs.Condition);
                Statement then = LowerStatement(ifs.ThenStatement);
                Statement? elseS = ifs.ElseStatement != null
                    ? LowerStatement(ifs.ElseStatement)
                    : null;
                bool changed = !ReferenceEquals(cond, ifs.Condition)
                               || !ReferenceEquals(then, ifs.ThenStatement)
                               || !ReferenceEquals(elseS, ifs.ElseStatement);
                return changed
                    ? ifs with { Condition = cond, ThenStatement = then, ElseStatement = elseS }
                    : stmt;
            }

            case WhileStatement w:
            {
                Expression cond = LowerExpression(w.Condition);
                Statement body = LowerStatement(w.Body);
                Statement? elseB = w.ElseBranch != null ? LowerStatement(w.ElseBranch) : null;
                bool changed = !ReferenceEquals(cond, w.Condition)
                               || !ReferenceEquals(body, w.Body)
                               || !ReferenceEquals(elseB, w.ElseBranch);
                return changed
                    ? w with { Condition = cond, Body = body, ElseBranch = elseB }
                    : stmt;
            }

            case LoopStatement loop:
            {
                Statement body = LowerStatement(loop.Body);
                return ReferenceEquals(body, loop.Body) ? stmt : loop with { Body = body };
            }

            case WhenStatement w:
            {
                Expression subj = LowerExpression(w.Expression);
                var clauses = new List<WhenClause>(capacity: w.Clauses.Count);
                bool clauseChanged = false;
                foreach (WhenClause c in w.Clauses)
                {
                    Statement lBody = LowerStatement(c.Body);
                    if (!ReferenceEquals(lBody, c.Body))
                    {
                        clauses.Add(c with { Body = lBody });
                        clauseChanged = true;
                    }
                    else
                    {
                        clauses.Add(c);
                    }
                }

                bool changed = !ReferenceEquals(subj, w.Expression) || clauseChanged;
                return changed ? w with { Expression = subj, Clauses = clauses } : stmt;
            }

            case UsingStatement u:
            {
                Expression res = LowerExpression(u.Resource);
                Statement body = LowerStatement(u.Body);
                bool changed = !ReferenceEquals(res, u.Resource) || !ReferenceEquals(body, u.Body);
                return changed ? u with { Resource = res, Body = body } : stmt;
            }

            case DangerStatement d:
            {
                Statement body = LowerStatement(d.Body);
                return ReferenceEquals(body, d.Body)
                    ? stmt
                    : d with { Body = (BlockStatement)body };
            }

            case AssignmentStatement asgn:
            {
                Expression val = LowerExpression(asgn.Value);
                return ReferenceEquals(val, asgn.Value) ? stmt : asgn with { Value = val };
            }

            case DeclarationStatement { Declaration: VariableDeclaration vd } decl
                when vd.Initializer != null:
            {
                Expression init = LowerExpression(vd.Initializer);
                return ReferenceEquals(init, vd.Initializer)
                    ? stmt
                    : decl with { Declaration = vd with { Initializer = init } };
            }

            case ReturnStatement { Value: not null } ret:
            {
                Expression val = LowerExpression(ret.Value);
                return ReferenceEquals(val, ret.Value) ? stmt : ret with { Value = val };
            }

            case ExpressionStatement es:
            {
                Expression e = LowerExpression(es.Expression);
                return ReferenceEquals(e, es.Expression) ? stmt : es with { Expression = e };
            }

            case DiscardStatement ds:
            {
                Expression e = LowerExpression(ds.Expression);
                return ReferenceEquals(e, ds.Expression) ? stmt : ds with { Expression = e };
            }

            case BecomesStatement bs:
            {
                Expression val = LowerExpression(bs.Value);
                return ReferenceEquals(val, bs.Value) ? stmt : bs with { Value = val };
            }

            case ThrowStatement t:
            {
                Expression err = LowerExpression(t.Error);
                return ReferenceEquals(err, t.Error) ? stmt : t with { Error = err };
            }

            default:
                return stmt;
        }
    }

    private List<Statement> LowerStatementList(List<Statement> stmts)
    {
        var result = new List<Statement>(capacity: stmts.Count);
        bool anyChanged = false;
        foreach (Statement stmt in stmts)
        {
            Statement lowered = LowerStatement(stmt);
            result.Add(lowered);
            if (!ReferenceEquals(lowered, stmt)) anyChanged = true;
        }

        return anyChanged ? result : stmts;
    }

    // ── Expression lowering ───────────────────────────────────────────────────

    private Expression LowerExpression(Expression expr)
    {
        switch (expr)
        {
            case InsertedTextExpression ftext:
                return LowerFString(ftext);

            case BinaryExpression bin:
            {
                Expression left = LowerExpression(bin.Left);
                Expression right = LowerExpression(bin.Right);
                return ReferenceEquals(left, bin.Left) && ReferenceEquals(right, bin.Right)
                    ? expr
                    : bin with { Left = left, Right = right };
            }

            case UnaryExpression unary:
            {
                Expression operand = LowerExpression(unary.Operand);
                return ReferenceEquals(operand, unary.Operand)
                    ? expr
                    : unary with { Operand = operand };
            }

            case CallExpression call:
            {
                Expression callee = LowerExpression(call.Callee);
                var args = new List<Expression>(capacity: call.Arguments.Count);
                bool argsChanged = false;
                foreach (Expression arg in call.Arguments)
                {
                    Expression lowered = LowerExpression(arg);
                    args.Add(lowered);
                    if (!ReferenceEquals(lowered, arg)) argsChanged = true;
                }

                return !argsChanged && ReferenceEquals(callee, call.Callee)
                    ? expr
                    : call with { Callee = callee, Arguments = args };
            }

            case MemberExpression mem:
            {
                Expression obj = LowerExpression(mem.Object);
                return ReferenceEquals(obj, mem.Object) ? expr : mem with { Object = obj };
            }

            case NamedArgumentExpression named:
            {
                Expression val = LowerExpression(named.Value);
                return ReferenceEquals(val, named.Value) ? expr : named with { Value = val };
            }

            case IndexExpression idx:
            {
                Expression obj = LowerExpression(idx.Object);
                Expression index = LowerExpression(idx.Index);
                return ReferenceEquals(obj, idx.Object) && ReferenceEquals(index, idx.Index)
                    ? expr
                    : idx with { Object = obj, Index = index };
            }

            case SliceExpression slice:
            {
                Expression obj = LowerExpression(slice.Object);
                Expression start = LowerExpression(slice.Start);
                Expression end = LowerExpression(slice.End);
                return ReferenceEquals(obj, slice.Object)
                       && ReferenceEquals(start, slice.Start)
                       && ReferenceEquals(end, slice.End)
                    ? expr
                    : slice with { Object = obj, Start = start, End = end };
            }

            case CreatorExpression creator:
            {
                var members = new List<(string Name, Expression Value)>(
                    capacity: creator.MemberVariables.Count);
                bool changed = false;
                foreach ((string name, Expression value) in creator.MemberVariables)
                {
                    Expression lowered = LowerExpression(value);
                    members.Add((name, lowered));
                    if (!ReferenceEquals(lowered, value)) changed = true;
                }

                return changed ? creator with { MemberVariables = members } : expr;
            }

            case WithExpression withExpr:
            {
                Expression loweredBase = LowerExpression(withExpr.Base);
                var updates =
                    new List<(List<string>? Path, Expression? Index, Expression Value)>(
                        capacity: withExpr.Updates.Count);
                bool changed = !ReferenceEquals(loweredBase, withExpr.Base);
                foreach ((List<string>? path, Expression? index, Expression value) in
                         withExpr.Updates)
                {
                    Expression loweredVal = LowerExpression(value);
                    updates.Add((path, index, loweredVal));
                    if (!ReferenceEquals(loweredVal, value)) changed = true;
                }

                return changed ? withExpr with { Base = loweredBase, Updates = updates } : expr;
            }

            case GenericMethodCallExpression gmc:
            {
                Expression obj = LowerExpression(gmc.Object);
                var args = new List<Expression>(capacity: gmc.Arguments.Count);
                bool argsChanged = false;
                foreach (Expression arg in gmc.Arguments)
                {
                    Expression lowered = LowerExpression(arg);
                    args.Add(lowered);
                    if (!ReferenceEquals(lowered, arg)) argsChanged = true;
                }

                return !argsChanged && ReferenceEquals(obj, gmc.Object)
                    ? expr
                    : gmc with { Object = obj, Arguments = args };
            }

            case GenericMemberExpression gme:
            {
                Expression obj = LowerExpression(gme.Object);
                return ReferenceEquals(obj, gme.Object) ? expr : gme with { Object = obj };
            }

            case CompoundAssignmentExpression compound:
            {
                Expression val = LowerExpression(compound.Value);
                return ReferenceEquals(val, compound.Value)
                    ? expr
                    : compound with { Value = val };
            }

            case StealExpression steal:
            {
                Expression operand = LowerExpression(steal.Operand);
                return ReferenceEquals(operand, steal.Operand)
                    ? expr
                    : steal with { Operand = operand };
            }

            case ConditionalExpression cond:
            {
                Expression condExpr = LowerExpression(cond.Condition);
                Expression thenExpr = LowerExpression(cond.TrueExpression);
                Expression elseExpr = LowerExpression(cond.FalseExpression);
                return ReferenceEquals(condExpr, cond.Condition)
                       && ReferenceEquals(thenExpr, cond.TrueExpression)
                       && ReferenceEquals(elseExpr, cond.FalseExpression)
                    ? expr
                    : cond with
                    {
                        Condition = condExpr,
                        TrueExpression = thenExpr,
                        FalseExpression = elseExpr
                    };
            }

            case TupleLiteralExpression tuple:
            {
                var elems = new List<Expression>(capacity: tuple.Elements.Count);
                bool changed = false;
                foreach (Expression el in tuple.Elements)
                {
                    Expression lowered = LowerExpression(el);
                    elems.Add(lowered);
                    if (!ReferenceEquals(lowered, el)) changed = true;
                }

                return changed ? tuple with { Elements = elems } : expr;
            }

            case ListLiteralExpression list:
            {
                var elems = new List<Expression>(capacity: list.Elements.Count);
                bool changed = false;
                foreach (Expression el in list.Elements)
                {
                    Expression lowered = LowerExpression(el);
                    elems.Add(lowered);
                    if (!ReferenceEquals(lowered, el)) changed = true;
                }

                return changed ? list with { Elements = elems } : expr;
            }

            default:
                // LiteralExpression, IdentifierExpression, TypeExpression, RangeExpression
                // (lowered earlier), LambdaExpression, DictLiteralExpression,
                // SetLiteralExpression, DictEntryLiteralExpression, TypeIdExpression, etc.
                return expr;
        }
    }

    // ── F-string lowering ─────────────────────────────────────────────────────

    /// <summary>
    /// Converts an <see cref="InsertedTextExpression"/> to a left-folded chain of
    /// <c>Text.$add</c> calls interleaved with <c>$represent</c>/<c>$diagnose</c> calls.
    /// </summary>
    private Expression LowerFString(InsertedTextExpression ftext)
    {
        TypeInfo? textType = ctx.Registry.LookupType(name: "Text");
        SourceLocation loc = ftext.Location;

        // Collect lowered Text expressions for each part (skipping empty text parts).
        var exprs = new List<Expression>(capacity: ftext.Parts.Count);
        foreach (InsertedTextPart part in ftext.Parts)
        {
            switch (part)
            {
                case TextPart tp when tp.Text.Length > 0:
                    exprs.Add(new LiteralExpression(
                        Value: tp.Text, LiteralType: TokenType.TextLiteral, Location: tp.Location)
                        { ResolvedType = textType });
                    break;

                case ExpressionPart ep:
                {
                    Expression loweredInner = LowerExpression(ep.Expression);
                    string methodName = ep.FormatSpec is "?" or "=?" ? "$diagnose" : "$represent";

                    // "=" and "=?" format specs prepend "varName=" as a text literal.
                    if (ep.FormatSpec is "=" or "=?")
                    {
                        string varName = ep.Expression is IdentifierExpression id ? id.Name : "";
                        if (varName.Length > 0)
                        {
                            exprs.Add(new LiteralExpression(
                                Value: varName + "=",
                                LiteralType: TokenType.TextLiteral,
                                Location: ep.Location) { ResolvedType = textType });
                        }
                    }

                    exprs.Add(new CallExpression(
                        Callee: new MemberExpression(
                            Object: loweredInner,
                            PropertyName: methodName,
                            Location: ep.Location),
                        Arguments: [],
                        Location: ep.Location) { ResolvedType = textType });
                    break;
                }
            }
        }

        if (exprs.Count == 0)
        {
            return new LiteralExpression(
                Value: "", LiteralType: TokenType.TextLiteral, Location: loc)
                { ResolvedType = textType };
        }

        // Left-fold: acc = acc.$add(other: next)
        Expression result = exprs[0];
        for (int i = 1; i < exprs.Count; i++)
        {
            result = new CallExpression(
                Callee: new MemberExpression(
                    Object: result,
                    PropertyName: "$add",
                    Location: loc),
                Arguments:
                [
                    new NamedArgumentExpression(
                        Name: "other",
                        Value: exprs[i],
                        Location: loc)
                ],
                Location: loc) { ResolvedType = textType };
        }

        return result;
    }
}
