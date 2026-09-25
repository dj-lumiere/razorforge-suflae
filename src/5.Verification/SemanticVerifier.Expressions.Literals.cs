using System.Globalization;
using Builder.Diagnostics;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Types;
using Builder.Verification.Results;

namespace Builder.Verification;

/// <summary>
/// Phase 5: Literal expression analysis and deferred numeric parsing.
/// </summary>
public sealed partial class SemanticVerifier
{
    private const string AddressTypeName = "Address";
    private const string IntegerTypeName = "Integer";

    /// <summary>
    /// Analyze literal expression as part of this compiler phase.
    /// </summary>
    private TypeSymbol AnalyzeLiteralExpression(LiteralExpression literal,
        TypeSymbol? expectedType = null)
    {
        // `none` value literal: needs a carrier-slot expected type
        // (Maybe[T] / Lookup[T] / variant-with-None). Anything else is a hard error.
        if (literal.LiteralType == TokenType.NoneValue)
        {
            return AnalyzeNoneValueLiteral(literal: literal, expectedType: expectedType);
        }

        string? typeName = MapLiteralTypeName(literal: literal);
        if (typeName == null)
        {
            ReportError(code: SemanticDiagnosticCode.UnknownLiteralType,
                message: $"Unknown literal type '{literal.LiteralType}'.",
                location: literal.Location);
            return ErrorTypeSymbol.Instance;
        }

        typeName = ApplyContextualTypeInference(literal: literal,
            expectedType: expectedType,
            typeName: typeName,
            earlyExit: out bool earlyExitOnOverflow);
        if (earlyExitOnOverflow)
        {
            return ErrorTypeSymbol.Instance;
        }

        StoreParsedLiteral(literal: literal, typeName: typeName);

        TypeSymbol? type = LookupTypeWithImports(name: typeName);
        if (type == null)
        {
            ReportError(code: SemanticDiagnosticCode.LiteralTypeNotDefined,
                message: $"Type '{typeName}' is not defined.",
                location: literal.Location);
            return ErrorTypeSymbol.Instance;
        }

        return type;
    }

    /// <summary>
    /// Applies contextual type inference for unsuffixed integer/decimal literals when an expected type
    /// is present. Sets <paramref name="earlyExit"/> to true if an overflow error was reported.
    /// </summary>
    private string ApplyContextualTypeInference(LiteralExpression literal,
        TypeSymbol? expectedType, string typeName, out bool earlyExit)
    {
        earlyExit = false;
        if (expectedType == null)
        {
            return typeName;
        }

        if (literal.LiteralType is TokenType.UndecidedInteger &&
            IsFixedWidthIntegerType(type: expectedType))
        {
            if (LiteralFitsInType(literal: literal, targetType: expectedType))
            {
                return expectedType.Name;
            }

            string range = GetIntegerTypeRange(typeName: expectedType.Name);
            ReportError(code: SemanticDiagnosticCode.IntegerLiteralOverflow,
                message:
                $"Integer literal '{literal.Value}' overflows type '{expectedType.Name}'. Valid range: {range}.",
                location: literal.Location);
            earlyExit = true;
            return typeName;
        }

        if (literal.LiteralType is TokenType.UndecidedDecimal &&
            (IsFloatType(type: expectedType) || IsDecimalType(type: expectedType)))
        {
            return expectedType.Name;
        }

        // Width-less imaginary literal `i` conforms to a contextual complex type (C64/C128/C256/
        // Complex); otherwise it keeps its C128 default (see MapLiteralTypeName).
        if (literal.LiteralType is TokenType.ImaginaryLiteral && IsComplexType(type: expectedType))
        {
            return expectedType.Name;
        }

        // A bare real int/float literal conforms to a contextual complex type as its REAL component
        // (imag = 0). This is the RF-S767 path that makes `3 + 4i` legible: `3` becomes `C(3, 0)` and
        // `4i` becomes `C(0, 4)`, so the sum is one Complex+Complex add. LiteralLoweringPass builds the
        // pure-real constructor from the resolved complex type.
        if (literal.LiteralType is TokenType.UndecidedInteger or TokenType.UndecidedDecimal &&
            IsComplexType(type: expectedType))
        {
            return expectedType.Name;
        }

        return typeName;
    }

    /// <summary>
    /// Parses a string-valued literal and stores the result in <c>_parsedLiterals</c>.
    /// </summary>
    private void StoreParsedLiteral(LiteralExpression literal, string typeName)
    {
        if (literal.Value is not string rawValue)
        {
            return;
        }

        ParsedLiteral? parsed = ParseDeferredLiteral(literal: literal,
            rawValue: rawValue,
            resolvedTypeName: typeName);
        if (parsed != null)
        {
            _parsedLiterals[key: literal.Location] = parsed;
        }
    }

