using System.Globalization;
using System.Text;
using Builder.Tokenizer;
using Builder.Verification;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.LlvmEmit;

// D2 (DEFERRED): Text/Bytes literals still emit their backing arrays + carrier struct as constant
// globals here rather than lowering to a `CreatorExpression` against the real stdlib Text/Bytes
// `create`. Doing that fully requires D1's memberwise-create synthesis plus a compile-time
// constant-aggregate argument path (the current stdlib `create` takes runtime args). As a partial
// step, the carrier struct LAYOUT is now derived from the registered TypeSymbol (BuildLiteralCarrierLayout)
// instead of a hardcoded `{ ptr, i64, ptr }`.
/// <summary>
/// Expression code generation for literals and scalar literal helpers.
/// </summary>
public partial class LlvmEmitter
{
    /// <summary>Module-level constant globals emitted for aggregate (Array[T,N]) presets,
    /// keyed by the preset's qualified name so the table is emitted once and shared.</summary>
    private readonly Dictionary<string, string> _presetGlobals =
        new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// Looks up <paramref name="name"/> as an aggregate (Array[T,N]) preset, trying the bare name
    /// and the current routine's module prefix. Returns null for non-presets and scalar presets
    /// (which are inlined before codegen). See <see cref="VariableInfo.IsPresettableAggregate"/>.
    /// </summary>
    private VariableInfo? ResolveAggregatePreset(string name)
    {
        if (_registry.LookupVariable(name: name) is { IsPresettableAggregate: true } direct)
        {
            return direct;
        }

        string? module = _currentEmittingRoutine?.OwnerType?.Module ??
                         _currentEmittingRoutine?.Module;
        if (module != null && !name.Contains(value: '.') &&
            _registry.LookupVariable(name: $"{module}.{name}") is
                { IsPresettableAggregate: true } qualified)
        {
            return qualified;
        }

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
        {
            return existing;
        }

        var list = (ListLiteralExpression)preset.PresetValue!;
        string arrLlvm = GetLlvmType(type: preset.Type); // "[1000 x i16]" / "[8 x i8]"
        string symbol = $"@\"preset.{key}\"";

        // BitArray[N] packs its `N` bool elements into `[(N+7)/8 x i8]`; Array[T,N] stores one
        // element per slot. Both reduce to a constant `[M x T]` initializer.
        string initializer = GetGenericBaseName(type: preset.Type) == "BitArray"
            ? BuildBitArrayPresetInitializer(key: key, list: list)
            : BuildArrayPresetInitializer(key: key, list: list, arrLlvm: arrLlvm);

        EmitLine(sb: _globalDeclarations,
            line: $"{symbol} = private unnamed_addr constant {arrLlvm} {initializer}");
        _presetGlobals[key: key] = symbol;
        return symbol;
    }

    /// <summary>Builds the <c>[N x T] [...]</c> constant initializer for an <c>Array[T,N]</c> preset.</summary>
    private string BuildArrayPresetInitializer(string key, ListLiteralExpression list,
        string arrLlvm)
    {
        if (list.Elements.Count == 0)
        {
            return "zeroinitializer";
        }

        string elemLlvm = ArrayElementLlvmType(arrLlvm: arrLlvm); // e.g. "i16"
        var scratch = new StringBuilder();
        var parts = new List<string>(capacity: list.Elements.Count);
        foreach (Expression element in list.Elements)
        {
            if (element is not LiteralExpression lit)
            {
                throw new NotImplementedException(
                    message:
                    $"Aggregate preset '{key}' element must be a scalar literal; got {element.GetType().Name}.");
            }

            // Numeric/bool/char literals render to a pure constant with no IR side effects.
            parts.Add(item: $"{elemLlvm} {EmitLiteral(sb: scratch, literal: lit)}");
        }

        return $"[{string.Join(separator: ", ", values: parts)}]";
    }

