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
    /// </summary>
    private void ScanStringLiteral(bool isRaw, bool isFormatted, TokenType tokenType,
        int bitWidth = 32)
    {
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

    #endregion

    #region Character Literals

    /// <summary>
    /// Scans a basic character literal (single-quoted).
    /// </summary>
    private void ScanLetter()
    {
        if (IsAtEnd())
        {
            throw new GrammarException(
                GrammarDiagnosticCode.UnterminatedString,
                "Unterminated character literal",
                _fileName, _line, _column, _language);
        }

        char value;
        if (Peek() == '\\')
        {
            Advance(); // consume backslash
            value = EscapeCharacter(c: Advance());
        }
        else
        {
            value = Advance();
        }

        if (Peek() != '\'')
        {
            throw new GrammarException(
                GrammarDiagnosticCode.UnterminatedString,
                "Unterminated character literal",
                _fileName, _line, _column, _language);
        }

        Advance(); // consume closing quote
        AddToken(type: TokenType.LetterLiteral, text: value.ToString());
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
