using SemanticAnalysis.Diagnostics;
using SemanticAnalysis.Results;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for tuple type inference and analysis.
/// </summary>
public class TupleTests
{
    #region Type Inference
    /// <summary>
    /// Tests Analyze_InfersTuple.
    /// </summary>

    [Fact]
    public void Analyze_InfersTuple()
    {
        // All tuples are inline structs regardless of element types
        string source = """
                        routine test()
                          var tuple = (1_s32, 2_s32, 3_s32)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Tests Analyze_ContainsEntity_InfersTuple.
    /// </summary>

    [Fact]
    public void Analyze_ContainsEntity_InfersTuple()
    {
        // Entity fields stored as ptr in the tuple struct
        string source = """
                        entity Point
                          x: S32
                          y: S32

                        routine test()
                          var p = Point(x: 1, y: 2)
                          var tuple = (1, p)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Tests Analyze_NestedTuples_InfersCorrectly.
    /// </summary>

    [Fact]
    public void Analyze_NestedTuples_InfersCorrectly()
    {
        string source = """
                        routine test()
                          var nested = (1, (2, 3))
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Tests Analyze_SingleElementTuple_WithTrailingComma.
    /// </summary>

    [Fact]
    public void Analyze_SingleElementTuple_WithTrailingComma()
    {
        string source = """
                        routine test()
                          var single = (42,)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Tuple Type Category
    /// <summary>
    /// Tests Analyze_TupleType_NoErrors.
    /// </summary>

    [Fact]
    public void Analyze_TupleType_NoErrors()
    {
        // Tuple types are created in the instantiations cache
        // This test verifies tuple analysis succeeds without errors
        string source = """
                        routine test()
                          var tuple = (1s32, 2s32)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Mixed Types
    /// <summary>
    /// Tests Analyze_MixedNumericTypes_Works.
    /// </summary>

    [Fact]
    public void Analyze_MixedNumericTypes_Works()
    {
        string source = """
                        routine test()
                          var mixed = (1_s32, 2.5f64, true)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Tests Analyze_TupleWithEntity_InfersTuple.
    /// </summary>

    [Fact]
    public void Analyze_TupleWithEntity_InfersTuple()
    {
        string source = """
                        entity User
                          id: S32

                        routine test()
                          var user = User(id: 42)
                          var tuple = (1, user)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region For-Loop Destructuring
    /// <summary>
    /// Tests Analyze_ForLoopDestructuring_NonTuple_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_ForLoopDestructuring_NonTuple_ReportsError()
    {
        // Destructuring on non-tuple iterable (range produces integers, not tuples)
        string source = """
                        routine test()
                          for (a, b) in 0 til 10
                            var x = a
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DestructuringArityMismatch);
    }

    #endregion

    #region Suflae Syntax
    /// <summary>
    /// Tests AnalyzeSuflae_TupleLiteral_Works.
    /// </summary>

    [Fact]
    public void AnalyzeSuflae_TupleLiteral_Works()
    {
        string source = """
                        routine test():
                          var tuple = (1, 2, 3)
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Tests AnalyzeSuflae_NestedTuple_Works.
    /// </summary>

    [Fact]
    public void AnalyzeSuflae_NestedTuple_Works()
    {
        string source = """
                        routine test():
                          var nested = ((1, 2), (3, 4))
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region #173: Tuple assignment destructuring
    /// <summary>
    /// Tests Analyze_TupleAssignmentDestructuring_NoError.
    /// </summary>

    [Fact]
    public void Analyze_TupleAssignmentDestructuring_NoError()
    {
        string source = """
                        routine test()
                          var a = 1
                          var b = 2
                          (a, b) = (b, a)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Tests Analyze_TupleAssignmentNonAssignableTarget_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_TupleAssignmentNonAssignableTarget_ReportsError()
    {
        string source = """
                        routine test()
                          var a = 1
                          (a, 42) = (1, 2)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.InvalidAssignmentTarget);
    }

    #endregion
}
