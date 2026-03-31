using SyntaxTree;
using Compiler.Lexer;
using SemanticAnalysis.Enums;
using Compiler.Diagnostics;

namespace Compiler.Parser;

/// <summary>
/// Partial class containing statement parsing (if, while, for, when, return, using, release, danger!, etc.).
/// Handles both RazorForge and Suflae syntax via <c>_language</c> dispatch.
/// </summary>
public partial class Parser
{
    // ═══════════════════════════════════════════════════════════════════════════════
    // CONTROL FLOW
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses an if statement with optional elseif/else chains.
    /// Syntax: <c>if condition</c> followed by indented body
    /// </summary>
    /// <remarks>
    /// Parsing phases:
    ///
    /// PHASE 1: INITIAL IF
    ///   - Parse condition expression
    ///   - Parse body (indented block or brace block)
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
        Statement thenBranch = ParseBody();

        Statement? elseBranch = null;

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 2: ELSEIF CHAIN (convert to nested if-else)
        // ═══════════════════════════════════════════════════════════════════════════
        while (Match(type: TokenType.Elseif))
        {
            SourceLocation elseifLocation = GetLocation(token: PeekToken(offset: -1));
            Expression elseifCondition = ParseExpression();
            Statement elseifBranch = ParseBody();

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

        Statement finalElse = ParseBody();

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
    /// Syntax: <c>unless condition</c> = <c>if not condition</c>, followed by indented body.
    /// </summary>
    /// <returns>An <see cref="IfStatement"/> with negated condition.</returns>
    private Statement ParseUnlessStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression condition = ParseExpression();
        Statement thenBranch = ParseBody();
        Statement? elseBranch = null;

        if (Match(type: TokenType.Else))
        {
            elseBranch = ParseBody();
        }

        // Unless is "if not condition"
        var negatedCondition = new UnaryExpression(Operator: UnaryOperator.Not,
            Operand: condition,
            Location: condition.Location);

        return new IfStatement(Condition: negatedCondition,
            ThenStatement: thenBranch,
            ElseStatement: elseBranch,
            Location: location);
    }

    /// <summary>
    /// Parses a while loop statement.
    /// Syntax: <c>while condition</c> followed by indented body
    /// Optional <c>else</c> block executes if the loop completes without hitting a break.
    /// </summary>
    /// <returns>A <see cref="WhileStatement"/> AST node.</returns>
    private Statement ParseWhileStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression condition = ParseExpression();
        Statement body = ParseBody();

        // Check for else clause (runs if loop completes without break)
        Statement? elseBranch = null;
        if (Match(type: TokenType.Else))
        {
            elseBranch = ParseBody();
        }