    /// <summary>
    /// Resolves the type of a bare <c>none</c> value literal against its expected carrier slot
    /// (Maybe[T] / Lookup[T] / variant-with-None, or a Suflae <c>Roamed[E]</c> optional entity slot).
    /// Reports RF-S if no valid carrier context is present.
    /// </summary>
    private TypeSymbol AnalyzeNoneValueLiteral(LiteralExpression literal, TypeSymbol? expectedType)
    {
        if (expectedType != null && IsNoneCarrierSlot(type: expectedType))
        {
            return expectedType;
        }

        // Suflae: `none` against a `Roamed[E]` slot (an OPTIONAL entity reference `x: E?`) is a null
        // Roamed handle (roamed_none). Entity references carry their own none via a null pointer, so
        // no Maybe carrier is needed.
        if (_registry.Language == Language.Suflae && expectedType is RecordTypeSymbol
            {
                GenericDefinition.Name: Declaration.RuntimeContract.Roamed
            })
        {
            return expectedType;
        }

        ReportError(code: SemanticDiagnosticCode.NoneOutsideCarrierSlot,
            message:
            $"'none' is only valid where the expected type is Maybe[T], Lookup[T], or a variant with a None arm; got {expectedType?.Name ?? "no contextual type"}.",
            location: literal.Location);
        return ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// Maps a literal's token type to its resolved type name (PascalCase), or null for an unknown
    /// literal type. Unsuffixed integer/decimal literals default to S64/B64 (RF) or Integer/Decimal
    /// (Suflae) per the literal's own source-file language.
    /// </summary>
    private string? MapLiteralTypeName(LiteralExpression literal)
    {
        return literal.LiteralType switch
        {
            // Signed integers
            TokenType.S8Literal => "S8",
            TokenType.S16Literal => "S16",
            TokenType.S32Literal => "S32",
            TokenType.S64Literal => "S64",
            TokenType.S128Literal => "S128",
            TokenType.S256Literal => "S256",
            // Unsigned integers
            TokenType.U8Literal => "U8",
            TokenType.U16Literal => "U16",
            TokenType.U32Literal => "U32",
            TokenType.U64Literal => "U64",
            TokenType.U128Literal => "U128",
            TokenType.U256Literal => "U256",
            TokenType.AddressLiteral => AddressTypeName,

            // Floating-point
            TokenType.B16Literal => "B16",
            TokenType.B32Literal => "B32",
            TokenType.B64Literal => "B64",
            TokenType.B128Literal => "B128",

            // Decimal floating-point
            TokenType.D32Literal => "D32",
            TokenType.D64Literal => "D64",
            TokenType.D128Literal => "D128",

            // Unsuffixed literals: type inference resolves these; fallback is S64/B64 (RF) or Integer/Decimal
            // (Suflae). CRITICAL: the Suflae default applies ONLY to Suflae SOURCE. The RF stdlib is shared
            // by SF ("SF's Core IS RF's Core") and its bodies get (re-)analyzed under an SF compile (generic
            // monomorphization / variant-body collection) OUTSIDE the AnalyzeStdlibBodies RF-mode override —
            // there `_registry.Language` is Suflae. A stdlib `int_eq[U256](b: 0)` must keep RF's S64 default
            // (coerces to the U256/i256 compare); the SF Integer default is a HEAP RECORD that can't coerce
            // into a scalar op → `store %Record.Numerics.Integer` / `icmp i256, %Record` type errors. Key on
            // the LITERAL's own file (its Location), NOT `_currentFilePath` (stale = the user entry under
            // cross-module body analysis).
            TokenType.UndecidedInteger => UsesSuflaeNumericDefaults(literal: literal)
                ? IntegerTypeName
                : "S64",
            // A hex float (0x1.8p3) is binary-only, so it defaults to B64 in Suflae too.
            TokenType.UndecidedDecimal => UsesSuflaeNumericDefaults(literal: literal) &&
                                          !(literal.Value is string raw &&
                                            NumericLiteralParser.IsHexFloatText(text: raw))
                ? "Decimal"
                : "B64",

            // Explicit arbitrary-precision suffix (n): always Integer or Decimal
            TokenType.IntegerLiteral => IntegerTypeName,
            TokenType.DecimalLiteral => "Decimal",

            // Boolean
            TokenType.True or TokenType.False => "Bool",

            // Text and characters — a raw string `r"..."` (RawText) is a Text too; only the escape
            // processing differed at tokenize time, the resulting value is a plain Text.
            TokenType.TextLiteral or TokenType.RawText => "Text",
            TokenType.BytesLiteral => "Bytes",
            TokenType.BytesRawLiteral => "Bytes",
            TokenType.ByteLetterLiteral => "Byte",
            TokenType.CharacterLiteral => "Character",

            // byte size literals (all map to ByteSize type)
            TokenType.ByteLiteral or TokenType.KilobyteLiteral or TokenType.KibibyteLiteral
                or TokenType.MegabyteLiteral or TokenType.MebibyteLiteral
                or TokenType.GigabyteLiteral or TokenType.GibibyteLiteral => "ByteSize",

            // Duration literals (all map to Duration type)
            TokenType.WeekLiteral or TokenType.DayLiteral or TokenType.HourLiteral
                or TokenType.MinuteLiteral or TokenType.SecondLiteral
                or TokenType.MillisecondLiteral or TokenType.MicrosecondLiteral
                or TokenType.NanosecondLiteral => "Duration",

            // Imaginary literal: width-less `i`. Default (no contextual complex type) is C128
            // (2×B64, double-precision), mirroring the B64 default for bare float literals.
            // ApplyContextualTypeInference adapts it to a C64/C256/Complex expected type.
            TokenType.ImaginaryLiteral => "C128",

            // Unknown literal type - error
            _ => null
        };
    }

    /// <summary>
    /// Checks if a type is a fixed-width integer type (S8-S128, U8-U128, Address).
    /// Uses protocol conformance: fixed-width integer types obey <c>FixedIntegral</c>.
    /// </summary>
    private bool IsFixedWidthIntegerType(TypeSymbol type)
    {
        return ImplementsProtocol(type: type, protocolName: "FixedIntegral");
    }

    /// <summary>
    /// The unsuffixed-literal default (Integer/Decimal vs S64/B64) follows the literal's SOURCE-FILE
    /// LANGUAGE, not the compile's language and not stdlib-ness: a <c>.sf</c> file uses Suflae defaults,
    /// a <c>.rf</c> file uses RF defaults. This is correct for every mix — RF/SF user code, the shared RF
    /// stdlib (<c>.rf</c>) borrowed by an SF compile (must keep S64/B64 so `int_eq[U256](b: 0)` stays a
    /// coercible scalar, not a heap Integer record), AND a future dedicated Suflae stdlib (<c>.sf</c>),
    /// which SHOULD get Suflae defaults. (Keying on <c>_registry.Language</c> wrongly gave RF stdlib bodies
    /// the SF default when re-analyzed under an SF compile; keying on stdlib-directory membership would
    /// wrongly force RF defaults onto a future <c>.sf</c> stdlib.) Uses the literal's OWN file (its
    /// Location) — <c>_currentFilePath</c> is the user entry when a cross-module stdlib body is
    /// (re-)analyzed during monomorphization, so it is unreliable here.
    /// </summary>
    private bool UsesSuflaeNumericDefaults(LiteralExpression literal)
    {
        string? litFile = literal.Location.FileName;
        string probeFile = string.IsNullOrEmpty(value: litFile)
            ? _currentFilePath ?? ""
            : litFile;
        return probeFile.EndsWith(value: ".sf",
            comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the integer type range needed by this compiler phase.
    /// </summary>
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
        // String-form literals whose magnitude doesn't fit in 64 bits can only fit in S128/U128.
        if (literal.Value is string strVal)
        {
            if (!TryExtractStringLiteralMagnitude(strValue: strVal,
                    negative: out bool neg64,
                    magnitude: out ulong mag64))
            {
                return targetType.Name is "S128" or "U128";
            }

            return MagnitudeFitsInType(targetType: targetType, negative: neg64, magnitude: mag64);
        }

        if (!TryExtractLiteralMagnitudeAndSign(literal: literal,
                negative: out bool negative,
                magnitude: out ulong magnitude))
        {
            return false;
        }

        return MagnitudeFitsInType(targetType: targetType,
            negative: negative,
            magnitude: magnitude);
    }

    /// <summary>
    /// Extracts the sign and 64-bit magnitude from a literal's stored value (long or string form).
    /// Returns false for unrecognised value shapes or when the string form's magnitude exceeds 64 bits
    /// and the target would need to be checked separately (caller handles that case via the string path).
    /// </summary>
    private static bool TryExtractLiteralMagnitudeAndSign(LiteralExpression literal,
        out bool negative, out ulong magnitude)
    {
        switch (literal.Value)
        {
            case long longValue:
                negative = longValue < 0;
                magnitude = negative
                    ? (ulong)-(longValue + 1) + 1 // two's-complement-safe |long.MinValue|
                    : (ulong)longValue;
                return true;

            case string strValue:
                return TryExtractStringLiteralMagnitude(strValue: strValue,
                    negative: out negative,
                    magnitude: out magnitude);

            default:
                negative = false;
                magnitude = 0;
                return false;
        }
    }

    /// <summary>
    /// Strips any type suffix and underscore separators from a string literal value, then parses the
    /// sign and 64-bit magnitude. Returns false when the magnitude does not fit in 64 bits
    /// (wide literals — only S128/U128 could hold them; callers handle that).
    /// </summary>
    private static bool TryExtractStringLiteralMagnitude(string strValue, out bool negative,
        out ulong magnitude)
    {
        // Strip type suffix before removing digit-separator underscores.
        // e.g. "20_s64" -> strip "_s64" -> "20" -> parses fine.
        // A suffix starts at the last '_' when what follows is a letter.
        int lastUnderscore = strValue.LastIndexOf(value: '_');
        string withoutSuffix = lastUnderscore >= 0 && lastUnderscore < strValue.Length - 1 &&
                               char.IsLetter(c: strValue[index: lastUnderscore + 1])
            ? strValue[..lastUnderscore]
            : strValue;
        string cleaned = withoutSuffix.Replace(oldValue: "_", newValue: "");
        negative = cleaned.StartsWith(value: '-');
        string digits = negative
            ? cleaned[1..]
            : cleaned;
        if (!TryParseLiteralMagnitude(digits: digits, magnitude: out magnitude))
        {
            // Magnitude doesn't fit in 64 bits — only S128/U128 could hold it, but we can't
            // determine that here; callers that need the wide-literal check do so themselves.
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns whether the given sign + 64-bit magnitude fits in the named target type.
    /// </summary>
    private static bool MagnitudeFitsInType(TypeSymbol targetType, bool negative, ulong magnitude)
    {
        return targetType.Name switch
        {
            "S8" => negative
                ? magnitude <= 128UL
                : magnitude <= 127UL,
            "S16" => negative
                ? magnitude <= 32768UL
                : magnitude <= 32767UL,
            "S32" => negative
                ? magnitude <= 2147483648UL
                : magnitude <= 2147483647UL,
            "S64" => negative
                ? magnitude <= 9223372036854775808UL
                : magnitude <= 9223372036854775807UL,
            "S128" => true, // Any 64-bit magnitude fits in S128
            "S256" => true, // Any 64-bit magnitude fits in S256
            "U8" => !negative && magnitude <= byte.MaxValue,
            "U16" => !negative && magnitude <= ushort.MaxValue,
            "U32" => !negative && magnitude <= uint.MaxValue,
            "U64" => !negative, // Any non-negative 64-bit magnitude fits in U64
            "U128" => !negative,
            "U256" => !negative, // Any non-negative 64-bit magnitude fits in U256
            AddressTypeName => true, // System-dependent, allow for now
            _ => false
        };
    }

    /// <summary>
    /// Parses the magnitude digits of an integer literal, honoring base prefixes
    /// (<c>0x</c> hex, <c>0b</c> binary, <c>0o</c> octal). Plain <c>long.TryParse</c>
    /// rejects "0xFF", which used to make the contextual range check report a bogus
    /// "overflows type" error for every base-prefixed literal.
    /// Returns false when the magnitude does not fit in 64 bits (or is malformed).
    /// </summary>
    private static bool TryParseLiteralMagnitude(string digits, out ulong magnitude)
    {
        if (digits.StartsWith(value: "0x", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(s: digits[2..],
                style: NumberStyles.HexNumber,
                provider: CultureInfo.InvariantCulture,
                result: out magnitude);
        }

        if (digits.StartsWith(value: "0b", comparisonType: StringComparison.OrdinalIgnoreCase) ||
            digits.StartsWith(value: "0o", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            int numericBase = char.ToLowerInvariant(c: digits[index: 1]) == 'b'
                ? 2
                : 8;
            try
            {
                magnitude = Convert.ToUInt64(value: digits[2..], fromBase: numericBase);
                return true;
            }
            catch (Exception ex) when (ex is FormatException or OverflowException
                                           or ArgumentOutOfRangeException)
            {
                magnitude = 0;
                return false;
            }
        }

        return ulong.TryParse(s: digits, result: out magnitude);
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
        // A hex float literal (0x1.8p3) is binary-only and exact, whatever its suffix or context.
        if (NumericLiteralParser.IsHexFloatText(text: rawValue))
        {
            return ParseHexFloatLiteral(literal: literal,
                rawValue: rawValue,
                resolvedTypeName: resolvedTypeName);
        }

        // A bare int/float literal SA promoted to a complex type (the real component of `3 + 4i`) is
        // already valid as a scalar from its first analysis; LiteralLoweringPass rebuilds the complex
        // constructor from the raw text, so no parsed-value cache entry is needed. Route it away from
        // ParseIntegerByResolvedType/ParseDecimalByResolvedType, which only know scalar numeric types.
        if (resolvedTypeName is "C64" or "C128" or "C256" or "Complex" &&
            literal.LiteralType is TokenType.UndecidedInteger or TokenType.UndecidedDecimal)
        {
            return null;
        }

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
                TokenType.S256Literal => ParseS256Literal(literal: literal, rawValue: rawValue),
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
                TokenType.U256Literal => ParseU256Literal(literal: literal, rawValue: rawValue),
                TokenType.AddressLiteral => ParseUnsignedIntLiteral(literal: literal,
                    rawValue: rawValue,
                    typeName: AddressTypeName,
                    maxValue: ulong.MaxValue,
                    suffix: "addr"),

                // Fixed-width floats (B16, B32, B64 use .NET native types; B128 uses native library)
                TokenType.B16Literal => ParseB16Literal(literal: literal, rawValue: rawValue),
                TokenType.B32Literal => ParseB32Literal(literal: literal, rawValue: rawValue),
                TokenType.B64Literal => ParseB64Literal(literal: literal, rawValue: rawValue),
                TokenType.B128Literal => ParseB128Literal(literal: literal, rawValue: rawValue),

                // Decimal floating-point (all use native library)
                TokenType.D32Literal => ParseD32Literal(literal: literal, rawValue: rawValue),
                TokenType.D64Literal => ParseD64Literal(literal: literal, rawValue: rawValue),
                TokenType.D128Literal => ParseD128Literal(literal: literal, rawValue: rawValue),

                // Explicit arbitrary-precision suffix (n): always parse as Integer/Decimal
                TokenType.IntegerLiteral => ParseIntegerLiteral(literal: literal,
                    rawValue: rawValue),
                TokenType.DecimalLiteral => ParseDecimalLiteral(literal: literal,
                    rawValue: rawValue),

                // Unsuffixed literals: route through resolved type (contextual inference may have changed it)
                TokenType.UndecidedInteger => resolvedTypeName == IntegerTypeName
                    ? ParseIntegerLiteral(literal: literal, rawValue: rawValue)
                    : ParseIntegerByResolvedType(literal: literal,
                        rawValue: rawValue,
                        resolvedTypeName: resolvedTypeName),
                TokenType.UndecidedDecimal => ParseDecimalByResolvedType(literal: literal,
                    rawValue: rawValue,
                    resolvedTypeName: resolvedTypeName),

                // Imaginary literal (width-less `i`): validated against the resolved complex type's
                // component domain (C64→B32, C128→B64, C256→B128, Complex→arbitrary).
                TokenType.ImaginaryLiteral => ParseImaginaryLiteral(literal: literal,
                    rawValue: rawValue,
                    resolvedTypeName: resolvedTypeName),

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

    /// <summary>
    /// Checks and encodes a hexadecimal float literal (<c>0x1.8p3</c>). Only a binary float takes one
    /// (B16/B32/B64/B128, or a C64/C128/C256 component), and its value must fit that format exactly.
    /// Returns null for a complex target, whose constructor LiteralLoweringPass rebuilds from the text.
    /// </summary>
    private ParsedLiteral? ParseHexFloatLiteral(LiteralExpression literal, string rawValue,
        string resolvedTypeName)
    {
        string formatName = resolvedTypeName switch
        {
            "C64" => "B32",
            "C128" => "B64",
            "C256" => "B128",
            _ => resolvedTypeName
        };
        (int MantBits, int ExpBits)? format = formatName switch
        {
            "B16" => (10, 5),
            "B32" => (23, 8),
            "B64" => (52, 11),
            "B128" => (112, 15),
            _ => null
        };
        if (format is not { } fmt)
        {
            ReportError(code: SemanticDiagnosticCode.HexFloatLiteralNotBinaryFloat,
                message:
                $"The hex float literal '{rawValue}' is used as {resolvedTypeName} here, but a hex float spells a power-of-two value exactly, so only B16, B32, B64 and B128 take one. Write the value in decimal, or give it a binary float type (for example '{StripHexFloatSuffix(rawValue: rawValue)}_b64').",
                location: literal.Location);
            return null;
        }

        string cleaned = CleanNumericLiteral(value: StripHexFloatSuffix(rawValue: rawValue));
        switch (NumericLiteralParser.TryEncodeHexFloat(cleaned: cleaned,
                    mantBits: fmt.MantBits,
                    expBits: fmt.ExpBits,
                    bits: out UInt128 bits))
        {
            case NumericLiteralParser.HexFloatStatus.Malformed:
                ReportError(code: SemanticDiagnosticCode.NumericLiteralParseFailed,
                    message:
                    $"'{rawValue}' is not a well-formed hex float literal. Write 0x, hex digits with an optional '.' fraction, then 'p' and a decimal power of two, as in 0x1.8p3.",
                    location: literal.Location);
                return null;
            case NumericLiteralParser.HexFloatStatus.Overflow:
                ReportError(code: SemanticDiagnosticCode.FloatLiteralOverflow,
                    message: $"{formatName} literal '{rawValue}' overflows the representable range.",
                    location: literal.Location);
                return null;
            case NumericLiteralParser.HexFloatStatus.Inexact:
                ReportError(code: SemanticDiagnosticCode.HexFloatLiteralInexact,
                    message:
                    $"The hex float literal '{rawValue}' has more significant bits than {formatName}'s {fmt.MantBits + 1}, so it would not be stored exactly. Drop the extra digits, or use a wider binary float.",
                    location: literal.Location);
                return null;
            case NumericLiteralParser.HexFloatStatus.BelowSubnormal:
                ReportError(code: SemanticDiagnosticCode.HexFloatLiteralInexact,
                    message:
                    $"The hex float literal '{rawValue}' has a bit below {formatName}'s smallest subnormal, 0x1p{2 - (1 << fmt.ExpBits - 1) - fmt.MantBits}, so it would not be stored exactly. Use a wider binary float.",
                    location: literal.Location);
                return null;
        }

        return formatName != resolvedTypeName
            ? null
            : formatName switch
            {
                "B16" => new ParsedFloat(Location: literal.Location,
                    TypeName: "B16",
                    Value: (double)BitConverter.UInt16BitsToHalf(value: (ushort)bits)),
                "B32" => new ParsedFloat(Location: literal.Location,
                    TypeName: "B32",
                    Value: BitConverter.UInt32BitsToSingle(value: (uint)bits)),
                "B64" => new ParsedFloat(Location: literal.Location,
                    TypeName: "B64",
                    Value: BitConverter.UInt64BitsToDouble(value: (ulong)bits)),
                _ => new ParsedB128(Location: literal.Location, Lo: (ulong)bits, Hi: (ulong)(bits >> 64))
            };
    }

    /// <summary>
    /// Drops a hex float literal's type suffix (<c>_b32</c>, <c>b64</c>, <c>i</c>, …): the literal
    /// body ends with the decimal digits of its <c>p</c> exponent, and the suffix is whatever follows.
    /// </summary>
    internal static string StripHexFloatSuffix(string rawValue)
    {
        int end = rawValue.IndexOfAny(anyOf: ['p', 'P']) + 1;
        if (end == 0)
        {
            return rawValue;
        }

        if (end < rawValue.Length && rawValue[index: end] is '+' or '-')
        {
            end++;
        }

        while (end < rawValue.Length && char.IsDigit(c: rawValue[index: end]))
        {
            end++;
        }

        return rawValue[..end];
    }

    /// <summary>
    /// Parse b128 literal as part of this compiler phase.
    /// </summary>
    private static ParsedB128 ParseB128Literal(LiteralExpression literal, string rawValue)
    {
        if (rawValue == "inf")
        {
            return new ParsedB128(Location: literal.Location, Lo: 0UL, Hi: 0x7FFF000000000000UL);
        }

        if (rawValue == "nan")
        {
            return new ParsedB128(Location: literal.Location, Lo: 0UL, Hi: 0x7FFF800000000000UL);
        }

        NumericLiteralParser.B128 result = NumericLiteralParser.EncodeB128(str: rawValue);
        return new ParsedB128(Location: literal.Location, Lo: result.Lo, Hi: result.Hi);
    }

    /// <summary>
    /// Parse d32 literal as part of this compiler phase.
    /// </summary>
    private static ParsedD32 ParseD32Literal(LiteralExpression literal, string rawValue)
    {
        if (rawValue == "inf")
        {
            return new ParsedD32(Location: literal.Location, Value: 0x78000000U);
        }

        if (rawValue == "nan")
        {
            return new ParsedD32(Location: literal.Location, Value: 0x7C000000U);
        }

        NumericLiteralParser.D32 result = NumericLiteralParser.EncodeD32Bid(str: rawValue);
        return new ParsedD32(Location: literal.Location, Value: result.Value);
    }

    /// <summary>
    /// Parse d64 literal as part of this compiler phase.
    /// </summary>
    private static ParsedD64 ParseD64Literal(LiteralExpression literal, string rawValue)
    {
        if (rawValue == "inf")
        {
            return new ParsedD64(Location: literal.Location, Value: 0x7800000000000000UL);
        }

        if (rawValue == "nan")
        {
            return new ParsedD64(Location: literal.Location, Value: 0x7C00000000000000UL);
        }

        NumericLiteralParser.D64 result = NumericLiteralParser.EncodeD64Bid(str: rawValue);
        return new ParsedD64(Location: literal.Location, Value: result.Value);
    }

    /// <summary>
    /// Parse d128 literal as part of this compiler phase.
    /// </summary>
    private static ParsedD128 ParseD128Literal(LiteralExpression literal, string rawValue)
    {
        if (rawValue == "inf")
        {
            return new ParsedD128(Location: literal.Location, Lo: 0UL, Hi: 0x7800000000000000UL);
        }

        if (rawValue == "nan")
        {
            return new ParsedD128(Location: literal.Location, Lo: 0UL, Hi: 0x7C00000000000000UL);
        }

        NumericLiteralParser.D128 result = NumericLiteralParser.EncodeD128Bid(str: rawValue);
        return new ParsedD128(Location: literal.Location, Lo: result.Lo, Hi: result.Hi);
    }

    /// <summary>
    /// Parse integer literal as part of this compiler phase.
    /// </summary>
    private ParsedInteger ParseIntegerLiteral(LiteralExpression literal, string rawValue)
    {
        // Strip the `n` suffix and digit-group underscores (e.g. "1_000_000n" -> "1000000"), then
        // parse the magnitude (decimal or 0x/0b/0o) into a BigInteger.
        string digits =
            CleanNumericLiteral(value: ExtractNumericPart(rawValue: rawValue, suffix: "n"));
        if (!TryParseWideMagnitude(cleaned: digits, value: out System.Numerics.BigInteger value))
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
            Limbs: System.Numerics.BigInteger.Abs(value: value)
                                             .ToByteArray(isUnsigned: true, isBigEndian: false),
            Sign: value.Sign < 0
                ? 1
                : 0,
            Exponent: 0);
    }

    /// <summary>
    /// Parse decimal literal as part of this compiler phase.
    /// </summary>
    private ParsedDecimal ParseDecimalLiteral(LiteralExpression literal, string rawValue)
    {
        // Strip the `dn` suffix and digit-group underscores (e.g. "3.14_159dn" -> "3.14159")
        // before parsing.
        string digits =
            CleanNumericLiteral(value: ExtractNumericPart(rawValue: rawValue, suffix: "dn"));

        // Decimal is finite-only: inf/nan Decimal literals are rejected outright (no non-finite
        // Decimal value can exist — arithmetic crashes and conversions crash on a non-finite source).
        if (rawValue == "inf" || rawValue == "nan")
        {
            ReportError(code: SemanticDiagnosticCode.InvalidDecimalLiteral,
                message: "Decimal is finite-only; 'inf' and 'nan' are not valid Decimal literals",
                location: literal.Location);
            return new ParsedDecimal(Location: literal.Location,
                StringValue: rawValue,
                Sign: 0,
                Exponent: 0,
                SignificantDigits: 0,
                IsInteger: false);
        }

        // Validate against the i128 BID Decimal range the same way codegen will encode it:
        // EncodeDecimalCanonical throws OverflowException when the value would round to ±infinity,
        // and the ParseDeferredLiteral catch turns that into a clean NumericLiteralParseFailed
        // diagnostic (overflow-to-infinity is a compile error, not a silently-saturated literal).
        // This mirrors the D32/D64/D128 fixed-width paths and keeps the compile-time encode the
        // single source of truth — codegen re-runs EncodeDecimalCanonical on the same text.
        NumericLiteralParser.EncodeDecimalCanonical(str: digits);

        (string value, int sign, int exponent, int significantDigits, bool isInteger) =
            NumericLiteralParser.ParseDecimalInfo(str: digits);

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
    private ParsedSignedInt? ParseSignedIntLiteral(LiteralExpression literal, string rawValue,
        string typeName, long minValue, long maxValue)
    {
        // Extract numeric part by removing the type suffix (e.g., "1s32" "1")
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
    private ParsedSignedInt? ParseS128Literal(LiteralExpression literal, string rawValue)
    {
        string numericPart = ExtractNumericPart(rawValue: rawValue, suffix: "s128");
        string cleanedValue = CleanNumericLiteral(value: numericPart);

        if (TryParseWideMagnitude(cleaned: cleanedValue,
                value: out System.Numerics.BigInteger value) &&
            value >= (System.Numerics.BigInteger)Int128.MinValue &&
            value <= (System.Numerics.BigInteger)Int128.MaxValue)
        {
            return new ParsedSignedInt(Location: literal.Location,
                TypeName: "S128",
                Value: (Int128)value);
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
            "S64" => ParseSignedIntLiteral(literal: literal,
                rawValue: rawValue,
                typeName: "S64",
                minValue: long.MinValue,
                maxValue: long.MaxValue),
            "S128" => ParseS128Literal(literal: literal, rawValue: rawValue),
            "S256" => ParseS256Literal(literal: literal, rawValue: rawValue),
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
            "U256" => ParseU256Literal(literal: literal, rawValue: rawValue),
            AddressTypeName => ParseUnsignedIntLiteral(literal: literal,
                rawValue: rawValue,
                typeName: AddressTypeName,
                maxValue: ulong.MaxValue,
                suffix: "addr"),
            _ => throw new InvalidOperationException(
                message: $"UndecidedInteger resolved to unexpected type '{resolvedTypeName}' ??" +
                         $"type inference must produce a concrete numeric type before reaching here; " +
                         $"'Integer' fallback is only valid when inference failed (handled by the caller)")
        };
    }

    /// <summary>
    /// Parse decimal by resolved type as part of this compiler phase.
    /// </summary>
    private ParsedLiteral? ParseDecimalByResolvedType(LiteralExpression literal, string rawValue,
        string resolvedTypeName)
    {
        return resolvedTypeName switch
        {
            "B16" => ParseB16Literal(literal: literal, rawValue: rawValue),
            "B32" => ParseB32Literal(literal: literal, rawValue: rawValue),
            "B64" => ParseB64Literal(literal: literal, rawValue: rawValue),
            "B128" => ParseB128Literal(literal: literal, rawValue: rawValue),
            "D32" => ParseD32Literal(literal: literal, rawValue: rawValue),
            "D64" => ParseD64Literal(literal: literal, rawValue: rawValue),
            "D128" => ParseD128Literal(literal: literal, rawValue: rawValue),
            "Decimal" => ParseDecimalLiteral(literal: literal, rawValue: rawValue),
            _ => throw new InvalidOperationException(
                message: $"UndecidedDecimal resolved to unexpected type '{resolvedTypeName}'")
        };
    }

    /// <summary>
    /// Parses an unsigned integer literal (U8-U64, Address) with overflow validation.
    /// </summary>
    private ParsedUnsignedInt? ParseUnsignedIntLiteral(LiteralExpression literal, string rawValue,
        string typeName, ulong maxValue, string? suffix = null)
    {
        // Extract numeric part by removing the type suffix (e.g., "1u32" "1")
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
                Value: value);
        }

        ReportError(code: SemanticDiagnosticCode.IntegerLiteralOverflow,
            message: $"{typeName} literal '{rawValue}' overflows. Valid range: 0 to {maxValue}.",
            location: literal.Location);
        return null;
    }

    /// <summary>
    /// Parses a U128 literal with UInt128 overflow validation.
    /// </summary>
    private ParsedUnsignedInt? ParseU128Literal(LiteralExpression literal, string rawValue)
    {
        string numericPart = ExtractNumericPart(rawValue: rawValue, suffix: "u128");
        string cleanedValue = CleanNumericLiteral(value: numericPart);

        if (TryParseWideMagnitude(cleaned: cleanedValue,
                value: out System.Numerics.BigInteger value) && value.Sign >= 0 &&
            value <= (System.Numerics.BigInteger)UInt128.MaxValue)
        {
            return new ParsedUnsignedInt(Location: literal.Location,
                TypeName: "U128",
                Value: (UInt128)value);
        }

        ReportError(code: SemanticDiagnosticCode.InvalidIntegerLiteral,
            message: $"Invalid U128 literal: '{rawValue}'",
            location: literal.Location);
        return null;
    }

    // 256-bit integer range bounds (no .NET native type — validated with BigInteger).
    private static readonly System.Numerics.BigInteger U256MaxValue =
        (System.Numerics.BigInteger.One << 256) - 1;

    private static readonly System.Numerics.BigInteger S256MaxValue =
        (System.Numerics.BigInteger.One << 255) - 1;

    private static readonly System.Numerics.BigInteger S256MinValue =
        -(System.Numerics.BigInteger.One << 255);

    /// <summary>
    /// Parses a U256 literal (range 0 .. 2^256-1) via BigInteger. The literal is a non-negative
    /// magnitude; codegen emits the raw decimal digits as the LLVM i256 constant.
    /// </summary>
    private ParsedWideInt? ParseU256Literal(LiteralExpression literal, string rawValue)
    {
        string cleanedValue =
            CleanNumericLiteral(value: ExtractNumericPart(rawValue: rawValue, suffix: "u256"));
        if (TryParseWideMagnitude(cleaned: cleanedValue,
                value: out System.Numerics.BigInteger value) && value.Sign >= 0 &&
            value <= U256MaxValue)
        {
            return new ParsedWideInt(Location: literal.Location, TypeName: "U256", Value: value);
        }

        ReportError(code: SemanticDiagnosticCode.InvalidIntegerLiteral,
            message:
            $"Invalid or out-of-range U256 literal: '{rawValue}' (valid range 0 to 2^256-1).",
            location: literal.Location);
        return null;
    }

    /// <summary>
    /// Parses an S256 literal magnitude (0 .. 2^255-1) via BigInteger. The sign comes from a unary
    /// minus operator, so the literal itself is a non-negative magnitude.
    /// </summary>
    private ParsedWideInt? ParseS256Literal(LiteralExpression literal, string rawValue)
    {
        string cleanedValue =
            CleanNumericLiteral(value: ExtractNumericPart(rawValue: rawValue, suffix: "s256"));
        // The lexer bakes a leading sign into the literal text (mirroring S128), so accept the
        // full signed range here. Codegen emits the (possibly negative) decimal as an i256 const.
        if (TryParseWideMagnitude(cleaned: cleanedValue,
                value: out System.Numerics.BigInteger value) && value >= S256MinValue &&
            value <= S256MaxValue)
        {
            return new ParsedWideInt(Location: literal.Location, TypeName: "S256", Value: value);
        }

        ReportError(code: SemanticDiagnosticCode.InvalidIntegerLiteral,
            message:
            $"Invalid or out-of-range S256 literal: '{rawValue}' (valid range -2^255 to 2^255-1).",
            location: literal.Location);
        return null;
    }

    /// <summary>
    /// Parses an B16 (half-precision) floating-point literal using .NET Half type.
    /// </summary>
    private ParsedFloat? ParseB16Literal(LiteralExpression literal, string rawValue)
    {
        if (rawValue == "inf")
        {
            return new ParsedFloat(Location: literal.Location,
                TypeName: "B16",
                Value: double.PositiveInfinity);
        }

        if (rawValue == "nan")
        {
            return new ParsedFloat(Location: literal.Location, TypeName: "B16", Value: double.NaN);
        }

        string numericPart = ExtractNumericPart(rawValue: rawValue, suffix: "b16");
        string cleanedValue = CleanNumericLiteral(value: numericPart);

        if (!Half.TryParse(s: cleanedValue, result: out Half value))
        {
            ReportError(code: SemanticDiagnosticCode.NumericLiteralParseFailed,
                message: $"Invalid B16 literal: '{rawValue}'",
                location: literal.Location);
            return null;
        }

        if (!Half.IsInfinity(value: value))
        {
            return new ParsedFloat(Location: literal.Location,
                TypeName: "B16",
                Value: (double)value);
        }

        ReportError(code: SemanticDiagnosticCode.FloatLiteralOverflow,
            message: $"B16 literal '{rawValue}' overflows the representable range.",
            location: literal.Location);
        return null;
    }

    /// <summary>
    /// Parses an B32 (single-precision) floating-point literal using .NET float type.
    /// </summary>
    private ParsedFloat? ParseB32Literal(LiteralExpression literal, string rawValue)
    {
        if (rawValue == "inf")
        {
            return new ParsedFloat(Location: literal.Location,
                TypeName: "B32",
                Value: double.PositiveInfinity);
        }

        if (rawValue == "nan")
        {
            return new ParsedFloat(Location: literal.Location, TypeName: "B32", Value: double.NaN);
        }

        string numericPart = ExtractNumericPart(rawValue: rawValue, suffix: "b32");
        string cleanedValue = CleanNumericLiteral(value: numericPart);

        if (!float.TryParse(s: cleanedValue, result: out float value))
        {
            ReportError(code: SemanticDiagnosticCode.NumericLiteralParseFailed,
                message: $"Invalid B32 literal: '{rawValue}'",
                location: literal.Location);
            return null;
        }

        if (!float.IsInfinity(f: value))
        {
            return new ParsedFloat(Location: literal.Location, TypeName: "B32", Value: value);
        }

        ReportError(code: SemanticDiagnosticCode.FloatLiteralOverflow,
            message: $"B32 literal '{rawValue}' overflows the representable range.",
            location: literal.Location);
        return null;
    }

    /// <summary>
    /// Parses an B64 (double-precision) floating-point literal using .NET double type.
    /// </summary>
    private ParsedFloat? ParseB64Literal(LiteralExpression literal, string rawValue)
    {
        if (rawValue == "inf")
        {
            return new ParsedFloat(Location: literal.Location,
                TypeName: "B64",
                Value: double.PositiveInfinity);
        }

        if (rawValue == "nan")
        {
            return new ParsedFloat(Location: literal.Location, TypeName: "B64", Value: double.NaN);
        }

        string numericPart = ExtractNumericPart(rawValue: rawValue, suffix: "b64");
        string cleanedValue = CleanNumericLiteral(value: numericPart);

        if (!double.TryParse(s: cleanedValue, result: out double value))
        {
            ReportError(code: SemanticDiagnosticCode.NumericLiteralParseFailed,
                message: $"Invalid B64 literal: '{rawValue}'",
                location: literal.Location);
            return null;
        }

        if (!double.IsInfinity(d: value))
        {
            return new ParsedFloat(Location: literal.Location, TypeName: "B64", Value: value);
        }

        ReportError(code: SemanticDiagnosticCode.FloatLiteralOverflow,
            message: $"B64 literal '{rawValue}' overflows the representable range.",
            location: literal.Location);
        return null;
    }

    #endregion

    #region Duration Literal Parsing

    /// <summary>
    /// Parses a duration literal and converts to nanoseconds.
    /// </summary>
    private ParsedDuration? ParseDurationLiteral(LiteralExpression literal, string rawValue,
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
    private ParsedByteSize? ParseByteSizeLiteral(LiteralExpression literal, string rawValue,
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
    /// Parses a width-less imaginary literal (<c>4.0i</c> / <c>4.0_i</c>). The magnitude is validated
    /// against the resolved complex type's component domain (C64→B32 float, C128→B64 double,
    /// C256→B128 via the native encoder, Complex→arbitrary decimal); the retained magnitude string is
    /// consumed by <see cref="Lowering.Passes.LiteralLoweringPass"/> to build the pure-imaginary
    /// constructor. The parsed record is validation-only (codegen sees the lowered constructor call).
    /// </summary>
    private ParsedImaginary? ParseImaginaryLiteral(LiteralExpression literal, string rawValue,
        string resolvedTypeName)
    {
        // Strip the `i` suffix (a `_i` spelling loses its separator underscore in CleanNumericLiteral).
        string numericPart = ExtractNumericPart(rawValue: rawValue, suffix: "i");
        string cleanedValue = CleanNumericLiteral(value: numericPart);

        try
        {
            bool ok = resolvedTypeName switch
            {
                "C64" => float.TryParse(s: cleanedValue, result: out float _),
                "C128" => double.TryParse(s: cleanedValue, result: out double _),
                "C256" => ValidateB128Magnitude(cleanedValue: cleanedValue),
                _ => !string.IsNullOrEmpty(
                    value: NumericLiteralParser.ParseDecimalInfo(str: cleanedValue).Item1)
            };

            if (ok)
            {
                return new ParsedImaginary(Location: literal.Location, Magnitude: cleanedValue);
            }
        }
        catch (Exception ex)
        {
            ReportError(code: SemanticDiagnosticCode.ImaginaryLiteralParseFailed,
                message: $"Invalid imaginary literal '{rawValue}': {ex.Message}",
                location: literal.Location);
            return null;
        }

        ReportError(code: SemanticDiagnosticCode.ImaginaryLiteralParseFailed,
            message: $"Invalid imaginary literal: '{rawValue}'",
            location: literal.Location);
        return null;
    }

    /// <summary>Returns true if the magnitude is a valid B128 value (via the native encoder).</summary>
    private static bool ValidateB128Magnitude(string cleanedValue)
    {
        NumericLiteralParser.EncodeB128(str: cleanedValue);
        return true;
    }

    #endregion

    #region Literal Parsing Helpers

    /// <summary>
    /// Cleans a numeric literal by removing underscores.
    /// </summary>
    internal static string CleanNumericLiteral(string value)
    {
        return value.Replace(oldValue: "_", newValue: "");
    }

    /// <summary>
    /// Parses a cleaned integer magnitude into a full-width <see cref="System.Numerics.BigInteger"/>,
    /// honoring an optional leading sign and a base prefix (<c>0x</c> hex, <c>0b</c> binary,
    /// <c>0o</c> octal; otherwise decimal). The wide-literal parsers (U128/S128/U256/S256) need this
    /// because <c>BigInteger</c>/<c>Int128</c>/<c>UInt128</c>.<c>TryParse</c> only accept decimal, so
    /// base-prefixed wide literals (e.g. a full 256-bit <c>0x…</c> constant) were rejected as
    /// out-of-range. Returns false for a malformed digit, an empty body, or a bare base prefix.
    /// </summary>
    private static bool TryParseWideMagnitude(string cleaned, out System.Numerics.BigInteger value)
    {
        value = System.Numerics.BigInteger.Zero;
        if (string.IsNullOrEmpty(value: cleaned))
        {
            return false;
        }

        int i = 0;
        bool negative = cleaned[index: 0] == '-';
        if (cleaned[index: 0] is '+' or '-')
        {
            i = 1;
        }

        (int numericBase, int start) = DetectWideBase(cleaned: cleaned, signEnd: i);
        i = start;

        if (i >= cleaned.Length)
        {
            return false; // sign / base prefix with no digits
        }

        if (!TryAccumulateWideDigits(cleaned: cleaned,
                startIndex: i,
                numericBase: numericBase,
                acc: out System.Numerics.BigInteger acc))
        {
            return false;
        }

        value = negative
            ? -acc
            : acc;
        return true;
    }

    /// <summary>
    /// Detects the numeric base (16/2/8/10) and the index of the first digit after any base prefix.
    /// </summary>
    private static (int NumericBase, int DigitStart) DetectWideBase(string cleaned, int signEnd)
    {
        if (signEnd + 1 < cleaned.Length && cleaned[index: signEnd] == '0')
        {
            switch (char.ToLowerInvariant(c: cleaned[index: signEnd + 1]))
            {
                case 'x': return (16, signEnd + 2);
                case 'b': return (2, signEnd + 2);
                case 'o': return (8, signEnd + 2);
            }
        }

        return (10, signEnd);
    }

    /// <summary>
    /// Accumulates the wide-integer digit characters into a <see cref="System.Numerics.BigInteger"/>.
    /// Returns false on any invalid or out-of-base character.
    /// </summary>
    private static bool TryAccumulateWideDigits(string cleaned, int startIndex, int numericBase,
        out System.Numerics.BigInteger acc)
    {
        acc = System.Numerics.BigInteger.Zero;
        for (int i = startIndex; i < cleaned.Length; i++)
        {
            int digit = char.ToLowerInvariant(c: cleaned[index: i]) switch
            {
                >= '0' and <= '9' => cleaned[index: i] - '0',
                >= 'a' and <= 'f' => char.ToLowerInvariant(c: cleaned[index: i]) - 'a' + 10,
                _ => -1
            };
            if (digit < 0 || digit >= numericBase)
            {
                return false;
            }

            acc = acc * numericBase + digit;
        }

        return true;
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
    /// Tries to parse a signed integer, handling hex (0x), octal (0o), and binary (0b) prefixes.
    /// </summary>
    internal static bool TryParseSignedInteger(string value, out long result)
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
            if (!long.TryParse(s: numPart[2..],
                    style: NumberStyles.HexNumber,
                    provider: null,
                    result: out long hexVal))
            {
                return false;
            }

            result = negative
                ? -hexVal
                : hexVal;
            return true;
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

        if (!numPart.StartsWith(value: "0b", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(s: value, result: out result);
        }

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
                style: NumberStyles.HexNumber,
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
