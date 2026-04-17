namespace Compiler.Diagnostics;

/// <summary>
/// Exception thrown when a fatal semantic analysis error occurs that cannot be
/// recovered from. Used for internal analyzer failures rather than user-facing
/// type or scope errors (which are reported via <see cref="SemanticDiagnosticCode"/>).
/// </summary>
public class SemanticException
{
}
