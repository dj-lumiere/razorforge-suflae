using Compiler.Resolution;
using Compiler.Desugaring;
using SyntaxTree;

namespace SemanticVerification.Results;

/// <summary>
/// Result of semantic analysis.
/// </summary>
/// <param name="Registry">The populated type registry.</param>
/// <param name="Errors">List of semantic errors.</param>
/// <param name="Warnings">List of semantic warnings.</param>
/// <param name="ParsedLiterals">Parsed literal values for code generation (f128, d32, d64, d128, Integer, Decimal).</param>
/// <param name="SynthesizedBodies">AST bodies for compiler-generated routines (derived operators + variant bodies),
/// keyed by RoutineInfo.RegistryKey. Includes both $ne/$lt/etc. operators and pre-transformed
/// try_/check_/lookup_ variant bodies produced by <see cref="Desugaring.Passes.ErrorHandlingVariantPass"/>.</param>
/// <param name="PreMonomorphizedBodies">Pre-rewritten generic method bodies produced by
/// <see cref="Desugaring.Passes.GenericMonomorphizationPass"/>, keyed by the concrete
/// RoutineInfo.RegistryKey.  Codegen uses these to skip AST search and re-rewriting
/// for all generic instantiations visible during semantic analysis.</param>
public sealed record AnalysisResult(
    TypeRegistry Registry,
    IReadOnlyList<SemanticError> Errors,
    IReadOnlyList<SemanticWarning> Warnings,
    IReadOnlyDictionary<SourceLocation, ParsedLiteral> ParsedLiterals,
    IReadOnlyDictionary<string, Statement> SynthesizedBodies,
    IReadOnlyDictionary<string, MonomorphizedBody> PreMonomorphizedBodies)
{
    /// <summary>Whether analysis completed without errors.</summary>
    public bool Success => Errors.Count == 0;
}
