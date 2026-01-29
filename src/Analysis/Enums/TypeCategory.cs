namespace Compilers.Analysis.Enums;

/// <summary>
/// Categories of types in RazorForge/Suflae.
/// </summary>
public enum TypeCategory
{
    // ═══════════════════════════════════════════════════════════════════════
    // Compiler-Internal Categories (not user-visible)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sentinel type used when type resolution fails.
    /// Never instantiated at runtime - purely for error recovery during analysis.
    /// </summary>
    Error,

    /// <summary>
    /// Unbound generic type parameter (T, U, etc.).
    /// Replaced with concrete type during instantiation.
    /// </summary>
    TypeParameter,

    /// <summary>LLVM intrinsic types (@intrinsic.*) - compiler internal only.</summary>
    Intrinsic,

    // ═══════════════════════════════════════════════════════════════════════
    // User-Defined Type Categories
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Value type with copy semantics (RazorForge: record, Suflae: class).</summary>
    Record,

    /// <summary>Reference type, heap-allocated, single owner (RazorForge: entity).</summary>
    Entity,

    /// <summary>Reference type, fixed-size, persistent memory (RazorForge only: resident).</summary>
    Resident,

    /// <summary>Simple enumeration with optional integer values, CAN have methods.</summary>
    Choice,

    /// <summary>Tagged union, local-only, immutable, NO methods.</summary>
    Variant,

    /// <summary>Untagged union, danger zone only, mutable, NO methods.</summary>
    Mutant,

    /// <summary>Interface/trait definition (protocol).</summary>
    Protocol,

    /// <summary>Compiler-generated error handling types (Maybe, Result, Lookup).</summary>
    ErrorHandling,

    /// <summary>First-class function type for lambdas and function references (e.g., Routine&lt;s32, s32, s32&gt; = two s32 params, returns s32).</summary>
    Routine,

    /// <summary>Compiler-generated tuple types (ValueTuple for value types, Tuple for reference types).</summary>
    Tuple,

    /// <summary>Compiler-synthesized wrapper types (Hijacked, Inspected, Seized, Viewed, Shared, Tracked, Snatched).</summary>
    Wrapper
}
