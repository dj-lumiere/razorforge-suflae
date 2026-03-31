namespace SemanticAnalysis;

using Enums;
using Results;
using Native;
using Types;
using Compiler.Lexer;
using SyntaxTree;
using Diagnostics;
using TypeSymbol = Types.TypeInfo;

/// <summary>
/// Phase 3: Literal expression analysis and deferred numeric parsing.
/// </summary>
public sealed partial class SemanticAnalyzer
{
    private TypeSymbol AnalyzeLiteralExpression(LiteralExpression literal,
        TypeSymbol? expectedType = null)
    {
        // Map token type to the corresponding type (PascalCase)
        string? typeName = literal.LiteralType switch
        {
            // Signed integers
            TokenType.S8Literal => "S8",
            TokenType.S16Literal => "S16",
            TokenType.S32Literal => "S32",
            TokenType.S64Literal => "S64",
            TokenType.S128Literal => "S128",
            // Unsigned integers
            TokenType.U8Literal => "U8",
            TokenType.U16Literal => "U16",
            TokenType.U32Literal => "U32",
            TokenType.U64Literal => "U64",
            TokenType.U128Literal => "U128",
            TokenType.AddressLiteral => "Address",

            // Floating-point
            TokenType.F16Literal => "F16",
            TokenType.F32Literal => "F32",
            TokenType.F64Literal => "F64",
            TokenType.F128Literal => "F128",

            // Decimal floating-point
            TokenType.D32Literal => "D32",
            TokenType.D64Literal => "D64",
            TokenType.D128Literal => "D128",

            // RazorForge uses fixed-width types (S64, F64) for unsuffixed literals
            // Suflae uses arbitrary precision types (Integer, Decimal)
            TokenType.Integer => _registry.Language == Language.Suflae
                ? "Integer"
                : "S64",
            TokenType.Decimal => _registry.Language == Language.Suflae
                ? "Decimal"
                : "F64",

            // Boolean
            TokenType.True or TokenType.False => "Bool",

            // Text and characters
            TokenType.TextLiteral => "Text",
            TokenType.BytesLiteral => "Bytes",
            TokenType.BytesRawLiteral => "Bytes",
            TokenType.ByteLetterLiteral => "Byte",
            TokenType.LetterLiteral => "Letter",

            // byte size literals (all map to ByteSize type)
            TokenType.ByteLiteral or TokenType.KilobyteLiteral or TokenType.KibibyteLiteral
                or TokenType.MegabyteLiteral or TokenType.MebibyteLiteral
                or TokenType.GigabyteLiteral or TokenType.GibibyteLiteral => "ByteSize",

            // Duration literals (all map to Duration type)
            TokenType.WeekLiteral or TokenType.DayLiteral or TokenType.HourLiteral
                or TokenType.MinuteLiteral or TokenType.SecondLiteral
                or TokenType.MillisecondLiteral or TokenType.MicrosecondLiteral
                or TokenType.NanosecondLiteral => "Duration",

            // Complex/Imaginary literals
            TokenType.J32Literal => "C32",
            TokenType.J64Literal => "C64",
            TokenType.J128Literal => "C128",
            TokenType.JnLiteral => "Complex",

            // Unknown literal type - error
            _ => null
        };

        // Report error for unknown literal types
        if (typeName == null)
        {
            ReportError(code: SemanticDiagnosticCode.UnknownLiteralType,
                message: $"Unknown literal type '{literal.LiteralType}'.",
                location: literal.Location);
            return ErrorTypeInfo.Instance;
        }

        // Contextual type inference for unsuffixed integer literals
        // If expected type is a fixed-width integer type and literal is Integer or S64 (default unsuffixed),
        // infer to expected type
        if (expectedType != null &&
            (literal.LiteralType == TokenType.Integer ||
             literal.LiteralType == TokenType.S64Literal) &&
            IsFixedWidthIntegerType(type: expectedType))
        {
            // Check if the literal value fits in the expected type
            if (LiteralFitsInType(literal: literal, targetType: expectedType))
            {
                typeName = expectedType.Name;
            }
            else
            {
                string range = GetIntegerTypeRange(typeName: expectedType.Name);
                ReportError(code: SemanticDiagnosticCode.IntegerLiteralOverflow,
                    message:
                    $"Integer literal '{literal.Value}' overflows type '{expectedType.Name}'. Valid range: {range}.",
                    location: literal.Location);
                return ErrorTypeInfo.Instance;
            }
        }

        // Parse and validate deferred numeric types using native libraries
        if (literal.Value is string rawValue)
        {
            ParsedLiteral? parsed = ParseDeferredLiteral(literal: literal,
                rawValue: rawValue,
                resolvedTypeName: typeName);
            if (parsed != null)
            {
                _parsedLiterals[key: literal.Location] = parsed;
            }
        }

        TypeSymbol? type = LookupTypeWithImports(name: typeName);
        if (type == null)
        {
            ReportError(code: SemanticDiagnosticCode.LiteralTypeNotDefined,
                message: $"Type '{typeName}' is not defined.",
                location: literal.Location);
            return ErrorTypeInfo.Instance;
        }

        return type;
    }

