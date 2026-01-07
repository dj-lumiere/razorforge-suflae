using Compilers.Shared.AST;

namespace Compilers.Analysis.Results;

/// <summary>
/// Represents a semantic error during analysis.
/// </summary>
/// <param name="Message">The error message.</param>
/// <param name="Location">The source location of the error.</param>
public sealed record SemanticError(string Message, SourceLocation Location);
