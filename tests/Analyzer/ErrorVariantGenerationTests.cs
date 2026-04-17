using Compiler.Diagnostics;
using SemanticVerification.Results;
using SemanticVerification.Symbols;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for error handling variant generation (try_/check_/lookup_).
/// The compiler generates safe wrapper functions from failable (!) routines.
/// </summary>
public class ErrorVariantGenerationTests
{
    #region Maybe Variant (absent only)
    /// <summary>
    /// Tests Analyze_FailableWithAbsentOnly_GeneratesTryVariant.
    /// </summary>

    [Fact]
    public void Analyze_FailableWithAbsentOnly_GeneratesTryVariant()
    {
        // Routine with 'absent' only generates:
        // - try_get() -> T?
        string source = """
                        routine get!(id: U64) -> User
                          unless has_user(id)
                            absent
                          return fetch_user(id)

                        entity User
                          name: Text

                        routine has_user(id: U64) -> bool
                          return true

                        routine fetch_user(id: U64) -> User
                          return User(name: "test")
                        """;

        AnalysisResult result = Analyze(source: source);

        // Should generate try_get variant
        RoutineInfo? tryVariant = result.Registry.GetRoutine(name: "try_get");
        Assert.NotNull(@object: tryVariant);
        // Return type should be Maybe[User] / User?
    }

    #endregion

    #region Result Variant (throw only)
    /// <summary>
    /// Tests Analyze_FailableWithThrowOnly_GeneratesCheckAndTryVariants.
    /// </summary>

    [Fact]
    public void Analyze_FailableWithThrowOnly_GeneratesCheckAndTryVariants()
    {
        // Routine with 'throw' only generates:
        // - check_validate() -> Result[T]
        // - try_validate() -> T?
        string source = """
                        entity ValidationError obeys Crashable
                          message: Text

                        @readonly
                        routine ValidationError.crash_message() -> Text
                          return me.message

                        protocol Crashable
                          @readonly
                          routine Me.crash_message() -> Text

                        routine validate!(value: S32) -> S32
                          if value < 0
                            throw ValidationError(message: "negative")
                          return value
                        """;

        AnalysisResult result = Analyze(source: source);

        // Should generate check_validate variant
        RoutineInfo? checkVariant = result.Registry.GetRoutine(name: "check_validate");
        Assert.NotNull(@object: checkVariant);

        // Should also generate try_validate variant
        RoutineInfo? tryVariant = result.Registry.GetRoutine(name: "try_validate");
        Assert.NotNull(@object: tryVariant);
    }

    #endregion

    #region Lookup Variant (throw AND absent)
    /// <summary>
    /// Tests Analyze_FailableWithBothThrowAndAbsent_GeneratesLookupAndTryVariants.
    /// </summary>

    [Fact]
    public void Analyze_FailableWithBothThrowAndAbsent_GeneratesLookupAndTryVariants()
    {
        // Routine with both 'throw' and 'absent' generates:
        // - lookup_get_user() -> Lookup[T]
        // - try_get_user() -> T?
        string source = """
                        entity DatabaseError obeys Crashable
                          code: S32

                        @readonly
                        routine DatabaseError.crash_message() -> Text
                          return "db error"

                        protocol Crashable
                          @readonly
                          routine Me.crash_message() -> Text

                        entity User
                          name: Text

                        routine get_user!(id: U64) -> User
                          if id == 0
                            throw DatabaseError(code: 1)
                          unless user_exists(id)
                            absent
                          return fetch_user(id)

                        routine user_exists(id: U64) -> bool
                          return true

                        routine fetch_user(id: U64) -> User
                          return User(name: "test")
                        """;

        AnalysisResult result = Analyze(source: source);

        // Should generate lookup_get_user variant
        RoutineInfo? lookupVariant = result.Registry.GetRoutine(name: "lookup_get_user");
        Assert.NotNull(@object: lookupVariant);

        // Should also generate try_get_user variant
        RoutineInfo? tryVariant = result.Registry.GetRoutine(name: "try_get_user");
        Assert.NotNull(@object: tryVariant);
    }

    #endregion

    #region Variant Generation for Methods
    /// <summary>
    /// Tests Analyze_FailableMethod_GeneratesVariants.
    /// </summary>

    [Fact]
    public void Analyze_FailableMethod_GeneratesVariants()
    {
        string source = """
                        entity Cache
                          data: Dict[Text, S32]

                        routine Cache.get!(key: Text) -> S32
                          unless me.data.has(key)
                            absent
                          return me.data.get(key)
                        """;

        AnalysisResult result = Analyze(source: source);

        // Should generate try_get method variant
        RoutineInfo? tryVariant = result.Registry.GetRoutine(name: "Cache.try_get");
        Assert.NotNull(@object: tryVariant);
    }

