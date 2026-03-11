namespace SemanticAnalysis.Enums;

/// <summary>
/// Modification category for methods, inferred by the builder.
/// Determines what token types can call a method.
/// </summary>
/// <remarks>
/// The builder automatically infers modification categories using three-phase analysis:
///
/// Phase 1 (Direct Analysis):
///   - If method writes to any member variable of me → Writable
///   - If method calls .hijack() on me member variables → Writable
///
/// Phase 2 (Call Graph Propagation):
///   - If method calls a Writable method on me → Writable
///   - If method calls a Migratable method on me → Migratable
///   - Repeat until fixpoint (no changes)
///
/// Phase 3 (Token Checking):
///   - Viewed/Inspected tokens can only call Readonly methods
///   - Hijacked/Seized tokens can call Readonly or Writable methods
///   - Only owned/non-token access can call Migratable methods
/// </remarks>
public enum ModificationCategory
{
    /// <summary>
    /// Read-only access, doesn't modify me.
    /// Works with all token types: Viewed, Hijacked, Inspected, Seized.
    /// </summary>
    Readonly,

    /// <summary>
    /// Modifies in-place within existing memory allocation.
    /// Requires modifiable token: Hijacked or Seized.
    /// Cannot be called through Viewed or Inspected tokens.
    /// </summary>
    Writable,

    /// <summary>
    /// Can relocate memory buffers (e.g., List.push causing reallocation).
    /// Banned during iteration to prevent iterator invalidation.
    /// Requires ownership or exclusive access outside iteration.
    /// This is the default/most permissive category.
    /// </summary>
    Migratable
}
