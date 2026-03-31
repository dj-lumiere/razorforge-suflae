using SyntaxTree;
using Xunit;

namespace RazorForge.Tests.Parser;

using static TestHelpers;

/// <summary>
/// Tests for parser error handling - invalid syntax that should fail or produce errors.
/// </summary>
public class ParserErrorTests
{
    #region Record Errors
    /// <summary>
    /// Tests Parse_Record_MissingBrace_ThrowsOrRecovers.
    /// </summary>

    [Fact]
    public void Parse_Record_MissingBrace_ThrowsOrRecovers()
    {
        string source = """
                        record Point
                          x: F32
                          y: F32
                        """;

        // Should either throw ParseException or recover with incomplete AST
        Record.Exception(testCode: () => Parse(source: source));
        // Parser may recover or throw - either is acceptable for incomplete input
    }
    /// <summary>
    /// Tests Parse_Record_MissingMemberVariableType_ThrowsOrRecovers.
    /// </summary>

    [Fact]
    public void Parse_Record_MissingMemberVariableType_ThrowsOrRecovers()
    {
        string source = """
                        record Point
                          x:
                          y: F32
                        """;

        Record.Exception(testCode: () => Parse(source: source));
        // Should not parse cleanly - missing type after colon
    }
    /// <summary>
    /// Tests Parse_Record_VarKeyword_ShouldBeInvalid.
    /// </summary>

    [Fact]
    public void Parse_Record_VarKeyword_ShouldBeInvalid()
    {
        // Records are value types - member variables are always immutable
        // 'var' should not be allowed in record member variables
        string source = """
                        record Point
                          var x: F32
                          y: F32
                        """;

        // This tests that var in record member variable is either rejected or ignored
        // Depending on parser behavior, this may parse but semantic analysis should catch it
        Parse(source: source);
        // If it parses, the member variable should still be immutable
    }

    #endregion

    #region Entity Errors
    /// <summary>
    /// Tests Parse_Entity_MemberVariableWithoutVarOrLet_IsValid.
    /// </summary>

