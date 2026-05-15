namespace TypeModel.Reprs;

/// <summary>
/// Distinguishes pointer-like backend representations that all erase to LLVM <c>ptr</c>.
/// </summary>
public enum PointerFlavor
{
    /// <summary>Not a pointer (non-pointer scalar).</summary>
    None,
    /// <summary>Heap entity pointer.</summary>
    Entity,
    /// <summary>Protocol witness/vtable pointer.</summary>
    Protocol,
    /// <summary>Function pointer.</summary>
    Routine,
    /// <summary>Viewing borrow (Viewed[T]).</summary>
    Viewed,
    /// <summary>Grasped borrow (Grasped[T]).</summary>
    Grasped,
    /// <summary>Inspected borrow (read-only, internal use).</summary>
    Inspected,
    /// <summary>Claimed borrow (exclusive, internal use).</summary>
    Claimed,
    /// <summary>Reference-counted shared pointer (Retained[T]).</summary>
    Retained,
    /// <summary>Tracked ownership pointer (Tracked[T]).</summary>
    Tracked,
    /// <summary>Shared ownership pointer (deferred concurrency use).</summary>
    Shared,
    /// <summary>Marked wrapper pointer (marker-protocol conformance).</summary>
    Marked,
    /// <summary>Unsafe hijack borrow (Hijacked[T]).</summary>
    Hijacked,
    /// <summary>Unique ownership pointer (Owned[T]).</summary>
    Owned,
    /// <summary>Untyped raw pointer (CPtr / Address).</summary>
    Raw
}
