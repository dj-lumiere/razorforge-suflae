using Compiler.Diagnostics;
using Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// `$iter` is dunder-private to the iterator protocol — only the for-loop lowering
/// (ControlFlowLoweringPass) may emit `xs.$iter()`. User code must use `for ... in ...`
/// or iterable combinators (skip, take, map, ...). Defining `$iter` is allowed; calling
/// it from user code is a compile error to prevent iterator-invalidation footguns.
/// </summary>
public class IterPrivacyTests
{
    [Fact]
    public void UserCode_CallsDollarIter_OnList_Rejected()
    {
        string source = """
                        routine start()
                          var xs = [1_s64, 2_s64, 3_s64]
                          var it = xs.$iter()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DirectWiredRoutineCall);
    }

    [Fact]
    public void ForLoop_OverList_Resolves()
    {
        string source = """
                        routine start()
                          var xs = [1_s64, 2_s64, 3_s64]
                          for x in xs
                            return
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void UserCode_DefinesDollarIter_OnRecord_Allowed()
    {
        // Declaring a `$iter` method on a user type is fine — the prohibition is on
        // *calling* `$iter` from user code, not on implementing the protocol.
        string source = """
                        record Bag
                          n: S64

                        routine Bag.$iter() -> Bag
                          return me
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DirectWiredRoutineCall);
    }
}
