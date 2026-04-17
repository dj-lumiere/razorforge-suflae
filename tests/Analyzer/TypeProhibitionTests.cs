using SemanticVerification.Results;
using Compiler.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for prohibited type constructions.
/// Blank cannot be used as a generic type argument anywhere.
/// Data? / Maybe[Data] is not allowed (Data already supports None).
/// </summary>
public class TypeProhibitionTests
{
    #region Blank as Type Argument (rejected)
    /// <summary>
    /// Tests Analyze_BlankNullable_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_BlankNullable_ReportsError()
    {
        // Blank? desugars to Maybe<Blank>, which is prohibited
        string source = """
                        routine foo(x: Blank?)
                          pass
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BlankAsTypeArgument);
    }
    /// <summary>
    /// Tests Analyze_ExplicitMaybeBlank_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_ExplicitMaybeBlank_ReportsError()
    {
        string source = """
                        routine bar() -> Maybe[Blank]
                          absent
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BlankAsTypeArgument);
    }

    #endregion

    #region Normal nullable types (allowed)
    /// <summary>
    /// Tests Analyze_NormalNullable_NoErrors.
    /// </summary>

    [Fact]
    public void Analyze_NormalNullable_NoErrors()
    {
        string source = """
                        routine foo(x: S32?)
                          pass
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BlankAsTypeArgument);
    }
    /// <summary>
    /// Tests Analyze_BlankDirectType_NoErrors.
    /// </summary>

    [Fact]
    public void Analyze_BlankDirectType_NoErrors()
    {
        // Blank as a direct type (not wrapped in a generic) is fine
        string source = """
                        routine foo() -> Blank
                          pass
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BlankAsTypeArgument);
    }
    /// <summary>
    /// Tests Analyze_ResultBlank_NoErrors.
    /// </summary>

    [Fact]
    public void Analyze_ResultBlank_NoErrors()
    {
        // Result<Blank> is allowed for failable void routines
        string source = """
                        routine foo(x: Result[Blank])
                          pass
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BlankAsTypeArgument);
    }
    /// <summary>
    /// Tests Analyze_LookupBlank_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_LookupBlank_ReportsError()
    {
        // Lookup<Blank> is ambiguous: Blank is also the absent sentinel in the type_id carrier.
        string source = """
                        routine foo(x: Lookup[Blank])
                          pass
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BlankAsTypeArgument);
    }

    #endregion

    #region Nested Maybe Prohibition
    /// <summary>
    /// Tests Analyze_NestedMaybe_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_NestedMaybe_ReportsError()
    {
        string source = """
                        routine test(x: Maybe[Maybe[S32]])
                          pass
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NestedMaybeProhibited);
    }
    /// <summary>
    /// Tests Analyze_SingleMaybe_NoError.
    /// </summary>

    [Fact]
    public void Analyze_SingleMaybe_NoError()
    {
        string source = """
                        routine test(x: S32?)
                          pass
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NestedMaybeProhibited);
    }

    #endregion

    #region Byte Literal ASCII Validation
    /// <summary>
    /// Tests Analyze_ByteLiteralNonAscii_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_ByteLiteralNonAscii_ReportsError()
    {
        // Non-ASCII byte literals are rejected at the lexer level (RF-G005)
        string source = """
                        routine test()
                          var x: Byte = b'é'
                          return
                        """;

        Assert.ThrowsAny<Compiler.Diagnostics.GrammarException>(() => Analyze(source: source));
    }
    /// <summary>
    /// Tests Analyze_ByteLiteralAscii_NoGrammarError.
    /// </summary>

    [Fact]
    public void Analyze_ByteLiteralAscii_NoGrammarError()
    {
        // ASCII byte literals pass the lexer without throwing
        string source = """
                        routine test()
                          var x: Byte = b'a'
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        // No GrammarException thrown ??lexer accepts ASCII byte literals
        Assert.NotNull(@object: result);
    }

    #endregion
}
