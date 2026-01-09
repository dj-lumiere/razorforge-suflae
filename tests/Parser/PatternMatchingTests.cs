using Xunit;

namespace RazorForge.Tests.Parser;

using Compilers.Shared.AST;
using static TestHelpers;

/// <summary>
/// Tests for parsing pattern matching in RazorForge:
/// when expressions, is keyword, guards, variant destructuring, literal patterns.
/// </summary>
public class PatternMatchingTests
{
    #region Basic When Statement Tests

    [Fact]
    public void Parse_SimpleWhen()
    {
        string source = """
                        routine test(x: S32) {
                            when x {
                                1 => show("one")
                                2 => show("two")
                                else => show("other")
                            }
                        }
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        var body = routine.Body as BlockStatement;
        Assert.NotNull(@object: body);
        WhenStatement whenStmt = body.Statements
                                     .OfType<WhenStatement>()
                                     .First();
        Assert.NotNull(@object: whenStmt);
    }

    [Fact]
    public void Parse_WhenWithBlock()
    {
        string source = """
                        routine test(x: S32) {
                            when x {
                                1 => {
                                    show("one")
                                    do_something()
                                }
                                else => show("other")
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WhenAsExpression()
    {
        string source = """
                        routine describe(x: S32) -> Text {
                            return when x {
                                0 => "zero"
                                1 => "one"
                                else => "many"
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WhenWithAssignment()
    {
        string source = """
                        routine test(status: Status) {
                            let description = when status {
                                is Status.ACTIVE => "Running"
                                else => "Not running"
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Is Type Pattern Tests

    [Fact]
    public void Parse_WhenIsType()
    {
        string source = """
                        routine test(value: Any) {
                            when value {
                                is S32 n => show(f"Integer: {n}")
                                is Text t => show(f"Text: {t}")
                                else => show("Unknown type")
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WhenIsTypeWithoutBinding()
    {
        string source = """
                        routine test(value: Any) {
                            when value {
                                is S32 => show("It's an integer")
                                is Text => show("It's text")
                                else => show("Unknown")
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WhenIsCustomType()
    {
        string source = """
                        routine test(value: Any) {
                            when value {
                                is Point p => show(f"Point: ({p.x}, {p.y})")
                                is Circle c => show(f"Circle: r={c.radius}")
                                else => show("Unknown shape")
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Choice Pattern Tests

    [Fact]
    public void Parse_WhenChoice()
    {
        string source = """
                        routine handle(status: Status) {
                            when status {
                                is Status.PENDING => show("Waiting...")
                                is Status.ACTIVE => show("In progress")
                                is Status.COMPLETED => show("Done!")
                                is Status.CANCELLED => show("Cancelled")
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WhenChoiceShorthand()
    {
        string source = """
                        routine handle(status: Status) {
                            when status {
                                is PENDING => show("Waiting...")
                                is ACTIVE => show("In progress")
                                is COMPLETED => show("Done!")
                                is CANCELLED => show("Cancelled")
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Variant Pattern Tests

    [Fact]
    public void Parse_WhenVariant()
    {
        string source = """
                        routine handle(msg: Message) {
                            when msg {
                                is Message.TEXT content => process(content)
                                is Message.NUMBER n => compute(n)
                                is Message.QUIT => exit()
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WhenVariantShorthand()
    {
        string source = """
                        routine handle(msg: Message) {
                            when msg {
                                is TEXT content => process(content)
                                is NUMBER n => compute(n)
                                is QUIT => exit()
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WhenVariantDestructuring()
    {
        string source = """
                        routine handle(shape: Shape) {
                            when shape {
                                is CIRCLE (center, radius) => {
                                    show(f"Circle at ({center.x}, {center.y}) with radius {radius}")
                                }
                                is RECTANGLE (top_left, size) => {
                                    show(f"Rectangle at ({top_left.x}, {top_left.y})")
                                }
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WhenVariantDestructuringWithAlias()
    {
        string source = """
                        routine handle(shape: Shape) {
                            when shape {
                                is CIRCLE (center: c, radius: r) => {
                                    show(f"Circle: center={c}, r={r}")
                                }
                                else => show("Not a circle")
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WhenNestedDestructuring()
    {
        string source = """
                        routine handle(shape: Shape) {
                            when shape {
                                is CIRCLE ((x, y), radius) => {
                                    show(f"Circle at ({x}, {y}) with radius {radius}")
                                }
                                is RECTANGLE ((x, y), (width, height)) => {
                                    show(f"Rectangle at ({x}, {y}), {width}x{height}")
                                }
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Guard Clause Tests

    [Fact]
    public void Parse_WhenWithGuard()
    {
        string source = """
                        routine classify(n: S32) {
                            when n {
                                is S32 x if x > 0 => show("Positive")
                                is S32 x if x < 0 => show("Negative")
                                is S32 x => show("Zero")
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WhenLiteralWithGuard()
    {
        string source = """
                        routine test(x: S32, y: S32) {
                            when x {
                                0 if y > 0 => show("x=0, y positive")
                                0 => show("x=0, y non-positive")
                                else => show("x not zero")
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WhenTypeWithComplexGuard()
    {
        string source = """
                        routine handle(error: Crashable) {
                            when error {
                                is NetworkError e if e.code == 404 => show("Not found")
                                is NetworkError e if e.code >= 500 => show("Server error")
                                is Crashable e => show(f"Error: {e.message()}")
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Maybe/Result/Lookup Pattern Tests

    [Fact]
    public void Parse_WhenMaybe()
    {
        string source = """
                        routine handle(value: User?) {
                            when value {
                                is None => show("User not found")
                                else user => show(f"Found: {user.name}")
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WhenResult()
    {
        string source = """
                        routine handle(result: Result<User>) {
                            when result {
                                is Crashable err => show(f"Error: {err.message()}")
                                else user => show(f"Found: {user.name}")
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WhenResultSpecificError()
    {
        string source = """
                        routine handle(result: Result<File>) {
                            when result {
                                is FileNotFoundError e => show(f"Not found: {e.path}")
                                is PermissionError e => show(f"Access denied: {e.path}")
                                is Crashable e => show(f"Unknown error: {e.message()}")
                                else file => file.read()
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WhenLookup()
    {
        string source = """
                        routine handle(result: Lookup<User>) {
                            when result {
                                is Crashable e => show(f"Error: {e.message()}")
                                is None => show("User not found")
                                else user => show(f"Found: {user.name}")
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WhenLookupSpecificErrors()
    {
        string source = """
                        routine handle(result: Lookup<Config>) {
                            when result {
                                is ValidationError e => stop!(f"Invalid config name: {e.message}")
                                is IOError e => show(f"IO error: {e.message}")
                                is Crashable e => breach!()
                                is None => use_default_config()
                                else config => apply(config)
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Literal Pattern Tests

    [Fact]
    public void Parse_WhenLiteralInteger()
    {
        string source = """
                        routine describe(n: S32) -> Text {
                            return when n {
                                0 => "zero"
                                1 => "one"
                                2 => "two"
                                else => "many"
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WhenLiteralText()
    {
        string source = """
                        routine handle(cmd: Text) {
                            when cmd {
                                "help" => show_help()
                                "version" => show_version()
                                "quit" => exit()
                                else => show("Unknown command")
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WhenLiteralBool()
    {
        string source = """
                        routine test(flag: bool) {
                            when flag {
                                true => show("enabled")
                                false => show("disabled")
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Wildcard Pattern Tests

    [Fact]
    public void Parse_WhenWildcard()
    {
        string source = """
                        routine handle(result: Result<S32>) {
                            when result {
                                is Crashable => absent
                                else value => value
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WhenWildcardInDestructuring()
    {
        string source = """
                        routine handle(shape: Shape) {
                            when shape {
                                is CIRCLE (_, radius) => {
                                    show(f"Radius: {radius}")
                                }
                                else => show("Not a circle")
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Else Binding Tests

    [Fact]
    public void Parse_WhenElseWithBinding()
    {
        string source = """
                        routine handle(value: Maybe<User>) {
                            when value {
                                is None => show("Not found")
                                else u => show(u.name)
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WhenElseWithBlock()
    {
        string source = """
                        routine handle(value: Maybe<User>) {
                            when value {
                                is None => {
                                    show("Not found")
                                    return
                                }
                                else user => {
                                    show(user.name)
                                    process(user)
                                }
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Let Destructuring Tests

    [Fact]
    public void Parse_LetDestructuringRecord()
    {
        string source = """
                        routine test() {
                            let (x, y) = Point(x: 5, y: 6)
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_LetDestructuringWithAlias()
    {
        string source = """
                        routine test() {
                            let (center: c, radius: r) = Circle(center: Point(5, 6), radius: 7)
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

    #endregion

    #region Complex Pattern Tests

    [Fact]
    public void Parse_ComplexNestedPatterns()
    {
        string source = """
                        routine process(result: Lookup<Shape>) {
                            when result {
                                is Crashable e => {
                                    log_error(e)
                                    return
                                }
                                is None => {
                                    show("No shape found")
                                    return
                                }
                                else shape => {
                                    when shape {
                                        is CIRCLE (center, radius) if radius > 10 => {
                                            show("Large circle")
                                        }
                                        is CIRCLE (center, radius) => {
                                            show("Small circle")
                                        }
                                        is RECTANGLE ((x, y), (w, h)) if w == h => {
                                            show("Square")
                                        }
                                        else => show("Other shape")
                                    }
                                }
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    [Fact]
    public void Parse_WhenWithMultipleStatements()
    {
        string source = """
                        routine handle(event: Event) {
                            when event {
                                is CLICK pos => {
                                    let x = pos.x
                                    let y = pos.y
                                    handle_click(x, y)
                                }
                                is KEY (code, modifiers) => {
                                    if modifiers.ctrl {
                                        handle_ctrl_key(code)
                                    } else {
                                        handle_key(code)
                                    }
                                }
                                else => {}
                            }
                        }
                        """;

        AssertParses(source: source);
    }

    #endregion
}
