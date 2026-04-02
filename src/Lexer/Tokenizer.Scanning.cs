using Compiler.Diagnostics;

namespace Compiler.Lexer;

/// <summary>
/// Partial class containing the main token scanning dispatch logic for the unified tokenizer.
/// </summary>
public partial class Tokenizer
{
    #region Main Token Scanning

    /// <summary>
    /// Scans a single token from the current position in the source code.
    /// </summary>
    private void ScanToken()
    {
        // Handle indentation at start of line
        if (_column == 1)
        {
            HandleIndentation();
            if (IsAtEnd())
            {
                return;
            }
        }

        // Skip non-newline whitespace and update token start
        while (Peek() == ' ' || Peek() == '\t' || Peek() == '\r')
        {
            Advance();
        }

        _tokenStart = _position;
        _tokenStartColumn = _column;
        _tokenStartLine = _line;
        char c = Advance();

        switch (c)
        {
            // Whitespace already handled above, but case needed for post-indent whitespace
            case ' ' or '\r' or '\t':
                break;

            // Newlines are significant
            case '\n':
                HandleNewline();
                break;

            // Comments
            case '#':
                ScanComment();
                break;

            // String and character literals
            case '"':
                ScanString();
                break;
            case '\'':
                ScanCharacter();
                break;

            // Potential prefixed literals or identifiers
            case 'r' or 'f':
                if (!TryParseTextPrefix())
                {
                    ScanIdentifier();
                }

                break;
            case 'b':
                // Could be bytes prefix (b"..."), byte character (b'x'), or identifier
                if (!TryParseTextPrefix() && !TryParseByteLiteralPrefix())
                {
                    ScanIdentifier();
                }

                break;

            // Delimiters
            case '(':
                AddToken(type: TokenType.LeftParen);
                _bracketDepth++;
                break;
            case ')':
                AddToken(type: TokenType.RightParen);
                if (_bracketDepth > 0)
                {
                    _bracketDepth--;
                }

                break;
            case '[':
                AddToken(type: TokenType.LeftBracket);
                _bracketDepth++;
                break;
            case ']':
                AddToken(type: TokenType.RightBracket);
                if (_bracketDepth > 0)
                {
                    _bracketDepth--;
                }

                break;
            // Braces kept for set/dict literals and f-text inserting (not block delimiters)
            case '{':
                AddToken(type: TokenType.LeftBrace);
                _bracketDepth++;
                break;
            case '}':
                AddToken(type: TokenType.RightBrace);
                if (_bracketDepth > 0)
                {
                    _bracketDepth--;
                }

                break;
            case ',':
                AddToken(type: TokenType.Comma);
                break;
            case ';':
                throw new GrammarException(code: GrammarDiagnosticCode.InvalidCharacter,
                    message: "Semicolons are not used. Statements are terminated by newlines.",
                    fileName: _fileName,
                    line: _line,
                    column: _column,
                    language: _language);

            // Multi-character punctuation
            case '.':
                if (Match(expected: '.'))
                {
                    if (Match(expected: '.'))
                    {
                        AddToken(type: TokenType.DotDotDot);
                    }
                    else
                    {
                        throw new GrammarException(code: GrammarDiagnosticCode.InvalidCharacter,
                            message:
                            "Range operator '..' is no longer supported. Use 'to' keyword instead (e.g., '1 to 10').",
                            fileName: _fileName,
                            line: _line,
                            column: _column,
                            language: _language);
                    }
                }
                else
                {
                    AddToken(type: TokenType.Dot);
                }

                break;
            case ':':
                if (Match(expected: ':'))
                {
                    throw new GrammarException(code: GrammarDiagnosticCode.InvalidCharacter,
                        message:
                        "Static access operator '::' is no longer supported. Use '.' instead.",
                        fileName: _fileName,
                        line: _line,
                        column: _column,
                        language: _language);
                }

                AddToken(type: TokenType.Colon);
                break;

            // Arithmetic operators with overflow variants
            case '+':
                ScanPlusOperator();
                break;
            case '-':
                if (Match(expected: '>'))
                {
                    AddToken(type: TokenType.Arrow);
                }
                else
                {
                    ScanMinusOperator();
                }

                break;
            case '*':
                ScanStarOperator();
                break;
            case '/':
                ScanSlashOperator();
                break;
            case '%':
                ScanPercentOperator();
                break;

            // Comparison and assignment
            case '=':
                AddToken(type: Match(expected: '=') ? Match(expected: '=')
                        ? TokenType.ReferenceEqual
                        : TokenType.Equal :
                    Match(expected: '>') ? TokenType.FatArrow : TokenType.Assign);
                break;
            case '!':
                if (Match(expected: '='))
                {
                    // != or !==
                    AddToken(type: Match(expected: '=')
                        ? TokenType.ReferenceNotEqual
                        : TokenType.NotEqual);
                }
                else if (Match(expected: '!'))
                {
                    // !! (force unwrap)
                    AddToken(type: TokenType.BangBang);
                }
                else
                {
                    // ! (failable marker or negation)
                    AddToken(type: TokenType.Bang);
                }

                break;
            case '<':
                ScanLessThanOperator();
                break;
            case '>':
                ScanGreaterThanOperator();
                break;

            // Single-character operators (with compound assignment variants)
            case '&':
                AddToken(type: Match(expected: '=')
                    ? TokenType.AmpersandAssign
                    : TokenType.Ampersand);
                break;
            case '|':
                AddToken(type: Match(expected: '=')
                    ? TokenType.PipeAssign
                    : TokenType.Pipe);
                break;
            case '^':
                AddToken(type: Match(expected: '=')
                    ? TokenType.CaretAssign
                    : TokenType.Caret);
                break;
            case '~':
                AddToken(type: TokenType.Tilde);
                break;
            case '?':
                if (Match(expected: '.'))
                {
                    AddToken(type: TokenType.QuestionDot);
                }
                else if (Match(expected: '?'))
                {
                    // ?? or ??=
                    AddToken(type: Match(expected: '=')
                        ? TokenType.NoneCoalesceAssign
                        : TokenType.NoneCoalesce);
                }
                else
                {
                    AddToken(type: TokenType.Question);
                }

                break;

            // Special @ tokens
            case '@':
                ScanAtSign();
                break;

            // Numbers (special handling for 0x, 0b, and 0o prefixes)
            case '0':
                if (Match(expected: 'x') || Match(expected: 'X'))
                {
                    ScanPrefixedNumber(isHex: true);
                }
                else if ((Peek() == 'b' || Peek() == 'B') && (Peek(offset: 1) == '0' ||
                                                              Peek(offset: 1) == '1' ||
                                                              Peek(offset: 1) == '_'))
                {
                    Advance(); // consume 'b' or 'B'
                    ScanPrefixedNumber(isHex: false);
                }
                else if ((Peek() == 'o' || Peek() == 'O') &&
                         (Peek(offset: 1) >= '0' && Peek(offset: 1) <= '7' ||
                          Peek(offset: 1) == '_'))
                {
                    Advance(); // consume 'o' or 'O'
                    ScanOctalNumber();
                }
                else
                {
                    ScanNumber();
                }

                break;

            // Default: digits, identifiers, or unknown
            default:
                if (char.IsDigit(c: c))
                {
                    ScanNumber();
                }
                else if (IsIdentifierStart(c: c))
                {
                    ScanIdentifier();
                }
                else
                {
                    AddToken(type: TokenType.Unknown);
                }

                break;
        }
    }

    #endregion
}
