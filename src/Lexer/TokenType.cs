namespace Compiler.Lexer;

#region Token Type Enumeration

/// <summary>
/// Defines all possible token types that can be produced by the lexical analyzer.
/// Each token represents a meaningful unit of source code that the parser can work with.
/// </summary>
/// <remarks>
/// This comprehensive enumeration covers:
/// <list type="bullet">
/// <item>Basic literals (integers, decimals, characters, strings)</item>
/// <item>Typed numeric literals with explicit bit widths</item>
/// <item>ByteSize literals (bytes, kilobytes, etc.)</item>
/// <item>Duration literals (seconds, milliseconds, etc.)</item>
/// <item>Keywords for declarations, control flow, and special operations</item>
/// <item>Operators (arithmetic, comparison, bitwise, assignment)</item>
/// <item>Delimiters and punctuation</item>
/// <item>Special tokens (indent, dedent, EOF)</item>
/// </list>
/// </remarks>
public enum TokenType
{
    #region Literals

    #region Basic Literals

    // Suflae default number types (arbitrary precision)
    /// <summary>Arbitrary precision integer in Suflae - unsuffixed integers (42, 0xFF, 0b1010)</summary>
    Integer,

    /// <summary>Arbitrary precision decimal in Suflae - unsuffixed decimals (3.14, 2.718)</summary>
    Decimal,

    /// <summary>Byte letter literal with prefix (b'a')</summary>
    ByteLetterLiteral,

    /// <summary>Single character literal - default for plain quotes ('a', '\n')</summary>
    LetterLiteral,

    #endregion

    #region Text Literals

    // Basic 32-bit texts (default)
    /// <summary>Text literal - default for plain quotes ("hello world")</summary>
    TextLiteral,

    /// <summary>Formatted text with inserting expressions (f"hello {name}")</summary>
    FormattedText,

    /// <summary>Raw text that doesn't process escape sequences (r"C:\path\file")</summary>
    RawText,

    /// <summary>Raw formatted text combining raw and inserting (rf"path: {dir}\file")</summary>
    RawFormattedText,

    /// <summary>Marks the beginning of an f-string (f" or rf")</summary>
    InsertionStart,

    /// <summary>Marks the end of an f-string (closing ")</summary>
    InsertionEnd,

    /// <summary>Literal text portion within an f-string</summary>
    TextSegment,

    /// <summary>Format specifier after : in {expr:spec}</summary>
    FormatSpec,

    /// <summary>Bytes literal with explicit prefix (b"hello")</summary>
    BytesLiteral,

    /// <summary>Bytes raw literal with explicit prefix (br"C:\path")</summary>
    BytesRawLiteral,

    #endregion

    #region Typed Numeric Literals

    // Signed integers
    /// <summary>8-bit signed integer literal (42s8)</summary>
    S8Literal,

    /// <summary>16-bit signed integer literal (1000s16)</summary>
    S16Literal,

    /// <summary>32-bit signed integer literal (42s32)</summary>
    S32Literal,

    /// <summary>64-bit signed integer literal (1000s64)</summary>
    S64Literal,

    /// <summary>128-bit signed integer literal (42s128)</summary>
    S128Literal,

    // Unsigned integers
    /// <summary>8-bit unsigned integer literal (255u8)</summary>
    U8Literal,

    /// <summary>16-bit unsigned integer literal (65535u16)</summary>
    U16Literal,

    /// <summary>32-bit unsigned integer literal (42u32)</summary>
    U32Literal,

    /// <summary>64-bit unsigned integer literal (1000u64)</summary>
    U64Literal,

    /// <summary>128-bit unsigned integer literal (42u128)</summary>
    U128Literal,

    /// <summary>Address-sized unsigned integer literal (42a)</summary>
    AddressLiteral,

    // Floating point
    /// <summary>16-bit floating point literal (3.14f16)</summary>
    F16Literal,

    /// <summary>32-bit floating point literal (3.14f32)</summary>
    F32Literal,

