using System.Runtime.CompilerServices;
using Xunit;

namespace RazorForge.Tests;

using SemanticAnalysis;
using SemanticAnalysis.Enums;
using SemanticAnalysis.Results;
using SemanticAnalysis.Symbols;
using Compiler.Lexer;
using SyntaxTree;
using TypeInfo = SemanticAnalysis.Types.TypeInfo;

/// <summary>
/// Helper methods for parsing and analyzing test code.
/// </summary>
public static class TestHelpers
{
    #region RazorForge Helpers

    /// <summary>
    /// Tokenizes RazorForge source code.
    /// </summary>
    public static List<Token> Tokenize(string source, [CallerMemberName] string? fileName = null)
    {
        var tokenizer = new Tokenizer(source: source, fileName: fileName ?? "test", language: Language.RazorForge);
        return tokenizer.Tokenize();
    }

    /// <summary>
    /// Parses RazorForge source code into an AST.
    /// </summary>
    public static Program Parse(string source, [CallerMemberName] string? fileName = null)
    {
        List<Token> tokens = Tokenize(source: source, fileName: fileName);
        var parser = new Compiler.Parser.Parser(tokens: tokens, language: Language.RazorForge, fileName: fileName);
        return parser.Parse();
    }

    /// <summary>
    /// Parses RazorForge source and returns the parser for error checking.
    /// </summary>
    public static (Program Program, Compiler.Parser.Parser Parser) ParseWithErrors(string source, [CallerMemberName] string? fileName = null)
    {
        List<Token> tokens = Tokenize(source: source, fileName: fileName);
        var parser = new Compiler.Parser.Parser(tokens: tokens, language: Language.RazorForge, fileName: fileName);
        Program program = parser.Parse();
        return (program, parser);
    }

    /// <summary>
    /// Asserts that parsing produces errors.
    /// </summary>
    public static void AssertParseError(string source, [CallerMemberName] string? fileName = null)
    {
        (Program _, Compiler.Parser.Parser parser) = ParseWithErrors(source: source, fileName: fileName);
        Assert.True(condition: parser.HasErrors, userMessage: "Expected parse errors but none were found");
    }

    /// <summary>
    /// Parses and analyzes RazorForge source code.
    /// </summary>
    public static AnalysisResult Analyze(string source, [CallerMemberName] string? fileName = null)
    {
        Program program = Parse(source: source, fileName: fileName);
        var analyzer = new SemanticAnalyzer(language: Language.RazorForge);
        return analyzer.Analyze(program: program);
    }

    /// <summary>
    /// Asserts that parsing succeeds without errors.
    /// </summary>
    public static Program AssertParses(string source, [CallerMemberName] string? fileName = null)
    {
        (Program program, Compiler.Parser.Parser parser) = ParseWithErrors(source: source, fileName: fileName);

        if (parser.HasErrors)
        {
            IReadOnlyList<string> errors = parser.GetErrors();
            string errorMessages = string.Join(separator: "\n",
                values: errors.Select(selector: e => $"  - {e}"));
            Assert.Fail(
                message: $"Expected no parse errors but got {errors.Count}:\n{errorMessages}");
        }

        Assert.NotNull(@object: program);
        Assert.NotEmpty(collection: program.Declarations);
        return program;
    }

    /// <summary>
    /// Asserts that analysis succeeds without errors.
    /// </summary>
    public static AnalysisResult AssertAnalyzes(string source, [CallerMemberName] string? fileName = null)
    {
        AnalysisResult result = Analyze(source: source, fileName: fileName);
        if (result.Errors.Count > 0)
        {
            string errorMessages = string.Join(separator: "\n",
                values: result.Errors.Select(selector: e => $"  - {e.Message} at {e.Location}"));
            Assert.Fail(
                message: $"Expected no errors but got {result.Errors.Count}:\n{errorMessages}");
        }

        return result;
    }