    /// <summary>
    /// Checks if a type is a fixed-width integer type (S8-S128, U8-U128, Address).
    /// </summary>
    // TODO: remove this because it is going to be protocol based
    private static bool IsFixedWidthIntegerType(TypeSymbol type)
    {
        return type.Name is "S8" or "S16" or "S32" or "S64" or "S128" or "U8" or "U16" or "U32"
            or "U64" or "U128" or "Address";
    }

    private static string GetIntegerTypeRange(string typeName)
    {
        return typeName switch
        {
            "S8" => $"{sbyte.MinValue} to {sbyte.MaxValue}",
            "S16" => $"{short.MinValue} to {short.MaxValue}",
            "S32" => $"{int.MinValue} to {int.MaxValue}",
            "S64" => $"{long.MinValue} to {long.MaxValue}",
            "S128" => $"{Int128.MinValue} to {Int128.MaxValue}",
            "U8" => $"0 to {byte.MaxValue}",
            "U16" => $"0 to {ushort.MaxValue}",
            "U32" => $"0 to {uint.MaxValue}",
            "U64" => $"0 to {ulong.MaxValue}",
            "U128" => $"0 to {UInt128.MaxValue}",
            _ => "unknown"
        };
    }

    /// <summary>
    /// Checks if an integer literal value fits within the range of the target type.
    /// </summary>
    private static bool LiteralFitsInType(LiteralExpression literal, TypeSymbol targetType)
    {
        long value;

        if (literal.Value is long longValue)
        {
            value = longValue;
        }
        else if (literal.Value is string strValue)
        {
            // Clean underscores from numeric literal string and parse
            string cleaned = strValue.Replace(oldValue: "_", newValue: "");
            if (!long.TryParse(s: cleaned, result: out value))
            {
                // Value doesn't fit in long ? only S128/U128 could hold it
                return targetType.Name is "S128" or "U128";
            }
        }
        else
        {
            return false;
        }

        return targetType.Name switch
        {
            "S8" => value is >= sbyte.MinValue and <= sbyte.MaxValue,
            "S16" => value is >= short.MinValue and <= short.MaxValue,
            "S32" => value is >= int.MinValue and <= int.MaxValue,
            "S64" => true, // Any long fits in S64
            "S128" => true, // Any long fits in S128
            "U8" => value is >= 0 and <= byte.MaxValue,
            "U16" => value is >= 0 and <= ushort.MaxValue,
            "U32" => value is >= 0 and <= uint.MaxValue,
            "U64" => value >= 0, // Any non-negative long fits in U64
            "U128" => value >= 0,
            "Address" => true, // System-dependent, allow for now
            _ => false
        };
    }