    /// <summary>64-bit floating point literal (3.14f64)</summary>
    F64Literal,

    /// <summary>128-bit floating point literal (3.14f128)</summary>
    F128Literal,

    // Decimal
    /// <summary>32-bit decimal literal (3.14d32)</summary>
    D32Literal,

    /// <summary>64-bit decimal literal (3.14d64)</summary>
    D64Literal,

    /// <summary>128-bit decimal literal (3.14d128)</summary>
    D128Literal,

    // Imaginary (for complex numbers)
    /// <summary>32-bit imaginary literal (4.0j32)</summary>
    J32Literal,

    /// <summary>64-bit imaginary literal (4.0j64 or 4.0j)</summary>
    J64Literal,

    /// <summary>128-bit imaginary literal (4.0j128)</summary>
    J128Literal,

    /// <summary>Arbitrary precision imaginary literal (4.0jn)</summary>
    JnLiteral,

    #endregion

    #region ByteSize Literals

    /// <summary>Byte ByteSize literal (100b)</summary>
    ByteLiteral,

    // Kilobyte variants
    /// <summary>Kilobyte ByteSize literal using decimal (1000 bytes) (8kb)</summary>
    KilobyteLiteral,

    /// <summary>Kibibyte ByteSize literal using binary (1024 bytes) (8kib)</summary>
    KibibyteLiteral,

    // Megabyte variants
    /// <summary>Megabyte ByteSize literal using decimal (1000² bytes) (100mb)</summary>
    MegabyteLiteral,

    /// <summary>Mebibyte ByteSize literal using binary (1024² bytes) (100mib)</summary>
    MebibyteLiteral,

    // Gigabyte variants
    /// <summary>Gigabyte ByteSize literal using decimal (1000³ bytes) (4gb)</summary>
    GigabyteLiteral,

    /// <summary>Gibibyte ByteSize literal using binary (1024³ bytes) (4gib)</summary>
    GibibyteLiteral,

    #endregion

    #region Duration Literals

    /// <summary>Week duration literal (2w)</summary>
    WeekLiteral,

    /// <summary>Day duration literal (30d)</summary>
    DayLiteral,

    /// <summary>Hour duration literal (24h)</summary>
    HourLiteral,

    /// <summary>Minute duration literal (30m)</summary>
    MinuteLiteral,

    /// <summary>Second duration literal (45s)</summary>
    SecondLiteral,

    /// <summary>Millisecond duration literal (500ms)</summary>
    MillisecondLiteral,

    /// <summary>Microsecond duration literal (100us)</summary>
    MicrosecondLiteral,

    /// <summary>Nanosecond duration literal (50ns)</summary>
    NanosecondLiteral,

    #endregion

    #endregion

    #region Identifiers

    /// <summary>Regular identifier prefixed with let/var/nothing</summary>
    Identifier,

    #endregion

    #region Keywords

    #region Keywords - Declarations

    #region Type Declarations

    /// <summary>
    /// Routine (function) declaration keyword.
    /// Defines a callable unit of code with optional parameters and return type.
    /// </summary>
    Routine,

    /// <summary>
    /// Entity declaration keyword.
    /// Reference type for complex, dynamically-sized objects with identity semantics.
    /// Uses theatrical memory model (.view(), .hijack(), .share(), steal).
    /// </summary>
    Entity,

    /// <summary>
    /// Record declaration keyword.
    /// Value type for simple, fixed-size data with value semantics. Copied on assignment.
    /// Use 'with' for modified copies (record with (memberVar: value)).
    /// </summary>
    Record,

    /// <summary>
    /// Choice declaration keyword.
    /// Simple enumeration of discrete, stateless options (type-safe enum).
    /// Perfect for pattern matching with 'when'.
    /// </summary>
    Choice,

    /// <summary>
    /// Flags declaration keyword.
    /// Bitwise flag set type - each member represents a power-of-two bit flag.
    /// Supports bitwise combination and pattern matching on flag combinations.
    /// </summary>
    Flags,

