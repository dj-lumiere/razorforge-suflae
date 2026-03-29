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
    private Expression ParseAssignment()
    {
        Expression expr = ParseWith();
        Expression value;
        // Check for simple assignment
        if (Match(type: TokenType.Assign))
        {
            value = ParseAssignment();
            return new BinaryExpression(Left: expr,
                Operator: BinaryOperator.Assign,
                Right: value,
                Location: expr.Location);
        }

        // Check for compound assignment operators
        BinaryOperator? compoundOp = TryMatchCompoundAssignment();
        if (!compoundOp.HasValue)
        {
            return expr;
        }

        value = ParseAssignment();

        // Base operators with in-place wired support -> CompoundAssignmentExpression
        if (compoundOp.Value.GetInPlaceMethodName() != null)
        {
            return new CompoundAssignmentExpression(
                Target: expr,
                Operator: compoundOp.Value,
                Value: value,
                Location: expr.Location);
        }

        // Overflow variants and ??= -> expand to a = a op b
        Expression binaryExpr = new BinaryExpression(
            Left: expr,
            Operator: compoundOp.Value,
            Right: value,
            Location: expr.Location);
        return new BinaryExpression(Left: expr,
            Operator: BinaryOperator.Assign,
            Right: binaryExpr,
            Location: expr.Location);
    }

    /// <summary>
    /// Tries to match a compound assignment operator and returns the corresponding binary operator.
    /// Returns null if the current token is not a compound assignment.
    /// </summary>
    private BinaryOperator? TryMatchCompoundAssignment()
    {
        TokenType current = CurrentToken.Type;
        BinaryOperator? op = current switch
        {
            TokenType.PlusAssign => BinaryOperator.Add,
            TokenType.MinusAssign => BinaryOperator.Subtract,
            TokenType.StarAssign => BinaryOperator.Multiply,
            TokenType.SlashAssign => BinaryOperator.TrueDivide,
            TokenType.DivideAssign => BinaryOperator.FloorDivide,
            TokenType.PercentAssign => BinaryOperator.Modulo,
            TokenType.PowerAssign => BinaryOperator.Power,
            TokenType.AmpersandAssign => BinaryOperator.BitwiseAnd,
            TokenType.PipeAssign => BinaryOperator.BitwiseOr,
            TokenType.CaretAssign => BinaryOperator.BitwiseXor,
            TokenType.LeftShiftAssign => BinaryOperator.ArithmeticLeftShift,
            TokenType.RightShiftAssign => BinaryOperator.ArithmeticRightShift,
            TokenType.LogicalLeftShiftAssign => BinaryOperator.LogicalLeftShift,
            TokenType.LogicalRightShiftAssign => BinaryOperator.LogicalRightShift,
            TokenType.NoneCoalesceAssign => BinaryOperator.NoneCoalesce,
            // Overflow variant compound assignments
            TokenType.PlusWrapAssign => BinaryOperator.AddWrap,
            TokenType.MinusWrapAssign => BinaryOperator.SubtractWrap,
            TokenType.MultiplyWrapAssign => BinaryOperator.MultiplyWrap,
            TokenType.PowerWrapAssign => BinaryOperator.PowerWrap,
            TokenType.PlusClampAssign => BinaryOperator.AddClamp,
            TokenType.MinusClampAssign => BinaryOperator.SubtractClamp,
            TokenType.MultiplyClampAssign => BinaryOperator.MultiplyClamp,
            TokenType.SlashClampAssign => BinaryOperator.TrueDivClamp,
            TokenType.PowerClampAssign => BinaryOperator.PowerClamp,
            _ => null
        };

        if (op.HasValue)
        {
            Advance(); // Consume the compound assignment token
        }

        return op;
    }

    /// <summary>
    /// Parses with expressions (lowest precedence operator).
    /// Syntax: <c>expr with .memberVar = value, .nested.memberVar = value, [index] = value</c>
    /// </summary>
    /// <returns>The parsed expression, possibly a with expression.</returns>
    private Expression ParseWith()
    {
        Expression expr = ParseInlineConditional();

        if (Match(type: TokenType.With))
        {
            SourceLocation withLocation = GetLocation(token: PeekToken(offset: -1));
            var updates = new List<(List<string>? MemberVariablePath, Expression? Index, Expression Value)>();

            do
            {
                List<string>? fieldPath = null;
                Expression? indexExpr = null;

                if (Match(type: TokenType.LeftBracket))
                {
                    // Index update: [expr] = value
                    indexExpr = ParseExpression();
                    Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after index in with expression");
                }
                else if (Match(type: TokenType.Dot))
                {
                    // Member variable update: .memberVar or .memberVar.nested
                    fieldPath = new List<string>();
                    Token memberVariableToken = Consume(type: TokenType.Identifier, errorMessage: "Expected member variable name after '.' in with expression");
                    fieldPath.Add(item: memberVariableToken.Text);

                    // Parse nested member variable path: .address.city
                    while (Check(type: TokenType.Dot) && PeekToken(offset: 1).Type == TokenType.Identifier)
                    {
                        Advance(); // consume dot
                        Token nestedToken = Consume(type: TokenType.Identifier, errorMessage: "Expected member variable name in with expression");
                        fieldPath.Add(item: nestedToken.Text);
                    }
                }
                else
                {
                    throw new GrammarException(
                        GrammarDiagnosticCode.UnexpectedToken,
                        "Expected '.' or '[' in with expression",
                        fileName, CurrentToken.Line, CurrentToken.Column,
                        _language);
                }

                Consume(type: TokenType.Assign, errorMessage: "Expected '=' after member variable or index in with expression");
                Expression value = ParseInlineConditional();
                updates.Add(item: (fieldPath, indexExpr, value));
            } while (Match(type: TokenType.Comma));

            expr = new WithExpression(Base: expr, Updates: updates, Location: withLocation);
        }

        return expr;
    }

    /// <summary>
    /// Parses inline conditional expressions.
    /// Syntax: <c>if condition then thenExpr else elseExpr</c>
    /// Note: This is expression-level only; block-level if uses ParseIfStatement.
    /// Nested inline conditionals are forbidden for readability.
    /// </summary>
    /// <returns>The parsed expression, possibly a conditional.</returns>
    private Expression ParseInlineConditional()
    {
        // Check for inline if-then-else expression
        // Skip if already inside an inline conditional (prevents nesting for readability)
        if (_parsingInlineConditional || !Match(type: TokenType.If))
        {
            return ParseNoneCoalesce();
        }

        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // Set flag to prevent nested inline conditionals
        _parsingInlineConditional = true;

        try
        {
            Expression condition = ParseNoneCoalesce();

            Consume(type: TokenType.Then, errorMessage: "Expected 'then' after condition in inline if");
            Expression thenExpr = ParseExpression(); // Full expression, but flag prevents nested inline if

            Consume(type: TokenType.Else, errorMessage: "Expected 'else' in inline if expression");
            Expression elseExpr = ParseExpression(); // Full expression, but flag prevents nested inline if

            return new ConditionalExpression(Condition: condition,
                TrueExpression: thenExpr,
                FalseExpression: elseExpr,
                Location: location);
        }
        finally
        {
            _parsingInlineConditional = false;
        }
    }

    /// <summary>
    /// Parses none-coalescing expressions.
    /// Syntax: <c>a ?? b</c>
    /// Returns a if a is not None, otherwise returns b.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseNoneCoalesce()
    {
        Expression expr = ParseLogicalOr();

        while (Match(type: TokenType.NoneCoalesce))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseLogicalOr();
            expr = new BinaryExpression(Left: expr,
                Operator: BinaryOperator.NoneCoalesce,
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses logical OR expressions.
    /// Syntax: <c>a or b</c>
    /// Left-associative, short-circuit evaluation.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseLogicalOr()
    {
        Expression expr = ParseRange();

        while (Match(TokenType.Or, TokenType.But))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseRange();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses range expressions.
    /// Syntax: <c>start to end</c>, <c>start to end by step</c>,
    /// <c>start downto end</c>, or <c>start downto end by step</c>
    /// Used for iteration ranges and slicing.
    /// </summary>
    /// <returns>The parsed expression, possibly a range.</returns>
    private Expression ParseRange()
    {
        Expression expr = ParseLogicalAnd();

        // Handle ascending range expressions: A to B or A to B by C
        if (Match(type: TokenType.To))
        {
            Expression end = ParseLogicalAnd();
            Expression? step = null;

            if (Match(type: TokenType.By))
            {
                step = ParseLogicalAnd();
            }

            return new RangeExpression(Start: expr,
                End: end,
                Step: step,
                IsDescending: false,
                Location: expr.Location);
        }

        // Handle exclusive range expressions: A til B or A til B by C
        if (Match(type: TokenType.Til))
        {
            Expression end = ParseLogicalAnd();
            Expression? step = null;

            if (Match(type: TokenType.By))
            {
                step = ParseLogicalAnd();
            }

            return new RangeExpression(Start: expr,
                End: end,
                Step: step,
                IsDescending: false,
                Location: expr.Location,
                IsExclusive: true);
        }

        return expr;
    }

    /// <summary>
    /// Parses logical AND expressions.
    /// Syntax: <c>a and b</c>
    /// Left-associative, short-circuit evaluation.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseLogicalAnd()
    {
        Expression expr = ParseEquality();

        while (Match(type: TokenType.And))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseEquality();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses equality expressions.
    /// Syntax: <c>a != b</c>
    /// Note: <c>==</c> is handled in ParseComparison for chaining support.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseEquality()
    {
        Expression expr = ParseComparison();

        while (Match(TokenType.NotEqual, TokenType.ReferenceEqual, TokenType.ReferenceNotEqual))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseComparison();
            expr = new BinaryExpression(
                Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses comparison expressions with support for chaining.
    /// Syntax: <c>a &lt; b</c>, <c>a &lt;= b &lt;= c</c> (chained), <c>a &lt;=&gt; b</c> (three-way)
    /// Supports chained comparisons only with consistent direction:
    /// - Ascending chain: &lt;, &lt;=, == (e.g., a &lt; b &lt;= c == d)
    /// - Descending chain: &gt;, &gt;=, == (e.g., a &gt; b &gt;= c == d)
    /// </summary>
    /// <returns>The parsed expression (may be <see cref="ChainedComparisonExpression"/> for chains).</returns>
    private Expression ParseComparison()
    {
        Expression expr = ParseIsExpression();

        // Check for chained comparisons
        var operators = new List<BinaryOperator>();
        var operands = new List<Expression> { expr };

        while (Match(TokenType.Less,
                   TokenType.LessEqual,
                   TokenType.Greater,
                   TokenType.GreaterEqual,
                   TokenType.Equal,
                   TokenType.ThreeWayComparison))
        {
            Token op = PeekToken(offset: -1);
            BinaryOperator binOp = TokenToBinaryOperator(tokenType: op.Type);

            operators.Add(item: binOp);
            Expression right = ParseIsExpression();
            operands.Add(item: right);
        }

        // If we have chained comparisons, create a ChainedComparisonExpression
        // Note: Chained comparisons are NOT desugared because they need special
        // handling to evaluate middle operands only once (a < b < c)
        if (operators.Count > 1)
        {
            return new ChainedComparisonExpression(Operands: operands, Operators: operators, Location: GetLocation());
        }
        if (operators.Count == 1)
        {
            // Single comparison
            return new BinaryExpression(
                Left: operands[index: 0],
                Operator: operators[index: 0],
                Right: operands[index: 1],
                Location: GetLocation());
        }

        return expr;
    }

    /// <summary>
    /// Parses type-checking, membership, and pattern matching expressions.
    /// Syntax:
    /// - <c>expr is Type</c> - type check
    /// - <c>expr is Type binding</c> - type check with variable binding
    /// - <c>expr is Type (field1, field2)</c> - destructuring pattern
    /// - <c>expr is Type (memberVar: binding)</c> - named destructuring
    /// - <c>expr isnot Type</c> - negated type check
    /// - <c>expr in collection</c> - membership test (desugars to collection.$contains(expr))
    /// - <c>expr notin collection</c> - negated membership test
    /// - <c>expr obeys Protocol</c> - protocol conformance check
    /// - <c>expr disobeys Protocol</c> - negated protocol check
    /// Context-sensitive: disabled inside when clause bodies to avoid ambiguity.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseIsExpression()
    {
        Expression expr = ParseBitwiseOr();

        // Handle is/isnot/isonly/in/notin/obeys/disobeys expressions when not in when pattern/clause context
        while (!_inWhenPatternContext && !_inWhenClauseBody && Match(TokenType.Is,
                   TokenType.IsNot, TokenType.IsOnly,
                   TokenType.In,
                   TokenType.NotIn,
                   TokenType.Obeys,
                   TokenType.Disobeys))
        {
            Token op = PeekToken(offset: -1);
            SourceLocation location = GetLocation(token: op);

            // Handle 'isonly' -- always a flags test (exact match)
            if (op.Type == TokenType.IsOnly)
            {
                string firstFlag = ConsumeIdentifier(errorMessage: "Expected flag name after 'isonly'");
                var flags = new List<string> { firstFlag };
                while (Match(type: TokenType.And))
                    flags.Add(item: ConsumeIdentifier(errorMessage: "Expected flag name after 'and'"));
                expr = new FlagsTestExpression(Subject: expr, Kind: FlagsTestKind.IsOnly,
                    TestFlags: flags, Connective: FlagsTestConnective.And,
                    ExcludedFlags: null, Location: location);
                continue;
            }

            if (op.Type is TokenType.Is or TokenType.IsNot)
            {
                bool isNegated = op.Type == TokenType.IsNot;

                // Handle 'is None' or 'isnot None' as a special case - None is a keyword
                TypeExpression type;
                if (Match(type: TokenType.None))
                {
                    type = new TypeExpression(Name: "None", GenericArguments: null, Location: location);
                }
                else
                {
                    type = ParseType();
                }

                // Check if this is a flags test chain: identifier followed by and/or/but
                if (Check(TokenType.And) || Check(TokenType.Or) || Check(TokenType.But))
                {
                    string firstFlag = type.Name;
                    expr = ParseFlagsTestChain(subject: expr, firstFlag: firstFlag,
                        isNegated: isNegated, location: location);
                    continue;
                }

                switch (isNegated)
                {
                    // Check for destructuring pattern: is Type (...)
                    case false when Check(type: TokenType.LeftParen):
                    {
                        // Destructuring pattern: is Point (x, y) or is Point (x: a, y: b)
                        List<DestructuringBinding> bindings = ParseDestructuringBindings();
                        var pattern = new TypeDestructuringPattern(Type: type, Bindings: bindings, Location: location);
                        expr = new IsPatternExpression(Expression: expr,
                            Pattern: pattern,
                            IsNegated: false,
                            Location: location);
                        break;
                    }
                    // Check for single binding: is Type identifier (only for 'is', not 'isnot')
                    case false when Check(TokenType.Identifier) && !IsKeywordToken(token: CurrentToken):
                    {
                        string variableName = Advance()
                           .Text;
                        var pattern = new TypePattern(Type: type, VariableName: variableName, Bindings: null, Location: location);
                        expr = new IsPatternExpression(Expression: expr,
                            Pattern: pattern,
                            IsNegated: false,
                            Location: location);
                        break;
                    }
                    default:
                    {
                        // Simple type check: is Type or isnot Type
                        var pattern = new TypePattern(Type: type, VariableName: null, Bindings: null, Location: location);
                        expr = new IsPatternExpression(Expression: expr,
                            Pattern: pattern,
                            IsNegated: isNegated,
                            Location: location);
                        break;
                    }
                }
            }
            else
            {
                // Handle obeys/disobeys as binary operators
                Expression right = ParseBitwiseOr();
                expr = new BinaryExpression(Left: expr,
                    Operator: TokenToBinaryOperator(tokenType: op.Type),
                    Right: right,
                    Location: location);
            }
        }

        return expr;
    }

    /// <summary>
    /// Parses a flags test chain after the first flag name has been consumed.
    /// Called when 'and', 'or', or 'but' follows an identifier after 'is'/'isnot'.
    /// Examples: is READ and WRITE, is READ or WRITE, is READ and WRITE but EXECUTE
    /// </summary>
    private FlagsTestExpression ParseFlagsTestChain(
        Expression subject, string firstFlag, bool isNegated, SourceLocation location)
    {
        var flags = new List<string> { firstFlag };
        List<string>? excluded = null;
        FlagsTestKind kind = isNegated ? FlagsTestKind.IsNot : FlagsTestKind.Is;

        if (Match(type: TokenType.And))
        {
            // 'and' chain: READ and WRITE and ...
            flags.Add(item: ConsumeIdentifier(errorMessage: "Expected flag name after 'and'"));
            while (Match(type: TokenType.And))
                flags.Add(item: ConsumeIdentifier(errorMessage: "Expected flag name after 'and'"));

            // Optional 'but' exclusion
            if (Match(type: TokenType.But))
            {
                excluded = new List<string>();
                excluded.Add(item: ConsumeIdentifier(errorMessage: "Expected flag name after 'but'"));
                while (Match(type: TokenType.And))
                    excluded.Add(item: ConsumeIdentifier(errorMessage: "Expected flag name after 'and'"));
            }

            return new FlagsTestExpression(Subject: subject, Kind: kind,
                TestFlags: flags, Connective: FlagsTestConnective.And,
                ExcludedFlags: excluded, Location: location);
        }

        if (Match(type: TokenType.Or))
        {
            flags.Add(item: ConsumeIdentifier(errorMessage: "Expected flag name after 'or'"));
            while (Match(type: TokenType.Or))
                flags.Add(item: ConsumeIdentifier(errorMessage: "Expected flag name after 'or'"));

            return new FlagsTestExpression(Subject: subject, Kind: kind,
                TestFlags: flags, Connective: FlagsTestConnective.Or,
                ExcludedFlags: null, Location: location);
        }

        if (Match(type: TokenType.But))
        {
            // Single flag with but exclusion: is READ but WRITE
            excluded = new List<string>();
            excluded.Add(item: ConsumeIdentifier(errorMessage: "Expected flag name after 'but'"));
            while (Match(type: TokenType.And))
                excluded.Add(item: ConsumeIdentifier(errorMessage: "Expected flag name after 'and'"));

            return new FlagsTestExpression(Subject: subject, Kind: kind,
                TestFlags: flags, Connective: FlagsTestConnective.And,
                ExcludedFlags: excluded, Location: location);
        }

        // Should not reach here (caller checked for and/or/but)
        throw ThrowParseError("Expected 'and', 'or', or 'but' in flags test");
    }

    /// <summary>
    /// Parses destructuring bindings for pattern matching.
    /// Syntax: (memberVar1, memberVar2) or (memberVar: binding, ...) or nested ((x: x1, y: y1), ...)
    /// </summary>
    private List<DestructuringBinding> ParseDestructuringBindings()
    {
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' for destructuring pattern");

        var bindings = new List<DestructuringBinding>();

        do
        {
            SourceLocation bindingLocation = GetLocation();

            // Check for nested pattern: (...)
            if (Check(type: TokenType.LeftParen))
            {
                // Nested destructuring without type name - used for anonymous record member variables
                List<DestructuringBinding> nestedBindings = ParseDestructuringBindings();
                // For nested patterns without a member variable name, use index-based binding
                bindings.Add(item: new DestructuringBinding(MemberVariableName: null,
                    BindingName: "_nested",
                    NestedPattern: new DestructuringPattern(Bindings: nestedBindings, Location: bindingLocation),
                    Location: bindingLocation));
            }
            // Check for wildcard: _
            else if (Check(type: TokenType.Identifier) && CurrentToken.Text == "_")
            {
                Advance();
                bindings.Add(item: new DestructuringBinding(MemberVariableName: null,
                    BindingName: "_",
                    NestedPattern: null,
                    Location: bindingLocation));
            }
            else
            {
                // Named or positional binding
                string name = ConsumeIdentifier(errorMessage: "Expected member variable name or binding in destructuring pattern");

                if (Match(type: TokenType.Colon))
                {
                    // Named binding: memberVar: binding
                    // Check if binding is a nested pattern
                    if (Check(type: TokenType.LeftParen))
                    {
                        List<DestructuringBinding> nestedBindings = ParseDestructuringBindings();
                        bindings.Add(item: new DestructuringBinding(MemberVariableName: name,
                            BindingName: name,
                            NestedPattern: new DestructuringPattern(Bindings: nestedBindings, Location: bindingLocation),
                            Location: bindingLocation));
                    }
                    else
                    {
                        string bindingName = ConsumeIdentifier(errorMessage: "Expected binding name after ':'");
                        bindings.Add(item: new DestructuringBinding(MemberVariableName: name,
                            BindingName: bindingName,
                            NestedPattern: null,
                            Location: bindingLocation));
                    }
                }
                else
                {
                    // Positional binding: name binds to member variable of same name
                    bindings.Add(item: new DestructuringBinding(MemberVariableName: name,
                        BindingName: name,
                        NestedPattern: null,
                        Location: bindingLocation));
                }
            }
        } while (Match(type: TokenType.Comma));

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after destructuring pattern");

        return bindings;
    }

    /// <summary>
    /// Parses a list of destructuring bindings (without consuming the surrounding parentheses).
    /// Used by ParseTypePattern() for type patterns with destructuring like: is CIRCLE ((x, y), radius)
    /// </summary>
    private List<DestructuringBinding> ParseDestructuringBindingList()
    {
        var bindings = new List<DestructuringBinding>();

        if (Check(type: TokenType.RightParen))
        {
            return bindings; // Empty list
        }

        do
        {
            SourceLocation bindingLocation = GetLocation();

            // Check for nested pattern: (...)
            if (Check(type: TokenType.LeftParen))
            {
                // Nested destructuring - recursively parse with parens
                List<DestructuringBinding> nestedBindings = ParseDestructuringBindings();
                // For nested patterns without a member variable name, use null MemberVariableName
                bindings.Add(item: new DestructuringBinding(MemberVariableName: null,
                    BindingName: null,
                    NestedPattern: new DestructuringPattern(Bindings: nestedBindings, Location: bindingLocation),
                    Location: bindingLocation));
            }
            // Check for wildcard: _
            else if (Check(type: TokenType.Identifier) && CurrentToken.Text == "_")
            {
                Advance();
                bindings.Add(item: new DestructuringBinding(MemberVariableName: null,
                    BindingName: "_",
                    NestedPattern: null,
                    Location: bindingLocation));
            }
            else
            {
                // Named or positional binding
                string name = ConsumeIdentifier(errorMessage: "Expected member variable name or binding in destructuring pattern");

                if (Match(type: TokenType.Colon))
                {
                    // Named binding: memberVar: binding or memberVar: (nested)
                    if (Check(type: TokenType.LeftParen))
                    {
                        List<DestructuringBinding> nestedBindings = ParseDestructuringBindings();
                        bindings.Add(item: new DestructuringBinding(MemberVariableName: name,
                            BindingName: null,
                            NestedPattern: new DestructuringPattern(Bindings: nestedBindings, Location: bindingLocation),
                            Location: bindingLocation));
                    }
                    else
                    {
                        string bindingName = ConsumeIdentifier(errorMessage: "Expected binding name after ':'");
                        bindings.Add(item: new DestructuringBinding(MemberVariableName: name,
                            BindingName: bindingName,
                            NestedPattern: null,
                            Location: bindingLocation));
                    }
                }
                else
                {
                    // Positional binding: name binds to member variable of same name
                    bindings.Add(item: new DestructuringBinding(MemberVariableName: name,
                        BindingName: name,
                        NestedPattern: null,
                        Location: bindingLocation));
                }
            }
        } while (Match(type: TokenType.Comma));

        return bindings;
    }

    /// <summary>
    /// Checks if the current token is a keyword that should not be treated as an identifier.
    /// Used to prevent parsing keywords as binding names in pattern matching.
    /// </summary>
    private bool IsKeywordToken(Token token)
    {
        return token.Type switch
        {
            TokenType.And or TokenType.Or or TokenType.Not or TokenType.Is or TokenType.IsNot or TokenType.In or TokenType.NotIn or TokenType.Obeys or TokenType.Disobeys or TokenType.If or TokenType.Else or TokenType.While or TokenType.For or TokenType.Return or TokenType.Throw or TokenType.When or TokenType.Then or TokenType.To or TokenType.Til or TokenType.By => true,
            _ => false
        };
    }

    /// <summary>
    /// Parses bitwise OR expressions.
    /// Syntax: <c>a | b</c>
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseBitwiseOr()
    {
        Expression expr = ParseBitwiseXor();

        while (Match(type: TokenType.Pipe))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseBitwiseXor();
            expr = new BinaryExpression(
                Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses bitwise XOR expressions.
    /// Syntax: <c>a ^ b</c>
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseBitwiseXor()
    {
        Expression expr = ParseBitwiseAnd();

        while (Match(type: TokenType.Caret))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseBitwiseAnd();
            expr = new BinaryExpression(
                Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses bitwise AND expressions.
    /// Syntax: <c>a &amp; b</c>
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseBitwiseAnd()
    {
        Expression expr = ParseShift();

        while (Match(type: TokenType.Ampersand))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseShift();
            expr = new BinaryExpression(
                Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses bit shift expressions.
    /// Syntax: <c>a &lt;&lt; b</c>, <c>a &gt;&gt; b</c>.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseShift()
    {
        Expression expr = ParseAdditive();

        while (Match(TokenType.LeftShift,
                   TokenType.RightShift,
                   TokenType.LogicalLeftShift,
                   TokenType.LogicalRightShift))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseAdditive();
            expr = new BinaryExpression(
                Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses additive expressions.
    /// Syntax: <c>a + b</c>, <c>a - b</c>, and overflow variants (+%, +^, -%, -^).
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseAdditive()
    {
        Expression expr = ParseMultiplicative();

        while (Match(TokenType.Plus,
                   TokenType.Minus,
                   TokenType.PlusWrap,
                   TokenType.PlusClamp,
                   TokenType.MinusWrap,
                   TokenType.MinusClamp))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseMultiplicative();
            expr = new BinaryExpression(
                Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses multiplicative expressions.
    /// Syntax: <c>a * b</c>, <c>a / b</c>, <c>a % b</c>, and overflow variants.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseMultiplicative()
    {
        Expression expr = ParsePower();

        while (Match(TokenType.Star,
                   TokenType.Slash,
                   TokenType.Percent,
                   TokenType.Divide,
                   TokenType.MultiplyWrap,
                   TokenType.MultiplyClamp,
                   TokenType.SlashClamp))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParsePower();
            expr = new BinaryExpression(
                Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses power/exponentiation expressions.
    /// Syntax: <c>a ** b</c> and overflow variants (**%, **^).
    /// Power is right-associative: <c>a ** b ** c</c> = <c>a ** (b ** c)</c>
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParsePower()
    {
        Expression expr = ParseUnary();

        if (Match(TokenType.Power,
                   TokenType.PowerWrap,
                   TokenType.PowerClamp))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParsePower(); // Recursive call for right-associativity
            expr = new BinaryExpression(
                Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses unary prefix expressions.
    /// Syntax: <c>-x</c> (negation), <c>not x</c> (logical not), <c>~x</c> (bitwise not), <c>^n</c> (backindex).
    /// Special handling for unary minus on numeric literals to support min values.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseUnary()
    {
        // Handle steal expression (steal expr = ownership transfer, RazorForge only)
        if (Match(type: TokenType.Steal))
        {
            SourceLocation stealLocation = GetLocation(token: PeekToken(offset: -1));
            Expression operand = ParseUnary(); // Right-associative
            return new StealExpression(Operand: operand, Location: stealLocation);
        }

        // Handle backindex operator (^n = index from end)
        if (Match(type: TokenType.Caret))
        {
            SourceLocation caretLocation = GetLocation(token: PeekToken(offset: -1));
            Expression operand = ParseUnary(); // Right-associative
            return new BackIndexExpression(Operand: operand, Location: caretLocation);
        }

        if (!Match(TokenType.Minus, TokenType.Not, TokenType.Tilde))
        {
            return ParsePostfix();
        }

        Token op = PeekToken(offset: -1);
        SourceLocation opLocation = GetLocation(token: op);

        // Special handling for unary minus on numeric literals
        // This allows parsing negative min values like -9_223_372_036_854_775_808_s64
        // All numeric literals are now strings, so we prepend "-" to the string
        if (op.Type == TokenType.Minus && Check(
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
                TokenType.AddressLiteral,
                TokenType.Integer,
                TokenType.F16Literal,
                TokenType.F32Literal,
                TokenType.F64Literal,
                TokenType.F128Literal,
                TokenType.D32Literal,
                TokenType.D64Literal,
                TokenType.D128Literal,
                TokenType.Decimal,
                TokenType.J32Literal,
                TokenType.J64Literal,
                TokenType.J128Literal,
                TokenType.JnLiteral))
        {
            // Parse the literal
            Expression literal = ParsePostfix();

            // If it's a literal expression with string value, prepend negative sign
            if (literal is LiteralExpression { Value: string strVal } litExpr)
            {
                // Toggle negative sign: if already negative, remove it; otherwise add it
                string newValue = strVal.StartsWith(value: "-") ? strVal[1..] : "-" + strVal;
                return new LiteralExpression(Value: newValue, LiteralType: litExpr.LiteralType, Location: opLocation);
            }
        }

        Expression expr = ParseUnary();
        return new UnaryExpression(Operator: TokenToUnaryOperator(tokenType: op.Type), Operand: expr, Location: opLocation);
    }

    /// <summary>
    /// Parses postfix expressions: function calls, indexing, member access, and more.
    /// Syntax: <c>f(args)</c>, <c>x[index]</c>, <c>obj.member</c>, <c>obj.method!()</c>, <c>value with (memberVar: newVal)</c>
    /// Handles generic method calls: <c>func[T]()</c>, <c>obj.method[T]()</c>
    /// </summary>
    /// <remarks>
    /// Postfix operators in order of check:
    /// 1. Generic function call: func[T]() or func![T]()
    /// 2. Failable function call: func!(args)
    /// 3. Regular function call: func(args)
    /// 4. Index access: expr[index]
    /// 5. Member access: expr.member (with sub-cases for generic/failable methods)
    /// 6. Functional update: expr with (memberVar: value)
    ///
    /// Generic type arguments use '[' and ']' which don't conflict with any operators.
    /// </remarks>
    /// <returns>The parsed expression.</returns>
    private Expression ParsePostfix()
    {
        Expression expr = ParsePrimary();

        while (true)
        {
            // ===============================================================================
            // CASE 1: Generic function call - func[T]() or func![T]()
            // ===============================================================================
            // The ! must come BEFORE [ if present: func![T]() not func[T]!()
            // IsLikelyGenericAfterIdentifier() checks bracket content and what follows ]
            // to distinguish generic args from index/slice (e.g. list[5], list[0 to 5])
            if (expr is IdentifierExpression expression && IsLikelyGenericAfterIdentifier())
            {
                // Check for failable marker ! before generic parameters: func![T]
                bool isMemoryOperation = Match(type: TokenType.Bang);

                Advance(); // consume '['
                var typeArgs = new List<TypeExpression>();
                do
                {
                    typeArgs.Add(item: ParseTypeOrConstGeneric());
                } while (Match(type: TokenType.Comma));

                Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after generic type arguments");

                if (Match(type: TokenType.LeftParen))
                {
                    List<Expression> args = ParseArgumentList();
                    Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");

                    string methodName = expression.Name;
                    if (isMemoryOperation)
                    {
                        methodName += "!";
                    }

                    expr = new GenericMethodCallExpression(Object: expression,
                        MethodName: methodName,
                        TypeArguments: typeArgs,
                        Arguments: args,
                        IsMemoryOperation: isMemoryOperation,
                        Location: expression.Location);
                }
                else
                {
                    expr = new GenericMemberExpression(Object: expr,
                        MemberName: ((IdentifierExpression)expr).Name,
                        TypeArguments: typeArgs,
                        Location: expr.Location);
                }
            }
            // Throwable function call: identifier!(args) with named arguments
            else if (Check(type: TokenType.Bang) && PeekToken(offset: 1)
                        .Type == TokenType.LeftParen)
            {
                Advance(); // consume '!'
                Advance(); // consume '('

                List<Expression> args = ParseArgumentList();
                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");

                if (expr is IdentifierExpression identExpr)
                {
                    expr = new CallExpression(Callee: new IdentifierExpression(Name: identExpr.Name + "!", Location: identExpr.Location), Arguments: args, Location: expr.Location);
                }
                else
                {
                    expr = new CallExpression(Callee: expr, Arguments: args, Location: expr.Location);
                }
            }
            else if (Match(type: TokenType.LeftParen))
            {
                // Function call - supports named arguments (name: value)
                List<Expression> args = ParseArgumentList();
                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");
                expr = new CallExpression(Callee: expr, Arguments: args, Location: expr.Location);
            }
            else if (Match(type: TokenType.LeftBracket))
            {
                Expression index = ParseExpression();
                Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after index");

                if (index is RangeExpression range)
                {
                    // [a to b] or [a til b] -> SliceExpression (desugars to $getslice)
                    if (range.Step != null)
                    {
                        throw new GrammarException(
                            GrammarDiagnosticCode.UnexpectedToken,
                            "Step ('by') is not supported in slice syntax.",
                            fileName, CurrentToken.Line, CurrentToken.Column,
                            _language);
                    }
                    expr = new SliceExpression(Object: expr, Start: range.Start, End: range.End,
                        Location: expr.Location);
                }
                else
                {
                    expr = new IndexExpression(Object: expr, Index: index, Location: expr.Location);
                }
            }
            else if (Match(type: TokenType.QuestionDot))
            {
                // Optional chaining: obj?.member
                string member = ConsumeMethodName(errorMessage: "Expected member name after '?.'");
                expr = new OptionalMemberExpression(Object: expr, PropertyName: member, Location: expr.Location);
            }
            else if (Match(type: TokenType.Dot))
            {
                // Member access - allow failable methods with ! suffix
                string member = ConsumeMethodName(errorMessage: "Expected member name after '.'");

                // Check for failable marker ! before generic parameters: obj.method![T]
                bool isGenericMemOp = false;
                if (Check(type: TokenType.Bang) && PeekToken(offset: 1)
                       .Type == TokenType.LeftBracket)
                {
                    isGenericMemOp = true;
                    Match(type: TokenType.Bang);
                }

                // Check for generic method call with type parameters
                // Disambiguate by checking bracket content (must look like type args)
                // and what follows ] (must be ( or .)
                if (Check(type: TokenType.LeftBracket) && IsLikelyGenericBracket(acceptDotAfterBracket: true))
                {
                    Advance(); // consume '['
                    var typeArgs = new List<TypeExpression>();
                    do
                    {
                        typeArgs.Add(item: ParseType());
                    } while (Match(type: TokenType.Comma));

                    Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after generic type arguments");

                    if (Match(type: TokenType.LeftParen))
                    {
                        List<Expression> genericArgs = ParseArgumentList();
                        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");

                        string methodName = member;
                        if (isGenericMemOp)
                        {
                            methodName += "!";
                        }

                        expr = new GenericMethodCallExpression(Object: expr,
                            MethodName: methodName,
                            TypeArguments: typeArgs,
                            Arguments: genericArgs,
                            IsMemoryOperation: isGenericMemOp,
                            Location: expr.Location);
                    }
                    else
                    {
                        expr = new GenericMemberExpression(Object: expr,
                            MemberName: member,
                            TypeArguments: typeArgs,
                            Location: expr.Location);
                    }

                    continue;
                }

                // Regular member access
                // Check for failable method call with ! suffix
                if (Match(type: TokenType.Bang) && Match(type: TokenType.LeftParen))
                {
                    // Failable method call: obj.method!(args)
                    // Represented as CallExpression with MemberExpression callee
                    List<Expression> args = ParseArgumentList();
                    Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");

                    Expression memberExpr = new MemberExpression(Object: expr, PropertyName: member + "!", Location: expr.Location);
                    expr = new CallExpression(Callee: memberExpr, Arguments: args, Location: expr.Location);
                }
                else if (Match(type: TokenType.LeftParen))
                {
                    // Regular method call: obj.method(args)
                    // Represented as CallExpression with MemberExpression callee
                    List<Expression> args = ParseArgumentList();
                    Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");

                    Expression memberExpr = new MemberExpression(Object: expr, PropertyName: member, Location: expr.Location);
                    expr = new CallExpression(Callee: memberExpr, Arguments: args, Location: expr.Location);
                }
                else
                {
                    expr = new MemberExpression(Object: expr, PropertyName: member, Location: expr.Location);
                }
            }
            // ===============================================================================
            // CASE 7: Force unwrap - expr!! (extract value from Maybe<T>, panic if None)
            // ===============================================================================
            else if (Match(type: TokenType.BangBang))
            {
                expr = new UnaryExpression(Operator: UnaryOperator.ForceUnwrap, Operand: expr, Location: expr.Location);
            }
            // ===============================================================================
            // CASE 8: Multi-line dot chaining - skip newlines if followed by a dot
            // Allows:  items
            //            .where(x => x > 0)
            //            .select(x => x * 2)
            // ===============================================================================
            else if (Check(type: TokenType.Newline))
            {
                int offset = 0;
                while (PeekToken(offset: offset).Type == TokenType.Newline)
                {
                    offset++;
                }

                if (PeekToken(offset: offset).Type == TokenType.Dot)
                {
                    // Consume newlines and let next iteration handle the dot
                    while (Match(type: TokenType.Newline)) { }
                    continue;
                }

                break;
            }
            else
            {
                break;
            }
        }

        return expr;
    }

    /// <summary>
    /// Parses primary expressions (literals, identifiers, parenthesized, intrinsics).
    /// This is the highest precedence level in the expression parser.
    /// Handles: true, false, none, numbers, strings, characters, identifiers,
    /// parenthesized expressions, inline conditionals, @intrinsic, @native.
    /// </summary>
    /// <returns>A primary expression AST node.</returns>
    private Expression ParsePrimary()
    {
        SourceLocation location = GetLocation();

        // Boolean and none literals
        if (Match(type: TokenType.True))
        {
            return new LiteralExpression(Value: true, LiteralType: TokenType.True, Location: location);
        }

        if (Match(type: TokenType.False))
        {
            return new LiteralExpression(Value: false, LiteralType: TokenType.False, Location: location);
        }

        if (Match(type: TokenType.None))
        {
            return new LiteralExpression(Value: null!, LiteralType: TokenType.None, Location: location);
        }

        // 'absent' as expression - evaluates to none (used in pattern matching arms)
        if (Match(type: TokenType.Absent))
        {
            return new LiteralExpression(Value: null!, LiteralType: TokenType.None, Location: location);
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

        // Letter literals
        if (TryParseLetterLiteral(location: location, result: out Expression? letterExpr))
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
        if (!_inWhenPatternContext && Check(type: TokenType.Identifier) &&
            (PeekToken(offset: 1).Type == TokenType.FatArrow || PeekToken(offset: 1).Type == TokenType.Given))
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

                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after tuple elements");
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

        throw ThrowParseError(GrammarDiagnosticCode.ExpectedExpression, $"Unexpected token: {CurrentToken.Type}");
    }

    /// <summary>
    /// Tries to parse a numeric literal (integer, float, decimal, imaginary).
    /// All numeric literals are stored as raw strings for semantic analysis.
    /// The semantic analyzer will parse the value based on the expected type context.
    /// </summary>
    /// <remarks>
    /// Storing literals as strings enables:
    /// - Contextual type inference: `var a: S16 = 100` treats 100 as S16
    /// - Arbitrary precision: `var b: Integer = 123...123` handles any size
    /// - Overflow detection: semantic analyzer can validate value fits in target type
    /// </remarks>
    private bool TryParseNumericLiteral(SourceLocation location, out Expression? result)
    {
        result = null;

        // Integer literals (S32/S64/S128 and Integer for arbitrary precision)
        if (Match(TokenType.Integer,
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
                TokenType.AddressLiteral,
                TokenType.Decimal,
                TokenType.F16Literal,
                TokenType.F32Literal,
                TokenType.F64Literal,
                TokenType.F128Literal,
                TokenType.D32Literal,
                TokenType.D64Literal,
                TokenType.D128Literal,
                TokenType.J32Literal,
                TokenType.J64Literal,
                TokenType.J128Literal,
                TokenType.JnLiteral))
        {
            Token token = PeekToken(offset: -1);
            result = new LiteralExpression(Value: token.Text, LiteralType: token.Type, Location: location);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Tries to parse a text literal (text, formatted text, raw text, bytes).
    /// </summary>
    private bool TryParseTextLiteral(SourceLocation location, out Expression? result)
    {
        result = null;

        if (!Match(TokenType.TextLiteral,
                TokenType.RawText,
                TokenType.BytesLiteral,
                TokenType.BytesRawLiteral))
        {
            return false;
        }

        Token token = PeekToken(offset: -1);
        string value = token.Text;

        // Regular text literals
        if (value.StartsWith(value: "\"") && value.EndsWith(value: "\""))
        {
            value = value.Substring(startIndex: 1, length: value.Length - 2);
        }
        else if (value.StartsWith(value: "b\""))
        {
            int prefixEnd = value.IndexOf(value: '"');
            if (prefixEnd > 0)
            {
                value = value.Substring(startIndex: prefixEnd + 1, length: value.Length - prefixEnd - 2);
            }
        }

        result = new LiteralExpression(Value: value, LiteralType: token.Type, Location: location);
        return true;
    }

    /// <summary>
    /// Tries to parse an inserted text expression (f-string).
    /// Consumes InsertionStart, then text segments and expression parts, until InsertionEnd.
    /// </summary>
    private bool TryParseInsertedText(SourceLocation location, out Expression? result)
    {
        result = null;

        if (!Match(type: TokenType.InsertionStart))
        {
            return false;
        }

        Token startToken = PeekToken(offset: -1);
        bool isRaw = startToken.Text.StartsWith(value: "rf");
        var parts = new List<InsertedTextPart>();

        while (!IsAtEnd && !Check(type: TokenType.InsertionEnd))
        {
            if (Match(type: TokenType.TextSegment))
            {
                Token textToken = PeekToken(offset: -1);
                parts.Add(item: new TextPart(
                    Text: textToken.Text,
                    Location: GetLocation(token: textToken)));
            }
            else if (Match(type: TokenType.LeftBrace))
            {
                Token braceToken = PeekToken(offset: -1);
                SourceLocation partLocation = GetLocation(token: braceToken);

                // Parse the expression inside the braces
                Expression expr = ParseExpression();

                // Check for optional format specifier
                string? formatSpec = null;
                if (Match(type: TokenType.FormatSpec))
                {
                    formatSpec = PeekToken(offset: -1).Text;
                }

                Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after insertion expression");

                parts.Add(item: new ExpressionPart(
                    Expression: expr,
                    FormatSpec: formatSpec,
                    Location: partLocation));
            }
            else
            {
                break;
            }
        }

        Consume(type: TokenType.InsertionEnd, errorMessage: "Expected closing '\"' for inserted text");

        result = new InsertedTextExpression(Parts: parts, IsRaw: isRaw, Location: location);
        return true;
    }

    /// <summary>
    /// Tries to parse a letter literal (letter8, letter16, letter, or byte letter).
    /// </summary>
    private bool TryParseLetterLiteral(SourceLocation location, out Expression? result)
    {
        result = null;

        if (!Match(TokenType.LetterLiteral, TokenType.ByteLetterLiteral))
        {
            return false;
        }

        Token token = PeekToken(offset: -1);
        string parsedLetter = ParseLetterContent(value: token.Text);
        result = new LiteralExpression(Value: parsedLetter, LiteralType: token.Type, Location: location);
        return true;
    }

    /// <summary>
    /// Tries to parse a ByteSize literal (bytes, kilobytes, etc.).
    /// All ByteSize literals are stored as raw strings for semantic analysis.
    /// The semantic analyzer will parse the value and validate it fits in the target type.
    /// </summary>
    private bool TryParseByteSizeLiteral(SourceLocation location, out Expression? result)
    {
        result = null;

        if (!Match(TokenType.ByteLiteral,
                TokenType.KilobyteLiteral,
                TokenType.KibibyteLiteral,
                TokenType.MegabyteLiteral,
                TokenType.MebibyteLiteral,
                TokenType.GigabyteLiteral,
                TokenType.GibibyteLiteral))
        {
            return false;
        }

        Token token = PeekToken(offset: -1);
        result = new LiteralExpression(Value: token.Text, LiteralType: token.Type, Location: location);
        return true;
    }

    /// <summary>
    /// Tries to parse a duration/time literal (hours, minutes, seconds, etc.).
    /// All duration literals are stored as raw strings for semantic analysis.
    /// The semantic analyzer will parse the value and validate it fits in the target type.
    /// </summary>
    private bool TryParseDurationLiteral(SourceLocation location, out Expression? result)
    {
        result = null;

        if (!Match(TokenType.WeekLiteral,
                TokenType.DayLiteral,
                TokenType.HourLiteral,
                TokenType.MinuteLiteral,
                TokenType.SecondLiteral,
                TokenType.MillisecondLiteral,
                TokenType.MicrosecondLiteral,
                TokenType.NanosecondLiteral))
        {
            return false;
        }

        Token token = PeekToken(offset: -1);
        result = new LiteralExpression(Value: token.Text, LiteralType: token.Type, Location: location);
        return true;
    }

    /// <summary>
    /// Parses the content of a letter literal, handling escape sequences.
    /// </summary>
    private static string ParseLetterContent(string value)
    {
        // Determine the actual character content — strip quotes if present
        string charContent;
        int quoteStart = value.IndexOf(value: '\'');
        int quoteEnd = value.Length - 1;

        if (quoteStart >= 0 && quoteEnd > quoteStart && value[index: quoteEnd] == '\'')
        {
            charContent = value.Substring(startIndex: quoteStart + 1, length: quoteEnd - quoteStart - 1);
        }
        else
        {
            charContent = value;
        }

        if (charContent.StartsWith("\\u") && charContent.Length == 8)
        {
            int codePoint = Convert.ToInt32(value: charContent.Substring(startIndex: 2), fromBase: 16);
            return char.ConvertFromUtf32(utf32: codePoint);
        }

        return charContent switch
        {
            "\\'" => "'",
            @"\\" => "\\",
            "\\n" => "\n",
            "\\t" => "\t",
            "\\r" => "\r",
            "\\0" => "\0",
            _ => charContent
        };
    }

    /// <summary>
    /// Parses a when expression for use in expression context.
    /// Syntax: when expression:
    ///             pattern => expr
    ///             pattern => expr
    /// Similar to Rust's match expression or Kotlin's when expression.
    /// </summary>
    /// <param name="location">The source location of the when keyword.</param>
    /// <returns>A <see cref="WhenExpression"/> AST node.</returns>
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
                    string varName = ConsumeIdentifier(errorMessage: "Expected variable name after 'else'");
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

            // Mandatory => on all when expression arms:
            // 1. is PATTERN => expr           (single expression)
            // 2. is PATTERN => \n INDENT ...  (block after =>)
            // Set flag to prevent 'is' expression parsing in when clause bodies
            _inWhenClauseBody = true;
            Statement body;
            Consume(type: TokenType.FatArrow, errorMessage: "Expected '=>' after when pattern");
            if (Check(type: TokenType.Newline) && PeekToken(offset: 1).Type == TokenType.Indent)
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

            clauses.Add(item: new WhenClause(Pattern: pattern, Body: body, Location: GetLocation()));

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
        Consume(type: TokenType.Waitfor, errorMessage: "Expected 'waitfor' after task dependencies");

        Expression operand = ParseUnary();
        Expression? timeout = null;

        if (Match(type: TokenType.Within))
        {
            timeout = ParseUnary();
        }

        return new DependentWaitforExpression(
            Dependencies: dependencies,
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
        List<TaskDependency> dependencies = new();

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

            dependencies.Add(item: new TaskDependency(DependencyExpr: depExpr, BindingName: binding, Location: depLocation));
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
            if (!Match(type: TokenType.LeftParen)) return false;

            // Skip to closing paren, counting nested parens
            int parenDepth = 1;
            while (parenDepth > 0 && !IsAtEnd)
            {
                if (Match(type: TokenType.LeftParen)) parenDepth++;
                else if (Match(type: TokenType.RightParen)) parenDepth--;
                else Advance();
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

        List<Expression> expressions = new();
        do
        {
            expressions.Add(item: ParseUnary());
        } while (Match(type: TokenType.Comma));

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after tuple dependencies");

        // Parse 'as' and binding tuple: (ident, ident, ...)
        Consume(type: TokenType.As, errorMessage: "Expected 'as' after tuple dependencies");

        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' for tuple bindings");

        List<string> bindings = new();
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
            throw new GrammarException(
                GrammarDiagnosticCode.TupleDependencyCountMismatch,
                $"Tuple dependency count ({expressions.Count}) does not match binding count ({bindings.Count})",
                fileName, current.Line, current.Column,
                _language);
        }

        // Create TaskDependency for each pair
        List<TaskDependency> dependencies = new();
        for (int i = 0; i < expressions.Count; i++)
        {
            string? binding = i < bindings.Count ? bindings[index: i] : null;
            dependencies.Add(item: new TaskDependency(
                DependencyExpr: expressions[index: i],
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
            return false;

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
            else if (depth == 1 && tt is not TokenType.Identifier and not TokenType.Comma and not TokenType.Question)
            {
                return false; // Content has non-type tokens (numbers, operators, range keywords, etc.)
            }

            scanPos++;
        }

        if (matchingBracketPos < 0)
            return false;

        if (matchingBracketPos + 1 >= Tokens.Count)
            return false;

        TokenType afterBracket = Tokens[index: matchingBracketPos + 1].Type;
        return afterBracket == TokenType.LeftParen ||
               (acceptDotAfterBracket && afterBracket == TokenType.Dot);
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
        bool hasBang = Check(type: TokenType.Bang) && PeekToken(offset: 1).Type == TokenType.LeftBracket;
        if (!Check(type: TokenType.LeftBracket) && !hasBang)
            return false;

        // func![T]() is always generic — ! before [ only makes sense for generics
        if (hasBang)
            return true;

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
            else if (depth == 1 && tt is not TokenType.Identifier and not TokenType.Comma and not TokenType.Question
                     // Allow integer literals for const generic arguments (e.g., ValueList[S64, 4], ValueBitList[8])
                     and not TokenType.Integer
                     and not TokenType.S64Literal and not TokenType.U64Literal
                     and not TokenType.S32Literal and not TokenType.U32Literal
                     and not TokenType.S16Literal and not TokenType.U16Literal
                     and not TokenType.S8Literal and not TokenType.U8Literal
                     and not TokenType.S128Literal and not TokenType.U128Literal
                     and not TokenType.AddressLiteral
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
            return false;

        if (matchingBracketPos + 1 >= Tokens.Count)
            return false;

        TokenType afterBracket = Tokens[index: matchingBracketPos + 1].Type;

        // ] followed by ( → always generic: Type[T](...)
        if (afterBracket == TokenType.LeftParen)
            return true;

        // ] followed by . with multiple args → generic: Type[T, N].method(...)
        // Single arg + dot stays as index: list[i].foo
        return hasTopLevelComma && afterBracket == TokenType.Dot;
    }
}
