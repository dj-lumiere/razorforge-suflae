using Builder.Tokenizer;
using SyntaxTree;

namespace Builder.Parser;

/// <summary>
/// Partial class containing pattern and flags-test expression parsing helpers.
/// </summary>
public partial class Parser
{
    private const string ExpectedFlagNameAfterAnd = "Expected flag name after 'and'";

    private FlagsTestExpression ParseFlagsTestChain(Expression subject, string firstFlag,
        bool isNegated, SourceLocation location)
    {
        var flags = new List<string> { firstFlag };
        List<string>? excluded = null;
        FlagsTestKind kind = isNegated
            ? FlagsTestKind.Lack
            : FlagsTestKind.Have;

        if (CheckAndAdvance(type: TokenType.And))
        {
            // 'and' chain: READ and WRITE and ...
            flags.Add(item: ConsumeIdentifier(errorMessage: ExpectedFlagNameAfterAnd));
            while (CheckAndAdvance(type: TokenType.And))
            {
                flags.Add(item: ConsumeIdentifier(errorMessage: ExpectedFlagNameAfterAnd));
            }

            // Optional 'but' exclusion
            if (CheckAndAdvance(type: TokenType.But))
            {
                excluded = ParseButExclusionList();
            }

            return new FlagsTestExpression(Subject: subject,
                Kind: kind,
                TestFlags: flags,
                Connective: FlagsTestConnective.And,
                ExcludedFlags: excluded,
                Location: location);
        }

        if (CheckAndAdvance(type: TokenType.Or))
        {
            flags.Add(item: ConsumeIdentifier(errorMessage: "Expected flag name after 'or'"));
            while (CheckAndAdvance(type: TokenType.Or))
            {
                flags.Add(item: ConsumeIdentifier(errorMessage: "Expected flag name after 'or'"));
            }

            return new FlagsTestExpression(Subject: subject,
                Kind: kind,
                TestFlags: flags,
                Connective: FlagsTestConnective.Or,
                ExcludedFlags: null,
                Location: location);
        }

        if (CheckAndAdvance(type: TokenType.But))
        {
            // Single flag with but exclusion: is READ but WRITE
            excluded = ParseButExclusionList();

            return new FlagsTestExpression(Subject: subject,
                Kind: kind,
                TestFlags: flags,
                Connective: FlagsTestConnective.And,
                ExcludedFlags: excluded,
                Location: location);
        }

        // Should not reach here (caller checked for and/or/but)
        throw ThrowParseError(message: "Expected 'and', 'or', or 'but' in flags test");
    }

