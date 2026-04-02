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
    [Fact]
    public void ListLiteral_WithDequeAnnotation_RetargetsToDeque()
    {
        string source = """
                        routine test()
                          var items: Deque[S64] = [1, 2, 3]
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void ListLiteral_WithSortedListAnnotation_RetargetsToSortedList()
    {
        string source = """
                        routine test()
                          var items: SortedList[S64] = [3, 1, 2]
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void SetLiteral_WithSortedSetAnnotation_RetargetsToSortedSet()
    {
        string source = """
                        routine test()
                          var items: SortedSet[S64] = {3, 1, 2}
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void DictLiteral_WithSortedDictAnnotation_RetargetsToSortedDict()
    {
        string source = """
                        routine test()
                          var items: SortedDict[S32, S32] = {3: 30, 1: 10, 2: 20}
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void DictLiteral_WithPriorityQueueAnnotation_RetargetsToPriorityQueue()
    {
        string source = """
                        routine test()
                          var items: PriorityQueue[S64, Text] = {1: "high", 10: "low"}
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void ListLiteral_WithValueListAnnotation_RetargetsToValueList()
    {
        string source = """
                        routine test()
                          var items: ValueList[S64, 4] = [1, 2, 3, 4]
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void ListLiteral_WithValueBitListAnnotation_RetargetsToValueBitList()
    {
        string source = """
                        routine test()
                          var items: ValueBitList[8] = [true, false, true, true, false, true, false, true]
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void ValueListLiteral_WithWrongArity_ReportsError()
    {
        string source = """
                        routine test()
                          var items: ValueList[S64, 4] = [1, 2, 3, 4, 5]
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArgumentCountMismatch);
    }

    [Fact]
    public void ValueBitListLiteral_WithWrongArity_ReportsError()
    {
        string source = """
                        routine test()
                          var items: ValueBitList[4] = [true, false, true]
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArgumentCountMismatch);
    }
}
