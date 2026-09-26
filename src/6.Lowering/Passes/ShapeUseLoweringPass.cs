using Builder.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Opens a SHAPE USE of a container while its element positions are in use, so a change that could move the
/// elements crashes instead of leaving the user of those positions pointing at the wrong place (the
/// IterGuard). The shape is the element count and positions; element values may still change. Two uses:
/// <list type="bullet">
/// <item>an <c>each</c> loop over a named container (a <see cref="LoopStatement"/> whose
/// <see cref="LoopStatement.IterationSourceName"/> is set): <c>xs.begin_shape_use()</c> before the loop,
/// <c>xs.end_shape_use()</c> after it and before every <c>return</c> inside it;</item>
/// <item>a statement that reaches an entity element through a <c>modify_at</c>/<c>view_at</c> token
/// (built by OperatorLoweringPass): <c>begin_shape_use</c> before the statement and <c>end_shape_use</c>
/// after it. A <c>return</c> value or an <c>if</c> condition holding such a token is first bound to a
/// temporary, so the use closes right after its evaluation.</item>
/// </list>
/// Every <c>@reshaping</c> routine of such a container starts with <c>require_shape_free</c> (added by
/// SemanticVerifier), which crashes with ReshapingWhileInUseError while a use is open. That catches the
/// change even when it is hidden behind a call, where the build-time checks (RF-S625, RF-S639) cannot see
/// it. Uses are counted, so nested loops and element calls on one container stack. A failure inside an open
/// use ends the program, so that use never needs closing.
/// <para>Runs on user programs after OperatorLoweringPass. The begin/end calls are spliced into the
/// enclosing block rather than wrapped in a new one, so a declaration stays visible to the statements after
/// it.</para>
/// </summary>
internal sealed class ShapeUseLoweringPass(PostprocessingContext ctx)
{
    private const string ModifyAt = "modify_at";
    private const string ViewAt = "view_at";

    /// <summary>The containers whose shape the enclosing <c>each</c> loops use, outermost first.</summary>
    private readonly List<Expression> _loopShapeUses = [];

    private int _tempCount;

    public void Run(Program program)
    {
        BodyDispatch.RunOnProgram(program: program, lower: r => LowerNested(stmt: r.Body));
    }

    /// <summary>Lowers a statement in a nested position (a body or a branch), wrapping a spliced result.</summary>
    private Statement LowerNested(Statement stmt)
    {
        List<Statement> lowered = LowerStatement(stmt: stmt, preceding: []);
        return lowered.Count == 1
            ? lowered[index: 0]
            : new BlockStatement(Statements: lowered, Location: stmt.Location);
    }

    private BlockStatement LowerBlock(BlockStatement block)
    {
        var rewritten = new List<Statement>(capacity: block.Statements.Count);
        foreach (Statement stmt in block.Statements)
        {
            rewritten.AddRange(collection: LowerStatement(stmt: stmt, preceding: rewritten));
        }

        block.Statements.Clear();
        block.Statements.AddRange(collection: rewritten);
        return block;
    }

