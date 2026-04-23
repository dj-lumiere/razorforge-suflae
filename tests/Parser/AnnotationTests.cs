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
    /// <summary>
    /// Tests Parse_ReadonlyAttribute.
    /// </summary>

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
    /// <summary>
    /// Tests Parse_WritableAttribute.
    /// </summary>

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
    /// <summary>
    /// Tests Parse_CrashOnlyAttribute.
    /// </summary>

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
    /// <summary>
    /// Tests Parse_PreludeAttribute.
    /// </summary>

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
    /// <summary>
    /// Tests Parse_StaticAttribute.
    /// </summary>

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
    /// <summary>
    /// Tests Parse_InlineAttribute.
    /// </summary>

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
    /// <summary>
    /// Tests Parse_ConfigAttributeTargetOs.
    /// </summary>

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
    /// <summary>
    /// Tests Parse_ConfigAttributeFeature.
    /// </summary>

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
    /// <summary>
    /// Tests Parse_ConfigAttributeArchitecture.
    /// </summary>

    [Fact]
    public void Parse_ConfigAttributeArchitecture()
    {
        string source = """
                        @config(target_arch: "x86_64")
                        routine simd_add(a: Array[F32, 4], b: Array[F32, 4]) -> Array[F32, 4]
                          pass
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_DeprecatedAttributeWithMessage.
    /// </summary>

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
    /// <summary>
    /// Tests Parse_CompoundAttributes.
    /// </summary>

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
    /// <summary>
    /// Tests Parse_CompoundAttributesMultiple.
    /// </summary>

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
    /// <summary>
    /// Tests Parse_CompoundProtocolAnnotations.
    /// </summary>

    [Fact]
    public void Parse_CompoundProtocolAnnotations()
    {
        string source = """
                        protocol Identifiable
                          @readonly
                          routine Me.$same(you: Me) -> Bool

                          @readonly
                          routine Me.$notsame(you: Me) -> Bool
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Type Annotation Tests
    /// <summary>
    /// Tests Parse_AttributeOnProtocol.
    /// </summary>

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


    #endregion

    #region Member Variable Annotation Tests
    /// <summary>
    /// Tests Parse_AttributeOnField.
    /// </summary>

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
    /// <summary>
    /// Tests Parse_AttributeOnEntityMemberVariable.
    /// </summary>

    [Fact]
    public void Parse_AttributeOnEntityMemberVariable()
    {
        string source = """
                        entity User
                          @readonly
                          id: U64

                          @initonly
                          email: Text
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Multiple Annotation Lines Tests
    /// <summary>
    /// Tests Parse_MultipleAttributeLines.
    /// </summary>

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
    /// <summary>
    /// Tests Parse_AttributesOnTypeAndMethods.
    /// </summary>

    [Fact]
    public void Parse_AttributesOnTypeAndMethods()
    {
        string source = """
                        @deprecated(message: "Use NewCalculator")
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
    /// <summary>
    /// Tests Parse_VisibilityAndAttribute.
    /// </summary>

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
    /// <summary>
    /// Tests Parse_PrivateWithAttribute.
    /// </summary>

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
    /// <summary>
    /// Tests Parse_InternalWithAttribute.
    /// </summary>

    [Fact]
    public void Parse_InternalWithAttribute()
    {
        string source = """
                        @deprecated(message: "Use PublicData")
                        secret record InternalData
                          value: S32
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Protocol Member Routine Annotations
    /// <summary>
    /// Tests Parse_ProtocolMethodAttributes.
    /// </summary>

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
    /// <summary>
    /// Tests Parse_TestAttribute.
    /// </summary>

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
    /// <summary>
    /// Tests Parse_BenchAttribute.
    /// </summary>

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
