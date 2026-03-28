using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for BuilderService import-gating and routine availability.
/// </summary>
public class BuilderServiceTests
{
    #region Import Gating — Per-Type Routines

    [Fact]
    public void TypeName_WithoutImport_ReportsError()
    {
        string source = """
                        record Point
                          x: S64
                          y: S64

                        func test()
                          var p = Point(x: 1, y: 2)
                          var name = p.type_name()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderServiceImportRequired);
    }

    [Fact]
    public void TypeName_WithImport_NoError()
    {
        string source = """
                        import BuilderService

                        record Point
                          x: S64
                          y: S64

                        func test()
                          var p = Point(x: 1, y: 2)
                          var name = p.type_name()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderServiceImportRequired);
    }

    [Fact]
    public void DataSize_WithoutImport_ReportsError()
    {
        string source = """
                        record Pair
                          a: S64
                          b: S64

                        func test()
                          var p = Pair(a: 1, b: 2)
                          var sz = p.data_size()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderServiceImportRequired);
    }

    [Fact]
    public void DataSize_WithImport_NoError()
    {
        string source = """
                        import BuilderService

                        record Pair
                          a: S64
                          b: S64

                        func test()
                          var p = Pair(a: 1, b: 2)
                          var sz = p.data_size()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderServiceImportRequired);
    }

    #endregion

    #region Import Gating — Standalone Routines

    [Fact]
    public void SourceFile_WithoutImport_ReportsError()
    {
        string source = """
                        func test()
                          var f = source_file()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderServiceImportRequired);
    }

    [Fact]
    public void SourceFile_WithImport_NoError()
    {
        string source = """
                        import BuilderService

                        func test()
                          var f = source_file()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderServiceImportRequired);
    }

    [Fact]
    public void SourceLine_WithoutImport_ReportsError()
    {
        string source = """
                        func test()
                          var ln = source_line()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderServiceImportRequired);
    }

    [Fact]
    public void CallerFile_WithoutImport_ReportsError()
    {
        string source = """
                        func test()
                          var f = caller_file()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderServiceImportRequired);
    }

    #endregion

    #region Wired Routines NOT Gated

    [Fact]
    public void WiredRepresent_WithoutImport_NoError()
    {
        string source = """
                        record Point
                          x: S64
                          y: S64

                        func test()
                          var p = Point(x: 1, y: 2)
                          var s = f"{p}"
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderServiceImportRequired);
    }

    [Fact]
    public void WiredEq_WithoutImport_NoError()
    {
        string source = """
                        record Point
                          x: S64
                          y: S64

                        func test()
                          var a = Point(x: 1, y: 2)
                          var b = Point(x: 3, y: 4)
                          var eq = a == b
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderServiceImportRequired);
    }

    #endregion

    #region Multiple Routines With Import

    [Fact]
    public void MultipleRoutines_WithImport_AllAvailable()
    {
        string source = """
                        import BuilderService

                        record Pair
                          x: S64
                          y: S64

                        func test()
                          var p = Pair(x: 1, y: 2)
                          var name = p.type_name()
                          var kind = p.type_kind()
                          var tid = p.type_id()
                          var mod = p.module_name()
                          var gen = p.is_generic()
                          var sz = p.data_size()
                          var al = p.align_size()
                          var cnt = p.member_variable_count()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderServiceImportRequired);
    }

    [Fact]
    public void ListRoutines_WithImport_Available()
    {
        string source = """
                        import BuilderService

                        record Pair
                          x: S64
                          y: S64

                        func test()
                          var p = Pair(x: 1, y: 2)
                          var protos = p.protocols()
                          var names = p.routine_names()
                          var annots = p.annotations()
                          var gargs = p.generic_args()
                          var deps = p.dependencies()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderServiceImportRequired);
    }

    [Fact]
    public void StandaloneRoutines_WithImport_Available()
    {
        string source = """
                        import BuilderService

                        func test()
                          var f = source_file()
                          var r = source_routine()
                          var m = source_module()
                          var ln = source_line()
                          var col = source_column()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderServiceImportRequired);
    }

    #endregion
}