    /// <summary>
    /// Parses the excluded-flag list following a <c>but</c> keyword (already consumed):
    /// the first flag name, then any <c>and</c>-chained additional flag names.
    /// </summary>
    private List<string> ParseButExclusionList()
    {
        var excluded = new List<string>
        {
            ConsumeIdentifier(errorMessage: "Expected flag name after 'but'")
        };
        while (CheckAndAdvance(type: TokenType.And))
        {
            excluded.Add(item: ConsumeIdentifier(errorMessage: ExpectedFlagNameAfterAnd));
        }

        return excluded;
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
                    NestedPattern: new DestructuringPattern(Bindings: nestedBindings,
                        Location: bindingLocation),
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
                bindings.Add(
                    item: ParseNamedOrPositionalBinding(bindingLocation: bindingLocation));
            }
        } while (CheckAndAdvance(type: TokenType.Comma));

        Consume(type: TokenType.RightParen,
            errorMessage: "Expected ')' after destructuring pattern");

        return bindings;
    }

    /// <summary>
    /// Parses a single named or positional destructuring binding: <c>name</c>, <c>memberVar: binding</c>,
    /// or <c>memberVar: (nested)</c>. Shared by <see cref="ParseDestructuringBindings"/> and
    /// <see cref="ParseDestructuringBindingList"/>.
    /// </summary>
    private DestructuringBinding ParseNamedOrPositionalBinding(SourceLocation bindingLocation)
    {
        // Named or positional binding
        string name = ConsumeIdentifier(
            errorMessage: "Expected member variable name or binding in destructuring pattern");

        if (CheckAndAdvance(type: TokenType.Colon))
        {
            // Named binding: memberVar: binding
            // Check if binding is a nested pattern
            if (Check(type: TokenType.LeftParen))
            {
                List<DestructuringBinding> nestedBindings = ParseDestructuringBindings();
                return new DestructuringBinding(MemberVariableName: name,
                    BindingName: name,
                    NestedPattern: new DestructuringPattern(Bindings: nestedBindings,
                        Location: bindingLocation),
                    Location: bindingLocation);
            }

            string bindingName =
                ConsumeIdentifier(errorMessage: "Expected binding name after ':'");
            return new DestructuringBinding(MemberVariableName: name,
                BindingName: bindingName,
                NestedPattern: null,
                Location: bindingLocation);
        }

        // Positional binding: name binds to member variable of same name
        return new DestructuringBinding(MemberVariableName: name,
            BindingName: name,
            NestedPattern: null,
            Location: bindingLocation);
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
                    NestedPattern: new DestructuringPattern(Bindings: nestedBindings,
                        Location: bindingLocation),
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
                bindings.Add(
                    item: ParseNamedOrPositionalBindingForList(bindingLocation: bindingLocation));
            }
        } while (CheckAndAdvance(type: TokenType.Comma));

        return bindings;
    }

    /// <summary>
    /// Parses a single named or positional binding for <see cref="ParseDestructuringBindingList"/>:
    /// <c>name</c>, <c>memberVar: binding</c>, or <c>memberVar: (nested)</c>. Differs from
    /// <see cref="ParseNamedOrPositionalBinding"/> only in that a named nested pattern uses a null
    /// BindingName.
    /// </summary>
    private DestructuringBinding ParseNamedOrPositionalBindingForList(
        SourceLocation bindingLocation)
    {
        // Named or positional binding
        string name = ConsumeIdentifier(
            errorMessage: "Expected member variable name or binding in destructuring pattern");

        if (CheckAndAdvance(type: TokenType.Colon))
        {
            // Named binding: memberVar: binding or memberVar: (nested)
            if (Check(type: TokenType.LeftParen))
            {
                List<DestructuringBinding> nestedBindings = ParseDestructuringBindings();
                return new DestructuringBinding(MemberVariableName: name,
                    BindingName: null,
                    NestedPattern: new DestructuringPattern(Bindings: nestedBindings,
                        Location: bindingLocation),
                    Location: bindingLocation);
            }

            string bindingName =
                ConsumeIdentifier(errorMessage: "Expected binding name after ':'");
            return new DestructuringBinding(MemberVariableName: name,
                BindingName: bindingName,
                NestedPattern: null,
                Location: bindingLocation);
        }

        // Positional binding: name binds to member variable of same name
        return new DestructuringBinding(MemberVariableName: name,
            BindingName: name,
            NestedPattern: null,
            Location: bindingLocation);
    }

    /// <summary>
    /// Checks if the current token is a keyword that should not be treated as an identifier.
    /// Used to prevent parsing keywords as binding names in pattern matching.
    /// </summary>
    private static bool IsKeywordToken(Token token)
    {
        return token.Type switch
        {
            TokenType.And or TokenType.Or or TokenType.Not or TokenType.Is or TokenType.IsNot
                or TokenType.In or TokenType.NotIn or TokenType.Obeys or TokenType.Disobeys
                or TokenType.If or TokenType.Else or TokenType.While or TokenType.Each
                or TokenType.Return or TokenType.Throw or TokenType.Pierce or TokenType.When
                or TokenType.Then or TokenType.To or TokenType.Til or TokenType.By => true,
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

        while (CheckAndAdvance(type: TokenType.Pipe))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseBitwiseXor();
            expr = new BinaryExpression(Left: expr,
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

        while (CheckAndAdvance(type: TokenType.Caret))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseBitwiseAnd();
            expr = new BinaryExpression(Left: expr,
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

        while (CheckAndAdvance(type: TokenType.Ampersand))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseShift();
            expr = new BinaryExpression(Left: expr,
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

        while (CheckAndAdvance(TokenType.LeftShift,
                   TokenType.RightShift,
                   TokenType.LogicalLeftShift,
                   TokenType.LogicalRightShift))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseAdditive();
            expr = new BinaryExpression(Left: expr,
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

        while (CheckAndAdvance(TokenType.Plus,
                   TokenType.Minus,
                   TokenType.PlusWrap,
                   TokenType.PlusClamp,
                   TokenType.MinusWrap,
                   TokenType.MinusClamp,
                   TokenType.PlusUnchecked,
                   TokenType.MinusUnchecked))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseMultiplicative();
            expr = new BinaryExpression(Left: expr,
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

        while (CheckAndAdvance(TokenType.Star,
                   TokenType.Slash,
                   TokenType.Percent,
                   TokenType.Divide,
                   TokenType.MultiplyWrap,
                   TokenType.MultiplyClamp,
                   TokenType.SlashClamp,
                   TokenType.MultiplyUnchecked,
                   TokenType.SlashUnchecked,
                   TokenType.DivideUnchecked,
                   TokenType.PercentUnchecked))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParsePower();
            expr = new BinaryExpression(Left: expr,
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

        if (CheckAndAdvance(TokenType.Power,
                TokenType.PowerWrap,
                TokenType.PowerClamp,
                TokenType.PowerUnchecked))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParsePower(); // Recursive call for right-associativity
            expr = new BinaryExpression(Left: expr,
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
        // Recovery prefix keywords (try/grab/lookup): wrap a failable CALL into a carrier. MILESTONE
        // lowering — rewrite the wrapped call's name to the existing generated recovery variant:
        //   try foo(a)      -> try_foo(a)          (Maybe[T])
        //   grab x.foo()    -> x.check_foo()       (Check[T])
        //   lookup foo()    -> lookup_foo()        (Lookup[T])
        // Bare-word keywords only (`try_foo` etc. tokenize as single identifiers). Whole-expression
        // monadic composition + a dedicated RecoveryExpression node replace this rewrite later.
        if (CheckAndAdvance(TokenType.Try, TokenType.Grab, TokenType.Lookup))
        {
            return ParseRecoveryPrefix();
        }

        // Handle steal expression (steal expr = ownership transfer, RazorForge only)
        if (CheckAndAdvance(type: TokenType.Steal))
        {
            SourceLocation stealLocation = GetLocation(token: PeekToken(offset: -1));
            Expression operand = ParseUnary(); // Right-associative
            return new StealExpression(Operand: operand, Location: stealLocation);
        }

        // Handle backindex operator (^n = index from end)
        if (CheckAndAdvance(type: TokenType.Caret))
        {
            SourceLocation caretLocation = GetLocation(token: PeekToken(offset: -1));
            Expression operand = ParseUnary(); // Right-associative
            return new BackIndexExpression(Operand: operand, Location: caretLocation);
        }

        if (!CheckAndAdvance(TokenType.Minus, TokenType.Not, TokenType.Tilde))
        {
            return ParsePostfix();
        }

        Token op = PeekToken(offset: -1);
        SourceLocation opLocation = GetLocation(token: op);

        // Special handling for unary minus on numeric literals
        // This allows parsing negative min values like -9_223_372_036_854_775_808_s64
        // All numeric literals are now strings, so we prepend "-" to the string
        if (op.Type == TokenType.Minus && Check(TokenType.UndecidedInteger,
                TokenType.UndecidedDecimal,
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
                TokenType.S256Literal,
                TokenType.U256Literal,
                TokenType.AddressLiteral,
                TokenType.IntegerLiteral,
                TokenType.B16Literal,
                TokenType.B32Literal,
                TokenType.B64Literal,
                TokenType.B128Literal,
                TokenType.D32Literal,
                TokenType.D64Literal,
                TokenType.D128Literal,
                TokenType.DecimalLiteral,
                TokenType.ImaginaryLiteral))
        {
            // Parse the literal
            Expression literal = ParsePostfix();

            // If it's a literal expression with string value, prepend negative sign
            if (literal is LiteralExpression { Value: string strVal } litExpr)
            {
                // Toggle negative sign: if already negative, remove it; otherwise add it
                string newValue = strVal.StartsWith(value: '-')
                    ? strVal[1..]
                    : "-" + strVal;
                return new LiteralExpression(Value: newValue,
                    LiteralType: litExpr.LiteralType,
                    Location: opLocation);
            }
        }

        Expression expr = ParseUnary();
        return new UnaryExpression(Operator: TokenToUnaryOperator(tokenType: op.Type),
            Operand: expr,
            Location: opLocation);
    }

    /// <summary>
    /// Parses a recovery-prefix expression (try/grab/lookup) after the keyword has been consumed.
    /// Rewrites the wrapped failable expression into a <see cref="RecoveryExpression"/> carrier,
    /// re-applying any trailing force-unwraps on top of the carrier.
    /// </summary>
    /// <returns>The parsed recovery expression.</returns>
    private Expression ParseRecoveryPrefix()
    {
        Token recoveryKw = PeekToken(offset: -1);
        RecoveryKind recoveryKind = recoveryKw.Type switch
        {
            TokenType.Grab => RecoveryKind.Grab,
            TokenType.Lookup => RecoveryKind.Lookup,
            _ => RecoveryKind.Try
        };
        // The keyword takes the WHOLE following expression (down to — but not including — `??`, which
        // ParseNoneCoalesce sits above and thus applies to the recovery RESULT). ParseLogicalOr captures
        // arithmetic/comparison/range/logical operators so `try a + b` recovers the checked-arith overflow
        // as a whole-expression composition (`try (a + b)`, not `(try a) + b`). SA's AnalyzeRecoveryExpression
        // hoists every failable sub-call AND checked-arith operator inside it.
        Expression recoveryInner = ParseLogicalOr();

        // A trailing force-unwrap binds LOOSER than the recovery keyword: `try route()!!` means
        // `(try route())!!`, not `try (route()!!)`. The keyword PRODUCES a carrier and `!!` CONSUMES it,
        // so `!!` applies to the recovery RESULT. ParseUnary already folded the `!!` onto the inner
        // (`ForceUnwrap(route())`), peel those TOP-LEVEL force-unwraps off, recover the inner, then re-apply
        // them on top of the RecoveryExpression. (`??` needs no such handling — it is a binary operator
        // above ParseLogicalOr, so it already applies to the recovery result.)
        var forceUnwraps = new List<SourceLocation>();
        Expression callSpine = recoveryInner;
        while (callSpine is UnaryExpression
               {
                   Operator: UnaryOperator.ForceUnwrap, Operand: { } unwrapped
               } fu)
        {
            forceUnwraps.Add(item: fu.Location);
            callSpine = unwrapped;
        }

        // No CallExpression requirement: the inner may be any expression (a bare call, a member chain, or
        // a checked-arith composition like `a + b`). An operand with no failable call or operator degenerates
        // to an always-present carrier in SA — harmless, and keeping the surface uniform.
        Expression recovery = new RecoveryExpression(Kind: recoveryKind,
            Inner: callSpine,
            Location: GetLocation(token: recoveryKw));

        // Re-apply the peeled `!!`s outermost-last so `try route()!!` → `(try route())!!`.
        for (int i = forceUnwraps.Count - 1; i >= 0; i--)
        {
            recovery = new UnaryExpression(Operator: UnaryOperator.ForceUnwrap,
                Operand: recovery,
                Location: forceUnwraps[index: i]);
        }

        return recovery;
    }
}
