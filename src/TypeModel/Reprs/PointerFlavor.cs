namespace TypeModel.Reprs;

/// <summary>
/// Distinguishes pointer-like backend representations that all erase to LLVM <c>ptr</c>.
/// </summary>
public enum PointerFlavor
{
    /// <summary>
    /// Represents N on e.
    /// </summary>
    None,
    /// <summary>
    /// Represents E nt it y.
    /// </summary>
    Entity,
    /// <summary>
    /// Represents P ro to co l.
    /// </summary>
    Protocol,
    /// <summary>
    /// Represents R ou ti ne.
    /// </summary>
    Routine,
    /// <summary>
    /// Represents V ie we d.
    /// </summary>
    Viewed,
    /// <summary>
    /// Represents G ra sp ed.
    /// </summary>
    Grasped,
    /// <summary>
    /// Represents I ns pe ct ed.
    /// </summary>
    Inspected,
    /// <summary>
    /// Represents C la im ed.
    /// </summary>
    Claimed,
    /// <summary>
    /// Represents R et ai ne d.
    /// </summary>
    Retained,
    /// <summary>
    /// Represents T ra ck ed.
    /// </summary>
    Tracked,
    /// <summary>
    /// Represents S ha re d.
    /// </summary>
    Shared,
    /// <summary>
    /// Represents M ar ke d.
    /// </summary>
    Marked,
    /// <summary>
    /// Represents H ij ac ke d.
    /// </summary>
    Hijacked,
    /// <summary>
    /// Represents O wn ed.
    /// </summary>
    Owned,
    /// <summary>
    /// Represents R aw.
    /// </summary>
    Raw
}
