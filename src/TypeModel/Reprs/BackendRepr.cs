using TypeModel.Types;

namespace TypeModel.Reprs;

/// <summary>
/// Concrete backend representation metadata attached before codegen entry.
/// Separates semantic type identity from ABI/storage identity.
/// </summary>
/// <param name="Kind">The K in d.</param>
/// <param name="SourceType">The S ou rc eT yp e.</param>
/// <param name="LlvmAbiType">The L lv mA bi Ty pe.</param>
/// <param name="PointerFlavor">The P oi nt er Fl av or.</param>
/// <param name="PointeeType">The P oi nt ee Ty pe.</param>
/// <param name="AggregateLayoutKey">The A gg re ga te La yo ut Ke y.</param>
/// <param name="IsPassedIndirectly">The I sP as se dI nd ir ec tl y.</param>
/// <param name="IsTransparent">The I sT ra ns pa re nt.</param>
public sealed record BackendRepr(
    BackendReprKind Kind,
    TypeInfo SourceType,
    string LlvmAbiType,
    PointerFlavor PointerFlavor = PointerFlavor.None,
    TypeInfo? PointeeType = null,
    string? AggregateLayoutKey = null,
    bool IsTransparent = false,
    bool IsPassedIndirectly = false)
{
    /// <summary>
    /// Gets I sP oi nt er Li ke.
    /// </summary>
    public bool IsPointerLike =>
        Kind is BackendReprKind.EntityRef or BackendReprKind.ProtocolRef or
            BackendReprKind.WrapperRef or BackendReprKind.RoutineRef or BackendReprKind.RawPtr;
}
