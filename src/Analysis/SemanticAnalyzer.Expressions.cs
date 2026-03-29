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
    /// <summary>
    /// Collection types that support literal constructor syntax: TypeName(elem1, elem2, ...)
    /// </summary>
    private static readonly HashSet<string> CollectionLiteralTypes = new(StringComparer.Ordinal)
    {
        "List", "Set", "Dict", "Deque", "BitList",
        "SortedSet", "SortedList", "SortedDict",
        "ValueList", "ValueBitList", "PriorityQueue"
    };

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
            ListLiteralExpression list => AnalyzeListLiteralExpression(list: list, expectedType: expectedType),
            SetLiteralExpression set => AnalyzeSetLiteralExpression(set: set, expectedType: expectedType),
            DictLiteralExpression dict => AnalyzeDictLiteralExpression(dict: dict, expectedType: expectedType),
            TupleLiteralExpression tuple => AnalyzeTupleLiteralExpression(tuple: tuple),
            TypeConversionExpression conv => AnalyzeTypeConversionExpression(conv: conv),
            ChainedComparisonExpression chain => AnalyzeChainedComparisonExpression(chain: chain),
            BlockExpression block => AnalyzeBlockExpression(block: block),
            WithExpression with => AnalyzeWithExpression(with: with),
            NamedArgumentExpression named => AnalyzeExpression(expression: named.Value),
            DictEntryLiteralExpression dictEntry => AnalyzeDictEntryLiteralExpression(dictEntry: dictEntry),
            GenericMethodCallExpression generic => AnalyzeGenericMethodCallExpression(generic: generic),
            GenericMemberExpression genericMember => AnalyzeGenericMemberExpression(genericMember: genericMember),
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
    /// Checks if a type is a fixed-width integer type (S8-S128, U8-U128, Address).
    /// </summary>
    // TODO: remove this
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
                // Generic type parameter owners (e.g., T in "routine T.view()") —
                // return the GenericParameterTypeInfo directly, no registry lookup needed
                if (_currentRoutine.OwnerType is GenericParameterTypeInfo)
                {
                    return _currentRoutine.OwnerType;
                }

                // Re-lookup to get the updated type with resolved protocols/member variables
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
        // Try current module prefix for presets (e.g., "MY_CONST" → "MyModule.MY_CONST")
        if (varInfo == null && _currentModuleName != null && !id.Name.Contains('.'))
            varInfo = _registry.LookupVariable(name: $"{_currentModuleName}.{id.Name}");
        if (varInfo != null)
        {
            // #11: Deadref tracking — report error if variable was invalidated by steal
            if (_deadrefVariables.Contains(id.Name))
            {
                ReportError(
                    SemanticDiagnosticCode.UseAfterSteal,
                    $"Variable '{id.Name}' is a deadref — it was invalidated by a previous 'steal' or ownership transfer. " +
                    "The variable can no longer be used.",
                    id.Location);
                return ErrorTypeInfo.Instance;
            }

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
        // Strip '!' suffix for failable routine references (e.g., "stop!" → "stop")
        string routineLookupName = id.Name.EndsWith('!') ? id.Name[..^1] : id.Name;
        RoutineInfo? routine = _registry.LookupRoutine(fullName: routineLookupName);
        // Try current module prefix (e.g., "infinite_loop" → "HelloWorld.infinite_loop")
        if (routine == null && _currentModuleName != null && !routineLookupName.Contains('.'))
            routine = _registry.LookupRoutine(fullName: $"{_currentModuleName}.{routineLookupName}");
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
    /// in the parser (e.g., a + b → a.$add(b)). This method only handles operators that
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

        // #117: Fixed-width numeric types must match exactly (S32 + S64 = error)
        // System types (Address) are exempt
        if (leftType.Name != rightType.Name
            && IsFixedWidthNumericType(type: leftType)
            && IsFixedWidthNumericType(type: rightType)
            && !IsLogicalOperator(op: binary.Operator)
            && !IsComparisonOperator(op: binary.Operator))
        {
            ReportError(
                SemanticDiagnosticCode.FixedWidthTypeMismatch,
                $"Fixed-width type mismatch: '{leftType.Name}' and '{rightType.Name}'. Explicit conversion required.",
                binary.Location);
            return ErrorTypeInfo.Instance;
        }

        // Flags combination: A and B → bitwise OR (combines flags)
        if (binary.Operator == BinaryOperator.And
            && leftType is FlagsTypeInfo
            && leftType.Name == rightType.Name)
        {
            return leftType;
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

        // Validate RHS type against the operator method's parameter type
        {
            string? methodName = binary.Operator.GetMethodName();
            if (methodName != null)
            {
                RoutineInfo? method = _registry.LookupMethod(type: leftType, methodName: methodName);
                if (method is { Parameters.Count: > 0 })
                {
                    TypeSymbol paramType = method.Parameters[0].Type;

                    // Substitute Me → leftType for protocol-sourced methods
                    if (paramType is ProtocolSelfTypeInfo)
                        paramType = leftType;

                    // Substitute generic type parameters (e.g., List[S32].$add → T becomes S32)
                    if (leftType is { IsGenericResolution: true, TypeArguments: not null })
                        paramType = SubstituteTypeParameters(type: paramType, genericType: leftType);

                    if (!IsAssignableTo(source: rightType, target: paramType))
                    {
                        ReportError(
                            SemanticDiagnosticCode.ArgumentTypeMismatch,
                            $"Operator '{binary.Operator.ToStringRepresentation()}': cannot convert '{rightType.Name}' to '{paramType.Name}'.",
                            binary.Location);
                        return ErrorTypeInfo.Instance;
                    }

                    // Return the method's actual return type instead of blindly returning leftType
                    TypeSymbol returnType = method.ReturnType ?? leftType;
                    if (returnType is ProtocolSelfTypeInfo)
                        returnType = leftType;
                    if (leftType is { IsGenericResolution: true, TypeArguments: not null })
                        returnType = SubstituteTypeParameters(type: returnType, genericType: leftType);
                    return returnType;
                }
            }
        }

        // Default: return left type
        // This handles any edge cases that might slip through
        return leftType;
    }

    /// <summary>
    /// Analyzes an assignment expression (target = value).
    /// Validates mutability, member variable access, and type compatibility.
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
        // #173: Tuple assignment destructuring — (a, b) = (b, a)
        if (target is TupleLiteralExpression tupleLhs)
        {
            // Verify all elements of the LHS tuple are assignable targets
            foreach (Expression element in tupleLhs.Elements)
            {
                if (!IsAssignableTarget(target: element))
                {
                    ReportError(
                        SemanticDiagnosticCode.InvalidAssignmentTarget,
                        "All elements of tuple destructuring must be assignable targets (variables, member accesses, or indices).",
                        element.Location);
                }

                // Check modifiability for identifier elements
                if (element is IdentifierExpression elemId)
                {
                    VariableInfo? varInfo = _registry.LookupVariable(name: elemId.Name);
                    if (varInfo is { IsModifiable: false })
                    {
                        ReportError(
                            SemanticDiagnosticCode.AssignmentToImmutable,
                            $"Cannot assign to preset variable '{elemId.Name}'.",
                            location);
                    }
                }
            }

            // Check that RHS is a tuple with matching arity
            if (valueType is TupleTypeInfo tupleType)
            {
                if (tupleLhs.Elements.Count != tupleType.ElementTypes.Count)
                {
                    ReportError(
                        SemanticDiagnosticCode.DestructuringArityMismatch,
                        $"Tuple destructuring has {tupleLhs.Elements.Count} targets but the value has {tupleType.ElementTypes.Count} elements.",
                        location);
                }
            }

            return targetType;
        }

        // Check if target is assignable (variable, member variable, or index)
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

        // Validate member variable write access (setter visibility)
        if (target is MemberExpression member)
        {
            TypeSymbol objectType = AnalyzeExpression(expression: member.Object);

            // Read-only wrapper types (Viewed, Inspected) cannot be written through
            if (IsReadOnlyWrapper(type: objectType))
            {
                ReportError(
                    SemanticDiagnosticCode.WriteThroughReadOnlyWrapper,
                    $"Cannot write to member '{member.PropertyName}' through read-only wrapper '{objectType.Name}'. " +
                    "Use Hijacked[T] for exclusive write access or Seized[T] for locked write access.",
                    location);
            }

            ValidateMemberVariableWriteAccess(objectType: objectType, memberVariableName: member.PropertyName, location: location);

            // Check if we're in a @readonly method trying to modify 'me'
            if (_currentRoutine is { IsReadOnly: true } &&
                member.Object is IdentifierExpression { Name: "me" })
            {
                ReportError(
                    SemanticDiagnosticCode.ModificationInReadonlyMethod,
                    $"Cannot modify member variable '{member.PropertyName}' in a @readonly method. " +
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

        // #42: ??= narrowing — `a ??= b` is expanded to `a = a ?? b`
        // When assigning `target = target ?? default` where target is Maybe[T],
        // narrow the variable to T after the coalescing assignment.
        if (target is IdentifierExpression narrowId
            && value is BinaryExpression { Operator: BinaryOperator.NoneCoalesce }
            && targetType is ErrorHandlingTypeInfo { Kind: ErrorHandlingKind.Maybe } maybeType)
        {
            _registry.NarrowVariable(name: narrowId.Name, narrowedType: maybeType.ValueType);
        }

        // Assignment expression returns the target type
        return targetType;
    }

    /// <summary>
    /// Analyzes a compound assignment expression (e.g., a += b).
    /// Dispatch order: (0) verify target is var, (1) try in-place wired ($iadd) → Blank,
    /// (2) fallback to create-and-assign ($add) for non-entity types, (3) error if neither.
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
            ValidateMemberVariableWriteAccess(objectType: objectType, memberVariableName: member.PropertyName, location: compound.Location);

            if (_currentRoutine is { IsReadOnly: true } &&
                member.Object is IdentifierExpression { Name: "me" })
            {
                ReportError(
                    SemanticDiagnosticCode.ModificationInReadonlyMethod,
                    $"Cannot modify member variable '{member.PropertyName}' in a @readonly method. " +
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

        // #67: Cannot use compound assignment on read-only token (Viewed or Inspected)
        if (targetType is WrapperTypeInfo { IsReadOnly: true } readOnlyWrapper)
        {
            ReportError(
                SemanticDiagnosticCode.CompoundAssignmentOnReadOnlyToken,
                $"Cannot use compound assignment on read-only token '{readOnlyWrapper.Name}'. " +
                "Read-only tokens (Viewed, Inspected) do not allow modifications.",
                compound.Location);
            return ErrorTypeInfo.Instance;
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

        // Step 1: Try in-place wired ($iadd, etc.)
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
                // Create-and-assign: a = a.$add(b) — returns target type
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
            // Strip '!' suffix for failable calls (e.g., "stop!" → "stop")
            string callName = id.Name.EndsWith('!') ? id.Name[..^1] : id.Name;

            // Wired routines ($-prefixed) cannot be called directly by user code
            if (callName.StartsWith(value: '$'))
            {
                ReportError(
                    SemanticDiagnosticCode.DirectWiredRoutineCall,
                    $"Wired routine '{callName}' cannot be called directly. " +
                    "Use the corresponding language construct instead (e.g., '==' for $eq, 'for' for $iter).",
                    call.Location);
                return ErrorTypeInfo.Instance;
            }

            RoutineInfo? routine = _registry.LookupRoutine(fullName: callName);
            // Try current module prefix (e.g., "infinite_loop" → "HelloWorld.infinite_loop")
            if (routine == null && _currentModuleName != null && !callName.Contains('.'))
                routine = _registry.LookupRoutine(fullName: $"{_currentModuleName}.{callName}");

            // Overload resolution: if the found routine is non-generic and the first
            // argument doesn't match, try a specific or generic overload (e.g., show[T])
            if (routine != null && !routine.IsGenericDefinition
                && call.Arguments.Count > 0 && routine.Parameters.Count > 0)
            {
                Expression firstArg = call.Arguments[0] is NamedArgumentExpression na
                    ? na.Value : call.Arguments[0];
                TypeSymbol firstArgType = AnalyzeExpression(expression: firstArg);
                TypeSymbol firstParamType = routine.Parameters[0].Type;
                if (firstArgType != ErrorTypeInfo.Instance
                    && firstArgType.FullName != firstParamType.FullName
                    && !IsAssignableTo(firstArgType, firstParamType))
                {
                    RoutineInfo? better = _registry.LookupRoutineOverload(callName, [firstArgType]);
                    if (better != null && better != routine)
                    {
                        routine = better;
                        call.ResolvedRoutine = routine;
                    }
                    else
                    {
                        RoutineInfo? generic = _registry.LookupGenericOverload(callName);
                        if (generic != null)
                        {
                            var inferred = InferGenericTypeArguments(generic, call.Arguments);
                            routine = inferred != null ? generic.CreateInstance(inferred) : generic;
                            call.ResolvedRoutine = routine;
                        }
                    }
                }
            }

            if (routine != null)
            {
                // Import-gating: BuilderService standalone routines require 'import BuilderService'
                if (routine.IsSynthesized && BuilderInfoProvider.IsBuilderServiceStandalone(routine.Name)
                    && !_importedModules.Contains("BuilderService"))
                {
                    ReportError(SemanticDiagnosticCode.BuilderServiceImportRequired,
                        $"'{routine.Name}()' requires 'import BuilderService'.",
                        call.Location);
                    return ErrorTypeInfo.Instance;
                }

                // Track failable calls for error handling variant generation
                if (routine.IsFailable && _currentRoutine != null)
                {
                    _currentRoutine.HasFailableCalls = true;

                    // Non-failable routine (except start) cannot call failable routines
                    if (!_currentRoutine.IsFailable && _currentRoutine.Name != "start")
                    {
                        ReportError(
                            SemanticDiagnosticCode.UnhandledCrashableCall,
                            $"Failable routine '{routine.Name}!' called without error handling. " +
                            "Use 'when' to match the result, '??' to provide a default, or make the enclosing routine failable (!).",
                            call.Location);
                    }
                }

                // Validate routine access
                ValidateRoutineAccess(routine: routine, accessLocation: call.Location);

                AnalyzeCallArguments(routine: routine, arguments: call.Arguments, location: call.Location);

                // C29: Dispatch inference for varargs calls
                call.ResolvedDispatch = InferDispatchStrategy(routine, call);
                if (call.ResolvedDispatch == DispatchStrategy.Runtime && _registry.Language == Language.RazorForge)
                {
                    ReportError(SemanticDiagnosticCode.RuntimeDispatchNotSupported,
                        $"Runtime dispatch is not supported in RazorForge. " +
                        $"All varargs arguments to '{routine.Name}' must be the same concrete type.",
                        call.Location);
                }

                // Validate exclusive token uniqueness (cannot pass same Hijacked/Seized twice)
                ValidateExclusiveTokenUniqueness(arguments: call.Arguments, location: call.Location);

                // Return type is Blank if not specified (routines without explicit return type return Blank)
                return routine.ReturnType ?? _registry.LookupType("Blank") ?? ErrorTypeInfo.Instance;
            }

            // Could be a type creator
            TypeSymbol? type = LookupTypeWithImports(name: id.Name);
            if (type != null)
            {
                // Collection literal constructor: List(1, 2, 3), Set(1, 2), Dict(1:2, 3:4), etc.
                // Detected when: type name is a known collection AND args are positional/DictEntry (not named field inits)
                if (CollectionLiteralTypes.Contains(id.Name)
                    && call.Arguments.Count > 0
                    && call.Arguments.All(a => a is not NamedArgumentExpression))
                {
                    call.IsCollectionLiteral = true;

                    // Analyze all arguments
                    var argTypes = new List<TypeSymbol>();
                    foreach (Expression arg in call.Arguments)
                        argTypes.Add(item: AnalyzeExpression(expression: arg));

                    // Infer generic type arguments from elements and resolve collection type
                    if (type.IsGenericDefinition)
                    {
                        bool isMapType = id.Name is "Dict" or "SortedDict";
                        TypeSymbol resolvedType;

                        if (isMapType && call.Arguments[0] is DictEntryLiteralExpression firstEntry)
                        {
                            // K, V from first DictEntry
                            TypeSymbol keyType = firstEntry.Key.ResolvedType ?? ErrorTypeInfo.Instance;
                            TypeSymbol valueType = firstEntry.Value.ResolvedType ?? ErrorTypeInfo.Instance;
                            resolvedType = _registry.GetOrCreateResolution(genericDef: type, typeArguments: [keyType, valueType]);
                        }
                        else
                        {
                            // T from first positional arg
                            TypeSymbol elemType = argTypes[0];
                            resolvedType = _registry.GetOrCreateResolution(genericDef: type, typeArguments: [elemType]);
                        }

                        return resolvedType;
                    }

                    return type;
                }

                // Creator call - analyze arguments and validate
                var creatorArgTypes = new List<TypeSymbol>();
                foreach (Expression arg in call.Arguments)
                {
                    creatorArgTypes.Add(item: AnalyzeExpression(expression: arg));
                }

                // #115: Data boxing restrictions — certain types cannot be boxed to Data
                if (id.Name == "Data" && creatorArgTypes.Count > 0)
                {
                    TypeSymbol argType = creatorArgTypes[0];
                    if (argType is ErrorHandlingTypeInfo { Kind: ErrorHandlingKind.Result or ErrorHandlingKind.Lookup }
                        or VariantTypeInfo
                        or WrapperTypeInfo { IsReadOnly: true } // Viewed, Inspected
                        || (argType is WrapperTypeInfo wrapper
                            && wrapper.InnerType != null
                            && wrapper.Name is "Hijacked" or "Seized"))
                    {
                        ReportError(
                            SemanticDiagnosticCode.DataBoxingProhibited,
                            $"Type '{argType.Name}' cannot be boxed to Data. " +
                            "Result, Lookup, variants, and access tokens (Viewed, Hijacked, Inspected, Seized) cannot be stored in Data.",
                            call.Location);
                    }

                    // #116: Nested Data flattening — Data(Data(x)) should warn
                    if (argType.Name == "Data")
                    {
                        ReportWarning(
                            SemanticWarningCode.NestedDataWrapping,
                            "Nested Data wrapping is redundant. Data(Data(x)) should be flattened to Data(x).",
                            call.Location);
                    }
                }

                // S510: Type creators with 2+ fields require all named arguments
                int memberCount = type switch
                {
                    EntityTypeInfo e => e.MemberVariables.Count,
                    RecordTypeInfo r => r.MemberVariables.Count,
                    _ => 0
                };
                if (memberCount >= 2)
                {
                    foreach (Expression arg in call.Arguments)
                    {
                        if (arg is not NamedArgumentExpression)
                        {
                            ReportError(
                                SemanticDiagnosticCode.NamedArgumentRequired,
                                $"Type '{id.Name}' has {memberCount} fields - all constructor arguments must be named.",
                                arg.Location);
                        }
                    }
                }

                ValidateExclusiveTokenUniqueness(arguments: call.Arguments, location: call.Location);
                return type;
            }

            // Try module-prefixed routine lookup (e.g., Core.normalize_duration)
            // This is done after type creator check to avoid shadowing type creators
            // with identically-named convenience functions (e.g., "routine U32(from: U8)")
            routine = LookupRoutineWithImports(name: id.Name);

            // Overload resolution for import-resolved routines (e.g., show[T] from IO/Console)
            if (routine != null && !routine.IsGenericDefinition
                && call.Arguments.Count > 0 && routine.Parameters.Count > 0)
            {
                Expression firstArgImport = call.Arguments[0] is NamedArgumentExpression naImport
                    ? naImport.Value : call.Arguments[0];
                TypeSymbol firstArgTypeImport = AnalyzeExpression(expression: firstArgImport);
                TypeSymbol firstParamTypeImport = routine.Parameters[0].Type;
                if (firstArgTypeImport != ErrorTypeInfo.Instance
                    && firstArgTypeImport.FullName != firstParamTypeImport.FullName
                    && !IsAssignableTo(firstArgTypeImport, firstParamTypeImport))
                {
                    // Try module-qualified specific overload (e.g., "IO.show#S64")
                    RoutineInfo? betterImport = _registry.LookupRoutineOverload(routine.FullName,
                        [firstArgTypeImport]);
                    if (betterImport != null && betterImport != routine)
                    {
                        routine = betterImport;
                        call.ResolvedRoutine = routine;
                    }
                    else
                    {
                        RoutineInfo? genericImport = _registry.LookupGenericOverload(id.Name);
                        if (genericImport != null)
                        {
                            var inferredImport = InferGenericTypeArguments(genericImport, call.Arguments);
                            routine = inferredImport != null
                                ? genericImport.CreateInstance(inferredImport)
                                : genericImport;
                            call.ResolvedRoutine = routine;
                        }
                    }
                }
            }

            if (routine != null)
            {
                // Import-gating: BuilderService standalone routines require 'import BuilderService'
                if (routine.IsSynthesized && BuilderInfoProvider.IsBuilderServiceStandalone(routine.Name)
                    && !_importedModules.Contains("BuilderService"))
                {
                    ReportError(SemanticDiagnosticCode.BuilderServiceImportRequired,
                        $"'{routine.Name}()' requires 'import BuilderService'.",
                        call.Location);
                    return ErrorTypeInfo.Instance;
                }

                // Track failable calls for error handling variant generation
                if (routine.IsFailable && _currentRoutine != null)
                {
                    _currentRoutine.HasFailableCalls = true;

                    if (!_currentRoutine.IsFailable && _currentRoutine.Name != "start")
                    {
                        ReportError(
                            SemanticDiagnosticCode.UnhandledCrashableCall,
                            $"Failable routine '{routine.Name}!' called without error handling. " +
                            "Use 'when' to match the result, '??' to provide a default, or make the enclosing routine failable (!).",
                            call.Location);
                    }
                }

                ValidateRoutineAccess(routine: routine, accessLocation: call.Location);
                AnalyzeCallArguments(routine: routine, arguments: call.Arguments, location: call.Location);

                // C29: Dispatch inference for varargs calls
                call.ResolvedDispatch = InferDispatchStrategy(routine, call);
                if (call.ResolvedDispatch == DispatchStrategy.Runtime && _registry.Language == Language.RazorForge)
                {
                    ReportError(SemanticDiagnosticCode.RuntimeDispatchNotSupported,
                        $"Runtime dispatch is not supported in RazorForge. " +
                        $"All varargs arguments to '{routine.Name}' must be the same concrete type.",
                        call.Location);
                }

                ValidateExclusiveTokenUniqueness(arguments: call.Arguments, location: call.Location);

                return routine.ReturnType ?? _registry.LookupType("Blank") ?? ErrorTypeInfo.Instance;
            }
        }

        if (call.Callee is MemberExpression member)
        {
            TypeSymbol objectType = AnalyzeExpression(expression: member.Object);

            // Choice types cannot use any operator wired methods
            if (objectType is ChoiceTypeInfo && IsOperatorWired(name: member.PropertyName))
            {
                ReportError(
                    SemanticDiagnosticCode.ArithmeticOnChoiceType,
                    $"Operator '{member.PropertyName}' cannot be used with choice type '{objectType.Name}'. " +
                    "Choice types do not support operators. Use 'is' for case matching and regular methods for additional behavior.",
                    call.Location);
                return ErrorTypeInfo.Instance;
            }

            // #134/#135: Flags types cannot use any operator wired methods
            if (objectType is FlagsTypeInfo && IsOperatorWired(name: member.PropertyName))
            {
                ReportError(
                    SemanticDiagnosticCode.ArithmeticOnFlagsType,
                    $"Operator '{member.PropertyName}' cannot be used with flags type '{objectType.Name}'. " +
                    "Use 'but' to remove flags and 'is'/'isnot'/'isonly' to test flags.",
                    call.Location);
                return ErrorTypeInfo.Instance;
            }

            // Wired routines ($-prefixed) cannot be called directly by user code
            if (member.PropertyName.StartsWith(value: '$'))
            {
                ReportError(
                    SemanticDiagnosticCode.DirectWiredRoutineCall,
                    $"Wired routine '{member.PropertyName}' cannot be called directly. " +
                    "Use the corresponding language construct instead (e.g., '==' for $eq, 'for' for $iter).",
                    call.Location);
                return ErrorTypeInfo.Instance;
            }

            // #137: Nested hijacking detection — checked before method resolution
            // since hijack() is generic extension T.hijack() that may not resolve by concrete type name
            if (member.PropertyName == "hijack" && IsNestedHijacking(source: member.Object))
            {
                ReportError(
                    SemanticDiagnosticCode.NestedHijackingNotAllowed,
                    "Cannot hijack a member of an already-hijacked object. " +
                    "Hijack the parent entity directly instead.",
                    call.Location);
            }

            string callLookupName = member.PropertyName.EndsWith('!') ? member.PropertyName[..^1] : member.PropertyName;
            RoutineInfo? method = _registry.LookupMethod(type: objectType, methodName: callLookupName);

            if (method != null)
            {
                // Import-gating: BuilderService routines require 'import BuilderService'
                if (method.IsSynthesized && BuilderInfoProvider.IsBuilderServiceRoutine(method.Name)
                    && !_importedModules.Contains("BuilderService"))
                {
                    ReportError(SemanticDiagnosticCode.BuilderServiceImportRequired,
                        $"'{method.Name}()' requires 'import BuilderService'.",
                        call.Location);
                    return ErrorTypeInfo.Instance;
                }

                // Track failable calls for error handling variant generation
                if (method.IsFailable && _currentRoutine != null)
                {
                    _currentRoutine.HasFailableCalls = true;

                    if (!_currentRoutine.IsFailable && _currentRoutine.Name != "start")
                    {
                        ReportError(
                            SemanticDiagnosticCode.UnhandledCrashableCall,
                            $"Failable routine '{method.Name}!' called without error handling. " +
                            "Use 'when' to match the result, '??' to provide a default, or make the enclosing routine failable (!).",
                            call.Location);
                    }
                }

                // #151: Static/instance mismatch — common routine called on instance
                if (method.IsCommon && member.Object is IdentifierExpression instanceId
                    && LookupTypeWithImports(name: instanceId.Name) == null)
                {
                    ReportError(
                        SemanticDiagnosticCode.CommonRoutineMismatch,
                        $"Common routine '{method.Name}' must be called on the type '{objectType.Name}', not on an instance.",
                        call.Location);
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

                AnalyzeCallArguments(routine: method, arguments: call.Arguments, location: call.Location,
                    callObjectType: objectType);

                // C29: Dispatch inference for varargs calls
                call.ResolvedDispatch = InferDispatchStrategy(method, call);
                if (call.ResolvedDispatch == DispatchStrategy.Runtime && _registry.Language == Language.RazorForge)
                {
                    ReportError(SemanticDiagnosticCode.RuntimeDispatchNotSupported,
                        $"Runtime dispatch is not supported in RazorForge. " +
                        $"All varargs arguments to '{method.Name}' must be the same concrete type.",
                        call.Location);
                }

                // #68: Real-to-Complex promotion — only $add/$sub allow float↔complex cross-type
                if (IsOperatorWired(name: member.PropertyName)
                    && member.PropertyName is not ("$add" or "$sub" or "$iadd" or "$isub")
                    && call.Arguments.Count > 0
                    && method.Parameters.Count > 0)
                {
                    TypeSymbol argType = method.Parameters[0].Type;
                    if ((IsFloatType(type: objectType) && IsComplexType(type: argType))
                        || (IsComplexType(type: objectType) && IsFloatType(type: argType)))
                    {
                        ReportError(
                            SemanticDiagnosticCode.RealComplexPromotionInvalid,
                            $"Operator '{member.PropertyName}' does not allow real↔complex promotion. " +
                            "Only '+' and '-' support implicit real-to-complex conversion. Use explicit conversion for other operators.",
                            call.Location);
                    }
                }

                // #12: Partial access rule — entity.field.view() is not allowed
                if (member.PropertyName is "view" or "hijack"
                    && member.Object is MemberExpression innerMember)
                {
                    TypeSymbol innerObjectType = innerMember.Object.ResolvedType ?? ErrorTypeInfo.Instance;
                    if (innerObjectType is EntityTypeInfo)
                    {
                        ReportError(
                            SemanticDiagnosticCode.PartialAccessOnEntity,
                            $"Cannot call '.{member.PropertyName}()' on entity member variable '{innerMember.PropertyName}'. " +
                            $"Access the entity directly instead of its individual member variables.",
                            call.Location);
                    }
                }

                // #137: Nested hijacking detection
                if (member.PropertyName == "hijack" && IsNestedHijacking(source: member.Object))
                {
                    ReportError(
                        SemanticDiagnosticCode.NestedHijackingNotAllowed,
                        "Cannot hijack a member of an already-hijacked object. " +
                        "Hijack the parent entity directly instead.",
                        call.Location);
                }

                // #92: Re-hijacking prohibition — cannot hijack an already-hijacked token
                if (member.PropertyName == "hijack" && IsHijackedType(type: objectType))
                {
                    ReportError(
                        SemanticDiagnosticCode.ReHijackingProhibited,
                        $"Cannot re-hijack an already-hijacked token '{objectType.Name}'. " +
                        "The entity is already exclusively accessed.",
                        call.Location);
                }

                // #170: Downgrade prohibition — cannot call .view() on Hijacked/Seized
                if (member.PropertyName == "view" && (IsHijackedType(type: objectType) || IsSeizedType(type: objectType)))
                {
                    ReportError(
                        SemanticDiagnosticCode.TokenDowngradeProhibited,
                        $"Cannot downgrade '{objectType.Name}' with '.view()'. " +
                        "Hijacked/Seized tokens already have write access — use them directly.",
                        call.Location);
                }

                // #97: Snatched[T] method calls require danger! block
                if (IsSnatched(type: objectType) && !InDangerBlock)
                {
                    ReportError(
                        SemanticDiagnosticCode.SnatchedRequiresDanger,
                        $"Method call on 'Snatched[T]' type requires a 'danger!' block. " +
                        "Snatched values bypass ownership safety checks.",
                        call.Location);
                }

                // #98: .snatch() on Shared/Tracked requires danger! block
                if (member.PropertyName == "snatch" && !InDangerBlock
                    && (IsSharedType(type: objectType) || IsTrackedType(type: objectType)))
                {
                    ReportError(
                        SemanticDiagnosticCode.SnatchRequiresDanger,
                        $"Calling '.snatch()' on '{objectType.Name}' requires a 'danger!' block. " +
                        "Snatching bypasses reference counting safety.",
                        call.Location);
                }

                // #100/#101: inspect!/seize! only valid on Shared entity handles
                if (member.PropertyName is "inspect" or "seize"
                    && !IsSharedType(type: objectType)
                    && objectType is not ErrorTypeInfo)
                {
                    ReportError(
                        member.PropertyName == "inspect"
                            ? SemanticDiagnosticCode.InspectRequiresMultiRead
                            : SemanticDiagnosticCode.ReadOnlyRejectsLocking,
                        $"'{member.PropertyName}!()' is only valid on Shared handles. " +
                        $"'{objectType.Name}' is not a Shared handle.",
                        call.Location);
                }

                // #19: Lock policy validation — inspect!/seize! must match the lock policy
                if (member.PropertyName is "inspect" or "seize"
                    && member.Object is IdentifierExpression lockPolicyTarget
                    && _variableLockPolicies.TryGetValue(lockPolicyTarget.Name, out string? policy))
                {
                    if (member.PropertyName == "inspect" && policy == "Exclusive")
                    {
                        ReportError(
                            SemanticDiagnosticCode.InspectRequiresMultiRead,
                            $"Cannot use 'inspect!()' on '{lockPolicyTarget.Name}' — it uses Exclusive lock policy. " +
                            "Exclusive locks do not support concurrent readers. Use 'seize!()' instead.",
                            call.Location);
                    }

                    if (member.PropertyName == "seize" && policy == "ReadOnly")
                    {
                        ReportError(
                            SemanticDiagnosticCode.ReadOnlyRejectsLocking,
                            $"Cannot use 'seize!()' on '{lockPolicyTarget.Name}' — it uses ReadOnly lock policy. " +
                            "ReadOnly does not support exclusive write access. Use 'inspect!()' instead.",
                            call.Location);
                    }

                    if (member.PropertyName == "inspect" && policy == "ReadOnly")
                    {
                        ReportError(
                            SemanticDiagnosticCode.ReadOnlyRejectsLocking,
                            $"Cannot use 'inspect!()' on '{lockPolicyTarget.Name}' — it uses ReadOnly lock policy. " +
                            "ReadOnly data does not need locking — use '.view()' instead.",
                            call.Location);
                    }
                }

                // #22: Reject migratable operations on collection being iterated
                if (member.Object is IdentifierExpression iterTarget
                    && _activeIterationSources.Contains(iterTarget.Name)
                    && method.ModificationCategory != ModificationCategory.Readonly)
                {
                    ReportError(
                        SemanticDiagnosticCode.MigratableDuringIteration,
                        $"Cannot call modifying method '{method.Name}' on '{iterTarget.Name}' while iterating over it. " +
                        "Collect changes and apply them after the loop.",
                        call.Location);
                }

                // #47: .hijack() on @initonly record warns — record is frozen after construction
                if (member.PropertyName == "hijack" && objectType is RecordTypeInfo)
                {
                    // Check if the variable holding the record is @initonly bound
                    if (member.Object is IdentifierExpression hijackTarget)
                    {
                        VariableInfo? targetVar = _registry.LookupVariable(name: hijackTarget.Name);
                        if (targetVar is { IsModifiable: false })
                        {
                            ReportWarning(
                                SemanticWarningCode.HijackOnInitOnly,
                                $"Calling '.hijack()' on @initonly-bound record '{hijackTarget.Name}'. " +
                                "The record is frozen after construction — hijacking has no practical effect.",
                                call.Location);
                        }
                    }
                }

                // #104/#23: Channel send() makes source variable a deadref
                if (member.PropertyName == "send" && member.Object is IdentifierExpression sendSource)
                {
                    string baseObjType = GetBaseTypeName(typeName: objectType.Name);
                    if (baseObjType == "Channel")
                    {
                        _deadrefVariables.Add(sendSource.Name);
                    }
                }

                // Validate exclusive token uniqueness (cannot pass same Hijacked/Seized twice)
                ValidateExclusiveTokenUniqueness(arguments: call.Arguments, location: call.Location);

                // Return type is Blank if not specified — substitute generic type parameters
                TypeSymbol? callReturnType = method.ReturnType;
                if (callReturnType != null && objectType is { IsGenericResolution: true, TypeArguments: not null })
                {
                    callReturnType = SubstituteTypeParameters(type: callReturnType, genericType: objectType);
                }
                if (callReturnType != null && method.OwnerType is GenericParameterTypeInfo genParamOwner)
                {
                    var substitutions = new Dictionary<string, TypeSymbol>
                    {
                        [genParamOwner.Name] = objectType
                    };
                    callReturnType = SubstituteWithMapping(type: callReturnType, substitutions: substitutions);
                }
                return callReturnType ?? _registry.LookupType("Blank") ?? ErrorTypeInfo.Instance;
            }
            else
            {
                // #78: Method-chain constructor — "42".S32!() → S32.$create!(from: "42")
                string propName = member.PropertyName;
                bool isFailable = propName.EndsWith(value: '!');
                string potentialTypeName = isFailable ? propName[..^1] : propName;

                TypeSymbol? targetType = LookupTypeWithImports(name: potentialTypeName);
                if (targetType != null)
                {
                    // Look up the creator on the target type
                    string creatorName = isFailable ? "$create!" : "$create";
                    RoutineInfo? creator = _registry.LookupRoutine(fullName: $"{potentialTypeName}.{creatorName}");

                    if (creator != null)
                    {
                        // Validate single non-me parameter
                        var nonMeParams = creator.Parameters
                            .Where(predicate: p => p.Name != "me")
                            .ToList();

                        if (nonMeParams.Count != 1)
                        {
                            ReportError(
                                SemanticDiagnosticCode.MethodChainMultiArg,
                                $"Method-chain constructor '{potentialTypeName}' requires exactly one non-'me' parameter, " +
                                $"but '$create' has {nonMeParams.Count}.",
                                call.Location);
                            return ErrorTypeInfo.Instance;
                        }

                        // Validate no extra args passed in the call
                        if (call.Arguments.Count > 0)
                        {
                            ReportError(
                                SemanticDiagnosticCode.MethodChainMultiArg,
                                $"Method-chain constructor '{potentialTypeName}' takes no additional arguments — " +
                                "the object itself is the argument.",
                                call.Location);
                            return ErrorTypeInfo.Instance;
                        }

                        // Type-check the object expression against the constructor parameter
                        if (!IsAssignableTo(source: objectType, target: nonMeParams[0].Type))
                        {
                            ReportError(
                                SemanticDiagnosticCode.ArgumentTypeMismatch,
                                $"Cannot convert '{objectType.Name}' to '{nonMeParams[0].Type.Name}' " +
                                $"for method-chain constructor '{potentialTypeName}'.",
                                call.Location);
                        }

                        if (creator.IsFailable && _currentRoutine != null)
                        {
                            _currentRoutine.HasFailableCalls = true;

                            if (!_currentRoutine.IsFailable && _currentRoutine.Name != "start")
                            {
                                ReportError(
                                    SemanticDiagnosticCode.UnhandledCrashableCall,
                                    $"Failable routine '{creator.Name}!' called without error handling. " +
                                    "Use 'when' to match the result, '??' to provide a default, or make the enclosing routine failable (!).",
                                    call.Location);
                            }
                        }

                        return targetType;
                    }
                }
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

    /// <summary>
    /// Infers dispatch strategy for a call site with protocol-constrained varargs.
    /// Returns null for non-varargs routines (always buildtime, no annotation needed).
    /// </summary>
    private DispatchStrategy? InferDispatchStrategy(RoutineInfo routine, CallExpression call)
    {
        if (!routine.IsVariadic)
            return null;

        // Find the varargs parameter
        ParameterInfo? varargsParam = routine.Parameters.FirstOrDefault(p => p.IsVariadicParam);
        if (varargsParam == null)
            return null;

        // Unwrap List[T] to get element type T
        TypeSymbol paramType = varargsParam.Type;
        if (paramType is not { IsGenericResolution: true, TypeArguments: [var elementType, ..] })
            return null;

        // Only protocol-constrained varargs need dispatch inference
        // Generic-constrained (GenericParameterTypeInfo) and concrete types are always buildtime
        if (elementType is not ProtocolTypeInfo)
            return DispatchStrategy.Buildtime;

        // Collect resolved types of all varargs arguments
        int varargsIndex = varargsParam.Index;
        var varargsArgTypes = new List<TypeSymbol>();
        for (int i = varargsIndex; i < call.Arguments.Count; i++)
        {
            TypeSymbol? argType = call.Arguments[i].ResolvedType;
            if (argType != null && argType is not ErrorTypeInfo)
                varargsArgTypes.Add(argType);
        }

        if (varargsArgTypes.Count == 0)
            return DispatchStrategy.Buildtime;

        // All same concrete type → buildtime; mixed → runtime
        TypeSymbol firstType = varargsArgTypes[0];
        bool allSame = varargsArgTypes.All(t => t.Name == firstType.Name);

        return allSame ? DispatchStrategy.Buildtime : DispatchStrategy.Runtime;
    }

    private TypeSymbol AnalyzeMemberExpression(MemberExpression member)
    {
        TypeSymbol objectType = AnalyzeExpression(expression: member.Object);

        // Look up the member variable/property on the type
        if (objectType is RecordTypeInfo record)
        {
            MemberVariableInfo? memberVariable = record.LookupMemberVariable(memberVariableName: member.PropertyName);
            if (memberVariable != null)
            {
                // Validate member variable access (read access)
                ValidateMemberVariableAccess(memberVariable: memberVariable, isWrite: false, accessLocation: member.Location);
                return memberVariable.Type;
            }

            // Wrapper type forwarding for record-based wrappers (Viewed[T], Hijacked[T], etc.)
            if (IsWrapperType(type: objectType))
            {
                MemberVariableInfo? innerMemberVariable = LookupMemberVariableOnWrapperInnerType(wrapperType: objectType, memberVariableName: member.PropertyName);
                if (innerMemberVariable != null)
                {
                    ValidateMemberVariableAccess(memberVariable: innerMemberVariable, isWrite: false, accessLocation: member.Location);
                    return innerMemberVariable.Type;
                }

                RoutineInfo? innerMethod = LookupMethodOnWrapperInnerType(wrapperType: objectType, methodName: member.PropertyName);
                if (innerMethod != null)
                {
                    ValidateReadOnlyWrapperMethodAccess(wrapperType: objectType, method: innerMethod, location: member.Location);
                    ValidateRoutineAccess(routine: innerMethod, accessLocation: member.Location);
                    return innerMethod.ReturnType ?? _registry.LookupType("Blank") ?? ErrorTypeInfo.Instance;
                }
            }
        }
        else if (objectType is EntityTypeInfo entity)
        {
            MemberVariableInfo? memberVariable = entity.LookupMemberVariable(memberVariableName: member.PropertyName);
            if (memberVariable != null)
            {
                // Validate member variable access (read access)
                ValidateMemberVariableAccess(memberVariable: memberVariable, isWrite: false, accessLocation: member.Location);
                return memberVariable.Type;
            }
        }
        // Wrapper type forwarding: Viewed<T>, Hijacked<T>, Shared<T>, etc.
        else if (IsWrapperType(type: objectType))
        {
            // Try to forward member variable access to the inner type
            MemberVariableInfo? innerMemberVariable = LookupMemberVariableOnWrapperInnerType(wrapperType: objectType, memberVariableName: member.PropertyName);
            if (innerMemberVariable != null)
            {
                // Validate member variable access on the inner type
                ValidateMemberVariableAccess(memberVariable: innerMemberVariable, isWrite: false, accessLocation: member.Location);
                return innerMemberVariable.Type;
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

        // Choice case member access: Color.RED → ChoiceTypeInfo
        if (objectType is ChoiceTypeInfo choice)
        {
            ChoiceCaseInfo? caseInfo = choice.Cases.FirstOrDefault(c => c.Name == member.PropertyName);
            if (caseInfo != null)
                return choice; // Color.RED has type Color

            // Fall through to method lookup — choice types can have methods
        }

        // Flags member access: Permissions.READ → FlagsTypeInfo
        if (objectType is FlagsTypeInfo flags)
        {
            FlagsMemberInfo? memberInfo = flags.Members.FirstOrDefault(m => m.Name == member.PropertyName);
            if (memberInfo != null)
                return flags; // Permissions.READ has type Permissions

            // Fall through to method lookup — flags types can have builder service methods
        }

        // Could be a method reference - use LookupMethod which handles generic resolutions
        // Strip '!' suffix from failable method calls (e.g., invalidate!() → invalidate)
        // The parser stores '!' in PropertyName, but routine declarations strip it (IsFailable = true)
        string lookupName = member.PropertyName.EndsWith('!') ? member.PropertyName[..^1] : member.PropertyName;
        RoutineInfo? method = _registry.LookupMethod(type: objectType, methodName: lookupName);
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

            // For methods on generic type parameters (e.g., routine T.view() → Viewed[T]),
            // substitute the generic parameter with the concrete receiver type
            if (returnType != null && method.OwnerType is GenericParameterTypeInfo genParamOwner)
            {
                var substitutions = new Dictionary<string, TypeSymbol>
                {
                    [genParamOwner.Name] = objectType
                };
                returnType = SubstituteWithMapping(type: returnType, substitutions: substitutions);
            }

            return returnType ?? _registry.LookupType("Blank") ?? ErrorTypeInfo.Instance;
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

        // Look for $getitem method — LookupMethod handles generic resolutions
        RoutineInfo? getItem = _registry.LookupMethod(type: objectType, methodName: "$getitem");
        if (getItem?.ReturnType != null)
        {
            TypeSymbol itemReturnType = getItem.ReturnType;
            if (objectType is { IsGenericResolution: true, TypeArguments: not null })
                itemReturnType = SubstituteTypeParameters(type: itemReturnType, genericType: objectType);
            return itemReturnType;
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

        // Look for $getslice method — LookupMethod handles generic resolutions
        RoutineInfo? getSlice = _registry.LookupMethod(type: objectType, methodName: "$getslice");
        if (getSlice?.ReturnType != null)
        {
            TypeSymbol sliceReturnType = getSlice.ReturnType;
            if (objectType is { IsGenericResolution: true, TypeArguments: not null })
                sliceReturnType = SubstituteTypeParameters(type: sliceReturnType, genericType: objectType);
            return sliceReturnType;
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
        // #145: Track nesting depth for deep conditional warning
        _conditionalNestingDepth++;
        if (_conditionalNestingDepth > 2)
        {
            ReportWarning(
                SemanticWarningCode.NestedConditionalExpression,
                "Deeply nested conditional expression. Consider using 'when' for readability.",
                cond.Location);
        }

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

        _conditionalNestingDepth--;

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
                foreach ((_, Expression value) in creator.MemberVariables)
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

            case DictEntryLiteralExpression dictEntry:
                CollectIdentifiersRecursive(expression: dictEntry.Key, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: dictEntry.Value, identifiers: identifiers);
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

        // #119: BackIndex (^n) cannot be used in Range expressions — only in subscript/slice context
        if (range.Start is BackIndexExpression)
        {
            ReportError(
                SemanticDiagnosticCode.BackIndexOutsideSubscript,
                "BackIndex (^n) cannot be used in Range expressions. Use it in subscript [^n] or slice [a to b] context instead.",
                range.Start.Location);
        }

        if (range.End is BackIndexExpression)
        {
            ReportError(
                SemanticDiagnosticCode.BackIndexOutsideSubscript,
                "BackIndex (^n) cannot be used in Range expressions. Use it in subscript [^n] or slice [a to b] context instead.",
                range.End.Location);
        }

        // Range types must be compatible
        if (!IsNumericType(type: startType) || !IsNumericType(type: endType))
        {
            ReportError(SemanticDiagnosticCode.RangeBoundsNotNumeric, "Range bounds must be numeric types.", range.Location);
        }

        // Return resolved Range[T] type with concrete element type
        TypeInfo? rangeGenericDef = _registry.LookupType(name: "Range");
        if (rangeGenericDef != null && startType is not ErrorTypeInfo)
        {
            return _registry.GetOrCreateResolution(rangeGenericDef, new List<TypeInfo> { startType });
        }
        return rangeGenericDef ?? ErrorTypeInfo.Instance;
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

        // Validate member variable initializers
        ValidateCreatorMemberVariables(type: type, memberVariables: creator.MemberVariables, location: creator.Location);

        return type;
    }

    /// <summary>
    /// Validates creator member variable initializers:
    /// - Each provided member variable exists on the type
    /// - Value types are assignable to member variable types
    /// - No duplicate member variable assignments
    /// - All required member variables are provided
    /// </summary>
    private void ValidateCreatorMemberVariables(
        TypeSymbol type,
        List<(string Name, Expression Value)> memberVariables,
        SourceLocation location)
    {
        // Get the type's member variables
        IReadOnlyList<MemberVariableInfo>? typeMemberVariables = type switch
        {
            RecordTypeInfo record => record.MemberVariables,
            EntityTypeInfo entity => entity.MemberVariables,
            _ => null
        };

        if (typeMemberVariables == null)
        {
            if (memberVariables.Count > 0)
            {
                ReportError(
                    SemanticDiagnosticCode.TypeNotMemberVariableInitializable,
                    $"Type '{type.Name}' does not support member variable initialization.",
                    location);
            }
            return;
        }

        // Build a lookup for expected member variables
        var memberVariableLookup = new Dictionary<string, MemberVariableInfo>();
        foreach (MemberVariableInfo memberVariable in typeMemberVariables)
        {
            memberVariableLookup[memberVariable.Name] = memberVariable;
        }

        // Track which member variables have been provided (to detect duplicates and missing member variables)
        var providedMemberVariables = new HashSet<string>();

        // Validate each provided member variable
        foreach ((string memberVariableName, Expression value) in memberVariables)
        {
            // Check for duplicates
            if (!providedMemberVariables.Add(memberVariableName))
            {
                ReportError(
                    SemanticDiagnosticCode.DuplicateMemberVariableInitializer,
                    $"Duplicate member variable initializer for '{memberVariableName}'.",
                    value.Location);
                continue;
            }

            // Check if member variable exists
            if (!memberVariableLookup.TryGetValue(memberVariableName, out MemberVariableInfo? expectedMemberVariable))
            {
                ReportError(
                    SemanticDiagnosticCode.MemberVariableNotFound,
                    $"Type '{type.Name}' does not have a member variable named '{memberVariableName}'.",
                    value.Location);
                AnalyzeExpression(expression: value); // Still analyze the value
                continue;
            }

            // Analyze value with expected type for contextual inference
            TypeSymbol memberVariableType = expectedMemberVariable.Type;

            // For generic resolutions, substitute type parameters in member variable type
            if (type is { IsGenericResolution: true, TypeArguments: not null })
            {
                memberVariableType = SubstituteTypeParameters(type: memberVariableType, genericType: type);
            }

            TypeSymbol valueType = AnalyzeExpression(expression: value, expectedType: memberVariableType);

            // Check type compatibility
            if (!IsAssignableTo(source: valueType, target: memberVariableType))
            {
                ReportError(
                    SemanticDiagnosticCode.MemberVariableTypeMismatch,
                    $"Cannot assign '{valueType.Name}' to member variable '{memberVariableName}' of type '{memberVariableType.Name}'.",
                    value.Location);
            }
        }

        // Check for missing required member variables (member variables without default values)
        foreach (MemberVariableInfo memberVariable in typeMemberVariables)
        {
            if (!providedMemberVariables.Contains(memberVariable.Name) && !memberVariable.HasDefaultValue)
            {
                ReportError(
                    SemanticDiagnosticCode.MissingRequiredMemberVariable,
                    $"Missing required member variable '{memberVariable.Name}' in creator for '{type.Name}'.",
                    location);
            }
        }
    }

    private TypeSymbol AnalyzeListLiteralExpression(ListLiteralExpression list, TypeSymbol? expectedType = null)
    {
        // Extract expected element type from List[X] expected type
        TypeSymbol? expectedElementType = null;
        if (expectedType is { IsGenericResolution: true, TypeArguments.Count: 1 } &&
            expectedType.Name.StartsWith("List["))
        {
            expectedElementType = expectedType.TypeArguments![0];
        }

        TypeSymbol? elementType = null;

        if (list.ElementType != null)
        {
            elementType = ResolveType(typeExpr: list.ElementType);
        }
        else if (list.Elements.Count > 0)
        {
            // Infer from first element, propagating expected element type
            elementType = AnalyzeExpression(expression: list.Elements[0], expectedType: expectedElementType);

            // Validate all elements have compatible types
            // Use inferred element type as context for subsequent elements (e.g., [] in [[1,2], []])
            TypeSymbol elemExpected = expectedElementType ?? elementType;
            for (int i = 1; i < list.Elements.Count; i++)
            {
                TypeSymbol elemType = AnalyzeExpression(expression: list.Elements[i], expectedType: elemExpected);
                if (!IsAssignableTo(source: elemType, target: elementType))
                {
                    ReportError(SemanticDiagnosticCode.ListElementTypeMismatch, $"List element type mismatch: expected '{elementType.Name}', got '{elemType.Name}'.", list.Elements[i].Location);
                }
            }
        }
        else if (expectedElementType != null)
        {
            // Empty list with expected type from context — use it
            elementType = expectedElementType;
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

    private TypeSymbol AnalyzeSetLiteralExpression(SetLiteralExpression set, TypeSymbol? expectedType = null)
    {
        // Extract expected element type from Set[X] expected type
        TypeSymbol? expectedElementType = null;
        if (expectedType is { IsGenericResolution: true, TypeArguments.Count: 1 } &&
            expectedType.Name.StartsWith("Set["))
        {
            expectedElementType = expectedType.TypeArguments![0];
        }

        TypeSymbol? elementType = null;

        if (set.ElementType != null)
        {
            elementType = ResolveType(typeExpr: set.ElementType);
        }
        else if (set.Elements.Count > 0)
        {
            elementType = AnalyzeExpression(expression: set.Elements[0], expectedType: expectedElementType);
        }
        else if (expectedElementType != null)
        {
            // Empty set with expected type from context — use it
            elementType = expectedElementType;
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

    private TypeSymbol AnalyzeDictLiteralExpression(DictLiteralExpression dict, TypeSymbol? expectedType = null)
    {
        // Extract expected key/value types from Dict[K, V] expected type
        TypeSymbol? expectedKeyType = null;
        TypeSymbol? expectedValueType = null;
        if (expectedType is { IsGenericResolution: true, TypeArguments.Count: 2 } &&
            expectedType.Name.StartsWith("Dict["))
        {
            expectedKeyType = expectedType.TypeArguments![0];
            expectedValueType = expectedType.TypeArguments![1];
        }

        TypeSymbol? keyType = null;
        TypeSymbol? valueType = null;

        if (dict is { KeyType: not null, ValueType: not null })
        {
            keyType = ResolveType(typeExpr: dict.KeyType);
            valueType = ResolveType(typeExpr: dict.ValueType);
        }
        else if (dict.Pairs.Count > 0)
        {
            keyType = AnalyzeExpression(expression: dict.Pairs[0].Key, expectedType: expectedKeyType);
            valueType = AnalyzeExpression(expression: dict.Pairs[0].Value, expectedType: expectedValueType);
        }
        else if (expectedKeyType != null && expectedValueType != null)
        {
            // Empty dict with expected types from context — use them
            keyType = expectedKeyType;
            valueType = expectedValueType;
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

    private TypeSymbol AnalyzeDictEntryLiteralExpression(DictEntryLiteralExpression dictEntry)
    {
        TypeSymbol keyType = AnalyzeExpression(expression: dictEntry.Key);
        TypeSymbol valueType = AnalyzeExpression(expression: dictEntry.Value);

        // Resolve to DictEntry[K, V]
        TypeSymbol? dictEntryDef = _registry.LookupType(name: "DictEntry");
        if (dictEntryDef != null)
        {
            return _registry.GetOrCreateResolution(genericDef: dictEntryDef, typeArguments: [keyType, valueType]);
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

        return _registry.GetOrCreateTupleType(elementTypes: elementTypes);
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

            // #45: Cannot modify secret member variables in 'with' expression
            if (fieldPath is { Count: > 0 } && baseType is RecordTypeInfo recordType)
            {
                MemberVariableInfo? memberInfo = recordType.LookupMemberVariable(memberVariableName: fieldPath[0]);
                if (memberInfo is { Visibility: VisibilityModifier.Secret })
                {
                    ReportError(
                        SemanticDiagnosticCode.WithSecretMemberProhibited,
                        $"Cannot modify secret member variable '{fieldPath[0]}' in 'with' expression.",
                        with.Location);
                }
            }
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
        // Analyze the matched expression (Bool for subject-less when — arms are conditions)
        TypeSymbol matchedType = when.Expression != null
            ? AnalyzeExpression(expression: when.Expression)
            : _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;

        // #88: Pattern order enforcement — else/wildcard must be last
        {
            bool seenElse = false;
            foreach (WhenClause clause in when.Clauses)
            {
                if (seenElse)
                {
                    ReportError(
                        SemanticDiagnosticCode.PatternOrderViolation,
                        "Unreachable pattern after 'else' or wildcard.",
                        clause.Pattern.Location);
                }

                if (clause.Pattern is ElsePattern or WildcardPattern)
                {
                    seenElse = true;
                }
            }
        }

        // #130/#148: Duplicate pattern detection
        {
            var seenPatterns = new HashSet<string>();
            foreach (WhenClause clause in when.Clauses)
            {
                string? patternKey = GetPatternKey(pattern: clause.Pattern);
                if (patternKey != null && !seenPatterns.Add(item: patternKey))
                {
                    ReportError(
                        SemanticDiagnosticCode.DuplicatePattern,
                        $"Duplicate pattern: {patternKey}.",
                        clause.Pattern.Location);
                }
            }
        }

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

    /// <summary>
    /// Infers type arguments for a generic routine from call arguments.
    /// Returns the inferred type arguments, or null if inference fails.
    /// </summary>
    private IReadOnlyList<TypeSymbol>? InferGenericTypeArguments(
        RoutineInfo genericRoutine, IReadOnlyList<Expression> arguments)
    {
        if (genericRoutine.GenericParameters == null || genericRoutine.GenericParameters.Count == 0)
            return null;

        var typeArgs = new TypeSymbol?[genericRoutine.GenericParameters.Count];

        int argCount = Math.Min(genericRoutine.Parameters.Count, arguments.Count);
        for (int i = 0; i < argCount; i++)
        {
            TypeSymbol paramType = genericRoutine.Parameters[i].Type;
            if (paramType is GenericParameterTypeInfo)
            {
                int idx = genericRoutine.GenericParameters.ToList().IndexOf(paramType.Name);
                if (idx >= 0 && typeArgs[idx] == null)
                {
                    Expression arg = arguments[i] is NamedArgumentExpression na ? na.Value : arguments[i];
                    TypeSymbol argType = AnalyzeExpression(expression: arg);
                    if (argType != ErrorTypeInfo.Instance)
                        typeArgs[idx] = argType;
                }
            }
        }

        // All type args must be inferred
        for (int i = 0; i < typeArgs.Length; i++)
        {
            if (typeArgs[i] == null)
                return null;
        }

        return typeArgs!;
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

        // #19: Track lock policy from lock![Policy]() on entities
        if (generic.MethodName == "lock" && generic.IsMemoryOperation
            && typeArgs.Count > 0 && generic.Object is IdentifierExpression lockTarget)
        {
            _variableLockPolicies[lockTarget.Name] = typeArgs[0].Name;
        }

        // #19: Track lock policy from share[Policy]() on entities — stored temporarily
        // on the source variable; propagated to the declared variable in AnalyzeVariableDeclaration
        if (generic.MethodName == "share" && typeArgs.Count > 0
            && generic.Object is IdentifierExpression shareTarget)
        {
            _lastSharePolicy = (shareTarget.Name, typeArgs[0].Name);
        }

        // Check if this is a generic type constructor call (e.g., Snatched[U8](addr))
        // The parser creates GenericMethodCallExpression for both Type[Args](args) and obj.method[Args](args)
        if (generic.Object is IdentifierExpression typeId
            && objectType is TypeInfo typeInfo
            && typeInfo.IsGenericDefinition
            && typeId.Name == generic.MethodName)
        {
            // Resolve the generic type with the provided type arguments
            TypeInfo resolvedType = _registry.GetOrCreateResolution(
                genericDef: typeInfo, typeArguments: typeArgs.Cast<TypeInfo>().ToList());

            // Analyze constructor arguments
            foreach (Expression arg in generic.Arguments)
            {
                AnalyzeExpression(expression: arg);
            }

            // Collection literal constructor with explicit type args: List[S64](1, 2, 3)
            if (CollectionLiteralTypes.Contains(typeId.Name)
                && generic.Arguments.Count > 0
                && generic.Arguments.All(a => a is not NamedArgumentExpression))
            {
                generic.IsCollectionLiteral = true;

                // ValueList[T, N] / ValueBitList[N] — argument count must match N
                if (typeId.Name == "ValueList" && typeArgs.Count >= 2)
                {
                    // N is the second type arg (a numeric constant type)
                    string nStr = typeArgs[1].Name;
                    if (ulong.TryParse(nStr, out ulong n) && (ulong)generic.Arguments.Count != n)
                    {
                        ReportError(SemanticDiagnosticCode.ArgumentCountMismatch,
                            $"ValueList[{typeArgs[0].Name}, {n}] constructor requires exactly {n} arguments, got {generic.Arguments.Count}.",
                            generic.Location);
                    }
                }
                else if (typeId.Name == "ValueBitList" && typeArgs.Count >= 1)
                {
                    string nStr = typeArgs[0].Name;
                    if (ulong.TryParse(nStr, out ulong n) && (ulong)generic.Arguments.Count != n)
                    {
                        ReportError(SemanticDiagnosticCode.ArgumentCountMismatch,
                            $"ValueBitList[{n}] constructor requires exactly {n} arguments, got {generic.Arguments.Count}.",
                            generic.Location);
                    }
                }
            }

            ValidateExclusiveTokenUniqueness(arguments: generic.Arguments, location: generic.Location);
            return resolvedType;
        }

        // Look up the method on receiver type — LookupMethod handles generic resolutions
        RoutineInfo? method = _registry.LookupMethod(type: objectType, methodName: generic.MethodName);
        if (method != null)
        {
            foreach (Expression arg in generic.Arguments)
                AnalyzeExpression(expression: arg);
            ValidateExclusiveTokenUniqueness(arguments: generic.Arguments, location: generic.Location);

            if (method.ReturnType == null)
                return _registry.LookupType("Blank") ?? ErrorTypeInfo.Instance;

            TypeSymbol returnType = method.ReturnType;

            // Step 1: Substitute owner type's generic params (T from Snatched[T] → Snatched[Point])
            if (objectType is { IsGenericResolution: true, TypeArguments: not null })
                returnType = SubstituteTypeParameters(type: returnType, genericType: objectType);

            // Step 2: Substitute method's own generic params (U from obtain_as[U])
            // GenericParameters includes both owner-level (T) and method-level (U) params,
            // but typeArgs only contains method-level args from the call site.
            // Compute the offset to skip owner-level params when indexing into typeArgs.
            if (method.GenericParameters != null)
            {
                int ownerParamCount = objectType is { IsGenericResolution: true, TypeArguments: not null }
                    ? objectType.TypeArguments.Count : 0;

                // Direct param (return type is just U)
                if (returnType is GenericParameterTypeInfo)
                {
                    int paramIndex = method.GenericParameters.ToList().IndexOf(returnType.Name);
                    int adjustedIndex = paramIndex - ownerParamCount;
                    if (adjustedIndex >= 0 && adjustedIndex < typeArgs.Count && typeArgs[adjustedIndex] is TypeInfo resolved)
                        return resolved;
                }
                // Resolution containing method's params (e.g., Snatched[U])
                if (returnType is { IsGenericResolution: true, TypeArguments: not null })
                {
                    var substitutedArgs = new List<TypeInfo>();
                    bool anySubstituted = false;
                    foreach (var typeArg in returnType.TypeArguments)
                    {
                        int idx = method.GenericParameters.ToList().IndexOf(typeArg.Name);
                        int adjustedIdx = idx - ownerParamCount;
                        if (adjustedIdx >= 0 && adjustedIdx < typeArgs.Count && typeArgs[adjustedIdx] is TypeInfo sub)
                        {
                            substitutedArgs.Add(sub);
                            anySubstituted = true;
                        }
                        else
                        {
                            substitutedArgs.Add(typeArg);
                        }
                    }
                    if (anySubstituted)
                    {
                        string baseName = returnType.Name;
                        int bracketIdx = baseName.IndexOf('[');
                        if (bracketIdx > 0) baseName = baseName[..bracketIdx];
                        var genericDef = _registry.LookupType(baseName);
                        if (genericDef != null)
                            return _registry.GetOrCreateResolution(genericDef, substitutedArgs);
                    }
                }
            }

            return returnType;
        }

        // Standalone generic function call (e.g., ptrtoint[Point, Address](p), snatched_none[T]())
        // The object is an identifier that resolves to a routine, not a type or variable
        if (generic.Object is IdentifierExpression funcId)
        {
            RoutineInfo? routine = _registry.LookupRoutine(fullName: funcId.Name)
                                   ?? _registry.LookupRoutineByName(funcId.Name);
            if (routine != null)
            {
                foreach (Expression arg in generic.Arguments)
                {
                    AnalyzeExpression(expression: arg);
                }

                if (routine.ReturnType == null)
                    return _registry.LookupType("Blank") ?? ErrorTypeInfo.Instance;

                // Substitute generic type parameters in return type
                TypeInfo returnType = routine.ReturnType;
                if (returnType is GenericParameterTypeInfo && routine.GenericParameters != null)
                {
                    int paramIndex = routine.GenericParameters.ToList().IndexOf(returnType.Name);
                    if (paramIndex >= 0 && paramIndex < typeArgs.Count && typeArgs[paramIndex] is TypeInfo resolved)
                        return resolved;
                }
                // Return type is a generic resolution (e.g., Snatched[T] → Snatched[U8])
                if (returnType.IsGenericResolution && returnType.TypeArguments != null && routine.GenericParameters != null)
                {
                    var substitutedArgs = new List<TypeInfo>();
                    bool anySubstituted = false;
                    foreach (var typeArg in returnType.TypeArguments)
                    {
                        int idx = routine.GenericParameters.ToList().IndexOf(typeArg.Name);
                        if (idx >= 0 && idx < typeArgs.Count && typeArgs[idx] is TypeInfo sub)
                        {
                            substitutedArgs.Add(sub);
                            anySubstituted = true;
                        }
                        else
                        {
                            substitutedArgs.Add(typeArg);
                        }
                    }
                    if (anySubstituted)
                    {
                        string baseName = returnType.Name;
                        int bracketIdx = baseName.IndexOf('[');
                        if (bracketIdx > 0) baseName = baseName[..bracketIdx];
                        var genericDef = _registry.LookupType(baseName);
                        if (genericDef != null)
                            return _registry.GetOrCreateResolution(genericDef, substitutedArgs);
                    }
                }
                return returnType;
            }
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
        TypeSymbol objectType = AnalyzeExpression(expression: genericMember.Object);

        // Resolve type arguments
        foreach (TypeExpression typeArg in genericMember.TypeArguments)
        {
            ResolveType(typeExpr: typeArg);
        }

        // Look up the member on the object type
        if (objectType is TypeInfo objTypeInfo)
        {
            IReadOnlyList<MemberVariableInfo>? memberVars = objTypeInfo switch
            {
                EntityTypeInfo e => e.MemberVariables,
                RecordTypeInfo r => r.MemberVariables,
                _ => null
            };
            var memberVar = memberVars?.FirstOrDefault(mv => mv.Name == genericMember.MemberName);
            if (memberVar != null)
            {
                // Member found — the [args] are indexing into the member's value.
                // Analyze the "type arguments" as expressions (they're actually index values).
                foreach (TypeExpression typeArg in genericMember.TypeArguments)
                {
                    // The type arg's Name is actually a variable name — analyze it as identifier
                    if (typeArg.Name != null)
                    {
                        AnalyzeExpression(expression: new IdentifierExpression(Name: typeArg.Name, Location: typeArg.Location));
                    }
                }

                // Determine the element type of the member's collection type
                TypeInfo? memberType = memberVar.Type;
                if (memberType is { TypeArguments: { Count: > 0 } })
                {
                    // e.g., List[SortedDict[K,V]] ��� element is SortedDict[K,V]
                    return memberType.TypeArguments[0];
                }

                // If the member type has a $getitem method, use its return type
                RoutineInfo? getItem = _registry.LookupMethod(memberType, "$getitem")
                    ?? _registry.LookupMethod(memberType, "$getitem!");
                if (getItem?.ReturnType != null)
                {
                    return getItem.ReturnType;
                }

                return memberType ?? ErrorTypeInfo.Instance;
            }
        }

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
                // #16: F-text expression level restriction — only Level 3 expressions
                // (identifiers, literals, member access, calls) are allowed
                ValidateFTextExpression(expression: exprPart.Expression, location: exprPart.Location);
                AnalyzeExpression(expression: exprPart.Expression);
                ValidateFTextFormatSpec(formatSpec: exprPart.FormatSpec, location: exprPart.Location);
            }
        }

        return _registry.LookupType(name: "Text") ?? ErrorTypeInfo.Instance;
    }

    /// <summary>
    /// Validates that an f-text format specifier is one of the allowed values.
    /// Valid: null (none), "=", "?", "=?". Invalid: "?=" (wrong order), anything else.
    /// </summary>
    private void ValidateFTextFormatSpec(string? formatSpec, SourceLocation location)
    {
        if (formatSpec is null or "=" or "?" or "=?")
            return;

        if (formatSpec == "?=")
        {
            ReportError(
                SemanticDiagnosticCode.InvalidFTextFormatSpec,
                "Invalid f-text format specifier '?='. The correct order is '=?' (name display first, then diagnose).",
                location);
            return;
        }

        ReportError(
            SemanticDiagnosticCode.InvalidFTextFormatSpec,
            $"Invalid f-text format specifier '{formatSpec}'. F-text only supports '=' (name display), '?' (diagnose), and '=?' (combined).",
            location);
    }

    /// <summary>
    /// Validates that an f-text embedded expression is a Level 3 expression.
    /// Level 3: identifiers, literals, member access, method calls, indexing.
    /// Disallowed: assignments, control flow, binary operators (except chained member access).
    /// </summary>
    private void ValidateFTextExpression(Expression expression, SourceLocation location)
    {
        switch (expression)
        {
            case IdentifierExpression:
            case LiteralExpression:
            case MemberExpression:
            case CallExpression:
            case IndexExpression:
            case OptionalMemberExpression:
                // Level 3 — allowed
                break;
            default:
                ReportError(
                    SemanticDiagnosticCode.FTextExpressionLevelRestriction,
                    "Only simple expressions (identifiers, literals, member access, calls) are allowed in f-text interpolation. " +
                    "Assign complex expressions to a variable first.",
                    location);
                break;
        }
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
        TypeSymbol? def = _registry.LookupType(name: baseName);
        // Try module-qualified name for non-Core types (e.g., "Collections.Deque")
        if (def == null && !string.IsNullOrEmpty(resolution.Module))
            def = _registry.LookupType(name: $"{resolution.Module}.{baseName}");
        return def;
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

        // #11: Deadref tracking — mark the stolen variable as invalidated
        if (steal.Operand is IdentifierExpression stolenId)
        {
            _deadrefVariables.Add(stolenId.Name);
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

        // Return a BackIndex type (or Address as the underlying representation)
        // BackIndex is conceptually a wrapper around an offset from the end
        TypeSymbol? backIndexType = _registry.LookupType(name: "BackIndex");
        if (backIndexType != null)
        {
            return backIndexType;
        }

        // Fallback: return Address as the index representation
        return _registry.LookupType(name: "Address") ?? operandType;
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

        // #14/#162: Validate that we're inside a suspended/threaded routine
        if (_currentRoutine != null && !_currentRoutine.IsAsync)
        {
            ReportError(
                SemanticDiagnosticCode.WaitforOutsideSuspendedRoutine,
                $"'waitfor' can only be used inside a 'suspended' or 'threaded' routine. " +
                $"Routine '{_currentRoutine.Name}' is not async.",
                waitfor.Location);
        }

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

        // #15: Non-leaf waitfor (with dependencies) requires 'within' timeout clause
        if (depWaitfor.Timeout == null)
        {
            ReportError(
                SemanticDiagnosticCode.WaitforRequiresTimeout,
                "Dependent 'waitfor' (with 'after' clause) requires a 'within' timeout. " +
                "Add 'within <duration>' to prevent unbounded blocking on dependency chains.",
                depWaitfor.Location);
        }

        // Exit the dependency scope
        _registry.ExitScope();

        // #14/#162: Validate that we're inside a suspended/threaded routine
        if (_currentRoutine != null && !_currentRoutine.IsAsync)
        {
            ReportError(
                SemanticDiagnosticCode.WaitforOutsideSuspendedRoutine,
                $"'waitfor' can only be used inside a 'suspended' or 'threaded' routine. " +
                $"Routine '{_currentRoutine.Name}' is not async.",
                depWaitfor.Location);
        }

        // Result type is Lookup<R> where R is the operand type
        return ErrorHandlingTypeInfo.WellKnown.LookupDefinition.CreateInstance(typeArguments: [operandType]);
    }

    #endregion
}
