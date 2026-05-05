using TypeModel.Symbols;

namespace Verification;

/// <summary>
/// Phase 4: Modification inference for RazorForge.
/// Implements the three-phase algorithm from the wiki:
///
/// Phase 1: Direct analysis - detect me.memberVar = value patterns (done during body analysis)
/// Phase 2: Call graph propagation - if A calls modifying B on me, A is modifying
/// Phase 3: Token verification - verify modifying methods called with ! token (enforced at call sites)
/// </summary>
public sealed partial class SemanticVerifier
{
    #region Phase 4: Mutation Inference

    /// <summary>
    /// Infers modification categories for all routines using call graph analysis.
    /// Called after Phase 3 body analysis is complete.
    /// </summary>
    private void InferModificationCategories()
    {
        // Create the modification inference engine and run propagation
        _modificationInference =
            new ModificationInference(callGraph: _callGraph, registry: _registry);
        _modificationInference.InferAll();

        // Apply inferred categories back to RoutineInfo
        foreach (CallGraphNode node in _callGraph.AllNodes)
        {
            node.Routine.ModificationCategory = node.InferredModification;
        }
    }

    #endregion
}
