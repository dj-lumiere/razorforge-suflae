using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Collection;

/// <summary>
/// Which of the routines about to be emitted can crash, directly or through anything they call. A crash
/// stack trace only ever shows frames of routines on the path to the crash, so a routine that cannot crash
/// (and calls nothing that can) never appears in one and needs no trace frame. Without this, every inlined
/// operator routine (<c>U64.lshr</c>, <c>U128.bitand</c>, ...) kept its trace push/pop and per-call location
/// stores in release builds, about 55 of the 80 ns of a B128 <c>exp</c>.
/// <para>A routine can crash when it is failable or makes a failable call (checked arithmetic included), calls
/// a routine VALUE or a routine whose body is not in this build (a C function, a routine in the JIT base),
/// uses an operator on a type that is not a plain number (its operator routine is not visible as a call
/// here), or calls a routine that can crash. A <c>danger</c> block is not a crash source by itself: the trace
/// reports RazorForge crashes (throw/absent and failable calls), and a bad memory access inside `danger` is
/// outside that model. Solved as a
/// fixpoint over all bodies, so recursive cycles get the cycle's answer. Anything unknown counts as able to
/// crash, which only keeps a frame that was not needed.</para>
/// </summary>
internal sealed class CrashReachability
{
    private readonly Dictionary<string, Statement> _bodies = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, RoutineInfo> _infos = new(comparer: StringComparer.Ordinal);
    private HashSet<string>? _canCrash;

    /// <summary>Registers a routine that will be emitted, with its body.</summary>
    public void Add(RoutineInfo routine, Statement? body)
    {
        if (body == null || !_bodies.TryAdd(key: routine.RegistryKey, value: body))
        {
            return;
        }

        _infos[key: routine.RegistryKey] = routine;
        _canCrash = null;
    }

    /// <summary>Whether <paramref name="routine"/> can crash, directly or through its callees.</summary>
    public bool CanCrash(RoutineInfo routine)
    {
        _canCrash ??= Solve();
        return !_bodies.ContainsKey(key: routine.RegistryKey)
            ? CalleeCanCrashWithoutBody(callee: routine)
            : _canCrash.Contains(item: routine.RegistryKey);
    }

    private HashSet<string> Solve()
    {
        var crash = new HashSet<string>(comparer: StringComparer.Ordinal);
        var callees = new Dictionary<string, List<string>>(comparer: StringComparer.Ordinal);
        foreach ((string key, Statement body) in _bodies)
        {
            var edges = new List<string>();
            if (CrashesLocally(routine: _infos[key: key], body: body, edges: edges))
            {
                crash.Add(item: key);
            }

            callees[key: key] = edges;
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach ((string key, List<string> edges) in callees)
            {
                if (!crash.Contains(item: key) && edges.Any(predicate: crash.Contains))
                {
                    changed |= crash.Add(item: key);
                }
            }
        }

        return crash;
    }

    // Local crash sources of one body; each call to a routine with a body in this build becomes an edge.
    private bool CrashesLocally(RoutineInfo routine, Statement body, List<string> edges)
    {
        if (routine.IsFailable || routine.HasFailableCalls)
        {
            return true;
        }

        bool crashes = false;
        AstWalker.Walk(root: body,
            visit: node =>
            {
                if (crashes)
                {
                    return;
                }

                switch (node)
                {
                    case CallExpression call:
                        crashes = call.ResolvedRoutine is { } target
                            ? Callee(callee: target, edges: edges)
                            : call.LoweringKind is not (CallLoweringKind.TypeConstructor
                                or CallLoweringKind.BuilderIntrinsic or CallLoweringKind.LlvmIntrinsic);
                        break;
                    case GenericMemberRoutineCallExpression { ResolvedRoutine: { } generic }:
                        crashes = Callee(callee: generic, edges: edges);
                        break;
                    case CreatorExpression { ResolvedCreatorRoutine: { } creator }:
                        crashes = Callee(callee: creator, edges: edges);
                        break;
                    case BinaryExpression binary when !IsPlainOperand(type: binary.Left.ResolvedType):
                        crashes = true;
                        break;
                }
            });
        return crashes;
    }

    // True when the call can crash no matter what else is known; otherwise records the edge.
    private bool Callee(RoutineInfo callee, List<string> edges)
    {
        if (_bodies.ContainsKey(key: callee.RegistryKey))
        {
            edges.Add(item: callee.RegistryKey);
            return false;
        }

        return CalleeCanCrashWithoutBody(callee: callee);
    }

    // A callee with no body here: an LLVM intrinsic cannot crash; anything else (a C function, a routine
    // emitted elsewhere) is assumed to.
    private static bool CalleeCanCrashWithoutBody(RoutineInfo callee)
    {
        return callee.LlvmIrTemplate == null || callee.IsFailable || callee.HasFailableCalls;
    }

    // A number, Bool or Character operand: its operators are wrapping/unchecked instructions or routines
    // whose failability the verifier already recorded on the caller (HasFailableCalls).
    private static bool IsPlainOperand(TypeSymbol? type)
    {
        return type is RecordTypeSymbol { BackendType: not null } && type.TypeArguments is not { Count: > 0 };
    }
}