    #endregion

    #region No Variant Generation
    /// <summary>
    /// Tests Analyze_NonFailableRoutine_NoVariantsGenerated.
    /// </summary>

    [Fact]
    public void Analyze_NonFailableRoutine_NoVariantsGenerated()
    {
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a + b
                        """;

        AnalysisResult result = Analyze(source: source);

        // Should NOT generate try_add, check_add, or lookup_add
        Assert.Null(@object: result.Registry.GetRoutine(name: "try_add"));
        Assert.Null(@object: result.Registry.GetRoutine(name: "check_add"));
        Assert.Null(@object: result.Registry.GetRoutine(name: "lookup_add"));
    }
    /// <summary>
    /// Tests Analyze_FailableWithNoThrowOrAbsent_WarnsOrErrors.
    /// </summary>

    [Fact]
    public void Analyze_FailableWithNoThrowOrAbsent_WarnsOrErrors()
    {
        // Failable routine that never throws or returns absent
        string source = """
                        routine get_value!() -> S32
                          return 42
                        """;

        AnalysisResult result = Analyze(source: source);

        // Should warn that failable routine never fails
        Assert.True(condition: result.Warnings.Count > 0 || result.Errors.Count > 0);
    }

    #endregion

    #region Error Cases
    /// <summary>
    /// Tests Analyze_ThrowInNonFailableRoutine_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_ThrowInNonFailableRoutine_ReportsWarning()
    {
        string source = """
                        entity SomeError obeys Crashable
                          msg: Text

                        @readonly
                        routine SomeError.crash_message() -> Text
                          return me.msg

                        protocol Crashable
                          @readonly
                          routine Me.crash_message() -> Text

                        routine will_fail() -> S32
                          throw SomeError(msg: "error")
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Warnings,
            filter: w => w.Code == SemanticWarningCode.ThrowAbsentInNonFailable);
    }
    /// <summary>
    /// Tests Analyze_AbsentInNonFailableRoutine_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_AbsentInNonFailableRoutine_ReportsWarning()
    {
        string source = """
                        routine might_fail() -> S32
                          absent
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Warnings,
            filter: w => w.Code == SemanticWarningCode.ThrowAbsentInNonFailable);
    }
    /// <summary>
    /// Tests Analyze_ThrowNonCrashable_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_ThrowNonCrashable_ReportsError()
    {
        string source = """
                        protocol Crashable
                          @readonly
                          routine Me.crash_message() -> Text

                        record NotAnError
                          value: S32

                        routine fail!()
                          throw NotAnError(value: 42)
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "Crashable",
                comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Variant Naming Convention
    /// <summary>
    /// Tests Analyze_VariantNames_FollowConvention.
    /// </summary>

    [Fact]
    public void Analyze_VariantNames_FollowConvention()
    {
        string source = """
                        entity SomeError obeys Crashable
                          msg: Text

                        @readonly
                        routine SomeError.crash_message() -> Text
                          return me.msg

                        protocol Crashable
                          @readonly
                          routine Me.crash_message() -> Text

                        routine parse_number!(text: Text) -> S32
                          throw SomeError(msg: "parse failed")
                          return
                        """;

        AnalysisResult result = Analyze(source: source);

        // Verify naming: routine_name! -> try_routine_name, check_routine_name
        RoutineInfo? checkVariant = result.Registry.GetRoutine(name: "check_parse_number");
        RoutineInfo? tryVariant = result.Registry.GetRoutine(name: "try_parse_number");

        Assert.NotNull(@object: checkVariant);
        Assert.NotNull(@object: tryVariant);
    }

    #endregion

    #region Error Handling Types Not Passable as Parameters
    /// <summary>
    /// Tests Analyze_ResultAsParameter_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_ResultAsParameter_ReportsError()
    {
        // Result[T] should not be passable as a function argument
        // It is an internal type for error handling flow, not a first-class type
        string source = """
                        entity User
                          name: Text

                        routine handle_result(result: Result[User])
                          pass
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
        Assert.Contains(collection: result.Errors,
            filter: e =>
                e.Message.Contains(value: "Result",
                    comparisonType: StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains(value: "parameter",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Tests Analyze_LookupAsParameter_ReportsError.
    /// </summary>

    [Fact]
    public void Analyze_LookupAsParameter_ReportsError()
    {
        // Lookup[T] should not be passable as a function argument
        // It is an internal type for error handling flow, not a first-class type
        string source = """
                        entity User
                          name: Text

                        routine handle_lookup(result: Lookup[User])
                          pass
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.True(condition: result.Errors.Count > 0);
        Assert.Contains(collection: result.Errors,
            filter: e =>
                e.Message.Contains(value: "Lookup",
                    comparisonType: StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains(value: "parameter",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    #endregion
}