    /// <summary>
    /// Lowers one statement to the statements that replace it. <paramref name="preceding"/> holds the
    /// already-lowered statements before it in the same block, where an <c>each</c> loop's iterator
    /// declaration (and so its source's type) is found.
    /// </summary>
    private List<Statement> LowerStatement(Statement stmt, List<Statement> preceding)
    {
        switch (stmt)
        {
            case BlockStatement block:
                return [LowerBlock(block: block)];

            case DangerStatement danger:
                return [danger with { Body = LowerBlock(block: danger.Body) }];

            case LoopStatement loop:
                return LowerLoop(loop: loop, preceding: preceding);

            case IfStatement ifs:
            {
                IfStatement rebuilt = ifs with
                {
                    ThenStatement = LowerNested(stmt: ifs.ThenStatement),
                    ElseStatement = ifs.ElseStatement is { } alt
                        ? LowerNested(stmt: alt)
                        : null
                };
                return BindHead(head: ifs.Condition,
                    rebuild: condition => rebuilt with { Condition = condition },
                    whole: rebuilt);
            }

            case WhenStatement whenStmt:
                return
                [
                    whenStmt with
                    {
                        Clauses = whenStmt.Clauses
                                      .Select(selector: c => c with { Body = LowerNested(stmt: c.Body) })
                                      .ToList()
                    }
                ];

            case UsingStatement usingStmt:
                return
                [
                    usingStmt with
                    {
                        Body = LowerNested(stmt: usingStmt.Body),
                        FallbackBody = usingStmt.FallbackBody is { } fallback
                            ? LowerNested(stmt: fallback)
                            : null
                    }
                ];

            case ReturnStatement ret:
                return LowerReturn(ret: ret);

            default:
            {
                List<Expression> containers = TokenContainers(root: stmt);
                if (containers.Count == 0)
                {
                    return [stmt];
                }

                var result = new List<Statement>();
                result.AddRange(collection: ShapeUseCalls(containers: containers, verb: RuntimeContract.ShapeUse.Begin));
                result.Add(item: stmt);
                result.AddRange(collection: ShapeUseCalls(containers: containers,
                    verb: RuntimeContract.ShapeUse.End));
                return result;
            }
        }
    }

    /// <summary>
    /// An <c>each</c> loop over a named container that counts shape uses is bracketed by
    /// <c>begin_shape_use</c>/<c>end_shape_use</c>, and its body is lowered with the container on the
    /// loop stack so a <c>return</c> inside closes the use.
    /// </summary>
    private List<Statement> LowerLoop(LoopStatement loop, List<Statement> preceding)
    {
        Expression? source = loop.IterationSourceName is { } name
            ? LoopSource(name: name, preceding: preceding)
            : loop.IterationSourcePath is { } path
                ? LoopSourceByPath(path: path, preceding: preceding)
                : null;
        if (source == null || !HasShapeUse(container: source))
        {
            return [loop with { Body = LowerNested(stmt: loop.Body) }];
        }

        _loopShapeUses.Add(item: source);
        Statement body = LowerNested(stmt: loop.Body);
        _loopShapeUses.RemoveAt(index: _loopShapeUses.Count - 1);

        return
        [
            .. ShapeUseCalls(containers: [source], verb: RuntimeContract.ShapeUse.Begin),
            loop with { Body = body },
            .. ShapeUseCalls(containers: [source], verb: RuntimeContract.ShapeUse.End)
        ];
    }

    /// <summary>
    /// A <c>return</c> leaves every enclosing loop that opened a shape use, so it closes those uses
    /// (innermost first) after its value is computed. A value that reaches an element token is computed
    /// inside that token's own use.
    /// </summary>
    private List<Statement> LowerReturn(ReturnStatement ret)
    {
        List<Expression> containers = ret.Value is { } v
            ? TokenContainers(root: v)
            : [];
        if (containers.Count == 0 && _loopShapeUses.Count == 0)
        {
            return [ret];
        }

        var loopEnds = ShapeUseCalls(containers: Enumerable.Reverse(source: _loopShapeUses).ToList(),
            verb: RuntimeContract.ShapeUse.End);
        // A value that is only a name or a literal (or none) reads no element, so the loop uses can be
        // closed right before it. So can a value whose type is unknown, which cannot be bound.
        if (containers.Count == 0 &&
            ret.Value is null or IdentifierExpression or LiteralExpression ||
            ret.Value is not { ResolvedType: { } valueType } value)
        {
            return [.. loopEnds, ret];
        }

        (DeclarationStatement decl, IdentifierExpression tmp) =
            MakeTemporary(value: value, type: valueType);
        return
        [
            .. ShapeUseCalls(containers: containers, verb: RuntimeContract.ShapeUse.Begin),
            decl,
            .. ShapeUseCalls(containers: containers, verb: RuntimeContract.ShapeUse.End),
            .. loopEnds,
            ret with { Value = tmp }
        ];
    }

