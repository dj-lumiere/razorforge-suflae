namespace Compilers.Analysis.Inference;

using Enums;
using Shared.AST;

/// <summary>
/// Performs mutation inference for routines.
/// Implements the three-phase algorithm from the wiki:
///
/// Phase 1 (Direct Analysis):
///   - If method writes to any field of me → mark as Writable
///   - If method calls .hijack() on me fields → mark as Writable
///
/// Phase 2 (Call Graph Propagation):
///   - If method calls a Writable method on me → mark as Writable
///   - If method calls a Migratable method on me → mark as Migratable
///   - Repeat until fixpoint (no changes)
///
/// Phase 3 (Token Checking):
///   - Viewed/Inspected tokens can only call Readonly methods
///   - Hijacked/Seized tokens can call Readonly or Writable methods
///   - Only owned/non-token access can call Migratable methods
/// </summary>
public sealed class MutationInference
{
    private readonly CallGraph _callGraph;
    private readonly TypeRegistry _registry;

    /// <summary>
    /// Initializes a new instance of the <see cref="MutationInference"/> class.
    /// </summary>
    /// <param name="callGraph">The call graph to analyze.</param>
    /// <param name="registry">The type registry for lookups.</param>
    public MutationInference(CallGraph callGraph, TypeRegistry registry)
    {
        _callGraph = callGraph;
        _registry = registry;
    }

    /// <summary>
    /// Runs the complete mutation inference algorithm.
    /// </summary>
    public void InferAll()
    {
        // Phase 1: Direct analysis (already done during AST traversal)
        // The DirectlyMutates flag should be set on CallGraphNodes

        // Phase 2: Call graph propagation
        PropagateCategories();
    }

    /// <summary>
    /// Phase 2: Propagates mutation categories through the call graph.
    /// Uses fixpoint iteration until no changes occur.
    /// </summary>
    private void PropagateCategories()
    {
        bool changed = true;

        while (changed)
        {
            changed = false;

            foreach (CallGraphNode node in _callGraph.AllNodes)
            {
                MutationCategory originalCategory = node.InferredMutation;
                MutationCategory newCategory = ComputeCategory(node: node);

                if (newCategory <= originalCategory)
                {
                    continue;
                }

                node.InferredMutation = newCategory;
                changed = true;
            }
        }
    }

    /// <summary>
    /// Computes the mutation category for a node based on its direct mutations
    /// and the categories of methods it calls on 'me'.
    /// </summary>
    /// <param name="node">The node to compute the category for.</param>
    /// <returns>The computed mutation category.</returns>
    private MutationCategory ComputeCategory(CallGraphNode node)
    {
        MutationCategory category = MutationCategory.Readonly;

        // Direct mutations
        if (node.DirectlyMutates)
        {
            category = MutationCategory.Writable;
        }

        if (node.DirectlyMigrates)
        {
            category = MutationCategory.Migratable;
        }

        // Propagate from callees (only for calls on 'me')
        foreach (CallEdge edge in node.Callees)
        {
            if (!edge.CallsOnMe)
            {
                continue;
            }

            MutationCategory calleeCategory = edge.Target.InferredMutation;
            if (calleeCategory > category)
            {
                category = calleeCategory;
            }
        }

        return category;
    }

    /// <summary>
    /// Analyzes a statement for direct mutations (field writes to 'me').
    /// Call this during Phase 1 AST traversal.
    /// </summary>
    /// <param name="node">The call graph node for the current routine.</param>
    /// <param name="statement">The statement to analyze.</param>
    public void AnalyzeStatementForMutation(CallGraphNode node, Statement statement)
    {
        switch (statement)
        {
            case AssignmentStatement assign:
                AnalyzeAssignmentForMutation(node: node, assignment: assign);
                break;

            case BlockStatement block:
                foreach (Statement stmt in block.Statements)
                {
                    AnalyzeStatementForMutation(node: node, statement: stmt);
                }

                break;

            case IfStatement ifStmt:
                AnalyzeStatementForMutation(node: node, statement: ifStmt.ThenStatement);
                if (ifStmt.ElseStatement != null)
                {
                    AnalyzeStatementForMutation(node: node, statement: ifStmt.ElseStatement);
                }

                break;

            case WhileStatement whileStmt:
                AnalyzeStatementForMutation(node: node, statement: whileStmt.Body);
                break;

            case ForStatement forStmt:
                AnalyzeStatementForMutation(node: node, statement: forStmt.Body);
                break;

            // Other statement types don't directly mutate fields
        }
    }

    /// <summary>
    /// Analyzes an assignment for direct mutations to 'me'.
    /// </summary>
    /// <param name="node">The call graph node.</param>
    /// <param name="assignment">The assignment statement.</param>
    private void AnalyzeAssignmentForMutation(CallGraphNode node, AssignmentStatement assignment)
    {
        // Check if the target is me.field or me.field.subfield...
        if (IsFieldOfMe(expression: assignment.Target))
        {
            node.DirectlyMutates = true;
            node.InferredMutation = MutationCategory.Writable;
        }
    }

    /// <summary>
    /// Checks if an expression is a field access on 'me' (directly or transitively).
    /// Examples: me.field, me.field.subfield, me.list[0]
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
