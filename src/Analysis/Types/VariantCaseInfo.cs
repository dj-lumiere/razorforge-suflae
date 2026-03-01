namespace SemanticAnalysis.Types;

using SyntaxTree;

/// <summary>
/// Information about a single case in a variant type.
/// </summary>
public sealed class VariantCaseInfo
{
    /// <summary>The name of the case (SCREAMING_SNAKE_CASE).</summary>
    public string Name { get; }

    /// <summary>
    /// The associated payload type, if any.
    /// Payload is a single type (not tuple-like).
    /// </summary>
    public TypeInfo? PayloadType { get; init; }

    /// <summary>Whether this case has an associated payload.</summary>
    public bool HasPayload => PayloadType != null;

    /// <summary>The tag value for this case.</summary>
    public int TagValue { get; init; }

    /// <summary>Source location where this case is defined.</summary>
    public SourceLocation? Location { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="VariantCaseInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the variant case.</param>
    public VariantCaseInfo(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Creates a copy with substituted payload type for generic resolution.
    /// </summary>
    /// <param name="newPayloadType">The new payload type to substitute.</param>
    /// <returns>A new <see cref="VariantCaseInfo"/> with the substituted payload type.</returns>
    public VariantCaseInfo WithSubstitutedType(TypeInfo? newPayloadType)
    {
        return new VariantCaseInfo(name: Name)
        {
            PayloadType = newPayloadType,
            TagValue = TagValue,
            Location = Location
        };
    }
}
