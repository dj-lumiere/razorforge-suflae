using Compiler.Diagnostics;
using Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for the readers-XOR-writer borrow checker (RF-S630). Within overlapping (nested) `using`
/// scopes on the SAME Shared handle, a `claim()` (writer) conflicts with any other hold; `inspect()`
/// readers may coexist with other readers. Non-overlapping (sequential) scopes and distinct handles
/// never conflict. Detection is name-based on the Shared handle (v1).
/// </summary>
public class RxwBorrowCheckerTests
{
    private const string Prelude = """
                                   import IO/Console
                                   import BuilderService

                                   entity Counter
                                     value: S64

                                   """;

    [Fact]
    public void Analyze_NestedClaimClaim_SameHandle_Errors()
    {
        string source = Prelude + """
                                  routine start()
                                    var s = Counter(value: 1).share[MultiRead]()
                                    using s.claim() as c1
                                      using s.claim() as c2
                                        show("nested")
                                    return
                                  """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring: "conflicts with an active 'claim()'");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ReadersXorWriter);
    }

    [Fact]
    public void Analyze_NestedClaimInspect_SameHandle_Errors()
    {
        string source = Prelude + """
                                  routine start()
                                    var s = Counter(value: 1).share[MultiRead]()
                                    using s.claim() as c
                                      using s.inspect() as v
                                        show("nested")
                                    return
                                  """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring: "conflicts with an active 'claim()'");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ReadersXorWriter);
    }

    [Fact]
    public void Analyze_NestedInspectClaim_SameHandle_Errors()
    {
        string source = Prelude + """
                                  routine start()
                                    var s = Counter(value: 1).share[MultiRead]()
                                    using s.inspect() as v
                                      using s.claim() as c
                                        show("nested")
                                    return
                                  """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring: "conflicts with an active 'inspect()'");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ReadersXorWriter);
    }

    [Fact]
    public void Analyze_NestedInspectInspect_SameHandle_Ok()
    {
        // Readers coexist — multiple inspect holds on the same handle are allowed.
        string source = Prelude + """
                                  routine start()
                                    var s = Counter(value: 1).share[MultiRead]()
                                    using s.inspect() as v1
                                      using s.inspect() as v2
                                        show(f"{v1.value} {v2.value}")
                                    return
                                  """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_NestedClaim_DifferentHandles_Ok()
    {
        // Distinct handles do not conflict (name-based).
        string source = Prelude + """
                                  routine start()
                                    var s1 = Counter(value: 1).share[MultiRead]()
                                    var s2 = Counter(value: 2).share[MultiRead]()
                                    using s1.claim() as c1
                                      using s2.claim() as c2
                                        show("two handles")
                                    return
                                  """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_SequentialClaim_SameHandle_Ok()
    {
        // Non-overlapping scopes — the first claim is released before the second opens.
        string source = Prelude + """
                                  routine Counter.bump(inc: S64)
                                    me.value = me.value + inc
                                    return

                                  routine start()
                                    var s = Counter(value: 1).share[MultiRead]()
                                    using s.claim() as c1
                                      c1.bump(inc: 1)
                                    using s.claim() as c2
                                      c2.bump(inc: 1)
                                    return
                                  """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
}