    /// <summary>
    /// Packs a flat list of bool-literal elements into <c>(N+7)/8</c> bytes, LSB-first (bit 0 = element
    /// 0 of each group of 8). Guarded by both BitArray[N] emission sites — the compile-time preset
    /// initializer (<see cref="BuildBitArrayPresetInitializer"/>) and the inline literal fast path in
    /// <c>EmitCollectionLiteralConstructor</c>.
    /// <para><paramref name="allLiteral"/> is set false as soon as a non-<c>true</c>/<c>false</c>-literal
    /// element is seen; the inline site uses that to fall back to a runtime bit-pack, while the preset
    /// site (which requires constant elements) treats it via <paramref name="onNonLiteral"/>.</para>
    /// </summary>
    private static int[] PackBitArrayLiteralBytes(List<Expression> elements, out bool allLiteral,
        Action<Expression>? onNonLiteral = null)
    {
        allLiteral = true;
        int bitCount = elements.Count;
        int byteCount = (bitCount + 7) / 8;
        int[] bytes = new int[byteCount];
        for (int byteIdx = 0; byteIdx < byteCount; byteIdx++)
        {
            int byteVal = 0;
            for (int bitIdx = 0; bitIdx < 8 && byteIdx * 8 + bitIdx < bitCount; bitIdx++)
            {
                Expression bit = elements[index: byteIdx * 8 + bitIdx];
                if (bit is LiteralExpression { Value: bool b })
                {
                    if (b)
                    {
                        byteVal |= 1 << bitIdx;
                    }
                }
                else
                {
                    allLiteral = false;
                    onNonLiteral?.Invoke(obj: bit);
                }
            }

            bytes[byteIdx] = byteVal;
        }

        return bytes;
    }

    /// <summary>
    /// Builds the <c>[(N+7)/8 x i8] [...]</c> constant initializer for a <c>BitArray[N]</c> preset by
    /// packing 8 bool literals per byte (bit 0 = LSB) via the shared <see cref="PackBitArrayLiteralBytes"/>.
    /// </summary>
    private static string BuildBitArrayPresetInitializer(string key, ListLiteralExpression list)
    {
        if (list.Elements.Count == 0)
        {
            return "zeroinitializer";
        }

        int[] bytes = PackBitArrayLiteralBytes(elements: list.Elements,
            allLiteral: out _,
            onNonLiteral: bit => throw new NotImplementedException(
                message:
                $"BitArray preset '{key}' element must be a bool literal; got {bit.GetType().Name}."));

        IEnumerable<string> parts = bytes.Select(selector: b => $"i8 {b}");
        return $"[{string.Join(separator: ", ", values: parts)}]";
    }

