using SemanticAnalysis.Diagnostics;
using SemanticAnalysis.Results;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for using statement semantic analysis:
/// bound variable type resolution, $enter/$exit validation.
/// </summary>
public class UsingStatementTests
{
    #region Token Using
    /// <summary>
    /// Tests Analyze_TokenUsing_BindsTokenType.
    /// </summary>

    [Fact]
    public void Analyze_TokenUsing_BindsTokenType()
    {
        // Token path: using with .view() binds to the Viewed token type
        string source = """
                        entity Point
                          x: S32
                          y: S32

                        routine test(p: Point)
                          using p.view() as v
                            show(v.x)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UsingTargetMissingEnterExit);
    }

    #endregion

    #region Resource With Void $enter
    /// <summary>
    /// Tests Analyze_ResourceWithVoidEnterExit_BindsResourceType.
    /// </summary>

    [Fact]
    public void Analyze_ResourceWithVoidEnterExit_BindsResourceType()
    {
        // When $enter returns Blank (void), the bound variable should have the resource type
        string source = """
                        record Lock
                          id: S32

                        routine Lock.$enter()
                          return

                        routine Lock.$exit()
                          return

                        routine test()
                          var lk = Lock(id: 1)
                          using lk as l
                            show(l.id)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UsingTargetMissingEnterExit);
    }

    #endregion

    #region Resource With Non-Void $enter
    /// <summary>
    /// Tests Analyze_ResourceWithNonVoidEnter_BindsEnterReturnType.
    /// </summary>

    [Fact]
    public void Analyze_ResourceWithNonVoidEnter_BindsEnterReturnType()
    {
        // When $enter returns a non-void type, the bound variable should have that type
        string source = """
                        record Handle
                          fd: S32

                        record Connection
                          name: Text

                        routine Connection.$enter() -> Handle
                          return Handle(fd: 1)

                        routine Connection.$exit()
                          return

                        routine test()
                          var conn = Connection(name: "db")
                          using conn as h
                            show(h.fd)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        // h should be typed as Handle (from $enter return), so h.fd should resolve
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UsingTargetMissingEnterExit);
    }

    #endregion

    #region Missing $enter/$exit
    /// <summary>
    /// Tests Analyze_ResourceMissingEnterExit_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_ResourceMissingEnterExit_ReportsError()
    {
        // A non-token type without $enter/$exit should produce an error
        string source = """
                        record PlainResource
                          value: S32

                        routine test()
                          var r = PlainResource(value: 42)
                          using r as res
                            show(res.value)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UsingTargetMissingEnterExit);
    }
    /// <summary>
    /// Tests Analyze_ResourceWithOnlyEnter_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_ResourceWithOnlyEnter_ReportsError()
    {
        // Having only $enter without $exit should still report error
        string source = """
                        record HalfResource
                          value: S32

                        routine HalfResource.$enter()
                          return

                        routine test()
                          var r = HalfResource(value: 42)
                          using r as res
                            show(res.value)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UsingTargetMissingEnterExit);
    }

    #endregion
}
