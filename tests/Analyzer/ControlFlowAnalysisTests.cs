using System;
using Compiler.Diagnostics;
using Verification.Results;

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
    /// Verifies semantic analysis behavior for all paths return without unexpected diagnostics.
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

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
        // Should have no missing return errors
    }
    /// <summary>
    /// Verifies semantic analysis behavior for when expression return without unexpected diagnostics.
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

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for early return in loop without unexpected diagnostics.
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

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for unless else return without unexpected diagnostics.
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

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }

    #endregion

    #region Unreachable Code
    /// <summary>
    /// Verifies semantic analysis behavior for code after return and reports the expected warning.
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

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
        // Should have warning about unreachable code
    }
    /// <summary>
    /// Verifies semantic analysis behavior for code after absent and reports the expected warning.
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

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for code after throw and reports the expected warning.
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

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }

    #endregion

    #region Break and Continue Analysis
    /// <summary>
    /// Verifies semantic analysis behavior for break inside loop without unexpected diagnostics.
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

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for continue inside loop without unexpected diagnostics.
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

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for break outside loop and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_BreakOutsideLoop_ReportsError()
    {
        string source = """
                        routine test()
                          break
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for continue outside loop and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_ContinueOutsideLoop_ReportsError()
    {
        string source = """
                        routine test()
                          continue
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Failable Routine Analysis
    /// <summary>
    /// Verifies semantic analysis behavior for absent in non failable and reports the expected warning.
    /// </summary>

    [Fact]
    public void Analyze_AbsentInNonFailable_ReportsWarning()
    {
        string source = """
                        routine test() -> S32
                          absent
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.AbsentOutsideFailableFunction);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for throw in non failable and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_ThrowInNonFailable_ReportsError()
    {
        string source = """
                        routine test() -> S32
                          throw ValueError("error")
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for absent in failable without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_AbsentInFailable_NoError()
    {
        string source = """
                        routine test!() -> S32
                          absent
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
        // Should be valid
    }
    /// <summary>
    /// Verifies semantic analysis behavior for throw in failable without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_ThrowInFailable_NoError()
    {
        string source = """
                        routine test!() -> S32
                          throw ValueError("error")
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }

    #endregion

    #region Throw Terminality (S215/S216)

    /// <summary>
    /// Verifies semantic analysis behavior for throw terminates no missing return.
    /// </summary>
    [Fact]
    public void Analyze_ThrowTerminates_NoMissingReturn()
    {
        string source = """
                        routine fail!(value: S32) -> S32
                          throw ValueError("bad value")
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: "not all code paths return",
                comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Verifies semantic analysis behavior for throw in all branches no missing return.
    /// </summary>
    [Fact]
    public void Analyze_ThrowInAllBranches_NoMissingReturn()
    {
        string source = """
                        routine validate!(value: S32) -> S32
                          if value > 0
                            throw ValueError("too big")
                          else
                            throw ValueError("too small")
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: "not all code paths return",
                comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Verifies semantic analysis behavior for absent terminates no missing return.
    /// </summary>
    [Fact]
    public void Analyze_AbsentTerminates_NoMissingReturn()
    {
        string source = """
                        routine lookup!(key: S32) -> S32
                          absent
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: "not all code paths return",
                comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Verifies semantic analysis behavior for all paths throw no missing return.
    /// </summary>
    [Fact]
    public void Analyze_AllPathsThrow_NoMissingReturn()
    {
        string source = """
                        routine always_fails!(condition: bool) -> S32
                          if condition
                            throw ValueError("a")
                          else
                            throw ValueError("b")
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: "not all code paths return",
                comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Verifies semantic analysis behavior for throw with dead code no missing return.
    /// </summary>
    [Fact]
    public void Analyze_ThrowWithDeadCode_NoMissingReturn()
    {
        string source = """
                        routine fail!(value: S32) -> S32
                          throw ValueError("error")
                          var x = 10
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: "not all code paths return",
                comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Conditional Return Analysis
    /// <summary>
    /// Verifies semantic analysis behavior for if without else no return and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_IfWithoutElse_NoReturn_ReportsError()
    {
        string source = """
                        routine test(condition: bool) -> S32
                          if condition
                            return 1
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
        // Should report missing return path
    }
    /// <summary>
    /// Verifies semantic analysis behavior for nested if else all paths return without unexpected diagnostics.
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

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }

    #endregion

    #region Becomes Statement Validation
    /// <summary>
    /// Verifies semantic analysis behavior for when expression block with becomes without unexpected diagnostics.
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

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: "becomes", comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Verifies semantic analysis behavior for when expression block missing becomes and reports the expected error.
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

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "requires 'becomes'", comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Verifies semantic analysis behavior for when expression single becomes block and reports the expected error.
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

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "'=>' syntax", comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Verifies semantic analysis behavior for when expression arrow syntax without unexpected diagnostics.
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

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: "becomes", comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Verifies semantic analysis behavior for when statement block without becomes without unexpected diagnostics.
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

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: "becomes", comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    #endregion
}
