namespace TypeModel.Reprs;

/// <summary>
/// Backend-visible ABI/storage category for a fully resolved semantic type.
/// </summary>
public enum BackendReprKind
{
    /// <summary>
    /// Represents V oi d.
    /// </summary>
    Void,
    /// <summary>
    /// Represents S ca la r.
    /// </summary>
    Scalar,
    /// <summary>
    /// Represents A gg re ga te.
    /// </summary>
    Aggregate,
    /// <summary>
    /// Represents E nt it yR ef.
    /// </summary>
    EntityRef,
    /// <summary>
    /// Represents P ro to co lR ef.
    /// </summary>
    ProtocolRef,
    /// <summary>
    /// Represents W ra pp er Re f.
    /// </summary>
    WrapperRef,
    /// <summary>
    /// Represents R ou ti ne Re f.
    /// </summary>
    RoutineRef,
    /// <summary>
    /// Represents R aw Pt r.
    /// </summary>
    RawPtr
}
