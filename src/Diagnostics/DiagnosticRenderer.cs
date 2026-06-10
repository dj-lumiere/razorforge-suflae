using System;
using System.Collections.Concurrent;
using System.IO;
using SyntaxTree;
using Verification.Results;

namespace Compiler.Diagnostics;

/// <summary>
/// Renders compiler diagnostics with a source-line excerpt and column caret, in the style of
/// modern compilers:
/// <code>
/// error[RF-S413]: playground\demo.rf:42:15: 'neighbors' cannot be directly assigned...
///     42 |   adj[i] = neighbors
///        |            ^
/// </code>
/// Color uses <see cref="Console.ForegroundColor"/> (portable — no VT escape handling needed)
/// and is suppressed when output is redirected or the NO_COLOR convention is set.
/// Source files are read lazily and cached per render batch; unreadable files degrade
/// gracefully to the header line only.
/// </summary>
public static class DiagnosticRenderer
{
    private static readonly ConcurrentDictionary<string, string[]?> _sourceCache =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    private static readonly bool _useColor =
        !Console.IsOutputRedirected
        && Environment.GetEnvironmentVariable(variable: "NO_COLOR") == null
        && Environment.GetEnvironmentVariable(variable: "TERM") != "dumb";

    /// <summary>Renders a semantic error (header + excerpt) to standard output.</summary>
    public static void Print(SemanticError error, string indent = "  ") =>
        PrintDiagnostic(severity: "error", severityColor: ConsoleColor.Red,
            header: error.FormattedMessage, location: error.Location, indent: indent);

    /// <summary>Renders a semantic warning (header + excerpt) to standard output.</summary>
    public static void Print(SemanticWarning warning, string indent = "  ") =>
        PrintDiagnostic(severity: "warning", severityColor: ConsoleColor.Yellow,
            header: warning.FormattedMessage, location: warning.Location, indent: indent);

    private static void PrintDiagnostic(string severity, ConsoleColor severityColor,
        string header, SourceLocation location, string indent)
    {
        // Header line: the severity word is colored; the rest stays default so the
        // file:line:col fragment remains terminal-clickable and copy-paste friendly.
        if (_useColor && header.StartsWith(value: severity, comparisonType: StringComparison.Ordinal))
        {
            Console.Write(value: indent);
            ConsoleColor saved = Console.ForegroundColor;
            Console.ForegroundColor = severityColor;
            Console.Write(value: severity);
            Console.ForegroundColor = saved;
            Console.WriteLine(value: header[severity.Length..]);
        }
        else
        {
            Console.WriteLine(value: $"{indent}{header}");
        }

        PrintExcerpt(location: location, severityColor: severityColor, indent: indent);
    }

    /// <summary>
    /// Prints the offending source line with a caret under the diagnostic column.
    /// Silently prints nothing when the file is missing/unreadable or the location
    /// is out of range (e.g. synthesized nodes carry line 0).
    /// </summary>
    private static void PrintExcerpt(SourceLocation location, ConsoleColor severityColor,
        string indent)
    {
        if (string.IsNullOrEmpty(value: location.FileName) || location.Line <= 0)
        {
            return;
        }

        string[]? lines = _sourceCache.GetOrAdd(key: location.FileName, valueFactory: static path =>
        {
            try
            {
                return File.Exists(path: path) ? File.ReadAllLines(path: path) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        });

        if (lines == null || location.Line > lines.Length)
        {
            return;
        }

        string sourceLine = lines[location.Line - 1];
        // Tabs would desynchronize the caret column; render them as single spaces
        // (RF style is space-indented, so this is a degenerate case anyway).
        sourceLine = sourceLine.Replace(oldChar: '\t', newChar: ' ');

        string lineNumber = location.Line.ToString();
        string gutterPad = new(c: ' ', count: lineNumber.Length);
        int caretOffset = Math.Clamp(value: location.Column - 1, min: 0, max: sourceLine.Length);

        WriteGutter(text: $"{indent}{lineNumber} | ");
        Console.WriteLine(value: sourceLine);
        WriteGutter(text: $"{indent}{gutterPad} | ");
        if (_useColor)
        {
            ConsoleColor saved = Console.ForegroundColor;
            Console.ForegroundColor = severityColor;
            Console.WriteLine(value: $"{new string(c: ' ', count: caretOffset)}^");
            Console.ForegroundColor = saved;
        }
        else
        {
            Console.WriteLine(value: $"{new string(c: ' ', count: caretOffset)}^");
        }
    }

    private static void WriteGutter(string text)
    {
        if (_useColor)
        {
            ConsoleColor saved = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(value: text);
            Console.ForegroundColor = saved;
        }
        else
        {
            Console.Write(value: text);
        }
    }
}
