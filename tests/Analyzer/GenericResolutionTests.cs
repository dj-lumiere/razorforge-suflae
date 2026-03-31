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

    #region LookupMethod fully-resolved results

    [Fact]
    public void Analyze_GenericOwnerMethod_ParamTypeSubstituted()
    {
        // LookupMethod on List[S32].$add should return param type S32 (not T)
        // After fix, no manual SubstituteTypeParameters needed at call site
        string source = """
                        record Pair[T]
                          first: T
                          second: T

                        routine Pair[T].swap_first(value: T) -> T
                          return me.first

                        routine test()
                          var p = Pair[S32](first: 1, second: 2)
                          var old: S32 = p.swap_first(value: 3)
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_GenericOwnerMethod_ReturnTypeSubstituted()
    {
        // LookupMethod on generic owner should substitute T in return type
        string source = """
                        record Container[T]
                          item: T

                        routine Container[T].get() -> T
                          return me.item

                        routine test()
                          var c = Container[Bool](item: true)
                          var v: Bool = c.get()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_GenericOwnerMethod_NestedGenericSubstitution()
    {
        // Dict[Text, List[S32]].$getitem should return List[S32], not T
        string source = """
                        record Mapping[K, V]
                          key: K
                          val: V

                        routine Mapping[K, V].get_val() -> V
                          return me.val

                        routine test()
                          var inner = Mapping[S32, Bool](key: 1, val: true)
                          var m = Mapping[Bool, Mapping[S32, Bool]](key: false, val: inner)
                          var result: Mapping[S32, Bool] = m.get_val()
                          return
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion
}
