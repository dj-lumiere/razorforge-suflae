using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;
using RazorForge.Diagnostics;

namespace Compilers.RazorForge.Parser;

/// <summary>
/// Partial class containing helper methods (intrinsics, native calls, argument parsing, etc.).
/// </summary>
public partial class RazorForgeParser
{
    #region Operator Desugaring

    /// <summary>
    /// Creates a method call expression that desugars a binary operator.
    /// Converts: a + b → a.__add__(b)
    /// </summary>
    /// <param name="left">The left operand (becomes the receiver of the method call).</param>
    /// <param name="op">The binary operator to desugar.</param>
    /// <param name="right">The right operand (becomes the argument to the method call).</param>
    /// <param name="location">Source location for error reporting.</param>
    /// <returns>A CallExpression representing the desugared operator, or null if operator is not desugared.</returns>
    private Expression? TryDesugarBinaryOperator(Expression left, BinaryOperator op, Expression right,
        SourceLocation location)
    {
        string? methodName = op.GetMethodName();
        if (methodName == null)
        {
            // Operator is not overloadable (and, or, ??, ===, etc.)
            return null;
        }

        // For membership operators (in, notin), the operands are reversed:
        // x in coll → coll.__contains__(x)
        // x notin coll → coll.__notcontains__(x)
        if (op is BinaryOperator.In or BinaryOperator.NotIn)
        {
            MemberExpression memberExpr = new(Object: right, PropertyName: methodName, Location: location);
            return new CallExpression(Callee: memberExpr, Arguments: [left], Location: location);
        }

        // Create: left.__methodName__(right)
        MemberExpression defaultMemberExpr = new(Object: left, PropertyName: methodName, Location: location);

        return new CallExpression(Callee: defaultMemberExpr, Arguments: [right], Location: location);
    }

    /// <summary>
    /// Creates either a desugared method call or a BinaryExpression based on whether the operator is overloadable.
    /// </summary>
    private Expression CreateBinaryExpression(Expression left, BinaryOperator op, Expression right,
        SourceLocation location)
    {
        // Try to desugar to method call for overloadable operators
        Expression? desugared = TryDesugarBinaryOperator(left: left,
            op: op,
            right: right,
            location: location);
        // Non-overloadable operators stay as BinaryExpression
        return desugared ?? new BinaryExpression(Left: left,
            Operator: op,
            Right: right,
            Location: location);
    }

    #endregion

    /// <summary>
    /// Parses an intrinsic routine call.
    /// Syntax: <c>@intrinsic_routine.operation&lt;T&gt;(args)</c>
    /// Intrinsic routines map directly to LLVM IR operations like sitofp, fpext, trunc.
    /// </summary>
    /// <param name="location">Source location of the intrinsic routine call.</param>
    /// <returns>An <see cref="IntrinsicCallExpression"/> AST node.</returns>
    private IntrinsicCallExpression ParseIntrinsicRoutineCall(SourceLocation location)
    {
        // Expect: .operation<T>(args)
        // The @intrinsic_routine token has already been consumed

        Consume(type: TokenType.Dot, errorMessage: "Expected '.' after '@intrinsic_routine'");

        // Parse intrinsic operation name (can contain dots like "add.wrapping", "icmp.slt")
        // Allow keywords as intrinsic names (e.g., @intrinsic.and, @intrinsic.or, @intrinsic.not)
        string intrinsicName = ConsumeIdentifier(errorMessage: "Expected intrinsic operation name", allowKeywords: true);

        // Handle dotted names like "add.wrapping" or "icmp.slt"
        while (Match(type: TokenType.Dot))
        {
            intrinsicName += "." + ConsumeIdentifier(errorMessage: "Expected identifier after '.'", allowKeywords: true);
        }

        // Parse optional type arguments: <T> or <T, U>
        var typeArgs = new List<string>();
        if (Match(type: TokenType.Less))
        {
            do
            {
                // For now, parse type arguments as simple identifiers
                // (more complex type expressions could be supported later)
                if (Match(TokenType.Identifier, TokenType.TypeIdentifier))
                {
                    typeArgs.Add(item: PeekToken(offset: -1)
                       .Text);
                }
                else
                {
                    throw ThrowParseError(RazorForgeDiagnosticCode.ExpectedTypeArgument,
                        "Expected type argument");
                }
            } while (Match(type: TokenType.Comma));

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after type arguments");
        }

        // Parse arguments: (arg1, arg2, ...)
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after intrinsic name");

        var args = new List<Expression>();
        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                args.Add(item: ParseExpression());
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after intrinsic arguments");

        return new IntrinsicCallExpression(IntrinsicName: intrinsicName,
            TypeArguments: typeArgs,
            Arguments: args,
            Location: location);
    }

