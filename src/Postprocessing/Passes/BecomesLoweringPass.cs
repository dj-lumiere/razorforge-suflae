using SyntaxTree;

namespace Compiler.Postprocessing.Passes;

/// <summary>
/// Future Phase 7 pass: lower block-expression result flow (`becomes`) into explicit
/// temporaries and assignments after verification is complete.
/// </summary>
internal sealed class BecomesLoweringPass(PostprocessingContext ctx)
{
    public void Run(Program program)
    {
        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[i])
            {
                case RoutineDeclaration routine:
                {
                    Statement lowered = LowerStatement(routine.Body);
                    if (!ReferenceEquals(lowered, routine.Body))
                    {
                        program.Declarations[i] = routine with { Body = lowered };
                    }

                    break;
                }

                case EntityDeclaration entity:
                    LowerMemberList(entity.Members);
                    break;

                case RecordDeclaration record:
                    LowerMemberList(record.Members);
                    break;

                case CrashableDeclaration crashable:
                    LowerMemberList(crashable.Members);
                    break;
            }
        }
    }

    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i] is not RoutineDeclaration routine)
            {
                continue;
            }

            Statement lowered = LowerStatement(routine.Body);
            if (!ReferenceEquals(lowered, routine.Body))
            {
                members[i] = routine with { Body = lowered };
            }
        }
    }

    private Statement LowerStatement(Statement statement)
    {
        return statement switch
        {
            BlockStatement block => LowerBlock(block),
            IfStatement ifs => LowerIf(ifs),
            WhileStatement whileStmt => LowerWhile(whileStmt),
            LoopStatement loop => LowerLoop(loop),
            ForStatement forStmt => LowerFor(forStmt),
            WhenStatement whenStmt => LowerWhen(whenStmt),
            UsingStatement usingStmt => LowerUsing(usingStmt),
            DangerStatement danger => LowerDanger(danger),
            _ => statement
        };
    }

    private Statement LowerBlock(BlockStatement block)
    {
        var loweredStatements = new List<Statement>(capacity: block.Statements.Count);
        bool changed = false;

        foreach (Statement statement in block.Statements)
        {
            Statement lowered = LowerStatement(statement);
            loweredStatements.Add(item: lowered);
            changed |= !ReferenceEquals(lowered, statement);
        }

        for (int i = 0; i < loweredStatements.Count - 1; i++)
        {
            if (TryGetSyntheticWhenResultTarget(loweredStatements[i], out IdentifierExpression? target) &&
                ContainsBecomes(loweredStatements[i + 1]))
            {
                loweredStatements[i + 1] = RewriteBecomes(loweredStatements[i + 1], target!);
                changed = true;
            }
        }

        return changed
            ? block with { Statements = loweredStatements }
            : block;
    }

    private Statement LowerIf(IfStatement ifs)
    {
        Statement thenStatement = LowerStatement(ifs.ThenStatement);
        Statement? elseStatement = ifs.ElseStatement != null
            ? LowerStatement(ifs.ElseStatement)
            : null;

        return !ReferenceEquals(thenStatement, ifs.ThenStatement) ||
               !ReferenceEquals(elseStatement, ifs.ElseStatement)
            ? ifs with { ThenStatement = thenStatement, ElseStatement = elseStatement }
            : ifs;
    }

    private Statement LowerWhile(WhileStatement whileStmt)
    {
        Statement body = LowerStatement(whileStmt.Body);
        Statement? elseBranch = whileStmt.ElseBranch != null
            ? LowerStatement(whileStmt.ElseBranch)
            : null;

        return !ReferenceEquals(body, whileStmt.Body) ||
               !ReferenceEquals(elseBranch, whileStmt.ElseBranch)
            ? whileStmt with { Body = body, ElseBranch = elseBranch }
            : whileStmt;
    }

    private Statement LowerLoop(LoopStatement loop)
    {
        Statement body = LowerStatement(loop.Body);
        return !ReferenceEquals(body, loop.Body)
            ? loop with { Body = body }
            : loop;
    }

    private Statement LowerFor(ForStatement forStmt)
    {
        Statement body = LowerStatement(forStmt.Body);
        Statement? elseBranch = forStmt.ElseBranch != null
            ? LowerStatement(forStmt.ElseBranch)
            : null;

        return !ReferenceEquals(body, forStmt.Body) ||
               !ReferenceEquals(elseBranch, forStmt.ElseBranch)
            ? forStmt with { Body = body, ElseBranch = elseBranch }
            : forStmt;
    }

    private Statement LowerWhen(WhenStatement whenStmt)
    {
        bool changed = false;
        var clauses = new List<WhenClause>(capacity: whenStmt.Clauses.Count);

        foreach (WhenClause clause in whenStmt.Clauses)
        {
            Statement loweredBody = LowerStatement(clause.Body);
            clauses.Add(item: !ReferenceEquals(loweredBody, clause.Body)
                ? clause with { Body = loweredBody }
                : clause);
            changed |= !ReferenceEquals(loweredBody, clause.Body);
        }

        return changed
            ? whenStmt with { Clauses = clauses }
            : whenStmt;
    }

    private Statement LowerUsing(UsingStatement usingStmt)
    {
        Statement body = LowerStatement(usingStmt.Body);
        return !ReferenceEquals(body, usingStmt.Body)
            ? usingStmt with { Body = body }
            : usingStmt;
    }

    private Statement LowerDanger(DangerStatement danger)
    {
        BlockStatement body = (BlockStatement)LowerStatement(danger.Body);
        return !ReferenceEquals(body, danger.Body)
            ? danger with { Body = body }
            : danger;
    }

    private static bool TryGetSyntheticWhenResultTarget(Statement statement,
        out IdentifierExpression? target)
    {
        target = null;

        if (statement is not DeclarationStatement
            {
                Declaration: VariableDeclaration
                {
                    Initializer: null
                } variable
            } ||
            !variable.Name.StartsWith(value: "_wres_", comparisonType: StringComparison.Ordinal))
        {
            return false;
        }

        target = new IdentifierExpression(Name: variable.Name, Location: variable.Location);
        return true;
    }

    private static bool ContainsBecomes(Statement statement)
    {
        return statement switch
        {
            BecomesStatement => true,
            BlockStatement block => block.Statements.Any(ContainsBecomes),
            IfStatement ifs =>
                ContainsBecomes(ifs.ThenStatement) ||
                ifs.ElseStatement != null && ContainsBecomes(ifs.ElseStatement),
            WhileStatement whileStmt =>
                ContainsBecomes(whileStmt.Body) ||
                whileStmt.ElseBranch != null && ContainsBecomes(whileStmt.ElseBranch),
            LoopStatement loop => ContainsBecomes(loop.Body),
            ForStatement forStmt =>
                ContainsBecomes(forStmt.Body) ||
                forStmt.ElseBranch != null && ContainsBecomes(forStmt.ElseBranch),
            WhenStatement whenStmt => whenStmt.Clauses.Any(clause => ContainsBecomes(clause.Body)),
            DangerStatement danger => ContainsBecomes(danger.Body),
            UsingStatement usingStmt => ContainsBecomes(usingStmt.Body),
            _ => false
        };
    }

    private static Statement RewriteBecomes(Statement statement, IdentifierExpression target)
    {
        return statement switch
        {
            BecomesStatement becomes => new AssignmentStatement(
                Target: target,
                Value: becomes.Value,
                Location: becomes.Location),
            BlockStatement block => block with
            {
                Statements = block.Statements
                    .Select(stmt => RewriteBecomes(stmt, target))
                    .ToList()
            },
            IfStatement ifs => ifs with
            {
                ThenStatement = RewriteBecomes(ifs.ThenStatement, target),
                ElseStatement = ifs.ElseStatement != null
                    ? RewriteBecomes(ifs.ElseStatement, target)
                    : null
            },
            WhileStatement whileStmt => whileStmt with
            {
                Body = RewriteBecomes(whileStmt.Body, target),
                ElseBranch = whileStmt.ElseBranch != null
                    ? RewriteBecomes(whileStmt.ElseBranch, target)
                    : null
            },
            LoopStatement loop => loop with
            {
                Body = RewriteBecomes(loop.Body, target)
            },
            ForStatement forStmt => forStmt with
            {
                Body = RewriteBecomes(forStmt.Body, target),
                ElseBranch = forStmt.ElseBranch != null
                    ? RewriteBecomes(forStmt.ElseBranch, target)
                    : null
            },
            WhenStatement whenStmt => whenStmt with
            {
                Clauses = whenStmt.Clauses
                    .Select(clause => clause with
                    {
                        Body = RewriteBecomes(clause.Body, target)
                    })
                    .ToList()
            },
            DangerStatement danger => danger with
            {
                Body = (BlockStatement)RewriteBecomes(danger.Body, target)
            },
            UsingStatement usingStmt => usingStmt with
            {
                Body = RewriteBecomes(usingStmt.Body, target)
            },
            _ => statement
        };
    }
}