    /// <summary>
    /// Parses a deferred numeric literal using native libraries or managed parsing.
    /// Called for all numeric, duration, and byte size literals stored as strings.
    /// </summary>
    /// <param name="literal">The literal expression.</param>
    /// <param name="rawValue">The raw string value to parse.</param>
    /// <param name="resolvedTypeName">The resolved type name (may differ from literal type due to contextual inference).</param>
    /// <returns>The parsed literal, or null if parsing failed.</returns>
    private ParsedLiteral? ParseDeferredLiteral(LiteralExpression literal, string rawValue,
        string resolvedTypeName)
    {
        try
        {
            return literal.LiteralType switch
            {
                // Fixed-width signed integers
                TokenType.S8Literal => ParseSignedIntLiteral(literal: literal,
                    rawValue: rawValue,
                    typeName: "S8",
                    minValue: sbyte.MinValue,
                    maxValue: sbyte.MaxValue),
                TokenType.S16Literal => ParseSignedIntLiteral(literal: literal,
                    rawValue: rawValue,
                    typeName: "S16",
                    minValue: short.MinValue,
                    maxValue: short.MaxValue),
                TokenType.S32Literal => ParseSignedIntLiteral(literal: literal,
                    rawValue: rawValue,
                    typeName: "S32",
                    minValue: int.MinValue,
                    maxValue: int.MaxValue),
                TokenType.S64Literal => ParseIntegerByResolvedType(literal: literal,
                    rawValue: rawValue,
                    resolvedTypeName: resolvedTypeName),
                TokenType.S128Literal => ParseS128Literal(literal: literal, rawValue: rawValue),
                // Fixed-width unsigned integers
                TokenType.U8Literal => ParseUnsignedIntLiteral(literal: literal,
                    rawValue: rawValue,
                    typeName: "U8",
                    maxValue: byte.MaxValue),
                TokenType.U16Literal => ParseUnsignedIntLiteral(literal: literal,
                    rawValue: rawValue,
                    typeName: "U16",
                    maxValue: ushort.MaxValue),
                TokenType.U32Literal => ParseUnsignedIntLiteral(literal: literal,
                    rawValue: rawValue,
                    typeName: "U32",
                    maxValue: uint.MaxValue),
                TokenType.U64Literal => ParseUnsignedIntLiteral(literal: literal,
                    rawValue: rawValue,
                    typeName: "U64",
                    maxValue: ulong.MaxValue),
                TokenType.U128Literal => ParseU128Literal(literal: literal, rawValue: rawValue),
                TokenType.AddressLiteral => ParseUnsignedIntLiteral(literal: literal,
                    rawValue: rawValue,
                    typeName: "Address",
                    maxValue: ulong.MaxValue,
                    suffix: "addr"),

                // Fixed-width floats (F16, F32, F64 use .NET native types; F128 uses native library)
                TokenType.F16Literal => ParseF16Literal(literal: literal, rawValue: rawValue),
                TokenType.F32Literal => ParseF32Literal(literal: literal, rawValue: rawValue),
                TokenType.F64Literal => ParseF64Literal(literal: literal, rawValue: rawValue),
                TokenType.F128Literal => ParseF128Literal(literal: literal, rawValue: rawValue),

                // Decimal floating-point (all use native library)
                TokenType.D32Literal => ParseD32Literal(literal: literal, rawValue: rawValue),
                TokenType.D64Literal => ParseD64Literal(literal: literal, rawValue: rawValue),
                TokenType.D128Literal => ParseD128Literal(literal: literal, rawValue: rawValue),

                // RazorForge uses fixed-width types (S64, F64) for unsuffixed literals
                // Suflae uses arbitrary precision types (Integer, Decimal)
                // Use resolvedTypeName to pick the right parser when contextual inference changed the type
                TokenType.Integer => _registry.Language == Language.Suflae
                    ? ParseIntegerLiteral(literal: literal, rawValue: rawValue)
                    : ParseIntegerByResolvedType(literal: literal,
                        rawValue: rawValue,
                        resolvedTypeName: resolvedTypeName),
                TokenType.Decimal => _registry.Language == Language.Suflae
                    ? ParseDecimalLiteral(literal: literal, rawValue: rawValue)
                    : ParseF64Literal(literal: literal, rawValue: rawValue),

                // Imaginary literals for complex numbers
                TokenType.J32Literal => ParseJ32Literal(literal: literal, rawValue: rawValue),
                TokenType.J64Literal => ParseJ64Literal(literal: literal, rawValue: rawValue),
                TokenType.J128Literal => ParseJ128Literal(literal: literal, rawValue: rawValue),
                TokenType.JnLiteral => ParseJnLiteral(literal: literal, rawValue: rawValue),

                // Duration literals
                TokenType.NanosecondLiteral => ParseDurationLiteral(literal: literal,
                    rawValue: rawValue,
                    unit: "ns",
                    multiplier: 1L),
                TokenType.MicrosecondLiteral => ParseDurationLiteral(literal: literal,
                    rawValue: rawValue,
                    unit: "us",
                    multiplier: 1_000L),
                TokenType.MillisecondLiteral => ParseDurationLiteral(literal: literal,
                    rawValue: rawValue,
                    unit: "ms",
                    multiplier: 1_000_000L),
                TokenType.SecondLiteral => ParseDurationLiteral(literal: literal,
                    rawValue: rawValue,
                    unit: "s",
                    multiplier: 1_000_000_000L),
                TokenType.MinuteLiteral => ParseDurationLiteral(literal: literal,
                    rawValue: rawValue,
                    unit: "m",
                    multiplier: 60_000_000_000L),
                TokenType.HourLiteral => ParseDurationLiteral(literal: literal,
                    rawValue: rawValue,
                    unit: "h",
                    multiplier: 3_600_000_000_000L),
                TokenType.DayLiteral => ParseDurationLiteral(literal: literal,
                    rawValue: rawValue,
                    unit: "d",
                    multiplier: 86_400_000_000_000L),
                TokenType.WeekLiteral => ParseDurationLiteral(literal: literal,
                    rawValue: rawValue,
                    unit: "w",
                    multiplier: 604_800_000_000_000L),

                // byte size literals
                TokenType.ByteLiteral => ParseByteSizeLiteral(literal: literal,
                    rawValue: rawValue,
                    unit: "b",
                    multiplier: 1UL),
                TokenType.KilobyteLiteral => ParseByteSizeLiteral(literal: literal,
                    rawValue: rawValue,
                    unit: "kb",
                    multiplier: 1_000UL),
                TokenType.KibibyteLiteral => ParseByteSizeLiteral(literal: literal,
                    rawValue: rawValue,
                    unit: "kib",
                    multiplier: 1_024UL),
                TokenType.MegabyteLiteral => ParseByteSizeLiteral(literal: literal,
                    rawValue: rawValue,
                    unit: "mb",
                    multiplier: 1_000_000UL),
                TokenType.MebibyteLiteral => ParseByteSizeLiteral(literal: literal,
                    rawValue: rawValue,
                    unit: "mib",
                    multiplier: 1_048_576UL),
                TokenType.GigabyteLiteral => ParseByteSizeLiteral(literal: literal,
                    rawValue: rawValue,
                    unit: "gb",
                    multiplier: 1_000_000_000UL),
                TokenType.GibibyteLiteral => ParseByteSizeLiteral(literal: literal,
                    rawValue: rawValue,
                    unit: "gib",
                    multiplier: 1_073_741_824UL),

                _ => null
            };
        }
        catch (Exception ex)
        {
            ReportError(code: SemanticDiagnosticCode.NumericLiteralParseFailed,
                message: $"Failed to parse numeric literal '{rawValue}': {ex.Message}",
                location: literal.Location);
            return null;
        }
    }

