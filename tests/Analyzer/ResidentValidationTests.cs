using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for resident type validation rules:
/// #52: Residents as local variables
/// #53: Resident member variable containment
/// #54: Resident share/track prohibition
/// #55: Resident Hashable prohibition
/// </summary>
public class ResidentValidationTests
{
    #region #52: Resident as local variable

    [Fact]
    public void Analyze_ResidentAsLocalVariable_ReportsError()
    {
        string source = """
                        resident GlobalState
                          counter: S32
                        routine test()
                          var state: GlobalState
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ResidentAsLocalVariable);
    }

    [Fact]
    public void Analyze_ResidentAsGlobalVariable_NoError()
    {
        string source = """
                        resident GlobalState
                          counter: S32
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ResidentAsLocalVariable);
    }

    #endregion

    #region #53: Resident member variable containment

    [Fact]
    public void Analyze_ResidentWithRecordMemberVariable_NoError()
    {
        string source = """
                        record Config
                          value: S32
                        resident GlobalState
                          config: Config
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ResidentContainsInvalidType);
    }

    [Fact]
    public void Analyze_ResidentWithPrimitiveMemberVariable_NoError()
    {
        string source = """
                        resident Counter
                          value: S32
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ResidentContainsInvalidType);
    }

    [Fact]
    public void Analyze_ResidentWithOtherResident_NoError()
    {
        string source = """
                        resident Inner
                          x: S32
                        resident Outer
                          inner: Inner
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ResidentContainsInvalidType);
    }

    [Fact]
    public void Analyze_ResidentWithEntityMemberVariable_ReportsError()
    {
        string source = """
                        entity User
                          name: Text
                        resident BadResident
                          user: User
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ResidentContainsInvalidType
                         && e.Message.Contains("user"));
    }

    #endregion

    #region #54: Resident share/track prohibition

    [Fact]
    public void Analyze_ResidentShareCall_ReportsError()
    {
        string source = """
                        resident GlobalState
                          counter: S32
                        routine test(state: GlobalState)
                          var s = state.share()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ResidentShareTrackProhibited);
    }

    [Fact]
    public void Analyze_ResidentTrackCall_ReportsError()
    {
        string source = """
                        resident GlobalState
                          counter: S32
                        routine test(state: GlobalState)
                          var t = state.track()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ResidentShareTrackProhibited);
    }

    [Fact]
    public void Analyze_ResidentNormalMethodCall_NoError()
    {
        string source = """
                        resident GlobalState
                          counter: S32
                        @readonly
                        routine GlobalState.get_counter() -> S32
                          return me.counter
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ResidentShareTrackProhibited);
    }

    #endregion

    #region #55: Resident Hashable prohibition

    [Fact]
    public void Analyze_ResidentImplementsHashable_ReportsError()
    {
        string source = """
                        protocol Hashable
                          @readonly
                          routine Me.hash() -> S64
                        resident BadResident obeys Hashable
                          value: S32
                        @readonly
                        routine BadResident.hash() -> S64
                          return 0_s64
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ResidentHashableProhibited);
    }

    [Fact]
    public void Analyze_ResidentImplementsEquatable_NoError()
    {
        string source = """
                        protocol Equatable
                          @readonly
                          routine Me.__eq__(other: Me) -> Bool
                        resident MyResident obeys Equatable
                          value: S32
                        @readonly
                        routine MyResident.__eq__(other: MyResident) -> Bool
                          return true
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ResidentHashableProhibited);
    }

    #endregion
}
