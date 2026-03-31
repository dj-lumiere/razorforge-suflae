using SemanticAnalysis.Native;

namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Types;
using SyntaxTree;

/// <summary>
/// Expression code generation for literals and scalar literal helpers.
/// </summary>
public partial class LLVMCodeGenerator
{
    private string EmitLiteral(StringBuilder sb, LiteralExpression literal)
    {
        // Numeric literals are stored as strings by the parser (e.g., "1_s32", "3.14_f32").
        // Check LiteralType first to handle them as numbers, not string constants.
        if (literal.Value is char ch)
        {
            return EmitLetterLiteral(sb: sb, text: ch.ToString());
        }

        if (literal.Value is string s)
        {
            if (IsIntegerLiteralType(type: literal.LiteralType))
            {
                return StripNumericSuffix(text: s);
            }

            if (IsFloatLiteralType(type: literal.LiteralType))
            {
                return EmitFloatLiteral(numericValue: StripNumericSuffix(text: s),
                    literalType: literal.LiteralType);
            }

            if (IsDecimalFloatLiteralType(type: literal.LiteralType))
            {
                return EmitDecimalFloatLiteral(sb: sb,
                    numericValue: StripNumericSuffix(text: s),
                    literalType: literal.LiteralType);
            }

            if (IsByteSizeLiteralType(type: literal.LiteralType))
            {
                return EmitByteSizeLiteral(sb: sb, text: s);
            }

            if (literal.LiteralType == Lexer.TokenType.LetterLiteral)
            {
                return EmitLetterLiteral(sb: sb, text: s);
            }

            if (literal.LiteralType == Lexer.TokenType.ByteLetterLiteral)
            {
                return EmitByteLetterLiteral(sb: sb, text: s);
            }

            if (literal.LiteralType == Lexer.TokenType.BytesLiteral)
            {
                return EmitBytesLiteral(sb: sb, value: s);
            }

            // Actual string literal
            return EmitStringLiteral(sb: sb, value: s);
        }

        // None literal �� emit zeroinitializer for Maybe types ({ i64, ptr } with tag=0)
        if (literal.LiteralType == Lexer.TokenType.None)
        {
            return "zeroinitializer";
        }

        return literal.Value switch
        {
            int i => i.ToString(),
            long l => l.ToString(),
            double d => $"0x{BitConverter.DoubleToInt64Bits(value: d):X16}",
            float f => $"0x{BitConverter.DoubleToInt64Bits(value: (double)f):X16}",
            bool b => b
                ? "true"
                : "false",
            null => "null",
            _ => literal.Value.ToString() ?? "0"
        };
    }

    /// <summary>
    /// Checks if a token type represents an integer literal.
    /// </summary>
    private static bool IsIntegerLiteralType(Lexer.TokenType type)
    {
        return type is Lexer.TokenType.S8Literal or Lexer.TokenType.S16Literal
            or Lexer.TokenType.S32Literal or Lexer.TokenType.S64Literal
            or Lexer.TokenType.S128Literal or Lexer.TokenType.U8Literal
            or Lexer.TokenType.U16Literal or Lexer.TokenType.U32Literal
            or Lexer.TokenType.U64Literal or Lexer.TokenType.U128Literal
            or Lexer.TokenType.AddressLiteral;
    }

    /// <summary>
    /// Checks if a token type represents a floating-point literal.
    /// </summary>
    private static bool IsFloatLiteralType(Lexer.TokenType type)
    {
        return type is Lexer.TokenType.F16Literal or Lexer.TokenType.F32Literal
            or Lexer.TokenType.F64Literal or Lexer.TokenType.F128Literal;
    }

    private static bool IsDecimalFloatLiteralType(Lexer.TokenType type)
    {
        return type is Lexer.TokenType.D32Literal or Lexer.TokenType.D64Literal
            or Lexer.TokenType.D128Literal;
    }

    private static bool IsByteSizeLiteralType(Lexer.TokenType type)
    {
        return type is Lexer.TokenType.ByteLiteral or Lexer.TokenType.KilobyteLiteral
            or Lexer.TokenType.KibibyteLiteral or Lexer.TokenType.MegabyteLiteral
            or Lexer.TokenType.MebibyteLiteral or Lexer.TokenType.GigabyteLiteral
            or Lexer.TokenType.GibibyteLiteral;
    }

