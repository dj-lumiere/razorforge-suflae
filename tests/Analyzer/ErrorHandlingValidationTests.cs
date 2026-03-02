using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for error handling semantic validation:
/// - Throw requires record type (#84)
/// - Failable routines must contain throw or absent (#77)
/// </summary>
public class ErrorHandlingValidationTests
{
    #region Throw Requires Record Type

    [Fact]
    public void Analyze_ThrowEntity_ReportsError()
    {
        string source = """
                        entity BadError obeys Crashable
                          message: Text
                        routine test!() -> S32
                          throw BadError(message: "oops")
                          return 0
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ThrowRequiresRecordType);
    }

    [Fact]
    public void Analyze_ThrowRecord_NoRecordError()
    {
        string source = """
                        record MyError obeys Crashable
                          message: Text
                        routine test!() -> S32
                          throw MyError(message: "oops")
                          return 0
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ThrowRequiresRecordType);
    }

    #endregion

    #region Failable Without Throw or Absent

    [Fact]
    public void Analyze_FailableWithoutThrowOrAbsent_ReportsError()
    {
        string source = """
                        routine useless!() -> S32
                          return 42
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FailableWithoutThrowOrAbsent);
    }

    [Fact]
    public void Analyze_FailableWithThrow_NoError()
    {
        string source = """
                        record MyError obeys Crashable
                          message: Text
                        routine useful!() -> S32
                          throw MyError(message: "bad")
                          return 42
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FailableWithoutThrowOrAbsent);
    }

    [Fact]
    public void Analyze_FailableWithAbsent_NoError()
    {
        string source = """
                        routine find!() -> S32
                          absent
                          return 0
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FailableWithoutThrowOrAbsent);
    }

    #endregion
}
