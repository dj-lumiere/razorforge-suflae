using SyntaxTree;
using Compiler.Lexer;
using Compiler.Diagnostics;

namespace Compiler.Parser;

/// <summary>
/// Partial class containing declaration parsing (variables, routines, entities, records, variants, etc.).
/// Unified parser for both RazorForge and Suflae languages.
/// </summary>
public partial class Parser
{
    /// <summary>
    /// Parses annotations like @crash_only, @inline, @llvm("i32"), @[readonly, inline], etc.
    /// Annotations are prefixed with @ and followed by an identifier, optionally with arguments.
    /// Also supports compound annotations: @[attr1, attr2, attr3]
    /// </summary>
    private VariableDeclaration ParseVariableDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Open,
        StorageClass storage = StorageClass.None,
        IReadOnlyList<string>? annotations = null)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected variable name");

        TypeExpression? type = null;
        if (Match(type: TokenType.Colon))
        {
            type = ParseType();
        }

        Expression? initializer = null;
        if (Match(type: TokenType.Assign))
        {
            initializer = ParseExpression();
        }

        ConsumeStatementTerminator();

        return new VariableDeclaration(Name: name,
            Type: type,
            Initializer: initializer,
            Visibility: visibility,
            Location: location,
            Storage: storage,
            Annotations: annotations?.Count > 0 ? annotations : null);
    }

    /// <summary>
    /// Parses a member variable declaration in records.
    /// Syntax: <c>name: Type</c> or <c>public name: Type = value</c>
    /// MemberVariables are declared without var keywords.
    /// </summary>
    /// <param name="visibility">Access modifier (public, published, internal, private).</param>
    /// <returns>A <see cref="VariableDeclaration"/> AST node.</returns>
    private VariableDeclaration ParseMemberVariableDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Open)
    {
        SourceLocation location = GetLocation();

        string name = ConsumeIdentifier(errorMessage: "Expected member variable name");

        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after member variable name");
        TypeExpression type = ParseType();

        Expression? initializer = null;
        if (Match(type: TokenType.Assign))
        {
            initializer = ParseExpression();
        }

        ConsumeStatementTerminator();

        return new VariableDeclaration(Name: name,
            Type: type,
            Initializer: initializer,
            Visibility: visibility,
            Location: location);
    }

    /// <summary>
    /// Parses a routine declaration.
    /// Syntax: <c>routine name(params) -&gt; ReturnType</c> followed by indented body.
    /// Supports generic parameters, slash-based module paths, failable routines (!), and inline constraints.
    /// </summary>
    /// <remarks>
    /// Parsing phases:
    ///
    /// PHASE 1: VALIDATION
    ///   - Reject nested routine declarations
    ///
    /// PHASE 2: NAME AND FAILABLE MARKER
    ///   - Parse routine name
    ///   - Parse optional type-level generic parameters
    ///   - Parse dot-separated qualified name (for member routines)
    ///   - Check for ! suffix (failable routine)
    ///
    /// PHASE 3: PARAMETERS
    ///   - Parse parameter list: (name: Type, name: Type = default)
    ///   - Handle 'me' self-reference parameter
    ///
    /// PHASE 4: RETURN TYPE
    ///   - Optional: -> ReturnType
    ///
    /// PHASE 5: GENERIC CONSTRAINTS
    ///   - Optional: where T obeys Protocol
    ///
    /// PHASE 6: BODY
    ///   - Parse indented block
    /// </remarks>
    /// <param name="visibility">Access modifier for the routine.</param>
    /// <param name="annotations">List of annotations applied to the routine.</param>
    /// <param name="storage">Storage class modifier (default: None, can be Common for type-level static).</param>
    /// <param name="asyncStatus">Suspended or threaded status of the routine.</param>
    /// <param name="isDangerous">Whether the routine is marked as dangerous (RF only).</param>
    /// <returns>A <see cref="RoutineDeclaration"/> AST node.</returns>
    private RoutineDeclaration ParseRoutineDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Open, List<string>? annotations = null,
        StorageClass storage = StorageClass.None, AsyncStatus asyncStatus = AsyncStatus.None,
        bool isDangerous = false)
    {
        // ===============================================================================
        // PHASE 1: VALIDATION
        // ===============================================================================
        if (_inRoutineBody)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.NestedRoutineNotAllowed,
                message:
                "Nested routine declarations are not allowed. Define routines at module or type level.");
        }

        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // ===============================================================================
        // PHASE 2: NAME PARSING - Base name + optional type-level generic parameters
        // ===============================================================================
        // Examples:
        //   "foo"          -> name="foo", no generics
        //   "List[T]"      -> name="List", genericParams=["T"]
        //   "Point.get_x"  -> name="Point.get_x"
        // ===============================================================================
        string name = ConsumeIdentifier(errorMessage: "Expected routine name");

        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        bool hasGenericParams = false;

        // Check for type-level generic params BEFORE the dot (e.g., "List[T].append")
        if (Match(type: TokenType.LeftBracket))
        {
            if (HasNestedBrackets())
            {
                // Nested generics: parse as type expressions (e.g., List[DictEntry[K, V]])
                var typeArgs = new List<string>();
                do
                {
                    TypeExpression typeArg = ParseTypeOrConstGeneric();
                    typeArgs.Add(item: SerializeTypeExpression(type: typeArg));
                } while (Match(type: TokenType.Comma));

                genericParams = typeArgs;
                hasGenericParams = true;
                Consume(type: TokenType.RightBracket,
                    errorMessage: "Expected ']' after generic parameters");
            }
            else
            {
                (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints)
                    result = ParseGenericParametersWithConstraints();
                genericParams = result.genericParams;
                inlineConstraints = result.inlineConstraints;
                hasGenericParams = true;

                Consume(type: TokenType.RightBracket,
                    errorMessage: "Expected ']' after generic parameters");
            }
        }

        // ===============================================================================
        // PHASE 2b: Parse dot-separated qualified name (for member routines)
        // ===============================================================================
        // Examples:
        //   "Console.print"           -> name="Console.print"
        //   "List[T].append"          -> name="List[T].append" (generics embedded in name)
        //   "Dict[K, V].get[I]"       -> name="Dict[K, V].get", genericParams=["K","V","I"]
        // ===============================================================================
        while (Match(type: TokenType.Dot))
        {
            string part = ConsumeMethodName(errorMessage: "Expected method name after '.'");

            // If we parsed generic params before the dot, embed them in the name
            // This transforms: name="List", generics=["T"], part="append"
            //             to: name="List[T].append"
            if (hasGenericParams && !name.Contains(value: '.') && genericParams != null)
            {
                name = name + "[" + string.Join(separator: ", ", values: genericParams) + "]." +
                       part;
                hasGenericParams = false; // Only add once
            }
            else
            {
                name = name + "." + part;
            }

            // Check for member-routine-level generic params AFTER the routine name
            // e.g., "List[T].get[I]" - the [I] belongs to the member routine
            if (Match(type: TokenType.LeftBracket))
            {
                if (HasNestedBrackets())
                {
                    // Nested generics in member-routine-level params
                    var typeArgs = new List<string>();
                    do
                    {
                        TypeExpression typeArg = ParseTypeOrConstGeneric();
                        typeArgs.Add(item: SerializeTypeExpression(type: typeArg));
                    } while (Match(type: TokenType.Comma));

                    if (genericParams is { Count: > 0 })
                    {
                        genericParams = new List<string>(collection: genericParams);
                        genericParams.AddRange(collection: typeArgs);
                    }
                    else
                    {
                        genericParams = typeArgs;
                    }

                    Consume(type: TokenType.RightBracket,
                        errorMessage: "Expected ']' after generic parameters");
                }
                else
                {
                    (List<string> genericParams, List<GenericConstraintDeclaration>?
                        inlineConstraints) result = ParseGenericParametersWithConstraints();

                    // Merge type-level and member-routine-level generic parameters
                    if (genericParams is { Count: > 0 })
                    {
                        genericParams = new List<string>(collection: genericParams);
                        genericParams.AddRange(collection: result.genericParams);
                        if (inlineConstraints != null && result.inlineConstraints != null)
                        {
                            inlineConstraints =
                                new List<GenericConstraintDeclaration>(
                                    collection: inlineConstraints);
                            inlineConstraints.AddRange(collection: result.inlineConstraints);
                        }
                        else if (result.inlineConstraints != null)
                        {
                            inlineConstraints = result.inlineConstraints;
                        }
                    }
                    else
                    {
                        genericParams = result.genericParams;
                        inlineConstraints = result.inlineConstraints;
                    }

                    Consume(type: TokenType.RightBracket,
                        errorMessage: "Expected ']' after generic parameters");
                }
            }
        }

        // ===============================================================================
        // PHASE 2c: Parse failable marker (!)
        // ===============================================================================
        // Support ! suffix for failable routines (can appear after qualified name)
        bool isFailable = Match(type: TokenType.Bang);

        // ConsumeMethodName may have already included '!' in the name
        if (name.EndsWith(value: '!'))
        {
            isFailable = true;
            name = name[..^1]; // Strip the '!' from name, we track it separately
        }

        // ===============================================================================
        // PHASE 3: PARAMETERS
        // ===============================================================================
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after routine name");
        var parameters = new List<Parameter>();

        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                // Handle 'me' parameter (self-reference for member routines)
                if (Check(type: TokenType.Me))
                {
                    Token selfToken = Advance();
                    TypeExpression? selfType = null;
                    if (Match(type: TokenType.Colon))
                    {
                        selfType = ParseType();
                    }

                    parameters.Add(item: new Parameter(Name: "me",
                        Type: selfType,
                        DefaultValue: null,
                        Location: GetLocation(token: selfToken)));
                }
                else
                {
                    // Regular parameter: name: Type = default
                    // Varargs parameter: name...: Type
                    // allowKeywords=true lets us use 'from', 'to', etc. as param names
                    string paramName = ConsumeIdentifier(errorMessage: "Expected parameter name",
                        allowKeywords: true);
                    bool isVariadic = Match(type: TokenType.DotDotDot);
                    TypeExpression? paramType = null;
                    Expression? defaultValue = null;

                    if (Match(type: TokenType.Colon))
                    {
                        paramType = ParseType();
                    }

                    if (Match(type: TokenType.Assign))
                    {
                        defaultValue = ParseExpression();
                    }

                    parameters.Add(item: new Parameter(Name: paramName,
                        Type: paramType,
                        DefaultValue: defaultValue,
                        Location: GetLocation(),
                        IsVariadic: isVariadic));
                }
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after parameters");

        // ===============================================================================
        // PHASE 4: GENERIC CONSTRAINTS (needs clause — before or after return type)
        // ===============================================================================
        // Supports both orderings:
        //   routine foo[T](x: T) needs T obeys P -> Text      (needs before ->)
        //   routine foo[T](x: T) -> Text \n needs T obeys P   (needs after -> on next line)
        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(
            genericParams: genericParams,
            existingConstraints: inlineConstraints);

        // ===============================================================================
        // PHASE 5: RETURN TYPE
        // ===============================================================================
        TypeExpression? returnType = null;
        if (Match(type: TokenType.Arrow))
        {
            returnType = ParseType();
        }

        // Try constraints again after return type (supports needs on next line after ->)
        constraints = ParseGenericConstraints(genericParams: genericParams,
            existingConstraints: constraints);

        // ===============================================================================
        // PHASE 6: BODY (indented block)
        // ===============================================================================

        _inRoutineBody = true;
        Statement body;
        try
        {
            body = ParseIndentedBlock();
        }
        finally
        {
            _inRoutineBody = false;
        }

        return new RoutineDeclaration(Name: name,
            Parameters: parameters,
            ReturnType: returnType,
            Body: body,
            Visibility: visibility,
            Annotations: annotations ?? [],
            Location: location,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            IsFailable: isFailable,
            Storage: storage,
            Async: asyncStatus,
            IsDangerous: isDangerous);
    }

    // Entity declaration parsing lives in Parser.Declarations.Types.cs.
    private (VisibilityModifier Visibility, StorageClass Storage) ParseModifiers()
    {
        VisibilityModifier visibility = VisibilityModifier.Open; // Default
        StorageClass storage = StorageClass.None; // Default
        bool hasVisibility = false;
        bool hasStorage = false;

        // Parse modifiers in any order (visibility and storage can appear in any order)
        while (true)
        {
            // Visibility modifiers (Open keyword removed - open is default, not a keyword)
            if (!hasVisibility && Match(type: TokenType.Posted))
            {
                visibility = VisibilityModifier.Posted;
                hasVisibility = true;
            }
            else if (!hasVisibility && Match(type: TokenType.Secret))
            {
                visibility = VisibilityModifier.Secret;
                hasVisibility = true;
            }
            else if (!hasVisibility && Match(type: TokenType.External))
            {
                visibility = VisibilityModifier.External;
                hasVisibility = true;
            }
            // Storage class modifiers
            else if (!hasStorage && Match(type: TokenType.Common))
            {
                storage = StorageClass.Common;
                hasStorage = true;
            }
            else if (!hasStorage && Match(type: TokenType.Global))
            {
                storage = StorageClass.Global;
                hasStorage = true;
            }
            else
            {
                break; // No more modifiers
            }
        }

        return (visibility, storage);
    }

    /// <summary>
    /// Checks whether the current bracket content contains nested generics (e.g., DictEntry[K, V]).
    /// Must be called after consuming the opening '['.
    /// Uses lookahead without advancing the parser position.
    /// </summary>
    private bool HasNestedBrackets()
    {
        int offset = 0;
        int depth = 0;

        while (true)
        {
            Token token = PeekToken(offset: offset);
            if (token.Type == TokenType.Eof)
            {
                break;
            }

            if (token.Type == TokenType.LeftBracket)
            {
                // A '[' at depth 0 means nested generics (we're already inside the outer '[')
                if (depth == 0)
                {
                    return true;
                }

                depth++;
            }
            else if (token.Type == TokenType.RightBracket)
            {
                if (depth == 0)
                {
                    break; // End of outer brackets
                }

                depth--;
            }

            offset++;
        }

        return false;
    }

    /// <summary>
    /// Serializes a TypeExpression back to its string form.
    /// e.g., TypeExpression("DictEntry", [TypeExpression("K"), TypeExpression("V")]) -> "DictEntry[K, V]"
    /// </summary>
    private static string SerializeTypeExpression(TypeExpression type)
    {
        if (type.GenericArguments is not { Count: > 0 })
        {
            return type.Name;
        }

        return type.Name + "[" + string.Join(separator: ", ",
            values: type.GenericArguments.Select(selector: SerializeTypeExpression)) + "]";
    }
}
