using Compilers.Shared.AST;
using Compilers.Shared.Lexer;

namespace Compilers.RazorForge.Parser;

/// <summary>
/// Partial class containing lambda expression and collection literal parsing.
/// </summary>
public partial class RazorForgeParser
{
    /// <summary>
    /// Parse the 'given' clause for explicit lambda captures.
    /// Supports both forms:
    /// - given x          (single capture without parentheses)
    /// - given (x, y, z)  (multiple captures with parentheses)
    /// </summary>
    private List<string> ParseGivenClause()
    {
        var captures = new List<string>();

        // Check if parenthesized or single identifier
        if (Match(type: TokenType.LeftParen))
        {
            // Parenthesized form: given (x, y, z)
            if (!Check(type: TokenType.RightParen))
            {
                do
                {
                    string captureName = ConsumeIdentifier(errorMessage: "Expected capture variable name");
                    captures.Add(item: captureName);
                } while (Match(type: TokenType.Comma));
            }

            Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after capture list");
        }
        else
        {
            // Single identifier form: given x
            string captureName = ConsumeIdentifier(errorMessage: "Expected capture variable name after 'given'");
            captures.Add(item: captureName);
        }

        return captures;
    }

    /// <summary>
    /// Parse arrow lambda with single unparenthesized parameter: x => expr or x given (a, b) => expr
    /// </summary>
    private LambdaExpression ParseArrowLambdaExpression(SourceLocation location)
    {
        // Single parameter without parentheses: x => expr or x given (a, b) => expr
        string paramName = ConsumeIdentifier(errorMessage: "Expected parameter name");

        var parameters = new List<Parameter>
        {
            new(Name: paramName,
                Type: null,
                DefaultValue: null,
                Location: location)
        };

        // Check for 'given' clause for explicit captures
        List<string>? captures = null;
        if (Match(type: TokenType.Given))
        {
            captures = ParseGivenClause();
        }

        Consume(type: TokenType.FatArrow, errorMessage: "Expected '=>' in lambda expression");

        Expression body = ParseExpression();
        return new LambdaExpression(Parameters: parameters, Body: body, Captures: captures, Location: location);
    }

    /// <summary>
    /// Check if we're inside parenthesized lambda parameters.
    /// Called after consuming '(' - scans ahead to see if we have:
    /// - identifier [, identifier]* ) =>
    /// - identifier [, identifier]* ) given ... =>
    /// </summary>
    /// <remarks>
    /// LOOKAHEAD FUNCTION - Does not consume tokens permanently.
    ///
    /// This function distinguishes between:
    ///   (x, y) => x + y         - Lambda expression
    ///   (x + y)                 - Parenthesized expression
    ///   (x, y)                  - Tuple expression (future)
    ///
    /// Lambda parameter patterns we accept:
    ///   ()                      - No parameters
    ///   (x)                     - Single untyped parameter
    ///   (x: S32)                - Single typed parameter
    ///   (x, y)                  - Multiple untyped parameters
    ///   (x: S32, y: Text)       - Multiple typed parameters
    ///   (x, y) given (a, b)     - With explicit captures
    ///
    /// The key discriminator is the '=>' or 'given' after the ')'.
    /// </remarks>
    private bool IsArrowLambdaParameters()
    {
        int savedPosition = _position;

        try
        {
            // ═══════════════════════════════════════════════════════════════════════════
            // CASE 1: Empty params - () => or () given =>
            // ═══════════════════════════════════════════════════════════════════════════
            if (Check(type: TokenType.RightParen))
            {
                Advance(); // consume )
                bool result = Check(type: TokenType.FatArrow) || Check(type: TokenType.Given);
                _position = savedPosition;
                return result;
            }

            // ═══════════════════════════════════════════════════════════════════════════
            // CASE 2: Scan parameter list - identifier [: type]? [, ...]* ) [given] =>
            // ═══════════════════════════════════════════════════════════════════════════
            while (true)
            {
                // ─────────────────────────────────────────────────────────────────────
                // Each parameter must start with identifier
                // ─────────────────────────────────────────────────────────────────────
                if (!Check(type: TokenType.Identifier))
                {
                    _position = savedPosition;
                    return false;
                }

                Advance(); // consume identifier

                // ─────────────────────────────────────────────────────────────────────
                // Optional type annotation - skip over it
                // ─────────────────────────────────────────────────────────────────────
                // We need to handle nested generics like List<Dict<Text, S32>>
                if (Check(type: TokenType.Colon))
                {
                    Advance(); // consume :
                    // Skip the type (track < > depth for generics)
                    int depth = 0;
                    while (!IsAtEnd)
                    {
                        if (Check(type: TokenType.Less))
                        {
                            depth++;
                        }
                        else if (Check(type: TokenType.Greater))
                        {
                            depth--;
                        }
                        else if (depth == 0 && (Check(type: TokenType.Comma) || Check(type: TokenType.RightParen)))
                        {
                            break;
                        }

                        Advance();
                    }
                }

                // ─────────────────────────────────────────────────────────────────────
                // Check for comma (more params) or closing paren (end of list)
                // ─────────────────────────────────────────────────────────────────────
                if (Check(type: TokenType.Comma))
                {
                    Advance(); // consume comma, continue loop
                }
                else if (Check(type: TokenType.RightParen))
                {
                    Advance(); // consume )
                    // Accept either direct => or given ... =>
                    bool result = Check(type: TokenType.FatArrow) || Check(type: TokenType.Given);
                    _position = savedPosition;
                    return result;
                }
                else
                {
                    // Not a valid lambda parameter list (e.g., expression like (x + y))
                    _position = savedPosition;
                    return false;
                }
            }
        }
        catch
        {
            _position = savedPosition;
            return false;
        }
    }

