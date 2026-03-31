using SemanticAnalysis.Results;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for control flow analysis in the semantic analyzer:
/// reachability, unreachable code, return path analysis.
/// </summary>
public class ControlFlowAnalysisTests
{
    #region Return Path Analysis
    /// <summary>
    /// Tests Analyze_AllPathsReturn_NoError.
    /// </summary>

    [Fact]
    public void Analyze_AllPathsReturn_NoError()
    {
        string source = """
                        routine get_value(condition: bool) -> S32
                          if condition
                            return 1
                          else
                            return 0
                        """;

        Analyze(source: source);
        // Should have no missing return errors
    }
    /// <summary>
    /// Tests Analyze_WhenExpressionReturn_NoError.
    /// </summary>

    [Fact]
    public void Analyze_WhenExpressionReturn_NoError()
    {
        string source = """
                        choice Status
                          ACTIVE
                          INACTIVE
                          PENDING

                        routine get_description(s: Status) -> Text
                          return when s
                            is ACTIVE => "Running"
                            is INACTIVE => "Stopped"
                            is PENDING => "Waiting"
                          return
                        """;

        Analyze(source: source);
    }
    /// <summary>
    /// Tests Analyze_EarlyReturnInLoop_NoError.
    /// </summary>

    [Fact]
    public void Analyze_EarlyReturnInLoop_NoError()
    {
        string source = """
                        routine find!(items: List[S32], target: S32) -> S32
                          for item in items
                            if item == target
                              return item
                          absent
                          return
                        """;

        Analyze(source: source);
    }
    /// <summary>
    /// Tests Analyze_UnlessElseReturn_NoError.
    /// </summary>

    [Fact]
    public void Analyze_UnlessElseReturn_NoError()
    {
        string source = """
                        routine validate!(value: S32) -> S32
                          unless value > 0
                            throw ValueError("Must be positive")
                          return value
                        """;

        Analyze(source: source);
    }

    #endregion

    #region Unreachable Code
    /// <summary>
    /// Tests Analyze_CodeAfterReturn_ReportsWarning.
    /// </summary>

    [Fact]
    public void Analyze_CodeAfterReturn_ReportsWarning()
    {
        string source = """
                        routine test() -> S32
                          return 42
                          var x = 10
                          return
                        """;

        Analyze(source: source);
        // Should have warning about unreachable code
    }
    /// <summary>
    /// Tests Analyze_CodeAfterAbsent_ReportsWarning.
    /// </summary>

    [Fact]
    public void Analyze_CodeAfterAbsent_ReportsWarning()
    {
        string source = """
                        routine test!() -> S32
                          absent
                          var x = 10
                          return
                        """;

        Analyze(source: source);
    }
    /// <summary>
    /// Tests Analyze_CodeAfterThrow_ReportsWarning.
    /// </summary>

    [Fact]
    public void Analyze_CodeAfterThrow_ReportsWarning()
    {
        string source = """
                        routine test!() -> S32
                          throw ValueError("error")
                          var x = 10
                          return
                        """;

        Analyze(source: source);
    }

    #endregion

    #region Break and Continue Analysis
    /// <summary>
    /// Tests Analyze_BreakInsideLoop_NoError.
    /// </summary>

    [Fact]
    public void Analyze_BreakInsideLoop_NoError()
    {
        string source = """
                        routine test()
                          for i in 0 til 10
                            if i == 5
                              break
                          return
                        """;

        Analyze(source: source);
    }
    /// <summary>
    /// Tests Analyze_ContinueInsideLoop_NoError.
    /// </summary>

