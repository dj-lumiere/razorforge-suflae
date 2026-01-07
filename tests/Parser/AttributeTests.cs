using Xunit;

namespace RazorForge.Tests.Parser;

using Compilers.Shared.AST;
using static TestHelpers;

/// <summary>
/// Tests for parsing attributes in RazorForge:
/// @readonly, @writable, @crash_only, @config, @prelude, @static, compound attributes.
/// </summary>
public class AttributeTests
{
    #region Simple Attribute Tests

    [Fact]
    public void Parse_ReadonlyAttribute()
    {
        string source = """
                        @readonly
                        routine Point.distance() -> F32 {
                            return 0.0_f32
                        }
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        Assert.NotNull(@object: routine.Attributes);
        Assert.Contains(expected: "readonly", collection: routine.Attributes);
    }

    [Fact]
    public void Parse_WritableAttribute()
    {
        string source = """
                        @writable
                        routine Counter.increment() {
                            me.count += 1
                        }
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        Assert.NotNull(@object: routine.Attributes);
        Assert.Contains(expected: "writable", collection: routine.Attributes);
    }

    [Fact]
    public void Parse_CrashOnlyAttribute()
    {
        string source = """
                        @crash_only
                        routine internal_divide!(a: S32, b: S32) -> S32 {
                            if b == 0 {
                                throw DivisionByZeroError()
                            }
                            return a // b
                        }
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        Assert.NotNull(@object: routine.Attributes);
        Assert.Contains(expected: "crash_only", collection: routine.Attributes);
    }

    [Fact]
    public void Parse_PreludeAttribute()
    {
        string source = """
                        @prelude
                        routine show(msg: Text) {
                            pass
                        }
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        Assert.NotNull(@object: routine.Attributes);
        Assert.Contains(expected: "prelude", collection: routine.Attributes);
    }

    [Fact]
    public void Parse_StaticAttribute()
    {
        string source = """
                        @static
                        routine Math.pi() -> F64 {
                            return 3.14159265359_f64
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_InlineAttribute()
    {
        string source = """
                        @inline
                        routine add(a: S32, b: S32) -> S32 {
                            return a + b
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    #endregion

    #region Parameterized Attribute Tests

    [Fact]
    public void Parse_ConfigAttributeTargetOs()
    {
        string source = """
                        @config(target_os: "windows")
                        routine get_config_path() -> Text {
                            return "C:\\config.toml"
                        }
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        Assert.NotNull(@object: routine.Attributes);
    }

    [Fact]
    public void Parse_ConfigAttributeFeature()
    {
        string source = """
                        @config(feature: "debug")
                        routine debug_log(msg: Text) {
                            show(f"[DEBUG] {msg}")
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_ConfigAttributeArchitecture()
    {
        string source = """
                        @config(target_arch: "x86_64")
                        routine simd_add(a: ValueList<F32, 4>, b: ValueList<F32, 4>) -> ValueList<F32, 4> {
                            pass
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_DeprecatedAttributeWithMessage()
    {
        string source = """
                        @deprecated(message: "Use new_function instead")
                        routine old_function() {
                            pass
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    #endregion

    #region Compound Attribute Tests

    [Fact]
    public void Parse_CompoundAttributes()
    {
        string source = """
                        @[readonly, crash_only]
                        routine validate!(value: S32) -> S32 {
                            if value < 0 {
                                throw ValidationError()
                            }
                            return value
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_CompoundAttributesMultiple()
    {
        string source = """
                        @[inline, readonly, prelude]
                        routine identity(x: S32) -> S32 {
                            return x
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    #endregion

    #region Type Attribute Tests

    [Fact]
    public void Parse_AttributeOnRecord()
    {
        string source = """
                        @serializable
                        record Point {
                            x: F32
                            y: F32
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_AttributeOnEntity()
    {
        string source = """
                        @serializable
                        entity User {
                            var name: Text
                            var age: U32
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_AttributeOnProtocol()
    {
        string source = """
                        @prelude
                        protocol Displayable {
                            @readonly
                            routine Me.display() -> Text
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_AttributeOnResident()
    {
        string source = """
                        @singleton
                        resident GlobalConfig {
                            var settings: Dict<Text, Text>
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    #endregion

    #region Field Attribute Tests

    [Fact]
    public void Parse_AttributeOnField()
    {
        string source = """
                        record Config {
                            @optional
                            name: Text

                            @default(42)
                            value: S32
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_AttributeOnEntityField()
    {
        string source = """
                        entity User {
                            @readonly
                            let id: U64

                            @indexed
                            var email: Text
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    #endregion

    #region Multiple Attribute Lines Tests

    [Fact]
    public void Parse_MultipleAttributeLines()
    {
        string source = """
                        @readonly
                        @inline
                        @config(feature: "optimized")
                        routine fast_compute(x: S32) -> S32 {
                            return x * 2
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_AttributesOnTypeAndMethods()
    {
        string source = """
                        @derive(Debug)
                        record Calculator {
                            value: S32
                        }

                        @readonly
                        routine Calculator.get() -> S32 {
                            return me.value
                        }

                        @writable
                        routine Calculator.set(v: S32) {
                            me.value = v
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    #endregion

    #region Visibility with Attribute Tests

    [Fact]
    public void Parse_VisibilityAndAttribute()
    {
        string source = """
                        @readonly
                        public routine Point.distance() -> F32 {
                            return 0.0_f32
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_PrivateWithAttribute()
    {
        string source = """
                        @inline
                        private routine helper(x: S32) -> S32 {
                            return x * 2
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_InternalWithAttribute()
    {
        string source = """
                        @serializable
                        internal record InternalData {
                            value: S32
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    #endregion

    #region Protocol Method Attributes

    [Fact]
    public void Parse_ProtocolMethodAttributes()
    {
        string source = """
                        protocol Container {
                            @readonly
                            routine Me.count() -> uaddr

                            @readonly
                            routine Me.is_empty() -> bool

                            @writable
                            routine Me.clear()
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    #endregion

    #region Test Attribute Tests

    [Fact]
    public void Parse_TestAttribute()
    {
        string source = """
                        @test
                        routine test_addition() {
                            verify!(1 + 1 == 2)
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    [Fact]
    public void Parse_BenchAttribute()
    {
        string source = """
                        @bench
                        routine bench_sort() {
                            let items = generate_random_list(1000)
                            sort(items)
                        }
                        """;

        Program program = AssertParses(source: source);
    }

    #endregion
}
