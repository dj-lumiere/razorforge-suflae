namespace SemanticVerification.Enums;

/// <summary>
/// Dispatch strategy for call sites with protocol-constrained varargs.
/// Determined by the semantic analyzer based on argument types at each call site.
/// </summary>
public enum DispatchStrategy
{
    /// <summary>
    /// All varargs arguments resolve to the same concrete type.
    /// Monomorphized at build time — zero-cost.
    /// </summary>
    Buildtime,

    /// <summary>
    /// Varargs arguments have mixed concrete types.
    /// Requires auto-boxing to Data and dict-based dispatch at runtime.
    /// </summary>
    Runtime
}
