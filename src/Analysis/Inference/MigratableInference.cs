namespace SemanticAnalysis.Inference;

using Enums;
using Types;
using SyntaxTree;

/// <summary>
/// Performs migratable inference for routines.
/// Detects methods that can relocate container buffers (Snatched<Byte>).
///
/// Base case: A method is migratable if it can relocate the Snatched<Byte> buffer
/// (by changing pointer, size, or capacity in ways that trigger reallocation).
///
/// Migratable operations are banned during iteration to prevent iterator invalidation.
/// </summary>
public sealed class MigratableInference
{
    private readonly CallGraph _callGraph;
    private readonly TypeRegistry _registry;

    /// <summary>
    /// Initializes a new instance of the <see cref="MigratableInference"/> class.
    /// </summary>
    /// <param name="callGraph">The call graph to analyze.</param>
    /// <param name="registry">The type registry for lookups.</param>
    public MigratableInference(CallGraph callGraph, TypeRegistry registry)
    {
        _callGraph = callGraph;
        _registry = registry;
    }

    /// <summary>
    /// Runs the complete migratable inference algorithm.
    /// </summary>
    public void InferAll()
    {
        // Phase 1: Detect direct Snatched<U8> modifications
        DetectDirectMigrations();

        // Phase 2: Propagate through call graph
        PropagateCategories();
    }

    /// <summary>
    /// Phase 1: Detects methods that directly modify Snatched<U8>.
    /// </summary>
    /// <remarks>
    /// TODO: Implement base case detection for migratable methods.
    /// A method is migratable if it reassigns the internal Snatched<U8> member variable of an entity.
    /// All entity types use Snatched<U8> internally for their dynamic storage.
    /// Detection should analyze method bodies for assignments to Snatched<U8>-typed member variables.
    /// </remarks>
    private void DetectDirectMigrations()
    {
        foreach (CallGraphNode node in _callGraph.AllNodes)
        {
            if (node.Routine.OwnerType == null)
            {
                continue;
            }

            // TODO: Base case detection - a method is migratable if it:
            // 1. Operates on an entity type (all entities have internal Snatched<U8>)
            // 2. Contains assignments that modify the Snatched<U8> member variable
            //    (pointer, size, or capacity changes that could trigger reallocation)
            //
            // This requires analyzing the method body for Snatched<U8> member variable writes.
            // Currently no detection is implemented - this is a placeholder.
        }
    }

    /// <summary>
    /// Phase 2: Propagates migratable category through the call graph.
    /// </summary>
    private void PropagateCategories()
    {
        bool changed = true;

        while (changed)
        {
            changed = false;

            foreach (CallGraphNode node in _callGraph.AllNodes)
            {
                // Only upgrade to Migratable if calling migratable methods on me
                if (node.InferredModification >= ModificationCategory.Migratable)
                {
                    continue;
                }

                if (!node.Callees.Any(predicate: edge => edge is
                    {
                        CallsOnMe: true,
                        Target.InferredModification: ModificationCategory.Migratable
                    }))
                {
                    continue;
                }

                node.InferredModification = ModificationCategory.Migratable;
                changed = true;
            }
        }
    }

    /// <summary>
    /// Analyzes a method call to check if it's migratable.
    /// Method calls are represented as CallExpression with a MemberExpression callee.
    /// </summary>
    /// <param name="node">The call graph node for the current routine.</param>
    /// <param name="callExpr">The call expression.</param>
    /// <param name="methodName">The method name being called.</param>
    /// <param name="receiver">The receiver expression (object the method is called on).</param>
    /// <param name="receiverType">The type of the receiver.</param>
    /// <remarks>
    /// TODO: Implement call-site migratable detection.
    /// This should check if the called method (methodName on receiverType) is migratable,
    /// and if the receiver is a member variable of 'me', mark the calling method as migratable too.
    /// Currently no detection is implemented - this is a placeholder.
    /// </remarks>
    public void AnalyzeCallForMigration(CallGraphNode node, CallExpression callExpr,
        string methodName, Expression receiver, TypeInfo receiverType)
    {
        // TODO: Check if the called method is migratable (from the call graph)
        // and if the receiver is a member variable of 'me', propagate migratable to the caller.
        //
        // This requires:
        // 1. Looking up the called method in the call graph
        // 2. Checking if it's marked as migratable
        // 3. If the receiver is me or me.memberVar, mark the current method as migratable

        if (!IsMemberVariableOfMe(expression: receiver))
        {
            return;
        }

        // Placeholder: lookup would happen here
        // RoutineInfo? calledRoutine = _registry.LookupRoutine($"{receiverType.Name}.{methodName}");
        // CallGraphNode? calledNode = _callGraph.GetNode(calledRoutine);
        // if (calledNode?.InferredModification == ModificationCategory.Migratable) { ... }
    }

    /// <summary>
    /// Checks if an expression is a member variable access on 'me'.
    /// </summary>
    /// <param name="expression">The expression to check.</param>
    /// <returns>True if the expression accesses a member variable of 'me'.</returns>
    private bool IsMemberVariableOfMe(Expression expression)
    {
        return expression switch
        {
            MemberExpression memberExpr =>
                memberExpr.Object is IdentifierExpression { Name: "me" } ||
                IsMemberVariableOfMe(expression: memberExpr.Object),

            IndexExpression indexExpr => IsMemberVariableOfMe(expression: indexExpr.Object),

            IdentifierExpression { Name: "me" } => true,

            _ => false
        };
    }
}
