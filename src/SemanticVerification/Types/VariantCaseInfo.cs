namespace SemanticVerification.Types;

using SyntaxTree;

/// <summary>
/// Information about a single member in a type-based variant.
/// Members are either real types (S64, Text, etc.) or the None state (zero-sized, no payload).
/// </summary>
public sealed class VariantMemberInfo
{
    /// <summary>The member type, or null for the None state.</summary>
    public TypeInfo? Type { get; }

    /// <summary>Whether this member is the None state.</summary>
    public bool IsNone => Type == null;

    /// <summary>Display name: the type name, or "None" for the None state.</summary>
    public string Name => Type?.Name ?? "None";

    /// <summary>The tag value for this member.</summary>
    public int TagValue { get; init; }

    /// <summary>Source location where this member is defined.</summary>
    public SourceLocation? Location { get; init; }

    /// <summary>
    /// Creates a variant member for a real type.
    /// </summary>
    public VariantMemberInfo(TypeInfo type)
    {
        Type = type;
    }

    /// <summary>
    /// Creates the None state member (zero-sized, no payload).
    /// </summary>
    private VariantMemberInfo()
    {
        Type = null;
    }

    /// <summary>
    /// Creates a None state member with the specified location and tag.
    /// </summary>
    public static VariantMemberInfo CreateNone(int tagValue, SourceLocation? location = null)
    {
        return new VariantMemberInfo { TagValue = tagValue, Location = location };
    }

    /// <summary>
    /// Creates a copy with substituted type for generic resolution.
    /// </summary>
    public VariantMemberInfo WithSubstitutedType(TypeInfo newType)
    {
        return new VariantMemberInfo(type: newType) { TagValue = TagValue, Location = Location };
    }
}
