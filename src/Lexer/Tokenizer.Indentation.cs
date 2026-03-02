using Compiler.Diagnostics;

namespace Compiler.Lexer;

using Lexer;

/// <summary>
/// Partial class containing indentation and newline handling methods for the unified tokenizer.
/// </summary>
/// <remarks>
/// <para>
/// Both RazorForge and Suflae use Python-style significant indentation for block structure.
/// This file contains the logic for:
/// </para>
/// <list type="bullet">
///   <item><description>Measuring indentation at the start of each line</description></item>
///   <item><description>Emitting INDENT tokens when indentation increases</description></item>
///   <item><description>Emitting DEDENT tokens when indentation decreases</description></item>
///   <item><description>Determining which newlines are significant</description></item>
/// </list>
/// </remarks>
public partial class Tokenizer
{
    #region Indentation Handling

    /// <summary>
    /// Handles indentation at the start of a line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is called at the beginning of each line (when column == 1).
    /// It measures the leading whitespace and compares it to the current
    /// indentation level to determine whether INDENT or DEDENT tokens are needed.
    /// </para>
    /// <para>
    /// Indentation rules:
    /// <list type="bullet">
    ///   <item><description>Each indentation level is 2 spaces</description></item>
    ///   <item><description>Tabs are counted as 2 spaces</description></item>
    ///   <item><description>Indentation must be a multiple of 2</description></item>
    ///   <item><description>Indentation increases start a new block</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <exception cref="GrammarException">
    /// Thrown when indentation is misaligned (not a multiple of 2).
    /// </exception>
    private void HandleIndentation()
    {
        int spaces = 0;
        bool hasSpaces = false;
        bool hasTabs = false;

        // Count leading whitespace
        while (Peek() == ' ' || Peek() == '\t')
        {
            if (Peek() == ' ')
            {
                spaces += 1;
                hasSpaces = true;
            }
            else // Tab counts as 2 spaces
            {
                spaces += 2;
                hasTabs = true;
            }

            Advance();
        }

        // Reject mixed tabs and spaces
        if (hasTabs && hasSpaces)
        {
            throw new GrammarException(
                GrammarDiagnosticCode.MixedTabsAndSpaces,
                "Indentation mixes tabs and spaces; use one or the other",
                _fileName, _line, _column, _language);
        }

        // Skip empty lines (don't change indentation state)
        // Note: Check for \r to handle CRLF line endings on Windows
        if (Peek() == '\n' || Peek() == '\r' || IsAtEnd())
        {
            return;
        }

        // Skip lines with only comments (don't change indentation state)
        if (Peek() == '#')
        {
            return;
        }

        int newIndentLevel = spaces / 2;

        // Validate indentation alignment
        if (spaces % 2 != 0)
        {
            throw new GrammarException(
                GrammarDiagnosticCode.InconsistentIndentation,
                $"Indentation error: expected multiple of 2 spaces, got {spaces} spaces",
                _fileName, _line, _column, _language);
        }

        // Handle indentation increase (new block)
        if (newIndentLevel > _currentIndentLevel)
        {
            // Ensure a Newline precedes the Indent token
            // (some tokens like > suppress newlines as continuation,
            //  but an indent always starts a new logical line)
            if (_tokens.Count == 0 || _tokens[^1].Type != TokenType.Newline)
            {
                AddToken(type: TokenType.Newline, text: "\\n");
            }

            AddToken(type: TokenType.Indent, text: "");
            _currentIndentLevel = newIndentLevel;
            return;
        }

        // Handle dedents when indentation decreases
        while (newIndentLevel < _currentIndentLevel)
        {
            AddToken(type: TokenType.Dedent, text: "");
            _currentIndentLevel -= 1;
        }
    }

    #endregion

    #region Newline Handling

    /// <summary>
    /// Handles a newline character, determining whether it's significant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Newlines are significant when they terminate a statement.
    /// However, newlines are ignored after certain tokens that indicate
    /// continuation (like open parentheses or binary operators).
    /// </para>
    /// <para>
    /// This method also resets the <see cref="_hasTokenOnLine"/> flag for
    /// the next line.
    /// </para>
    /// </remarks>
    private void HandleNewline()
    {
        bool isSignificant = IsNewlineSignificant();

        if (isSignificant)
        {
            AddToken(type: TokenType.Newline, text: "\\n");
        }

        _hasTokenOnLine = false;
    }

    /// <summary>
    /// Determines whether the current newline is significant (terminates a statement).
    /// </summary>
    /// <returns>
    /// <c>true</c> if the newline should produce a token; <c>false</c> if it should be ignored.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A newline is NOT significant (ignored) when:
    /// <list type="bullet">
    ///   <item><description>The line was empty (no tokens yet)</description></item>
    ///   <item><description>The last token was an opening delimiter ((, [)</description></item>
    ///   <item><description>The last token was a comma or dot</description></item>
    ///   <item><description>The last token was a binary operator (+, -, *, /, etc.)</description></item>
    ///   <item><description>The last token was an arrow (->, =>)</description></item>
    ///   <item><description>The last token was already a newline</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    private bool IsNewlineSignificant()
    {
        // No tokens on line = not significant
        if (!_hasTokenOnLine)
        {
            return false;
        }

        // No tokens at all = not significant
        if (_tokens.Count == 0)
        {
            return false;
        }

        TokenType lastToken = _tokens[^1].Type;

        return lastToken switch
        {
            // Opening delimiters - continuation expected
            TokenType.LeftParen => false,
            TokenType.LeftBracket => false,

            // Separators - continuation expected
            TokenType.Comma => false,
            TokenType.Dot => false,

            // Binary operators - continuation expected
            TokenType.Plus => false,
            TokenType.Minus => false,
            TokenType.Star => false,
            TokenType.Slash => false,
            TokenType.Equal => false,
            TokenType.Less => false,
            TokenType.Greater => false,
            TokenType.And => false,
            TokenType.Or => false,

            // Arrows - continuation expected
            TokenType.Arrow => false,
            TokenType.FatArrow => false,

            // Already a newline - don't duplicate
            TokenType.Newline => false,

            // Colon is significant (type annotations)
            TokenType.Colon => true,

            // Everything else is significant
            _ => true
        };
    }


    #endregion
}
