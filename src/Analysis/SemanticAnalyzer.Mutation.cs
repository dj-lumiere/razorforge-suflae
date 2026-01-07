namespace Compilers.Analysis;

using Compilers.Analysis.Enums;
using Compilers.Analysis.Inference;
using Compilers.Analysis.Symbols;
using Compilers.Analysis.Types;
using Compilers.Shared.AST;
using TypeSymbol = Compilers.Analysis.Types.TypeInfo;

/// <summary>
/// Phase 4: Mutation inference for RazorForge.
/// Implements the three-phase algorithm from the wiki:
///
/// Phase 1: Direct analysis - detect me.field = value patterns (done during body analysis)
/// Phase 2: Call graph propagation - if A calls mutating B on me, A is mutating
/// Phase 3: Token verification - verify mutating methods called with ! token (enforced at call sites)
/// </summary>
public sealed partial class SemanticAnalyzer
{
    #region Phase 4: Mutation Inference

    /// <summary>
    /// Infers mutation categories for all routines using call graph analysis.
    /// Called after Phase 3 body analysis is complete.
    /// </summary>
    private void InferMutationCategories()
    {
        // Create the mutation inference engine and run propagation
        _mutationInference = new MutationInference(callGraph: _callGraph, registry: _registry);
        _mutationInference.InferAll();

        // Apply inferred categories back to RoutineInfo
        foreach (CallGraphNode node in _callGraph.AllNodes)
        {
            node.Routine.MutationCategory = node.InferredMutation;
        }
    }

    /// <summary>
    /// Tracks a call for call graph building.
    /// Called during expression analysis when a method call is encountered.
    /// </summary>
    /// <param name="callee">The routine being called.</param>
    /// <param name="callOnMe">Whether the call is on 'me' (affects mutation propagation).</param>
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
    /// Analyzes a statement for direct mutations to 'me' fields.
    /// Called during Phase 3 body analysis.
    /// </summary>
    /// <param name="statement">The statement to analyze for mutations.</param>
    private void AnalyzeStatementForMutations(Statement statement)
    {
        if (_currentCallGraphNode == null)
        {
            return;
        }

        _mutationInference?.AnalyzeStatementForMutation(
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
    /// Validates that a mutating call uses the correct token.
    /// Called during method call analysis to enforce mutability rules.
    /// </summary>
    /// <param name="callee">The routine being called.</param>
    /// <param name="callExpr">The call expression.</param>
    /// <param name="hasMutatingToken">Whether the ! token was used.</param>
    private void ValidateMutatingCall(RoutineInfo callee, Expression callExpr, bool hasMutatingToken)
    {
        MutationCategory calleeCategory = callee.MutationCategory;

        // Readonly methods don't need special tokens
        if (calleeCategory == MutationCategory.Readonly)
        {
            if (hasMutatingToken)
            {
                ReportWarning(
                    message: $"Method '{callee.Name}' is readonly, no ! token needed.",
                    location: callExpr.Location);
            }

            return;
        }

        // Writable and Migratable methods require ! token
        if (!hasMutatingToken && calleeCategory >= MutationCategory.Writable)
        {
            ReportError(
                message: $"Method '{callee.Name}' is {calleeCategory.ToString().ToLower()} and requires ! token.",
                location: callExpr.Location);
        }
    }

    /// <summary>
    /// Validates token-based access for mutation.
    /// Viewed/Inspected tokens can only call readonly methods.
    /// </summary>
    /// <param name="tokenType">The wrapper type (Viewed, Inspected, Hijacked, Seized).</param>
    /// <param name="callee">The routine being called.</param>
    /// <param name="callExpr">The call expression for error location.</param>
    private void ValidateTokenAccess(string tokenType, RoutineInfo callee, Expression callExpr)
    {
        MutationCategory calleeCategory = callee.MutationCategory;

        switch (tokenType)
        {
            case "Viewed":
            case "Inspected":
                // Read-only tokens can only call readonly methods
                if (calleeCategory != MutationCategory.Readonly)
                {
                    ReportError(
                        message: $"Cannot call {calleeCategory.ToString().ToLower()} method '{callee.Name}' through {tokenType} token.",
                        location: callExpr.Location);
                }

                break;

            case "Hijacked":
            case "Seized":
                // Write tokens can call readonly or writable
                // TODO: Migratable is possible unless iterating.
                if (calleeCategory == MutationCategory.Migratable)
                {
                    ReportError(
                        message: $"Cannot call migratable method '{callee.Name}' through {tokenType} token.",
                        location: callExpr.Location);
                }

                break;

                // Owned access (no token wrapper) can call any method
        }
    }

    #endregion
}
