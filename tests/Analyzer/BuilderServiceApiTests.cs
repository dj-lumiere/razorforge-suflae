using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Full API tests for BuilderService — verifies every per-type routine, standalone routine,
/// and platform/build info routine resolves correctly with 'import BuilderService'.
/// Also verifies import-gating for each category and that wired routines are unaffected.
/// </summary>
public class BuilderServiceApiTests
{
    #region Per-Type Routines — Records
    /// <summary>
    /// Tests Record_TypeName_Available.
    /// </summary>

    [Fact]
    public void Record_TypeName_Available()
    {
        AssertAnalyzes("""
                       import BuilderService

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.type_name()
                         return
                       """);
    }
    /// <summary>
    /// Tests Record_TypeKind_Available.
    /// </summary>

    [Fact]
    public void Record_TypeKind_Available()
    {
        AssertAnalyzes("""
                       import BuilderService

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.type_kind()
                         return
                       """);
    }
    /// <summary>
    /// Tests Record_TypeId_Available.
    /// </summary>

    [Fact]
    public void Record_TypeId_Available()
    {
        AssertAnalyzes("""
                       import BuilderService

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.type_id()
                         return
                       """);
    }
    /// <summary>
    /// Tests Record_ModuleName_Available.
    /// </summary>

    [Fact]
    public void Record_ModuleName_Available()
    {
        AssertAnalyzes("""
                       import BuilderService

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.module_name()
                         return
                       """);
    }
    /// <summary>
    /// Tests Record_IsGeneric_Available.
    /// </summary>

    [Fact]
    public void Record_IsGeneric_Available()
    {
        AssertAnalyzes("""
                       import BuilderService

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.is_generic()
                         return
                       """);
    }
    /// <summary>
    /// Tests Record_DataSize_Available.
    /// </summary>

    [Fact]
    public void Record_DataSize_Available()
    {
        AssertAnalyzes("""
                       import BuilderService

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.data_size()
                         return
                       """);
    }
    /// <summary>
    /// Tests Record_MemberVariableCount_Available.
    /// </summary>

    [Fact]
    public void Record_MemberVariableCount_Available()
    {
        AssertAnalyzes("""
                       import BuilderService

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.member_variable_count()
                         return
                       """);
    }
    /// <summary>
    /// Tests Record_GenericArgs_Available.
    /// </summary>

    [Fact]
    public void Record_GenericArgs_Available()
    {
        AssertAnalyzes("""
                       import BuilderService

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.generic_args()
                         return
                       """);
    }
    /// <summary>
    /// Tests Record_Protocols_Available.
    /// </summary>

    [Fact]
    public void Record_Protocols_Available()
    {
        AssertAnalyzes("""
                       import BuilderService

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.protocols()
                         return
                       """);
    }
    /// <summary>
    /// Tests Record_RoutineNames_Available.
    /// </summary>

    [Fact]
    public void Record_RoutineNames_Available()
    {
        AssertAnalyzes("""
                       import BuilderService

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.routine_names()
                         return
                       """);
    }
    /// <summary>
    /// Tests Record_Annotations_Available.
    /// </summary>

    [Fact]
    public void Record_Annotations_Available()
    {
        AssertAnalyzes("""
                       import BuilderService

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.annotations()
                         return
                       """);
    }
    /// <summary>
    /// Tests Record_Dependencies_Available.
    /// </summary>

    [Fact]
    public void Record_Dependencies_Available()
    {
        AssertAnalyzes("""
                       import BuilderService

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.dependencies()
                         return
                       """);
    }
    /// <summary>
    /// Tests Record_OriginModule_Available.
    /// </summary>

    [Fact]
    public void Record_OriginModule_Available()
    {
        AssertAnalyzes("""
                       import BuilderService

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.full_type_name()
                         return
                       """);
    }

    #endregion

    #region Per-Type Routines — Entities
    /// <summary>
    /// Tests Entity_TypeName_Available.
    /// </summary>

    [Fact]
    public void Entity_TypeName_Available()
    {
        AssertAnalyzes("""
                       import BuilderService

                       entity Counter
                         value: S64

                       routine test()
                         var c = Counter(value: 0)
                         var v = c.type_name()
                         return
                       """);
    }
    /// <summary>
    /// Tests Entity_TypeKind_Available.
    /// </summary>

    [Fact]
    public void Entity_TypeKind_Available()
    {
        AssertAnalyzes("""
                       import BuilderService

                       entity Counter
                         value: S64

                       routine test()
                         var c = Counter(value: 0)
                         var v = c.type_kind()
                         return
                       """);
    }
    /// <summary>
    /// Tests Entity_MemberVariableCount_Available.
    /// </summary>

    [Fact]
    public void Entity_MemberVariableCount_Available()
    {
        AssertAnalyzes("""
                       import BuilderService

                       entity Counter
                         value: S64

                       routine test()
                         var c = Counter(value: 0)
                         var v = c.member_variable_count()
                         return
                       """);
    }

    #endregion

    #region Per-Type Routines — Choices
    /// <summary>
    /// Tests Choice_TypeName_Available.
    /// </summary>