    private static readonly (string suffix, ulong multiplier)[] ByteSizeSuffixes =
    [
        ("gib", 1_073_741_824UL),
        ("mib", 1_048_576UL),
        ("kib", 1_024UL),
        ("gb", 1_000_000_000UL),
        ("mb", 1_000_000UL),
        ("kb", 1_000UL),
        ("b", 1UL)
    ];

    private string EmitByteSizeLiteral(StringBuilder sb, string text)
    {
        // Compute the byte value from the literal text + unit suffix.
        ulong bytes = 0;
        string lower = text.ToLowerInvariant();
        foreach ((string suffix, ulong multiplier) in ByteSizeSuffixes)
        {
            if (lower.EndsWith(value: suffix))
            {
                string numPart = text[..^suffix.Length]
                                .TrimEnd(trimChar: '_')
                                .Replace(oldValue: "_", newValue: "");
                if (ulong.TryParse(s: numPart, result: out ulong value))
                {
                    bytes = value * multiplier;
                }

                break;
            }
        }

        // ByteSize is %Record.ByteSize = type { i64 } ? construct the aggregate.
        TypeInfo? bsType = _registry.LookupType(name: "ByteSize");
        string llvmType = bsType != null
            ? GetLLVMType(type: bsType)
            : "%Record.ByteSize";

        // If ByteSize resolves to a struct, use insertvalue; if it's a plain i64, return directly
        if (llvmType.StartsWith(value: "%"))
        {
            string result = NextTemp();
            EmitLine(sb: sb, line: $"  {result} = insertvalue {llvmType} undef, i64 {bytes}, 0");
            return result;
        }

        return bytes.ToString();
    }

    /// <summary>
    /// Emits a Letter literal as a %Record.Letter aggregate with the Unicode codepoint.
    /// </summary>
    private string EmitLetterLiteral(StringBuilder sb, string text)
    {
        int codepoint = text.Length > 0
            ? char.ConvertToUtf32(s: text, index: 0)
            : 0;

        TypeInfo? letterType = _registry.LookupType(name: "Letter");
        string llvmType = letterType != null
            ? GetLLVMType(type: letterType)
            : "%\"Record.Letter\"";

        if (llvmType.StartsWith(value: "%"))
        {
            string result = NextTemp();
            EmitLine(sb: sb,
                line: $"  {result} = insertvalue {llvmType} undef, i32 {codepoint}, 0");
            return result;
        }

        return codepoint.ToString();
    }

    private string EmitByteLetterLiteral(StringBuilder sb, string text)
    {
        int byteValue = text.Length > 0
            ? (int)text[index: 0] & 0xFF
            : 0;

        TypeInfo? byteType = _registry.LookupType(name: "Byte");
        string llvmType = byteType != null
            ? GetLLVMType(type: byteType)
            : "%\"Record.Byte\"";

        if (llvmType.StartsWith(value: "%"))
        {
            string result = NextTemp();
            EmitLine(sb: sb,
                line: $"  {result} = insertvalue {llvmType} undef, i8 {byteValue}, 0");
            return result;
        }

        return byteValue.ToString();
    }

    /// <summary>
    /// Emits a Bytes literal (b"...") as a constant Bytes entity.
    /// Bytes is entity { letters: List[Byte] } where List is entity { data: ptr, count: U64, capacity: U64 }
    /// and Byte is an i8. Returns a pointer to the Bytes struct.
    /// </summary>
    private string EmitBytesLiteral(StringBuilder sb, string value)
    {
        int idx = _stringCounter++;
        string constName = $"@.bytes.{idx}";

        // Collect ASCII byte values
        var bytes = new List<int>();
        foreach (char c in value)
        {
            bytes.Add(item: (int)c & 0xFF);
        }

        int count = bytes.Count;

        // Layer 1: raw byte data array [N x i8]
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

        // Layer 2: List[Byte] struct { ptr data, i64 count, i64 capacity }
        string listName = $"@.bytes.list.{idx}";
        EmitLine(sb: _globalDeclarations,
            line:
            $"{listName} = private unnamed_addr constant {{ ptr, i64, i64 }} {{ ptr {dataName}, i64 {count}, i64 {count} }}");

        // Layer 3: Bytes entity struct { ptr letters }
        EmitLine(sb: _globalDeclarations,
            line: $"{constName} = private unnamed_addr constant {{ ptr }} {{ ptr {listName} }}");

        return constName;
    }

    /// <summary>
    /// Strips the type suffix from a numeric literal string (e.g., "1_s32" �� "1", "3_14_f64" �� "3_14")
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
                        style: System.Globalization.NumberStyles.HexNumber,
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

