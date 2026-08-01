using System.Collections.Generic;
using Compiler.Diagnostics;
using Compiler.Tokenizer;
using SyntaxTree;

namespace Compiler.Parser;

/// <summary>
/// Partial class containing type parsing and generic constraints.
/// </summary>
public partial class Parser
{
    /// <summary>
    /// Parses a type expression.
    /// Supports: named types, generic types (Type[T]),
    /// Me (self type), and nullable types (T? = Maybe[T]).
    /// </summary>
    /// <remarks>
    /// The old `T` rvalue-entity prefix was removed 2026-07-13: it was redundant with position and
    /// the move-vs-link distinction (bare entity = move, borrow-wrapper = link), and confusable with
    /// the `T?` Maybe suffix. Entity rvalue-ness is now inferred (see SignatureResolver return
    /// inference); moves are marked by `steal` at use sites.
    /// </remarks>
    /// <returns>A <see cref="TypeExpression"/> AST node.</returns>
    private TypeExpression ParseType()
    {
        TypeExpression baseType = ParseBaseType();

        // Handle nullable suffix: T? Maybe[T]
        if (Match(type: TokenType.Question))
        {
            return new TypeExpression(Name: "Maybe",
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
    /// 1. Me - Self type in protocols/member routines
    /// 2. Name[T, U] - Generic named type
    /// 3. Name - Simple named type
    ///
    /// Named types support qualified paths like razorforge/Collections.Dict
    /// for referencing types from other modules in type annotations.
    /// </remarks>
    private TypeExpression ParseBaseType()
    {
        SourceLocation location = GetLocation();

        // ═══════════════════════════════════════════════════════════════════════════
        // CASE 1: Me - self type in protocols/member routines (like Self in Rust)
        // ═══════════════════════════════════════════════════════════════════════════
        if (Match(type: TokenType.MyType))
        {
            // `Me` may be followed by an associated-type projection: `Me/Iter`, `Me/Iter/Inner`.
            // Carry it in the flattened name; the resolver walks `/` segments (Me → owner type,
            // then each following segment is an associated-type projection).
            if (Check(type: TokenType.Slash))
            {
                var meSb = new System.Text.StringBuilder("Me");
                while (Match(type: TokenType.Slash))
                {
                    meSb.Append('/');
                    meSb.Append(ConsumeIdentifier(
                        errorMessage: "Expected associated-type name after '/' in projection"));
                }
                return new TypeExpression(Name: meSb.ToString(), GenericArguments: null,
                    Location: location);
            }
            return new TypeExpression(Name: "Me", GenericArguments: null, Location: location);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // CASE 2: Tuple type - (T, U) or (T,)
        // ═══════════════════════════════════════════════════════════════════════════
        if (Match(type: TokenType.LeftParen))
        {
            var elementTypes = new List<TypeExpression>();
            elementTypes.Add(item: ParseType());

            if (!Match(type: TokenType.Comma))
            {
                // Single parenthesized type without comma: just (T)
                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after type");
                return elementTypes[index: 0];
            }

            // Single-element tuple: (T,)
            if (Check(type: TokenType.RightParen))
            {
                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after tuple type");
                return new TypeExpression(Name: "Tuple",
                    GenericArguments: elementTypes,
                    Location: location);
            }

            // Multi-element tuple: (T, U, ...)
            do
            {
                elementTypes.Add(item: ParseType());
            } while (Match(type: TokenType.Comma) && !Check(type: TokenType.RightParen));

            Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after tuple type");
            return new TypeExpression(Name: "Tuple",
                GenericArguments: elementTypes,
                Location: location);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // CASE 4/5: Named type - simple or generic
        // ═══════════════════════════════════════════════════════════════════════════
        // Forms:
        // User simple type
        // List[T] generic type
        // Dict[Text, S32] multi-param generic
        // FixedBytes[4] const generic (number as type arg)
        if (!Match(type: TokenType.Identifier))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedType,
                message: $"Expected type, got {CurrentToken.Type} ('{CurrentToken.Text}')");
        }

        string name = PeekToken(offset: -1)
           .Text;

        // Support qualified type paths like RazorForge/Collections.Dict
        // This allows referencing types from other modules in type annotations
        var nameSb = new System.Text.StringBuilder(name);
        while (Match(type: TokenType.Slash))
        {
            nameSb.Append('/');
            nameSb.Append(ConsumeIdentifier(errorMessage: "Expected module path component after '/'"));

            // Dot separates the type name from the slash-based module path: razorforge/Core.Bool
            if (Match(type: TokenType.Dot))
            {
                nameSb.Append('.');
                nameSb.Append(ConsumeIdentifier(errorMessage: "Expected type name after '.'"));
                break; // Dot marks the end of the path (rest is the type name)
            }
        }
        name = nameSb.ToString();

        // ─────────────────────────────────────────────────────────────────────
        // Simple type without generics
        // ─────────────────────────────────────────────────────────────────────
        if (!Match(type: TokenType.LeftBracket))
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

        Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after type arguments");

        return new TypeExpression(Name: name, GenericArguments: typeArgs, Location: location);

    }

    /// <summary>
    /// Parses a type expression or a const generic literal.
    /// Used for generic arguments like FixedBytes[4].
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
        if (Match(TokenType.UndecidedInteger,
                TokenType.IntegerLiteral,
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
                TokenType.S256Literal,
                TokenType.U256Literal,
                TokenType.AddressLiteral))
        {
            string value = PeekToken(offset: -1)
               .Text;
            return new TypeExpression(Name: value, GenericArguments: null, Location: location);
        }

        // Check for letter/character literal (const generic)
        if (Match(TokenType.CharacterLiteral, TokenType.ByteLetterLiteral))
        {
            string value = PeekToken(offset: -1)
               .Text;
            return new TypeExpression(Name: value, GenericArguments: null, Location: location);
        }

        // Otherwise parse as normal type
        return ParseType();
    }

    /// <summary>
    /// Parses a single bracket argument uniformly as an expression, for the parser's
    /// classification-free <see cref="SyntaxTree.BracketAccessExpression"/>.
    /// <see cref="BracketReclassifyPass"/> later decides whether the whole bracket is a generic
    /// type-argument list or a value index and, in the generic case, converts each argument to a
    /// <see cref="TypeExpression"/> via <c>ExpressionToTypeArg</c>.
    /// </summary>
    /// <remarks>
    /// The one grammar gap between "expression" and "type argument" is the self-TYPE token
    /// <c>Me</c> (<see cref="TokenType.MyType"/>, and its projections <c>Me/Iter</c>), which is not
    /// a valid expression primary. Those are handled here by materializing <c>Me</c>/<c>Me/Iter</c>
    /// as an identifier / <c>/</c>-chain expression — structurally identical to how <c>S/Iter</c>
    /// parses — so the reclassifier's <c>ExpressionToTypeArg</c> flattens both uniformly. Note this
    /// is the capitalized self-TYPE only; the lowercase <c>me</c> receiver
    /// (<see cref="TokenType.Me"/>) is a normal expression and is NOT intercepted here (so
    /// <c>me.list[me.index]</c> parses its inner <c>me.index</c> as an ordinary member access).
    /// Everything else (<c>S64</c>, <c>i+1</c>, <c>4</c>, <c>List[S64]</c>, <c>S/Iter</c>) is a
    /// plain expression.
    /// </remarks>
    private Expression ParseBracketArg()
    {
        if (Check(type: TokenType.MyType))
        {
            SourceLocation location = GetLocation();
            string headText = CurrentToken.Text;
            Advance(); // consume Me / MyType
            Expression head = new IdentifierExpression(Name: headText, Location: location);

            // Projection chain: Me/Iter, Me/Iter/Inner — modeled as a left-nested `/` chain,
            // matching the shape ordinary `S/Iter` produces from expression parsing.
            while (Match(type: TokenType.Slash))
            {
                string seg = ConsumeIdentifier(
                    errorMessage: "Expected associated-type name after '/' in projection");
                head = new BinaryExpression(Left: head,
                    Operator: BinaryOperator.TrueDivide,
                    Right: new IdentifierExpression(Name: seg, Location: location),
                    Location: location);
            }

            return head;
        }

        return ParseExpression();
    }

    /// <summary>
    /// Parses generic parameters with optional inline constraints like [T obeys Integral].
    /// Returns both the parameter names and any inline constraints found.
    /// </summary>
    /// <remarks>
    /// Inline constraint forms (inside brackets):
    ///
    /// PROTOCOL CONSTRAINTS (obeys):
    /// [T obeys Comparable] - Single protocol
    /// [T obeys Comparable, Hashable] - Multiple protocols
    ///
    /// TYPE KIND CONSTRAINTS (is):
    /// [T is record] - Must be a value type (record)
    /// [T is entity] - Must be a reference type (entity)
    /// [T is routine] - Must be a routine type
    /// [T is choice] - Must be a choice type
    /// [T is variant] - Must be a variant type
    /// [N is S32] - Const generic (N is a build-time constant of type S32)
    ///
    /// TYPE EQUALITY CONSTRAINTS (in):
    /// [T in [S32, S64, F64]] - T must be one of the listed types
    ///
    /// DISAMBIGUATION CHALLENGE:
    /// When parsing "T obeys A, B", we need to distinguish between:
    ///  - Multiple protocols for same param: [T obeys A, B]
    ///  - Next parameter with constraint: [T obeys A, U obeys B]
    /// We look ahead to check if the next identifier has obeys/is/in after it.
    /// </remarks>
    private (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints)
        ParseGenericParametersWithConstraints()
    {
        var genericParams = new List<string>();
        var inlineConstraints = new List<GenericConstraintDeclaration>();

        // ═══════════════════════════════════════════════════════════════════════════
        // Parse each generic parameter with optional inline constraint
        // ═══════════════════════════════════════════════════════════════════════════
        do
        {
            SourceLocation location = GetLocation();
            string paramName = ConsumeIdentifier(errorMessage: "Expected generic parameter name");
            genericParams.Add(item: paramName);

            // ─────────────────────────────────────────────────────────────────────
            // CONSTRAINT TYPE 1: obeys - protocol conformance
            // ─────────────────────────────────────────────────────────────────────
            // Forms: T obeys Protocol
            // T obeys Protocol1, Protocol2 (multiple protocols)
            if (Match(type: TokenType.Obeys))
            {
                var constraintTypes = new List<TypeExpression>();
                do
                {
                    constraintTypes.Add(item: ParseType());
                    // Continue if comma but next token is NOT an identifier followed by obeys/is/in or greater
                    // This handles both "T obeys A, B" (multiple protocols) and "T obeys A, U obeys B" (next param)
                } while (Match(type: TokenType.Comma) && !Check(type: TokenType.RightBracket) &&
                         !(Check(type: TokenType.Identifier) && (PeekToken(offset: 1)
                            .Type == TokenType.Obeys || PeekToken(offset: 1)
                            .Type == TokenType.Is || PeekToken(offset: 1)
                            .Type == TokenType.In)));

                inlineConstraints.Add(item: new GenericConstraintDeclaration(
                    ParameterName: paramName,
                    ConstraintType: ConstraintKind.Obeys,
                    ConstraintTypes: constraintTypes,
                    Location: location));
            }
            // ─────────────────────────────────────────────────────────────────────
            // CONSTRAINT TYPE 2: is - type kind or const generic
            // ─────────────────────────────────────────────────────────────────────
            // Type kinds: T is record/entity/routine/choice/variant
            // Const generic: N is S32 (N is a build-time S32 value)
            else if (Match(type: TokenType.Is))
            {
                if (Match(type: TokenType.Record))
                {
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(
                        ParameterName: paramName,
                        ConstraintType: ConstraintKind.ValueType,
                        ConstraintTypes: null,
                        Location: location));
                }
                else if (Match(type: TokenType.Entity))
                {
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(
                        ParameterName: paramName,
                        ConstraintType: ConstraintKind.ReferenceType,
                        ConstraintTypes: null,
                        Location: location));
                }
                else if (Match(type: TokenType.Routine))
                {
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(
                        ParameterName: paramName,
                        ConstraintType: ConstraintKind.RoutineType,
                        ConstraintTypes: null,
                        Location: location));
                }
                else if (Match(type: TokenType.Choice))
                {
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(
                        ParameterName: paramName,
                        ConstraintType: ConstraintKind.ChoiceType,
                        ConstraintTypes: null,
                        Location: location));
                }
                else if (Match(type: TokenType.Flags))
                {
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(
                        ParameterName: paramName,
                        ConstraintType: ConstraintKind.FlagsType,
                        ConstraintTypes: null,
                        Location: location));
                }
                else if (Match(type: TokenType.Variant))
                {
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(
                        ParameterName: paramName,
                        ConstraintType: ConstraintKind.VariantType,
                        ConstraintTypes: null,
                        Location: location));
                }
                else if (Match(type: TokenType.Crashable))
                {
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(
                        ParameterName: paramName,
                        ConstraintType: ConstraintKind.Crashable,
                        ConstraintTypes: null,
                        Location: location));
                }
                else if (Check(type: TokenType.Identifier))
                {
                    // Const generic constraint: N is Address
                    // Type validation happens in semantic analysis, not parsing
                    TypeExpression constType = ParseType();
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(
                        ParameterName: paramName,
                        ConstraintType: ConstraintKind.ConstGeneric,
                        ConstraintTypes: [constType],
                        Location: location));
                }
                else
                {
                    throw ThrowParseError(code: GrammarDiagnosticCode.InvalidConstraintKind,
                        message:
                        "Expected 'record', 'entity', 'routine', 'choice', 'flags', 'variant', or type after 'is' in inline constraint");
                }
            }
            // ─────────────────────────────────────────────────────────────────────
            // CONSTRAINT TYPE 3: in - type equality (must be one of listed types)
            // ─────────────────────────────────────────────────────────────────────
            // Form: T in [S32, S64, F64]
            else if (Match(type: TokenType.In))
            {
                Consume(type: TokenType.LeftBracket,
                    errorMessage: "Expected '[' after 'in' for type equality constraint");

                var equalityTypes = new List<TypeExpression>();
                do
                {
                    equalityTypes.Add(item: ParseType());
                } while (Match(type: TokenType.Comma));

                Consume(type: TokenType.RightBracket,
                    errorMessage: "Expected ']' after type list");

                inlineConstraints.Add(item: new GenericConstraintDeclaration(
                    ParameterName: paramName,
                    ConstraintType: ConstraintKind.TypeEquality,
                    ConstraintTypes: equalityTypes,
                    Location: location));
            }
            // No constraint for this parameter, continue to next
        } while (Match(type: TokenType.Comma));

        return (genericParams, inlineConstraints.Count > 0
            ? inlineConstraints
            : null);
    }

    /// <summary>
    /// Parses generic constraints for type parameters using 'needs' clauses.
    /// Called after generic parameters have been parsed.
    /// </summary>
    /// <remarks>
    /// This parses the EXTERNAL needs clause form (after brackets):
    ///
    /// Example:
    /// record Container[T, U]
    /// needs T obeys Comparable, U is entity
    ///  ...
    ///
    /// The same constraint kinds are supported as inline constraints:
    /// - obeys: protocol conformance
    /// - is: type kind (record/entity/routine/choice/variant) or const generic
    /// - in: type equality (must be one of listed types)
    ///
    /// Multiple needs clauses can be chained, or constraints can be comma-separated:
    /// needs T obeys A needs U obeys B (chained)
    /// needs T obeys A, U obeys B (comma-separated)
    /// </remarks>
    private List<GenericConstraintDeclaration>? ParseGenericConstraints(
        List<string>? genericParams,
        List<GenericConstraintDeclaration>? existingConstraints = null)
    {
        // Allow needs clauses even without explicit generic params (implicit generics from parameter types)
        // But only if there's actually a 'needs' keyword ahead — peek through newlines
        if (genericParams == null || genericParams.Count == 0)
        {
            int offset = 0;
            while (PeekToken(offset: offset)
                      .Type == TokenType.Newline)
            {
                offset++;
            }

            if (PeekToken(offset: offset)
                   .Type != TokenType.Needs)
            {
                return existingConstraints;
            }

            // Initialize genericParams so constraint parsing works
            genericParams ??= [];
        }

        List<GenericConstraintDeclaration> constraints = existingConstraints != null
            ? [..existingConstraints]
            : [];

        // ═══════════════════════════════════════════════════════════════════════════
        // Parse needs clauses: needs T obeys Protocol
        // ═══════════════════════════════════════════════════════════════════════════
        // Each parameter can have its own needs clause or they can be comma-separated
        // Skip newlines between needs clauses only when 'needs' obeys
        while (SkipNewlinesIfFollowedBy(type: TokenType.Needs) &&
               Match(type: TokenType.Needs))
        {
            do
            {
                SourceLocation location = GetLocation();
                string paramName = ConsumeIdentifier(errorMessage: "Expected type parameter name");

                // Note: Type parameter validation (whether paramName is in genericParams)
                // is intentionally deferred to semantic analysis for better error reporting.

                // ─────────────────────────────────────────────────────────────────────
                // Parse constraint kind and types (same logic as inline constraints)
                // ─────────────────────────────────────────────────────────────────────
                if (Match(type: TokenType.Obeys))
                {
                    // T obeys Protocol1, Protocol2
                    var constraintTypes = new List<TypeExpression>();
                    constraintTypes.Add(item: ParseType());
                    while (Check(type: TokenType.Comma))
                    {
                        // Peek PAST the comma (and any newlines): if a new "Param obeys/is/in"
                        // constraint follows, this comma separates whole constraints — leave it
                        // unconsumed for the outer constraint-separator loop. Consuming it here
                        // (the old bug) dropped the next constraint, e.g. the `U obeys B` in
                        // `needs T obeys A, U obeys B`, on routines and types alike.
                        int peek = 1;
                        while (PeekToken(offset: peek).Type == TokenType.Newline)
                        {
                            peek++;
                        }

                        if (PeekToken(offset: peek).Type == TokenType.Identifier &&
                            PeekToken(offset: peek + 1).Type is TokenType.Obeys or TokenType.Is
                                or TokenType.In)
                        {
                            break;
                        }

                        Match(type: TokenType.Comma);
                        while (Match(type: TokenType.Newline)) { } // NOSONAR S108: intentional newline-consuming loop
                        constraintTypes.Add(item: ParseType());
                    }

                    constraints.Add(item: new GenericConstraintDeclaration(
                        ParameterName: paramName,
                        ConstraintType: ConstraintKind.Obeys,
                        ConstraintTypes: constraintTypes,
                        Location: location));
                }
                else if (Match(type: TokenType.Is))
                {
                    // T is record/entity/routine/choice/variant or N is Address (const generic)
                    if (Match(type: TokenType.Record))
                    {
                        constraints.Add(item: new GenericConstraintDeclaration(
                            ParameterName: paramName,
                            ConstraintType: ConstraintKind.ValueType,
                            ConstraintTypes: null,
                            Location: location));
                    }
                    else if (Match(type: TokenType.Entity))
                    {
                        constraints.Add(item: new GenericConstraintDeclaration(
                            ParameterName: paramName,
                            ConstraintType: ConstraintKind.ReferenceType,
                            ConstraintTypes: null,
                            Location: location));
                    }
                    else if (Match(type: TokenType.Routine))
                    {
                        constraints.Add(item: new GenericConstraintDeclaration(
                            ParameterName: paramName,
                            ConstraintType: ConstraintKind.RoutineType,
                            ConstraintTypes: null,
                            Location: location));
                    }
                    else if (Match(type: TokenType.Choice))
                    {
                        constraints.Add(item: new GenericConstraintDeclaration(
                            ParameterName: paramName,
                            ConstraintType: ConstraintKind.ChoiceType,
                            ConstraintTypes: null,
                            Location: location));
                    }
                    else if (Match(type: TokenType.Flags))
                    {
                        constraints.Add(item: new GenericConstraintDeclaration(
                            ParameterName: paramName,
                            ConstraintType: ConstraintKind.FlagsType,
                            ConstraintTypes: null,
                            Location: location));
                    }
                    else if (Match(type: TokenType.Variant))
                    {
                        constraints.Add(item: new GenericConstraintDeclaration(
                            ParameterName: paramName,
                            ConstraintType: ConstraintKind.VariantType,
                            ConstraintTypes: null,
                            Location: location));
                    }
                    else if (Match(type: TokenType.Crashable))
                    {
                        constraints.Add(item: new GenericConstraintDeclaration(
                            ParameterName: paramName,
                            ConstraintType: ConstraintKind.Crashable,
                            ConstraintTypes: null,
                            Location: location));
                    }
                    else if (Check(type: TokenType.Identifier))
                    {
                        // Const generic constraint: N is Address
                        // Type validation happens in semantic analysis, not parsing
                        TypeExpression constType = ParseType();
                        constraints.Add(item: new GenericConstraintDeclaration(
                            ParameterName: paramName,
                            ConstraintType: ConstraintKind.ConstGeneric,
                            ConstraintTypes: [constType],
                            Location: location));
                    }
                    else
                    {
                        throw ThrowParseError(code: GrammarDiagnosticCode.InvalidConstraintKind,
                            message:
                            "Expected 'record', 'entity', 'routine', 'choice', 'flags', 'variant', 'crashable', or type after 'is' in constraint");
                    }
                }
                else if (Match(type: TokenType.In))
                {
                    // T in [s32, s64, u32] - type equality constraint with list syntax
                    Consume(type: TokenType.LeftBracket,
                        errorMessage: "Expected '[' after 'in' for type equality constraint");

                    var equalityTypes = new List<TypeExpression>();
                    do
                    {
                        equalityTypes.Add(item: ParseType());
                    } while (Match(type: TokenType.Comma));

                    Consume(type: TokenType.RightBracket,
                        errorMessage: "Expected ']' after type list");

                    constraints.Add(item: new GenericConstraintDeclaration(
                        ParameterName: paramName,
                        ConstraintType: ConstraintKind.TypeEquality,
                        ConstraintTypes: equalityTypes,
                        Location: location));
                }
                else
                {
                    throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedConstraintType,
                        message: "Expected 'obeys', 'is', or 'in' in generic constraint");
                }

                // Continue parsing if there's a comma
            } while (Match(type: TokenType.Comma));
        }

        return constraints.Count > 0
            ? constraints
            : null;
    }

    /// <summary>
    /// Parses <c>relates</c> clauses on a type declaration — a <c>needs</c>-sibling clause placed
    /// after the header (and any <c>needs</c>), before the indented body. Two forms:
    /// <list type="bullet">
    ///   <item>Protocol slot declaration: <c>relates Iter obeys Iterator[T]</c></item>
    ///   <item>Implementer binding: <c>relates ListEmitter[T] as Iter</c></item>
    /// </list>
    /// Returns the accumulated list (merged with <paramref name="existing"/>), or null if none.
    /// </summary>
    private List<AssociatedTypeDeclaration>? ParseRelatesClauses(
        List<AssociatedTypeDeclaration>? existing = null)
    {
        List<AssociatedTypeDeclaration> related = existing != null ? [..existing] : [];

        // Each clause may be preceded by doc comments and blank lines (a slot is often
        // documented just like a member). Only commit to consuming that trivia once a
        // `relates` keyword is confirmed to follow, so trivia before the indented body
        // (which has no `relates`) is left intact for the body parser.
        while (true)
        {
            int offset = 0;
            while (PeekToken(offset: offset).Type is TokenType.Newline or TokenType.DocComment)
            {
                offset++;
            }

            if (PeekToken(offset: offset).Type != TokenType.Relates)
            {
                break;
            }

            while (Match(TokenType.Newline, TokenType.DocComment)) { } // NOSONAR S108
            Match(type: TokenType.Relates);

            SourceLocation location = GetLocation();

            // Parse the first token group as a type. For a slot declaration it is a bare
            // identifier (the slot name); for a binding it is the concrete type.
            TypeExpression first = ParseType();

            if (Match(type: TokenType.Obeys))
            {
                // Constrained slot declaration: `relates Iter obeys Iterator[T]`.
                TypeExpression constraint = ParseType();
                related.Add(item: new AssociatedTypeDeclaration(
                    Name: first.Name,
                    Constraint: constraint,
                    Binding: null,
                    Location: location));
            }
            else if (Match(type: TokenType.As))
            {
                // Implementer binding: `relates ListEmitter[T] as Iter`.
                string slotName = ConsumeIdentifier(
                    errorMessage: "Expected associated-type name after 'as' in 'relates' clause");
                related.Add(item: new AssociatedTypeDeclaration(
                    Name: slotName,
                    Constraint: null,
                    Binding: first,
                    Location: location));
            }
            else
            {
                // Bare slot declaration: `relates Key` — an associated type with no
                // constraint and no binding (the implementer supplies it via `relates ... as`).
                related.Add(item: new AssociatedTypeDeclaration(
                    Name: first.Name,
                    Constraint: null,
                    Binding: null,
                    Location: location));
            }
        }

        return related.Count > 0 ? related : null;
    }

    /// <summary>
    /// Checks if the current position looks like a new constraint declaration (Identifier obeys/is/in).
    /// Used to distinguish between "K obeys A, B" (K obeys both A and B) and
    /// "K obeys A, U obeys B" (K obeys A, then U obeys B).
    /// </summary>
    private bool IsNewConstraintDeclaration()
    {
        // Must start with an identifier (type parameter name)
        if (!Check(type: TokenType.Identifier))
        {
            return false;
        }

        // Lookahead: check if identifier is followed by a constraint keyword
        Token next = PeekToken(offset: 1);
        return next.Type is TokenType.Obeys or TokenType.Is or TokenType.In;
    }

}
