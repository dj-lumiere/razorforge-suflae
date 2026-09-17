using System.Numerics;
using SyntaxTree;

namespace Builder.Verification.Results;

/// <summary>
/// Represents a parsed numeric literal value ready for code generation.
/// Used for types that require native library parsing (b128, d32, d64, d128, Integer, Decimal).
/// </summary>
public abstract record ParsedLiteral(SourceLocation Location);

/// <summary>
/// Parsed b128 (IEEE binary128) value.
/// </summary>
public sealed record ParsedB128(SourceLocation Location, ulong Lo, ulong Hi)
    : ParsedLiteral(Location: Location)
{
    /// <inheritdoc/>
    public override string ToString()
    {
        return $"b128(0x{Hi:X16}{Lo:X16})";
    }
}

/// <summary>
/// Parsed d32 (IEEE decimal32) value.
/// </summary>
public sealed record ParsedD32(SourceLocation Location, uint Value)
    : ParsedLiteral(Location: Location)
{
    /// <inheritdoc/>
    public override string ToString()
    {
        return $"d32(0x{Value:X8})";
    }
}

/// <summary>
/// Parsed d64 (IEEE decimal64) value.
/// </summary>
public sealed record ParsedD64(SourceLocation Location, ulong Value)
    : ParsedLiteral(Location: Location)
{
    /// <inheritdoc/>
    public override string ToString()
    {
        return $"d64(0x{Value:X16})";
    }
}

/// <summary>
/// Parsed d128 (IEEE decimal128) value.
/// </summary>
public sealed record ParsedD128(SourceLocation Location, ulong Lo, ulong Hi)
    : ParsedLiteral(Location: Location)
{
    /// <inheritdoc/>
    public override string ToString()
    {
        return $"d128(0x{Hi:X16}{Lo:X16})";
    }
}

/// <summary>
/// Parsed arbitrary-precision Integer value.
/// </summary>
public sealed record ParsedInteger(SourceLocation Location, byte[] Limbs, int Sign, long Exponent)
    : ParsedLiteral(Location: Location)
{
    /// <summary>
    /// Gets whether the parsed integer has a negative sign.
    /// </summary>
    public bool IsNegative => Sign != 0;

    /// <inheritdoc/>
    public override string ToString()
    {
        return $"Integer({(IsNegative ? "-" : "")}{Limbs.Length} limbs)";
    }
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
    bool IsInteger) : ParsedLiteral(Location: Location)
{
    /// <summary>
    /// Gets whether the parsed decimal has a negative sign.
    /// </summary>
    public bool IsNegative => Sign < 0;

    /// <summary>
    /// Gets whether the parsed decimal represents zero.
    /// </summary>
    public bool IsZero => Sign == 0;

    /// <inheritdoc/>
    public override string ToString()
    {
        return $"Decimal({StringValue}, exp={Exponent})";
    }
}

/// <summary>
/// Parsed fixed-width signed integer value (S8, S16, S32, S64, S128).
/// </summary>
public sealed record ParsedSignedInt(SourceLocation Location, string TypeName, Int128 Value)
    : ParsedLiteral(Location: Location)
{
    /// <inheritdoc/>
    public override string ToString()
    {
        return $"{TypeName}({Value})";
    }
}

/// <summary>
/// Parsed fixed-width unsigned integer value (U8, U16, U32, U64, U128).
/// </summary>
public sealed record ParsedUnsignedInt(SourceLocation Location, string TypeName, UInt128 Value)
    : ParsedLiteral(Location: Location)
{
    /// <inheritdoc/>
    public override string ToString()
    {
        return $"{TypeName}({Value})";
    }
}

/// <summary>
/// Parsed fixed-width wide integer value (S256, U256) — wider than any .NET native integer,
/// so the magnitude is carried as a <see cref="BigInteger"/>. Codegen emits the literal from the
/// raw decimal string (LLVM accepts arbitrary-width integer constants); this record only carries
/// the validated value for the semantic phase.
/// </summary>
public sealed record ParsedWideInt(SourceLocation Location, string TypeName, BigInteger Value)
    : ParsedLiteral(Location: Location)
{
    /// <inheritdoc/>
    public override string ToString()
    {
        return $"{TypeName}({Value})";
    }
}

/// <summary>
/// Parsed fixed-width float value (B16, B32, B64).
/// B128 uses ParsedB128 with native library parsing.
/// </summary>
public sealed record ParsedFloat(SourceLocation Location, string TypeName, double Value)
    : ParsedLiteral(Location: Location)
{
    /// <inheritdoc/>
    public override string ToString()
    {
        return $"{TypeName}({Value})";
    }
}

/// <summary>
/// Parsed Duration literal value stored as nanoseconds.
/// Supports: ns, us, ms, s, m, h, d, w suffixes.
/// </summary>
public sealed record ParsedDuration(SourceLocation Location, long Nanoseconds, string OriginalUnit)
    : ParsedLiteral(Location: Location)
{
    /// <inheritdoc/>
    public override string ToString()
    {
        return $"Duration({Nanoseconds}ns, original={OriginalUnit})";
    }
}

/// <summary>
/// Parsed ByteSize literal value.
/// Supports: b, kb, kib, mb, mib, gb, gib suffixes.
/// </summary>
public sealed record ParsedByteSize(SourceLocation Location, ulong Bytes, string OriginalUnit)
    : ParsedLiteral(Location: Location)
{
    /// <inheritdoc/>
    public override string ToString()
    {
        return $"ByteSize({Bytes}b, original={OriginalUnit})";
    }
}

/// <summary>
/// Parsed width-less imaginary literal (<c>4.0i</c>). Validation-only: it retains the magnitude
/// string, and <see cref="Builder.Lowering.Passes.LiteralLoweringPass"/> rebuilds the pure-imaginary
/// constructor for the resolved complex type (C64/C128/C256/Complex). The magnitude carries no width;
/// the surrounding complex type fixes the component radix/precision.
/// </summary>
public sealed record ParsedImaginary(SourceLocation Location, string Magnitude)
    : ParsedLiteral(Location: Location)
{
    /// <inheritdoc/>
    public override string ToString()
    {
        return $"Imaginary({Magnitude}i)";
    }
}