    private ParsedLiteral ParseF128Literal(LiteralExpression literal, string rawValue)
    {
        NumericLiteralParser.F128 result = NumericLiteralParser.ParseF128(str: rawValue);
        return new ParsedF128(Location: literal.Location, Lo: result.Lo, Hi: result.Hi);
    }

    private ParsedLiteral ParseD32Literal(LiteralExpression literal, string rawValue)
    {
        NumericLiteralParser.D32 result = NumericLiteralParser.ParseD32(str: rawValue);
        return new ParsedD32(Location: literal.Location, Value: result.Value);
    }

    private ParsedLiteral ParseD64Literal(LiteralExpression literal, string rawValue)
    {
        NumericLiteralParser.D64 result = NumericLiteralParser.ParseD64(str: rawValue);
        return new ParsedD64(Location: literal.Location, Value: result.Value);
    }

    private ParsedLiteral ParseD128Literal(LiteralExpression literal, string rawValue)
    {
        NumericLiteralParser.D128 result = NumericLiteralParser.ParseD128(str: rawValue);
        return new ParsedD128(Location: literal.Location, Lo: result.Lo, Hi: result.Hi);
    }

    private ParsedLiteral ParseIntegerLiteral(LiteralExpression literal, string rawValue)
    {
        (byte[] bytes, int sign) = NumericLiteralParser.ParseIntegerToBytes(str: rawValue);
        if (bytes.Length == 0)
        {
            ReportError(code: SemanticDiagnosticCode.InvalidIntegerLiteral,
                message: $"Invalid Integer literal: '{rawValue}'",
                location: literal.Location);
            return new ParsedInteger(Location: literal.Location,
                Limbs: [],
                Sign: 0,
                Exponent: 0);
        }

        return new ParsedInteger(Location: literal.Location,
            Limbs: bytes,
            Sign: sign,
            Exponent: 0);
    }

    private ParsedLiteral ParseDecimalLiteral(LiteralExpression literal, string rawValue)
    {
        (string value, int sign, int exponent, int significantDigits, bool isInteger) =
            NumericLiteralParser.ParseDecimalInfo(str: rawValue);

        if (string.IsNullOrEmpty(value: value))
        {
            ReportError(code: SemanticDiagnosticCode.InvalidDecimalLiteral,
                message: $"Invalid Decimal literal: '{rawValue}'",
                location: literal.Location);
            return new ParsedDecimal(Location: literal.Location,
                StringValue: rawValue,
                Sign: 0,
                Exponent: 0,
                SignificantDigits: 0,
                IsInteger: false);
        }

        return new ParsedDecimal(Location: literal.Location,
            StringValue: value,
            Sign: sign,
            Exponent: exponent,
            SignificantDigits: significantDigits,
            IsInteger: isInteger);
    }

    #region Fixed-Width Numeric Literal Parsing

