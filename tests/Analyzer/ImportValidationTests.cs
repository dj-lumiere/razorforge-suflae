using SemanticVerification.Results;
using Compiler.Diagnostics;
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
    /// <summary>
    /// Tests Analyze_ImportAfterDeclaration_ReportsError.
    /// </summary>

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
    /// <summary>
    /// Tests Analyze_ImportBeforeDeclaration_NoError.
    /// </summary>

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
    /// <summary>
    /// Tests Analyze_MultipleImportsBeforeDeclarations_NoError.
    /// </summary>

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
    /// <summary>
    /// Tests Analyze_DuplicateImportedSymbol_ReportsError.
    /// </summary>

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
    /// <summary>
    /// Tests Analyze_DisjointSpecificImports_NoError.
    /// </summary>

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
