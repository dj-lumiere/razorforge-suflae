using Builder.Verification;

namespace RazorForge.Tests.Meta;

/// <summary>
/// Pins the managed <see cref="NumericLiteralParser.EncodeB128"/> (pure C#, BigInteger,
/// round-to-nearest-even) against IEEE binary128 bit patterns computed independently with exact
/// rational arithmetic, for a battery of decimal literals including the extremes of the finite
/// range. Subnormals are checked by round-trip to within half a ULP.
/// </summary>
public sealed class B128EncoderTests
{
    [Theory]
    [InlineData("0", 0x0000000000000000UL, 0x0000000000000000UL)]
    [InlineData("1", 0x3FFF000000000000UL, 0x0000000000000000UL)]
    [InlineData("2", 0x4000000000000000UL, 0x0000000000000000UL)]
    [InlineData("0.5", 0x3FFE000000000000UL, 0x0000000000000000UL)]
    [InlineData("0.25", 0x3FFD000000000000UL, 0x0000000000000000UL)]
    [InlineData("0.1", 0x3FFB999999999999UL, 0x999999999999999AUL)]
    [InlineData("0.2", 0x3FFC999999999999UL, 0x999999999999999AUL)]
    [InlineData("3.14159", 0x4000921F9F01B866UL, 0xE43AA79BBADC0981UL)]
    [InlineData("3.141592653589793238462643383279502884", 0x4000921FB54442D1UL, 0x8469898CC51701B8UL)]
    [InlineData("2.718281828459045235360287471352662498", 0x40005BF0A8B14576UL, 0x95355FB8AC404E7AUL)]
    [InlineData("10", 0x4002400000000000UL, 0x0000000000000000UL)]
    [InlineData("100", 0x4005900000000000UL, 0x0000000000000000UL)]
    [InlineData("1000000", 0x4012E84800000000UL, 0x0000000000000000UL)]
    [InlineData("123456789.123456789", 0x4019D6F34547E6B7UL, 0x4DCE58D7CC490820UL)]
    [InlineData("1e10", 0x40202A05F2000000UL, 0x0000000000000000UL)]
    [InlineData("1e-10", 0x3FDDB7CDFD9D7BDBUL, 0xAB7D6AE6881CB511UL)]
    [InlineData("1e100", 0x414B249AD2594C37UL, 0xCEB0B2784C4CE0BFUL)]
    [InlineData("1e-100", 0x3EB2BFF2EE48E052UL, 0xFD7AB2F0FC572779UL)]
    [InlineData("1e1000", 0x4CF8E71B63F3BA7BUL, 0x580AF1A52D2A7379UL)]
    [InlineData("1e-1000", 0x33050D152311513CUL, 0x28CE202627C06EC2UL)]
    [InlineData("1e4000", 0x73E6A3750647FCABUL, 0x18C21AB905450CC3UL)]
    [InlineData("1e-4000", 0x0C17387AE70C9E70UL, 0x0B8049732D11A23DUL)]
    [InlineData("1.18973149535723176508575932662800702e4932", 0x7FFEFFFFFFFFFFFFUL, 0xFFFFFFFFFFFFFFFFUL)]
    [InlineData("3.36210314311209350626267781732175260e-4932", 0x0001000000000000UL, 0x0000000000000000UL)]
    [InlineData("9.99999999999999999999999999999999999e4931", 0x7FFEAE596552B8FDUL, 0xED99D037E3D04B75UL)]
    [InlineData("0.333333333333333333333333333333333333", 0x3FFD555555555555UL, 0x5555555555555555UL)]
    [InlineData("7", 0x4001C00000000000UL, 0x0000000000000000UL)]
    [InlineData("0.0001220703125", 0x3FF2000000000000UL, 0x0000000000000000UL)]
    [InlineData("12345678901234567890123456789012345678", 0x407A29361EDE0046UL, 0x627889320A1BC71EUL)]
    public void EncodeB128_MatchesReferenceBits_OnFiniteNormals(string s, ulong hi, ulong lo)
    {
        NumericLiteralParser.B128 managed = NumericLiteralParser.EncodeB128(str: s);
        Assert.Equal(expected: (hi, lo), actual: (managed.Hi, managed.Lo));
    }

    /// <summary>
    /// The managed encoder produces correct binary128 SUBNORMALS (not flushed to zero). Verified
    /// self-consistently by round-trip: decoding the managed bits back to a rational reproduces the
    /// literal within half a ULP.
    /// </summary>
    [Theory]
    [InlineData("1e-4940")]
    [InlineData("5e-4945")]
    [InlineData("1.5e-4950")]
    [InlineData("1e-4960")]
    [InlineData("6.475175119438025110924438958227646552e-4966")] // smallest subnormal
    public void EncodeB128_SubnormalsAreCorrect(string s)
    {
        NumericLiteralParser.B128 managed = NumericLiteralParser.EncodeB128(str: s);

        System.Numerics.BigInteger bits =
            (System.Numerics.BigInteger)managed.Hi << 64 | managed.Lo;
        int biasedExp = (int)(bits >> 112 & 0x7FFF);
        System.Numerics.BigInteger mant = bits & (System.Numerics.BigInteger.One << 112) - 1;
        Assert.Equal(expected: 0, actual: biasedExp); // subnormal
        Assert.NotEqual(expected: System.Numerics.BigInteger.Zero, actual: mant); // not flushed

        // value = mant * 2^-16494; compare to the literal num/den within 0.5 ULP (one ULP of the
        // mantissa equals `den` after scaling both sides by `den`).
        ParseLiteralRational(s: s,
            num: out System.Numerics.BigInteger num,
            den: out System.Numerics.BigInteger den);
        var diff = System.Numerics.BigInteger.Abs(value: mant * den - (num << 16494));
        Assert.True(condition: diff * 2 <= den,
            userMessage: $"managed subnormal off by > 0.5 ULP for {s}");
    }

    private static void ParseLiteralRational(string s, out System.Numerics.BigInteger num,
        out System.Numerics.BigInteger den)
    {
        int e = s.IndexOf(value: 'e');
        int exp10 = e >= 0
            ? int.Parse(s: s[(e + 1)..])
            : 0;
        string mant = e >= 0
            ? s[..e]
            : s;
        int dot = mant.IndexOf(value: '.');
        if (dot >= 0)
        {
            exp10 -= mant.Length - dot - 1;
            mant = mant.Remove(startIndex: dot, count: 1);
        }

        var coeff = System.Numerics.BigInteger.Parse(value: mant);
        if (exp10 >= 0)
        {
            num = coeff * System.Numerics.BigInteger.Pow(value: 10, exponent: exp10);
            den = System.Numerics.BigInteger.One;
        }
        else
        {
            num = coeff;
            den = System.Numerics.BigInteger.Pow(value: 10, exponent: -exp10);
        }
    }
}
