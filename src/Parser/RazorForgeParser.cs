using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;
using RazorForge.Diagnostics;

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
    /// Contains simple names like "List", "Dict" that have been declared or imported.
    /// </summary>
    private readonly HashSet<string> _knownTypeNames = [];

    /// <summary>
    /// Set of imported module names for qualified type resolution.
    /// Contains module names like "Collections", "core" from import statements.
    /// </summary>
    private readonly HashSet<string> _importedModules = [];

    /// <summary>
    /// Stack of generic parameter scopes for type name resolution.
    /// Each scope contains parameter names like "K", "V" that are valid within that context.
    /// Pushed when entering a generic declaration, popped when exiting.
    /// </summary>
    private readonly Stack<HashSet<string>> _genericParameterScopes = new();

    private readonly string _fileName = fileName ?? "unknown";

    #endregion

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
            catch (RazorForgeGrammarException ex)
            {
                // RazorForgeGrammarException.Message already contains formatted error:
                // error[RF-G150]: filename.rf:9:14: message
                _errors.Add(item: ex.Message);
                Console.Error.WriteLine(value: ex.Message);
                Synchronize();
            }
        }

        return new Compilers.Shared.AST.Program(Declarations: declarations, Location: GetLocation());
    }

    /// <summary>
    /// Parses a single top-level or nested declaration.
    /// Handles all declaration types: module, import, define, using, preset,
    /// routines, entities, records, residents, choices, variants, mutants, and protocols.
    /// </summary>
    /// <remarks>
    /// Declaration parsing order (checked in sequence):
    ///
    /// FILE-LEVEL DECLARATIONS (must appear first):
    ///   module       - Module declaration
    ///   import       - Import external modules
    ///   define       - Type alias/redefinition
    ///   preset       - Compile-time constant
    ///
    /// MODIFIERS (optional, parsed before declaration):
    ///   attributes   - @crash_only, @inline, @config, etc.
    ///   visibility   - private, family, internal, public, published, imported
    ///   storage      - global (for file-scope variables)
    ///
    /// TYPE/VALUE DECLARATIONS:
    ///   imported     - FFI routine declaration (with optional calling convention)
    ///   var/let      - Variable declarations
    ///   pass         - Empty placeholder
    ///   field: Type  - Field declaration (inside type bodies)
    ///   routine      - Function declaration
    ///   entity       - Heap-allocated reference type
    ///   record       - Stack-allocated value type
    ///   resident     - Singleton static type
    ///   choice       - Simple enumeration
    ///   variant      - Tagged union (sum type)
    ///   protocol     - Interface/trait definition
    ///
    /// If no declaration keyword matches, falls through to ParseStatement.
    /// </remarks>
    /// <returns>The parsed declaration node.</returns>
    private IAstNode ParseDeclaration()
    {
        // ═══════════════════════════════════════════════════════════════════════════
        // SKIP DOC COMMENTS (### comment lines before declarations)
        // ═══════════════════════════════════════════════════════════════════════════
        // Doc comments are preserved in the token stream but currently not attached
        // to declarations. Skip them to prevent "Unexpected token" errors.
        while (Match(type: TokenType.DocComment))
        {
            // Skip any newlines after doc comments
            while (Match(type: TokenType.Newline)) { }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // FILE-LEVEL DECLARATIONS (must appear at top of file)
        // ═══════════════════════════════════════════════════════════════════════════

        // Module declaration (must appear at top of file)
        if (Match(type: TokenType.Module))
        {
            return ParseModuleDeclaration();
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

        // Preset (compile-time constant)
        if (Match(type: TokenType.Preset))
        {
            return ParsePresetDeclaration();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PARSE MODIFIERS (attributes, visibility, storage class)
        // ═══════════════════════════════════════════════════════════════════════════

        // Parse attributes (e.g., @crash_only, @inline, @config)
        List<string> attributes = ParseAttributes();

        // Parse visibility and storage class modifiers
        var (visibility, storage) = ParseModifiers();

        // Define declaration with attributes (e.g., @config(target: "windows") define CLong as S32)
        if (Match(type: TokenType.Define))
        {
            // TODO: Pass attributes to DefineDeclaration when supported
            return ParseDefineDeclaration();
        }

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
            // In type bodies (record, entity, resident), var/let/preset are not allowed
            // Fields use 'name: Type' syntax without var/let keywords
            if (_parsingTypeBody)
            {
                throw new RazorForgeGrammarException(
                    RazorForgeDiagnosticCode.InvalidDeclarationInBody,
                    "Type fields cannot use 'var', 'let', or 'preset'. " +
                    "Use 'name: Type' syntax instead.",
                    _fileName, CurrentToken.Line, CurrentToken.Column);
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
            // In record bodies, imported is not allowed
            if (_parsingStrictRecordBody && visibility is VisibilityModifier.Imported)
            {
                throw new RazorForgeGrammarException(
                    RazorForgeDiagnosticCode.InvalidDeclarationInBody,
                    $"'{visibility.ToString().ToLower()}' is not valid for record fields. " +
                    "Record fields can use 'private', 'internal', 'published', or 'public'.",
                    _fileName, CurrentToken.Line, CurrentToken.Column);
            }
            return ParseFieldDeclaration(visibility: visibility);
        }

        // Routine declaration (access modifiers: private, family, internal, public)
        // Supports: threaded routine foo() for OS-level threading
        AsyncStatus asyncStatus = AsyncStatus.None;
        if (Match(type: TokenType.Threaded))
        {
            asyncStatus = AsyncStatus.Threaded;
        }

        if (Match(type: TokenType.Routine))
        {
            // Validate: global storage is not allowed for routines
            if (storage == StorageClass.Global)
            {
                throw new RazorForgeGrammarException(
                    RazorForgeDiagnosticCode.InvalidDeclarationInBody,
                    "'global' storage class is not valid for routines. " +
                    "'global' can only be used for file-scope static variables.",
                    _fileName, CurrentToken.Line, CurrentToken.Column);
            }
            return ParseRoutineDeclaration(visibility: visibility, attributes: attributes, storage: storage, asyncStatus: asyncStatus);
        }

        // If we consumed 'threaded' but no 'routine' follows, it's an error
        if (asyncStatus != AsyncStatus.None)
        {
            throw new RazorForgeGrammarException(
                RazorForgeDiagnosticCode.UnexpectedToken,
                "'threaded' must be followed by 'routine'",
                _fileName, CurrentToken.Line, CurrentToken.Column);
        }

        // Validate: storage class modifiers are not valid for type declarations
        if (storage != StorageClass.None && Check(TokenType.Entity, TokenType.Record,
                TokenType.Resident, TokenType.Choice, TokenType.Variant, TokenType.Protocol))
        {
            throw new RazorForgeGrammarException(
                RazorForgeDiagnosticCode.InvalidDeclarationInBody,
                $"'{storage.ToString().ToLower()}' storage class is not valid for type declarations.",
                _fileName, CurrentToken.Line, CurrentToken.Column);
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

        // Protocol declarations (interface/trait definitions)
        if (Match(type: TokenType.Protocol))
        {
            return ParseProtocolDeclaration(visibility: visibility);
        }

        // If we parsed a visibility modifier but no declaration follows, it's an error
        if (visibility != VisibilityModifier.Public)
        {
            throw ThrowParseError(RazorForgeDiagnosticCode.VisibilityWithoutDeclaration,
                $"Visibility modifier '{visibility}' must be followed by a declaration "
                + $"(routine, entity, record, resident, choice, variant, protocol, preset,"
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
    /// <remarks>
    /// Statement types (checked in sequence):
    ///
    /// CONTROL FLOW:
    ///   if/unless    - Conditional branching
    ///   while/loop   - Loop constructs
    ///   for          - Iteration over ranges/collections
    ///   when         - Pattern matching (switch-like)
    ///
    /// JUMP STATEMENTS:
    ///   return       - Return from routine (with optional value)
    ///   becomes      - argument assign with if-elseif-else
    ///   break        - Exit loop
    ///   continue     - Skip to next iteration
    ///
    /// SPECIAL STATEMENTS:
    ///   pass         - Empty placeholder (no-op)
    ///   throw        - Throw error (in failable routines)
    ///   absent       - Return none (in failable routines)
    ///
    /// MEMORY BLOCKS (scoped access):
    ///   danger!      - Unsafe block (raw pointers, FFI)
    ///   viewing      - Scoped read-only access (single-thread)
    ///   hijacking    - Scoped exclusive access (single-thread)
    ///   inspecting   - Scoped read access (multi-thread)
    ///   seizing      - Scoped exclusive access (multi-thread)
    ///   using        - resource management
    ///
    /// BLOCK/EXPRESSION:
    ///   { ... }      - Block statement
    ///   expr         - Expression statement (fallback)
    /// </remarks>
    /// <returns>The parsed statement, or null if no valid statement was found.</returns>
    private Statement ParseStatement()
    {
        // ═══════════════════════════════════════════════════════════════════════════
        // CONTROL FLOW STATEMENTS
        // ═══════════════════════════════════════════════════════════════════════════

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

        // ═══════════════════════════════════════════════════════════════════════════
        // JUMP STATEMENTS
        // ═══════════════════════════════════════════════════════════════════════════

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

        // ═══════════════════════════════════════════════════════════════════════════
        // SPECIAL STATEMENTS
        // ═══════════════════════════════════════════════════════════════════════════

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

        if (Match(type: TokenType.Discard))
        {
            return ParseDiscardStatement();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // MEMORY/SCOPE BLOCKS
        // ═══════════════════════════════════════════════════════════════════════════

        // Danger block (unsafe operations)
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

        // Using declaration
        if (Match(type: TokenType.Using))
        {
            return ParseUsingStatement();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // BLOCK AND EXPRESSION STATEMENTS
        // ═══════════════════════════════════════════════════════════════════════════

        // Block statement
        if (Check(type: TokenType.LeftBrace))
        {
            return ParseBlockStatement();
        }

        // Expression statement (fallback)
        return ParseExpressionStatement();
    }
}