    [Fact]
    public void Analyze_ContinueInsideLoop_NoError()
    {
        string source = """
                        routine test()
                          for i in 0 til 10
                            if i == 5
                              continue
                            show(i)
                          return
                        """;

        Analyze(source: source);
    }
    /// <summary>
    /// Tests Analyze_BreakOutsideLoop_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_BreakOutsideLoop_ReportsError()
    {
        string source = """
                        routine test()
                          break
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }
    /// <summary>
    /// Tests Analyze_ContinueOutsideLoop_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_ContinueOutsideLoop_ReportsError()
    {
        string source = """
                        routine test()
                          continue
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Failable Routine Analysis
    /// <summary>
    /// Tests Analyze_AbsentInNonFailable_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_AbsentInNonFailable_ReportsError()
    {
        string source = """
                        routine test() -> S32
                          absent
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }
    /// <summary>
    /// Tests Analyze_ThrowInNonFailable_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_ThrowInNonFailable_ReportsError()
    {
        string source = """
                        routine test() -> S32
                          throw ValueError("error")
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }
    /// <summary>
    /// Tests Analyze_AbsentInFailable_NoError.
    /// </summary>

    [Fact]
    public void Analyze_AbsentInFailable_NoError()
    {
        string source = """
                        routine test!() -> S32
                          absent
                          return
                        """;

        Analyze(source: source);
        // Should be valid
    }
    /// <summary>
    /// Tests Analyze_ThrowInFailable_NoError.
    /// </summary>

    [Fact]
    public void Analyze_ThrowInFailable_NoError()
    {
        string source = """
                        routine test!() -> S32
                          throw ValueError("error")
                          return
                        """;

        Analyze(source: source);
    }

    #endregion

    #region Conditional Return Analysis
    /// <summary>
    /// Tests Analyze_IfWithoutElse_NoReturn_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_IfWithoutElse_NoReturn_ReportsError()
    {
        string source = """
                        routine test(condition: bool) -> S32
                          if condition
                            return 1
                        """;

        Analyze(source: source);
        // Should report missing return path
    }
    /// <summary>
    /// Tests Analyze_NestedIfElse_AllPathsReturn_NoError.
    /// </summary>

    [Fact]
    public void Analyze_NestedIfElse_AllPathsReturn_NoError()
    {
        string source = """
                        routine test(a: bool, b: bool) -> S32
                          if a
                            if b
                              return 1
                            else
                              return 2
                          else
                            return 3
                        """;

        Analyze(source: source);
    }

    #endregion

    #region Suflae Control Flow
    /// <summary>
    /// Tests AnalyzeSuflae_AllPathsReturn_NoError.
    /// </summary>

    [Fact]
    public void AnalyzeSuflae_AllPathsReturn_NoError()
    {
        string source = """
                        routine get_value(condition: bool) -> Integer
                          if condition
                            return 1
                          else
                            return 0
                        """;

        AnalyzeSuflae(source: source);
    }
    /// <summary>
    /// Tests AnalyzeSuflae_BreakInsideLoop_NoError.
    /// </summary>

    [Fact]
    public void AnalyzeSuflae_BreakInsideLoop_NoError()
    {
        string source = """
                        routine test()
                          for i in 0 til 10
                            if i == 5
                              break
                        """;

        AnalyzeSuflae(source: source);
    }
    /// <summary>
    /// Tests AnalyzeSuflae_BreakOutsideLoop_ReportsError.
    /// </summary>

    [Fact]
    public void AnalyzeSuflae_BreakOutsideLoop_ReportsError()
    {
        string source = """
                        routine test()
                          break
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Becomes Statement Validation
    /// <summary>
    /// Tests Analyze_WhenExpressionBlockWithBecomes_NoError.
    /// </summary>

    [Fact]
    public void Analyze_WhenExpressionBlockWithBecomes_NoError()
    {
        // Multi-statement block with becomes is valid
        string source = """
                        routine test(value: S32) -> S32
                          var result = when value
                            == 1 =>
                              var x = value * 2
                              becomes x
                            else => 0
                          return result
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: "becomes", comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Tests Analyze_WhenExpressionBlockMissingBecomes_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_WhenExpressionBlockMissingBecomes_ReportsError()
    {
        // Multi-statement block in when expression without becomes should error
        string source = """
                        routine test(value: S32) -> S32
                          var result = when value
                            == 1 =>
                              var x = value * 2
                              x
                            else => 0
                          return result
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "requires 'becomes'", comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Tests Analyze_WhenExpressionSingleBecomesBlock_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_WhenExpressionSingleBecomesBlock_ReportsError()
    {
        // Block containing only 'becomes' should use => syntax instead
        string source = """
                        routine test(value: S32) -> S32
                          var result = when value
                            == 1 =>
                              becomes 42
                            else => 0
                          return result
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "'=>' syntax", comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Tests Analyze_WhenExpressionArrowSyntax_NoError.
    /// </summary>

    [Fact]
    public void Analyze_WhenExpressionArrowSyntax_NoError()
    {
        // Single expression with => is valid
        string source = """
                        routine test(value: S32) -> S32
                          var result = when value
                            == 1 => 42
                            else => 0
                          return result
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: "becomes", comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Tests Analyze_WhenStatementBlockWithoutBecomes_NoError.
    /// </summary>

    [Fact]
    public void Analyze_WhenStatementBlockWithoutBecomes_NoError()
    {
        // When statement (not expression) doesn't need becomes
        string source = """
                        routine test(value: S32)
                          when value
                            == 1 =>
                              var x = value * 2
                              show(x)
                            else =>
                              show(value)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: "becomes", comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Tests AnalyzeSuflae_WhenExpressionBlockWithBecomes_NoError.
    /// </summary>

    [Fact]
    public void AnalyzeSuflae_WhenExpressionBlockWithBecomes_NoError()
    {
        // Multi-statement block with becomes is valid in Suflae
        string source = """
                        routine test(value: Integer) -> Integer
                          var result = when value
                            == 1 =>
                              var x = value * 2
                              becomes x
                            else => 0
                          return result
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: "becomes", comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Tests AnalyzeSuflae_WhenExpressionBlockMissingBecomes_ReportsError.
    /// </summary>

    [Fact]
    public void AnalyzeSuflae_WhenExpressionBlockMissingBecomes_ReportsError()
    {
        // Multi-statement block in when expression without becomes should error in Suflae
        string source = """
                        routine test(value: Integer) -> Integer
                          var result = when value
                            == 1 =>
                              var x = value * 2
                              x
                            else => 0
                          return result
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "requires 'becomes'", comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    #endregion
}
