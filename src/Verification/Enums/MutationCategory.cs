namespace Verification.Enums;

/// <summary>
/// Mutation category for methods, inferred by the builder.
/// Determines what token types can call a method.
/// </summary>
/// <remarks>
/// The builder automatically infers mutation categories using three-phase analysis:
///
/// Phase 1 (Direct Analysis):
///   - If method writes to any member variable of me -> Writable
///   - If method calls .grasp() on me member variables -> Writable
///
/// Phase 2 (Call Graph Propagation):
///   - If method calls a Writable method on me -> Writable
///   - If method calls a Migratable method on me -> Migratable
///   - Repeat until fixpoint (no changes)
///
/// Phase 3 (Token Checking):
///   - Viewing/Inspecting tokens can only call Readonly methods
///   - Modifying/Claiming tokens can call Readonly or Writable methods
///   - Only owned/non-token access can call Migratable methods
/// </remarks>
public enum MutationCategory
{
    /// <summary>
    /// Read-only access, doesn't mutate me.
    /// Works with all token types: Viewing, Modifying, Inspecting, Claiming.
    /// </summary>
    Readonly,

    /// <summary>
    /// Mutates in-place within existing memory allocation.
    /// Needs modifiable token: Modifying or Claiming.
    /// Cannot be called through Viewing or Inspecting tokens.
    /// </summary>
    Writable,

    /// <summary>
    /// Can relocate memory buffers (e.g., List.push causing reallocation).
    /// Banned during iteration to prevent iterator invalidation.
    /// Needs ownership or exclusive access outside iteration.
    /// This is the default/most permissive category.
    /// </summary>
    Migratable
}
