namespace Compiler.Synthesis;

using Compiler.Resolution;
using SemanticVerification;
using SemanticVerification.Enums;
using SyntaxTree;

/// <summary>
/// Performs modification inference for routines.
/// Implements the three-phase algorithm from the wiki:
///
/// Phase 1 (Direct Analysis):
///   - If method writes to any member variable of me → mark as Writable
///   - If method calls .hijack() on me member variables → mark as Writable
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
public sealed class ModificationInference
{
    private readonly CallGraph _callGraph;
    private readonly TypeRegistry _registry;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModificationInference"/> class.
    /// </summary>
    /// <param name="callGraph">The call graph to analyze.</param>
    /// <param name="registry">The type registry for lookups.</param>
    public ModificationInference(CallGraph callGraph, TypeRegistry registry)
    {
        _callGraph = callGraph;
        _registry = registry;
    }

    /// <summary>
    /// Runs the complete modification inference algorithm.
    /// </summary>
    public void InferAll()
    {
        // Phase 1: Direct analysis (already done during AST traversal)
        // The DirectlyModifies flag should be set on CallGraphNodes

        // Phase 2: Call graph propagation
        PropagateCategories();
    }

    /// <summary>
    /// Phase 2: Propagates modification categories through the call graph.
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
                ModificationCategory originalCategory = node.InferredModification;
                ModificationCategory newCategory = ComputeCategory(node: node);

                if (newCategory <= originalCategory)
                {
                    continue;
                }

                node.InferredModification = newCategory;
                changed = true;
            }
        }
    }

    /// <summary>
    /// Computes the modification category for a node based on its direct modifications
    /// and the categories of methods it calls on 'me'.
    /// </summary>
    /// <param name="node">The node to compute the category for.</param>
    /// <returns>The computed modification category.</returns>
    private ModificationCategory ComputeCategory(CallGraphNode node)
    {
        // Start from the declared floor — user annotations are never downgraded.
        ModificationCategory category = node.Routine.DeclaredModification;

        // Direct modifications
        if (node.DirectlyModifies && category < ModificationCategory.Writable)
        {
            category = ModificationCategory.Writable;
        }

        if (node.DirectlyMigrates && category < ModificationCategory.Migratable)
        {
            category = ModificationCategory.Migratable;
        }

        // Propagate from callees (only for calls on 'me')
        foreach (CallEdge edge in node.Callees)
        {
            if (!edge.CallsOnMe)
            {
                continue;
            }

            ModificationCategory calleeCategory = edge.Target.InferredModification;
            if (calleeCategory > category)
            {
                category = calleeCategory;
            }
        }

        return category;
    }

    /// <summary>
    /// Analyzes a statement for direct modifications (member variable writes to 'me').
    /// Call this during Phase 1 AST traversal.
    /// </summary>
    /// <param name="node">The call graph node for the current routine.</param>
    /// <param name="statement">The statement to analyze.</param>
    public void AnalyzeStatementForModification(CallGraphNode node, Statement statement)
    {
        switch (statement)
        {
            case AssignmentStatement assign:
                AnalyzeAssignmentForModification(node: node, assignment: assign);
                break;

            case BlockStatement block:
                foreach (Statement stmt in block.Statements)
                {
                    AnalyzeStatementForModification(node: node, statement: stmt);
                }

                break;

            case IfStatement ifStmt:
                AnalyzeStatementForModification(node: node, statement: ifStmt.ThenStatement);
                if (ifStmt.ElseStatement != null)
                {
                    AnalyzeStatementForModification(node: node, statement: ifStmt.ElseStatement);
                }

                break;

            case WhileStatement whileStmt:
                AnalyzeStatementForModification(node: node, statement: whileStmt.Body);
                break;

            case ForStatement forStmt:
                AnalyzeStatementForModification(node: node, statement: forStmt.Body);
                break;

            // Other statement types don't directly modify member variables
        }
    }

    /// <summary>
    /// Analyzes an assignment for direct modifications to 'me'.
    /// </summary>
    /// <param name="node">The call graph node.</param>
    /// <param name="assignment">The assignment statement.</param>
    private void AnalyzeAssignmentForModification(CallGraphNode node,
        AssignmentStatement assignment)
    {
        // Check if the target is me.memberVar or me.memberVar.subfield...
        if (IsMemberVariableOfMe(expression: assignment.Target))
        {
            node.DirectlyModifies = true;
            node.InferredModification = ModificationCategory.Writable;
        }
    }

    /// <summary>
    /// Checks if an expression is a member variable access on 'me' (directly or transitively).
    /// Examples: me.memberVar, me.memberVar.subfield, me.list[0]
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
