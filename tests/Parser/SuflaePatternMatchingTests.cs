using Xunit;

namespace RazorForge.Tests.Parser;

using SyntaxTree;
using static TestHelpers;

/// <summary>
/// Tests for parsing pattern matching in Suflae:
/// when expressions, is keyword, guards, variant destructuring, literal patterns.
/// Suflae uses indentation-based syntax with colons.
/// </summary>
public class SuflaePatternMatchingTests
{
    #region Basic When Statement Tests

    [Fact]
    public void ParseSuflae_SimpleWhen()
    {
        string source = """
                        routine test(x: Integer)
                          when x
                            1 => show("one")
                            2 => show("two")
                            else => show("other")
                        """;

        Program program = AssertParsesSuflae(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        var body = routine.Body as BlockStatement;
        Assert.NotNull(@object: body);
        WhenStatement whenStmt = body.Statements
                                     .OfType<WhenStatement>()
                                     .First();
        Assert.NotNull(@object: whenStmt);
    }

    [Fact]
    public void ParseSuflae_WhenWithBlock()
    {
        string source = """
                        routine test(x: Integer)
                          when x
                            1 =>
                              show("one")
                              do_something()
                            else => show("other")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenAsExpression()
    {
        string source = """
                        routine describe(x: Integer) -> Text
                          return when x
                            0 => "zero"
                            1 => "one"
                            else => "many"
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenWithAssignment()
    {
        string source = """
                        routine test(status: Status)
                          var description = when status
                            is ACTIVE => "Running"
                            else => "Not running"
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Is Type Pattern Tests

    [Fact]
    public void ParseSuflae_WhenIsType()
    {
        string source = """
                        routine test(value: Any)
                          when value
                            is Integer n => show(f"Integer: {n}")
                            is Text t => show(f"Text: {t}")
                            else => show("Unknown type")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenIsTypeWithoutBinding()
    {
        string source = """
                        routine test(value: Any)
                          when value
                            is Integer => show("It's an integer")
                            is Text => show("It's text")
                            else => show("Unknown")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenIsCustomType()
    {
        string source = """
                        routine test(value: Any)
                          when value
                            is Point p => show(f"Point: ({p.x}, {p.y})")
                            is Circle c => show(f"Circle: r={c.radius}")
                            else => show("Unknown shape")
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Choice Pattern Tests

    [Fact]
    public void ParseSuflae_WhenChoice()
    {
        string source = """
                        routine handle(status: Status)
                          when status
                            is Status.PENDING => show("Waiting...")
                            is Status.ACTIVE => show("In progress")
                            is Status.COMPLETED => show("Done!")
                            is Status.CANCELLED => show("Cancelled")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenChoiceShorthand()
    {
        string source = """
                        routine handle(status: Status)
                          when status
                            is PENDING => show("Waiting...")
                            is ACTIVE => show("In progress")
                            is COMPLETED => show("Done!")
                            is CANCELLED => show("Cancelled")
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Variant Pattern Tests

    [Fact]
    public void ParseSuflae_WhenVariant()
    {
        string source = """
                        routine handle(msg: Message)
                          when msg
                            is TEXT content => process(content)
                            is NUMBER n => compute(n)
                            is QUIT => exit()
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenVariantDestructuring()
    {
        string source = """
                        routine handle(shape: Shape)
                          when shape
                            is CIRCLE (center, radius) =>
                              show(f"Circle at ({center.x}, {center.y}) with radius {radius}")
                            is RECTANGLE (top_left, size) =>
                              show(f"Rectangle at ({top_left.x}, {top_left.y})")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenVariantDestructuringWithAlias()
    {
        string source = """
                        routine handle(shape: Shape)
                          when shape
                            is CIRCLE (center: c, radius: r) =>
                              show(f"Circle: center={c}, r={r}")
                            else => show("Not a circle")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenNestedDestructuring()
    {
        string source = """
                        routine handle(shape: Shape)
                          when shape
                            is CIRCLE ((x, y), radius) =>
                              show(f"Circle at ({x}, {y}) with radius {radius}")
                            is RECTANGLE ((x, y), (width, height)) =>
                              show(f"Rectangle at ({x}, {y}), {width}x{height}")
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Guard Clause Tests

    [Fact]
    public void ParseSuflae_WhenWithGuard()
    {
        string source = """
                        routine classify(n: Integer)
                          when n
                            is Integer x if x > 0 => show("Positive")
                            is Integer x if x < 0 => show("Negative")
                            is Integer x => show("Zero")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenLiteralWithGuard()
    {
        string source = """
                        routine test(x: Integer, y: Integer)
                          when x
                            0 if y > 0 => show("x=0, y positive")
                            0 => show("x=0, y non-positive")
                            else => show("x not zero")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenTypeWithComplexGuard()
    {
        string source = """
                        routine handle(error: Crashable)
                          when error
                            is NetworkError e if e.code == 404 => show("Not found")
                            is NetworkError e if e.code >= 500 => show("Server error")
                            is Crashable e => show(f"Error: {e.message()}")
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Maybe/Result/Lookup Pattern Tests

    [Fact]
    public void ParseSuflae_WhenMaybe()
    {
        string source = """
                        routine handle(value: User?)
                          when value
                            is None => show("User not found")
                            else user => show(f"Found: {user.name}")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenResult()
    {
        string source = """
                        routine handle(result: Result[User])
                          when result
                            is Crashable err => show(f"Error: {err.message()}")
                            else user => show(f"Found: {user.name}")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenResultSpecificError()
    {
        string source = """
                        routine handle(result: Result[File])
                          when result
                            is FileNotFoundError e => show(f"Not found: {e.path}")
                            is PermissionError e => show(f"Access denied: {e.path}")
                            is Crashable e => show(f"Unknown error: {e.message()}")
                            else file => file.read()
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenLookup()
    {
        string source = """
                        routine handle(result: Lookup[User])
                          when result
                            is Crashable e => show(f"Error: {e.message()}")
                            is None => show("User not found")
                            else user => show(f"Found: {user.name}")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenLookupSpecificErrors()
    {
        string source = """
                        routine handle(result: Lookup[Config])
                          when result
                            is ValidationError e => stop!(f"Invalid config name: {e.message}")
                            is IOError e => show(f"IO error: {e.message}")
                            is Crashable e => breach!()
                            is None => use_default_config()
                            else config => apply(config)
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Literal Pattern Tests

    [Fact]
    public void ParseSuflae_WhenLiteralInteger()
    {
        string source = """
                        routine describe(n: Integer) -> Text
                          return when n
                            0 => "zero"
                            1 => "one"
                            2 => "two"
                            else => "many"
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenLiteralText()
    {
        string source = """
                        routine handle(cmd: Text)
                          when cmd
                            "help" => show_help()
                            "version" => show_version()
                            "quit" => exit()
                            else => show("Unknown command")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenLiteralBool()
    {
        string source = """
                        routine test(flag: bool)
                          when flag
                            true => show("enabled")
                            false => show("disabled")
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Wildcard Pattern Tests

    [Fact]
    public void ParseSuflae_WhenWildcard()
    {
        string source = """
                        routine handle(result: Result[Integer])
                          when result
                            is Crashable => absent
                            else value => value
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenWildcardInDestructuring()
    {
        string source = """
                        routine handle(shape: Shape)
                          when shape
                            is CIRCLE (_, radius) =>
                              show(f"Radius: {radius}")
                            else => show("Not a circle")
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Else Binding Tests

    [Fact]
    public void ParseSuflae_WhenElseWithBinding()
    {
        string source = """
                        routine handle(value: Maybe[User])
                          when value
                            is None => show("Not found")
                            else u => show(u.name)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenElseWithBlock()
    {
        string source = """
                        routine handle(value: Maybe[User])
                          when value
                            is None =>
                              show("Not found")
                              return
                            else user =>
                              show(user.name)
                              process(user)
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Let Destructuring Tests

    [Fact]
    public void ParseSuflae_LetDestructuringRecord()
    {
        string source = """
                        routine test()
                          var (x, y) = Point(x: 5, y: 6)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LetDestructuringWithAlias()
    {
        string source = """
                        routine test()
                          var (center: c, radius: r) = Circle(center: Point(5, 6), radius: 7)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LetNestedDestructuring()
    {
        string source = """
                        routine test()
                          var ((x, y), radius) = Circle(center: Point(5, 6), radius: 7)
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Multi-line Arm Tests

    [Fact]
    public void ParseSuflae_WhenMultiLineArm()
    {
        // Fixed: 'becomes' should only be used in multi-statement blocks, not after '=>'
        // The else clause uses '=>' for single expression (no becomes needed)
        string source = """
                        routine test(value: Integer)
                          var result = when value
                            is A =>
                              var x = compute()
                              becomes transform(x)
                            else => default()
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Complex Pattern Tests

    [Fact]
    public void ParseSuflae_ComplexNestedPatterns()
    {
        string source = """
                        routine process(result: Lookup[Shape])
                          when result
                            is Crashable e =>
                              log_error(e)
                              return
                            is None =>
                              show("No shape found")
                              return
                            else shape =>
                              when shape
                                is CIRCLE (center, radius) if radius > 10 =>
                                  show("Large circle")
                                is CIRCLE (center, radius) =>
                                  show("Small circle")
                                is RECTANGLE ((x, y), (w, h)) if w == h =>
                                  show("Square")
                                else => show("Other shape")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenWithMultipleStatements()
    {
        string source = """
                        routine handle(event: Event)
                          when event
                            is CLICK pos =>
                              var x = pos.x
                              var y = pos.y
                              handle_click(x, y)
                            is KEY (code, modifiers) =>
                              if modifiers.ctrl
                                handle_ctrl_key(code)
                              else
                                handle_key(code)
                            else =>
                              pass
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Comparison Pattern Tests

    [Fact]
    public void ParseSuflae_WhenComparisonNotEqual()
    {
        string source = """
                        routine test(x: Integer)
                          when x
                            != 0 => show("non-zero")
                            else => show("zero")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenComparisonLessThan()
    {
        string source = """
                        routine classify(n: Integer)
                          when n
                            < 0 => show("negative")
                            == 0 => show("zero")
                            else => show("positive")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenComparisonGreaterThan()
    {
        string source = """
                        routine classify(n: Integer) -> Text
                          return when n
                            > 100 => "large"
                            > 10 => "medium"
                            else => "small"
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenComparisonLessThanOrEqual()
    {
        string source = """
                        routine grade(score: Integer) -> Text
                          return when score
                            <= 50 => "fail"
                            <= 70 => "pass"
                            <= 90 => "good"
                            else => "excellent"
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenComparisonGreaterThanOrEqual()
    {
        string source = """
                        routine classify(temp: Integer) -> Text
                          return when temp
                            >= 100 => "boiling"
                            >= 30 => "hot"
                            >= 0 => "cold"
                            else => "freezing"
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenComparisonStrictEqual()
    {
        string source = """
                        routine test(a: Data, b: Data)
                          when a
                            === b => show("same reference")
                            else => show("different reference")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenComparisonStrictNotEqual()
    {
        string source = """
                        routine test(a: Data, b: Data)
                          when a
                            !== b => show("different reference")
                            else => show("same reference")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenComparisonWithMemberAccess()
    {
        string source = """
                        routine handle(code: Integer)
                          when code
                            == HttpStatus.OK => show("success")
                            == HttpStatus.NOT_FOUND => show("not found")
                            >= HttpStatus.SERVER_ERROR => show("server error")
                            else => show("unknown")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenComparisonWithMethodCall()
    {
        string source = """
                        routine test(value: Integer)
                          when value
                            == get_threshold() => show("at threshold")
                            > get_threshold() => show("above threshold")
                            else => show("below threshold")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenMixedComparisonPatterns()
    {
        string source = """
                        routine categorize(n: Integer)
                          when n
                            < 0 => show("negative")
                            == 0 => show("zero")
                            < 50 => show("small")
                            < 100 => show("medium")
                            else => show("large")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ReturnWhenWithLessThanPattern()
    {
        // Regression test: return when + < comparison pattern
        // Previously failed due to ParseWhenExpression not using ProcessIndentToken
        string source = """
                        routine classify(n: Integer) -> Text
                          return when n
                            < 0 => "negative"
                            else => "positive"
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Becomes Statement Tests

    [Fact]
    public void ParseSuflae_WhenBlockWithBecomes()
    {
        string source = """
                        routine describe(n: Integer) -> Text
                          return when n
                            == 0 =>
                              log("found zero")
                              becomes "zero"
                            else => "non-zero"
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenMultipleBlocksWithBecomes()
    {
        string source = """
                        routine process(status: Status) -> Text
                          return when status
                            == Status.PENDING =>
                              log("still waiting")
                              notify_user()
                              becomes "pending"
                            == Status.ACTIVE =>
                              log("running")
                              update_progress()
                              becomes "active"
                            else =>
                              log("completed or unknown")
                              becomes "done"
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenTypePatternWithBlockAndBecomes()
    {
        string source = """
                        routine handle(shape: Shape) -> Float
                          return when shape
                            is Circle (center, radius) =>
                              var area = 3.14159 * radius * radius
                              log(f"Circle area: {area}")
                              becomes area
                            is Rectangle (_, size) =>
                              var area = size.width * size.height
                              log(f"Rectangle area: {area}")
                              becomes area
                            else =>
                              becomes 0.0
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenBlockWithBecomesExpression()
    {
        string source = """
                        routine compute(n: Integer)
                          when n
                            == 0 =>
                              var x = 0
                              becomes x
                            else =>
                              becomes n * 2
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Comparison Patterns with Guards Tests

    [Fact]
    public void ParseSuflae_WhenComparisonWithGuard()
    {
        string source = """
                        routine test(x: Integer, y: Integer)
                          when x
                            > 0 if y > 0 => show("both positive")
                            > 0 => show("x positive, y not")
                            < 0 if y < 0 => show("both negative")
                            else => show("other")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenComparisonWithComplexGuard()
    {
        string source = """
                        routine validate(score: Integer, bonus: Integer) -> Text
                          return when score
                            >= 90 if bonus > 0 => "A+"
                            >= 90 => "A"
                            >= 80 if bonus >= 5 => "B+"
                            >= 80 => "B"
                            else => "C"
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Complex Boolean Guard Tests

    [Fact]
    public void ParseSuflae_WhenGuardWithAnd()
    {
        string source = """
                        routine test(n: Integer, m: Integer)
                          when n
                            is Integer x if x > 0 and m > 0 => show("both positive")
                            else => show("not both positive")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenGuardWithOr()
    {
        string source = """
                        routine test(n: Integer)
                          when n
                            is Integer x if x < -100 or x > 100 => show("extreme")
                            else => show("moderate")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenGuardWithAndOr()
    {
        string source = """
                        routine classify(x: Integer, y: Integer, z: Integer)
                          when x
                            > 0 if y > 0 and z > 0 => show("all positive")
                            > 0 if y > 0 or z > 0 => show("x and at least one other positive")
                            > 0 => show("only x positive")
                            else => show("x not positive")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenGuardWithMemberVariableAccess()
    {
        string source = """
                        routine handle(user: User)
                          when user
                            is User u if u.age >= 18 and u.verified => show("verified adult")
                            is User u if u.age >= 18 => show("unverified adult")
                            else => show("minor")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhenGuardWithMethodCall()
    {
        string source = """
                        routine process(item: Item)
                          when item
                            is Item i if i.is_valid() and i.count() > 0 => process_valid(i)
                            is Item i if i.is_valid() => handle_empty(i)
                            else => reject(item)
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion
}
