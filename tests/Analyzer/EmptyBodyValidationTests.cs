using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for empty block/body validation.
/// Any empty body without 'pass' should error.
/// Choice/variant/flags must always have members (even pass is not allowed).
/// </summary>
public class EmptyBodyValidationTests
{
    #region Statement Blocks

    [Fact]
    public void Analyze_RoutineBodyWithPass_NoError()
    {
        string source = """
                        routine do_nothing()
                          pass
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.EmptyBlockWithoutPass);
    }

    #endregion

    #region Type Bodies

    [Fact]
    public void Analyze_EmptyRecordBody_ReportsError()
    {
        // record with no body (no indent after header) — followed by another decl to ensure valid parse
        string source = "record Empty\nrecord Other\n  pass\n";

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.EmptyBlockWithoutPass);
    }

    [Fact]
    public void Analyze_RecordWithPass_NoError()
    {
        string source = """
                        record Empty
                          pass
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.EmptyBlockWithoutPass);
    }

    [Fact]
    public void Analyze_EmptyEntityBody_ReportsError()
    {
        // entity with no body (no indent after header)
        string source = "entity Empty\nentity Other\n  pass\n";

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.EmptyBlockWithoutPass);
    }

    [Fact]
    public void Analyze_EntityWithPass_NoError()
    {
        string source = """
                        entity Empty
                          pass
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.EmptyBlockWithoutPass);
    }

    #endregion

    #region Enumerations (always error when empty)

    [Fact]
    public void Analyze_EmptyChoice_ReportsError()
    {
        // choice with no cases
        string source = "choice Empty\nchoice Other\n  OK\n";

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.EmptyEnumerationBody);
    }

    [Fact]
    public void Analyze_EmptyVariant_ReportsError()
    {
        // variant with no cases
        string source = "variant Empty\nvariant Other\n  SOME\n";

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.EmptyEnumerationBody);
    }

    [Fact]
    public void Analyze_EmptyFlags_ReportsError()
    {
        // flags with no members
        string source = "flags Empty\nflags Other\n  READ\n";

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.EmptyEnumerationBody);
    }

    #endregion
}
