using Compilers.Analysis.Results;
using RazorForge.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for prohibited type constructions.
/// Blank cannot be used as a generic type argument anywhere.
/// Data? / Maybe&lt;Data&gt; is not allowed (Data already supports None).
/// </summary>
public class TypeProhibitionTests
{
    #region Blank as Type Argument (rejected)

    [Fact]
    public void Analyze_BlankNullable_ReportsError()
    {
        // Blank? desugars to Maybe<Blank>, which is prohibited
        string source = """
                        routine foo(x: Blank?) {
                            pass
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BlankAsTypeArgument);
    }

    [Fact]
    public void Analyze_ExplicitMaybeBlank_ReportsError()
    {
        string source = """
                        routine bar() -> Maybe<Blank> {
                            absent
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BlankAsTypeArgument);
    }

    #endregion

    #region Normal nullable types (allowed)

    [Fact]
    public void Analyze_NormalNullable_NoErrors()
    {
        string source = """
                        routine foo(x: S32?) {
                            pass
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BlankAsTypeArgument);
    }

    [Fact]
    public void Analyze_BlankDirectType_NoErrors()
    {
        // Blank as a direct type (not wrapped in a generic) is fine
        string source = """
                        routine foo() -> Blank {
                            pass
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BlankAsTypeArgument);
    }

    [Fact]
    public void Analyze_ResultBlank_NoErrors()
    {
        // Result<Blank> is allowed for failable void routines
        string source = """
                        routine foo(x: Result<Blank>) {
                            pass
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BlankAsTypeArgument);
    }

    [Fact]
    public void Analyze_LookupBlank_NoErrors()
    {
        // Lookup<Blank> is allowed for failable void routines
        string source = """
                        routine foo(x: Lookup<Blank>) {
                            pass
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BlankAsTypeArgument);
    }

    #endregion
}