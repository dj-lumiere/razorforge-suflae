using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;
using Compilers.RazorForge.Parser; // For ParseException

namespace Compilers.Suflae.Parser;

/// <summary>
/// Partial class containing type parsing and generic constraints.
/// </summary>
public partial class SuflaeParser
{
    /// <summary>
    /// Parses a type expression.
    /// Supports: named types, generic types (Type&lt;T&gt;), Routine types (Routine&lt;A, B, R&gt;),
    /// Me (self type), and nullable types (T?).
    /// </summary>
    /// <returns>A <see cref="TypeExpression"/> AST node.</returns>
    private TypeExpression ParseType()
    {
        TypeExpression baseType = ParseBaseType();

        // Handle nullable suffix: T? -> Maybe<T>
        if (Match(type: TokenType.Question))
        {
            return new TypeExpression(
                Name: "Maybe",
                GenericArguments: [baseType],
                Location: baseType.Location);
        }

        return baseType;
    }

    /// <summary>
    /// Parses a base type expression without nullable suffix.
    /// </summary>
    /// <remarks>
    /// Type forms in priority order:
    /// 1. Me               - Self type in protocols/methods
    /// 2. Routine&lt;...&gt;     - Function type with parameter/return types
    /// 3. Name&lt;T, U&gt;       - Generic named type
    /// 4. Name             - Simple named type
    ///
    /// Note: Suflae does not have @intrinsic types (those are RazorForge-only).
    /// </remarks>
    private TypeExpression ParseBaseType()
    {
        SourceLocation location = GetLocation();

        // ═══════════════════════════════════════════════════════════════════════════
        // CASE 1: Me - self type in protocols/methods (like Self in Rust)
        // ═══════════════════════════════════════════════════════════════════════════
        if (Match(type: TokenType.MyType))
        {
            return new TypeExpression(Name: "Me", GenericArguments: null, Location: location);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // CASE 2: Routine type - arity-based function types
        // ═══════════════════════════════════════════════════════════════════════════
        // Forms:
        //   Routine            -> zero-arg void routine
        //   Routine<R>         -> zero-arg returning R
        //   Routine<P, R>      -> single param P, returns R
        //   Routine<P1, P2, R> -> two params P1/P2, returns R (last type is return)
        if (Match(type: TokenType.Routine))
        {
            string name = "Routine";

            // Bare 'Routine' without generics - represents a zero-arg void routine
            if (!Match(type: TokenType.Less))
            {
                return new TypeExpression(Name: name, GenericArguments: null, Location: location);
            }

            // Routine types require generic arguments: Routine<R>, Routine<P, R>, Routine<P1, P2, R>
            var typeArgs = new List<TypeExpression>();

            do
            {
                typeArgs.Add(item: ParseType());
            } while (Match(type: TokenType.Comma));

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after Routine type arguments");

            return new TypeExpression(Name: name, GenericArguments: typeArgs, Location: location);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // CASE 3/4: Named type - simple or generic
        // ═══════════════════════════════════════════════════════════════════════════
        // Forms:
        //   User                  -> simple type
        //   List<T>               -> generic type
        //   Dict<Text, S32>       -> multi-param generic
        //   FixedBytes<4>         -> const generic (number as type arg)
        if (Match(TokenType.Identifier, TokenType.TypeIdentifier))
        {
            string name = PeekToken(offset: -1)
               .Text;

            // Support qualified type paths like razorforge/Collections.Dict
            // This allows referencing types from other modules in type annotations
            while (Match(type: TokenType.Slash))
            {
                string part = ConsumeIdentifier(errorMessage: "Expected module path component after '/'");
                name += "/" + part;

                // Handle dot separator for type within module: razorforge/Core.Bool
                if (Match(type: TokenType.Dot))
                {
                    string typeName = ConsumeIdentifier(errorMessage: "Expected type name after '.'");
                    name += "." + typeName;
                    break; // Dot marks the end of the path (rest is the type name)
                }
            }

            // ─────────────────────────────────────────────────────────────────────
            // Simple type without generics
            // ─────────────────────────────────────────────────────────────────────
            if (!Match(type: TokenType.Less))
            {
                return new TypeExpression(Name: name, GenericArguments: null, Location: location);
            }

            // ─────────────────────────────────────────────────────────────────────
            // Generic type with type arguments
            // ─────────────────────────────────────────────────────────────────────
            var typeArgs = new List<TypeExpression>();

            do
            {
                typeArgs.Add(item: ParseTypeOrConstGeneric());
            } while (Match(type: TokenType.Comma));

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after type arguments");

            return new TypeExpression(Name: name, GenericArguments: typeArgs, Location: location);
        }

        throw ThrowParseError($"Expected type, got {CurrentToken.Type} ('{CurrentToken.Text}')");
    }

    /// <summary>
    /// Parses a type expression or a const generic literal.
    /// Used for generic arguments like FixedBytes&lt;4&gt;.
    /// Supports: integers, booleans, letters, and choice values (e.g., Color.Red).
    /// </summary>
    private TypeExpression ParseTypeOrConstGeneric()
    {
        SourceLocation location = GetLocation();

        // Check for boolean literal (const generic)
        if (Match(TokenType.True, TokenType.False))
        {
            string value = PeekToken(offset: -1)
               .Text;
            return new TypeExpression(Name: value, GenericArguments: null, Location: location);
        }

        // Check for integer literal (const generic)
        // Support both typed literals (10u32) and untyped literals (10)
        if (Match(TokenType.Integer,
                TokenType.S64Literal,
                TokenType.U64Literal,
                TokenType.S32Literal,
                TokenType.U32Literal,
                TokenType.S16Literal,
                TokenType.U16Literal,
                TokenType.S8Literal,
                TokenType.U8Literal,
                TokenType.S128Literal,
                TokenType.U128Literal,
                TokenType.SAddrLiteral,
                TokenType.UAddrLiteral))
        {
            string value = PeekToken(offset: -1)
               .Text;
            return new TypeExpression(Name: value, GenericArguments: null, Location: location);
        }

        // Check for letter/character literal (const generic)
        if (Match(TokenType.LetterLiteral, TokenType.ByteLetterLiteral))
        {
            string value = PeekToken(offset: -1)
               .Text;
            return new TypeExpression(Name: value, GenericArguments: null, Location: location);
        }

        // Check for choice value (const generic): ChoiceType.CASE_NAME
        // TypeIdentifier starts with uppercase (choice types follow type naming conventions)
        // Semantic analysis will validate that the type is actually a choice type
        if (Check(type: TokenType.TypeIdentifier) && PeekToken(offset: 1)
               .Type == TokenType.Dot)
        {
            // Parse choice value: Type.CASE
            string typeName = Advance()
               .Text;
            Advance(); // consume dot
            string caseName = ConsumeIdentifier(errorMessage: "Expected choice case name after '.'");
            string value = $"{typeName}.{caseName}";
            return new TypeExpression(Name: value, GenericArguments: null, Location: location);
        }

        // Otherwise parse as normal type
        return ParseType();
    }

    /// <summary>
    /// Consumes a '>' token for closing generic type arguments.
    /// Handles the case where '>>' was tokenized as RightShift by splitting it
    /// and leaving one '>' for the next parse.
    /// </summary>
    private void ConsumeGreaterForGeneric(string errorMessage)
    {
        if (Match(type: TokenType.Greater))
        {
            // Simple case - just a single '>'
            return;
        }

        if (Check(type: TokenType.RightShift))
        {
            // '>>' was tokenized as RightShift - we need to split it
            // Replace the current RightShift token with a single Greater token
            // and leave a Greater for the next parse
            Token currentToken = CurrentToken;
            var newGreater = new Token(Type: TokenType.Greater,
                FileName: currentToken.FileName,
                Text: ">",
                Line: currentToken.Line,
                Column: currentToken.Column + 1); // Second > is one position after

            // Advance past the RightShift
            Advance();

            // Insert a Greater token to be consumed next
            // We do this by adjusting the position and inserting
            InsertToken(token: newGreater);
            return;
        }

        // Neither > nor >> found - error
        throw ThrowParseError($"{errorMessage}. Expected Greater, got {CurrentToken.Type}.");
    }

    /// <summary>
    /// Inserts a token at the current position to be parsed next.
    /// Used for splitting '>>' into two '>' tokens.
    /// </summary>
    private void InsertToken(Token token)
    {
        Tokens.Insert(index: Position, item: token);
    }

    /// <summary>
    /// Parses generic parameters with optional inline constraints like &lt;T follows Integral&gt;.
    /// Returns both the parameter names and any inline constraints found.
    /// </summary>
    private (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) ParseGenericParametersWithConstraints()
    {
        var genericParams = new List<string>();
        var inlineConstraints = new List<GenericConstraintDeclaration>();

        do
        {
            SourceLocation location = GetLocation();
            string paramName = ConsumeIdentifier(errorMessage: "Expected generic parameter name");
            genericParams.Add(item: paramName);

            // Check for inline constraint: T follows Protocol or T is record/entity/resident
            if (Match(type: TokenType.Follows))
            {
                // Parse protocol constraints: T follows Protocol
                var constraintTypes = new List<TypeExpression>();
                do
                {
                    constraintTypes.Add(item: ParseType());
                    // Continue if comma but next token is NOT an identifier followed by follows/is/in or greater
                    // This handles both "T follows A, B" (multiple protocols) and "T follows A, U follows B" (next param)
                } while (Match(type: TokenType.Comma) && !Check(type: TokenType.Greater) && !(Check(type: TokenType.Identifier) && (PeekToken(offset: 1)
                            .Type == TokenType.Follows || PeekToken(offset: 1)
                            .Type == TokenType.Is || PeekToken(offset: 1)
                            .Type == TokenType.In)));

                inlineConstraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                    ConstraintType: ConstraintKind.Follows,
                    ConstraintTypes: constraintTypes,
                    Location: location));
            }
            else if (Match(type: TokenType.Is))
            {
                // Parse type kind constraints: T is record/entity/routine/choice/variant
                // Or const generic type constraints: N is uaddr/s32/etc.
                // Note: Suflae does not have 'resident' or 'mutant' - those are RazorForge only
                if (Match(type: TokenType.Record))
                {
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                        ConstraintType: ConstraintKind.ValueType,
                        ConstraintTypes: null,
                        Location: location));
                }
                else if (Match(type: TokenType.Entity))
                {
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                        ConstraintType: ConstraintKind.ReferenceType,
                        ConstraintTypes: null,
                        Location: location));
                }
                else if (Match(type: TokenType.Routine))
                {
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                        ConstraintType: ConstraintKind.RoutineType,
                        ConstraintTypes: null,
                        Location: location));
                }
                else if (Match(type: TokenType.Choice))
                {
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                        ConstraintType: ConstraintKind.ChoiceType,
                        ConstraintTypes: null,
                        Location: location));
                }
                else if (Match(type: TokenType.Variant))
                {
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                        ConstraintType: ConstraintKind.VariantType,
                        ConstraintTypes: null,
                        Location: location));
                }
                else if (Check(type: TokenType.Identifier) || Check(type: TokenType.TypeIdentifier))
                {
                    // Const generic constraint: N is uaddr
                    // Type validation happens in semantic analysis, not parsing
                    TypeExpression constType = ParseType();
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                        ConstraintType: ConstraintKind.ConstGeneric,
                        ConstraintTypes: [constType],
                        Location: location));
                }
                else
                {
                    throw ThrowParseError("Expected 'record', 'entity', 'routine', 'choice', 'variant', or type after 'is' in inline constraint");
                }
            }
            else if (Match(type: TokenType.In))
            {
                // T in [s32, s64, u32] - type equality constraint with list syntax
                Consume(type: TokenType.LeftBracket, errorMessage: "Expected '[' after 'in' for type equality constraint");

                var equalityTypes = new List<TypeExpression>();
                do
                {
                    equalityTypes.Add(item: ParseType());
                } while (Match(type: TokenType.Comma));

                Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after type list");

                inlineConstraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                    ConstraintType: ConstraintKind.TypeEquality,
                    ConstraintTypes: equalityTypes,
                    Location: location));
            }
            // No constraint for this parameter, continue
        } while (Match(type: TokenType.Comma));

        return (genericParams, inlineConstraints.Count > 0
            ? inlineConstraints
            : null);
    }

    /// <summary>
    /// Parses generic constraints for type parameters.
    /// Supports inline constraints (T follows Protocol) and where clauses.
    /// </summary>
    private List<GenericConstraintDeclaration>? ParseGenericConstraints(List<string>? genericParams, List<GenericConstraintDeclaration>? existingConstraints = null)
    {
        if (genericParams == null || genericParams.Count == 0)
        {
            return existingConstraints;
        }

        List<GenericConstraintDeclaration> constraints = existingConstraints != null
            ? [..existingConstraints]
            : [];

        // Parse requires clauses: requires T follows Protocol requires U is record
        // Each parameter can have its own requires clause or they can be comma-separated
        while (Match(type: TokenType.Requires))
        {
            do
            {
                SourceLocation location = GetLocation();
                string paramName = ConsumeIdentifier(errorMessage: "Expected type parameter name");

                // Verify this parameter was declared
                if (!genericParams.Contains(item: paramName))
                {
                    throw ThrowParseError($"Type parameter '{paramName}' not declared in generic parameters");
                }

                // Parse constraint kind and types
                if (Match(type: TokenType.Follows))
                {
                    // T follows Protocol1, Protocol2
                    var constraintTypes = new List<TypeExpression>();
                    do
                    {
                        constraintTypes.Add(item: ParseType());
                        // Continue if comma followed by type name that's NOT a new constraint declaration
                        // (i.e., identifier NOT followed by follows/is/in)
                    } while (Match(type: TokenType.Comma) && !IsNewConstraintDeclaration());

                    constraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                        ConstraintType: ConstraintKind.Follows,
                        ConstraintTypes: constraintTypes,
                        Location: location));
                }
                else if (Match(type: TokenType.Is))
                {
                    // T is record/entity/routine/choice/variant or N is uaddr (const generic)
                    // Note: Suflae does not have 'resident' or 'mutant' - those are RazorForge only
                    if (Match(type: TokenType.Record))
                    {
                        constraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                            ConstraintType: ConstraintKind.ValueType,
                            ConstraintTypes: null,
                            Location: location));
                    }
                    else if (Match(type: TokenType.Entity))
                    {
                        constraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                            ConstraintType: ConstraintKind.ReferenceType,
                            ConstraintTypes: null,
                            Location: location));
                    }
                    else if (Match(type: TokenType.Routine))
                    {
                        constraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                            ConstraintType: ConstraintKind.RoutineType,
                            ConstraintTypes: null,
                            Location: location));
                    }
                    else if (Match(type: TokenType.Choice))
                    {
                        constraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                            ConstraintType: ConstraintKind.ChoiceType,
                            ConstraintTypes: null,
                            Location: location));
                    }
                    else if (Match(type: TokenType.Variant))
                    {
                        constraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                            ConstraintType: ConstraintKind.VariantType,
                            ConstraintTypes: null,
                            Location: location));
                    }
                    else if (Check(type: TokenType.Identifier) || Check(type: TokenType.TypeIdentifier))
                    {
                        // Const generic constraint: N is uaddr
                        // Type validation happens in semantic analysis, not parsing
                        TypeExpression constType = ParseType();
                        constraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                            ConstraintType: ConstraintKind.ConstGeneric,
                            ConstraintTypes: [constType],
                            Location: location));
                    }
                    else
                    {
                        throw ThrowParseError("Expected 'record', 'entity', 'routine', 'choice', 'variant', or type after 'is' in constraint");
                    }
                }
                else if (Match(type: TokenType.In))
                {
                    // T in [s32, s64, u32] - type equality constraint with list syntax
                    Consume(type: TokenType.LeftBracket, errorMessage: "Expected '[' after 'in' for type equality constraint");

                    var equalityTypes = new List<TypeExpression>();
                    do
                    {
                        equalityTypes.Add(item: ParseType());
                    } while (Match(type: TokenType.Comma));

                    Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after type list");

                    constraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                        ConstraintType: ConstraintKind.TypeEquality,
                        ConstraintTypes: equalityTypes,
                        Location: location));
                }
                else
                {
                    throw ThrowParseError("Expected 'follows', 'is', or 'in' in generic constraint");
                }

                // Continue parsing if there's a comma
            } while (Match(type: TokenType.Comma));
        }

        return constraints.Count > 0
            ? constraints
            : null;
    }

    /// <summary>
    /// Checks if the current position looks like a new constraint declaration (Identifier follows/is/in).
    /// Used to distinguish between "K follows A, B" (K follows both A and B) and
    /// "K follows A, U follows B" (K follows A, then U follows B).
    /// </summary>
    private bool IsNewConstraintDeclaration()
    {
        // Must start with an identifier (type parameter name)
        if (!Check(TokenType.Identifier, TokenType.TypeIdentifier))
        {
            return false;
        }

        // Lookahead: check if identifier is followed by a constraint keyword
        Token next = PeekToken(offset: 1);
        return next.Type is TokenType.Follows or TokenType.Is or TokenType.In;
    }
}
