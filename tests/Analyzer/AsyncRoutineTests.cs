using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for suspended/threaded routine semantics and waitfor behavior.
/// </summary>
public class AsyncRoutineTests
{
    /// <summary>
    /// Suspended routine calls should type as Task[T].
    /// </summary>
    [Fact]
    public void Analyze_SuspendedRoutineCall_AssignsToTask_NoError()
    {
        string source = """
                        suspended routine fetch() -> S32
                          return 42

                        routine start()
                          var task: Task[S32] = fetch()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);

        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.VariableInitializerTypeMismatch);
    }

    /// <summary>
    /// Waitfor should unwrap Task[T] to T inside a suspended routine.
    /// </summary>
    [Fact]
    public void Analyze_WaitforTaskInsideSuspendedRoutine_AssignsInnerType_NoError()
    {
        string source = """
                        threaded routine compute() -> S32
                          return 42

                        suspended routine start()
                          var task = compute()
                          var value: S32 = waitfor task
                          return
                        """;

        AnalysisResult result = Analyze(source: source);

        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.VariableInitializerTypeMismatch);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WaitForTypeMisMatch);
    }

    /// <summary>
    /// Waitfor is only valid inside suspended or threaded routines.
    /// </summary>
    [Fact]
    public void Analyze_WaitforOutsideAsyncRoutine_ReportsError()
    {
        string source = """
                        threaded routine compute() -> S32
                          return 42

                        routine start()
                          var task = compute()
                          var value = waitfor task
                          return
                        """;

        AnalysisResult result = Analyze(source: source);

        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WaitforOutsideSuspendedRoutine);
    }

    /// <summary>
    /// Waitfor should accept Duration operands in suspended routines.
    /// </summary>
    [Fact]
    public void Analyze_WaitforDurationInsideSuspendedRoutine_NoError()
    {
        string source = """
                        suspended routine start()
                          waitfor 10ms
                          return
                        """;

        AnalysisResult result = Analyze(source: source);

        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WaitForTypeMisMatch);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WaitforOutsideSuspendedRoutine);
    }
}
