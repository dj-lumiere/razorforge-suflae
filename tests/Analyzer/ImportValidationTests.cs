using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for import validation rules:
/// #105: Import name collision (two imports expose the same symbol name)
/// #106: Import position enforcement (imports must appear before other declarations)
/// </summary>
public class ImportValidationTests
{
    #region #106: Import position enforcement

    [Fact]
    public void Analyze_ImportAfterDeclaration_ReportsError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32
                        import Core
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ImportPositionViolation);
    }

    [Fact]
    public void Analyze_ImportBeforeDeclaration_NoError()
    {
        string source = """
                        import Core
                        record Point
                          x: S32
                          y: S32
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ImportPositionViolation);
    }

    [Fact]
    public void Analyze_MultipleImportsBeforeDeclarations_NoError()
    {
        string source = """
                        import Core
                        import Core.Text
                        import Core.Bool
                        record Point
                          x: S32
                          y: S32
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ImportPositionViolation);
    }

    #endregion

    #region #105: Import name collision

    [Fact]
    public void Analyze_DuplicateImportedSymbol_ReportsError()
    {
        string source = """
                        import Core.[Text]
                        import Core.[Text]
                        record Dummy
                          value: S32
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ImportNameCollision);
    }

    [Fact]
    public void Analyze_DisjointSpecificImports_NoError()
    {
        string source = """
                        import Core.[Text]
                        import Core.[Bool]
                        record Dummy
                          value: S32
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ImportNameCollision);
    }

    #endregion
}
