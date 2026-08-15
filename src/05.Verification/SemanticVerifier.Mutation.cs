using TypeModel.Symbols;

namespace Verification;

/// <summary>
/// Phase 4: Mutation inference for RazorForge.
/// Implements the three-phase algorithm from the wiki:
///
/// Phase 1: Direct analysis - detect me.memberVar = value patterns (done during body analysis)
/// Phase 2: Call graph propagation - if A calls mutating B on me, A is mutating
/// Phase 3: Token verification - verify mutating methods called with ! token (enforced at call sites)
/// </summary>
public sealed partial class SemanticVerifier
{
    #region Phase 4: Mutation Inference

    #endregion
}
