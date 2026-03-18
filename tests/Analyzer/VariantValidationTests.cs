using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for variant type validation rules:
/// #59: Variant case containment restrictions
/// </summary>
public class VariantValidationTests
{
    #region #59: Variant case containment

    [Fact]
    public void Analyze_VariantWithRecordPayload_NoError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32
                        variant Shape
                          CIRCLE: S32
                          RECTANGLE: Point
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.VariantCaseContainsInvalidType);
    }

    [Fact]
    public void Analyze_VariantWithNestedVariant_ReportsError()
    {
        string source = """
                        variant Inner
                          A
                          B: S32
                        variant Outer
                          X: Inner
                          Y
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.VariantCaseContainsInvalidType
                         && e.Message.Contains("nested variant"));
    }

    [Fact]
    public void Analyze_VariantWithPrimitivePayload_NoError()
    {
        string source = """
                        variant Value
                          INT: S32
                          FLOAT: F64
                          NONE
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.VariantCaseContainsInvalidType);
    }

    #endregion
}
