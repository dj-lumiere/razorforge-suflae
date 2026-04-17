using SemanticVerification.Results;
using Compiler.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for annotation and conditional expression validation rules:
/// #145: Nested conditional expression warning (defense-in-depth; parser currently prevents nesting)
/// </summary>
public class AnnotationValidationTests
{
    #region #145: Nested conditional expression warning
    /// <summary>
    /// Tests Analyze_SimpleConditional_NoWarning.
    /// </summary>

    [Fact]
    public void Analyze_SimpleConditional_NoWarning()
    {
        string source = """
                        routine test(x: Bool) -> S32
                          return if x then 1_s32 else 0_s32
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Warnings,
            filter: w => w.Code == SemanticWarningCode.NestedConditionalExpression);
    }
    /// <summary>
    /// Tests Analyze_ConditionalInRoutine_NoWarning.
    /// </summary>

    [Fact]
    public void Analyze_ConditionalInRoutine_NoWarning()
    {
        string source = """
                        routine classify(x: S32) -> Text
                          return if x > 0_s32 then "positive" else "non-positive"
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Warnings,
            filter: w => w.Code == SemanticWarningCode.NestedConditionalExpression);
    }

    #endregion
}
