namespace RazorForge.Diagnostics;

/// <summary>
/// Grammar diagnostic codes for RazorForge (RF-G prefix).
/// Covers lexer and parser errors.
/// </summary>
public enum RazorForgeDiagnosticCode
{
    // ═══════════════════════════════════════════════════════════════════════════
    // LEXER ERRORS (RF-G001 - RF-G099)
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
    // EXPECTED TOKEN ERRORS (RF-G100 - RF-G149)
    // ═══════════════════════════════════════════════════════════════════════════

    ExpectedClosingBrace = 100,
    ExpectedClosingParen = 101,
    ExpectedClosingBracket = 102,
    ExpectedClosingAngle = 103,
    ExpectedColon = 104,
    ExpectedArrow = 105,
    ExpectedFatArrow = 106,
    ExpectedEquals = 107,
    ExpectedComma = 108,
    ExpectedDot = 109,
    ExpectedIdentifier = 110,
    ExpectedTypeIdentifier = 111,
    ExpectedExpression = 112,
    ExpectedType = 113,
    ExpectedPattern = 114,
    ExpectedDeclaration = 115,
    ExpectedAttributeValue = 116,
    ExpectedTypeArgument = 117,
    ExpectedColonOrFatArrow = 118,

    // ═══════════════════════════════════════════════════════════════════════════
    // UNEXPECTED TOKEN ERRORS (RF-G150 - RF-G199)
    // ═══════════════════════════════════════════════════════════════════════════

    UnexpectedToken = 150,
    UnexpectedEof = 151,
    UnexpectedNewline = 152,

    // ═══════════════════════════════════════════════════════════════════════════
    // STRUCTURE ERRORS (RF-G200 - RF-G249)
    // ═══════════════════════════════════════════════════════════════════════════

    NestedRoutineNotAllowed = 200,
    VisibilityWithoutDeclaration = 201,
    AttributesWithoutDeclaration = 202,
    InvalidDeclarationInBody = 203,
    ThrowRequiresExpression = 204,
    DiscardRequiresCall = 205,

    // ═══════════════════════════════════════════════════════════════════════════
    // PATTERN ERRORS (RF-G250 - RF-G299)
    // ═══════════════════════════════════════════════════════════════════════════

    InvalidPattern = 250,
    ExpectedPatternBinding = 251,
    ExpectedDotInQualifiedPattern = 252,

    // ═══════════════════════════════════════════════════════════════════════════
    // GENERIC CONSTRAINT ERRORS (RF-G300 - RF-G349)
    // ═══════════════════════════════════════════════════════════════════════════

    UndeclaredTypeParameter = 300,
    InvalidConstraintKind = 301,
    ExpectedConstraintType = 302,
}

public static class RazorForgeDiagnosticCodeExtensions
{
    /// <summary>
    /// Formats code as RF-Gnnn (e.g., RF-G001, RF-G100)
    /// </summary>
    public static string ToCodeString(this RazorForgeDiagnosticCode code)
    {
        return $"RF-G{(int)code:D3}";
    }
}