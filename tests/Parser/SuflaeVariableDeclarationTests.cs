using Xunit;

namespace RazorForge.Tests.Parser;

using SyntaxTree;
using static TestHelpers;

/// <summary>
/// Tests for parsing variable declarations in Suflae:
/// var, type annotations, initializers, mutability.
/// Suflae uses indentation-based syntax.
/// </summary>
public class SuflaeVariableDeclarationTests
{
    #region Variable Declarations

    [Fact]
    public void ParseSuflae_VarWithTypeAndInitializer()
    {
        string source = """
                        routine test()
                          var x: Integer = 42
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_VarWithInferredType()
    {
        string source = """
                        routine test()
                          var x = 42
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_VarWithComplexType()
    {
        string source = """
                        routine test()
                          var items: List[Integer] = [1, 2, 3]
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_VarWithGenericType()
    {
        string source = """
                        routine test()
                          var map: Dict[Text, Integer] = Dict()
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_VarWithMaybeType()
    {
        string source = """
                        routine test()
                          var value: Integer? = none
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_VarWithFunctionCall()
    {
        string source = """
                        routine test()
                          var result = compute(10, 20)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_VarWithExpression()
    {
        string source = """
                        routine test()
                          var sum = a + b * c
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_VarWithMutation()
    {
        string source = """
                        routine test()
                          var count = 0
                          count = 1
                          count += 1
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_VarWithoutInitializer()
    {
        string source = """
                        routine test()
                          var x: Integer
                          x = 42
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Multiple Declarations

    [Fact]
    public void ParseSuflae_MultipleVarDeclarations()
    {
        string source = """
                        routine test()
                          var a = 1
                          var b = 2
                          var c = 3
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Destructuring Declarations

    [Fact]
    public void ParseSuflae_VarDestructuringTuple()
    {
        string source = """
                        routine test()
                          var (x, y) = get_point()
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_VarDestructuringRecord()
    {
        string source = """
                        routine test()
                          var (name, age) = Person(name: "Alice", age: 30)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_VarNestedDestructuring()
    {
        string source = """
                        routine test()
                          var ((x, y), radius) = Circle(center: Point(5, 6), radius: 7)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_VarDestructuringWithAlias()
    {
        string source = """
                        routine test()
                          var (center: c, radius: r) = circle
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_VarDestructuringWithWildcard()
    {
        string source = """
                        routine test()
                          var (_, y) = get_point()
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Type Annotations

    [Fact]
    public void ParseSuflae_PrimitiveTypes()
    {
        string source = """
                        routine test()
                          var a: S8 = 1
                          var b: S16 = 2
                          var c: S32 = 3
                          var d: S64 = 4
                          var e: U8 = 5
                          var f: U16 = 6
                          var g: U32 = 7
                          var h: U64 = 8
                          var i: Decimal = 9.0
                          var j: bool = true
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_TextType()
    {
        string source = """
                        routine test()
                          var name: Text = "Alice"
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_NestedGenericType()
    {
        string source = """
                        routine test()
                          var nested: Dict[Text, List[Integer]] = Dict()
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Literal Initializers

    [Fact]
    public void ParseSuflae_IntegerLiterals()
    {
        string source = """
                        routine test()
                          var dec = 42
                          var hex = 0xFF
                          var bin = 0b1010
                          var oct = 0o77
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_FloatLiterals()
    {
        string source = """
                        routine test()
                          var a = 3.14
                          var b = 1.0e10
                          var c = 2.5e-3
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_StringLiterals()
    {
        string source = """
                        routine test()
                          var simple = "hello"
                          var escaped = "line1\nline2"
                          var formatted = f"value = {x}"
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_BooleanLiterals()
    {
        string source = """
                        routine test()
                          var t = true
                          var f = false
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_NoneLiteral()
    {
        string source = """
                        routine test()
                          var x: Integer? = None
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ListLiteral()
    {
        string source = """
                        routine test()
                          var items = [1, 2, 3, 4, 5]
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_EmptyListLiteral()
    {
        string source = """
                        routine test()
                          var empty: List[Integer] = []
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Field Declarations in Types

    [Fact]
    public void ParseSuflae_RecordMemberVariables()
    {
        string source = """
                        record Point
                          x: F32
                          y: F32
                        """;

        Program program = AssertParsesSuflae(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);
        Assert.Equal(expected: 2, actual: record.Members.Count);
    }

    [Fact]
    public void ParseSuflae_EntityVarMemberVariables_Rejected()
    {
        // var keywords are no longer allowed in entity bodies
        // MemberVariables use 'name: Type' syntax without var keywords
        string source = """
                        entity Counter
                          var count: Integer
                        """;

        (_, var parser) = ParseSuflaeWithErrors(source: source);
        Assert.True(condition: parser.HasErrors, userMessage: "Expected parse errors for var in entity body");
    }

    [Fact]
    public void ParseSuflae_EntityMultipleVarMemberVariables_Rejected()
    {
        // var keywords are no longer allowed in entity bodies
        // MemberVariables use 'name: Type' syntax without var keywords
        string source = """
                        entity User
                          var id: U64
                          var name: Text
                        """;

        (_, var parser) = ParseSuflaeWithErrors(source: source);
        Assert.True(condition: parser.HasErrors, userMessage: "Expected parse errors for var in entity body");
    }

    #endregion

    #region Complex Initializers

    [Fact]
    public void ParseSuflae_ConstructorCall()
    {
        string source = """
                        routine test()
                          var point = Point(x: 10.0, y: 20.0)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_MethodCallChain()
    {
        string source = """
                        routine test()
                          var result = data.filter().map().collect()
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ConditionalInitializer()
    {
        string source = """
                        routine test()
                          var value = if condition then 1 else 0
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenExpressionInitializer()
    {
        string source = """
                        routine test()
                          var description = when status
                            is ACTIVE => "running"
                            else => "stopped"
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion
}
