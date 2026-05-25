using System.Collections.Generic;
using System.Linq;
using SyntaxTree;
using TypeModel.Types;

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
/// record type to <c>r1.$copy()</c>. Required for RC wrapper types
///  (<c>Retained[T]</c>, <c>Tracked[T]</c>, etc.) where a bit-for-bit struct copy
/// would not increment the reference count, causing a double-free bug.
/// For plain records (no RC fields) <c>$copy()</c> is semantically identical to
/// a bit copy and is optimized away by LLVM inlining.</item>
/// </list>
///
/// <para>Runs last in the per-file desugaring pipeline (after <see cref="PatternLoweringPass"/>).
/// Needs <c>ResolvedType</c> to be set on all expressions (Phase 5 output).</para>
///
/// <para>Injection is limited to <em>borrowed-reference</em> expressions in assignment
/// positions: <see cref="IdentifierExpression"/> and <see cref="MemberExpression"/> with a
/// record <c>ResolvedType</c>. Fresh values (calls, constructors, arithmetic) are already
/// owned and do not need <c>$copy()</c>.</para>
/// </summary>
internal sealed class RecordCopyLoweringPass(PostprocessingContext ctx)
{
    /// <summary>
    /// Runs this compiler phase over its configured input.
    /// </summary>
    public static void Run(Program program)
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

    /// <summary>
    /// Runs this compiler phase over its configured input.
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

    /// <summary>
    /// Lower member list as part of this compiler phase.
    /// </summary>
    private static void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i] is RoutineDeclaration mr)
            {
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
    private static Statement LowerStatement(Statement stmt)
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
                Expression lowered = LowerOwnership(expr: vd.Initializer);
                if (ReferenceEquals(lowered, vd.Initializer)) return stmt;
                var newVd = vd with { Initializer = lowered };
                return ds with { Declaration = newVd };
            }

            case AssignmentStatement assign:
            {
                Expression lowered = LowerOwnership(expr: assign.Value);
                return ReferenceEquals(lowered, assign.Value)
                    ? stmt
                    : assign with { Value = lowered };
            }

            case ReturnStatement { Value: not null } ret:
            {
                Expression lowered = LowerOwnership(expr: ret.Value);
                return ReferenceEquals(lowered, ret.Value)
                    ? stmt
                    : ret with { Value = lowered };
            }

            case VariantReturnStatement { Value: not null } vrs:
            {
                Expression lowered = LowerOwnership(expr: vrs.Value);
                return ReferenceEquals(lowered, vrs.Value)
                    ? stmt
                    : vrs with { Value = lowered };
            }

            case IfStatement ifStmt:
            {
                Statement newThen = LowerStatement(stmt: ifStmt.ThenStatement);
                Statement? newElse = ifStmt.ElseStatement != null
                    ? LowerStatement(stmt: ifStmt.ElseStatement)
                    : null;
                bool changed = !ReferenceEquals(newThen, ifStmt.ThenStatement) ||
                               !ReferenceEquals(newElse, ifStmt.ElseStatement);
                return changed
                    ? ifStmt with { ThenStatement = newThen, ElseStatement = newElse }
                    : stmt;
            }

            case WhileStatement whileStmt:
            {
                Statement newBody = LowerStatement(stmt: whileStmt.Body);
                return ReferenceEquals(newBody, whileStmt.Body)
                    ? stmt
                    : whileStmt with { Body = newBody };
            }

            case LoopStatement loopStmt:
            {
                Statement newBody = LowerStatement(stmt: loopStmt.Body);
                return ReferenceEquals(newBody, loopStmt.Body)
                    ? stmt
                    : loopStmt with { Body = newBody };
            }

            case ForStatement forStmt:
            {
                Statement newBody = LowerStatement(stmt: forStmt.Body);
                return ReferenceEquals(newBody, forStmt.Body)
                    ? stmt
                    : forStmt with { Body = newBody };
            }

            case WhenStatement whenStmt:
            {
                bool changed = false;
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
                return changed ? whenStmt with { Clauses = newClauses } : stmt;
            }

            case UsingStatement usingStmt:
            {
                Statement newBody = LowerStatement(stmt: usingStmt.Body);
                return ReferenceEquals(newBody, usingStmt.Body)
                    ? stmt
                    : usingStmt with { Body = newBody };
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
    /// Applies steal-stripping to a single expression in an ownership-transfer position.
    /// Implicit `$copy()` injection has been removed — trivially-copyable records emit a
    /// bitwise store at codegen time, and non-trivially-copyable records are rejected by
    /// SA (see <c>SemanticDiagnosticCode.ImplicitWrapperCopy</c>). This pass now exists
    /// solely to preserve <see cref="StealExpression"/> markers for later codegen.
    /// </summary>
    private static Expression LowerOwnership(Expression expr)
    {
        // Preserve explicit steal so later stages can observe the ownership transfer site.
        if (expr is StealExpression steal)
            return steal;

        // For complex expressions in ownership positions (calls, constructors, etc.),
        // strip steal from any nested argument positions.
        return StripStealFromExpr(expr: expr);
    }

    /// <summary>
    /// Recursively lowers nested expressions without erasing explicit <see cref="StealExpression"/>
    /// markers. Steal wrappers are preserved for later ownership-transfer handling.
    /// </summary>
    private static Expression StripStealFromExpr(Expression expr) // NOSONAR S3776
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
                    Expression s = StripStealFromExpr(expr: arg);
                    args.Add(item: s);
                    if (!ReferenceEquals(s, arg)) changed = true;
                }
                return changed ? call with { Arguments = args } : call;
            }

            case GenericMethodCallExpression gmc:
            {
                bool changed = false;
                var args = new List<Expression>(capacity: gmc.Arguments.Count);
                foreach (Expression arg in gmc.Arguments)
                {
                    Expression s = StripStealFromExpr(expr: arg);
                    args.Add(item: s);
                    if (!ReferenceEquals(s, arg)) changed = true;
                }
                return changed ? gmc with { Arguments = args } : gmc;
            }

            // All other expression types: steal cannot appear as a direct child in practice
            // (steal only wraps identifier or member expressions).
            default:
                return expr;
        }
    }
}
