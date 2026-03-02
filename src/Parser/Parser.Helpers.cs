using Compiler.Diagnostics;
using SyntaxTree;
using Compiler.Lexer;

namespace Compiler.Parser;

/// <summary>
/// Partial class containing helper methods: statement terminators,
/// identifier consumption, indentation handling, argument parsing, and collection literals.
/// </summary>
public partial class Parser
{
    #region Statement/Token Helpers

    /// <summary>
    /// Consumes statement terminators (newlines).
    /// Checks for unnecessary braces before accepting newline.
    /// </summary>
    private void ConsumeStatementTerminator()
    {
        // Check for unnecessary closing braces
        CheckUnnecessaryBrace();

        // Valid implicit terminators: DEDENT, else, elseif, EOF
        if (Check(type: TokenType.Dedent) ||
            Check(type: TokenType.Else) ||
            Check(type: TokenType.Elseif) ||
            IsAtEnd)
        {
            return;
        }

        // Optionally consume a newline if present
        Match(type: TokenType.Newline);
    }

    /// <summary>
    /// Consumes an identifier token and returns its text.
    /// </summary>
    /// <param name="errorMessage">Error message to show if token is not an identifier.</param>
    /// <returns>The identifier text.</returns>
    /// <exception cref="GrammarException">Thrown if current token is not a valid identifier.</exception>
    private string ConsumeIdentifier(string errorMessage, bool allowKeywords = false)
    {
        if (Match(TokenType.Identifier))
        {
            return PeekToken(offset: -1).Text;
        }

        // Allow 'me' or 'Me' (Self tokens) as a valid identifier for method parameters
        // 'me' is lowercase self reference, 'Me' is the type of self (for protocol method signatures)
        if (Match(TokenType.Me, TokenType.MyType))
        {
            return PeekToken(offset: -1).Text;
        }

        // When allowKeywords is true, accept contextual keywords as identifiers
        // (e.g., 'from', 'to', 'by', 'step' as parameter names)
        if (allowKeywords && CurrentToken.Type != TokenType.Eof && CurrentToken.Type != TokenType.Newline)
        {
            string text = CurrentToken.Text;
            Advance();
            return text;
        }

        Token current = CurrentToken;
        throw ThrowParseError(GrammarDiagnosticCode.ExpectedIdentifier,
            $"{errorMessage}. Expected Identifier, got {current.Type}.");
    }

    /// <summary>
    /// Consumes an identifier that can be used as a method/routine name.
    /// Supports '!' suffix for failable methods.
    /// </summary>
    /// <param name="errorMessage">Error message to show if token is not a valid method name.</param>
    /// <returns>The method name, possibly with '!' suffix for failable methods.</returns>
    private string ConsumeMethodName(string errorMessage)
    {
        if (!Check(type: TokenType.Identifier))
        {
            throw ThrowParseError(GrammarDiagnosticCode.ExpectedIdentifier, errorMessage);
        }

        string name = CurrentToken.Text;
        Advance();

        // Check for ! suffix (failable method marker)
        if (Match(type: TokenType.Bang))
        {
            name += "!";
        }

        return name;
    }

    /// <summary>
    /// Process an INDENT token by pushing a new indentation level.
    /// </summary>
    private void ProcessIndentToken()
    {
        if (!Match(type: TokenType.Indent))
        {
            throw ThrowParseError(GrammarDiagnosticCode.ExpectedIndentedBlock, "Expected INDENT token");
        }

        _currentIndentationLevel++;
        _indentationStack.Push(item: _currentIndentationLevel);
    }

    /// <summary>
    /// Process a single DEDENT token by popping one indentation level.
    /// Each block should only process its own DEDENT, not consume all consecutive ones.
    /// </summary>
    private void ProcessDedentTokens()
    {
        // Check for unnecessary closing braces before processing dedents
        CheckUnnecessaryBrace();

        // Only process ONE DEDENT - each block is responsible for its own dedent
        if (Check(type: TokenType.Dedent) && !IsAtEnd)
        {
            Advance(); // Consume the DEDENT token

            if (_indentationStack.Count > 1) // Keep base level
            {
                _indentationStack.Pop();
                _currentIndentationLevel = _indentationStack.Peek();
            }
            else
            {
                throw ThrowParseError(GrammarDiagnosticCode.UnexpectedDedent, "Unexpected dedent - no matching indent");
            }
        }
    }

    /// <summary>
    /// Check if we're at a valid indentation level for statements.
    /// </summary>
    private bool IsAtValidIndentationLevel()
    {
        return _indentationStack.Count > 0;
    }

    /// <summary>
    /// Get current indentation depth for debugging.
    /// </summary>
    private int GetIndentationDepth()
    {
        return _indentationStack.Count - 1; // Subtract 1 for base level
    }

    #endregion

    #region Argument Parsing

    /// <summary>
    /// Parses a single argument (named or positional).
    /// Named arguments have the form: name: expression
    /// </summary>
    private Expression ParseArgument()
    {
        SourceLocation location = GetLocation();

        // Check for named argument: identifier followed by colon
        if (Check(type: TokenType.Identifier) && PeekToken(offset: 1).Type == TokenType.Colon)
        {
            string argName = CurrentToken.Text;
            Advance(); // consume identifier
            Advance(); // consume colon

            // Parse the value expression
            Expression value = ParseExpression();

            return new NamedArgumentExpression(Name: argName, Value: value, Location: location);
        }

        // Regular positional argument
        return ParseExpression();
    }

    /// <summary>
    /// Parses a comma-separated list of arguments (named or positional).
    /// Called after '(' has been consumed.
    /// </summary>
    /// <returns>List of argument expressions.</returns>
    private List<Expression> ParseArgumentList()
    {
        var args = new List<Expression>();

        // Skip leading newlines
        while (Match(type: TokenType.Newline)) { }

        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                // Skip newlines before each argument (for multi-line formatting)
                while (Match(type: TokenType.Newline)) { }

                args.Add(item: ParseArgument());

                // Skip newlines after each argument (before comma or closing paren)
                while (Match(type: TokenType.Newline)) { }
            } while (Match(type: TokenType.Comma));
        }

        // Skip trailing newlines
        while (Match(type: TokenType.Newline)) { }

        return args;
    }

    /// <summary>
    /// Parses field initializers for record/entity literals: (field1: value1, field2: value2)
    /// Called after '(' has been consumed.
    /// </summary>
    /// <returns>List of field name and value pairs.</returns>
    private List<(string Name, Expression Value)> ParseAllArgsCreatorFields()
    {
        var fields = new List<(string Name, Expression Value)>();

        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                // Parse field name
                string fieldName = ConsumeIdentifier(errorMessage: "Expected field name in record/entity literal");
                Consume(type: TokenType.Colon, errorMessage: "Expected ':' after field name");

                // Parse field value
                Expression value = ParseExpression();
                fields.Add(item: (fieldName, value));
            } while (Match(type: TokenType.Comma));
        }

        return fields;
    }

    #endregion

    #region Collection Literals

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
    private Expression ParseSetOrDictLiteral(SourceLocation location)
    {
        // Empty braces -> empty set
        if (Match(type: TokenType.RightBrace))
        {
            return new SetLiteralExpression(Elements: [], ElementType: null, Location: location);
        }

        // Parse first element to determine if set or dict
        Expression firstExpr = ParseExpression();

        // If we see a colon, this is a dict literal
        if (Match(type: TokenType.Colon))
        {
            return ParseDictLiteralContinuation(firstKey: firstExpr, location: location);
        }

        // Otherwise it's a set literal
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

    #endregion
}