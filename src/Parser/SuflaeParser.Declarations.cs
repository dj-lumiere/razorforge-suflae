using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.RazorForge.Parser;

namespace Compilers.Suflae.Parser;

/// <summary>
/// Partial class containing declaration parsing (variables, routines, entities, records, variants, etc.).
/// </summary>
public partial class SuflaeParser
{
    /// <summary>
    /// Parses attributes like @crash_only, @inline, @[readonly, inline], etc.
    /// Attributes are prefixed with @ and followed by an identifier, optionally with arguments.
    /// Also supports compound attributes: @[attr1, attr2, attr3]
    /// </summary>
    private List<string> ParseAttributes()
    {
        var attributes = new List<string>();

        // Handle both @attribute, @[...] compound, and special tokens (@intrinsic_type, @intrinsic_routine, @native)
        while (Check(TokenType.At, TokenType.IntrinsicType, TokenType.IntrinsicRoutine, TokenType.Native))
        {
            string attrName;

            if (Match(type: TokenType.IntrinsicType))
            {
                // @intrinsic_type was tokenized as a single IntrinsicType token
                attrName = "intrinsic_type";
                attributes.Add(item: attrName);
            }
            else if (Match(type: TokenType.IntrinsicRoutine))
            {
                // @intrinsic_routine was tokenized as a single IntrinsicRoutine token
                attrName = "intrinsic_routine";
                attributes.Add(item: attrName);
            }
            else if (Match(type: TokenType.Native))
            {
                // @native was tokenized as a single Native token
                attrName = "native";
                attributes.Add(item: attrName);
            }
            else if (Match(type: TokenType.At))
            {
                // Check for compound attribute syntax: @[attr1, attr2, ...]
                if (Match(type: TokenType.LeftBracket))
                {
                    // Parse comma-separated list of attribute names
                    do
                    {
                        string compoundAttr = ConsumeIdentifier(errorMessage: "Expected attribute name in compound attribute");

                        // Check for optional arguments on each attribute
                        if (Match(type: TokenType.LeftParen))
                        {
                            compoundAttr += "(" + ParseAttributeArgumentList() + ")";
                        }

                        attributes.Add(item: compoundAttr);
                    } while (Match(type: TokenType.Comma));

                    Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after compound attributes");
                }
                else
                {
                    // Regular attribute: @identifier
                    attrName = ConsumeIdentifier(errorMessage: "Expected attribute name after '@'");

                    // Check for attribute arguments: @something("size_of") or @config(name: "value", count: 5)
                    if (Match(type: TokenType.LeftParen))
                    {
                        attrName += "(" + ParseAttributeArgumentList() + ")";
                    }

                    attributes.Add(item: attrName);
                }
            }
            else
            {
                break; // No more attributes
            }

            // Skip newlines between attributes (allows multiple @attr on separate lines)
            while (Match(type: TokenType.Newline))
            {
                // Skip newlines
            }
        }

        return attributes;
    }

