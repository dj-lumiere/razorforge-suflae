namespace SemanticAnalysis.Inference;

/// <summary>
/// Result of error handling variant generation.
/// </summary>
public sealed class ErrorHandlingResult
{
    /// <summary>Empty result (no variants generated).</summary>
    public static readonly ErrorHandlingResult Empty = new() { Variants = [] };

    /// <summary>The generated variants.</summary>
    public IReadOnlyList<GeneratedVariant> Variants { get; init; } = [];

    /// <summary>Error message if generation failed.</summary>
    public string? Error { get; init; }

    /// <summary>Whether the routine body contains throw statements.</summary>
    public bool HasThrow { get; init; }

    /// <summary>Whether the routine body contains absent statements.</summary>
    public bool HasAbsent { get; init; }

    /// <summary>Whether generation was successful.</summary>
    public bool Success => Error == null;
}
