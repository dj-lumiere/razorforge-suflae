using System;
using System.Collections.Generic;
using Compiler.Diagnostics;
using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;

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
    private readonly List<Token> _tokens;

    /// <summary>
    /// Current position in the token stream.
    /// </summary>
    private int _position;

    /// <summary>
    /// Collection of warnings generated during parsing.
    /// </summary>
    private readonly List<BuildWarning> _warnings = [];

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
    public List<string> GetErrors()
    {
        return _errors;
    }

    /// <summary>
    /// The source file name for error reporting.
    /// </summary>
    public string FileName = "";

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
    private int _currentIndentationLevel;

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
    /// Indicates whether we're currently parsing inside a type body (record, entity).
    /// When true, allows member variable declarations without var keywords.
    /// </summary>
    private bool _parsingTypeBody;

    /// <summary>
    /// Indicates whether we're parsing inside a record body (actual record, not entity).
    /// When true, only secret/posted/open modifiers are allowed (not external).
    /// Also var/preset keywords are disallowed (use 'name: Type' syntax).
    /// </summary>
    private bool _parsingStrictRecordBody;

    /// <summary>
    /// Indicates whether we're currently parsing inside a routine body.
    /// When true, nested routine declarations are rejected.
    /// </summary>
    private bool _inRoutineBody;

    /// <summary>
    /// Indicates whether we are currently parsing within a 'when' pattern context.
    /// Used to disambiguate pattern matching syntax from regular expressions.
    /// </summary>
    private bool _inWhenPatternContext;

    /// <summary>
    /// Indicates we are parsing the condition of a subjectless (condition-based) 'when' arm.
    /// Suppresses bare-identifier lambda parsing so `flag => result` reads as
    /// condition `flag` + arm arrow, not lambda `flag => result`. Unlike
    /// _inWhenPatternContext it leaves the 'is' operator available, and it is
    /// suspended inside parentheses and argument lists so explicit lambdas there
    /// still parse.
    /// </summary>
    private bool _inWhenConditionContext;

    /// <summary>
    /// The reserved parameter name a single-hole `_` lambda desugars to. `xs.map(_ * 2)` parses the
    /// `_` as a reference to this name and wraps the whole argument in `LambdaExpression([<hole>], _*2)`.
    /// </summary>
    internal const string HoleParamName = "__rf_hole";

    /// <summary>
    /// Set by <c>ParsePrimary</c> when it parses a bare `_` placeholder (single-hole lambda). Read and
    /// reset per-argument by <c>ParseArgument</c>, which wraps the argument into a lambda when set — so
    /// the lambda boundary is the nearest enclosing argument.
    /// </summary>
    private bool _sawHole;

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
        _tokens = tokens;
        _language = language;
        FileName = fileName ?? "unknown";
        _indentationStack.Push(item: 0); // Base indentation level
    }

    /// <summary>
    /// Parses the token stream into a complete program AST.
    /// Main entry point for parsing source files.
    /// </summary>
    /// <returns>A <see cref="SyntaxTree.Program"/> containing all top-level declarations.</returns>
    public Program Parse()
    {
        var declarations = new List<ISyntaxTreeNode>();

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

                ISyntaxTreeNode decl = ParseDeclaration();
                declarations.Add(item: decl);
            }
            catch (GrammarException ex)
            {
                // GrammarException.Message already contains formatted error:
                // error[RF-G150]: filename.rf:9:14: message
                // error[SF-G150]: filename.sf:9:14: message
                _errors.Add(item: ex.Message);
                DiagnosticRenderer.Print(ex: ex, writer: Console.Error);
                Synchronize();
            }
        }

        if (_language == Language.Suflae)
        {
            declarations = WrapScriptStatementsIntoStart(nodes: declarations);
        }

        return new Program(Declarations: declarations, Location: GetLocation());
    }

    /// <summary>
    /// Suflae "script mode": a file whose top level has loose STATEMENTS (an expression, `each`/`while`/
    /// `if`, an assignment, …) needs no explicit entry point — those statements, together with any top-level
    /// runtime <c>var</c> declarations, become the body of an implicit <c>routine start()</c> (in source
    /// order; a trailing <c>return</c> is added). Hoistable declarations (module/import/type/routine/preset/
    /// define) stay as siblings and hoist as usual. A no-op for a normal module file (no loose statements).
    /// An explicit <c>start</c> alongside top-level statements is a conflict.
    /// </summary>
    private List<ISyntaxTreeNode> WrapScriptStatementsIntoStart(List<ISyntaxTreeNode> nodes)
    {
        // Trigger only on a loose top-level STATEMENT — a pure module file (declarations only) is untouched.
        bool hasLooseStatement = false;
        foreach (ISyntaxTreeNode n in nodes)
        {
            if (n is Statement)
            {
                hasLooseStatement = true;
                break;
            }
        }

        if (!hasLooseStatement)
        {
            return nodes;
        }

        // Collect the executable top-level nodes (statements + runtime var decls) in source order.
        var body = new List<Statement>();
        var kept = new List<ISyntaxTreeNode>();
        SourceLocation startLoc = GetLocation();
        bool locSet = false;
        RoutineDeclaration? explicitStart = null;
        foreach (ISyntaxTreeNode n in nodes)
        {
            switch (n)
            {
                case Statement s:
                    if (!locSet) { startLoc = s.Location; locSet = true; }
                    body.Add(item: s);
                    break;
                case VariableDeclaration vd:
                    if (!locSet) { startLoc = vd.Location; locSet = true; }
                    body.Add(item: new DeclarationStatement(Declaration: vd, Location: vd.Location));
                    break;
                default:
                    if (n is RoutineDeclaration { Name: "start" } rd) { explicitStart = rd; }
                    kept.Add(item: n);
                    break;
            }
        }

        if (explicitStart != null)
        {
            // Report a clean diagnostic (matching the per-statement error path) rather than throwing out of
            // the parser; keep the explicit start so the Program stays well-formed and downstream is safe.
            var ex = new GrammarException(code: GrammarDiagnosticCode.UnexpectedToken,
                message:
                "A Suflae file cannot mix top-level statements with an explicit `routine start()`. " +
                "Either move the top-level statements into start(), or remove the explicit start().",
                fileName: FileName, line: startLoc.Line, column: startLoc.Column, language: _language);
            _errors.Add(item: ex.Message);
            DiagnosticRenderer.Print(ex: ex, writer: Console.Error);
            return kept;
        }

        if (body.Count == 0 || body[^1] is not ReturnStatement)
        {
            body.Add(item: new ReturnStatement(Value: null, Location: startLoc));
        }

        kept.Add(item: new RoutineDeclaration(
            Name: "start",
            Parameters: [],
            ReturnType: null,
            Body: new BlockStatement(Statements: body, Location: startLoc),
            Visibility: VisibilityModifier.Open,
            Annotations: [],
            Location: startLoc));
        return kept;
    }

    /// <summary>
    /// Parses a single top-level or nested declaration.
    /// Handles: module, import, define, using, var, routine, entity, record, choice, variant, protocol, impl.
    /// RazorForge-only: external, dangerous modifier, threaded async status.
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
    ///   annotations   - @crash_only, @inline, @llvm("i32"), etc.
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
    private ISyntaxTreeNode ParseDeclaration()
    {
        // ═══════════════════════════════════════════════════════════════════════════
        // SKIP DOC COMMENTS (### comment lines before declarations)
        // ═══════════════════════════════════════════════════════════════════════════
        // Doc comments are preserved in the token stream but currently not attached
        // to declarations. Skip them to prevent "Unexpected token" errors.
        while (Match(type: TokenType.DocComment))
        {
            // Skip any newlines after doc comments
            while (Match(type: TokenType.Newline)) { } // NOSONAR S108: intentional newline-consuming loop
        }

        // A leading `@target(...)` build directive (file-granularity conditional compilation) precedes
        // `module`. It is read PRE-PARSE by the build's file gate to decide whether to compile this file
        // at all; by the time the parser sees it the file is already selected, so here it is consumed and
        // discarded (its effect happened earlier). Keeping it a real `@`-annotation — not a comment — is
        // what gives it editor highlighting.
        if (Check(type: TokenType.At) && PeekToken(offset: 1).Type == TokenType.Identifier
            && PeekToken(offset: 1).Text == "target")
        {
            ParseAnnotations();
            while (Match(type: TokenType.Newline)) { } // NOSONAR S108
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
        // PARSE MODIFIERS (annotations, visibility, storage class)
        // ═══════════════════════════════════════════════════════════════════════════

        // Parse annotations (e.g., @inline, @crash_only, @llvm("i32"))
        List<string> annotations = ParseAnnotations();

        // Skip newlines between annotations and the declaration they modify
        // e.g., @readonly\nroutine foo() should work
        if (annotations.Count > 0)
        {
            while (Match(type: TokenType.Newline))
            {
                // Skip newlines
            }
        }

        // Parse visibility and storage class modifiers
        (VisibilityModifier visibility, StorageClass storage) = ParseModifiers();

        // Define declaration with annotations (e.g., @llvm("i32") define MyInt as S32)
        if (Match(type: TokenType.Define))
        {
            return ParseDefineDeclaration(annotations: annotations);
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

        // Foreign routines are declared via realm-qualified names — `routine C::name(...)` /
        // `routine LLVM::name(...)` (handled in the routine-declaration dispatch below), which produce an
        // ExternalDeclaration. The old `external("C"|"llvm")` keyword form (incl. the block form) was
        // removed in favor of that spelling.

        // Decl-position expand: `expand m in memvarof(T)` inside a record/entity body generates one
        // member-variable column per member of the (concrete-at-instantiation) source type. Used by the
        // struct-of-arrays collections SplitArray/SplitList.
        if (_parsingTypeBody && Check(type: TokenType.Expand))
        {
            return ParseExpandMemberDeclaration();
        }

        // Field declaration in type bodies: name: Type
        // Detected by identifier followed by colon (no var keyword needed)
        // Only allowed inside type bodies (record, entity)
        if (_parsingTypeBody && Check(type: TokenType.Identifier) && PeekToken(offset: 1)
               .Type == TokenType.Colon)
        {
            // In record bodies, external is not allowed
            if (_parsingStrictRecordBody && visibility is VisibilityModifier.External)
            {
                throw new GrammarException(code: GrammarDiagnosticCode.InvalidDeclarationInBody,
                    message:
                    $"'{visibility.ToString().ToLower()}' is not valid for record member variables. " +
                    "Record member variables can use 'secret', 'posted', or 'open'",
                    fileName: FileName,
                    line: CurrentToken.Line,
                    column: CurrentToken.Column,
                    language: _language);
            }

            return ParseMemberVariableDeclaration(visibility: visibility);
        }

        // Variable declarations — optionally prefixed with `lateinit`
        bool declLateInit = false;
        if (Check(type: TokenType.LateInit) && PeekToken(offset: 1).Type == TokenType.Var)
        {
            Advance(); // consume 'lateinit'
            declLateInit = true;
        }
        // `secret preset NAME` (visibility-prefixed preset) — route to the dedicated preset parser so
        // it becomes a PresetDeclaration carrying its secret (file-private) flag, not a VariableDeclaration.
        if (Check(type: TokenType.Preset))
        {
            if (_parsingTypeBody)
            {
                throw new GrammarException(code: GrammarDiagnosticCode.InvalidDeclarationInBody,
                    message: "Type member variables cannot use 'var' or 'preset'. " +
                             "Use 'name: Type' syntax instead",
                    fileName: FileName,
                    line: CurrentToken.Line,
                    column: CurrentToken.Column,
                    language: _language);
            }

            Advance(); // consume 'preset'
            return ParsePresetDeclaration(isSecret: visibility == VisibilityModifier.Secret);
        }
        if (Match(TokenType.Var))
        {
            // In type bodies (record, entity), var/preset are not allowed
            // MemberVariables use 'name: Type' syntax without var keywords
            if (_parsingTypeBody)
            {
                throw new GrammarException(code: GrammarDiagnosticCode.InvalidDeclarationInBody,
                    message: "Type member variables cannot use 'var' or 'preset'. " +
                             "Use 'name: Type' syntax instead",
                    fileName: FileName,
                    line: CurrentToken.Line,
                    column: CurrentToken.Column,
                    language: _language);
            }

            return ParseVariableDeclaration(visibility: visibility, storage: storage,
                annotations: annotations, isLateInit: declLateInit);
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

        AsyncStatus asyncStatus = AsyncStatus.None;

        // Concurrency modifier: threaded routine foo() (RazorForge only, v0.1)
        if (_language == Language.RazorForge && Match(type: TokenType.Threaded))
        {
            asyncStatus = AsyncStatus.Threaded;
        }
        // Concurrency modifier: suspended routine foo() — a stackful coroutine (v0.2 async).
        else if (_language == Language.RazorForge && Match(type: TokenType.Suspended))
        {
            asyncStatus = AsyncStatus.Suspended;
        }

        // Routine (function) declaration
        if (Match(type: TokenType.Routine))
        {
            // Realm-qualified FOREIGN routine: `routine C::malloc(...)` / `routine LLVM::sqrt(...)`. The
            // realm tag before `::` picks the calling convention; the declaration is an ExternalDeclaration
            // (no body, foreign impl) — the modern spelling of `external("C"|"llvm") routine ...`.
            if (Check(type: TokenType.Identifier) &&
                PeekToken(offset: 1).Type == TokenType.DoubleColon)
            {
                string? conv = CurrentToken.Text switch
                {
                    "C" => "C",
                    "LLVM" => "llvm",
                    _ => null
                };
                if (conv != null)
                {
                    Advance(); // realm tag (C / LLVM)
                    Advance(); // ::
                    return ParseExternalDeclaration(callingConvention: conv,
                        annotations: annotations,
                        isDangerous: isDangerous);
                }
            }

            return ParseRoutineDeclaration(visibility: visibility,
                annotations: annotations,
                storage: storage,
                asyncStatus: asyncStatus,
                isDangerous: isDangerous);
        }

        // If we consumed async modifiers but no 'routine' follows, that's an error
        if (asyncStatus != AsyncStatus.None)
        {
            string modifier = asyncStatus switch
            {
                AsyncStatus.Suspended => "suspended",
                AsyncStatus.Threaded  => "threaded",
                _                     => asyncStatus.ToString().ToLower()
            };
            throw new GrammarException(code: GrammarDiagnosticCode.UnexpectedToken,
                message: $"'{modifier}' must be followed by 'routine'",
                fileName: FileName,
                line: CurrentToken.Line,
                column: CurrentToken.Column,
                language: _language);
        }

        // Validate: storage class modifiers are not valid for type declarations
        if (storage != StorageClass.None)
        {
            bool isTypeKeyword = Check(TokenType.Entity,
                TokenType.Record,
                TokenType.Choice,
                TokenType.Flags,
                TokenType.Crashable,
                TokenType.Variant,
                TokenType.Protocol);

            if (isTypeKeyword)
            {
                throw new GrammarException(code: GrammarDiagnosticCode.InvalidDeclarationInBody,
                    message:
                    $"'{storage.ToString().ToLower()}' storage class is not valid for type declarations",
                    fileName: FileName,
                    line: CurrentToken.Line,
                    column: CurrentToken.Column,
                    language: _language);
            }
        }

        // Entity/Record/Choice declarations
        if (Match(type: TokenType.Entity))
        {
            return ParseEntityDeclaration(visibility: visibility);
        }

        if (Match(type: TokenType.Record))
        {
            return ParseRecordDeclaration(visibility: visibility, annotations: annotations);
        }

        if (Match(type: TokenType.Choice))
        {
            return ParseChoiceDeclaration(visibility: visibility);
        }

        if (Match(type: TokenType.Flags))
        {
            return ParseFlagsDeclaration(visibility: visibility);
        }

        if (Match(type: TokenType.Crashable))
        {
            return ParseCrashableDeclaration(visibility: visibility);
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
            string validDeclarations =
                "routine, entity, record, choice, variant, protocol, preset, or var";
            throw ThrowParseError(code: GrammarDiagnosticCode.VisibilityWithoutDeclaration,
                message: $"Visibility modifier '{visibility}' must be followed by a declaration " +
                         $"({validDeclarations})");
        }

        // If we have annotations but no declaration, that's an error
        if (annotations.Count > 0)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.AnnotationsWithoutDeclaration,
                message:
                "Annotations must be followed by a declaration (routine, entity, record, etc.)");
        }

        // Otherwise parse as statement
        return ParseStatement();
    }

    /// <summary>
    /// Parses a single statement within a block or function body.
    /// Handles: if, while, for, when, return, throw, absent, break, continue, and expression statements.
    /// RazorForge-only: danger block, steal expression, release statement, block statements.
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
    ///   danger      - Unsafe block (raw pointers, FFI)
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
    private Statement ParseStatement() // NOSONAR S3776
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
        while (Match(type: TokenType.Newline)) { } // NOSONAR S108: intentional newline-consuming loop

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

        if (Match(type: TokenType.Each))
        {
            return ParseEachStatement();
        }

        if (Match(type: TokenType.Expand))
        {
            return ParseExpandStatement();
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
            return ParseThrowStatement(isFatal: false);
        }

        if (Match(type: TokenType.Pierce))
        {
            return ParseThrowStatement(isFatal: true);
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
        bool stmtLateInit = false;
        if (Check(type: TokenType.LateInit) && PeekToken(offset: 1).Type == TokenType.Var)
        {
            Advance(); // consume 'lateinit'
            stmtLateInit = true;
        }
        if (Match(TokenType.Var, TokenType.Preset))
        {
            // Check if this is destructuring: var (a, b) = expr
            if (Check(type: TokenType.LeftParen))
            {
                return ParseDestructuringDeclaration();
            }

            VariableDeclaration varDecl = ParseVariableDeclaration(isLateInit: stmtLateInit);
            // Wrap the variable declaration as a declaration statement
            return new DeclarationStatement(Declaration: varDecl, Location: varDecl.Location);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // EXPRESSION STATEMENT (FALLBACK)
        // ═══════════════════════════════════════════════════════════════════════════

        return ParseExpressionStatement();
    }
}
