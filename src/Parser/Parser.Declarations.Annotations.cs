namespace Compiler.Parser;

using Lexer;
using Diagnostics;

/// <summary>
/// Partial class containing annotation parsing helpers for declarations.
/// </summary>
public partial class Parser
{
    private List<string> ParseAnnotations()
    {
        var annotations = new List<string>();

        // Handle @annotation and @[...] compound annotations
        while (Check(type: TokenType.At))
        {
            string annotName;

            if (Match(type: TokenType.At))
            {
                // Check for compound annotation syntax: @[attr1, attr2, ...]
                if (Match(type: TokenType.LeftBracket))
                {
                    // Parse comma-separated list of annotation names
                    do
                    {
                        string compoundAnnot = ConsumeIdentifier(
                            errorMessage: "Expected annotation name in compound annotation");

                        // Check for optional arguments on each annotation
                        if (Match(type: TokenType.LeftParen))
                        {
                            compoundAnnot += "(" + ParseAnnotationArgumentList() + ")";
                        }

                        annotations.Add(item: compoundAnnot);
                    } while (Match(type: TokenType.Comma));

                    Consume(type: TokenType.RightBracket,
                        errorMessage: "Expected ']' after compound annotations");
                }
                else
                {
                    // Regular annotation: @identifier
                    annotName =
                        ConsumeIdentifier(errorMessage: "Expected annotation name after '@'");

                    // Check for annotation arguments: @something("size_of") or @config(name: "value", count: 5)
                    if (Match(type: TokenType.LeftParen))
                    {
                        annotName += "(" + ParseAnnotationArgumentList() + ")";
                    }

                    annotations.Add(item: annotName);
                }
            }
            else
            {
                break; // No more annotations
            }

            // Skip newlines between annotations (allows multiple @attr on separate lines)
            while (Match(type: TokenType.Newline))
            {
                // Skip newlines
            }
        }

        return annotations;
    }

    /// <summary>
    /// Parses the argument list for an annotation (the content inside parentheses).
    /// </summary>
    /// <returns>String representation of the argument list.</returns>
    private string ParseAnnotationArgumentList()
    {
        var arguments = new List<string>();

        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                // Check for named argument: name: value or name = value
                TokenType nextToken = PeekToken(offset: 1)
                   .Type;
                if (Check(type: TokenType.Identifier) &&
                    (nextToken == TokenType.Colon || nextToken == TokenType.Assign))
                {
                    string argName = ConsumeIdentifier(errorMessage: "Expected argument name");
                    // Accept both ':' and '=' as separators
                    if (!Match(TokenType.Colon, TokenType.Assign))
                    {
                        throw ThrowParseError(code: GrammarDiagnosticCode.UnexpectedToken,
                            message: "Expected ':' or '=' after argument name");
                    }

                    string argValue = ParseAnnotationValue();
                    arguments.Add(item: $"{argName}={argValue}");
                }
                else
                {
                    // Positional argument (string literal, number, identifier)
                    arguments.Add(item: ParseAnnotationValue());
                }
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightParen,
            errorMessage: "Expected ')' after annotation arguments");

        return string.Join(separator: ", ", values: arguments);
    }

    /// <summary>
    /// Parses a single annotation argument value (string, number, bool, or identifier).
    /// </summary>
    /// <returns>String representation of the annotation value.</returns>
    private string ParseAnnotationValue()
    {
        // Annotation values are limited to build-time constants:
        // string, number, bool, or identifier (for enums/presets)

        // String literal
        if (Check(TokenType.TextLiteral, TokenType.BytesLiteral))
        {
            return Advance()
               .Text;
        }

        // Boolean literals
        if (Match(type: TokenType.True))
        {
            return "true";
        }

        if (Match(type: TokenType.False))
        {
            return "false";
        }

        // Numeric literals
        if (Check(TokenType.Integer,
                TokenType.S8Literal,
                TokenType.S16Literal,
                TokenType.S32Literal,
                TokenType.S64Literal,
                TokenType.S128Literal,
                TokenType.U8Literal,
                TokenType.U16Literal,
                TokenType.U32Literal,
                TokenType.U64Literal,
                TokenType.U128Literal,
                TokenType.AddressLiteral))
        {
            return Advance()
               .Text;
        }

        // Identifier (for choice values or constant references)
        if (Check(type: TokenType.Identifier))
        {
            return Advance()
               .Text;
        }

        throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedAnnotationValue,
            message: $"Expected annotation value, got {CurrentToken.Type}");
    }

}
