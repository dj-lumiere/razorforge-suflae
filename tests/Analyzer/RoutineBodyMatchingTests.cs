using SemanticAnalysis.Diagnostics;
using SemanticAnalysis.Results;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for routine body matching against registered declarations (P4).
/// Verifies that overloaded routines, generic member routines, and
/// module-qualified routines correctly match bodies to declarations.
/// </summary>
public class RoutineBodyMatchingTests
{
    #region Overloaded $create Routines

    [Fact]
    public void Analyze_OverloadedCreate_BothBodiesMatch()
    {
        // Two $create overloads with different parameter types should each
        // match their correct body via resolved overload keys
        string source = """
                        record Measurement
                          value: F64

                        routine Measurement.$create(from: S32) -> Measurement
                          return Measurement(value: from.F64())

                        routine Measurement.$create(from: F64) -> Measurement
                          return Measurement(value: from)

                        routine test()
                          var a = Measurement(from: 42)
                          var b = Measurement(from: 3.14)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnresolvedRoutineBody);
    }

    #endregion

    #region Generic Member Routines (External Syntax)

    [Fact]
    public void Analyze_GenericOwnerExternalRoutine_BodyMatches()
    {
        // routine Box[T].unwrap() defined outside the type body
        // should match the registered declaration
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].unwrap() -> T
                          return me.value

                        routine test()
                          var b = Box[S32](value: 42)
                          var v = b.unwrap()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnresolvedRoutineBody);
    }

    [Fact]
    public void Analyze_GenericMethodLevelParam_BodyMatches()
    {
        // Method-level generic params (Box[T].convert[U]) should match correctly
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].convert[U](new_val: U) -> Box[U]
                          return Box[U](value: new_val)

                        routine test()
                          var b = Box[S32](value: 42)
                          var c = b.convert[Bool](true)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnresolvedRoutineBody);
    }

    #endregion

    #region Module-Qualified Routines

    [Fact]
    public void Analyze_ModuleQualifiedRoutine_BodyMatches()
    {
        // Module-qualified routine body should match its registered declaration
        string source = """
                        module MyLib

                        routine compute(x: S32) -> S32
                          return x

                        routine test()
                          var r = compute(x: 10)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnresolvedRoutineBody);
    }

    #endregion
}
