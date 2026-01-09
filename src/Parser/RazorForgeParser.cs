using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;

namespace Compilers.RazorForge.Parser;

/// <summary>
/// Parser for RazorForge language (brace-based syntax).
/// Converts a stream of tokens into an Abstract Syntax Tree (AST).
/// </summary>
/// <param name="tokens">The list of tokens to parse, produced by <see cref="Compilers.RazorForge.Lexer.RazorForgeTokenizer"/>.</param>
/// <param name="fileName">Optional source file name for error reporting and source location tracking.</param>
public partial class RazorForgeParser(List<Token> tokens, string? fileName = null)
{
    #region Base Parser Fields

    /// <summary>
    /// Current position in the token stream. Advances as tokens are consumed during parsing.
    /// </summary>
    private int _position = 0;

    /// <summary>
    /// Collection of non-fatal warnings generated during parsing.
    /// Retrieved via <see cref="GetWarnings"/>.
    /// </summary>
    private readonly List<CompileWarning> _warnings = [];

    /// <summary>
    /// Collection of parse errors encountered during parsing.
    /// Errors are accumulated during error recovery.
    /// </summary>
    private readonly List<string> _errors = [];

    /// <summary>
    /// Returns true if any parse errors occurred during parsing.
    /// </summary>
    public bool HasErrors => _errors.Count > 0;

    /// <summary>
    /// Gets all parse errors encountered during parsing.
    /// </summary>
    public IReadOnlyList<string> GetErrors() => _errors;

    #endregion

    #region RazorForge-specific Fields

    /// <summary>
    /// Indicates whether we are currently parsing within a 'when' pattern context.
    /// Used to disambiguate pattern matching syntax from regular expressions.
    /// </summary>
    private bool _inWhenPatternContext;

    /// <summary>
    /// Prevents 'is' expression parsing in when clause bodies.
    /// When true, 'is' is not treated as a pattern-matching operator.
    /// </summary>
    private bool _inWhenClauseBody;

    /// <summary>
    /// Prevents nested inline conditionals (if-then-else expressions).
    /// When true, 'if' at expression level is not parsed as inline conditional.
    /// This improves readability by forbidding constructs like:
    /// <c>if a then (if b then c else d) else e</c>
    /// </summary>
    private bool _parsingInlineConditional;

    /// <summary>
    /// Indicates whether we're currently parsing inside a type body (record, entity, resident).
    /// When true, allows field declarations without var/let keywords.
    /// </summary>
    private bool _parsingTypeBody;

    /// <summary>
    /// Indicates whether we're parsing inside a record body (actual record, not entity/resident).
    /// When true, only private/internal/public modifiers are allowed (not published/imported).
    /// Also var/let/preset keywords are disallowed (use 'field: Type' syntax).
    /// </summary>
    private bool _parsingStrictRecordBody;

    /// <summary>
    /// Indicates whether we're currently parsing inside a routine body.
    /// When true, nested routine declarations are rejected.
    /// </summary>
    private bool _inRoutineBody;

    /// <summary>
    /// Set of known type names for generic disambiguation.
    /// Contains simple names like "List", "DictEntry" that have been declared or imported.
    /// </summary>
    private readonly HashSet<string> _knownTypeNames = [];

    /// <summary>
    /// Set of imported namespace names for qualified type resolution.
    /// Contains namespace names like "Collections", "core" from import statements.
    /// </summary>
    private readonly HashSet<string> _importedNamespaces = [];

    /// <summary>
    /// Stack of generic parameter scopes for type name resolution.
    /// Each scope contains parameter names like "K", "V" that are valid within that context.
    /// Pushed when entering a generic declaration, popped when exiting.
    /// </summary>
    private readonly Stack<HashSet<string>> _genericParameterScopes = new();

    #endregion

    /// <summary>
    /// Checks if an identifier is a known type name (declared, imported, or generic parameter).
    /// Used for generic disambiguation when parsing expressions like <c>x &lt; y</c> vs <c>List&lt;T&gt;</c>.
    /// </summary>
    /// <param name="name">The identifier name to check.</param>
    /// <returns>True if the name is a known type; false otherwise.</returns>
    private bool IsKnownTypeName(string name)
    {
        // Check simple type names (declared locally or individually imported)
        if (_knownTypeNames.Contains(item: name))
        {
            return true;
        }

        // Check generic parameter scopes (e.g., K, V within Dict<K, V>)
        return _genericParameterScopes.Any(scope => scope.Contains(item: name));
    }

