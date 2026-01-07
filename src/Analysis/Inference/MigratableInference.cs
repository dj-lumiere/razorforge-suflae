namespace Compilers.Analysis.Inference;

using Compilers.Analysis.Enums;
using Compilers.Analysis.Symbols;
using Compilers.Analysis.Types;
using Compilers.Shared.AST;

/// <summary>
/// Performs migratable inference for routines.
/// Detects methods that can relocate container buffers (DynamicSlice).
///
/// Base case: A method is migratable if it can relocate the DynamicSlice buffer
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
        // Phase 1: Detect direct DynamicSlice modifications
        DetectDirectMigrations();

        // Phase 2: Propagate through call graph
        PropagateCategories();
    }

    /// <summary>
    /// Phase 1: Detects methods that directly modify DynamicSlice.
    /// </summary>
    /// <remarks>
    /// TODO: Implement base case detection for migratable methods.
    /// A method is migratable if it reassigns the internal DynamicSlice field of an entity.
    /// All entity types use DynamicSlice internally for their dynamic storage.
    /// Detection should analyze method bodies for assignments to DynamicSlice-typed fields.
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
            // 1. Operates on an entity type (all entities have internal DynamicSlice)
            // 2. Contains assignments that modify the DynamicSlice field
            //    (pointer, size, or capacity changes that could trigger reallocation)
            //
            // This requires analyzing the method body for DynamicSlice field writes.
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
                if (node.InferredMutation >= MutationCategory.Migratable)
                {
                    continue;
                }

                if (!node.Callees.Any(edge => edge is { CallsOnMe: true, Target.InferredMutation: MutationCategory.Migratable }))
                {
                    continue;
                }

                node.InferredMutation = MutationCategory.Migratable;
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
    /// and if the receiver is a field of 'me', mark the calling method as migratable too.
    /// Currently no detection is implemented - this is a placeholder.
    /// </remarks>
    public void AnalyzeCallForMigration(CallGraphNode node, CallExpression callExpr,
        string methodName, Expression receiver, Types.TypeInfo receiverType)
    {
        // TODO: Check if the called method is migratable (from the call graph)
        // and if the receiver is a field of 'me', propagate migratable to the caller.
        //
        // This requires:
        // 1. Looking up the called method in the call graph
        // 2. Checking if it's marked as migratable
        // 3. If the receiver is me or me.field, mark the current method as migratable

        if (!IsFieldOfMe(expression: receiver))
        {
            return;
        }

        // Placeholder: lookup would happen here
        // RoutineInfo? calledRoutine = _registry.LookupRoutine($"{receiverType.Name}.{methodName}");
        // CallGraphNode? calledNode = _callGraph.GetNode(calledRoutine);
        // if (calledNode?.InferredMutation == MutationCategory.Migratable) { ... }
    }

    /// <summary>
    /// Checks if an expression is a field access on 'me'.
    /// </summary>
    /// <param name="expression">The expression to check.</param>
    /// <returns>True if the expression accesses a field of 'me'.</returns>
    private bool IsFieldOfMe(Expression expression)
    {
        return expression switch
        {
            MemberExpression memberExpr =>
                memberExpr.Object is IdentifierExpression { Name: "me" } ||
                IsFieldOfMe(expression: memberExpr.Object),

            IndexExpression indexExpr =>
                IsFieldOfMe(expression: indexExpr.Object),

            IdentifierExpression { Name: "me" } => true,

            _ => false
        };
    }
}