    /// <summary>
    /// Variant declaration keyword.
    /// Tagged union - each case can hold different data types.
    /// Requires pattern matching to unpack. Cases are immediately disposed after unpacking.
    /// </summary>
    Variant,

    /// <summary>
    /// Protocol declaration keyword.
    /// Interface/trait contract defining method signatures that types must implement.
    /// Types use 'obeys' keyword to implement protocols.
    /// </summary>
    Protocol,

    #endregion

    #region Variable Declarations

    /// <summary>Modifiable variable binding - value can be reassigned</summary>
    Var,

    /// <summary>
    /// Build-time constant declaration.
    /// Unmodifiable, build-time evaluated. Use SCREAMING_SNAKE_CASE by convention.
    /// </summary>
    Preset,

    #endregion

    #region Access Modifiers

    /// <summary>Secret access modifier - visible only within the declaring module</summary>
    Secret,

    /// <summary>Posted access modifier - open read, secret write</summary>
    Posted,

    /// <summary>External linkage modifier - marks externally-linked declarations (FFI)</summary>
    External,

    /// <summary>Dangerous modifier - marks unsafe/dangerous routines or blocks</summary>
    Dangerous,

    /// <summary>Global scope modifier - declares module-level global variable</summary>
    Global,

    /// <summary>
    /// Static/class-level routine modifier.
    /// No receiver (no 'me'), accessed via Type.method() syntax.
    /// </summary>
    Common,

    #endregion

    #region Self References

    /// <summary>Self reference keyword (me) - refers to the current instance in methods</summary>
    Me,

    /// <summary>Self type keyword (Me) - the type of 'me', used in protocols for associated types</summary>
    MyType,

    #endregion

    #region Protocol Implementation

    /// <summary>
    /// Protocol implementation keyword.
    /// Declares that a type implements a protocol (record Foo obeys Bar).
    /// Similar to 'implements' in Java or ': Trait' in Rust.
    /// </summary>
    Obeys,

    /// <summary>
    /// Negated protocol constraint.
    /// Asserts a type does NOT implement a protocol (needs T disobeys SomeTrait).
    /// </summary>
    Disobeys,

    #endregion

    #endregion

    #region Keywords - Control Flow

    /// <summary>Conditional if statement keyword</summary>
    If,

    /// <summary>Elseif conditional keyword</summary>
    Elseif,

    /// <summary>Else conditional keyword</summary>
    Else,

    /// <summary>One-liner of conditional expression (if predicate then A else B)</summary>
    Then,

    /// <summary>Unless (negative if) conditional keyword</summary>
    Unless,

    /// <summary>Pattern matching when keyword</summary>
    When,

    /// <summary>Pattern matching is keyword</summary>
    Is,

    /// <summary>Infinite loop keyword</summary>
    Loop,

    /// <summary>While loop keyword</summary>
    While,

    /// <summary>For loop keyword</summary>
    For,

    /// <summary>Break statement keyword</summary>
    Break,

    /// <summary>Continue statement keyword</summary>
    Continue,

    /// <summary>Return statement keyword</summary>
    Return,

    /// <summary>Throw statement keyword - crashes with error</summary>
    Throw,

    /// <summary>Absent statement keyword - indicates value not found (triggers Lookup generation)</summary>
    Absent,

    /// <summary>Becomes statement keyword (block result value)</summary>
    Becomes,

    #endregion

    #region Keywords - Module System

    /// <summary>Import statement keyword</summary>
    Import,

    /// <summary>Module declaration keyword (maps to 'module' keyword)</summary>
    Module,

    #endregion

    #region Keywords - Special

    /// <summary>Using statement keyword for resource management (using db.open() as conn)</summary>
    Using,

    /// <summary>Type alias/Resource management as keyword (type Foo as Bar, or 'as' in scoped access blocks)</summary>
    As,

    /// <summary>Method/Type redefinition keyword (define A as B)</summary>
    Define,

    /// <summary>No-operation pass keyword - placeholder for empty blocks</summary>
    Pass,

    /// <summary>Danger mode keyword for unsafe operations (danger! { ... })</summary>
    Danger,

