using SemanticVerification.Results;
using Compiler.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for routine declaration validation rules:
/// #151: Static/instance mismatch
/// #157: Mutation category conflict
/// </summary>
public class RoutineValidationTests
{
    #region #157: Mutation category conflict
    /// <summary>
    /// Tests Analyze_SingleMutationAnnotation_NoError.
    /// </summary>

    [Fact]
    public void Analyze_SingleMutationAnnotation_NoError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32
                        @readonly
                        routine Point.get_x() -> S32
                          return me.x
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MutationCategoryConflict);
    }
    /// <summary>
    /// Tests Analyze_ConflictingMutationAnnotations_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_ConflictingMutationAnnotations_ReportsError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32
                        @readonly
                        @writable
                        routine Point.set_x(new_x: S32)
                          me.x = new_x
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MutationCategoryConflict);
    }

    #endregion

    #region #151: Static/instance mismatch
    /// <summary>
    /// Tests Analyze_CommonRoutineCalledOnInstance_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_CommonRoutineCalledOnInstance_ReportsError()
    {
        string source = """
                        record Counter
                          value: S32
                        common routine Counter.create() -> Counter
                          return Counter(value: 0)

                        routine test()
                          var c = Counter(value: 0)
                          var d = c.create()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CommonRoutineMismatch);
    }
    /// <summary>
    /// Tests Analyze_CommonRoutineCalledOnType_NoError.
    /// </summary>

    [Fact]
    public void Analyze_CommonRoutineCalledOnType_NoError()
    {
        string source = """
                        record Counter
                          value: S32
                        common routine Counter.create() -> Counter
                          return Counter(value: 0)

                        routine test()
                          var c = Counter.create()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CommonRoutineMismatch);
    }

    #endregion
}
