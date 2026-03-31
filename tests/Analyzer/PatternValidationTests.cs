using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for pattern validation in semantic analysis:
/// variable shadowing, scope isolation, type compatibility.
/// </summary>
public class PatternValidationTests
{
    #region Variable Shadowing
    /// <summary>
    /// Tests Analyze_TypePattern_ShadowsOuterVariable_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_TypePattern_ShadowsOuterVariable_ReportsError()
    {
        string source = """
                        routine test()
                          var x: S32 = 5
                          when x
                            is S32 x => show(x)
                            else => pass
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.IdentifierShadowing);
    }
    /// <summary>
    /// Tests Analyze_ElsePattern_ShadowsOuterVariable_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_ElsePattern_ShadowsOuterVariable_ReportsError()
    {
        string source = """
                        routine test()
                          var value: S32 = 5
                          when value
                            is S32 n => show(n)
                            else value => show(value)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.IdentifierShadowing);
    }
    /// <summary>
    /// Tests Analyze_TypePattern_UniqueVariableName_NoShadowingError.
    /// </summary>

    [Fact]
    public void Analyze_TypePattern_UniqueVariableName_NoShadowingError()
    {
        string source = """
                        routine test()
                          var x: S32 = 5
                          when x
                            is S32 n => show(n)
                            else => pass
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.IdentifierShadowing);
    }
    /// <summary>
    /// Tests Analyze_TypePattern_SameNameDifferentClauses_NoShadowingError.
    /// </summary>

    [Fact]
    public void Analyze_TypePattern_SameNameDifferentClauses_NoShadowingError()
    {
        string source = """
                        routine test()
                          var x: S32 = 5
                          when x
                            is S32 n => show(n)
                            else n => show(n)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.IdentifierShadowing);
    }

    #endregion

    #region Scope Isolation
    /// <summary>
    /// Tests Analyze_WhenExpression_ClauseScopesIsolated.
    /// </summary>

    [Fact]
    public void Analyze_WhenExpression_ClauseScopesIsolated()
    {
        string source = """
                        routine test() -> S32
                          var x: S32 = 5
                          var result: S32 = when x
                            is S32 n => n
                            else => 0
                          return result
                        """;

        AnalysisResult result = Analyze(source: source);
        // Pattern variables should not leak between clauses or outside the when expression
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.IdentifierShadowing);
    }

    #endregion

    #region Type Compatibility
    /// <summary>
    /// Tests Analyze_TypePattern_CompatibleType_NoError.
    /// </summary>

    [Fact]
    public void Analyze_TypePattern_CompatibleType_NoError()
    {
        string source = """
                        routine test()
                          var x: S32 = 5
                          when x
                            is S32 n => show(n)
                            else => pass
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PatternTypeMismatch);
    }
    /// <summary>
    /// Tests Analyze_TypePattern_IncompatibleType_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_TypePattern_IncompatibleType_ReportsError()
    {
        string source = """
                        record Foo
                          x: S32
                        record Bar
                          y: S32
                        routine test()
                          var f = Foo(x: 1)
                          when f
                            is Bar b => pass
                            else => pass
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PatternTypeMismatch);
    }

    #endregion
}
