using Compilers.Shared.AST;

namespace Compilers.Analysis.Results;

/// <summary>
/// Represents a semantic warning during analysis.
/// </summary>
/// <param name="Message">The warning message.</param>
/// <param name="Location">The source location of the warning.</param>
public sealed record SemanticWarning(string Message, SourceLocation Location);
