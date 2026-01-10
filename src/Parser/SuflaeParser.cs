using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;
using RazorForge.Diagnostics;

namespace Compilers.Suflae.Parser;

/// <summary>
/// Parser for Suflae language (indentation-based syntax)
/// Handles Python-like indentation with colons and blocks
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
                Console.Error.WriteLine(value: ex.Message);
                Synchronize();
            }
        }

        return new Compilers.Shared.AST.Program(Declarations: declarations, Location: GetLocation());
    }

    /// <summary>
    /// Parses a single top-level or nested declaration.
    /// Handles: namespace, import, define, using, var/let, routine, entity, record, choice, variant, protocol, impl.
    /// </summary>
    /// <remarks>
    /// Declaration parsing order (checked in sequence):
    ///
    /// FILE-LEVEL DECLARATIONS (must appear first):
    ///   namespace    - Module namespace declaration
    ///   import       - Import external modules
    ///   define       - Type alias/redefinition
    ///   using        - Namespace import
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
    /// If no declaration keyword matches, falls through to ParseStatement.
    /// </remarks>
    /// <returns>The parsed declaration node.</returns>
    /// <exception cref="ParseException">Thrown when no valid declaration or statement can be parsed.</exception>
    private IAstNode ParseDeclaration()
    {
        // ═══════════════════════════════════════════════════════════════════════════
        // FILE-LEVEL DECLARATIONS (must appear at top of file)
        // ═══════════════════════════════════════════════════════════════════════════

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

        // Parse visibility and storage class modifiers
        var (visibility, storage) = ParseModifiers();

        // Field declaration in type bodies: name: Type
        // Detected by identifier followed by colon (no var/let keyword needed)
        // Only allowed inside type bodies (record, entity)
        if (_parsingTypeBody && Check(type: TokenType.Identifier) && PeekToken(offset: 1)
               .Type == TokenType.Colon)
        {
            // In record bodies, only private/internal/public are allowed (not published/imported)
            if (_parsingStrictRecordBody && visibility is VisibilityModifier.Published or VisibilityModifier.Imported)
            {
                throw new SuflaeGrammarException(
                    SuflaeDiagnosticCode.InvalidDeclarationInBody,
                    $"'{visibility.ToString().ToLower()}' is not valid for record fields. " +
                    "Record fields can use 'private', 'internal', or 'public'",
                    fileName, CurrentToken.Line, CurrentToken.Column);
            }
            return ParseFieldDeclaration(visibility: visibility);
        }

        // Variable declarations
        if (Match(TokenType.Var, TokenType.Let, TokenType.Preset))
        {
            // In strict record bodies, var/let/preset are not allowed
            if (_parsingStrictRecordBody)
            {
                throw new SuflaeGrammarException(
                    SuflaeDiagnosticCode.InvalidDeclarationInBody,
                    "Record fields cannot use 'var', 'let', or 'preset'. " +
                    "Records are immutable with all-public fields. Use 'field: Type' syntax instead",
                    fileName, CurrentToken.Line, CurrentToken.Column);
            }
            return ParseVariableDeclaration(visibility: visibility, storage: storage);
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
            return ParseRoutineDeclaration(visibility: visibility, attributes: attributes, storage: storage);
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
        if (visibility != VisibilityModifier.Public)
        {
            throw ThrowParseError($"Visibility modifier '{visibility}' must be followed by a declaration " + $"(routine, entity, record, choice, variant, protocol, var, preset, or let)");
        }

        // If we have attributes but no declaration, that's an error
        if (attributes.Count > 0)
        {
            throw ThrowParseError($"Attributes must be followed by a declaration (routine, entity, record, etc.)");
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
    ///   becomes      - Tail call (return with tail-call optimization)
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
            return new ExpressionStatement(Expression: new IdentifierExpression(Name: $"var {varDecl.Name}", Location: GetLocation()), Location: GetLocation());
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // EXPRESSION STATEMENT (fallback)
        // ═══════════════════════════════════════════════════════════════════════════

        return ParseExpressionStatement();
    }
}
