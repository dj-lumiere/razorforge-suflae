using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for choice type validation.
/// Choices are S64-backed enums with all-or-nothing explicit values,
/// duplicate value detection, and range validation.
/// </summary>
public class ChoiceValidationTests
{
    #region Valid Choices (no errors expected)

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

    [Fact]
    public void Analyze_ChoiceWithLargeS64Values_NoErrors()
    {
        // Values exceeding S32 range but within S64
        string source = """
                        choice BigValues
                          SMALL: 0
                          LARGE: 3000000000
                          HUGE: 9000000000000000000
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceCaseValueOverflow);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceDuplicateValue);
    }

    #endregion

    #region Mixed Values (error expected)

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
}
