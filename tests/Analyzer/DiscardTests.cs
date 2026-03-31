using SemanticAnalysis.Results;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for the discard keyword behavior:
/// - discard routine_call() - no warning
/// - routine_call() with non-Blank return - warning
/// - routine_call() with Blank return - no warning
/// - discard x or discard 42 - parser error (must be call)
/// </summary>
public class DiscardTests
{
    #region RazorForge - Discard With Call
    /// <summary>
    /// Tests Analyze_DiscardCall_NoWarning.
    /// </summary>

    [Fact]
    public void Analyze_DiscardCall_NoWarning()
    {
        string source = """
                        routine get_value() -> S32
                          return 42

                        routine test()
                          discard get_value()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
        Assert.Empty(collection: result.Warnings);
    }
    /// <summary>
    /// Tests Analyze_CallWithoutDiscard_NonBlankReturn_Warning.
    /// </summary>

    [Fact]
    public void Analyze_CallWithoutDiscard_NonBlankReturn_Warning()
    {
        string source = """
                        routine get_value() -> S32
                          return 42

                        routine test()
                          get_value()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
        Assert.True(condition: result.Warnings.Count > 0,
            userMessage: "Expected warning for unused return value");
        Assert.Contains(collection: result.Warnings,
            filter: w => w.Message.Contains(value: "unused",
                comparisonType: StringComparison.OrdinalIgnoreCase) ||
                w.Message.Contains(value: "discard",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Tests Analyze_CallWithoutDiscard_BlankReturn_NoWarning.
    /// </summary>

    [Fact]
    public void Analyze_CallWithoutDiscard_BlankReturn_NoWarning()
    {
        string source = """
                        routine do_something()
                          pass
                          return

                        routine test()
                          do_something()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
        Assert.Empty(collection: result.Warnings);
    }
    /// <summary>
    /// Tests Analyze_AssignedCall_NoWarning.
    /// </summary>

    [Fact]
    public void Analyze_AssignedCall_NoWarning()
    {
        string source = """
                        routine get_value() -> S32
                          return 42

                        routine test()
                          var x = get_value()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
        // No warning about unused return value since it's assigned
        Assert.DoesNotContain(collection: result.Warnings,
            filter: w => w.Message.Contains(value: "unused",
                comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Tests Analyze_DiscardMemberCall_NoError.
    /// </summary>

    [Fact]
    public void Analyze_DiscardMemberCall_NoError()
    {
        string source = """
                        routine get_value() -> S32
                          return 42

                        record Wrapper
                          value: S32

                        routine Wrapper.extract(self: Wrapper) -> S32
                          return self.value

                        routine test()
                          var w = Wrapper(value: 0)
                          discard w.extract(w)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Tests Parse_DiscardMemberCall_Succeeds.
    /// </summary>

    [Fact]
    public void Parse_DiscardMemberCall_Succeeds()
    {
        string source = """
                        record Counter
                          value: S32

                        routine Counter.increment(self: Counter) -> Counter
                          return Counter(value: self.value)

                        routine test()
                          var c = Counter(value: 0)
                          discard c.increment()
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_DiscardFailableMemberCall_Succeeds.
    /// </summary>

    [Fact]
    public void Parse_DiscardFailableMemberCall_Succeeds()
    {
        string source = """
                        record Wrapper
                          value: S32

                        routine Wrapper.try_get!(self: Wrapper) -> S32
                          return self.value

                        routine test()
                          var w = Wrapper(value: 42)
                          discard w.try_get!()
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region RazorForge - Discard Parser Errors
    /// <summary>
    /// Tests Parse_DiscardVariable_ReportsError.
    /// </summary>

    [Fact]
    public void Parse_DiscardVariable_ReportsError()
    {
        string source = """
                        routine test()
                          var x = 42
                          discard x
                          return
                        """;

        // discard must be followed by a call expression - parser uses error recovery
        AssertParseError(source: source);
    }
    /// <summary>
    /// Tests Parse_DiscardLiteral_ReportsError.
    /// </summary>

    [Fact]
    public void Parse_DiscardLiteral_ReportsError()
    {
        string source = """
                        routine test()
                          discard 42
                          return
                        """;

        // discard must be followed by a call expression
        AssertParseError(source: source);
    }
    /// <summary>
    /// Tests Parse_DiscardStringLiteral_ReportsError.
    /// </summary>

    [Fact]
    public void Parse_DiscardStringLiteral_ReportsError()
    {
        string source = """
                        routine test()
                          discard "hello"
                          return
                        """;

        // discard must be followed by a call expression
        AssertParseError(source: source);
    }

    #endregion

    #region Suflae - Discard With Call
    /// <summary>
    /// Tests AnalyzeSuflae_DiscardCall_NoWarning.
    /// </summary>

    [Fact]
    public void AnalyzeSuflae_DiscardCall_NoWarning()
    {
        string source = """
                        routine get_value() -> Integer
                          return 42

                        routine test()
                          discard get_value()
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.Empty(collection: result.Errors);
        Assert.Empty(collection: result.Warnings);
    }
    /// <summary>
    /// Tests AnalyzeSuflae_CallWithoutDiscard_NonBlankReturn_Warning.
    /// </summary>

    [Fact]
    public void AnalyzeSuflae_CallWithoutDiscard_NonBlankReturn_Warning()
    {
        string source = """
                        routine get_value() -> Integer
                          return 42

                        routine test()
                          get_value()
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.Empty(collection: result.Errors);
        Assert.True(condition: result.Warnings.Count > 0,
            userMessage: "Expected warning for unused return value");
        Assert.Contains(collection: result.Warnings,
            filter: w => w.Message.Contains(value: "unused",
                comparisonType: StringComparison.OrdinalIgnoreCase) ||
                w.Message.Contains(value: "discard",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Tests AnalyzeSuflae_CallWithoutDiscard_BlankReturn_NoWarning.
    /// </summary>

    [Fact]
    public void AnalyzeSuflae_CallWithoutDiscard_BlankReturn_NoWarning()
    {
        string source = """
                        routine do_something()
                          pass

                        routine test()
                          do_something()
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.Empty(collection: result.Errors);
        Assert.Empty(collection: result.Warnings);
    }
    /// <summary>
    /// Tests AnalyzeSuflae_AssignedCall_NoWarning.
    /// </summary>

    [Fact]
    public void AnalyzeSuflae_AssignedCall_NoWarning()
    {
        string source = """
                        routine get_value() -> Integer
                          return 42

                        routine test()
                          var x = get_value()
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.Empty(collection: result.Errors);
        // No warning about unused return value since it's assigned
        Assert.DoesNotContain(collection: result.Warnings,
            filter: w => w.Message.Contains(value: "unused",
                comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Suflae - Discard Parser Errors
    /// <summary>
    /// Tests ParseSuflae_DiscardVariable_ReportsError.
    /// </summary>

    [Fact]
    public void ParseSuflae_DiscardVariable_ReportsError()
    {
        string source = """
                        routine test()
                          var x = 42
                          discard x
                        """;

        // discard must be followed by a call expression - uses error recovery
        (_, var parser) = ParseSuflaeWithErrors(source: source);
        Assert.True(condition: parser.HasErrors, userMessage: "Expected parse errors");
    }
    /// <summary>
    /// Tests ParseSuflae_DiscardLiteral_ReportsError.
    /// </summary>

    [Fact]
    public void ParseSuflae_DiscardLiteral_ReportsError()
    {
        string source = """
                        routine test()
                          discard 42
                        """;

        // discard must be followed by a call expression
        (_, var parser) = ParseSuflaeWithErrors(source: source);
        Assert.True(condition: parser.HasErrors, userMessage: "Expected parse errors");
    }
    /// <summary>
    /// Tests ParseSuflae_DiscardStringLiteral_ReportsError.
    /// </summary>

    [Fact]
    public void ParseSuflae_DiscardStringLiteral_ReportsError()
    {
        string source = """
                        routine test()
                          discard "hello"
                        """;

        // discard must be followed by a call expression
        (_, var parser) = ParseSuflaeWithErrors(source: source);
        Assert.True(condition: parser.HasErrors, userMessage: "Expected parse errors");
    }

    #endregion
}
