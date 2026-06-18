using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Compiler.Tokenizer;
using Verification;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

// TODO: This should be on the AST level with constructor.
/// <summary>
/// Expression code generation for literals and scalar literal helpers.
/// </summary>
public partial class LlvmCodeGenerator
{
    /// <summary>Module-level constant globals emitted for aggregate (Array[T,N]) presets,
    /// keyed by the preset's qualified name so the table is emitted once and shared.</summary>
    private readonly Dictionary<string, string> _presetGlobals = new(StringComparer.Ordinal);

    /// <summary>
    /// Looks up <paramref name="name"/> as an aggregate (Array[T,N]) preset, trying the bare name
    /// and the current routine's module prefix. Returns null for non-presets and scalar presets
    /// (which are inlined before codegen). See <see cref="VariableInfo.IsPresettableAggregate"/>.
    /// </summary>
    private VariableInfo? ResolveAggregatePreset(string name)
    {
        if (_registry.LookupVariable(name: name) is { IsPresettableAggregate: true } direct)
            return direct;

        string? module = _currentEmittingRoutine?.OwnerType?.Module ??
                         _currentEmittingRoutine?.Module;
        if (module != null && !name.Contains(value: '.') &&
            _registry.LookupVariable(name: $"{module}.{name}") is
                { IsPresettableAggregate: true } qualified)
            return qualified;

        return null;
    }

    /// <summary>
    /// Emits (once) a <c>private unnamed_addr constant</c> global for an aggregate preset and
    /// returns its symbol. The global holds the whole <c>[N x T]</c> array, so references become a
    /// pointer to it (indexing = one gep+load) instead of rebuilding the array at each use site.
    /// </summary>
    private string EmitOrGetPresetGlobal(VariableInfo preset)
    {
        string key = preset.QualifiedName;
        if (_presetGlobals.TryGetValue(key: key, value: out string? existing))
            return existing;

        var list = (ListLiteralExpression)preset.PresetValue!;
        string arrLlvm = GetLlvmType(type: preset.Type);          // "[1000 x i16]" / "[8 x i8]"
        string symbol = $"@\"preset.{key}\"";

        // BitArray[N] packs its `N` bool elements into `[(N+7)/8 x i8]`; Array[T,N] stores one
        // element per slot. Both reduce to a constant `[M x T]` initializer.
        string initializer = GetGenericBaseName(type: preset.Type) == "BitArray"
            ? BuildBitArrayPresetInitializer(key: key, list: list, arrLlvm: arrLlvm)
            : BuildArrayPresetInitializer(key: key, list: list, arrLlvm: arrLlvm);

        EmitLine(sb: _globalDeclarations,
            line: $"{symbol} = private unnamed_addr constant {arrLlvm} {initializer}");
        _presetGlobals[key: key] = symbol;
        return symbol;
    }

    /// <summary>Builds the <c>[N x T] [...]</c> constant initializer for an <c>Array[T,N]</c> preset.</summary>
    private string BuildArrayPresetInitializer(string key, ListLiteralExpression list, string arrLlvm)
    {
        if (list.Elements.Count == 0)
            return "zeroinitializer";

        string elemLlvm = ArrayElementLlvmType(arrLlvm: arrLlvm);  // e.g. "i16"
        var scratch = new StringBuilder();
        var parts = new List<string>(capacity: list.Elements.Count);
        foreach (Expression element in list.Elements)
        {
            if (element is not LiteralExpression lit)
                throw new NotImplementedException(
                    message:
                    $"Aggregate preset '{key}' element must be a scalar literal; got {element.GetType().Name}.");
            // Numeric/bool/char literals render to a pure constant with no IR side effects.
            parts.Add(item: $"{elemLlvm} {EmitLiteral(sb: scratch, literal: lit)}");
        }

        return $"[{string.Join(separator: ", ", values: parts)}]";
    }

    /// <summary>
    /// Builds the <c>[(N+7)/8 x i8] [...]</c> constant initializer for a <c>BitArray[N]</c> preset by
    /// packing 8 bool literals per byte (bit 0 = LSB), mirroring the inline literal bit-packing.
    /// </summary>
    private static string BuildBitArrayPresetInitializer(string key, ListLiteralExpression list,
        string arrLlvm)
    {
        int bitCount = list.Elements.Count;
        if (bitCount == 0)
            return "zeroinitializer";

        int byteCount = (bitCount + 7) / 8;
        var parts = new List<string>(capacity: byteCount);
        for (int byteIdx = 0; byteIdx < byteCount; byteIdx++)
        {
            int byteVal = 0;
            for (int bitIdx = 0; bitIdx < 8 && byteIdx * 8 + bitIdx < bitCount; bitIdx++)
            {
                Expression bit = list.Elements[index: byteIdx * 8 + bitIdx];
                if (bit is not LiteralExpression { Value: bool b })
                    throw new NotImplementedException(
                        message:
                        $"BitArray preset '{key}' element must be a bool literal; got {bit.GetType().Name}.");
                if (b)
                    byteVal |= 1 << bitIdx;
            }

            parts.Add(item: $"i8 {byteVal}");
        }

        return $"[{string.Join(separator: ", ", values: parts)}]";
    }

