namespace SemanticAnalysis.Types;

using Enums;

/// <summary>
/// Represents the 'Me' type in protocol method signatures.
/// This is a placeholder that represents the implementing type.
/// Similar to 'Self' in Rust or 'Self' in Swift.
/// </summary>
public sealed class ProtocolSelfTypeInfo : TypeInfo
{
    /// <summary>
    /// Singleton instance for the protocol self type.
    /// </summary>
    public static readonly ProtocolSelfTypeInfo Instance = new();

    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.ProtocolSelf;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolSelfTypeInfo"/> class.
    /// </summary>
    private ProtocolSelfTypeInfo() : base(name: "Me")
    {
    }

    /// <inheritdoc/>
    public override TypeInfo CreateInstance(IReadOnlyList<TypeInfo> typeArguments)
    {
        throw new InvalidOperationException(
            message:
            "Cannot resolve the protocol self type 'Me'. It must be replaced with the implementing type.");
    }
}
