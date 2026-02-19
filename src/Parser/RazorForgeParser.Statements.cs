using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using RazorForge.Diagnostics;

namespace Compilers.RazorForge.Parser;

/// <summary>
/// Partial class containing statement parsing (if, while, for, when, blocks, scoped access, etc.).
/// </summary>
public partial class RazorForgeParser
{
    /// <summary>
    /// Parses an if statement with optional elseif/else chains.
    /// Syntax: <c>if condition { body } elseif condition { body } else { body }</c>
    /// </summary>
    /// <returns>An <see cref="IfStatement"/> AST node.</returns>
    private IfStatement ParseIfStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression condition = ParseExpression();
        Statement thenBranch = ParseStatement();
        Statement? elseBranch = null;

        // Handle elseif chain
        while (Match(type: TokenType.Elseif))
        {
            Expression elseifCondition = ParseExpression();
            Statement elseifBranch = ParseStatement();

            // Convert elseif to nested if-else
            var nestedIf = new IfStatement(Condition: elseifCondition,
                ThenStatement: elseifBranch,
                ElseStatement: null,
                Location: GetLocation(token: PeekToken(offset: -1)));

            elseBranch = elseBranch switch
            {
                null => nestedIf,
                IfStatement { ElseStatement: null } prevIf => new IfStatement(Condition: prevIf.Condition,
                    ThenStatement: prevIf.ThenStatement,
                    ElseStatement: nestedIf,
                    Location: prevIf.Location),
                _ => nestedIf
            };
        }

        if (Match(type: TokenType.Else))
        {
            Statement finalElse = ParseStatement();

            if (elseBranch == null)
            {
                elseBranch = finalElse;
            }
            else if (elseBranch is IfStatement lastIf)
            {
                // Attach final else to the end of the elif chain
                IfStatement current = lastIf;
                while (current.ElseStatement is IfStatement nextIf)
                {
                    current = nextIf;
                }

                elseBranch = new IfStatement(Condition: lastIf.Condition,
                    ThenStatement: lastIf.ThenStatement,
                    ElseStatement: current.ElseStatement ?? finalElse,
                    Location: lastIf.Location);
            }
        }

