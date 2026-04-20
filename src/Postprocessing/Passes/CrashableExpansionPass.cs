using TypeModel.Types;
using SyntaxTree;


using Compiler.Postprocessing;
namespace Compiler.Postprocessing.Passes;

/// <summary>
/// Expands <see cref="CrashablePattern"/> clauses in <see cref="WhenStatement"/>s whose subject
/// is a <c>Result[T]</c> or <c>Lookup[T]</c> carrier into one <see cref="TypePattern"/> clause
/// per registered crashable type.
///
/// <para>
/// Input:  <c>is Crashable e => body</c> ??matches any error in the carrier.<br/>
/// Output: <c>is ParseError e => body</c>, <c>is NetworkError e => body</c>, ??
///         (one clause per <see cref="CrashableTypeInfo"/> registered in the type registry)
/// </para>
///
/// <para>
/// The expanded <see cref="TypePattern"/> clauses are fully lowerable by the subsequent
/// <see cref="PatternLoweringPass"/>: the condition becomes
/// <c>carrier.type_id == &lt;U64 constant&gt;</c>
/// and the binding becomes a <see cref="CarrierPayloadExpression"/>.
/// </para>
///
/// <para>
/// Conservative expansion: all registered crashable types are emitted, not just the ones
/// thrown by the specific called routine. Extra branches never fire and are eliminated by
/// LLVM dead-code elimination.
/// </para>
///
/// Must run after <see cref="ErrorHandlingVariantPass"/> (global) so that
/// <see cref="TypeModel.Symbols.RoutineInfo.ThrowableTypes"/> is populated,
/// and before <see cref="PatternLoweringPass"/> so the expanded
/// <see cref="TypePattern"/> clauses can be lowered.
/// </summary>
internal sealed class CrashableExpansionPass(PostprocessingContext ctx)
{
    public void Run(Program program)
    {
        IReadOnlyList<CrashableTypeInfo> crashableTypes = ctx.Registry
            .GetAllTypes()
            .OfType<CrashableTypeInfo>()
            .ToList();

        // Nothing to expand if no crashable types are registered.
        if (crashableTypes.Count == 0) return;

        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[i])
            {
                case RoutineDeclaration r:
                {
                    Statement newBody = ExpandStatement(stmt: r.Body, crashableTypes: crashableTypes);
                    if (!ReferenceEquals(newBody, r.Body))
                        program.Declarations[i] = r with { Body = newBody };
                    break;
                }

                case EntityDeclaration e:
                    ExpandMemberList(members: e.Members, crashableTypes: crashableTypes);
                    break;

                case RecordDeclaration rec:
                    ExpandMemberList(members: rec.Members, crashableTypes: crashableTypes);
                    break;

                case CrashableDeclaration cr:
                    ExpandMemberList(members: cr.Members, crashableTypes: crashableTypes);
                    break;
            }
        }
    }

    private void ExpandMemberList(List<SyntaxTree.Declaration> members,
        IReadOnlyList<CrashableTypeInfo> crashableTypes)
    {
        for (int j = 0; j < members.Count; j++)
        {
            if (members[j] is not RoutineDeclaration m) continue;
            Statement newBody = ExpandStatement(stmt: m.Body, crashableTypes: crashableTypes);
            if (!ReferenceEquals(newBody, m.Body))
                members[j] = m with { Body = newBody };
        }
    }

    // ?€?€?€ Statement walker ?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€

    private Statement ExpandStatement(Statement stmt, IReadOnlyList<CrashableTypeInfo> crashableTypes)
    {
        switch (stmt)
        {
            case WhenStatement w:
                return ExpandWhen(when: w, crashableTypes: crashableTypes);

            case BlockStatement b:
            {
                bool changed = false;
                var stmts = new List<Statement>(capacity: b.Statements.Count);
                foreach (Statement s in b.Statements)
                {
                    Statement n = ExpandStatement(stmt: s, crashableTypes: crashableTypes);
                    stmts.Add(item: n);
                    if (!ReferenceEquals(n, s)) changed = true;
                }

                return changed ? b with { Statements = stmts } : b;
            }

            case IfStatement ifs:
            {
                Statement then = ExpandStatement(stmt: ifs.ThenStatement,
                    crashableTypes: crashableTypes);
                Statement? elseS = ifs.ElseStatement != null
                    ? ExpandStatement(stmt: ifs.ElseStatement, crashableTypes: crashableTypes)
                    : null;
                bool changed = !ReferenceEquals(then, ifs.ThenStatement)
                               || !ReferenceEquals(elseS, ifs.ElseStatement);
                return changed
                    ? ifs with { ThenStatement = then, ElseStatement = elseS }
                    : ifs;
            }

            case WhileStatement w:
            {
                Statement body = ExpandStatement(stmt: w.Body, crashableTypes: crashableTypes);
                Statement? elseB = w.ElseBranch != null
                    ? ExpandStatement(stmt: w.ElseBranch, crashableTypes: crashableTypes)
                    : null;
                bool changed = !ReferenceEquals(body, w.Body)
                               || !ReferenceEquals(elseB, w.ElseBranch);
                return changed ? w with { Body = body, ElseBranch = elseB } : w;
            }

            case LoopStatement loop:
            {
                Statement body = ExpandStatement(stmt: loop.Body, crashableTypes: crashableTypes);
                return ReferenceEquals(body, loop.Body) ? loop : loop with { Body = body };
            }

            case ForStatement f:
            {
                Statement body = ExpandStatement(stmt: f.Body, crashableTypes: crashableTypes);
                Statement? elseB = f.ElseBranch != null
                    ? ExpandStatement(stmt: f.ElseBranch, crashableTypes: crashableTypes)
                    : null;
                bool changed = !ReferenceEquals(body, f.Body)
                               || !ReferenceEquals(elseB, f.ElseBranch);
                return changed ? f with { Body = body, ElseBranch = elseB } : f;
            }

            case UsingStatement u:
            {
                Statement body = ExpandStatement(stmt: u.Body, crashableTypes: crashableTypes);
                return !ReferenceEquals(body, u.Body) ? u with { Body = body } : u;
            }

            case DangerStatement d:
            {
                Statement lowered = ExpandStatement(stmt: d.Body, crashableTypes: crashableTypes);
                return !ReferenceEquals(lowered, d.Body)
                    ? d with { Body = (BlockStatement)lowered }
                    : d;
            }

            default:
                return stmt;
        }
    }

    // ?€?€?€ WhenStatement expansion ?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€

    private Statement ExpandWhen(WhenStatement when,
        IReadOnlyList<CrashableTypeInfo> crashableTypes)
    {
        // Only expand carrier-type subjects (Result/Lookup).
        // Subject-less when (Expression == null) is never a carrier ??just recurse.
        if (when.Expression == null || !IsResultOrLookup(type: when.Expression.ResolvedType))
        {
            // Still recurse into clause bodies for nested whens.
            return RecurseIntoClauses(when: when, crashableTypes: crashableTypes);
        }

        bool changed = false;
        var expanded = new List<WhenClause>(capacity: when.Clauses.Count);

        foreach (WhenClause clause in when.Clauses)
        {
            if (clause.Pattern is CrashablePattern cp)
            {
                changed = true;
                // Replace one CrashablePattern clause with N TypePattern clauses.
                foreach (CrashableTypeInfo crashable in crashableTypes)
                {
                    var typeExpr = new TypeExpression(
                        Name: crashable.Name,
                        GenericArguments: null,
                        Location: cp.Location)
                    {
                        ResolvedType = crashable
                    };
                    var newPattern = new TypePattern(
                        Type: typeExpr,
                        VariableName: cp.VariableName,
                        Bindings: null,
                        Location: cp.Location);
                    expanded.Add(item: clause with { Pattern = newPattern });
                }
            }
            else
            {
                // Recurse into clause body for nested WhenStatements.
                Statement newBody = ExpandStatement(stmt: clause.Body,
                    crashableTypes: crashableTypes);
                if (!ReferenceEquals(newBody, clause.Body))
                {
                    expanded.Add(item: clause with { Body = newBody });
                    changed = true;
                }
                else
                {
                    expanded.Add(item: clause);
                }
            }
        }

        return changed ? when with { Clauses = expanded } : when;
    }

    /// <summary>Recurses into clause bodies without changing the clauses themselves.</summary>
    private Statement RecurseIntoClauses(WhenStatement when,
        IReadOnlyList<CrashableTypeInfo> crashableTypes)
    {
        bool changed = false;
        var clauses = new List<WhenClause>(capacity: when.Clauses.Count);
        foreach (WhenClause c in when.Clauses)
        {
            Statement newBody = ExpandStatement(stmt: c.Body, crashableTypes: crashableTypes);
            if (!ReferenceEquals(newBody, c.Body))
            {
                clauses.Add(item: c with { Body = newBody });
                changed = true;
            }
            else
            {
                clauses.Add(item: c);
            }
        }

        return changed ? when with { Clauses = clauses } : when;
    }

    // ?€?€?€ Type classification helpers ?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€

    private static bool IsResultOrLookup(TypeInfo? type)
    {
        if (type == null) return false;
        string baseName = GetCarrierBaseName(type: type);
        return baseName is "Result" or "Lookup";
    }

    private static string GetCarrierBaseName(TypeInfo type)
    {
        if (type is RecordTypeInfo { GenericDefinition: not null } r) return r.GenericDefinition.Name;
        if (type is EntityTypeInfo { GenericDefinition: not null } e) return e.GenericDefinition.Name;
        return type.Name;
    }
}
