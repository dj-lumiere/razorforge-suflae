using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for operator validation rules:
/// #66: Index operator type-kind restriction
/// #67: Compound assignment on read-only tokens
/// #117: Fixed-width numeric type mismatch
/// #119: BackIndex in Range restriction
/// </summary>
public class OperatorValidationTests
{
    #region #66: Index operator type-kind restriction

    [Fact]
    public void Analyze_IndexOperatorOnEntity_NoError()
    {
        string source = """
                        protocol Indexable
                          @readonly
                          routine Me.__getitem__(index: S32) -> S32
                        entity Grid obeys Indexable
                          size: S32
                        @readonly
                        routine Grid.__getitem__(index: S32) -> S32
                          return 0_s32
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.IndexOperatorTypeKindRestriction);
    }

    [Fact]
    public void Analyze_IndexOperatorOnRecord_ReportsError()
    {
        string source = """
                        protocol Indexable
                          @readonly
                          routine Me.__getitem__(index: S32) -> S32
                        record Pair obeys Indexable
                          x: S32
                          y: S32
                        @readonly
                        routine Pair.__getitem__(index: S32) -> S32
                          return 0_s32
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.IndexOperatorTypeKindRestriction);
    }

    #endregion

    #region #117: Fixed-width numeric type mismatch

    [Fact]
    public void Analyze_SameFixedWidthArithmetic_NoError()
    {
        string source = """
                        protocol Addable
                          @readonly
                          routine Me.$add(other: Me) -> Me
                        routine test(a: S32, b: S32) -> S32
                          return a + b
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FixedWidthTypeMismatch);
    }

    [Fact]
    public void Analyze_MixedFixedWidthArithmetic_ReportsError()
    {
        string source = """
                        routine test(a: S32, b: S64) -> S64
                          return a + b
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FixedWidthTypeMismatch);
    }

    #endregion

    #region #119: BackIndex in Range restriction

    [Fact]
    public void Analyze_BackIndexInRange_ReportsError()
    {
        string source = """
                        routine test()
                          var r = (^1 to 10)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BackIndexOutsideSubscript);
    }

    [Fact]
    public void Analyze_BackIndexInSlice_NoError()
    {
        string source = """
                        routine test(list: Sequence[S32])
                          var x = list[^1]
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BackIndexOutsideSubscript);
    }

    #endregion
}
