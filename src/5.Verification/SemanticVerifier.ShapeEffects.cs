using Builder.Declaration;
using Builder.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// Build-time shape checks (RazorForge). A container's SHAPE is its element count and positions; a
/// <c>@reshaping</c> routine can change it. While an <c>each</c> loop goes through a container, or a statement
/// works on one of its entity elements in place, the shape must not change: the loop could no longer trust
/// its next element, and the element access could be left pointing at a moved or freed element.
/// <para>The direct case (<c>xs.add_last(...)</c> inside <c>each x in xs</c>) is RF-S625 at the call. This
/// check also sees the INDIRECT cases, because every routine gets a RESHAPE EFFECT: the set of its
/// parameters (and <c>me</c>) whose shape it may change. A <c>@reshaping</c> routine changes <c>me</c>; a
/// routine that hands one of its parameters to a slot that is changed changes that parameter too,
/// transitively through user routines. Stdlib routines count through their <c>@reshaping</c> marker.</para>
/// <para>Two paths the effect cannot follow are rejected outright: handing the container to a routine VALUE
/// (its target is not known at build time), and changing another shared handle (<c>Retained</c>/
/// <c>Tracked</c>) of the same kind of container, which may be the same container.</para>
/// <para>Runs once after all user bodies are analyzed, so every call is resolved. Suflae keeps the runtime
/// check instead (its containers are shared <c>Roamed</c> handles the build cannot track).</para>
/// </summary>
public sealed partial class SemanticVerifier
{
    private const string MeSlot = "me";

    /// <summary>Token mints and marker coercions that hand out the receiver itself.</summary>
    private static readonly HashSet<string> ReceiverPassThroughVerbs =
        new(comparer: StringComparer.Ordinal)
        {
            "view",
            "modify",
            "consult",
            "amend",
            RuntimeContract.Access,
            RuntimeContract.Control
        };

    private readonly Dictionary<string, RoutineDeclaration> _userRoutineDeclarations =
        new(comparer: StringComparer.Ordinal);

    private readonly Dictionary<RoutineDeclaration, HashSet<string>> _reshapeEffects =
        new(comparer: ReferenceEqualityComparer.Instance);

    private readonly HashSet<RoutineDeclaration> _reshapeEffectsInProgress =
        new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>A container whose shape must stay still: its access path, its type, and why.</summary>
    private sealed record ShapeGuard(string Path, TypeSymbol? Type, bool IsLoop);

    /// <summary>Runs the shape checks over the user programs (RazorForge only).</summary>
    private void CheckShapeEffects(IEnumerable<Program> programs)
    {
        if (_registry.Language != Language.RazorForge)
        {
            return;
        }

        List<Program> userPrograms = programs.ToList();
        foreach (RoutineDeclaration routine in userPrograms.SelectMany(selector: EnumerateRoutines))
        {
            if (routine.ResolvedInfo is { } info)
            {
                _userRoutineDeclarations.TryAdd(key: info.RegistryKey, value: routine);
            }
        }

        foreach (RoutineDeclaration routine in userPrograms.SelectMany(selector: EnumerateRoutines))
        {
            CheckStatementShapes(stmt: routine.Body, guards: [], preceding: []);
        }
    }

