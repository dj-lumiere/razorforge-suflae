using SyntaxTree;
using Compiler.Lexer;
using SemanticAnalysis.Enums;
using Compiler.Diagnostics;

namespace Compiler.Parser;

/// <summary>
/// Unified parser for both RazorForge and Suflae languages.
/// Converts a stream of tokens into an Abstract Syntax Tree (AST).
/// Language-specific constructs are guarded by <see cref="_language"/> checks.
/// </summary>
public partial class Parser
{
    #region Base Parser Fields

    /// <summary>
    /// The list of tokens to parse.
    /// </summary>
    private readonly List<Token> Tokens;

    /// <summary>
    /// Current position in the token stream.
    /// </summary>
    private int Position = 0;

    /// <summary>
    /// Collection of warnings generated during parsing.
    /// </summary>
    private readonly List<BuildWarning> Warnings = [];

    /// <summary>
    /// Collection of errors accumulated during error recovery.
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

    /// <summary>
    /// The source file name for error reporting.
    /// </summary>
    public string fileName = "";

    /// <summary>
    /// The language being parsed (RazorForge or Suflae).
    /// Used to guard language-specific constructs.
    /// </summary>
    private readonly Language _language;

    #endregion

    #region Indentation Fields

    /// <summary>
    /// Stack tracking indentation levels for block detection.
    /// </summary>
    private readonly Stack<int> _indentationStack = new();

    /// <summary>
    /// Current indentation level being parsed.
    /// </summary>
    private int _currentIndentationLevel = 0;

    #endregion

    #region Shared Parser State

    /// <summary>
    /// Prevents nested inline conditionals (if-then-else expressions).
    /// When true, 'if' at expression level is not parsed as inline conditional.
    /// This improves readability by forbidding constructs like:
    /// <c>if a then (if b then c else d) else e</c>
    /// </summary>
    private bool _parsingInlineConditional;

    /// <summary>
    /// Indicates whether we're currently parsing inside a type body (record, entity, resident).
    /// When true, allows member variable declarations without var keywords.
    /// </summary>
    private bool _parsingTypeBody = false;

    /// <summary>
    /// Indicates whether we're parsing inside a record body (actual record, not entity/resident).
    /// When true, only secret/posted/open modifiers are allowed (not external).
    /// Also var/preset keywords are disallowed (use 'name: Type' syntax).
    /// </summary>
    private bool _parsingStrictRecordBody = false;

    /// <summary>
    /// Indicates whether we're currently parsing inside a routine body.
    /// When true, nested routine declarations are rejected.
    /// </summary>
    private bool _inRoutineBody = false;

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

    #endregion

    /// <summary>
    /// Creates a new unified parser for the given token stream and language.
    /// </summary>
    /// <param name="tokens">The tokens to parse.</param>
    /// <param name="language">The language being parsed (RazorForge or Suflae).</param>
    /// <param name="fileName">Optional source file name for error reporting.</param>
    public Parser(List<Token> tokens, Language language, string? fileName = null)
    {
        Tokens = tokens;
        _language = language;
        this.fileName = fileName ?? "unknown";
        _indentationStack.Push(item: 0); // Base indentation level
    }

    /// <summary>
    /// Parses the token stream into a complete program AST.
    /// Main entry point for parsing source files.
    /// </summary>
    /// <returns>A <see cref="SyntaxTree.Program"/> containing all top-level declarations.</returns>
    public Program Parse()
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

                // Handle dedent tokens (should not occur at top level, but be safe)
                if (Check(type: TokenType.Dedent))
                {
                    ProcessDedentTokens();
                    continue;
                }

