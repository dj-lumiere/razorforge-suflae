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

    #endregion
}
