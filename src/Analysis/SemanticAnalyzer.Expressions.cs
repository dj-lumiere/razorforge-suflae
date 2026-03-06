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
/// Phase 3: Expression analysis.
/// </summary>
public sealed partial class SemanticAnalyzer
{
    #region Expression Analysis

    /// <summary>
    /// Analyzes an expression and returns its resolved type.
    /// Also sets the ResolvedType property on the expression.
    /// </summary>
    /// <param name="expression">The expression to analyze.</param>
    /// <param name="expectedType">Optional expected type for contextual inference (e.g., return type, parameter type).</param>
    /// <returns>The resolved type of the expression.</returns>
    private TypeSymbol AnalyzeExpression(Expression expression, TypeSymbol? expectedType = null)
    {
        TypeSymbol resultType = expression switch
        {
            LiteralExpression literal => AnalyzeLiteralExpression(literal: literal, expectedType: expectedType),
            IdentifierExpression id => AnalyzeIdentifierExpression(id: id),
            CompoundAssignmentExpression compound => AnalyzeCompoundAssignment(compound: compound),
            BinaryExpression binary => AnalyzeBinaryExpression(binary: binary),
            UnaryExpression unary => AnalyzeUnaryExpression(unary: unary),
            CallExpression call => AnalyzeCallExpression(call: call),
            MemberExpression member => AnalyzeMemberExpression(member: member),
            OptionalMemberExpression optMember => AnalyzeOptionalMemberExpression(optMember: optMember),
            IndexExpression index => AnalyzeIndexExpression(index: index),
            SliceExpression slice => AnalyzeSliceExpression(slice: slice),
            ConditionalExpression cond => AnalyzeConditionalExpression(cond: cond),
            LambdaExpression lambda => AnalyzeLambdaExpression(lambda: lambda),
            RangeExpression range => AnalyzeRangeExpression(range: range),
            CreatorExpression creator => AnalyzeCreatorExpression(creator: creator),
            ListLiteralExpression list => AnalyzeListLiteralExpression(list: list),
            SetLiteralExpression set => AnalyzeSetLiteralExpression(set: set),
            DictLiteralExpression dict => AnalyzeDictLiteralExpression(dict: dict),
            TupleLiteralExpression tuple => AnalyzeTupleLiteralExpression(tuple: tuple),
            TypeConversionExpression conv => AnalyzeTypeConversionExpression(conv: conv),
            ChainedComparisonExpression chain => AnalyzeChainedComparisonExpression(chain: chain),
            BlockExpression block => AnalyzeBlockExpression(block: block),
            WithExpression with => AnalyzeWithExpression(with: with),
            NamedArgumentExpression named => AnalyzeExpression(expression: named.Value),
            GenericMethodCallExpression generic => AnalyzeGenericMethodCallExpression(generic: generic),
            GenericMemberExpression genericMember => AnalyzeGenericMemberExpression(genericMember: genericMember),
            IntrinsicCallExpression intrinsic => AnalyzeIntrinsicCallExpression(intrinsic: intrinsic),
            NativeCallExpression native => AnalyzeNativeCallExpression(native: native),
            IsPatternExpression isPat => AnalyzeIsPatternExpression(isPat: isPat),
            FlagsTestExpression flagsTest => AnalyzeFlagsTestExpression(flagsTest: flagsTest),
            StealExpression steal => AnalyzeStealExpression(steal: steal),
            BackIndexExpression back => AnalyzeBackIndexExpression(back: back),
            TypeExpression typeExpr => ResolveType(typeExpr: typeExpr),
            WhenExpression whenExpr => AnalyzeWhenExpression(when: whenExpr),
            WaitforExpression waitfor => AnalyzeWaitforExpression(waitfor: waitfor),
            DependentWaitforExpression depWaitfor => AnalyzeDependentWaitforExpression(depWaitfor: depWaitfor),
            InsertedTextExpression insertedText => AnalyzeInsertedTextExpression(insertedText: insertedText),
            _ => HandleUnknownExpression(expression: expression)
        };

        // Set the resolved type directly (no conversion needed)
        expression.ResolvedType = resultType;
        return resultType;
    }

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
            TokenType.SAddrLiteral => "SAddr",