                IAstNode decl = ParseDeclaration();
                declarations.Add(item: decl);
            }
            catch (GrammarException ex)
            {
                // GrammarException.Message already contains formatted error:
                // error[RF-G150]: filename.rf:9:14: message
                // error[SF-G150]: filename.sf:9:14: message
                _errors.Add(item: ex.Message);
                Console.Error.WriteLine(value: ex.Message);
                Synchronize();
            }
        }

        return new Program(Declarations: declarations, Location: GetLocation());
    }

    /// <summary>
    /// Parses a single top-level or nested declaration.
    /// Handles: module, import, define, using, var, routine, entity, record, choice, variant, protocol, impl.
    /// RazorForge-only: resident, external, dangerous modifier, threaded async status.
    /// </summary>
    /// <remarks>
    /// Declaration parsing order (checked in sequence):
    ///
    /// FILE-LEVEL DECLARATIONS (must appear first):
    ///   module       - Module declaration
    ///   import       - Import external modules
    ///   define       - Type alias/redefinition
    ///   preset       - Build-time constant
    ///
    /// MODIFIERS (optional, parsed before declaration):
    ///   attributes   - @crash_only, @inline, @intrinsic, etc.
    ///   visibility   - secret, posted, open, external
    ///   storage      - common, global
    ///
    /// RF-ONLY MODIFIERS:
    ///   dangerous    - Marks routine as unsafe (RazorForge only)
    ///
    /// TYPE/VALUE DECLARATIONS:
    ///   external     - FFI routine declaration (RazorForge only)
    ///   name: Type  - Member variable declaration (inside type bodies)
    ///   var          - Variable declarations
    ///   pass         - Empty placeholder (RazorForge only)
    ///   routine      - Function declaration
    ///   entity       - Heap-allocated reference type
    ///   record       - Stack-allocated value type
    ///   resident     - Singleton static type (RazorForge only)
    ///   choice       - Simple enumeration
    ///   variant      - Tagged union (sum type)
    ///   protocol     - Interface/trait definition
    ///
    /// SPECIAL DECLARATION:
    ///   using        - Resource management (declaration form, no body block)
    ///
    /// If no declaration keyword matches, falls through to ParseStatement.
    /// </remarks>
    /// <returns>The parsed declaration node.</returns>
    /// <exception cref="GrammarException">Thrown when no valid declaration or statement can be parsed.</exception>
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

        // Preset (build-time constant)
        if (Match(type: TokenType.Preset))
        {
            return ParsePresetDeclaration();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PARSE MODIFIERS (attributes, visibility, storage class)
        // ═══════════════════════════════════════════════════════════════════════════

        // Parse attributes (e.g., @inline, @crash_only, @intrinsic("name"))
        List<string> attributes = ParseAnnotations();

        // Skip newlines between attributes and the declaration they modify
        // e.g., @readonly\nroutine foo() should work
        if (attributes.Count > 0)
        {
            while (Match(type: TokenType.Newline))
            {
                // Skip newlines
            }
        }

        // Parse visibility and storage class modifiers
        var (visibility, storage) = ParseModifiers();

        // Define declaration with attributes (e.g., @config(target: "windows") define CLong as S32)
        if (Match(type: TokenType.Define))
        {
            // TODO: Pass attributes to DefineDeclaration when supported
            return ParseDefineDeclaration();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // RF-ONLY: DANGEROUS MODIFIER
        // ═══════════════════════════════════════════════════════════════════════════

        // Check for dangerous modifier: dangerous routine foo(), dangerous external("C") routine bar()
        // (RazorForge only)
        bool isDangerous = false;
        if (_language == Language.RazorForge)
        {
            isDangerous = Match(type: TokenType.Dangerous);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // RF-ONLY: EXTERNAL DECLARATIONS
        // ═══════════════════════════════════════════════════════════════════════════

        // External declaration with optional calling convention (RazorForge only)
        // Supports: external routine foo() or external("C") routine foo()
        //           external("C") { routine ... routine ... } (block form)
        if (_language == Language.RazorForge && visibility == VisibilityModifier.External)
        {
            string? callingConvention = null;

            // Check for calling convention: external("C")
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

            // Block form: external("C")\n  routine ... routine ...
            // Check for block form: next meaningful token is Newline (not 'routine')
            if (Check(type: TokenType.Newline))
            {
                return ParseExternalBlockDeclaration(callingConvention: callingConvention, isDangerous: isDangerous);
            }

            // Single form: external("C") routine foo()
            if (Match(type: TokenType.Routine))
            {
                return ParseExternalDeclaration(
                    callingConvention: callingConvention, attributes: attributes, isDangerous: isDangerous);
            }
        }

        // Field declaration in type bodies: name: Type
        // Detected by identifier followed by colon (no var keyword needed)
        // Only allowed inside type bodies (record, entity, resident)
        if (_parsingTypeBody && Check(type: TokenType.Identifier) && PeekToken(offset: 1)
               .Type == TokenType.Colon)
        {
            // In record bodies, external is not allowed
            if (_parsingStrictRecordBody && visibility is VisibilityModifier.External)
            {
                throw new GrammarException(
                    GrammarDiagnosticCode.InvalidDeclarationInBody,
                    $"'{visibility.ToString().ToLower()}' is not valid for record member variables. " +
                    "Record member variables can use 'secret', 'posted', or 'open'",
                    fileName, CurrentToken.Line, CurrentToken.Column, _language);
            }
            return ParseMemberVariableDeclaration(visibility: visibility);
        }

        // Variable declarations
        if (Match(TokenType.Var, TokenType.Preset))
        {
            // In type bodies (record, entity, resident), var/preset are not allowed
            // MemberVariables use 'name: Type' syntax without var keywords
            if (_parsingTypeBody)
            {
                throw new GrammarException(
                    GrammarDiagnosticCode.InvalidDeclarationInBody,
                    "Type member variables cannot use 'var' or 'preset'. " +
                    "Use 'name: Type' syntax instead",
                    fileName, CurrentToken.Line, CurrentToken.Column, _language);
            }
            return ParseVariableDeclaration(visibility: visibility, storage: storage);
        }

        // Pass statement/declaration (empty placeholder)
        // Inside type bodies, returns PassDeclaration (a Declaration subtype)
        // Outside type bodies, returns PassStatement (a Statement subtype)
        if (_language == Language.RazorForge && Match(type: TokenType.Pass))
        {
            ConsumeStatementTerminator();

            // Inside type bodies, return a PassDeclaration (extends Declaration)
            if (_parsingTypeBody)
            {
                return new PassDeclaration(Location: GetLocation());
            }

            return new PassStatement(Location: GetLocation());
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // ROUTINE DECLARATION (with async status modifiers)
        // ═══════════════════════════════════════════════════════════════════════════

        // Check for suspended modifier before routine
        AsyncStatus asyncStatus = AsyncStatus.None;
        if (Match(type: TokenType.Suspended))
        {
            asyncStatus = AsyncStatus.Suspended;
        }
        // RF-only: threaded async status
        else if (_language == Language.RazorForge && Match(type: TokenType.Threaded))
        {
            asyncStatus = AsyncStatus.Threaded;
        }

        // Routine (function) declaration
        if (Match(type: TokenType.Routine))
        {
            // Validate: global storage is not allowed for routines
            if (storage == StorageClass.Global)
            {
                throw new GrammarException(
                    GrammarDiagnosticCode.InvalidDeclarationInBody,
                    "'global' storage class is not valid for routines. " +
                    "'global' can only be used for file-scope static variables",
                    fileName, CurrentToken.Line, CurrentToken.Column, _language);
            }
            return ParseRoutineDeclaration(visibility: visibility, attributes: attributes, storage: storage, asyncStatus: asyncStatus, isDangerous: isDangerous);
        }

        // If we consumed 'suspended'/'threaded' but no 'routine' follows, that's an error
        if (asyncStatus != AsyncStatus.None)
        {
            string modifier = asyncStatus == AsyncStatus.Suspended ? "suspended" : "threaded";
            throw new GrammarException(
                GrammarDiagnosticCode.UnexpectedToken,
                $"'{modifier}' must be followed by 'routine'",
                fileName, CurrentToken.Line, CurrentToken.Column, _language);
        }

        // Validate: storage class modifiers are not valid for type declarations
        if (storage != StorageClass.None)
        {
            // Check all type keywords (including RF-only Resident)
            bool isTypeKeyword = Check(TokenType.Entity, TokenType.Record,
                TokenType.Choice, TokenType.Flags, TokenType.Variant, TokenType.Protocol);

            if (_language == Language.RazorForge)
            {
                isTypeKeyword = isTypeKeyword || Check(type: TokenType.Resident);
            }

            if (isTypeKeyword)
            {
                throw new GrammarException(
                    GrammarDiagnosticCode.InvalidDeclarationInBody,
                    $"'{storage.ToString().ToLower()}' storage class is not valid for type declarations",
                    fileName, CurrentToken.Line, CurrentToken.Column, _language);
            }
        }

        // Entity/Record/Choice declarations
        if (Match(type: TokenType.Entity))
        {
            return ParseEntityDeclaration(visibility: visibility);
        }

        if (Match(type: TokenType.Record))
        {
            return ParseRecordDeclaration(visibility: visibility);
        }

        // RF-only: Resident declarations (singleton static types)
        if (_language == Language.RazorForge && Match(type: TokenType.Resident))
        {
            return ParseResidentDeclaration(visibility: visibility);
        }

        if (Match(type: TokenType.Choice))
        {
            return ParseChoiceDeclaration(visibility: visibility);
        }

        if (Match(type: TokenType.Flags))
        {
            return ParseFlagsDeclaration(visibility: visibility);
        }

        if (Match(type: TokenType.Variant))
        {
            return ParseVariantDeclaration();
        }

        if (Match(type: TokenType.Protocol))
        {
            return ParseProtocolDeclaration(visibility: visibility);
        }

        // If we parsed a visibility modifier but no declaration follows, it's an error (unless
        // it is an record or protocol)
        if (visibility != VisibilityModifier.Open)
        {
            string validDeclarations = _language == Language.RazorForge
                ? "routine, entity, record, resident, choice, variant, protocol, preset, or var"
                : "routine, entity, record, choice, variant, protocol, preset, or var";
            throw ThrowParseError(GrammarDiagnosticCode.VisibilityWithoutDeclaration,
                $"Visibility modifier '{visibility}' must be followed by a declaration " +
                $"({validDeclarations})");
        }

        // If we have attributes but no declaration, that's an error
        if (attributes.Count > 0)
        {
            throw ThrowParseError(GrammarDiagnosticCode.AnnotationsWithoutDeclaration,
                "Annotations must be followed by a declaration (routine, entity, record, etc.)");
        }

        // Otherwise parse as statement
        return ParseStatement();
    }

    /// <summary>
    /// Parses a single statement within a block or function body.
    /// Handles: if, while, for, when, return, throw, absent, break, continue, and expression statements.
    /// RazorForge-only: danger! block, steal expression, release statement, block statements.
    /// </summary>
    /// <remarks>
    /// Statement types (checked in sequence):
    ///
    /// INDENTATION HANDLING:
    ///   dedent       - Process block end
    ///   newlines     - Skip empty lines
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
    ///   throw        - Throw error (in failable routines)
    ///   absent       - Return none (in failable routines)
    ///   pass         - Empty placeholder (no-op)
    ///   using        - Resource management (declaration form)
    ///
    /// MEMORY BLOCKS (RazorForge only):
    ///   danger!      - Unsafe block (raw pointers, FFI)
    ///   release      - Early resource cleanup
    ///
    /// DECLARATIONS IN STATEMENT CONTEXT:
    ///   var          - Variable declarations (including destructuring)
    ///
    /// BLOCK/EXPRESSION:
    ///   { ... }      - Block statement (RazorForge only)
    ///   expr         - Expression statement (fallback)
    /// </remarks>
    /// <returns>The parsed statement, or null if at end of block.</returns>
    private Statement ParseStatement()
    {
        // ═══════════════════════════════════════════════════════════════════════════
        // INDENTATION HANDLING
        // ═══════════════════════════════════════════════════════════════════════════

        // Handle dedent tokens
        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }

        // Skip newlines
        while (Match(type: TokenType.Newline)) { }

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

        // Using block (scoped resource management with indented body)
        if (Match(type: TokenType.Using))
        {
            return ParseUsingStatement();
        }

        if (Match(type: TokenType.Absent))
        {
            return ParseAbsentStatement();
        }

        if (Match(type: TokenType.Discard))
        {
            return ParseDiscardStatement();
        }

        if (Match(type: TokenType.Emit))
        {
            return ParseEmitStatement();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // RF-ONLY: MEMORY/SCOPE BLOCKS
        // ═══════════════════════════════════════════════════════════════════════════

        // Danger block (unsafe operations) - RazorForge only
        if (_language == Language.RazorForge && Match(type: TokenType.Danger))
        {
            return ParseDangerStatement();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // DECLARATIONS IN STATEMENT CONTEXT
        // ═══════════════════════════════════════════════════════════════════════════

        // Variable declarations (can appear in statement context)
        if (Match(TokenType.Var, TokenType.Preset))
        {
            // Check if this is destructuring: var (a, b) = expr
            if (Check(type: TokenType.LeftParen))
            {
                return ParseDestructuringDeclaration();
            }

            VariableDeclaration varDecl = ParseVariableDeclaration();
            // Wrap the variable declaration as a declaration statement
            return new DeclarationStatement(Declaration: varDecl, Location: varDecl.Location);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // EXPRESSION STATEMENT (FALLBACK)
        // ═══════════════════════════════════════════════════════════════════════════

        return ParseExpressionStatement();
    }
}
