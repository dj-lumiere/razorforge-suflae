using Xunit;

namespace RazorForge.Tests.Parser;

using SyntaxTree;
using static TestHelpers;

/// <summary>
/// Tests for parsing error handling syntax: failable routines (!), throw, absent.
/// </summary>
public class ErrorHandlingTests
{
    #region Failable Routine Declaration
    /// <summary>
    /// Tests Parse_FailableRoutine_WithBang.
    /// </summary>

    [Fact]
    public void Parse_FailableRoutine_WithBang()
    {
        string source = """
                        routine get_value!() -> S32
                          return 42
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        Assert.Equal(expected: "get_value", actual: routine.Name);
        Assert.True(condition: routine.IsFailable);
    }
    /// <summary>
    /// Tests Parse_FailableRoutine_WithParameter.
    /// </summary>

    [Fact]
    public void Parse_FailableRoutine_WithParameter()
    {
        string source = """
                        routine parse_int!(text: Text) -> S32
                          return 42
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        Assert.True(condition: routine.IsFailable);
        Assert.Single(collection: routine.Parameters);
    }
    /// <summary>
    /// Tests Parse_FailableMethod_WithBang.
    /// </summary>

    [Fact]
    public void Parse_FailableMethod_WithBang()
    {
        string source = """
                        routine User.validate!() -> bool
                          return true
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        Assert.Equal(expected: "User.validate", actual: routine.Name);
        Assert.True(condition: routine.IsFailable);
    }
    /// <summary>
    /// Tests Parse_NonFailableRoutine.
    /// </summary>

    [Fact]
    public void Parse_NonFailableRoutine()
    {
        string source = """
                        routine get_value() -> S32
                          return 42
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        Assert.False(condition: routine.IsFailable);
    }

    #endregion

    #region Throw Statement
    /// <summary>
    /// Tests Parse_ThrowStatement_Simple.
    /// </summary>

    [Fact]
    public void Parse_ThrowStatement_Simple()
    {
        string source = """
                        routine validate!(x: S32) -> S32
                          if x < 0
                            throw ValidationError("negative value")
                          return x
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        // Find throw statement in body
        var body = routine.Body as BlockStatement;
        Assert.NotNull(@object: body);
        IfStatement ifStmt = body.Statements
                                 .OfType<IfStatement>()
                                 .First();
        var thenBlock = ifStmt.ThenStatement as BlockStatement;
        Assert.NotNull(@object: thenBlock);
        ThrowStatement? throwStmt = thenBlock.Statements
                                             .OfType<ThrowStatement>()
                                             .FirstOrDefault();
        Assert.NotNull(@object: throwStmt);
    }
    /// <summary>
    /// Tests Parse_ThrowStatement_WithExpression.
    /// </summary>

    [Fact]
    public void Parse_ThrowStatement_WithExpression()
    {
        string source = """
                        routine fail!() -> S32
                          throw CustomError(code: 123, message: "failed")
                          return
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        var body = routine.Body as BlockStatement;
        Assert.NotNull(@object: body);
        ThrowStatement throwStmt = body.Statements
                                       .OfType<ThrowStatement>()
                                       .First();
        Assert.NotNull(@object: throwStmt.Error);
    }

    #endregion

    #region Absent Statement
    /// <summary>
    /// Tests Parse_AbsentStatement.
    /// </summary>

    [Fact]
    public void Parse_AbsentStatement()
    {
        string source = """
                        routine find!(id: U64) -> User
                          if id == 0
                            absent
                          return get_user(id)
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        var body = routine.Body as BlockStatement;
        Assert.NotNull(@object: body);
        IfStatement ifStmt = body.Statements
                                 .OfType<IfStatement>()
                                 .First();
        var thenBlock = ifStmt.ThenStatement as BlockStatement;
        Assert.NotNull(@object: thenBlock);
        AbsentStatement? absentStmt = thenBlock.Statements
                                               .OfType<AbsentStatement>()
                                               .FirstOrDefault();
        Assert.NotNull(@object: absentStmt);
    }
    /// <summary>
    /// Tests Parse_AbsentStatement_InUnless.
    /// </summary>

    [Fact]
    public void Parse_AbsentStatement_InUnless()
    {
        // unless parses til IfStatement with negated condition
        string source = """
                        routine get!(key: Text) -> Value
                          unless cache.has(key)
                            absent
                          return cache.get(key)
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        var body = routine.Body as BlockStatement;
        Assert.NotNull(@object: body);
        // Unless is parsed as an IfStatement with negated condition
        IfStatement ifStmt = body.Statements
                                 .OfType<IfStatement>()
                                 .First();
        var thenBlock = ifStmt.ThenStatement as BlockStatement;
        Assert.NotNull(@object: thenBlock);
        AbsentStatement? absentStmt = thenBlock.Statements
                                               .OfType<AbsentStatement>()
                                               .FirstOrDefault();
        Assert.NotNull(@object: absentStmt);
    }

