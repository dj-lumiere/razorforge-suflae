using Builder.Diagnostics;
using Builder.Verification;
using Builder.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Hexadecimal float literals (<c>0x1.8p3</c>): the exact encoder, and the verifier's rule that only
/// a binary float takes one and only when the value fits it exactly.
/// </summary>
public class HexFloatLiteralTests
{
    /// <summary>Verifies exact encodings, including subnormals, sign, and the B128 carrier.</summary>
    [Theory]
    [InlineData("0x1.8p3", 52, 11, "4028000000000000")] // 12.0
    [InlineData("0x1p-23", 23, 8, "34000000")] // B32 epsilon
    [InlineData("0x1.fffffep127", 23, 8, "7F7FFFFF")] // B32 max
    [InlineData("0x1p-149", 23, 8, "00000001")] // smallest B32 subnormal
    [InlineData("0x1.ffcp15", 10, 5, "7BFF")] // B16 max
    [InlineData("-0x1p0", 52, 11, "BFF0000000000000")]
    [InlineData("0x0p0", 52, 11, "0000000000000000")]
    [InlineData("0x10p-4", 52, 11, "3FF0000000000000")] // unnormalized mantissa, 1.0
    [InlineData("0x1.0000000000000000000000000001p0", 112, 15,
        "3FFF0000000000000000000000000001")] // 1 + 2^-112
    public void Encode_Exact(string text, int mantBits, int expBits, string expectedHex)
    {
        NumericLiteralParser.HexFloatStatus status = NumericLiteralParser.TryEncodeHexFloat(cleaned: text,
            mantBits: mantBits,
            expBits: expBits,
            bits: out UInt128 bits);
        Assert.Equal(expected: NumericLiteralParser.HexFloatStatus.Exact, actual: status);
        Assert.Equal(expected: UInt128.Parse(s: expectedHex, style: System.Globalization.NumberStyles.HexNumber),
            actual: bits);
    }

    /// <summary>Verifies the rejected shapes: too many bits, below the subnormals, overflow, malformed.</summary>
    [Theory]
    [InlineData("0x1.000001p0", NumericLiteralParser.HexFloatStatus.Inexact)]
    [InlineData("0x1p-150", NumericLiteralParser.HexFloatStatus.BelowSubnormal)]
    [InlineData("0x1p128", NumericLiteralParser.HexFloatStatus.Overflow)]
    [InlineData("0x1.8", NumericLiteralParser.HexFloatStatus.Malformed)]
    [InlineData("0xp3", NumericLiteralParser.HexFloatStatus.Malformed)]
    public void Encode_Rejected_B32(string text, NumericLiteralParser.HexFloatStatus expected)
    {
        Assert.Equal(expected: expected,
            actual: NumericLiteralParser.TryEncodeHexFloat(cleaned: text,
                mantBits: 23,
                expBits: 8,
                bits: out _));
    }

    /// <summary>Verifies a hex float on a decimal float type is RF-S017.</summary>
    [Theory]
    [InlineData("var x = 0x1p3_d64")]
    [InlineData("var x: D64 = 0x1p3")]
    [InlineData("var x = 0x1p3dn")]
    public void DecimalTarget_IsRejected(string statement)
    {
        AnalysisResult result = AnalyzeSa(source: $"""
                                                   routine probe()
                                                     {statement}
                                                     return
                                                   """);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.HexFloatLiteralNotBinaryFloat);
    }

    /// <summary>Verifies a hex float that does not fit its binary float exactly is RF-S018.</summary>
    [Theory]
    [InlineData("var x = 0x1.000001p0_b32")]
    [InlineData("var x: B32 = 0x1p-150")]
    public void InexactValue_IsRejected(string statement)
    {
        AnalysisResult result = AnalyzeSa(source: $"""
                                                   routine probe()
                                                     {statement}
                                                     return
                                                   """);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.HexFloatLiteralInexact);
    }

    /// <summary>Verifies exact hex floats on binary floats analyze cleanly.</summary>
    [Fact]
    public void BinaryTargets_Analyze()
    {
        AnalysisResult result = AnalyzeSa(source: """
                                                  routine probe()
                                                    var a = 0x1.8p3
                                                    var b: B32 = 0x1p-149
                                                    var c = 0x1.ffcp15_b16
                                                    var d = 0x1.0000000000000000000000000001p0_b128
                                                    return
                                                  """);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code is SemanticDiagnosticCode.HexFloatLiteralNotBinaryFloat
                or SemanticDiagnosticCode.HexFloatLiteralInexact
                or SemanticDiagnosticCode.NumericLiteralParseFailed);
    }
}
