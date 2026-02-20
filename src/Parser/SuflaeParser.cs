using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;
using RazorForge.Diagnostics;

namespace Compilers.Suflae.Parser;

/// <summary>
/// Parser for Suflae language (indentation-based syntax)
/// Handles indentation-based syntax with blocks
/// </summary>
public partial class SuflaeParser
{
    #region Base Parser Fields

    /// <summary>
    /// The list of tokens to parse.
    /// </summary>
    protected readonly List<Token> Tokens;

    /// <summary>
    /// Current position in the token stream.
    /// </summary>
    protected int Position = 0;

    /// <summary>
    /// Collection of warnings generated during parsing.
    /// </summary>
    protected readonly List<CompileWarning> Warnings = [];

    /// <summary>
    /// Collection of errors accumulated during error recovery.
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

    /// <summary>
    /// The source file name for error reporting.
    /// </summary>
    public string fileName = "";

    #endregion

    #region Suflae-specific Fields

    /// <summary>
    /// Stack tracking indentation levels for block detection.
    /// </summary>
    private readonly Stack<int> _indentationStack = new();

    /// <summary>
    /// Current indentation level being parsed.
    /// </summary>
    private int _currentIndentationLevel = 0;

    /// <summary>
    /// Prevents nested inline conditionals (if-then-else expressions).
    /// When true, 'if' at expression level is not parsed as inline conditional.
    /// This improves readability by forbidding constructs like:
    /// <c>if a then (if b then c else d) else e</c>
    /// </summary>
    private bool _parsingInlineConditional;

    /// <summary>
    /// Indicates whether we're currently parsing inside a type body (record, entity).
    /// When true, allows field declarations without var/let keywords.
    /// </summary>
    private bool _parsingTypeBody = false;

    /// <summary>
    /// Indicates whether we're parsing inside a record body (actual record, not entity).
    /// When true, only private/internal/public modifiers are allowed (not published/imported).
    /// Also var/let/preset keywords are disallowed (use 'field: Type' syntax).
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

    /// <summary>
    /// Set of known type names for generic disambiguation.
    /// Contains simple names like "List", "User" that have been declared or imported.
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
        foreach (HashSet<string> scope in _genericParameterScopes)
        {
            if (scope.Contains(item: name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Creates a new Suflae parser for the given token stream.
    /// </summary>
    /// <param name="tokens">The tokens to parse.</param>
    /// <param name="fileName">Optional source file name for error reporting.</param>
    public SuflaeParser(List<Token> tokens, string? fileName = null)
    {
        Tokens = tokens;
        this.fileName = fileName ?? "unknown";
        _indentationStack.Push(item: 0); // Base indentation level
    }

    /// <summary>
    /// Parses the token stream into a complete program AST.
    /// Main entry point for parsing Suflae source files.
    /// </summary>
    /// <returns>A <see cref="Compilers.Shared.AST.Program"/> containing all top-level declarations.</returns>
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

                // Handle dedent tokens (should not occur at top level, but be safe)
                if (Check(type: TokenType.Dedent))
                {
                    ProcessDedentTokens();
                    continue;
                }

                IAstNode decl = ParseDeclaration();
                declarations.Add(item: decl);
            }
            catch (SuflaeGrammarException ex)
            {
                // SuflaeGrammarException.Message already contains formatted error:
                // error[SF-G150]: filename.sf:9:14: message
                _errors.Add(item: ex.Message);
                Console.Error.WriteLine(value: ex.Message);
                Synchronize();
            }
        }

        return new Compilers.Shared.AST.Program(Declarations: declarations, Location: GetLocation());
    }

    /// <summary>
    /// Parses a single top-level or nested declaration.
    /// Handles: module, import, define, using, var/let, routine, entity, record, choice, variant, protocol, impl.
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
    ///   attributes   - @crash_only, @inline, @intrinsic, etc.
    ///   visibility   - private, internal, public, published, imported
    ///   storage      - common, global
    ///
    /// TYPE/VALUE DECLARATIONS:
    ///   field: Type  - Field declaration (inside type bodies)
    ///   var/let      - Variable declarations
    ///   routine      - Function declaration
    ///   entity       - Heap-allocated reference type
    ///   record       - Stack-allocated value type
    ///   choice       - Simple enumeration
    ///   variant      - Tagged union (sum type)
    ///   protocol     - Interface/trait definition
    ///
    /// SPECIAL DECLARATION:
    ///   using        - resource management
    ///
    /// If no declaration keyword matches, falls through to ParseStatement.
    /// </remarks>
    /// <returns>The parsed declaration node.</returns>
    /// <exception cref="ParseException">Thrown when no valid declaration or statement can be parsed.</exception>
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

        // Parse attributes (e.g., @inline, @crash_only, @intrinsic("name"))
        List<string> attributes = ParseAttributes();

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

        // Field declaration in type bodies: name: Type
        // Detected by identifier followed by colon (no var/let keyword needed)
        // Only allowed inside type bodies (record, entity)
        if (_parsingTypeBody && Check(type: TokenType.Identifier) && PeekToken(offset: 1)
               .Type == TokenType.Colon)
        {
            // In record bodies, imported is not allowed
            if (_parsingStrictRecordBody && visibility is VisibilityModifier.Imported)
            {
                throw new SuflaeGrammarException(
                    SuflaeDiagnosticCode.InvalidDeclarationInBody,
                    $"'{visibility.ToString().ToLower()}' is not valid for record fields. " +
                    "Record fields can use 'private', 'internal', 'published', or 'public'",
                    fileName, CurrentToken.Line, CurrentToken.Column);
            }
            return ParseFieldDeclaration(visibility: visibility);
        }