    /// <summary>
    /// Asserts that analysis produces specific errors.
    /// </summary>
    public static AnalysisResult AssertHasError(string source, string expectedErrorSubstring, [CallerMemberName] string? fileName = null)
    {
        AnalysisResult result = Analyze(source: source, fileName: fileName);
        Assert.True(condition: result.Errors.Count > 0,
            userMessage: "Expected at least one error");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: expectedErrorSubstring,
                comparisonType: StringComparison.OrdinalIgnoreCase));
        return result;
    }

    #endregion

    #region Suflae Helpers

    /// <summary>
    /// Tokenizes Suflae source code.
    /// </summary>
    public static List<Token> TokenizeSuflae(string source, [CallerMemberName] string? fileName = null)
    {
        var tokenizer = new Tokenizer(source: source, fileName: fileName ?? "test", language: Language.Suflae);
        return tokenizer.Tokenize();
    }

    /// <summary>
    /// Parses Suflae source code into an AST.
    /// </summary>
    public static Program ParseSuflae(string source, [CallerMemberName] string? fileName = null)
    {
        List<Token> tokens = TokenizeSuflae(source: source, fileName: fileName);
        var parser = new Compiler.Parser.Parser(tokens: tokens, language: Language.Suflae, fileName: fileName);
        return parser.Parse();
    }

    /// <summary>
    /// Parses Suflae source and returns the parser for error checking.
    /// </summary>
    public static (Program Program, Compiler.Parser.Parser Parser) ParseSuflaeWithErrors(string source, [CallerMemberName] string? fileName = null)
    {
        List<Token> tokens = TokenizeSuflae(source: source, fileName: fileName);
        var parser = new Compiler.Parser.Parser(tokens: tokens, language: Language.Suflae, fileName: fileName);
        Program program = parser.Parse();
        return (program, parser);
    }

    /// <summary>
    /// Parses and analyzes Suflae source code.
    /// </summary>
    public static AnalysisResult AnalyzeSuflae(string source, [CallerMemberName] string? fileName = null)
    {
        Program program = ParseSuflae(source: source, fileName: fileName);
        var analyzer = new SemanticAnalyzer(language: Language.Suflae);
        return analyzer.Analyze(program: program);
    }

    /// <summary>
    /// Asserts that Suflae parsing succeeds without errors.
    /// </summary>
    public static Program AssertParsesSuflae(string source, [CallerMemberName] string? fileName = null)
    {
        (Program program, Compiler.Parser.Parser parser) = ParseSuflaeWithErrors(source: source, fileName: fileName);

        if (parser.HasErrors)
        {
            IReadOnlyList<string> errors = parser.GetErrors();
            string errorMessages = string.Join(separator: "\n",
                values: errors.Select(selector: e => $"  - {e}"));
            Assert.Fail(
                message: $"Expected no parse errors but got {errors.Count}:\n{errorMessages}");
        }

        Assert.NotNull(@object: program);
        Assert.NotEmpty(collection: program.Declarations);
        return program;
    }

    /// <summary>
    /// Asserts that Suflae analysis succeeds without errors.
    /// </summary>
    public static AnalysisResult AssertAnalyzesSuflae(string source, [CallerMemberName] string? fileName = null)
    {
        AnalysisResult result = AnalyzeSuflae(source: source, fileName: fileName);
        if (result.Errors.Count > 0)
        {
            string errorMessages = string.Join(separator: "\n",
                values: result.Errors.Select(selector: e => $"  - {e.Message} at {e.Location}"));
            Assert.Fail(
                message: $"Expected no errors but got {result.Errors.Count}:\n{errorMessages}");
        }

        return result;
    }

    /// <summary>
    /// Asserts that Suflae analysis produces specific errors.
    /// </summary>
    public static AnalysisResult AssertHasErrorSuflae(string source, string expectedErrorSubstring, [CallerMemberName] string? fileName = null)
    {
        AnalysisResult result = AnalyzeSuflae(source: source, fileName: fileName);
        Assert.True(condition: result.Errors.Count > 0,
            userMessage: "Expected at least one error");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: expectedErrorSubstring,
                comparisonType: StringComparison.OrdinalIgnoreCase));
        return result;
    }

    #endregion

    #region Common Helpers

    /// <summary>
    /// Gets a declaration of a specific type from the program.
    /// </summary>
    public static T GetDeclaration<T>(Program program) where T : IAstNode
    {
        T? decl = program.Declarations
                         .OfType<T>()
                         .FirstOrDefault();
        Assert.NotNull(@object: decl);
        return decl;
    }

    /// <summary>
    /// Gets all declarations of a specific type from the program.
    /// </summary>
    public static List<T> GetDeclarations<T>(Program program) where T : IAstNode
    {
        return program.Declarations
                      .OfType<T>()
                      .ToList();
    }

    #endregion
}

/// <summary>
/// Extension methods for TypeRegistry to make tests more readable.
/// </summary>
public static class TypeRegistryExtensions
{
    /// <summary>
    /// Gets a type by name (wrapper for LookupType).
    /// </summary>
    public static TypeInfo? GetType(this TypeRegistry registry, string name)
    {
        return registry.LookupType(name: name);
    }

    /// <summary>
    /// Gets a routine by name (wrapper for LookupRoutine).
    /// </summary>
    public static RoutineInfo? GetRoutine(this TypeRegistry registry, string name)
    {
        return registry.LookupRoutine(fullName: name);
    }
}
