using System.Numerics;
using Builder.Verification;

namespace RazorForge.Tests.Meta;

/// <summary>
/// Validates the managed <see cref="NumericLiteralParser.EncodeDecimalCanonical"/> (compile-time
/// decimal literal → finite-only CANONICAL 34-digit BID decimal128 bits) by round-trip: decoding
/// the produced i128 back to (sign, exp, coeff) — exactly as <c>Core.D128</c> <c>decode()</c> does
/// — must reproduce the literal (exact for ≤34 significant digits, with fractional trailing zeros
/// stripped). Layout: bit127 sign, bits126..113 biased exponent (bias 6176), bits112..0 coefficient.
/// </summary>
public sealed class DecimalEncoderTests
{
    private const int Bias = 6176;

    private static (bool sign, int exp, BigInteger coeff) Decode(NumericLiteralParser.D128 d)
    {
        UInt128 bits = (UInt128)d.Hi << 64 | d.Lo;
        bool sign = (bits & ((UInt128)1 << 127)) != 0;
        int biased = (int)(uint)(bits >> 113 & 0x3FFF);
        BigInteger coeff = (BigInteger)(bits & (((UInt128)1 << 113) - 1));
        return (sign, biased - Bias, coeff);
    }

    [Theory]
    [InlineData("0", false, 0)]
    [InlineData("1", false, 0)]
    [InlineData("3.14159", false, -5)]
    [InlineData("-2.5", true, -1)]
    [InlineData("100", false, 0)]
    [InlineData("0.001", false, -3)]
    [InlineData("1e1000", false, 1000)]
    [InlineData("1e-1000", false, -1000)]
    [InlineData("6.02214076e23", false, 15)]
    public void EncodeDecimalCanonical_RoundTrips(string s, bool wantSign, int wantExpHint)
    {
        NumericLiteralParser.D128 enc = NumericLiteralParser.EncodeDecimalCanonical(str: s);
        (bool sign, int exp, BigInteger coeff) = Decode(d: enc);

        Assert.Equal(expected: wantSign, actual: sign);
        Assert.Equal(expected: wantExpHint, actual: exp);

        // Reconstruct value = (sign) coeff * 10^exp and compare to the literal num/den exactly
        // (these inputs all have <= 34 significant digits, so encoding is exact).
        BigInteger sCoeff = sign
            ? -coeff
            : coeff;
        ParseRational(s: s, num: out BigInteger num, den: out BigInteger den);
        BigInteger lhs, rhs;
        if (exp >= 0)
        {
            lhs = sCoeff * BigInteger.Pow(value: 10, exponent: exp) * den;
            rhs = num;
        }
        else
        {
            lhs = sCoeff * den;
            rhs = num * BigInteger.Pow(value: 10, exponent: -exp);
        }

        Assert.Equal(expected: rhs, actual: lhs);
    }

    [Fact]
    public void EncodeDecimalCanonical_OverflowThrows()
    {
        // decimal128 emax is 6144; 1e6200 (coeff 1, exp 6200) exceeds it -> overflow -> throw.
        Assert.Throws<OverflowException>(testCode: () =>
            NumericLiteralParser.EncodeDecimalCanonical(str: "1e6200"));
    }

    private static void ParseRational(string s, out BigInteger num, out BigInteger den)
    {
        int e = s.IndexOf(value: 'e');
        int exp10 = e >= 0
            ? int.Parse(s: s[(e + 1)..])
            : 0;
        string mant = e >= 0
            ? s[..e]
            : s;
        bool neg = mant.StartsWith(value: '-');
        if (neg || mant.StartsWith(value: '+'))
        {
            mant = mant[1..];
        }

        int dot = mant.IndexOf(value: '.');
        if (dot >= 0)
        {
            exp10 -= mant.Length - dot - 1;
            mant = mant.Remove(startIndex: dot, count: 1);
        }

        var coeff = BigInteger.Parse(value: mant);
        if (neg)
        {
            coeff = -coeff;
        }

        if (exp10 >= 0)
        {
            num = coeff * BigInteger.Pow(value: 10, exponent: exp10);
            den = BigInteger.One;
        }
        else
        {
            num = coeff;
            den = BigInteger.Pow(value: 10, exponent: -exp10);
        }
    }
}
