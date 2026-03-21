using SyntaxTree;
using Compiler.Lexer;
using Compiler.Diagnostics;

namespace Compiler.Parser;

/// <summary>
/// Partial class containing type parsing and generic constraints.
/// </summary>
public partial class Parser
{
    /// <summary>
    /// Parses a type expression.
    /// Supports: named types, generic types (Type[T]),
    /// Me (self type), and nullable types (T?).
    /// </summary>
    /// <returns>A <see cref="TypeExpression"/> AST node.</returns>
    private TypeExpression ParseType()
    {
        TypeExpression baseType = ParseBaseType();

        // Handle nullable suffix: T? -> Maybe[T]
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
    /// 2. @intrinsic.xxx   - LLVM IR intrinsic types (RazorForge stdlib)
    /// 3. Name[T, U]       - Generic named type
    /// 4. Name             - Simple named type
    ///
    /// Named types support qualified paths like razorforge/Collections.Dict
    /// for referencing types from other modules in type annotations.
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
        // CASE 2: Intrinsic type - direct LLVM IR types
        // ═══════════════════════════════════════════════════════════════════════════
        // Forms: @intrinsic.i1, @intrinsic.i32, @intrinsic.f64, @intrinsic.iptr, @intrinsic.uptr
        if (Match(type: TokenType.Intrinsic))
        {
            Consume(type: TokenType.Dot, errorMessage: "Expected '.' after '@intrinsic'");

            // Allow any identifier as intrinsic type name (i1, i8, i16, i32, i64, i128, f16, f32, f64, f128, iptr, uptr, etc.)
            if (!Match(TokenType.Identifier))
            {
                throw new GrammarException(
                    GrammarDiagnosticCode.ExpectedIdentifier,
                    $"Expected intrinsic type name after '@intrinsic.', got {CurrentToken.Type}",
                    fileName, CurrentToken.Line, CurrentToken.Column, _language);
            }

            string intrinsicName = PeekToken(offset: -1).Text;
            return new TypeExpression(Name: $"@intrinsic.{intrinsicName}", GenericArguments: null, Location: location);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // CASE 3/4: Named type - simple or generic
        // ═══════════════════════════════════════════════════════════════════════════
        // Forms:
        //   User                  -> simple type
        //   List[T]               -> generic type
        //   Dict[Text, S32]       -> multi-param generic
        //   FixedBytes[4]         -> const generic (number as type arg)
        if (!Match(TokenType.Identifier))
        {
            throw ThrowParseError(GrammarDiagnosticCode.ExpectedType,
                $"Expected type, got {CurrentToken.Type} ('{CurrentToken.Text}')");
        }

        string name = PeekToken(offset: -1)
           .Text;

        // Support qualified type paths like RazorForge/Collections.Dict
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

        // Otherwise parse as normal type
        return ParseType();
    }

    /// <summary>
    /// Parses generic parameters with optional inline constraints like [T obeys Integral].
    /// Returns both the parameter names and any inline constraints found.
    /// </summary>
    /// <remarks>
    /// Inline constraint forms (inside brackets):
    ///
    /// PROTOCOL CONSTRAINTS (obeys):
    ///   [T obeys Comparable]           - Single protocol
    ///   [T obeys Comparable, Hashable]  - Multiple protocols
    ///
    /// TYPE KIND CONSTRAINTS (is):
    ///   [T is record]    - Must be a value type (record)
    ///   [T is entity]    - Must be a reference type (entity)
    ///   [T is resident]  - Must be a resident type
    ///   [T is routine]   - Must be a routine type
    ///   [T is choice]    - Must be a choice type
    ///   [T is variant]   - Must be a variant type
    ///   [N is S32]       - Const generic (N is a build-time constant of type S32)
    ///
    /// TYPE EQUALITY CONSTRAINTS (in):
    ///   [T in [S32, S64, F64]]  - T must be one of the listed types
    ///
    /// DISAMBIGUATION CHALLENGE:
    /// When parsing "T obeys A, B", we need to distinguish between:
    ///   - Multiple protocols for same param: [T obeys A, B]
    ///   - Next parameter with constraint: [T obeys A, U obeys B]
    /// We look ahead to check if the next identifier has obeys/is/in after it.
    /// </remarks>
    private (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) ParseGenericParametersWithConstraints()
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
            //        T obeys Protocol1, Protocol2  (multiple protocols)
            if (Match(type: TokenType.Obeys))
            {
                var constraintTypes = new List<TypeExpression>();
                do
                {
                    constraintTypes.Add(item: ParseType());
                    // Continue if comma but next token is NOT an identifier followed by obeys/is/in or greater
                    // This handles both "T obeys A, B" (multiple protocols) and "T obeys A, U obeys B" (next param)
                } while (Match(type: TokenType.Comma) && !Check(type: TokenType.RightBracket) && !(Check(type: TokenType.Identifier) && (PeekToken(offset: 1)
                            .Type == TokenType.Obeys || PeekToken(offset: 1)
                            .Type == TokenType.Is || PeekToken(offset: 1)
                            .Type == TokenType.In)));

                inlineConstraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                    ConstraintType: ConstraintKind.Obeys,
                    ConstraintTypes: constraintTypes,
                    Location: location));
            }
            // ─────────────────────────────────────────────────────────────────────
            // CONSTRAINT TYPE 2: is - type kind or const generic
            // ─────────────────────────────────────────────────────────────────────
            // Type kinds: T is record/entity/resident/routine/choice/variant
            // Const generic: N is S32 (N is a build-time S32 value)
            else if (Match(type: TokenType.Is))
            {
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
                else if (Match(type: TokenType.Resident))
                {
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                        ConstraintType: ConstraintKind.ResidentType,
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
                else if (Match(type: TokenType.Flags))
                {
                    inlineConstraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                        ConstraintType: ConstraintKind.FlagsType,
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
                else if (Check(type: TokenType.Identifier))
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
                    throw ThrowParseError(GrammarDiagnosticCode.InvalidConstraintKind,
                        "Expected 'record', 'entity', 'resident', 'routine', 'choice', 'flags', 'variant', or type after 'is' in inline constraint");
                }
            }
            // ─────────────────────────────────────────────────────────────────────
            // CONSTRAINT TYPE 3: in - type equality (must be one of listed types)
            // ─────────────────────────────────────────────────────────────────────
            // Form: T in [S32, S64, F64]
            else if (Match(type: TokenType.In))
            {
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
    ///   record Container[T, U]
    ///   needs T obeys Comparable, U is entity
    ///     ...
    ///
    /// The same constraint kinds are supported as inline constraints:
    /// - obeys: protocol conformance
    /// - is: type kind (record/entity/resident/routine/choice/variant) or const generic
    /// - in: type equality (must be one of listed types)
    ///
    /// Multiple needs clauses can be chained, or constraints can be comma-separated:
    ///   needs T obeys A needs U obeys B    (chained)
    ///   needs T obeys A, U obeys B            (comma-separated)
    /// </remarks>
    private List<GenericConstraintDeclaration>? ParseGenericConstraints(List<string>? genericParams, List<GenericConstraintDeclaration>? existingConstraints = null)
    {
        if (genericParams == null || genericParams.Count == 0)
        {
            return existingConstraints;
        }

        List<GenericConstraintDeclaration> constraints = existingConstraints != null
            ? [..existingConstraints]
            : [];

        // ═══════════════════════════════════════════════════════════════════════════
        // Parse needs clauses: needs T obeys Protocol
        // ═══════════════════════════════════════════════════════════════════════════
        // Each parameter can have its own needs clause or they can be comma-separated
        // Skip newlines between needs clauses only when 'needs' obeys
        while (SkipNewlinesIfFollowedBy(type: TokenType.Requires) && Match(type: TokenType.Requires))
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
                    do
                    {
                        constraintTypes.Add(item: ParseType());
                        // Continue if comma followed by type name that's NOT a new constraint declaration
                        // (i.e., identifier NOT followed by obeys/is/in)
                    } while (Match(type: TokenType.Comma) && !IsNewConstraintDeclaration());

                    constraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                        ConstraintType: ConstraintKind.Obeys,
                        ConstraintTypes: constraintTypes,
                        Location: location));
                }
                else if (Match(type: TokenType.Is))
                {
                    // T is record/entity/resident/routine/choice/variant or N is uaddr (const generic)
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
                    else if (Match(type: TokenType.Resident))
                    {
                        constraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                            ConstraintType: ConstraintKind.ResidentType,
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
                    else if (Match(type: TokenType.Flags))
                    {
                        constraints.Add(item: new GenericConstraintDeclaration(ParameterName: paramName,
                            ConstraintType: ConstraintKind.FlagsType,
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
                    else if (Check(type: TokenType.Identifier))
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
                        throw ThrowParseError(GrammarDiagnosticCode.InvalidConstraintKind,
                            "Expected 'record', 'entity', 'resident', 'routine', 'choice', 'flags', 'variant', or type after 'is' in constraint");
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
                    throw ThrowParseError(GrammarDiagnosticCode.ExpectedConstraintType,
                        "Expected 'obeys', 'is', or 'in' in generic constraint");
                }

                // Continue parsing if there's a comma
            } while (Match(type: TokenType.Comma));
        }

        return constraints.Count > 0
            ? constraints
            : null;
    }

    /// <summary>
    /// Checks if the current position looks like a new constraint declaration (Identifier obeys/is/in).
    /// Used to distinguish between "K obeys A, B" (K obeys both A and B) and
    /// "K obeys A, U obeys B" (K obeys A, then U obeys B).
    /// </summary>
    private bool IsNewConstraintDeclaration()
    {
        // Must start with an identifier (type parameter name)
        if (!Check(TokenType.Identifier))
        {
            return false;
        }

        // Lookahead: check if identifier is followed by a constraint keyword
        Token next = PeekToken(offset: 1);
        return next.Type is TokenType.Obeys or TokenType.Is or TokenType.In;
    }
}
