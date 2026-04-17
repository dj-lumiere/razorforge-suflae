using Compiler.Diagnostics;
using SemanticVerification.Results;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

public class ErrorHandlingPatternTests
{
    [Fact]
    public void Analyze_ResultIsNone_ReportsPatternMismatch()
    {
        string source = """
                        routine test(value: Result[S32])
                          when value
                            is None => pass
                            else => pass
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PatternTypeMismatch);
    }

    [Fact]
    public void Analyze_LookupUsesBlankAbsentArm_NoPatternMismatch()
    {
        string source = """
                        routine test(value: Lookup[S32])
                          when value
                            is Blank => pass
                            is Crashable err => pass
                            else v => pass
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PatternTypeMismatch);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }

    [Fact]
    public void Analyze_ResultBlankUsesBlankValueArm_NoPatternMismatch()
    {
        string source = """
                        routine test(value: Result[Blank])
                          when value
                            is Crashable err => pass
                            is Blank => pass
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PatternTypeMismatch);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }
}
