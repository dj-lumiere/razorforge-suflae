using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for record fixed-size containment validation.
/// Records can only contain value types (records, choices, value tuples) and Snatched&lt;T&gt;.
/// Entities, wrappers (handles/tokens), and other reference types are not allowed.
/// </summary>
public class RecordContainmentTests
{
    #region Valid Record Fields (no errors expected)

    [Fact]
    public void Analyze_RecordWithPrimitiveFields_NoErrors()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }

    [Fact]
    public void Analyze_RecordWithRecordField_NoErrors()
    {
        string source = """
                        record Inner
                          value: S32
                        record Outer
                          inner: Inner
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }

    [Fact]
    public void Analyze_RecordWithChoiceField_NoErrors()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE
                        record Pixel
                          x: S32
                          y: S32
                          color: Color
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }

    [Fact]
    public void Analyze_GenericRecordWithTypeParameter_NoErrors()
    {
        // Generic type parameters are validated at instantiation time, not definition time
        string source = """
                        record Container[T]
                          value: T
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }

    [Fact]
    public void Analyze_GenericRecordMultipleTypeParams_NoErrors()
    {
        string source = """
                        record Pair[K, V]
                          key: K
                          value: V
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }

    #endregion

    #region Invalid Record Fields (errors expected)

    [Fact]
    public void Analyze_RecordWithEntityField_ReportsError()
    {
        string source = """
                        entity User
                          name: Text
                        record BadRecord
                          user: User
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType
                         && e.Message.Contains("user"));
    }

    [Fact]
    public void Analyze_RecordWithEntityField_MessageMentionsValueTypes()
    {
        string source = """
                        entity Connection
                          id: S32
                        record BadConfig
                          conn: Connection
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType
                         && e.Message.Contains("value type"));
    }

    [Fact]
    public void Analyze_RecordWithResidentField_ReportsError()
    {
        string source = """
                        resident GlobalState
                          counter: S32
                        record BadRecord
                          state: GlobalState
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType
                         && e.Message.Contains("state"));
    }

    #endregion

    #region With Expression on Non-Records

    [Fact]
    public void Analyze_WithOnEntity_ReportsError()
    {
        string source = """
                        entity Foo
                          x: S32
                        routine test(f: Foo)
                          var g = f with .x = 2
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WithExpressionNotRecord);
    }

    [Fact]
    public void Analyze_WithOnRecord_NoError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32
                        routine test(p: Point)
                          var q = p with .x = 2
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WithExpressionNotRecord);
    }

    [Fact]
    public void Analyze_WithOnRecordMultiField_NoError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32
                        routine test(p: Point)
                          var q = p with .x = 2, .y = 3
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WithExpressionNotRecord);
    }

    #endregion
}