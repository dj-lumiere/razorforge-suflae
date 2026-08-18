namespace Verification.Enums;

/// <summary>
/// Mutation category for memberRoutines, inferred by the builder.
/// Determines what token types can call a memberRoutine.
/// </summary>
/// <remarks>
/// The builder automatically infers mutation categories using three-phase analysis:
///
/// Step 1 (Direct Analysis):
///   - If memberRoutine writes to any member variable of me -> Writable
///   - If memberRoutine calls .grasp() on me member variables -> Writable
///
/// Step 2 (Call Graph Propagation):
///   - If memberRoutine calls a Writable memberRoutine on me -> Writable
///   - If memberRoutine calls a Migratable memberRoutine on me -> Migratable
///   - Repeat until fixpoint (no changes)
///
/// Step 3 (Token Checking):
///   - Viewing/Inspecting tokens can only call Readonly memberRoutines
///   - Modifying/Claiming tokens can call Readonly or Writable memberRoutines
///   - Only owned/non-token access can call Migratable memberRoutines
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
