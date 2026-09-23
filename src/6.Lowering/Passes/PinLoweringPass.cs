using Builder.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Pins a container while its element positions are in use, so a change that could move the elements
/// crashes instead of leaving the user of those positions pointing at the wrong place (the IterGuard).
/// Two uses are pinned:
/// <list type="bullet">
/// <item>an <c>each</c> loop over a named container (a <see cref="LoopStatement"/> whose
/// <see cref="LoopStatement.IterationSourceName"/> is set): <c>xs.pin()</c> before the loop,
/// <c>xs.unpin()</c> after it and before every <c>return</c> inside it;</item>
/// <item>a statement that reaches an entity element through a <c>modify_at</c>/<c>view_at</c> token
/// (built by OperatorLoweringPass): <c>c.pin()</c> before the statement and <c>c.unpin()</c> after it. A
/// <c>return</c> value or an <c>if</c> condition holding such a token is first bound to a temporary, so
/// the unpin follows its evaluation.</item>
/// </list>
/// Every <c>@reshaping</c> routine of a pinnable container starts with <c>require_unpinned</c> (added by
/// SemanticVerifier), which crashes with ReshapingWhilePinnedError while the pin count is not zero. That
/// catches the change even when it is hidden behind a call, where the build-time checks (RF-S625,
/// RF-S639) cannot see it. Pins are counted, so nested loops and element calls on one container stack.
/// A failure inside a pinned region ends the program, so its pin never needs releasing.
/// <para>Runs on user programs after OperatorLoweringPass. Pins and unpins are spliced into the enclosing
/// block rather than wrapped in a new one, so a declaration stays visible to the statements after it.</para>
/// </summary>
internal sealed class PinLoweringPass(PostprocessingContext ctx)
{
    private const string ModifyAt = "modify_at";
    private const string ViewAt = "view_at";

    /// <summary>The containers pinned by the enclosing <c>each</c> loops, outermost first.</summary>
    private readonly List<Expression> _loopPins = [];

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
                result.AddRange(collection: PinCalls(containers: containers, verb: RuntimeContract.Pinning.Pin));
                result.Add(item: stmt);
                result.AddRange(collection: PinCalls(containers: containers,
                    verb: RuntimeContract.Pinning.Unpin));
                return result;
            }
        }
    }

    /// <summary>
    /// An <c>each</c> loop over a named pinnable container is bracketed by <c>pin</c>/<c>unpin</c>, and its
    /// body is lowered with the container on the loop-pin stack so a <c>return</c> inside releases it.
    /// </summary>
    private List<Statement> LowerLoop(LoopStatement loop, List<Statement> preceding)
    {
        Expression? source = loop.IterationSourceName is { } name
            ? LoopSource(name: name, preceding: preceding)
            : null;
        if (source == null || !IsPinnable(container: source))
        {
            return [loop with { Body = LowerNested(stmt: loop.Body) }];
        }

        _loopPins.Add(item: source);
        Statement body = LowerNested(stmt: loop.Body);
        _loopPins.RemoveAt(index: _loopPins.Count - 1);

        return
        [
            .. PinCalls(containers: [source], verb: RuntimeContract.Pinning.Pin),
            loop with { Body = body },
            .. PinCalls(containers: [source], verb: RuntimeContract.Pinning.Unpin)
        ];
    }

    /// <summary>
    /// A <c>return</c> leaves every enclosing pinned loop, so it releases their pins (innermost first)
    /// after its value is computed. A value that reaches an element token is computed between that
    /// token's own pin and unpin.
    /// </summary>
    private List<Statement> LowerReturn(ReturnStatement ret)
    {
        List<Expression> containers = ret.Value is { } v
            ? TokenContainers(root: v)
            : [];
        if (containers.Count == 0 && _loopPins.Count == 0)
        {
            return [ret];
        }

        var loopUnpins = PinCalls(containers: Enumerable.Reverse(source: _loopPins).ToList(),
            verb: RuntimeContract.Pinning.Unpin);
        // A value that is only a name or a literal (or none) reads nothing a pin guards, so the loop pins
        // can be released right before it. So can a value whose type is unknown, which cannot be bound.
        if (containers.Count == 0 &&
            ret.Value is null or IdentifierExpression or LiteralExpression ||
            ret.Value is not { ResolvedType: { } valueType } value)
        {
            return [.. loopUnpins, ret];
        }

        (DeclarationStatement decl, IdentifierExpression tmp) =
            MakeTemporary(value: value, type: valueType);
        return
        [
            .. PinCalls(containers: containers, verb: RuntimeContract.Pinning.Pin),
            decl,
            .. PinCalls(containers: containers, verb: RuntimeContract.Pinning.Unpin),
            .. loopUnpins,
            ret with { Value = tmp }
        ];
    }

    /// <summary>
    /// When the head expression of a compound statement reaches an element token, computes it into a
    /// temporary between the token's pin and unpin, so the pin does not stay on for the whole branch.
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
            .. PinCalls(containers: containers, verb: RuntimeContract.Pinning.Pin),
            decl,
            .. PinCalls(containers: containers, verb: RuntimeContract.Pinning.Unpin),
            rebuild(arg: tmp)
        ];
    }

    /// <summary>
    /// The containers of the <c>modify_at</c>/<c>view_at</c> element tokens inside
    /// <paramref name="root"/> whose container is a plain named path (a fresh container value has no
    /// other reference that could change it) and keeps a pin count.
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
                    } && IsNamedPath(expr: container) && IsPinnable(container: container))
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

    private bool IsPinnable(Expression container)
    {
        return container.ResolvedType is { } type &&
               ctx.Registry.LookupMemberRoutine(type: type,
                   memberRoutineName: RuntimeContract.Pinning.Pin) != null;
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

    private List<Statement> PinCalls(List<Expression> containers, string verb)
    {
        var calls = new List<Statement>(capacity: containers.Count);
        foreach (Expression container in containers)
        {
            TypeSymbol type = container.ResolvedType!;
            RoutineInfo routine =
                ctx.Registry.LookupMemberRoutine(type: type, memberRoutineName: verb)!;
            var callee = new MemberExpression(Object: container,
                MemberName: verb,
                Location: container.Location) { ResolvedType = type };
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

    private (DeclarationStatement Declaration, IdentifierExpression Reference) MakeTemporary(
        Expression value, TypeSymbol type)
    {
        string name = $"_pin_{_tempCount++}";
        var declaration = new VariableDeclaration(Name: name,
            Type: ExpressionLoweringPass.TypeInfoToExpr(type: type, loc: value.Location),
            Initializer: value,
            Visibility: VisibilityModifier.Secret,
            Location: value.Location);
        return (new DeclarationStatement(Declaration: declaration, Location: value.Location),
            new IdentifierExpression(Name: name, Location: value.Location) { ResolvedType = type });
    }
}
