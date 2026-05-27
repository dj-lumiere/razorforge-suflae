using Compiler.Diagnostics;
using Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for variant validation.
/// </summary>
public class VariantValidationTests
{
    #region #59: Variant member containment
    /// <summary>
    /// Verifies semantic analysis behavior for variant with record member without unexpected diagnostics.
    /// </summary>

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

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.VariantCaseContainsInvalidType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for variant with primitive member without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_VariantWithPrimitiveMember_NoError()
    {
        string source = """
                        variant Value
                          S32
                          F64
                          None
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.VariantCaseContainsInvalidType);
    }

    #endregion
}
