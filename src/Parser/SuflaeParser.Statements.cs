using Compilers.Shared.AST;
using Compilers.Shared.Lexer;

namespace Compilers.Suflae.Parser;

/// <summary>
/// Partial class containing statement parsing (if, while, for, when, return, etc.).
/// </summary>
public partial class SuflaeParser
{
    /// <summary>
    /// Parses an if statement with indented blocks and optional elseif/else chains.
    /// Syntax: <c>if condition:</c> followed by indented body, optional <c>elseif condition:</c> blocks, optional <c>else:</c> block.
    /// </summary>
    /// <remarks>
    /// Parsing phases:
    ///
    /// PHASE 1: INITIAL IF
    ///   - Parse condition expression
    ///   - Parse indented then-block
    ///
    /// PHASE 2: ELSEIF CHAIN (optional)
    ///   - Parse each elseif as a nested IfStatement
    ///   - Chain them together via ElseStatement
    ///
    /// PHASE 3: FINAL ELSE (optional)
    ///   - Parse else block
    ///   - Attach to end of elseif chain
    ///
    /// The elseif chain is desugared to nested if-else:
    ///   if a:       becomes    IfStatement(a, then1,
    ///       then1                  IfStatement(b, then2,
    ///   elseif b:                      else3))
    ///       then2
    ///   else:
    ///       else3
    /// </remarks>
    /// <returns>An <see cref="IfStatement"/> AST node.</returns>
    private Statement ParseIfStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 1: INITIAL IF (condition and then-block)
        // ═══════════════════════════════════════════════════════════════════════════
        Expression condition = ParseExpression();
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after if condition");
        Statement thenBranch = ParseIndentedBlock();

        Statement? elseBranch = null;

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 2: ELSEIF CHAIN (convert to nested if-else)
        // ═══════════════════════════════════════════════════════════════════════════
        while (Match(type: TokenType.Elseif))
        {
            SourceLocation elseifLocation = GetLocation(token: PeekToken(offset: -1));
            Expression elseifCondition = ParseExpression();
            Consume(type: TokenType.Colon, errorMessage: "Expected ':' after elseif condition");
            Statement elseifBranch = ParseIndentedBlock();

            // Create nested if statement for this elseif
            var nestedIf = new IfStatement(Condition: elseifCondition,
                ThenStatement: elseifBranch,
                ElseStatement: null,
                Location: elseifLocation);

            // Attach to the chain
            if (elseBranch == null)
            {
                elseBranch = nestedIf;
            }
            else if (elseBranch is IfStatement prevIf)
            {
                // Find the end of the elseif chain and attach
                IfStatement current = prevIf;
                while (current.ElseStatement is IfStatement nextIf)
                {
                    current = nextIf;
                }
                // Create new chain with the nested if attached
                elseBranch = AttachElseBranch(root: prevIf, newBranch: nestedIf);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 3: FINAL ELSE (optional)
        // ═══════════════════════════════════════════════════════════════════════════
        if (!Match(type: TokenType.Else))
        {
            return new IfStatement(Condition: condition,
                ThenStatement: thenBranch,
                ElseStatement: elseBranch,
                Location: location);
        }

        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after else");
        Statement finalElse = ParseIndentedBlock();

        if (elseBranch == null)
        {
            elseBranch = finalElse;
        }
        else if (elseBranch is IfStatement lastIf)
        {
            // Attach final else to the end of the elseif chain
            elseBranch = AttachElseBranch(root: lastIf, newBranch: finalElse);
        }

        return new IfStatement(Condition: condition,
            ThenStatement: thenBranch,
            ElseStatement: elseBranch,
            Location: location);
    }

    /// <summary>
    /// Helper to recursively attach an else branch to the end of an if-elseif chain.
    /// Since IfStatement is immutable, we need to rebuild the chain.
    /// </summary>
    private static IfStatement AttachElseBranch(IfStatement root, Statement newBranch)
    {
        if (root.ElseStatement == null)
        {
            return new IfStatement(Condition: root.Condition,
                ThenStatement: root.ThenStatement,
                ElseStatement: newBranch,
                Location: root.Location);
        }
        if (root.ElseStatement is IfStatement nestedIf)
        {
            return new IfStatement(Condition: root.Condition,
                ThenStatement: root.ThenStatement,
                ElseStatement: AttachElseBranch(root: nestedIf, newBranch: newBranch),
                Location: root.Location);
        }
        // Already has a non-if else branch, shouldn't happen
        return root;
    }

    /// <summary>
    /// Parses an unless statement (inverted if).
    /// Syntax: <c>unless condition { body }</c> = <c>if not condition { body }</c>
    /// </summary>
    /// <returns>An <see cref="IfStatement"/> with negated condition.</returns>
    private Statement ParseUnlessStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression condition = ParseExpression();
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after if condition");
        Statement thenBranch = ParseIndentedBlock();
        Statement? elseBranch = null;

        if (Match(type: TokenType.Else))
        {
            Consume(type: TokenType.Colon, errorMessage: "Expected ':' after else");
            elseBranch = ParseIndentedBlock();
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
    /// Syntax: <c>while condition:</c> followed by indented body.
    /// </summary>
    /// <returns>A <see cref="WhileStatement"/> AST node.</returns>
    private Statement ParseWhileStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression condition = ParseExpression();
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after while condition");
        Statement body = ParseIndentedBlock();

        return new WhileStatement(Condition: condition, Body: body, Location: location);
    }

    /// <summary>
    /// Parses a loop statement (infinite loop).
    /// Syntax: <c>loop:</c> followed by indented body.
    /// Equivalent to <c>while true:</c>
    /// </summary>
    /// <returns>A <see cref="WhileStatement"/> AST node with true condition.</returns>
    private Statement ParseLoopStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // loop: body is equivalent to while true: body
        Expression trueCondition = new LiteralExpression(Value: true, LiteralType: TokenType.True, Location: location);
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after loop");
        Statement body = ParseIndentedBlock();

        return new WhileStatement(Condition: trueCondition, Body: body, Location: location);
    }

