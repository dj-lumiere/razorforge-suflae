using Builder.Diagnostics;
using Builder.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for RF-S639: while an access token is in use (its `using` block, or the one call it is passed
/// to inline), the object it was taken from must stay put. Reassigning or stealing the token's source
/// there would leave the token pointing at freed memory, so it is rejected. Other bindings, and the
/// source itself once the token is done, stay free.
/// </summary>
public class TokenSourceFreezeTests
{
    private const string Prelude = """
                                   import IO/Console

                                   entity Box
                                     n: S64

                                   routine Box.bump()
                                     me.n = me.n + 1
                                     return

                                   routine take(m: Modifying[Box], b: Box)
                                     m.bump()
                                     return

                                   """;

    [Fact]
    public void Analyze_ReassignSourceInsideUsing_Errors()
    {
        string source = Prelude + """
                                  routine start()
                                    var a = Box(n: 1)
                                    using a.modify() as m
                                      a = Box(n: 50)
                                      m.bump()
                                    return
                                  """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring: "You are trying to reassign 'a'");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TokenSourceReplaced);
    }

    [Fact]
    public void Analyze_StealSourceInsideUsing_Errors()
    {
        string source = Prelude + """
                                  routine start()
                                    var a = Box(n: 1)
                                    using a.view() as v
                                      var b = steal a
                                      show(f"{v.n}")
                                    return
                                  """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring: "You are trying to steal 'a'");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TokenSourceReplaced);
    }

    [Fact]
    public void Analyze_StealSourceInSameCallAsInlineToken_Errors()
    {
        string source = Prelude + """
                                  routine start()
                                    var a = Box(n: 1)
                                    take(m: a.modify(), b: steal a)
                                    return
                                  """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TokenSourceReplaced);
    }

    [Fact]
    public void Analyze_ReassignSourceAfterUsing_Ok()
    {
        string source = Prelude + """
                                  routine start()
                                    var a = Box(n: 1)
                                    using a.modify() as m
                                      m.bump()
                                    a = Box(n: 50)
                                    show(f"{a.n}")
                                    return
                                  """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TokenSourceReplaced);
    }

    [Fact]
    public void Analyze_ReassignOtherBindingInsideUsing_Ok()
    {
        string source = Prelude + """
                                  routine start()
                                    var a = Box(n: 1)
                                    var b = Box(n: 2)
                                    using a.modify() as m
                                      b = Box(n: 9)
                                      m.bump()
                                    show(f"{a.n} {b.n}")
                                    return
                                  """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TokenSourceReplaced);
    }

    [Fact]
    public void Analyze_InlineTokenCallWithoutSteal_Ok()
    {
        string source = Prelude + """
                                  routine start()
                                    var a = Box(n: 1)
                                    a.modify().bump()
                                    a = Box(n: 3)
                                    show(f"{a.n}")
                                    return
                                  """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TokenSourceReplaced);
    }
}
