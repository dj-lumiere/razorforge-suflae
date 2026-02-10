namespace Compilers.Suflae.Lexer;

using Compilers.Shared.Lexer;

/// <summary>
/// Tokenizer implementation for the Suflae programming language.
/// </summary>
/// <remarks>
/// <para>
/// Suflae is a Python-inspired syntax variant of RazorForge that uses significant
/// indentation instead of braces for block structure. Key differences from RazorForge:
/// </para>
/// <list type="bullet">
///   <item><description>Indentation-based blocks (4 spaces per level)</description></item>
///   <item><description>No braces { } - colons followed by newlines start blocks</description></item>
///   <item><description>Defaults to arbitrary precision for unsuffixed numeric literals</description></item>
///   <item><description>Character literals use byte' and letter' prefixes</description></item>
///   <item><description>Text literals use b"..." prefix for bytes</description></item>
///   <item><description>Significant newlines (statement terminators)</description></item>
///   <item><description>Script mode detection for top-level code</description></item>
/// </list>
/// <para>
/// The tokenizer produces INDENT and DEDENT tokens to represent block structure,
/// similar to Python's tokenization model.
/// </para>
/// </remarks>
public partial class SuflaeTokenizer
{
    #region Fields

    /// <summary>
    /// The complete source code text being tokenized.
    /// </summary>
    private readonly string _source;

    /// <summary>
    /// The filename being tokenized;
    /// </summary>
    private readonly string _fileName;

    /// <summary>
    /// Current character position in the source text (0-based index).
    /// </summary>
    private int _position;

    /// <summary>
    /// Current line number in the source text (1-based).
    /// </summary>
    private int _line = 1;

    /// <summary>
    /// Current column number in the current line (1-based).
    /// </summary>
    private int _column = 1;

    /// <summary>
    /// Starting position of the current token being processed.
    /// </summary>
    private int _tokenStart;

    /// <summary>
    /// Starting column of the current token being processed.
    /// </summary>
    private int _tokenStartColumn;

    /// <summary>
    /// Starting line of the current token being processed.
    /// </summary>
    private int _tokenStartLine;

    /// <summary>
    /// List of tokens that have been successfully parsed from the source.
    /// </summary>
    private readonly List<Token> _tokens = [];

    /// <summary>
    /// Current indentation level (measured in units of 4 spaces).
    /// </summary>
    /// <remarks>
    /// An indentation level of 0 means no indentation (column 1).
    /// Level 1 = 4 spaces, Level 2 = 8 spaces, etc.
    /// </remarks>
    private int _currentIndentLevel;

    /// <summary>
    /// Flag indicating that an INDENT token is expected on the next non-empty line.
    /// </summary>
    /// <remarks>
    /// Set to true after encountering a colon that ends a line (block starter).
    /// The next line must have greater indentation than the current level.
    /// </remarks>
    private bool _expectIndent;

    /// <summary>
    /// Flag tracking whether any non-whitespace tokens have been processed on the current line.
    /// </summary>
    /// <remarks>
    /// Used to determine whether newlines are significant. A newline after
    /// actual content is significant; a blank line is not.
    /// </remarks>
    private bool _hasTokenOnLine;

    /// <summary>
    /// Flag tracking whether any definitions (routine, entity, etc.) have been found.
    /// </summary>
    /// <remarks>
    /// Used to detect script mode - if no definitions are found, the file is
    /// treated as a script with implicit main routine wrapping.
    /// </remarks>
    private bool _hasDefinitions;

    #endregion

    #region Keywords

