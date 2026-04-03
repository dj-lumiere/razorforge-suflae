using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using SemanticAnalysis.Symbols;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for error handling semantic validation:
/// - Throw requires named crashable type (#84)
/// - Failable routines must contain throw or absent (#77)
/// - @crash_only validation (#76)
/// - Unhandled crashable call (#159)
/// - Crashable catch-all requirement (#89)
/// - ??= type narrowing (#42)
/// - Result/Lookup storage restriction (#81)
/// </summary>
public class ErrorHandlingValidationTests
{
    #region Throw Requires Named Crashable Type
    /// <summary>
    /// Tests Analyze_ThrowEntity_NoRecordError.
    /// </summary>

    [Fact]
    public void Analyze_ThrowEntity_NoRecordError()
    {
        string source = """
                        entity BadError obeys Crashable
                          message: Text
                        routine test!() -> S32
                          throw BadError(message: "oops")
                          return 0
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ThrowRequiresRecordType);
    }
    /// <summary>
    /// Tests Analyze_ThrowRecord_NoRecordError.
    /// </summary>

    [Fact]
    public void Analyze_ThrowRecord_NoRecordError()
    {
        string source = """
                        record MyError obeys Crashable
                          message: Text
                        routine test!() -> S32
                          throw MyError(message: "oops")
                          return 0
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ThrowRequiresRecordType);
    }

    #endregion

    #region Failable Without Throw or Absent
    /// <summary>
    /// Tests Analyze_FailableWithoutThrowOrAbsent_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_FailableWithoutThrowOrAbsent_ReportsError()
    {
        string source = """
                        routine useless!() -> S32
                          return 42
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FailableWithoutThrowOrAbsent);
    }
    /// <summary>
    /// Tests Analyze_FailableWithThrow_NoError.
    /// </summary>

    [Fact]
    public void Analyze_FailableWithThrow_NoError()
    {
        string source = """
                        record MyError obeys Crashable
                          message: Text
                        routine useful!() -> S32
                          throw MyError(message: "bad")
                          return 42
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FailableWithoutThrowOrAbsent);
    }
    /// <summary>
    /// Tests Analyze_FailableWithAbsent_NoError.
    /// </summary>

    [Fact]
    public void Analyze_FailableWithAbsent_NoError()
    {
        string source = """
                        routine find!() -> S32
                          absent
                          return 0
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FailableWithoutThrowOrAbsent);
    }

    #endregion

    #region @crash_only Validation (#76)
    /// <summary>
    /// Tests Analyze_CrashOnlyOnNonFailable_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_CrashOnlyOnNonFailable_ReportsError()
    {
        string source = """
                        @crash_only
                        routine safe_routine() -> S32
                          return 42
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CrashOnlyOnNonFailable);
    }
    /// <summary>
    /// Tests Analyze_CrashOnlyOnFailable_NoError.
    /// </summary>

    [Fact]
    public void Analyze_CrashOnlyOnFailable_NoError()
    {
        string source = """
                        record MyError obeys Crashable
                          message: Text
                        @crash_only
                        routine crash_routine!() -> S32
                          throw MyError(message: "fatal")
                          return 42
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CrashOnlyOnNonFailable);
    }
    /// <summary>
    /// Tests Analyze_CrashOnlySuppressesVariantGeneration.
    /// </summary>

    [Fact]
    public void Analyze_CrashOnlySuppressesVariantGeneration()
    {
        string source = """
                        record MyError obeys Crashable
                          message: Text
                        @crash_only
                        routine crash_routine!() -> S32
                          throw MyError(message: "fatal")
                          return 42
                        """;

        AnalysisResult result = Analyze(source: source);

        // Should NOT generate try_, check_, or lookup_ variants
        Assert.Null(@object: result.Registry.GetRoutine(name: "try_crash_routine"));
        Assert.Null(@object: result.Registry.GetRoutine(name: "check_crash_routine"));
        Assert.Null(@object: result.Registry.GetRoutine(name: "lookup_crash_routine"));
    }
    /// <summary>
    /// Tests Analyze_NonCrashOnlyGeneratesVariants.
    /// </summary>

    [Fact]
    public void Analyze_NonCrashOnlyGeneratesVariants()
    {
        string source = """
                        record MyError obeys Crashable
                          message: Text
                        routine normal_routine!() -> S32
                          throw MyError(message: "error")
                          return 42
                        """;

        AnalysisResult result = Analyze(source: source);

        // Without @crash_only, variants SHOULD be generated
        RoutineInfo? tryVariant = result.Registry.GetRoutine(name: "try_normal_routine");
        Assert.NotNull(@object: tryVariant);
    }

    #endregion

    #region Unhandled Crashable Call (#159)
    /// <summary>
    /// Tests Analyze_FailableCallAsStatement_InNonFailable_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_FailableCallAsStatement_InNonFailable_ReportsError()
    {
        string source = """
                        record ParseError obeys Crashable
                          message: Text
                        routine parse!(data: S32) -> S32
                          throw ParseError(message: "bad")
                          return 42
                        routine caller() -> S32
                          parse!(data: 1)
                          return 0
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnhandledCrashableCall);
    }
    /// <summary>
    /// Tests Analyze_FailableCallAsStatement_InFailable_NoError.
    /// </summary>

    [Fact]
    public void Analyze_FailableCallAsStatement_InFailable_NoError()
    {
        string source = """
                        record ParseError obeys Crashable
                          message: Text
                        routine parse!(data: S32) -> S32
                          throw ParseError(message: "bad")
                          return 42
                        routine caller!() -> S32
                          parse!(data: 1)
                          return 0
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnhandledCrashableCall);
    }

    /// <summary>
    /// Tests Analyze_LookupVariable_NotDismantledBeforeScopeExit_ReportsError.
    /// </summary>

    [Fact(Skip = "check_/lookup_ variants are generated in Phase 5, not available during Phase 3 expression analysis")]
    public void Analyze_LookupVariable_NotDismantledBeforeScopeExit_ReportsError()
    {
        string source = """
                        record DbError obeys Crashable
                          message: Text

                        @readonly
                        routine DbError.crash_message() -> Text
                          return me.message

                        routine get_value!(id: U64) -> S32
                          if id == 0
                            throw DbError(message: "bad")
                          unless id == 1
                            absent
                          return 42

                        routine test()
                          var pending = lookup_get_value(id: 1)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.LookupNotDismantled);
    }

    /// <summary>
    /// Tests Analyze_ResultCopiedFromVariable_ReportsError.
    /// </summary>

    [Fact(Skip = "check_/lookup_ variants are generated in Phase 5, not available during Phase 3 expression analysis")]
    public void Analyze_ResultCopiedFromVariable_ReportsError()
    {
        string source = """
                        record ParseError obeys Crashable
                          message: Text

                        @readonly
                        routine ParseError.crash_message() -> Text
                          return me.message

                        routine validate!(value: S32) -> S32
                          if value < 0
                            throw ParseError(message: "negative")
                          return value

                        routine test()
                          var first = check_validate(value: 1)
                          var second = first
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ErrorHandlingTypeStoredInVariable);
    }

    #endregion

    // NOTE: #81 (Result/Lookup storage restriction) tests require multi-module test
    // infrastructure since check_/lookup_ variants are generated in Phase 5 but body
    // analysis happens in Phase 3. The implementation is in place and will be validated
    // when multi-module compilation support is available for tests.
}