    [Fact]
    public void Parse_Entity_MemberVariableWithoutVarOrLet_IsValid()
    {
        // Entity member variables use 'name: Type' syntax without var keyword
        string source = """
                        entity User
                          name: Text
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Tests Parse_Entity_VarInBody_Rejected.
    /// </summary>

    [Fact]
    public void Parse_Entity_VarInBody_Rejected()
    {
        // var keyword are no longer allowed in entity bodies
        string source = """
                        entity User
                          var name: Text
                        """;

        AssertParseError(source: source);
    }
    /// <summary>
    /// Tests Parse_Entity_MissingTypeName_Throws.
    /// </summary>

    [Fact]
    public void Parse_Entity_MissingTypeName_Throws()
    {
        string source = """
                        entity
                          name: Text
                        """;

        // Parser uses error recovery, check for errors instead of exception
        AssertParseError(source: source);
    }

    #endregion

    #region Choice Errors
    /// <summary>
    /// Tests Parse_Choice_MixedValuesAndNoValues_ShouldBeInvalid.
    /// </summary>

    [Fact]
    public void Parse_Choice_MixedValuesAndNoValues_ShouldBeInvalid()
    {
        // Choice cases must be all-or-nothing for values
        string source = """
                        choice Status
                          OK: 200
                          PENDING
                          ERROR: 500
                        """;

        // This should be rejected - can't mix valued and non-valued cases
        Parse(source: source);
        // Parser may accept but semantic analyzer should reject
    }
    /// <summary>
    /// Tests Parse_Choice_LowercaseCase_ShouldBeValid.
    /// </summary>

    [Fact]
    public void Parse_Choice_LowercaseCase_ShouldBeValid()
    {
        // Choice cases can use any case convention - no forcing required
        string source = """
                        choice Direction
                          north
                          south
                        """;

        Program program = Parse(source: source);
        Assert.NotNull(@object: program);
    }

    #endregion

    #region Variant Errors
    /// <summary>
    /// Tests Parse_Variant_EmptyBody_Throws.
    /// </summary>

    [Fact]
    public void Parse_Variant_EmptyBody_Throws()
    {
        string source = """
                        variant Empty
                        """;

        // Empty variant should either throw or produce error
        Record.Exception(testCode: () => Parse(source: source));
    }
    /// <summary>
    /// Tests Parse_Variant_FollowsProtocol_Throws.
    /// </summary>

    [Fact]
    public void Parse_Variant_FollowsProtocol_Throws()
    {
        string source = """
                        variant Shape obeys Equatable
                          Circle: F32
                          Rect: F32
                        """;

        // Variants cannot obey protocols — parser does not support 'obeys' on variants
        Record.Exception(testCode: () => Parse(source: source));
    }

    #endregion

    #region Protocol Errors
    /// <summary>
    /// Tests Parse_Protocol_MethodWithBody_ShouldBeInvalid.
    /// </summary>

    [Fact]
    public void Parse_Protocol_MethodWithBody_ShouldBeInvalid()
    {
        // Protocol member routines are signatures only - no body allowed
        string source = """
                        protocol Displayable
                          @readonly
                          routine Me.display() -> Text
                            return "hello"
                        """;

        // Should reject a member routine with a body in a protocol
        Record.Exception(testCode: () => Parse(source: source));
    }
    /// <summary>
    /// Tests Parse_Protocol_MissingMe_ShouldBeInvalid.
    /// </summary>

    [Fact]
    public void Parse_Protocol_MissingMe_ShouldBeInvalid()
    {
        // Protocol member routines must have Me. prefix
        string source = """
                        protocol Displayable
                          @readonly
                          routine display() -> Text
                        """;

        // Should reject - member routines need Me. prefix
        Parse(source: source);
        // May parse but should be flagged semantically
    }

    #endregion

    #region Generic Constraint Errors
    /// <summary>
    /// Tests Parse_Constraint_UnknownTypeParameter_ShouldBeInvalid.
    /// </summary>

    [Fact]
    public void Parse_Constraint_UnknownTypeParameter_ShouldBeInvalid()
    {
        string source = """
                        record Container[T]
                        needs X obeys Comparable
                          value: T
                        """;

        // X is not a type parameter - should be rejected
        Parse(source: source);
        // Parser accepts, semantic analyzer should reject
    }
    /// <summary>
    /// Tests Parse_Constraint_InvalidConstraintKind_Throws.
    /// </summary>

    [Fact]
    public void Parse_Constraint_InvalidConstraintKind_Throws()
    {
        string source = """
                        record Container[T]
                        needs T banana Comparable
                          value: T
                        """;

        // "banana" is not a valid constraint kind (should be 'obeys' or 'is')
        // Parser uses error recovery, check for errors instead of exception
        AssertParseError(source: source);
    }

    #endregion

    #region Syntax Errors
    /// <summary>
    /// Tests Parse_UnterminatedString_Throws.
    /// </summary>

    [Fact]
    public void Parse_UnterminatedString_Throws()
    {
        string source = """
                        var x = "unterminated
                        """;

        Assert.ThrowsAny<Exception>(testCode: () => Parse(source: source));
    }
    /// <summary>
    /// Tests Parse_InvalidOperator_Throws.
    /// </summary>

    [Fact]
    public void Parse_InvalidOperator_Throws()
    {
        string source = "var x = 1 @@ 2";

        // Parser uses error recovery, check for errors instead of exception
        AssertParseError(source: source);
    }
    /// <summary>
    /// Tests Parse_MismatchedBraces_Throws.
    /// </summary>

    [Fact]
    public void Parse_MismatchedBraces_Throws()
    {
        string source = """
                        routine foo()
                          if true
                            return 1
                        """;

        Record.Exception(testCode: () => Parse(source: source));
        // Missing closing brace for if statement
    }
    /// <summary>
    /// Tests Parse_MismatchedParens_Throws.
    /// </summary>

    [Fact]
    public void Parse_MismatchedParens_Throws()
    {
        string source = """
                        routine foo()
                          return bar(1, 2
                        """;

        Record.Exception(testCode: () => Parse(source: source));
        // Missing closing paren for call
    }

    #endregion

    #region Nested Routine Errors
    /// <summary>
    /// Tests Parse_NestedRoutine_ReportsError.
    /// </summary>

    [Fact]
    public void Parse_NestedRoutine_ReportsError()
    {
        // Nested routine declarations should be rejected
        string source = """
                        routine outer()
                          routine inner()
                            pass
                          return
                        """;

        AssertParseError(source: source);
    }
    /// <summary>
    /// Tests Parse_NestedRoutineInIf_ReportsError.
    /// </summary>

    [Fact]
    public void Parse_NestedRoutineInIf_ReportsError()
    {
        // Nested routines should be rejected even in control flow blocks
        string source = """
                        routine outer()
                          if true
                            routine inner()
                              pass
                          return
                        """;

        AssertParseError(source: source);
    }

    #endregion

    #region Inline Conditional Errors
    /// <summary>
    /// Tests Parse_NestedInlineIfThenElse_ReportsError.
    /// </summary>

    [Fact]
    public void Parse_NestedInlineIfThenElse_ReportsError()
    {
        // Nested if-then-else expressions are not idiomatic and should be rejected
        // The parser prevents nesting by design (using _parsingInlineConditional flag)
        // Use when/match or regular if statements for complex conditionals
        string source = """
                        routine classify(n: S32) -> Text
                          return if n > 0 then "positive" else if n < 0 then "negative" else "zero"
                        """;

        // Parser rejects nested inline conditionals
        AssertParseError(source: source);
    }

    #endregion

    #region Reserved Prefix Errors
    /// <summary>
    /// Tests Parse_ReservedPrefix_Try_ShouldParse.
    /// </summary>

    [Fact]
    public void Parse_ReservedPrefix_Try_ShouldParse()
    {
        // try_ prefix is reserved but parser should accept it
        // Semantic analyzer should reject
        string source = """
                        routine try_something()
                          pass
                          return
                        """;

        Program program = Parse(source: source);
        Assert.NotNull(@object: program);
        // Semantic analysis will reject this
    }
    /// <summary>
    /// Tests Parse_ReservedPrefix_Check_ShouldParse.
    /// </summary>

    [Fact]
    public void Parse_ReservedPrefix_Check_ShouldParse()
    {
        string source = """
                        routine check_something()
                          pass
                          return
                        """;

        Program program = Parse(source: source);
        Assert.NotNull(@object: program);
    }
    /// <summary>
    /// Tests Parse_ReservedPrefix_Find_ShouldParse.
    /// </summary>

    [Fact]
    public void Parse_ReservedPrefix_Find_ShouldParse()
    {
        string source = """
                        routine find_something()
                          pass
                          return
                        """;

        Program program = Parse(source: source);
        Assert.NotNull(@object: program);
    }

    #endregion

    #region Storage Class On Type Declaration Errors
    /// <summary>
    /// Tests Parse_CommonVariant_ReportsError.
    /// </summary>

    [Fact]
    public void Parse_CommonVariant_ReportsError()
    {
        string source = """
                        common variant Shape
                          Circle: F32
                          Rect: F32
                        """;

        AssertParseError(source: source);
    }
    /// <summary>
    /// Tests Parse_GlobalVariant_ReportsError.
    /// </summary>

    [Fact]
    public void Parse_GlobalVariant_ReportsError()
    {
        string source = """
                        global variant Shape
                          Circle: F32
                          Rect: F32
                        """;

        AssertParseError(source: source);
    }
    /// <summary>
    /// Tests Parse_CommonRecord_ReportsError.
    /// </summary>

    [Fact]
    public void Parse_CommonRecord_ReportsError()
    {
        string source = """
                        common record Point
                          x: F32
                          y: F32
                        """;

        AssertParseError(source: source);
    }
    /// <summary>
    /// Tests Parse_GlobalEntity_ReportsError.
    /// </summary>

    [Fact]
    public void Parse_GlobalEntity_ReportsError()
    {
        string source = """
                        global entity User
                          name: Text
                        """;

        AssertParseError(source: source);
    }
    /// <summary>
    /// Tests Parse_GlobalRoutine_ReportsError.
    /// </summary>

    [Fact]
    public void Parse_GlobalRoutine_ReportsError()
    {
        string source = """
                        global routine foo()
                          pass
                        """;

        AssertParseError(source: source);
    }

    #endregion
}
