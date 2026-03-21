using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for record 'with' expression secret member validation:
/// #45: with secret member prohibition
/// </summary>
public class RecordSecretWithTests
{
    #region #45: With secret member prohibition

    [Fact]
    public void Analyze_WithOpenMemberVariable_NoError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32
                        routine test(p: Point)
                          var q = p with .x = 2
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WithSecretMemberProhibited);
    }

    [Fact]
    public void Analyze_WithSecretMemberVariable_ReportsError()
    {
        string source = """
                        record SecretRecord
                          secret hash: S32
                          name: Text
                        routine test(r: SecretRecord)
                          var s = r with .hash = 42
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WithSecretMemberProhibited);
    }

    [Fact]
    public void Analyze_WithPostedMemberVariable_NoError()
    {
        string source = """
                        record Info
                          posted status: S32
                          name: Text
                        routine test(info: Info)
                          var i = info with .name = "test"
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WithSecretMemberProhibited);
    }

    #endregion
}
