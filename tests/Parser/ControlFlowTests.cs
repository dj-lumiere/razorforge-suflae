using Xunit;

namespace RazorForge.Tests.Parser;

using SyntaxTree;
using static TestHelpers;

/// <summary>
/// Tests for parsing control flow statements in RazorForge:
/// if/elseif/else, unless, loop, while, for, break, continue, inline if-then-else.
/// </summary>
public class ControlFlowTests
{
    #region If Statement Tests
    /// <summary>
    /// Tests Parse_SimpleIf.
    /// </summary>

    [Fact]
    public void Parse_SimpleIf()
    {
        string source = """
                        routine test()
                          if x > 0
                            show("positive")
                          return
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        var body = routine.Body as BlockStatement;
        Assert.NotNull(@object: body);
        IfStatement ifStmt = body.Statements
                                 .OfType<IfStatement>()
                                 .First();
        Assert.NotNull(@object: ifStmt);
        Assert.Null(@object: ifStmt.ElseStatement);
    }
    /// <summary>
    /// Tests Parse_IfElse.
    /// </summary>

    [Fact]
    public void Parse_IfElse()
    {
        string source = """
                        routine test()
                          if x > 0
                            show("positive")
                          else
                            show("non-positive")
                          return
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        var body = routine.Body as BlockStatement;
        Assert.NotNull(@object: body);
        IfStatement ifStmt = body.Statements
                                 .OfType<IfStatement>()
                                 .First();
        Assert.NotNull(@object: ifStmt);
        Assert.NotNull(@object: ifStmt.ElseStatement);
    }
    /// <summary>
    /// Tests Parse_IfElseIfElse.
    /// </summary>