    /// <summary>
    /// When the head expression of a compound statement reaches an element token, computes it into a
    /// temporary inside the token's shape use, so the use does not stay open for the whole branch.
    /// </summary>
    private List<Statement> BindHead(Expression head, Func<Expression, Statement> rebuild,
        Statement whole)
    {
        List<Expression> containers = TokenContainers(root: head);
        if (containers.Count == 0 || head.ResolvedType is not { } headType)
        {
            return [whole];
        }

        (DeclarationStatement decl, IdentifierExpression tmp) =
            MakeTemporary(value: head, type: headType);
        return
        [
            .. ShapeUseCalls(containers: containers, verb: RuntimeContract.ShapeUse.Begin),
            decl,
            .. ShapeUseCalls(containers: containers, verb: RuntimeContract.ShapeUse.End),
            rebuild(arg: tmp)
        ];
    }

    /// <summary>
    /// The containers of the <c>modify_at</c>/<c>view_at</c> element tokens inside
    /// <paramref name="root"/> whose container is a plain named path (a fresh container value has no
    /// other reference that could change it) and counts shape uses.
    /// </summary>
    private List<Expression> TokenContainers(object root)
    {
        var containers = new List<Expression>();
        AstWalker.WalkExpressions(root: root,
            visit: e =>
            {
                if (e is CallExpression
                    {
                        Callee: MemberExpression { MemberName: ModifyAt or ViewAt, Object: var container }
                    } && IsNamedPath(expr: container) && HasShapeUse(container: container))
                {
                    containers.Add(item: container);
                }
            });
        return containers;
    }

    /// <summary>
    /// The named source of an <c>each</c> loop, typed from its iterator declaration among the statements
    /// before the loop (`var _iter = xs.iter()`).
    /// </summary>
    private static Expression? LoopSource(string name, List<Statement> preceding)
    {
        for (int i = preceding.Count - 1; i >= 0; i--)
        {
            if (preceding[index: i] is not DeclarationStatement
                {
                    Declaration: VariableDeclaration { Initializer: { } init }
                })
            {
                continue;
            }

            TypeSymbol? sourceType = null;
            AstWalker.WalkExpressions(root: init,
                visit: e =>
                {
                    if (e is IdentifierExpression { ResolvedType: { } t } id && id.Name == name)
                    {
                        sourceType ??= t;
                    }
                });
            if (sourceType != null)
            {
                return new IdentifierExpression(Name: name, Location: init.Location)
                {
                    ResolvedType = sourceType
                };
            }
        }

        return null;
    }

    /// <summary>
    /// A field-chain or element source of an <c>each</c> loop (<c>me.items</c>, <c>grid[0]</c>): the analyzed
    /// receiver of its iterator declaration among the statements before the loop, copied so the shape-use
    /// calls read it again without sharing the declaration's nodes.
    /// </summary>
    private static Expression? LoopSourceByPath(string path, List<Statement> preceding)
    {
        for (int i = preceding.Count - 1; i >= 0; i--)
        {
            if (preceding[index: i] is not DeclarationStatement
                {
                    Declaration: VariableDeclaration { Initializer: { } init }
                })
            {
                continue;
            }

            Expression? found = null;
            AstWalker.WalkExpressions(root: init,
                visit: e =>
                {
                    if (found == null && e.ResolvedType != null && SourcePath(expr: e) == path)
                    {
                        found = e;
                    }
                });
            if (found != null)
            {
                return CopyReadPath(expr: found);
            }
        }

        return null;
    }

    /// <summary>The path of a name, field chain or element read (<c>grid[0]</c> is <c>grid[]</c>), as
    /// ControlFlowLoweringPass records it on the loop.</summary>
    private static string? SourcePath(Expression expr)
    {
        return expr switch
        {
            IdentifierExpression id => id.Name,
            MemberExpression { Object: var inner, MemberName: var field } =>
                SourcePath(expr: inner) is { } prefix
                    ? $"{prefix}.{field}"
                    : null,
            IndexExpression { Object: var container } =>
                SourcePath(expr: container) is { } owner
                    ? $"{owner}[]"
                    : null,
            _ => null
        };
    }

