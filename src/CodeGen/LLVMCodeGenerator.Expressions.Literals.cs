using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Compiler.Lexer;
using Verification;
using SyntaxTree;
using TypeModel.Types;

namespace Compiler.CodeGen;

// TODO: This should be on the AST level with constructor.
/// <summary>
/// Expression code generation for literals and scalar literal helpers.
/// </summary>
public partial class LlvmCodeGenerator
{
    /// <summary>
    /// Emit literal as part of this compiler phase.
    /// </summary>
    private string EmitLiteral(StringBuilder sb, LiteralExpression literal)
    {
        switch (literal.Value)
        {
            // Numeric literals are stored as strings by the parser (e.g., "1_s32", "3.14_f32").
            // Check LiteralType first to handle them as numbers, not string constants.
            case string s when IsIntegerLiteralType(type: literal.LiteralType):
                return StripNumericSuffix(text: s);
            case string s when IsFloatLiteralType(type: literal.LiteralType):
                return EmitFloatLiteral(numericValue: StripNumericSuffix(text: s),
                    literalType: literal.LiteralType);
            case string s when IsDecimalFloatLiteralType(type: literal.LiteralType):
                return EmitDecimalFloatLiteral(sb: sb,
                    numericValue: StripNumericSuffix(text: s),
                    literalType: literal.LiteralType);
            case string s when literal.LiteralType == TokenType.BytesLiteral:
                return EmitBytesLiteral(sb: sb, value: s);
            // Actual string literal
            case string s:
                return EmitStringLiteral(sb: sb, value: s);
        }

        // None literal -> emit zeroinitializer for Maybe types ({ i64, ptr } with tag=0)
        if (literal.LiteralType == TokenType.None)
        {
            return "zeroinitializer";
        }

        return literal.Value switch
        {
            int i => i.ToString(),
            long l => l.ToString(),
            ulong ul => ul.ToString(),
            double d => $"0x{BitConverter.DoubleToInt64Bits(value: d):X16}",
            float f => $"0x{BitConverter.DoubleToInt64Bits(value: f):X16}",
            bool b => b ? "true" : "false",
            null => "null",
            _ => literal.Value.ToString() ?? "0"
        };
    }

    /// <summary>
    /// Checks if a token type represents an integer literal.
    /// </summary>
    private static bool IsIntegerLiteralType(TokenType type)
    {
        return type is TokenType.IntegerLiteral or TokenType.S8Literal
            or TokenType.S16Literal or TokenType.S32Literal
            or TokenType.S64Literal or TokenType.S128Literal
            or TokenType.U8Literal or TokenType.U16Literal
            or TokenType.U32Literal or TokenType.U64Literal
            or TokenType.U128Literal or TokenType.AddressLiteral;
    }

    /// <summary>
    /// Checks if a token type represents a floating-point literal.
    /// </summary>
    private static bool IsFloatLiteralType(TokenType type)
    {
        return type is TokenType.DecimalLiteral or TokenType.F16Literal
            or TokenType.F32Literal or TokenType.F64Literal
            or TokenType.F128Literal;
    }

    /// <summary>
    /// Returns whether is decimal float literal type applies in the current compiler context.
    /// </summary>
    private static bool IsDecimalFloatLiteralType(TokenType type)
    {
        return type is TokenType.D32Literal or TokenType.D64Literal
            or TokenType.D128Literal;
    }

    /// <summary>
    /// Emits a Bytes literal (b"...") as a constant Bytes entity.
    /// Bytes is `entity Bytes { data: Hijacked[Byte], count: U64 }` — LLVM layout `{ ptr, i64 }`.
    /// Returns a pointer to the Bytes struct.
    /// </summary>
    private string EmitBytesLiteral(StringBuilder sb, string value)
    {
        int idx = _stringCounter++;
        string constName = $"@.bytes.{idx}";

        // Collect ASCII byte values
        var bytes = new List<int>();
        foreach (char c in value)
        {
            bytes.Add(item: c & 0xFF);
        }

        int count = bytes.Count;

        // Raw byte data array [N x i8]
        string dataName = $"@.bytes.data.{idx}";
        string byteValues =
            string.Join(separator: ", ", values: bytes.Select(selector: b => $"i8 {b}"));
        if (count > 0)
        {
            EmitLine(sb: _globalDeclarations,
                line: $"{dataName} = private unnamed_addr constant [{count} x i8] [{byteValues}]");
        }
        else
        {
            EmitLine(sb: _globalDeclarations,
                line: $"{dataName} = private unnamed_addr constant [0 x i8] zeroinitializer");
        }

        // Bytes entity literal — must mirror the runtime layout `{ ptr, i64 }`.
        // An older revision built a `List[Byte]`-shaped {ptr,i64,i64} indirection wrapped in a
        // single-field {ptr} entity. That broke any code that read `count` off the entity
        // directly: GEP at field 1 landed past the single-pointer literal and returned garbage.
        EmitLine(sb: _globalDeclarations,
            line: $"{constName} = private unnamed_addr constant {{ ptr, i64 }} {{ ptr {dataName}, i64 {count} }}");

        return constName;
    }

