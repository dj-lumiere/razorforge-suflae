using Xunit;

namespace RazorForge.Tests.Parser;

using Compilers.Shared.AST;
using static TestHelpers;

/// <summary>
/// Tests for parsing variable declarations in Suflae:
/// let/var, type annotations, initializers, mutability.
/// Suflae uses indentation-based syntax.
/// </summary>
public class SuflaeVariableDeclarationTests
{
    #region Let (Immutable) Declarations

    [Fact]
    public void ParseSuflae_LetWithTypeAndInitializer()
    {
        string source = """
                        routine test()
                            let x: Integer = 42
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LetWithInferredType()
    {
        string source = """
                        routine test()
                            let x = 42
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LetWithComplexType()
    {
        string source = """
                        routine test()
                            let items: List<Integer> = [1, 2, 3]
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LetWithGenericType()
    {
        string source = """
                        routine test()
                            let map: Dict<Text, Integer> = Dict()
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LetWithMaybeType()
    {
        string source = """
                        routine test()
                            let value: Integer? = none
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LetWithFunctionCall()
    {
        string source = """
                        routine test()
                            let result = compute(10, 20)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LetWithExpression()
    {
        string source = """
                        routine test()
                            let sum = a + b * c
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Var (Mutable) Declarations

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
    public void ParseSuflae_MultipleLetDeclarations()
    {
        string source = """
                        routine test()
                            let a = 1
                            let b = 2
                            let c = 3
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_MixedDeclarations()
    {
        string source = """
                        routine test()
                            let constant = 100
                            var mutable = 0
                            let another_constant = 200
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Destructuring Declarations

    [Fact]
    public void ParseSuflae_LetDestructuringTuple()
    {
        string source = """
                        routine test()
                            let (x, y) = get_point()
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LetDestructuringRecord()
    {
        string source = """
                        routine test()
                            let (name, age) = Person(name: "Alice", age: 30)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LetNestedDestructuring()
    {
        string source = """
                        routine test()
                            let ((x, y), radius) = Circle(center: Point(5, 6), radius: 7)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LetDestructuringWithAlias()
    {
        string source = """
                        routine test()
                            let (center: c, radius: r) = circle
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LetDestructuringWithWildcard()
    {
        string source = """
                        routine test()
                            let (_, y) = get_point()
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
                            let a: S8 = 1
                            let b: S16 = 2
                            let c: S32 = 3
                            let d: S64 = 4
                            let e: U8 = 5
                            let f: U16 = 6
                            let g: U32 = 7
                            let h: U64 = 8
                            let i: Decimal = 9.0
                            let j: bool = true
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_TextType()
    {
        string source = """
                        routine test()
                            let name: Text = "Alice"
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_NestedGenericType()
    {
        string source = """
                        routine test()
                            let nested: Dict<Text, List<Integer>> = Dict()
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
                            let dec = 42
                            let hex = 0xFF
                            let bin = 0b1010
                            let oct = 0o77
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_FloatLiterals()
    {
        string source = """
                        routine test()
                            let a = 3.14
                            let b = 1.0e10
                            let c = 2.5e-3
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_StringLiterals()
    {
        string source = """
                        routine test()
                            let simple = "hello"
                            let escaped = "line1\nline2"
                            let formatted = f"value = {x}"
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_BooleanLiterals()
    {
        string source = """
                        routine test()
                            let t = true
                            let f = false
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_NoneLiteral()
    {
        string source = """
                        routine test()
                            let x: Integer? = None
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ListLiteral()
    {
        string source = """
                        routine test()
                            let items = [1, 2, 3, 4, 5]
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_EmptyListLiteral()
    {
        string source = """
                        routine test()
                            let empty: List<Integer> = []
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Field Declarations in Types

    [Fact]
    public void ParseSuflae_RecordFields()
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
    public void ParseSuflae_EntityVarFields()
    {
        string source = """
                        entity Counter
                            var count: Integer
                        """;

        Program program = AssertParsesSuflae(source: source);
        EntityDeclaration entity = GetDeclaration<EntityDeclaration>(program: program);
        var fields = entity.Members
                           .OfType<VariableDeclaration>()
                           .ToList();
        Assert.Single(collection: fields);
        Assert.True(condition: fields[index: 0].IsMutable);
    }

    [Fact]
    public void ParseSuflae_EntityLetFields()
    {
        string source = """
                        entity User
                            let id: U64
                            var name: Text
                        """;

        Program program = AssertParsesSuflae(source: source);
        EntityDeclaration entity = GetDeclaration<EntityDeclaration>(program: program);
        var fields = entity.Members
                           .OfType<VariableDeclaration>()
                           .ToList();
        Assert.Equal(expected: 2, actual: fields.Count);
        Assert.False(condition: fields[index: 0].IsMutable);
        Assert.True(condition: fields[index: 1].IsMutable);
    }

    #endregion

    #region Complex Initializers

    [Fact]
    public void ParseSuflae_ConstructorCall()
    {
        string source = """
                        routine test()
                            let point = Point(x: 10.0, y: 20.0)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_MethodCallChain()
    {
        string source = """
                        routine test()
                            let result = data.filter().map().collect()
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ConditionalInitializer()
    {
        string source = """
                        routine test()
                            let value = if condition then 1 else 0
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenExpressionInitializer()
    {
        string source = """
                        routine test()
                            let description = when status
                                is ACTIVE => "running"
                                else => "stopped"
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion
}
