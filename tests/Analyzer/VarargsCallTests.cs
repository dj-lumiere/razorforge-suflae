using SemanticAnalysis.Results;
using SemanticAnalysis.Diagnostics;
using Xunit;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for varargs call resolution, $create overload resolution (C95),
/// and variadic argument validation.
/// </summary>
public class VarargsCallTests
{
    // ========================================================================
    // Varargs dispatch inference (mirrors DispatchInferenceTests patterns)
    // ========================================================================

    [Fact]
    public void Varargs_MultipleConcreteArgs_NoDispatchError()
    {
        string source = """
                        routine collect(values...: S64) -> S64
                          return 0
                        routine test() -> S64
                          return collect(1, 2, 3, 4, 5)
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RuntimeDispatchNotSupported);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TooManyArguments);
    }

    [Fact]
    public void Varargs_MethodWithMeParam_NoDispatchError()
    {
        // Varargs as second param is valid when first param is 'me'
        string source = """
                        record Logger
                          label: Text
                        routine Logger.log(values...: S64) -> S64
                          return 0
                        routine test() -> S64
                          preset l = Logger(label: "test")
                          return l.log(1, 2, 3)
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RuntimeDispatchNotSupported);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TooManyArguments);
    }

    [Fact]
    public void Varargs_LeadingNonMeParam_ReportsVarargsNotFirst()
    {
        // Varargs must be first non-me param — leading params are rejected
        string source = """
                        routine log(label: Text, values...: S64) -> S64
                          return 0
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.VarargsNotFirst);
    }

    [Fact]
    public void Varargs_MixedTypes_ReportsRuntimeDispatchError()
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

    [Fact]
    public void Varargs_SameConcreteType_NoDispatchError()
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

    // ========================================================================
    // C95: $create overload resolution
    // ========================================================================

    [Fact]
    public void CreateOverload_SingleNamedArg_ResolvesToCreate()
    {
        // Entity with 3 fields + $create(capacity: U64) — named arg should resolve to $create
        string source = """
                        entity Pool
                          secret data: U64
                          secret count: U64
                          secret capacity: U64
                        routine Pool.$create(capacity: U64) -> Pool
                          return Pool(data: 0u64, count: 0u64, capacity: capacity)
                        routine test()
                          var p = Pool(capacity: 32u64)
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void CreateOverload_SinglePositionalArg_ResolvesToCreate()
    {
        // Entity with 3 fields + $create(capacity: U64) — positional U64 should resolve to $create
        string source = """
                        entity Pool
                          secret data: U64
                          secret count: U64
                          secret capacity: U64
                        routine Pool.$create(capacity: U64) -> Pool
                          return Pool(data: 0u64, count: 0u64, capacity: capacity)
                        routine test()
                          var p = Pool(32u64)
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void CreateOverload_AllNamedFields_WorksAsFieldConstruction()
    {
        // When all field names are provided, should work as direct field construction
        string source = """
                        entity Pool
                          secret data: U64
                          secret count: U64
                          secret capacity: U64
                        routine test()
                          var p = Pool(data: 0u64, count: 0u64, capacity: 32u64)
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void CreateOverload_SingleFieldRecord_NoError()
    {
        string source = """
                        record Wrapper
                          value: S64
                        routine Wrapper.$create(from: Text) -> Wrapper
                          return Wrapper(value: 0)
                        routine test()
                          var w = Wrapper(value: 42)
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void CreateOverload_NoMatchingCreate_FallsThrough()
    {
        // Two S64 args don't match any $create(capacity: U64) overload → falls through to S510
        string source = """
                        entity Pool
                          secret data: U64
                          secret count: U64
                          secret capacity: U64
                        routine Pool.$create(capacity: U64) -> Pool
                          return Pool(data: 0u64, count: 0u64, capacity: capacity)
                        routine test()
                          var p = Pool(1u64, 2u64)
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NamedArgumentRequired);
    }

    [Fact]
    public void CreateOverload_ZeroArgEntity_WorksWithoutCreate()
    {
        // Zero-arg entity construction still works even without $create
        string source = """
                        entity Empty
                          pass
                        routine test()
                          var e = Empty()
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void CreateOverload_RecordConversion_ResolvesToCreate()
    {
        // Record with $create(from: Type) conversion constructor
        string source = """
                        record Celsius
                          value: F64
                        routine Celsius.$create(from: F64) -> Celsius
                          return Celsius(value: from)
                        routine test()
                          var c = Celsius(100.0)
                        """;

        AnalysisResult result = Analyze(source: source);
        Assert.Empty(collection: result.Errors);
    }
}
