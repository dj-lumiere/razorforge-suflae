using SemanticVerification.Results;
using Compiler.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for routine declaration validation:
/// - Duplicate routine definitions (#150)
/// </summary>
public class RoutineDeclarationTests
{
    #region Duplicate Routine Definition
    /// <summary>
    /// Tests Analyze_DuplicateRoutine_ReportsError.
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

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DuplicateRoutineDefinition);
    }
    /// <summary>
    /// Tests Analyze_UniqueRoutines_NoDuplicateError.
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

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DuplicateRoutineDefinition);
    }

    #endregion
}
