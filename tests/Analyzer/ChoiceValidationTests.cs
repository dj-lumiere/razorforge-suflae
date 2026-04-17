using SemanticVerification.Results;
using Compiler.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for choice type validation.
/// Choices are S32-backed enums with all-or-nothing explicit values,
/// duplicate value detection, and range validation.
/// </summary>
public class ChoiceValidationTests
{
    #region Valid Choices (no errors expected)
    /// <summary>
    /// Tests Analyze_SimpleChoice_NoErrors.
    /// </summary>

    [Fact]
    public void Analyze_SimpleChoice_NoErrors()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH
                          EAST
                          WEST
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceMixedValues);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceDuplicateValue);
    }
    /// <summary>
    /// Tests Analyze_ChoiceWithExplicitValues_NoErrors.
    /// </summary>

    [Fact]
    public void Analyze_ChoiceWithExplicitValues_NoErrors()
    {
        string source = """
                        choice HttpStatus
                          OK: 200
                          NOT_FOUND: 404
                          ERROR: 500
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceMixedValues);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceDuplicateValue);
    }
    /// <summary>
    /// Tests Analyze_ChoiceWithNegativeValues_NoErrors.
    /// </summary>

    [Fact]
    public void Analyze_ChoiceWithNegativeValues_NoErrors()
    {
        string source = """
                        choice ComparisonSign
                          ME_SMALL: -1
                          SAME: 0
                          ME_LARGE: 1
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceMixedValues);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceDuplicateValue);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceCaseValueOverflow);
    }
    /// <summary>
    /// Tests Analyze_ChoiceWithLargeValues_OverflowError.
    /// </summary>

    [Fact]
    public void Analyze_ChoiceWithLargeValues_OverflowError()
    {
        // Values exceeding S32 range should produce overflow errors
        string source = """
                        choice BigValues
                          SMALL: 0
                          LARGE: 3000000000
                          HUGE: 9000000000000000000
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceCaseValueOverflow);
    }

    #endregion

    #region Mixed Values (error expected)
    /// <summary>
    /// Tests Analyze_ChoiceMixedValues_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_ChoiceMixedValues_ReportsError()
    {
        string source = """
                        choice Bad
                          FIRST: 1
                          SECOND
                          THIRD: 3
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceMixedValues);
    }

    #endregion

    #region Duplicate Values (error expected)
    /// <summary>
    /// Tests Analyze_ChoiceDuplicateExplicitValues_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_ChoiceDuplicateExplicitValues_ReportsError()
    {
        string source = """
                        choice Duplicated
                          FIRST: 1
                          SECOND: 2
                          THIRD: 1
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceDuplicateValue);
    }

    #endregion

    #region Operator Prohibition (choices do not support any operators)
    /// <summary>
    /// Tests Analyze_ChoiceAddition_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_ChoiceAddition_ReportsError()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH

                        routine test()
                          var d = NORTH
                          var x = d + SOUTH
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArithmeticOnChoiceType);
    }
    /// <summary>
    /// Tests Analyze_ChoiceCompoundAssignment_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_ChoiceCompoundAssignment_ReportsError()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH

                        routine test()
                          var d = NORTH
                          d += SOUTH
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArithmeticOnChoiceType);
    }
    /// <summary>
    /// Tests Analyze_ChoiceBitwise_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_ChoiceBitwise_ReportsError()
    {
        string source = """
                        choice Flags
                          A
                          B

                        routine test()
                          var f = A
                          var x = f & B
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArithmeticOnChoiceType);
    }
    /// <summary>
    /// Tests Analyze_ChoiceComparison_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_ChoiceComparison_ReportsError()
    {
        string source = """
                        choice Priority
                          LOW
                          HIGH

                        routine test()
                          var p = LOW
                          var x = p < HIGH
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArithmeticOnChoiceType);
    }
    /// <summary>
    /// Tests Analyze_ChoiceEquality_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_ChoiceEquality_ReportsError()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH

                        routine test()
                          var d = NORTH
                          var same = d == SOUTH
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArithmeticOnChoiceType);
    }

    #endregion

    #region Member Access (C98)
    /// <summary>
    /// Tests Choice_MemberAccess_AsValue.
    /// </summary>

    [Fact]
    public void Choice_MemberAccess_AsValue()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE

                        routine test()
                          var c = Color.RED
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MemberNotFound);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownIdentifier);
    }
    /// <summary>
    /// Tests Choice_MemberAccess_InvalidCase.
    /// </summary>

    [Fact]
    public void Choice_MemberAccess_InvalidCase()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE

                        routine test()
                          var c = Color.PURPLE
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MemberNotFound);
    }
    /// <summary>
    /// Tests Choice_MemberAccess_Assignment_And_Comparison.
    /// </summary>

    [Fact]
    public void Choice_MemberAccess_Assignment_And_Comparison()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE

                        routine test()
                          var c = Color.RED
                          var same = (c == Color.BLUE)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MemberNotFound);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownIdentifier);
    }

    #endregion
}
