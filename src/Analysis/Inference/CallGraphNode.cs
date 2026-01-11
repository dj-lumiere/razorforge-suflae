namespace Compilers.Analysis.Inference;

using Enums;
using Symbols;

/// <summary>
/// Represents a node in the call graph (a single routine).
/// </summary>
public sealed class CallGraphNode
{
    /// <summary>The routine this node represents.</summary>
    public RoutineInfo Routine { get; }

    /// <summary>Routines that this routine calls.</summary>
    private readonly List<CallEdge> _callees = [];

    /// <summary>Routines that call this routine.</summary>
    private readonly List<CallGraphNode> _callers = [];

    /// <summary>
    /// The inferred mutation category for this routine.
    /// Starts as Readonly and propagates up to Writable/Migratable.
    /// </summary>
    public MutationCategory InferredMutation { get; set; } = MutationCategory.Readonly;

    /// <summary>
    /// Whether this routine directly writes to fields of 'me'.
    /// </summary>
    public bool DirectlyMutates { get; set; }

    /// <summary>
    /// Whether this routine directly modifies DynamicSlice (migratable).
    /// </summary>
    public bool DirectlyMigrates { get; set; }

    /// <summary>
    /// Whether this node has been visited during propagation.
    /// </summary>
    public bool Visited { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="CallGraphNode"/> class.
    /// </summary>
    /// <param name="routine">The routine this node represents.</param>
    public CallGraphNode(RoutineInfo routine)
    {
        Routine = routine;
    }

    /// <summary>
    /// Adds a callee (routine that this routine calls).
    /// </summary>
    /// <param name="callee">The called routine's node.</param>
    /// <param name="callsOnMe">Whether this call is on the 'me' reference.</param>
    public void AddCallee(CallGraphNode callee, bool callsOnMe)
    {
        _callees.Add(item: new CallEdge(Target: callee, CallsOnMe: callsOnMe));
    }

    /// <summary>
    /// Adds a caller (routine that calls this routine).
    /// </summary>
    /// <param name="caller">The calling routine's node.</param>
    public void AddCaller(CallGraphNode caller)
    {
        _callers.Add(item: caller);
    }

    /// <summary>Gets all callees (routines called by this routine).</summary>
    public IReadOnlyList<CallEdge> Callees => _callees;

    /// <summary>Gets all callers (routines that call this routine).</summary>
    public IReadOnlyList<CallGraphNode> Callers => _callers;
}
