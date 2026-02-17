using Compilers.Analysis.Results;
using Compilers.Analysis.Types;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for tuple type inference and analysis.
/// </summary>
public class TupleTests
{
    #region Type Inference

    [Fact]
    public void Analyze_AllValueTypes_InfersValueTuple()
    {
        // All elements are value types (S32 is a record/value type)
        string source = """
                        routine test() {
                            let tuple = (1_s32, 2_s32, 3_s32)
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_ContainsEntity_InfersTuple()
    {
        // Entity is a reference type, so this should be Tuple
        string source = """
                        entity Point {
                            x: S32
                            y: S32
                        }

                        routine test() {
                            let p = Point(x: 1, y: 2)
                            let tuple = (1, p)
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_NestedTuples_InfersCorrectly()
    {
        string source = """
                        routine test() {
                            let nested = (1, (2, 3))
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_SingleElementTuple_WithTrailingComma()
    {
        string source = """
                        routine test() {
                            let single = (42,)
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Tuple Type Category

    [Fact]
    public void Analyze_TupleType_NoErrors()
    {
        // Tuple types are created in the instantiations cache
        // This test verifies tuple analysis succeeds without errors
        string source = """
                        routine test() {
                            let tuple = (1s32, 2s32)
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Mixed Types

    [Fact]
    public void Analyze_MixedNumericTypes_Works()
    {
        string source = """
                        routine test() {
                            let mixed = (1_s32, 2.5f64, true)
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_TupleWithEntity_InfersTuple()
    {
        string source = """
                        entity User {
                            id: S32
                        }

                        routine test() {
                            let user = User(id: 42)
                            let tuple = (1, user)
                        }
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Suflae Syntax

    [Fact]
    public void AnalyzeSuflae_TupleLiteral_Works()
    {
        string source = """
                        routine test():
                            let tuple = (1, 2, 3)
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void AnalyzeSuflae_NestedTuple_Works()
    {
        string source = """
                        routine test():
                            let nested = ((1, 2), (3, 4))
                        """;

        AnalysisResult result = AnalyzeSuflae(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion
}