        // Variable declarations
        if (Match(TokenType.Var, TokenType.Let, TokenType.Preset))
        {
            // In type bodies (record, entity, resident), var/let/preset are not allowed
            // Fields use 'name: Type' syntax without var/let keywords
            if (_parsingTypeBody)
            {
                throw new SuflaeGrammarException(
                    SuflaeDiagnosticCode.InvalidDeclarationInBody,
                    "Type fields cannot use 'var', 'let', or 'preset'. " +
                    "Use 'name: Type' syntax instead",
                    fileName, CurrentToken.Line, CurrentToken.Column);
            }
            return ParseVariableDeclaration(visibility: visibility, storage: storage);
        }

        // Check for suspended modifier before routine
        AsyncStatus asyncStatus = AsyncStatus.None;
        if (Match(type: TokenType.Suspended))
        {
            asyncStatus = AsyncStatus.Suspended;
        }

        // Routine (function) declaration - using 'routine' keyword in Suflae
        if (Match(type: TokenType.Routine))
        {
            // Validate: global storage is not allowed for routines
            if (storage == StorageClass.Global)
            {
                throw new SuflaeGrammarException(
                    SuflaeDiagnosticCode.InvalidDeclarationInBody,
                    "'global' storage class is not valid for routines. " +
                    "'global' can only be used for file-scope static variables",
                    fileName, CurrentToken.Line, CurrentToken.Column);
            }
            return ParseRoutineDeclaration(visibility: visibility, attributes: attributes, storage: storage, asyncStatus: asyncStatus);
        }

        // If we consumed 'suspended' but no 'routine' follows, that's an error
        if (asyncStatus != AsyncStatus.None)
        {
            throw new SuflaeGrammarException(
                SuflaeDiagnosticCode.InvalidDeclarationInBody,
                "'suspended' must be followed by 'routine'",
                fileName, CurrentToken.Line, CurrentToken.Column);
        }

        // Validate: storage class modifiers are not valid for type declarations
        if (storage != StorageClass.None && Check(TokenType.Entity, TokenType.Record,
                TokenType.Choice, TokenType.Flags, TokenType.Variant, TokenType.Protocol))
        {
            throw new SuflaeGrammarException(
                SuflaeDiagnosticCode.InvalidDeclarationInBody,
                $"'{storage.ToString().ToLower()}' storage class is not valid for type declarations",
                fileName, CurrentToken.Line, CurrentToken.Column);
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
            throw ThrowParseError($"Visibility modifier '{visibility}' must be followed by a declaration " + $"(routine, entity, record, choice, variant, protocol, var, preset, or let)");
        }

        // If we have attributes but no declaration, that's an error
        if (attributes.Count > 0)
        {
            throw ThrowParseError("Attributes must be followed by a declaration (routine, entity, record, etc.)");
        }

        // Otherwise parse as statement
        return ParseStatement();
    }

    /// <summary>
    /// Parses a single statement within a block or function body.
    /// Handles: if, while, for, when, return, throw, absent, break, continue, and expression statements.
    /// </summary>
    /// <remarks>
    /// Statement types (checked in sequence):
    ///
    /// INDENTATION HANDLING:
    ///   dedent       - Process block end (Suflae uses indentation)
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
    ///
    /// DECLARATIONS IN STATEMENT CONTEXT:
    ///   var/let      - Variable declarations (including destructuring)
    ///
    /// EXPRESSION:
    ///   expr         - Expression statement (fallback)
    /// </remarks>
    /// <returns>The parsed statement, or null if at end of block.</returns>
    private Statement ParseStatement()
    {
        // ═══════════════════════════════════════════════════════════════════════════
        // INDENTATION HANDLING (Suflae-specific)
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

        if (Match(type: TokenType.Throw))
        {
            return ParseThrowStatement();
        }

        // Using statement for resource management: using expr as name:
        if (Match(type: TokenType.Using))
        {
            return ParseUsingStatement();
        }

        if (Match(type: TokenType.Absent))
        {
            return ParseAbsentStatement();
        }

        if (Match(type: TokenType.Pass))
        {
            return ParsePassStatement();
        }

        if (Match(type: TokenType.Discard))
        {
            return ParseDiscardStatement();
        }

        if (Match(type: TokenType.Generate))
        {
            return ParseGenerateStatement();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // DECLARATIONS IN STATEMENT CONTEXT
        // ═══════════════════════════════════════════════════════════════════════════

        // Variable declarations (can appear in statement context)
        if (Match(TokenType.Var, TokenType.Let, TokenType.Preset))
        {
            // Check if this is destructuring: let (a, b) = expr or var (x, y) = expr
            if (Check(type: TokenType.LeftParen))
            {
                return ParseDestructuringDeclaration();
            }

            VariableDeclaration varDecl = ParseVariableDeclaration();
            // Wrap the variable declaration as a declaration statement
            return new DeclarationStatement(Declaration: varDecl, Location: varDecl.Location);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // EXPRESSION STATEMENT (fallback)
        // ═══════════════════════════════════════════════════════════════════════════

        return ParseExpressionStatement();
    }
}
