using TypeModel.Symbols;
using TypeModel.Types;
using TypeInfo = TypeModel.Types.TypeInfo;

namespace Compiler.Resolution;

/// <summary>
/// The single decision point for Suflae <c>Roamed[T]</c> display/receiver transparency at codegen.
/// A Suflae local promoted to <c>Roamed[E]</c> (post-SA) reaches its inner value's methods through
/// the wrapper handle; codegen must (a) re-resolve a wrapper-shadowed <c>represent</c>/<c>diagnose</c>
/// to the inner value's, and (b) project the <c>RoamController</c> handle to the bare inner pointer
/// via <c>raw_inner()</c> for a bare-<c>me</c> inner method (a Roamed-<c>me</c> inner method — an SF
/// entity's own — takes the handle directly). Both the codegen member-call path and
/// <c>OperatorLoweringPass</c> route their Roamed receivers through here so the rule lives in one
/// place. Genuine Roamed-own methods (<c>roam</c>/<c>promote</c>/<c>lock_enter</c>/<c>raw_inner</c>/
/// <c>destroy</c>) are deliberately left alone — their name never resolves onto the inner.
/// </summary>
internal static class RoamedTransparency
{
    /// <summary>The outcome of routing a call on a <c>Roamed[T]</c> receiver: the method to actually
    /// call, whether the receiver must be projected to the bare inner pointer, and the inner type
    /// (the projected receiver type).</summary>
    public readonly record struct Projection(RoutineInfo Method, bool ProjectToInner, TypeInfo InnerType);

    /// <summary>
    /// Decide the transparency projection for calling <paramref name="method"/> (named
    /// <paramref name="memberName"/>) on a value of <paramref name="receiverType"/>. Returns
    /// <c>null</c> when the receiver is not <c>Roamed[T]</c>, or when the call targets a genuine
    /// Roamed-own method (no transparency applies — call it as-is).
    /// </summary>
    public static Projection? Project(TypeInfo receiverType, RoutineInfo? method, string memberName,
        TypeRegistry registry)
    {
        if (TypeRegistry.GetRcWrapperBaseName(type: receiverType) != RuntimeContract.Roamed)
            return null;

        TypeInfo? inner = receiverType switch
        {
            RecordTypeInfo { TypeArguments: { Count: >= 1 } ta } => ta[index: 0],
            WrapperTypeInfo w => w.InnerType,
            _ => null
        };
        if (inner == null) return null;

        RoutineInfo? effective = method;

        // Display transparency: a `represent`/`diagnose` still bound to the WRAPPER (its own
        // hand-written/auto-derived one shadows the inner) re-resolves to the inner value's, so
        // `f"{d}"` / `d.diagnose()` render the contents, not the wrapper type.
        if (method is { Name: RuntimeContract.Display.Represent or RuntimeContract.Display.Diagnose }
            && method.OwnerType?.FullName != inner.FullName
            && registry.LookupMethod(type: inner, methodName: memberName)
                is { OwnerType: { } innerDisplayOwner } innerDisplay
            && innerDisplayOwner.FullName == inner.FullName)
        {
            effective = innerDisplay;
        }

        // Only an inner-owned method is transparent. A bare-`me` inner method needs the handle
        // projected to the real inner pointer; a Roamed-`me` inner method (an SF entity's own) takes
        // the handle directly.
        if (effective?.OwnerType?.FullName != inner.FullName)
            return null;

        bool projectToInner = effective.MeType is not RecordTypeInfo
            { GenericDefinition.Name: RuntimeContract.Roamed };
        return new Projection(Method: effective, ProjectToInner: projectToInner, InnerType: inner);
    }
}