    /// <summary>
    /// Dictionary mapping Suflae keywords to their corresponding token types.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Suflae shares most keywords with RazorForge, but excludes some
    /// RazorForge-specific keywords like 'danger' (Suflae doesn't support danger blocks).
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, TokenType> _keywords = new()
    {
        // Type declarations
        [key: "routine"] = TokenType.Routine,
        [key: "entity"] = TokenType.Entity,
        [key: "record"] = TokenType.Record,
        [key: "choice"] = TokenType.Choice,
        [key: "variant"] = TokenType.Variant,
        [key: "protocol"] = TokenType.Protocol,

        // Variable declarations
        [key: "let"] = TokenType.Let,
        [key: "var"] = TokenType.Var,
        [key: "preset"] = TokenType.Preset,

        // Access modifiers
        [key: "private"] = TokenType.Private,
        [key: "internal"] = TokenType.Internal,
        [key: "published"] = TokenType.Published,
        [key: "public"] = TokenType.Public,
        [key: "imported"] = TokenType.Imported,
        [key: "global"] = TokenType.Global,
        [key: "common"] = TokenType.Common,

        // Self references
        [key: "me"] = TokenType.Me,
        [key: "Me"] = TokenType.MyType,

        // Protocol implementation
        [key: "follows"] = TokenType.Follows,
        [key: "notfollows"] = TokenType.NotFollows,

        // Control flow
        [key: "if"] = TokenType.If,
        [key: "elseif"] = TokenType.Elseif,
        [key: "else"] = TokenType.Else,
        [key: "then"] = TokenType.Then,
        [key: "unless"] = TokenType.Unless,
        [key: "when"] = TokenType.When,
        [key: "is"] = TokenType.Is,
        [key: "loop"] = TokenType.Loop,
        [key: "while"] = TokenType.While,
        [key: "for"] = TokenType.For,
        [key: "break"] = TokenType.Break,
        [key: "continue"] = TokenType.Continue,
        [key: "return"] = TokenType.Return,
        [key: "throw"] = TokenType.Throw,
        [key: "absent"] = TokenType.Absent,
        [key: "becomes"] = TokenType.Becomes,

        // Module system
        [key: "import"] = TokenType.Import,
        [key: "module"] = TokenType.Module,

        // Special keywords
        [key: "using"] = TokenType.Using,
        [key: "as"] = TokenType.As,
        [key: "define"] = TokenType.Define,
        [key: "pass"] = TokenType.Pass,
        [key: "with"] = TokenType.With,
        [key: "given"] = TokenType.Given,
        [key: "in"] = TokenType.In,
        [key: "notin"] = TokenType.NotIn,
        [key: "isnot"] = TokenType.IsNot,
        [key: "to"] = TokenType.To,
        [key: "downto"] = TokenType.Downto,
        [key: "by"] = TokenType.By,
        [key: "discard"] = TokenType.Discard,

        // Logical operators
        [key: "and"] = TokenType.And,
        [key: "or"] = TokenType.Or,
        [key: "not"] = TokenType.Not,

        // Literals
        [key: "true"] = TokenType.True,
        [key: "false"] = TokenType.False,
        [key: "None"] = TokenType.None,

        // Generic constraints
        [key: "requires"] = TokenType.Requires,

        // Async/generator
        [key: "generate"] = TokenType.Generate,
        [key: "suspended"] = TokenType.Suspended,
        [key: "waitfor"] = TokenType.Waitfor,
        [key: "until"] = TokenType.Until,
        [key: "after"] = TokenType.After
    };

    #endregion

    #region Suffix Mappings

    /// <summary>
    /// Maps numeric type suffixes to their corresponding token types.
    /// </summary>
    /// <remarks>
    /// Suflae supports all RazorForge numeric type suffixes for interoperability.
    /// </remarks>
    private readonly Dictionary<string, TokenType> _numericSuffixToTokenType = new()
    {
        // Signed integers
        [key: "s8"] = TokenType.S8Literal,
        [key: "s16"] = TokenType.S16Literal,
        [key: "s32"] = TokenType.S32Literal,
        [key: "s64"] = TokenType.S64Literal,
        [key: "s128"] = TokenType.S128Literal,
        [key: "saddr"] = TokenType.SAddrLiteral,

        // Unsigned integers
        [key: "u8"] = TokenType.U8Literal,
        [key: "u16"] = TokenType.U16Literal,
        [key: "u32"] = TokenType.U32Literal,
        [key: "u64"] = TokenType.U64Literal,
        [key: "u128"] = TokenType.U128Literal,
        [key: "uaddr"] = TokenType.UAddrLiteral,

        // Floating-point
        [key: "f16"] = TokenType.F16Literal,
        [key: "f32"] = TokenType.F32Literal,
        [key: "f64"] = TokenType.F64Literal,
        [key: "f128"] = TokenType.F128Literal,

        // Decimal floating-point
        [key: "d32"] = TokenType.D32Literal,
        [key: "d64"] = TokenType.D64Literal,
        [key: "d128"] = TokenType.D128Literal,

        // Imaginary (for complex numbers)
        [key: "j"] = TokenType.J64Literal,    // Default to 64-bit
        [key: "j32"] = TokenType.J32Literal,
        [key: "j64"] = TokenType.J64Literal,
        [key: "j128"] = TokenType.J128Literal,
        [key: "jn"] = TokenType.JnLiteral     // Arbitrary precision
    };

    /// <summary>
    /// The suffix for arbitrary precision numbers.
    /// Maps to Integer (whole numbers) or Decimal (floating-point).
    /// </summary>
    private const string ArbitraryPrecisionSuffix = "n";

