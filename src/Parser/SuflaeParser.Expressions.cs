using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;
using RazorForge.Diagnostics;

namespace Compilers.Suflae.Parser;

/// <summary>
/// Partial class containing expression parsing (precedence climbing chain).
/// </summary>
public partial class SuflaeParser
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
    /// Compound assignments are desugared: <c>a += b</c> becomes <c>a = a + b</c>.
    /// </summary>
    /// <returns>The parsed expression, possibly an assignment.</returns>
    private Expression ParseAssignment()
    {
        Expression expr = ParseInlineConditional();
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
        // Desugar: a += b becomes a = a.__add__(b)
        Expression binaryExpr = CreateBinaryExpression(
            left: expr,
            op: compoundOp.Value,
            right: value,
            location: expr.Location);
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
            expr = CreateBinaryExpression(
                left: expr,
                op: TokenToBinaryOperator(tokenType: op.Type),
                right: right,
                location: GetLocation(token: op));
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

            // Validate chain direction consistency
            if (operators.Count > 0)
            {
                // Determine the direction of existing chain
                bool isAscending = operators.All(predicate: o =>
                    o == BinaryOperator.Less ||
                    o == BinaryOperator.LessEqual ||
                    o == BinaryOperator.Equal);
                bool isDescending = operators.All(predicate: o =>
                    o == BinaryOperator.Greater ||
                    o == BinaryOperator.GreaterEqual ||
                    o == BinaryOperator.Equal);

                // Check if new operator is compatible
                bool newIsAscending = binOp == BinaryOperator.Less ||
                                      binOp == BinaryOperator.LessEqual ||
                                      binOp == BinaryOperator.Equal;
                bool newIsDescending = binOp == BinaryOperator.Greater ||
                                       binOp == BinaryOperator.GreaterEqual ||
                                       binOp == BinaryOperator.Equal;

                if ((isAscending && !newIsAscending) || (isDescending && !newIsDescending))
                {
                    throw new SuflaeGrammarException(
                        SuflaeDiagnosticCode.UnexpectedToken,
                        "Invalid comparison chain: cannot mix ascending (<, <=) and descending (>, >=) operators",
                        fileName, CurrentToken.Line, CurrentToken.Column);
                }
            }

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
            // Single comparison - desugar to method call
            return CreateBinaryExpression(
                left: operands[index: 0],
                op: operators[index: 0],
                right: operands[index: 1],
                location: GetLocation());
        }

        return expr;
    }

    /// <summary>
    /// Parses type-checking and pattern matching expressions.
    /// Syntax:
    /// - <c>expr is Type</c> - type check
    /// - <c>expr is Type binding</c> - type check with variable binding
    /// - <c>expr is Type (field1, field2)</c> - destructuring pattern
    /// - <c>expr is Type (field: binding)</c> - named destructuring
    /// - <c>expr isnot Type</c> - negated type check
    /// - <c>expr follows Protocol</c> - protocol conformance check
    /// - <c>expr notfollows Protocol</c> - negated protocol check
    /// Context-sensitive: disabled inside when clause bodies to avoid ambiguity.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseIsExpression()
    {
        Expression expr = ParseBitwiseOr();

        // Handle is/isnot/follows/notfollows expressions when not in when pattern/clause context
        while (!_inWhenPatternContext && !_inWhenClauseBody && Match(TokenType.Is,
                   TokenType.IsNot,
                   TokenType.Follows,
                   TokenType.NotFollows))
        {
            Token op = PeekToken(offset: -1);
            SourceLocation location = GetLocation(token: op);

            if (op.Type is TokenType.Is or TokenType.IsNot)
            {
                bool isNegated = op.Type == TokenType.IsNot;
                TypeExpression type = ParseType();

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
                    case false when Check(TokenType.Identifier, TokenType.TypeIdentifier) && !IsKeywordToken(token: CurrentToken):
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
            expr = CreateBinaryExpression(
                left: expr,
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
            expr = CreateBinaryExpression(
                left: expr,
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
            expr = CreateBinaryExpression(
                left: expr,
                op: TokenToBinaryOperator(tokenType: op.Type),
                right: right,
                location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses bit shift expressions.
    /// Syntax: <c>a &lt;&lt; b</c>, <c>a &gt;&gt; b</c>, and checked variants.
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
            expr = CreateBinaryExpression(
                left: expr,
                op: TokenToBinaryOperator(tokenType: op.Type),
                right: right,
                location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses additive expressions.
    /// Syntax: <c>a + b</c>, <c>a - b</c>, and overflow variants (+%, +^, +!, -%, -^, -!).
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
            expr = CreateBinaryExpression(
                left: expr,
                op: TokenToBinaryOperator(tokenType: op.Type),
                right: right,
                location: GetLocation(token: op));
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
                   TokenType.MultiplySaturate,
                   TokenType.MultiplyChecked,
                   TokenType.DivideChecked,
                   TokenType.ModuloChecked))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParsePower();
            expr = CreateBinaryExpression(
                left: expr,
                op: TokenToBinaryOperator(tokenType: op.Type),
                right: right,
                location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses power/exponentiation expressions.
    /// Syntax: <c>a ** b</c> and overflow variants (**%, **^, **!).
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParsePower()
    {
        Expression expr = ParseUnary();

        while (Match(TokenType.Power,
                   TokenType.PowerWrap,
                   TokenType.PowerSaturate,
                   TokenType.PowerChecked))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseUnary();
            expr = CreateBinaryExpression(
                left: expr,
                op: TokenToBinaryOperator(tokenType: op.Type),
                right: right,
                location: GetLocation(token: op));
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
                TokenType.SAddrLiteral,
                TokenType.U8Literal,
                TokenType.U16Literal,
                TokenType.U32Literal,
                TokenType.U64Literal,
                TokenType.U128Literal,
                TokenType.UAddrLiteral,
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
                string newValue = strVal.StartsWith(value: "-") ? strVal.Substring(startIndex: 1) : "-" + strVal;
                return new LiteralExpression(Value: newValue, LiteralType: litExpr.LiteralType, Location: opLocation);
            }
        }

        Expression expr = ParseUnary();
        return new UnaryExpression(Operator: TokenToUnaryOperator(tokenType: op.Type), Operand: expr, Location: opLocation);
    }

    /// <summary>
    /// Parses postfix expressions: function calls, indexing, member access, and more.
    /// Syntax: <c>f(args)</c>, <c>x[index]</c>, <c>obj.member</c>, <c>obj.method!()</c>, <c>value with (field: newVal)</c>
    /// Handles generic method calls: <c>func&lt;T&gt;()</c>, <c>obj.method&lt;T&gt;()</c>
    /// </summary>
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
    /// The '&lt;' token can be either a generic bracket or a less-than operator.
    /// We disambiguate by scanning ahead: if we find &lt;...&gt;() or &lt;...&gt;., it's generics.
    /// </remarks>
    /// <returns>The parsed expression.</returns>
    private Expression ParsePostfix()
    {
        Expression expr = ParsePrimary();

        while (true)
        {
            // ═══════════════════════════════════════════════════════════════════════════
            // CASE 1: Generic function call - func<T>() or func!<T>()
            // ═══════════════════════════════════════════════════════════════════════════
            // The ! must come BEFORE < if present: func!<T>() not func<T>!()
            if (expr is IdentifierExpression expression && (Check(type: TokenType.Less) || Check(type: TokenType.Bang) && PeekToken(offset: 1)
                   .Type == TokenType.Less))
            {
                // Check for failable marker ! before generic parameters: func!<T>
                bool isMemoryOperation = Match(type: TokenType.Bang);

                // Now we should be at '<'
                if (!Check(type: TokenType.Less))
                {
                    break;
                }

                // Lookahead to check if this is likely a generic or a comparison
                int savedPos = Position;
                Advance(); // consume '<'

                bool isLikelyGeneric = false;
                int scanPos = Position;
                int depth = 1;

                while (scanPos < Tokens.Count && depth > 0)
                {
                    TokenType tt = Tokens[index: scanPos].Type;
                    if (tt == TokenType.Less)
                    {
                        depth++;
                    }
                    else if (tt == TokenType.Greater)
                    {
                        depth--;
                        if (depth == 0)
                        {
                            if (scanPos + 1 < Tokens.Count && (Tokens[index: scanPos + 1].Type == TokenType.LeftParen || Tokens[index: scanPos + 1].Type == TokenType.Dot))
                            {
                                isLikelyGeneric = true;
                            }

                            break;
                        }
                    }
                    else if (tt == TokenType.RightShift)
                    {
                        depth -= 2;
                        if (depth == 0)
                        {
                            if (scanPos + 1 < Tokens.Count && (Tokens[index: scanPos + 1].Type == TokenType.LeftParen || Tokens[index: scanPos + 1].Type == TokenType.Dot))
                            {
                                isLikelyGeneric = true;
                            }

                            break;
                        }
                        else if (depth < 0)
                        {
                            break;
                        }
                    }
                    else if (tt == TokenType.Newline || tt == TokenType.Colon)
                    {
                        break;
                    }

                    scanPos++;
                }

                Position = savedPos;

                if (!isLikelyGeneric)
                {
                    break;
                }

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
                // Array/map indexing
                Expression index = ParseExpression();
                Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after index");
                expr = new IndexExpression(Object: expr, Index: index, Location: expr.Location);
            }
            else if (Match(type: TokenType.Dot))
            {
                // Member access - allow failable methods with ! suffix
                string member = ConsumeMethodName(errorMessage: "Expected member name after '.'");

                // Check for failable marker ! before generic parameters: obj.method!<T>
                bool isGenericMemOp = false;
                if (Check(type: TokenType.Bang) && PeekToken(offset: 1)
                       .Type == TokenType.Less)
                {
                    isGenericMemOp = true;
                    Match(type: TokenType.Bang);
                }

                // Check for generic method call with type parameters
                // Disambiguate by scanning for the pattern <...>() or <...>.
                // If we find matching > followed by ( or ., it's definitely a generic call.
                if (Check(type: TokenType.Less))
                {
                    int savedPos = Position;
                    Advance(); // consume '<'

                    // Scan forward to find matching > and check what follows
                    bool isLikelyGeneric = false;
                    int scanPos = Position;
                    int depth = 1;

                    while (scanPos < Tokens.Count && depth > 0)
                    {
                        TokenType tt = Tokens[index: scanPos].Type;
                        if (tt == TokenType.Less)
                        {
                            depth++;
                        }
                        else if (tt == TokenType.Greater)
                        {
                            depth--;
                            if (depth == 0)
                            {
                                if (scanPos + 1 < Tokens.Count &&
                                    (Tokens[index: scanPos + 1].Type == TokenType.LeftParen ||
                                     Tokens[index: scanPos + 1].Type == TokenType.Dot))
                                {
                                    isLikelyGeneric = true;
                                }

                                break;
                            }
                        }
                        else if (tt == TokenType.RightShift)
                        {
                            // >> could be two > in nested generics
                            depth -= 2;
                            if (depth <= 0)
                            {
                                if (scanPos + 1 < Tokens.Count &&
                                    (Tokens[index: scanPos + 1].Type == TokenType.LeftParen ||
                                     Tokens[index: scanPos + 1].Type == TokenType.Dot))
                                {
                                    isLikelyGeneric = true;
                                }

                                break;
                            }
                        }
                        else if (tt == TokenType.Newline || tt == TokenType.Colon)
                        {
                            break;
                        }

                        scanPos++;
                    }

                    Position = savedPos;

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

                        continue;
                    }
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
            else if (Match(type: TokenType.With))
            {
                // Functional update expression for records: record with (field: newVal)
                // Only valid on record types - semantic analyzer will validate this
                SourceLocation withLocation = GetLocation(token: PeekToken(offset: -1));
                Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after 'with'");

                var updates = new List<(string FieldName, Expression Value)>();

                // Parse update list: (field: value, field2: value2, ...)
                do
                {
                    Token fieldToken = Consume(type: TokenType.Identifier, errorMessage: "Expected field name in with expression");
                    Consume(type: TokenType.Colon, errorMessage: "Expected ':' after field name in with expression");
                    Expression value = ParseExpression();
                    updates.Add(item: (fieldToken.Text, value));
                } while (Match(type: TokenType.Comma));

                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after with updates");

                expr = new WithExpression(Base: expr, Updates: updates, Location: withLocation);
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

        // Memory size literals
        if (TryParseMemoryLiteral(location: location, result: out Expression? memoryExpr))
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

        // Identifiers and Suflae-specific keywords
        // Note: 'me' is tokenized as TokenType.Me, so we need to handle it explicitly
        if (Match(TokenType.Identifier, TokenType.TypeIdentifier, TokenType.Me))
        {
            string text = PeekToken(offset: -1)
               .Text;
            if (text == "me")
            {
                return new IdentifierExpression(Name: "me", Location: location);
            }

            return new IdentifierExpression(Name: text, Location: location);
        }

        // Parenthesized expression or arrow lambda with parenthesized params
        if (Match(type: TokenType.LeftParen))
        {
            if (IsArrowLambdaParameters())
            {
                return ParseParenthesizedArrowLambda(location: location);
            }

            Expression expr = ParseExpression();
            Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after expression");
            return expr;
        }

        // When expression: when x: pattern => expr, ...
        // Used in expression context: return when x: ..., let y = when x: ...
        if (Match(type: TokenType.When))
        {
            return ParseWhenExpression(location: location);
        }

        // Intrinsic routine call: @intrinsic_routine.operation<T>(args)
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

        throw ThrowParseError($"Unexpected token: {CurrentToken.Type}");
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

        // Integer literals (Suflae uses S32/S64/S128 and Integer for arbitrary precision)
        if (Match(TokenType.Integer,
                TokenType.S32Literal,
                TokenType.S64Literal,
                TokenType.S128Literal,
                TokenType.SAddrLiteral,
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
    /// Tries to parse a text literal (text, formatted text, raw text, bytes).
    /// </summary>
    private bool TryParseTextLiteral(SourceLocation location, out Expression? result)
    {
        result = null;

        if (!Match(TokenType.TextLiteral,
                TokenType.FormattedText,
                TokenType.RawText,
                TokenType.RawFormattedText,
                TokenType.BytesLiteral,
                TokenType.BytesFormatted,
                TokenType.BytesRawLiteral,
                TokenType.BytesRawFormatted))
        {
            return false;
        }

        Token token = PeekToken(offset: -1);
        string value = token.Text;

        // Formatted text literals (f"...")
        if (value.StartsWith(value: "f\"") && value.EndsWith(value: "\""))
        {
            value = value.Substring(startIndex: 2, length: value.Length - 3);
            result = new LiteralExpression(Value: value, LiteralType: TokenType.FormattedText, Location: location);
            return true;
        }

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
        char parsedLetter = ParseLetterContent(value: token.Text);
        result = new LiteralExpression(Value: parsedLetter, LiteralType: token.Type, Location: location);
        return true;
    }

    /// <summary>
    /// Tries to parse a memory size literal (bytes, kilobytes, etc.).
    /// All memory literals are stored as raw strings for semantic analysis.
    /// The semantic analyzer will parse the value and validate it fits in the target type.
    /// </summary>
    private bool TryParseMemoryLiteral(SourceLocation location, out Expression? result)
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
                "\\\\" => '\\',
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
        Expression expression = ParseExpression();
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after when expression");

        Consume(type: TokenType.Newline, errorMessage: "Expected newline after when header");
        Consume(type: TokenType.Indent, errorMessage: "Expected indented block after when");

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
            // 2. is PATTERN:                  (indented block)
            //        statements...
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
                // Optional colon after => for multi-line body
                if (Check(type: TokenType.Colon))
                {
                    Consume(type: TokenType.Colon, errorMessage: "Expected ':' after '=>'");
                    body = ParseIndentedBlock();
                }
                else
                {
                    body = ParseExpressionStatement();
                }
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

        Consume(type: TokenType.Dedent, errorMessage: "Expected dedent after when clauses");

        return new WhenExpression(Expression: expression, Clauses: clauses, Location: location);
    }
}