    /// <summary>
    /// Parses the token stream and produces a complete program AST.
    /// This is the main entry point for parsing a RazorForge source file.
    /// </summary>
    /// <returns>A <see cref="Compilers.Shared.AST.Program"/> containing all top-level declarations.</returns>
    /// <exception cref="ParseException">Thrown for unrecoverable parse errors. Recoverable errors are logged and parsing continues.</exception>
    public Compilers.Shared.AST.Program Parse()
    {
        var declarations = new List<IAstNode>();

        while (!IsAtEnd)
        {
            try
            {
                // Skip newlines at top level
                if (Match(type: TokenType.Newline))
                {
                    continue;
                }

                IAstNode decl = ParseDeclaration();
                declarations.Add(item: decl);
            }
            catch (ParseException ex)
            {
                Token errorToken = _position < tokens.Count
                    ? tokens[index: _position]
                    : tokens[^1];
                string location = !string.IsNullOrEmpty(value: fileName)
                    ? $"[{fileName}:{errorToken.Line}:{errorToken.Column}]"
                    : $"[{errorToken.Line}:{errorToken.Column}]";
                string errorMessage = $"Parse error{location}: {ex.Message}";
                _errors.Add(item: errorMessage);
                Console.Error.WriteLine(value: errorMessage);
                Synchronize();
            }
        }

        return new Compilers.Shared.AST.Program(Declarations: declarations, Location: GetLocation());
    }

    /// <summary>
    /// Parses a single top-level or nested declaration.
    /// Handles all declaration types: namespace, import, define, using, preset,
    /// routines, entities, records, residents, choices, variants, mutants, and protocols.
    /// </summary>
    /// <returns>The parsed declaration node.</returns>
    private IAstNode ParseDeclaration()
    {
        // Namespace declaration (must appear at top of file)
        if (Match(type: TokenType.Namespace))
        {
            return ParseNamespaceDeclaration();
        }

        // Import declaration
        if (Match(type: TokenType.Import))
        {
            return ParseImportDeclaration();
        }

        // Redefinition
        if (Match(type: TokenType.Define))
        {
            return ParseDefineDeclaration();
        }

        // Using declaration
        if (Match(type: TokenType.Using))
        {
            return ParseUsingDeclaration();
        }

        // Preset (compile-time constant)
        if (Match(type: TokenType.Preset))
        {
            return ParsePresetDeclaration();
        }

        // Parse attributes (e.g., @crash_only, @inline, @config)
        List<string> attributes = ParseAttributes();

        // Parse visibility and storage class modifiers
        var (visibility, storage) = ParseModifiers();

        // Imported declaration with optional calling convention
        // Supports: imported routine foo() or imported("C") routine foo()
        if (visibility == VisibilityModifier.Imported)
        {
            string? callingConvention = null;

            // Check for calling convention: imported("C")
            if (Match(type: TokenType.LeftParen))
            {
                if (Check(TokenType.TextLiteral, TokenType.BytesLiteral))
                {
                    Token conventionToken = Advance();
                    // Remove quotes from the text literal
                    callingConvention = conventionToken.Text.Trim(trimChar: '"');
                }

                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after calling convention");
            }

            if (Match(type: TokenType.Routine))
            {
                return ParseImportedDeclaration(callingConvention: callingConvention);
            }
        }

        // Variable declarations
        if (Match(TokenType.Var, TokenType.Let, TokenType.Preset))
        {
            // In strict record bodies, var/let/preset are not allowed
            if (_parsingStrictRecordBody)
            {
                throw new ParseException(
                    message: "Record fields cannot use 'var', 'let', or 'preset'. " +
                             "Records are immutable with all-public fields. Use 'field: Type' syntax instead.");
            }
            return ParseVariableDeclaration(visibility: visibility, storage: storage);
        }

        // Pass statement/declaration (empty placeholder)
        // Inside type bodies, returns PassDeclaration (a Declaration subtype)
        // Outside type bodies, returns PassStatement (a Statement subtype)
        if (Match(type: TokenType.Pass))
        {
            ConsumeStatementTerminator();

            // Inside type bodies, return a PassDeclaration (extends Declaration)
            if (_parsingTypeBody)
            {
                return new PassDeclaration(Location: GetLocation());
            }

            return new PassStatement(Location: GetLocation());
        }

        // Field declaration in type bodies: name: Type
        // Detected by identifier followed by colon (no var/let keyword needed)
        // Only allowed inside type bodies (record, entity, resident)
        if (_parsingTypeBody && Check(type: TokenType.Identifier) && PeekToken(offset: 1)
               .Type == TokenType.Colon)
        {
            // In record bodies, only private/internal/public are allowed (not published/imported)
            if (_parsingStrictRecordBody && visibility is VisibilityModifier.Published or VisibilityModifier.Imported)
            {
                throw new ParseException(
                    message: $"'{visibility.ToString().ToLower()}' is not valid for record fields. " +
                             "Record fields can use 'private', 'internal', or 'public'.");
            }
            return ParseFieldDeclaration(visibility: visibility);
        }

        // Routine declaration (access modifiers: private, family, internal, public)
        if (Match(type: TokenType.Routine))
        {
            // Validate: global storage is not allowed for routines
            if (storage == StorageClass.Global)
            {
                throw new ParseException(
                    message: "'global' storage class is not valid for routines. " +
                             "'global' can only be used for file-scope static variables.");
            }
            return ParseRoutineDeclaration(visibility: visibility, attributes: attributes, storage: storage);
        }

        // Entity declarations (heap-allocated reference types)
        if (Match(type: TokenType.Entity))
        {
            return ParseEntityDeclaration(visibility: visibility);
        }

        // Record declarations (stack-allocated value types)
        if (Match(type: TokenType.Record))
        {
            return ParseRecordDeclaration(visibility: visibility);
        }

        // Resident declarations (singleton static types)
        if (Match(type: TokenType.Resident))
        {
            return ParseResidentDeclaration(visibility: visibility);
        }

        // Choice declarations (simple enumerations with integer values)
        if (Match(type: TokenType.Choice))
        {
            return ParseChoiceDeclaration(visibility: visibility);
        }

        // Variant declarations (tagged unions/sum types)
        if (Match(type: TokenType.Variant))
        {
            return ParseVariantDeclaration();
        }

        // Mutant declarations (untagged unions)
        if (Match(type: TokenType.Mutant))
        {
            return ParseMutantDeclaration();
        }

        // Protocol declarations (interface/trait definitions)
        if (Match(type: TokenType.Protocol))
        {
            return ParseProtocolDeclaration(visibility: visibility);
        }

        // If we parsed a visibility modifier but no declaration follows, it's an error
        if (visibility != VisibilityModifier.Public)
        {
            throw new ParseException(message: $"Visibility modifier '{visibility}' must be followed by a declaration "
                                              + $"(routine, entity, record, resident, choice, variant, mutant, protocol, preset,"
                                              + $" var, or let)");
        }

        // Otherwise parse as statement
        return ParseStatement();
    }