    /// <summary>
    /// Parses a signed integer literal (S8-S64) with overflow validation.
    /// </summary>
    private ParsedLiteral? ParseSignedIntLiteral(LiteralExpression literal, string rawValue,
        string typeName, long minValue, long maxValue)
    {
        // Extract numeric part by removing the type suffix (e.g., "1s32" -> "1")
        string numericPart =
            ExtractNumericPart(rawValue: rawValue, suffix: typeName.ToLowerInvariant());
        string cleanedValue = CleanNumericLiteral(value: numericPart);

        if (!TryParseSignedInteger(value: cleanedValue, result: out long value))
        {
            ReportError(code: SemanticDiagnosticCode.InvalidIntegerLiteral,
                message: $"Invalid {typeName} literal: '{rawValue}'",
                location: literal.Location);
            return null;
        }

        if (value >= minValue && value <= maxValue)
        {
            return new ParsedSignedInt(Location: literal.Location,
                TypeName: typeName,
                Value: value);
        }

        ReportError(code: SemanticDiagnosticCode.IntegerLiteralOverflow,
            message:
            $"{typeName} literal '{rawValue}' overflows. Valid range: {minValue} to {maxValue}.",
            location: literal.Location);
        return null;
    }

    /// <summary>
    /// Parses an S128 literal with Int128 overflow validation.
    /// </summary>
    private ParsedLiteral? ParseS128Literal(LiteralExpression literal, string rawValue)
    {
        string numericPart = ExtractNumericPart(rawValue: rawValue, suffix: "s128");
        string cleanedValue = CleanNumericLiteral(value: numericPart);

        if (Int128.TryParse(s: cleanedValue, result: out Int128 value))
        {
            return new ParsedSignedInt(Location: literal.Location, TypeName: "S128", Value: value);
        }

        ReportError(code: SemanticDiagnosticCode.InvalidIntegerLiteral,
            message: $"Invalid S128 literal: '{rawValue}'",
            location: literal.Location);
        return null;
    }

    /// <summary>
    /// Routes unsuffixed integer literal parsing based on the contextually resolved type name.
    /// </summary>
    private ParsedLiteral? ParseIntegerByResolvedType(LiteralExpression literal, string rawValue,
        string resolvedTypeName)
    {
        return resolvedTypeName switch
        {
            "S8" => ParseSignedIntLiteral(literal: literal,
                rawValue: rawValue,
                typeName: "S8",
                minValue: sbyte.MinValue,
                maxValue: sbyte.MaxValue),
            "S16" => ParseSignedIntLiteral(literal: literal,
                rawValue: rawValue,
                typeName: "S16",
                minValue: short.MinValue,
                maxValue: short.MaxValue),
            "S32" => ParseSignedIntLiteral(literal: literal,
                rawValue: rawValue,
                typeName: "S32",
                minValue: int.MinValue,
                maxValue: int.MaxValue),
            "S128" => ParseS128Literal(literal: literal, rawValue: rawValue),
            "U8" => ParseUnsignedIntLiteral(literal: literal,
                rawValue: rawValue,
                typeName: "U8",
                maxValue: byte.MaxValue),
            "U16" => ParseUnsignedIntLiteral(literal: literal,
                rawValue: rawValue,
                typeName: "U16",
                maxValue: ushort.MaxValue),
            "U32" => ParseUnsignedIntLiteral(literal: literal,
                rawValue: rawValue,
                typeName: "U32",
                maxValue: uint.MaxValue),
            "U64" => ParseUnsignedIntLiteral(literal: literal,
                rawValue: rawValue,
                typeName: "U64",
                maxValue: ulong.MaxValue),
            "U128" => ParseU128Literal(literal: literal, rawValue: rawValue),
            "Address" => ParseUnsignedIntLiteral(literal: literal,
                rawValue: rawValue,
                typeName: "Address",
                maxValue: ulong.MaxValue,
                suffix: "addr"),
            _ => ParseSignedIntLiteral(literal: literal,
                rawValue: rawValue,
                typeName: "S64",
                minValue: long.MinValue,
                maxValue: long.MaxValue)
        };
    }

    /// <summary>
    /// Parses an unsigned integer literal (U8-U64, Address) with overflow validation.
    /// </summary>
    private ParsedLiteral? ParseUnsignedIntLiteral(LiteralExpression literal, string rawValue,
        string typeName, ulong maxValue, string? suffix = null)
    {
        // Extract numeric part by removing the type suffix (e.g., "1u32" -> "1")
        string numericPart = ExtractNumericPart(rawValue: rawValue,
            suffix: suffix ?? typeName.ToLowerInvariant());
        string cleanedValue = CleanNumericLiteral(value: numericPart);

        if (!TryParseUnsignedInteger(value: cleanedValue, result: out ulong value))
        {
            ReportError(code: SemanticDiagnosticCode.InvalidIntegerLiteral,
                message: $"Invalid {typeName} literal: '{rawValue}'",
                location: literal.Location);
            return null;
        }

        if (value <= maxValue)
        {
            return new ParsedUnsignedInt(Location: literal.Location,
                TypeName: typeName,
                Value: (UInt128)value);
        }

        ReportError(code: SemanticDiagnosticCode.IntegerLiteralOverflow,
            message: $"{typeName} literal '{rawValue}' overflows. Valid range: 0 to {maxValue}.",
            location: literal.Location);
        return null;
    }

