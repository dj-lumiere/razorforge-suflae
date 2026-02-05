using Xunit;

namespace RazorForge.Tests.Parser;

using Compilers.Shared.AST;
using static TestHelpers;

/// <summary>
/// Tests for parsing control flow statements in Suflae:
/// if/elseif/else, unless, loop, while, for, break, continue, try/catch/finally, using.
/// Suflae uses indentation-based syntax with colons.
/// </summary>
public class SuflaeControlFlowTests
{
    #region If Statement Tests

    [Fact]
    public void ParseSuflae_SimpleIf()
    {
        string source = """
                        routine test():
                            if x > 0:
                                show("positive")
                        """;

        Program program = AssertParsesSuflae(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        var body = routine.Body as BlockStatement;
        Assert.NotNull(@object: body);
        IfStatement ifStmt = body.Statements
                                 .OfType<IfStatement>()
                                 .First();
        Assert.NotNull(@object: ifStmt);
        Assert.Null(@object: ifStmt.ElseStatement);
    }

    [Fact]
    public void ParseSuflae_IfElse()
    {
        string source = """
                        routine test():
                            if x > 0:
                                show("positive")
                            else:
                                show("non-positive")
                        """;

        Program program = AssertParsesSuflae(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        var body = routine.Body as BlockStatement;
        Assert.NotNull(@object: body);
        IfStatement ifStmt = body.Statements
                                 .OfType<IfStatement>()
                                 .First();
        Assert.NotNull(@object: ifStmt);
        Assert.NotNull(@object: ifStmt.ElseStatement);
    }

    [Fact]
    public void ParseSuflae_IfElseIfElse()
    {
        string source = """
                        routine test():
                            if x > 0:
                                show("positive")
                            elseif x < 0:
                                show("negative")
                            else:
                                show("zero")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_MultipleElseIf()
    {
        string source = """
                        routine test(n: Integer) -> Text:
                            if n == 1:
                                return "one"
                            elseif n == 2:
                                return "two"
                            elseif n == 3:
                                return "three"
                            else:
                                return "other"
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_NestedIf()
    {
        string source = """
                        routine test():
                            if x > 0:
                                if y > 0:
                                    show("both positive")
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Unless Statement Tests

    [Fact]
    public void ParseSuflae_SimpleUnless()
    {
        string source = """
                        routine test():
                            unless x.is_valid():
                                return
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_UnlessWithElse()
    {
        string source = """
                        routine test():
                            unless condition:
                                show("condition is false")
                            else:
                                show("condition is true")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_UnlessGuardClause()
    {
        string source = """
                        routine process!(data: Text):
                            unless data.length() > 0:
                                absent
                            show(data)
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Inline If-Then-Else Tests

    [Fact]
    public void ParseSuflae_InlineIfThenElse()
    {
        string source = """
                        routine min(a: Integer, b: Integer) -> Integer:
                            return if a < b then a else b
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_InlineIfThenElse_InAssignment()
    {
        string source = """
                        routine test():
                            let sign = if x >= 0 then 1 else -1
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Loop Statement Tests

    [Fact]
    public void ParseSuflae_InfiniteLoop()
    {
        string source = """
                        routine run():
                            loop:
                                process()
                                if should_stop():
                                    break
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LoopWithBreak()
    {
        string source = """
                        routine test():
                            loop:
                                break
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_LoopWithContinue()
    {
        string source = """
                        routine test():
                            loop:
                                if skip_this():
                                    continue
                                process()
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region While Loop Tests

    [Fact]
    public void ParseSuflae_SimpleWhile()
    {
        string source = """
                        routine test():
                            while condition:
                                process()
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhileWithCounter()
    {
        string source = """
                        routine count_up():
                            var count = 0
                            while count < 10:
                                show(count)
                                count += 1
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_WhileWithElse()
    {
        string source = """
                        routine search():
                            while has_more():
                                if found_target():
                                    break
                                advance()
                            else:
                                show("Loop completed without finding it")
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region For Loop Tests

    [Fact]
    public void ParseSuflae_ForRangeInclusive()
    {
        string source = """
                        routine test():
                            for i in 0 to 10:
                                show(i)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ForRangeWithStep()
    {
        string source = """
                        routine test():
                            for i in 0 to 100 by 5:
                                show(i)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ForDownto()
    {
        string source = """
                        routine test():
                            for i in 10 downto 0:
                                show(i)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ForInCollection()
    {
        string source = """
                        routine test():
                            let items = [1, 2, 3, 4, 5]
                            for item in items:
                                show(item)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ForWithEnumerate()
    {
        string source = """
                        routine test():
                            let items = ["a", "b", "c"]
                            for (index, item) in items.enumerate():
                                show(f"{index}: {item}")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ForWithElse()
    {
        string source = """
                        routine find_item(items: List<Integer>, target: Integer):
                            for item in items:
                                if item == target:
                                    show("Found!")
                                    break
                            else:
                                show("No match found")
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_NestedFor()
    {
        string source = """
                        routine matrix():
                            for i in 0 to 10:
                                for j in 0 to 10:
                                    show(f"{i},{j}")
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Throw Tests

    [Fact]
    public void ParseSuflae_Throw()
    {
        string source = """
                        routine validate(value: Integer) -> Integer:
                            if value < 0:
                                throw ValueError("Value must be non-negative")
                            return value
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Using Statement Tests

    [Fact]
    public void ParseSuflae_SimpleUsing()
    {
        string source = """
                        routine test():
                            using open("file.txt") as file:
                                let content = file.read_all()
                                process(content)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_UsingMultipleResources()
    {
        string source = """
                        routine test():
                            using open("input.txt") as input, open("output.txt") as output:
                                let data = input.read_all()
                                output.write(transform(data))
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_NestedUsing()
    {
        string source = """
                        routine test():
                            using acquire_lock() as lock:
                                using open_connection() as conn:
                                    process(conn)
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Return and Pass Tests

    [Fact]
    public void ParseSuflae_ReturnWithValue()
    {
        string source = """
                        routine add(a: Integer, b: Integer) -> Integer:
                            return a + b
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ReturnWithoutValue()
    {
        string source = """
                        routine log(msg: Text):
                            show(msg)
                            return
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_PassStatement()
    {
        string source = """
                        routine empty():
                            pass
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Async Tests

    [Fact]
    public void ParseSuflae_SuspendedRoutine()
    {
        string source = """
                        suspended routine fetch_data(url: Text) -> Text:
                            let response = waitfor http.get(url)
                            return response.body
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_Waitfor()
    {
        string source = """
                        suspended routine start():
                            let data = waitfor fetch_data("https://api.example.com")
                            show(data)
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Stop and Verify Tests

    [Fact]
    public void ParseSuflae_StopWithMessage()
    {
        string source = """
                        routine divide(a: Integer, b: Integer) -> Integer:
                            if b == 0:
                                stop!("Division by zero")
                            return a // b
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_VerifyWithCondition()
    {
        string source = """
                        routine process(data: List<Integer>):
                            verify!(data.length() > 0, "Data cannot be empty")
                            do_process(data)
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_Breach()
    {
        string source = """
                        routine unreachable():
                            breach!("Should never reach here")
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion

    #region Complex Control Flow Tests

    [Fact]
    public void ParseSuflae_ComplexNestedControlFlow()
    {
        string source = """
                        routine process_matrix(matrix: List<List<Integer>>):
                            for row in matrix:
                                for cell in row:
                                    if cell < 0:
                                        continue
                                    if cell > 100:
                                        break
                                    while cell > 0:
                                        cell -= 1
                        """;

        AssertParsesSuflae(source: source);
    }

    [Fact]
    public void ParseSuflae_ControlFlowWithErrorHandling()
    {
        string source = """
                        routine search!(items: List<Integer>, target: Integer) -> Integer:
                            for (index, item) in items.enumerate():
                                if item == target:
                                    return index
                            absent
                        """;

        AssertParsesSuflae(source: source);
    }

    #endregion
}