    /// <summary>Extracts the element type from an array LLVM type (<c>"[N x ELEM]"</c> -&gt; <c>"ELEM"</c>).</summary>
    private static string ArrayElementLlvmType(string arrLlvm)
    {
        int x = arrLlvm.IndexOf(value: " x ", comparisonType: StringComparison.Ordinal);
        if (arrLlvm.StartsWith(value: '[') && x > 0 && arrLlvm.EndsWith(value: ']'))
        {
            return arrLlvm[(x + 3)..^1];
        }

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
            // Numeric literals are stored as strings by the parser (e.g., "1_s32", "3.14_b32").
            // Check LiteralType first to handle them as numbers, not string constants.
            // The arbitrary-precision `n` suffix is not in the fixed-width suffix table (a plain `n`
            // would also match the end of `nan`), so an Integer literal drops it here.
            case string s when literal.LiteralType == TokenType.IntegerLiteral:
                return StripNumericSuffix(text: s.TrimEnd('n').TrimEnd('_'));
            case string s when IsIntegerLiteralType(type: literal.LiteralType):
                return StripNumericSuffix(text: s);
            case string s when IsFloatLiteralType(type: literal.LiteralType) &&
                               NumericLiteralParser.IsHexFloatText(text: s):
                return EmitHexFloatLiteral(
                    cleaned: SemanticVerifier.StripHexFloatSuffix(rawValue: s)
                                            .Replace(oldValue: "_", newValue: ""),
                    literalType: literal.LiteralType);
            case string s when IsFloatLiteralType(type: literal.LiteralType):
                return EmitFloatLiteral(numericValue: StripNumericSuffix(text: s),
                    literalType: literal.LiteralType);
            case string s when IsDecimalFloatLiteralType(type: literal.LiteralType):
                return EmitDecimalFloatLiteral(numericValue: StripNumericSuffix(text: s),
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
    private static bool IsIntegerLiteralType(TokenType type)
    {
        return type is TokenType.IntegerLiteral or TokenType.S8Literal or TokenType.S16Literal
            or TokenType.S32Literal or TokenType.S64Literal or TokenType.S128Literal
            or TokenType.S256Literal or TokenType.U8Literal or TokenType.U16Literal
            or TokenType.U32Literal or TokenType.U64Literal or TokenType.U128Literal
            or TokenType.U256Literal or TokenType.AddressLiteral;
    }

    /// <summary>
    /// Checks if a token type represents a floating-point literal.
    /// </summary>
    private static bool IsFloatLiteralType(TokenType type)
    {
        return type is TokenType.B16Literal or TokenType.B32Literal or TokenType.B64Literal
            or TokenType.B128Literal;
    }

    /// <summary>
    /// Returns whether is decimal float literal type applies in the current compiler context.
    /// </summary>
    private static bool IsDecimalFloatLiteralType(TokenType type)
    {
        return type is TokenType.D32Literal or TokenType.D64Literal or TokenType.D128Literal
            or TokenType.DecimalLiteral;
    }

    /// <summary>
    /// D2 (partial): derives the LLVM field-type layout of a literal-backed carrier record
    /// (<c>Text</c> / <c>Bytes</c>) from its registered <see cref="RecordTypeSymbol"/> rather than
    /// hardcoding <c>{ ptr, i64, ptr }</c>. Returns the joined field-type string (e.g.
    /// <c>"ptr, i64, ptr"</c>) and the named struct type via <paramref name="structTypeName"/>.
    /// <para>DEFERRED: the values themselves (data ptr / count / null ctrl) are still positionally
    /// hand-built here and the backing arrays are emitted as constant globals — fully routing string/
    /// bytes literals through the real stdlib <c>Text.create</c>/<c>Bytes.create</c> needs D1's
    /// memberwise-create synthesis plus a compile-time constant-aggregate argument path, which the
    /// current stdlib <c>create</c> (runtime-arg) does not accept. See the task report.</para>
    /// </summary>
    private string BuildLiteralCarrierLayout(string carrierName, int expectedMemberVariables,
        out string structTypeName)
    {
        TypeSymbol? carrier = _registry.LookupType(name: carrierName) ??
                            _registry.LookupType(name: $"Core.{carrierName}");
        if (carrier is RecordTypeSymbol record &&
            record.MemberVariables.Count == expectedMemberVariables)
        {
            structTypeName = EnsureRecordTypeDeclared(record: record);
            IEnumerable<string> fieldTypes =
                record.MemberVariables.Select(selector: mv =>
                    GetFieldStorageLlvmType(type: mv.Type));
            return string.Join(separator: ", ", values: fieldTypes);
        }

        // Fallback to the known physical layout when the type isn't registered yet (e.g. a bare
        // literal-only compilation without the stdlib carrier loaded).
        structTypeName = $"%Record.Core.{carrierName}";
        return expectedMemberVariables == 3
            ? "ptr, i64, ptr"
            : "ptr, i64";
    }

    /// <summary>
    /// Builds the constant initializer for a Bytes/Text literal, mapping the buffer pointer and element
    /// count onto the carrier's fields BY NAME (<c>data</c> → buffer, <c>count</c> → element count,
    /// any other field — the <c>ctrl</c> refcount controller — zero/null). Keying on field name keeps
    /// codegen independent of the physical field order; a reorder of the record moves the values with it.
    /// </summary>
    private string BuildLiteralCarrierValue(string carrierName, string dataName, long count)
    {
        TypeSymbol? carrier = _registry.LookupType(name: carrierName) ??
                            _registry.LookupType(name: $"Core.{carrierName}");
        if (carrier is RecordTypeSymbol record && record.MemberVariables.Count > 0)
        {
            IEnumerable<string> parts = record.MemberVariables.Select(selector: mv =>
            {
                string ft = GetFieldStorageLlvmType(type: mv.Type);
                return mv.Name switch
                {
                    "data" => $"ptr {dataName}",
                    "count" => $"{ft} {count}",
                    _ => ft == "ptr"
                        ? "ptr null"
                        : $"{ft} 0"
                };
            });
            return string.Join(separator: ", ", values: parts);
        }

        // Fallback matching the physical { ptr data, i64 count, ptr ctrl } layout.
        return $"ptr {dataName}, i64 {count}, ptr null";
    }

    /// <summary>
    /// Emits a Bytes literal (b"...") as a constant Bytes record.
    /// Bytes layout is derived from its registered fields (physically <c>{ ptr, i64, ptr }</c>:
    /// data, count, ctrl). Returns the loaded record value.
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

        // Bytes record literal — layout derived from the registered Bytes fields
        // (physically `{ ptr data, i64 count, ptr ctrl }`). The `ctrl` slot is null for static
        // literals; `store`/`destroy` treat null ctrl as a no-op so the literal
        // is never freed and refcount ops are skipped.
        string bytesLayout = BuildLiteralCarrierLayout(carrierName: "Bytes",
            expectedMemberVariables: 3,
            structTypeName: out string bytesStructType);
        string bytesValue =
            BuildLiteralCarrierValue(carrierName: "Bytes", dataName: dataName, count: count);
        EmitLine(sb: _globalDeclarations,
            line:
            $"{constName} = private unnamed_addr constant {{ {bytesLayout} }} {{ {bytesValue} }}");

        // Load the record value from the global. Bytes is a value-typed record, so call
        // sites expect the record by value, not a pointer. Use the named struct type so
        // the SSA value matches the call signature.
        string loaded = NextTemp();
        EmitLine(sb: sb, line: $"{loaded} = load {bytesStructType}, ptr {constName}");
        return loaded;
    }

    /// <summary>
    /// Strips the type suffix from a numeric literal string (e.g., "1_s32" "1", "3_14_b64" "3_14")
    /// and removes digit separator underscores.
    /// </summary>
    /// <summary>
    /// Converts a prefixed literal (0x hex, 0b binary, 0o octal) to decimal for LLVM IR.
    /// Hex floats (containing '.' or 'p') are passed through for EmitFloatLiteral.
    /// </summary>
    private static string ConvertPrefixedToDecimal(string value)
    {
        // A leading sign may be baked into signed wide literals (S128/S256); strip it, convert the
        // magnitude, and re-apply it so `-0x…` becomes a valid signed decimal rather than `-0x…`.
        string sign = "";
        string magnitude = value;
        if (magnitude.Length > 0 && magnitude[index: 0] is '+' or '-')
        {
            sign = magnitude[index: 0] == '-'
                ? "-"
                : "";
            magnitude = magnitude[1..];
        }

        if (magnitude.Length <= 2)
        {
            return value;
        }

        // Don't convert hex floats — they go through EmitFloatLiteral.
        if (magnitude.StartsWith(value: "0x",
                comparisonType: StringComparison.OrdinalIgnoreCase) &&
            magnitude.IndexOfAny(anyOf: ['.', 'p', 'P'], startIndex: 2) >= 0)
        {
            return value;
        }

        int numericBase = DetectNumericBase(magnitude: magnitude);
        if (numericBase == 0)
        {
            return value;
        }

        // Accumulate via BigInteger so wide (U128/U256/...) base-prefixed literals convert to their
        // full decimal value — a ulong path overflows past 64 bits and would leave the raw `0x…`
        // string in the IR, which LLVM rejects. Decimal literals pass straight through.
        return TryAccumulateBigInteger(magnitude: magnitude,
            numericBase: numericBase,
            acc: out System.Numerics.BigInteger acc)
            ? sign + acc.ToString()
            : value;
    }

    /// <summary>Returns the radix for a base-prefixed magnitude (0x/0b/0o), or 0 if unprefixed.</summary>
    private static int DetectNumericBase(string magnitude)
    {
        if (magnitude.StartsWith(value: "0x", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return 16;
        }

        if (magnitude.StartsWith(value: "0b", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (magnitude.StartsWith(value: "0o", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return 8;
        }

        return 0;
    }

    /// <summary>
    /// Accumulates the digits of <paramref name="magnitude"/> (after its 2-char prefix) in the given
    /// radix; returns false if any digit is invalid for the radix.
    /// </summary>
    private static bool TryAccumulateBigInteger(string magnitude, int numericBase,
        out System.Numerics.BigInteger acc)
    {
        acc = System.Numerics.BigInteger.Zero;
        foreach (char c in magnitude.AsSpan(start: 2))
        {
            int digit = char.ToLowerInvariant(c: c) switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => char.ToLowerInvariant(c: c) - 'a' + 10,
                _ => -1
            };
            if (digit < 0 || digit >= numericBase)
            {
                return false; // malformed digit — leave as-is
            }

            acc = acc * numericBase + digit;
        }

        return true;
    }

    /// <summary>
    /// Stores the numeric suffixes state used by this compiler phase.
    /// </summary>
    private static readonly string[] NumericSuffixes =
    [
        "addr", "s256", "u256", "s128", "u128", "s64", "u64", "s32", "u32",
        "s16", "u16", "s8", "u8", "b128", "b64", "b32", "b16",
        "d128", "d64", "d32"
    ];

    /// <summary>
    /// Performs the strip numeric suffix step for this compiler phase.
    /// </summary>
    private static string StripNumericSuffix(string text)
    {
        return ConvertPrefixedToDecimal(value: text[..NumericBodyLength(text: text)]
           .Replace(oldValue: "_", newValue: ""));
    }

    /// <summary>
    /// The length of a numeric literal's text before its type suffix, following the tokenizer's
    /// rules: an underscore followed by a known suffix running to the end (<c>1_s32</c>,
    /// <c>0xFF_u8</c>), or a known suffix written straight after the digits (<c>0u64</c>,
    /// <c>0x7Fs32</c>). In a hex literal the digits a-f are never a suffix, so <c>0x1b16</c> is the
    /// number 0x1B16 and <c>0xFF_AB</c> is 0xFFAB.
    /// </summary>
    private static int NumericBodyLength(string text)
    {
        int underscore = text.LastIndexOf(value: '_');
        if (underscore >= 0 && IsNumericSuffix(suffix: text[(underscore + 1)..]))
        {
            return underscore;
        }

        string magnitude = text.TrimStart('+', '-');
        if (magnitude.StartsWith(value: "0x", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            int i = text.Length - magnitude.Length + 2;
            while (i < text.Length && (Uri.IsHexDigit(character: text[index: i]) || text[index: i] == '_'))
            {
                i++;
            }

            return IsNumericSuffix(suffix: text[i..])
                ? i
                : text.Length;
        }

        string lower = text.ToLowerInvariant();
        string? matchedSuffix =
            NumericSuffixes.FirstOrDefault(predicate: s => lower.EndsWith(value: s));
        return matchedSuffix != null
            ? text.Length - matchedSuffix.Length
            : text.Length;
    }

    /// <summary>Whether <paramref name="suffix"/> is one of the known numeric type suffixes.</summary>
    private static bool IsNumericSuffix(string suffix)
    {
        return NumericSuffixes.Contains(value: suffix.ToLowerInvariant());
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

        // B128: use native parser for full 128-bit precision. B128 is an i128
        // bit carrier in LLVM (never fp128), so the literal is emitted as an
        // i128 integer constant holding the IEEE binary128 bit pattern
        // (LLVM hex integer syntax: u0x<Hi16hex><Lo16hex>).
        if (literalType == TokenType.B128Literal)
        {
            NumericLiteralParser.B128 b128 = NumericLiteralParser.ParseB128(str: numericValue);
            return $"u0x{b128.Hi:X16}{b128.Lo:X16}";
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
        // B128 quiet-NaN: exp=all-ones, MSB of mantissa set; B128 inf: exp=all-ones, mantissa=0.
        // Emitted as an i128 bit-pattern constant (B128 is never LLVM fp128).
        if (literalType == TokenType.B128Literal)
        {
            ulong hi = name == "nan"
                ? 0x7FFF800000000000UL
                : 0x7FFF000000000000UL;
            return $"u0x{hi:X16}0000000000000000";
        }

        double d = name == "nan"
            ? double.NaN
            : double.PositiveInfinity;
        return EmitDoubleAsLlvmHex(d: d, literalType: literalType);
    }

    /// <summary>
    /// Emit double as LLVM hex as part of this compiler phase.
    /// </summary>
    private static string EmitDoubleAsLlvmHex(double d, TokenType literalType)
    {
        if (literalType == TokenType.B16Literal)
        {
            // half constants use LLVM's 16-bit hex form `0xH<4 hex digits>` — NOT the 64-bit
            // double form (`0x...16 hex...`) the float/double branches below emit. Without this,
            // every B16 literal (incl. inf/nan, which also route here) emits invalid IR that
            // llvm-as rejects. Round the value to IEEE binary16 and emit its bit pattern.
            ushort halfBits = BitConverter.HalfToUInt16Bits(value: (Half)d);
            return $"0xH{halfBits:X4}";
        }

        if (literalType == TokenType.B32Literal)
        {
            // B32: promote to double for LLVM's float hex format
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
    /// Emits a hex float literal (<c>0x1.8p3</c>) as the exact bits the verifier already checked it
    /// encodes to: half as <c>0xH</c>, float/double in LLVM's double hex form, B128 as its i128 carrier.
    /// </summary>
    private static string EmitHexFloatLiteral(string cleaned, TokenType literalType)
    {
        (int mantBits, int expBits) = literalType switch
        {
            TokenType.B16Literal => (10, 5),
            TokenType.B32Literal => (23, 8),
            TokenType.B128Literal => (112, 15),
            _ => (52, 11)
        };
        if (NumericLiteralParser.TryEncodeHexFloat(cleaned: cleaned,
                mantBits: mantBits,
                expBits: expBits,
                bits: out UInt128 bits) != NumericLiteralParser.HexFloatStatus.Exact)
        {
            throw new InvalidOperationException(
                message:
                $"Hex float literal '{cleaned}' reached the LLVM emitter without an exact {literalType} encoding; the verifier should have rejected it.");
        }

        return literalType switch
        {
            TokenType.B16Literal => $"0xH{(ushort)bits:X4}",
            TokenType.B32Literal =>
                $"0x{BitConverter.DoubleToInt64Bits(value: BitConverter.UInt32BitsToSingle(value: (uint)bits)):X16}",
            TokenType.B128Literal => $"u0x{(ulong)(bits >> 64):X16}{(ulong)bits:X16}",
            _ => $"0x{(ulong)bits:X16}"
        };
    }

    /// <summary>
    /// Emits a decimal floating-point literal (D32, D64, D128) as raw integer bits.
    /// D32/D64 return scalar values. D128 emits insertvalue instructions and returns a temp.
    /// </summary>
    private static string EmitDecimalFloatLiteral(string numericValue, TokenType literalType)
    {
        // IEEE 754-2008 decimal: bit-pattern combination-field encodes special values.
        // Common-form: top 5 bits 11110 = inf, 11111 = NaN (quiet NaN: payload MSB 0).
        if ((numericValue == "inf" || numericValue == "nan") &&
            EmitSpecialDecimalFloatLiteral(isNan: numericValue == "nan", literalType: literalType)
                is { } special)
        {
            return special;
        }

        switch (literalType)
        {
            case TokenType.D32Literal:
                return NumericLiteralParser.EncodeD32Bid(str: numericValue)
                                           .Value
                                           .ToString();
            case TokenType.D64Literal:
                return NumericLiteralParser.EncodeD64Bid(str: numericValue)
                                           .Value
                                           .ToString();
            case TokenType.D128Literal:
            {
                // D128 is now @llvm("i128") BID; emit a single i128 constant (like B128).
                NumericLiteralParser.D128 d128 =
                    NumericLiteralParser.EncodeD128Bid(str: numericValue);
                return $"u0x{d128.Hi:X16}{d128.Lo:X16}";
            }
            case TokenType.DecimalLiteral:
            {
                // Decimal is @llvm("i128") BID (finite-only, canonical); emit a single i128 constant.
                NumericLiteralParser.D128 d =
                    NumericLiteralParser.EncodeDecimalCanonical(str: numericValue);
                return $"u0x{d.Hi:X16}{d.Lo:X16}";
            }
            default:
                return numericValue;
        }
    }

    /// <summary>
    /// Emits the raw bit-pattern constant for a decimal inf/NaN special value (combination-field
    /// encoding: 0x78.. inf, 0x7C.. quiet NaN). Returns null for a non-decimal literal type so the
    /// caller falls through to normal encoding.
    /// </summary>
    private static string? EmitSpecialDecimalFloatLiteral(bool isNan, TokenType literalType)
    {
        switch (literalType)
        {
            case TokenType.D32Literal:
                return (isNan
                    ? 0x7C000000U
                    : 0x78000000U).ToString();
            case TokenType.D64Literal:
                return (isNan
                    ? 0x7C00000000000000UL
                    : 0x7800000000000000UL).ToString();
            case TokenType.D128Literal:
            {
                // D128 is now @llvm("i128") BID; emit a single i128 constant. The combination
                // prefix (0x78.. inf / 0x7C.. nan) lives in the high 64 bits, low bits zero.
                ulong hi = isNan
                    ? 0x7C00000000000000UL
                    : 0x7800000000000000UL;
                return $"u0x{hi:X16}0000000000000000";
            }
            // Decimal is finite-only: inf/nan Decimal literals are rejected by the analyzer, so
            // there is no DecimalLiteral special case here (fall through to null).
            default:
                return null;
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
        // Load the record value from the global. Text is a value-typed record, so call
        // sites expect the record by value, not a pointer. Use the named struct type
        // (derived from the registered Text fields) so the SSA value matches the call
        // signature. The optimizer collapses redundant loads of the same global.
        _ = BuildLiteralCarrierLayout(carrierName: "Text",
            expectedMemberVariables: 3,
            structTypeName: out string textStructType);
        string loaded = NextTemp();
        EmitLine(sb: sb, line: $"{loaded} = load {textStructType}, ptr {constName}");
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

        // Layer 2: Text record payload — layout derived from the registered Text fields
        // (physically `{ ptr data, i64 count, ptr ctrl }`). `ctrl` is null for static literals —
        // store/destroy short-circuit on null and never free the literal or touch the refcount.
        string textLayout = BuildLiteralCarrierLayout(carrierName: "Text",
            expectedMemberVariables: 3,
            structTypeName: out _);
        string textValue =
            BuildLiteralCarrierValue(carrierName: "Text", dataName: dataName, count: count);
        EmitLine(sb: _globalDeclarations,
            line:
            $"{constName} = private unnamed_addr constant {{ {textLayout} }} {{ {textValue} }}");

        return constName;
    }
}
