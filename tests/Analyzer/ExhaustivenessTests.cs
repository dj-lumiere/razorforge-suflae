using SemanticVerification.Results;
using Compiler.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for exhaustive pattern matching enforcement (#18).
/// When expressions must be exhaustive (S356 error).
/// When statements on enumerable types warn if non-exhaustive (W250 warning).
///
/// Note: Variant exhaustiveness tests are deferred ??variant case names
/// (e.g., Shape.CIRCLE) are not yet resolved by the type system in pattern context.
/// The exhaustiveness infrastructure supports variants (CheckVariantExhaustiveness)
/// and will activate once variant type resolution in patterns is implemented.
/// </summary>
public class ExhaustivenessTests
{
    #region Choice ??When Expression
    /// <summary>
    /// Tests WhenExpression_Choice_AllCasesCovered_NoError.
    /// </summary>

    [Fact]
    public void WhenExpression_Choice_AllCasesCovered_NoError()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH
                          EAST
                          WEST
                        routine test(d: Direction) -> S32
                          return when d
                            is Direction.NORTH => 1
                            is Direction.SOUTH => 2
                            is Direction.EAST => 3
                            is Direction.WEST => 4
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }
    /// <summary>
    /// Tests WhenExpression_Choice_MissingCase_ReportsError.
    /// </summary>

    [Fact]
    public void WhenExpression_Choice_MissingCase_ReportsError()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH
                          EAST
                          WEST
                        routine test(d: Direction) -> S32
                          return when d
                            is Direction.NORTH => 1
                            is Direction.SOUTH => 2
                            is Direction.EAST => 3
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }
    /// <summary>
    /// Tests WhenExpression_Choice_WithElse_NoError.
    /// </summary>

    [Fact]
    public void WhenExpression_Choice_WithElse_NoError()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH
                          EAST
                          WEST
                        routine test(d: Direction) -> S32
                          return when d
                            is Direction.NORTH => 1
                            else => 0
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }
    /// <summary>
    /// Tests WhenExpression_Choice_Shorthand_AllCasesCovered_NoError.
    /// </summary>

    [Fact]
    public void WhenExpression_Choice_Shorthand_AllCasesCovered_NoError()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE
                        routine test(c: Color) -> S32
                          return when c
                            is RED => 1
                            is GREEN => 2
                            is BLUE => 3
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }
    /// <summary>
    /// Tests WhenExpression_Choice_EqualsOperator_ReportsError.
    /// </summary>

    [Fact]
    public void WhenExpression_Choice_EqualsOperator_ReportsError()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH
                          EAST
                          WEST
                        routine test(d: Direction) -> S32
                          return when d
                            == Direction.NORTH => 1
                            else => 0
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PatternTypeMismatch);
    }

    #endregion

    #region Choice ??When Statement
    /// <summary>
    /// Tests WhenStatement_Choice_MissingCase_ReportsWarning.
    /// </summary>

    [Fact]
    public void WhenStatement_Choice_MissingCase_ReportsWarning()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH
                          EAST
                          WEST
                        routine test(d: Direction)
                          when d
                            is Direction.NORTH => show("N")
                            is Direction.SOUTH => show("S")
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Warnings,
            filter: w => w.Code == SemanticWarningCode.NonExhaustiveWhen);
    }
    /// <summary>
    /// Tests WhenStatement_Choice_AllCasesCovered_NoWarning.
    /// </summary>

    [Fact]
    public void WhenStatement_Choice_AllCasesCovered_NoWarning()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE
                        routine test(c: Color)
                          when c
                            is Color.RED => show("red")
                            is Color.GREEN => show("green")
                            is Color.BLUE => show("blue")
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Warnings,
            filter: w => w.Code == SemanticWarningCode.NonExhaustiveWhen);
    }
    /// <summary>
    /// Tests WhenStatement_Choice_WithElse_NoWarning.
    /// </summary>

    [Fact]
    public void WhenStatement_Choice_WithElse_NoWarning()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH
                          EAST
                          WEST
                        routine test(d: Direction)
                          when d
                            is Direction.NORTH => show("N")
                            else => show("other")
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Warnings,
            filter: w => w.Code == SemanticWarningCode.NonExhaustiveWhen);
    }

    #endregion

    #region Bool ??When Expression
    /// <summary>
    /// Tests WhenExpression_Bool_BothCases_NoError.
    /// </summary>

    [Fact]
    public void WhenExpression_Bool_BothCases_NoError()
    {
        string source = """
                        routine test(b: Bool) -> S32
                          return when b
                            true => 1
                            false => 0
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }
    /// <summary>
    /// Tests WhenExpression_Bool_MissingFalse_ReportsError.
    /// </summary>

    [Fact]
    public void WhenExpression_Bool_MissingFalse_ReportsError()
    {
        string source = """
                        routine test(b: Bool) -> S32
                          return when b
                            true => 1
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }
    /// <summary>
    /// Tests WhenExpression_Bool_MissingTrue_ReportsError.
    /// </summary>

    [Fact]
    public void WhenExpression_Bool_MissingTrue_ReportsError()
    {
        string source = """
                        routine test(b: Bool) -> S32
                          return when b
                            false => 0
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }

    #endregion

    #region Error Handling Types ??When Expression
    /// <summary>
    /// Tests WhenExpression_Maybe_NoneAndElse_NoError.
    /// </summary>

    [Fact]
    public void WhenExpression_Maybe_NoneAndElse_NoError()
    {
        string source = """
                        routine process(value: S32?) -> S32
                          return when value
                            is None => 0
                            else v => v
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }

    #endregion

    #region Wildcard and Else
    /// <summary>
    /// Tests WhenExpression_Wildcard_AlwaysExhaustive.
    /// </summary>

    [Fact]
    public void WhenExpression_Wildcard_AlwaysExhaustive()
    {
        string source = """
                        routine test(x: S32) -> S32
                          return when x
                            == 1 => 10
                            _ => 0
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }
    /// <summary>
    /// Tests WhenExpression_ElseBinding_AlwaysExhaustive.
    /// </summary>

    [Fact]
    public void WhenExpression_ElseBinding_AlwaysExhaustive()
    {
        string source = """
                        routine test(x: S32) -> S32
                          return when x
                            == 1 => 10
                            else v => v
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }
    /// <summary>
    /// Tests WhenExpression_NoElse_NonEnumerableType_ReportsError.
    /// </summary>

    [Fact]
    public void WhenExpression_NoElse_NonEnumerableType_ReportsError()
    {
        // S32 has 2**32 values ??without else, cannot be exhaustive
        string source = """
                        routine test(x: S32) -> S32
                          return when x
                            == 1 => 10
                            == 2 => 20
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }

    #endregion

    #region Error Message Content
    /// <summary>
    /// Tests WhenExpression_Choice_MissingCase_ErrorIncludesMissingCaseName.
    /// </summary>

    [Fact]
    public void WhenExpression_Choice_MissingCase_ErrorIncludesMissingCaseName()
    {
        string source = """
                        choice Status
                          ACTIVE
                          INACTIVE
                          PENDING
                        routine test(s: Status) -> S32
                          return when s
                            is Status.ACTIVE => 1
                            is Status.INACTIVE => 2
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch
                         && e.Message.Contains("PENDING"));
    }
    /// <summary>
    /// Tests WhenExpression_Bool_MissingCase_ErrorIncludesMissingValue.
    /// </summary>

    [Fact]
    public void WhenExpression_Bool_MissingCase_ErrorIncludesMissingValue()
    {
        string source = """
                        routine test(b: Bool) -> S32
                          return when b
                            true => 1
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch
                         && e.Message.Contains("false"));
    }
    /// <summary>
    /// Tests WhenStatement_Choice_MissingCases_WarningIncludesMissingNames.
    /// </summary>

    [Fact]
    public void WhenStatement_Choice_MissingCases_WarningIncludesMissingNames()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH
                          EAST
                          WEST
                        routine test(d: Direction)
                          when d
                            is Direction.NORTH => show("N")
                          return
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

    #region Choice ??Unified 'is' Pattern (Phase 12)
    /// <summary>
    /// Tests WhenExpression_Choice_IsPattern_AllCases_NoError.
    /// </summary>

    [Fact]
    public void WhenExpression_Choice_IsPattern_AllCases_NoError()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH
                          EAST
                          WEST
                        routine test(d: Direction) -> S32
                          return when d
                            is NORTH => 1
                            is SOUTH => 2
                            is EAST => 3
                            is WEST => 4
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }
    /// <summary>
    /// Tests WhenExpression_Choice_IsPattern_QualifiedName_NoError.
    /// </summary>

    [Fact]
    public void WhenExpression_Choice_IsPattern_QualifiedName_NoError()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH
                          EAST
                          WEST
                        routine test(d: Direction) -> S32
                          return when d
                            is Direction.NORTH => 1
                            is Direction.SOUTH => 2
                            is Direction.EAST => 3
                            is Direction.WEST => 4
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }
    /// <summary>
    /// Tests WhenExpression_Choice_IsPattern_MissingCase_ReportsError.
    /// </summary>

    [Fact]
    public void WhenExpression_Choice_IsPattern_MissingCase_ReportsError()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH
                          EAST
                          WEST
                        routine test(d: Direction) -> S32
                          return when d
                            is NORTH => 1
                            is SOUTH => 2
                            is EAST => 3
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }
    /// <summary>
    /// Tests WhenExpression_Choice_IsPattern_WithElse_NoError.
    /// </summary>

    [Fact]
    public void WhenExpression_Choice_IsPattern_WithElse_NoError()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH
                          EAST
                          WEST
                        routine test(d: Direction) -> S32
                          return when d
                            is NORTH => 1
                            else => 0
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }
    /// <summary>
    /// Tests WhenStatement_Choice_IsPattern_MissingCase_ReportsWarning.
    /// </summary>

    [Fact]
    public void WhenStatement_Choice_IsPattern_MissingCase_ReportsWarning()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH
                          EAST
                          WEST
                        routine test(d: Direction)
                          when d
                            is NORTH => show("N")
                            is SOUTH => show("S")
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Warnings,
            filter: w => w.Code == SemanticWarningCode.NonExhaustiveWhen);
    }
    /// <summary>
    /// Tests WhenExpression_Choice_MixedIsAndEquals_ReportsError.
    /// </summary>

    [Fact]
    public void WhenExpression_Choice_MixedIsAndEquals_ReportsError()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH
                          EAST
                          WEST
                        routine test(d: Direction) -> S32
                          return when d
                            is NORTH => 1
                            == Direction.SOUTH => 2
                            is EAST => 3
                            is WEST => 4
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PatternTypeMismatch);
    }
    /// <summary>
    /// Tests WhenExpression_Choice_IsPattern_InvalidCase_ReportsError.
    /// </summary>

    [Fact]
    public void WhenExpression_Choice_IsPattern_InvalidCase_ReportsError()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH
                          EAST
                          WEST
                        routine test(d: Direction) -> S32
                          return when d
                            is NORTH => 1
                            is SOUTH => 2
                            is EAST => 3
                            is INVALID => 4
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceCaseNotFound);
    }
    /// <summary>
    /// Tests WhenExpression_Choice_IsPattern_VariableBinding_ReportsError.
    /// </summary>

    [Fact]
    public void WhenExpression_Choice_IsPattern_VariableBinding_ReportsError()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH
                          EAST
                          WEST
                        routine test(d: Direction) -> S32
                          return when d
                            is NORTH n => 1
                            else => 0
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PatternTypeMismatch);
    }

    #endregion
}
