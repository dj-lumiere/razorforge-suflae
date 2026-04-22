using Compiler.Postprocessing;
using Compiler.Lexer;
using TypeModel.Symbols;
using TypeModel.Types;
using SyntaxTree;

namespace Compiler.Postprocessing.Passes;

/// <summary>
/// Lowers <see cref="UsingStatement"/> to explicit <c>$enter</c> / <c>$exit</c> call sequences,
/// injecting <c>$exit()</c> before every control-flow escape from the using body.
/// After this pass codegen sees only plain declarations, calls, and standard control flow.
///
/// <para>Transformation of <c>using x = resource { body }</c>:</para>
/// <code>
/// {
///   var __uf_N = resource
///   var x = __uf_N.$enter()      // if $enter returns a value
///   // OR: __uf_N.$enter(); var x = __uf_N   // if $enter is void
///   // OR: var x = __uf_N                     // if no $enter
///   [body, with $exit() injected before every escape]
///   __uf_N.$exit()               // normal-path exit (unreachable if body always terminates)
/// }
/// </code>
///
/// <para>Escape injection rules (loopDepth = loops enclosing the point inside the using body):</para>
/// <list type="bullet">
///   <item><see cref="ReturnStatement"/>, <see cref="AbsentStatement"/>, <see cref="ThrowStatement"/>,
///         <see cref="VariantReturnStatement"/> — always inject.</item>
///   <item><see cref="BreakStatement"/>, <see cref="ContinueStatement"/> at loopDepth 0 — inject
///         (break/continue escapes the using's enclosing loop).</item>
///   <item>break/continue at loopDepth &gt; 0 — do not inject (exits a loop inside the using).</item>
/// </list>
///
/// <para>Nested usings are lowered bottom-up: the body is recursively lowered before the outer
/// using is processed, so inner <c>$exit()</c> calls appear before outer ones in every escape path.</para>
/// </summary>
internal sealed class UsingLoweringPass(PostprocessingContext ctx)
{
    private int _tempCount;

    private string NextResTemp() => $"__uf_{_tempCount++}";

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