    /// <summary>Extracts the element type from an array LLVM type (<c>"[N x ELEM]"</c> -&gt; <c>"ELEM"</c>).</summary>
    private static string ArrayElementLlvmType(string arrLlvm)
    {
        int x = arrLlvm.IndexOf(value: " x ", comparisonType: StringComparison.Ordinal);
        if (arrLlvm.StartsWith(value: '[') && x > 0 && arrLlvm.EndsWith(value: ']'))
            return arrLlvm[(x + 3)..^1];
        throw new InvalidOperationException(
            message: $"Expected an array LLVM type for an aggregate preset, got '{arrLlvm}'.");
    }

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

        // `none` value literal -> emit zeroinitializer (carriers are zero-tagged in the absent arm).
        // ExpressionLoweringPass.TryWrapCarrier rewrites this into a typed CreatorExpression
        // before codegen for most contexts; this is the fallback path.
        if (literal.LiteralType == TokenType.NoneValue)
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
            or TokenType.S256Literal
            or TokenType.U8Literal or TokenType.U16Literal
            or TokenType.U32Literal or TokenType.U64Literal
            or TokenType.U128Literal or TokenType.U256Literal
            or TokenType.AddressLiteral;
    }

    /// <summary>
    /// Checks if a token type represents a floating-point literal.
    /// </summary>
    private static bool IsFloatLiteralType(TokenType type)
    {
        return type is TokenType.F16Literal
            or TokenType.F32Literal or TokenType.F64Literal
            or TokenType.F128Literal;
    }

    /// <summary>
    /// Returns whether is decimal float literal type applies in the current compiler context.
    /// </summary>
    private static bool IsDecimalFloatLiteralType(TokenType type)
    {
        return type is TokenType.D32Literal or TokenType.D64Literal
            or TokenType.D128Literal or TokenType.DecimalLiteral;
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

        // Bytes record literal — must mirror the runtime layout
        // `{ ptr data, i64 count, ptr ctrl }`. The `ctrl` slot is null for static
        // literals; `$copy`/`$destroy` treat null ctrl as a no-op so the literal
        // is never freed and refcount ops are skipped.
        EmitLine(sb: _globalDeclarations,
            line: $"{constName} = private unnamed_addr constant {{ ptr, i64, ptr }} {{ ptr {dataName}, i64 {count}, ptr null }}");

        // Load the record value from the global. Bytes is now a value-typed
        // record, so call sites expect the record by value, not a pointer. Use
        // the named struct type so the SSA value matches the call signature.
        string loaded = NextTemp();
        EmitLine(sb: sb, line: $"{loaded} = load %Record.Bytes, ptr {constName}");
        return loaded;
    }

    /// <summary>
    /// Strips the type suffix from a numeric literal string (e.g., "1_s32" "1", "3_14_f64" "3_14")
    /// and removes digit separator underscores.
    /// </summary>
    /// <summary>
    /// Converts a prefixed literal (0x hex, 0b binary, 0o octal) to decimal for LLVM IR.
    /// Hex floats (containing '.' or 'p') are passed through for EmitFloatLiteral.
    /// </summary>
    private static string ConvertPrefixedToDecimal(string value) // NOSONAR S3776
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
        "addr", "s256", "u256", "s128", "u128", "s64", "u64", "s32", "u32",
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
        // Special float literals: inf/nan (NaN uses IEEE 754 quiet-NaN bit patterns)
        if (numericValue == "inf" || numericValue == "nan")
        {
            return EmitSpecialFloatLiteral(name: numericValue, literalType: literalType);
        }

        // F128: use native parser for full 128-bit precision. F128 is an i128
        // bit carrier in LLVM (never fp128), so the literal is emitted as an
        // i128 integer constant holding the IEEE binary128 bit pattern
        // (LLVM hex integer syntax: u0x<Hi16hex><Lo16hex>).
        if (literalType == TokenType.F128Literal)
        {
            NumericLiteralParser.F128 f128 =
                NumericLiteralParser.ParseF128(str: numericValue);
            return $"u0x{f128.Hi:X16}{f128.Lo:X16}";
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

    private static string EmitSpecialFloatLiteral(string name, TokenType literalType)
    {
        // F128 quiet-NaN: exp=all-ones, MSB of mantissa set; F128 inf: exp=all-ones, mantissa=0.
        // Emitted as an i128 bit-pattern constant (F128 is never LLVM fp128).
        if (literalType == TokenType.F128Literal)
        {
            ulong hi = name == "nan" ? 0x7FFF800000000000UL : 0x7FFF000000000000UL;
            return $"u0x{hi:X16}0000000000000000";
        }
        double d = name == "nan" ? double.NaN : double.PositiveInfinity;
        return EmitDoubleAsLlvmHex(d: d, literalType: literalType);
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
    private static bool TryParseHexFloat(string value, out double result) // NOSONAR S3776
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
        // IEEE 754-2008 decimal: bit-pattern combination-field encodes special values.
        // Common-form: top 5 bits 11110 = inf, 11111 = NaN (quiet NaN: payload MSB 0).
        if (numericValue == "inf" || numericValue == "nan")
        {
            bool isNan = numericValue == "nan";
            switch (literalType)
            {
                case TokenType.D32Literal:
                    return (isNan ? 0x7C000000U : 0x78000000U).ToString();
                case TokenType.D64Literal:
                    return (isNan ? 0x7C00000000000000UL : 0x7800000000000000UL).ToString();
                case TokenType.D128Literal:
                {
                    // D128 is now @llvm("i128") BID; emit a single i128 constant. The combination
                    // prefix (0x78.. inf / 0x7C.. nan) lives in the high 64 bits, low bits zero.
                    ulong hi = isNan ? 0x7C00000000000000UL : 0x7800000000000000UL;
                    return $"u0x{hi:X16}0000000000000000";
                }
                case TokenType.DecimalLiteral:
                {
                    // Decimal is @llvm("i256") BID; combination prefix in the top byte, rest zero.
                    ulong top = isNan ? 0x7C00000000000000UL : 0x7800000000000000UL;
                    return $"u0x{top:X16}000000000000000000000000000000000000000000000000";
                }
            }
        }
        switch (literalType)
        {
            case TokenType.D32Literal:
                return NumericLiteralParser
                                       .EncodeD32Bid(str: numericValue)
                                       .Value
                                       .ToString();
            case TokenType.D64Literal:
                return NumericLiteralParser
                                       .EncodeD64Bid(str: numericValue)
                                       .Value
                                       .ToString();
            case TokenType.D128Literal:
            {
                // D128 is now @llvm("i128") BID; emit a single i128 constant (like F128).
                NumericLiteralParser.D128 d128 =
                    NumericLiteralParser.EncodeD128Bid(str: numericValue);
                return $"u0x{d128.Hi:X16}{d128.Lo:X16}";
            }
            case TokenType.DecimalLiteral:
            {
                // Decimal is @llvm("i256") BID; emit a single i256 constant (4 words, big-endian).
                NumericLiteralParser.Decimal256 d =
                    NumericLiteralParser.EncodeDecimal(str: numericValue);
                return $"u0x{d.W3:X16}{d.W2:X16}{d.W1:X16}{d.W0:X16}";
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
        string constName = EmitStringLiteralGlobal(value: value);
        // Load the record value from the global. Text is now a value-typed
        // record, so call sites expect the record by value, not a pointer.
        // Use the named struct type so the SSA value matches the call signature.
        // The optimizer collapses redundant loads of the same global.
        string loaded = NextTemp();
        EmitLine(sb: sb, line: $"{loaded} = load %Record.Text, ptr {constName}");
        return loaded;
    }

    /// <summary>
    /// Returns the name of the global constant that backs a string literal.
    /// Use this when you need the literal's address (e.g. to ptrtoint or to GEP
    /// directly into the data/count fields) rather than its value.
    /// </summary>
    private string EmitStringLiteralGlobal(string value)
    {
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

        // Layer 2: Text record payload `{ ptr data, i64 count, ptr ctrl }`.
        // `ctrl` is null for static literals — $copy/$destroy short-circuit on
        // null and never free the literal or touch the refcount.
        EmitLine(sb: _globalDeclarations,
            line: $"{constName} = private unnamed_addr constant {{ ptr, i64, ptr }} {{ ptr {dataName}, i64 {count}, ptr null }}");

        return constName;
    }
}