            // Unsigned integers
            TokenType.U8Literal => "U8",
            TokenType.U16Literal => "U16",
            TokenType.U32Literal => "U32",
            TokenType.U64Literal => "U64",
            TokenType.U128Literal => "U128",
            TokenType.UAddrLiteral => "UAddr",

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
        }

        // Parse and validate deferred numeric types using native libraries
        if (literal.Value is string rawValue)
        {
            ParsedLiteral? parsed = ParseDeferredLiteral(literal: literal, rawValue: rawValue);
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
    /// Checks if a type is a fixed-width integer type (S8-S128, U8-U128, SAddr, UAddr).
    /// </summary>
    // TODO: remove this
    private static bool IsFixedWidthIntegerType(TypeSymbol type)
    {
        return type.Name is "S8" or "S16" or "S32" or "S64" or "S128"
            or "U8" or "U16" or "U32" or "U64" or "U128"
            or "SAddr" or "UAddr";
    }

    /// <summary>
    /// Checks if an integer literal value fits within the range of the target type.
    /// </summary>
    private static bool LiteralFitsInType(LiteralExpression literal, TypeSymbol targetType)
    {
        // Get the numeric value from the literal
        if (literal.Value is not long value)
        {
            // TODO: For string-stored values (large numbers), we'd need more sophisticated checking
            // For now, allow inference and let runtime handle overflow
            return true;
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
            "SAddr" or "UAddr" => true, // System-dependent, allow for now
            _ => false
        };
    }

    /// <summary>
    /// Parses a deferred numeric literal using native libraries or managed parsing.
    /// Called for all numeric, duration, and byte size literals stored as strings.
    /// </summary>
    /// <param name="literal">The literal expression.</param>
    /// <param name="rawValue">The raw string value to parse.</param>
    /// <returns>The parsed literal, or null if parsing failed.</returns>
    private ParsedLiteral? ParseDeferredLiteral(LiteralExpression literal, string rawValue)
    {
        try
        {
            return literal.LiteralType switch
            {
                // Fixed-width signed integers
                TokenType.S8Literal => ParseSignedIntLiteral(literal, rawValue, "S8", sbyte.MinValue, sbyte.MaxValue),
                TokenType.S16Literal => ParseSignedIntLiteral(literal, rawValue, "S16", short.MinValue, short.MaxValue),
                TokenType.S32Literal => ParseSignedIntLiteral(literal, rawValue, "S32", int.MinValue, int.MaxValue),
                TokenType.S64Literal => ParseSignedIntLiteral(literal, rawValue, "S64", long.MinValue, long.MaxValue),
                TokenType.S128Literal => ParseS128Literal(literal, rawValue),
                TokenType.SAddrLiteral => ParseSignedIntLiteral(literal, rawValue, "SAddr", long.MinValue, long.MaxValue),

                // Fixed-width unsigned integers
                TokenType.U8Literal => ParseUnsignedIntLiteral(literal, rawValue, "U8", byte.MaxValue),
                TokenType.U16Literal => ParseUnsignedIntLiteral(literal, rawValue, "U16", ushort.MaxValue),
                TokenType.U32Literal => ParseUnsignedIntLiteral(literal, rawValue, "U32", uint.MaxValue),
                TokenType.U64Literal => ParseUnsignedIntLiteral(literal, rawValue, "U64", ulong.MaxValue),
                TokenType.U128Literal => ParseU128Literal(literal, rawValue),
                TokenType.UAddrLiteral => ParseUnsignedIntLiteral(literal, rawValue, "UAddr", ulong.MaxValue),

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
                TokenType.Integer => _registry.Language == Language.Suflae
                    ? ParseIntegerLiteral(literal: literal, rawValue: rawValue)
                    : ParseSignedIntLiteral(literal, rawValue, "S64", long.MinValue, long.MaxValue),
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
    /// Parses a signed integer literal (S8-S64, SAddr) with overflow validation.
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
    /// Parses an unsigned integer literal (U8-U64, UAddr) with overflow validation.
    /// </summary>
    private ParsedLiteral? ParseUnsignedIntLiteral(LiteralExpression literal, string rawValue, string typeName, ulong maxValue)
    {
        // Extract numeric part by removing the type suffix (e.g., "1u32" -> "1")
        string numericPart = ExtractNumericPart(rawValue, typeName.ToLowerInvariant());
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

        if (!Half.TryParse(cleanedValue, out Half value))
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

        if (!float.TryParse(cleanedValue, out float value))
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

        if (!double.TryParse(cleanedValue, out double value))
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

    private TypeSymbol AnalyzeIdentifierExpression(IdentifierExpression id)
    {
        // Special identifiers
        if (id.Name == "me")
        {
            // First check if we're inside a type body
            if (_currentType != null)
            {
                return _currentType;
            }

            // For extension methods (routine Type.method), check the routine's owner type
            if (_currentRoutine?.OwnerType != null)
            {
                // Re-lookup to get the updated type with resolved protocols/fields
                TypeSymbol? ownerType = _registry.LookupType(name: _currentRoutine.OwnerType.Name);
                if (ownerType != null)
                {
                    return ownerType;
                }
            }

            ReportError(SemanticDiagnosticCode.MeOutsideTypeMethod, "'me' can only be used inside a type method.", id.Location);
            return ErrorTypeInfo.Instance;
        }

        if (id.Name == "None")
        {
            // None represents Maybe.None - return a generic Maybe type
            return ErrorHandlingTypeInfo.WellKnown.MaybeDefinition;
        }

        // Try to look up as variable first
        VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
        if (varInfo != null)
        {
            // Check for type narrowing (e.g., after "unless x is None")
            TypeSymbol? narrowed = _registry.GetNarrowedType(name: id.Name);
            return narrowed ?? varInfo.Type;
        }

        // Try to look up as choice case (SCREAMING_SNAKE_CASE identifiers like ME_SMALL, SAME)
        var choiceCase = _registry.LookupChoiceCase(caseName: id.Name);
        if (choiceCase.HasValue)
        {
            return choiceCase.Value.ChoiceType;
        }

        // Try to look up as routine (function reference)
        RoutineInfo? routine = _registry.LookupRoutine(fullName: id.Name);
        if (routine != null)
        {
            // Return the function type for first-class function references
            return GetRoutineType(routine);
        }

        // Try to look up as type (for static access)
        TypeSymbol? type = LookupTypeWithImports(name: id.Name);
        if (type != null)
        {
            return type;
        }

        ReportError(SemanticDiagnosticCode.UnknownIdentifier, $"Unknown identifier '{id.Name}'.", id.Location);
        return ErrorTypeInfo.Instance;
    }

    /// <summary>
    /// Analyzes binary expressions that remain as BinaryExpression nodes after parsing.
    /// Note: Most arithmetic, comparison, and bitwise operators are desugared to method calls
    /// in the parser (e.g., a + b → a.__add__(b)). This method only handles operators that
    /// are NOT desugared:
    /// - Assignment (=)
    /// - Logical operators (and, or) — require short-circuit evaluation
    /// - Identity operators (===, !==)
    /// - Membership/type operators (in, notin, is, isnot, obeys, disobeys)
    /// - None coalescing (??) — requires short-circuit evaluation
    /// </summary>
    private TypeSymbol AnalyzeBinaryExpression(BinaryExpression binary)
    {
        TypeSymbol leftType = AnalyzeExpression(expression: binary.Left);
        TypeSymbol rightType = AnalyzeExpression(expression: binary.Right);

        // Handle assignment operator
        if (binary.Operator == BinaryOperator.Assign)
        {
            return AnalyzeAssignmentExpression(target: binary.Left, value: binary.Right, targetType: leftType, valueType: rightType, location: binary.Location);
        }

        // Handle flags removal operator (but) — removes flags from a value
        if (binary.Operator == BinaryOperator.But)
        {
            if (leftType is not FlagsTypeInfo)
            {
                ReportError(
                    SemanticDiagnosticCode.FlagsTypeMismatch,
                    $"'but' operator requires a flags type on the left side, but got '{leftType.Name}'.",
                    binary.Location);
                return ErrorTypeInfo.Instance;
            }

            if (rightType is not FlagsTypeInfo)
            {
                ReportError(
                    SemanticDiagnosticCode.FlagsTypeMismatch,
                    $"'but' operator requires a flags type on the right side, but got '{rightType.Name}'.",
                    binary.Location);
                return ErrorTypeInfo.Instance;
            }

            if (leftType.Name != rightType.Name)
            {
                ReportError(
                    SemanticDiagnosticCode.FlagsTypeMismatch,
                    $"'but' operator requires both operands to be the same flags type, but got '{leftType.Name}' and '{rightType.Name}'.",
                    binary.Location);
                return ErrorTypeInfo.Instance;
            }

            return leftType;
        }

        // #128: 'or' cannot be used to combine flags outside of is/isnot/isonly tests
        if (binary.Operator == BinaryOperator.Or && (leftType is FlagsTypeInfo || rightType is FlagsTypeInfo))
        {
            ReportError(
                SemanticDiagnosticCode.FlagsOrInAssignment,
                "Cannot use 'or' to combine flags values. Use 'is FLAG_A or FLAG_B' for testing, " +
                "or separate flag assignments.",
                binary.Location);
            return leftType;
        }

        // Check for operator prohibitions on choice and flags types
        // Choices do not support ANY overloadable operators — use 'is' for case matching
        // Flags do not support arithmetic/comparison/bitwise operators — use 'is'/'isnot'/'but'
        {
            string? operatorMethod = binary.Operator.GetMethodName();
            if (operatorMethod != null)
            {
                if (leftType is ChoiceTypeInfo)
                {
                    ReportError(
                        SemanticDiagnosticCode.ArithmeticOnChoiceType,
                        $"Operator '{binary.Operator.ToStringRepresentation()}' cannot be used with choice type '{leftType.Name}'. Use 'is' for case matching.",
                        binary.Location);
                    return ErrorTypeInfo.Instance;
                }

                if (leftType is FlagsTypeInfo)
                {
                    ReportError(
                        SemanticDiagnosticCode.ArithmeticOnFlagsType,
                        $"Operator '{binary.Operator.ToStringRepresentation()}' cannot be used with flags type '{leftType.Name}'. Use 'is'/'isnot'/'but' for flag operations.",
                        binary.Location);
                    return ErrorTypeInfo.Instance;
                }
            }
        }

        // Handle logical operators (and, or) — require bool operands, return bool
        // These are not desugared because they need short-circuit evaluation
        if (IsLogicalOperator(op: binary.Operator))
        {
            if (!IsBoolType(type: leftType) || !IsBoolType(type: rightType))
            {
                ReportError(SemanticDiagnosticCode.LogicalOperatorRequiresBool, $"Logical operator '{binary.Operator.ToStringRepresentation()}' requires boolean operands.", binary.Location);
            }

            return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;
        }

        // Handle comparison operators — all return Bool
        // Includes overloadable (==, !=, <, <=, >, >=, in, notin) and non-overloadable (===, !==, is, isnot, obeys, disobeys)
        if (IsComparisonOperator(op: binary.Operator))
        {
            ValidateComparisonOperands(left: leftType, right: rightType, op: binary.Operator, location: binary.Location);
            return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;
        }

        // Handle none coalescing operator (??)
        // Not desugared because it needs short-circuit evaluation
        if (binary.Operator == BinaryOperator.NoneCoalesce)
        {
            // Returns the non-optional type (right operand provides the default)
            return rightType;
        }

        // Default: return left type
        // This handles any edge cases that might slip through
        return leftType;
    }

    /// <summary>
    /// Analyzes an assignment expression (target = value).
    /// Validates mutability, field access, and type compatibility.
    /// </summary>
    /// <param name="target">The assignment target expression.</param>
    /// <param name="value">The value being assigned.</param>
    /// <param name="targetType">The resolved type of the target.</param>
    /// <param name="valueType">The resolved type of the value.</param>
    /// <param name="location">Source location for error reporting.</param>
    /// <returns>The type of the assignment expression (same as target type).</returns>
    private TypeSymbol AnalyzeAssignmentExpression(
        Expression target,
        Expression value,
        TypeSymbol targetType,
        TypeSymbol valueType,
        SourceLocation location)
    {
        // Check if target is assignable (variable, field, or index)
        if (!IsAssignableTarget(target: target))
        {
            ReportError(
                SemanticDiagnosticCode.InvalidAssignmentTarget,
                "Invalid assignment target.",
                target.Location);
            return targetType;
        }

        // Check modifiability for variable assignments
        if (target is IdentifierExpression id)
        {
            VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
            if (varInfo is { IsModifiable: false })
            {
                ReportError(
                    SemanticDiagnosticCode.AssignmentToImmutable,
                    $"Cannot assign to preset variable '{id.Name}'.",
                    location);
            }
        }

        // Validate field write access (setter visibility)
        if (target is MemberExpression member)
        {
            TypeSymbol objectType = AnalyzeExpression(expression: member.Object);
            ValidateFieldWriteAccess(objectType: objectType, fieldName: member.PropertyName, location: location);

            // Check if we're in a @readonly method trying to modify 'me'
            if (_currentRoutine is { IsReadOnly: true } &&
                member.Object is IdentifierExpression { Name: "me" })
            {
                ReportError(
                    SemanticDiagnosticCode.ModificationInReadonlyMethod,
                    $"Cannot modify field '{member.PropertyName}' in a @readonly method. " +
                    "Use @writable or @migratable to allow modifications.",
                    location);
            }
        }

        // Check modifiability for index assignments
        if (target is IndexExpression index)
        {
            // The object being indexed must be modifiable
            if (index.Object is IdentifierExpression indexedVar)
            {
                VariableInfo? varInfo = _registry.LookupVariable(name: indexedVar.Name);
                if (varInfo is { IsModifiable: false })
                {
                    ReportError(
                        SemanticDiagnosticCode.AssignmentToImmutable,
                        $"Cannot assign to index of preset variable '{indexedVar.Name}'.",
                        location);
                }
            }
        }

        // RazorForge: Entity bare assignment prohibition
        // `b = a` where `a` is a bare identifier of entity type is a build error
        if (_registry.Language == Language.RazorForge
            && value is IdentifierExpression
            && valueType is EntityTypeInfo)
        {
            ReportError(
                SemanticDiagnosticCode.BareEntityAssignment,
                $"Cannot directly assign entity of type '{valueType.Name}'. " +
                "Use '.share()' for shared ownership or 'steal' for ownership transfer.",
                location);
        }

        // Check type compatibility
        if (!IsAssignableTo(source: valueType, target: targetType))
        {
            ReportError(
                SemanticDiagnosticCode.AssignmentTypeMismatch,
                $"Cannot assign value of type '{valueType.Name}' to target of type '{targetType.Name}'.",
                location);
        }

        // Variant reassignment prohibition: variants cannot be reassigned
        // Variants must be dismantled immediately with pattern matching
        if (valueType is VariantTypeInfo)
        {
            ReportError(
                SemanticDiagnosticCode.VariantReassignmentNotAllowed,
                $"Variant type '{valueType.Name}' cannot be reassigned. " +
                "Variants must be dismantled immediately with pattern matching.",
                location);
        }

        // Assignment expression returns the target type
        return targetType;
    }

    /// <summary>
    /// Analyzes a compound assignment expression (e.g., a += b).
    /// Dispatch order: (0) verify target is var, (1) try in-place dunder (__iadd__) → Blank,
    /// (2) fallback to create-and-assign (__add__) for non-entity types, (3) error if neither.
    /// </summary>
    private TypeSymbol AnalyzeCompoundAssignment(CompoundAssignmentExpression compound)
    {
        TypeSymbol targetType = AnalyzeExpression(expression: compound.Target);
        TypeSymbol valueType = AnalyzeExpression(expression: compound.Value);

        // Step 0: Verify target is assignable and modifiable
        if (!IsAssignableTarget(target: compound.Target))
        {
            ReportError(
                SemanticDiagnosticCode.InvalidAssignmentTarget,
                "Invalid compound assignment target.",
                compound.Target.Location);
            return targetType;
        }

        if (compound.Target is IdentifierExpression id)
        {
            VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
            if (varInfo is { IsModifiable: false })
            {
                ReportError(
                    SemanticDiagnosticCode.AssignmentToImmutable,
                    $"Cannot assign to preset variable '{id.Name}'.",
                    compound.Location);
            }
        }

        if (compound.Target is MemberExpression member)
        {
            TypeSymbol objectType = AnalyzeExpression(expression: member.Object);
            ValidateFieldWriteAccess(objectType: objectType, fieldName: member.PropertyName, location: compound.Location);

            if (_currentRoutine is { IsReadOnly: true } &&
                member.Object is IdentifierExpression { Name: "me" })
            {
                ReportError(
                    SemanticDiagnosticCode.ModificationInReadonlyMethod,
                    $"Cannot modify field '{member.PropertyName}' in a @readonly method. " +
                    "Use @writable or @migratable to allow modifications.",
                    compound.Location);
            }
        }

        if (compound.Target is IndexExpression index)
        {
            if (index.Object is IdentifierExpression indexedVar)
            {
                VariableInfo? varInfo = _registry.LookupVariable(name: indexedVar.Name);
                if (varInfo is { IsModifiable: false })
                {
                    ReportError(
                        SemanticDiagnosticCode.AssignmentToImmutable,
                        $"Cannot assign to index of preset variable '{indexedVar.Name}'.",
                        compound.Location);
                }
            }
        }

        // Don't try dispatch on error types (prevent cascade)
        if (targetType.Category == TypeCategory.Error)
        {
            return targetType;
        }

        // Choice types cannot use compound assignment — choices do not support operators
        if (targetType is ChoiceTypeInfo)
        {
            ReportError(
                SemanticDiagnosticCode.ArithmeticOnChoiceType,
                $"Operator '{compound.Operator.ToStringRepresentation()}=' cannot be used with choice type '{targetType.Name}'. " +
                "Choice types do not support operators. Use 'is' for case matching.",
                compound.Location);
            return ErrorTypeInfo.Instance;
        }

        // #134: Flags types cannot use arithmetic or compound assignment operators
        if (targetType is FlagsTypeInfo)
        {
            ReportError(
                SemanticDiagnosticCode.ArithmeticOnFlagsType,
                $"Operator '{compound.Operator.ToStringRepresentation()}=' cannot be used with flags type '{targetType.Name}'. " +
                "Use 'but' to remove flags and 'is'/'isnot'/'isonly' to test flags.",
                compound.Location);
            return ErrorTypeInfo.Instance;
        }

        string? inPlaceMethod = compound.Operator.GetInPlaceMethodName();
        string? regularMethod = compound.Operator.GetMethodName();
        bool isEntity = targetType is EntityTypeInfo;

        // Step 1: Try in-place dunder (__iadd__, etc.)
        if (inPlaceMethod != null)
        {
            RoutineInfo? inPlaceRoutine = _registry.LookupRoutine(fullName: $"{targetType.Name}.{inPlaceMethod}");
            if (inPlaceRoutine != null)
            {
                // In-place method found — returns Blank (modifies in-place)
                return _registry.LookupType("Blank") ?? ErrorTypeInfo.Instance;
            }
        }

        // Step 2: Fallback to create-and-assign (NOT for entities — bare assignment prohibited)
        if (!isEntity && regularMethod != null)
        {
            RoutineInfo? regularRoutine = _registry.LookupRoutine(fullName: $"{targetType.Name}.{regularMethod}");
            if (regularRoutine != null)
            {
                // Create-and-assign: a = a.__add__(b) — returns target type
                TypeSymbol returnType = regularRoutine.ReturnType ?? targetType;
                if (!IsAssignableTo(source: returnType, target: targetType))
                {
                    ReportError(
                        SemanticDiagnosticCode.AssignmentTypeMismatch,
                        $"Compound assignment: return type '{returnType.Name}' of '{regularMethod}' " +
                        $"is not assignable to target type '{targetType.Name}'.",
                        compound.Location);
                }
                return targetType;
            }
        }

        // Step 3: Error — neither in-place nor fallback available
        string opSymbol = compound.Operator.ToStringRepresentation();
        if (isEntity)
        {
            ReportError(
                SemanticDiagnosticCode.CompoundAssignmentNotSupported,
                $"Entity type '{targetType.Name}' requires in-place operator '{inPlaceMethod}' for " +
                $"compound assignment '{opSymbol}='. Define '{inPlaceMethod}' or use explicit method calls.",
                compound.Location);
        }
        else
        {
            ReportError(
                SemanticDiagnosticCode.CompoundAssignmentNotSupported,
                $"Type '{targetType.Name}' does not support compound assignment '{opSymbol}='. " +
                $"Define '{inPlaceMethod}' or '{regularMethod}' to enable this operation.",
                compound.Location);
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeUnaryExpression(UnaryExpression unary)
    {
        TypeSymbol operandType = AnalyzeExpression(expression: unary.Operand);

        switch (unary.Operator)
        {
            case UnaryOperator.Not:
                if (!IsBoolType(type: operandType))
                {
                    ReportError(SemanticDiagnosticCode.LogicalNotRequiresBool, "Logical 'not' operator requires a boolean operand.", unary.Location);
                }

                return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;

            case UnaryOperator.Minus:
                if (!IsNumericType(type: operandType))
                {
                    ReportError(SemanticDiagnosticCode.NegationRequiresNumeric, "Negation operator requires a numeric operand.", unary.Location);
                }

                return operandType;

            case UnaryOperator.BitwiseNot:
                if (!IsIntegerType(type: operandType))
                {
                    ReportError(SemanticDiagnosticCode.BitwiseNotRequiresInteger, "Bitwise 'not' operator requires an integer operand.", unary.Location);
                }

                return operandType;

            default:
                return operandType;
        }
    }

    private TypeSymbol AnalyzeCallExpression(CallExpression call)
    {
        // Get the callee type/routine
        if (call.Callee is IdentifierExpression id)
        {
            RoutineInfo? routine = _registry.LookupRoutine(fullName: id.Name);
            if (routine != null)
            {
                // Track failable calls for error handling variant generation
                if (routine.IsFailable && _currentRoutine != null)
                {
                    _currentRoutine.HasFailableCalls = true;
                }

                // Validate routine access
                ValidateRoutineAccess(routine: routine, accessLocation: call.Location);

                AnalyzeCallArguments(routine: routine, arguments: call.Arguments, location: call.Location);

                // Validate exclusive token uniqueness (cannot pass same Hijacked/Seized twice)
                ValidateExclusiveTokenUniqueness(arguments: call.Arguments, location: call.Location);

                // Return type is Blank if not specified (routines without explicit return type return Blank)
                return routine.ReturnType ?? _registry.LookupType("Blank") ?? ErrorTypeInfo.Instance;
            }

            // Could be a type creator
            TypeSymbol? type = LookupTypeWithImports(name: id.Name);
            if (type != null)
            {
                // Creator call - also validate token uniqueness
                foreach (Expression arg in call.Arguments)
                {
                    AnalyzeExpression(expression: arg);
                }

                ValidateExclusiveTokenUniqueness(arguments: call.Arguments, location: call.Location);
                return type;
            }

            // Try module-prefixed routine lookup (e.g., Core.normalize_duration)
            // This is done after type creator check to avoid shadowing type creators
            // with identically-named convenience functions (e.g., "routine U32(from: U8)")
            routine = LookupRoutineWithImports(name: id.Name);
            if (routine != null)
            {
                // Track failable calls for error handling variant generation
                if (routine.IsFailable && _currentRoutine != null)
                {
                    _currentRoutine.HasFailableCalls = true;
                }

                ValidateRoutineAccess(routine: routine, accessLocation: call.Location);
                AnalyzeCallArguments(routine: routine, arguments: call.Arguments, location: call.Location);
                ValidateExclusiveTokenUniqueness(arguments: call.Arguments, location: call.Location);

                return routine.ReturnType ?? _registry.LookupType("Blank") ?? ErrorTypeInfo.Instance;
            }
        }

        if (call.Callee is MemberExpression member)
        {
            TypeSymbol objectType = AnalyzeExpression(expression: member.Object);

            // Choice types cannot use any operator dunders
            if (objectType is ChoiceTypeInfo && IsOperatorDunder(name: member.PropertyName))
            {
                ReportError(
                    SemanticDiagnosticCode.ArithmeticOnChoiceType,
                    $"Operator '{member.PropertyName}' cannot be used with choice type '{objectType.Name}'. " +
                    "Choice types do not support operators. Use 'is' for case matching and regular methods for additional behavior.",
                    call.Location);
                return ErrorTypeInfo.Instance;
            }

            // #134/#135: Flags types cannot use any operator dunders
            if (objectType is FlagsTypeInfo && IsOperatorDunder(name: member.PropertyName))
            {
                ReportError(
                    SemanticDiagnosticCode.ArithmeticOnFlagsType,
                    $"Operator '{member.PropertyName}' cannot be used with flags type '{objectType.Name}'. " +
                    "Use 'but' to remove flags and 'is'/'isnot'/'isonly' to test flags.",
                    call.Location);
                return ErrorTypeInfo.Instance;
            }

            RoutineInfo? method = _registry.LookupRoutine(fullName: $"{objectType.Name}.{member.PropertyName}");
            if (method != null)
            {
                // Track failable calls for error handling variant generation
                if (method.IsFailable && _currentRoutine != null)
                {
                    _currentRoutine.HasFailableCalls = true;
                }

                // Validate method access
                ValidateRoutineAccess(routine: method, accessLocation: call.Location);

                // @readonly enforcement: cannot call modifying methods on 'me'
                if (_currentRoutine is { IsReadOnly: true } &&
                    member.Object is IdentifierExpression { Name: "me" } &&
                    !method.IsReadOnly)
                {
                    ReportError(
                        SemanticDiagnosticCode.ModificationInReadonlyMethod,
                        $"Cannot call non-readonly method '{method.Name}' on 'me' in a @readonly method. " +
                        "Mark the called method @readonly or use @writable/@migratable.",
                        call.Location);
                }

                // Preset enforcement: cannot call modifying methods on preset variables
                if (member.Object is IdentifierExpression letTarget &&
                    method.ModificationCategory != ModificationCategory.Readonly)
                {
                    VariableInfo? targetVar = _registry.LookupVariable(name: letTarget.Name);
                    if (targetVar is { IsModifiable: false })
                    {
                        ReportError(
                            SemanticDiagnosticCode.ModifyingCallOnImmutable,
                            $"Cannot call modifying method '{method.Name}' on preset variable '{letTarget.Name}'.",
                            call.Location);
                    }
                }

                AnalyzeCallArguments(routine: method, arguments: call.Arguments, location: call.Location);

                // Validate exclusive token uniqueness (cannot pass same Hijacked/Seized twice)
                ValidateExclusiveTokenUniqueness(arguments: call.Arguments, location: call.Location);

                // Return type is Blank if not specified
                return method.ReturnType ?? _registry.LookupType("Blank") ?? ErrorTypeInfo.Instance;
            }
        }

        // Analyze callee expression (lambda or other callable)
        TypeSymbol calleeType = AnalyzeExpression(expression: call.Callee);

        // Analyze arguments
        foreach (Expression arg in call.Arguments)
        {
            AnalyzeExpression(expression: arg);
        }

        // Validate exclusive token uniqueness for dynamic calls too
        ValidateExclusiveTokenUniqueness(arguments: call.Arguments, location: call.Location);

        return calleeType;
    }

    private TypeSymbol AnalyzeMemberExpression(MemberExpression member)
    {
        TypeSymbol objectType = AnalyzeExpression(expression: member.Object);

        // Look up the field/property on the type
        if (objectType is RecordTypeInfo record)
        {
            FieldInfo? field = record.LookupField(fieldName: member.PropertyName);
            if (field != null)
            {
                // Validate field access (read access)
                ValidateFieldAccess(field: field, isWrite: false, accessLocation: member.Location);
                return field.Type;
            }
        }
        else if (objectType is EntityTypeInfo entity)
        {
            FieldInfo? field = entity.LookupField(fieldName: member.PropertyName);
            if (field != null)
            {
                // Validate field access (read access)
                ValidateFieldAccess(field: field, isWrite: false, accessLocation: member.Location);
                return field.Type;
            }
        }
        else if (objectType is ResidentTypeInfo resident)
        {
            FieldInfo? field = resident.LookupField(fieldName: member.PropertyName);
            if (field != null)
            {
                // Validate field access (read access)
                ValidateFieldAccess(field: field, isWrite: false, accessLocation: member.Location);
                return field.Type;
            }
        }
        // Wrapper type forwarding: Viewed<T>, Hijacked<T>, Shared<T>, etc.
        else if (IsWrapperType(type: objectType))
        {
            // Try to forward field access to the inner type
            FieldInfo? innerField = LookupFieldOnWrapperInnerType(wrapperType: objectType, fieldName: member.PropertyName);
            if (innerField != null)
            {
                // Validate field access on the inner type
                ValidateFieldAccess(field: innerField, isWrite: false, accessLocation: member.Location);
                return innerField.Type;
            }

            // Try to forward method access to the inner type
            RoutineInfo? innerMethod = LookupMethodOnWrapperInnerType(wrapperType: objectType, methodName: member.PropertyName);
            if (innerMethod != null)
            {
                // Validate read-only wrapper restrictions
                ValidateReadOnlyWrapperMethodAccess(wrapperType: objectType, method: innerMethod, location: member.Location);
                // Validate method access
                ValidateRoutineAccess(routine: innerMethod, accessLocation: member.Location);
                // Return type is Blank if not specified
                return innerMethod.ReturnType ?? _registry.LookupType("Blank") ?? ErrorTypeInfo.Instance;
            }
        }

        // Could be a method reference - use LookupMethod which handles generic resolutions
        RoutineInfo? method = _registry.LookupMethod(type: objectType, methodName: member.PropertyName);
        if (method != null)
        {
            // Validate method access
            ValidateRoutineAccess(routine: method, accessLocation: member.Location);

            // For generic resolutions, substitute type parameters in return type
            TypeSymbol? returnType = method.ReturnType;
            if (returnType != null && objectType is { IsGenericResolution: true, TypeArguments: not null })
            {
                returnType = SubstituteTypeParameters(type: returnType, genericType: objectType);
            }

            return returnType ?? ErrorTypeInfo.Instance;
        }

        ReportError(SemanticDiagnosticCode.MemberNotFound, $"Type '{objectType.Name}' does not have a member '{member.PropertyName}'.", member.Location);
        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeOptionalMemberExpression(OptionalMemberExpression optMember)
    {
        // Analyze the object expression to get its type
        TypeSymbol objectType = AnalyzeExpression(expression: optMember.Object);

        // Delegate to regular member analysis for the property lookup
        // The result is wrapped in Maybe[T] since the access may produce none
        var regularMember = new MemberExpression(Object: optMember.Object, PropertyName: optMember.PropertyName, Location: optMember.Location);
        TypeSymbol memberType = AnalyzeMemberExpression(member: regularMember);

        return memberType;
    }

    private TypeSymbol AnalyzeIndexExpression(IndexExpression index)
    {
        TypeSymbol objectType = AnalyzeExpression(expression: index.Object);
        AnalyzeExpression(expression: index.Index);

        // Look for __getitem__ method
        RoutineInfo? getItem = _registry.LookupRoutine(fullName: $"{objectType.Name}.__getitem__");
        if (getItem?.ReturnType != null)
        {
            return getItem.ReturnType;
        }

        // For generic types like List<T>, return the element type
        if (objectType.TypeArguments is { Count: > 0 })
        {
            return objectType.TypeArguments[0];
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeSliceExpression(SliceExpression slice)
    {
        TypeSymbol objectType = AnalyzeExpression(expression: slice.Object);
        AnalyzeExpression(expression: slice.Start);
        AnalyzeExpression(expression: slice.End);

        // Look for __getslice__ method
        RoutineInfo? getSlice = _registry.LookupRoutine(fullName: $"{objectType.Name}.__getslice__");
        if (getSlice?.ReturnType != null)
        {
            return getSlice.ReturnType;
        }

        // For generic types like List<T>, return the element type
        if (objectType.TypeArguments is { Count: > 0 })
        {
            return objectType.TypeArguments[0];
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeConditionalExpression(ConditionalExpression cond)
    {
        TypeSymbol conditionType = AnalyzeExpression(expression: cond.Condition);

        if (!IsBoolType(type: conditionType))
        {
            ReportError(SemanticDiagnosticCode.ConditionalNotBool, $"Conditional expression requires a boolean condition, got '{conditionType.Name}'.", cond.Condition.Location);
        }

        TypeSymbol trueType = AnalyzeExpression(expression: cond.TrueExpression);
        TypeSymbol falseType = AnalyzeExpression(expression: cond.FalseExpression);

        // Both branches must be compatible
        if (!IsAssignableTo(source: trueType, target: falseType) && !IsAssignableTo(source: falseType, target: trueType))
        {
            ReportError(SemanticDiagnosticCode.ConditionalBranchTypeMismatch, $"Conditional expression branches have incompatible types: '{trueType.Name}' and '{falseType.Name}'.", cond.Location);
        }

        // Return the common type (for now, use the true branch type)
        return trueType;
    }

    private TypeSymbol AnalyzeLambdaExpression(LambdaExpression lambda)
    {
        // Collect variables from enclosing scope that might be captured
        var enclosingScopeVariables = _registry.GetAllVariablesInScope();
        // Collect only local (function-level) variables — these require 'given' to capture
        var localScopeVariables = _registry.GetLocalScopeVariables();

        _registry.EnterScope(kind: ScopeKind.Function, name: "lambda");

        // Register lambda parameters and collect their types
        var parameterNames = new HashSet<string>();
        var parameterTypes = new List<TypeSymbol>();
        foreach (Parameter param in lambda.Parameters)
        {
            TypeSymbol paramType = param.Type != null
                ? ResolveType(typeExpr: param.Type)
                : ErrorTypeInfo.Instance;

            _registry.DeclareVariable(name: param.Name, type: paramType);
            parameterNames.Add(item: param.Name);
            parameterTypes.Add(paramType);
        }

        // Analyze body and get return type
        TypeSymbol returnType = AnalyzeExpression(expression: lambda.Body);

        // Validate captured variables (RazorForge only)
        // Lambda bodies can reference variables from enclosing scope - these are captures
        ValidateLambdaCaptures(
            lambda: lambda,
            enclosingScopeVariables: enclosingScopeVariables,
            localScopeVariables: localScopeVariables,
            parameterNames: parameterNames);

        _registry.ExitScope();

        // Create a proper function type: (ParamTypes) -> ReturnType
        return _registry.GetOrCreateRoutineType(
            parameterTypes: parameterTypes,
            returnType: returnType,
            isFailable: false);
    }

    /// <summary>
    /// Validates that lambda captures don't include forbidden types and that all
    /// local-scope captures are declared in the 'given' clause (RazorForge only).
    /// </summary>
    /// <param name="lambda">The lambda expression being analyzed.</param>
    /// <param name="enclosingScopeVariables">All variables available in the enclosing scope.</param>
    /// <param name="localScopeVariables">Variables from local (function-level) scopes only — require 'given'.</param>
    /// <param name="parameterNames">Names of lambda parameters (not captures).</param>
    private void ValidateLambdaCaptures(
        LambdaExpression lambda,
        IReadOnlyDictionary<string, VariableInfo> enclosingScopeVariables,
        IReadOnlyDictionary<string, VariableInfo> localScopeVariables,
        HashSet<string> parameterNames)
    {
        // Find all identifier expressions in the lambda body
        var identifiers = CollectIdentifiers(expression: lambda.Body);

        // Build set of given captures for quick lookup
        var givenNames = lambda.Captures != null
            ? new HashSet<string>(collection: lambda.Captures)
            : null;

        foreach (IdentifierExpression id in identifiers)
        {
            // Skip if it's a parameter (not a capture)
            if (parameterNames.Contains(item: id.Name))
            {
                continue;
            }

            // Skip special identifiers
            if (id.Name is "me" or "none")
            {
                continue;
            }

            // Check if this identifier refers to a captured variable
            if (enclosingScopeVariables.TryGetValue(key: id.Name, out VariableInfo? varInfo))
            {
                // Validate that the captured type is allowed
                ValidateCapturedType(varName: id.Name, varType: varInfo.Type, location: id.Location);

                // Check 'given' clause enforcement for local captures (RazorForge only)
                if (_registry.Language == Language.RazorForge
                    && localScopeVariables.ContainsKey(key: id.Name)
                    && !varInfo.IsPreset)
                {
                    if (givenNames == null)
                    {
                        // No 'given' clause — implicit capture of local variable
                        ReportError(
                            SemanticDiagnosticCode.LambdaCaptureWithoutGiven,
                            $"Lambda captures local variable '{id.Name}' without declaring it in 'given' clause. " +
                            "All local captures must be explicit via 'given'.",
                            id.Location);
                    }
                    else if (!givenNames.Contains(item: id.Name))
                    {
                        // Has 'given' clause but this variable isn't in it
                        ReportError(
                            SemanticDiagnosticCode.LambdaCaptureWithoutGiven,
                            $"Lambda captures local variable '{id.Name}' but it is not listed in the 'given' clause.",
                            id.Location);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Validates that a captured variable's type is allowed in lambda captures.
    /// </summary>
    /// <param name="varName">Name of the captured variable.</param>
    /// <param name="varType">Type of the captured variable.</param>
    /// <param name="location">Source location for error reporting.</param>
    private void ValidateCapturedType(string varName, TypeSymbol varType, SourceLocation location)
    {
        // Check for memory tokens (scope-bound, cannot be captured)
        if (IsMemoryToken(type: varType))
        {
            string tokenKind = GetMemoryTokenKind(type: varType);
            ReportError(
                SemanticDiagnosticCode.LambdaCaptureToken,
                $"Cannot capture '{varName}' of type '{tokenKind}' in lambda - " +
                $"scope-bound tokens cannot escape their scope. " +
                $"Use a handle type (Shared[T] or Tracked[T]) instead.",
                location);
            return;
        }

        // Check for raw entities (must use handles for capture)
        if (IsRawEntityType(type: varType))
        {
            ReportError(
                SemanticDiagnosticCode.LambdaCaptureRawEntity,
                $"Cannot capture raw entity '{varName}' of type '{varType.Name}' in lambda - " +
                $"raw entities cannot be captured. " +
                $"Wrap in a handle type (Shared[T] or Tracked[T]) before capturing.",
                location);
        }
    }

    /// <summary>
    /// Checks if a type is a raw entity (not wrapped in a handle or token).
    /// </summary>
    private bool IsRawEntityType(TypeSymbol type)
    {
        // Raw entities are entity types that are not wrapped
        return type.Category == TypeCategory.Entity
            && !IsMemoryToken(type: type)
            && !IsStealableHandle(type: type)
            && !IsSnatched(type: type);
    }

    /// <summary>
    /// Collects all identifier expressions in an expression tree.
    /// </summary>
    private static List<IdentifierExpression> CollectIdentifiers(Expression expression)
    {
        var identifiers = new List<IdentifierExpression>();
        CollectIdentifiersRecursive(expression: expression, identifiers: identifiers);
        return identifiers;
    }

    /// <summary>
    /// Recursively collects identifier expressions.
    /// </summary>
    private static void CollectIdentifiersRecursive(Expression expression, List<IdentifierExpression> identifiers)
    {
        switch (expression)
        {
            case IdentifierExpression id:
                identifiers.Add(item: id);
                break;

            case CompoundAssignmentExpression compound:
                CollectIdentifiersRecursive(expression: compound.Target, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: compound.Value, identifiers: identifiers);
                break;

            case BinaryExpression binary:
                CollectIdentifiersRecursive(expression: binary.Left, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: binary.Right, identifiers: identifiers);
                break;

            case UnaryExpression unary:
                CollectIdentifiersRecursive(expression: unary.Operand, identifiers: identifiers);
                break;

            case StealExpression steal:
                CollectIdentifiersRecursive(expression: steal.Operand, identifiers: identifiers);
                break;

            case BackIndexExpression back:
                CollectIdentifiersRecursive(expression: back.Operand, identifiers: identifiers);
                break;

            case CallExpression call:
                CollectIdentifiersRecursive(expression: call.Callee, identifiers: identifiers);
                foreach (Expression arg in call.Arguments)
                {
                    CollectIdentifiersRecursive(expression: arg, identifiers: identifiers);
                }
                break;

            case MemberExpression member:
                CollectIdentifiersRecursive(expression: member.Object, identifiers: identifiers);
                break;

            case IndexExpression index:
                CollectIdentifiersRecursive(expression: index.Object, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: index.Index, identifiers: identifiers);
                break;

            case SliceExpression slice:
                CollectIdentifiersRecursive(expression: slice.Object, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: slice.Start, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: slice.End, identifiers: identifiers);
                break;

            case ConditionalExpression cond:
                CollectIdentifiersRecursive(expression: cond.Condition, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: cond.TrueExpression, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: cond.FalseExpression, identifiers: identifiers);
                break;

            case LambdaExpression:
                // Don't descend into nested lambdas - they have their own capture context
                break;

            case RangeExpression range:
                CollectIdentifiersRecursive(expression: range.Start, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: range.End, identifiers: identifiers);
                if (range.Step != null)
                {
                    CollectIdentifiersRecursive(expression: range.Step, identifiers: identifiers);
                }
                break;

            case CreatorExpression creator:
                foreach ((_, Expression value) in creator.Fields)
                {
                    CollectIdentifiersRecursive(expression: value, identifiers: identifiers);
                }
                break;

            case ListLiteralExpression list:
                foreach (Expression elem in list.Elements)
                {
                    CollectIdentifiersRecursive(expression: elem, identifiers: identifiers);
                }
                break;

            case SetLiteralExpression set:
                foreach (Expression elem in set.Elements)
                {
                    CollectIdentifiersRecursive(expression: elem, identifiers: identifiers);
                }
                break;

            case DictLiteralExpression dict:
                foreach ((Expression key, Expression value) in dict.Pairs)
                {
                    CollectIdentifiersRecursive(expression: key, identifiers: identifiers);
                    CollectIdentifiersRecursive(expression: value, identifiers: identifiers);
                }
                break;

            case TupleLiteralExpression tuple:
                foreach (Expression elem in tuple.Elements)
                {
                    CollectIdentifiersRecursive(expression: elem, identifiers: identifiers);
                }
                break;

            case BlockExpression block:
                CollectIdentifiersRecursive(expression: block.Value, identifiers: identifiers);
                break;

            case WithExpression with:
                CollectIdentifiersRecursive(expression: with.Base, identifiers: identifiers);
                foreach ((_, Expression? index, Expression value) in with.Updates)
                {
                    if (index != null)
                    {
                        CollectIdentifiersRecursive(expression: index, identifiers: identifiers);
                    }
                    CollectIdentifiersRecursive(expression: value, identifiers: identifiers);
                }
                break;

            case IsPatternExpression isPat:
                CollectIdentifiersRecursive(expression: isPat.Expression, identifiers: identifiers);
                break;

            case NamedArgumentExpression named:
                CollectIdentifiersRecursive(expression: named.Value, identifiers: identifiers);
                break;

            case GenericMethodCallExpression generic:
                CollectIdentifiersRecursive(expression: generic.Object, identifiers: identifiers);
                foreach (Expression arg in generic.Arguments)
                {
                    CollectIdentifiersRecursive(expression: arg, identifiers: identifiers);
                }
                break;

            case GenericMemberExpression genericMember:
                CollectIdentifiersRecursive(expression: genericMember.Object, identifiers: identifiers);
                break;

            case IntrinsicCallExpression intrinsic:
                foreach (Expression arg in intrinsic.Arguments)
                {
                    CollectIdentifiersRecursive(expression: arg, identifiers: identifiers);
                }
                break;

            case NativeCallExpression native:
                foreach (Expression arg in native.Arguments)
                {
                    CollectIdentifiersRecursive(expression: arg, identifiers: identifiers);
                }
                break;

            case TypeConversionExpression conv:
                CollectIdentifiersRecursive(expression: conv.Expression, identifiers: identifiers);
                break;

            case ChainedComparisonExpression chain:
                foreach (Expression operand in chain.Operands)
                {
                    CollectIdentifiersRecursive(expression: operand, identifiers: identifiers);
                }
                break;

            // Literal expressions and type expressions have no identifiers to collect
            case LiteralExpression:
            case TypeExpression:
                break;
        }
    }

    private TypeSymbol AnalyzeRangeExpression(RangeExpression range)
    {
        TypeSymbol startType = AnalyzeExpression(expression: range.Start);
        TypeSymbol endType = AnalyzeExpression(expression: range.End);

        if (range.Step != null)
        {
            AnalyzeExpression(expression: range.Step);
        }

        // Range types must be compatible
        if (!IsNumericType(type: startType) || !IsNumericType(type: endType))
        {
            ReportError(SemanticDiagnosticCode.RangeBoundsNotNumeric, "Range bounds must be numeric types.", range.Location);
        }

        // Return Range type
        return _registry.LookupType(name: "Range") ?? ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeCreatorExpression(CreatorExpression creator)
    {
        TypeSymbol? type = LookupTypeWithImports(name: creator.TypeName);
        if (type == null)
        {
            ReportError(SemanticDiagnosticCode.UnknownType, $"Unknown type '{creator.TypeName}'.", creator.Location);
            return ErrorTypeInfo.Instance;
        }

        // Handle generic type arguments
        if (creator.TypeArguments is { Count: > 0 })
        {
            var typeArgs = new List<TypeSymbol>();
            foreach (TypeExpression typeArg in creator.TypeArguments)
            {
                typeArgs.Add(item: ResolveType(typeExpr: typeArg));
            }

            ValidateGenericConstraints(genericDef: type, typeArgs: typeArgs, location: creator.Location);
            type = _registry.GetOrCreateResolution(genericDef: type, typeArguments: typeArgs);
        }

        // Validate field initializers
        ValidateCreatorFields(type: type, fields: creator.Fields, location: creator.Location);

        return type;
    }

    /// <summary>
    /// Validates creator field initializers:
    /// - Each provided field exists on the type
    /// - Value types are assignable to field types
    /// - No duplicate field assignments
    /// - All required fields are provided
    /// </summary>
    private void ValidateCreatorFields(
        TypeSymbol type,
        List<(string Name, Expression Value)> fields,
        SourceLocation location)
    {
        // Get the type's fields
        IReadOnlyList<FieldInfo>? typeFields = type switch
        {
            RecordTypeInfo record => record.Fields,
            EntityTypeInfo entity => entity.Fields,
            ResidentTypeInfo resident => resident.Fields,
            _ => null
        };

        if (typeFields == null)
        {
            if (fields.Count > 0)
            {
                ReportError(
                    SemanticDiagnosticCode.TypeNotFieldInitializable,
                    $"Type '{type.Name}' does not support field initialization.",
                    location);
            }
            return;
        }

        // Build a lookup for expected fields
        var fieldLookup = new Dictionary<string, FieldInfo>();
        foreach (FieldInfo field in typeFields)
        {
            fieldLookup[field.Name] = field;
        }

        // Track which fields have been provided (to detect duplicates and missing fields)
        var providedFields = new HashSet<string>();

        // Validate each provided field
        foreach ((string fieldName, Expression value) in fields)
        {
            // Check for duplicates
            if (!providedFields.Add(fieldName))
            {
                ReportError(
                    SemanticDiagnosticCode.DuplicateFieldInitializer,
                    $"Duplicate field initializer for '{fieldName}'.",
                    value.Location);
                continue;
            }

            // Check if field exists
            if (!fieldLookup.TryGetValue(fieldName, out FieldInfo? expectedField))
            {
                ReportError(
                    SemanticDiagnosticCode.FieldNotFound,
                    $"Type '{type.Name}' does not have a field named '{fieldName}'.",
                    value.Location);
                AnalyzeExpression(expression: value); // Still analyze the value
                continue;
            }

            // Analyze value with expected type for contextual inference
            TypeSymbol fieldType = expectedField.Type;

            // For generic resolutions, substitute type parameters in field type
            if (type is { IsGenericResolution: true, TypeArguments: not null })
            {
                fieldType = SubstituteTypeParameters(type: fieldType, genericType: type);
            }

            TypeSymbol valueType = AnalyzeExpression(expression: value, expectedType: fieldType);

            // Check type compatibility
            if (!IsAssignableTo(source: valueType, target: fieldType))
            {
                ReportError(
                    SemanticDiagnosticCode.FieldTypeMismatch,
                    $"Cannot assign '{valueType.Name}' to field '{fieldName}' of type '{fieldType.Name}'.",
                    value.Location);
            }
        }

        // Check for missing required fields (fields without default values)
        foreach (FieldInfo field in typeFields)
        {
            if (!providedFields.Contains(field.Name) && !field.HasDefaultValue)
            {
                ReportError(
                    SemanticDiagnosticCode.MissingRequiredField,
                    $"Missing required field '{field.Name}' in creator for '{type.Name}'.",
                    location);
            }
        }
    }

    private TypeSymbol AnalyzeListLiteralExpression(ListLiteralExpression list)
    {
        TypeSymbol? elementType = null;

        if (list.ElementType != null)
        {
            elementType = ResolveType(typeExpr: list.ElementType);
        }
        else if (list.Elements.Count > 0)
        {
            // Infer from first element
            elementType = AnalyzeExpression(expression: list.Elements[0]);

            // Validate all elements have compatible types
            for (int i = 1; i < list.Elements.Count; i++)
            {
                TypeSymbol elemType = AnalyzeExpression(expression: list.Elements[i]);
                if (!IsAssignableTo(source: elemType, target: elementType))
                {
                    ReportError(SemanticDiagnosticCode.ListElementTypeMismatch, $"List element type mismatch: expected '{elementType.Name}', got '{elemType.Name}'.", list.Elements[i].Location);
                }
            }
        }
        else
        {
            ReportError(SemanticDiagnosticCode.EmptyListNoTypeAnnotation, "Cannot infer element type from empty list literal without type annotation.", list.Location);
            elementType = ErrorTypeInfo.Instance;
        }

        // Return List<T> type
        TypeSymbol? listDef = _registry.LookupType(name: "List");
        if (listDef != null && elementType != null)
        {
            return _registry.GetOrCreateResolution(genericDef: listDef, typeArguments: [elementType]);
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeSetLiteralExpression(SetLiteralExpression set)
    {
        TypeSymbol? elementType = null;

        if (set.ElementType != null)
        {
            elementType = ResolveType(typeExpr: set.ElementType);
        }
        else if (set.Elements.Count > 0)
        {
            elementType = AnalyzeExpression(expression: set.Elements[0]);
        }
        else
        {
            ReportError(SemanticDiagnosticCode.EmptySetNoTypeAnnotation, "Cannot infer element type from empty set literal without type annotation.", set.Location);
            elementType = ErrorTypeInfo.Instance;
        }

        // Analyze all elements
        foreach (Expression elem in set.Elements)
        {
            AnalyzeExpression(expression: elem);
        }

        // Return Set<T> type
        TypeSymbol? setDef = _registry.LookupType(name: "Set");
        if (setDef != null && elementType != null)
        {
            return _registry.GetOrCreateResolution(genericDef: setDef, typeArguments: [elementType]);
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeDictLiteralExpression(DictLiteralExpression dict)
    {
        TypeSymbol? keyType = null;
        TypeSymbol? valueType = null;

        if (dict is { KeyType: not null, ValueType: not null })
        {
            keyType = ResolveType(typeExpr: dict.KeyType);
            valueType = ResolveType(typeExpr: dict.ValueType);
        }
        else if (dict.Pairs.Count > 0)
        {
            keyType = AnalyzeExpression(expression: dict.Pairs[0].Key);
            valueType = AnalyzeExpression(expression: dict.Pairs[0].Value);
        }
        else
        {
            ReportError(SemanticDiagnosticCode.EmptyDictNoTypeAnnotation, "Cannot infer types from empty dict literal without type annotation.", dict.Location);
            keyType = ErrorTypeInfo.Instance;
            valueType = ErrorTypeInfo.Instance;
        }

        // Analyze all pairs
        foreach ((Expression Key, Expression Value) pair in dict.Pairs)
        {
            AnalyzeExpression(expression: pair.Key);
            AnalyzeExpression(expression: pair.Value);
        }

        // Return Dict<K, V> type
        TypeSymbol? dictDef = _registry.LookupType(name: "Dict");
        if (dictDef != null && keyType != null && valueType != null)
        {
            return _registry.GetOrCreateResolution(genericDef: dictDef, typeArguments: [keyType, valueType]);
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeTupleLiteralExpression(TupleLiteralExpression tuple)
    {
        // Analyze all element expressions
        var elementTypes = new List<TypeSymbol>();
        foreach (Expression element in tuple.Elements)
        {
            TypeSymbol elementType = AnalyzeExpression(expression: element);
            elementTypes.Add(item: elementType);
        }

        // Empty tuples are not allowed - use Blank instead
        if (elementTypes.Count == 0)
        {
            ReportError(
                SemanticDiagnosticCode.UnknownType,
                "Empty tuples are not allowed. Use 'Blank' for the unit type.",
                tuple.Location);
            return ErrorTypeInfo.Instance;
        }

        // Determine tuple kind based on element types:
        // ValueTuple: all elements are value types (Record, Choice, Variant, ValueTuple)
        // FixedTuple: all elements are resident-compatible (records + residents, no entities)
        // Tuple: any element is an entity or other reference type
        bool allValueTypes = elementTypes.All(predicate: TypeRegistry.IsValueType);
        if (allValueTypes)
        {
            return _registry.GetOrCreateTupleType(elementTypes: elementTypes, kind: TupleKind.Value);
        }

        bool allResidentCompatible = elementTypes.All(predicate: TypeRegistry.IsResidentCompatible);
        if (allResidentCompatible)
        {
            return _registry.GetOrCreateTupleType(elementTypes: elementTypes, kind: TupleKind.Fixed);
        }

        return _registry.GetOrCreateTupleType(elementTypes: elementTypes, kind: TupleKind.Reference);
    }

    private TypeSymbol AnalyzeTypeConversionExpression(TypeConversionExpression conv)
    {
        AnalyzeExpression(expression: conv.Expression);

        TypeSymbol? targetType = LookupTypeWithImports(name: conv.TargetType);
        if (targetType == null)
        {
            ReportError(SemanticDiagnosticCode.UnknownConversionTargetType, $"Unknown conversion target type '{conv.TargetType}'.", conv.Location);
            return ErrorTypeInfo.Instance;
        }

        return targetType;
    }

    private TypeSymbol AnalyzeChainedComparisonExpression(ChainedComparisonExpression chain)
    {
        // Validate that operators don't mix ascending and descending
        ValidateComparisonChain(chain: chain, location: chain.Location);

        // Analyze all operands and validate comparisons between consecutive pairs
        var operandTypes = new List<TypeSymbol>();
        foreach (Expression operand in chain.Operands)
        {
            operandTypes.Add(item: AnalyzeExpression(expression: operand));
        }

        // Validate each comparison pair
        for (int i = 0; i < chain.Operators.Count; i++)
        {
            ValidateComparisonOperands(
                left: operandTypes[i],
                right: operandTypes[i + 1],
                op: chain.Operators[i],
                location: chain.Location);
        }

        // Chained comparisons always return bool
        return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeBlockExpression(BlockExpression block)
    {
        // Block expression evaluates to its contained value expression
        return AnalyzeExpression(expression: block.Value);
    }

    private TypeSymbol AnalyzeWithExpression(WithExpression with)
    {
        TypeSymbol baseType = AnalyzeExpression(expression: with.Base);

        // 'with' expressions are only valid on record types
        if (baseType.Category != TypeCategory.Record)
        {
            ReportError(SemanticDiagnosticCode.WithExpressionNotRecord, $"'with' expression requires a record type, got '{baseType.Name}'.", with.Location);
        }

        // Analyze update expressions
        foreach ((List<string>? fieldPath, Expression? index, Expression value) in with.Updates)
        {
            // Analyze index expression if present
            if (index != null)
            {
                AnalyzeExpression(expression: index);
            }
            AnalyzeExpression(expression: value);
            // TODO: Validate field exists and types match for field updates
            // TODO: Validate index type and value type match for index updates
        }

        // Returns the same type as the base
        return baseType;
    }

    /// <summary>
    /// Analyzes a when expression (pattern matching expression).
    /// Returns the common type of all branch results.
    /// </summary>
    private TypeSymbol AnalyzeWhenExpression(WhenExpression when)
    {
        // Analyze the matched expression
        TypeSymbol matchedType = AnalyzeExpression(expression: when.Expression);

        TypeSymbol? resultType = null;
        bool hasElse = false;

        foreach (WhenClause clause in when.Clauses)
        {
            _registry.EnterScope(kind: ScopeKind.Block, name: "when_clause");

            // Analyze the pattern
            AnalyzePattern(pattern: clause.Pattern, matchedType: matchedType);

            // Check for else clause
            if (clause.Pattern is WildcardPattern or ElsePattern)
            {
                hasElse = true;
            }

            // When expressions require expression bodies that return values
            // The Body is a Statement, but for expressions it should typically be an ExpressionStatement
            if (clause.Body is ExpressionStatement exprStmt)
            {
                TypeSymbol branchType = AnalyzeExpression(expression: exprStmt.Expression);

                if (resultType == null)
                {
                    resultType = branchType;
                }
                else if (!IsAssignableTo(source: branchType, target: resultType))
                {
                    ReportError(
                        SemanticDiagnosticCode.WhenBranchTypeMismatch,
                        $"When expression branches have incompatible types: '{resultType.Name}' and '{branchType.Name}'.",
                        clause.Body.Location);
                }
            }
            else if (clause.Body is ReturnStatement ret && ret.Value != null)
            {
                // Allow return statements in when expressions
                TypeSymbol branchType = AnalyzeExpression(expression: ret.Value);

                if (resultType == null)
                {
                    resultType = branchType;
                }
            }
            else if (clause.Body is BlockStatement block)
            {
                // For block statements in when expressions, we need to validate 'becomes' usage
                // and extract the result type from the becomes statement
                BecomesStatement? becomesStmt = null;
                int statementCount = 0;

                foreach (Statement stmt in block.Statements)
                {
                    AnalyzeStatement(statement: stmt);
                    statementCount++;

                    if (stmt is BecomesStatement becomes)
                    {
                        becomesStmt = becomes;
                    }
                }

                if (becomesStmt != null)
                {
                    // Found a becomes statement - check if it's a single-statement block
                    if (statementCount == 1)
                    {
                        // Block contains only 'becomes expr' - should use => syntax instead
                        ReportError(
                            SemanticDiagnosticCode.SingleExpressionBranchUsesBecomes,
                            "Single-expression when branch should use '=>' syntax instead of block with 'becomes'.",
                            becomesStmt.Location);
                    }

                    // Extract the result type from the becomes expression (already analyzed via AnalyzeStatement)
                    TypeSymbol branchType = becomesStmt.Value.ResolvedType ?? ErrorTypeInfo.Instance;

                    if (resultType == null)
                    {
                        resultType = branchType;
                    }
                    else if (!IsAssignableTo(source: branchType, target: resultType))
                    {
                        ReportError(
                            SemanticDiagnosticCode.WhenBranchTypeMismatch,
                            $"When expression branches have incompatible types: '{resultType.Name}' and '{branchType.Name}'.",
                            becomesStmt.Location);
                    }
                }
                else if (statementCount > 0)
                {
                    // Multi-statement block without 'becomes' in a when expression
                    ReportError(
                        SemanticDiagnosticCode.WhenExpressionBlockMissingBecomes,
                        "Multi-statement block in when expression requires 'becomes' to specify the result value.",
                        block.Location);
                }
            }
            else
            {
                // Analyze as regular statement
                AnalyzeStatement(statement: clause.Body);
            }

            _registry.ExitScope();
        }

        // Check exhaustiveness — when expressions MUST produce a value for all inputs
        if (!hasElse)
        {
            ExhaustivenessResult exhaustiveness = CheckExhaustiveness(
                clauses: when.Clauses,
                matchedType: matchedType);

            if (!exhaustiveness.IsExhaustive)
            {
                string missing = exhaustiveness.MissingCases.Count > 0
                    ? $" Missing cases: {string.Join(separator: ", ", values: exhaustiveness.MissingCases)}."
                    : "";
                ReportError(
                    SemanticDiagnosticCode.NonExhaustiveMatch,
                    $"When expression is not exhaustive — all possible values must be handled.{missing}",
                    when.Location);
            }
        }

        return resultType ?? ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeGenericMethodCallExpression(GenericMethodCallExpression generic)
    {
        TypeSymbol objectType = AnalyzeExpression(expression: generic.Object);

        // Resolve type arguments
        var typeArgs = new List<TypeSymbol>();
        foreach (TypeExpression typeArg in generic.TypeArguments)
        {
            typeArgs.Add(item: ResolveType(typeExpr: typeArg));
        }

        // Look up the method
        RoutineInfo? method = _registry.LookupRoutine(fullName: $"{objectType.Name}.{generic.MethodName}");
        if (method?.ReturnType != null)
        {
            // Substitute type arguments in return type
            return method.ReturnType;
        }

        // Analyze arguments
        foreach (Expression arg in generic.Arguments)
        {
            AnalyzeExpression(expression: arg);
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeGenericMemberExpression(GenericMemberExpression genericMember)
    {
        AnalyzeExpression(expression: genericMember.Object);

        // Resolve type arguments
        foreach (TypeExpression typeArg in genericMember.TypeArguments)
        {
            ResolveType(typeExpr: typeArg);
        }

        // Look up the member with type arguments
        // TODO: Implement proper generic member resolution

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeIntrinsicCallExpression(IntrinsicCallExpression intrinsic)
    {
        // Intrinsic calls require being in a danger block
        if (!InDangerBlock)
        {
            ReportError(
                SemanticDiagnosticCode.IntrinsicOutsideDanger,
                $"Intrinsic call '@intrinsic.{intrinsic.IntrinsicName}' can only be used inside a danger block.",
                intrinsic.Location);
        }

        foreach (Expression arg in intrinsic.Arguments)
        {
            AnalyzeExpression(expression: arg);
        }

        // Return type depends on the specific intrinsic
        // For now, return based on type arguments if available
        if (intrinsic.TypeArguments.Count > 0)
        {
            TypeSymbol? type = _registry.LookupType(name: intrinsic.TypeArguments[0]);
            if (type != null)
            {
                return type;
            }
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeNativeCallExpression(NativeCallExpression native)
    {
        // Native calls require being in a danger block
        if (!InDangerBlock)
        {
            ReportError(
                SemanticDiagnosticCode.NativeOutsideDanger,
                $"Native call '@native.{native.FunctionName}' can only be used inside a danger block.",
                native.Location);
        }

        foreach (Expression arg in native.Arguments)
        {
            AnalyzeExpression(expression: arg);
        }

        // Native calls return platform-dependent types
        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeIsPatternExpression(IsPatternExpression isPat)
    {
        TypeSymbol exprType = AnalyzeExpression(expression: isPat.Expression);

        // Analyze the pattern (may bind variables)
        AnalyzePattern(pattern: isPat.Pattern, matchedType: exprType);

        // 'is' expressions always return bool
        return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeFlagsTestExpression(FlagsTestExpression flagsTest)
    {
        TypeSymbol subjectType = AnalyzeExpression(expression: flagsTest.Subject);

        if (subjectType.Category == TypeCategory.Error)
        {
            return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;
        }

        if (subjectType is not FlagsTypeInfo flagsType)
        {
            ReportError(
                SemanticDiagnosticCode.FlagsTypeMismatch,
                $"Flags test operators (is/isnot/isonly) require a flags type, but got '{subjectType.Name}'.",
                flagsTest.Location);
            return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;
        }

        // #133: isonly rejects 'or' and 'but'
        if (flagsTest.Kind == FlagsTestKind.IsOnly)
        {
            if (flagsTest.Connective == FlagsTestConnective.Or)
            {
                ReportError(
                    SemanticDiagnosticCode.FlagsIsOnlyRejectsOrBut,
                    "'isonly' cannot be used with 'or'. Use 'and' to specify the exact set of flags.",
                    flagsTest.Location);
            }

            if (flagsTest.ExcludedFlags is { Count: > 0 })
            {
                ReportError(
                    SemanticDiagnosticCode.FlagsIsOnlyRejectsOrBut,
                    "'isonly' cannot be used with 'but'. Specify the exact set of flags directly.",
                    flagsTest.Location);
            }
        }

        // Validate each flag name exists in the type
        foreach (string flagName in flagsTest.TestFlags)
        {
            if (flagsType.Members.All(m => m.Name != flagName))
            {
                ReportError(
                    SemanticDiagnosticCode.FlagsMemberNotFound,
                    $"Flags type '{flagsType.Name}' does not have a member named '{flagName}'.",
                    flagsTest.Location);
            }
        }

        // Validate excluded flags too
        if (flagsTest.ExcludedFlags != null)
        {
            foreach (string flagName in flagsTest.ExcludedFlags)
            {
                if (flagsType.Members.All(m => m.Name != flagName))
                {
                    ReportError(
                        SemanticDiagnosticCode.FlagsMemberNotFound,
                        $"Flags type '{flagsType.Name}' does not have a member named '{flagName}'.",
                        flagsTest.Location);
                }
            }
        }

        return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;
    }

    private TypeSymbol HandleUnknownExpression(Expression expression)
    {
        ReportWarning(SemanticWarningCode.UnknownExpressionType, $"Unknown expression type: {expression.GetType().Name}", expression.Location);
        return ErrorTypeInfo.Instance;
    }

    /// <summary>
    /// Analyzes an inserted text expression (f-string).
    /// Validates all embedded expressions and returns Text type.
    /// </summary>
    private TypeSymbol AnalyzeInsertedTextExpression(InsertedTextExpression insertedText)
    {
        foreach (InsertedTextPart part in insertedText.Parts)
        {
            if (part is ExpressionPart exprPart)
            {
                AnalyzeExpression(expression: exprPart.Expression);
            }
        }

        return _registry.LookupType(name: "Text") ?? ErrorTypeInfo.Instance;
    }

    /// <summary>
    /// Substitutes type parameters in a type based on a generic resolution.
    /// For example, if genericType is List&lt;S32&gt; and type is T, returns S32.
    /// </summary>
    /// <param name="type">The type that may contain type parameters.</param>
    /// <param name="genericType">The resolved generic type providing type argument bindings.</param>
    /// <returns>The substituted type.</returns>
    private TypeSymbol SubstituteTypeParameters(TypeSymbol type, TypeSymbol genericType)
    {
        if (genericType.TypeArguments == null || genericType.TypeArguments.Count == 0)
        {
            return type;
        }

        // Get the generic definition to find type parameter names
        TypeSymbol? genericDef = GetGenericDefinition(resolution: genericType);
        if (genericDef == null)
        {
            return type;
        }

        // Build a mapping from type parameter names to actual types
        IReadOnlyList<string>? typeParamNames = genericDef.GenericParameters;
        if (typeParamNames == null || typeParamNames.Count != genericType.TypeArguments.Count)
        {
            return type;
        }

        var substitutions = new Dictionary<string, TypeSymbol>();
        for (int i = 0; i < typeParamNames.Count; i++)
        {
            substitutions[typeParamNames[i]] = genericType.TypeArguments[i];
        }

        return SubstituteWithMapping(type: type, substitutions: substitutions);
    }

    /// <summary>
    /// Gets the generic definition from a resolution.
    /// </summary>
    private TypeSymbol? GetGenericDefinition(TypeSymbol resolution)
    {
        if (!resolution.IsGenericResolution)
        {
            return null;
        }

        // Extract base name (e.g., "List" from "List[S32]")
        string baseName = GetBaseTypeName(typeName: resolution.Name);
        return _registry.LookupType(name: baseName);
    }

    /// <summary>
    /// Substitutes type parameters using a mapping.
    /// </summary>
    private TypeSymbol SubstituteWithMapping(TypeSymbol type, Dictionary<string, TypeSymbol> substitutions)
    {
        // Direct type parameter replacement
        if (substitutions.TryGetValue(type.Name, out TypeSymbol? replacement))
        {
            return replacement;
        }

        // For generic resolutions, recursively substitute in type arguments
        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            var substitutedArgs = new List<TypeSymbol>();
            bool anyChanged = false;

            foreach (TypeSymbol arg in type.TypeArguments)
            {
                TypeSymbol substitutedArg = SubstituteWithMapping(type: arg, substitutions: substitutions);
                substitutedArgs.Add(substitutedArg);
                if (!ReferenceEquals(substitutedArg, arg))
                {
                    anyChanged = true;
                }
            }

            if (anyChanged)
            {
                // Create a new resolution with substituted arguments
                TypeSymbol? baseDef = GetGenericDefinition(resolution: type);
                if (baseDef != null)
                {
                    return _registry.GetOrCreateResolution(genericDef: baseDef, typeArguments: substitutedArgs);
                }
            }
        }

        return type;
    }

    /// <summary>
    /// Analyzes a steal expression (RazorForge only).
    /// Validates that the operand can be stolen and returns the stolen type.
    /// </summary>
    /// <param name="steal">The steal expression to analyze.</param>
    /// <returns>The type of the stolen value.</returns>
    /// <remarks>
    /// Stealable types:
    /// - Raw entities (direct entity references)
    /// - Shared[T] (shared ownership handle)
    /// - Tracked[T] (reference-counted handle)
    ///
    /// Non-stealable types (build error):
    /// - Viewed[T] (read-only token, scope-bound)
    /// - Hijacked[T] (exclusive token, scope-bound)
    /// - Inspected[T] (thread-safe read token, scope-bound)
    /// - Seized[T] (thread-safe exclusive token, scope-bound)
    /// - Snatched[T] (internal ownership, not for user code)
    /// </remarks>
    private TypeSymbol AnalyzeStealExpression(StealExpression steal)
    {
        // Analyze the operand
        TypeSymbol operandType = AnalyzeExpression(expression: steal.Operand);

        // Check if the type is a memory token (cannot be stolen)
        if (IsMemoryToken(type: operandType))
        {
            string tokenKind = GetMemoryTokenKind(type: operandType);
            ReportError(
                SemanticDiagnosticCode.StealScopeBoundToken,
                $"Cannot steal '{tokenKind}' - scope-bound tokens cannot be stolen. " +
                $"Only raw entities, Shared[T], and Tracked[T] can be stolen.",
                steal.Location);
            return operandType;
        }

        // Check for Snatched[T] (internal ownership, not for user code)
        if (IsSnatched(type: operandType))
        {
            ReportError(
                SemanticDiagnosticCode.StealSnatched,
                "Cannot steal 'Snatched[T]' - internal ownership type cannot be stolen.",
                steal.Location);
            return operandType;
        }

        // For Shared[T] or Tracked[T], return the inner type
        if (IsStealableHandle(type: operandType))
        {
            // Unwrap the handle to get the inner type
            if (operandType.TypeArguments is { Count: > 0 })
            {
                return operandType.TypeArguments[0];
            }
        }

        // For raw entities (not wrapped), return the same type
        // The steal operation moves ownership, making the source a deadref
        return operandType;
    }

    /// <summary>
    /// Checks if a type is a memory token (Viewed, Hijacked, Inspected, Seized).
    /// Memory tokens are scope-bound and cannot be stolen.
    /// </summary>
    private static bool IsMemoryToken(TypeSymbol type)
    {
        return type.Name is "Viewed" or "Hijacked" or "Inspected" or "Seized"
            || (type.Name.StartsWith(value: "Viewed[") ||
                type.Name.StartsWith(value: "Hijacked[") ||
                type.Name.StartsWith(value: "Inspected[") ||
                type.Name.StartsWith(value: "Seized["));
    }

    /// <summary>
    /// Gets the kind of memory token for error messages.
    /// </summary>
    private static string GetMemoryTokenKind(TypeSymbol type)
    {
        if (type.Name.StartsWith(value: "Viewed")) return "Viewed[T]";
        if (type.Name.StartsWith(value: "Hijacked")) return "Hijacked[T]";
        if (type.Name.StartsWith(value: "Inspected")) return "Inspected[T]";
        if (type.Name.StartsWith(value: "Seized")) return "Seized[T]";
        return type.Name;
    }

    /// <summary>
    /// Checks if a type is Snatched[T] (internal ownership type).
    /// </summary>
    private static bool IsSnatched(TypeSymbol type)
    {
        return type.Name == "Snatched" || type.Name.StartsWith(value: "Snatched[");
    }

    /// <summary>
    /// Checks if a type is a stealable handle (Shared[T] or Tracked[T]).
    /// </summary>
    private static bool IsStealableHandle(TypeSymbol type)
    {
        return type.Name is "Shared" or "Tracked"
            || type.Name.StartsWith(value: "Shared[")
            || type.Name.StartsWith(value: "Tracked[");
    }

    /// <summary>
    /// Analyzes a backindex expression (^n = index from end).
    /// Validates that the operand is a non-negative integer type.
    /// </summary>
    /// <param name="back">The back index expression to analyze.</param>
    /// <returns>The BackIndex type.</returns>
    /// <remarks>
    /// BackIndex expressions create indices that count from the end of a sequence:
    /// - ^1 = last element
    /// - ^2 = second to last element
    /// - ^0 = one past the end (valid for slicing, not indexing)
    ///
    /// Used with IndexExpression for end-relative indexing: list[^1], text[^3]
    /// </remarks>
    private TypeSymbol AnalyzeBackIndexExpression(BackIndexExpression back)
    {
        // Analyze the operand
        TypeSymbol operandType = AnalyzeExpression(expression: back.Operand);

        // Validate that the operand is an integer type
        if (!IsIntegerType(type: operandType))
        {
            ReportError(
                SemanticDiagnosticCode.BackIndexRequiresInteger,
                $"BackIndex operator '^' requires an integer operand, got '{operandType.Name}'.",
                back.Location);
        }

        // Return a BackIndex type (or UAddr as the underlying representation)
        // BackIndex is conceptually a wrapper around an offset from the end
        TypeSymbol? backIndexType = _registry.LookupType(name: "BackIndex");
        if (backIndexType != null)
        {
            return backIndexType;
        }

        // Fallback: return UAddr as the index representation
        return _registry.LookupType(name: "UAddr") ?? operandType;
    }

    /// <summary>
    /// Creates a RoutineTypeInfo from a RoutineInfo for first-class function references.
    /// </summary>
    /// <param name="routine">The routine to create a type for.</param>
    /// <returns>The function type representing this routine's signature.</returns>
    private RoutineTypeInfo GetRoutineType(RoutineInfo routine)
    {
        // Extract parameter types from ParameterInfo
        List<TypeSymbol> parameterTypes = routine.Parameters
            .Select(selector: p => p.Type)
            .ToList();

        // Get return type (null means Blank/void)
        TypeSymbol? returnType = routine.ReturnType;

        // Create or retrieve the cached function type
        return _registry.GetOrCreateRoutineType(
            parameterTypes: parameterTypes,
            returnType: returnType,
            isFailable: routine.IsFailable);
    }

    /// <summary>
    /// Analyzes a waitfor expression (async/concurrency).
    /// Waits for an async operation to complete, optionally with a timeout.
    /// </summary>
    /// <param name="waitfor">The waitfor expression to analyze.</param>
    /// <returns>The type of the awaited value.</returns>
    private TypeSymbol AnalyzeWaitforExpression(WaitforExpression waitfor)
    {
        // Analyze the operand (the async operation to wait for)
        TypeSymbol operandType = AnalyzeExpression(expression: waitfor.Operand);

        // Analyze optional timeout expression
        if (waitfor.Timeout != null)
        {
            TypeSymbol timeoutType = AnalyzeExpression(expression: waitfor.Timeout);

            // Validate that timeout is a Duration type
            TypeSymbol? durationType = _registry.LookupType(name: "Duration");
            if (durationType != null && !IsAssignableTo(source: timeoutType, target: durationType))
            {
                ReportError(
                    SemanticDiagnosticCode.WaitforTimeoutNotDuration,
                    $"Waitfor 'within' clause requires a Duration, got '{timeoutType.Name}'.",
                    waitfor.Timeout.Location);
            }
        }

        // TODO: Validate that we're inside a suspended/threaded routine

        // The result type is the inner type of the async operation
        // For now, return the operand type directly
        return operandType;
    }

    /// <summary>
    /// Analyzes a dependent waitfor expression (task dependency graph).
    /// Syntax: after dep1 [as val1], dep2 [as val2] waitfor expr [within timeout]
    /// </summary>
    /// <param name="depWaitfor">The dependent waitfor expression to analyze.</param>
    /// <returns>Lookup&lt;T&gt; where T is the result type of the awaited operation.</returns>
    private TypeSymbol AnalyzeDependentWaitforExpression(DependentWaitforExpression depWaitfor)
    {
        // Create a new scope for the dependency bindings
        _registry.EnterScope(kind: ScopeKind.Block, name: "waitfor_deps");

        // Analyze each dependency
        foreach (TaskDependency dep in depWaitfor.Dependencies)
        {
            TypeSymbol depType = AnalyzeExpression(expression: dep.DependencyExpr);

            // Dependency must be Lookup<T> type
            if (depType is not ErrorHandlingTypeInfo { Kind: ErrorHandlingKind.Lookup } lookupType)
            {
                ReportError(
                    SemanticDiagnosticCode.DependencyNotLookupType,
                    $"Task dependency must be a Lookup[T] type, got '{depType.Name}'.",
                    dep.Location);

                // If there's a binding, still declare it (as error type) to prevent cascading errors
                if (dep.BindingName != null)
                {
                    _registry.DeclareVariable(name: dep.BindingName, type: ErrorTypeInfo.Instance);
                }
            }
            else if (dep.BindingName != null)
            {
                // Introduce the binding variable with the unwrapped type T from Lookup<T>
                _registry.DeclareVariable(name: dep.BindingName, type: lookupType.ValueType);
            }
        }

        // Analyze the operand expression (with dependency bindings in scope)
        TypeSymbol operandType = AnalyzeExpression(expression: depWaitfor.Operand);

        // Analyze optional timeout expression
        if (depWaitfor.Timeout != null)
        {
            TypeSymbol timeoutType = AnalyzeExpression(expression: depWaitfor.Timeout);

            // Validate that timeout is a Duration type
            TypeSymbol? durationType = _registry.LookupType(name: "Duration");
            if (durationType != null && !IsAssignableTo(source: timeoutType, target: durationType))
            {
                ReportError(
                    SemanticDiagnosticCode.WaitforTimeoutNotDuration,
                    $"Waitfor 'within' clause requires a Duration, got '{timeoutType.Name}'.",
                    depWaitfor.Timeout.Location);
            }
        }

        // Exit the dependency scope
        _registry.ExitScope();

        // TODO: Validate that we're inside a suspended/threaded routine

        // Result type is Lookup<R> where R is the operand type
        return ErrorHandlingTypeInfo.WellKnown.LookupDefinition.CreateInstance(typeArguments: [operandType]);
    }

    #endregion
}
