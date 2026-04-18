namespace Compiler.Parser;

using Lexer;
using SyntaxTree;
using Diagnostics;

public partial class Parser
{
    private WhenExpression ParseWhenExpression(SourceLocation location)
    {
        // Subject-less when: when\n  condition => body
        // With subject: when expr\n  pattern => body
        Expression? expression = null;
        if (!Check(type: TokenType.Newline))
        {
            expression = ParseExpression();
        }

        Consume(type: TokenType.Newline, errorMessage: "Expected newline after when expression");
        Consume(type: TokenType.Indent, errorMessage: "Expected indented block after when");

        var clauses = new List<WhenClause>();

        while (!Check(type: TokenType.Dedent) && !IsAtEnd)
        {
            // Skip newlines and doc comments
            if (Match(TokenType.Newline, TokenType.DocComment))
            {
                continue;
            }

            Pattern pattern;
            SourceLocation clauseLocation = GetLocation();

            // Handle 'else' keyword for default case: else => body or else varName => body
            if (Match(type: TokenType.Else))
            {
                // Check for variable binding: else varName =>
                if (Check(type: TokenType.Identifier) && PeekToken(offset: 1)
                       .Type == TokenType.FatArrow)
                {
                    string varName =
                        ConsumeIdentifier(errorMessage: "Expected variable name after 'else'");
                    pattern = new IdentifierPattern(Name: varName, Location: clauseLocation);
                }
                else
                {
                    // Plain else without variable binding - treat as wildcard
                    pattern = new WildcardPattern(Location: clauseLocation);
                }
            }
            else if (expression == null)
            {
                // Subject-less when: arms are condition expressions (e.g., me > 0_s8 => 1_s8)
                _inWhenPatternContext = true;
                Expression condition = ParseExpression();
                _inWhenPatternContext = false;
                pattern = new ExpressionPattern(Expression: condition, Location: clauseLocation);
            }
            // Handle 'is' keyword pattern: is None, is SomeType, is SomeType varName
            else if (Match(type: TokenType.Is))
            {
                _inWhenPatternContext = true;
                pattern = ParseTypePattern();
                _inWhenPatternContext = false;
            }
            // Comparison patterns (==, !=, <, >, <=, >=, ===, !==)
            else if (IsComparisonOperator(tokenType: CurrentToken.Type))
            {
                pattern = ParseComparisonPattern();
            }
            else
            {
                // Set context flag to prevent single-param lambdas from being parsed
                // inside when patterns (e.g., a < b => action should not treat b => action as lambda)
                _inWhenPatternContext = true;
                pattern = ParsePattern();
                _inWhenPatternContext = false;
            }

            // Mandatory => on all when expression arms:
            // 1. is PATTERN => expr           (single expression)
            // 2. is PATTERN => \n INDENT ...  (block after =>)
            // Set flag to prevent 'is' expression parsing in when clause bodies
            _inWhenClauseBody = true;
            Statement body;
            Consume(type: TokenType.FatArrow, errorMessage: "Expected '=>' after when pattern");
            if (Check(type: TokenType.Newline) && PeekToken(offset: 1)
                   .Type == TokenType.Indent)
            {
                // Block form: PATTERN => \n INDENT body DEDENT
                Advance(); // consume newline
                body = ParseIndentedBlock();
            }
            else if (Match(type: TokenType.Pass))
            {
                body = new PassStatement(Location: GetLocation());
            }
            else
            {
                // Single-line body: PATTERN => statement (expression, break, return, etc.)
                body = ParseStatement();
            }

            _inWhenClauseBody = false;

            clauses.Add(
                item: new WhenClause(Pattern: pattern, Body: body, Location: GetLocation()));

            // Optional newline between clauses
            Match(type: TokenType.Newline);
        }

        Consume(type: TokenType.Dedent, errorMessage: "Expected dedent after when clauses");

        return new WhenExpression(Expression: expression, Clauses: clauses, Location: location);
    }

    /// <summary>
    /// Parses a dependent waitfor expression with task dependencies.
    /// Syntax: after dep [as binding] [, ...] waitfor expr [within timeout]
    /// </summary>
    /// <param name="location">Source location of the after keyword</param>
    /// <returns>The parsed DependentWaitforExpression</returns>
    private Expression ParseDependentWaitfor(SourceLocation location)
    {
        List<TaskDependency> dependencies = ParseTaskDependencies();

        // Expect 'waitfor' keyword
        Consume(type: TokenType.Waitfor,
            errorMessage: "Expected 'waitfor' after task dependencies");

        Expression operand = ParseUnary();
        Expression? timeout = null;

        if (Match(type: TokenType.Within))
        {
            timeout = ParseUnary();
        }

        return new DependentWaitforExpression(Dependencies: dependencies,
            Operand: operand,
            Timeout: timeout,
            Location: location);
    }