    /// <summary>
    /// Parses a for-in loop statement.
    /// Syntax: <c>for variable in iterable:</c> followed by indented body.
    /// </summary>
    /// <returns>A <see cref="ForStatement"/> AST node.</returns>
    private Statement ParseForStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string variable = ConsumeIdentifier(errorMessage: "Expected variable name");
        Consume(type: TokenType.In, errorMessage: "Expected 'in' in for loop");
        Expression iterable = ParseExpression();
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after for header");
        Statement body = ParseIndentedBlock();

        return new ForStatement(Variable: variable,
            Iterable: iterable,
            Body: body,
            Location: location);
    }

    /// <summary>
    /// Parses a when (pattern matching) statement.
    /// Syntax: <c>when expression:</c> followed by indented pattern clauses with<c> =&gt;</c>.
    /// Similar to Rust's match or C# switch expressions.
    /// </summary>
    /// <returns>A <see cref="WhenStatement"/> AST node.</returns>
    private WhenStatement ParseWhenStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression expression = ParseExpression();
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after when expression");

        Consume(type: TokenType.Newline, errorMessage: "Expected newline after when header");

        // Must have an indent token for a proper indented block
        if (!Check(type: TokenType.Indent))
        {
            throw ThrowParseError("Expected indented block after when");
        }
        ProcessIndentToken();

        var clauses = new List<WhenClause>();

        while (!Check(type: TokenType.Dedent) && !IsAtEnd)
        {
            // Skip newlines
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            Pattern pattern;
            SourceLocation clauseLocation = GetLocation();

            // Handle 'is' keyword pattern: is None, is SomeType, is SomeType varName
            if (Match(type: TokenType.Is))
            {
                _inWhenPatternContext = true;
                pattern = ParseTypePattern();
                _inWhenPatternContext = false;
            }
            // Handle 'else' keyword for default case: else => body or else varName => body
            else if (Match(type: TokenType.Else))
            {
                // Check for variable binding: else varName =>
                if (Check(type: TokenType.Identifier) && PeekToken(offset: 1)
                       .Type == TokenType.FatArrow)
                {
                    string varName = ConsumeIdentifier(errorMessage: "Expected variable name after 'else'");
                    pattern = new IdentifierPattern(Name: varName, Location: clauseLocation);
                }
                else
                {
                    // Plain else without variable binding - treat as wildcard
                    pattern = new WildcardPattern(Location: clauseLocation);
                }
            }
            // Comparison patterns (==, !=, <, >, <=, >=, ===, !==)
            else if (IsComparisonOperator(CurrentToken.Type))
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

            // Suflae supports two clause syntaxes:
            // 1. is PATTERN => expr           (single expression)
            // 2. is PATTERN:                  (indented block, requires 'becomes')
            //        statements...
            //        becomes value
            // Set flag to prevent 'is' expression parsing in when clause bodies
            _inWhenClauseBody = true;
            Statement body;
            if (Match(type: TokenType.Colon))
            {
                // Multi-line body: is PATTERN: followed by indented block
                body = ParseIndentedBlock();
            }
            else if (Match(type: TokenType.FatArrow))
            {
                // Single-line body: is PATTERN => expression
                // Block is NOT allowed after => (use PATTERN: with indented block instead)
                if (Check(type: TokenType.Colon))
                {
                    throw ThrowParseError("Block ':' is not allowed after '=>'. Use 'pattern:' with indented block and 'becomes' for multi-statement branches");
                }

                body = ParseExpressionStatement();
            }
            else
            {
                throw ThrowParseError("Expected ':' or '=>' after pattern");
            }

            _inWhenClauseBody = false;

            clauses.Add(item: new WhenClause(Pattern: pattern, Body: body, Location: GetLocation()));

            // Optional newline between clauses
            Match(type: TokenType.Newline);
        }

        // Process the when block's dedent (matching the ProcessIndentToken at the start)
        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }
        else if (!IsAtEnd)
        {
            throw ThrowParseError("Expected dedent after when clauses");
        }

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
            Advance(); // consume the '_'
            Pattern wildcardPattern = new WildcardPattern(Location: location);
            return TryParseGuard(innerPattern: wildcardPattern, location: location);
        }

        // Type/Variant pattern: Type, Type varName, Choice.CASE, Variant.CASE varName, CASE, CASE (a, b)
        if (Check(type: TokenType.Identifier) && CurrentToken.Text.Length > 0)
        {
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
                    throw ThrowParseError("Expected identifier after '.' in pattern");
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

            TypeExpression type = new (Name: name, GenericArguments: null, Location: location);
            Pattern typePattern = new TypePattern(Type: type, VariableName: variableName, Bindings: bindings, Location: location);
            return TryParseGuard(innerPattern: typePattern, location: location);
        }

        // Identifier pattern: variable binding
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

        throw ThrowParseError($"Expected pattern, got {CurrentToken.Type}");
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
            Expression guard = ParseExpression();
            return new GuardPattern(InnerPattern: innerPattern, Guard: guard, Location: location);
        }

        return innerPattern;
    }

    /// <summary>
    /// Parses a type pattern (used after 'is' keyword).
    /// Accepts all identifiers since Suflae lexer emits Identifier for all.
    /// Syntax: Type, Type varName, Type.CASE, Type.CASE varName, CASE (a, b)
    /// Also handles special keywords like 'none'.
    /// </summary>
    /// <returns>A <see cref="TypePattern"/> AST node.</returns>
    private Pattern ParseTypePattern()
    {
        SourceLocation location = GetLocation();

        // Handle 'is None' as a special case - None is a keyword
        if (Match(type: TokenType.None))
        {
            // Create a special "None" type pattern
            TypeExpression noneType = new TypeExpression(Name: "None", GenericArguments: null, Location: location);
            Pattern nonePattern = new TypePattern(Type: noneType, VariableName: null, Bindings: null, Location: location);
            return TryParseGuard(innerPattern: nonePattern, location: location);
        }

        // Accept Identifier (Suflae emits Identifier for all identifiers)
        if (!Check(type: TokenType.Identifier) && !Check(type: TokenType.TypeIdentifier))
        {
            throw ThrowParseError($"Expected type name after 'is', got {CurrentToken.Type}");
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
                throw ThrowParseError("Expected identifier after '.' in pattern");
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
    /// Parses a return statement.
    /// Syntax: <c>return</c> or <c>return expression</c>
    /// </summary>
    /// <returns>A <see cref="ReturnStatement"/> AST node.</returns>
    private Statement ParseReturnStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression? value = null;
        // Check for both Newline and Dedent - in Suflae, either can follow a valueless return
        if (!Check(type: TokenType.Newline) && !Check(type: TokenType.Dedent) && !IsAtEnd)
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
    private Statement ParseBecomesStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // becomes requires an expression (unlike return which can be valueless)
        Expression value = ParseExpression();

        ConsumeStatementTerminator();

        return new BecomesStatement(Value: value, Location: location);
    }

    /// <summary>
    /// Parses a throw (fail) statement for error propagation.
    /// Syntax: <c>throw errorExpression</c>
    /// Used with Crashable types for error handling.
    /// </summary>
    /// <returns>A <see cref="ThrowStatement"/> AST node.</returns>
    private Statement ParseThrowStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // fail requires an error expression (Crashable type)
        Expression error = ParseExpression();

        ConsumeStatementTerminator();

        return new ThrowStatement(Error: error, Location: location);
    }

    /// <summary>
    /// Parses an absent statement (return none/null).
    /// Syntax: <c>absent</c>
    /// Indicates the function returns no value (for optional types).
    /// </summary>
    /// <returns>An <see cref="AbsentStatement"/> AST node.</returns>
    private Statement ParseAbsentStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // absent takes no arguments
        ConsumeStatementTerminator();

        return new AbsentStatement(Location: location);
    }

    /// <summary>
    /// Parses a break statement for exiting loops.
    /// Syntax: <c>break</c>
    /// </summary>
    /// <returns>A <see cref="BreakStatement"/> AST node.</returns>
    private Statement ParseBreakStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new BreakStatement(Location: location);
    }

    /// <summary>
    /// Parses a continue statement for skipping to next loop iteration.
    /// Syntax: <c>continue</c>
    /// </summary>
    /// <returns>A <see cref="ContinueStatement"/> AST node.</returns>
    private Statement ParseContinueStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new ContinueStatement(Location: location);
    }

    /// <summary>
    /// Parses a pass statement (empty statement placeholder).
    /// Syntax: <c>pass</c>
    /// Used as a placeholder in empty blocks or protocols.
    /// </summary>
    /// <returns>A <see cref="PassStatement"/> AST node.</returns>
    private PassStatement ParsePassStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new PassStatement(Location: location);
    }

    /// <summary>
    /// Parses an indented block of statements.
    /// Expects INDENT token, parses statements until DEDENT.
    /// This is core to Suflae's Python-like indentation syntax.
    /// </summary>
    /// <returns>A <see cref="BlockStatement"/> containing the parsed statements.</returns>
    private Statement ParseIndentedBlock()
    {
        SourceLocation location = GetLocation();
        var statements = new List<Statement>();

        // Consume newline after colon
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after ':'");

        // Must have an indent token for a proper indented block
        if (!Check(type: TokenType.Indent))
        {
            throw ThrowParseError("Expected indented block after ':'");
        }

        // Process the indent token
        ProcessIndentToken();

        // Parse statements until we hit a dedent
        while (!Check(type: TokenType.Dedent) && !IsAtEnd)
        {
            // Skip empty lines
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            Statement stmt = ParseStatement();
            statements.Add(item: stmt);
        }

        // Process dedent tokens
        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }
        else if (!IsAtEnd)
        {
            throw ThrowParseError("Expected dedent to close indented block");
        }

        return new BlockStatement(Statements: statements, Location: location);
    }

    /// <summary>
    /// Parses an expression statement (expression followed by newline).
    /// Used for function calls, assignments, and other expressions at statement level.
    /// </summary>
    /// <returns>An <see cref="ExpressionStatement"/> AST node.</returns>
    private ExpressionStatement ParseExpressionStatement()
    {
        Expression expr = ParseExpression();
        ConsumeStatementTerminator();
        return new ExpressionStatement(Expression: expr, Location: expr.Location);
    }

    /// <summary>
    /// Parses record/entity destructuring in let/var bindings.
    /// Syntax: <c>let (field, field2) = expr</c> or <c>let (field: alias, field2: alias2) = expr</c>
    /// or nested: <c>let ((x, y), radius) = circle</c>
    /// Destructuring only works for types where ALL fields are public.
    /// </summary>
    /// <returns>A <see cref="DestructuringStatement"/> AST node.</returns>
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
    /// Parses a using statement for resource management.
    /// Syntax: <c>using resource_expr as name:</c> followed by indented body.
    /// Similar to Python's 'with' statement or C#'s 'using' statement.
    /// </summary>
    /// <remarks>
    /// The resource is acquired when entering the block and automatically released when exiting.
    /// <code>
    /// using open("file.txt") as file:
    ///     let content = file.read_all()
    ///     process(content)
    /// # file is automatically closed here
    /// </code>
    /// </remarks>
    /// <returns>A <see cref="UsingStatement"/> AST node.</returns>
    private Statement ParseUsingStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // Parse resource expression
        Expression resource = ParseExpression();

        // Expect 'as'
        Consume(type: TokenType.As, errorMessage: "Expected 'as' after resource expression in using statement");

        // Parse the binding name
        string name = ConsumeIdentifier(errorMessage: "Expected identifier after 'as' in using statement");

        // Expect ':' and indented body
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after using statement");
        Statement body = ParseIndentedBlock();

        return new UsingStatement(Resource: resource, Name: name, Body: body, Location: location);
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
