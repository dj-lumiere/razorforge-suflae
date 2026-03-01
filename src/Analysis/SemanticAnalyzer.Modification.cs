namespace SemanticAnalysis;

using Enums;
using Inference;
using Symbols;
using SyntaxTree;
using Diagnostics;

/// <summary>
/// Phase 4: Modification inference for RazorForge.
/// Implements the three-phase algorithm from the wiki:
///
/// Phase 1: Direct analysis - detect me.field = value patterns (done during body analysis)
/// Phase 2: Call graph propagation - if A calls modifying B on me, A is modifying
/// Phase 3: Token verification - verify modifying methods called with ! token (enforced at call sites)
/// </summary>
public sealed partial class SemanticAnalyzer
{
    #region Phase 4: Mutation Inference

    /// <summary>
    /// Infers modification categories for all routines using call graph analysis.
    /// Called after Phase 3 body analysis is complete.
    /// </summary>
    private void InferModificationCategories()
    {
        // Create the modification inference engine and run propagation
        _modificationInference = new ModificationInference(callGraph: _callGraph, registry: _registry);
        _modificationInference.InferAll();

        // Apply inferred categories back to RoutineInfo
        foreach (CallGraphNode node in _callGraph.AllNodes)
        {
            node.Routine.ModificationCategory = node.InferredModification;
        }
    }

    /// <summary>
    /// Tracks a call for call graph building.
    /// Called during expression analysis when a method call is encountered.
    /// </summary>
    /// <param name="callee">The routine being called.</param>
    /// <param name="callOnMe">Whether the call is on 'me' (affects modification propagation).</param>
    private void TrackCall(RoutineInfo callee, bool callOnMe)
    {
        if (_currentRoutine == null)
        {
            return;
        }

        _callGraph.AddEdge(
            caller: _currentRoutine,
            callee: callee,
            callsOnMe: callOnMe);
    }

    /// <summary>
    /// Analyzes a statement for direct modifications to 'me' fields.
    /// Called during Phase 3 body analysis.
    /// </summary>
    /// <param name="statement">The statement to analyze for modifications.</param>
    private void AnalyzeStatementForModifications(Statement statement)
    {
        if (_currentCallGraphNode == null)
        {
            return;
        }

        _modificationInference?.AnalyzeStatementForModification(
            node: _currentCallGraphNode,
            statement: statement);
    }

    /// <summary>
    /// Checks if an expression represents a call on 'me'.
    /// Used for call graph edge annotation.
    /// </summary>
    /// <param name="target">The call target expression.</param>
    /// <returns>True if the call is on 'me' or a field of 'me'.</returns>
    private bool IsCallOnMe(Expression target)
    {
        return target switch
        {
            // me.method() - direct call on me
            MemberExpression { Object: IdentifierExpression { Name: "me" } } => true,

            // me.field.method() - call on a field of me
            MemberExpression member => IsFieldOfMe(expression: member.Object),

            // me itself (for protocols)
            IdentifierExpression { Name: "me" } => true,

            _ => false
        };
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

    /// <summary>
    /// Validates that a modifying call uses the correct token.
    /// Called during method call analysis to enforce modifiability rules.
    /// </summary>
    /// <param name="callee">The routine being called.</param>
    /// <param name="callExpr">The call expression.</param>
    /// <param name="hasModifyingToken">Whether the ! token was used.</param>
    private void ValidateModifyingCall(RoutineInfo callee, Expression callExpr, bool hasModifyingToken)
    {
        ModificationCategory calleeCategory = callee.ModificationCategory;

        // Readonly methods don't need special tokens
        if (calleeCategory == ModificationCategory.Readonly)
        {
            if (hasModifyingToken)
            {
                ReportWarning(
                    SemanticWarningCode.UnnecessaryModificationToken,
                    $"Method '{callee.Name}' is readonly, no ! token needed.",
                    callExpr.Location);
            }

            return;
        }

        // Writable and Migratable methods require ! token
        if (!hasModifyingToken && calleeCategory >= ModificationCategory.Writable)
        {
            ReportError(
                SemanticDiagnosticCode.ModifyingMethodRequiresToken,
                $"Method '{callee.Name}' is {calleeCategory.ToString().ToLower()} and requires ! token.",
                callExpr.Location);
        }
    }

    /// <summary>
    /// Validates token-based access for modification.
    /// Viewed/Inspected tokens can only call readonly methods.
    /// </summary>
    /// <param name="tokenType">The wrapper type (Viewed, Inspected, Hijacked, Seized).</param>
    /// <param name="callee">The routine being called.</param>
    /// <param name="callExpr">The call expression for error location.</param>
    private void ValidateTokenAccess(string tokenType, RoutineInfo callee, Expression callExpr)
    {
        ModificationCategory calleeCategory = callee.ModificationCategory;

        switch (tokenType)
        {
            case "Viewed":
            case "Inspected":
                // Read-only tokens can only call readonly methods
                if (calleeCategory != ModificationCategory.Readonly)
                {
                    ReportError(
                        SemanticDiagnosticCode.ModifyingMethodThroughReadOnlyToken,
                        $"Cannot call {calleeCategory.ToString().ToLower()} method '{callee.Name}' through {tokenType} token.",
                        callExpr.Location);
                }

                break;

            case "Hijacked":
            case "Seized":
                // Write tokens can call readonly or writable
                // TODO: Migratable is possible unless iterating.
                if (calleeCategory == ModificationCategory.Migratable)
                {
                    ReportError(
                        SemanticDiagnosticCode.MigratableMethodThroughExclusiveToken,
                        $"Cannot call migratable method '{callee.Name}' through {tokenType} token.",
                        callExpr.Location);
                }

                break;

                // Owned access (no token wrapper) can call any method
        }
    }

    #endregion
}
