using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for C38: Field accessor activation (all_fields / open_fields).
/// These routines are auto-registered when Data type is available in stdlib.
/// </summary>
public class FieldAccessorTests
{
    [Fact]
    public void Analyze_RecordWithOpenAndSecretFields_AllFieldsResolves()
    {
        string source = """
                        record Person
                          open name: Text
                          secret age: S32
                        routine test(p: Person) -> Dict[Text, Data]
                          return p.all_fields()
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MethodNotFound);
    }

    [Fact]
    public void Analyze_RecordWithOpenAndSecretFields_OpenFieldsResolves()
    {
        string source = """
                        record Person
                          open name: Text
                          secret age: S32
                        routine test(p: Person) -> Dict[Text, Data]
                          return p.open_fields()
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MethodNotFound);
    }
}
