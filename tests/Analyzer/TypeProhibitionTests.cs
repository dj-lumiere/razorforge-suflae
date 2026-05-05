using Compiler.Diagnostics;
using Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for type prohibition.
/// </summary>
public class TypeProhibitionTests
{
    #region Blank as Type Argument (rejected)
    /// <summary>
    /// Verifies semantic analysis behavior for blank nullable and reports the expected error.
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
    /// Verifies semantic analysis behavior for explicit maybe blank and reports the expected error.
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
    /// Verifies semantic analysis behavior for normal nullable without unexpected diagnostics.
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
    /// Verifies semantic analysis behavior for blank direct type without unexpected diagnostics.
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
    /// Verifies semantic analysis behavior for result blank without unexpected diagnostics.
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
    /// Verifies semantic analysis behavior for lookup blank and reports the expected error.
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
    /// Verifies semantic analysis behavior for nested maybe and reports the expected error.
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
    /// Verifies semantic analysis behavior for single maybe without unexpected diagnostics.
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
    /// Verifies semantic analysis behavior for byte literal non ascii and reports the expected error.
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

        Assert.ThrowsAny<GrammarException>(() => Analyze(source: source));
    }
    /// <summary>
    /// Verifies semantic analysis behavior for byte literal ascii without grammar diagnostics.
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