    [Fact]
    public void Parse_IfElseIfElse()
    {
        string source = """
                        routine test()
                          if x > 0
                            show("positive")
                          elseif x < 0
                            show("negative")
                          else
                            show("zero")
                          return
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        var body = routine.Body as BlockStatement;
        Assert.NotNull(@object: body);
        IfStatement ifStmt = body.Statements
                                 .OfType<IfStatement>()
                                 .First();
        Assert.NotNull(@object: ifStmt);
        // elseif is parsed as nested if in else
        Assert.NotNull(@object: ifStmt.ElseStatement);
    }
    /// <summary>
    /// Tests Parse_MultipleElseIf.
    /// </summary>

    [Fact]
    public void Parse_MultipleElseIf()
    {
        string source = """
                        routine test(n: S32) -> Text
                          if n == 1
                            return "one"
                          elseif n == 2
                            return "two"
                          elseif n == 3
                            return "three"
                          else
                            return "other"
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_NestedIf.
    /// </summary>

    [Fact]
    public void Parse_NestedIf()
    {
        string source = """
                        routine test()
                          if x > 0
                            if y > 0
                              show("both positive")
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Unless Statement Tests
    /// <summary>
    /// Tests Parse_SimpleUnless.
    /// </summary>

    [Fact]
    public void Parse_SimpleUnless()
    {
        string source = """
                        routine test()
                          unless x.is_valid()
                            return
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        var body = routine.Body as BlockStatement;
        Assert.NotNull(@object: body);
        // Unless is parsed as an IfStatement with negated condition
        IfStatement ifStmt = body.Statements
                                 .OfType<IfStatement>()
                                 .First();
        Assert.NotNull(@object: ifStmt);
    }
    /// <summary>
    /// Tests Parse_UnlessWithElse.
    /// </summary>

    [Fact]
    public void Parse_UnlessWithElse()
    {
        string source = """
                        routine test()
                          unless condition
                            show("condition is false")
                          else
                            show("condition is true")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_UnlessGuardClause.
    /// </summary>

    [Fact]
    public void Parse_UnlessGuardClause()
    {
        string source = """
                        routine process!(data: Text)
                          unless data.length() > 0
                            absent
                          show(data)
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Inline If-Then-Else Tests
    /// <summary>
    /// Tests Parse_InlineIfThenElse.
    /// </summary>

    [Fact]
    public void Parse_InlineIfThenElse()
    {
        string source = """
                        routine min(a: S32, b: S32) -> S32
                          return if a < b then a else b
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        Assert.NotNull(@object: routine.ReturnType);
    }
    /// <summary>
    /// Tests Parse_InlineIfThenElse_InAssignment.
    /// </summary>

    [Fact]
    public void Parse_InlineIfThenElse_InAssignment()
    {
        string source = """
                        routine test()
                          var sign = if x >= 0 then 1 else -1
                          return
                        """;

        AssertParses(source: source);
    }

    // Nested inline if-then-else test moved to Analyzer/ControlFlowAnalysisTests.cs
    // It parses correctly but should be rejected as not idiomatic

    #endregion

    #region Loop Statement Tests
    /// <summary>
    /// Tests Parse_InfiniteLoop.
    /// </summary>

    [Fact]
    public void Parse_InfiniteLoop()
    {
        string source = """
                        routine run()
                          loop
                            process()
                            if should_stop()
                              break
                          return
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        var body = routine.Body as BlockStatement;
        Assert.NotNull(@object: body);
        // The loop statement is parsed as a LoopStatement (infinite loop primitive)
        LoopStatement? loopStmt = body.Statements
                                      .OfType<LoopStatement>()
                                      .FirstOrDefault();
        Assert.NotNull(@object: loopStmt);
    }
    /// <summary>
    /// Tests Parse_LoopWithBreak.
    /// </summary>

    [Fact]
    public void Parse_LoopWithBreak()
    {
        string source = """
                        routine test()
                          loop
                            break
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_LoopWithContinue.
    /// </summary>

    [Fact]
    public void Parse_LoopWithContinue()
    {
        string source = """
                        routine test()
                          loop
                            if skip_this()
                              continue
                            process()
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region While Loop Tests
    /// <summary>
    /// Tests Parse_SimpleWhile.
    /// </summary>

    [Fact]
    public void Parse_SimpleWhile()
    {
        string source = """
                        routine test()
                          while condition
                            process()
                          return
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        var body = routine.Body as BlockStatement;
        Assert.NotNull(@object: body);
        WhileStatement? whileStmt = body.Statements
                                        .OfType<WhileStatement>()
                                        .FirstOrDefault();
        Assert.NotNull(@object: whileStmt);
    }
    /// <summary>
    /// Tests Parse_WhileWithCounter.
    /// </summary>

    [Fact]
    public void Parse_WhileWithCounter()
    {
        string source = """
                        routine count_up()
                          var count = 0
                          while count < 10
                            show(count)
                            count += 1
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhileWithBreak.
    /// </summary>

    [Fact]
    public void Parse_WhileWithBreak()
    {
        string source = """
                        routine search()
                          while has_more()
                            if found_target()
                              break
                            advance()
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_WhileWithContinue.
    /// </summary>

    [Fact]
    public void Parse_WhileWithContinue()
    {
        string source = """
                        routine process_items()
                          while has_items()
                            var item = next_item()
                            if item.skip()
                              continue
                            process(item)
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region For Loop Tests
    /// <summary>
    /// Tests Parse_ForRangeInclusive.
    /// </summary>

    [Fact]
    public void Parse_ForRangeInclusive()
    {
        string source = """
                        routine test()
                          for i in 0 til 10
                            show(i)
                          return
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        var body = routine.Body as BlockStatement;
        Assert.NotNull(@object: body);
        ForStatement? forStmt = body.Statements
                                    .OfType<ForStatement>()
                                    .FirstOrDefault();
        Assert.NotNull(@object: forStmt);
    }
    /// <summary>
    /// Tests Parse_ForRangeWithStep.
    /// </summary>

    [Fact]
    public void Parse_ForRangeWithStep()
    {
        string source = """
                        routine test()
                          for i in 0 til 100 by 5
                            show(i)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_ForInCollection.
    /// </summary>

    [Fact]
    public void Parse_ForInCollection()
    {
        string source = """
                        routine test()
                          var items = [1, 2, 3, 4, 5]
                          for item in items
                            show(item)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_ForWithEnumerate.
    /// </summary>

    [Fact]
    public void Parse_ForWithEnumerate()
    {
        string source = """
                        routine test()
                          var items = ["a", "b", "c"]
                          for (index, item) in items.enumerate()
                            show(f"{index}: {item}")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_ForWithBreak.
    /// </summary>

    [Fact]
    public void Parse_ForWithBreak()
    {
        string source = """
                        routine find_first(items: List[S32], target: S32)
                          for item in items
                            if item == target
                              show("Found!")
                              break
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_ForWithContinue.
    /// </summary>

    [Fact]
    public void Parse_ForWithContinue()
    {
        string source = """
                        routine process_valid(items: List[S32])
                          for item in items
                            if item < 0
                              continue
                            process(item)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_NestedFor.
    /// </summary>

    [Fact]
    public void Parse_NestedFor()
    {
        string source = """
                        routine matrix()
                          for i in 0 til 10
                            for j in 0 til 10
                              show(f"{i},{j}")
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Return Statement Tests
    /// <summary>
    /// Tests Parse_ReturnWithValue.
    /// </summary>

    [Fact]
    public void Parse_ReturnWithValue()
    {
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a + b
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);
        var body = routine.Body as BlockStatement;
        Assert.NotNull(@object: body);
        ReturnStatement returnStmt = body.Statements
                                         .OfType<ReturnStatement>()
                                         .First();
        Assert.NotNull(@object: returnStmt);
        Assert.NotNull(@object: returnStmt.Value);
    }
    /// <summary>
    /// Tests Parse_ReturnWithoutValue.
    /// </summary>

    [Fact]
    public void Parse_ReturnWithoutValue()
    {
        string source = """
                        routine log(msg: Text)
                          show(msg)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_EarlyReturn.
    /// </summary>

    [Fact]
    public void Parse_EarlyReturn()
    {
        string source = """
                        routine process(x: S32) -> S32
                          if x < 0
                            return 0
                          return x * 2
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Pass Statement Tests
    /// <summary>
    /// Tests Parse_PassStatement.
    /// </summary>

    [Fact]
    public void Parse_PassStatement()
    {
        string source = """
                        routine empty()
                          pass
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_PassInIfBranch.
    /// </summary>

    [Fact]
    public void Parse_PassInIfBranch()
    {
        string source = """
                        routine test()
                          if condition
                            pass
                          else
                            do_something()
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Stop and Verify Tests
    /// <summary>
    /// Tests Parse_StopWithMessage.
    /// </summary>

    [Fact]
    public void Parse_StopWithMessage()
    {
        string source = """
                        routine divide!(a: S32, b: S32) -> S32
                          if b == 0
                            stop!("Division by zero")
                          return a // b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_VerifyWithCondition.
    /// </summary>

    [Fact]
    public void Parse_VerifyWithCondition()
    {
        string source = """
                        routine process(data: List[S32])
                          verify!(data.length() > 0, "Data cannot be empty")
                          do_process(data)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_VerifyWithoutMessage.
    /// </summary>

    [Fact]
    public void Parse_VerifyWithoutMessage()
    {
        string source = """
                        routine test(x: S32)
                          verify!(x >= 0)
                          process(x)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_Breach.
    /// </summary>

    [Fact]
    public void Parse_Breach()
    {
        string source = """
                        routine unreachable()
                          breach!("Should never reach here")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_BreachWithoutMessage.
    /// </summary>

    [Fact]
    public void Parse_BreachWithoutMessage()
    {
        string source = """
                        routine test()
                          breach!()
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Complex Control Flow Tests
    /// <summary>
    /// Tests Parse_ComplexNestedControlFlow.
    /// </summary>

    [Fact]
    public void Parse_ComplexNestedControlFlow()
    {
        string source = """
                        routine process_matrix(matrix: List[List[S32]])
                          for row in matrix
                            for cell in row
                              if cell < 0
                                continue
                              if cell > 100
                                break
                              while cell > 0
                                cell -= 1
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_ControlFlowWithErrorHandling.
    /// </summary>

    [Fact]
    public void Parse_ControlFlowWithErrorHandling()
    {
        string source = """
                        routine search!(items: List[S32], target: S32) -> S32
                          for (index, item) in items.enumerate()
                            if item == target
                              return index
                          absent
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Suspended Routine Tests
    /// <summary>
    /// Tests Parse_SuspendedRoutine_RazorForge.
    /// </summary>

    [Fact]
    public void Parse_SuspendedRoutine_RazorForge()
    {
        string source = """
                        suspended routine fetch_value() -> S32
                          waitfor 10ms
                          return 42
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        Assert.Equal(expected: AsyncStatus.Suspended, actual: routine.Async);
        BlockStatement body = Assert.IsType<BlockStatement>(@object: routine.Body);
        Assert.Contains(collection: body.Statements,
            filter: stmt => stmt is ExpressionStatement
            {
                Expression: WaitforExpression
            });
    }

    #endregion
}