    [Fact]
    public void Choice_TypeName_Available()
    {
        AssertAnalyzes("""
                       import BuilderService

                       choice Color
                         RED
                         GREEN
                         BLUE

                       routine test()
                         var c = Color.RED
                         var v = c.type_name()
                         return
                       """);
    }
    /// <summary>
    /// Tests Choice_TypeKind_Available.
    /// </summary>

    [Fact]
    public void Choice_TypeKind_Available()
    {
        AssertAnalyzes("""
                       import BuilderService

                       choice Color
                         RED
                         GREEN
                         BLUE

                       routine test()
                         var c = Color.RED
                         var v = c.type_kind()
                         return
                       """);
    }

    #endregion

    #region Per-Type Routines — Import Gating (every routine without import)
    /// <summary>
    /// Tests PerTypeRoutine_WithoutImport_ReportsError.
    /// </summary>

    [Theory]
    [InlineData("type_name")]
    [InlineData("type_kind")]
    [InlineData("type_id")]
    [InlineData("module_name")]
    [InlineData("is_generic")]
    [InlineData("data_size")]
    [InlineData("member_variable_count")]
    [InlineData("generic_args")]
    [InlineData("protocols")]
    [InlineData("routine_names")]
    [InlineData("annotations")]
    [InlineData("dependencies")]
    [InlineData("full_type_name")]
    public void PerTypeRoutine_WithoutImport_ReportsError(string routineName)
    {
        string source = $$"""
                          record Pair
                            a: S64
                            b: S64

                          routine test()
                            var p = Pair(a: 1, b: 2)
                            var v = p.{{routineName}}()
                            return
                          """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderServiceImportRequired);
    }
    /// <summary>
    /// Tests PerTypeRoutine_WithImport_NoError.
    /// </summary>

    [Theory]
    [InlineData("type_name")]
    [InlineData("type_kind")]
    [InlineData("type_id")]
    [InlineData("module_name")]
    [InlineData("is_generic")]
    [InlineData("data_size")]
    [InlineData("member_variable_count")]
    [InlineData("generic_args")]
    [InlineData("protocols")]
    [InlineData("routine_names")]
    [InlineData("annotations")]
    [InlineData("dependencies")]
    [InlineData("full_type_name")]
    public void PerTypeRoutine_WithImport_NoError(string routineName)
    {
        string source = $$"""
                          import BuilderService

                          record Pair
                            a: S64
                            b: S64

                          routine test()
                            var p = Pair(a: 1, b: 2)
                            var v = p.{{routineName}}()
                            return
                          """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderServiceImportRequired);
    }

    #endregion

    #region Standalone Routines — Source Location
    /// <summary>
    /// Tests SourceLocationRoutine_WithoutImport_ReportsError.
    /// </summary>

    [Theory]
    [InlineData("source_file")]
    [InlineData("source_line")]
    [InlineData("source_column")]
    [InlineData("source_routine")]
    [InlineData("source_module")]
    [InlineData("source_text")]
    [InlineData("caller_file")]
    [InlineData("caller_line")]
    [InlineData("caller_routine")]
    public void SourceLocationRoutine_WithoutImport_ReportsError(string routineName)
    {
        string source = $$"""
                          routine test()
                            var v = {{routineName}}()
                            return
                          """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderServiceImportRequired);
    }
    /// <summary>
    /// Tests SourceLocationRoutine_WithImport_NoError.
    /// </summary>

    [Theory]
    [InlineData("source_file")]
    [InlineData("source_line")]
    [InlineData("source_column")]
    [InlineData("source_routine")]
    [InlineData("source_module")]
    [InlineData("source_text")]
    [InlineData("caller_file")]
    [InlineData("caller_line")]
    [InlineData("caller_routine")]
    public void SourceLocationRoutine_WithImport_NoError(string routineName)
    {
        string source = $$"""
                          import BuilderService

                          routine test()
                            var v = {{routineName}}()
                            return
                          """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderServiceImportRequired);
    }

    #endregion

    #region Standalone Routines — Platform/Build Info
    /// <summary>
    /// Tests PlatformRoutine_WithoutImport_ReportsError.
    /// </summary>

    [Theory]
    [InlineData("target_os")]
    [InlineData("target_arch")]
    [InlineData("builder_version")]
    [InlineData("build_timestamp")]
    [InlineData("build_mode")]
    [InlineData("page_size")]
    [InlineData("cache_line")]
    [InlineData("word_size")]
    public void PlatformRoutine_WithoutImport_ReportsError(string routineName)
    {
        string source = $$"""
                          routine test()
                            var v = {{routineName}}()
                            return
                          """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderServiceImportRequired);
    }
    /// <summary>
    /// Tests PlatformRoutine_WithImport_NoError.
    /// </summary>

    [Theory]
    [InlineData("target_os")]
    [InlineData("target_arch")]
    [InlineData("builder_version")]
    [InlineData("build_timestamp")]
    [InlineData("build_mode")]
    [InlineData("page_size")]
    [InlineData("cache_line")]
    [InlineData("word_size")]
    public void PlatformRoutine_WithImport_NoError(string routineName)
    {
        string source = $$"""
                          import BuilderService

                          routine test()
                            var v = {{routineName}}()
                            return
                          """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderServiceImportRequired);
    }

    #endregion

    #region Wired Routines — Never Gated
    /// <summary>
    /// Tests WiredRoutine_WithoutImport_NoError.
    /// </summary>

    [Theory]
    [InlineData("$represent", """var s = f"{p}" """)]
    [InlineData("$diagnose", """var s = f"{p:?}" """)]
    [InlineData("$eq", "var eq = p == q")]
    [InlineData("$ne", "var ne = p != q")]
    [InlineData("$hash", """var h = {p}""")]
    public void WiredRoutine_WithoutImport_NoError(string description, string usage)
    {
        string source = $$"""
                          record Point
                            x: S64
                            y: S64

                          routine test()
                            var p = Point(x: 1, y: 2)
                            var q = Point(x: 3, y: 4)
                            {{usage}}
                            return
                          """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderServiceImportRequired);
    }

    #endregion

    #region Multiple Routines Combined
    /// <summary>
    /// Tests AllPerTypeRoutines_OnRecord_Available.
    /// </summary>

    [Fact]
    public void AllPerTypeRoutines_OnRecord_Available()
    {
        AssertAnalyzes("""
                       import BuilderService

                       record Pair
                         x: S64
                         y: S64

                       routine test()
                         var p = Pair(x: 1, y: 2)
                         var a = p.type_name()
                         var b = p.type_kind()
                         var c = p.type_id()
                         var d = p.module_name()
                         var e = p.is_generic()
                         var f = p.data_size()
                         var g = p.member_variable_count()
                         var h = p.generic_args()
                         var i = p.protocols()
                         var j = p.routine_names()
                         var k = p.annotations()
                         var l = p.dependencies()
                         var m = p.full_type_name()
                         return
                       """);
    }
    /// <summary>
    /// Tests AllStandaloneRoutines_Available.
    /// </summary>

    [Fact]
    public void AllStandaloneRoutines_Available()
    {
        AssertAnalyzes("""
                       import BuilderService

                       routine test()
                         var a = source_file()
                         var b = source_line()
                         var c = source_column()
                         var d = source_routine()
                         var e = source_module()
                         var f = source_text()
                         var g = caller_file()
                         var h = caller_line()
                         var i = caller_routine()
                         var j = target_os()
                         var k = target_arch()
                         var l = builder_version()
                         var m = build_timestamp()
                         var n = build_mode()
                         var o = page_size()
                         var p = cache_line()
                         var q = word_size()
                         return
                       """);
    }
    /// <summary>
    /// Tests MixedPerTypeAndStandalone_Available.
    /// </summary>

    [Fact]
    public void MixedPerTypeAndStandalone_Available()
    {
        AssertAnalyzes("""
                       import BuilderService

                       record Pair
                         a: S64
                         b: S64

                       routine test()
                         var p = Pair(a: 1, b: 2)
                         var name = p.type_name()
                         var file = source_file()
                         var os = target_os()
                         var sz = p.data_size()
                         var line = source_line()
                         return
                       """);
    }

    #endregion

    #region data_size — RazorForge Only
    /// <summary>
    /// Tests DataSize_InSuflae_NotAvailable.
    /// </summary>

    [Fact]
    public void DataSize_InSuflae_NotAvailable()
    {
        string source = """
                        import BuilderService

                        record Pair
                          a: S64
                          b: S64

                        func test()
                          let p = Pair(a: 1, b: 2)
                          let sz = p.data_size()
                          return
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        // data_size should not be registered for Suflae, so it should produce an error
        Assert.True(result.Errors.Count > 0,
            "Expected data_size() to be unavailable in Suflae");
    }

    #endregion

    #region Per-Type Routines — On Different Type Kinds
    /// <summary>
    /// Tests Entity_AllMetadata_Available.
    /// </summary>

    [Fact]
    public void Entity_AllMetadata_Available()
    {
        AssertAnalyzes("""
                       import BuilderService

                       entity Node
                         value: S64

                       routine test()
                         var n = Node(value: 42)
                         var a = n.type_name()
                         var b = n.type_kind()
                         var c = n.type_id()
                         var d = n.module_name()
                         var e = n.is_generic()
                         var f = n.member_variable_count()
                         var g = n.protocols()
                         var h = n.routine_names()
                         return
                       """);
    }
    /// <summary>
    /// Tests Choice_AllMetadata_Available.
    /// </summary>

    [Fact]
    public void Choice_AllMetadata_Available()
    {
        AssertAnalyzes("""
                       import BuilderService

                       choice Direction
                         NORTH
                         SOUTH
                         EAST
                         WEST

                       routine test()
                         var d = Direction.NORTH
                         var a = d.type_name()
                         var b = d.type_kind()
                         var c = d.type_id()
                         var e = d.module_name()
                         return
                       """);
    }

    #endregion
}
