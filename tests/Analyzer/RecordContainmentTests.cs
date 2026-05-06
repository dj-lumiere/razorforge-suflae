using Compiler.Diagnostics;
using Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for record containment.
/// </summary>
public class RecordContainmentTests
{
    #region Valid Record MemberVariables (no errors expected)
    /// <summary>
    /// Verifies semantic analysis behavior for record with primitive member variables without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_RecordWithPrimitiveMemberVariables_NoErrors()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for record with record member variable without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_RecordWithRecordMemberVariable_NoErrors()
    {
        string source = """
                        record Inner
                          value: S32
                        record Outer
                          inner: Inner
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for record with choice member variable without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_RecordWithChoiceMemberVariable_NoErrors()
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

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for generic record with type parameter without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_GenericRecordWithTypeParameter_NoErrors()
    {
        // Generic type parameters are validated at instantiation time, not definition time
        string source = """
                        record Container[T]
                          value: T
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for generic record multiple type params without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_GenericRecordMultipleTypeParams_NoErrors()
    {
        string source = """
                        record Pair[K, V]
                          key: K
                          value: V
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for record with retained field without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_RecordWithRetainedField_NoErrors()
    {
        string source = """
                        entity Node
                          value: S32
                        record Handle
                          ref: Retained[Node]
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for record with shared field without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_RecordWithSharedField_NoErrors()
    {
        string source = """
                        entity Node
                          value: S32
                        record Handle
                          ref: Shared[Node]
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for record with hijacked field without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_RecordWithHijackedField_NoErrors()
    {
        string source = """
                        entity Node
                          value: S32
                        record RawHandle
                          ptr: Hijacked[Node]
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for nested record with retained field without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_NestedRecordWithRetainedField_NoErrors()
    {
        string source = """
                        entity Node
                          value: S32
                        record Inner
                          ref: Retained[Node]
                        record Outer
                          inner: Inner
                          count: S32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }

    #endregion

    #region Invalid Record MemberVariables (errors expected)
    /// <summary>
    /// Verifies semantic analysis behavior for record with entity member variable and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_RecordWithEntityMemberVariable_ReportsError()
    {
        string source = """
                        entity User
                          name: Text
                        record BadRecord
                          user: User
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType
                         && e.Message.Contains("user"));
    }
    /// <summary>
    /// Verifies semantic analysis behavior for record with entity member variable message mentions value types.
    /// </summary>

    [Fact]
    public void Analyze_RecordWithEntityMemberVariable_MessageMentionsValueTypes()
    {
        string source = """
                        entity Connection
                          id: S32
                        record BadConfig
                          conn: Connection
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType
                         && e.Message.Contains("value type"));
    }

    /// <summary>
    /// Verifies semantic analysis behavior for record with viewed field and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_RecordWithViewedField_ReportsError()
    {
        // Scoped tokens are caught by S601 (TokenMemberVariableNotAllowed) before S412
        string source = """
                        entity Node
                          value: S32
                        record BadRecord
                          view: Viewed[Node]
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TokenMemberVariableNotAllowed
                         && e.Message.Contains("view"));
    }

    /// <summary>
    /// Verifies semantic analysis behavior for record with grasped field and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_RecordWithGraspedField_ReportsError()
    {
        // Scoped tokens are caught by S601 (TokenMemberVariableNotAllowed) before S412
        string source = """
                        entity Node
                          value: S32
                        record BadRecord
                          grasped: Grasped[Node]
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TokenMemberVariableNotAllowed
                         && e.Message.Contains("grasped"));
    }

    #endregion

    #region With Expression on Non-Records
    /// <summary>
    /// Verifies semantic analysis behavior for with on entity and reports the expected error.
    /// </summary>

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

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WithExpressionNotRecord);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for with on record without unexpected diagnostics.
    /// </summary>

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

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WithExpressionNotRecord);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for with on record multi member variable without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_WithOnRecordMultiMemberVariable_NoError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32
                        routine test(p: Point)
                          var q = p with .x = 2, .y = 3
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WithExpressionNotRecord);
    }

    #endregion
}
