using Compilers.Shared.AST;

namespace Compilers.Analysis.Results;

/// <summary>
/// Represents a parsed numeric literal value ready for code generation.
/// Used for types that require native library parsing (f128, d32, d64, d128, Integer, Decimal).
/// </summary>
public abstract record ParsedLiteral(SourceLocation Location);

/// <summary>
/// Parsed f128 (IEEE binary128) value.
/// </summary>
public sealed record ParsedF128(SourceLocation Location, ulong Lo, ulong Hi) : ParsedLiteral(Location)
{
    public override string ToString() => $"f128(0x{Hi:X16}{Lo:X16})";
}

/// <summary>
/// Parsed d32 (IEEE decimal32) value.
/// </summary>
public sealed record ParsedD32(SourceLocation Location, uint Value) : ParsedLiteral(Location)
{
    public override string ToString() => $"d32(0x{Value:X8})";
}

/// <summary>
/// Parsed d64 (IEEE decimal64) value.
/// </summary>
public sealed record ParsedD64(SourceLocation Location, ulong Value) : ParsedLiteral(Location)
{
    public override string ToString() => $"d64(0x{Value:X16})";
}

/// <summary>
/// Parsed d128 (IEEE decimal128) value.
/// </summary>
public sealed record ParsedD128(SourceLocation Location, ulong Lo, ulong Hi) : ParsedLiteral(Location)
{
    public override string ToString() => $"d128(0x{Hi:X16}{Lo:X16})";
}

/// <summary>
/// Parsed arbitrary-precision Integer value.
/// </summary>
public sealed record ParsedInteger(
    SourceLocation Location,
    byte[] Limbs,
    int Sign,
    long Exponent) : ParsedLiteral(Location)
{
    public bool IsNegative => Sign != 0;
    public override string ToString() => $"Integer({(IsNegative ? "-" : "")}{Limbs.Length} limbs)";
}

/// <summary>
/// Parsed arbitrary-precision Decimal value.
/// Contains the string representation for native code generation.
/// </summary>
public sealed record ParsedDecimal(
    SourceLocation Location,
    string StringValue,
    int Sign,
    int Exponent,
    int SignificantDigits,
    bool IsInteger) : ParsedLiteral(Location)
{
    public bool IsNegative => Sign < 0;
    public bool IsZero => Sign == 0;
    public override string ToString() => $"Decimal({StringValue}, exp={Exponent})";
}