    /// <summary>
    /// Parses the argument list for an attribute (the content inside parentheses).
    /// </summary>
    /// <returns>String representation of the argument list.</returns>
    private string ParseAttributeArgumentList()
    {
        var arguments = new List<string>();

        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                // Check for named argument: name: value or name = value
                TokenType nextToken = PeekToken(offset: 1).Type;
                if (Check(type: TokenType.Identifier) &&
                    (nextToken == TokenType.Colon || nextToken == TokenType.Assign))
                {
                    string argName = ConsumeIdentifier(errorMessage: "Expected argument name");
                    // Accept both ':' and '=' as separators
                    if (!Match(TokenType.Colon, TokenType.Assign))
                    {
                        throw ThrowParseError("Expected ':' or '=' after argument name");
                    }
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

        return string.Join(separator: ", ", values: arguments);
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

        // Identifier (for choice values or constant references)
        if (Check(type: TokenType.Identifier))
        {
            return Advance()
               .Text;
        }

        throw ThrowParseError($"Expected attribute value, got {CurrentToken.Type}");
    }

    /// <summary>
    /// Parses a variable declaration.
    /// Syntax: <c>var name: Type = value</c> or <c>let name: Type = value</c> or <c>preset name: Type = value</c>
    /// </summary>
    /// <param name="visibility">Access modifier (public, published, internal, private).</param>
    /// <param name="storage">Storage class modifier (default: None).</param>
    /// <returns>A <see cref="VariableDeclaration"/> AST node.</returns>
    private VariableDeclaration ParseVariableDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Open,
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
    /// Parses a field declaration in records.
    /// Syntax: <c>name: Type</c> or <c>public name: Type = value</c>
    /// Fields are declared without var/let keywords.
    /// </summary>
    /// <param name="visibility">Access modifier (public, published, internal, private).</param>
    /// <returns>A <see cref="VariableDeclaration"/> AST node.</returns>
    private VariableDeclaration ParseFieldDeclaration(VisibilityModifier visibility = VisibilityModifier.Open)
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
    /// Parses a routine (function) declaration.
    /// Syntax: <c>routine name(params) -&gt; ReturnType:</c> followed by indented body.
    /// Supports ! suffix for failable routines.
    /// </summary>
    /// <remarks>
    /// Parsing phases:
    ///
    /// PHASE 1: VALIDATION
    ///   - Reject nested routine declarations
    ///
    /// PHASE 2: NAME AND FAILABLE MARKER
    ///   - Parse routine name
    ///   - Check for ! suffix (failable routine)
    ///
    /// PHASE 3: PARAMETERS
    ///   - Parse parameter list: (name: Type, name: Type = default)
    ///
    /// PHASE 4: RETURN TYPE
    ///   - Optional: -> ReturnType
    ///
    /// PHASE 5: BODY
    ///   - Parse indented block
    /// </remarks>
    /// <param name="visibility">Access modifier for the routine.</param>
    /// <param name="attributes">List of attributes applied to the routine.</param>
    /// <param name="storage">Storage class modifier (default: None, can be Common for type-level static).</param>
    /// <returns>A <see cref="RoutineDeclaration"/> AST node.</returns>
    private RoutineDeclaration ParseRoutineDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Open,
        List<string>? attributes = null,
        StorageClass storage = StorageClass.None,
        AsyncStatus asyncStatus = AsyncStatus.None)
    {
        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 1: VALIDATION
        // ═══════════════════════════════════════════════════════════════════════════
        if (_inRoutineBody)
        {
            throw ThrowParseError("Nested routine declarations are not allowed. Define routines at module or type level.");
        }

        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 2: NAME PARSING - Base name + optional type-level generic parameters
        // ═══════════════════════════════════════════════════════════════════════════
        // Examples:
        //   "foo"          -> name="foo", no generics
        //   "List<T>"      -> name="List", genericParams=["T"]
        //   "Point.get_x"  -> name="Point.get_x"
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

            Consume(type: TokenType.Greater, errorMessage: "Expected '>' after generic parameters");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 2b: Parse dot-separated qualified name (for methods)
        // ═══════════════════════════════════════════════════════════════════════════
        // Examples:
        //   "Point.get_x"          -> name="Point.get_x"
        //   "List<T>.append"       -> name="List<T>.append"
        // ═══════════════════════════════════════════════════════════════════════════
        while (Match(type: TokenType.Dot))
        {
            string part = ConsumeMethodName(errorMessage: "Expected method name after '.'");

            // If we parsed generic params before the dot, embed them in the name
            if (hasGenericParams && !name.Contains(value: '.') && genericParams != null)
            {
                name = name + "<" + string.Join(separator: ", ", values: genericParams) + ">." + part;
                hasGenericParams = false; // Only add once
            }
            else
            {
                name = name + "." + part;
            }

            // Check for method-level generic params AFTER the method name
            if (Check(type: TokenType.Less))
            {
                Token nextToken = PeekToken(offset: 1);
                if (nextToken.Type == TokenType.Identifier || nextToken.Type == TokenType.TypeIdentifier || nextToken.Type == TokenType.Greater)
                {
                    Match(type: TokenType.Less);
                    (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();

                    // Merge type-level and method-level generic parameters
                    if (genericParams is { Count: > 0 })
                    {
                        genericParams = new List<string>(collection: genericParams);
                        genericParams.AddRange(collection: result.genericParams);
                        if (inlineConstraints != null && result.inlineConstraints != null)
                        {
                            inlineConstraints = new List<GenericConstraintDeclaration>(collection: inlineConstraints);
                            inlineConstraints.AddRange(collection: result.inlineConstraints);
                        }
                    }
                    else
                    {
                        genericParams = result.genericParams;
                        inlineConstraints = result.inlineConstraints;
                    }

                    Consume(type: TokenType.Greater, errorMessage: "Expected '>' after generic parameters");
                }
            }
        }

        // Support ! suffix for failable functions (can appear after qualified name)
        bool isFailable = Match(type: TokenType.Bang);

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 3: PARAMETERS
        // ═══════════════════════════════════════════════════════════════════════════
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after routine name");
        var parameters = new List<Parameter>();

        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                string paramName = ConsumeIdentifier(errorMessage: "Expected parameter name");
                Consume(type: TokenType.Colon, errorMessage: "Expected ':' after parameter name");
                TypeExpression paramType = ParseType();

                Expression? defaultValue = null;
                if (Match(type: TokenType.Assign))
                {
                    defaultValue = ParseExpression();
                }

                parameters.Add(item: new Parameter(Name: paramName,
                    Type: paramType,
                    DefaultValue: defaultValue,
                    Location: GetLocation()));
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after parameters");

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 3.5: GENERIC CONSTRAINTS (requires clause)
        // ═══════════════════════════════════════════════════════════════════════════
        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(
            genericParams: genericParams, existingConstraints: inlineConstraints);

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 4: RETURN TYPE
        // ═══════════════════════════════════════════════════════════════════════════
        TypeExpression? returnType = null;
        if (Match(type: TokenType.Arrow))
        {
            returnType = ParseType();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 5: BODY (indented block)
        // ═══════════════════════════════════════════════════════════════════════════

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
            Attributes: attributes ?? [],
            Location: location,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            IsFailable: isFailable,
            Storage: storage,
            Async: asyncStatus);
    }

    /// <summary>
    /// Parses an entity (class/reference type) declaration.
    /// Syntax: <c>entity Name&lt;T&gt; follows Protocol:</c> followed by indented body.
    /// Entities are heap-allocated reference types.
    /// </summary>
    /// <param name="visibility">Access modifier for the entity.</param>
    /// <returns>An <see cref="EntityDeclaration"/> AST node.</returns>
    private EntityDeclaration ParseEntityDeclaration(VisibilityModifier visibility = VisibilityModifier.Open)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected entity name");

        // Register this type name for generic disambiguation
        _knownTypeNames.Add(item: name);

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.Less))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            Consume(type: TokenType.Greater, errorMessage: "Expected '>' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

        // Push generic parameters into scope before parsing body
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Push(item: [..genericParams]);
        }

        // Parse interfaces/protocols the entity follows
        var interfaces = new List<TypeExpression>();
        if (Match(type: TokenType.Follows))
        {
            do
            {
                interfaces.Add(item: ParseType());
            } while (Match(type: TokenType.Comma));
        }

        var members = new List<Declaration>();

        // Parse entity body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after entity header");

        // Enable field declaration syntax inside entity body
        // Entities allow modifiers on fields (unlike records)
        bool wasParsingTypeBody = _parsingTypeBody;
        bool wasParsingStrictRecordBody = _parsingStrictRecordBody;
        _parsingTypeBody = true;
        _parsingStrictRecordBody = false;  // Entities allow modifiers

        // Parse indented members
        if (Check(type: TokenType.Indent))
        {
            ProcessIndentToken();

            while (!Check(type: TokenType.Dedent) && !IsAtEnd)
            {
                if (Match(type: TokenType.Newline))
                {
                    continue;
                }

                // Allow 'pass' to indicate empty body
                if (Match(type: TokenType.Pass))
                {
                    Match(type: TokenType.Newline);
                    continue;
                }

                IAstNode node = ParseDeclaration();
                if (node is Declaration member)
                {
                    members.Add(item: member);
                }
                else
                {
                    throw ThrowParseError($"Expected declaration inside entity body, got {node.GetType().Name}");
                }
            }

            if (Check(type: TokenType.Dedent))
            {
                ProcessDedentTokens();
            }
            else if (!IsAtEnd)
            {
                throw ThrowParseError("Expected dedent after entity body");
            }
        }

        _parsingTypeBody = wasParsingTypeBody;
        _parsingStrictRecordBody = wasParsingStrictRecordBody;

        // Pop generic parameter scope
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Pop();
        }

        return new EntityDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Protocols: interfaces,
            Members: members,
            Visibility: visibility,
            Location: location);
    }

    /// <summary>
    /// Parses a record (struct/value type) declaration.
    /// Syntax: <c>record Name&lt;T&gt; follows Protocol:</c> followed by indented members.
    /// Records are stack-allocated value types.
    /// </summary>
    /// <param name="visibility">Access modifier for the record.</param>
    /// <returns>A <see cref="RecordDeclaration"/> AST node.</returns>
    private RecordDeclaration ParseRecordDeclaration(VisibilityModifier visibility = VisibilityModifier.Open)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected record name");

        // Register this type name for generic disambiguation
        _knownTypeNames.Add(item: name);

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.Less))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            Consume(type: TokenType.Greater, errorMessage: "Expected '>' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

        // Push generic parameters into scope before parsing body
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
                interfaces.Add(item: ParseType());
            } while (Match(type: TokenType.Comma));
        }

        var members = new List<Declaration>();

        // Parse record body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after record header");

        // Enable field declaration syntax inside record body
        // Records are strict: no modifiers allowed on fields
        bool wasParsingTypeBody = _parsingTypeBody;
        bool wasParsingStrictRecordBody = _parsingStrictRecordBody;
        _parsingTypeBody = true;
        _parsingStrictRecordBody = true;  // Records disallow modifiers on fields

        if (Check(type: TokenType.Indent))
        {
            ProcessIndentToken();

            while (!Check(type: TokenType.Dedent) && !IsAtEnd)
            {
                if (Match(type: TokenType.Newline))
                {
                    continue;
                }

                // Allow 'pass' to indicate empty body
                if (Match(type: TokenType.Pass))
                {
                    Match(type: TokenType.Newline);
                    continue;
                }

                IAstNode node = ParseDeclaration();
                if (node is Declaration member)
                {
                    members.Add(item: member);
                }
                else
                {
                    throw ThrowParseError($"Expected declaration inside record body, got {node.GetType().Name}");
                }
            }

            if (Check(type: TokenType.Dedent))
            {
                ProcessDedentTokens();
            }
            else if (!IsAtEnd)
            {
                throw ThrowParseError("Expected dedent after record body");
            }
        }

        _parsingTypeBody = wasParsingTypeBody;
        _parsingStrictRecordBody = wasParsingStrictRecordBody;

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
    /// Parses a choice (C-style enum) declaration.
    /// Syntax: <c>choice Name:</c> followed by indented cases with optional values.
    /// Choices are simple enumerations with integer-backed values.
    /// </summary>
    /// <param name="visibility">Access modifier for the choice.</param>
    /// <returns>A <see cref="ChoiceDeclaration"/> AST node.</returns>
    private ChoiceDeclaration ParseChoiceDeclaration(VisibilityModifier visibility = VisibilityModifier.Open)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected option name");

        // Register this type name for generic disambiguation
        _knownTypeNames.Add(item: name);

        var variants = new List<ChoiceCase>();
        var methods = new List<RoutineDeclaration>();

        // Parse option body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after option header");

        if (!Check(type: TokenType.Indent))
        {
            return new ChoiceDeclaration(Name: name,
                Cases: variants,
                Methods: methods,
                Visibility: visibility,
                Location: location);
        }

        ProcessIndentToken();

        while (!Check(type: TokenType.Dedent) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            // Check if it's a method (routine) - no visibility modifiers allowed in choice
            if (Check(type: TokenType.Routine))
            {
                Advance(); // consume 'routine'
                RoutineDeclaration method = ParseRoutineDeclaration();
                methods.Add(item: method);
            }
            else
            {
                // Parse enum variant
                string variantName = ConsumeIdentifier(errorMessage: "Expected option variant name");

                // CASE: value syntax for choice values (e.g., OK: 200)
                // Store expression as-is; semantic analyzer will validate and convert
                Expression? value = null;
                if (Match(type: TokenType.Colon))
                {
                    value = ParseExpression();
                }

                variants.Add(item: new ChoiceCase(Name: variantName, Value: value, Location: GetLocation()));
                Match(type: TokenType.Newline);
            }
        }

        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }
        else if (!IsAtEnd)
        {
            throw ThrowParseError("Expected dedent after option body");
        }

        return new ChoiceDeclaration(Name: name,
            Cases: variants,
            Methods: methods,
            Visibility: visibility,
            Location: location);
    }

    /// <summary>
    /// Parses a flags declaration (combinable bitflag set).
    /// Grammar: "flags" IDENTIFIER NEWLINE INDENT FlagsMember { FlagsMember } DEDENT
    /// FlagsMember = UPPER_IDENTIFIER NEWLINE
    /// </summary>
    /// <param name="visibility">Access modifier for this flags type.</param>
    /// <returns>A <see cref="FlagsDeclaration"/> AST node.</returns>
    private FlagsDeclaration ParseFlagsDeclaration(VisibilityModifier visibility = VisibilityModifier.Open)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected flags name");

        // Register this type name for generic disambiguation
        _knownTypeNames.Add(item: name);

        var members = new List<string>();

        // Parse flags body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after flags header");

        if (!Check(type: TokenType.Indent))
        {
            return new FlagsDeclaration(Name: name,
                Members: members,
                Visibility: visibility,
                Location: location);
        }

        ProcessIndentToken();

        while (!Check(type: TokenType.Dedent) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            string memberName = ConsumeIdentifier(errorMessage: "Expected flags member name");
            members.Add(item: memberName);
            Match(type: TokenType.Newline);
        }

        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }
        else if (!IsAtEnd)
        {
            throw ThrowParseError("Expected dedent after flags body");
        }

        return new FlagsDeclaration(Name: name,
            Members: members,
            Visibility: visibility,
            Location: location);
    }

    /// <summary>
    /// Parses a variant (tagged union) declaration.
    /// Syntax: <c>variant Name:</c> followed by indented cases with optional associated types.
    /// Variants are sum types where each case can carry different data.
    /// </summary>
    /// <returns>A <see cref="VariantDeclaration"/> AST node.</returns>
    private VariantDeclaration ParseVariantDeclaration()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected variant name");

        // Register this type name for generic disambiguation
        _knownTypeNames.Add(item: name);

        // Generic parameters
        List<string>? genericParams = null;
        if (Match(type: TokenType.Less))
        {
            genericParams = [];
            do
            {
                genericParams.Add(item: ConsumeIdentifier(errorMessage: "Expected generic parameter name"));
            } while (Match(type: TokenType.Comma));

            Consume(type: TokenType.Greater, errorMessage: "Expected '>' after generic parameters");
        }

        // Push generic parameters into scope before parsing body
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Push(item: [..genericParams]);
        }

        // Indented block follows variant header

        var cases = new List<VariantCase>();

        // Parse variant body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after variant header");

        if (!Check(type: TokenType.Indent))
        {
            // Pop generic parameter scope
            if (genericParams is { Count: > 0 })
            {
                _genericParameterScopes.Pop();
            }

            return new VariantDeclaration(Name: name,
                GenericParameters: genericParams,
                Cases: cases,
                Location: location);
        }

        ProcessIndentToken();

        while (!Check(type: TokenType.Dedent) && !IsAtEnd)
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
            Match(type: TokenType.Newline);
        }

        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }
        else if (!IsAtEnd)
        {
            throw ThrowParseError("Expected dedent after variant body");
        }

        // Pop generic parameter scope
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Pop();
        }

        return new VariantDeclaration(Name: name,
            GenericParameters: genericParams,
            Cases: cases,
            Location: location);
    }

    /// <summary>
    /// Parses a protocol (trait/interface) declaration.
    /// Called "protocol" in Suflae, but uses the same AST as protocols.
    /// Syntax: <c>protocol Name:</c> followed by indented routine signatures.
    /// </summary>
    /// <param name="visibility">Access modifier for the protocol.</param>
    /// <returns>A <see cref="ProtocolDeclaration"/> AST node.</returns>
    private ProtocolDeclaration ParseProtocolDeclaration(VisibilityModifier visibility = VisibilityModifier.Open)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected protocol name");

        // Register this type name for generic disambiguation
        _knownTypeNames.Add(item: name);

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.Less))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            Consume(type: TokenType.Greater, errorMessage: "Expected '>' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

        // Push generic parameters into scope before parsing body
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Push(item: [..genericParams]);
        }

        // Parse parent protocols (protocol X follows Y, Z)
        var parentProtocols = new List<TypeExpression>();
        if (Match(type: TokenType.Follows))
        {
            do
            {
                while (Match(type: TokenType.Newline)) { } // Skip newlines before protocol name
                parentProtocols.Add(item: ParseType());
            }
            while (Match(type: TokenType.Comma));
        }

        // Indented block follows protocol header

        var methods = new List<RoutineSignature>();

        // Parse protocol body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after protocol header");

        if (!Check(type: TokenType.Indent))
        {
            // Pop generic parameter scope before returning
            if (genericParams is { Count: > 0 })
            {
                _genericParameterScopes.Pop();
            }

            return new ProtocolDeclaration(Name: name,
                GenericParameters: genericParams,
                ParentProtocols: parentProtocols,
                Methods: methods,
                Visibility: visibility,
                Location: location,
                GenericConstraints: constraints);
        }

        ProcessIndentToken();

        while (!Check(type: TokenType.Dedent) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            // Parse optional attributes on routine signatures (e.g., @readonly)
            List<string> methodAttributes = ParseAttributes();

            // Skip newlines between attributes and routine keyword
            while (Match(type: TokenType.Newline))
            {
            }

            // Parse routine signature
            Consume(type: TokenType.Routine, errorMessage: "Expected 'routine' in protocol method");
            string methodName = ConsumeIdentifier(errorMessage: "Expected method name");

            // Handle Me.methodName syntax for instance methods
            // Protocol methods can be: "routine Me.methodName()" or "routine methodName()"
            while (Match(type: TokenType.Dot))
            {
                string part = ConsumeMethodName(errorMessage: "Expected method name after '.'");
                methodName = methodName + "." + part;
            }

            // Support failable methods: "method!"
            if (Match(type: TokenType.Bang))
            {
                methodName += "!";
            }

            // Parameters
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

            // Return type
            TypeExpression? returnType = null;
            if (Match(type: TokenType.Arrow))
            {
                returnType = ParseType();
            }

            methods.Add(item: new RoutineSignature(Name: methodName,
                Parameters: parameters,
                ReturnType: returnType,
                Attributes: methodAttributes.Count > 0 ? methodAttributes : null,
                Location: GetLocation()));
            Match(type: TokenType.Newline);
        }

        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }
        else if (!IsAtEnd)
        {
            throw ThrowParseError("Expected dedent after protocol body");
        }

        // Pop generic parameter scope
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Pop();
        }

        return new ProtocolDeclaration(Name: name,
            GenericParameters: genericParams,
            ParentProtocols: parentProtocols,
            Methods: methods,
            Visibility: visibility,
            Location: location,
            GenericConstraints: constraints);
    }

    /// <summary>
    /// Parses a module declaration.
    /// Syntax: <c>module path/to/module</c>
    /// Uses slash separators for module paths.
    /// </summary>
    /// <returns>A <see cref="ModuleDeclaration"/> AST node.</returns>
    private ModuleDeclaration ParseModuleDeclaration()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string modulePath = "";

        // Parse module path - could be multiple identifiers separated by slashes
        // e.g., module standard/errors
        do
        {
            string part = ConsumeIdentifier(errorMessage: "Expected module name");
            modulePath += part;
            if (Match(type: TokenType.Slash))
            {
                modulePath += "/";
            }
            else
            {
                break;
            }
        } while (true);

        ConsumeStatementTerminator();

        return new ModuleDeclaration(Path: modulePath, Location: location);
    }

    /// <summary>
    /// Parses an import declaration.
    /// Syntax: <c>import path/to/module</c> or <c>import path/to/module as alias</c>
    /// Uses slash separators for module paths.
    /// </summary>
    /// <returns>An <see cref="ImportDeclaration"/> AST node.</returns>
    private ImportDeclaration ParseImportDeclaration()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string modulePath = "";
        string? alias = null;
        List<string>? specificImports = null;

        // Parse module path - could be multiple identifiers separated by slashes
        // Dot marks a specific type within the module: import razorforge/Core.Bool
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
                if (Match(type: TokenType.LeftBracket))
                {
                    // Selective imports: Module.[A, B, C]
                    specificImports = new List<string>();
                    do
                    {
                        string name = ConsumeIdentifier(errorMessage: "Expected type name in selective import");
                        specificImports.Add(item: name);
                        _knownTypeNames.Add(item: name);
                    } while (Match(type: TokenType.Comma));
                    Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after selective imports");
                }
                else
                {
                    // Single type: Core.Bool → module "Core", type "Bool"
                    string typeName = ConsumeIdentifier(errorMessage: "Expected type name after '.'");
                    modulePath += "." + typeName;
                }
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

        // Register imported types/modules for generic disambiguation
        // import Collections.SortedDict -> adds "SortedDict" to known types (bare name usage)
        // import Collections -> adds "Collections" to modules (qualified name usage)
        if (modulePath.Contains(value: '.'))
        {
            // Specific type import: Collections.SortedDict
            string typeName = modulePath[(modulePath.LastIndexOf(value: '.') + 1)..];
            _knownTypeNames.Add(item: typeName);
        }
        else
        {
            // Module import: Collections
            _importedModules.Add(item: modulePath);
        }

        return new ImportDeclaration(ModulePath: modulePath,
            Alias: alias,
            SpecificImports: specificImports,
            Location: location);
    }

    /// <summary>
    /// Parses a define (type alias/redefinition) declaration.
    /// Syntax: <c>define OldName as NewName</c>
    /// Creates a type alias for cleaner code.
    /// </summary>
    /// <returns>A <see cref="DefineDeclaration"/> AST node.</returns>
    private IAstNode ParseDefineDeclaration()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string oldName = ConsumeIdentifier(errorMessage: "Expected identifier after 'define'");
        Consume(type: TokenType.As, errorMessage: "Expected 'as' in redefinition");
        string newName = ConsumeIdentifier(errorMessage: "Expected new identifier in redefinition");

        ConsumeStatementTerminator();

        return new DefineDeclaration(OldName: oldName, NewName: newName, Location: location);
    }

    /// <summary>
    /// Parses a preset (compile-time constant) declaration.
    /// Syntax: <c>preset name: Type = value</c>
    /// </summary>
    /// <returns>A <see cref="PresetDeclaration"/> AST node.</returns>
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
        var visibility = VisibilityModifier.Open; // Default
        var storage = StorageClass.None; // Default
        bool hasVisibility = false;
        bool hasStorage = false;

        // Parse modifiers in any order (visibility and storage can appear in any order)
        while (true)
        {
            // Visibility modifiers
            if (!hasVisibility && Match(type: TokenType.Open))
            {
                visibility = VisibilityModifier.Open;
                hasVisibility = true;
            }
            else if (!hasVisibility && Match(type: TokenType.Posted))
            {
                visibility = VisibilityModifier.Posted;
                hasVisibility = true;
            }
            else if (!hasVisibility && Match(type: TokenType.Secret))
            {
                visibility = VisibilityModifier.Secret;
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
}
