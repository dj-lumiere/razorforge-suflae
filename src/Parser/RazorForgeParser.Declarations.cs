using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using RazorForge.Diagnostics;

namespace Compilers.RazorForge.Parser;

/// <summary>
/// Partial class containing declaration parsing (variable, routine, entity, record, variant, protocol, etc.).
/// </summary>
public partial class RazorForgeParser
{
    /// <summary>
    /// Parses a variable declaration with var/let/preset keyword.
    /// Syntax: <c>var name: Type = value</c> or <c>let name = value</c> or <c>preset name = value</c>
    /// </summary>
    /// <param name="visibility">The visibility modifier (public, published, internal, private).</param>
    /// <param name="storage">The storage class modifier (default: None).</param>
    /// <returns>A <see cref="VariableDeclaration"/> AST node.</returns>
    private VariableDeclaration ParseVariableDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Public,
        StorageClass storage = StorageClass.None)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        bool isMutable = PeekToken(offset: -1)
           .Type == TokenType.Var;

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
            IsMutable: isMutable,
            Location: location,
            Storage: storage);
    }

    /// <summary>
    /// Parses a field declaration in records: public name: Type or name: Type
    /// Fields are declared without var/let keywords.
    /// </summary>
    /// <param name="visibility">The visibility modifier (public, published, internal, private).</param>
    private VariableDeclaration ParseFieldDeclaration(VisibilityModifier visibility = VisibilityModifier.Public)
    {
        SourceLocation location = GetLocation();

        string name = ConsumeIdentifier(errorMessage: "Expected field name");

        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after field name");
        TypeExpression type = ParseType();

        Expression? initializer = null;
        if (Match(type: TokenType.Assign))
        {
            initializer = ParseExpression();
        }

        ConsumeStatementTerminator();

        // Fields are not mutable by default in record
        return new VariableDeclaration(Name: name,
            Type: type,
            Initializer: initializer,
            Visibility: visibility,
            IsMutable: false,
            Location: location);
    }

    /// <summary>
    /// Parses a namespace declaration.
    /// Syntax: <c>namespace path/to/module</c>
    /// </summary>
    /// <returns>A <see cref="NamespaceDeclaration"/> AST node.</returns>
    private NamespaceDeclaration ParseNamespaceDeclaration()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string namespacePath = "";

        // Parse namespace path - could be multiple identifiers separated by slashes
        // e.g., namespace standard/errors
        do
        {
            string part = ConsumeIdentifier(errorMessage: "Expected namespace name");
            namespacePath += part;
            if (Match(type: TokenType.Slash))
            {
                namespacePath += "/";
            }
            else
            {
                break;
            }
        } while (true);

        ConsumeStatementTerminator();

        return new NamespaceDeclaration(Path: namespacePath, Location: location);
    }

    /// <summary>
    /// Parses an import declaration.
    /// Syntax: <c>import module/path</c> or <c>import module/path as alias</c>
    /// Registers imported types/namespaces for generic disambiguation.
    /// </summary>
    /// <returns>An <see cref="ImportDeclaration"/> AST node.</returns>
    private ImportDeclaration ParseImportDeclaration()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string modulePath = "";
        string? alias = null;
        List<string>? specificImports = null;

        // Parse module path - could be multiple identifiers separated by slashes
        // Dot marks a specific type within the module: import Standard/Collection.List
        do
        {
            string part = ConsumeIdentifier(errorMessage: "Expected module name");
            modulePath += part;
            if (Match(type: TokenType.Slash))
            {
                modulePath += "/";
            }
            else if (Match(type: TokenType.Dot))
            {
                // Dot marks specific type: CSubsystem.CStr → path "CSubsystem", type "CStr"
                string typeName = ConsumeIdentifier(errorMessage: "Expected type name after '.'");
                modulePath += "/" + typeName;
                break;
            }
            else
            {
                break;
            }
        } while (true);

        // Optional alias
        if (Match(type: TokenType.As))
        {
            alias = ConsumeIdentifier(errorMessage: "Expected alias name");
        }

        ConsumeStatementTerminator();

        // Register imported types/namespaces for generic disambiguation
        // import Collections/SortedDict -> adds "SortedDict" to known types (bare name usage)
        // import Collections -> adds "Collections" to namespaces (qualified name usage)
        if (modulePath.Contains(value: '/'))
        {
            // Specific type import: Collections/SortedDict
            string typeName = modulePath[(modulePath.LastIndexOf(value: '/') + 1)..];
            _knownTypeNames.Add(item: typeName);
        }
        else
        {
            // Namespace import: Collections
            _importedNamespaces.Add(item: modulePath);
        }

        return new ImportDeclaration(ModulePath: modulePath,
            Alias: alias,
            SpecificImports: specificImports,
            Location: location);
    }

    /// <summary>
    /// Parses a redefinition declaration (type alias using 'define').
    /// Syntax: <c>define OldName as NewName</c>
    /// </summary>
    /// <returns>A <see cref="DefineDeclaration"/> AST node.</returns>
    private IAstNode ParseDefineDeclaration()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string oldName = ConsumeIdentifier(errorMessage: "Expected identifier after 'redefine'");
        Consume(type: TokenType.As, errorMessage: "Expected 'as' in redefinition");
        string newName = ConsumeIdentifier(errorMessage: "Expected new identifier in redefinition");

        ConsumeStatementTerminator();

        return new DefineDeclaration(OldName: oldName, NewName: newName, Location: location);
    }

    /// <summary>
    /// Parses a preset declaration: preset name: Type = value
    /// Preset is a compile-time constant.
    /// </summary>
    private PresetDeclaration ParsePresetDeclaration()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected preset name");
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after preset name");
        TypeExpression type = ParseType();
        Consume(type: TokenType.Assign, errorMessage: "Expected '=' after preset type");
        Expression value = ParseExpression();

        ConsumeStatementTerminator();

        return new PresetDeclaration(Name: name,
            Type: type,
            Value: value,
            Location: location);
    }

    /// <summary>
    /// Parses visibility and storage class modifiers.
    /// Visibility: public, published, internal, private, imported
    /// Storage: common, global
    /// These are orthogonal and can be combined: public common, private common, etc.
    /// </summary>
    /// <returns>A tuple of (visibility, storage) modifiers.</returns>
    private (VisibilityModifier Visibility, StorageClass Storage) ParseModifiers()
    {
        var visibility = VisibilityModifier.Public; // Default
        var storage = StorageClass.None; // Default
        bool hasVisibility = false;
        bool hasStorage = false;

        // Parse modifiers in any order (visibility and storage can appear in any order)
        while (true)
        {
            // Visibility modifiers
            if (!hasVisibility && Match(type: TokenType.Public))
            {
                visibility = VisibilityModifier.Public;
                hasVisibility = true;
            }
            else if (!hasVisibility && Match(type: TokenType.Published))
            {
                visibility = VisibilityModifier.Published;
                hasVisibility = true;
            }
            else if (!hasVisibility && Match(type: TokenType.Internal))
            {
                visibility = VisibilityModifier.Internal;
                hasVisibility = true;
            }
            else if (!hasVisibility && Match(type: TokenType.Private))
            {
                visibility = VisibilityModifier.Private;
                hasVisibility = true;
            }
            else if (!hasVisibility && Match(type: TokenType.Imported))
            {
                visibility = VisibilityModifier.Imported;
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
    /// Parses attributes like @crash_only, @inline, @intrinsic("name"), etc.
    /// Attributes are prefixed with @ and followed by an identifier, optionally with arguments.
    /// </summary>
    private List<string> ParseAttributes()
    {
        var attributes = new List<string>();

        // Handle both @attribute and special tokens (@intrinsic_type, @intrinsic_routine, @native)
        while (Check(TokenType.At, TokenType.IntrinsicType, TokenType.IntrinsicRoutine, TokenType.Native))
        {
            string attrName;

            if (Match(type: TokenType.IntrinsicType))
            {
                // @intrinsic_type was tokenized as a single IntrinsicType token
                attrName = "intrinsic_type";
            }
            else if (Match(type: TokenType.IntrinsicRoutine))
            {
                // @intrinsic_routine was tokenized as a single IntrinsicRoutine token
                attrName = "intrinsic_routine";
            }
            else if (Match(type: TokenType.Native))
            {
                // @native was tokenized as a single Native token
                attrName = "native";
            }
            else if (Match(type: TokenType.At))
            {
                // Regular attribute: @identifier
                attrName = ConsumeIdentifier(errorMessage: "Expected attribute name after '@'");
            }
            else
            {
                break; // No more attributes
            }

            // Check for attribute arguments: @intrinsic.sitofp() or @config(name: "value", count: 5)
            if (Match(type: TokenType.LeftParen))
            {
                var arguments = new List<string>();

                if (!Check(type: TokenType.RightParen))
                {
                    do
                    {
                        // Check for named argument: name = value
                        if (Check(type: TokenType.Identifier) && PeekToken(offset: 1)
                               .Type == TokenType.Colon)
                        {
                            string argName = ConsumeIdentifier(errorMessage: "Expected argument name");
                            Consume(type: TokenType.Colon, errorMessage: "Expected '=' after argument name");
                            string argValue = ParseAttributeValue();
                            arguments.Add(item: $"{argName}={argValue}");
                        }
                        else
                        {
                            // Positional argument (string literal, number, identifier)
                            arguments.Add(item: ParseAttributeValue());
                        }
                    } while (Match(type: TokenType.Comma));
                }

                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after attribute arguments");

                // Store attribute as: intrinsic("size_of") or config(name="value", count=5)
                attrName += "(" + string.Join(separator: ", ", values: arguments) + ")";
            }

            attributes.Add(item: attrName);
        }

        return attributes;
    }

    /// <summary>
    /// Parses a single attribute argument value (string, number, bool, or identifier).
    /// </summary>
    /// <returns>String representation of the attribute value.</returns>
    private string ParseAttributeValue()
    {
        // Attribute values are limited to compile-time constants:
        // string, number, bool, or identifier (for enums/presets)

        // String literal
        if (Check(TokenType.TextLiteral, TokenType.BytesLiteral))
        {
            return Advance()
               .Text;
        }

        // Boolean literals
        if (Match(type: TokenType.True))
        {
            return "true";
        }

        if (Match(type: TokenType.False))
        {
            return "false";
        }

        // Numeric literals
        if (Check(TokenType.Integer,
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
                TokenType.SAddrLiteral,
                TokenType.UAddrLiteral))
        {
            return Advance()
               .Text;
        }

        // Identifier (for enum values or constant references)
        if (Check(type: TokenType.Identifier))
        {
            return Advance()
               .Text;
        }

        throw ThrowParseError(RazorForgeDiagnosticCode.ExpectedAttributeValue,
            $"Expected attribute value, got {CurrentToken.Type}");
    }

    /// <summary>
    /// Parses a function/routine declaration.
    /// Syntax: <c>routine name&lt;T&gt;(params) -&gt; ReturnType where T is/isnot/in/notin/follows/notfollows Constraint { body }</c>
    /// Supports generic parameters, namespace-qualified names, failable routines (!), and inline constraints.
    /// </summary>
    /// <param name="visibility">The visibility modifier for the function.</param>
    /// <param name="attributes">List of attributes applied to the function (e.g., @intrinsic).</param>
    /// <param name="allowNoBody">If true, allows signature-only declarations (for protocols/intrinsics/imported).</param>
    /// <param name="storage">The storage class modifier (default: None, can be Common for type-level static).</param>
    /// <returns>A <see cref="RoutineDeclaration"/> AST node.</returns>
    /// <remarks>
    /// Parsing phases:
    /// 1. NAME PARSING - Base name + optional type-level generics (e.g., "List&lt;T&gt;")
    /// 2. QUALIFIED NAME - Dot-separated parts for methods (e.g., "List&lt;T&gt;.append")
    /// 3. FAILABLE MARKER - Check for "!" suffix
    /// 4. PARAMETERS - "(param: Type, ...)" including 'me' self-reference
    /// 5. RETURN TYPE - "-> ReturnType" clause
    /// 6. CONSTRAINTS - "where T follows Protocol" clause
    /// 7. BODY - "{...}" or signature-only for protocols/intrinsics
    /// </remarks>
    private RoutineDeclaration ParseRoutineDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Public,
        List<string>? attributes = null,
        bool allowNoBody = false,
        StorageClass storage = StorageClass.None,
        AsyncStatus asyncStatus = AsyncStatus.None)
    {
        // ═══════════════════════════════════════════════════════════════════════════
        // VALIDATION: Reject nested routine declarations
        // ═══════════════════════════════════════════════════════════════════════════
        if (_inRoutineBody)
        {
            throw ThrowParseError(RazorForgeDiagnosticCode.NestedRoutineNotAllowed,
                "Nested routine declarations are not allowed. Define routines at module or type level.");
        }

        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 1: Parse base routine name and optional type-level generic parameters
        // ═══════════════════════════════════════════════════════════════════════════
        // Examples:
        //   "foo"          -> name="foo", no generics
        //   "List<T>"      -> name="List", genericParams=["T"]
        //   "Dict<K, V>"   -> name="Dict", genericParams=["K", "V"]
        // ═══════════════════════════════════════════════════════════════════════════

        string name = ConsumeIdentifier(errorMessage: "Expected routine name");

        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        bool hasGenericParams = false;

        // Check for type-level generic params BEFORE the dot (e.g., "List<T>.append")
        if (Match(type: TokenType.Less))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;
            hasGenericParams = true;

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 2: Parse dot-separated qualified name (for methods)
        // ═══════════════════════════════════════════════════════════════════════════
        // Examples:
        //   "Console.print"           -> name="Console.print"
        //   "List<T>.append"          -> name="List<T>.append" (generics embedded in name)
        //   "Dict<K, V>.get<I>"       -> name="Dict<K, V>.get", genericParams=["K","V","I"]
        //
        // The loop handles:
        //   - Simple qualified names: "Type.method"
        //   - Generic types with methods: "Type<T>.method"
        //   - Methods with their own generics: "Type.method<I>"
        //   - Both: "Type<T>.method<I>" merges both param lists
        // ═══════════════════════════════════════════════════════════════════════════

        while (Match(type: TokenType.Dot))
        {
            string part = ConsumeMethodName(errorMessage: "Expected method name after '.'");

            // ─────────────────────────────────────────────────────────────────────
            // If we parsed generic params before the dot, embed them in the name
            // This transforms: name="List", generics=["T"], part="append"
            //             to: name="List<T>.append"
            // ─────────────────────────────────────────────────────────────────────
            if (hasGenericParams && !name.Contains(value: '.') && genericParams != null)
            {
                name = name + "<" + string.Join(separator: ", ", values: genericParams) + ">." + part;
                hasGenericParams = false; // Only add once
            }
            else
            {
                name = name + "." + part;
            }

            // ─────────────────────────────────────────────────────────────────────
            // Check for method-level generic params AFTER the method name
            // e.g., "List<T>.get<I>" - the <I> belongs to the method
            // ─────────────────────────────────────────────────────────────────────
            if (!Check(type: TokenType.Less))
            {
                continue;
            }

            // Disambiguate: Is '<' starting generics or a comparison?
            // Generics: followed by Identifier, TypeIdentifier, or '>' (empty)
            // Comparison: followed by literal, expression, etc.
            Token nextToken = PeekToken(offset: 1);
            if (nextToken.Type != TokenType.Identifier && nextToken.Type != TokenType.TypeIdentifier && nextToken.Type != TokenType.Greater)
            {
                continue;
            }

            Match(type: TokenType.Less);
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();

            // ─────────────────────────────────────────────────────────────────────
            // MERGE: Combine type-level and method-level generic parameters
            // For "List<T>.__setitem__!<I>", we need both T (from List) and I (from method)
            // Result: genericParams = ["T", "I"]
            // ─────────────────────────────────────────────────────────────────────
            if (genericParams is { Count: > 0 })
            {
                // Merge: type params first, then method params
                genericParams = new List<string>(collection: genericParams);
                genericParams.AddRange(collection: result.genericParams);

                // Merge constraints too
                if (inlineConstraints != null && result.inlineConstraints != null)
                {
                    inlineConstraints = new List<GenericConstraintDeclaration>(collection: inlineConstraints);
                    inlineConstraints.AddRange(collection: result.inlineConstraints);
                }
                else if (result.inlineConstraints != null)
                {
                    inlineConstraints = result.inlineConstraints;
                }
            }
            else
            {
                // No type params, just use method params
                genericParams = result.genericParams;
                inlineConstraints = result.inlineConstraints;
            }

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 3: Parse failable marker (!)
        // ═══════════════════════════════════════════════════════════════════════════
        // Failable routines can return errors or "absent" (none).
        // The '!' can appear:
        //   - After routine name: "get_value!()"
        //   - Already in name from ConsumeMethodName: name ends with "!"
        // ═══════════════════════════════════════════════════════════════════════════

        bool isFailable = Match(type: TokenType.Bang);

        // ConsumeMethodName may have already included '!' in the name
        if (name.EndsWith('!'))
        {
            isFailable = true;
            name = name[..^1]; // Strip the '!' from name, we track it separately
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 4: Parse parameter list
        // ═══════════════════════════════════════════════════════════════════════════
        // Syntax: (param1: Type1, param2: Type2 = default, ...)
        // Special cases:
        //   - 'me' keyword for self-reference (optional type)
        //   - Keywords allowed as param names (e.g., 'from' in conversion routines)
        //   - Optional default values with '='
        // ═══════════════════════════════════════════════════════════════════════════

        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after routine name");
        var parameters = new List<Parameter>();

        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                // ─────────────────────────────────────────────────────────────────
                // Handle 'me' parameter (self-reference for methods)
                // Can be typed: "me: MyType" or untyped: "me"
                // ─────────────────────────────────────────────────────────────────
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
                    // ─────────────────────────────────────────────────────────────
                    // Regular parameter: name: Type = default
                    // allowKeywords=true lets us use 'from', 'to', etc. as param names
                    // ─────────────────────────────────────────────────────────────
                    string paramName = ConsumeIdentifier(errorMessage: "Expected parameter name", allowKeywords: true);
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
                        Location: GetLocation()));
                }
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after parameters");

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 5: Parse return type
        // ═══════════════════════════════════════════════════════════════════════════
        // Syntax: -> ReturnType
        // Optional - if omitted, routine returns nothing (void/unit)
        // ═══════════════════════════════════════════════════════════════════════════

        TypeExpression? returnType = null;
        if (Match(type: TokenType.Arrow))
        {
            returnType = ParseType();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 6: Parse generic constraints (where clause)
        // ═══════════════════════════════════════════════════════════════════════════
        // Syntax: where T follows Protocol, K is SomeType
        // Merges inline constraints (from <T follows X>) with where-clause constraints
        // ═══════════════════════════════════════════════════════════════════════════

        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

        // Push generic parameters into scope before parsing body
        // This allows the body to reference T, K, etc. as types
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Push(item: [..genericParams]);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 7: Parse routine body
        // ═══════════════════════════════════════════════════════════════════════════
        // Three cases:
        //   1. Has body: { ... } - normal routine
        //   2. No body allowed: signature-only (protocols, @intrinsic)
        //   3. No body but required: error
        // ═══════════════════════════════════════════════════════════════════════════

        bool hasIntrinsicAttribute = attributes != null && attributes.Any(predicate: a => a.StartsWith(value: "intrinsic"));
        bool canSkipBody = allowNoBody || hasIntrinsicAttribute;

        BlockStatement? body;
        if (Check(type: TokenType.LeftBrace))
        {
            // ─────────────────────────────────────────────────────────────────────
            // Normal case: Parse the routine body
            // Set _inRoutineBody to prevent nested routine declarations
            // ─────────────────────────────────────────────────────────────────────
            _inRoutineBody = true;
            try
            {
                body = ParseBlockStatement();
            }
            finally
            {
                _inRoutineBody = false;
            }
        }
        else if (!canSkipBody)
        {
            // ─────────────────────────────────────────────────────────────────────
            // Error case: Body required but not present
            // ─────────────────────────────────────────────────────────────────────
            Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after routine signature");
            body = new BlockStatement(Statements: [], Location: location);
        }
        else
        {
            // ─────────────────────────────────────────────────────────────────────
            // Signature-only: Protocol method or @intrinsic function
            // ─────────────────────────────────────────────────────────────────────
            ConsumeStatementTerminator();
            body = new BlockStatement(Statements: [], Location: location);
        }

        // Pop generic parameter scope after parsing body
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Pop();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // BUILD AST NODE
        // ═══════════════════════════════════════════════════════════════════════════

        return new RoutineDeclaration(Name: name,
            Parameters: parameters,
            ReturnType: returnType,
            Body: body,
            Visibility: visibility,
            Attributes: attributes ?? [],
            Location: location,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            IsFailable: isFailable,
            Storage: storage,
            Async: asyncStatus);
    }

    /// <summary>
    /// Parses an entity declaration (heap-allocated reference type).
    /// Syntax: <c>entity Name&lt;T&gt; from BaseClass follows Protocol { members }</c>
    /// </summary>
    /// <param name="visibility">The visibility modifier for the entity.</param>
    /// <returns>An <see cref="EntityDeclaration"/> AST node.</returns>
    /// <remarks>
    /// Parsing phases (shared pattern with record/resident/protocol):
    /// 1. NAME - Type name identifier
    /// 2. GENERICS - Optional &lt;T, K&gt; with inline constraints
    /// 3. CONSTRAINTS - Optional "where T follows X" clause
    /// 4. PROTOCOLS - Optional "follows Protocol1, Protocol2" clause
    /// 5. BODY - "{...}" containing member declarations
    /// </remarks>
    private EntityDeclaration ParseEntityDeclaration(VisibilityModifier visibility = VisibilityModifier.Public)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 1: Parse type name
        // ═══════════════════════════════════════════════════════════════════════════

        string name = ConsumeIdentifier(errorMessage: "Expected entity name");

        // Register for generic disambiguation (so Parser knows "MyEntity<T>" is a type, not comparison)
        _knownTypeNames.Add(item: name);

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 2: Parse optional generic parameters with inline constraints
        // ═══════════════════════════════════════════════════════════════════════════
        // Syntax: <T, K follows Comparable>
        // Inline constraints like "K follows Comparable" are parsed here
        // ═══════════════════════════════════════════════════════════════════════════

        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.Less))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 3: Parse generic constraints (where clause)
        // ═══════════════════════════════════════════════════════════════════════════
        // Syntax: where T follows Protocol, K is SomeType
        // Merges with inline constraints from phase 2
        // ═══════════════════════════════════════════════════════════════════════════

        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

        // Push generic parameters into scope so body can reference them as types
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Push(item: [..genericParams]);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 4: Parse protocol implementations
        // ═══════════════════════════════════════════════════════════════════════════
        // Syntax: follows Protocol1, Protocol2
        // Allows multi-line formatting with newlines around protocol names
        // ═══════════════════════════════════════════════════════════════════════════

        var protocols = new List<TypeExpression>();

        if (Match(type: TokenType.Follows))
        {
            do
            {
                while (Match(type: TokenType.Newline)) { } // Skip newlines before protocol name
                protocols.Add(item: ParseType());
                while (Match(type: TokenType.Newline)) { } // Skip newlines after protocol name
            } while (Match(type: TokenType.Comma));
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 5: Parse entity body
        // ═══════════════════════════════════════════════════════════════════════════
        // Contains: fields (var/let), methods, nested types
        // _parsingTypeBody=true enables field declaration syntax
        // _parsingStrictRecordBody=false allows visibility modifiers on fields
        // ═══════════════════════════════════════════════════════════════════════════

        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after entity header");

        var members = new List<Declaration>();

        // Save and set parsing context flags
        bool wasParsingTypeBody = _parsingTypeBody;
        bool wasParsingStrictRecordBody = _parsingStrictRecordBody;
        _parsingTypeBody = true;
        _parsingStrictRecordBody = false;  // Entities allow modifiers (private, public, etc.)

        while (!Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            IAstNode node = ParseDeclaration();
            if (node is Declaration member)
            {
                members.Add(item: member);
            }
            else
            {
                throw ThrowParseError(RazorForgeDiagnosticCode.InvalidDeclarationInBody,
                    $"Expected declaration inside entity body, got {node.GetType().Name}");
            }
        }

        // Restore parsing context flags
        _parsingTypeBody = wasParsingTypeBody;
        _parsingStrictRecordBody = wasParsingStrictRecordBody;

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after entity body");

        // Pop generic parameter scope
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Pop();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // BUILD AST NODE
        // ═══════════════════════════════════════════════════════════════════════════

        return new EntityDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Protocols: protocols,
            Members: members,
            Visibility: visibility,
            Location: location);
    }

    /// <summary>
    /// Parses a record declaration (stack-allocated value type).
    /// Syntax: <c>record Name&lt;T&gt; follows Protocol { members }</c>
    /// </summary>
    /// <param name="visibility">The visibility modifier for the record.</param>
    /// <returns>A <see cref="RecordDeclaration"/> AST node.</returns>
    private RecordDeclaration ParseRecordDeclaration(VisibilityModifier visibility = VisibilityModifier.Public)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected record name");

        // Register this type name
        _knownTypeNames.Add(item: name);

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.Less))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

        // Push generic parameters into scope
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Push(item: [..genericParams]);
        }

        // Parse interfaces/protocols the record follows
        var interfaces = new List<TypeExpression>();
        if (Match(type: TokenType.Follows))
        {
            do
            {
                // Skip newlines before protocol name (for multi-line formatting)
                while (Match(type: TokenType.Newline)) { }

                interfaces.Add(item: ParseType());

                // Skip newlines after protocol name (before comma or brace)
                while (Match(type: TokenType.Newline)) { }
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after record header");

        var members = new List<Declaration>();

        // Enable field declaration syntax inside record body
        // Records are strict: no modifiers allowed on fields
        bool wasParsingTypeBody = _parsingTypeBody;
        bool wasParsingStrictRecordBody = _parsingStrictRecordBody;
        _parsingTypeBody = true;
        _parsingStrictRecordBody = true;  // Records disallow modifiers on fields

        while (!Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            IAstNode node = ParseDeclaration();
            if (node is Declaration member)
            {
                members.Add(item: member);
            }
            else
            {
                throw ThrowParseError(RazorForgeDiagnosticCode.InvalidDeclarationInBody,
                    $"Expected declaration inside record body, got {node.GetType().Name}");
            }
        }

        _parsingTypeBody = wasParsingTypeBody;
        _parsingStrictRecordBody = wasParsingStrictRecordBody;

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after record body");

        // Pop generic parameter scope
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Pop();
        }

        return new RecordDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Protocols: interfaces,
            Members: members,
            Visibility: visibility,
            Location: location);
    }

    /// <summary>
    /// Parses a resident declaration (singleton static type).
    /// Syntax: <c>resident Name&lt;T&gt; follows Protocol { members }</c>
    /// </summary>
    /// <param name="visibility">The visibility modifier for the resident.</param>
    /// <returns>A <see cref="ResidentDeclaration"/> AST node.</returns>
    private ResidentDeclaration ParseResidentDeclaration(VisibilityModifier visibility = VisibilityModifier.Public)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected resident name");

        // Register this type name
        _knownTypeNames.Add(item: name);

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.Less))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

        // Push generic parameters into scope
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Push(item: [..genericParams]);
        }

        // Parse interfaces/protocols the resident follows
        var interfaces = new List<TypeExpression>();
        if (Match(type: TokenType.Follows))
        {
            do
            {
                // Skip newlines before protocol name (for multi-line formatting)
                while (Match(type: TokenType.Newline)) { }

                interfaces.Add(item: ParseType());

                // Skip newlines after protocol name (before comma or brace)
                while (Match(type: TokenType.Newline)) { }
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after resident header");

        var members = new List<Declaration>();

        // Enable field declaration syntax inside resident body
        // Residents allow modifiers on fields (like entities, unlike records)
        bool wasParsingTypeBody = _parsingTypeBody;
        bool wasParsingStrictRecordBody = _parsingStrictRecordBody;
        _parsingTypeBody = true;
        _parsingStrictRecordBody = false;  // Residents allow modifiers

        while (!Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            IAstNode node = ParseDeclaration();
            if (node is Declaration member)
            {
                members.Add(item: member);
            }
            else
            {
                throw ThrowParseError(RazorForgeDiagnosticCode.InvalidDeclarationInBody,
                $"Expected declaration inside resident body, got {node.GetType().Name}");
            }
        }

        _parsingTypeBody = wasParsingTypeBody;
        _parsingStrictRecordBody = wasParsingStrictRecordBody;

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after resident body");

        // Pop generic parameter scope
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Pop();
        }

        return new ResidentDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Protocols: interfaces,
            Members: members,
            Visibility: visibility,
            Location: location);
    }

    /// <summary>
    /// Parses a choice declaration (C-style enum with integer values).
    /// Syntax: <c>choice Name { Case1, Case2 = 5, Case3 }</c>
    /// </summary>
    /// <param name="visibility">The visibility modifier for the choice.</param>
    /// <returns>A <see cref="ChoiceDeclaration"/> AST node.</returns>
    private ChoiceDeclaration ParseChoiceDeclaration(VisibilityModifier visibility = VisibilityModifier.Public)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected choice name");

        // Register this type name
        _knownTypeNames.Add(item: name);

        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after choice name");

        var variants = new List<ChoiceCase>();

        while (!Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            string variantName = ConsumeIdentifier(errorMessage: "Expected choice variant name");

            // CASE: value syntax for choice values (e.g., OK: 200)
            // Store expression as-is; semantic analyzer will validate and convert
            Expression? value = null;
            if (Match(type: TokenType.Colon))
            {
                value = ParseExpression();
            }

            variants.Add(item: new ChoiceCase(Name: variantName, Value: value, Location: GetLocation()));

            if (!Match(type: TokenType.Comma))
            {
                Match(type: TokenType.Newline);
            }
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after choice body");

        return new ChoiceDeclaration(Name: name,
            Cases: variants,
            Methods: [],
            Visibility: visibility,
            Location: location);
    }

    /// <summary>
    /// Parses a variant declaration (tagged union with associated data).
    /// Syntax: <c>variant Name&lt;T&gt; { Case1, Case2: Type }</c>
    /// </summary>
    /// <returns>A <see cref="VariantDeclaration"/> AST node.</returns>
    private VariantDeclaration ParseVariantDeclaration()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected variant name");

        // Register this type name
        _knownTypeNames.Add(item: name);

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.Less))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

        // Push generic parameters into scope
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Push(item: [..genericParams]);
        }

        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after variant header");

        var cases = new List<VariantCase>();

        while (!Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            // Parse variant case
            string caseName = ConsumeIdentifier(errorMessage: "Expected variant case name");

            // CASE: Type syntax for associated types
            TypeExpression? associatedType = null;
            if (Match(type: TokenType.Colon))
            {
                associatedType = ParseType();
            }

            cases.Add(item: new VariantCase(Name: caseName, AssociatedTypes: associatedType, Location: GetLocation()));

            if (!Match(type: TokenType.Comma))
            {
                Match(type: TokenType.Newline);
            }
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after variant body");

        // Pop generic parameter scope
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Pop();
        }

        return new VariantDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Cases: cases,
            Location: location);
    }

    /// <summary>
    /// Parses a protocol declaration (interface/trait definition).
    /// Syntax: <c>protocol Name&lt;T&gt; follows Parent { routine method(me) -&gt; Type }</c>
    /// Supports method signatures (without bodies), field requirements, and protocol inheritance.
    /// </summary>
    /// <param name="visibility">The visibility modifier for the protocol.</param>
    /// <returns>A <see cref="ProtocolDeclaration"/> AST node.</returns>
    /// <remarks>
    /// Parsing phases:
    /// 1. NAME - Protocol name identifier
    /// 2. GENERICS - Optional &lt;T, K&gt; with inline constraints
    /// 3. CONSTRAINTS - Optional "where T follows X" clause
    /// 4. PARENT PROTOCOLS - Optional "follows Parent1, Parent2" for inheritance
    /// 5. BODY - "{...}" containing method SIGNATURES (no bodies)
    ///
    /// Method signature parsing (nested within body):
    ///   a. Attributes (@readonly, etc.)
    ///   b. Method name (with optional "Me." prefix and "!" suffix)
    ///   c. Parameters
    ///   d. Return type
    /// </remarks>
    private ProtocolDeclaration ParseProtocolDeclaration(VisibilityModifier visibility = VisibilityModifier.Public)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 1: Parse protocol name
        // ═══════════════════════════════════════════════════════════════════════════

        string name = ConsumeIdentifier(errorMessage: "Expected protocol name");

        // Register for generic disambiguation
        _knownTypeNames.Add(item: name);

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 2: Parse optional generic parameters with inline constraints
        // ═══════════════════════════════════════════════════════════════════════════

        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.Less))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 3: Parse generic constraints (where clause)
        // ═══════════════════════════════════════════════════════════════════════════

        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

        // Push generic parameters into scope for body parsing
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Push(item: [..genericParams]);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 4: Parse parent protocols (protocol inheritance)
        // ═══════════════════════════════════════════════════════════════════════════
        // Syntax: follows Parent1, Parent2
        // Protocols can inherit from multiple parent protocols
        // ═══════════════════════════════════════════════════════════════════════════

        var parentProtocols = new List<TypeExpression>();
        if (Match(type: TokenType.Follows))
        {
            do
            {
                while (Match(type: TokenType.Newline)) { } // Skip newlines before protocol name
                parentProtocols.Add(item: ParseType());
                while (Match(type: TokenType.Newline)) { } // Skip newlines after protocol name
            }
            while (Match(type: TokenType.Comma));
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 5: Parse protocol body (method signatures)
        // ═══════════════════════════════════════════════════════════════════════════
        // Protocol body contains method SIGNATURES only (no implementations).
        // Each signature consists of:
        //   - Optional attributes (@readonly, @mutating)
        //   - "routine" keyword
        //   - Method name (optionally qualified: "Me.method")
        //   - Parameters
        //   - Optional return type
        // ═══════════════════════════════════════════════════════════════════════════

        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after protocol header");

        var methods = new List<RoutineSignature>();

        while (!Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            // ─────────────────────────────────────────────────────────────────────
            // Parse method signature: @attr routine Me.method!(params) -> Type
            // ─────────────────────────────────────────────────────────────────────

            // Step 5a: Parse attributes before routine declaration
            List<string> attributes = ParseAttributes();

            if (Match(type: TokenType.Routine))
            {
                // Step 5b: Parse method name
                string methodName = ConsumeIdentifier(errorMessage: "Expected method name");

                // Support qualified names: "Me.method" or "Type.method"
                while (Match(type: TokenType.Dot))
                {
                    string part = ConsumeMethodName(errorMessage: "Expected method name after '.'");
                    methodName = methodName + "." + part;
                }

                // Support failable methods: "method!"
                if (Match(type: TokenType.Bang))
                {
                    methodName = methodName + "!";
                }

                // Step 5c: Parse parameters
                Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after method name");
                var parameters = new List<Parameter>();

                if (!Check(type: TokenType.RightParen))
                {
                    do
                    {
                        // Handle 'me' parameter (self-reference, optionally typed)
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
                            // Regular parameter
                            string paramName = ConsumeIdentifier(errorMessage: "Expected parameter name");

                            TypeExpression? paramType = null;
                            if (Match(type: TokenType.Colon))
                            {
                                paramType = ParseType();
                            }

                            parameters.Add(item: new Parameter(Name: paramName,
                                Type: paramType,
                                DefaultValue: null,
                                Location: GetLocation()));
                        }
                    } while (Match(type: TokenType.Comma));
                }

                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after parameters");

                // Step 5d: Parse optional return type
                TypeExpression? returnType = null;
                if (Match(type: TokenType.Arrow))
                {
                    returnType = ParseType();
                }

                // Add the method signature (no body - protocols only have signatures)
                methods.Add(item: new RoutineSignature(Name: methodName,
                    Parameters: parameters,
                    ReturnType: returnType,
                    Attributes: attributes.Count > 0 ? attributes : null,
                    Location: GetLocation()));

                ConsumeStatementTerminator();
            }
            else
            {
                // Unknown token in protocol body, skip it
                Advance();
            }
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after protocol body");

        // Pop generic parameter scope
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Pop();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // BUILD AST NODE
        // ═══════════════════════════════════════════════════════════════════════════

        return new ProtocolDeclaration(Name: name,
            GenericParameters: genericParams,
            ParentProtocols: parentProtocols,
            Methods: methods,
            Visibility: visibility,
            Location: location,
            GenericConstraints: constraints);
    }

    /// <summary>
    /// Parses an imported (external/FFI) function declaration.
    /// Syntax: <c>imported("C") routine name(param: Type, ...) -&gt; ReturnType</c>
    /// Supports variadic functions and calling convention specification.
    /// </summary>
    /// <param name="callingConvention">The calling convention (e.g., "C"). Defaults to "C" if null.</param>
    /// <returns>An <see cref="ImportedDeclaration"/> AST node.</returns>
    private ImportedDeclaration ParseImportedDeclaration(string? callingConvention = null)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -2)); // -2 because we consumed 'imported' and 'routine'

        string name = ConsumeIdentifier(errorMessage: "Expected routine name");

        // Support namespace-qualified names like Console.print
        while (Match(type: TokenType.Dot))
        {
            string part = ConsumeIdentifier(errorMessage: "Expected identifier after '.'");
            name = name + "." + part;
        }

        // Support ! suffix for failable routines
        if (Match(type: TokenType.Bang))
        {
            name = name + "!";
        }

        // Check for generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.Less))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");
        }

        // Parameters
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after routine name");
        var parameters = new List<Parameter>();
        bool isVariadic = false;

        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                // Check for variadic marker (...)
                if (Match(type: TokenType.DotDotDot))
                {
                    isVariadic = true;
                    break; // ... must be last
                }

                string paramName = ConsumeIdentifier(errorMessage: "Expected parameter name");
                Consume(type: TokenType.Colon, errorMessage: "Expected ':' after parameter name");
                TypeExpression paramType = ParseType();
                parameters.Add(item: new Parameter(Name: paramName,
                    Type: paramType,
                    DefaultValue: null,
                    Location: GetLocation()));
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after parameters");

        // Return type
        TypeExpression? returnType = null;
        if (Match(type: TokenType.Arrow))
        {
            returnType = ParseType();
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

        ConsumeStatementTerminator();

        // Default to "C" calling convention if not specified
        string effectiveCallingConvention = callingConvention ?? "C";

        return new ImportedDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Parameters: parameters,
            ReturnType: returnType,
            CallingConvention: effectiveCallingConvention,
            IsVariadic: isVariadic,
            Location: location);
    }
}