    #endregion

    #region Combined Throw and Absent
    /// <summary>
    /// Tests Parse_RoutineWithBothThrowAndAbsent.
    /// </summary>

    [Fact]
    public void Parse_RoutineWithBothThrowAndAbsent()
    {
        string source = """
                        routine get_user!(id: U64) -> User
                          if id == 0
                            throw ValidationError("invalid id")
                          unless database.has_user(id)
                            absent
                          return database.get_user(id)
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        Assert.True(condition: routine.IsFailable);

        var body = routine.Body as BlockStatement;
        Assert.NotNull(@object: body);

        // Has both throw and absent
        bool hasThrow = body.Statements
                            .OfType<IfStatement>()
                            .Any(predicate: s => (s.ThenStatement as BlockStatement)?.Statements
                                .OfType<ThrowStatement>()
                                .Any() ?? false);
        bool hasAbsent = body.Statements
                             .OfType<IfStatement>()
                             .Any(predicate: s => (s.ThenStatement as BlockStatement)?.Statements
                                 .OfType<AbsentStatement>()
                                 .Any() ?? false);

        Assert.True(condition: hasThrow);
        Assert.True(condition: hasAbsent);
    }

    #endregion

    #region Maybe Type (?)
    /// <summary>
    /// Tests Parse_MaybeReturnType.
    /// </summary>

    [Fact]
    public void Parse_MaybeReturnType()
    {
        string source = """
                        routine try_get(id: U64) -> User?
                          if id == 0
                            return None
                          return get_user(id)
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        // Return type should be Maybe[User] or User?
        Assert.NotNull(@object: routine.ReturnType);
    }
    /// <summary>
    /// Tests Parse_MaybeParameter.
    /// </summary>

    [Fact]
    public void Parse_MaybeParameter()
    {
        string source = """
                        routine process(data: Text?)
                          pass
                          return
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        Assert.Single(collection: routine.Parameters);
    }
    /// <summary>
    /// Tests Parse_MaybeVariable.
    /// </summary>

    [Fact]
    public void Parse_MaybeVariable()
    {
        string source = """
                        routine foo()
                          var x: S32? = None
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region None Coalescing (??)
    /// <summary>
    /// Tests Parse_NoneCoalescingOperator.
    /// </summary>

    [Fact]
    public void Parse_NoneCoalescingOperator()
    {
        string source = """
                        routine get_or_default() -> S32
                          var value: S32? = None
                          return value ?? 42
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_ChainedNoneCoalescing.
    /// </summary>

    [Fact]
    public void Parse_ChainedNoneCoalescing()
    {
        string source = """
                        routine get_first_available() -> S32
                          var a: S32? = None
                          var b: S32? = None
                          var c: S32 = 100
                          return a ?? b ?? c
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Pattern Matching with Error Types
    /// <summary>
    /// Tests Parse_WhenExpression_WithMaybe.
    /// </summary>

    [Fact]
    public void Parse_WhenExpression_WithMaybe()
    {
        string source = """
                        routine handle(value: User?)
                          when value
                            is None => show("not found")
                            else u => show(u.name)
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

    // Result[T] and Lookup[T] parameter tests moved til Analyzer/ErrorVariantGenerationTests.cs
    // They parse correctly but should be rejected by semantic analysis

    #endregion

    #region Error Cases
    /// <summary>
    /// Tests Parse_ThrowInNonFailableRoutine_ShouldParse.
    /// </summary>

    [Fact]
    public void Parse_ThrowInNonFailableRoutine_ShouldParse()
    {
        // Parser accepts, semantic analyzer should reject
        string source = """
                        routine not_failable()
                          throw SomeError()
                          return
                        """;

        Program program = Parse(source: source);
        Assert.NotNull(@object: program);
        // Semantic analysis will catch this error
    }
    /// <summary>
    /// Tests Parse_AbsentInNonFailableRoutine_ShouldParse.
    /// </summary>

    [Fact]
    public void Parse_AbsentInNonFailableRoutine_ShouldParse()
    {
        // Parser accepts, semantic analyzer should reject
        string source = """
                        routine not_failable()
                          absent
                          return
                        """;

        Program program = Parse(source: source);
        Assert.NotNull(@object: program);
    }
    /// <summary>
    /// Tests Parse_ThrowWithoutExpression_Throws.
    /// </summary>

    [Fact]
    public void Parse_ThrowWithoutExpression_Throws()
    {
        string source = """
                        routine fail!()
                          throw
                          return
                        """;

        // Parser uses error recovery, check for errors instead of exception
        AssertParseError(source: source);
    }

    #endregion
}