    /// <summary>
    /// Parses a native/FFI function call.
    /// Syntax: <c>@native.function_name(args)</c>
    /// Calls native C functions linked at compile time.
    /// </summary>
    /// <param name="location">Source location of the native call.</param>
    /// <returns>A <see cref="NativeCallExpression"/> AST node.</returns>
    private NativeCallExpression ParseNativeCall(SourceLocation location)
    {
        // Expect: .function_name(args)
        // The @native token has already been consumed

        Consume(type: TokenType.Dot, errorMessage: "Expected '.' after '@native'");

        // Parse function name (can contain underscores like rf_bigint_new)
        string functionName = ConsumeIdentifier(errorMessage: "Expected native function name");

        // Parse arguments: (arg1, arg2, ...)
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after native function name");

        var args = new List<Expression>();
        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                args.Add(item: ParseExpression());
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after native function arguments");

        return new NativeCallExpression(FunctionName: functionName, Arguments: args, Location: location);
    }

    /// <summary>
    /// Consumes statement terminators.
    /// RazorForge doesn't use explicit newline tokens - statements are separated by whitespace.
    /// </summary>
    private void ConsumeStatementTerminator()
    {
        // Valid implicit terminators: }, else, elseif, EOF
        if (Check(type: TokenType.RightBrace) ||
            Check(type: TokenType.Else) ||
            Check(type: TokenType.Elseif) ||
            IsAtEnd)
        {
            return;
        }

        // Optionally consume a newline if present (from semicolon)
        Match(type: TokenType.Newline);
    }

    /// <summary>
    /// Consumes an identifier token and returns its text.
    /// </summary>
    /// <param name="errorMessage">Error message to show if token is not an identifier.</param>
    /// <param name="allowKeywords">If true, allows reserved keywords as identifiers.</param>
    /// <returns>The identifier text.</returns>
    /// <exception cref="ParseException">Thrown if current token is not a valid identifier.</exception>
    private string ConsumeIdentifier(string errorMessage, bool allowKeywords = false)
    {
        if (Match(TokenType.Identifier, TokenType.TypeIdentifier))
        {
            return PeekToken(offset: -1)
               .Text;
        }

        // Allow 'me' or 'Me' (Self tokens) as a valid identifier for method parameters
        // 'me' is lowercase self reference, 'Me' is the type of self (for protocol method signatures)
        if (Match(TokenType.Me, TokenType.MyType))
        {
            return PeekToken(offset: -1).Text;
        }

        // Allow keywords as identifiers in specific contexts (intrinsic names, parameter names)
        if (allowKeywords)
        {
            // Check if current token is a keyword (And, Or, Not, From, etc.)
            if (Match(TokenType.And,
                    TokenType.Or,
                    TokenType.Not,
                    TokenType.In,
                    TokenType.Is,
                    TokenType.As,
                    TokenType.To,
                    TokenType.By))
            {
                return PeekToken(offset: -1)
                   .Text;
            }
        }

        Token currentToken = CurrentToken;
        throw ThrowParseError(RazorForgeDiagnosticCode.ExpectedIdentifier,
            $"{errorMessage}. Expected Identifier or TypeIdentifier, got {currentToken.Type}.");
    }

    /// <summary>
    /// Parses a single argument which may be named (name: value) or positional (value).
    /// </summary>
    private Expression ParseArgument()
    {
        SourceLocation location = GetLocation();

        // Check if this is a named argument: identifier followed by colon
        // We need to look ahead to distinguish between named argument and ternary/type annotation
        if (Check(type: TokenType.Identifier) && PeekToken(offset: 1)
               .Type == TokenType.Colon)
        {
            // Could be named argument, check that what follows isn't a type (for ternary with typed vars)
            // Named arguments: name: expression
            string potentialName = CurrentToken.Text;
            Advance(); // consume identifier
            Advance(); // consume colon

            // Parse the value expression
            Expression value = ParseExpression();

            return new NamedArgumentExpression(Name: potentialName, Value: value, Location: location);
        }

        // Regular positional argument
        return ParseExpression();
    }

    /// <summary>
    /// Parses a comma-separated list of arguments (named or positional).
    /// Called after '(' has been consumed.
    /// </summary>
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
    /// Consumes an identifier that can be used as a method/function name.
    /// Supports '!' suffix for failable methods.
    /// </summary>
    /// <param name="errorMessage">Error message to show if token is not a valid method name.</param>
    /// <returns>The method name, possibly with '!' suffix for failable methods.</returns>
    private string ConsumeMethodName(string errorMessage)
    {
        if (!Check(type: TokenType.Identifier) && !Check(type: TokenType.TypeIdentifier))
        {
            throw ThrowParseError(RazorForgeDiagnosticCode.ExpectedIdentifier, errorMessage);
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
}
