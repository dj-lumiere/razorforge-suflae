namespace RazorForge.Diagnostics;

/// <summary>
/// Exception thrown for Suflae grammar (lexer/parser) errors.
/// Contains diagnostic code, message, and source location.
/// </summary>
public class SuflaeGrammarException : Exception
{
    /// <summary>
    /// The diagnostic code for this error.
    /// </summary>
    public SuflaeDiagnosticCode Code { get; }

    /// <summary>
    /// The source file where the error occurred.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// The 1-based line number where the error occurred.
    /// </summary>
    public int Line { get; }

    /// <summary>
    /// The 1-based column number where the error occurred.
    /// </summary>
    public int Column { get; }

    public SuflaeGrammarException(
        SuflaeDiagnosticCode code,
        string message,
        string fileName,
        int line,
        int column)
        : base(FormatMessage(code, message, fileName, line, column))
    {
        Code = code;
        FileName = fileName;
        Line = line;
        Column = column;
    }

    /// <summary>
    /// Formats the error message in the standard format:
    /// error[SF-G001]: filename.sf:10:5: message
    /// </summary>
    private static string FormatMessage(
        SuflaeDiagnosticCode code,
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