    /// <summary>
    /// Parses task dependencies for the 'after' clause.
    /// </summary>
    /// <returns>List of parsed task dependencies</returns>
    private List<TaskDependency> ParseTaskDependencies()
    {
        List<TaskDependency> dependencies = [];

        // Check for tuple-style: after (a, b) as (va, vb)
        if (Check(type: TokenType.LeftParen))
        {
            // Could be tuple-style or just parenthesized expression
            // Try tuple-style first by looking ahead for pattern: ( expr, expr ) as ( ident, ident )
            if (IsTupleDependencyPattern())
            {
                return ParseTupleDependencies();
            }
        }

        // Parse comma-separated dependencies: after a as va, b as vb
        do
        {
            SourceLocation depLocation = GetLocation();
            Expression depExpr = ParseUnary();
            string? binding = null;

            if (Match(type: TokenType.As))
            {
                Token bindingToken = Consume(type: TokenType.Identifier,
                    errorMessage: "Expected identifier after 'as' in task dependency");
                binding = bindingToken.Text;
            }

            dependencies.Add(item: new TaskDependency(DependencyExpr: depExpr,
                BindingName: binding,
                Location: depLocation));
        } while (Match(type: TokenType.Comma));

        return dependencies;
    }

    /// <summary>
    /// Checks if the current position matches a tuple dependency pattern.
    /// Pattern: ( expr, ... ) as ( ident, ... )
    /// </summary>
    private bool IsTupleDependencyPattern()
    {
        // Save position for backtracking
        int savedPosition = Position;

        try
        {
            if (!Match(type: TokenType.LeftParen))
            {
                return false;
            }

            // Skip to closing paren, counting nested parens
            int parenDepth = 1;
            while (parenDepth > 0 && !IsAtEnd)
            {
                if (Match(type: TokenType.LeftParen))
                {
                    parenDepth++;
                }
                else if (Match(type: TokenType.RightParen))
                {
                    parenDepth--;
                }
                else
                {
                    Advance();
                }
            }

            // Check for 'as' followed by '('
            return Match(type: TokenType.As) && Check(type: TokenType.LeftParen);
        }
        finally
        {
            Position = savedPosition;
        }
    }

    /// <summary>
    /// Parses tuple-style dependencies: (a, b) as (va, vb)
    /// </summary>
    private List<TaskDependency> ParseTupleDependencies()
    {
        SourceLocation location = GetLocation();

        // Parse expression tuple: (expr, expr, ...)
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' for tuple dependencies");

        List<Expression> expressions = [];
        do
        {
            expressions.Add(item: ParseUnary());
        } while (Match(type: TokenType.Comma));

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after tuple dependencies");

        // Parse 'as' and binding tuple: (ident, ident, ...)
        Consume(type: TokenType.As, errorMessage: "Expected 'as' after tuple dependencies");

        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' for tuple bindings");

        List<string> bindings = [];
        do
        {
            Token bindingToken = Consume(type: TokenType.Identifier,
                errorMessage: "Expected identifier in tuple binding");
            bindings.Add(item: bindingToken.Text);
        } while (Match(type: TokenType.Comma));

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after tuple bindings");

        // Validate counts match
        if (expressions.Count != bindings.Count)
        {
            Token current = CurrentToken;
            throw new GrammarException(code: GrammarDiagnosticCode.TupleDependencyCountMismatch,
                message:
                $"Tuple dependency count ({expressions.Count}) does not match binding count ({bindings.Count})",
                fileName: fileName,
                line: current.Line,
                column: current.Column,
                language: _language);
        }

        // Create TaskDependency for each pair
        List<TaskDependency> dependencies = [];
        for (int i = 0; i < expressions.Count; i++)
        {
            string? binding = i < bindings.Count
                ? bindings[index: i]
                : null;
            dependencies.Add(item: new TaskDependency(DependencyExpr: expressions[index: i],
                BindingName: binding,
                Location: location));
        }

        return dependencies;
    }

