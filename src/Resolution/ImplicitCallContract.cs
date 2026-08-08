using System.Collections.Generic;
using TypeModel.Types;
using TypeInfo = TypeModel.Types.TypeInfo;

namespace Compiler.Resolution;

/// <summary>
/// Single source of truth for the routines codegen inserts IMPLICITLY — with no surface AST call
/// node — for a value of a given live type. <c>RoutineReachabilityPass</c> consumes this to seed
/// those routines so monomorphization and codegen emit their bodies; the matching codegen insertion
/// sites (<c>LLVMCodeGenerator</c>'s promote / lock_enter / lock_exit / raw_inner, and the RC-wrapper
/// copy verb) are bound to the same <see cref="RuntimeContract"/> constants.
/// <para>
/// Reachability runs BEFORE generic monomorphization, so it walks generic-def bodies and cannot see
/// the concrete calls codegen will later insert. Historically each implicit insertion was mirrored
/// by a hand-written seed block in reachability; when the mirror drifted, a routine was declared and
/// called but never defined — surfacing as the "declared+called but never defined" over-prune crash.
/// Centralising the "what codegen will insert" list here keeps the two sides from diverging.
/// </para>
/// </summary>
internal static class ImplicitCallContract
{
    /// <summary>
    /// The <c>(owner, methodName)</c> pairs codegen implicitly inserts for a value of
    /// <paramref name="liveType"/>. The caller resolves each against the registry and seeds it.
    /// Only genuine no-AST-node insertions belong here — routines reached through a real (even
    /// synthesized) AST call are walked normally and must NOT be listed.
    /// </summary>
    public static IEnumerable<(TypeInfo owner, string methodName)> ForLiveType(TypeInfo liveType)
    {
        // Structured base-name classification (canonical helper — prefers the generic definition's
        // BareName, no ad-hoc bracket parsing). Returns null for anything that isn't an RC wrapper.
        string? ownerBase = TypeRegistry.GetRcWrapperBaseName(type: liveType);

        // RC wrappers: codegen inserts the copy verb on every var binding of this wrapper (and on
        // PLP-synthesized else-pattern bindings that appear after reachability runs).
        string? copyVerb = ownerBase switch
        {
            RuntimeContract.Retained => RuntimeContract.RefCount.Retain,
            RuntimeContract.Tracked => RuntimeContract.RefCount.Track,
            RuntimeContract.Roamed => RuntimeContract.RefCount.Roam,
            _ => null
        };
        if (copyVerb != null)
            yield return (liveType, copyVerb);

        if (ownerBase != RuntimeContract.Roamed)
            yield break;

        // Roamed[T]: promote at spawn boundaries, lock_enter/lock_exit around direct field access,
        // raw_inner for the display-transparency projection — all on the Roamed handle itself.
        yield return (liveType, RuntimeContract.RoamedMethod.Promote);
        yield return (liveType, RuntimeContract.RoamedMethod.LockEnter);
        yield return (liveType, RuntimeContract.RoamedMethod.LockExit);
        yield return (liveType, RuntimeContract.RoamedMethod.RawInner);

        // Display transparency: codegen re-resolves represent/diagnose on the Roamed handle to the
        // INNER value's, so the inner display routines must be live even when the inner type is only
        // ever reached through the wrapper (e.g. an SF entity local that is never held bare).
        TypeInfo? inner = liveType switch
        {
            RecordTypeInfo { TypeArguments: { Count: >= 1 } ta } => ta[index: 0],
            WrapperTypeInfo w => w.InnerType,
            _ => null
        };
        if (inner != null)
        {
            yield return (inner, RuntimeContract.Display.Represent);
            yield return (inner, RuntimeContract.Display.Diagnose);
        }
    }
}
