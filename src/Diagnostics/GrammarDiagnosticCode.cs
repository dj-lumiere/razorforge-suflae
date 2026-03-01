using SemanticAnalysis.Enums;

namespace Compiler.Diagnostics;

/// <summary>
/// Unified grammar diagnostic codes for both RazorForge (RF-G) and Suflae (SF-G).
/// Covers lexer, parser, and indentation errors.
/// </summary>
public enum GrammarDiagnosticCode
{
    // ═══════════════════════════════════════════════════════════════════════════
    // LEXER ERRORS (001 - 049)
    // ═══════════════════════════════════════════════════════════════════════════

    UnterminatedString = 1,
    UnterminatedComment = 2,
    InvalidCharacter = 3,
    InvalidNumericLiteral = 4,
    InvalidEscapeSequence = 5,
    InvalidFloatLiteral = 6,
    InvalidMemoryLiteral = 7,
    InvalidDurationLiteral = 8,

    // ═══════════════════════════════════════════════════════════════════════════
    // INDENTATION ERRORS (050 - 079)
    // ═══════════════════════════════════════════════════════════════════════════

    ExpectedIndent = 50,
    ExpectedDedent = 51,
    UnexpectedDedent = 52,
    InconsistentIndentation = 53,
    MixedTabsAndSpaces = 54,
    ExpectedIndentedBlock = 55,

    // ═══════════════════════════════════════════════════════════════════════════
    // EXPECTED TOKEN ERRORS (100 - 149)
    // ═══════════════════════════════════════════════════════════════════════════

    ExpectedClosingBrace = 100,
    ExpectedClosingParen = 101,
    ExpectedClosingBracket = 102,
    ExpectedColon = 104,
    ExpectedArrow = 105,
    ExpectedFatArrow = 106,
    ExpectedEquals = 107,
    ExpectedComma = 108,
    ExpectedDot = 109,
    ExpectedIdentifier = 110,
    ExpectedExpression = 112,
    ExpectedType = 113,
    ExpectedPattern = 114,
    ExpectedDeclaration = 115,
    ExpectedAttributeValue = 116,
    ExpectedTypeArgument = 117,
    ExpectedColonOrFatArrow = 118,
    ExpectedLeftParen = 119,
    ExpectedRightParen = 120,
    ExpectedAs = 121,
    ExpectedWaitforAfterDependencies = 122,

    // ═══════════════════════════════════════════════════════════════════════════
    // UNEXPECTED TOKEN ERRORS (150 - 199)
    // ═══════════════════════════════════════════════════════════════════════════

    UnexpectedToken = 150,
    UnexpectedEof = 151,
    UnexpectedNewline = 152,

    // ═══════════════════════════════════════════════════════════════════════════
    // STRUCTURE ERRORS (200 - 249)
    // ═══════════════════════════════════════════════════════════════════════════

    NestedRoutineNotAllowed = 200,
    VisibilityWithoutDeclaration = 201,
    AttributesWithoutDeclaration = 202,
    InvalidDeclarationInBody = 203,
    ThrowRequiresExpression = 204,
    ExpectedDedentAfterBody = 205,
    DiscardRequiresCall = 206,
    TupleDependencyCountMismatch = 207,
    MissingReturn = 208,
    RFOnlyConstruct = 210,

    // ═══════════════════════════════════════════════════════════════════════════
    // PATTERN ERRORS (250 - 299)
    // ═══════════════════════════════════════════════════════════════════════════

    InvalidPattern = 250,
    ExpectedPatternBinding = 251,
    ExpectedDotInQualifiedPattern = 252,

    // ═══════════════════════════════════════════════════════════════════════════
    // GENERIC CONSTRAINT ERRORS (300 - 349)
    // ═══════════════════════════════════════════════════════════════════════════

    UndeclaredTypeParameter = 300,
    InvalidConstraintKind = 301,
    ExpectedConstraintType = 302,
}

public static class GrammarDiagnosticCodeExtensions
{
    /// <summary>
    /// Formats code as RF-Gnnn or SF-Gnnn depending on language.
    /// </summary>
    public static string ToCodeString(this GrammarDiagnosticCode code, Language language)
    {
        string prefix = language == Language.RazorForge ? "RF" : "SF";
        return $"{prefix}-G{(int)code:D3}";
    }
}
