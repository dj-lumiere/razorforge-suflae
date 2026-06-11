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
    /// <summary>Viewing borrow (Viewing[T]).</summary>
    Viewing,
    /// <summary>Modifying borrow (Modifying[T]).</summary>
    Modifying,
    /// <summary>Inspecting borrow (read-only, internal use).</summary>
    Inspecting,
    /// <summary>Claiming borrow (exclusive, internal use).</summary>
    Claiming,
    /// <summary>Reference-counted shared pointer (Retained[T]).</summary>
    Retained,
    /// <summary>Tracked ownership pointer (Tracked[T]).</summary>
    Tracked,
    /// <summary>Shared ownership pointer (deferred concurrency use).</summary>
    Shared,
    /// <summary>Watched wrapper pointer (marker-protocol conformance).</summary>
    Watched,
    /// <summary>Unsafe hijack borrow (Hijacked[T]).</summary>
    Hijacked,
    /// <summary>Untyped raw pointer (CPtr / Address).</summary>
    Raw
}
