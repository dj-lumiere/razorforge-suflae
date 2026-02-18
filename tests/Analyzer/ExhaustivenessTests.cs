using Compilers.Analysis.Results;
using RazorForge.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for exhaustive pattern matching enforcement (#18).
/// When expressions must be exhaustive (S356 error).
/// When statements on enumerable types warn if non-exhaustive (W250 warning).
///
/// Note: Variant exhaustiveness tests are deferred — variant case names
/// (e.g., Shape.CIRCLE) are not yet resolved by the type system in pattern context.
/// The exhaustiveness infrastructure supports variants (CheckVariantExhaustiveness)
/// and will activate once variant type resolution in patterns is implemented.
/// </summary>
public class ExhaustivenessTests
{
    #region Choice — When Expression

    [Fact]
    public void WhenExpression_Choice_AllCasesCovered_NoError()
    {
        string source = """
                        choice Direction {
                            NORTH
                            SOUTH
                            EAST
                            WEST
                        }
                        routine test(d: Direction) -> S32 {
                            return when d {
                                == Direction.NORTH => 1
                                == Direction.SOUTH => 2
                                == Direction.EAST => 3
                                == Direction.WEST => 4
                            }
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }

    [Fact]
    public void WhenExpression_Choice_MissingCase_ReportsError()
    {
        string source = """
                        choice Direction {
                            NORTH
                            SOUTH
                            EAST
                            WEST
                        }
                        routine test(d: Direction) -> S32 {
                            return when d {
                                == Direction.NORTH => 1
                                == Direction.SOUTH => 2
                                == Direction.EAST => 3
                            }
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }

    [Fact]
    public void WhenExpression_Choice_WithElse_NoError()
    {
        string source = """
                        choice Direction {
                            NORTH
                            SOUTH
                            EAST
                            WEST
                        }
                        routine test(d: Direction) -> S32 {
                            return when d {
                                == Direction.NORTH => 1
                                else => 0
                            }
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }

    [Fact]
    public void WhenExpression_Choice_Shorthand_AllCasesCovered_NoError()
    {
        string source = """
                        choice Color {
                            RED
                            GREEN
                            BLUE
                        }
                        routine test(c: Color) -> S32 {
                            return when c {
                                == RED => 1
                                == GREEN => 2
                                == BLUE => 3
                            }
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }

    #endregion

    #region Choice — When Statement

    [Fact]
    public void WhenStatement_Choice_MissingCase_ReportsWarning()
    {
        string source = """
                        choice Direction {
                            NORTH
                            SOUTH
                            EAST
                            WEST
                        }
                        routine test(d: Direction) {
                            when d {
                                == Direction.NORTH => show("N")
                                == Direction.SOUTH => show("S")
                            }
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Warnings,
            filter: w => w.Code == SemanticWarningCode.NonExhaustiveWhen);
    }

    [Fact]
    public void WhenStatement_Choice_AllCasesCovered_NoWarning()
    {
        string source = """
                        choice Color {
                            RED
                            GREEN
                            BLUE
                        }
                        routine test(c: Color) {
                            when c {
                                == Color.RED => show("red")
                                == Color.GREEN => show("green")
                                == Color.BLUE => show("blue")
                            }
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Warnings,
            filter: w => w.Code == SemanticWarningCode.NonExhaustiveWhen);
    }

    [Fact]
    public void WhenStatement_Choice_WithElse_NoWarning()
    {
        string source = """
                        choice Direction {
                            NORTH
                            SOUTH
                            EAST
                            WEST
                        }
                        routine test(d: Direction) {
                            when d {
                                == Direction.NORTH => show("N")
                                else => show("other")
                            }
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Warnings,
            filter: w => w.Code == SemanticWarningCode.NonExhaustiveWhen);
    }

    #endregion

    #region Bool — When Expression

    [Fact]
    public void WhenExpression_Bool_BothCases_NoError()
    {
        string source = """
                        routine test(b: Bool) -> S32 {
                            return when b {
                                true => 1
                                false => 0
                            }
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }

    [Fact]
    public void WhenExpression_Bool_MissingFalse_ReportsError()
    {
        string source = """
                        routine test(b: Bool) -> S32 {
                            return when b {
                                true => 1
                            }
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }

    [Fact]
    public void WhenExpression_Bool_MissingTrue_ReportsError()
    {
        string source = """
                        routine test(b: Bool) -> S32 {
                            return when b {
                                false => 0
                            }
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }

    #endregion

    #region Error Handling Types — When Expression

    [Fact]
    public void WhenExpression_Maybe_NoneAndElse_NoError()
    {
        string source = """
                        routine process(value: S32?) -> S32 {
                            return when value {
                                is None => 0
                                else v => v
                            }
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }

    #endregion

    #region Wildcard and Else

    [Fact]
    public void WhenExpression_Wildcard_AlwaysExhaustive()
    {
        string source = """
                        routine test(x: S32) -> S32 {
                            return when x {
                                == 1 => 10
                                _ => 0
                            }
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }

    [Fact]
    public void WhenExpression_ElseBinding_AlwaysExhaustive()
    {
        string source = """
                        routine test(x: S32) -> S32 {
                            return when x {
                                == 1 => 10
                                else v => v
                            }
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }

    [Fact]
    public void WhenExpression_NoElse_NonEnumerableType_ReportsError()
    {
        // S32 has 2**32 values — without else, cannot be exhaustive
        string source = """
                        routine test(x: S32) -> S32 {
                            return when x {
                                == 1 => 10
                                == 2 => 20
                            }
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }

    #endregion

    #region Error Message Content

    [Fact]
    public void WhenExpression_Choice_MissingCase_ErrorIncludesMissingCaseName()
    {
        string source = """
                        choice Status {
                            ACTIVE
                            INACTIVE
                            PENDING
                        }
                        routine test(s: Status) -> S32 {
                            return when s {
                                == Status.ACTIVE => 1
                                == Status.INACTIVE => 2
                            }
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch
                         && e.Message.Contains("PENDING"));
    }

    [Fact]
    public void WhenExpression_Bool_MissingCase_ErrorIncludesMissingValue()
    {
        string source = """
                        routine test(b: Bool) -> S32 {
                            return when b {
                                true => 1
                            }
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch
                         && e.Message.Contains("false"));
    }

    [Fact]
    public void WhenStatement_Choice_MissingCases_WarningIncludesMissingNames()
    {
        string source = """
                        choice Direction {
                            NORTH
                            SOUTH
                            EAST
                            WEST
                        }
                        routine test(d: Direction) {
                            when d {
                                == Direction.NORTH => show("N")
                            }
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        SemanticWarning? warning = result.Warnings
            .FirstOrDefault(predicate: w => w.Code == SemanticWarningCode.NonExhaustiveWhen);
        Assert.NotNull(@object: warning);
        Assert.Contains(expectedSubstring: "SOUTH", actualString: warning.Message);
        Assert.Contains(expectedSubstring: "EAST", actualString: warning.Message);
        Assert.Contains(expectedSubstring: "WEST", actualString: warning.Message);
    }

    #endregion
}
