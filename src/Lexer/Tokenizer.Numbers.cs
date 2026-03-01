using SemanticAnalysis.Enums;
using Compiler.Diagnostics;

namespace Compiler.Lexer;

/// <summary>
/// Partial class containing numeric literal scanning methods for the unified tokenizer.
/// </summary>
/// <remarks>
/// Key language-conditional: unsuffixed defaults differ between RF and SF.
/// RF: integer -> S64Literal, float -> F64Literal
/// SF: integer -> Integer, float -> Decimal
/// </remarks>
public partial class Tokenizer
{
    #region Decimal Numbers

    /// <summary>
    /// Scans a decimal numeric literal, handling integers, floats, and suffixed numbers.
    /// </summary>
    private void ScanNumber()
    {
        // Consume digits and underscores
        while (char.IsDigit(c: Peek()) || Peek() == '_')
        {
            Advance();
        }

        bool isFloat = false;

        // Check for decimal point followed by digit
        if (Peek() == '.' && char.IsDigit(c: Peek(offset: 1)))
        {
            isFloat = true;
            Advance(); // consume '.'

            // Consume fractional digits
            while (char.IsDigit(c: Peek()) || Peek() == '_')
            {
                Advance();
            }
        }

        // Check for scientific notation
        if (Peek() == 'e' || Peek() == 'E')
        {
            isFloat = true;
            Advance(); // consume 'e' or 'E'

            // Optional sign
            if (Peek() == '+' || Peek() == '-')
            {
                Advance();
            }

            // Exponent digits
            while (char.IsDigit(c: Peek()))
            {
                Advance();
            }
        }

        // Check for type suffix
        if (char.IsLetter(c: Peek()))
        {
            int suffixStart = _position;
            while (char.IsLetterOrDigit(c: Peek()))
            {
                Advance();
            }

            string suffix =
                _source.Substring(startIndex: suffixStart, length: _position - suffixStart);

            // Handle arbitrary precision suffix (n) - maps to Integer or Decimal based on decimal point
            if (suffix == ArbitraryPrecisionSuffix)
            {
                AddToken(type: isFloat ? TokenType.Decimal : TokenType.Integer);
            }
            else if (_numericSuffixToTokenType.TryGetValue(key: suffix,
                    value: out TokenType numericType))
            {
                AddToken(type: numericType);
            }
            else if (_byteSizeSuffixToTokenType.TryGetValue(key: suffix,
                         value: out TokenType memoryType))
            {
                AddToken(type: memoryType);
            }
            else if (_durationSuffixToTokenType.TryGetValue(key: suffix,
                         value: out TokenType durationToken))
            {
                AddToken(type: durationToken);
            }
            else
            {
                throw new GrammarException(
                    GrammarDiagnosticCode.InvalidNumericLiteral,
                    $"Unknown suffix '{suffix}'",
                    _fileName, _line, _column, _language);
            }
        }
        else
        {
            // Language-conditional defaults:
            // RF: S64Literal for integers, F64Literal for floats
            // SF: Integer for integers, Decimal for floats
            if (_language == Language.RazorForge)
            {
                AddToken(type: isFloat
                    ? TokenType.F64Literal
                    : TokenType.S64Literal);
            }
            else
            {
                AddToken(type: isFloat
                    ? TokenType.Decimal
                    : TokenType.Integer);
            }
        }
    }

    #endregion

    #region Prefixed Numbers

    /// <summary>
    /// Scans a prefixed numeric literal (hexadecimal or binary).
    /// </summary>
    private void ScanPrefixedNumber(bool isHex)
    {
        // Consume valid digits and underscores
        if (isHex)
        {
            while (IsHexDigit(c: Peek()) || Peek() == '_')
            {
                Advance();
            }
        }
        else
        {
            while (Peek() == '0' || Peek() == '1' || Peek() == '_')
            {
                Advance();
            }
        }

        // Check for type suffix
        if (char.IsLetter(c: Peek()))
        {
            int suffixStart = _position;
            while (char.IsLetterOrDigit(c: Peek()))
            {
                Advance();
            }

            string suffix =
                _source.Substring(startIndex: suffixStart, length: _position - suffixStart);

            // Handle arbitrary precision suffix (n) - always Integer for hex/binary
            if (suffix == ArbitraryPrecisionSuffix)
            {
                AddToken(type: TokenType.Integer);
            }
            else if (_numericSuffixToTokenType.TryGetValue(key: suffix, value: out TokenType tokenType))
            {
                AddToken(type: tokenType);
            }
            else
            {
                string baseType = isHex
                    ? "hex"
                    : "binary";
                throw new GrammarException(
                    GrammarDiagnosticCode.InvalidNumericLiteral,
                    $"Unknown {baseType} suffix '{suffix}'",
                    _fileName, _line, _column, _language);
            }
        }
        else
        {
            // Language-conditional defaults for prefixed:
            // RF: S64Literal, SF: Integer
            if (_language == Language.RazorForge)
            {
                AddToken(type: TokenType.S64Literal);
            }
            else
            {
                AddToken(type: TokenType.Integer);
            }
        }
    }

    /// <summary>
    /// Scans an octal numeric literal (0o prefix).
    /// </summary>
    private void ScanOctalNumber()
    {
        // Consume valid octal digits and underscores
        while ((Peek() >= '0' && Peek() <= '7') || Peek() == '_')
        {
            Advance();
        }

        // Check for type suffix
        if (char.IsLetter(c: Peek()))
        {
            int suffixStart = _position;
            while (char.IsLetterOrDigit(c: Peek()))
            {
                Advance();
            }

            string suffix =
                _source.Substring(startIndex: suffixStart, length: _position - suffixStart);

            // Handle arbitrary precision suffix (n) - always Integer for octal
            if (suffix == ArbitraryPrecisionSuffix)
            {
                AddToken(type: TokenType.Integer);
            }
            else if (_numericSuffixToTokenType.TryGetValue(key: suffix, value: out TokenType tokenType))
            {
                AddToken(type: tokenType);
            }
            else
            {
                throw new GrammarException(
                    GrammarDiagnosticCode.InvalidNumericLiteral,
                    $"Unknown octal suffix '{suffix}'",
                    _fileName, _line, _column, _language);
            }
        }
        else
        {
            // Language-conditional defaults for octal:
            // RF: S64Literal, SF: Integer
            if (_language == Language.RazorForge)
            {
                AddToken(type: TokenType.S64Literal);
            }
            else
            {
                AddToken(type: TokenType.Integer);
            }
        }
    }

    #endregion
}
