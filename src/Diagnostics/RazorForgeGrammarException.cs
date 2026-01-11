namespace RazorForge.Diagnostics;

/// <summary>
/// Exception thrown for RazorForge grammar (lexer/parser) errors.
/// Contains diagnostic code, message, and source location.
/// </summary>
public class RazorForgeGrammarException(
    RazorForgeDiagnosticCode code,
    string message,
    string fileName,
    int line,
    int column) : Exception(FormatMessage(code,
    message,
    fileName,
    line,
    column))
{
    /// <summary>
    /// The diagnostic code for this error.
    /// </summary>
    public RazorForgeDiagnosticCode Code { get; } = code;

    /// <summary>
    /// The source file where the error occurred.
    /// </summary>
    public string FileName { get; } = fileName;

    /// <summary>
    /// The 1-based line number where the error occurred.
    /// </summary>
    public int Line { get; } = line;

    /// <summary>
    /// The 1-based column number where the error occurred.
    /// </summary>
    public int Column { get; } = column;

    /// <summary>
    /// Formats the error message in the standard format:
    /// error[RF-G001]: filename.rf:10:5: message
    /// </summary>
    private static string FormatMessage(
        RazorForgeDiagnosticCode code,
        string message,
        string fileName,
        int line,
        int column)
    {
        var location = fileName;
        if (line > 0)
        {
            location += $":{line}";
            if (column > 0)
            {
                location += $":{column}";
            }
        }

        return $"error[{code.ToCodeString()}]: {location}: {message}";
    }
}
