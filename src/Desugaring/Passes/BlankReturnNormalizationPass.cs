using SyntaxTree;

namespace Compiler.Desugaring.Passes;

/// <summary>
/// D-AST-0: Normalizes null return types and bare return statements.
/// <list type="bullet">
///   <item><see cref="RoutineDeclaration"/> with <c>ReturnType == null</c> (no <c>-&gt;</c> written)
///         → <c>ReturnType = TypeExpression("Blank")</c>.</item>
///   <item><see cref="ReturnStatement"/> with <c>Value == null</c> (bare <c>return</c>)
///         → <c>Value = IdentifierExpression("Blank")</c>.</item>
/// </list>
/// After this pass, <c>null</c> in either position is a genuine unresolved error.
/// Runs as the very first pass in <see cref="DesugaringPipeline.Run"/>.
/// Does NOT touch <see cref="ExternalDeclaration"/> (null ReturnType = void in C interop)
/// or <see cref="VariantReturnStatement"/> (null Value = FromAbsent, semantically meaningful).
/// </summary>
internal sealed class BlankReturnNormalizationPass(DesugaringContext ctx)
{
    public void Run(Program program)
    {
        for (int i = 0; i < program.Declarations.Count; i++)
        {
            if (program.Declarations[i] is SyntaxTree.Declaration decl)
                program.Declarations[i] = NormalizeDeclaration(decl: decl);
        }
    }

    private SyntaxTree.Declaration NormalizeDeclaration(SyntaxTree.Declaration decl)
    {
        switch (decl)
        {
            case RoutineDeclaration r:
                return NormalizeRoutine(r: r);

            case EntityDeclaration e:
                NormalizeMemberList(members: e.Members);
                return e;

            case RecordDeclaration rec:
                NormalizeMemberList(members: rec.Members);
                return rec;

            case CrashableDeclaration cr:
                NormalizeMemberList(members: cr.Members);
                return cr;

            default:
                return decl;
        }
    }

    private void NormalizeMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i] is RoutineDeclaration r)
                members[i] = NormalizeRoutine(r: r);
        }
    }

    private RoutineDeclaration NormalizeRoutine(RoutineDeclaration r)
    {
        TypeExpression? returnType = r.ReturnType
            ?? new TypeExpression(Name: "Blank", GenericArguments: null, Location: r.Location);
        Statement body = NormalizeStatement(stmt: r.Body);
        if (ReferenceEquals(returnType, r.ReturnType) && ReferenceEquals(body, r.Body))
            return r;
        return r with { ReturnType = returnType, Body = body };
    }

    private Statement NormalizeStatement(Statement stmt)
    {
        switch (stmt)
        {
            case ReturnStatement { Value: null } ret:
                return ret with
                {
                    Value = new IdentifierExpression(Name: "Blank", Location: ret.Location)
                };

            case BlockStatement b:
            {
                var stmts = b.Statements;
                List<Statement>? replaced = null;
                for (int i = 0; i < stmts.Count; i++)
                {
                    Statement lowered = NormalizeStatement(stmt: stmts[i]);
                    if (!ReferenceEquals(lowered, stmts[i]))
                    {
                        replaced ??= new List<Statement>(stmts);
                        replaced[i] = lowered;
                    }
                }
                return replaced != null ? b with { Statements = replaced } : b;
            }

            case IfStatement ifs:
            {
                Statement thenN = NormalizeStatement(stmt: ifs.ThenStatement);
                Statement? elseN = ifs.ElseStatement != null
                    ? NormalizeStatement(stmt: ifs.ElseStatement)
                    : null;
                if (ReferenceEquals(thenN, ifs.ThenStatement) && ReferenceEquals(elseN, ifs.ElseStatement))
                    return ifs;
                return ifs with { ThenStatement = thenN, ElseStatement = elseN };
            }

            case LoopStatement loop:
            {
                Statement bodyN = NormalizeStatement(stmt: loop.Body);
                if (ReferenceEquals(bodyN, loop.Body)) return loop;
                return loop with { Body = bodyN };
            }

            case WhenStatement ws:
            {
                bool changed = false;
                var clauses = new List<WhenClause>(capacity: ws.Clauses.Count);
                foreach (WhenClause c in ws.Clauses)
                {
                    Statement bodyN = NormalizeStatement(stmt: c.Body);
                    changed |= !ReferenceEquals(bodyN, c.Body);
                    clauses.Add(item: c with { Body = bodyN });
                }
                return changed ? ws with { Clauses = clauses } : ws;
            }

            case DeclarationStatement { Declaration: RoutineDeclaration r } ds:
            {
                RoutineDeclaration rN = NormalizeRoutine(r: r);
                if (ReferenceEquals(rN, r)) return ds;
                return ds with { Declaration = rN };
            }

            default:
                return stmt;
        }
    }
}