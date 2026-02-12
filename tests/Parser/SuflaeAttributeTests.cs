using Xunit;

namespace RazorForge.Tests.Parser;

using Compilers.Shared.AST;
using static TestHelpers;

/// <summary>
/// Tests for parsing attributes in Suflae:
/// @readonly, @writable, @crash_only, @config, @prelude, compound attributes.
/// Suflae uses indentation-based syntax.
/// </summary>
public class SuflaeAttributeTests
{
    #region Simple Attribute Tests

    [Fact]
    public void ParseSuflae_ReadonlyAttribute()
    {
        string source = """
                        @readonly
                        routine Point.distance() -> Decimal
                            return 0.0
                        """;

        Program program = AssertParsesSuflae(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        Assert.NotNull(@object: routine.Attributes);
        Assert.Contains(expected: "readonly", collection: routine.Attributes);
    }

    [Fact]
    public void ParseSuflae_WritableAttribute()
    {
        string source = """
                        @writable
                        routine Counter.increment()
                            me.count += 1
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_CrashOnlyAttribute()
    {
        string source = """
                        @crash_only
                        routine internal_divide!(a: Integer, b: Integer) -> Integer
                            if b == 0
                                throw DivisionByZeroError()
                            return a // b
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_PreludeAttribute()
    {
        string source = """
                        @prelude
                        routine show(msg: Text)
                            pass
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_StaticAttribute()
    {
        string source = """
                        @static
                        routine Math.pi() -> Decimal
                            return 3.14159265359
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_InlineAttribute()
    {
        string source = """
                        @inline
                        routine add(a: Integer, b: Integer) -> Integer
                            return a + b
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Parameterized Attribute Tests

    [Fact]
    public void ParseSuflae_ConfigAttributeTargetOs()
    {
        string source = """
                        @config(target_os: "windows")
                        routine get_config_path() -> Text
                            return "C:\\config.toml"
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ConfigAttributeFeature()
    {
        string source = """
                        @config(feature: "debug")
                        routine debug_log(msg: Text)
                            show(f"[DEBUG] {msg}")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_DeprecatedAttributeWithMessage()
    {
        string source = """
                        @deprecated(message: "Use new_function instead")
                        routine old_function()
                            pass
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Compound Attribute Tests

    [Fact]
    public void ParseSuflae_CompoundAttributes()
    {
        string source = """
                        @[readonly, crash_only]
                        routine validate!(value: Integer) -> Integer
                            if value < 0
                                throw ValidationError()
                            return value
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_CompoundAttributesMultiple()
    {
        string source = """
                        @[inline, readonly, prelude]
                        routine identity(x: Integer) -> Integer
                            return x
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Type Attribute Tests

    [Fact]
    public void ParseSuflae_AttributeOnRecord()
    {
        string source = """
                        @derive(Debug, Clone)
                        record Point
                            x: F32
                            y: F32
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_AttributeOnEntity()
    {
        string source = """
                        @serializable
                        entity User
                            var name: Text
                            var age: U32
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_AttributeOnProtocol()
    {
        string source = """
                        @prelude
                        protocol Displayable
                            @readonly
                            routine display() -> Text
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Field Attribute Tests

    [Fact]
    public void ParseSuflae_AttributeOnField()
    {
        string source = """
                        record Config
                            @optional
                            name: Text

                            @default(42)
                            value: Integer
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_AttributeOnEntityField()
    {
        string source = """
                        entity User
                            @readonly
                            let id: U64

                            @indexed
                            var email: Text
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Multiple Attribute Lines Tests

    [Fact]
    public void ParseSuflae_MultipleAttributeLines()
    {
        string source = """
                        @readonly
                        @inline
                        @config(feature: "optimized")
                        routine fast_compute(x: Integer) -> Integer
                            return x * 2
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_AttributesOnTypeAndMethods()
    {
        string source = """
                        @derive(Debug)
                        record Calculator
                            value: Integer

                        @readonly
                        routine Calculator.get() -> Integer
                            return me.value

                        @writable
                        routine Calculator.set(v: Integer)
                            me.value = v
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Visibility with Attribute Tests

    [Fact]
    public void ParseSuflae_VisibilityAndAttribute()
    {
        string source = """
                        @readonly
                        public routine Point.distance() -> Decimal
                            return 0.0
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_PrivateWithAttribute()
    {
        string source = """
                        @inline
                        private routine helper(x: Integer) -> Integer
                            return x * 2
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_InternalWithAttribute()
    {
        string source = """
                        @serializable
                        internal record InternalData
                            value: Integer
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Protocol Method Attributes

    [Fact]
    public void ParseSuflae_ProtocolMethodAttributes()
    {
        string source = """
                        protocol Container
                            @readonly
                            routine count() -> uaddr

                            @readonly
                            routine is_empty() -> bool

                            @writable
                            routine clear()
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Async Attributes

    [Fact]
    public void ParseSuflae_SuspendedWithAttribute()
    {
        string source = """
                        @timeout(5000)
                        suspended routine fetch_data(url: Text) -> Text
                            let response = waitfor http.get(url)
                            return response.body
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Test Attribute Tests

    [Fact]
    public void ParseSuflae_TestAttribute()
    {
        string source = """
                        @test
                        routine test_addition()
                            verify!(1 + 1 == 2)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_BenchAttribute()
    {
        string source = """
                        @bench
                        routine bench_sort()
                            let items = generate_random_list(1000)
                            sort(items)
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion
}
