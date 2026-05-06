using Compiler.Diagnostics;
using Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for using statement.
/// </summary>
public class UsingStatementTests
{
    #region Token Using
    /// <summary>
    /// Verifies semantic analysis behavior for token using and binds the expected token type.
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
                            var a: S32 = v.x
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Resource With Void $enter
    /// <summary>
    /// Verifies semantic analysis behavior for resource with void enter exit and binds the expected resource type.
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
                            var a: S32 = l.id
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Resource With Non-Void $enter
    /// <summary>
    /// Verifies semantic analysis behavior for resource with non void enter and binds the enter return type.
    /// </summary>

    [Fact]
    public void Analyze_ResourceWithNonVoidEnter_BindsEnterReturnType()
    {
        // When $enter returns a non-void type, the bound variable should have that type
        string source = """
                        record Handle
                          fd: S32

                        entity Connection
                          tag: S32

                        routine Connection.$enter() -> Handle
                          return Handle(fd: 1)

                        routine Connection.$exit()
                          return

                        routine test()
                          var conn = Connection(tag: 1)
                          using conn as h
                            var a: S32 = h.fd
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        // h should be typed as Handle (from $enter return), so h.fd should resolve
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Missing $enter/$exit
    /// <summary>
    /// Verifies semantic analysis behavior for resource missing enter exit and reports the expected error.
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
                            var a: S32 = res.value
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UsingTargetMissingEnterExit);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for resource with only enter and reports the expected error.
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
                            var a: S32 = res.value
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UsingTargetMissingEnterExit);
    }

    #endregion

    #region Generic Using

    /// <summary>
    /// Verifies semantic analysis behavior for generic using and binds the viewed wrapper type.
    /// </summary>
    [Fact]
    public void Analyze_GenericUsing_BindsViewedType()
    {
        // using on a generic resolution type (e.g., Container[Point].$enter)
        // should bind the inner type and allow member access
        string source = """
                        record Point
                          x: S32
                          y: S32

                        entity Container[T]
                          item: T

                        routine Container[T].$enter() -> T
                          return me.item

                        routine Container[T].$exit()
                          return

                        routine test()
                          var c = Container[Point](item: Point(x: 1, y: 2))
                          using c as p
                            var a: S32 = p.x
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion
}
