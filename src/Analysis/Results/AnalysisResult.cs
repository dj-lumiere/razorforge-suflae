using Compilers.Shared.AST;

namespace Compilers.Analysis.Results;

/// <summary>
/// Result of semantic analysis.
/// </summary>
/// <param name="Registry">The populated type registry.</param>
/// <param name="Errors">List of semantic errors.</param>
/// <param name="Warnings">List of semantic warnings.</param>
/// <param name="ParsedLiterals">Parsed literal values for code generation (f128, d32, d64, d128, Integer, Decimal).</param>
public sealed record AnalysisResult(
    TypeRegistry Registry,
    IReadOnlyList<SemanticError> Errors,
    IReadOnlyList<SemanticWarning> Warnings,
    IReadOnlyDictionary<SourceLocation, ParsedLiteral> ParsedLiterals)
{
    /// <summary>Whether analysis completed without errors.</summary>
    public bool Success => Errors.Count == 0;
}
