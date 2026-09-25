using System.Numerics;

namespace Builder.Verification;

/// <summary>
/// Exact encoding of hexadecimal float literals (<c>0x1.8p3</c>, <c>0x1p-23</c>) into IEEE 754 binary
/// float bits. Shared by the semantic verifier (which reports the diagnostics) and the LLVM emitter
/// (which only emits the bits of a literal the verifier already accepted).
/// </summary>
public static partial class NumericLiteralParser
{
    /// <summary>Outcome of <see cref="TryEncodeHexFloat"/>.</summary>
    public enum HexFloatStatus
    {
        /// <summary>The literal is exactly representable and the bits were produced.</summary>
        Exact,

        /// <summary>The text is not a well-formed hex float (<c>0x</c> digits, optional <c>.</c>
        /// fraction, then a required <c>p</c> exponent).</summary>
        Malformed,

        /// <summary>The value needs more significant bits than the format's precision.</summary>
        Inexact,

        /// <summary>The value's lowest bit lies below the format's smallest subnormal.</summary>
        BelowSubnormal,

        /// <summary>The value is beyond the format's largest finite value.</summary>
        Overflow
    }

    /// <summary>
    /// Whether <paramref name="text"/> (underscores and type suffix may still be present) spells a
    /// hexadecimal float literal: a <c>0x</c> prefix with a <c>p</c> binary exponent.
    /// </summary>
    public static bool IsHexFloatText(string text)
    {
        string body = text.TrimStart('+', '-');
        return body.StartsWith(value: "0x", comparisonType: StringComparison.OrdinalIgnoreCase) &&
               body.IndexOfAny(anyOf: ['p', 'P'], startIndex: 2) >= 0;
    }

    /// <summary>
    /// Encodes a hexadecimal float literal into the bits of a binary float format, EXACTLY: the value
    /// hex-mantissa × 2^exponent must fit the format without rounding (a hex float spells its bits, so
    /// rounding one would silently change what was written).
    /// </summary>
    /// <param name="cleaned">The literal without underscores or type suffix (an optional leading sign
    /// is allowed).</param>
    /// <param name="mantBits">Stored fraction bits (10 / 23 / 52 / 112 for B16 / B32 / B64 / B128).</param>
    /// <param name="expBits">Exponent field width (5 / 8 / 11 / 15).</param>
    /// <param name="bits">The IEEE 754 bit pattern in the low bits, on <see cref="HexFloatStatus.Exact"/>.</param>
    public static HexFloatStatus TryEncodeHexFloat(string cleaned, int mantBits, int expBits,
        out UInt128 bits)
    {
        bits = UInt128.Zero;
        int i = 0;
        bool negative = false;
        if (cleaned.Length > 0 && cleaned[index: 0] is '+' or '-')
        {
            negative = cleaned[index: 0] == '-';
            i = 1;
        }

        if (!cleaned.AsSpan(start: i).StartsWith(value: "0x", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return HexFloatStatus.Malformed;
        }

        i += 2;
        BigInteger mantissa = BigInteger.Zero;
        int digits = 0;
        int fractionDigits = 0;
        bool seenDot = false;
        while (i < cleaned.Length && cleaned[index: i] is not ('p' or 'P'))
        {
            char c = cleaned[index: i];
            if (c == '.')
            {
                if (seenDot)
                {
                    return HexFloatStatus.Malformed;
                }

                seenDot = true;
            }
            else
            {
                int digit = HexDigitValue(c: c);
                if (digit < 0)
                {
                    return HexFloatStatus.Malformed;
                }

                mantissa = mantissa * 16 + digit;
                digits++;
                if (seenDot)
                {
                    fractionDigits++;
                }
            }

            i++;
        }

        if (digits == 0 || i >= cleaned.Length ||
            !int.TryParse(s: cleaned.AsSpan(start: i + 1), result: out int exponent))
        {
            return HexFloatStatus.Malformed;
        }

        UInt128 sign = negative
            ? UInt128.One << mantBits + expBits
            : UInt128.Zero;
        if (mantissa.IsZero)
        {
            bits = sign;
            return HexFloatStatus.Exact;
        }

        // value = mantissa × 2^e2, reduced so the mantissa is odd (its fewest significant bits).
        long e2 = (long)exponent - 4L * fractionDigits;
        while (mantissa.IsEven)
        {
            mantissa >>= 1;
            e2++;
        }

        long length = (long)mantissa.GetBitLength();
        long leading = e2 + length - 1; // exponent of the leading bit
        long bias = (1L << expBits - 1) - 1;
        long emin = 1 - bias;
        if (leading > bias)
        {
            return HexFloatStatus.Overflow;
        }

        if (length > mantBits + 1)
        {
            return HexFloatStatus.Inexact;
        }

        if (e2 < emin - mantBits)
        {
            return HexFloatStatus.BelowSubnormal;
        }

        UInt128 m = (UInt128)mantissa;
        if (leading < emin)
        {
            // Subnormal: exponent field 0, the fraction holds the value in units of 2^(emin - mantBits).
            bits = sign | m << (int)(e2 - (emin - mantBits));
            return HexFloatStatus.Exact;
        }

        UInt128 fraction = (m << (int)(mantBits + 1 - length)) & ((UInt128.One << mantBits) - 1);
        bits = sign | (UInt128)(ulong)(leading + bias) << mantBits | fraction;
        return HexFloatStatus.Exact;
    }

    /// <summary>Returns the value of a hex digit, or -1 for any other character.</summary>
    private static int HexDigitValue(char c)
    {
        return c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1
        };
    }
}
