namespace SemanticAnalysis.Inference;

/// <summary>
/// Result of analyzing throw/absent keywords in a failable function body.
/// </summary>
internal sealed class ErrorHandlingAnalysis
{
    /// <summary>Whether the body contains any throw statements.</summary>
    public bool HasThrow { get; set; }

    /// <summary>Whether the body contains any absent statements.</summary>
    public bool HasAbsent { get; set; }
}