    /// <summary>
    /// Parses a single statement within a block or function body.
    /// Handles control flow (if, unless, while, loop, for, when), jumps (return, break, continue),
    /// special statements (pass, throw, absent), memory blocks (danger, viewing, hijacking, inspecting, seizing),
    /// block statements, and expression statements.
    /// </summary>
    /// <returns>The parsed statement, or null if no valid statement was found.</returns>
    private Statement ParseStatement()
    {
        // Control flow
        if (Match(type: TokenType.If))
        {
            return ParseIfStatement();
        }

        if (Match(type: TokenType.Unless))
        {
            return ParseUnlessStatement();
        }

        if (Match(type: TokenType.While))
        {
            return ParseWhileStatement();
        }

        if (Match(type: TokenType.Loop))
        {
            return ParseLoopStatement();
        }

        if (Match(type: TokenType.For))
        {
            return ParseForStatement();
        }

        if (Match(type: TokenType.When))
        {
            return ParseWhenStatement();
        }

        if (Match(type: TokenType.Return))
        {
            return ParseReturnStatement();
        }

        if (Match(type: TokenType.Becomes))
        {
            return ParseBecomesStatement();
        }

        if (Match(type: TokenType.Break))
        {
            return ParseBreakStatement();
        }

        if (Match(type: TokenType.Continue))
        {
            return ParseContinueStatement();
        }

        if (Match(type: TokenType.Pass))
        {
            return ParsePassStatement();
        }

        if (Match(type: TokenType.Throw))
        {
            return ParseThrowStatement();
        }

        if (Match(type: TokenType.Absent))
        {
            return ParseAbsentStatement();
        }

        // Danger block
        if (Match(type: TokenType.Danger))
        {
            return ParseDangerStatement();
        }

        // Viewing block (scoped read-only access)
        if (Match(type: TokenType.Viewing))
        {
            return ParseViewingStatement();
        }

        // Hijacking block (scoped exclusive access)
        if (Match(type: TokenType.Hijacking))
        {
            return ParseHijackingStatement();
        }

        // Inspecting block (thread-safe scoped read access)
        if (Match(type: TokenType.Inspecting))
        {
            return ParseInspectingStatement();
        }

        // Seizing block (thread-safe scoped exclusive access)
        if (Match(type: TokenType.Seizing))
        {
            return ParseSeizingStatement();
        }

        // Block statement
        if (Check(type: TokenType.LeftBrace))
        {
            return ParseBlockStatement();
        }

        // Expression statement
        return ParseExpressionStatement();
    }
}