    /// <summary>
    /// Strips the type suffix from a numeric literal string (e.g., "1_s32" "1", "3_14_f64" "3_14")
    /// and removes digit separator underscores.
    /// </summary>
    /// <summary>
    /// Converts a prefixed literal (0x hex, 0b binary, 0o octal) to decimal for LLVM IR.
    /// Hex floats (containing '.' or 'p') are passed through for EmitFloatLiteral.
    /// </summary>
    private static string ConvertPrefixedToDecimal(string value)
    {
        if (value.Length > 2)
        {
            if (value.StartsWith(value: "0x", comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                // Don't convert hex floats ? they go through EmitFloatLiteral
                if (value.IndexOfAny(anyOf: ['.', 'p', 'P'], startIndex: 2) >= 0)
                {
                    return value;
                }

                if (ulong.TryParse(s: value[2..],
                        style: NumberStyles.HexNumber,
                        provider: null,
                        result: out ulong hexVal))
                {
                    return hexVal.ToString();
                }
            }
            else if (value.StartsWith(value: "0b",
                         comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return Convert.ToUInt64(value: value[2..], fromBase: 2)
                                  .ToString();
                }
                catch
                {
                    /* fall through */
                }
            }
            else if (value.StartsWith(value: "0o",
                         comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return Convert.ToUInt64(value: value[2..], fromBase: 8)
                                  .ToString();
                }
                catch
                {
                    /* fall through */
                }
            }
        }

        return value;
    }

    /// <summary>
    /// Stores the numeric suffixes state used by this compiler phase.
    /// </summary>
    private static readonly string[] NumericSuffixes =
    [
        "addr", "s128", "u128", "s64", "u64", "s32", "u32",
        "s16", "u16", "s8", "u8", "f128", "f64", "f32", "f16",
        "d128", "d64", "d32"
    ];

    /// <summary>
    /// Performs the strip numeric suffix step for this compiler phase.
    /// </summary>
    private static string StripNumericSuffix(string text)
    {
        // First try: underscore-separated suffix (e.g., "1_s32" "1")
        for (int i = text.Length - 1; i >= 0; i--)
        {
            if (text[index: i] == '_' && i + 1 < text.Length &&
                char.IsLetter(c: text[index: i + 1]))
            {
                return ConvertPrefixedToDecimal(value: text[..i]
                   .Replace(oldValue: "_", newValue: ""));
            }
        }

        // Second try: direct suffix without underscore (e.g., "0u64" "0", "0x7Fu32" "127")
        string lower = text.ToLowerInvariant();
        foreach (string suffix in NumericSuffixes)
        {
            if (lower.EndsWith(value: suffix))
            {
                string numPart = text[..^suffix.Length]
                   .Replace(oldValue: "_", newValue: "");
                return ConvertPrefixedToDecimal(value: numPart);
            }
        }

        // No suffix found ? just remove underscores
        return ConvertPrefixedToDecimal(value: text.Replace(oldValue: "_", newValue: ""));
    }

    /// <summary>
    /// Emits a float literal in LLVM IR format.
    /// LLVM requires specific formats for different float types.
    /// </summary>
    private static string EmitFloatLiteral(string numericValue, TokenType literalType)
    {
        // F128: use native parser for full 128-bit precision
        if (literalType == TokenType.F128Literal)
        {
            NumericLiteralParser.F128 f128 =
                NumericLiteralParser.ParseF128(str: numericValue);
            // LLVM fp128 hex format: 0xL<Lo16hex><Hi16hex> (low bits first)
            return $"0xL{f128.Lo:X16}{f128.Hi:X16}";
        }

        // Try hex float format first (0x1.ABCDp5)
        if (TryParseHexFloat(value: numericValue, result: out double hexFloatVal))
        {
            return EmitDoubleAsLlvmHex(d: hexFloatVal, literalType: literalType);
        }

        if (double.TryParse(s: numericValue,
                style: NumberStyles.Float,
                provider: CultureInfo.InvariantCulture,
                result: out double d))
        {
            return EmitDoubleAsLlvmHex(d: d, literalType: literalType);
        }

        return numericValue;
    }

    /// <summary>
    /// Emit double as LLVM hex as part of this compiler phase.
    /// </summary>
    private static string EmitDoubleAsLlvmHex(double d, TokenType literalType)
    {
        if (literalType == TokenType.F32Literal)
        {
            // F32: promote to double for LLVM's float hex format
            float f = (float)d;
            long bits = BitConverter.DoubleToInt64Bits(value: f);
            return $"0x{bits:X16}";
        }
        else
        {
            long bits = BitConverter.DoubleToInt64Bits(value: d);
            return $"0x{bits:X16}";
        }
    }

    /// <summary>
    /// Parses C99 hex float format: 0x1.ABCDp5 = (hex mantissa) 2^(exponent).
    /// </summary>
    private static bool TryParseHexFloat(string value, out double result)
    {
        result = 0;
        if (!value.StartsWith(value: "0x", comparisonType: StringComparison.OrdinalIgnoreCase) ||
            value.Length <= 2)
        {
            return false;
        }

        string body = value[2..];
        int pIndex = body.IndexOfAny(anyOf: ['p', 'P']);
        if (pIndex < 0)
        {
            return false;
        }

        string mantissaStr = body[..pIndex];
        string exponentStr = body[(pIndex + 1)..];

        if (!int.TryParse(s: exponentStr, result: out int exponent))
        {
            return false;
        }

        double mantissa = 0;
        int dotIndex = mantissaStr.IndexOf(value: '.');

        if (dotIndex >= 0)
        {
            string intPart = mantissaStr[..dotIndex];
            string fracPart = mantissaStr[(dotIndex + 1)..];

            if (intPart.Length > 0 && ulong.TryParse(s: intPart,
                    style: NumberStyles.HexNumber,
                    provider: null,
                    result: out ulong intVal))
            {
                mantissa = intVal;
            }

            double scale = 1.0 / 16;
            foreach (char c in fracPart)
            {
                int digit = c switch
                {
                    >= '0' and <= '9' => c - '0',
                    >= 'a' and <= 'f' => c - 'a' + 10,
                    >= 'A' and <= 'F' => c - 'A' + 10,
                    _ => 0
                };
                mantissa += digit * scale;
                scale /= 16;
            }
        }
        else
        {
            if (!ulong.TryParse(s: mantissaStr,
                    style: NumberStyles.HexNumber,
                    provider: null,
                    result: out ulong intVal))
            {
                return false;
            }

            mantissa = intVal;
        }

        result = Math.ScaleB(x: mantissa, n: exponent);
        return !double.IsNaN(d: result) && !double.IsInfinity(d: result);
    }

    /// <summary>
    /// Emits a decimal floating-point literal (D32, D64, D128) as raw integer bits.
    /// D32/D64 return scalar values. D128 emits insertvalue instructions and returns a temp.
    /// </summary>
    private string EmitDecimalFloatLiteral(StringBuilder sb, string numericValue,
        TokenType literalType)
    {
        switch (literalType)
        {
            case TokenType.D32Literal:
                return NumericLiteralParser
                                       .ParseD32(str: numericValue)
                                       .Value
                                       .ToString();
            case TokenType.D64Literal:
                return NumericLiteralParser
                                       .ParseD64(str: numericValue)
                                       .Value
                                       .ToString();
            case TokenType.D128Literal:
            {
                NumericLiteralParser.D128 d128 =
                    NumericLiteralParser.ParseD128(str: numericValue);
                string tmp1 = NextTemp();
                string tmp2 = NextTemp();
                EmitLine(sb: sb,
                    line: $"  {tmp1} = insertvalue %Record.D128 zeroinitializer, i64 {d128.Lo}, 0");
                EmitLine(sb: sb,
                    line: $"  {tmp2} = insertvalue %Record.D128 {tmp1}, i64 {d128.Hi}, 1");
                return tmp2;
            }
            default:
                return numericValue;
        }
    }

    /// <summary>
    /// Generates code for a string literal.
    /// Emits a Text string literal as a UTF-32 constant.
    /// Text is entity { characters: List[Character] } where List is entity { data: ptr, count: U64, capacity: U64 }
    /// and Character is a U32 codepoint. Returns a pointer to the Text struct.
    /// </summary>
    private string EmitStringLiteral(StringBuilder sb, string value)
    {
        // Check if we've already emitted this string
        if (_stringConstants.TryGetValue(key: value, value: out string? existingName))
        {
            return existingName;
        }

        int idx = _stringCounter++;
        string constName = $"@.str.{idx}";
        _stringConstants[key: value] = constName;

        // Collect Unicode codepoints (UTF-32)
        var codepoints = new List<int>();
        foreach (Rune rune in value.EnumerateRunes())
        {
            codepoints.Add(item: rune.Value);
        }

        int count = codepoints.Count;

        // Layer 1: raw codepoint data array [N x i32]
        string dataName = $"@.str.data.{idx}";
        string cpValues = string.Join(separator: ", ",
            values: codepoints.Select(selector: cp => $"i32 {cp}"));
        if (count > 0)
        {
            EmitLine(sb: _globalDeclarations,
                line: $"{dataName} = private unnamed_addr constant [{count} x i32] [{cpValues}]");
        }
        else
        {
            EmitLine(sb: _globalDeclarations,
                line: $"{dataName} = private unnamed_addr constant [0 x i32] zeroinitializer");
        }

        // Layer 2: Text entity payload { ptr data, i64 count }.
        // Text used to be emitted through an intermediate list-like wrapper, but the
        // current runtime/entity layout is the raw pair (data, count).
        EmitLine(sb: _globalDeclarations,
            line: $"{constName} = private unnamed_addr constant {{ ptr, i64 }} {{ ptr {dataName}, i64 {count} }}");

        return constName;
    }
}
