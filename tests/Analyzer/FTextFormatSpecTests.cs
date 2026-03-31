using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for f-text format specifier validation.
/// Valid specifiers: null, "=", "?", "=?".
/// Invalid: "?=" (wrong order), any other string.
/// </summary>
public class FTextFormatSpecTests
{
    /// <summary>
    /// Tests Analyze_FTextDiagnoseSpec_NoError.
    /// </summary>
    [Fact]
    public void Analyze_FTextDiagnoseSpec_NoError()
    {
        string source = """
                        routine test(x: S64)
                          var msg = f"{x:?}"
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.InvalidFTextFormatSpec);
    }
    /// <summary>
    /// Tests Analyze_FTextNameSpec_NoError.
    /// </summary>

    [Fact]
    public void Analyze_FTextNameSpec_NoError()
    {
        string source = """
                        routine test(x: S64)
                          var msg = f"{x:=}"
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.InvalidFTextFormatSpec);
    }
    /// <summary>
    /// Tests Analyze_FTextNameDiagnoseSpec_NoError.
    /// </summary>

    [Fact]
    public void Analyze_FTextNameDiagnoseSpec_NoError()
    {
        string source = """
                        routine test(x: S64)
                          var msg = f"{x:=?}"
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.InvalidFTextFormatSpec);
    }
    /// <summary>
    /// Tests Analyze_FTextNoSpec_NoError.
    /// </summary>

    [Fact]
    public void Analyze_FTextNoSpec_NoError()
    {
        string source = """
                        routine test(x: S64)
                          var msg = f"{x}"
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.InvalidFTextFormatSpec);
    }
    /// <summary>
    /// Tests Analyze_FTextWrongOrderSpec_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_FTextWrongOrderSpec_ReportsError()
    {
        string source = """
                        routine test(x: S64)
                          var msg = f"{x:?=}"
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.InvalidFTextFormatSpec);
    }
    /// <summary>
    /// Tests Analyze_FTextInvalidSpec_d_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_FTextInvalidSpec_d_ReportsError()
    {
        string source = """
                        routine test(x: S64)
                          var msg = f"{x:d}"
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.InvalidFTextFormatSpec);
    }
    /// <summary>
    /// Tests Analyze_FTextInvalidSpec_Format_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_FTextInvalidSpec_Format_ReportsError()
    {
        string source = """
                        routine test(x: S64)
                          var msg = f"{x:0.2f}"
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.InvalidFTextFormatSpec);
    }
}
