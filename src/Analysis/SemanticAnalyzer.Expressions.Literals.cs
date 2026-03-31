namespace SemanticAnalysis;

using Enums;
using Results;
using Native;
using Symbols;
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

    private TypeSymbol AnalyzeLiteralExpression(LiteralExpression literal, TypeSymbol? expectedType = null)
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
            TokenType.Integer => _registry.Language == Language.Suflae ? "Integer" : "S64",
            TokenType.Decimal => _registry.Language == Language.Suflae ? "Decimal" : "F64",

            // Boolean
            TokenType.True or TokenType.False => "Bool",

            // Text and characters
            TokenType.TextLiteral => "Text",
            TokenType.BytesLiteral => "Bytes",
            TokenType.BytesRawLiteral => "Bytes",
            TokenType.ByteLetterLiteral => "Byte",
            TokenType.LetterLiteral => "Letter",

            // byte size literals (all map to ByteSize type)
            TokenType.ByteLiteral or
            TokenType.KilobyteLiteral or TokenType.KibibyteLiteral or
            TokenType.MegabyteLiteral or TokenType.MebibyteLiteral or
            TokenType.GigabyteLiteral or TokenType.GibibyteLiteral => "ByteSize",

            // Duration literals (all map to Duration type)
            TokenType.WeekLiteral or TokenType.DayLiteral or
            TokenType.HourLiteral or TokenType.MinuteLiteral or
            TokenType.SecondLiteral or TokenType.MillisecondLiteral or
            TokenType.MicrosecondLiteral or TokenType.NanosecondLiteral => "Duration",

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
            ReportError(
                SemanticDiagnosticCode.UnknownLiteralType,
                $"Unknown literal type '{literal.LiteralType}'.",
                literal.Location);
            return ErrorTypeInfo.Instance;
        }

        // Contextual type inference for unsuffixed integer literals
        // If expected type is a fixed-width integer type and literal is Integer or S64 (default unsuffixed),
        // infer to expected type
        if (expectedType != null &&
            (literal.LiteralType == TokenType.Integer || literal.LiteralType == TokenType.S64Literal) &&
            IsFixedWidthIntegerType(expectedType))
        {
            // Check if the literal value fits in the expected type
            if (LiteralFitsInType(literal, expectedType))
            {
                typeName = expectedType.Name;
            }
            else
            {
                string range = GetIntegerTypeRange(expectedType.Name);
                ReportError(
                    SemanticDiagnosticCode.IntegerLiteralOverflow,
                    $"Integer literal '{literal.Value}' overflows type '{expectedType.Name}'. Valid range: {range}.",
                    literal.Location);
                return ErrorTypeInfo.Instance;
            }
        }

        // Parse and validate deferred numeric types using native libraries
        if (literal.Value is string rawValue)
        {
            ParsedLiteral? parsed = ParseDeferredLiteral(literal: literal, rawValue: rawValue, resolvedTypeName: typeName);
            if (parsed != null)
            {
                _parsedLiterals[literal.Location] = parsed;
            }
        }

        TypeSymbol? type = LookupTypeWithImports(name: typeName);
        if (type == null)
        {
            ReportError(
                SemanticDiagnosticCode.LiteralTypeNotDefined,
                $"Type '{typeName}' is not defined.",
                literal.Location);
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
        return type.Name is "S8" or "S16" or "S32" or "S64" or "S128"
            or "U8" or "U16" or "U32" or "U64" or "U128"
            or "Address";
    }

    private static string GetIntegerTypeRange(string typeName) => typeName switch
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
            string cleaned = strValue.Replace("_", "");
            if (!long.TryParse(cleaned, out value))
            {
                // Value doesn't fit in long — only S128/U128 could hold it
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
    private ParsedLiteral? ParseDeferredLiteral(LiteralExpression literal, string rawValue, string resolvedTypeName)
    {
        try
        {
            return literal.LiteralType switch
            {
                // Fixed-width signed integers
                TokenType.S8Literal => ParseSignedIntLiteral(literal, rawValue, "S8", sbyte.MinValue, sbyte.MaxValue),
                TokenType.S16Literal => ParseSignedIntLiteral(literal, rawValue, "S16", short.MinValue, short.MaxValue),
                TokenType.S32Literal => ParseSignedIntLiteral(literal, rawValue, "S32", int.MinValue, int.MaxValue),
                TokenType.S64Literal => ParseIntegerByResolvedType(literal, rawValue, resolvedTypeName),
                TokenType.S128Literal => ParseS128Literal(literal, rawValue),
                // Fixed-width unsigned integers
                TokenType.U8Literal => ParseUnsignedIntLiteral(literal, rawValue, "U8", byte.MaxValue),
                TokenType.U16Literal => ParseUnsignedIntLiteral(literal, rawValue, "U16", ushort.MaxValue),
                TokenType.U32Literal => ParseUnsignedIntLiteral(literal, rawValue, "U32", uint.MaxValue),
                TokenType.U64Literal => ParseUnsignedIntLiteral(literal, rawValue, "U64", ulong.MaxValue),
                TokenType.U128Literal => ParseU128Literal(literal, rawValue),
                TokenType.AddressLiteral => ParseUnsignedIntLiteral(literal, rawValue, "Address", ulong.MaxValue, "addr"),

                // Fixed-width floats (F16, F32, F64 use .NET native types; F128 uses native library)
                TokenType.F16Literal => ParseF16Literal(literal, rawValue),
                TokenType.F32Literal => ParseF32Literal(literal, rawValue),
                TokenType.F64Literal => ParseF64Literal(literal, rawValue),
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
                    : ParseIntegerByResolvedType(literal, rawValue, resolvedTypeName),
                TokenType.Decimal => _registry.Language == Language.Suflae
                    ? ParseDecimalLiteral(literal: literal, rawValue: rawValue)
                    : ParseF64Literal(literal, rawValue),

                // Imaginary literals for complex numbers
                TokenType.J32Literal => ParseJ32Literal(literal, rawValue),
                TokenType.J64Literal => ParseJ64Literal(literal, rawValue),
                TokenType.J128Literal => ParseJ128Literal(literal, rawValue),
                TokenType.JnLiteral => ParseJnLiteral(literal, rawValue),

                // Duration literals
                TokenType.NanosecondLiteral => ParseDurationLiteral(literal, rawValue, "ns", 1L),
                TokenType.MicrosecondLiteral => ParseDurationLiteral(literal, rawValue, "us", 1_000L),
                TokenType.MillisecondLiteral => ParseDurationLiteral(literal, rawValue, "ms", 1_000_000L),
                TokenType.SecondLiteral => ParseDurationLiteral(literal, rawValue, "s", 1_000_000_000L),
                TokenType.MinuteLiteral => ParseDurationLiteral(literal, rawValue, "m", 60_000_000_000L),
                TokenType.HourLiteral => ParseDurationLiteral(literal, rawValue, "h", 3_600_000_000_000L),
                TokenType.DayLiteral => ParseDurationLiteral(literal, rawValue, "d", 86_400_000_000_000L),
                TokenType.WeekLiteral => ParseDurationLiteral(literal, rawValue, "w", 604_800_000_000_000L),

                // byte size literals
                TokenType.ByteLiteral => ParseByteSizeLiteral(literal, rawValue, "b", 1UL),
                TokenType.KilobyteLiteral => ParseByteSizeLiteral(literal, rawValue, "kb", 1_000UL),
                TokenType.KibibyteLiteral => ParseByteSizeLiteral(literal, rawValue, "kib", 1_024UL),
                TokenType.MegabyteLiteral => ParseByteSizeLiteral(literal, rawValue, "mb", 1_000_000UL),
                TokenType.MebibyteLiteral => ParseByteSizeLiteral(literal, rawValue, "mib", 1_048_576UL),
                TokenType.GigabyteLiteral => ParseByteSizeLiteral(literal, rawValue, "gb", 1_000_000_000UL),
                TokenType.GibibyteLiteral => ParseByteSizeLiteral(literal, rawValue, "gib", 1_073_741_824UL),

                _ => null
            };
        }
        catch (Exception ex)
        {
            ReportError(SemanticDiagnosticCode.NumericLiteralParseFailed, $"Failed to parse numeric literal '{rawValue}': {ex.Message}", literal.Location);
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
            ReportError(SemanticDiagnosticCode.InvalidIntegerLiteral, $"Invalid Integer literal: '{rawValue}'", literal.Location);
            return new ParsedInteger(Location: literal.Location, Limbs: [], Sign: 0, Exponent: 0);
        }

        return new ParsedInteger(Location: literal.Location, Limbs: bytes, Sign: sign, Exponent: 0);
    }

    private ParsedLiteral ParseDecimalLiteral(LiteralExpression literal, string rawValue)
    {
        (string value, int sign, int exponent, int significantDigits, bool isInteger) =
            NumericLiteralParser.ParseDecimalInfo(str: rawValue);

        if (string.IsNullOrEmpty(value: value))
        {
            ReportError(SemanticDiagnosticCode.InvalidDecimalLiteral, $"Invalid Decimal literal: '{rawValue}'", literal.Location);
            return new ParsedDecimal(Location: literal.Location, StringValue: rawValue, Sign: 0, Exponent: 0, SignificantDigits: 0, IsInteger: false);
        }

        return new ParsedDecimal(Location: literal.Location, StringValue: value, Sign: sign, Exponent: exponent, SignificantDigits: significantDigits, IsInteger: isInteger);
    }

    #region Fixed-Width Numeric Literal Parsing

    /// <summary>
    /// Parses a signed integer literal (S8-S64) with overflow validation.
    /// </summary>
    private ParsedLiteral? ParseSignedIntLiteral(LiteralExpression literal, string rawValue, string typeName, long minValue, long maxValue)
    {
        // Extract numeric part by removing the type suffix (e.g., "1s32" -> "1")
        string numericPart = ExtractNumericPart(rawValue, typeName.ToLowerInvariant());
        string cleanedValue = CleanNumericLiteral(numericPart);

        if (!TryParseSignedInteger(cleanedValue, out long value))
        {
            ReportError(SemanticDiagnosticCode.InvalidIntegerLiteral, $"Invalid {typeName} literal: '{rawValue}'", literal.Location);
            return null;
        }

        if (value >= minValue && value <= maxValue)
        {
            return new ParsedSignedInt(Location: literal.Location,
                TypeName: typeName,
                Value: value);
        }

        ReportError(SemanticDiagnosticCode.IntegerLiteralOverflow,
            $"{typeName} literal '{rawValue}' overflows. Valid range: {minValue} to {maxValue}.",
            literal.Location);
        return null;
    }

    /// <summary>
    /// Parses an S128 literal with Int128 overflow validation.
    /// </summary>
    private ParsedLiteral? ParseS128Literal(LiteralExpression literal, string rawValue)
    {
        string numericPart = ExtractNumericPart(rawValue, "s128");
        string cleanedValue = CleanNumericLiteral(numericPart);

        if (Int128.TryParse(cleanedValue, out Int128 value))
        {
            return new ParsedSignedInt(Location: literal.Location, TypeName: "S128", Value: value);
        }

        ReportError(SemanticDiagnosticCode.InvalidIntegerLiteral, $"Invalid S128 literal: '{rawValue}'", literal.Location);
        return null;
    }

    /// <summary>
    /// Routes unsuffixed integer literal parsing based on the contextually resolved type name.
    /// </summary>
    private ParsedLiteral? ParseIntegerByResolvedType(LiteralExpression literal, string rawValue, string resolvedTypeName)
    {
        return resolvedTypeName switch
        {
            "S8" => ParseSignedIntLiteral(literal, rawValue, "S8", sbyte.MinValue, sbyte.MaxValue),
            "S16" => ParseSignedIntLiteral(literal, rawValue, "S16", short.MinValue, short.MaxValue),
            "S32" => ParseSignedIntLiteral(literal, rawValue, "S32", int.MinValue, int.MaxValue),
            "S128" => ParseS128Literal(literal, rawValue),
            "U8" => ParseUnsignedIntLiteral(literal, rawValue, "U8", byte.MaxValue),
            "U16" => ParseUnsignedIntLiteral(literal, rawValue, "U16", ushort.MaxValue),
            "U32" => ParseUnsignedIntLiteral(literal, rawValue, "U32", uint.MaxValue),
            "U64" => ParseUnsignedIntLiteral(literal, rawValue, "U64", ulong.MaxValue),
            "U128" => ParseU128Literal(literal, rawValue),
            "Address" => ParseUnsignedIntLiteral(literal, rawValue, "Address", ulong.MaxValue, "addr"),
            _ => ParseSignedIntLiteral(literal, rawValue, "S64", long.MinValue, long.MaxValue),
        };
    }

    /// <summary>
    /// Parses an unsigned integer literal (U8-U64, Address) with overflow validation.
    /// </summary>
    private ParsedLiteral? ParseUnsignedIntLiteral(LiteralExpression literal, string rawValue, string typeName, ulong maxValue, string? suffix = null)
    {
        // Extract numeric part by removing the type suffix (e.g., "1u32" -> "1")
        string numericPart = ExtractNumericPart(rawValue, suffix ?? typeName.ToLowerInvariant());
        string cleanedValue = CleanNumericLiteral(numericPart);

        if (!TryParseUnsignedInteger(cleanedValue, out ulong value))
        {
            ReportError(SemanticDiagnosticCode.InvalidIntegerLiteral, $"Invalid {typeName} literal: '{rawValue}'", literal.Location);
            return null;
        }

        if (value <= maxValue)
        {
            return new ParsedUnsignedInt(Location: literal.Location,
                TypeName: typeName,
                Value: (UInt128)value);
        }

        ReportError(SemanticDiagnosticCode.IntegerLiteralOverflow,
            $"{typeName} literal '{rawValue}' overflows. Valid range: 0 to {maxValue}.",
            literal.Location);
        return null;
    }

    /// <summary>
    /// Parses a U128 literal with UInt128 overflow validation.
    /// </summary>
    private ParsedLiteral? ParseU128Literal(LiteralExpression literal, string rawValue)
    {
        string numericPart = ExtractNumericPart(rawValue, "u128");
        string cleanedValue = CleanNumericLiteral(numericPart);

        if (UInt128.TryParse(cleanedValue, out UInt128 value))
        {
            return new ParsedUnsignedInt(Location: literal.Location,
                TypeName: "U128",
                Value: value);
        }

        ReportError(SemanticDiagnosticCode.InvalidIntegerLiteral, $"Invalid U128 literal: '{rawValue}'", literal.Location);
        return null;
    }

    /// <summary>
    /// Parses an F16 (half-precision) floating-point literal using .NET Half type.
    /// </summary>
    private ParsedLiteral? ParseF16Literal(LiteralExpression literal, string rawValue)
    {
        string numericPart = ExtractNumericPart(rawValue, "f16");
        string cleanedValue = CleanNumericLiteral(numericPart);

        Half value;
        if (TryParseHexFloat(cleanedValue, out double hexVal))
            value = (Half)hexVal;
        else if (!Half.TryParse(cleanedValue, out value))
        {
            ReportError(SemanticDiagnosticCode.NumericLiteralParseFailed, $"Invalid F16 literal: '{rawValue}'", literal.Location);
            return null;
        }

        if (!Half.IsInfinity(value))
        {
            return new ParsedFloat(Location: literal.Location,
                TypeName: "F16",
                Value: (double)value);
        }

        ReportError(SemanticDiagnosticCode.FloatLiteralOverflow,
            $"F16 literal '{rawValue}' overflows the representable range.",
            literal.Location);
        return null;
    }

    /// <summary>
    /// Parses an F32 (single-precision) floating-point literal using .NET float type.
    /// </summary>
    private ParsedLiteral? ParseF32Literal(LiteralExpression literal, string rawValue)
    {
        string numericPart = ExtractNumericPart(rawValue, "f32");
        string cleanedValue = CleanNumericLiteral(numericPart);

        float value;
        if (TryParseHexFloat(cleanedValue, out double hexVal32))
            value = (float)hexVal32;
        else if (!float.TryParse(cleanedValue, out value))
        {
            ReportError(SemanticDiagnosticCode.NumericLiteralParseFailed, $"Invalid F32 literal: '{rawValue}'", literal.Location);
            return null;
        }

        if (!float.IsInfinity(value))
        {
            return new ParsedFloat(Location: literal.Location, TypeName: "F32", Value: value);
        }

        ReportError(SemanticDiagnosticCode.FloatLiteralOverflow,
            $"F32 literal '{rawValue}' overflows the representable range.",
            literal.Location);
        return null;
    }

    /// <summary>
    /// Parses an F64 (double-precision) floating-point literal using .NET double type.
    /// </summary>
    private ParsedLiteral? ParseF64Literal(LiteralExpression literal, string rawValue)
    {
        string numericPart = ExtractNumericPart(rawValue, "f64");
        string cleanedValue = CleanNumericLiteral(numericPart);

        double value;
        if (TryParseHexFloat(cleanedValue, out double hexVal64))
            value = hexVal64;
        else if (!double.TryParse(cleanedValue, out value))
        {
            ReportError(SemanticDiagnosticCode.NumericLiteralParseFailed, $"Invalid F64 literal: '{rawValue}'", literal.Location);
            return null;
        }

        if (!double.IsInfinity(value))
        {
            return new ParsedFloat(Location: literal.Location, TypeName: "F64", Value: value);
        }

        ReportError(SemanticDiagnosticCode.FloatLiteralOverflow,
            $"F64 literal '{rawValue}' overflows the representable range.",
            literal.Location);
        return null;
    }

    #endregion

    #region Duration Literal Parsing

    /// <summary>
    /// Parses a duration literal and converts to nanoseconds.
    /// </summary>
    private ParsedLiteral? ParseDurationLiteral(LiteralExpression literal, string rawValue, string unit, long multiplier)
    {
        // Extract numeric part (remove unit suffix)
        string numericPart = ExtractNumericPart(rawValue, unit);
        string cleanedValue = CleanNumericLiteral(numericPart);

        if (!TryParseSignedInteger(cleanedValue, out long value))
        {
            ReportError(SemanticDiagnosticCode.NumericLiteralParseFailed, $"Invalid duration literal: '{rawValue}'", literal.Location);
            return null;
        }

        // Check for overflow when multiplying
        try
        {
            checked
            {
                long nanoseconds = value * multiplier;
                return new ParsedDuration(Location: literal.Location, Nanoseconds: nanoseconds, OriginalUnit: unit);
            }
        }
        catch (OverflowException)
        {
            ReportError(SemanticDiagnosticCode.DurationLiteralOverflow,
                $"Duration literal '{rawValue}' overflows the maximum representable duration.",
                literal.Location);
            return null;
        }
    }

    #endregion

    #region ByteSize Literal Parsing

    /// <summary>
    /// Parses a ByteSize literal and converts to ByteSize.
    /// </summary>
    private ParsedLiteral? ParseByteSizeLiteral(LiteralExpression literal, string rawValue, string unit, ulong multiplier)
    {
        // Extract numeric part (remove unit suffix)
        string numericPart = ExtractNumericPart(rawValue, unit);
        string cleanedValue = CleanNumericLiteral(numericPart);

        if (!TryParseUnsignedInteger(cleanedValue, out ulong value))
        {
            ReportError(SemanticDiagnosticCode.NumericLiteralParseFailed, $"Invalid byte size literal: '{rawValue}'", literal.Location);
            return null;
        }

        // Check for overflow when multiplying
        try
        {
            checked
            {
                ulong bytes = value * multiplier;
                return new ParsedByteSize(Location: literal.Location, Bytes: bytes, OriginalUnit: unit);
            }
        }
        catch (OverflowException)
        {
            ReportError(SemanticDiagnosticCode.ByteSizeLiteralOverflow,
                $"ByteSize literal '{rawValue}' overflows the maximum representable size.",
                literal.Location);
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
        string numericPart = RemoveImaginarySuffix(rawValue, "j32");
        string cleanedValue = CleanNumericLiteral(numericPart);

        if (float.TryParse(cleanedValue, out float value))
        {
            return new ParsedJ32(Location: literal.Location, Value: value);
        }

        ReportError(SemanticDiagnosticCode.ImaginaryLiteralParseFailed, $"Invalid J32 literal: '{rawValue}'", literal.Location);
        return null;
    }

    /// <summary>
    /// Parses a J64 (F64-based) imaginary literal.
    /// </summary>
    private ParsedLiteral? ParseJ64Literal(LiteralExpression literal, string rawValue)
    {
        // Remove 'j64' or 'j' suffix
        string numericPart = rawValue.EndsWith("j64", StringComparison.OrdinalIgnoreCase)
            ? RemoveImaginarySuffix(rawValue, "j64")
            : RemoveImaginarySuffix(rawValue, "j");
        string cleanedValue = CleanNumericLiteral(numericPart);

        if (double.TryParse(cleanedValue, out double value))
        {
            return new ParsedJ64(Location: literal.Location, Value: value);
        }

        ReportError(SemanticDiagnosticCode.ImaginaryLiteralParseFailed, $"Invalid J64 literal: '{rawValue}'", literal.Location);
        return null;
    }

    /// <summary>
    /// Parses a J128 (F128-based) imaginary literal using native library.
    /// </summary>
    private ParsedLiteral? ParseJ128Literal(LiteralExpression literal, string rawValue)
    {
        // Remove 'j128' suffix
        string numericPart = RemoveImaginarySuffix(rawValue, "j128");
        string cleanedValue = CleanNumericLiteral(numericPart);

        try
        {
            NumericLiteralParser.F128 result = NumericLiteralParser.ParseF128(str: cleanedValue);
            return new ParsedJ128(Location: literal.Location, Lo: result.Lo, Hi: result.Hi);
        }
        catch (Exception ex)
        {
            ReportError(SemanticDiagnosticCode.ImaginaryLiteralParseFailed, $"Invalid J128 literal: '{rawValue}': {ex.Message}", literal.Location);
            return null;
        }
    }

    /// <summary>
    /// Parses a Jn (arbitrary-precision Decimal-based) imaginary literal using native library.
    /// </summary>
    private ParsedLiteral? ParseJnLiteral(LiteralExpression literal, string rawValue)
    {
        // Remove 'jn' suffix
        string numericPart = RemoveImaginarySuffix(rawValue, "jn");
        string cleanedValue = CleanNumericLiteral(numericPart);

        try
        {
            (string value, int sign, int exponent, int significantDigits, bool _) =
                NumericLiteralParser.ParseDecimalInfo(str: cleanedValue);

            if (string.IsNullOrEmpty(value))
            {
                ReportError(SemanticDiagnosticCode.ImaginaryLiteralParseFailed, $"Invalid Jn literal: '{rawValue}'", literal.Location);
                return null;
            }

            return new ParsedJn(Location: literal.Location, StringValue: value, Sign: sign, Exponent: exponent, SignificantDigits: significantDigits);
        }
        catch (Exception ex)
        {
            ReportError(SemanticDiagnosticCode.ImaginaryLiteralParseFailed, $"Invalid Jn literal: '{rawValue}': {ex.Message}", literal.Location);
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
        return value.Replace("_", "");
    }

    /// <summary>
    /// Parses C99 hex float format: 0x1.ABCDp5 = (hex mantissa) × 2^(exponent).
    /// </summary>
    private static bool TryParseHexFloat(string value, out double result)
    {
        result = 0;
        if (!value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || value.Length <= 2)
            return false;

        string body = value[2..];
        int pIndex = body.IndexOfAny(['p', 'P']);
        if (pIndex < 0) return false;

        string mantissaStr = body[..pIndex];
        string exponentStr = body[(pIndex + 1)..];

        if (!int.TryParse(exponentStr, out int exponent))
            return false;

        double mantissa = 0;
        int dotIndex = mantissaStr.IndexOf('.');

        if (dotIndex >= 0)
        {
            string intPart = mantissaStr[..dotIndex];
            string fracPart = mantissaStr[(dotIndex + 1)..];

            if (intPart.Length > 0 &&
                ulong.TryParse(intPart, System.Globalization.NumberStyles.HexNumber, null, out ulong intVal))
                mantissa = intVal;

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
            if (!ulong.TryParse(mantissaStr, System.Globalization.NumberStyles.HexNumber, null, out ulong intVal))
                return false;
            mantissa = intVal;
        }

        result = Math.ScaleB(mantissa, exponent);
        return !double.IsNaN(result) && !double.IsInfinity(result);
    }

    /// <summary>
    /// Extracts the numeric part from a literal by removing the unit suffix.
    /// </summary>
    private static string ExtractNumericPart(string rawValue, string suffix)
    {
        if (rawValue.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
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
        if (rawValue.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
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
        if (string.IsNullOrEmpty(value)) return false;

        // Handle negative sign
        bool negative = value.StartsWith('-');
        string numPart = negative ? value[1..] : value;

        // Handle base prefixes
        if (numPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(numPart[2..], System.Globalization.NumberStyles.HexNumber, null, out long hexVal))
            {
                result = negative ? -hexVal : hexVal;
                return true;
            }
            return false;
        }

        if (numPart.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                long octalVal = Convert.ToInt64(numPart[2..], 8);
                result = negative ? -octalVal : octalVal;
                return true;
            }
            catch { return false; }
        }

        if (numPart.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                long binaryVal = Convert.ToInt64(numPart[2..], 2);
                result = negative ? -binaryVal : binaryVal;
                return true;
            }
            catch { return false; }
        }

        // Decimal
        return long.TryParse(value, out result);
    }

    /// <summary>
    /// Tries to parse an unsigned integer, handling hex (0x), octal (0o), and binary (0b) prefixes.
    /// </summary>
    private static bool TryParseUnsignedInteger(string value, out ulong result)
    {
        result = 0;
        if (string.IsNullOrEmpty(value)) return false;

        // Handle base prefixes
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out result);
        }

        if (value.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                result = Convert.ToUInt64(value[2..], 8);
                return true;
            }
            catch { return false; }
        }

        if (value.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                result = Convert.ToUInt64(value[2..], 2);
                return true;
            }
            catch { return false; }
        }

        // Decimal
        return ulong.TryParse(value, out result);
    }

    #endregion
}
