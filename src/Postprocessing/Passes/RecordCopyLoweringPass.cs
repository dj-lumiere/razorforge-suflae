using System.Collections.Generic;
using System.Linq;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using Verification;

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
    // True while lowering the body of a `$copy` routine. Returning `me` there is the identity-copy
    // primitive itself, so it must NOT be rewritten to `me.$copy()` (that would recurse forever).
    private bool _inCopyRoutine;

    /// <summary>
    /// Runs this compiler phase over its configured input.
    /// </summary>
    public void Run(Program program)
    {
        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[i])
            {
                case RoutineDeclaration r:
                {
                    _inCopyRoutine = r.Name.EndsWith(value: "$copy");
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
            _inCopyRoutine = key.Contains(value: "$copy");
            Statement lowered = LowerStatement(stmt: body);
            if (!ReferenceEquals(lowered, body))
                ctx.VariantBodies[key] = lowered;
        }
    }

    /// <summary>
    /// Lower member list as part of this compiler phase.
    /// </summary>
    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i] is RoutineDeclaration mr)
            {
                _inCopyRoutine = mr.Name.EndsWith(value: "$copy");
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
    private Statement LowerStatement(Statement stmt)
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
                Expression lowered = LowerOwnership(expr: vd.Initializer, isReturn: false);
                if (ReferenceEquals(lowered, vd.Initializer)) return stmt;
                var newVd = vd with { Initializer = lowered };
                return ds with { Declaration = newVd };
            }

            case AssignmentStatement assign:
            {
                Expression lowered = LowerOwnership(expr: assign.Value, isReturn: false);
                return ReferenceEquals(lowered, assign.Value)
                    ? stmt
                    : assign with { Value = lowered };
            }

            case ReturnStatement { Value: not null } ret:
            {
                Expression lowered = LowerOwnership(expr: ret.Value, isReturn: true);
                return ReferenceEquals(lowered, ret.Value)
                    ? stmt
                    : ret with { Value = lowered };
            }

            case VariantReturnStatement { Value: not null } vrs:
            {
                Expression lowered = LowerOwnership(expr: vrs.Value, isReturn: true);
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
    /// Lowers a single expression sitting in an ownership/copy position (var-binding RHS,
    /// assignment RHS, return value, call argument). A "borrowed reference" — an identifier or
    /// field read — of a type that carries a retaining <c>$copy</c> (e.g. <c>Text</c>, whose
    /// <c>$copy</c> bumps the controller refcount) is rewritten to <c>expr.$copy()</c> so the new
    /// owner holds its own reference, balancing the <c>$destroy</c> that scope-teardown inserts.
    /// Explicit <see cref="StealExpression"/> is preserved (an explicit move, no copy). Fresh
    /// values (calls, constructors, arithmetic) are already owned and are only recursed for
    /// nested copy positions.
    /// </summary>
    /// <param name="isReturn">
    /// True at a return position. Returning a bare local <see cref="IdentifierExpression"/>
    /// transfers ownership out (scope-teardown skips the returned local), so it must NOT be
    /// copied — that would leak the local. A returned field (<see cref="MemberExpression"/>) is
    /// still copied, since the aggregate keeps its own reference.
    /// </param>
    private Expression LowerOwnership(Expression expr, bool isReturn)
    {
        // Preserve explicit steal so later stages can observe the ownership transfer site.
        if (expr is StealExpression steal)
            return steal;

        if (expr is IdentifierExpression or MemberExpression
            && NeedsRetainingCopy(type: expr.ResolvedType, copyMethod: out RoutineInfo? copyMethod))
        {
            if (isReturn && expr is IdentifierExpression id)
            {
                // Returning the borrowed receiver `me` hands the caller an owned value, so it must
                // be copied (retained) — except inside `$copy` itself, where `return me` is the
                // identity primitive. Any other bare identifier is an owned local/param being moved
                // out, so it is returned as-is.
                bool returningBorrowedReceiver = id.Name == "me" && !_inCopyRoutine;
                if (!returningBorrowedReceiver)
                    return expr;
            }
            return MakeCopyCall(expr: expr, copyMethod: copyMethod!);
        }

        // For complex expressions in ownership positions (calls, constructors, etc.),
        // recurse into argument positions (which are themselves copy positions).
        return StripStealFromExpr(expr: expr);
    }

    /// <summary>
    /// Recursively lowers nested expressions, preserving explicit <see cref="StealExpression"/>
    /// markers and injecting a retaining <c>$copy</c> on borrowed-reference call arguments (each
    /// argument is a copy position — the callee's parameter is an independent owner that gets
    /// torn down at the callee's scope exit).
    /// </summary>
    private Expression StripStealFromExpr(Expression expr) // NOSONAR S3776
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
                    Expression s = LowerArgument(arg: arg);
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
                    Expression s = LowerArgument(arg: arg);
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

    /// <summary>
    /// Lowers a single call argument as a copy position, transparently handling the
    /// <see cref="NamedArgumentExpression"/> wrapper required by named-argument calls (S510).
    /// </summary>
    private Expression LowerArgument(Expression arg)
    {
        if (arg is NamedArgumentExpression named)
        {
            Expression loweredValue = LowerOwnership(expr: named.Value, isReturn: false);
            return ReferenceEquals(loweredValue, named.Value) ? named : named with { Value = loweredValue };
        }
        return LowerOwnership(expr: arg, isReturn: false);
    }

    /// <summary>
    /// True when copying a value of <paramref name="type"/> must go through its <c>$copy</c> rather
    /// than a bitwise duplicate: the type declares a non-synthesized (hand-written) <c>$copy</c>,
    /// which is how leaf managed types like <c>Text</c> bump their refcount. Trivially-copyable
    /// records have only a synthesized identity <c>$copy</c> and need no injection.
    /// </summary>
    private bool NeedsRetainingCopy(TypeInfo? type, out RoutineInfo? copyMethod)
    {
        copyMethod = null;
        if (type is not RecordTypeInfo)
            return false;
        copyMethod = ctx.Registry.GetMethodsForType(type: type)
            .FirstOrDefault(predicate: m => m.Name == "$copy" && m.Parameters.Count == 0 && !m.IsSynthesized);
        return copyMethod != null;
    }

    /// <summary>
    /// Builds a fully resolved <c>expr.$copy()</c> call. The routine and lowering kind are stamped
    /// here (not left for a later pass) so codegen materializes the <c>$copy</c> return value — an
    /// unresolved call is emitted as a discarded <c>void</c> call, dropping the retained copy and
    /// leaving the binding with a dangling reference.
    /// </summary>
    private static Expression MakeCopyCall(Expression expr, RoutineInfo copyMethod)
    {
        var callee = new MemberExpression(Object: expr, PropertyName: "$copy", Location: expr.Location)
            { ResolvedType = expr.ResolvedType };
        return new CallExpression(Callee: callee, Arguments: [], Location: expr.Location)
        {
            ResolvedRoutine = copyMethod,
            ResolvedType = expr.ResolvedType,
            LoweringKind = CallClassifier.ClassifyMethodCall(method: copyMethod)
        };
    }
}
