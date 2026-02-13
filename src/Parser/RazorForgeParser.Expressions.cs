using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;
using RazorForge.Diagnostics;

namespace Compilers.RazorForge.Parser;

/// <summary>
/// Partial class containing expression parsing (precedence chain, operators, primary expressions).
/// </summary>
public partial class RazorForgeParser
{
    /// <summary>
    /// Parses an expression at the lowest precedence level.
    /// Entry point for expression parsing.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseExpression()
    {
        return ParseAssignment();
    }

    /// <summary>
    /// Parses assignment expressions including compound assignments.
    /// Syntax: <c>target = value</c>, <c>target += value</c>, etc.
    /// Assignment is right-associative.
    /// Compound assignments are desugared: <c>a += b</c> becomes <c>a = a + b</c>.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseAssignment()
    {
        Expression expr = ParseWith();

        // Check for simple assignment
        if (Match(type: TokenType.Assign))
        {
            Expression value = ParseAssignment();
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

        {
            Expression value = ParseAssignment();
            // Desugar: a += b becomes a = a.__add__(b) (the inner binary op is desugared to method call)
            Expression binaryExpr = CreateBinaryExpression(left: expr,
                op: compoundOp.Value,
                right: value,
                location: expr.Location);
            return new BinaryExpression(Left: expr,
                Operator: BinaryOperator.Assign,
                Right: binaryExpr,
                Location: expr.Location);
        }
    }

    /// <summary>
    /// Parses with expressions (lowest precedence operator).
    /// Syntax: <c>expr with .field = value, .nested.field = value, [index] = value</c>
    /// </summary>
    /// <returns>The parsed expression, possibly a with expression.</returns>
    private Expression ParseWith()
    {
        Expression expr = ParseInlineConditional();

        if (Match(type: TokenType.With))
        {
            SourceLocation withLocation = GetLocation(token: PeekToken(offset: -1));
            var updates = new List<(List<string>? FieldPath, Expression? Index, Expression Value)>();

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
                    // Field update: .field or .field.nested
                    fieldPath = new List<string>();
                    Token fieldToken = Consume(type: TokenType.Identifier, errorMessage: "Expected field name after '.' in with expression");
                    fieldPath.Add(item: fieldToken.Text);

                    // Parse nested field path: .address.city
                    while (Check(type: TokenType.Dot) && PeekToken(offset: 1).Type == TokenType.Identifier)
                    {
                        Advance(); // consume dot
                        Token nestedToken = Consume(type: TokenType.Identifier, errorMessage: "Expected field name in with expression");
                        fieldPath.Add(item: nestedToken.Text);
                    }
                }
                else
                {
                    throw new RazorForgeGrammarException(
                        RazorForgeDiagnosticCode.UnexpectedToken,
                        "Expected '.' or '[' in with expression",
                        _fileName, CurrentToken.Line, CurrentToken.Column);
                }

                Consume(type: TokenType.Assign, errorMessage: "Expected '=' after field or index in with expression");
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
            TokenType.PlusSaturateAssign => BinaryOperator.AddSaturate,
            TokenType.MinusSaturateAssign => BinaryOperator.SubtractSaturate,
            TokenType.MultiplySaturateAssign => BinaryOperator.MultiplySaturate,
            TokenType.PowerSaturateAssign => BinaryOperator.PowerSaturate,
            _ => null
        };

        if (op.HasValue)
        {
            Advance(); // Consume the compound assignment token
        }

        return op;
    }

