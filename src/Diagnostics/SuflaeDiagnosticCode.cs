namespace RazorForge.Diagnostics;

/// <summary>
/// Grammar diagnostic codes for Suflae (SF-G prefix).
/// Covers lexer and parser errors, including indentation-specific errors.
/// </summary>
public enum SuflaeDiagnosticCode
{
    // ═══════════════════════════════════════════════════════════════════════════
    // LEXER ERRORS (SF-G001 - SF-G099)
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
    // INDENTATION ERRORS (SF-G050 - SF-G079) - Suflae-specific
    // ═══════════════════════════════════════════════════════════════════════════

    ExpectedIndent = 50,
    ExpectedDedent = 51,
    UnexpectedDedent = 52,
    InconsistentIndentation = 53,
    MixedTabsAndSpaces = 54,
    ExpectedIndentedBlock = 55,

    // ═══════════════════════════════════════════════════════════════════════════
    // EXPECTED TOKEN ERRORS (SF-G100 - SF-G149)
    // ═══════════════════════════════════════════════════════════════════════════

    ExpectedColon = 100,
    ExpectedArrow = 101,
    ExpectedFatArrow = 102,
    ExpectedEquals = 103,
    ExpectedComma = 104,
    ExpectedDot = 105,
    ExpectedIdentifier = 106,
    ExpectedTypeIdentifier = 107,
    ExpectedExpression = 108,
    ExpectedType = 109,
    ExpectedPattern = 110,
    ExpectedDeclaration = 111,
    ExpectedAttributeValue = 112,
    ExpectedTypeArgument = 113,
    ExpectedColonOrFatArrow = 114,
    ExpectedClosingParen = 115,
    ExpectedClosingBracket = 116,
    ExpectedClosingAngle = 117,
    ExpectedLeftParen = 118,
    ExpectedRightParen = 119,
    ExpectedAs = 120,
    ExpectedWaitforAfterDependencies = 121,

    // ═══════════════════════════════════════════════════════════════════════════
    // UNEXPECTED TOKEN ERRORS (SF-G150 - SF-G199)
    // ═══════════════════════════════════════════════════════════════════════════

    UnexpectedToken = 150,
    UnexpectedEof = 151,
    UnexpectedNewline = 152,

    // ═══════════════════════════════════════════════════════════════════════════
    // STRUCTURE ERRORS (SF-G200 - SF-G249)
    // ═══════════════════════════════════════════════════════════════════════════

    NestedRoutineNotAllowed = 200,
    VisibilityWithoutDeclaration = 201,
    AttributesWithoutDeclaration = 202,
    InvalidDeclarationInBody = 203,
    ThrowRequiresExpression = 204,
    ExpectedDedentAfterBody = 205,
    DiscardRequiresCall = 206,
    TupleDependencyCountMismatch = 207,

    // ═══════════════════════════════════════════════════════════════════════════
    // PATTERN ERRORS (SF-G250 - SF-G299)
    // ═══════════════════════════════════════════════════════════════════════════

    InvalidPattern = 250,
    ExpectedPatternBinding = 251,
    ExpectedDotInQualifiedPattern = 252,

    // ═══════════════════════════════════════════════════════════════════════════
    // GENERIC CONSTRAINT ERRORS (SF-G300 - SF-G349)
    // ═══════════════════════════════════════════════════════════════════════════

    UndeclaredTypeParameter = 300,
    InvalidConstraintKind = 301,
    ExpectedConstraintType = 302,
}

public static class SuflaeDiagnosticCodeExtensions
{
    /// <summary>
    /// Formats code as SF-Gnnn (e.g., SF-G001, SF-G050)
    /// </summary>
    public static string ToCodeString(this SuflaeDiagnosticCode code)
    {
        return $"SF-G{(int)code:D3}";
    }
}