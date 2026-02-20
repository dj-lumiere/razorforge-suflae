namespace Compilers.Analysis.Types;

using Enums;

/// <summary>
/// Type information for flags (bitmask types with named members).
/// Backed by U64; max 64 members. Members are auto-assigned power-of-two bit positions.
/// Only compiler-generated operators allowed (and, but, is, isnot, isonly).
/// </summary>
public sealed class FlagsTypeInfo : TypeInfo
{
    public override TypeCategory Category => TypeCategory.Flags;

    /// <summary>The members of this flags type.</summary>
    public IReadOnlyList<FlagsMemberInfo> Members { get; init; } = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="FlagsTypeInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the flags type.</param>
    public FlagsTypeInfo(string name) : base(name: name)
    {
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Always thrown as flags types cannot be generic.</exception>
    public override TypeInfo Instantiate(IReadOnlyList<TypeInfo> typeArguments)
    {
        throw new InvalidOperationException(
            message: $"Flags type '{Name}' cannot be instantiated with type arguments.");
    }
}

/// <summary>
/// Information about a single flags member.
/// </summary>
/// <param name="Name">The name of the member (SCREAMING_SNAKE_CASE).</param>
/// <param name="BitPosition">The bit position (0-63). The bitmask value is 1UL &lt;&lt; BitPosition.</param>
public sealed record FlagsMemberInfo(string Name, int BitPosition);
