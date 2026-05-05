using Compiler.Diagnostics;
using Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for field accessor.
/// </summary>
public class FieldAccessorTests
{
    /// <summary>
    /// Verifies semantic analysis behavior for record with open and secret fields all fields and resolves the expected symbol.
    /// </summary>
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
    /// <summary>
    /// Verifies semantic analysis behavior for record with open and secret fields open fields and resolves the expected symbol.
    /// </summary>

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
