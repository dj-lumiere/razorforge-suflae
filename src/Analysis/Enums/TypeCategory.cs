namespace SemanticAnalysis.Enums;

/// <summary>
/// Categories of types in RazorForge/Suflae.
/// </summary>
public enum TypeCategory
{
    // ═══════════════════════════════════════════════════════════════════════
    // Builder-Internal Categories (not user-visible)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sentinel type used when type resolution fails.
    /// Never created at runtime - purely for error recovery during analysis.
    /// </summary>
    Error,

    /// <summary>
    /// Unbound generic type parameter (T, U, etc.).
    /// Replaced with concrete type during resolution.
    /// </summary>
    TypeParameter,

    /// <summary>
    /// Protocol self type (Me).
    /// Represents the implementing type in protocol signatures.
    /// </summary>
    ProtocolSelf,

    /// <summary>LLVM intrinsic types (@intrinsic.*) - builder internal only.</summary>
    Intrinsic,

    // ═══════════════════════════════════════════════════════════════════════
    // User-Defined Type Categories
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Value type with copy semantics (RazorForge: record, Suflae: class).</summary>
    Record,

    /// <summary>Reference type, heap-allocated, single owner (RazorForge: entity).</summary>
    Entity,

    /// <summary>Simple enumeration with optional integer values, CAN have methods.</summary>
    Choice,

    /// <summary>Bitmask type with named members, builder-generated operators only.</summary>
    Flags,

    /// <summary>Tagged union, local-only, unmodifiable, NO methods.</summary>
    Variant,

    /// <summary>Interface/trait definition (protocol).</summary>
    Protocol,

    /// <summary>Builder-generated error handling types (Maybe, Result, Lookup).</summary>
    ErrorHandling,

    /// <summary>First-class function type for lambdas and function references (e.g., Routine&lt;s32, s32, s32&gt; = two s32 params, returns s32).</summary>
    Routine,

    /// <summary>Builder-generated tuple types (ValueTuple for value types, Tuple for reference types).</summary>
    Tuple,

    /// <summary>Builder-synthesized wrapper types (Hijacked, Inspected, Seized, Viewed, Shared, Tracked, Snatched).</summary>
    Wrapper
}
