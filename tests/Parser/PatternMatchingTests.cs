using Xunit;

namespace RazorForge.Tests.Parser;

using SyntaxTree;
using static TestHelpers;

/// <summary>
/// Tests for parsing pattern matching in RazorForge:
/// when expressions, is keyword, guards, variant destructuring, literal patterns.
/// </summary>
public class PatternMatchingTests
{
    #region Basic When Statement Tests
    /// <summary>
    /// Tests Parse_SimpleWhen.
    /// </summary>

    [Fact]
    public void Parse_SimpleWhen()
    {
        string source = """
                        routine test(x: S32)
                          when x
                            1 => show("one")
                            2 => show("two")
                            else => show("other")
                          return
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
    /// <summary>
    /// Tests Parse_WhenWithBlock.
    /// </summary>

    [Fact]
    public void Parse_WhenWithBlock()
    {
        string source = """
                        routine test(x: S32)
                          when x
                            == 1 =>
                              show("one")
                              do_something()
                            else => show("other")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenAsExpression.
    /// </summary>

    [Fact]
    public void Parse_WhenAsExpression()
    {
        string source = """
                        routine describe(x: S32) -> Text
                          return when x
                            == 0 => "zero"
                            == 1 => "one"
                            else => "many"
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenWithAssignment.
    /// </summary>

    [Fact]
    public void Parse_WhenWithAssignment()
    {
        string source = """
                        routine test(status: Status)
                          var description = when status
                            == Status.ACTIVE => "Running"
                            else => "Not running"
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Is Type Pattern Tests
    /// <summary>
    /// Tests Parse_WhenIsType.
    /// </summary>

    [Fact]
    public void Parse_WhenIsType()
    {
        string source = """
                        routine test(value: Data)
                          when value
                            is S32 n => show(f"Integer: {n}")
                            is Text t => show(f"Text: {t}")
                            else => show("Unknown type")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenIsTypeWithoutBinding.
    /// </summary>

    [Fact]
    public void Parse_WhenIsTypeWithoutBinding()
    {
        string source = """
                        routine test(value: Data)
                          when value
                            is S32 => show("It's an integer")
                            is Text => show("It's text")
                            else => show("Unknown")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenIsCustomType.
    /// </summary>

    [Fact]
    public void Parse_WhenIsCustomType()
    {
        string source = """
                        routine test(value: Data)
                          when value
                            is Point p => show(f"Point: ({p.x}, {p.y})")
                            is Circle c => show(f"Circle: r={c.radius}")
                            else => show("Unknown shape")
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Choice Pattern Tests
    /// <summary>
    /// Tests Parse_WhenChoice.
    /// </summary>

    [Fact]
    public void Parse_WhenChoice()
    {
        string source = """
                        routine handle(status: Status)
                          when status
                            == Status.PENDING => show("Waiting...")
                            == Status.ACTIVE => show("In progress")
                            == Status.COMPLETED => show("Done!")
                            == Status.CANCELLED => show("Cancelled")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenChoiceShorthand.
    /// </summary>

    [Fact]
    public void Parse_WhenChoiceShorthand()
    {
        string source = """
                        routine handle(status: Status)
                          when status
                            == PENDING => show("Waiting...")
                            == ACTIVE => show("In progress")
                            == COMPLETED => show("Done!")
                            == CANCELLED => show("Cancelled")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenChoiceIsPattern.
    /// </summary>

    [Fact]
    public void Parse_WhenChoiceIsPattern()
    {
        string source = """
                        routine handle(status: Status)
                          when status
                            is PENDING => show("Waiting...")
                            is ACTIVE => show("In progress")
                            is COMPLETED => show("Done!")
                            is CANCELLED => show("Cancelled")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenChoiceIsPatternQualified.
    /// </summary>

    [Fact]
    public void Parse_WhenChoiceIsPatternQualified()
    {
        string source = """
                        routine handle(status: Status)
                          when status
                            is Status.PENDING => show("Waiting...")
                            is Status.ACTIVE => show("In progress")
                            is Status.COMPLETED => show("Done!")
                            is Status.CANCELLED => show("Cancelled")
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Variant Pattern Tests
    /// <summary>
    /// Tests Parse_WhenVariant.
    /// </summary>

    [Fact]
    public void Parse_WhenVariant()
    {
        string source = """
                        routine handle(msg: Message)
                          when msg
                            is Message.TEXT content => process(content)
                            is Message.NUMBER n => compute(n)
                            is Message.QUIT => exit()
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenVariantShorthand.
    /// </summary>

    [Fact]
    public void Parse_WhenVariantShorthand()
    {
        string source = """
                        routine handle(msg: Message)
                          when msg
                            is Text content => process(content)
                            is Number n => compute(n)
                            is Quit => exit()
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenVariantDestructuring.
    /// </summary>

    [Fact]
    public void Parse_WhenVariantDestructuring()
    {
        string source = """
                        routine handle(shape: Shape)
                          when shape
                            is Circle (center, radius) =>
                              show(f"Circle at ({center.x}, {center.y}) with radius {radius}")
                            is Rectangle (top_left, size) =>
                              show(f"Rectangle at ({top_left.x}, {top_left.y})")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenVariantDestructuringWithAlias.
    /// </summary>

    [Fact]
    public void Parse_WhenVariantDestructuringWithAlias()
    {
        string source = """
                        routine handle(shape: Shape)
                          when shape
                            is Circle (center: c, radius: r) =>
                              show(f"Circle: center={c}, r={r}")
                            else => show("Not a circle")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenNestedDestructuring.
    /// </summary>

    [Fact]
    public void Parse_WhenNestedDestructuring()
    {
        string source = """
                        routine handle(shape: Shape)
                          when shape
                            is Circle ((x, y), radius) =>
                              show(f"Circle at ({x}, {y}) with radius {radius}")
                            is Rectangle ((x, y), (width, height)) =>
                              show(f"Rectangle at ({x}, {y}), {width}x{height}")
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Guard Clause Tests
    /// <summary>
    /// Tests Parse_WhenWithGuard.
    /// </summary>

    [Fact]
    public void Parse_WhenWithGuard()
    {
        string source = """
                        routine classify(n: S32?)
                          when n
                            is S32 x if x > 0 => show("Positive")
                            is S32 x if x < 0 => show("Negative")
                            is S32 x => show("Zero")
                            else => show("None")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenLiteralWithGuard.
    /// </summary>

    [Fact]
    public void Parse_WhenLiteralWithGuard()
    {
        string source = """
                        routine test(x: S32, y: S32)
                          when x
                            == 0 if y > 0 => show("x=0, y positive")
                            == 0 => show("x=0, y non-positive")
                            else => show("x not zero")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenTypeWithComplexGuard.
    /// </summary>

    [Fact]
    public void Parse_WhenTypeWithComplexGuard()
    {
        string source = """
                        routine handle(error: Crashable)
                          when error
                            is NetworkError e if e.code == 404 => show("Not found")
                            is NetworkError e if e.code >= 500 => show("Server error")
                            is Crashable e => show(f"Error: {e.message()}")
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Maybe/Result/Lookup Pattern Tests
    /// <summary>
    /// Tests Parse_WhenMaybe.
    /// </summary>

    [Fact]
    public void Parse_WhenMaybe()
    {
        string source = """
                        routine handle(value: User?)
                          when value
                            is None => show("User not found")
                            else user => show(f"Found: {user.name}")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenResult.
    /// </summary>

    [Fact]
    public void Parse_WhenResult()
    {
        string source = """
                        routine handle(result: Result[User])
                          when result
                            is Crashable err => show(f"Error: {err.message()}")
                            else user => show(f"Found: {user.name}")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenResultSpecificError.
    /// </summary>

    [Fact]
    public void Parse_WhenResultSpecificError()
    {
        string source = """
                        routine handle(result: Result[File])
                          when result
                            is FileNotFoundError e => show(f"Not found: {e.path}")
                            is PermissionError e => show(f"Access denied: {e.path}")
                            is Crashable e => show(f"Unknown error: {e.message()}")
                            else file => file.read()
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenLookup.
    /// </summary>

    [Fact]
    public void Parse_WhenLookup()
    {
        string source = """
                        routine handle(result: Lookup[User])
                          when result
                            is Crashable e => show(f"Error: {e.message()}")
                            is None => show("User not found")
                            else user => show(f"Found: {user.name}")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenLookupSpecificErrors.
    /// </summary>

    [Fact]
    public void Parse_WhenLookupSpecificErrors()
    {
        string source = """
                        routine handle(result: Lookup[Config])
                          when result
                            is ValidationError e => stop!(f"Invalid config name: {e.message}")
                            is IOError e => show(f"IO error: {e.message}")
                            is Crashable e => breach!()
                            is None => use_default_config()
                            else config => apply(config)
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Literal Pattern Tests
    /// <summary>
    /// Tests Parse_WhenLiteralInteger.
    /// </summary>

    [Fact]
    public void Parse_WhenLiteralInteger()
    {
        string source = """
                        routine describe(n: S32) -> Text
                          return when n
                            0 => "zero"
                            1 => "one"
                            2 => "two"
                            else => "many"
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenLiteralText.
    /// </summary>

    [Fact]
    public void Parse_WhenLiteralText()
    {
        string source = """
                        routine handle(cmd: Text)
                          when cmd
                            "help" => show_help()
                            "version" => show_version()
                            "quit" => exit()
                            else => show("Unknown command")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenLiteralBool.
    /// </summary>

    [Fact]
    public void Parse_WhenLiteralBool()
    {
        string source = """
                        routine test(flag: bool)
                          when flag
                            true => show("enabled")
                            false => show("disabled")
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Wildcard Pattern Tests
    /// <summary>
    /// Tests Parse_WhenWildcard.
    /// </summary>

    [Fact]
    public void Parse_WhenWildcard()
    {
        string source = """
                        routine handle(result: Result[S32])
                          when result
                            is Crashable => absent
                            else value => value
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenWildcardInDestructuring.
    /// </summary>

    [Fact]
    public void Parse_WhenWildcardInDestructuring()
    {
        string source = """
                        routine handle(shape: Shape)
                          when shape
                            is CIRCLE (_, radius) =>
                              show(f"Radius: {radius}")
                            else => show("Not a circle")
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Else Binding Tests
    /// <summary>
    /// Tests Parse_WhenElseWithBinding.
    /// </summary>

    [Fact]
    public void Parse_WhenElseWithBinding()
    {
        string source = """
                        routine handle(value: Maybe[User])
                          when value
                            is None => show("Not found")
                            else u => show(u.name)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenElseWithBlock.
    /// </summary>

    [Fact]
    public void Parse_WhenElseWithBlock()
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
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Let Destructuring Tests
    /// <summary>
    /// Tests Parse_LetDestructuringRecord.
    /// </summary>

    [Fact]
    public void Parse_LetDestructuringRecord()
    {
        string source = """
                        routine test()
                          var (x, y) = Point(x: 5, y: 6)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_LetDestructuringWithAlias.
    /// </summary>

    [Fact]
    public void Parse_LetDestructuringWithAlias()
    {
        string source = """
                        routine test()
                          var (center: c, radius: r) = Circle(center: Point(5, 6), radius: 7)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_LetNestedDestructuring.
    /// </summary>

    [Fact]
    public void Parse_LetNestedDestructuring()
    {
        string source = """
                        routine test()
                          var ((x, y), radius) = Circle(center: Point(5, 6), radius: 7)
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Complex Pattern Tests
    /// <summary>
    /// Tests Parse_ComplexNestedPatterns.
    /// </summary>

    [Fact]
    public void Parse_ComplexNestedPatterns()
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
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenWithMultipleStatements.
    /// </summary>

    [Fact]
    public void Parse_WhenWithMultipleStatements()
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
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Comparison Pattern Tests
    /// <summary>
    /// Tests Parse_WhenComparisonNotEqual.
    /// </summary>

    [Fact]
    public void Parse_WhenComparisonNotEqual()
    {
        string source = """
                        routine test(x: S32)
                          when x
                            != 0 => show("non-zero")
                            else => show("zero")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenComparisonLessThan.
    /// </summary>

    [Fact]
    public void Parse_WhenComparisonLessThan()
    {
        string source = """
                        routine classify(n: S32) -> Text
                          return when n
                            < 0 => "negative"
                            == 0 => "zero"
                            else => "positive"
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenComparisonGreaterThan.
    /// </summary>

    [Fact]
    public void Parse_WhenComparisonGreaterThan()
    {
        string source = """
                        routine classify(n: S32) -> Text
                          return when n
                            > 100 => "large"
                            > 10 => "medium"
                            else => "small"
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenComparisonLessThanOrEqual.
    /// </summary>

    [Fact]
    public void Parse_WhenComparisonLessThanOrEqual()
    {
        string source = """
                        routine grade(score: S32) -> Text
                          return when score
                            <= 50 => "fail"
                            <= 70 => "pass"
                            <= 90 => "good"
                            else => "excellent"
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenComparisonGreaterThanOrEqual.
    /// </summary>

    [Fact]
    public void Parse_WhenComparisonGreaterThanOrEqual()
    {
        string source = """
                        routine classify(temp: S32) -> Text
                          return when temp
                            >= 100 => "boiling"
                            >= 30 => "hot"
                            >= 0 => "cold"
                            else => "freezing"
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenComparisonStrictEqual.
    /// </summary>

    [Fact]
    public void Parse_WhenComparisonStrictEqual()
    {
        string source = """
                        routine test(a: Data, b: Data)
                          when a
                            === b => show("same reference")
                            else => show("different reference")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenComparisonStrictNotEqual.
    /// </summary>

    [Fact]
    public void Parse_WhenComparisonStrictNotEqual()
    {
        string source = """
                        routine test(a: Data, b: Data)
                          when a
                            !== b => show("different reference")
                            else => show("same reference")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenComparisonWithMemberAccess.
    /// </summary>

    [Fact]
    public void Parse_WhenComparisonWithMemberAccess()
    {
        string source = """
                        routine handle(code: S32)
                          when code
                            == HttpStatus.OK => show("success")
                            == HttpStatus.NOT_FOUND => show("not found")
                            >= HttpStatus.SERVER_ERROR => show("server error")
                            else => show("unknown")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenComparisonWithMethodCall.
    /// </summary>

    [Fact]
    public void Parse_WhenComparisonWithMethodCall()
    {
        string source = """
                        routine test(value: S32)
                          when value
                            == get_threshold() => show("at threshold")
                            > get_threshold() => show("above threshold")
                            else => show("below threshold")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenMixedComparisonPatterns.
    /// </summary>

    [Fact]
    public void Parse_WhenMixedComparisonPatterns()
    {
        string source = """
                        routine categorize(n: S32) -> Text
                          return when n
                            < 0 => "negative"
                            == 0 => "zero"
                            < 50 => "small"
                            < 100 => "medium"
                            else => "large"
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Becomes Statement Tests
    /// <summary>
    /// Tests Parse_WhenBlockWithBecomes.
    /// </summary>

    [Fact]
    public void Parse_WhenBlockWithBecomes()
    {
        string source = """
                        routine describe(n: S32) -> Text
                          return when n
                            == 0 =>
                              log("found zero")
                              becomes "zero"
                            else => "non-zero"
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenMultipleBlocksWithBecomes.
    /// </summary>

    [Fact]
    public void Parse_WhenMultipleBlocksWithBecomes()
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
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenTypePatternWithBlockAndBecomes.
    /// </summary>

    [Fact]
    public void Parse_WhenTypePatternWithBlockAndBecomes()
    {
        string source = """
                        routine handle(shape: Shape) -> F64
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
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenBlockWithBecomesExpression.
    /// </summary>

    [Fact]
    public void Parse_WhenBlockWithBecomesExpression()
    {
        string source = """
                        routine compute(n: S32) -> S32
                          return when n
                            < 0 =>
                              var abs = -n
                              becomes abs * 2
                            else =>
                              becomes n * 2
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Comparison Patterns with Guards Tests
    /// <summary>
    /// Tests Parse_WhenComparisonWithGuard.
    /// </summary>

    [Fact]
    public void Parse_WhenComparisonWithGuard()
    {
        string source = """
                        routine test(x: S32, y: S32)
                          when x
                            > 0 if y > 0 => show("both positive")
                            > 0 => show("x positive, y not")
                            < 0 if y < 0 => show("both negative")
                            else => show("other")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenComparisonWithComplexGuard.
    /// </summary>

    [Fact]
    public void Parse_WhenComparisonWithComplexGuard()
    {
        string source = """
                        routine validate(score: S32, bonus: S32) -> Text
                          return when score
                            >= 90 if bonus > 0 => "A+"
                            >= 90 => "A"
                            >= 80 if bonus >= 5 => "B+"
                            >= 80 => "B"
                            else => "C"
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Complex Boolean Guard Tests
    /// <summary>
    /// Tests Parse_WhenGuardWithAnd.
    /// </summary>

    [Fact]
    public void Parse_WhenGuardWithAnd()
    {
        string source = """
                        routine test(n: S32, m: S32)
                          when n
                            is S32 x if x > 0 and m > 0 => show("both positive")
                            else => show("not both positive")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenGuardWithOr.
    /// </summary>

    [Fact]
    public void Parse_WhenGuardWithOr()
    {
        string source = """
                        routine test(n: S32)
                          when n
                            is S32 x if x < -100 or x > 100 => show("extreme")
                            else => show("moderate")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenGuardWithAndOr.
    /// </summary>

    [Fact]
    public void Parse_WhenGuardWithAndOr()
    {
        string source = """
                        routine classify(x: S32, y: S32, z: S32)
                          when x
                            > 0 if y > 0 and z > 0 => show("all positive")
                            > 0 if y > 0 or z > 0 => show("x and at least one other positive")
                            > 0 => show("only x positive")
                            else => show("x not positive")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenGuardWithMemberVariableAccess.
    /// </summary>

    [Fact]
    public void Parse_WhenGuardWithMemberVariableAccess()
    {
        string source = """
                        routine handle(user: User)
                          when user
                            is User u if u.age >= 18 and u.verified => show("verified adult")
                            is User u if u.age >= 18 => show("unverified adult")
                            else => show("minor")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhenGuardWithMethodCall.
    /// </summary>

    [Fact]
    public void Parse_WhenGuardWithMethodCall()
    {
        string source = """
                        routine process(item: Item)
                          when item
                            is Item i if i.is_valid() and i.count() > 0 => process_valid(i)
                            is Item i if i.is_valid() => handle_empty(i)
                            else => reject(item)
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion
}