    private static readonly string[] NumericSuffixes =
    [
        "addr", "s128", "u128", "s64", "u64", "s32", "u32",
        "s16", "u16", "s8", "u8", "f128", "f64", "f32", "f16",
        "d128", "d64", "d32"
    ];

    private static string StripNumericSuffix(string text)
    {
        // First try: underscore-separated suffix (e.g., "1_s32" �� "1")
        for (int i = text.Length - 1; i >= 0; i--)
        {
            if (text[index: i] == '_' && i + 1 < text.Length &&
                char.IsLetter(c: text[index: i + 1]))
            {
                return ConvertPrefixedToDecimal(value: text[..i]
                   .Replace(oldValue: "_", newValue: ""));
            }
        }

        // Second try: direct suffix without underscore (e.g., "0u64" �� "0", "0x7Fu32" �� "127")
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
    private static string EmitFloatLiteral(string numericValue, Lexer.TokenType literalType)
    {
        // F128: use native parser for full 128-bit precision
        if (literalType == Lexer.TokenType.F128Literal)
        {
            NumericLiteralParser.F128 f128 =
                SemanticAnalysis.Native.NumericLiteralParser.ParseF128(str: numericValue);
            // LLVM fp128 hex format: 0xL<Lo16hex><Hi16hex> (low bits first)
            return $"0xL{f128.Lo:X16}{f128.Hi:X16}";
        }

        // Try hex float format first (0x1.ABCDp5)
        if (TryParseHexFloat(value: numericValue, result: out double hexFloatVal))
        {
            return EmitDoubleAsLlvmHex(d: hexFloatVal, literalType: literalType);
        }

        if (double.TryParse(s: numericValue,
                style: System.Globalization.NumberStyles.Float,
                provider: System.Globalization.CultureInfo.InvariantCulture,
                result: out double d))
        {
            return EmitDoubleAsLlvmHex(d: d, literalType: literalType);
        }

        return numericValue;
    }

    private static string EmitDoubleAsLlvmHex(double d, Lexer.TokenType literalType)
    {
        if (literalType == Lexer.TokenType.F32Literal)
        {
            // F32: promote to double for LLVM's float hex format
            float f = (float)d;
            long bits = BitConverter.DoubleToInt64Bits(value: (double)f);
            return $"0x{bits:X16}";
        }
        else
        {
            long bits = BitConverter.DoubleToInt64Bits(value: d);
            return $"0x{bits:X16}";
        }
    }

    /// <summary>
    /// Parses C99 hex float format: 0x1.ABCDp5 = (hex mantissa) �� 2^(exponent).
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
                    style: System.Globalization.NumberStyles.HexNumber,
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
                    style: System.Globalization.NumberStyles.HexNumber,
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
        Lexer.TokenType literalType)
    {
        switch (literalType)
        {
            case Lexer.TokenType.D32Literal:
                return SemanticAnalysis.Native
                                       .NumericLiteralParser
                                       .ParseD32(str: numericValue)
                                       .Value
                                       .ToString();
            case Lexer.TokenType.D64Literal:
                return SemanticAnalysis.Native
                                       .NumericLiteralParser
                                       .ParseD64(str: numericValue)
                                       .Value
                                       .ToString();
            case Lexer.TokenType.D128Literal:
            {
                NumericLiteralParser.D128 d128 =
                    SemanticAnalysis.Native.NumericLiteralParser.ParseD128(str: numericValue);
                string tmp1 = NextTemp();
                string tmp2 = NextTemp();
                EmitLine(sb: sb,
                    line: $"  {tmp1} = insertvalue %Record.D128 undef, i64 {d128.Lo}, 0");
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
    /// Text is entity { letters: List[Letter] } where List is entity { data: ptr, count: U64, capacity: U64 }
    /// and Letter is a U32 codepoint. Returns a pointer to the Text struct.
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

        // Layer 2: List[Letter] struct { ptr data, i64 count, i64 capacity }
        string listName = $"@.str.list.{idx}";
        EmitLine(sb: _globalDeclarations,
            line:
            $"{listName} = private unnamed_addr constant {{ ptr, i64, i64 }} {{ ptr {dataName}, i64 {count}, i64 {count} }}");

        // Layer 3: Text struct { ptr letters }
        EmitLine(sb: _globalDeclarations,
            line: $"{constName} = private unnamed_addr constant {{ ptr }} {{ ptr {listName} }}");

        return constName;
    }
}