        return new IfStatement(Condition: condition,
            ThenStatement: thenBranch,
            ElseStatement: elseBranch,
            Location: location);
    }

    /// <summary>
    /// Parses an unless statement (inverted if).
    /// Syntax: <c>unless condition { body }</c> = <c>if not condition { body }</c>
    /// </summary>
    /// <returns>An <see cref="IfStatement"/> with negated condition.</returns>
    private IfStatement ParseUnlessStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression condition = ParseExpression();
        Statement thenBranch = ParseStatement();
        Statement? elseBranch = null;

        if (Match(type: TokenType.Else))
        {
            elseBranch = ParseStatement();
        }

        // Unless is "if not condition"
        var negatedCondition = new UnaryExpression(Operator: UnaryOperator.Not, Operand: condition, Location: condition.Location);

        return new IfStatement(Condition: negatedCondition,
            ThenStatement: thenBranch,
            ElseStatement: elseBranch,
            Location: location);
    }

    /// <summary>
    /// Parses a while loop statement.
    /// Syntax: <c>while condition { body }</c>
    /// </summary>
    /// <returns>A <see cref="WhileStatement"/> AST node.</returns>
    private WhileStatement ParseWhileStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression condition = ParseExpression();
        Statement body = ParseStatement();

        return new WhileStatement(Condition: condition, Body: body, ElseBranch: null, Location: location);
    }

    /// <summary>
    /// Parses an infinite loop statement.
    /// Syntax: <c>loop { body }</c> = <c>while true { body }</c>
    /// </summary>
    /// <returns>A <see cref="WhileStatement"/> with always-true condition.</returns>
    private WhileStatement ParseLoopStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // loop { body } is equivalent to while true { body }
        Expression trueCondition = new LiteralExpression(Value: true, LiteralType: TokenType.True, Location: location);
        Statement body = ParseStatement();

        return new WhileStatement(Condition: trueCondition, Body: body, ElseBranch: null, Location: location);
    }

    /// <summary>
    /// Parses a for-in loop statement (iteration over collections).
    /// Syntax: <c>for variable in iterable { body }</c> or <c>for (a, b) in iterable { body }</c>
    /// </summary>
    /// <returns>A <see cref="ForStatement"/> AST node.</returns>
    private ForStatement ParseForStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string? variable = null;
        DestructuringPattern? variablePattern = null;

        // Check for tuple destructuring: for (index, item) in items.enumerate()
        if (Check(type: TokenType.LeftParen))
        {
            List<DestructuringBinding> bindings = ParseDestructuringBindings();
            variablePattern = new DestructuringPattern(Bindings: bindings, Location: location);
        }
        else
        {
            variable = ConsumeIdentifier(errorMessage: "Expected variable name");
        }

        Consume(type: TokenType.In, errorMessage: "Expected 'in' in for loop");
        Expression iterable = ParseExpression();
        Statement body = ParseStatement();

        return new ForStatement(Variable: variable,
            VariablePattern: variablePattern,
            Iterable: iterable,
            Body: body,
            ElseBranch: null,
            Location: location);
    }

    /// <summary>
    /// Parses a when statement (pattern matching).
    /// Syntax: <c>when expr { pattern =&gt; body, ... }</c> or <c>when { condition =&gt; body, ... }</c>
    /// Supports type patterns, literal patterns, wildcard patterns, and expression guards.
    /// </summary>
    /// <returns>A <see cref="WhenStatement"/> AST node.</returns>
    /// <remarks>
    /// Two forms of when statements:
    /// 1. Subject-based: when expr { is Type => ..., LITERAL => ..., else => ... }
    /// 2. Condition-based: when { condition1 => ..., condition2 => ..., else => ... }
    ///
    /// Pattern types supported:
    /// - 'else' / 'else varName' - default case (wildcard or binding)
    /// - '_' - explicit wildcard
    /// - 'is Type' / 'is Type varName' - type pattern with optional binding
    /// - 'is Type (field1, field2)' - destructuring pattern
    /// - literal values (42, "hello", true)
    /// - expression patterns (for condition-based when)
    /// </remarks>
    private WhenStatement ParseWhenStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 1: Determine when form (subject-based vs condition-based)
        // ═══════════════════════════════════════════════════════════════════════════
        // Subject-based:    when value { is Type => ... }
        // Condition-based:  when { x > 0 => ..., x < 0 => ... }  (like Lisp's cond)
        //                   when true { x > 0 => ..., x < 0 => ... }  (explicit true)
        // ═══════════════════════════════════════════════════════════════════════════

        // Check for condition-based forms: `when {` or `when true {`
        bool isConditionBased = Check(type: TokenType.LeftBrace) ||
                                (Check(type: TokenType.True) && PeekToken(offset: 1).Type == TokenType.LeftBrace);
        Expression expression;
        if (isConditionBased)
        {
            // Standalone when - use 'true' as the implicit subject
            // For `when true {`, consume the 'true' token
            Match(type: TokenType.True);
            expression = new LiteralExpression(Value: true, LiteralType: TokenType.True, Location: location);
        }
        else
        {
            // Traditional when with explicit subject
            expression = ParseExpression();
        }

        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after when expression");

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 2: Parse clauses (pattern => body)
        // ═══════════════════════════════════════════════════════════════════════════

        var clauses = new List<WhenClause>();

        while (!Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            Pattern pattern;
            SourceLocation clauseLocation = GetLocation();

            // ─────────────────────────────────────────────────────────────────────
            // Pattern dispatch: determine which pattern type we're parsing
            // Order matters - check specific patterns before general ones
            // ─────────────────────────────────────────────────────────────────────

            // Case 1: 'else' keyword - default/fallback case
            // Forms: else => body, else varName => body, else varName { block }
            if (Match(type: TokenType.Else))
            {
                // Check for variable binding: else varName => or else varName {
                TokenType nextAfterIdent = PeekToken(offset: 1).Type;
                if (Check(type: TokenType.Identifier) &&
                    (nextAfterIdent == TokenType.FatArrow || nextAfterIdent == TokenType.LeftBrace))
                {
                    string varName = ConsumeIdentifier(errorMessage: "Expected variable name after 'else'");
                    pattern = new ElsePattern(VariableName: varName, Location: clauseLocation);
                }
                else
                {
                    // Plain else without variable binding
                    pattern = new ElsePattern(VariableName: null, Location: clauseLocation);
                }
            }
            // Case 2: Explicit wildcard '_'
            else if (Check(type: TokenType.Identifier) && CurrentToken.Text == "_" && PeekToken(offset: 1).Type == TokenType.FatArrow)
            {
                Advance(); // consume _
                pattern = new WildcardPattern(Location: clauseLocation);
            }
            // Case 3: Condition-based when - parse full expression as pattern
            else if (isConditionBased)
            {
                // Parse the condition as a full expression (e.g., me > 0)
                Expression condition = ParseExpression();
                pattern = new ExpressionPattern(Expression: condition, Location: clauseLocation);
            }
            // Case 4: 'is' keyword - type pattern only
            // Forms: is Type, is Type varName, is Type (field1, field2), is None
            // Note: 'is <value>' is NOT allowed - use '== value' for value comparisons
            // Semantic analysis validates whether the identifier is a valid type or variant
            else if (Match(type: TokenType.Is))
            {
                _inWhenPatternContext = true;
                // 'is' must be followed by a type/variant name - semantic analysis validates
                if (Check(type: TokenType.TypeIdentifier) ||
                    Check(type: TokenType.None) ||
                    Check(type: TokenType.Identifier))
                {
                    // Parse as type pattern - semantic analysis will validate if it's a valid type
                    pattern = ParseTypePattern();
                }
                else
                {
                    throw ThrowParseError(RazorForgeDiagnosticCode.InvalidPattern,
                        $"'is' must be followed by a type name. For value comparisons, use '== {CurrentToken.Text}' instead of 'is {CurrentToken.Text}'.");
                }
                _inWhenPatternContext = false;
            }
            // Case 5: Comparison patterns (==, !=, <, >, <=, >=, ===, !==)
            else if (IsComparisonOperator(CurrentToken.Type))
            {
                pattern = ParseComparisonPattern();
            }
            // Case 6: Other patterns (literals, identifiers)
            else
            {
                // Set context flag to prevent single-param lambdas from being parsed
                // inside when patterns (e.g., a < b => action should not treat b => action as lambda)
                _inWhenPatternContext = true;
                pattern = ParsePattern();
                _inWhenPatternContext = false;
            }

            // ─────────────────────────────────────────────────────────────────────
            // Parse arrow or block
            // Three forms:
            //   pattern => expression       (single expression/statement)
            //   pattern => { statements }   (multi-statement block after arrow)
            //   pattern { statements... }   (multi-statement block, requires 'becomes')
            // ─────────────────────────────────────────────────────────────────────

            Statement body;

            if (Match(type: TokenType.FatArrow))
            {
                // Arrow branch: pattern => single_statement (no blocks allowed after =>)
                if (Check(type: TokenType.LeftBrace))
                {
                    throw ThrowParseError(RazorForgeDiagnosticCode.InvalidPattern,
                        "Block '{ }' is not allowed after '=>'. Use 'pattern { block }' without '=>' for multi-statement branches.");
                }

                // Single statement: pattern => statement
                // Set flag to prevent comparisons from continuing the expression
                // This prevents `=> value < pattern =>` from being parsed as `=> (value < pattern) =>`
                _inWhenClauseBody = true;
                body = ParseStatement();
                _inWhenClauseBody = false;
            }
            else if (Check(type: TokenType.LeftBrace))
            {
                // Multi-statement branch: pattern { statements... becomes value }
                // Block has its own scope, so don't restrict comparisons inside it
                body = ParseBlockStatement();
            }
            else
            {
                throw ThrowParseError(RazorForgeDiagnosticCode.InvalidPattern,
                    "Expected '=>' or '{' after pattern");
            }

            clauses.Add(item: new WhenClause(Pattern: pattern, Body: body, Location: GetLocation()));

            // Optional comma or newline between clauses
            Match(TokenType.Comma, TokenType.Newline);
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after when clauses");

        return new WhenStatement(Expression: expression, Clauses: clauses, Location: location);
    }

    /// <summary>
    /// Parses a pattern for use in when clauses.
    /// Supports: wildcard (_), type patterns (Type varName), variant patterns (Type.CASE or CASE),
    /// destructuring patterns (CASE (a, b)), literal patterns, guard patterns (n if n &lt; 0).
    /// </summary>
    /// <returns>A <see cref="Pattern"/> AST node.</returns>
    private Pattern ParsePattern()
    {
        SourceLocation location = GetLocation();

        // Wildcard pattern: _
        if (Check(type: TokenType.Identifier) && CurrentToken.Text == "_")
        {
            Advance();
            Pattern wildcardPattern = new WildcardPattern(Location: location);
            return TryParseGuard(innerPattern: wildcardPattern, location: location);
        }

        // Type pattern with TypeIdentifier token (lexer identifies types by case/context)
        if (Check(type: TokenType.TypeIdentifier))
        {
            return ParseTypePattern();
        }

        // Identifier pattern: variable binding (check before parsing full expression)
        if (Check(type: TokenType.Identifier))
        {
            string name = ConsumeIdentifier(errorMessage: "Expected identifier for pattern");
            Pattern identPattern = new IdentifierPattern(Name: name, Location: location);
            return TryParseGuard(innerPattern: identPattern, location: location);
        }

        // Literal pattern: constants like 42, "hello", true, etc.
        Expression expr = ParsePrimary();
        if (expr is LiteralExpression literal)
        {
            Pattern litPattern = new LiteralPattern(Value: literal.Value, LiteralType: literal.LiteralType, Location: location);
            return TryParseGuard(innerPattern: litPattern, location: location);
        }

        // Otherwise, treat as expression pattern
        return new ExpressionPattern(Expression: expr, Location: location);
    }

    /// <summary>
    /// Parses a type pattern (used after 'is' keyword).
    /// Accepts both Identifier and TypeIdentifier tokens since lexer may emit either.
    /// Syntax: Type, Type varName, Type.CASE, Type.CASE varName, CASE (a, b)
    /// </summary>
    /// <returns>A <see cref="TypePattern"/> AST node.</returns>
    private Pattern ParseTypePattern()
    {
        SourceLocation location = GetLocation();
        // Handle 'is None' as a special case - None is a keyword
        if (Match(type: TokenType.None))
        {
            // Create a special "None" type pattern
            TypeExpression noneType = new TypeExpression(Name: "None", GenericArguments: null,
                Location: location);
            Pattern nonePattern = new TypePattern(Type: noneType, VariableName: null, Bindings: null, Location: location);
            return TryParseGuard(innerPattern: nonePattern, location: location);
        }

        // Accept both Identifier and TypeIdentifier (lexer may emit Identifier for all)
        if (!Check(type: TokenType.Identifier) && !Check(type: TokenType.TypeIdentifier))
        {
            throw ThrowParseError(RazorForgeDiagnosticCode.ExpectedPattern,
                $"Expected type name after 'is', got {CurrentToken.Type}");
        }

        string name = CurrentToken.Text;
        Advance();

        // Check for qualified name: Type.CASE or Type.CASE.SubCase
        while (Match(type: TokenType.Dot))
        {
            if (Match(TokenType.Identifier, TokenType.TypeIdentifier))
            {
                name += "." + PeekToken(offset: -1).Text;
            }
            else
            {
                throw ThrowParseError(RazorForgeDiagnosticCode.ExpectedDotInQualifiedPattern,
                    "Expected identifier after '.' in pattern");
            }
        }

        // Check for destructuring: Type.CASE (field1, field2), (field: alias), or ((x, y), z)
        List<DestructuringBinding>? bindings = null;
        if (Match(type: TokenType.LeftParen))
        {
            bindings = ParseDestructuringBindingList();
            Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after destructuring bindings");
        }

        // Check for variable binding (only if no destructuring)
        string? variableName = null;
        if (bindings == null && Check(type: TokenType.Identifier))
        {
            variableName = ConsumeIdentifier(errorMessage: "Expected variable name for type pattern");
        }

        TypeExpression type = new TypeExpression(Name: name, GenericArguments: null, Location: location);
        Pattern typePattern = new TypePattern(Type: type, VariableName: variableName, Bindings: bindings, Location: location);
        return TryParseGuard(innerPattern: typePattern, location: location);
    }

    /// <summary>
    /// Tries to parse a guard clause (if condition) after a pattern.
    /// Syntax: <c>pattern if condition</c>
    /// </summary>
    /// <param name="innerPattern">The pattern before the guard.</param>
    /// <param name="location">Source location of the pattern.</param>
    /// <returns>A <see cref="GuardPattern"/> if guard is present, otherwise the original pattern.</returns>
    private Pattern TryParseGuard(Pattern innerPattern, SourceLocation location)
    {
        if (Match(type: TokenType.If))
        {
            // Temporarily reset _inWhenClauseBody to allow full expression parsing for the guard.
            // This is needed for nested when statements where the outer clause body flag would
            // otherwise cause ParseEquality/ParseComparison to return early.
            bool savedInWhenClauseBody = _inWhenClauseBody;
            _inWhenClauseBody = false;
            Expression guard = ParseExpression();
            _inWhenClauseBody = savedInWhenClauseBody;
            return new GuardPattern(InnerPattern: innerPattern, Guard: guard, Location: location);
        }

        return innerPattern;
    }

    /// <summary>
    /// Parses a return statement.
    /// Syntax: <c>return</c> or <c>return value</c>
    /// </summary>
    /// <returns>A <see cref="ReturnStatement"/> AST node.</returns>
    private ReturnStatement ParseReturnStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression? value = null;
        // Check if there's a return value - if next token is a statement terminator or block end, there isn't one
        if (!Check(type: TokenType.Newline) && !Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            value = ParseExpression();
        }

        ConsumeStatementTerminator();

        return new ReturnStatement(Value: value, Location: location);
    }

    /// <summary>
    /// Parses a becomes statement (block result value).
    /// Syntax: <c>becomes expression</c>
    /// Used in multi-statement when/if branches to explicitly indicate the branch's result.
    /// </summary>
    /// <returns>A <see cref="BecomesStatement"/> AST node.</returns>
    private BecomesStatement ParseBecomesStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // becomes requires an expression (unlike return which can be valueless)
        Expression value = ParseExpression();

        ConsumeStatementTerminator();

        return new BecomesStatement(Value: value, Location: location);
    }

    /// <summary>
    /// Parses a break statement (exits loop).
    /// Syntax: <c>break</c>
    /// </summary>
    /// <returns>A <see cref="BreakStatement"/> AST node.</returns>
    private BreakStatement ParseBreakStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new BreakStatement(Location: location);
    }

    /// <summary>
    /// Parses a continue statement (skips to next loop iteration).
    /// Syntax: <c>continue</c>
    /// </summary>
    /// <returns>A <see cref="ContinueStatement"/> AST node.</returns>
    private ContinueStatement ParseContinueStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new ContinueStatement(Location: location);
    }

    /// <summary>
    /// Parses a pass statement (no-op placeholder).
    /// Syntax: <c>pass</c>
    /// </summary>
    /// <returns>A <see cref="PassStatement"/> AST node.</returns>
    private PassStatement ParsePassStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new PassStatement(Location: location);
    }

    /// <summary>
    /// Parses a throw statement (raises an error).
    /// Syntax: <c>throw errorExpression</c>
    /// </summary>
    /// <returns>A <see cref="ThrowStatement"/> AST node.</returns>
    private ThrowStatement ParseThrowStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        Expression error = ParseExpression();
        ConsumeStatementTerminator();
        return new ThrowStatement(Error: error, Location: location);
    }

    /// <summary>
    /// Parses an absent statement (returns None from failable function).
    /// Syntax: <c>absent</c>
    /// </summary>
    /// <returns>An <see cref="AbsentStatement"/> AST node.</returns>
    private AbsentStatement ParseAbsentStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new AbsentStatement(Location: location);
    }

    /// <summary>
    /// Parses a discard statement (explicitly ignores a return value).
    /// Syntax: <c>discard routine_call()</c>
    /// Used to explicitly indicate that a routine's return value is intentionally ignored.
    /// </summary>
    /// <returns>A <see cref="DiscardStatement"/> AST node.</returns>
    private DiscardStatement ParseDiscardStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        Expression expression = ParseExpression();

        // Discard must be followed by a call expression
        if (expression is not CallExpression)
        {
            throw new RazorForgeGrammarException(
                RazorForgeDiagnosticCode.DiscardRequiresCall,
                "The 'discard' keyword must be followed by a routine call. " +
                "Use 'discard routine_call()' to explicitly ignore a return value.",
                fileName, location.Line, location.Column);
        }

        ConsumeStatementTerminator();
        return new DiscardStatement(Expression: expression, Location: location);
    }

    /// <summary>
    /// Parses a generate statement (generator yield).
    /// Syntax: <c>generate expression</c>
    /// Yields a value from a generator routine.
    /// </summary>
    /// <returns>A <see cref="GenerateStatement"/> AST node.</returns>
    private GenerateStatement ParseGenerateStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        Expression expression = ParseExpression();
        ConsumeStatementTerminator();
        return new GenerateStatement(Expression: expression, Location: location);
    }

    /// <summary>
    /// Parses a block statement (sequence of statements in braces).
    /// Syntax: <c>{ statement1\nstatement2\n... }</c>
    /// Handles variable declarations with var/let keywords.
    /// </summary>
    /// <returns>A <see cref="BlockStatement"/> AST node.</returns>
    private BlockStatement ParseBlockStatement()
    {
        SourceLocation location = GetLocation();
        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{'");

        var statements = new List<Statement>();

        while (!Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            // Handle variable declarations inside blocks
            if (Match(TokenType.Var, TokenType.Let))
            {
                // Check if this is destructuring: let (field, field2) = expr
                if (Check(type: TokenType.LeftParen))
                {
                    Statement destructuring = ParseDestructuringDeclaration();
                    statements.Add(item: destructuring);
                }
                else
                {
                    VariableDeclaration varDecl = ParseVariableDeclaration();
                    // Wrap the variable declaration as a declaration statement
                    statements.Add(item: new DeclarationStatement(Declaration: varDecl, Location: varDecl.Location));
                }

                continue;
            }

            Statement stmt = ParseStatement();
            statements.Add(item: stmt);
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}'");

        return new BlockStatement(Statements: statements, Location: location);
    }

    /// <summary>
    /// Parses an expression statement (expression used as statement).
    /// Syntax: <c>expression</c>
    /// </summary>
    /// <returns>An <see cref="ExpressionStatement"/> AST node.</returns>
    private ExpressionStatement ParseExpressionStatement()
    {
        Expression expr = ParseExpression();
        ConsumeStatementTerminator();
        return new ExpressionStatement(Expression: expr, Location: expr.Location);
    }

    /// <summary>
    /// Parses a danger statement (unsafe memory operations block).
    /// Syntax: <c>danger! { unsafe_operations }</c>
    /// </summary>
    /// <returns>A <see cref="DangerStatement"/> AST node.</returns>
    private DangerStatement ParseDangerStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // Expect 'danger!'
        Consume(type: TokenType.Bang, errorMessage: "Expected '!' after 'danger'");

        BlockStatement body = ParseBlockStatement();

        return new DangerStatement(Body: body, Location: location);
    }

    /// <summary>
    /// Parses a using declaration (resource management)
    /// Syntax: <c>using file as a</c> or <c>using file1 as a, file2 as b</c>
    /// </summary>
    /// <returns>A <see cref="UsingStatement"/> AST node (nested for multiple resources).</returns>
    private Statement ParseUsingStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        var resources = new List<(Expression Resource, string Name, SourceLocation Location)>();

        // Parse comma-separated resources: using r1 as n1, r2 as n2 { ... }
        do
        {
            SourceLocation resourceLocation = GetLocation();
            Expression resource = ParseExpression();

            // Expect 'as'
            Consume(type: TokenType.As, errorMessage: "Expected 'as' after resource expression in using statement");

            // Parse the binding name
            string name = ConsumeIdentifier(errorMessage: "Expected identifier after 'as' in using statement");

            resources.Add(item: (resource, name, resourceLocation));
        } while (Match(type: TokenType.Comma));

        Statement body = ParseBlockStatement();

        // Build nested UsingStatements from inside out
        for (int i = resources.Count - 1; i >= 0; i--)
        {
            (Expression resource, string name, SourceLocation resLocation) = resources[index: i];
            body = new UsingStatement(Resource: resource, Name: name, Body: body,
                Location: i == 0 ? location : resLocation);
        }

        return (UsingStatement)body;
    }

    /// <summary>
    /// Parses record/entity destructuring: let (field, field2) = expr
    /// or let (field: alias, field2: alias2) = expr
    /// or nested: let ((x, y), radius) = circle
    /// Destructuring only works for types where ALL fields are public.
    /// </summary>
    private DestructuringStatement ParseDestructuringDeclaration()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -2)); // -2 because we already consumed 'let'/'var'
        bool isMutable = PeekToken(offset: -2)
           .Type == TokenType.Var;

        // Parse the destructuring pattern (reuse ParseDestructuringBindings from Expressions)
        List<DestructuringBinding> bindings = ParseDestructuringBindings();

        var pattern = new DestructuringPattern(Bindings: bindings, Location: location);

        // Expect '='
        Consume(type: TokenType.Assign, errorMessage: "Expected '=' in destructuring");

        // Parse the initializer expression
        Expression initializer = ParseExpression();

        ConsumeStatementTerminator();

        return new DestructuringStatement(Pattern: pattern,
            Initializer: initializer,
            IsMutable: isMutable,
            Location: location);
    }

    /// <summary>
    /// Checks if the given token type is a comparison operator used in when patterns.
    /// Supported operators: ==, !=, &lt;, &gt;, &lt;=, &gt;=, ===, !==
    /// </summary>
    /// <param name="tokenType">The token type to check.</param>
    /// <returns>True if the token is a comparison operator for patterns.</returns>
    private static bool IsComparisonOperator(TokenType tokenType)
    {
        return tokenType is TokenType.Equal or TokenType.NotEqual
            or TokenType.Less or TokenType.Greater
            or TokenType.LessEqual or TokenType.GreaterEqual
            or TokenType.ReferenceEqual or TokenType.ReferenceNotEqual;
    }

    /// <summary>
    /// Parses a comparison pattern in a when clause.
    /// Syntax: <c>== value</c>, <c>!= value</c>, <c>&lt; value</c>, <c>&gt; value</c>,
    /// <c>&lt;= value</c>, <c>&gt;= value</c>, <c>=== value</c>, <c>!== value</c>
    /// </summary>
    /// <returns>A <see cref="ComparisonPattern"/> or <see cref="GuardPattern"/> AST node.</returns>
    private Pattern ParseComparisonPattern()
    {
        SourceLocation location = GetLocation();
        TokenType op = CurrentToken.Type;
        Advance(); // consume the operator

        // Parse the value to compare against
        // Set context flag to prevent lambda parsing
        _inWhenPatternContext = true;
        Expression value = ParsePrimary();

        // Allow member access and calls on the primary expression (e.g., Status.ACTIVE, get_user())
        while (Check(type: TokenType.Dot) || Check(type: TokenType.LeftParen))
        {
            if (Match(type: TokenType.Dot))
            {
                string memberName = ConsumeIdentifier(errorMessage: "Expected member name after '.'");
                value = new MemberExpression(Object: value, PropertyName: memberName, Location: GetLocation());
            }
            else if (Match(type: TokenType.LeftParen))
            {
                List<Expression> args = ParseArgumentList();
                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");
                value = new CallExpression(Callee: value, Arguments: args, Location: GetLocation());
            }
        }

        _inWhenPatternContext = false;

        var pattern = new ComparisonPattern(Operator: op, Value: value, Location: location);
        return TryParseGuard(innerPattern: pattern, location: location);
    }
}
