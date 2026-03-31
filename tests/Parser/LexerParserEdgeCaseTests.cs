using Compiler.Lexer;
using SyntaxTree;
using Xunit;

namespace RazorForge.Tests.Parser;

using static TestHelpers;

/// <summary>
/// Tests lexer and parser edge cases around indentation, continuation, comments, and EOF handling.
/// </summary>
public class LexerParserEdgeCaseTests
{
    /// <summary>
    /// Tests Tokenize_MixedTabsAndSpacesInIndentation_Throws.
    /// </summary>
    [Fact]
    public void Tokenize_MixedTabsAndSpacesInIndentation_Throws()
    {
        string source = "routine test()\n\t  return\n";

        Assert.ThrowsAny<Exception>(testCode: () => Tokenize(source: source));
    }

    /// <summary>
    /// Tests Parse_TrailingWhitespaceWithinBlock_Parses.
    /// </summary>
    [Fact]
    public void Parse_TrailingWhitespaceWithinBlock_Parses()
    {
        string source = "routine test()   \n  var x = 1   \n  return   \n";

        AssertParses(source: source);
    }

    /// <summary>
    /// Tests Parse_EmptyLinesWithinBlock_Parses.
    /// </summary>
    [Fact]
    public void Parse_EmptyLinesWithinBlock_Parses()
    {
        string source = """
                        routine test()

                          var x = 1

                          return
                        """;

        AssertParses(source: source);
    }

    /// <summary>
    /// Tests Parse_ContinuationLineAfterBinaryOperator_Parses.
    /// </summary>
    [Fact]
    public void Parse_ContinuationLineAfterBinaryOperator_Parses()
    {
        string source = """
                        routine total() -> S32
                          return 1 +
                          2
                        """;

        AssertParses(source: source);
    }

    /// <summary>
    /// Tests Parse_DeeplyNestedScopes_Parses.
    /// </summary>
    [Fact]
    public void Parse_DeeplyNestedScopes_Parses()
    {
        string source = """
                        routine test(items: List[S32]) -> S32
                          if true
                            while true
                              for item in items
                                when item
                                  == 0 => return 0
                                  else => return item
                          return 1
                        """;

        AssertParses(source: source);
    }

    /// <summary>
    /// Tests Tokenize_DedentJumpingMultipleLevels_EmitsMultipleDedents.
    /// </summary>
    [Fact]
    public void Tokenize_DedentJumpingMultipleLevels_EmitsMultipleDedents()
    {
        string source = """
                        routine test()
                          if true
                            if true
                              var x = 1
                          return
                        """;

        List<Token> tokens = Tokenize(source: source);
        int dedentCount = tokens.Count(predicate: t => t.Type == TokenType.Dedent);

        Assert.True(condition: dedentCount >= 2,
            userMessage: $"Expected at least 2 dedent tokens, got {dedentCount}.");
    }

    /// <summary>
    /// Tests Parse_CommentOnlyLinesWithUnusualIndentation_Parses.
    /// </summary>
    [Fact]
    public void Parse_CommentOnlyLinesWithUnusualIndentation_Parses()
    {
        string source = """
                        routine test()
                          # regular comment
                                # oddly indented comment-only line
                          var x = 1
                          return
                        """;

        AssertParses(source: source);
    }

    /// <summary>
    /// Tests Parse_EofWithoutFinalNewline_Parses.
    /// </summary>
    [Fact]
    public void Parse_EofWithoutFinalNewline_Parses()
    {
        string source = "routine test()\n  return";

        Program program = Parse(source: source);

        Assert.NotNull(@object: program);
        Assert.NotEmpty(collection: program.Declarations);
    }
}
