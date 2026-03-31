using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for named argument validation:
/// - Unknown named argument (#152 / S505)
/// - Duplicate named argument (#153 / S506)
/// - Positional after named argument (#154 / S507)
/// </summary>
public class NamedArgumentTests
{
    #region Unknown Named Argument (#152 / S505)
    /// <summary>
    /// Tests Analyze_UnknownNamedArgument_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_UnknownNamedArgument_ReportsError()
    {
        string source = """
                        routine greet(name: Text) -> Text
                          return name
                        routine main()
                          greet(unknown: "Alice")
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownNamedArgument);
    }
    /// <summary>
    /// Tests Analyze_ValidNamedArgument_NoError.
    /// </summary>

    [Fact]
    public void Analyze_ValidNamedArgument_NoError()
    {
        string source = """
                        routine greet(name: Text) -> Text
                          return name
                        routine main()
                          greet(name: "Alice")
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownNamedArgument);
    }
    /// <summary>
    /// Tests Analyze_UnknownNamedArgument_MultipleParams_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_UnknownNamedArgument_MultipleParams_ReportsError()
    {
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a
                        routine main()
                          add(a: 1, c: 2)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownNamedArgument);
    }

    #endregion

    #region Duplicate Named Argument (#153 / S506)
    /// <summary>
    /// Tests Analyze_DuplicateNamedArgument_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_DuplicateNamedArgument_ReportsError()
    {
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a
                        routine main()
                          add(a: 1, a: 2)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DuplicateNamedArgument);
    }
    /// <summary>
    /// Tests Analyze_PositionalThenSameNamed_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_PositionalThenSameNamed_ReportsError()
    {
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a
                        routine main()
                          add(1, a: 2)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DuplicateNamedArgument);
    }
    /// <summary>
    /// Tests Analyze_DistinctNamedArguments_NoError.
    /// </summary>

    [Fact]
    public void Analyze_DistinctNamedArguments_NoError()
    {
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a
                        routine main()
                          add(a: 1, b: 2)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DuplicateNamedArgument);
    }

    #endregion

    #region Positional After Named (#154 / S507)
    /// <summary>
    /// Tests Analyze_PositionalAfterNamed_S510SubsumesS507.
    /// </summary>

    [Fact]
    public void Analyze_PositionalAfterNamed_S510SubsumesS507()
    {
        // With 2+ params, S510 fires instead of S507 (S510 subsumes S507)
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a
                        routine main()
                          add(a: 1, 2)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NamedArgumentRequired);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PositionalAfterNamed);
    }
    /// <summary>
    /// Tests Analyze_AllNamed_NoError.
    /// </summary>

    [Fact]
    public void Analyze_AllNamed_NoError()
    {
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a
                        routine main()
                          add(a: 1, b: 2)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PositionalAfterNamed);
    }
    /// <summary>
    /// Tests Analyze_NamedOutOfOrder_NoError.
    /// </summary>

    [Fact]
    public void Analyze_NamedOutOfOrder_NoError()
    {
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a
                        routine main()
                          add(b: 2, a: 1)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PositionalAfterNamed);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownNamedArgument);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DuplicateNamedArgument);
    }

    #endregion

    #region Named Argument Enforcement (S510)
    /// <summary>
    /// Tests Analyze_TwoParams_AllPositional_ReportsS510.
    /// </summary>

    [Fact]
    public void Analyze_TwoParams_AllPositional_ReportsS510()
    {
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a
                        routine main()
                          add(1, 2)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NamedArgumentRequired);
    }
    /// <summary>
    /// Tests Analyze_TwoParams_AllNamed_NoS510.
    /// </summary>

    [Fact]
    public void Analyze_TwoParams_AllNamed_NoS510()
    {
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a
                        routine main()
                          add(a: 1, b: 2)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NamedArgumentRequired);
    }
    /// <summary>
    /// Tests Analyze_TwoParams_MixedPositionalNamed_ReportsS510.
    /// </summary>

    [Fact]
    public void Analyze_TwoParams_MixedPositionalNamed_ReportsS510()
    {
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a
                        routine main()
                          add(1, b: 2)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NamedArgumentRequired);
    }
    /// <summary>
    /// Tests Analyze_OneParam_Positional_NoS510.
    /// </summary>

    [Fact]
    public void Analyze_OneParam_Positional_NoS510()
    {
        string source = """
                        routine greet(name: Text) -> Text
                          return name
                        routine main()
                          greet("Alice")
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NamedArgumentRequired);
    }
    /// <summary>
    /// Tests Analyze_ZeroParams_NoS510.
    /// </summary>

    [Fact]
    public void Analyze_ZeroParams_NoS510()
    {
        string source = """
                        routine noop() -> S32
                          return 0
                        routine main()
                          noop()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NamedArgumentRequired);
    }
    /// <summary>
    /// Tests Analyze_MemberRoutine_OneNonMeParam_Positional_NoS510.
    /// </summary>

    [Fact]
    public void Analyze_MemberRoutine_OneNonMeParam_Positional_NoS510()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32

                        routine Point.get_x() -> S32
                          return me.x

                        routine Point.offset_x(dx: S32) -> S32
                          return me.x

                        routine main()
                          var p = Point(x: 1, y: 2)
                          p.offset_x(5)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NamedArgumentRequired);
    }
    /// <summary>
    /// Tests Analyze_MemberRoutine_TwoNonMeParams_Positional_ReportsS510.
    /// </summary>

    [Fact]
    public void Analyze_MemberRoutine_TwoNonMeParams_Positional_ReportsS510()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32

                        routine Point.offset(dx: S32, dy: S32) -> S32
                          return me.x

                        routine main()
                          var p = Point(x: 1, y: 2)
                          p.offset(3, 4)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NamedArgumentRequired);
    }
    /// <summary>
    /// Tests Analyze_TwoParams_NamedOutOfOrder_NoS510.
    /// </summary>

    [Fact]
    public void Analyze_TwoParams_NamedOutOfOrder_NoS510()
    {
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a
                        routine main()
                          add(b: 2, a: 1)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NamedArgumentRequired);
    }

    #endregion
}
