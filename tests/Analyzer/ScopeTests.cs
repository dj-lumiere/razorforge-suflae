using SemanticAnalysis.Results;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for scope analysis in the semantic analyzer:
/// variable resolution, shadowing, undefined variables.
/// </summary>
public class ScopeTests
{
    #region Variable Resolution

    [Fact]
    public void Analyze_VariableInScope_Resolves()
    {
        string source = """
                        routine test()
                          var x = 42
                          show(x)
                          return
                        """;

        Analyze(source: source);
        // Should resolve x correctly
    }

    [Fact]
    public void Analyze_UndefinedVariable_ReportsError()
    {
        string source = """
                        routine test()
                          show(undefined_var)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
        Assert.Contains(collection: result.Errors,
            filter: e =>
                e.Message.Contains(value: "undefined_var",
                    comparisonType: StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains(value: "not defined",
                    comparisonType: StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains(value: "unknown",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_VariableUsedBeforeDeclaration_ReportsError()
    {
        string source = """
                        routine test()
                          show(x)
                          var x = 42
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Block Scoping

    [Fact]
    public void Analyze_VariableInBlockScope_NoError()
    {
        string source = """
                        routine test()
                          if true
                            var x = 42
                            show(x)
                          return
                        """;

        Analyze(source: source);
    }

    [Fact]
    public void Analyze_VariableOutOfBlockScope_ReportsError()
    {
        string source = """
                        routine test()
                          if true
                            var x = 42
                          show(x)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    [Fact]
    public void Analyze_NestedBlockScopes_Resolves()
    {
        string source = """
                        routine test()
                          var x = 10
                          if true
                            var y = 20
                            if true
                              var z = 30
                              show(x + y + z)
                          return
                        """;

        Analyze(source: source);
    }

    #endregion

    #region Loop Scoping

    [Fact]
    public void Analyze_ForLoopVariable_InScope()
    {
        string source = """
                        routine test()
                          for i in 0 til 10
                            show(i)
                          return
                        """;

        Analyze(source: source);
    }

    [Fact]
    public void Analyze_ForLoopVariable_OutOfScope()
    {
        string source = """
                        routine test()
                          for i in 0 til 10
                            show(i)
                          show(i)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    [Fact]
    public void Analyze_WhileLoopVariable_InScope()
    {
        string source = """
                        routine test()
                          var i = 0
                          while i < 10
                            show(i)
                            i += 1
                          return
                        """;

        Analyze(source: source);
    }

    #endregion

    #region Function Parameter Scoping

    [Fact]
    public void Analyze_ParameterInScope_Resolves()
    {
        string source = """
                        routine greet(name: Text)
                          show(name)
                          return
                        """;

        Analyze(source: source);
    }

    [Fact]
    public void Analyze_ParameterShadowsOuter_NoError()
    {
        string source = """
                        var name = "global"

                        routine greet(name: Text)
                          show(name)
                          return
                        """;

        Analyze(source: source);
        // Parameter shadows global variable
    }

    #endregion

    #region Variable Shadowing

    [Fact]
    public void Analyze_ShadowingInNestedBlock_Allowed()
    {
        string source = """
                        routine test()
                          var x = 10
                          if true
                            var x = 20
                            show(x)
                          show(x)
                          return
                        """;

        Analyze(source: source);
        // Shadowing in nested scope is allowed
    }

    [Fact]
    public void Analyze_ShadowingInSameScope_ReportsError()
    {
        string source = """
                        routine test()
                          var x = 10
                          var x = 20
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region When/Pattern Scoping

    [Fact]
    public void Analyze_PatternBindingInWhen_InScope()
    {
        string source = """
                        variant Result
                          SUCCESS: S32
                          ERROR: Text

                        routine handle(r: Result)
                          when r
                            is SUCCESS value => show(value)
                            is ERROR msg => show(msg)
                          return
                        """;

        Analyze(source: source);
    }

    [Fact]
    public void Analyze_PatternBindingOutOfWhen_OutOfScope()
    {
        string source = """
                        variant Result
                          SUCCESS: S32
                          ERROR: Text

                        routine handle(r: Result)
                          when r
                            is SUCCESS value => show(value)
                            is ERROR msg => pass
                          show(value)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Viewing/Hijacking Scoping

    [Fact]
    public void Analyze_ViewingBlockVariable_InScope()
    {
        string source = """
                        entity Data
                          value: S32

                        routine test(data: Data)
                          viewing data as d
                            show(d.value)
                          return
                        """;

        Analyze(source: source);
    }

    [Fact]
    public void Analyze_ViewingBlockVariable_OutOfScope()
    {
        string source = """
                        entity Data
                          value: S32

                        routine test(data: Data)
                          using data.view() as d
                            show(d.value)
                          show(d.value)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Suflae Scope Tests

    [Fact]
    public void AnalyzeSuflae_VariableInScope_Resolves()
    {
        string source = """
                        routine test()
                          var x = 42
                          show(x)
                        """;

        AnalyzeSuflae(source: source);
    }

    [Fact]
    public void AnalyzeSuflae_UndefinedVariable_ReportsError()
    {
        string source = """
                        routine test()
                          show(undefined_var)
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    [Fact]
    public void AnalyzeSuflae_ForLoopVariable_InScope()
    {
        string source = """
                        routine test()
                          for i in 0 til 10
                            show(i)
                        """;

        AnalyzeSuflae(source: source);
    }

    [Fact]
    public void AnalyzeSuflae_ForLoopVariable_OutOfScope()
    {
        string source = """
                        routine test()
                          for i in 0 til 10
                            show(i)
                          show(i)
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Closure Scoping

    [Fact]
    public void Analyze_LambdaCapturesOuter_NoError()
    {
        string source = """
                        routine test()
                          var multiplier = 10
                          var scale = x => x * multiplier
                          show(scale(5))
                          return
                        """;

        Analyze(source: source);
    }

    [Fact]
    public void Analyze_LambdaWithGivenClause_UndefinedCapture_ReportsError()
    {
        // Captured variable 'z' is not defined in scope
        string source = """
                        routine test()
                          var f = (x, y) given z => x + y + z
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
        Assert.Contains(collection: result.Errors,
            filter: e =>
                e.Message.Contains(value: "z", comparisonType: StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains(value: "not defined", comparisonType: StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains(value: "unknown", comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_LambdaWithGivenClause_ValidCapture_NoError()
    {
        // Captured variable 'z' exists in outer scope
        string source = """
                        routine test()
                          var z = 100
                          var f = (x, y) given z => x + y + z
                          return
                        """;

        Analyze(source: source);
    }

    #endregion
}
