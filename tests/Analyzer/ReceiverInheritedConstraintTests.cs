using Compiler.Diagnostics;
using Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for the generalized <c>needs T in [Types]</c> (type-equality) constraint when the
/// constrained parameter is INHERITED FROM THE RECEIVER rather than supplied as an explicit type
/// argument at the call site — e.g. <c>Box[T].only_ab() needs T in [Alpha, Beta]</c> called on a
/// <c>Box[Gamma]</c>, or the stdlib <c>Shared[T, P].claim() needs P in [Exclusive, MultiRead]</c>.
///
/// Such constraints used to be silently dropped during method resolution (the resolved instance
/// method kept only constraints on its OWN generic params, discarding owner-param constraints), so
/// they went unchecked. The fix preserves owner-param TypeEquality constraints through resolution
/// and validates them at the call site against the receiver's bound argument (RF-S160).
/// </summary>
public class ReceiverInheritedConstraintTests
{
    #region Generalized `needs T in [Types]`

    private const string GeneralPrelude = """
                                          record Alpha
                                            pass
                                          record Beta
                                            pass
                                          record Gamma
                                            pass

                                          record Box[T]
                                            pass

                                          routine Box[T].only_ab()
                                          needs T in [Alpha, Beta]
                                            return

                                          """;

    [Fact]
    public void Analyze_NeedsTInTypes_ReceiverArgInSet_Ok()
    {
        string source = GeneralPrelude + """
                                         routine use_alpha(b: Box[Alpha])
                                           b.only_ab()
                                           return
                                         """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_NeedsTInTypes_ReceiverArgNotInSet_Errors()
    {
        string source = GeneralPrelude + """
                                         routine use_gamma(b: Box[Gamma])
                                           b.only_ab()
                                           return
                                         """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring: "'Gamma' is not in [Alpha, Beta]");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TypeEqualityConstraintViolation);
    }

    #endregion

    #region Stdlib lock-policy instantiation (Shared[T, P].inspect/claim)

    private const string LockPrelude = """
                                       import IO/Console
                                       import BuilderService

                                       entity Counter
                                         value: S64

                                       """;

    [Fact]
    public void Analyze_ClaimOnReadOnly_Errors()
    {
        string source = LockPrelude + """
                                      routine start()
                                        var s = Counter(value: 1).share[ReadOnly]()
                                        using s.claim() as c
                                          show("nope")
                                        return
                                      """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring: "'ReadOnly' is not in [Exclusive, MultiRead]");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TypeEqualityConstraintViolation);
    }

    [Fact]
    public void Analyze_InspectOnExclusive_Errors()
    {
        string source = LockPrelude + """
                                      routine start()
                                        var s = Counter(value: 1).share[Exclusive]()
                                        using s.inspect() as v
                                          show("nope")
                                        return
                                      """;

        AssertHasErrorSa(source: source,
            expectedErrorSubstring: "'Exclusive' is not in [MultiRead, ReadOnly]");
    }

    [Fact]
    public void Analyze_MultiRead_InspectAndClaim_Ok()
    {
        string source = LockPrelude + """
                                      routine Counter.bump(inc: S64)
                                        me.value = me.value + inc
                                        return

                                      routine start()
                                        var s = Counter(value: 10).share[MultiRead]()
                                        using s.inspect() as v
                                          show(f"value: {v.value}")
                                        using s.claim() as c
                                          c.bump(inc: 5)
                                        return
                                      """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_ClaimOnExclusive_Ok()
    {
        string source = LockPrelude + """
                                      routine Counter.bump(inc: S64)
                                        me.value = me.value + inc
                                        return

                                      routine start()
                                        var s = Counter(value: 1).share[Exclusive]()
                                        using s.claim() as c
                                          c.bump(inc: 1)
                                        return
                                      """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_InspectOnReadOnly_Ok()
    {
        string source = LockPrelude + """
                                      routine start()
                                        var s = Counter(value: 1).share[ReadOnly]()
                                        using s.inspect() as v
                                          show(f"{v.value}")
                                        return
                                      """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion
}