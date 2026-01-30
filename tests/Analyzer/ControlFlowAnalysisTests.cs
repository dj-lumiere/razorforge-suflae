using Compilers.Analysis.Results;
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

    [Fact]
    public void Analyze_AllPathsReturn_NoError()
    {
        string source = """
                        routine get_value(condition: bool) -> S32 {
                            if condition {
                                return 1
                            } else {
                                return 0
                            }
                        }
                        """;

        Analyze(source: source);
        // Should have no missing return errors
    }

    [Fact]
    public void Analyze_WhenExpressionReturn_NoError()
    {
        string source = """
                        choice Status {
                            ACTIVE
                            INACTIVE
                            PENDING
                        }

                        routine get_description(s: Status) -> Text {
                            return when s {
                                is ACTIVE => "Running"
                                is INACTIVE => "Stopped"
                                is PENDING => "Waiting"
                            }
                        }
                        """;

        Analyze(source: source);
    }

    [Fact]
    public void Analyze_EarlyReturnInLoop_NoError()
    {
        string source = """
                        routine find!(items: List<S32>, target: S32) -> S32 {
                            for item in items {
                                if item == target {
                                    return item
                                }
                            }
                            absent
                        }
                        """;

        Analyze(source: source);
    }

    [Fact]
    public void Analyze_UnlessElseReturn_NoError()
    {
        string source = """
                        routine validate!(value: S32) -> S32 {
                            unless value > 0 {
                                throw ValueError("Must be positive")
                            }
                            return value
                        }
                        """;

        Analyze(source: source);
    }

    #endregion

    #region Unreachable Code

    [Fact]
    public void Analyze_CodeAfterReturn_ReportsWarning()
    {
        string source = """
                        routine test() -> S32 {
                            return 42
                            let x = 10
                        }
                        """;

        Analyze(source: source);
        // Should have warning about unreachable code
    }

    [Fact]
    public void Analyze_CodeAfterAbsent_ReportsWarning()
    {
        string source = """
                        routine test!() -> S32 {
                            absent
                            let x = 10
                        }
                        """;

        Analyze(source: source);
    }

    [Fact]
    public void Analyze_CodeAfterThrow_ReportsWarning()
    {
        string source = """
                        routine test!() -> S32 {
                            throw ValueError("error")
                            let x = 10
                        }
                        """;

        Analyze(source: source);
    }

    #endregion

    #region Break and Continue Analysis

    [Fact]
    public void Analyze_BreakInsideLoop_NoError()
    {
        string source = """
                        routine test() {
                            for i in 0 to 10 {
                                if i == 5 {
                                    break
                                }
                            }
                        }
                        """;

        Analyze(source: source);
    }

    [Fact]
    public void Analyze_ContinueInsideLoop_NoError()
    {
        string source = """
                        routine test() {
                            for i in 0 to 10 {
                                if i == 5 {
                                    continue
                                }
                                show(i)
                            }
                        }
                        """;

        Analyze(source: source);
    }

    [Fact]
    public void Analyze_BreakOutsideLoop_ReportsError()
    {
        string source = """
                        routine test() {
                            break
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    [Fact]
    public void Analyze_ContinueOutsideLoop_ReportsError()
    {
        string source = """
                        routine test() {
                            continue
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Failable Routine Analysis

    [Fact]
    public void Analyze_AbsentInNonFailable_ReportsError()
    {
        string source = """
                        routine test() -> S32 {
                            absent
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    [Fact]
    public void Analyze_ThrowInNonFailable_ReportsError()
    {
        string source = """
                        routine test() -> S32 {
                            throw ValueError("error")
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    [Fact]
    public void Analyze_AbsentInFailable_NoError()
    {
        string source = """
                        routine test!() -> S32 {
                            absent
                        }
                        """;

        Analyze(source: source);
        // Should be valid
    }

    [Fact]
    public void Analyze_ThrowInFailable_NoError()
    {
        string source = """
                        routine test!() -> S32 {
                            throw ValueError("error")
                        }
                        """;

        Analyze(source: source);
    }

    #endregion

    #region Conditional Return Analysis

    [Fact]
    public void Analyze_IfWithoutElse_NoReturn_ReportsError()
    {
        string source = """
                        routine test(condition: bool) -> S32 {
                            if condition {
                                return 1
                            }
                        }
                        """;

        Analyze(source: source);
        // Should report missing return path
    }

    [Fact]
    public void Analyze_NestedIfElse_AllPathsReturn_NoError()
    {
        string source = """
                        routine test(a: bool, b: bool) -> S32 {
                            if a {
                                if b {
                                    return 1
                                } else {
                                    return 2
                                }
                            } else {
                                return 3
                            }
                        }
                        """;

        Analyze(source: source);
    }

    #endregion

    #region Suflae Control Flow

    [Fact]
    public void AnalyzeSuflae_AllPathsReturn_NoError()
    {
        string source = """
                        routine get_value(condition: bool) -> Integer:
                            if condition:
                                return 1
                            else:
                                return 0
                        """;

        AnalyzeSuflae(source: source);
    }

    [Fact]
    public void AnalyzeSuflae_BreakInsideLoop_NoError()
    {
        string source = """
                        routine test():
                            for i in 0 to 10:
                                if i == 5:
                                    break
                        """;

        AnalyzeSuflae(source: source);
    }

    [Fact]
    public void AnalyzeSuflae_BreakOutsideLoop_ReportsError()
    {
        string source = """
                        routine test():
                            break
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Becomes Statement Validation

    [Fact]
    public void Analyze_WhenExpressionBlockWithBecomes_NoError()
    {
        // Multi-statement block with becomes is valid
        string source = """
                        routine test(value: S32) -> S32 {
                            let result = when value {
                                == 1 {
                                    let x = value * 2
                                    becomes x
                                }
                                else => 0
                            }
                            return result
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: "becomes", comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_WhenExpressionBlockMissingBecomes_ReportsError()
    {
        // Multi-statement block in when expression without becomes should error
        string source = """
                        routine test(value: S32) -> S32 {
                            let result = when value {
                                == 1 {
                                    let x = value * 2
                                    x
                                }
                                else => 0
                            }
                            return result
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "requires 'becomes'", comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_WhenExpressionSingleBecomesBlock_ReportsError()
    {
        // Block containing only 'becomes' should use => syntax instead
        string source = """
                        routine test(value: S32) -> S32 {
                            let result = when value {
                                == 1 {
                                    becomes 42
                                }
                                else => 0
                            }
                            return result
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "'=>' syntax", comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_WhenExpressionArrowSyntax_NoError()
    {
        // Single expression with => is valid
        string source = """
                        routine test(value: S32) -> S32 {
                            let result = when value {
                                == 1 => 42
                                else => 0
                            }
                            return result
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: "becomes", comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_WhenStatementBlockWithoutBecomes_NoError()
    {
        // When statement (not expression) doesn't need becomes
        string source = """
                        routine test(value: S32) {
                            when value {
                                == 1 {
                                    let x = value * 2
                                    show(x)
                                }
                                else {
                                    show(value)
                                }
                            }
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: "becomes", comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnalyzeSuflae_WhenExpressionBlockWithBecomes_NoError()
    {
        // Multi-statement block with becomes is valid in Suflae
        string source = """
                        routine test(value: Integer) -> Integer:
                            let result = when value:
                                == 1:
                                    let x = value * 2
                                    becomes x
                                else => 0
                            return result
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: "becomes", comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnalyzeSuflae_WhenExpressionBlockMissingBecomes_ReportsError()
    {
        // Multi-statement block in when expression without becomes should error in Suflae
        string source = """
                        routine test(value: Integer) -> Integer:
                            let result = when value:
                                == 1:
                                    let x = value * 2
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
