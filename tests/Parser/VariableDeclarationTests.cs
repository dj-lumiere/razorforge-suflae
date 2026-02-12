using Xunit;

namespace RazorForge.Tests.Parser;

using Compilers.Shared.AST;
using static TestHelpers;

/// <summary>
/// Tests for parsing variable declarations in RazorForge:
/// let/var, type annotations, initializers, mutability.
/// </summary>
public class VariableDeclarationTests
{
    #region Let (Immutable) Declarations

    [Fact]
    public void Parse_LetWithTypeAndInitializer()
    {
        string source = """
                        routine test() {
                            let x: S32 = 42
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LetWithInferredType()
    {
        string source = """
                        routine test() {
                            let x = 42
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LetWithComplexType()
    {
        string source = """
                        routine test() {
                            let items: List<S32> = [1, 2, 3]
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LetWithGenericType()
    {
        string source = """
                        routine test() {
                            let map: Dict<Text, S32> = Dict()
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LetWithMaybeType()
    {
        string source = """
                        routine test() {
                            let value: S32? = None
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LetWithFunctionCall()
    {
        string source = """
                        routine test() {
                            let result = compute(10, 20)
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LetWithExpression()
    {
        string source = """
                        routine test() {
                            let sum = a + b * c
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Var (Mutable) Declarations

    [Fact]
    public void Parse_VarWithTypeAndInitializer()
    {
        string source = """
                        routine test() {
                            var x: S32 = 42
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_VarWithInferredType()
    {
        string source = """
                        routine test() {
                            var x = 42
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_VarWithMutation()
    {
        string source = """
                        routine test() {
                            var count = 0
                            count = 1
                            count += 1
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_VarWithoutInitializer()
    {
        string source = """
                        routine test() {
                            var x: S32
                            x = 42
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Multiple Declarations

    [Fact]
    public void Parse_MultipleLetDeclarations()
    {
        string source = """
                        routine test() {
                            let a = 1
                            let b = 2
                            let c = 3
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_MixedDeclarations()
    {
        string source = """
                        routine test() {
                            let constant = 100
                            var mutable = 0
                            let another_constant = 200
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Destructuring Declarations

    [Fact]
    public void Parse_LetDestructuringTuple()
    {
        string source = """
                        routine test() {
                            let (x, y) = get_point()
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LetDestructuringRecord()
    {
        string source = """
                        routine test() {
                            let (name, age) = Person(name: "Alice", age: 30)
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LetNestedDestructuring()
    {
        string source = """
                        routine test() {
                            let ((x, y), radius) = Circle(center: Point(5, 6), radius: 7)
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LetDestructuringWithAlias()
    {
        string source = """
                        routine test() {
                            let (center: c, radius: r) = circle
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LetDestructuringWithWildcard()
    {
        string source = """
                        routine test() {
                            let (_, y) = get_point()
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Type Annotations

    [Fact]
    public void Parse_PrimitiveTypes()
    {
        string source = """
                        routine test() {
                            let a: S8 = 1
                            let b: S16 = 2
                            let c: S32 = 3
                            let d: S64 = 4
                            let e: U8 = 5
                            let f: U16 = 6
                            let g: U32 = 7
                            let h: U64 = 8
                            let i: F32 = 9.0_f32
                            let j: F64 = 10.0_f64
                            let k: bool = true
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_TextType()
    {
        string source = """
                        routine test() {
                            let name: Text = "Alice"
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_ConstGenericType()
    {
        string source = """
                        routine test() {
                            let data: ValueBytes<4> = bytes
                        }
                        """;

        AssertParses(source: source);
    }


    [Fact]
    public void Parse_NestedGenericType()
    {
        string source = """
                        routine test() {
                            let nested: Dict<Text, List<S32>> = Dict()
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Literal Initializers

    [Fact]
    public void Parse_IntegerLiterals()
    {
        string source = """
                        routine test() {
                            let dec = 42
                            let hex = 0xFF
                            let bin = 0b1010
                            let oct = 0o77
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_FloatLiterals()
    {
        string source = """
                        routine test() {
                            let a = 3.14
                            let b = 1.0e10
                            let c = 2.5e-3
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_TypedNumericLiterals()
    {
        string source = """
                        routine test() {
                            let a = 42_s32
                            let b = 100_u64
                            let c = 3.14_f32
                            let d = 2.718_f64
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_StringLiterals()
    {
        string source = """
                        routine test() {
                            let simple = "hello"
                            let escaped = "line1\nline2"
                            let formatted = f"value = {x}"
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_BooleanLiterals()
    {
        string source = """
                        routine test() {
                            let t = true
                            let f = false
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_NoneLiteral()
    {
        string source = """
                        routine test() {
                            let x: S32? = none
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_ListLiteral()
    {
        string source = """
                        routine test() {
                            let items = [1, 2, 3, 4, 5]
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_EmptyListLiteral()
    {
        string source = """
                        routine test() {
                            let empty: List<S32> = []
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LongTextEscapeLiteral()
    {
        string source = """
                        routine test() {
                           let a = "asdfasdfgasdfasdfasdfasdfsjhdygfckiujsdhfokiulsdjfjhoiwsedhfweolfwefgolijwserdgvoiwe\
                           asdfljhwe4foitrujwergopijwergfolijuwerifopwejgfoiweujiogfwehrjfgoiwehjfoij"
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Field Declarations in Types

    [Fact]
    public void Parse_RecordFields()
    {
        string source = """
                        record Point {
                            x: F32
                            y: F32
                        }
                        """;

        Program program = AssertParses(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);
        Assert.Equal(expected: 2, actual: record.Members.Count);
    }

    [Fact]
    public void Parse_EntityVarFields()
    {
        string source = """
                        entity Counter {
                            var count: S32
                        }
                        """;

        Program program = AssertParses(source: source);
        EntityDeclaration entity = GetDeclaration<EntityDeclaration>(program: program);
        var fields = entity.Members
                           .OfType<VariableDeclaration>()
                           .ToList();
        Assert.Single(collection: fields);
        Assert.True(condition: fields[index: 0].IsMutable);
    }

    [Fact]
    public void Parse_EntityLetFields()
    {
        string source = """
                        entity User {
                            let id: U64
                            var name: Text
                        }
                        """;

        Program program = AssertParses(source: source);
        EntityDeclaration entity = GetDeclaration<EntityDeclaration>(program: program);
        var fields = entity.Members
                           .OfType<VariableDeclaration>()
                           .ToList();
        Assert.Equal(expected: 2, actual: fields.Count);
        Assert.False(condition: fields[index: 0].IsMutable);
        Assert.True(condition: fields[index: 1].IsMutable);
    }

    [Fact]
    public void Parse_ResidentFields()
    {
        string source = """
                        resident GlobalState {
                            var counter: S32
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Complex Initializers

    [Fact]
    public void Parse_ConstructorCall()
    {
        string source = """
                        routine test() {
                            let point = Point(x: 10.0, y: 20.0)
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_MethodCallChain()
    {
        string source = """
                        routine test() {
                            let result = data.where().select().to_list()
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_ConditionalInitializer()
    {
        string source = """
                        routine test() {
                            let value = if condition then 1 else 0
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WhenExpressionInitializer()
    {
        string source = """
                        routine test() {
                            let description = when status {
                                is ACTIVE => "running"
                                else => "stopped"
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion
}
