using SemanticAnalysis.Results;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for generic type resolution bugs (S191, S192, S193).
/// </summary>
public class GenericResolutionTests
{
    #region S191 — Void return on generic method calls

    [Fact]
    public void Analyze_GenericVoidMethod_ReturnsBlank()
    {
        // S191: Calling a void method on a generic resolution should return Blank, not <error>
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].clear()
                          return

                        routine test()
                          var b = Box[S32](value: 42)
                          b.clear()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region S192 — Double-generic method return type

    [Fact]
    public void Analyze_MethodLevelGenericReturnType_ResolvesCorrectly()
    {
        // S192: Method-level generic params + owner-level generic params
        // convert[U](new_val: U) -> Box[U] should resolve Box[U] with the call-site type arg
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].convert[U](new_val: U) -> Box[U]
                          return Box[U](value: new_val)

                        routine test()
                          var b = Box[S32](value: 42)
                          var c: Box[Bool] = b.convert[Bool](true)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_MethodLevelGenericReturnType_InfersWithoutAnnotation()
    {
        // S192: Without explicit type annotation, var c should infer as Box[Bool]
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].convert[U](new_val: U) -> Box[U]
                          return Box[U](value: new_val)

                        routine test()
                          var b = Box[S32](value: 42)
                          var c = b.convert[Bool](true)
                          var d: Box[Bool] = c
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_MethodLevelGenericDirectReturn_ResolvesCorrectly()
    {
        // S192: Method returning U directly (not wrapped in owner type)
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].extract[U](val: U) -> U
                          return val

                        routine test()
                          var b = Box[S32](value: 42)
                          var x: Bool = b.extract[Bool](true)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region S193 — $eq on generic record types

    [Fact]
    public void Analyze_GenericRecordMethodLookup_WorksOnResolution()
    {
        // S193: Methods registered on generic def (Wrapper) should be found for Wrapper[S32]
        // LookupMethod falls back from Wrapper[S32] → Wrapper to find user-defined methods
        string source = """
                        record Wrapper[T]
                          value: T

                        routine Wrapper[T].unwrap() -> T
                          return me.value

                        routine test()
                          var a = Wrapper[S32](value: 1)
                          var v: S32 = a.unwrap()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion
}
