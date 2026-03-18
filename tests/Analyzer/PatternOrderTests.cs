using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for pattern validation rules:
/// #88: Pattern order enforcement
/// #130/#148: Duplicate pattern detection
/// </summary>
public class PatternOrderTests
{
    #region #88: Pattern order enforcement

    [Fact]
    public void Analyze_WhenElseBeforeOtherPatterns_ReportsError()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE
                        routine test(c: Color) -> S32
                          return when c
                            else => 0_s32
                            is Color.RED => 1_s32
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PatternOrderViolation);
    }

    [Fact]
    public void Analyze_WhenElseLast_NoError()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE
                        routine test(c: Color) -> S32
                          return when c
                            is Color.RED => 1_s32
                            is Color.GREEN => 2_s32
                            else => 0_s32
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PatternOrderViolation);
    }

    #endregion

    #region #130/#148: Duplicate pattern detection

    [Fact]
    public void Analyze_DuplicateLiteralPattern_ReportsError()
    {
        string source = """
                        routine test(x: S32) -> S32
                          return when x
                            42 => 1_s32
                            42 => 2_s32
                            else => 0_s32
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DuplicatePattern);
    }

    [Fact]
    public void Analyze_DuplicateChoiceCasePattern_ReportsError()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE
                        routine test(c: Color) -> S32
                          return when c
                            is Color.RED => 1_s32
                            is Color.RED => 2_s32
                            else => 0_s32
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DuplicatePattern);
    }

    [Fact]
    public void Analyze_DistinctPatterns_NoError()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE
                        routine test(c: Color) -> S32
                          return when c
                            is Color.RED => 1_s32
                            is Color.GREEN => 2_s32
                            is Color.BLUE => 3_s32
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DuplicatePattern);
    }

    #endregion
}
