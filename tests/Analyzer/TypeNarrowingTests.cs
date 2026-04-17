using Compiler.Diagnostics;
using SemanticVerification.Results;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for type narrowing after null/error checks.
/// After eliminating None or Crashable via pattern checks,
/// the type should narrow from Maybe/Result/Lookup to the inner value type.
/// </summary>
public class TypeNarrowingTests
{
    #region Guard Clause Narrowing (unless / if-return)
    /// <summary>
    /// Tests Analyze_UnlessIsNone_NarrowsMaybeToValue.
    /// </summary>

    [Fact]
    public void Analyze_UnlessIsNone_NarrowsMaybeToValue()
    {
        // unless desugars to: if Not(value is None) { body }
        // The body runs when value is NOT None, so value
        // is narrowed from Maybe[S32] to S32 inside the block
        string source = """
                        routine process(value: S32?) -> S32
                          unless value is None
                            return value
                          return 0
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Tests Analyze_IfIsNoneReturn_NarrowsMaybeToValue.
    /// </summary>

    [Fact]
    public void Analyze_IfIsNoneReturn_NarrowsMaybeToValue()
    {
        // if value is None { return 0 }
        // After the if (guard clause), value narrows til S32
        string source = """
                        routine process(value: S32?) -> S32
                          if value is None
                            return 0
                          return value
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Tests Analyze_IfIsNotNone_NarrowsInThenBranch.
    /// </summary>

    [Fact]
    public void Analyze_IfIsNotNone_NarrowsInThenBranch()
    {
        // if value isnot None { return value }
        // The then branch narrows value to S32
        string source = """
                        routine process(value: S32?) -> S32
                          if value isnot None
                            return value
                          return 0
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Tests Analyze_IfIsNoneWithElse_NarrowsInElseBranch.
    /// </summary>

    [Fact]
    public void Analyze_IfIsNoneWithElse_NarrowsInElseBranch()
    {
        // if value is None { ... } else { return value }
        // The else branch narrows value til S32
        string source = """
                        routine process(value: S32?) -> S32
                          if value is None
                            return 0
                          else
                            return value
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Tests Analyze_IfIsNoneWithoutExit_DoesNotNarrowAfterIf.
    /// </summary>

    [Fact]
    public void Analyze_IfIsNoneWithoutExit_DoesNotNarrowAfterIf()
    {
        // A non-exiting if branch must not narrow the remainder of the scope.
        string source = """
                        routine process(value: S32?) -> S32
                          if value is None
                            show("missing")
                          return value
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ReturnTypeMismatch);
    }

    #endregion

    #region When Statement Narrowing
    /// <summary>
    /// Tests Analyze_WhenMaybe_ElseBindsNarrowedType.
    /// </summary>

    [Fact]
    public void Analyze_WhenMaybe_ElseBindsNarrowedType()
    {
        // when value { is None => ... | else v => ... }
        // After handling None, else v should be S32
        string source = """
                        routine process(value: S32?) -> S32
                          when value
                            is None => return 0
                            else v => return v
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region No Narrowing for Non-Error Types
    /// <summary>
    /// Tests Analyze_NonErrorHandlingType_NoNarrowingCrash.
    /// </summary>

    [Fact]
    public void Analyze_NonErrorHandlingType_NoNarrowingCrash()
    {
        // Ensure narrowing logic doesn't break for non-error-handling types
        string source = """
                        routine process(value: S32) -> S32
                          if value isnot None
                            return value
                          return 0
                        """;

        // Should not crash ??the is/isnot check may produce warnings
        // but should not cause an internal error
        Analyze(source: source);
    }

    #endregion
}