    /// <summary>
    /// Parses a U128 literal with UInt128 overflow validation.
    /// </summary>
    private ParsedLiteral? ParseU128Literal(LiteralExpression literal, string rawValue)
    {
        string numericPart = ExtractNumericPart(rawValue: rawValue, suffix: "u128");
        string cleanedValue = CleanNumericLiteral(value: numericPart);

        if (UInt128.TryParse(s: cleanedValue, result: out UInt128 value))
        {
            return new ParsedUnsignedInt(Location: literal.Location,
                TypeName: "U128",
                Value: value);
        }

        ReportError(code: SemanticDiagnosticCode.InvalidIntegerLiteral,
            message: $"Invalid U128 literal: '{rawValue}'",
            location: literal.Location);
        return null;
    }

    /// <summary>
    /// Parses an F16 (half-precision) floating-point literal using .NET Half type.
    /// </summary>
    private ParsedLiteral? ParseF16Literal(LiteralExpression literal, string rawValue)
    {
        string numericPart = ExtractNumericPart(rawValue: rawValue, suffix: "f16");
        string cleanedValue = CleanNumericLiteral(value: numericPart);

        Half value;
        if (TryParseHexFloat(value: cleanedValue, result: out double hexVal))
        {
            value = (Half)hexVal;
        }
        else if (!Half.TryParse(s: cleanedValue, result: out value))
        {
            ReportError(code: SemanticDiagnosticCode.NumericLiteralParseFailed,
                message: $"Invalid F16 literal: '{rawValue}'",
                location: literal.Location);
            return null;
        }

        if (!Half.IsInfinity(value: value))
        {
            return new ParsedFloat(Location: literal.Location,
                TypeName: "F16",
                Value: (double)value);
        }

        ReportError(code: SemanticDiagnosticCode.FloatLiteralOverflow,
            message: $"F16 literal '{rawValue}' overflows the representable range.",
            location: literal.Location);
        return null;
    }

    /// <summary>
    /// Parses an F32 (single-precision) floating-point literal using .NET float type.
    /// </summary>
    private ParsedLiteral? ParseF32Literal(LiteralExpression literal, string rawValue)
    {
        string numericPart = ExtractNumericPart(rawValue: rawValue, suffix: "f32");
        string cleanedValue = CleanNumericLiteral(value: numericPart);

        float value;
        if (TryParseHexFloat(value: cleanedValue, result: out double hexVal32))
        {
            value = (float)hexVal32;
        }
        else if (!float.TryParse(s: cleanedValue, result: out value))
        {
            ReportError(code: SemanticDiagnosticCode.NumericLiteralParseFailed,
                message: $"Invalid F32 literal: '{rawValue}'",
                location: literal.Location);
            return null;
        }

        if (!float.IsInfinity(f: value))
        {
            return new ParsedFloat(Location: literal.Location, TypeName: "F32", Value: value);
        }

        ReportError(code: SemanticDiagnosticCode.FloatLiteralOverflow,
            message: $"F32 literal '{rawValue}' overflows the representable range.",
            location: literal.Location);
        return null;
    }

    /// <summary>
    /// Parses an F64 (double-precision) floating-point literal using .NET double type.
    /// </summary>
    private ParsedLiteral? ParseF64Literal(LiteralExpression literal, string rawValue)
    {
        string numericPart = ExtractNumericPart(rawValue: rawValue, suffix: "f64");
        string cleanedValue = CleanNumericLiteral(value: numericPart);

        double value;
        if (TryParseHexFloat(value: cleanedValue, result: out double hexVal64))
        {
            value = hexVal64;
        }
        else if (!double.TryParse(s: cleanedValue, result: out value))
        {
            ReportError(code: SemanticDiagnosticCode.NumericLiteralParseFailed,
                message: $"Invalid F64 literal: '{rawValue}'",
                location: literal.Location);
            return null;
        }

        if (!double.IsInfinity(d: value))
        {
            return new ParsedFloat(Location: literal.Location, TypeName: "F64", Value: value);
        }

        ReportError(code: SemanticDiagnosticCode.FloatLiteralOverflow,
            message: $"F64 literal '{rawValue}' overflows the representable range.",
            location: literal.Location);
        return null;
    }

    #endregion

    #region Duration Literal Parsing

