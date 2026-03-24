using Compiler.Diagnostics;

namespace Compiler.Lexer;

/// <summary>
/// Partial class containing string and character literal scanning methods for the unified tokenizer.
/// </summary>
public partial class Tokenizer
{
    #region String Literals

    /// <summary>
    /// Scans a basic string literal (without prefix).
    /// </summary>
    private void ScanString()
    {
        ScanStringLiteral(isRaw: false,
            isFormatted: false,
            tokenType: TokenType.TextLiteral,
            bitWidth: 32);
    }

    /// <summary>
    /// Attempts to parse a text prefix (r, f, rf, b, br) followed by a quoted string.
    /// </summary>
    /// <returns>
    /// <c>true</c> if a valid text prefix was found; <c>false</c> otherwise.
    /// </returns>
    private bool TryParseTextPrefix()
    {
        int startPos = _position - 1;
        int originalPos = _position;
        int originalCol = _column;

        char firstChar = _source[index: startPos];
        string prefix = firstChar.ToString();

        // Greedy match: build the longest valid prefix
        while (!IsAtEnd() && char.IsLetterOrDigit(c: Peek()))
        {
            string testPrefix = prefix + Peek();
            if (_textPrefixes.Any(predicate: p => p.StartsWith(value: testPrefix)))
            {
                prefix += Advance();
            }
            else
            {
                break;
            }
        }

        // Check if we found a valid prefix
        if (!_textPrefixToTokenType.ContainsKey(key: prefix))
        {
            _position = originalPos;
            _column = originalCol;
            return false;
        }

        // Must be followed by a quote
        if (Peek() != '"')
        {
            _position = originalPos;
            _column = originalCol;
            return false;
        }

        Advance(); // consume opening quote

        TokenType tokenType = _textPrefixToTokenType[key: prefix];
        bool isRaw = prefix.Contains(value: 'r');
        bool isFormatted = prefix.Contains(value: 'f');

        // Byte strings are 8-bit, regular strings are 32-bit
        int bitWidth = prefix.Contains(value: 'b')
            ? 8
            : 32;

        ScanStringLiteral(isRaw: isRaw,
            isFormatted: isFormatted,
            tokenType: tokenType,
            bitWidth: bitWidth);
        return true;
    }

    /// <summary>
    /// Scans a string literal with the specified properties.
    /// For formatted strings, delegates to ScanFormattedStringLiteral.
    /// </summary>
    private void ScanStringLiteral(bool isRaw, bool isFormatted, TokenType tokenType,
        int bitWidth = 32)
    {
        if (isFormatted)
        {
            ScanFormattedStringLiteral(isRaw: isRaw);
            return;
        }

        int startLine = _line;
        int startColumn = _column;
        var content = new System.Text.StringBuilder();

        while (!IsAtEnd() && Peek() != '"')
        {
            if (Peek() == '\n')
            {
                content.Append(value: '\n');
                Advance();
            }
            else if (!isRaw && Peek() == '\\')
            {
                int escapeStart = _position;
                Advance(); // consume backslash

                // Check for line continuation (\ followed by newline)
                if (Peek() == '\n' || Peek() == '\r')
                {
                    // Line continuation: skip newline and leading whitespace, don't add to content
                    ScanEscapeSequence(bitWidth: bitWidth);
                }
                else
                {
                    ScanEscapeSequence(bitWidth: bitWidth);
                    content.Append(value: ParseEscapeSequence(escapeStart: escapeStart,
                        bitWidth: bitWidth));
                }
            }
            else
            {
                char c = Advance();
                if (bitWidth == 8 && c > '\x7F')
                {
                    throw new GrammarException(
                        GrammarDiagnosticCode.InvalidEscapeSequence,
                        $"Non-ASCII character '{c}' (U+{(int)c:X4}) in byte literal. " +
                        "Byte literals only accept ASCII (0x00-0x7F). Use \"text\".encode_as(UTF8) instead.",
                        _fileName, _line, _column, _language);
                }
                content.Append(value: c);
            }
        }

        if (IsAtEnd())
        {
            throw new GrammarException(
                GrammarDiagnosticCode.UnterminatedString,
                $"Unterminated text starting at line {startLine}, column {startColumn}",
                _fileName, startLine, startColumn, _language);
        }

        Advance(); // consume closing quote
        AddToken(type: tokenType, text: content.ToString());
    }