    /// <summary>A fresh copy of a name / field chain / element read, keeping every level's resolved type.</summary>
    private static Expression CopyReadPath(Expression expr)
    {
        return expr switch
        {
            MemberExpression member => member with { Object = CopyReadPath(expr: member.Object) },
            IndexExpression index => index with { Object = CopyReadPath(expr: index.Object) },
            _ => expr with { }
        };
    }

    private bool HasShapeUse(Expression container)
    {
        return container.ResolvedType is { } type && ShapeOwner(type: type) is { } owner &&
               ctx.Registry.LookupMemberRoutine(type: owner,
                   memberRoutineName: RuntimeContract.ShapeUse.Begin) != null;
    }

    /// <summary>
    /// The type that counts the shape uses: the container itself, or for a Suflae <c>Roamed[C]</c> handle
    /// the inner container <c>C</c>, reached through the handle's <c>control()</c> projection.
    /// </summary>
    private static TypeSymbol? ShapeOwner(TypeSymbol type)
    {
        return IsRoamed(type: type)
            ? type.TypeArguments is [{ } inner]
                ? inner
                : null
            : type;
    }

    private static bool IsRoamed(TypeSymbol type)
    {
        return TypeRegistry.GetRcWrapperBaseName(type: type) == RuntimeContract.Roamed;
    }

    private static bool IsNamedPath(Expression expr)
    {
        return expr switch
        {
            IdentifierExpression => true,
            MemberExpression member => IsNamedPath(expr: member.Object),
            _ => false
        };
    }

    private List<Statement> ShapeUseCalls(List<Expression> containers, string verb)
    {
        var calls = new List<Statement>(capacity: containers.Count);
        foreach (Expression container in containers)
        {
            TypeSymbol type = container.ResolvedType!;
            TypeSymbol owner = ShapeOwner(type: type)!;
            Expression receiver = IsRoamed(type: type)
                ? ProjectRoamed(handle: container, handleType: type, inner: owner)
                : container;
            RoutineInfo routine =
                ctx.Registry.LookupMemberRoutine(type: owner, memberRoutineName: verb)!;
            var callee = new MemberExpression(Object: receiver,
                MemberName: verb,
                Location: container.Location) { ResolvedType = owner };
            var call = new CallExpression(Callee: callee, Arguments: [], Location: container.Location)
            {
                ResolvedRoutine = routine,
                ResolvedType = routine.ReturnType,
                LoweringKind = Verification.CallClassifier.ClassifyMemberRoutineCall(memberRoutine: routine)
            };
            calls.Add(item: new ExpressionStatement(Expression: call, Location: container.Location));
        }

        return calls;
    }

    /// <summary>
    /// <c>handle.control()</c>: the inner container of a Suflae <c>Roamed</c> handle, the same projection
    /// RoamedProjectionLoweringPass puts on a call through the handle (this pass runs after it). The
    /// access lock around the statement comes from RoamedLockBracketLoweringPass, which recognizes it.
    /// </summary>
    private Expression ProjectRoamed(Expression handle, TypeSymbol handleType, TypeSymbol inner)
    {
        RoutineInfo? control = ctx.Registry.LookupMemberRoutine(type: handleType,
            memberRoutineName: RuntimeContract.Control);
        if (control is null)
        {
            return handle;
        }

        var callee = new MemberExpression(Object: handle,
            MemberName: RuntimeContract.Control,
            Location: handle.Location) { ResolvedType = inner };
        return new CallExpression(Callee: callee, Arguments: [], Location: handle.Location)
        {
            ResolvedRoutine = control,
            ResolvedType = inner
        };
    }

    private (DeclarationStatement Declaration, IdentifierExpression Reference) MakeTemporary(
        Expression value, TypeSymbol type)
    {
        string name = $"_shape_use_{_tempCount++}";
        var declaration = new VariableDeclaration(Name: name,
            Type: ExpressionLoweringPass.TypeInfoToExpr(type: type, loc: value.Location),
            Initializer: value,
            Visibility: VisibilityModifier.Secret,
            Location: value.Location);
        return (new DeclarationStatement(Declaration: declaration, Location: value.Location),
            new IdentifierExpression(Name: name, Location: value.Location) { ResolvedType = type });
    }
}
