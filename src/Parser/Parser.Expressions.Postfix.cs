namespace Compiler.Parser;

using Lexer;
using SyntaxTree;
using Diagnostics;

/// <summary>
/// Partial class containing postfix expression parsing.
/// </summary>
public partial class Parser
{
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

                Consume(type: TokenType.RightBracket,
                    errorMessage: "Expected ']' after generic type arguments");

                if (Match(type: TokenType.LeftParen))
                {
                    List<Expression> args = ParseArgumentList();
                    Consume(type: TokenType.RightParen,
                        errorMessage: "Expected ')' after arguments");

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
                    expr = new CallExpression(
                        Callee: new IdentifierExpression(Name: identExpr.Name + "!",
                            Location: identExpr.Location),
                        Arguments: args,
                        Location: expr.Location);
                }
                else
                {
                    expr = new CallExpression(Callee: expr,
                        Arguments: args,
                        Location: expr.Location);
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
                        throw new GrammarException(code: GrammarDiagnosticCode.UnexpectedToken,
                            message: "Step ('by') is not supported in slice syntax.",
                            fileName: fileName,
                            line: CurrentToken.Line,
                            column: CurrentToken.Column,
                            language: _language);
                    }

                    expr = new SliceExpression(Object: expr,
                        Start: range.Start,
                        End: range.End,
                        Location: expr.Location);
                }
                else
                {
                    expr = new IndexExpression(Object: expr,
                        Index: index,
                        Location: expr.Location);
                }
            }
            else if (Match(type: TokenType.QuestionDot))
            {
                // Optional chaining: obj?.member
                string member = ConsumeMethodName(errorMessage: "Expected member name after '?.'");
                expr = new OptionalMemberExpression(Object: expr,
                    PropertyName: member,
                    Location: expr.Location);
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
                if (Check(type: TokenType.LeftBracket) &&
                    IsLikelyGenericBracket(acceptDotAfterBracket: true))
                {
                    Advance(); // consume '['
                    var typeArgs = new List<TypeExpression>();
                    do
                    {
                        typeArgs.Add(item: ParseType());
                    } while (Match(type: TokenType.Comma));

                    Consume(type: TokenType.RightBracket,
                        errorMessage: "Expected ']' after generic type arguments");

                    if (Match(type: TokenType.LeftParen))
                    {
                        List<Expression> genericArgs = ParseArgumentList();
                        Consume(type: TokenType.RightParen,
                            errorMessage: "Expected ')' after arguments");

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
                    Consume(type: TokenType.RightParen,
                        errorMessage: "Expected ')' after arguments");

                    Expression memberExpr = new MemberExpression(Object: expr,
                        PropertyName: member + "!",
                        Location: expr.Location);
                    expr = new CallExpression(Callee: memberExpr,
                        Arguments: args,
                        Location: expr.Location);
                }
                else if (Match(type: TokenType.LeftParen))
                {
                    // Regular method call: obj.method(args)
                    // Represented as CallExpression with MemberExpression callee
                    List<Expression> args = ParseArgumentList();
                    Consume(type: TokenType.RightParen,
                        errorMessage: "Expected ')' after arguments");

                    Expression memberExpr = new MemberExpression(Object: expr,
                        PropertyName: member,
                        Location: expr.Location);
                    expr = new CallExpression(Callee: memberExpr,
                        Arguments: args,
                        Location: expr.Location);
                }
                else
                {
                    expr = new MemberExpression(Object: expr,
                        PropertyName: member,
                        Location: expr.Location);
                }
            }
            // ===============================================================================
            // CASE 7: Force unwrap - expr!! (extract value from Maybe<T>, panic if None)
            // ===============================================================================
            else if (Match(type: TokenType.BangBang))
            {
                expr = new UnaryExpression(Operator: UnaryOperator.ForceUnwrap,
                    Operand: expr,
                    Location: expr.Location);
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
                while (PeekToken(offset: offset)
                          .Type == TokenType.Newline)
                {
                    offset++;
                }

                if (PeekToken(offset: offset)
                       .Type == TokenType.Dot)
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

}
