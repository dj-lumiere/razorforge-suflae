using System.Collections.Generic;
using System.Linq;
using Compiler.Synthesis;
using Compiler.Tokenizer;
using SyntaxTree;

namespace Compiler.Postprocessing.Passes;

/// <summary>
/// Phase 7: lowers compiler-synthesized <see cref="VariantReturnStatement"/> nodes into ordinary AST
/// so codegen needs no carrier-construction special-casing (and the dump shows no <c>#carrier</c>
/// pseudo-op).
///
/// STAGE 1 (current): only the <see cref="ErrorHandlingVariantKind.TryBool"/> variant, whose carrier
/// is a plain <c>Bool</c>. A <c>FromReturn</c> site becomes <c>return true</c>; a
/// <c>FromThrow</c>/<c>FromAbsent</c> site becomes <c>return false</c>. The thrown error is discarded
/// in a TryBool context (the routine returns Bool, never the error) — matching codegen's
/// <c>EmitTryBoolVariantReturn</c>, which drops it — so it is simply never constructed: nothing to
/// clean up, no leak. Try (Maybe) / Check (Result) / Lookup carriers still lower in codegen and are
/// handled by later stages.
/// </summary>
internal sealed class VariantReturnLoweringPass(PostprocessingContext ctx)
{
    private readonly Dictionary<string, Statement>? _variantBodies = ctx.VariantBodies;

    /// <summary>Lowers routine bodies in a single program (user file or stdlib file).</summary>
    public void Run(Program program)
    {
        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[i])
            {
                case RoutineDeclaration routine:
                {
                    Statement lowered = Lower(statement: routine.Body);
                    if (!ReferenceEquals(objA: lowered, objB: routine.Body))
                        program.Declarations[i] = routine with { Body = lowered };
                    break;
                }
                case EntityDeclaration entity:
                    LowerMembers(members: entity.Members);
                    break;
                case RecordDeclaration record:
                    LowerMembers(members: record.Members);
                    break;
                case CrashableDeclaration crashable:
                    LowerMembers(members: crashable.Members);
                    break;
            }
        }
    }

    /// <summary>Lowers the synthesized try_/check_/lookup_ variant bodies.</summary>
    public void RunOnVariantBodies()
    {
        if (_variantBodies == null) return;
        foreach (string key in _variantBodies.Keys.ToList())
        {
            Statement lowered = Lower(statement: _variantBodies[key: key]);
            if (!ReferenceEquals(objA: lowered, objB: _variantBodies[key: key]))
                _variantBodies[key: key] = lowered;
        }
    }

    private void LowerMembers(List<SyntaxTree.Declaration> members)
    {
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i] is not RoutineDeclaration routine) continue;
            Statement lowered = Lower(statement: routine.Body);
            if (!ReferenceEquals(objA: lowered, objB: routine.Body))
                members[i] = routine with { Body = lowered };
        }
    }

    private Statement Lower(Statement statement)
    {
        switch (statement)
        {
            case VariantReturnStatement { VariantKind: ErrorHandlingVariantKind.TryBool } vr:
            {
                bool present = vr.SiteKind == VariantSiteKind.FromReturn;
                return new ReturnStatement(
                    Value: new LiteralExpression(
                        Value: present,
                        LiteralType: present ? TokenType.True : TokenType.False,
                        Location: vr.Location),
                    Location: vr.Location);
            }

            case BlockStatement block:
            {
                List<Statement>? outStmts = null;
                for (int i = 0; i < block.Statements.Count; i++)
                {
                    Statement lowered = Lower(statement: block.Statements[i]);
                    if (!ReferenceEquals(objA: lowered, objB: block.Statements[i]) && outStmts == null)
                        outStmts = new List<Statement>(collection: block.Statements.Take(count: i));
                    outStmts?.Add(item: lowered);
                }
                return outStmts == null ? block : block with { Statements = outStmts };
            }

            case IfStatement ifs:
            {
                Statement then = Lower(statement: ifs.ThenStatement);
                Statement? els = ifs.ElseStatement != null ? Lower(statement: ifs.ElseStatement) : null;
                return ReferenceEquals(objA: then, objB: ifs.ThenStatement)
                       && ReferenceEquals(objA: els, objB: ifs.ElseStatement)
                    ? ifs
                    : ifs with { ThenStatement = then, ElseStatement = els };
            }

            case WhileStatement whileStmt:
            {
                Statement body = Lower(statement: whileStmt.Body);
                Statement? eb = whileStmt.ElseBranch != null ? Lower(statement: whileStmt.ElseBranch) : null;
                return ReferenceEquals(objA: body, objB: whileStmt.Body)
                       && ReferenceEquals(objA: eb, objB: whileStmt.ElseBranch)
                    ? whileStmt
                    : whileStmt with { Body = body, ElseBranch = eb };
            }

            case LoopStatement loop:
            {
                Statement body = Lower(statement: loop.Body);
                return ReferenceEquals(objA: body, objB: loop.Body) ? loop : loop with { Body = body };
            }

            case EachStatement each:
            {
                Statement body = Lower(statement: each.Body);
                Statement? eb = each.ElseBranch != null ? Lower(statement: each.ElseBranch) : null;
                return ReferenceEquals(objA: body, objB: each.Body)
                       && ReferenceEquals(objA: eb, objB: each.ElseBranch)
                    ? each
                    : each with { Body = body, ElseBranch = eb };
            }

            case WhenStatement whenStmt:
            {
                bool changed = false;
                var clauses = new List<WhenClause>(capacity: whenStmt.Clauses.Count);
                foreach (WhenClause clause in whenStmt.Clauses)
                {
                    Statement lb = Lower(statement: clause.Body);
                    clauses.Add(item: ReferenceEquals(objA: lb, objB: clause.Body)
                        ? clause
                        : clause with { Body = lb });
                    changed |= !ReferenceEquals(objA: lb, objB: clause.Body);
                }
                return changed ? whenStmt with { Clauses = clauses } : whenStmt;
            }

            case DangerStatement danger:
            {
                var body = (BlockStatement)Lower(statement: danger.Body);
                return ReferenceEquals(objA: body, objB: danger.Body) ? danger : danger with { Body = body };
            }

            case UsingStatement usingStmt:
            {
                Statement body = Lower(statement: usingStmt.Body);
                Statement? fb = usingStmt.FallbackBody != null ? Lower(statement: usingStmt.FallbackBody) : null;
                return ReferenceEquals(objA: body, objB: usingStmt.Body)
                       && ReferenceEquals(objA: fb, objB: usingStmt.FallbackBody)
                    ? usingStmt
                    : usingStmt with { Body = body, FallbackBody = fb };
            }

            default:
                return statement;
        }
    }
}