    /// <summary>With clause keyword for record copying with modifications (a with .x: 42)</summary>
    With,

    /// <summary>Given clause keyword for lambda captures (x given a => x + a)</summary>
    Given,

    /// <summary>
    /// Ownership transfer keyword (RazorForge only).
    /// Transfers ownership and invalidates the source (becomes deadref).
    /// Used for: single entity transfer (steal node), container push (list.push(steal node)),
    /// and consuming iteration (for item in steal list).
    /// </summary>
    Steal,

    /// <summary>In keyword for iteration and containment (for i in list, x in set)</summary>
    In,

    /// <summary>Not in keyword - negated containment check (x notin set)</summary>
    NotIn,

    /// <summary>Is not keyword - negated type/pattern check (x isnot Type)</summary>
    IsNot,

    /// <summary>To keyword for ascending Range (for i in 1 to 10)</summary>
    To,

    /// <summary>Til keyword for exclusive range end (for i in 0 til 10 means [0, 10))</summary>
    Til,

    /// <summary>By keyword for Range step size (for i in 1 to 10 by 2)</summary>
    By,

    /// <summary>
    /// Discard keyword for explicit discarding of variable
    /// </summary>
    Discard,

    #endregion

    #region Keywords - Logical Operators

    /// <summary>Logical AND operator keyword</summary>
    And,

    /// <summary>Logical OR operator keyword</summary>
    Or,

    /// <summary>Logical NOT operator keyword</summary>
    Not,

    /// <summary>
    /// Flags exact match keyword.
    /// Tests that only the specified flags are set — no more, no less.
    /// 'isonly A and B' builds to equality check (value == mask) vs
    /// 'is A and B' which is a superset check ((value &amp; mask) == mask).
    /// </summary>
    IsOnly,

    /// <summary>
    /// Flags removal / exclusion keyword.
    /// Removes flags from a value (perms but WRITE = bitwise AND NOT), or
    /// excludes flags in 'is' tests (perms is READ and WRITE but EXECUTE =
    /// READ and WRITE are set AND EXECUTE is NOT set).
    /// </summary>
    But,

    #endregion

    #region Keywords - Literals

    /// <summary>Boolean true literal keyword</summary>
    True,

    /// <summary>Boolean false literal keyword</summary>
    False,

    /// <summary>Null/none literal keyword</summary>
    None,

    #endregion

    #endregion

    #region Operators

    #region Operators - Basic Arithmetic

    /// <summary>Addition operator (+)</summary>
    Plus,

    /// <summary>Subtraction or unary minus operator (-)</summary>
    Minus,

    /// <summary>Multiplication operator (*)</summary>
    Star,

    /// <summary>Regular division or module path separation operator (/)</summary>
    Slash,

    /// <summary>Integer division operator (//)</summary>
    Divide,

    /// <summary>Modulo operator (%)</summary>
    Percent,

    #endregion

    #region Operators - Overflow Variants

    /// <summary>Wrapping addition operator (+%)</summary>
    PlusWrap,

    /// <summary>Clamping addition operator (+^)</summary>
    PlusClamp,

    /// <summary>Wrapping subtraction operator (-%)</summary>
    MinusWrap,

    /// <summary>Clamping subtraction operator (-^)</summary>
    MinusClamp,

    /// <summary>Wrapping multiplication operator (*%)</summary>
    MultiplyWrap,

    /// <summary>Clamping multiplication operator (*^)</summary>
    MultiplyClamp,

    /// <summary>Exponentiation operator (**)</summary>
    Power,

    /// <summary>Wrapping exponentiation operator (**%)</summary>
    PowerWrap,

    /// <summary>Clamping exponentiation operator (**^)</summary>
    PowerClamp,

    #endregion

    #region Operators - Comparison

    /// <summary>Equality comparison operator (==)</summary>
    Equal,

    /// <summary>Inequality comparison operator (!=)</summary>
    NotEqual,

    /// <summary>Reference equality comparison operator (===)</summary>
    ReferenceEqual,

