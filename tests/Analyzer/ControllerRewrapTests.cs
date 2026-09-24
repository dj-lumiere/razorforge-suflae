using Builder.Diagnostics;
using Builder.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// RF-S640: putting an existing value's buffer refcount (<c>r.ctrl</c>, a <c>Hijacked[FrozenController]</c>)
/// into a new value needs a <c>hold()</c> on it in the same routine, the way every shared-buffer
/// <c>assign()</c> (Text, Bytes, Integer, Real) does. Without it both values release one count and the
/// buffer is freed twice.
/// </summary>
public class ControllerRewrapTests
{
    [Fact]
    public void Analyze_ControllerRewrapWithoutHold_Errors()
    {
        const string source = """
                              import BuilderQuery

                              record Blob obeys Assignable, Copyable
                                secret count: U64
                                secret ctrl: Hijacked[FrozenController]

                              @[readonly, inline]
                              routine Blob.assign() -> Blob
                                danger
                                  if not me.ctrl.is_none()
                                    me.ctrl.as_entity().hold()
                                return Blob(count: me.count, ctrl: me.ctrl)

                              @override
                              dangerous routine Blob.destroy()
                                if me.ctrl.is_none()
                                  return
                                var prev = me.ctrl.as_entity().unhold()
                                if prev <= 1u64
                                  me.ctrl.invalidate()
                                return

                              routine blob_new(count: U64) -> Blob
                                return Blob(count: count, ctrl: fresh_frozen_controller())

                              routine resized_bad(r: Blob, count: U64) -> Blob
                                return Blob(count: count, ctrl: r.ctrl)

                              routine resized_ok(r: Blob, count: U64) -> Blob
                                danger
                                  if not r.ctrl.is_none()
                                    r.ctrl.as_entity().hold()
                                return Blob(count: count, ctrl: r.ctrl)

                              routine start()
                                return
                              """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring: "'r.ctrl', the refcount of a buffer that 'r' still owns");
        Assert.Single(collection: result.Errors,
            predicate: e => e.Code == SemanticDiagnosticCode.ControllerRewrapWithoutHold);
    }

    [Fact]
    public void Analyze_ControllerRewrapAfterHoldAndFreshController_Passes()
    {
        const string source = """
                              import BuilderQuery

                              record Blob obeys Assignable, Copyable
                                secret count: U64
                                secret ctrl: Hijacked[FrozenController]

                              @[readonly, inline]
                              routine Blob.assign() -> Blob
                                danger
                                  if not me.ctrl.is_none()
                                    me.ctrl.as_entity().hold()
                                return Blob(count: me.count, ctrl: me.ctrl)

                              @override
                              dangerous routine Blob.destroy()
                                if me.ctrl.is_none()
                                  return
                                var prev = me.ctrl.as_entity().unhold()
                                if prev <= 1u64
                                  me.ctrl.invalidate()
                                return

                              routine blob_new(count: U64) -> Blob
                                return Blob(count: count, ctrl: fresh_frozen_controller())

                              routine resized_ok(r: Blob, count: U64) -> Blob
                                danger
                                  if not r.ctrl.is_none()
                                    r.ctrl.as_entity().hold()
                                return Blob(count: count, ctrl: r.ctrl)

                              routine start()
                                return
                              """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
}