    /// <summary>
    /// Scans a formatted string literal (f"..." or rf"..."), emitting a structured token sequence:
    /// InsertionStart, TextSegment*, (LeftBrace, expr tokens, RightBrace)*, InsertionEnd
    /// </summary>
    private void ScanFormattedStringLiteral(bool isRaw)
    {
        int startLine = _line;
        int startColumn = _column;

        // Emit InsertionStart token (text = "f\"" or "rf\"")
        string prefix = isRaw ? "rf\"" : "f\"";
        AddToken(type: TokenType.InsertionStart, text: prefix);

        var textBuffer = new System.Text.StringBuilder();

        while (!IsAtEnd())
        {
            char c = Peek();

            if (c == '"')
            {
                // End of f-string — flush remaining text and emit InsertionEnd
                FlushTextSegment(textBuffer: textBuffer);
                Advance(); // consume closing quote
                _tokenStart = _position - 1;
                _tokenStartColumn = _column - 1;
                _tokenStartLine = _line;
                AddToken(type: TokenType.InsertionEnd, text: "\"");
                return;
            }

            if (c == '{')
            {
                if (Peek(offset: 1) == '{')
                {
                    // Escaped brace {{ → literal {
                    Advance();
                    Advance();
                    textBuffer.Append(value: '{');
                    continue;
                }

                // Start of insertion expression — flush text, emit LeftBrace
                FlushTextSegment(textBuffer: textBuffer);
                _tokenStart = _position;
                _tokenStartColumn = _column;
                _tokenStartLine = _line;
                Advance(); // consume {
                AddToken(type: TokenType.LeftBrace, text: "{");
                _bracketDepth++;
                ScanInsertionExpression();
                continue;
            }

            if (c == '}')
            {
                if (Peek(offset: 1) == '}')
                {
                    // Escaped brace }} → literal }
                    Advance();
                    Advance();
                    textBuffer.Append(value: '}');
                    continue;
                }

                // Unmatched } outside insertion — treat as error
                throw new GrammarException(
                    GrammarDiagnosticCode.UnexpectedToken,
                    "Unmatched '}' in formatted text. Use '}}' for a literal brace.",
                    _fileName, _line, _column, _language);
            }

            if (!isRaw && c == '\\')
            {
                // Process escape sequence
                int escapeStart = _position;
                Advance(); // consume backslash
                if (Peek() == '\n' || Peek() == '\r')
                {
                    ScanEscapeSequence(bitWidth: 32);
                }
                else
                {
                    ScanEscapeSequence(bitWidth: 32);
                    textBuffer.Append(value: ParseEscapeSequence(escapeStart: escapeStart, bitWidth: 32));
                }
                continue;
            }

            if (c == '\n')
            {
                textBuffer.Append(value: '\n');
                Advance();
                continue;
            }

            // Regular character
            textBuffer.Append(value: Advance());
        }

        // Reached EOF without closing quote
        throw new GrammarException(
            GrammarDiagnosticCode.UnterminatedString,
            $"Unterminated formatted text starting at line {startLine}, column {startColumn}",
            _fileName, startLine, startColumn, _language);
    }