    /// <summary>Reference inequality comparison operator (!==)</summary>
    ReferenceNotEqual,

    /// <summary>Less than comparison operator (&lt;)</summary>
    Less,

    /// <summary>Less than or equal comparison operator (&lt;=)</summary>
    LessEqual,

    /// <summary>Greater than comparison operator (>)</summary>
    Greater,

    /// <summary>Greater than or equal comparison operator (>=)</summary>
    GreaterEqual,

    /// <summary>Three-way comparison (spaceship) operator (&lt;=&gt;)</summary>
    ThreeWayComparison,

    #endregion

    #region Operators - Bitwise

    /// <summary>Bitwise AND operator (&)</summary>
    Ampersand,

    /// <summary>Bitwise OR operator (|)</summary>
    Pipe,

    /// <summary>Bitwise XOR operator (^)</summary>
    Caret,

    /// <summary>Bitwise NOT operator (~)</summary>
    Tilde,

    /// <summary>Left bit shift operator (&lt;&lt;)</summary>
    LeftShift,

    /// <summary>Right bit shift operator (>>)</summary>
    RightShift,

    /// <summary>Logical left bit shift operator (&lt;&lt;&lt;)</summary>
    LogicalLeftShift,

    /// <summary>Logical right bit shift operator (>>>)</summary>
    LogicalRightShift,

    #endregion

    #region Operators - Assignment and Special

    /// <summary>Assignment operator (=)</summary>
    Assign,

    /// <summary>Addition assignment operator (+=)</summary>
    PlusAssign,

    /// <summary>Subtraction assignment operator (-=)</summary>
    MinusAssign,

    /// <summary>Multiplication assignment operator (*=)</summary>
    StarAssign,

    /// <summary>Division assignment operator (/=)</summary>
    SlashAssign,

    /// <summary>Modulo assignment operator (%=)</summary>
    PercentAssign,

    /// <summary>Bitwise AND assignment operator (&amp;=)</summary>
    AmpersandAssign,

    /// <summary>Bitwise OR assignment operator (|=)</summary>
    PipeAssign,

    /// <summary>Bitwise XOR assignment operator (^=)</summary>
    CaretAssign,

    /// <summary>Left shift assignment operator (&lt;&lt;=)</summary>
    LeftShiftAssign,

    /// <summary>Right shift assignment operator (&gt;&gt;=)</summary>
    RightShiftAssign,

    /// <summary>Logical left shift assignment operator (&lt;&lt;&lt;=)</summary>
    LogicalLeftShiftAssign,

    /// <summary>Logical right shift assignment operator (&gt;&gt;&gt;=)</summary>
    LogicalRightShiftAssign,

    /// <summary>Integer division assignment operator (//=)</summary>
    DivideAssign,

    /// <summary>Power assignment operator (**=)</summary>
    PowerAssign,

    /// <summary>Wrapping add assignment operator (+%=)</summary>
    PlusWrapAssign,

    /// <summary>Wrapping subtract assignment operator (-%=)</summary>
    MinusWrapAssign,

    /// <summary>Wrapping multiply assignment operator (*%=)</summary>
    MultiplyWrapAssign,

    /// <summary>Wrapping power assignment operator (**%=)</summary>
    PowerWrapAssign,

    /// <summary>Clamping add assignment operator (+^=)</summary>
    PlusClampAssign,

    /// <summary>Clamping subtract assignment operator (-^=)</summary>
    MinusClampAssign,

    /// <summary>Clamping multiply assignment operator (*^=)</summary>
    MultiplyClampAssign,

    /// <summary>Clamping division operator (/^)</summary>
    SlashClamp,

    /// <summary>Clamping division assignment operator (/^=)</summary>
    SlashClampAssign,

    /// <summary>Clamping power assignment operator (**^=)</summary>
    PowerClampAssign,

    /// <summary>None coalescing assignment operator (??=)</summary>
    NoneCoalesceAssign,

    /// <summary>Crashable routine suffix (!)</summary>
    Bang,

