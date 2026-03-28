using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for C29: Dispatch inference for varargs calls.
/// Protocol-constrained varargs with mixed types → RuntimeDispatchNotSupported in RazorForge.
/// </summary>
public class DispatchInferenceTests
{
    [Fact]
    public void Analyze_ConcreteVarargs_NoDispatchError()
    {
        string source = """
                        routine sum(values...: S32) -> S32
                          return 0
                        routine test() -> S32
                          return sum(1, 2, 3)
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RuntimeDispatchNotSupported);
    }

    [Fact]
    public void Analyze_GenericConstrainedVarargs_NoDispatchError()
    {
        string source = """
                        protocol Comparable
                          routine $cmp(you: Me) -> S32
                        routine max_of[T](values...: T) -> T needs T obeys Comparable
                          return values[0]
                        record Score obeys Comparable
                          value: S32
                        routine Score.$cmp(you: Score) -> S32
                          return me.value
                        routine test() -> Score
                          preset a = Score(value: 1)
                          preset b = Score(value: 2)
                          return max_of(a, b)
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RuntimeDispatchNotSupported);
    }

    [Fact]
    public void Analyze_ProtocolVarargsSameType_NoDispatchError()
    {
        string source = """
                        protocol Representable
                          routine Text() -> Text
                        record Name obeys Representable
                          value: Text
                        routine Name.Text() -> Text
                          return me.value
                        routine show(values...: Representable)
                          pass
                        routine test()
                          preset a = Name(value: "Alice")
                          preset b = Name(value: "Bob")
                          show(a, b)
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RuntimeDispatchNotSupported);
    }

    [Fact]
    public void Analyze_ProtocolVarargsMixedTypes_ReportsRuntimeDispatchError()
    {
        string source = """
                        protocol Representable
                          routine Text() -> Text
                        record Name obeys Representable
                          value: Text
                        routine Name.Text() -> Text
                          return me.value
                        record Age obeys Representable
                          value: S32
                        routine Age.Text() -> Text
                          return "age"
                        routine show(values...: Representable)
                          pass
                        routine test()
                          preset a = Name(value: "Alice")
                          preset b = Age(value: 30)
                          show(a, b)
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RuntimeDispatchNotSupported);
    }
}
