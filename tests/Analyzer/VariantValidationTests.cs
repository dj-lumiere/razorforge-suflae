using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for variant type validation rules:
/// #59: Variant member containment restrictions
/// </summary>
public class VariantValidationTests
{
    #region #59: Variant member containment

    [Fact]
    public void Analyze_VariantWithRecordMember_NoError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32
                        variant Shape
                          S32
                          Point
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
                          S32
                          None
                        variant Outer
                          Inner
                          None
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.VariantCaseContainsInvalidType
                         && e.Message.Contains("nested variant"));
    }

    [Fact]
    public void Analyze_VariantWithPrimitiveMember_NoError()
    {
        string source = """
                        variant Value
                          S32
                          F64
                          None
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.VariantCaseContainsInvalidType);
    }

    #endregion

    #region #58: Variant must dismantle immediately

    [Fact]
    public void Analyze_VariantDismantledImmediately_NoError()
    {
        string source = """
                        variant Shape
                          S32
                          F64

                        routine make_shape() -> Shape
                          pass
                          return

                        routine test()
                          var s = make_shape()
                          when s
                            is S32(r) => pass
                            else => pass
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.VariantNotDismantled);
    }

    [Fact]
    public void Analyze_VariantNotDismantled_ReportsError()
    {
        string source = """
                        variant Shape
                          S32
                          F64

                        routine make_shape() -> Shape
                          pass
                          return

                        routine other()
                          return

                        routine test()
                          var s = make_shape()
                          other()
                          when s
                            is S32(r) => pass
                            else => pass
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.VariantNotDismantled);
    }

    [Fact]
    public void Analyze_VariantNeverDismantled_ReportsError()
    {
        string source = """
                        variant Shape
                          S32
                          F64

                        routine make_shape() -> Shape
                          pass
                          return

                        routine test()
                          var s = make_shape()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.VariantNotDismantled);
    }

    #endregion
}