    /// <summary>
    /// Parses none-coalescing expressions.
    /// Syntax: <c>value ?? default</c>
    /// Returns right operand if left is None.
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
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseLogicalOr()
    {
        Expression expr = ParseRange();

        while (Match(type: TokenType.Or))
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
    /// </summary>
    /// <returns>The parsed expression.</returns>
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

        // Handle descending range expressions: A downto B or A downto B by C
        if (Match(type: TokenType.Downto))
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
                IsDescending: true,
                Location: expr.Location);
        }

        return expr;
    }

    /// <summary>
    /// Parses logical AND expressions.
    /// Syntax: <c>a and b</c>
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
    /// Parses equality comparison expressions.
    /// Syntax: <c>a == b</c> or <c>a != b</c>
    /// </summary>
    /// <remarks>
    /// When inside a when clause body (<see cref="_inWhenClauseBody"/> is true),
    /// comparison operators are not allowed to continue the expression.
    /// This prevents ambiguity between `=> body` followed by `== pattern`
    /// and `=> body == continuation` since newlines don't produce tokens.
    /// </remarks>
    /// <returns>The parsed expression.</returns>
    private Expression ParseEquality()
    {
        Expression expr = ParseComparison();

        // In when clause body context, don't allow comparison operators to continue.
        // This prevents `=> 1 == Status.ACTIVE` from being parsed as `=> (1 == Status.ACTIVE)`
        // instead of `=> 1` followed by `== Status.ACTIVE => ...`
        if (_inWhenClauseBody)
        {
            return expr;
        }

        while (Match(TokenType.Equal, TokenType.NotEqual, TokenType.ReferenceEqual, TokenType.ReferenceNotEqual))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseComparison();
            expr = CreateBinaryExpression(left: expr,
                op: TokenToBinaryOperator(tokenType: op.Type),
                right: right,
                location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses comparison expressions with support for chaining.
    /// Syntax: <c>a &lt; b</c>, <c>a &lt;= b &lt;= c</c> (chained), <c>a &lt;=&gt; b</c> (three-way)
    /// Supports: &lt;, &lt;=, &gt;, &gt;=, ==, !=, &lt;=&gt;
    /// </summary>
    /// <remarks>
    /// When inside a when clause body (<see cref="_inWhenClauseBody"/> is true),
    /// comparison operators are not allowed to continue the expression.
    /// This prevents ambiguity between `=> body` followed by `== pattern`
    /// and `=> body == continuation` since newlines don't produce tokens.
    /// </remarks>
    /// <returns>The parsed expression (may be <see cref="ChainedComparisonExpression"/> for chains).</returns>
    private Expression ParseComparison()
    {
        Expression expr = ParseIsExpression();

        // In when clause body context, don't allow comparison operators to continue.
        // This prevents `=> 1 < Status.Value` from being parsed as a continued comparison
        // instead of `=> 1` followed by `< Status.Value => ...`
        if (_inWhenClauseBody)
        {
            return expr;
        }

        // Check for chained comparisons (a < b < c)
        var operators = new List<BinaryOperator>();
        var operands = new List<Expression> { expr };

        while (Match(TokenType.Less,
                   TokenType.LessEqual,
                   TokenType.Greater,
                   TokenType.GreaterEqual,
                   TokenType.Equal,
                   TokenType.NotEqual,
                   TokenType.ThreeWayComparison))
        {
            Token op = PeekToken(offset: -1);
            operators.Add(item: TokenToBinaryOperator(tokenType: op.Type));

            Expression right = ParseIsExpression();
            operands.Add(item: right);
        }

        return operators.Count switch
        {
            // If we have chained comparisons, create a ChainedComparisonExpression
            // Note: Chained comparisons are NOT desugared because they need special
            // handling to evaluate middle operands only once (a < b < c)
            > 1 => new ChainedComparisonExpression(Operands: operands, Operators: operators, Location: GetLocation()),
            1 => CreateBinaryExpression(left: operands[index: 0],
                op: operators[index: 0],
                right: operands[index: 1],
                location: GetLocation()),
            _ => expr
        };
    }

    /// <summary>
    /// Parses type-checking, membership, and pattern matching expressions.
    /// Syntax:
    /// - <c>expr is Type</c> - type check
    /// - <c>expr is Type binding</c> - type check with variable binding
    /// - <c>expr is Type (field1, field2)</c> - destructuring pattern
    /// - <c>expr is Type (field: binding)</c> - named destructuring
    /// - <c>expr isnot Type</c> - negated type check
    /// - <c>expr in collection</c> - membership test (desugars to collection.__contains__(expr))
    /// - <c>expr notin collection</c> - negated membership test
    /// - <c>expr follows Protocol</c> - protocol conformance check
    /// - <c>expr notfollows Protocol</c> - negated protocol check
    /// Context-sensitive: disabled inside when clause bodies to avoid ambiguity.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseIsExpression()
    {
        Expression expr = ParseBitwiseOr();

        // Handle is/isnot/in/notin/follows/notfollows expressions when not in when pattern/clause context
        while (!_inWhenPatternContext && !_inWhenClauseBody && Match(TokenType.Is,
                   TokenType.IsNot,
                   TokenType.In,
                   TokenType.NotIn,
                   TokenType.Follows,
                   TokenType.NotFollows))
        {
            Token op = PeekToken(offset: -1);
            SourceLocation location = GetLocation(token: op);

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

                // Check for destructuring pattern: is Type (...)
                if (!isNegated && Check(type: TokenType.LeftParen))
                {
                    // Destructuring pattern: is Point (x, y) or is Point (x: a, y: b)
                    List<DestructuringBinding> bindings = ParseDestructuringBindings();
                    var pattern = new TypeDestructuringPattern(Type: type, Bindings: bindings, Location: location);
                    expr = new IsPatternExpression(Expression: expr,
                        Pattern: pattern,
                        IsNegated: false,
                        Location: location);
                }
                // Check for single binding: is Type identifier (only for 'is', not 'isnot')
                else if (!isNegated && Check(TokenType.Identifier, TokenType.TypeIdentifier) && !IsKeywordToken(token: CurrentToken))
                {
                    string variableName = Advance()
                       .Text;
                    var pattern = new TypePattern(Type: type, VariableName: variableName, Bindings: null, Location: location);
                    expr = new IsPatternExpression(Expression: expr,
                        Pattern: pattern,
                        IsNegated: false,
                        Location: location);
                }
                else
                {
                    // Simple type check: is Type or isnot Type
                    var pattern = new TypePattern(Type: type, VariableName: null, Bindings: null, Location: location);
                    expr = new IsPatternExpression(Expression: expr,
                        Pattern: pattern,
                        IsNegated: isNegated,
                        Location: location);
                }
            }
            else
            {
                // Handle follows/notfollows as binary operators
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
    /// Parses destructuring bindings for pattern matching.
    /// Syntax: (field1, field2) or (field: binding, ...) or nested ((x: x1, y: y1), ...)
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
                // Nested destructuring without type name - used for anonymous record fields
                List<DestructuringBinding> nestedBindings = ParseDestructuringBindings();
                // For nested patterns without a field name, use index-based binding
                bindings.Add(item: new DestructuringBinding(FieldName: null,
                    BindingName: "_nested",
                    NestedPattern: new DestructuringPattern(Bindings: nestedBindings, Location: bindingLocation),
                    Location: bindingLocation));
            }
            // Check for wildcard: _
            else if (Check(type: TokenType.Identifier) && CurrentToken.Text == "_")
            {
                Advance();
                bindings.Add(item: new DestructuringBinding(FieldName: null,
                    BindingName: "_",
                    NestedPattern: null,
                    Location: bindingLocation));
            }
            else
            {
                // Named or positional binding
                string name = ConsumeIdentifier(errorMessage: "Expected field name or binding in destructuring pattern");

                if (Match(type: TokenType.Colon))
                {
                    // Named binding: field: binding
                    // Check if binding is a nested pattern
                    if (Check(type: TokenType.LeftParen))
                    {
                        List<DestructuringBinding> nestedBindings = ParseDestructuringBindings();
                        bindings.Add(item: new DestructuringBinding(FieldName: name,
                            BindingName: name,
                            NestedPattern: new DestructuringPattern(Bindings: nestedBindings, Location: bindingLocation),
                            Location: bindingLocation));
                    }
                    else
                    {
                        string bindingName = ConsumeIdentifier(errorMessage: "Expected binding name after ':'");
                        bindings.Add(item: new DestructuringBinding(FieldName: name,
                            BindingName: bindingName,
                            NestedPattern: null,
                            Location: bindingLocation));
                    }
                }
                else
                {
                    // Positional binding: name binds to field of same name
                    bindings.Add(item: new DestructuringBinding(FieldName: name,
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
                // For nested patterns without a field name, use null FieldName
                bindings.Add(item: new DestructuringBinding(FieldName: null,
                    BindingName: null,
                    NestedPattern: new DestructuringPattern(Bindings: nestedBindings, Location: bindingLocation),
                    Location: bindingLocation));
            }
            // Check for wildcard: _
            else if (Check(type: TokenType.Identifier) && CurrentToken.Text == "_")
            {
                Advance();
                bindings.Add(item: new DestructuringBinding(FieldName: null,
                    BindingName: "_",
                    NestedPattern: null,
                    Location: bindingLocation));
            }
            else
            {
                // Named or positional binding
                string name = ConsumeIdentifier(errorMessage: "Expected field name or binding in destructuring pattern");

                if (Match(type: TokenType.Colon))
                {
                    // Named binding: field: binding or field: (nested)
                    if (Check(type: TokenType.LeftParen))
                    {
                        List<DestructuringBinding> nestedBindings = ParseDestructuringBindings();
                        bindings.Add(item: new DestructuringBinding(FieldName: name,
                            BindingName: null,
                            NestedPattern: new DestructuringPattern(Bindings: nestedBindings, Location: bindingLocation),
                            Location: bindingLocation));
                    }
                    else
                    {
                        string bindingName = ConsumeIdentifier(errorMessage: "Expected binding name after ':'");
                        bindings.Add(item: new DestructuringBinding(FieldName: name,
                            BindingName: bindingName,
                            NestedPattern: null,
                            Location: bindingLocation));
                    }
                }
                else
                {
                    // Positional binding: name binds to field of same name
                    bindings.Add(item: new DestructuringBinding(FieldName: name,
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
            TokenType.And or TokenType.Or or TokenType.Not or TokenType.Is or TokenType.IsNot or TokenType.In or TokenType.NotIn or TokenType.Follows or TokenType.NotFollows or TokenType.If or TokenType.Else or TokenType.While or TokenType.For or TokenType.Return or TokenType.Throw or TokenType.When or TokenType.Then or TokenType.To or TokenType.Downto or TokenType.By => true,
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
            expr = CreateBinaryExpression(left: expr,
                op: TokenToBinaryOperator(tokenType: op.Type),
                right: right,
                location: GetLocation(token: op));
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
            expr = CreateBinaryExpression(left: expr,
                op: TokenToBinaryOperator(tokenType: op.Type),
                right: right,
                location: GetLocation(token: op));
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
            expr = CreateBinaryExpression(left: expr,
                op: TokenToBinaryOperator(tokenType: op.Type),
                right: right,
                location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses bit shift expressions.
    /// Syntax: <c>a &lt;&lt; b</c>, <c>a &gt;&gt; b</c> (arithmetic and logical variants)
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseShift()
    {
        Expression expr = ParseAdditive();

        while (Match(TokenType.LeftShift,
                   TokenType.LeftShiftChecked,
                   TokenType.RightShift,
                   TokenType.LogicalLeftShift,
                   TokenType.LogicalRightShift))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseAdditive();
            expr = CreateBinaryExpression(left: expr,
                op: TokenToBinaryOperator(tokenType: op.Type),
                right: right,
                location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses additive expressions with overflow variants.
    /// Syntax: <c>a + b</c>, <c>a - b</c>, plus variants: +%, +^, +?, +!, etc.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseAdditive()
    {
        Expression expr = ParseMultiplicative();

        while (Match(TokenType.Plus,
                   TokenType.Minus,
                   TokenType.PlusWrap,
                   TokenType.PlusSaturate,
                   TokenType.PlusChecked,
                   TokenType.MinusWrap,
                   TokenType.MinusSaturate,
                   TokenType.MinusChecked))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseMultiplicative();
            expr = CreateBinaryExpression(left: expr,
                op: TokenToBinaryOperator(tokenType: op.Type),
                right: right,
                location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses multiplicative expressions with overflow variants.
    /// Syntax: <c>a * b</c>, <c>a / b</c>, <c>a // b</c>, <c>a % b</c>, plus variants.
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
                   TokenType.MultiplySaturate,
                   TokenType.MultiplyChecked,
                   TokenType.DivideChecked,
                   TokenType.ModuloChecked))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParsePower();
            expr = CreateBinaryExpression(left: expr,
                op: TokenToBinaryOperator(tokenType: op.Type),
                right: right,
                location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses power/exponentiation expressions with overflow variants.
    /// Syntax: <c>a ** b</c>, plus variants: **%, **^, **?, **!
    /// Power is right-associative: <c>a ** b ** c</c> = <c>a ** (b ** c)</c>
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParsePower()
    {
        Expression expr = ParseUnary();

        if (Match(TokenType.Power,
                   TokenType.PowerWrap,
                   TokenType.PowerSaturate,
                   TokenType.PowerChecked))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParsePower(); // Recursive call for right-associativity
            expr = CreateBinaryExpression(left: expr,
                op: TokenToBinaryOperator(tokenType: op.Type),
                right: right,
                location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses unary prefix expressions.
    /// Syntax: <c>-x</c>, <c>not x</c>, <c>~x</c>, <c>steal x</c>, <c>^n</c>, <c>waitfor x</c>, <c>after x waitfor y</c>
    /// Special handling for unary minus on numeric literals to support min values.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseUnary()
    {
        SourceLocation location = GetLocation(token: CurrentToken);

        // Handle 'after' keyword for task dependencies
        // Syntax: after dep [as binding] [, ...] waitfor expr [until timeout]
        if (Match(type: TokenType.After))
        {
            return ParseDependentWaitfor(location: location);
        }

        // Handle 'waitfor' keyword for async operations
        // Syntax: waitfor expr [until timeout]
        if (Match(type: TokenType.Waitfor))
        {
            return ParseWaitfor(location: location);
        }

        // Handle steal keyword (RazorForge only - ownership transfer)
        if (Match(type: TokenType.Steal))
        {
            SourceLocation stealLocation = GetLocation(token: PeekToken(offset: -1));
            Expression operand = ParseUnary(); // Right-associative, can chain: steal steal x (though unusual)
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
        if (op.Type == TokenType.Minus && Check(TokenType.Integer,
                TokenType.S8Literal,
                TokenType.S16Literal,
                TokenType.S32Literal,
                TokenType.S64Literal,
                TokenType.S128Literal,
                TokenType.SAddrLiteral,
                TokenType.U8Literal,
                TokenType.U16Literal,
                TokenType.U32Literal,
                TokenType.U64Literal,
                TokenType.U128Literal,
                TokenType.UAddrLiteral,
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
    /// Parses a waitfor expression without dependencies.
    /// Syntax: <c>waitfor expr [until timeout]</c>
    /// </summary>
    /// <param name="location">Source location of the waitfor keyword</param>
    /// <returns>The parsed WaitforExpression</returns>
    private Expression ParseWaitfor(SourceLocation location)
    {
        Expression operand = ParseUnary();
        Expression? timeout = null;

        if (Match(type: TokenType.Until))
        {
            timeout = ParseUnary();
        }

        return new WaitforExpression(Operand: operand, Timeout: timeout, Location: location);
    }

    /// <summary>
    /// Parses a dependent waitfor expression with task dependencies.
    /// Syntax: <c>after dep [as binding] [, ...] waitfor expr [until timeout]</c>
    /// </summary>
    /// <param name="location">Source location of the after keyword</param>
    /// <returns>The parsed DependentWaitforExpression</returns>
    /// <remarks>
    /// Dependency syntax examples:
    /// <list type="bullet">
    /// <item>after a waitfor ... - single dependency, no binding</item>
    /// <item>after a as val waitfor ... - single dependency with binding</item>
    /// <item>after a, b waitfor ... - multiple dependencies, no bindings</item>
    /// <item>after a as va, b as vb waitfor ... - multiple dependencies with bindings</item>
    /// <item>after (a, b) as (va, vb) waitfor ... - tuple-style dependencies</item>
    /// </list>
    /// </remarks>
    private Expression ParseDependentWaitfor(SourceLocation location)
    {
        List<TaskDependency> dependencies = ParseTaskDependencies();

        // Expect 'waitfor' keyword
        Consume(type: TokenType.Waitfor, code: RazorForgeDiagnosticCode.ExpectedWaitforAfterDependencies,
            errorMessage: "Expected 'waitfor' after task dependencies");

        Expression operand = ParseUnary();
        Expression? timeout = null;

        if (Match(type: TokenType.Until))
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
            SourceLocation depLocation = GetLocation(token: CurrentToken);
            Expression depExpr = ParseUnary();
            string? binding = null;

            if (Match(type: TokenType.As))
            {
                Token bindingToken = Consume(type: TokenType.Identifier,
                    code: RazorForgeDiagnosticCode.ExpectedIdentifier,
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
        int savedPosition = _position;

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
            _position = savedPosition;
        }
    }

    /// <summary>
    /// Parses tuple-style dependencies: (a, b) as (va, vb)
    /// </summary>
    private List<TaskDependency> ParseTupleDependencies()
    {
        SourceLocation location = GetLocation(token: CurrentToken);

        // Parse expression tuple: (expr, expr, ...)
        Consume(type: TokenType.LeftParen, code: RazorForgeDiagnosticCode.ExpectedLeftParen,
            errorMessage: "Expected '(' for tuple dependencies");

        List<Expression> expressions = new();
        do
        {
            expressions.Add(item: ParseUnary());
        } while (Match(type: TokenType.Comma));

        Consume(type: TokenType.RightParen, code: RazorForgeDiagnosticCode.ExpectedRightParen,
            errorMessage: "Expected ')' after tuple dependencies");

        // Parse 'as' and binding tuple: (ident, ident, ...)
        Consume(type: TokenType.As, code: RazorForgeDiagnosticCode.ExpectedAs,
            errorMessage: "Expected 'as' after tuple dependencies");

        Consume(type: TokenType.LeftParen, code: RazorForgeDiagnosticCode.ExpectedLeftParen,
            errorMessage: "Expected '(' for tuple bindings");

        List<string> bindings = new();
        do
        {
            Token bindingToken = Consume(type: TokenType.Identifier,
                code: RazorForgeDiagnosticCode.ExpectedIdentifier,
                errorMessage: "Expected identifier in tuple binding");
            bindings.Add(item: bindingToken.Text);
        } while (Match(type: TokenType.Comma));

        Consume(type: TokenType.RightParen, code: RazorForgeDiagnosticCode.ExpectedRightParen,
            errorMessage: "Expected ')' after tuple bindings");

        // Validate counts match
        if (expressions.Count != bindings.Count)
        {
            Token current = CurrentToken;
            throw new RazorForgeGrammarException(
                RazorForgeDiagnosticCode.TupleDependencyCountMismatch,
                $"Tuple dependency count ({expressions.Count}) does not match binding count ({bindings.Count})",
                _fileName, current.Line, current.Column);
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
    /// Parses postfix expressions: function calls, indexing, member access, and more.
    /// Syntax: <c>f(args)</c>, <c>x[index]</c>, <c>obj.member</c>, <c>obj.method!()</c>, <c>value with (field: newVal)</c>
    /// Handles generic method calls: <c>func&lt;T&gt;()</c>, <c>obj.method&lt;T&gt;()</c>
    /// </summary>
    /// <returns>The parsed expression.</returns>
    /// <remarks>
    /// Postfix operators in order of check:
    /// 1. Generic function call: func&lt;T&gt;() or func!&lt;T&gt;()
    /// 2. Failable function call: func!(args)
    /// 3. Regular function call: func(args)
    /// 4. Index access: expr[index]
    /// 5. Member access: expr.member (with sub-cases for generic/failable methods)
    /// 6. Functional update: expr with (field: value)
    ///
    /// DISAMBIGUATION CHALLENGE:
    /// The '<' token can be either a generic bracket or a less-than operator.
    /// We disambiguate by scanning ahead: if we find <...>() or <...>., it's generics.
    /// </remarks>
    private Expression ParsePostfix()
    {
        Expression expr = ParsePrimary();

        while (true)
        {
            // ═══════════════════════════════════════════════════════════════════════════
            // CASE 1: Generic function call - func<T>() or func!<T>()
            // ═══════════════════════════════════════════════════════════════════════════
            // The ! (failable marker) must come BEFORE < if present: func!<T>() not func<T>!()
            // We need to disambiguate: is '<' starting generics or a comparison?
            // Strategy: scan ahead for pattern <...>() which definitively means generics
            // ═══════════════════════════════════════════════════════════════════════════

            if (expr is IdentifierExpression beforeExpr && (Check(type: TokenType.Less) || Check(type: TokenType.Bang) && PeekToken(offset: 1)
                   .Type == TokenType.Less))
            {
                // Check for failable marker ! before generic parameters: func!<T>
                bool isMemoryOperation = Match(type: TokenType.Bang);

                // Now we should be at '<'
                if (!Check(type: TokenType.Less))
                {
                    // If we consumed !, but there's no <, this is not a generic call
                    // Backtrack by not consuming anything and break
                    break;
                }

                // ─────────────────────────────────────────────────────────────────────
                // GENERIC DISAMBIGUATION: Scan ahead to find <...>() pattern
                // This distinguishes: sizeof<T>() (generic) vs i < N (comparison)
                // ─────────────────────────────────────────────────────────────────────

                int savedPos = _position;
                Advance(); // consume '<'

                bool isLikelyGeneric = false;
                int scanPos = _position;
                int depth = 1; // We already consumed one <

                while (scanPos < tokens.Count && depth > 0)
                {
                    TokenType tt = tokens[index: scanPos].Type;
                    if (tt == TokenType.Less)
                    {
                        depth++;
                    }
                    else if (tt == TokenType.Greater)
                    {
                        depth--;
                        if (depth == 0)
                        {
                            // Found matching >, check if ( or . follows (indicating generic call/access)
                            if (scanPos + 1 < tokens.Count && (tokens[index: scanPos + 1].Type == TokenType.LeftParen || tokens[index: scanPos + 1].Type == TokenType.Dot))
                            {
                                isLikelyGeneric = true;
                            }

                            break;
                        }
                    }
                    else if (tt == TokenType.RightShift)
                    {
                        // >> is tokenized as RightShift - treat as two > tokens
                        // This handles nested generics: List<Dict<K, V>>
                        depth -= 2;
                        if (depth == 0)
                        {
                            if (scanPos + 1 < tokens.Count && (tokens[index: scanPos + 1].Type == TokenType.LeftParen || tokens[index: scanPos + 1].Type == TokenType.Dot))
                            {
                                isLikelyGeneric = true;
                            }

                            break;
                        }
                        if (depth < 0)
                        {
                            break; // Invalid pattern
                        }
                    }
                    else if (tt is TokenType.Newline or TokenType.LeftBrace)
                    {
                        // Hit statement boundary without finding matching > - this is a comparison
                        break;
                    }

                    scanPos++;
                }

                _position = savedPos; // restore position

                if (!isLikelyGeneric)
                {
                    // Not a generic, let the comparison operator handle it
                    break;
                }

                // ─────────────────────────────────────────────────────────────────────
                // Parse the generic call: func<T, U>(args)
                // ─────────────────────────────────────────────────────────────────────

                Advance(); // consume '<' again
                var typeArgs = new List<TypeExpression>();
                do
                {
                    typeArgs.Add(item: ParseType());
                } while (Match(type: TokenType.Comma));

                ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic type arguments");

                if (Match(type: TokenType.LeftParen))
                {
                    List<Expression> args = ParseArgumentList();
                    Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");

                    string methodName = beforeExpr.Name;
                    if (isMemoryOperation)
                    {
                        methodName += "!";
                    }

                    expr = new GenericMethodCallExpression(Object: beforeExpr,
                        MethodName: methodName,
                        TypeArguments: typeArgs,
                        Arguments: args,
                        IsMemoryOperation: isMemoryOperation,
                        Location: beforeExpr.Location);
                }
                else
                {
                    // Generic type reference without call: Type<T>
                    expr = new GenericMemberExpression(Object: expr,
                        MemberName: ((IdentifierExpression)expr).Name,
                        TypeArguments: typeArgs,
                        Location: expr.Location);
                }
            }
            // ═══════════════════════════════════════════════════════════════════════════
            // CASE 2: Failable function call without generics - func!(args)
            // ═══════════════════════════════════════════════════════════════════════════
            else if (Check(type: TokenType.Bang) && PeekToken(offset: 1)
                        .Type == TokenType.LeftParen)
            {
                Advance(); // consume '!'
                Advance(); // consume '('

                List<Expression> args = ParseArgumentList();
                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");

                // Mark as failable call by appending ! to the callee name
                if (expr is IdentifierExpression identExpr)
                {
                    expr = new CallExpression(Callee: new IdentifierExpression(Name: identExpr.Name + "!", Location: identExpr.Location), Arguments: args, Location: expr.Location);
                }
                else
                {
                    expr = new CallExpression(Callee: expr, Arguments: args, Location: expr.Location);
                }
            }
            // ═══════════════════════════════════════════════════════════════════════════
            // CASE 3: Regular function call - func(args)
            // ═══════════════════════════════════════════════════════════════════════════
            else if (Match(type: TokenType.LeftParen))
            {
                List<Expression> args = ParseArgumentList();
                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");
                expr = new CallExpression(Callee: expr, Arguments: args, Location: expr.Location);
            }
            // ═══════════════════════════════════════════════════════════════════════════
            // CASE 4: Index access - expr[index]
            // ═══════════════════════════════════════════════════════════════════════════
            else if (Match(type: TokenType.LeftBracket))
            {
                Expression index = ParseExpression();
                Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after index");

                if (index is RangeExpression range)
                {
                    // [a to b] → SliceExpression (desugars to __getslice__)
                    if (range.IsDescending)
                    {
                        throw new RazorForgeGrammarException(
                            RazorForgeDiagnosticCode.UnexpectedToken,
                            "'downto' is not supported in slice syntax. Use ascending 'to' only.",
                            _fileName, CurrentToken.Line, CurrentToken.Column);
                    }
                    if (range.Step != null)
                    {
                        throw new RazorForgeGrammarException(
                            RazorForgeDiagnosticCode.UnexpectedToken,
                            "Step ('by') is not supported in slice syntax.",
                            _fileName, CurrentToken.Line, CurrentToken.Column);
                    }
                    expr = new SliceExpression(Object: expr, Start: range.Start, End: range.End,
                        Location: expr.Location);
                }
                else
                {
                    expr = new IndexExpression(Object: expr, Index: index, Location: expr.Location);
                }
            }
            // ═══════════════════════════════════════════════════════════════════════════
            // CASE 5: Member access - expr.member
            // ═══════════════════════════════════════════════════════════════════════════
            // Sub-cases after parsing member name:
            //   5a. Generic method: obj.method<T>() or obj.method!<T>()
            //   5b. Failable method: obj.method!(args)
            //   5c. Regular method: obj.method(args)
            //   5d. Property access: obj.property
            // ═══════════════════════════════════════════════════════════════════════════
            else if (Match(type: TokenType.Dot))
            {
                string member = ConsumeMethodName(errorMessage: "Expected member name after '.'");

                // Check for failable marker ! before generic parameters: obj.method!<T>
                bool isGenericMemOp = false;
                if (Check(type: TokenType.Bang) && PeekToken(offset: 1)
                       .Type == TokenType.Less)
                {
                    isGenericMemOp = true;
                    Match(type: TokenType.Bang); // consume !
                }

                // ─────────────────────────────────────────────────────────────────────
                // Case 5a: Generic method call - obj.method<T>()
                // Same disambiguation strategy as Case 1
                // ─────────────────────────────────────────────────────────────────────

                if (Check(type: TokenType.Less))
                {
                    int savedPos = _position;
                    Advance(); // consume '<'

                    // Scan forward to find matching > and check what follows
                    bool isLikelyGeneric = false;
                    int scanPos = _position;
                    int depth = 1;

                    while (scanPos < tokens.Count && depth > 0)
                    {
                        TokenType tt = tokens[index: scanPos].Type;
                        if (tt == TokenType.Less)
                        {
                            depth++;
                        }
                        else if (tt == TokenType.Greater)
                        {
                            depth--;
                            if (depth == 0)
                            {
                                if (scanPos + 1 < tokens.Count &&
                                    (tokens[index: scanPos + 1].Type == TokenType.LeftParen ||
                                     tokens[index: scanPos + 1].Type == TokenType.Dot))
                                {
                                    isLikelyGeneric = true;
                                }

                                break;
                            }
                        }
                        else if (tt == TokenType.RightShift)
                        {
                            depth -= 2;
                            if (depth <= 0)
                            {
                                if (scanPos + 1 < tokens.Count &&
                                    (tokens[index: scanPos + 1].Type == TokenType.LeftParen ||
                                     tokens[index: scanPos + 1].Type == TokenType.Dot))
                                {
                                    isLikelyGeneric = true;
                                }

                                break;
                            }
                        }
                        else if (tt == TokenType.Newline || tt == TokenType.LeftBrace)
                        {
                            break; // Statement boundary - this is a comparison
                        }

                        scanPos++;
                    }

                    _position = savedPos; // restore position

                    if (isLikelyGeneric)
                    {
                        Advance(); // consume '<' again
                        var typeArgs = new List<TypeExpression>();
                        do
                        {
                            typeArgs.Add(item: ParseType());
                        } while (Match(type: TokenType.Comma));

                        ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic type arguments");

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

                        continue; // continue the while loop to check for more postfix ops
                    }
                    // Not a generic, fall through to regular member access below
                }

                // ─────────────────────────────────────────────────────────────────────
                // Case 5b: Failable method call - obj.method!(args)
                // ─────────────────────────────────────────────────────────────────────

                if (Match(type: TokenType.Bang) && Match(type: TokenType.LeftParen))
                {
                    List<Expression> args = ParseArgumentList();
                    Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");

                    Expression memberExpr = new MemberExpression(Object: expr, PropertyName: member + "!", Location: expr.Location);
                    expr = new CallExpression(Callee: memberExpr, Arguments: args, Location: expr.Location);
                }
                // ─────────────────────────────────────────────────────────────────────
                // Case 5c: Regular method call - obj.method(args)
                // ─────────────────────────────────────────────────────────────────────
                else if (Match(type: TokenType.LeftParen))
                {
                    List<Expression> args = ParseArgumentList();
                    Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");

                    Expression memberExpr = new MemberExpression(Object: expr, PropertyName: member, Location: expr.Location);
                    expr = new CallExpression(Callee: memberExpr, Arguments: args, Location: expr.Location);
                }
                // ─────────────────────────────────────────────────────────────────────
                // Case 5d: Property access - obj.property
                // ─────────────────────────────────────────────────────────────────────
                else
                {
                    expr = new MemberExpression(Object: expr, PropertyName: member, Location: expr.Location);
                }
            }
            // ═══════════════════════════════════════════════════════════════════════════
            // CASE 6: Functional update - record with (field: newValue)
            // ═══════════════════════════════════════════════════════════════════════════
            // Creates a copy of a record with some fields updated.
            // Semantic analyzer validates that expr is actually a record type.
            // ═══════════════════════════════════════════════════════════════════════════
            // ═══════════════════════════════════════════════════════════════════════════
            // CASE 7: Force unwrap - expr!! (extract value from Maybe<T>, panic if None)
            // ═══════════════════════════════════════════════════════════════════════════
            else if (Match(type: TokenType.BangBang))
            {
                expr = new UnaryExpression(Operator: UnaryOperator.ForceUnwrap, Operand: expr, Location: expr.Location);
            }
            else
            {
                break; // No more postfix operators
            }
        }

        return expr;
    }

    /// <summary>
    /// Parses primary expressions: literals, identifiers, parenthesized expressions, lambdas, and intrinsics.
    /// This is the highest precedence level in the expression hierarchy.
    /// Handles: booleans, none, numbers, strings, chars, memory/time literals, identifiers,
    /// lambdas (arrow: x => expr, (x, y) => expr), intrinsics, list/set/dict literals.
    /// </summary>
    /// <returns>The parsed primary expression.</returns>
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

        // Numeric literals (integers and floats)
        if (TryParseNumericLiteral(location: location, result: out Expression? numericExpr))
        {
            return numericExpr!;
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

        // When expression: when x { pattern => expr, ... }
        // Used in expression context: return when x { ... }, let y = when x { ... }
        if (Match(type: TokenType.When))
        {
            return ParseWhenExpression(location: location);
        }

        // Arrow lambda expression: x => expr or x given y => expr (single parameter, no parens)
        if (!_inWhenPatternContext && Check(type: TokenType.Identifier) &&
            (PeekToken(offset: 1).Type == TokenType.FatArrow || PeekToken(offset: 1).Type == TokenType.Given))
        {
            return ParseArrowLambdaExpression(location: location);
        }

        // Identifiers (including keywords allowed as parameter/variable names)
        // Note: 'me' is handled as a regular identifier, mapped to 'this' at semantic level
        if (Match(TokenType.Identifier, TokenType.TypeIdentifier, TokenType.Me))
        {
            return new IdentifierExpression(Name: PeekToken(offset: -1)
                   .Text,
                Location: location);
        }

        // Allow certain keywords to be used as identifiers in expressions
        if (Match(TokenType.To, TokenType.By, TokenType.In))
        {
            return new IdentifierExpression(Name: PeekToken(offset: -1)
                   .Text,
                Location: location);
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

        // Intrinsic routine call: @intrinsic_routine.operation<T, U>(args)
        if (Match(type: TokenType.IntrinsicRoutine))
        {
            return ParseIntrinsicRoutineCall(location: location);
        }

        // Native function call: @native.function_name(args)
        if (Match(type: TokenType.Native))
        {
            return ParseNativeCall(location: location);
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

        throw ThrowParseError(RazorForgeDiagnosticCode.UnexpectedToken,
            $"Unexpected token: {CurrentToken.Type}");
    }

    /// <summary>
    /// Tries to parse a numeric literal (integer, float, decimal, imaginary).
    /// All numeric literals are stored as raw strings for semantic analysis.
    /// The semantic analyzer will parse the value based on the expected type context.
    /// </summary>
    /// <remarks>
    /// Storing literals as strings enables:
    /// - Contextual type inference: `let a: S16 = 100` treats 100 as S16
    /// - Arbitrary precision: `let b: Integer = 123...123` handles any size
    /// - Overflow detection: semantic analyzer can validate value fits in target type
    /// </remarks>
    private bool TryParseNumericLiteral(SourceLocation location, out Expression? result)
    {
        result = null;

        // Integer literals
        if (Match(TokenType.Integer,
                TokenType.S8Literal,
                TokenType.S16Literal,
                TokenType.S32Literal,
                TokenType.S64Literal,
                TokenType.S128Literal,
                TokenType.SAddrLiteral,
                TokenType.U8Literal,
                TokenType.U16Literal,
                TokenType.U32Literal,
                TokenType.U64Literal,
                TokenType.U128Literal,
                TokenType.UAddrLiteral,
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
    /// Tries to parse a text literal.
    /// </summary>
    private bool TryParseTextLiteral(SourceLocation location, out Expression? result)
    {
        result = null;

        if (!Match(TokenType.TextLiteral,
                TokenType.FormattedText,
                TokenType.RawText,
                TokenType.RawFormattedText,
                TokenType.BytesLiteral,
                TokenType.BytesRawLiteral))
        {
            return false;
        }

        Token token = PeekToken(offset: -1);
        string value = token.Text;

        // Formatted string literals (f"...")
        if (value.StartsWith(value: "f\"") && value.EndsWith(value: "\""))
        {
            value = value.Substring(startIndex: 2, length: value.Length - 3);
            result = new LiteralExpression(Value: value, LiteralType: TokenType.FormattedText, Location: location);
            return true;
        }

        // Regular string literals - strip quotes
        if (value.StartsWith(value: "\"") && value.EndsWith(value: "\""))
        {
            value = value.Substring(startIndex: 1, length: value.Length - 2);
        }

        result = new LiteralExpression(Value: value, LiteralType: token.Type, Location: location);
        return true;
    }

    /// <summary>
    /// Tries to parse a letter literal.
    /// </summary>
    private bool TryParseLetterLiteral(SourceLocation location, out Expression? result)
    {
        result = null;

        if (!Match(TokenType.LetterLiteral, TokenType.ByteLetterLiteral))
        {
            return false;
        }

        Token token = PeekToken(offset: -1);
        char parsedLetter = ParseLetterContent(value: token.Text);
        result = new LiteralExpression(Value: parsedLetter, LiteralType: token.Type, Location: location);
        return true;
    }

    /// <summary>
    /// Parses the content of a letter literal, handling escape sequences.
    /// </summary>
    private static char ParseLetterContent(string value)
    {
        int quoteStart = value.LastIndexOf(value: '\'');
        int quoteEnd = value.Length - 1;

        if (quoteStart >= 0 && quoteEnd > quoteStart && value[index: quoteEnd] == '\'')
        {
            string charContent = value.Substring(startIndex: quoteStart + 1, length: quoteEnd - quoteStart - 1);

            return charContent switch
            {
                "\\'" => '\'',
                @"\\" => '\\',
                "\\n" => '\n',
                "\\t" => '\t',
                "\\r" => '\r',
                _ when charContent.Length == 1 => charContent[index: 0],
                _ => '?'
            };
        }

        return '?';
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
    /// Parses a when expression (pattern matching in expression context).
    /// Syntax: <c>when expr { pattern => value, ... }</c>
    /// The 'when' token has already been consumed before calling this method.
    /// </summary>
    /// <param name="location">Source location of the when keyword</param>
    /// <returns>A <see cref="WhenExpression"/> AST node.</returns>
    private WhenExpression ParseWhenExpression(SourceLocation location)
    {
        // Check for standalone when block (condition-based without subject)
        // when { condition => body, ... }  - like Lisp's cond
        bool isConditionBased = Check(type: TokenType.LeftBrace);
        Expression expression;
        if (isConditionBased)
        {
            // Standalone when - use 'true' as the implicit subject
            expression = new LiteralExpression(Value: true, LiteralType: TokenType.True, Location: location);
        }
        else
        {
            // Traditional when with explicit subject
            expression = ParseExpression();
        }

        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after when expression");

        var clauses = new List<WhenClause>();

        while (!Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            // Skip newlines
            if (Match(type: TokenType.Newline))
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
            // Handle wildcard: _ => body
            else if (Check(type: TokenType.Identifier) && CurrentToken.Text == "_" && PeekToken(offset: 1).Type == TokenType.FatArrow)
            {
                Advance(); // consume _
                pattern = new WildcardPattern(Location: clauseLocation);
            }
            // Condition-based when: parse as expression, wrap in ExpressionPattern
            else if (isConditionBased)
            {
                // Parse the condition as a full expression (e.g., me > 0)
                Expression condition = ParseExpression();
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

            // Parse arrow or block
            // Two forms:
            //   pattern => expression       (single expression)
            //   pattern { statements... }   (multi-statement block, requires 'becomes')
            _inWhenClauseBody = true;
            Statement body;

            if (Match(type: TokenType.FatArrow))
            {
                // Single-expression branch: pattern => expression
                // Block is NOT allowed after => (use pattern { } instead)
                if (Check(type: TokenType.LeftBrace))
                {
                    throw ThrowParseError(RazorForgeDiagnosticCode.InvalidPattern,
                        "Block '{ }' is not allowed after '=>'. Use 'pattern { statements... becomes value }' for multi-statement branches");
                }

                body = ParseStatement();
            }
            else if (Check(type: TokenType.LeftBrace))
            {
                // Multi-statement branch: pattern { statements... becomes value }
                body = ParseBlockStatement();
            }
            else
            {
                throw ThrowParseError(RazorForgeDiagnosticCode.InvalidPattern,
                    "Expected '=>' or '{' after pattern");
            }

            _inWhenClauseBody = false;

            clauses.Add(item: new WhenClause(Pattern: pattern, Body: body, Location: GetLocation()));

            // Optional comma or newline between clauses
            Match(TokenType.Comma, TokenType.Newline);
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after when clauses");

        return new WhenExpression(Expression: expression, Clauses: clauses, Location: location);
    }
}
