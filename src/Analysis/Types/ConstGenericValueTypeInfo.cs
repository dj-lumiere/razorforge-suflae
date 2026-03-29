namespace SemanticAnalysis.Types;

using Enums;

/// <summary>
/// Represents a compile-time constant value used as a generic argument.
/// For example, the <c>4</c> in <c>ValueList[S64, 4]</c> or the <c>8</c> in <c>ValueBitList[8]</c>.
/// The <see cref="TypeInfo.Name"/> is the literal text (e.g., "4", "8u64") so that
/// generic resolution names include the value (e.g., "ValueList[S64, 4]").
/// </summary>
public sealed class ConstGenericValueTypeInfo : TypeInfo
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.ConstGenericValue;

    /// <summary>The numeric value of this const generic.</summary>
    public long Value { get; }

    /// <summary>
    /// The explicit type name if a typed literal was used (e.g., "U64" for "4u64"),
    /// or null for untyped integer literals (e.g., "4").
    /// </summary>
    public string? ExplicitTypeName { get; }

    public ConstGenericValueTypeInfo(string literalText, long value, string? explicitTypeName)
        : base(name: literalText)
    {
        Value = value;
        ExplicitTypeName = explicitTypeName;
    }

    /// <inheritdoc/>
    public override TypeInfo CreateInstance(IReadOnlyList<TypeInfo> typeArguments)
    {
        throw new InvalidOperationException(
            message: "Cannot create instance of a const generic value.");
    }
}
