using System;
using System.Collections.Generic;
using TypeModel.Enums;
using TypeModel.Symbols;

namespace TypeModel.Types;

/// <summary>
/// Type information for flags (bitmask types with named members).
/// Backed by <c>i64</c> at the LLVM level. Max 64 members, auto-assigned power-of-two bit positions.
/// Only builder-generated operators allowed.
/// </summary>
public sealed class FlagsTypeInfo : RecordTypeInfo
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.Flags;

    /// <summary>The members of this flags type.</summary>
    public IReadOnlyList<FlagsMemberInfo> Members { get; init; } = [];

    /// <summary>Protocols this flags type implements (obeys).</summary>
    public new IReadOnlyList<TypeInfo> ImplementedProtocols
    {
        get => base.ImplementedProtocols;
        init => base.ImplementedProtocols = value;
    }

    /// <summary>Creates a new flags type with the given name and default i64 backend type.</summary>
    public FlagsTypeInfo(string name) : base(name: name)
    {
        BackendType = "i64";
    }

 
    /// <inheritdoc/>
    public override TypeInfo CreateInstance(IReadOnlyList<TypeInfo> typeArguments)
    {
        throw new InvalidOperationException(
            message: $"Flags type '{Name}' cannot be resolved with type arguments.");
    }
}

/// <summary>A single flags member.</summary>
/// <param name="Name">The name (SCREAMING_SNAKE_CASE).</param>
/// <param name="BitPosition">The bit position (0-63). Bitmask = 1UL &lt;&lt; BitPosition.</param>
public sealed record FlagsMemberInfo(string Name, int BitPosition);