    /// <summary>
    /// Maps memory size suffixes to their corresponding token types.
    /// </summary>
    private readonly Dictionary<string, TokenType> _memorySuffixToTokenType = new()
    {
        [key: "b"] = TokenType.ByteLiteral,
        [key: "kb"] = TokenType.KilobyteLiteral,
        [key: "kib"] = TokenType.KibibyteLiteral,
        [key: "mb"] = TokenType.MegabyteLiteral,
        [key: "mib"] = TokenType.MebibyteLiteral,
        [key: "gb"] = TokenType.GigabyteLiteral,
        [key: "gib"] = TokenType.GibibyteLiteral,
    };

    /// <summary>
    /// Maps duration suffixes to their corresponding token types.
    /// </summary>
    private readonly Dictionary<string, TokenType> _durationSuffixToTokenType = new()
    {
        [key: "w"] = TokenType.WeekLiteral,
        [key: "d"] = TokenType.DayLiteral,
        [key: "h"] = TokenType.HourLiteral,
        [key: "m"] = TokenType.MinuteLiteral,
        [key: "s"] = TokenType.SecondLiteral,
        [key: "ms"] = TokenType.MillisecondLiteral,
        [key: "us"] = TokenType.MicrosecondLiteral,
        [key: "ns"] = TokenType.NanosecondLiteral
    };

    /// <summary>
    /// Maps text literal prefixes to their corresponding token types.
    /// </summary>
    /// <remarks>
    /// Suflae uses a simplified prefix system: r, f, rf for text, and b, br, bf, brf for bytes.
    /// </remarks>
    private readonly Dictionary<string, TokenType> _textPrefixToTokenType = new()
    {
        [key: "r"] = TokenType.RawText,
        [key: "f"] = TokenType.FormattedText,
        [key: "rf"] = TokenType.RawFormattedText,
        [key: "b"] = TokenType.BytesLiteral,
        [key: "br"] = TokenType.BytesRawLiteral
    };

    /// <summary>
    /// List of all valid text prefixes for prefix matching.
    /// </summary>
    private readonly List<string> _textPrefixes =
    [
        "r", "f", "rf", "b", "br"
    ];

    #endregion

    #region Constructor and Properties

    /// <summary>
    /// Initializes a new instance of the <see cref="SuflaeTokenizer"/> class.
    /// </summary>
    /// <param name="source">The Suflae source code to tokenize.</param>
    /// <param name="fileName">The source code's file name being tokenized.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="source"/> is null.</exception>
    public SuflaeTokenizer(string source, string fileName)
    {
        _fileName = fileName;
        _source = source ?? throw new ArgumentNullException(paramName: nameof(source));
    }

    /// <summary>
    /// Gets a value indicating whether the source code is in script mode.
    /// </summary>
    /// <value>
    /// <c>true</c> if no definitions (routine, entity, record, etc.) were found;
    /// <c>false</c> if at least one definition was found.
    /// </value>
    /// <remarks>
    /// <para>
    /// Script mode allows Suflae files to contain top-level statements without
    /// an explicit main routine. The compiler wraps script mode code in an
    /// implicit main routine.
    /// </para>
    /// <para>
    /// This property is only valid after <see cref="Tokenize"/> has been called.
    /// </para>
    /// </remarks>
    public bool IsScriptMode => !_hasDefinitions;

    #endregion

    #region Public Methods

    /// <summary>
    /// Tokenizes the entire source code and returns a list of tokens.
    /// </summary>
    /// <returns>
    /// A list of <see cref="Token"/> objects representing the tokenized source code.
    /// The list includes INDENT/DEDENT tokens for block structure and ends with EOF.
    /// </returns>
    /// <exception cref="LexerException">
    /// Thrown when the source contains invalid syntax, such as misaligned indentation,
    /// unterminated strings, or invalid escape sequences.
    /// </exception>
    /// <remarks>
    /// <para>
    /// After tokenization completes, any remaining indentation levels are closed
    /// with DEDENT tokens to ensure proper block closure.
    /// </para>
    /// <para>
    /// After calling this method, the <see cref="IsScriptMode"/> property reflects
    /// whether the source contained any definition keywords.
    /// </para>
    /// </remarks>
    public List<Token> Tokenize()
    {
        while (!IsAtEnd())
        {
            _tokenStart = _position;
            _tokenStartColumn = _column;
            _tokenStartLine = _line;

            ScanToken();
        }

        // Emit remaining DEDENT tokens at end of file
        for (int i = 0; i < _currentIndentLevel; i += 1)
        {
            AddToken(type: TokenType.Dedent, text: "");
        }

        _tokens.Add(item: new Token(Type: TokenType.Eof,
            FileName: _fileName,
            Text: "",
            Line: _line,
            Column: _column,
            Position: _position));
        return _tokens;
    }

    #endregion
}