    private static IEnumerable<RoutineDeclaration> EnumerateRoutines(Program program)
    {
        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            switch (node)
            {
                case RoutineDeclaration r:
                    yield return r;
                    break;
                case EntityDeclaration e:
                    foreach (RoutineDeclaration m in e.Members.OfType<RoutineDeclaration>())
                    {
                        yield return m;
                    }

                    break;
                case RecordDeclaration rec:
                    foreach (RoutineDeclaration m in rec.Members.OfType<RoutineDeclaration>())
                    {
                        yield return m;
                    }

                    break;
                case CrashableDeclaration cr:
                    foreach (RoutineDeclaration m in cr.Members.OfType<RoutineDeclaration>())
                    {
                        yield return m;
                    }

                    break;
            }
        }
    }

    // ---- Reshape effects ------------------------------------------------------------------------------

    /// <summary>The slots (parameter names, and <c>me</c>) whose shape a call to <paramref name="callee"/>
    /// may change.</summary>
    private HashSet<string> ReshapedSlots(RoutineInfo callee)
    {
        var slots = new HashSet<string>(comparer: StringComparer.Ordinal);
        if (callee.IsReshaping)
        {
            slots.Add(item: MeSlot);
        }

        RoutineDeclaration? decl = FindUserDeclaration(routine: callee);
        if (decl != null)
        {
            slots.UnionWith(other: InferReshapedSlots(routine: decl));
        }

        return slots;
    }

    private RoutineDeclaration? FindUserDeclaration(RoutineInfo routine)
    {
        if (_userRoutineDeclarations.TryGetValue(key: routine.RegistryKey, value: out RoutineDeclaration? d))
        {
            return d;
        }

        return routine.GenericDefinition is { } def &&
               _userRoutineDeclarations.TryGetValue(key: def.RegistryKey, value: out RoutineDeclaration? g)
            ? g
            : null;
    }

    /// <summary>
    /// The parameters (and <c>me</c>) whose shape <paramref name="routine"/>'s body may change: every
    /// parameter it hands to a changed slot of a call, or to a routine value. Memoized. A recursive cycle
    /// sees the partial result, which only grows, so the fixpoint is reached by the time the cycle
    /// unwinds for self-recursion and simple mutual recursion.
    /// </summary>
    private HashSet<string> InferReshapedSlots(RoutineDeclaration routine)
    {
        if (_reshapeEffects.TryGetValue(key: routine, value: out HashSet<string>? known))
        {
            return known;
        }

        var slots = new HashSet<string>(comparer: StringComparer.Ordinal);
        _reshapeEffects[key: routine] = slots;
        if (!_reshapeEffectsInProgress.Add(item: routine))
        {
            return slots;
        }

        var own = new HashSet<string>(
            collection: routine.Parameters.Select(selector: p => p.Name).Append(element: MeSlot),
            comparer: StringComparer.Ordinal);
        AstWalker.WalkExpressions(root: routine.Body,
            visit: e =>
            {
                if (e is not CallExpression call)
                {
                    return;
                }

                foreach ((Expression arg, string _) in ChangedOperands(call: call))
                {
                    if (ShapePath(expr: arg) is { } path && own.Contains(item: RootName(path: path)))
                    {
                        slots.Add(item: RootName(path: path));
                    }
                }
            });
        _reshapeEffectsInProgress.Remove(item: routine);
        return slots;
    }

    /// <summary>
    /// The operands of <paramref name="call"/> whose shape the call may change, each with the reason's
    /// callee name: the receiver when the callee changes <c>me</c>, each argument bound to a changed
    /// parameter, and every argument of a routine-value call (its target is unknown).
    /// </summary>
    private IEnumerable<(Expression Operand, string Callee)> ChangedOperands(CallExpression call)
    {
        if (IsRoutineValueCall(call: call))
        {
            foreach (Expression arg in call.Arguments)
            {
                yield return (Unwrap(expr: arg), "");
            }

            yield break;
        }

        if (call.ResolvedRoutine is not { } callee)
        {
            yield break;
        }

        HashSet<string> slots = ReshapedSlots(callee: callee);
        if (slots.Count == 0)
        {
            yield break;
        }

        if (slots.Contains(item: MeSlot) && call.Callee is MemberExpression { Object: var receiver } &&
            callee.Kind is not (RoutineKind.CommonRoutine or RoutineKind.Creator))
        {
            yield return (receiver, callee.Name);
        }

        for (int i = 0; i < call.Arguments.Count; i++)
        {
            Expression arg = call.Arguments[index: i];
            string? paramName = arg is NamedArgumentExpression named
                ? named.Name
                : i < callee.Parameters.Count
                    ? callee.Parameters[index: i].Name
                    : null;
            if (paramName != null && slots.Contains(item: paramName))
            {
                yield return (Unwrap(expr: arg), callee.Name);
            }
        }
    }

    private static bool IsRoutineValueCall(CallExpression call)
    {
        return call.Callee.ResolvedType is RoutineTypeSymbol;
    }

    private static Expression Unwrap(Expression expr)
    {
        return expr is NamedArgumentExpression named
            ? named.Value
            : expr;
    }

    /// <summary>
    /// The container path an operand stands for: a name or field chain, seen through a token mint or a
    /// marker coercion (<c>xs.modify()</c> is <c>xs</c>). Null for anything else.
    /// </summary>
    private static string? ShapePath(Expression expr)
    {
        return Unwrap(expr: expr) switch
        {
            IdentifierExpression id => id.Name,
            MemberExpression { Object: var inner, MemberName: var field } =>
                ShapePath(expr: inner) is { } prefix
                    ? $"{prefix}.{field}"
                    : null,
            CallExpression
            {
                Callee: MemberExpression { Object: var receiver, MemberName: var verb },
                Arguments.Count: 0
            } when ReceiverPassThroughVerbs.Contains(item: verb) => ShapePath(expr: receiver),
            _ => null
        };
    }

    private static string RootName(string path)
    {
        int dot = path.IndexOf(value: '.');
        return dot < 0
            ? path
            : path[..dot];
    }

    // ---- Guarded regions ------------------------------------------------------------------------------

    /// <summary>
    /// Walks a statement, checking the calls in every region where a container's shape must stay still:
    /// the body of an <c>each</c> loop over a named container, and a statement that works on an entity
    /// element of a named container in place.
    /// </summary>
    private void CheckStatementShapes(Statement stmt, List<ShapeGuard> guards,
        List<Statement> preceding)
    {
        switch (stmt)
        {
            case BlockStatement block:
            {
                var seen = new List<Statement>();
                foreach (Statement s in block.Statements)
                {
                    CheckStatementShapes(stmt: s, guards: guards, preceding: seen);
                    seen.Add(item: s);
                }

                return;
            }
            case DangerStatement danger:
                CheckStatementShapes(stmt: danger.Body, guards: guards, preceding: []);
                return;
            case LoopStatement loop:
            {
                List<ShapeGuard> inner = guards;
                if (loop.IterationSourceName is { } name)
                {
                    inner = [.. guards, new ShapeGuard(Path: name,
                        Type: LoopSourceType(name: name, preceding: preceding),
                        IsLoop: true)];
                }

                CheckStatementShapes(stmt: loop.Body, guards: inner, preceding: []);
                return;
            }
            case IfStatement ifs:
                CheckOwnExpressions(exprs: [ifs.Condition], guards: guards);
                CheckStatementShapes(stmt: ifs.ThenStatement, guards: guards, preceding: []);
                if (ifs.ElseStatement is { } alt)
                {
                    CheckStatementShapes(stmt: alt, guards: guards, preceding: []);
                }

                return;
            case WhenStatement whenStmt:
                CheckOwnExpressions(exprs: [whenStmt.Expression], guards: guards);
                foreach (WhenClause clause in whenStmt.Clauses)
                {
                    CheckStatementShapes(stmt: clause.Body, guards: guards, preceding: []);
                }

                return;
            case UsingStatement usingStmt:
                CheckOwnExpressions(exprs: [usingStmt.Resource], guards: guards);
                CheckStatementShapes(stmt: usingStmt.Body, guards: guards, preceding: []);
                if (usingStmt.FallbackBody is { } fallback)
                {
                    CheckStatementShapes(stmt: fallback, guards: guards, preceding: []);
                }

                return;
            default:
                CheckOwnExpressions(exprs: OwnExpressions(stmt: stmt), guards: guards);
                return;
        }
    }

    private static List<Expression> OwnExpressions(Statement stmt)
    {
        return stmt switch
        {
            ExpressionStatement s => [s.Expression],
            DiscardStatement s => [s.Expression],
            ReturnStatement { Value: { } v } => [v],
            ThrowStatement s => [s.Error],
            BecomesStatement s => [s.Value],
            AssignmentStatement s => [s.Target, s.Value],
            DeclarationStatement { Declaration: VariableDeclaration { Initializer: { } init } } => [init],
            _ => []
        };
    }

    /// <summary>
    /// Checks one statement's own expressions: against the enclosing loop guards, and against the element
    /// guard the statement itself opens when it works on an entity element of a named container.
    /// </summary>
    private void CheckOwnExpressions(List<Expression> exprs, List<ShapeGuard> guards)
    {
        var active = new List<ShapeGuard>(collection: guards);
        foreach (Expression e in exprs)
        {
            AstWalker.WalkExpressions(root: e,
                visit: node =>
                {
                    Expression? owner = node switch
                    {
                        CallExpression { Callee: MemberExpression { Object: var o } } => o,
                        BinaryExpression { Operator: BinaryOperator.Assign, Left: MemberExpression m } => m.Object,
                        BinaryExpression { Operator: BinaryOperator.Assign, Left: IndexExpression ix } => ix.Object,
                        _ => null
                    };
                    if (owner != null && EntityElementContainer(expr: owner) is { } element &&
                        !active.Any(predicate: g => !g.IsLoop && g.Path == element.Path))
                    {
                        active.Add(item: element);
                    }
                });
        }

        if (active.Count == 0)
        {
            return;
        }

        foreach (Expression e in exprs)
        {
            AstWalker.WalkExpressions(root: e,
                visit: node =>
                {
                    switch (node)
                    {
                        case CallExpression call:
                            CheckCallAgainstGuards(call: call, guards: active);
                            break;
                        case StealExpression steal when ShapePath(expr: steal.Operand) is { } stolen:
                            foreach (ShapeGuard g in active.Where(predicate: g => !g.IsLoop))
                            {
                                if (IsPathPrefixOrEqual(prefix: stolen, path: g.Path))
                                {
                                    ReportShapeChange(guard: g,
                                        attempt: $"steal '{stolen}'",
                                        why: $"'{stolen}' would move away with the element",
                                        location: steal.Location);
                                }
                            }

                            break;
                    }
                });
        }
    }

    /// <summary>The element guard for a path that reaches an entity element of a named container.</summary>
    private static ShapeGuard? EntityElementContainer(Expression expr)
    {
        Expression cur = expr;
        while (cur is MemberExpression member)
        {
            cur = member.Object;
        }

        return cur is IndexExpression { ResolvedType: EntityTypeSymbol } element &&
               ShapePath(expr: element.Object) is { } path
            ? new ShapeGuard(Path: path, Type: element.Object.ResolvedType, IsLoop: false)
            : null;
    }

    private void CheckCallAgainstGuards(CallExpression call, List<ShapeGuard> guards)
    {
        foreach ((Expression operand, string callee) in ChangedOperands(call: call))
        {
            string? path = ShapePath(expr: operand);
            foreach (ShapeGuard g in guards)
            {
                // The direct `@reshaping` call on the loop variable itself is RF-S625 at the call site.
                if (g.IsLoop && operand is IdentifierExpression { Name: var direct } && direct == g.Path &&
                    call.ResolvedRoutine?.IsReshaping == true && call.Callee is MemberExpression)
                {
                    continue;
                }

                if (path != null && IsPathPrefixOrEqual(prefix: path, path: g.Path))
                {
                    string attempt = callee.Length == 0
                        ? $"pass '{path}' to a routine value"
                        : operand is IdentifierExpression or MemberExpression &&
                          call.Callee is MemberExpression { Object: var r } && ReferenceEquals(objA: r, objB: operand)
                            ? $"call '{path}.{callee}()'"
                            : $"pass '{path}' to '{callee}'";
                    string why = callee.Length == 0
                        ? "the builder cannot tell whether that routine adds to or removes from it"
                        : $"'{callee}' can add to or remove from it";
                    ReportShapeChange(guard: g, attempt: attempt, why: why, location: call.Location);
                }
                else if (path != null && IsSharedHandleOfSameKind(a: operand.ResolvedType, b: g.Type))
                {
                    ReportShapeChange(guard: g,
                        attempt: callee.Length == 0
                            ? $"pass '{path}' to a routine value"
                            : $"change '{path}' through '{callee}'",
                        why: $"'{path}' and '{g.Path}' are both shared handles to a " +
                             $"{SharedHandleInner(type: g.Type!)!.Name}, so they may be the same one",
                        location: call.Location);
                }
            }
        }
    }

    /// <summary>Two shared (reference-counted) handles to the same kind of container.</summary>
    private static bool IsSharedHandleOfSameKind(TypeSymbol? a, TypeSymbol? b)
    {
        return a != null && b != null && SharedHandleInner(type: a) is { } ia &&
               SharedHandleInner(type: b) is { } ib && ia.FullName == ib.FullName;
    }

    private static TypeSymbol? SharedHandleInner(TypeSymbol type)
    {
        return TypeRegistry.GetRcWrapperBaseName(type: type) is { } rc &&
               rc != RuntimeContract.Roamed && type.TypeArguments is [{ } inner]
            ? inner
            : null;
    }

    private void ReportShapeChange(ShapeGuard guard, string attempt, string why,
        SourceLocation location)
    {
        if (guard.IsLoop)
        {
            ReportError(code: SemanticDiagnosticCode.ReshapingDuringIteration,
                message:
                $"You are trying to {attempt} while an `each` loop is going through '{guard.Path}': {why}. " +
                "After such a change the loop could no longer trust that its next element is really the " +
                "next one. Make the change after the loop.",
                location: location);
            return;
        }

        ReportError(code: SemanticDiagnosticCode.TokenSourceReplaced,
            message:
            $"You are trying to {attempt} while this statement works on an element of '{guard.Path}' in " +
            $"place: {why}. That could move or free the element under it. Do it in a separate statement " +
            "before or after.",
            location: location);
    }

    /// <summary>The type of an <c>each</c> loop's named source, read from its iterator declaration among
    /// the statements before the loop (<c>var _iter = xs.iter()</c>).</summary>
    private static TypeSymbol? LoopSourceType(string name, List<Statement> preceding)
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

            TypeSymbol? found = null;
            AstWalker.WalkExpressions(root: init,
                visit: e =>
                {
                    if (e is IdentifierExpression { ResolvedType: { } t } id && id.Name == name)
                    {
                        found ??= t;
                    }
                });
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }
}
