using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for collection literal type inference from variable annotations.
/// </summary>
public class CollectionLiteralTests
{
    /// <summary>
    /// Tests EmptyListLiteral_WithTypeAnnotation_InfersElementType.
    /// </summary>
    [Fact]
    public void EmptyListLiteral_WithTypeAnnotation_InfersElementType()
    {
        string source = """
                        routine test()
                          var items: List[S64] = []
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Tests EmptyListLiteral_WithoutTypeAnnotation_ReportsError.
    /// </summary>

    [Fact]
    public void EmptyListLiteral_WithoutTypeAnnotation_ReportsError()
    {
        string source = """
                        routine test()
                          var items = []
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.EmptyListNoTypeAnnotation);
    }
    /// <summary>
    /// Tests EmptySetLiteral_WithTypeAnnotation_InfersElementType.
    /// </summary>

    [Fact]
    public void EmptySetLiteral_WithTypeAnnotation_InfersElementType()
    {
        string source = """
                        routine test()
                          var items: Set[S64] = {}
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Tests EmptySetLiteral_WithoutTypeAnnotation_ReportsError.
    /// </summary>

    [Fact]
    public void EmptySetLiteral_WithoutTypeAnnotation_ReportsError()
    {
        string source = """
                        routine test()
                          var items = {}
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.EmptySetNoTypeAnnotation);
    }
    /// <summary>
    /// Tests EmptyDictLiteral_WithTypeAnnotation_InfersKeyValueTypes.
    /// </summary>

    [Fact]
    public void EmptyDictLiteral_WithTypeAnnotation_InfersKeyValueTypes()
    {
        string source = """
                        routine test()
                          var items: Dict[S64, Text] = {:}
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Tests EmptyDictLiteral_WithoutTypeAnnotation_ReportsError.
    /// </summary>

    [Fact]
    public void EmptyDictLiteral_WithoutTypeAnnotation_ReportsError()
    {
        string source = """
                        routine test()
                          var items = {:}
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.EmptyDictNoTypeAnnotation);
    }
    /// <summary>
    /// Tests NonEmptySetLiteral_InfersFromElements.
    /// </summary>

    [Fact]
    public void NonEmptySetLiteral_InfersFromElements()
    {
        string source = """
                        routine test()
                          var items = {1, 2, 3}
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Tests NonEmptyDictLiteral_InfersFromElements.
    /// </summary>

    [Fact]
    public void NonEmptyDictLiteral_InfersFromElements()
    {
        string source = """
                        routine test()
                          var items = {1: "one", 2: "two"}
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }
}