    /// <summary>
    /// Parses a duration literal and converts to nanoseconds.
    /// </summary>
    private ParsedLiteral? ParseDurationLiteral(LiteralExpression literal, string rawValue,
        string unit, long multiplier)
    {
        // Extract numeric part (remove unit suffix)
        string numericPart = ExtractNumericPart(rawValue: rawValue, suffix: unit);
        string cleanedValue = CleanNumericLiteral(value: numericPart);

        if (!TryParseSignedInteger(value: cleanedValue, result: out long value))
        {
            ReportError(code: SemanticDiagnosticCode.NumericLiteralParseFailed,
                message: $"Invalid duration literal: '{rawValue}'",
                location: literal.Location);
            return null;
        }

        // Check for overflow when multiplying
        try
        {
            checked
            {
                long nanoseconds = value * multiplier;
                return new ParsedDuration(Location: literal.Location,
                    Nanoseconds: nanoseconds,
                    OriginalUnit: unit);
            }
        }
        catch (OverflowException)
        {
            ReportError(code: SemanticDiagnosticCode.DurationLiteralOverflow,
                message:
                $"Duration literal '{rawValue}' overflows the maximum representable duration.",
                location: literal.Location);
            return null;
        }
    }

    #endregion

    #region ByteSize Literal Parsing

    /// <summary>
    /// Parses a ByteSize literal and converts to ByteSize.
    /// </summary>
    private ParsedLiteral? ParseByteSizeLiteral(LiteralExpression literal, string rawValue,
        string unit, ulong multiplier)
    {
        // Extract numeric part (remove unit suffix)
        string numericPart = ExtractNumericPart(rawValue: rawValue, suffix: unit);
        string cleanedValue = CleanNumericLiteral(value: numericPart);

        if (!TryParseUnsignedInteger(value: cleanedValue, result: out ulong value))
        {
            ReportError(code: SemanticDiagnosticCode.NumericLiteralParseFailed,
                message: $"Invalid byte size literal: '{rawValue}'",
                location: literal.Location);
            return null;
        }

        // Check for overflow when multiplying
        try
        {
            checked
            {
                ulong bytes = value * multiplier;
                return new ParsedByteSize(Location: literal.Location,
                    Bytes: bytes,
                    OriginalUnit: unit);
            }
        }
        catch (OverflowException)
        {
            ReportError(code: SemanticDiagnosticCode.ByteSizeLiteralOverflow,
                message:
                $"ByteSize literal '{rawValue}' overflows the maximum representable size.",
                location: literal.Location);
            return null;
        }
    }

    #endregion

    #region Imaginary Literal Parsing

    /// <summary>
    /// Parses a J32 (F32-based) imaginary literal.
    /// </summary>
    private ParsedLiteral? ParseJ32Literal(LiteralExpression literal, string rawValue)
    {
        // Remove 'j32' suffix
        string numericPart = RemoveImaginarySuffix(rawValue: rawValue, suffix: "j32");
        string cleanedValue = CleanNumericLiteral(value: numericPart);

        if (float.TryParse(s: cleanedValue, result: out float value))
        {
            return new ParsedJ32(Location: literal.Location, Value: value);
        }

        ReportError(code: SemanticDiagnosticCode.ImaginaryLiteralParseFailed,
            message: $"Invalid J32 literal: '{rawValue}'",
            location: literal.Location);
        return null;
    }

    /// <summary>
    /// Parses a J64 (F64-based) imaginary literal.
    /// </summary>
    private ParsedLiteral? ParseJ64Literal(LiteralExpression literal, string rawValue)
    {
        // Remove 'j64' or 'j' suffix
        string numericPart =
            rawValue.EndsWith(value: "j64", comparisonType: StringComparison.OrdinalIgnoreCase)
                ? RemoveImaginarySuffix(rawValue: rawValue, suffix: "j64")
                : RemoveImaginarySuffix(rawValue: rawValue, suffix: "j");
        string cleanedValue = CleanNumericLiteral(value: numericPart);

        if (double.TryParse(s: cleanedValue, result: out double value))
        {
            return new ParsedJ64(Location: literal.Location, Value: value);
        }

        ReportError(code: SemanticDiagnosticCode.ImaginaryLiteralParseFailed,
            message: $"Invalid J64 literal: '{rawValue}'",
            location: literal.Location);
        return null;
    }

    /// <summary>
    /// Parses a J128 (F128-based) imaginary literal using native library.
    /// </summary>
    private ParsedLiteral? ParseJ128Literal(LiteralExpression literal, string rawValue)
    {
        // Remove 'j128' suffix
        string numericPart = RemoveImaginarySuffix(rawValue: rawValue, suffix: "j128");
        string cleanedValue = CleanNumericLiteral(value: numericPart);

        try
        {
            NumericLiteralParser.F128 result = NumericLiteralParser.ParseF128(str: cleanedValue);
            return new ParsedJ128(Location: literal.Location, Lo: result.Lo, Hi: result.Hi);
        }
        catch (Exception ex)
        {
            ReportError(code: SemanticDiagnosticCode.ImaginaryLiteralParseFailed,
                message: $"Invalid J128 literal: '{rawValue}': {ex.Message}",
                location: literal.Location);
            return null;
        }
    }

