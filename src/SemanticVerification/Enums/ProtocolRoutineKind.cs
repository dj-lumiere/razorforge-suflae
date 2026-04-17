namespace SemanticVerification.Enums;

/// <summary>
/// Describes the generation status of a protocol routine.
/// </summary>
public enum ProtocolRoutineKind
{
    /// <summary>No generation — user must implement.</summary>
    None,

    /// <summary>Builder-generated, user CAN override.</summary>
    Generated,

    /// <summary>Builder-generated and NOT overridable.</summary>
    Innate
}