    /// <summary>
    /// Scans the tokens inside an insertion expression ({...}) within a formatted string.
    /// Delegates to ScanToken() for each token until the matching } is found.
    /// </summary>
    private void ScanInsertionExpression()
    {
        int entryDepth = _bracketDepth;

        while (!IsAtEnd())
        {
            // Skip whitespace inside the insertion expression
            while (!IsAtEnd() && (Peek() == ' ' || Peek() == '\t' || Peek() == '\r' || Peek() == '\n'))
            {
                if (Peek() == '\n')
                {
                    _line++;
                    _column = 0;
                }
                Advance();
            }

            if (IsAtEnd()) break;

            _tokenStart = _position;
            _tokenStartColumn = _column;
            _tokenStartLine = _line;

            // Check for } at entry depth — end of insertion
            if (Peek() == '}' && _bracketDepth == entryDepth)
            {
                Advance(); // consume }
                AddToken(type: TokenType.RightBrace, text: "}");
                _bracketDepth--;
                return;
            }

            // Check for : at entry depth — format specifier
            if (Peek() == ':' && _bracketDepth == entryDepth)
            {
                ScanFormatSpec(entryDepth: entryDepth);
                continue;
            }

            // Otherwise, scan a regular token
            char c = Advance();

            switch (c)
            {
                case '(':
                    AddToken(type: TokenType.LeftParen);
                    _bracketDepth++;
                    break;
                case ')':
                    AddToken(type: TokenType.RightParen);
                    if (_bracketDepth > 0) _bracketDepth--;
                    break;
                case '[':
                    AddToken(type: TokenType.LeftBracket);
                    _bracketDepth++;
                    break;
                case ']':
                    AddToken(type: TokenType.RightBracket);
                    if (_bracketDepth > 0) _bracketDepth--;
                    break;
                case '{':
                    AddToken(type: TokenType.LeftBrace);
                    _bracketDepth++;
                    break;
                case '}':
                    AddToken(type: TokenType.RightBrace);
                    if (_bracketDepth > 0) _bracketDepth--;
                    break;
                case ',':
                    AddToken(type: TokenType.Comma);
                    break;
                case '.':
                    AddToken(type: TokenType.Dot);
                    break;
                case '+':
                    AddToken(type: TokenType.Plus);
                    break;
                case '-':
                    if (Match(expected: '>'))
                        AddToken(type: TokenType.Arrow);
                    else
                        AddToken(type: TokenType.Minus);
                    break;
                case '*':
                    if (Match(expected: '*'))
                        AddToken(type: TokenType.Power);
                    else
                        AddToken(type: TokenType.Star);
                    break;
                case '/':
                    if (Match(expected: '/'))
                        AddToken(type: TokenType.Divide);
                    else
                        AddToken(type: TokenType.Slash);
                    break;
                case '%':
                    AddToken(type: TokenType.Percent);
                    break;
                case '=':
                    if (Match(expected: '='))
                        AddToken(type: TokenType.Equal);
                    else
                        AddToken(type: TokenType.Assign);
                    break;
                case '!':
                    if (Match(expected: '='))
                        AddToken(type: TokenType.NotEqual);
                    else
                        AddToken(type: TokenType.Bang);
                    break;
                case '<':
                    if (Match(expected: '='))
                        AddToken(type: TokenType.LessEqual);
                    else
                        AddToken(type: TokenType.Less);
                    break;
                case '>':
                    if (Match(expected: '='))
                        AddToken(type: TokenType.GreaterEqual);
                    else
                        AddToken(type: TokenType.Greater);
                    break;
                case '"':
                    ScanString();
                    break;
                case '\'':
                    ScanLetter();
                    break;
                default:
                    if (char.IsDigit(c: c))
                    {
                        ScanNumber();
                    }
                    else if (char.IsLetter(c: c) || c == '_')
                    {
                        ScanIdentifier();
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Scans a format specifier after ':' inside an insertion expression.
    /// Consumes everything from ':' until '}' at entry depth as a single FormatSpec token.
    /// </summary>
    private void ScanFormatSpec(int entryDepth)
    {
        Advance(); // consume ':'
        _tokenStart = _position;
        _tokenStartColumn = _column;
        _tokenStartLine = _line;

        var spec = new System.Text.StringBuilder();
        int depth = _bracketDepth;

        while (!IsAtEnd())
        {
            char c = Peek();
            if (c == '}' && depth == entryDepth)
            {
                break;
            }
            if (c == '{') depth++;
            if (c == '}') depth--;
            spec.Append(value: Advance());
        }

        if (spec.Length > 0)
        {
            AddToken(type: TokenType.FormatSpec, text: spec.ToString());
        }
    }

    /// <summary>
    /// Flushes accumulated text in the buffer as a TextSegment token.
    /// </summary>
    private void FlushTextSegment(System.Text.StringBuilder textBuffer)
    {
        if (textBuffer.Length > 0)
        {
            _tokenStart = _position;
            _tokenStartColumn = _column;
            _tokenStartLine = _line;
            AddToken(type: TokenType.TextSegment, text: textBuffer.ToString());
            textBuffer.Clear();
        }
    }

    #endregion

    #region Character Literals

    /// <summary>
    /// Scans a basic character literal (single-quoted).
    /// </summary>
    private void ScanLetter()
    {
        ScanLetterLiteral(tokenType: TokenType.LetterLiteral, bitWidth: 32);
    }

    /// <summary>
    /// Attempts to parse a b'x' byte character literal.
    /// </summary>
    private bool TryParseByteLiteralPrefix()
    {
        // We already consumed 'b', check if followed by single quote
        if (Peek() != '\'')
        {
            return false;
        }

        Advance(); // consume opening quote
        ScanLetterLiteral(tokenType: TokenType.ByteLetterLiteral, bitWidth: 8);
        return true;
    }

    /// <summary>
    /// Scans a character literal with the specified token type and bit width.
    /// </summary>
    private void ScanLetterLiteral(TokenType tokenType, int bitWidth = 32)
    {
        if (Peek() == '\\')
        {
            Advance(); // consume backslash

            // \x is byte-only, \u is letter-only
            if (bitWidth != 8 && Peek() == 'x')
            {
                throw new GrammarException(
                    GrammarDiagnosticCode.InvalidEscapeSequence,
                    "Hex escape \\x is not valid in letter literals. Use \\u for Unicode codepoints.",
                    _fileName, _line, _column, _language);
            }

            ScanEscapeSequence(bitWidth: bitWidth);
        }
        else
        {
            char c = Peek();
            if (bitWidth == 8 && c > '\x7F')
            {
                throw new GrammarException(
                    GrammarDiagnosticCode.InvalidEscapeSequence,
                    $"Non-ASCII character '{c}' (U+{(int)c:X4}) in byte literal. " +
                    "Byte literals only accept ASCII (0x00-0x7F).",
                    _fileName, _line, _column, _language);
            }
            Advance(); // consume the character
            // Handle UTF-16 surrogate pairs for non-BMP codepoints (Letter is UTF-32)
            if (bitWidth != 8 && char.IsHighSurrogate(c) && !IsAtEnd() && char.IsLowSurrogate(Peek()))
            {
                Advance(); // consume low surrogate
            }
        }

        if (!Match(expected: '\''))
        {
            throw new GrammarException(
                GrammarDiagnosticCode.UnterminatedString,
                "Unterminated character literal",
                _fileName, _line, _column, _language);
        }

        AddToken(type: tokenType);
    }

    #endregion

    #region Escape Sequences

    /// <summary>
    /// Scans and validates an escape sequence.
    /// </summary>
    private void ScanEscapeSequence(int bitWidth = 32)
    {
        if (IsAtEnd())
        {
            throw new GrammarException(
                GrammarDiagnosticCode.InvalidEscapeSequence,
                "Unterminated escape sequence",
                _fileName, _line, _column, _language);
        }

        char escapeChar = Peek();
        switch (escapeChar)
        {
            case 'n' or 't' or 'r' or '\\' or '"' or '\'' or '0':
                Advance();
                break;
            case 'x':
                Advance(); // consume 'x'
                ScanHexByteEscape();
                break;
            case 'u':
                if (bitWidth == 8)
                {
                    throw new GrammarException(
                        GrammarDiagnosticCode.InvalidEscapeSequence,
                        "Unicode escape \\u is not valid in byte literals. Use \\x for hex byte values.",
                        _fileName, _line, _column, _language);
                }
                Advance(); // consume 'u'
                ScanUnicodeEscape();
                break;
            case '\r':
                // Line continuation: \ followed by CRLF
                Advance();
                if (Peek() == '\n')
                {
                    Advance();
                }
                while (Peek() == ' ' || Peek() == '\t')
                {
                    Advance();
                }
                break;
            case '\n':
                // Line continuation: \ followed by LF
                Advance();
                while (Peek() == ' ' || Peek() == '\t')
                {
                    Advance();
                }
                break;
            default:
                throw new GrammarException(
                    GrammarDiagnosticCode.InvalidEscapeSequence,
                    $"Invalid escape sequence '\\{escapeChar}'",
                    _fileName, _line, _column, _language);
        }
    }

    /// <summary>
    /// Scans and validates a hex byte escape sequence (\xFF).
    /// </summary>
    private void ScanHexByteEscape()
    {
        for (int i = 0; i < 2; i += 1)
        {
            if (!IsHexDigit(c: Peek()))
            {
                throw new GrammarException(
                    GrammarDiagnosticCode.InvalidEscapeSequence,
                    "Invalid hex byte escape: expected 2 hex digits (\\xFF)",
                    _fileName, _line, _column, _language);
            }

            Advance();
        }
    }

    /// <summary>
    /// Scans and validates a Unicode escape sequence (\uXXXXXX).
    /// </summary>
    private void ScanUnicodeEscape()
    {
        for (int i = 0; i < 6; i += 1)
        {
            if (!IsHexDigit(c: Peek()))
            {
                throw new GrammarException(
                    GrammarDiagnosticCode.InvalidEscapeSequence,
                    "Invalid Unicode escape: expected 6 hex digits (\\uXXXXXX)",
                    _fileName, _line, _column, _language);
            }

            Advance();
        }
    }

    /// <summary>
    /// Parses an escape sequence and returns the actual character value.
    /// </summary>
    private char ParseEscapeSequence(int escapeStart, int bitWidth = 32)
    {
        char c = _source[index: escapeStart + 1];

        if (c == 'x')
        {
            string hexStr = _source.Substring(startIndex: escapeStart + 2, length: 2);
            int byteValue = Convert.ToInt32(value: hexStr, fromBase: 16);
            return (char)byteValue;
        }

        if (c == 'u')
        {
            string hexStr = _source.Substring(startIndex: escapeStart + 2, length: 6);
            int codePoint = Convert.ToInt32(value: hexStr, fromBase: 16);

            if (codePoint > 0x10FFFF)
            {
                throw new GrammarException(
                    GrammarDiagnosticCode.InvalidEscapeSequence,
                    $"Unicode escape value U+{codePoint:X} exceeds valid Unicode range",
                    _fileName, _line, _column, _language);
            }

            return (char)codePoint;
        }

        return EscapeCharacter(c: c);
    }

    /// <summary>
    /// Converts a simple escape character to its actual value.
    /// </summary>
    private static char EscapeCharacter(char c)
    {
        return c switch
        {
            'n' => '\n',
            't' => '\t',
            'r' => '\r',
            '\\' => '\\',
            '"' => '"',
            '\'' => '\'',
            '0' => '\0',
            _ => c
        };
    }

    #endregion
}
