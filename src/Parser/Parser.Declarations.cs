using SyntaxTree;
using Compiler.Lexer;
using SemanticAnalysis.Enums;
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
    private List<string> ParseAnnotations()
    {
        var annotations = new List<string>();

        // Handle @annotation and @[...] compound annotations
        while (Check(type: TokenType.At))
        {
            string annotName;

            if (Match(type: TokenType.At))
            {
                // Check for compound annotation syntax: @[attr1, attr2, ...]
                if (Match(type: TokenType.LeftBracket))
                {
                    // Parse comma-separated list of annotation names
                    do
                    {
                        string compoundAnnot = ConsumeIdentifier(errorMessage: "Expected annotation name in compound annotation");

                        // Check for optional arguments on each annotation
                        if (Match(type: TokenType.LeftParen))
                        {
                            compoundAnnot += "(" + ParseAnnotationArgumentList() + ")";
                        }

                        annotations.Add(item: compoundAnnot);
                    } while (Match(type: TokenType.Comma));

                    Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after compound annotations");
                }
                else
                {
                    // Regular annotation: @identifier
                    annotName = ConsumeIdentifier(errorMessage: "Expected annotation name after '@'");

                    // Check for annotation arguments: @something("size_of") or @config(name: "value", count: 5)
                    if (Match(type: TokenType.LeftParen))
                    {
                        annotName += "(" + ParseAnnotationArgumentList() + ")";
                    }

                    annotations.Add(item: annotName);
                }
            }
            else
            {
                break; // No more annotations
            }

            // Skip newlines between annotations (allows multiple @attr on separate lines)
            while (Match(type: TokenType.Newline))
            {
                // Skip newlines
            }
        }

        return annotations;
    }

    /// <summary>
    /// Parses the argument list for an annotation (the content inside parentheses).
    /// </summary>
    /// <returns>String representation of the argument list.</returns>
    private string ParseAnnotationArgumentList()
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
                        throw ThrowParseError(GrammarDiagnosticCode.UnexpectedToken,
                            "Expected ':' or '=' after argument name");
                    }
                    string argValue = ParseAnnotationValue();
                    arguments.Add(item: $"{argName}={argValue}");
                }
                else
                {
                    // Positional argument (string literal, number, identifier)
                    arguments.Add(item: ParseAnnotationValue());
                }
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after annotation arguments");

        return string.Join(separator: ", ", values: arguments);
    }

    /// <summary>
    /// Parses a single annotation argument value (string, number, bool, or identifier).
    /// </summary>
    /// <returns>String representation of the annotation value.</returns>
    private string ParseAnnotationValue()
    {
        // Annotation values are limited to build-time constants:
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
                TokenType.AddressLiteral))
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

        throw ThrowParseError(GrammarDiagnosticCode.ExpectedAnnotationValue,
            $"Expected annotation value, got {CurrentToken.Type}");
    }

    /// <summary>
    /// Parses a variable declaration.
    /// Syntax: <c>var name: Type = value</c> or <c>preset name: Type = value</c>
    /// </summary>
    /// <param name="visibility">Access modifier (public, published, internal, private).</param>
    /// <param name="storage">Storage class modifier (default: None).</param>
    /// <returns>A <see cref="VariableDeclaration"/> AST node.</returns>
    private VariableDeclaration ParseVariableDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Open,
        StorageClass storage = StorageClass.None)
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
            Storage: storage);
    }

    /// <summary>
    /// Parses a member variable declaration in records.
    /// Syntax: <c>name: Type</c> or <c>public name: Type = value</c>
    /// MemberVariables are declared without var keywords.
    /// </summary>
    /// <param name="visibility">Access modifier (public, published, internal, private).</param>
    /// <returns>A <see cref="VariableDeclaration"/> AST node.</returns>
    private VariableDeclaration ParseMemberVariableDeclaration(VisibilityModifier visibility = VisibilityModifier.Open)
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
    /// Parses a routine (function) declaration.
    /// Syntax: <c>routine name(params) -&gt; ReturnType</c> followed by indented body.
    /// Supports generic parameters, module-qualified names, failable routines (!), and inline constraints.
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
    ///   - Parse dot-separated qualified name (for methods)
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
    /// <param name="asyncStatus">Async status of the routine.</param>
    /// <param name="isDangerous">Whether the routine is marked as dangerous (RF only).</param>
    /// <returns>A <see cref="RoutineDeclaration"/> AST node.</returns>
    private RoutineDeclaration ParseRoutineDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Open,
        List<string>? annotations = null,
        StorageClass storage = StorageClass.None,
        AsyncStatus asyncStatus = AsyncStatus.None,
        bool isDangerous = false)
    {
        // ===============================================================================
        // PHASE 1: VALIDATION
        // ===============================================================================
        if (_inRoutineBody)
        {
            throw ThrowParseError(GrammarDiagnosticCode.NestedRoutineNotAllowed,
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
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;
            hasGenericParams = true;

            Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after generic parameters");
        }

        // ===============================================================================
        // PHASE 2b: Parse dot-separated qualified name (for methods)
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
                name = name + "[" + string.Join(separator: ", ", values: genericParams) + "]." + part;
                hasGenericParams = false; // Only add once
            }
            else
            {
                name = name + "." + part;
            }

            // Check for method-level generic params AFTER the method name
            // e.g., "List[T].get[I]" - the [I] belongs to the method
            if (Match(type: TokenType.LeftBracket))
            {
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

                    Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after generic parameters");
            }
        }

        // ===============================================================================
        // PHASE 2c: Parse failable marker (!)
        // ===============================================================================
        // Support ! suffix for failable functions (can appear after qualified name)
        bool isFailable = Match(type: TokenType.Bang);

        // ConsumeMethodName may have already included '!' in the name
        if (name.EndsWith('!'))
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
                // Handle 'me' parameter (self-reference for methods)
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
                    string paramName = ConsumeIdentifier(errorMessage: "Expected parameter name", allowKeywords: true);
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
            genericParams: genericParams, existingConstraints: inlineConstraints);

        // ===============================================================================
        // PHASE 5: RETURN TYPE
        // ===============================================================================
        TypeExpression? returnType = null;
        if (Match(type: TokenType.Arrow))
        {
            returnType = ParseType();
        }

        // Try constraints again after return type (supports needs on next line after ->)
        constraints = ParseGenericConstraints(
            genericParams: genericParams, existingConstraints: constraints);

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

    /// <summary>
    /// Parses an entity (class/reference type) declaration.
    /// Syntax: <c>entity Name[T] obeys Protocol</c> followed by indented body.
    /// Entities are heap-allocated reference types.
    /// </summary>
    /// <param name="visibility">Access modifier for the entity.</param>
    /// <returns>An <see cref="EntityDeclaration"/> AST node.</returns>
    private EntityDeclaration ParseEntityDeclaration(VisibilityModifier visibility = VisibilityModifier.Open)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected entity name");

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.LeftBracket))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        // Supports needs before or after obeys
        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);


        // Parse interfaces/protocols the entity obeys
        var interfaces = new List<TypeExpression>();
        if (Match(type: TokenType.Obeys))
        {
            do
            {
                while (Match(type: TokenType.Newline)) { } // Skip newlines before protocol name
                interfaces.Add(item: ParseType());
                // Newlines between comma-separated protocols are handled by the 'before' skip
            } while (Match(type: TokenType.Comma));
        }

        // Try constraints again after obeys (supports needs on next line)
        constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: constraints);

        var members = new List<Declaration>();
        bool hasPass = false;

        // Parse entity body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after entity header");

        // Enable member variable declaration syntax inside entity body
        // Entities allow modifiers on member variables (unlike records)
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
                if (Match(TokenType.Newline, TokenType.DocComment))
                {
                    continue;
                }

                // Allow 'pass' to indicate empty body
                if (Match(type: TokenType.Pass))
                {
                    hasPass = true;
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
                    throw ThrowParseError(GrammarDiagnosticCode.InvalidDeclarationInBody,
                        $"Expected declaration inside entity body, got {node.GetType().Name}");
                }
            }

            if (Check(type: TokenType.Dedent))
            {
                ProcessDedentTokens();
            }
            else if (!IsAtEnd)
            {
                throw ThrowParseError(GrammarDiagnosticCode.ExpectedDedentAfterBody,
                    "Expected dedent after entity body");
            }
        }

        _parsingTypeBody = wasParsingTypeBody;
        _parsingStrictRecordBody = wasParsingStrictRecordBody;


        return new EntityDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Protocols: interfaces,
            Members: members,
            Visibility: visibility,
            Location: location,
            HasPassBody: hasPass);
    }

    /// <summary>
    /// Parses a record (struct/value type) declaration.
    /// Syntax: <c>record Name[T] obeys Protocol</c> followed by indented members.
    /// Records are stack-allocated value types.
    /// </summary>
    /// <param name="visibility">Access modifier for the record.</param>
    /// <returns>A <see cref="RecordDeclaration"/> AST node.</returns>
    private RecordDeclaration ParseRecordDeclaration(VisibilityModifier visibility = VisibilityModifier.Open, List<string>? annotations = null)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected record name");

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.LeftBracket))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);


        // Parse interfaces/protocols the record obeys
        var interfaces = new List<TypeExpression>();
        if (Match(type: TokenType.Obeys))
        {
            do
            {
                while (Match(type: TokenType.Newline)) { } // Skip newlines before protocol name
                interfaces.Add(item: ParseType());
                // Newlines between comma-separated protocols are handled by the 'before' skip
            } while (Match(type: TokenType.Comma));
        }

        // Try constraints again after obeys (supports needs on next line)
        constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: constraints);

        var members = new List<Declaration>();
        bool hasPass = false;

        // Parse record body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after record header");

        // Enable member variable declaration syntax inside record body
        // Records are strict: no modifiers allowed on member variables
        bool wasParsingTypeBody = _parsingTypeBody;
        bool wasParsingStrictRecordBody = _parsingStrictRecordBody;
        _parsingTypeBody = true;
        _parsingStrictRecordBody = true;  // Records disallow modifiers on member variables

        if (Check(type: TokenType.Indent))
        {
            ProcessIndentToken();

            while (!Check(type: TokenType.Dedent) && !IsAtEnd)
            {
                if (Match(TokenType.Newline, TokenType.DocComment))
                {
                    continue;
                }

                // Allow 'pass' to indicate empty body
                if (Match(type: TokenType.Pass))
                {
                    hasPass = true;
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
                    throw ThrowParseError(GrammarDiagnosticCode.InvalidDeclarationInBody,
                        $"Expected declaration inside record body, got {node.GetType().Name}");
                }
            }

            if (Check(type: TokenType.Dedent))
            {
                ProcessDedentTokens();
            }
            else if (!IsAtEnd)
            {
                throw ThrowParseError(GrammarDiagnosticCode.ExpectedDedentAfterBody,
                    "Expected dedent after record body");
            }
        }

        _parsingTypeBody = wasParsingTypeBody;
        _parsingStrictRecordBody = wasParsingStrictRecordBody;


        return new RecordDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Protocols: interfaces,
            Members: members,
            Visibility: visibility,
            Location: location,
            HasPassBody: hasPass,
            Annotations: annotations);
    }

    /// <summary>
    /// Parses a choice (C-style enum) declaration.
    /// Syntax: <c>choice Name</c> followed by indented cases with optional values.
    /// Choices are simple enumerations with integer-backed values.
    /// </summary>
    /// <param name="visibility">Access modifier for the choice.</param>
    /// <returns>A <see cref="ChoiceDeclaration"/> AST node.</returns>
    private ChoiceDeclaration ParseChoiceDeclaration(VisibilityModifier visibility = VisibilityModifier.Open)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected choice name");

        var variants = new List<ChoiceCase>();
        var methods = new List<RoutineDeclaration>();

        // Parse choice body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after choice header");

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
                string variantName = ConsumeIdentifier(errorMessage: "Expected choice variant name");

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
            throw ThrowParseError(GrammarDiagnosticCode.ExpectedDedentAfterBody,
                "Expected dedent after choice body");
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
            throw ThrowParseError(GrammarDiagnosticCode.ExpectedDedentAfterBody,
                "Expected dedent after flags body");
        }

        return new FlagsDeclaration(Name: name,
            Members: members,
            Visibility: visibility,
            Location: location);
    }

    /// <summary>
    /// Parses a variant (tagged union) declaration.
    /// Syntax: <c>variant Name</c> followed by indented cases with optional associated types.
    /// Variants are sum types where each case can carry different data.
    /// </summary>
    /// <returns>A <see cref="VariantDeclaration"/> AST node.</returns>
    private VariantDeclaration ParseVariantDeclaration()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected variant name");

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.LeftBracket))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);


        var members = new List<VariantMember>();

        // Parse variant body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after variant header");

        if (!Check(type: TokenType.Indent))
        {
            return new VariantDeclaration(Name: name,
                GenericParameters: genericParams,
                GenericConstraints: constraints,
                Members: members,
                Location: location);
        }

        ProcessIndentToken();

        while (!Check(type: TokenType.Dedent) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            // Each member is a type expression (or None keyword)
            SourceLocation memberLoc = GetLocation();
            TypeExpression memberType;
            if (Match(type: TokenType.None))
            {
                memberType = new TypeExpression(Name: "None", GenericArguments: null, Location: memberLoc);
            }
            else
            {
                memberType = ParseType();
            }

            members.Add(item: new VariantMember(Type: memberType, Location: memberLoc));
            Match(type: TokenType.Newline);
        }

        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }
        else if (!IsAtEnd)
        {
            throw ThrowParseError(GrammarDiagnosticCode.ExpectedDedentAfterBody,
                "Expected dedent after variant body");
        }

        return new VariantDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Members: members,
            Location: location);
    }

    /// <summary>
    /// Parses a protocol (trait/interface) declaration.
    /// Syntax: <c>protocol Name</c> followed by indented routine signatures.
    /// </summary>
    /// <param name="visibility">Access modifier for the protocol.</param>
    /// <returns>A <see cref="ProtocolDeclaration"/> AST node.</returns>
    private ProtocolDeclaration ParseProtocolDeclaration(VisibilityModifier visibility = VisibilityModifier.Open)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected protocol name");

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.LeftBracket))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);


        // Parse parent protocols (protocol X obeys Y, Z)
        var parentProtocols = new List<TypeExpression>();
        if (Match(type: TokenType.Obeys))
        {
            do
            {
                while (Match(type: TokenType.Newline)) { } // Skip newlines before protocol name
                parentProtocols.Add(item: ParseType());
                // Newlines between comma-separated protocols are handled by the 'before' skip
            }
            while (Match(type: TokenType.Comma));
        }

        // Try constraints again after obeys (supports needs on next line)
        constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: constraints);

        var methods = new List<RoutineSignature>();

        // Parse protocol body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after protocol header");

        if (!Check(type: TokenType.Indent))
        {
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

            // Parse optional annotations on routine signatures (e.g., @readonly)
            List<string> methodAnnotations = ParseAnnotations();

            // Skip newlines between annotations and routine keyword
            while (Match(type: TokenType.Newline))
            {
            }

            // Parse routine signature
            if (Match(type: TokenType.Routine))
            {
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
                    Annotations: methodAnnotations.Count > 0 ? methodAnnotations : null,
                    Location: GetLocation()));
                Match(type: TokenType.Newline);
            }
            else
            {
                // Unknown token in protocol body, skip it
                Advance();
            }
        }

        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }
        else if (!IsAtEnd)
        {
            throw ThrowParseError(GrammarDiagnosticCode.ExpectedDedentAfterBody,
                "Expected dedent after protocol body");
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
                    } while (Match(type: TokenType.Comma));
                    Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after selective imports");
                }
                else
                {
                    // Single type: Core.Bool -> module "Core", type "Bool"
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
    /// Parses a preset (build-time constant) declaration.
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
    /// Parses an external (FFI) function declaration.
    /// RF-only construct. Syntax: <c>external("C") routine name(param: Type, ...) -&gt; ReturnType</c>
    /// Supports variadic functions and calling convention specification.
    /// </summary>
    /// <param name="callingConvention">The calling convention (e.g., "C"). Defaults to "C" if null.</param>
    /// <param name="annotations">Optional annotations applied to the external declaration.</param>
    /// <param name="isDangerous">Whether the external routine is marked as dangerous.</param>
    /// <returns>An <see cref="ExternalDeclaration"/> AST node.</returns>
    private ExternalDeclaration ParseExternalDeclaration(string? callingConvention = null, List<string>? annotations = null, bool isDangerous = false)
    {
        if (_language == Language.Suflae)
        {
            throw ThrowParseError(GrammarDiagnosticCode.RFOnlyConstruct,
                "External declarations are only available in RazorForge.");
        }

        SourceLocation location = GetLocation(token: PeekToken(offset: -2)); // -2 because we consumed 'external' and 'routine'

        string name = ConsumeIdentifier(errorMessage: "Expected routine name");

        // Support module-qualified names like Console.print
        while (Match(type: TokenType.Dot))
        {
            string part = ConsumeIdentifier(errorMessage: "Expected identifier after '.'");
            name = name + "." + part;
        }

        // Support ! suffix for failable routines
        if (Match(type: TokenType.Bang))
        {
            name += "!";
        }

        // Check for generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.LeftBracket))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after generic parameters");
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

        return new ExternalDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Parameters: parameters,
            ReturnType: returnType,
            CallingConvention: effectiveCallingConvention,
            IsVariadic: isVariadic,
            Annotations: annotations,
            IsDangerous: isDangerous,
            Location: location);
    }

    /// <summary>
    /// Parses an external block declaration grouping multiple external routines under one calling convention.
    /// RF-only construct. Syntax: <c>external("C")</c> followed by an indented block of routine declarations.
    /// Uses INDENT/DEDENT for the block structure.
    /// </summary>
    /// <param name="callingConvention">The calling convention (e.g., "C").</param>
    /// <param name="isDangerous">Whether all routines in the block are marked as dangerous.</param>
    /// <returns>An <see cref="ExternalBlockDeclaration"/> AST node.</returns>
    private ExternalBlockDeclaration ParseExternalBlockDeclaration(string? callingConvention, bool isDangerous)
    {
        if (_language == Language.Suflae)
        {
            throw ThrowParseError(GrammarDiagnosticCode.RFOnlyConstruct,
                "External block declarations are only available in RazorForge.");
        }

        SourceLocation blockLocation = GetLocation();

        // Expect a newline followed by an indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after external block header");

        var declarations = new List<Declaration>();

        if (Check(type: TokenType.Indent))
        {
            ProcessIndentToken();

            while (!Check(type: TokenType.Dedent) && !IsAtEnd)
            {
                if (Match(TokenType.Newline, TokenType.DocComment))
                {
                    continue;
                }

                // Per-routine dangerous modifier inside the block
                bool routineDangerous = isDangerous || Match(type: TokenType.Dangerous);
                Consume(type: TokenType.Routine, errorMessage: "Expected 'routine' inside external block");
                declarations.Add(item: ParseExternalDeclaration(
                    callingConvention: callingConvention, annotations: null, isDangerous: routineDangerous));
            }

            if (Check(type: TokenType.Dedent))
            {
                ProcessDedentTokens();
            }
            else if (!IsAtEnd)
            {
                throw ThrowParseError(GrammarDiagnosticCode.ExpectedDedentAfterBody,
                    "Expected dedent after external block");
            }
        }

        return new ExternalBlockDeclaration(
            Declarations: declarations, Location: blockLocation);
    }

    /// <summary>
    /// Parses visibility and storage class modifiers.
    /// Visibility: posted, secret, external
    /// Storage: common, global
    /// These are orthogonal and can be combined: posted common, secret common, etc.
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
}