        return new WhileStatement(Condition: condition,
            Body: body,
            ElseBranch: elseBranch,
            Location: location);
    }

    /// <summary>
    /// Parses a loop statement (infinite loop).
    /// Syntax: <c>loop</c> followed by indented body
    /// Equivalent to <c>while true</c>.
    /// </summary>
    /// <returns>A <see cref="WhileStatement"/> AST node with true condition.</returns>
    private Statement ParseLoopStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // loop body is equivalent to while true body
        Expression trueCondition =
            new LiteralExpression(Value: true, LiteralType: TokenType.True, Location: location);
        Statement body = ParseBody();

        return new WhileStatement(Condition: trueCondition,
            Body: body,
            ElseBranch: null,
            Location: location);
    }

    /// <summary>
    /// Parses a for-in loop statement.
    /// Syntax: <c>for variable in iterable</c> or <c>for (a, b) in iterable</c> followed by body.
    /// Optional <c>else</c> block executes if loop completes without break.
    /// </summary>
    /// <returns>A <see cref="ForStatement"/> AST node.</returns>
    private Statement ParseForStatement()
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
        Expression sequenceable = ParseExpression();
        Statement body = ParseBody();

        // Check for else clause (runs if loop completes without break)
        Statement? elseBranch = null;
        if (Match(type: TokenType.Else))
        {
            elseBranch = ParseBody();
        }

        return new ForStatement(Variable: variable,
            VariablePattern: variablePattern,
            Iterable: sequenceable,
            Body: body,
            ElseBranch: elseBranch,
            Location: location);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // WHEN (PATTERN MATCHING)
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a when (pattern matching) statement.
    /// Mandatory <c>=&gt;</c> on all arms.
    /// Both languages use indentation-based arms.
    /// </summary>
    /// <remarks>
    /// Two forms of when statements:
    /// 1. Subject-based: when expr { is Type =&gt; ..., LITERAL =&gt; ..., else =&gt; ... }
    /// 2. Condition-based (RF only): when { condition1 =&gt; ..., condition2 =&gt; ..., else =&gt; ... }
    ///
    /// Pattern types supported:
    /// - 'else' / 'else varName' - default case (wildcard or binding)
    /// - '_' - explicit wildcard
    /// - 'is Type' / 'is Type varName' - type pattern with optional binding
    /// - 'is Type (field1, field2)' - destructuring pattern
    /// - 'isnot Type' - negated type pattern
    /// - 'isonly FLAG' - exact flags pattern
    /// - comparison operators (==, !=, &lt;, &gt;, &lt;=, &gt;=, ===, !==)
    /// - literal values (42, "hello", true)
    /// - expression patterns (for condition-based when)
    /// </remarks>
    /// <returns>A <see cref="WhenStatement"/> AST node.</returns>
    private WhenStatement ParseWhenStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 1: Determine when form (subject-based vs condition-based)
        // ═══════════════════════════════════════════════════════════════════════════
        // Subject-based:    when value { is Type => ... }     (RF)
        //                   when value\n    is Type => ...     (SF)
        // Condition-based:  when { x > 0 => ... }             (RF only)
        //                   when true { x > 0 => ... }        (RF only)
        // ═══════════════════════════════════════════════════════════════════════════

        bool isConditionBased = false;
        Expression expression;

        // Check for condition-based forms:
        // 1. `when true\n` - explicit condition-based
        // 2. `when\n` - bare when (no subject) = condition-based
        if (Check(type: TokenType.Newline))
        {
            // Bare `when` followed by newline — condition-based with no subject
            isConditionBased = true;
            expression = new LiteralExpression(Value: true,
                LiteralType: TokenType.True,
                Location: location);
        }
        else if (Check(type: TokenType.True))
        {
            // Peek ahead to see if this is `when true\n  INDENT` (condition-based)
            // vs `when true_var\n  INDENT` (subject-based with a variable named starting with true)
            Token nextToken = PeekToken(offset: 1);
            if (nextToken.Type == TokenType.Newline)
            {
                isConditionBased = true;
                Advance(); // consume 'true'
                expression = new LiteralExpression(Value: true,
                    LiteralType: TokenType.True,
                    Location: location);
            }
            else
            {
                expression = ParseExpression();
            }
        }
        else
        {
            expression = ParseExpression();
        }

        // Indentation-delimited when block for both languages
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after when expression");

        if (!Check(type: TokenType.Indent))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedIndentedBlock,
                message: "Expected indented block after when");
        }

        ProcessIndentToken();

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 2: Parse clauses (pattern => body)
        // ═══════════════════════════════════════════════════════════════════════════

        var clauses = new List<WhenClause>();

        bool AtClauseEnd()
        {
            return Check(type: TokenType.Dedent) || IsAtEnd;
        }

        while (!AtClauseEnd())
        {
            // Skip newlines between clauses
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
            if (Match(type: TokenType.Else))
            {
                // Check for variable binding: else varName => ... or else varName\n INDENT
                if (Check(type: TokenType.Identifier))
                {
                    TokenType nextAfterIdent = PeekToken(offset: 1)
                       .Type;
                    if (nextAfterIdent is TokenType.FatArrow or TokenType.Newline)
                    {
                        string varName =
                            ConsumeIdentifier(errorMessage: "Expected variable name after 'else'");
                        pattern = new ElsePattern(VariableName: varName, Location: clauseLocation);
                    }
                    else
                    {
                        pattern = new ElsePattern(VariableName: null, Location: clauseLocation);
                    }
                }
                else
                {
                    // Plain else without variable binding
                    pattern = new ElsePattern(VariableName: null, Location: clauseLocation);
                }
            }
            // Case 2: Condition-based when (RF only) - parse full expression as pattern
            else if (isConditionBased)
            {
                Expression condExpr = ParseExpression();
                pattern = new ExpressionPattern(Expression: condExpr, Location: clauseLocation);
            }
            // Case 3: 'is' keyword - type pattern
            else if (Match(type: TokenType.Is))
            {
                _inWhenPatternContext = true;
                // Check if this is a flags pattern: identifier followed by and/or/but
                if (Check(type: TokenType.Identifier) && PeekToken(offset: 1)
                       .Type is TokenType.And or TokenType.Or or TokenType.But)
                {
                    pattern = ParseFlagsIsWhenPattern();
                }
                // 'is' must be followed by a type/variant name
                else if (Check(type: TokenType.None) || Check(type: TokenType.Identifier))
                {
                    pattern = ParseTypePattern();
                }
                else
                {
                    throw ThrowParseError(code: GrammarDiagnosticCode.InvalidPattern,
                        message:
                        $"'is' must be followed by a type name. For value comparisons, use '== {CurrentToken.Text}' instead of 'is {CurrentToken.Text}'.");
                }

                _inWhenPatternContext = false;
            }
            // Case 4: 'isnot' keyword - negated type pattern (no variable binding)
            else if (Match(type: TokenType.IsNot))
            {
                _inWhenPatternContext = true;
                if (Check(type: TokenType.None) || Check(type: TokenType.Identifier))
                {
                    TypeExpression type = ParseType();
                    pattern = new NegatedTypePattern(Type: type, Location: clauseLocation);
                }
                else
                {
                    throw ThrowParseError(code: GrammarDiagnosticCode.InvalidPattern,
                        message: "'isnot' must be followed by a type name.");
                }

                _inWhenPatternContext = false;
            }
            // Case 5: 'isonly' keyword - exact flags pattern
            else if (Match(type: TokenType.IsOnly))
            {
                _inWhenPatternContext = true;
                var flagNames = new List<string>();
                flagNames.Add(
                    item: ConsumeIdentifier(errorMessage: "Expected flag name after 'isonly'"));
                while (Match(type: TokenType.And))
                {
                    flagNames.Add(
                        item: ConsumeIdentifier(errorMessage: "Expected flag name after 'and'"));
                }

                pattern = new FlagsPattern(FlagNames: flagNames,
                    Connective: FlagsTestConnective.And,
                    ExcludedFlags: null,
                    IsExact: true,
                    Location: clauseLocation);
                _inWhenPatternContext = false;
            }
            // Case 6: Comparison patterns (==, !=, <, >, <=, >=, ===, !==)
            else if (IsComparisonOperator(tokenType: CurrentToken.Type))
            {
                pattern = ParseComparisonPattern();
            }
            // Case 7: Other patterns (wildcards, literals, identifiers)
            else
            {
                // Set context flag to prevent single-param lambdas from being parsed
                // inside when patterns (e.g., a < b => action should not treat b => action as lambda)
                _inWhenPatternContext = true;
                pattern = ParsePattern();
                _inWhenPatternContext = false;
            }

            // ─────────────────────────────────────────────────────────────────────
            // Arm body: either `=> expression` (single-line) or indented block
            // ─────────────────────────────────────────────────────────────────────

            Statement body;
            _inWhenClauseBody = true;
            if (Match(type: TokenType.FatArrow))
            {
                // After =>, check for block form: => \n INDENT block DEDENT
                if (Check(type: TokenType.Newline) && PeekToken(offset: 1)
                       .Type == TokenType.Indent)
                {
                    Advance(); // consume newline
                    body = ParseIndentedBlock();
                }
                else if (Match(type: TokenType.Pass))
                {
                    // Single-line pass: pattern => pass
                    body = new PassStatement(Location: GetLocation());
                }
                else
                {
                    // Single-line form: pattern => statement (break, return, expression, etc.)
                    body = ParseStatement();
                }
            }
            else
            {
                Consume(type: TokenType.FatArrow,
                    errorMessage: "Expected '=>' after when pattern");
                body = ParseStatement();
            }

            _inWhenClauseBody = false;

            clauses.Add(
                item: new WhenClause(Pattern: pattern, Body: body, Location: GetLocation()));

            // Optional comma or newline between clauses
            Match(TokenType.Comma, TokenType.Newline);
        }

        // Close the when block (indentation-based)
        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }
        else if (!IsAtEnd)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedDedent,
                message: "Expected dedent after when clauses");
        }

        return new WhenStatement(Expression: expression, Clauses: clauses, Location: location);
    }

    /// <summary>
    /// Parses a flags pattern in a when clause after 'is' when the next tokens
    /// indicate a flags chain (identifier followed by and/or/but).
    /// Examples: is READ and WRITE =&gt; ..., is READ or WRITE =&gt; ...
    /// </summary>
    private FlagsPattern ParseFlagsIsWhenPattern()
    {
        SourceLocation loc = GetLocation();
        var flags = new List<string>();
        flags.Add(item: ConsumeIdentifier(errorMessage: "Expected flag name after 'is'"));

        FlagsTestConnective connective = FlagsTestConnective.And;
        List<string>? excluded = null;

        if (Check(type: TokenType.And))
        {
            while (Match(type: TokenType.And))
            {
                flags.Add(item: ConsumeIdentifier(errorMessage: "Expected flag name after 'and'"));
            }

            if (Match(type: TokenType.But))
            {
                excluded = new List<string>();
                excluded.Add(
                    item: ConsumeIdentifier(errorMessage: "Expected flag name after 'but'"));
                while (Match(type: TokenType.And))
                {
                    excluded.Add(
                        item: ConsumeIdentifier(errorMessage: "Expected flag name after 'and'"));
                }
            }
        }
        else if (Check(type: TokenType.Or))
        {
            connective = FlagsTestConnective.Or;
            while (Match(type: TokenType.Or))
            {
                flags.Add(item: ConsumeIdentifier(errorMessage: "Expected flag name after 'or'"));
            }
        }
        else if (Check(type: TokenType.But))
        {
            if (Match(type: TokenType.But))
            {
                excluded = new List<string>();
                excluded.Add(
                    item: ConsumeIdentifier(errorMessage: "Expected flag name after 'but'"));
                while (Match(type: TokenType.And))
                {
                    excluded.Add(
                        item: ConsumeIdentifier(errorMessage: "Expected flag name after 'and'"));
                }
            }
        }

        return new FlagsPattern(FlagNames: flags,
            Connective: connective,
            ExcludedFlags: excluded,
            IsExact: false,
            Location: loc);
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
                if (Match(type: TokenType.Identifier))
                {
                    name += "." + PeekToken(offset: -1)
                       .Text;
                }
                else
                {
                    throw ThrowParseError(
                        code: GrammarDiagnosticCode.ExpectedDotInQualifiedPattern,
                        message: "Expected identifier after '.' in pattern");
                }
            }

            // Check for destructuring: Type.CASE (memberVar1, memberVar2), (memberVar: alias), or ((x, y), z)
            List<DestructuringBinding>? bindings = null;
            if (Match(type: TokenType.LeftParen))
            {
                bindings = ParseDestructuringBindingList();
                Consume(type: TokenType.RightParen,
                    errorMessage: "Expected ')' after destructuring bindings");
            }

            // Check for variable binding (only if no destructuring)
            string? variableName = null;
            if (bindings == null && Check(type: TokenType.Identifier))
            {
                variableName =
                    ConsumeIdentifier(errorMessage: "Expected variable name for type pattern");
            }

            TypeExpression type = new(Name: name, GenericArguments: null, Location: location);
            Pattern typePattern = new TypePattern(Type: type,
                VariableName: variableName,
                Bindings: bindings,
                Location: location);
            return TryParseGuard(innerPattern: typePattern, location: location);
        }

        // Literal pattern: constants like 42, "hello", true, etc.
        Expression expr = ParsePrimary();
        if (expr is LiteralExpression literal)
        {
            Pattern litPattern = new LiteralPattern(Value: literal.Value,
                LiteralType: literal.LiteralType,
                Location: location);
            return TryParseGuard(innerPattern: litPattern, location: location);
        }

        // Otherwise, treat as expression pattern
        return new ExpressionPattern(Expression: expr, Location: location);
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
    /// Parses a type pattern (used after 'is' keyword).
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
            var noneType =
                new TypeExpression(Name: "None", GenericArguments: null, Location: location);
            Pattern nonePattern = new TypePattern(Type: noneType,
                VariableName: null,
                Bindings: null,
                Location: location);
            return TryParseGuard(innerPattern: nonePattern, location: location);
        }

        if (!Check(type: TokenType.Identifier))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedPattern,
                message: $"Expected type name after 'is', got {CurrentToken.Type}");
        }

        string name = CurrentToken.Text;
        Advance();

        // Check for qualified name: Type.CASE or Type.CASE.SubCase
        while (Match(type: TokenType.Dot))
        {
            if (Match(type: TokenType.Identifier))
            {
                name += "." + PeekToken(offset: -1)
                   .Text;
            }
            else
            {
                throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedDotInQualifiedPattern,
                    message: "Expected identifier after '.' in pattern");
            }
        }

        // Check for destructuring: Type.CASE (memberVar1, memberVar2), (memberVar: alias), or ((x, y), z)
        List<DestructuringBinding>? bindings = null;
        if (Match(type: TokenType.LeftParen))
        {
            bindings = ParseDestructuringBindingList();
            Consume(type: TokenType.RightParen,
                errorMessage: "Expected ')' after destructuring bindings");
        }

        // Check for variable binding (only if no destructuring)
        string? variableName = null;
        if (bindings == null && Check(type: TokenType.Identifier))
        {
            variableName =
                ConsumeIdentifier(errorMessage: "Expected variable name for type pattern");
        }

        var type = new TypeExpression(Name: name, GenericArguments: null, Location: location);
        Pattern typePattern = new TypePattern(Type: type,
            VariableName: variableName,
            Bindings: bindings,
            Location: location);
        return TryParseGuard(innerPattern: typePattern, location: location);
    }

    /// <summary>
    /// Checks if the given token type is a comparison operator used in when patterns.
    /// Supported operators: ==, !=, &lt;, &gt;, &lt;=, &gt;=, ===, !==
    /// </summary>
    /// <param name="tokenType">The token type to check.</param>
    /// <returns>True if the token is a comparison operator for patterns.</returns>
    private static bool IsComparisonOperator(TokenType tokenType)
    {
        return tokenType is TokenType.Equal or TokenType.NotEqual or TokenType.Less
            or TokenType.Greater or TokenType.LessEqual or TokenType.GreaterEqual
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
                string memberName =
                    ConsumeIdentifier(errorMessage: "Expected member name after '.'");
                value = new MemberExpression(Object: value,
                    PropertyName: memberName,
                    Location: GetLocation());
            }
            else if (Match(type: TokenType.LeftParen))
            {
                List<Expression> args = ParseArgumentList();
                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");
                value = new CallExpression(Callee: value,
                    Arguments: args,
                    Location: GetLocation());
            }
        }

        _inWhenPatternContext = false;

        var pattern = new ComparisonPattern(Operator: op, Value: value, Location: location);
        return TryParseGuard(innerPattern: pattern, location: location);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // JUMP STATEMENTS
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a return statement.
    /// Syntax: <c>return</c> or <c>return expression</c>
    /// </summary>
    /// <returns>A <see cref="ReturnStatement"/> AST node.</returns>
    private Statement ParseReturnStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression? value = null;
        // Check for both Newline and Dedent/RightBrace - either can follow a valueless return
        if (!Check(type: TokenType.Newline) && !Check(type: TokenType.Dedent) &&
            !Check(type: TokenType.RightBrace) && !IsAtEnd)
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

        // throw requires an error expression (Crashable type)
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
            throw new GrammarException(code: GrammarDiagnosticCode.DiscardRequiresCall,
                message: "The 'discard' keyword must be followed by a routine call. " +
                         "Use 'discard routine_call()' to explicitly ignore a return value.",
                fileName: fileName,
                line: location.Line,
                column: location.Column,
                language: _language);
        }

        ConsumeStatementTerminator();
        return new DiscardStatement(Expression: expression, Location: location);
    }

    /// <summary>
    /// Parses an emit statement (generator yield).
    /// Syntax: <c>emit expression</c>
    /// Yields a value from a generator routine.
    /// </summary>
    /// <returns>An <see cref="EmitStatement"/> AST node.</returns>
    private EmitStatement ParseEmitStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        Expression expression = ParseExpression();
        ConsumeStatementTerminator();
        return new EmitStatement(Expression: expression, Location: location);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // RESOURCE MANAGEMENT
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a using block for scoped resource management (P17).
    /// Syntax: <c>using expr as name</c> with indented body.
    /// Multi-resource: <c>using expr1 as name1, expr2 as name2</c> (desugars to nested blocks).
    /// </summary>
    /// <remarks>
    /// <code>
    /// using open("file.txt") as file
    ///   var content = file.read_all()
    ///   process(content)
    ///   # file is automatically closed at block exit
    /// </code>
    /// </remarks>
    /// <returns>A <see cref="UsingStatement"/> AST node (possibly nested for multi-resource).</returns>
    private Statement ParseUsingStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // Parse resource-binding pairs: using expr as name [, expr as name]*
        var resources = new List<(Expression Resource, string Name)>();

        do
        {
            Expression resource = ParseExpression();
            Consume(type: TokenType.As,
                errorMessage: "Expected 'as' after resource expression in using block");
            string name = ConsumeIdentifier(errorMessage: "Expected binding name after 'as'");
            resources.Add(item: (resource, name));
        } while (Match(type: TokenType.Comma));

        // Parse the indented body
        Statement body = ParseBody();

        // Build nested UsingStatements from inside out (last resource is innermost)
        Statement result = body;
        for (int i = resources.Count - 1; i >= 0; i--)
        {
            result = new UsingStatement(Resource: resources[index: i].Resource,
                Name: resources[index: i].Name,
                Body: result,
                Location: location);
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // RF-ONLY CONSTRUCTS
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a danger! statement (unsafe memory operations block).
    /// RF-only construct guarded by <c>_language == Language.RazorForge</c>.
    /// Syntax: <c>danger!</c> followed by indented body.
    /// </summary>
    /// <returns>A <see cref="DangerStatement"/> AST node.</returns>
    private DangerStatement ParseDangerStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // 'danger!' is tokenized as a single Danger token (including the '!')
        var body = (BlockStatement)ParseIndentedBlock();

        return new DangerStatement(Body: body, Location: location);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // BLOCK / BODY PARSING
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a block body using indentation (indent/dedent delimited).
    /// Both RazorForge and Suflae use indentation-based syntax.
    /// </summary>
    /// <returns>A <see cref="BlockStatement"/> AST node.</returns>
    private Statement ParseBody()
    {
        return ParseIndentedBlock();
    }

    /// <summary>
    /// Parses an indented block of statements.
    /// Expects INDENT token, parses statements until DEDENT.
    /// </summary>
    /// <returns>A <see cref="BlockStatement"/> containing the parsed statements.</returns>
    private Statement ParseIndentedBlock()
    {
        SourceLocation location = GetLocation();
        var statements = new List<Statement>();

        // Consume newline before indent (optional if we're already at Indent)
        if (Check(type: TokenType.Newline))
        {
            Advance(); // consume newline
        }
        else if (!Check(type: TokenType.Indent))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedIndentedBlock,
                message: "Expected newline before indented block");
        }

        // Must have an indent token for a proper indented block
        if (!Check(type: TokenType.Indent))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedIndentedBlock,
                message: "Expected indented block");
        }

        // Process the indent token
        ProcessIndentToken();

        // Parse statements until we hit a dedent
        while (!Check(type: TokenType.Dedent) && !IsAtEnd)
        {
            // Skip empty lines and doc comments (indentation handler doesn't emit
            // Dedent for comment-only lines, so doc comments at lower indent may appear here)
            if (Match(TokenType.Newline, TokenType.DocComment))
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
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedDedent,
                message: "Expected dedent to close indented block");
        }

        return new BlockStatement(Statements: statements, Location: location);
    }

    /// <summary>
    /// Parses an expression statement (expression followed by newline/terminator).
    /// Used for function calls, assignments, and other expressions at statement level.
    /// </summary>
    /// <returns>An <see cref="ExpressionStatement"/> AST node.</returns>
    private ExpressionStatement ParseExpressionStatement()
    {
        Expression expr = ParseExpression();
        ConsumeStatementTerminator();
        return new ExpressionStatement(Expression: expr, Location: expr.Location);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // DESTRUCTURING
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses record/entity destructuring in var bindings.
    /// Syntax: <c>var (memberVar, memberVar2) = expr</c> or <c>var (memberVar: alias, memberVar2: alias2) = expr</c>
    /// or nested: <c>var ((x, y), radius) = circle</c>
    /// Destructuring only works for types where ALL member variables are public.
    /// </summary>
    /// <returns>A <see cref="DestructuringStatement"/> AST node.</returns>
    private DestructuringStatement ParseDestructuringDeclaration()
    {
        SourceLocation
            location =
                GetLocation(token: PeekToken(offset: -2)); // -2 because we already consumed 'var'

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
            Location: location);
    }
}
