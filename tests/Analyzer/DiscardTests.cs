using Verification.Results;

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
    /// Verifies semantic analysis behavior for discard call without unexpected warnings.
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
    /// Verifies semantic analysis behavior for call without discard non blank return warning.
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
    /// Verifies semantic analysis behavior for call without discard blank return without unexpected warnings.
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
    /// Verifies semantic analysis behavior for assigned call without unexpected warnings.
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
    /// Verifies semantic analysis behavior for discard member call without unexpected diagnostics.
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
    /// Verifies that the parser accepts discard member call succeeds.
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
    /// Verifies that the parser accepts discard failable member call succeeds.
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
    /// Verifies that the parser accepts discard variable and reports the expected error.
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
    /// Verifies that the parser accepts discard literal and reports the expected error.
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
    /// Verifies that the parser accepts discard string literal and reports the expected error.
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
}
