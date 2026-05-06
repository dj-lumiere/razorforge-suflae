using Compiler.Diagnostics;
using Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for routine declaration.
/// </summary>
public class RoutineDeclarationTests
{
    #region Duplicate Routine Definition
    /// <summary>
    /// Verifies semantic analysis behavior for duplicate routine and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_DuplicateRoutine_ReportsError()
    {
        string source = """
                        routine foo() -> S32
                          return 1
                        routine foo() -> S32
                          return 2
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DuplicateRoutineDefinition);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for unique routines no duplicate error.
    /// </summary>

    [Fact]
    public void Analyze_UniqueRoutines_NoDuplicateError()
    {
        string source = """
                        routine foo() -> S32
                          return 1
                        routine bar() -> S32
                          return 2
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DuplicateRoutineDefinition);
    }

    #endregion
}