    /// <summary>
    /// Parse arrow lambda with parenthesized parameters: (x) => expr or (x, y) given (a, b) => expr
    /// Called after '(' has been consumed and IsArrowLambdaParameters() returned true.
    /// </summary>
    private LambdaExpression ParseParenthesizedArrowLambda(SourceLocation location)
    {
        var parameters = new List<Parameter>();

        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                string paramName = ConsumeIdentifier(errorMessage: "Expected parameter name in lambda");
                TypeExpression? paramType = null;

                if (Match(type: TokenType.Colon))
                {
                    paramType = ParseType();
                }

                parameters.Add(item: new Parameter(Name: paramName,
                    Type: paramType,
                    DefaultValue: null,
                    Location: GetLocation()));
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after lambda parameters");

        // Check for 'given' clause for explicit captures
        List<string>? captures = null;
        if (Match(type: TokenType.Given))
        {
            captures = ParseGivenClause();
        }

        Consume(type: TokenType.FatArrow, errorMessage: "Expected '=>' after lambda parameters");

        Expression body = ParseExpression();
        return new LambdaExpression(Parameters: parameters, Body: body, Captures: captures, Location: location);
    }

    /// <summary>
    /// Parse list literal: [expr, expr, ...]
    /// The opening '[' has already been consumed.
    /// </summary>
    private ListLiteralExpression ParseListLiteral(SourceLocation location)
    {
        var elements = new List<Expression>();

        if (!Check(type: TokenType.RightBracket))
        {
            do
            {
                elements.Add(item: ParseExpression());
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after list elements");

        return new ListLiteralExpression(Elements: elements, ElementType: null, Location: location);
    }

    /// <summary>
    /// Parse set or dict literal: {expr, expr, ...} or {key: value, ...}
    /// The opening '{' has already been consumed.
    /// Disambiguation: If the first element contains ':', it's a dict; otherwise it's a set.
    /// Empty {} is treated as an empty set.
    /// </summary>
    /// <remarks>
    /// Collection literal forms:
    ///
    /// SET LITERALS:
    ///   {}              - Empty set
    ///   {1, 2, 3}       - Set with elements
    ///   {"a", "b"}      - Set of strings
    ///
    /// DICT LITERALS:
    ///   {"a": 1, "b": 2}  - Dict with key-value pairs
    ///   {key: value}      - Single entry dict
    ///
    /// DISAMBIGUATION STRATEGY:
    /// We parse the first expression, then check if a ':' follows.
    /// - If ':' follows -> dict literal (first expr was a key)
    /// - Otherwise -> set literal (first expr was an element)
    ///
    /// Note: Empty {} is always a set. For empty dict, use Dict&lt;K, V&gt;() constructor.
    /// </remarks>
    private Expression ParseSetOrDictLiteral(SourceLocation location)
    {
        // ═══════════════════════════════════════════════════════════════════════════
        // CASE 1: Empty braces -> empty set
        // ═══════════════════════════════════════════════════════════════════════════
        if (Match(type: TokenType.RightBrace))
        {
            return new SetLiteralExpression(Elements: [], ElementType: null, Location: location);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // CASE 2: Parse first element to determine set vs dict
        // ═══════════════════════════════════════════════════════════════════════════
        Expression firstExpr = ParseExpression();

        // ─────────────────────────────────────────────────────────────────────
        // Colon after first expression -> dict literal
        // ─────────────────────────────────────────────────────────────────────
        if (Match(type: TokenType.Colon))
        {
            return ParseDictLiteralContinuation(firstKey: firstExpr, location: location);
        }

        // ─────────────────────────────────────────────────────────────────────
        // No colon -> set literal
        // ─────────────────────────────────────────────────────────────────────
        return ParseSetLiteralContinuation(firstElement: firstExpr, location: location);
    }

    /// <summary>
    /// Continue parsing dict literal after first key and colon: {key: value, ...}
    /// </summary>
    private DictLiteralExpression ParseDictLiteralContinuation(Expression firstKey, SourceLocation location)
    {
        var pairs = new List<(Expression Key, Expression Value)>();

        // Parse value for first key
        Expression firstValue = ParseExpression();
        pairs.Add(item: (firstKey, firstValue));

        // Parse remaining key-value pairs
        while (Match(type: TokenType.Comma))
        {
            Expression key = ParseExpression();
            Consume(type: TokenType.Colon, errorMessage: "Expected ':' between dict key and value");
            Expression value = ParseExpression();
            pairs.Add(item: (key, value));
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after dict elements");

        return new DictLiteralExpression(Pairs: pairs,
            KeyType: null,
            ValueType: null,
            Location: location);
    }

    /// <summary>
    /// Continue parsing set literal after first element: {expr, expr, ...}
    /// </summary>
    private SetLiteralExpression ParseSetLiteralContinuation(Expression firstElement, SourceLocation location)
    {
        var elements = new List<Expression> { firstElement };

        // Parse remaining elements
        while (Match(type: TokenType.Comma))
        {
            elements.Add(item: ParseExpression());
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after set elements");

        return new SetLiteralExpression(Elements: elements, ElementType: null, Location: location);
    }
}
