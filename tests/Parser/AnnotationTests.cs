using Xunit;

namespace RazorForge.Tests.Parser;

using SyntaxTree;
using static TestHelpers;

/// <summary>
/// Tests for parsing attributes in RazorForge:
/// @readonly, @writable, @crash_only, @config, @prelude, @static, compound attributes.
/// </summary>
public class AttributeTests
{
    #region Simple Annotation Tests

    [Fact]
    public void Parse_ReadonlyAttribute()
    {
        string source = """
                        @readonly
                        routine Point.distance() -> F32
                          return 0.0_f32
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        Assert.NotNull(@object: routine.Annotations);
        Assert.Contains(expected: "readonly", collection: routine.Annotations);
    }

    [Fact]
    public void Parse_WritableAttribute()
    {
        string source = """
                        @writable
                        routine Counter.increment()
                          me.count += 1
                          return
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        Assert.NotNull(@object: routine.Annotations);
        Assert.Contains(expected: "writable", collection: routine.Annotations);
    }

    [Fact]
    public void Parse_CrashOnlyAttribute()
    {
        string source = """
                        @crash_only
                        routine internal_divide!(a: S32, b: S32) -> S32
                          if b == 0
                            throw DivisionByZeroError()
                          return a // b
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        Assert.NotNull(@object: routine.Annotations);
        Assert.Contains(expected: "crash_only", collection: routine.Annotations);
    }

    [Fact]
    public void Parse_PreludeAttribute()
    {
        string source = """
                        @prelude
                        routine show(msg: Text)
                          pass
                          return
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        Assert.NotNull(@object: routine.Annotations);
        Assert.Contains(expected: "prelude", collection: routine.Annotations);
    }

    [Fact]
    public void Parse_StaticAttribute()
    {
        string source = """
                        @static
                        routine Math.pi() -> F64
                          return 3.14159265359_f64
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_InlineAttribute()
    {
        string source = """
                        @inline
                        routine add(a: S32, b: S32) -> S32
                          return a + b
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Parameterized Annotation Tests

    [Fact]
    public void Parse_ConfigAttributeTargetOs()
    {
        string source = """
                        @config(target_os: "windows")
                        routine get_config_path() -> Text
                          return "C:\\config.toml"
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        Assert.NotNull(@object: routine.Annotations);
    }

    [Fact]
    public void Parse_ConfigAttributeFeature()
    {
        string source = """
                        @config(feature: "debug")
                        routine debug_log(msg: Text)
                          show(f"[DEBUG] {msg}")
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_ConfigAttributeArchitecture()
    {
        string source = """
                        @config(target_arch: "x86_64")
                        routine simd_add(a: ValueList[F32, 4], b: ValueList[F32, 4]) -> ValueList[F32, 4]
                          pass
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_DeprecatedAttributeWithMessage()
    {
        string source = """
                        @deprecated(message: "Use new_function instead")
                        routine old_function()
                          pass
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Compound Annotation Tests

    [Fact]
    public void Parse_CompoundAttributes()
    {
        string source = """
                        @[readonly, crash_only]
                        routine validate!(value: S32) -> S32
                          if value < 0
                            throw ValidationError()
                          return value
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_CompoundAttributesMultiple()
    {
        string source = """
                        @[inline, readonly, prelude]
                        routine identity(x: S32) -> S32
                          return x
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_FinalAttribute()
    {
        string source = """
                        @final
                        routine EntityType.id() -> U64
                          return internal_object_id(me)
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        Assert.NotNull(@object: routine.Annotations);
        Assert.Contains(expected: "final", collection: routine.Annotations);
    }

    [Fact]
    public void Parse_GeneratedAttribute()
    {
        string source = """
                        @generated
                        routine Me.__ne__(you: Me) -> Bool
                          return not (me == you)
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        Assert.NotNull(@object: routine.Annotations);
        Assert.Contains(expected: "generated", collection: routine.Annotations);
    }

    [Fact]
    public void Parse_CompoundFinalDerived()
    {
        string source = """
                        @[readonly, final, generated]
                        routine EntityType.id() -> U64
                          return internal_object_id(me)
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_CompoundProtocolAnnotations()
    {
        string source = """
                        protocol EntityType obeys Diagnosable, Equatable
                          @[readonly, final, generated]
                          routine Me.id() -> U64

                          @[readonly, final, generated]
                          routine Me.__same__(you: Me) -> Bool

                          @[readonly, final, generated]
                          routine Me.__not_same__(you: Me) -> Bool
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Type Annotation Tests

    [Fact]
    public void Parse_AttributeOnRecord()
    {
        string source = """
                        @serializable
                        record Point
                          x: F32
                          y: F32
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_AttributeOnEntity()
    {
        string source = """
                        @serializable
                        entity User
                          name: Text
                          age: U32
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_AttributeOnProtocol()
    {
        string source = """
                        @prelude
                        protocol Displayable
                          @readonly
                          routine Me.display() -> Text
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_AttributeOnResident()
    {
        string source = """
                        @singleton
                        resident GlobalConfig
                          settings: Dict[Text, Text]
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Field Annotation Tests

    [Fact]
    public void Parse_AttributeOnField()
    {
        string source = """
                        record Config
                          @optional
                          name: Text

                          @default(42)
                          value: S32
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_AttributeOnEntityMemberVariable()
    {
        string source = """
                        entity User
                          @readonly
                          id: U64

                          @indexed
                          email: Text
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Multiple Annotation Lines Tests

    [Fact]
    public void Parse_MultipleAttributeLines()
    {
        string source = """
                        @readonly
                        @inline
                        @config(feature: "optimized")
                        routine fast_compute(x: S32) -> S32
                          return x * 2
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_AttributesOnTypeAndMethods()
    {
        string source = """
                        @derive(Debug)
                        record Calculator
                          value: S32

                        @readonly
                        routine Calculator.get() -> S32
                          return me.value

                        @writable
                        routine Calculator.set(v: S32)
                          me.value = v
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Visibility with Annotation Tests

    [Fact]
    public void Parse_VisibilityAndAttribute()
    {
        // open is the default visibility (not a keyword), so just use @readonly + routine
        string source = """
                        @readonly
                        routine Point.distance() -> F32
                          return 0.0_f32
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_PrivateWithAttribute()
    {
        string source = """
                        @inline
                        secret routine helper(x: S32) -> S32
                          return x * 2
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_InternalWithAttribute()
    {
        string source = """
                        @serializable
                        secret record InternalData
                          value: S32
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Protocol Method Annotations

    [Fact]
    public void Parse_ProtocolMethodAttributes()
    {
        string source = """
                        protocol Container
                          @readonly
                          routine Me.count() -> uaddr

                          @readonly
                          routine Me.is_empty() -> bool

                          @writable
                          routine Me.clear()
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Test Annotation Tests

    [Fact]
    public void Parse_TestAttribute()
    {
        string source = """
                        @test
                        routine test_addition()
                          verify!(1 + 1 == 2)
                          return
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_BenchAttribute()
    {
        string source = """
                        @bench
                        routine bench_sort()
                          var items = generate_random_list(1000)
                          sort(items)
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion
}
