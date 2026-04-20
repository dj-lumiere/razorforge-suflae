namespace SemanticVerification;

using TypeModel.Enums;
using SemanticVerification.Enums;
using TypeModel.Symbols;

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
    /// The inferred modification category for this routine.
    /// Seeded from the routine's declared category so user annotations are never downgraded;
    /// propagation can only upgrade toward Migratable.
    /// </summary>
    public ModificationCategory InferredModification { get; set; }

    /// <summary>
    /// Whether this routine directly writes to member variables of 'me'.
    /// </summary>
    public bool DirectlyModifies { get; set; }

    /// <summary>
    /// Whether this routine directly modifies the internal Hijacked buffer (migratable).
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
        InferredModification = routine.DeclaredModification;
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