    /// <summary>Force unwrap operator (!!)</summary>
    BangBang,

    /// <summary>Optional type operator (?)</summary>
    Question,

    /// <summary>Annotation operator (@)</summary>
    At,

    /// <summary>Intrinsic type (@intrinsic) - LLVM IR primitive types like i8, i32, f64, ptr</summary>
    Intrinsic,

    /// <summary>Comment operator (#)</summary>
    Hash,

    /// <summary>Function arrow operator (->)</summary>
    Arrow,

    /// <summary>Match/lambda fat arrow operator (=>)</summary>
    FatArrow,

    /// <summary>None coalescing operator (??)</summary>
    NoneCoalesce,

    #endregion

    #endregion

    #region Delimiters

    #region Delimiters - Brackets and Braces

    /// <summary>Left parenthesis delimiter (</summary>
    LeftParen,

    /// <summary>Right parenthesis delimiter )</summary>
    RightParen,

    /// <summary>Left square bracket delimiter [</summary>
    LeftBracket,

    /// <summary>Right square bracket delimiter ]</summary>
    RightBracket,

    /// <summary>Left curly brace delimiter {</summary>
    LeftBrace,

    /// <summary>Right curly brace delimiter }</summary>
    RightBrace,

    #endregion

    #region Delimiters - Punctuation

    /// <summary>Member access dot operator (.)</summary>
    Dot,

    /// <summary>Optional chaining operator (?.)</summary>
    QuestionDot,

    /// <summary>Comma separator (,)</summary>
    Comma,

    /// <summary>Type annotation colon (:)</summary>
    Colon,

    /// <summary>Vararg declaration operator (...) (ex, routine foo(values...: S32) -> S32</summary>
    DotDotDot,

    #endregion

    #endregion

    #region Special Tokens

    #region Whitespace Tokens

    /// <summary>Significant newline token (statement terminator)</summary>
    Newline,

    /// <summary>Indentation increment token</summary>
    Indent,

    /// <summary>Indentation decrement token</summary>
    Dedent,

    /// <summary>Documentation comment token (###)</summary>
    DocComment,

    #endregion

    #region Generic Constraints

    /// <summary>
    /// Generic type constraint keyword.
    /// Enforces build-time protocol requirements on generic types.
    /// Example: routine sort[T]() needs T obeys Comparable
    /// </summary>
    Requires,

    #endregion

    #region Async/Generator Keywords

    /// <summary>
    /// Generator yield keyword.
    /// Yields values from generator/iterator functions (coroutine mechanism).
    /// Similar to 'yield' in Python/C#.
    /// </summary>
    Emit,

    /// <summary>
    /// Emitting routine modifier keyword.
    /// Marks a routine as a generator that produces values via 'emit'.
    /// </summary>
    Emitting,

    /// <summary>
    /// Async function declaration keyword.
    /// Marks a function as asynchronous, returning a suspended computation.
    /// Similar to 'async' in Rust/JavaScript.
    /// </summary>
    Suspended,

    /// <summary>
    /// Await keyword for async operations.
    /// Waits for a suspended computation to complete and extracts the value.
    /// Similar to 'await' in Rust/JavaScript.
    /// </summary>
    Waitfor,

    /// <summary>
    /// Within keyword for await timeout settings.
    /// </summary>
    Within,

    /// <summary>
    /// After keyword for task dependency chains.
    /// Declares dependencies that must complete before the current task can run.
    /// Used with waitfor/within for declarative task graphs.
    /// </summary>
    After,

    /// <summary>
    /// OS thread function declaration keyword.
    /// Marks a function as running on a dedicated OS thread (heavyweight, CPU-bound).
    /// Counterpart to 'suspended' which uses green threads (lightweight, I/O-bound).
    /// </summary>
    Threaded,

    #endregion

    #region Terminal Tokens

    /// <summary>End of file marker token</summary>
    Eof,

    /// <summary>Unknown or invalid token</summary>
    Unknown

    #endregion

    #endregion
}

#endregion