    /// <summary>
    /// Checks whether the current <c>[</c> starts a generic type argument list.
    /// Verifies that bracket contents look like type arguments (identifiers, commas, <c>?</c>, nested <c>[]</c>)
    /// and that the closing <c>]</c> is followed by <c>(</c> (or <c>.</c> if <paramref name="acceptDotAfterBracket"/> is true).
    /// Does not modify <see cref="Position"/>.
    /// </summary>
    /// <param name="acceptDotAfterBracket">If true, also returns true when <c>]</c> is followed by <c>.</c> (for member access generics).</param>
    private bool IsLikelyGenericBracket(bool acceptDotAfterBracket)
    {
        if (!Check(type: TokenType.LeftBracket))
        {
            return false;
        }

        int scanPos = Position + 1; // start after [
        int depth = 1;
        int matchingBracketPos = -1;

        while (scanPos < Tokens.Count && depth > 0)
        {
            TokenType tt = Tokens[index: scanPos].Type;
            if (tt == TokenType.LeftBracket)
            {
                depth++;
            }
            else if (tt == TokenType.RightBracket)
            {
                depth--;
                if (depth == 0)
                {
                    matchingBracketPos = scanPos;
                    break;
                }
            }
            else if (tt is TokenType.Newline or TokenType.Eof)
            {
                return false;
            }
            else if (depth == 1 && tt is not TokenType.Identifier and not TokenType.Comma
                         and not TokenType.Question)
            {
                return
                    false; // Content has non-type tokens (numbers, operators, range keywords, etc.)
            }

            scanPos++;
        }

        if (matchingBracketPos < 0)
        {
            return false;
        }

        if (matchingBracketPos + 1 >= Tokens.Count)
        {
            return false;
        }

        TokenType afterBracket = Tokens[index: matchingBracketPos + 1].Type;
        return afterBracket == TokenType.LeftParen ||
               acceptDotAfterBracket && afterBracket == TokenType.Dot;
    }

    /// <summary>
    /// Checks whether the current position has brackets that likely contain generic type arguments
    /// following a standalone identifier. Returns true for patterns like <c>func[T]()</c> or <c>func![T]()</c>.
    /// Distinguishes generic brackets from index/slice brackets by checking:
    /// <list type="number">
    /// <item>If preceded by <c>!</c>, always considered generic (<c>func![T]()</c>)</item>
    /// <item>Contents must look like type arguments (identifiers, commas, <c>?</c>, nested <c>[]</c> only)</item>
    /// <item>Closing bracket must be followed by <c>(</c> to confirm a generic call</item>
    /// </list>
    /// This avoids false positives like <c>list[5]</c>, <c>list[0 to 5]</c>, or <c>list[i].foo</c>.
    /// </summary>
    private bool IsLikelyGenericAfterIdentifier()
    {
        // Check for ![ (failable generic) or [ (potential generic or index)
        bool hasBang = Check(type: TokenType.Bang) && PeekToken(offset: 1)
           .Type == TokenType.LeftBracket;
        if (!Check(type: TokenType.LeftBracket) && !hasBang)
        {
            return false;
        }

        // func![T]() is always generic — ! before [ only makes sense for generics
        if (hasBang)
        {
            return true;
        }

        // Scan the bracket contents without modifying Position
        int scanPos = Position + 1; // start after [
        int depth = 1;
        int matchingBracketPos = -1;
        bool hasTopLevelComma = false;

        while (scanPos < Tokens.Count && depth > 0)
        {
            TokenType tt = Tokens[index: scanPos].Type;
            if (tt == TokenType.LeftBracket)
            {
                depth++;
            }
            else if (tt == TokenType.RightBracket)
            {
                depth--;
                if (depth == 0)
                {
                    matchingBracketPos = scanPos;
                    break;
                }
            }
            else if (tt is TokenType.Newline or TokenType.Eof)
            {
                return false; // Unclosed or multi-line brackets — not generic
            }
            else if (depth == 1 && tt is not TokenType.Identifier and not TokenType.Comma
                         and not TokenType.Question
                         // Allow integer literals for const generic arguments (e.g., ValueList[S64, 4], ValueBitList[8])
                         and not TokenType.Integer and not TokenType.S64Literal
                         and not TokenType.U64Literal and not TokenType.S32Literal
                         and not TokenType.U32Literal and not TokenType.S16Literal
                         and not TokenType.U16Literal and not TokenType.S8Literal
                         and not TokenType.U8Literal and not TokenType.S128Literal
                         and not TokenType.U128Literal and not TokenType.AddressLiteral
                         and not TokenType.True and not TokenType.False)
            {
                // Content at top level doesn't look like type arguments (has numbers, operators, etc.)
                return false;
            }
            else if (depth == 1 && tt == TokenType.Comma)
            {
                hasTopLevelComma = true;
            }

            scanPos++;
        }

        if (matchingBracketPos < 0)
        {
            return false;
        }

        if (matchingBracketPos + 1 >= Tokens.Count)
        {
            return false;
        }

        TokenType afterBracket = Tokens[index: matchingBracketPos + 1].Type;

        // ] followed by ( → always generic: Type[T](...)
        if (afterBracket == TokenType.LeftParen)
        {
            return true;
        }

        // ] followed by . with multiple args → generic: Type[T, N].method(...)
        // Single arg + dot stays as index: list[i].foo
        return hasTopLevelComma && afterBracket == TokenType.Dot;
    }
}
