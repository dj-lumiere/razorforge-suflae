using SemanticVerification.Enums;
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

        // Skip underscore before suffix after scientific notation (e.g., 3.4e10_f64)
        if (Peek() == '_' && char.IsLetter(c: Peek(offset: 1)))
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

            // Handle arbitrary precision suffix (n) - maps to Integer or Decimal based on decimal point
            if (suffix == ArbitraryPrecisionSuffix)
            {
                AddToken(type: isFloat
                    ? TokenType.Decimal
                    : TokenType.Integer);
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
                    code: ClassifySuffixError(suffix: suffix, isFloat: isFloat),
                    message: $"Unknown suffix '{suffix}'",
                    fileName: _fileName,
                    line: _line,
                    column: _column,
                    language: _language);
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
    /// Scans a prefixed numeric literal (hexadecimal, binary, or hex float).
    /// Hex floats use C99 format: 0x1.ABCDp5 (hex mantissa + binary exponent).
    /// </summary>
    private void ScanPrefixedNumber(bool isHex)
    {
        bool isHexFloat = false;

        // Consume valid digits and underscores
        if (isHex)
        {
            while (IsHexDigit(c: Peek()) || Peek() == '_')
            {
                // When encountering underscore in hex mode, check if what follows
                // is a type suffix (e.g., _addr) rather than a digit separator (e.g., _ABCD)
                if (Peek() == '_')
                {
                    int lookAhead = 1;
                    while (char.IsLetterOrDigit(c: Peek(offset: lookAhead)))
                    {
                        lookAhead++;
                    }

                    if (lookAhead > 1)
                    {
                        string candidate = _source.Substring(startIndex: _position + 1,
                            length: lookAhead - 1);
                        if (_numericSuffixToTokenType.ContainsKey(key: candidate) ||
                            candidate == ArbitraryPrecisionSuffix)
                        {
                            Advance(); // consume the underscore
                            break; // suffix follows
                        }
                    }
                }

                Advance();
            }

            // Check for hex float fractional part: 0x1.ABCDp5
            if (Peek() == '.' && IsHexDigit(c: Peek(offset: 1)))
            {
                isHexFloat = true;
                Advance(); // consume '.'
                while (IsHexDigit(c: Peek()) || Peek() == '_')
                {
                    Advance();
                }
            }

            // Check for hex float binary exponent (p/P)
            if (Peek() == 'p' || Peek() == 'P')
            {
                isHexFloat = true;
                Advance(); // consume 'p'/'P'
                if (Peek() == '+' || Peek() == '-')
                {
                    Advance();
                }

                while (char.IsDigit(c: Peek()))
                {
                    Advance();
                }
            }
        }
        else
        {
            while (Peek() == '0' || Peek() == '1' || Peek() == '_')
            {
                Advance();
            }
        }

        // Skip underscore before suffix (e.g., 0x1.0p5_f64)
        if (Peek() == '_' && char.IsLetter(c: Peek(offset: 1)))
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

            // Handle arbitrary precision suffix (n)
            if (suffix == ArbitraryPrecisionSuffix)
            {
                AddToken(type: isHexFloat
                    ? TokenType.Decimal
                    : TokenType.Integer);
            }
            else if (_numericSuffixToTokenType.TryGetValue(key: suffix,
                         value: out TokenType tokenType))
            {
                AddToken(type: tokenType);
            }
            else
            {
                string baseType = isHex
                    ? "hex"
                    : "binary";
                throw new GrammarException(code: GrammarDiagnosticCode.InvalidNumericLiteral,
                    message: $"Unknown {baseType} suffix '{suffix}'",
                    fileName: _fileName,
                    line: _line,
                    column: _column,
                    language: _language);
            }
        }
        else
        {
            // Language-conditional defaults for prefixed:
            // RF: S64Literal (integer) / F64Literal (hex float), SF: Integer / Decimal
            if (_language == Language.RazorForge)
            {
                AddToken(type: isHexFloat
                    ? TokenType.F64Literal
                    : TokenType.S64Literal);
            }
            else
            {
                AddToken(type: isHexFloat
                    ? TokenType.Decimal
                    : TokenType.Integer);
            }
        }
    }

    /// <summary>
    /// Scans an octal numeric literal (0o prefix).
    /// </summary>
    private void ScanOctalNumber()
    {
        // Consume valid octal digits and underscores
        while (Peek() >= '0' && Peek() <= '7' || Peek() == '_')
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
            else if (_numericSuffixToTokenType.TryGetValue(key: suffix,
                         value: out TokenType tokenType))
            {
                AddToken(type: tokenType);
            }
            else
            {
                throw new GrammarException(code: GrammarDiagnosticCode.InvalidNumericLiteral,
                    message: $"Unknown octal suffix '{suffix}'",
                    fileName: _fileName,
                    line: _line,
                    column: _column,
                    language: _language);
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

    #region Suffix Error Classification

    /// <summary>
    /// Classifies an unknown numeric suffix into the most specific diagnostic code.
    /// </summary>
    private static GrammarDiagnosticCode ClassifySuffixError(string suffix, bool isFloat)
    {
        char first = char.ToLowerInvariant(c: suffix[index: 0]);

        // Memory-unit-like suffixes (b, k, m, g)
        if (first is 'b' or 'k' or 'm' or 'g')
        {
            return GrammarDiagnosticCode.InvalidMemoryLiteral;
        }

        // Duration-like suffixes (w, d, h, s, or contains ms/us/ns)
        if (first is 'w' or 'd' or 'h' or 's')
        {
            return GrammarDiagnosticCode.InvalidDurationLiteral;
        }

        string lower = suffix.ToLowerInvariant();
        if (lower.Contains(value: "ms") || lower.Contains(value: "us") ||
            lower.Contains(value: "ns"))
        {
            return GrammarDiagnosticCode.InvalidDurationLiteral;
        }

        // Float with decimal point
        if (isFloat)
        {
            return GrammarDiagnosticCode.InvalidFloatLiteral;
        }

        return GrammarDiagnosticCode.InvalidNumericLiteral;
    }

    #endregion
}