    /// <summary>
    /// Parses a Jn (arbitrary-precision Decimal-based) imaginary literal using native library.
    /// </summary>
    private ParsedLiteral? ParseJnLiteral(LiteralExpression literal, string rawValue)
    {
        // Remove 'jn' suffix
        string numericPart = RemoveImaginarySuffix(rawValue: rawValue, suffix: "jn");
        string cleanedValue = CleanNumericLiteral(value: numericPart);

        try
        {
            (string value, int sign, int exponent, int significantDigits, bool _) =
                NumericLiteralParser.ParseDecimalInfo(str: cleanedValue);

            if (string.IsNullOrEmpty(value: value))
            {
                ReportError(code: SemanticDiagnosticCode.ImaginaryLiteralParseFailed,
                    message: $"Invalid Jn literal: '{rawValue}'",
                    location: literal.Location);
                return null;
            }

            return new ParsedJn(Location: literal.Location,
                StringValue: value,
                Sign: sign,
                Exponent: exponent,
                SignificantDigits: significantDigits);
        }
        catch (Exception ex)
        {
            ReportError(code: SemanticDiagnosticCode.ImaginaryLiteralParseFailed,
                message: $"Invalid Jn literal: '{rawValue}': {ex.Message}",
                location: literal.Location);
            return null;
        }
    }

    #endregion

    #region Literal Parsing Helpers

    /// <summary>
    /// Cleans a numeric literal by removing underscores.
    /// </summary>
    private static string CleanNumericLiteral(string value)
    {
        return value.Replace(oldValue: "_", newValue: "");
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
    /// Extracts the numeric part from a literal by removing the unit suffix.
    /// </summary>
    private static string ExtractNumericPart(string rawValue, string suffix)
    {
        if (rawValue.EndsWith(value: suffix, comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return rawValue[..^suffix.Length];
        }

        return rawValue;
    }

    /// <summary>
    /// Removes the imaginary suffix from a literal value.
    /// </summary>
    private static string RemoveImaginarySuffix(string rawValue, string suffix)
    {
        if (rawValue.EndsWith(value: suffix, comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return rawValue[..^suffix.Length];
        }

        return rawValue;
    }

    /// <summary>
    /// Tries to parse a signed integer, handling hex (0x), octal (0o), and binary (0b) prefixes.
    /// </summary>
    private static bool TryParseSignedInteger(string value, out long result)
    {
        result = 0;
        if (string.IsNullOrEmpty(value: value))
        {
            return false;
        }

        // Handle negative sign
        bool negative = value.StartsWith(value: '-');
        string numPart = negative
            ? value[1..]
            : value;

        // Handle base prefixes
        if (numPart.StartsWith(value: "0x", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(s: numPart[2..],
                    style: System.Globalization.NumberStyles.HexNumber,
                    provider: null,
                    result: out long hexVal))
            {
                result = negative
                    ? -hexVal
                    : hexVal;
                return true;
            }

            return false;
        }

        if (numPart.StartsWith(value: "0o", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                long octalVal = Convert.ToInt64(value: numPart[2..], fromBase: 8);
                result = negative
                    ? -octalVal
                    : octalVal;
                return true;
            }
            catch { return false; }
        }

        if (numPart.StartsWith(value: "0b", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                long binaryVal = Convert.ToInt64(value: numPart[2..], fromBase: 2);
                result = negative
                    ? -binaryVal
                    : binaryVal;
                return true;
            }
            catch { return false; }
        }

        // Decimal
        return long.TryParse(s: value, result: out result);
    }

    /// <summary>
    /// Tries to parse an unsigned integer, handling hex (0x), octal (0o), and binary (0b) prefixes.
    /// </summary>
    private static bool TryParseUnsignedInteger(string value, out ulong result)
    {
        result = 0;
        if (string.IsNullOrEmpty(value: value))
        {
            return false;
        }

        // Handle base prefixes
        if (value.StartsWith(value: "0x", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(s: value[2..],
                style: System.Globalization.NumberStyles.HexNumber,
                provider: null,
                result: out result);
        }

        if (value.StartsWith(value: "0o", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                result = Convert.ToUInt64(value: value[2..], fromBase: 8);
                return true;
            }
            catch { return false; }
        }

        if (value.StartsWith(value: "0b", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                result = Convert.ToUInt64(value: value[2..], fromBase: 2);
                return true;
            }
            catch { return false; }
        }

        // Decimal
        return ulong.TryParse(s: value, result: out result);
    }

    #endregion
}
