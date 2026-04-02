using SyntaxTree;
using Compiler.Lexer;
using Compiler.Diagnostics;

namespace Compiler.Parser;

/// <summary>
/// Partial class containing expression parsing (precedence climbing chain).
/// </summary>
public partial class Parser
{
    /// <summary>
    /// Entry point for expression parsing. Delegates to the assignment level.
    /// </summary>
    /// <returns>The parsed <see cref="Expression"/> AST node.</returns>
    /// <remarks>Audited at 2025.12.21</remarks>
    private Expression ParseExpression()
    {
        return ParseAssignment();
    }

    /// <summary>
    /// Parses assignment expressions including compound assignments (lowest precedence).
    /// Syntax: <c>target = value</c>, <c>target += value</c>, etc.
    /// Right-associative to support chained assignments.
    /// Base compound assignments (+=, -=, etc.) emit CompoundAssignmentExpression for in-place dispatch.
    /// Overflow variants (+%=, +^=, etc.) and ??= expand to: <c>a +%= b</c> becomes <c>a = a +% b</c>.
    /// </summary>
    /// <returns>The parsed expression, possibly an assignment.</returns>
    private Expression ParsePrimary()
    {
        SourceLocation location = GetLocation();

        // Boolean and none literals
        if (Match(type: TokenType.True))
        {
            return new LiteralExpression(Value: true,
                LiteralType: TokenType.True,
                Location: location);
        }

        if (Match(type: TokenType.False))
        {
            return new LiteralExpression(Value: false,
                LiteralType: TokenType.False,
                Location: location);
        }

        if (Match(type: TokenType.None))
        {
            return new LiteralExpression(Value: null!,
                LiteralType: TokenType.None,
                Location: location);
        }

        // 'absent' as expression - evaluates to none (used in pattern matching arms)
        if (Match(type: TokenType.Absent))
        {
            return new LiteralExpression(Value: null!,
                LiteralType: TokenType.None,
                Location: location);
        }

        // Numeric literals (integers and floats)
        if (TryParseNumericLiteral(location: location, result: out Expression? numericExpr))
        {
            return numericExpr!;
        }

        // Inserted text (f-strings)
        if (TryParseInsertedText(location: location, result: out Expression? insertedTextExpr))
        {
            return insertedTextExpr!;
        }

        // Text literals
        if (TryParseTextLiteral(location: location, result: out Expression? textExpr))
        {
            return textExpr!;
        }

        // Character literals
        if (TryParseCharacterLiteral(location: location, result: out Expression? letterExpr))
        {
            return letterExpr!;
        }

        // ByteSize literals
        if (TryParseByteSizeLiteral(location: location, result: out Expression? memoryExpr))
        {
            return memoryExpr!;
        }

        // Duration/time literals
        if (TryParseDurationLiteral(location: location, result: out Expression? durationExpr))
        {
            return durationExpr!;
        }

        // Arrow lambda expression: x => expr or x given y => expr (single parameter, no parens)
        if (!_inWhenPatternContext && Check(type: TokenType.Identifier) && (PeekToken(offset: 1)
               .Type == TokenType.FatArrow || PeekToken(offset: 1)
               .Type == TokenType.Given))
        {
            return ParseArrowLambdaExpression(location: location);
        }

        // Identifiers and language-specific keywords
        // Note: 'me' is tokenized as TokenType.Me, so we need to handle it explicitly
        if (Match(TokenType.Identifier, TokenType.Me))
        {
            string text = PeekToken(offset: -1)
               .Text;
            if (text == "me")
            {
                return new IdentifierExpression(Name: "me", Location: location);
            }

            return new IdentifierExpression(Name: text, Location: location);
        }

        // Parenthesized expression, tuple literal, or arrow lambda with parenthesized params
        if (Match(type: TokenType.LeftParen))
        {
            if (IsArrowLambdaParameters())
            {
                return ParseParenthesizedArrowLambda(location: location);
            }

            // Parse first expression
            Expression firstExpr = ParseExpression();

            // Check if this is a tuple (has comma) or just parenthesized expression
            if (Match(type: TokenType.Comma))
            {
                // It's a tuple
                var elements = new List<Expression> { firstExpr };

                // Check for single-element tuple: (expr,)
                if (Check(type: TokenType.RightParen))
                {
                    Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after tuple");
                    return new TupleLiteralExpression(Elements: elements, Location: location);
                }

                // Multi-element tuple: (expr1, expr2, ...)
                do
                {
                    elements.Add(item: ParseExpression());
                } while (Match(type: TokenType.Comma) && !Check(type: TokenType.RightParen));

                Consume(type: TokenType.RightParen,
                    errorMessage: "Expected ')' after tuple elements");
                return new TupleLiteralExpression(Elements: elements, Location: location);
            }

            // Just a parenthesized expression
            Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after expression");
            return firstExpr;
        }

        // When expression: when x: pattern => expr, ...
        // Used in expression context: return when x: ..., var y = when x: ...
        if (Match(type: TokenType.When))
        {
            return ParseWhenExpression(location: location);
        }

        // Dependent waitfor with 'after' clause: after dep [as binding] waitfor expr [within timeout]
        if (Match(type: TokenType.After))
        {
            return ParseDependentWaitfor(location: location);
        }

        // Waitfor expression: waitfor expr or waitfor expr within timeout
        // Used for async/concurrency: var result = waitfor asyncOperation
        if (Match(type: TokenType.Waitfor))
        {
            Expression operand = ParseUnary(); // Parse the expression to wait for
            Expression? timeout = null;

            if (Match(type: TokenType.Within))
            {
                timeout = ParseUnary(); // Parse the timeout expression
            }

            return new WaitforExpression(Operand: operand, Timeout: timeout, Location: location);
        }

        // List literal: [expr, expr, ...]
        if (Match(type: TokenType.LeftBracket))
        {
            return ParseListLiteral(location: location);
        }

        // Set or Dict literal: {expr, expr, ...} or {key: value, ...}
        if (Match(type: TokenType.LeftBrace))
        {
            return ParseSetOrDictLiteral(location: location);
        }

        throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedExpression,
            message: $"Unexpected token: {CurrentToken.Type}");
    }

}