    public void RunOnVariantBodies()
    {
        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            Statement body = ctx.VariantBodies[key];
            Statement lowered = LowerStatement(body);
            if (!ReferenceEquals(lowered, body))
                ctx.VariantBodies[key] = lowered;
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

    private Statement LowerStatement(Statement stmt)
    {
        switch (stmt)
        {
            case UsingStatement u:
                return LowerUsing(u);

            case BlockStatement b:
            {
                bool changed = false;
                var stmts = new List<Statement>(capacity: b.Statements.Count);
                foreach (Statement s in b.Statements)
                {
                    Statement n = LowerStatement(s);
                    stmts.Add(n);
                    if (!ReferenceEquals(n, s)) changed = true;
                }
                return changed ? b with { Statements = stmts } : b;
            }

            case IfStatement ifs:
            {
                Statement then = LowerStatement(ifs.ThenStatement);
                Statement? elseS = ifs.ElseStatement != null
                    ? LowerStatement(ifs.ElseStatement)
                    : null;
                bool changed = !ReferenceEquals(then, ifs.ThenStatement)
                               || !ReferenceEquals(elseS, ifs.ElseStatement);
                return changed ? ifs with { ThenStatement = then, ElseStatement = elseS } : ifs;
            }

            case WhenStatement w:
            {
                bool changed = false;
                var clauses = new List<WhenClause>(capacity: w.Clauses.Count);
                foreach (WhenClause c in w.Clauses)
                {
                    Statement body = LowerStatement(c.Body);
                    clauses.Add(!ReferenceEquals(body, c.Body) ? c with { Body = body } : c);
                    if (!ReferenceEquals(body, c.Body)) changed = true;
                }
                return changed ? w with { Clauses = clauses } : w;
            }

            case LoopStatement loop:
            {
                Statement body = LowerStatement(loop.Body);
                return !ReferenceEquals(body, loop.Body) ? loop with { Body = body } : loop;
            }

            case DangerStatement d:
            {
                Statement lowered = LowerStatement(d.Body);
                return !ReferenceEquals(lowered, d.Body)
                    ? d with { Body = (BlockStatement)lowered }
                    : d;
            }

            default:
                return stmt;
        }
    }

    private Statement LowerUsing(UsingStatement u)
    {
        // Lower body first → nested usings expand bottom-up.
        Statement loweredBody = LowerStatement(u.Body);

        TypeInfo? resourceType = u.Resource.ResolvedType;
        SourceLocation loc = u.Location;

        string resTemp = NextResTemp();
        var resTempIdent = new IdentifierExpression(Name: resTemp, Location: loc)
        {
            ResolvedType = resourceType
        };

        RoutineInfo? enterMethod = resourceType != null
            ? ctx.Registry.LookupMethod(type: resourceType, methodName: "$enter")
            : null;
        RoutineInfo? exitMethod = resourceType != null
            ? ctx.Registry.LookupMethod(type: resourceType, methodName: "$exit")
            : null;

        var stmts = new List<Statement>();

        // var __uf_N = resource
        stmts.Add(MakeBinding(name: resTemp, value: u.Resource, type: resourceType, loc: loc));

        // Bind user's name via $enter (or directly to the resource if no $enter)
        if (enterMethod != null)
        {
            var enterCallee = new MemberExpression(
                Object: resTempIdent, PropertyName: "$enter", Location: loc);
            var enterCall = new CallExpression(
                Callee: enterCallee, Arguments: [], Location: loc)
            {
                ResolvedRoutine = enterMethod,
                ResolvedType = enterMethod.ReturnType
            };

            bool returnsValue = enterMethod.ReturnType != null
                && enterMethod.ReturnType.Name != "Blank";

            if (returnsValue)
            {
                stmts.Add(MakeBinding(name: u.Name, value: enterCall,
                    type: enterMethod.ReturnType, loc: loc));
            }
            else
            {
                stmts.Add(new ExpressionStatement(Expression: enterCall, Location: loc));
                stmts.Add(MakeBinding(name: u.Name, value: resTempIdent,
                    type: resourceType, loc: loc));
            }
        }
        else
        {
            stmts.Add(MakeBinding(name: u.Name, value: resTempIdent, type: resourceType, loc: loc));
        }

        // Build the $exit() call expression (reused for injection and normal exit)
        ExpressionStatement? exitCallStmt = null;
        if (exitMethod != null)
        {
            var exitCallee = new MemberExpression(
                Object: resTempIdent, PropertyName: "$exit", Location: loc);
            var exitCall = new CallExpression(
                Callee: exitCallee, Arguments: [], Location: loc)
            {
                ResolvedRoutine = exitMethod,
                ResolvedType = null
            };
            exitCallStmt = new ExpressionStatement(Expression: exitCall, Location: loc);
        }

        Statement body = exitCallStmt != null
            ? InjectExitBeforeEscapes(loweredBody, exitCallStmt, loopDepth: 0)
            : loweredBody;

        stmts.Add(body);

        // Normal-path exit — unreachable (and skipped by EmitBlock) if body always terminates.
        if (exitCallStmt != null)
            stmts.Add(exitCallStmt);

        return new BlockStatement(Statements: stmts, Location: loc);
    }

    private static Statement InjectExitBeforeEscapes(
        Statement stmt, ExpressionStatement exitStmt, int loopDepth)
    {
        switch (stmt)
        {
            case ReturnStatement:
            case AbsentStatement:
            case ThrowStatement:
            case VariantReturnStatement:
                return MakeBlock([exitStmt, stmt], stmt.Location);

            case BreakStatement:
            case ContinueStatement:
                return loopDepth == 0
                    ? MakeBlock([exitStmt, stmt], stmt.Location)
                    : stmt;

            case BlockStatement b:
            {
                bool changed = false;
                var stmts = new List<Statement>(capacity: b.Statements.Count);
                foreach (Statement s in b.Statements)
                {
                    Statement n = InjectExitBeforeEscapes(s, exitStmt, loopDepth);
                    stmts.Add(n);
                    if (!ReferenceEquals(n, s)) changed = true;
                }
                return changed ? b with { Statements = stmts } : b;
            }

            case IfStatement ifs:
            {
                Statement then = InjectExitBeforeEscapes(ifs.ThenStatement, exitStmt, loopDepth);
                Statement? elseS = ifs.ElseStatement != null
                    ? InjectExitBeforeEscapes(ifs.ElseStatement, exitStmt, loopDepth)
                    : null;
                bool changed = !ReferenceEquals(then, ifs.ThenStatement)
                               || !ReferenceEquals(elseS, ifs.ElseStatement);
                return changed ? ifs with { ThenStatement = then, ElseStatement = elseS } : ifs;
            }

            case WhenStatement w:
            {
                bool changed = false;
                var clauses = new List<WhenClause>(capacity: w.Clauses.Count);
                foreach (WhenClause c in w.Clauses)
                {
                    Statement body = InjectExitBeforeEscapes(c.Body, exitStmt, loopDepth);
                    clauses.Add(!ReferenceEquals(body, c.Body) ? c with { Body = body } : c);
                    if (!ReferenceEquals(body, c.Body)) changed = true;
                }
                return changed ? w with { Clauses = clauses } : w;
            }

            case LoopStatement loop:
            {
                Statement body = InjectExitBeforeEscapes(loop.Body, exitStmt, loopDepth + 1);
                return !ReferenceEquals(body, loop.Body) ? loop with { Body = body } : loop;
            }

            case DangerStatement d:
            {
                Statement body = InjectExitBeforeEscapes(d.Body, exitStmt, loopDepth);
                return !ReferenceEquals(body, d.Body)
                    ? d with { Body = (BlockStatement)body }
                    : d;
            }

            default:
                return stmt;
        }
    }

    private static BlockStatement MakeBlock(IEnumerable<Statement> stmts, SourceLocation loc)
        => new(Statements: stmts.ToList(), Location: loc);

    private static Statement MakeBinding(
        string name, Expression value, TypeInfo? type, SourceLocation loc)
    {
        var decl = new VariableDeclaration(
            Name: name,
            Type: type != null ? TypeInfoToExpr(type: type, loc: loc) : null,
            Initializer: value,
            Visibility: VisibilityModifier.Secret,
            Location: loc);
        return new DeclarationStatement(Declaration: decl, Location: loc);
    }

    private static TypeExpression TypeInfoToExpr(TypeInfo type, SourceLocation loc)
    {
        string baseName = type switch
        {
            RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition.Name,
            EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition.Name,
            _ => type.IsGenericResolution && type.Name.Contains(value: '[')
                ? type.Name[..type.Name.IndexOf(value: '[')]
                : type.Name
        };
        return new TypeExpression(
            Name: baseName,
            GenericArguments: [],
            Location: loc)
        {
            ResolvedType = type
        };
    }
}