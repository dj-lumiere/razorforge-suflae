namespace Compiler.Parser;

using Lexer;
using SyntaxTree;
using Diagnostics;

/// <summary>
/// Partial class containing type, entity, and routine declaration parsing.
/// </summary>
public partial class Parser
{
    private EntityDeclaration ParseEntityDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Open)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected entity name");

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.LeftBracket))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints)
                result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            Consume(type: TokenType.RightBracket,
                errorMessage: "Expected ']' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        // Supports needs before or after obeys
        List<GenericConstraintDeclaration>? constraints =
            ParseGenericConstraints(genericParams: genericParams,
                existingConstraints: inlineConstraints);


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
        constraints = ParseGenericConstraints(genericParams: genericParams,
            existingConstraints: constraints);

        var members = new List<Declaration>();
        bool hasPass = false;

        // Parse entity body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after entity header");

        // Enable member variable declaration syntax inside entity body
        // Entities allow modifiers on member variables (unlike records)
        bool wasParsingTypeBody = _parsingTypeBody;
        bool wasParsingStrictRecordBody = _parsingStrictRecordBody;
        _parsingTypeBody = true;
        _parsingStrictRecordBody = false; // Entities allow modifiers

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
                    throw ThrowParseError(code: GrammarDiagnosticCode.InvalidDeclarationInBody,
                        message:
                        $"Expected declaration inside entity body, got {node.GetType().Name}");
                }
            }

            if (Check(type: TokenType.Dedent))
            {
                ProcessDedentTokens();
            }
            else if (!IsAtEnd)
            {
                throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedDedentAfterBody,
                    message: "Expected dedent after entity body");
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
    /// <param name="annotations">Optional annotations attached to the record declaration.</param>
    /// <returns>A <see cref="RecordDeclaration"/> AST node.</returns>
    private RecordDeclaration ParseRecordDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Open, List<string>? annotations = null)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected record name");

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.LeftBracket))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints)
                result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            Consume(type: TokenType.RightBracket,
                errorMessage: "Expected ']' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints =
            ParseGenericConstraints(genericParams: genericParams,
                existingConstraints: inlineConstraints);


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
        constraints = ParseGenericConstraints(genericParams: genericParams,
            existingConstraints: constraints);

        var members = new List<Declaration>();
        bool hasPass = false;

        // Parse record body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after record header");

        // Enable member variable declaration syntax inside record body
        // Records are strict: no modifiers allowed on member variables
        bool wasParsingTypeBody = _parsingTypeBody;
        bool wasParsingStrictRecordBody = _parsingStrictRecordBody;
        _parsingTypeBody = true;
        _parsingStrictRecordBody = true; // Records disallow modifiers on member variables

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
                    throw ThrowParseError(code: GrammarDiagnosticCode.InvalidDeclarationInBody,
                        message:
                        $"Expected declaration inside record body, got {node.GetType().Name}");
                }
            }

            if (Check(type: TokenType.Dedent))
            {
                ProcessDedentTokens();
            }
            else if (!IsAtEnd)
            {
                throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedDedentAfterBody,
                    message: "Expected dedent after record body");
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
    private ChoiceDeclaration ParseChoiceDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Open)
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

            // Check if it's a member routine - no visibility modifiers allowed in choice
            if (Check(type: TokenType.Routine))
            {
                Advance(); // consume 'routine'
                RoutineDeclaration method = ParseRoutineDeclaration();
                methods.Add(item: method);
            }
            else
            {
                // Parse enum variant
                string variantName =
                    ConsumeIdentifier(errorMessage: "Expected choice variant name");

                // CASE: value syntax for choice values (e.g., OK: 200)
                // Store expression as-is; semantic analyzer will validate and convert
                Expression? value = null;
                if (Match(type: TokenType.Colon))
                {
                    value = ParseExpression();
                }

                variants.Add(item: new ChoiceCase(Name: variantName,
                    Value: value,
                    Location: GetLocation()));
                Match(type: TokenType.Newline);
            }
        }

        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }
        else if (!IsAtEnd)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedDedentAfterBody,
                message: "Expected dedent after choice body");
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
    private FlagsDeclaration ParseFlagsDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Open)
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
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedDedentAfterBody,
                message: "Expected dedent after flags body");
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
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints)
                result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            Consume(type: TokenType.RightBracket,
                errorMessage: "Expected ']' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints =
            ParseGenericConstraints(genericParams: genericParams,
                existingConstraints: inlineConstraints);


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
                memberType = new TypeExpression(Name: "None",
                    GenericArguments: null,
                    Location: memberLoc);
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
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedDedentAfterBody,
                message: "Expected dedent after variant body");
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
    private ProtocolDeclaration ParseProtocolDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Open)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected protocol name");

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.LeftBracket))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints)
                result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            Consume(type: TokenType.RightBracket,
                errorMessage: "Expected ']' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints =
            ParseGenericConstraints(genericParams: genericParams,
                existingConstraints: inlineConstraints);


        // Parse parent protocols (protocol X obeys Y, Z)
        var parentProtocols = new List<TypeExpression>();
        if (Match(type: TokenType.Obeys))
        {
            do
            {
                while (Match(type: TokenType.Newline)) { } // Skip newlines before protocol name

                parentProtocols.Add(item: ParseType());
                // Newlines between comma-separated protocols are handled by the 'before' skip
            } while (Match(type: TokenType.Comma));
        }

        // Try constraints again after obeys (supports needs on next line)
        constraints = ParseGenericConstraints(genericParams: genericParams,
            existingConstraints: constraints);

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
                string methodName = ConsumeIdentifier(errorMessage: "Expected member routine name");

                // Handle Me.methodName syntax for instance member routines
                // Protocol member routines can be: "routine Me.methodName()" or "routine methodName()"
                while (Match(type: TokenType.Dot))
                {
                    string part =
                        ConsumeMethodName(errorMessage: "Expected member routine name after '.'");
                    methodName = methodName + "." + part;
                }

                // Support failable member routines: "routine!"
                if (Match(type: TokenType.Bang))
                {
                    methodName += "!";
                }

                // Parameters
                Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after member routine name");
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
                            string paramName =
                                ConsumeIdentifier(errorMessage: "Expected parameter name");

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
                    Annotations: methodAnnotations.Count > 0
                        ? methodAnnotations
                        : null,
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
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedDedentAfterBody,
                message: "Expected dedent after protocol body");
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
                        string name =
                            ConsumeIdentifier(
                                errorMessage: "Expected type name in selective import");
                        specificImports.Add(item: name);
                    } while (Match(type: TokenType.Comma));

                    Consume(type: TokenType.RightBracket,
                        errorMessage: "Expected ']' after selective imports");
                }
                else
                {
                    // Single type: Core.Bool -> module "Core", type "Bool"
                    string typeName =
                        ConsumeIdentifier(errorMessage: "Expected type name after '.'");
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
        string newName =
            ConsumeIdentifier(errorMessage: "Expected new identifier in redefinition");

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

}
