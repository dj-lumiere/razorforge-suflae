using SemanticAnalysis.Types;
using Compiler.Lexer;

namespace SyntaxTree;

#region Base Expression Types

/// <summary>
/// Base entity for all expression nodes in the AST.
/// Expressions are constructs that evaluate to produce values and can be used as operands.
/// They represent computations, value retrievals, and data transformations.
/// </summary>
/// <param name="Location">Source location information for error reporting and debugging</param>
/// <remarks>
/// Expressions form the computational foundation of the languages:
/// <list type="bullet">
/// <item>Literal values: numbers, strings, booleans</item>
/// <item>Variable access and function calls</item>
/// <item>Arithmetic and logical operations</item>
/// <item>Complex operations: member access, indexing, type conversions</item>
/// <item>Memory operations: slice creators, memory slice method calls</item>
/// <item>Danger zone operations: raw memory access, type punning</item>
/// </list>
/// After semantic analysis, ResolvedType contains the computed type of this expression.
/// </remarks>
public abstract record Expression(SourceLocation Location) : AstNode(Location: Location)
{
    /// <summary>
    /// The resolved type of this expression, set by the semantic analyzer.
    /// This is null before semantic analysis and populated during type checking.
    /// Code generators should use this instead of re-inferring types.
    /// </summary>
    public TypeInfo? ResolvedType { get; set; }
}

#endregion

#region Literal and Identifier Expressions

/// <summary>
/// Expression representing constant literal values embedded in source code.
/// Includes all primitive types: integers, floats, strings, characters, and booleans.
/// </summary>
/// <param name="Value">The actual value of the literal (42, 3.14, "hello", 'a', true)</param>
/// <param name="LiteralType">Token type indicating the specific literal format used</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Supports the full range of RazorForge/Suflae literal types:
/// <list type="bullet">
/// <item>Integers: S8, S16, S32, S64, S128, U8, U16, U32, U64, U128, Address</item>
/// <item>Floats: F16, F32, F64, F128</item>
/// <item>Decimals: D32, D64, D128</item>
/// <item>Text and characters: Letter, Byte, Text, Bytes</item>
/// <item>Booleans: true, false</item>
/// <item>Duration: w, d, h, m, s, ms, us, ns</item>
/// <item>ByteSize: b, kb, kib, mb, mib, gb, gib</item>
/// </list>
/// </remarks>
public record LiteralExpression(object Value, TokenType LiteralType, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitLiteralExpression(node: this);
    }
}

/// <summary>
/// Abstract base for parts of an inserted text expression (f-string).
/// </summary>
public abstract record InsertedTextPart(SourceLocation Location);

/// <summary>
/// A literal text segment within an f-string.
/// </summary>
public record TextPart(string Text, SourceLocation Location) : InsertedTextPart(Location: Location);

/// <summary>
/// An embedded expression within an f-string, with optional format specifier.
/// </summary>
public record ExpressionPart(Expression Expression, string? FormatSpec, SourceLocation Location)
    : InsertedTextPart(Location: Location);

/// <summary>
/// Expression representing an f-string with text insertion: f"Hello, {name}!"
/// Contains a list of text and expression parts.
/// </summary>
public record InsertedTextExpression(List<InsertedTextPart> Parts, bool IsRaw, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitInsertedTextExpression(node: this);
    }
}

/// <summary>
/// Expression representing a list literal: [1, 2, 3]
/// Creates a List containing the specified elements.
/// </summary>
/// <param name="Elements">The expressions for each list element</param>
/// <param name="ElementType">Optional explicit element type annotation</param>
/// <param name="Location">Source location information</param>
public record ListLiteralExpression(
    List<Expression> Elements,
    TypeExpression? ElementType,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitListLiteralExpression(node: this);
    }
}

/// <summary>
/// Expression representing a set literal: {1, 2, 3}
/// Creates a Set containing the specified unique elements.
/// </summary>
/// <param name="Elements">The expressions for each set element</param>
/// <param name="ElementType">Optional explicit element type annotation</param>
/// <param name="Location">Source location information</param>
public record SetLiteralExpression(
    List<Expression> Elements,
    TypeExpression? ElementType,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitSetLiteralExpression(node: this);
    }
}

/// <summary>
/// Expression representing a dictionary literal: {key1: value1, key2: value2}
/// Creates a Dict with the specified key-value pairs.
/// </summary>
/// <param name="Pairs">The key-value expression pairs</param>
/// <param name="KeyType">Optional explicit key type annotation</param>
/// <param name="ValueType">Optional explicit value type annotation</param>
/// <param name="Location">Source location information</param>
public record DictLiteralExpression(
    List<(Expression Key, Expression Value)> Pairs,
    TypeExpression? KeyType,
    TypeExpression? ValueType,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitDictLiteralExpression(node: this);
    }
}

/// <summary>
/// Expression representing a tuple literal: (1, 2, 3) or (x,) for single-element.
/// Always creates a Tuple (inline LLVM struct). Entities stored as ptr fields.
/// </summary>
/// <param name="Elements">The expressions for each tuple element (must have at least 1 element)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Tuple literal syntax:
/// <list type="bullet">
/// <item>Multi-element: (1, 2, 3) - creates tuple with 3 elements</item>
/// <item>Single-element: (42,) - trailing comma required to distinguish from parenthesized expression</item>
/// <item>Nested: (1, (2, 3)) - tuples can contain other tuples</item>
/// </list>
/// Note: Empty tuples () are not valid. Use Blank for unit type.
/// Access elements via .item0, .item1, etc.
/// </remarks>
public record TupleLiteralExpression(
    List<Expression> Elements,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitTupleLiteralExpression(node: this);
    }
}

/// <summary>
/// Expression that references a named symbol (variable, function, entity, etc.).
/// Represents the use of identifiers that must be resolved during semantic analysis.
/// </summary>
/// <param name="Name">The identifier name to look up in the symbol table</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Identifier resolution follows lexical scoping rules:
/// <list type="bullet">
/// <item>Variables: local scope, then enclosing scopes, then global</item>
/// <item>Functions: may be resolved with overloading based on call context</item>
/// <item>Types: resolved in type scope with visibility rules</item>
/// <item>Qualified names: may represent member access chains</item>
/// </list>
/// </remarks>
public record IdentifierExpression(string Name, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitIdentifierExpression(node: this);
    }
}

#endregion

#region Operator Expressions

/// <summary>
/// Expression representing a compound assignment operation (e.g., a += b).
/// The semantic analyzer dispatches this to either an in-place wired method ($iadd, etc.)
/// or falls back to create-and-assign (a = a.$add(b)) for records/primitives.
/// Entities require the in-place wired (no fallback, since bare entity assignment is prohibited).
/// </summary>
/// <param name="Target">The assignment target (must be a modifiable variable, member variable, or index)</param>
/// <param name="Operator">The base binary operator (Add, Subtract, etc. — not Assign)</param>
/// <param name="Value">The right-hand operand expression</param>
/// <param name="Location">Source location information</param>
public sealed record CompoundAssignmentExpression(
    Expression Target,
    BinaryOperator Operator,
    Expression Value,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitCompoundAssignmentExpression(node: this);
    }
}

/// <summary>
/// Expression that combines two operands with a binary operator.
/// Supports arithmetic, comparison, logical, and bitwise operations.
/// </summary>
/// <param name="Left">Left-hand side operand expression</param>
/// <param name="Operator">Binary operator defining the operation to perform</param>
/// <param name="Right">Right-hand side operand expression</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Supports extensive operator categories:
/// <list type="bullet">
/// <item>Arithmetic: +, -, *, /, //, %, ** (with overflow variants)</item>
/// <item>Comparison: ==, !=, ===, !==, &lt;, &lt;=, &gt;, &gt;=</item>
/// <item>Logical: and, or</item>
/// <item>Membership: in, notin, is, isnot, obeys, disobeys</item>
/// <item>Bitwise: &amp;, |, ^, &lt;&lt;, &lt;&lt;?, &gt;&gt;, &lt;&lt;&lt;, &gt;&gt;&gt;</item>
/// </list>
/// </remarks>
public record BinaryExpression(
    Expression Left,
    BinaryOperator Operator,
    Expression Right,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitBinaryExpression(node: this);
    }
}

/// <summary>
/// Expression that applies a unary operator to a single operand.
/// Supports arithmetic negation, logical negation, and bitwise operations.
/// </summary>
/// <param name="Operator">Unary operator defining the operation to perform</param>
/// <param name="Operand">Expression to apply the operator to</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Supported unary operators:
/// <list type="bullet">
/// <item>Arithmetic: -x (negation)</item>
/// <item>Logical: not (logical NOT)</item>
/// <item>Bitwise: ~x (bitwise complement)</item>
/// </list>
/// </remarks>
public record UnaryExpression(UnaryOperator Operator, Expression Operand, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitUnaryExpression(node: this);
    }
}

/// <summary>
/// Expression that invokes a function or method with zero or more arguments.
/// Represents function calls, method invocations, and creator calls.
/// </summary>
/// <param name="Callee">Expression that evaluates to a callable (function, method, lambda)</param>
/// <param name="Arguments">List of argument expressions to pass to the callable</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Supports various call patterns:
/// <list type="bullet">
/// <item>Function calls: routine(a, b, c)</item>
/// <item>Method calls: me.method(x, y)</item>
/// <item>Creator calls: Point(x, y)</item>
/// <item>Lambda calls: ((x) => x + 1)(42)</item>
/// <item>Operator method calls: me.$add(you)</item>
/// </list>
/// </remarks>
public record CallExpression(
    Expression Callee,
    List<Expression> Arguments,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitCallExpression(node: this);
    }

    /// <summary>
    /// Compatibility property for tests that expect CallExpression.Name
    /// Returns the name if Callee is an IdentifierExpression
    /// </summary>
    public string? Name => Callee is IdentifierExpression id
        ? id.Name
        : null;

    /// <summary>
    /// The resolved dispatch strategy for this call, set by the semantic analyzer.
    /// Null for non-varargs calls. Buildtime or Runtime for protocol-constrained varargs.
    /// </summary>
    public SemanticAnalysis.Enums.DispatchStrategy? ResolvedDispatch { get; set; }

    /// <summary>
    /// The resolved routine for this call, set by the semantic analyzer during overload resolution.
    /// When set, the codegen should use this instead of performing its own lookup.
    /// </summary>
    public SemanticAnalysis.Symbols.RoutineInfo? ResolvedRoutine { get; set; }

    /// <summary>
    /// When true, this call is a collection literal constructor (e.g., List(1, 2, 3), Set(1, 2, 3)).
    /// Codegen should emit $create() + repeated add/add_last calls instead of a normal function call.
    /// </summary>
    public bool IsCollectionLiteral { get; set; }
}

/// <summary>
/// Named argument expression used in function calls.
/// Allows specifying argument name explicitly: func(name: value)
/// </summary>
/// <param name="Name">The parameter name</param>
/// <param name="Value">The argument value expression</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Named arguments support:
/// <list type="bullet">
/// <item>Explicit parameter binding: Error(message: "failed", code: 500)</item>
/// <item>Out-of-order arguments: Point(y: 10, x: 5)</item>
/// <item>Clarity for boolean/numeric arguments: set_visible(visible: true)</item>
/// <item>Optional parameter selection: connect(host: "localhost", port: 8080)</item>
/// </list>
/// </remarks>
public record NamedArgumentExpression(string Name, Expression Value, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitNamedArgumentExpression(node: this);
    }
}

/// <summary>
/// Expression representing a dict entry literal (key:value pair) in argument context.
/// Used in collection literal constructors: Dict(1:10, 2:20), SortedDict("a":"b")
/// </summary>
/// <param name="Key">The key expression</param>
/// <param name="Value">The value expression</param>
/// <param name="Location">Source location information</param>
public record DictEntryLiteralExpression(Expression Key, Expression Value, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitDictEntryLiteralExpression(node: this);
    }
}

/// <summary>
/// Expression representing a creator call with named member variable initializers.
/// Creates instances using parenthesis syntax: TypeName(field1: value1, field2: value2)
/// </summary>
/// <param name="TypeName">The name of the type being created</param>
/// <param name="TypeArguments">Optional generic type arguments (e.g., List&lt;T&gt;)</param>
/// <param name="MemberVariables">List of member variable name-value pairs for initialization</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Creator expression patterns:
/// <list type="bullet">
/// <item>Simple: Point(x: 10, y: 20)</item>
/// <item>Generic: TextIterator&lt;T&gt;(text: me, index: 0)</item>
/// <item>Nested: Node(value: 5, next: Node(value: 10, next: None))</item>
/// </list>
/// </remarks>
public record CreatorExpression(
    string TypeName,
    List<TypeExpression>? TypeArguments,
    List<(string Name, Expression Value)> MemberVariables,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitCreatorExpression(node: this);
    }
}

/// <summary>
/// Expression for functional update - creating a modified copy of a value with specified changes.
/// Represents the 'with' keyword for unmodifiable updates.
/// </summary>
/// <param name="Base">The base expression to copy and modify</param>
/// <param name="Updates">List of member variable/index updates (name/index, value)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// With expression patterns:
/// <list type="bullet">
/// <item>Member variable update: record with .memberVar = newValue</item>
/// <item>Multiple member variables: record with .x = 1, .y = 2</item>
/// <item>Nested member variable: record with .address.city = "NYC"</item>
/// <item>Index update: collection with [i] = newValue</item>
/// </list>
/// For member variable updates, MemberVariablePath is set and Index is null.
/// For index updates, Index is set and MemberVariablePath is null.
/// </remarks>
public record WithExpression(
    Expression Base,
    List<(List<string>? MemberVariablePath, Expression? Index, Expression Value)> Updates,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitWithExpression(node: this);
    }
}

/// <summary>
/// Expression that accesses a member (member variable, property, or method) of an object.
/// Represents the dot notation for accessing object members.
/// </summary>
/// <param name="Object">Expression that evaluates to the object containing the member</param>
/// <param name="PropertyName">Name of the member to access</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Member access patterns:
/// <list type="bullet">
/// <item>Field access: obj.memberVar</item>
/// <item>Method reference: obj.method (not a call)</item>
/// <item>Chained access: obj.child.grandchild</item>
/// </list>
/// </remarks>
public record MemberExpression(Expression Object, string PropertyName, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitMemberExpression(node: this);
    }
}

/// <summary>
/// Expression that conditionally accesses a member of an object if the object is not none.
/// Represents the ?. operator for safe navigation / optional chaining.
/// </summary>
/// <param name="Object">Expression that may evaluate to none</param>
/// <param name="PropertyName">Name of the property/member variable to access if object is not none</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Examples: obj?.memberVar, result?.value
/// If the object is none, the entire expression evaluates to none.
/// </remarks>
public record OptionalMemberExpression(Expression Object, string PropertyName, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitOptionalMemberExpression(node: this);
    }
}

/// <summary>
/// Expression that accesses an element of a collection using an index or key.
/// Represents bracket notation for array indexing and dictionary access.
/// </summary>
/// <param name="Object">Expression that evaluates to an indexable collection</param>
/// <param name="Index">Expression that evaluates to the index or key</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Index access patterns:
/// <list type="bullet">
/// <item>Array indexing: arr[0], arr[i]</item>
/// <item>Dictionary access: dict["key"]</item>
/// <item>String indexing: str[pos]</item>
/// <item>Multi-dimensional: matrix[row][col]</item>
/// </list>
/// </remarks>
public record IndexExpression(Expression Object, Expression Index, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitIndexExpression(node: this);
    }
}

/// <summary>
/// Slice expression: obj[start to end]
/// Desugars to obj.$getslice(from: start, to: end).
/// Returns Sequence&lt;T&gt; (lazy). Both bounds required, exclusive end.
/// </summary>
/// <param name="Object">The collection being sliced</param>
/// <param name="Start">Start index expression</param>
/// <param name="End">End index expression (exclusive)</param>
/// <param name="Location">Source location for error reporting</param>
public record SliceExpression(Expression Object, Expression Start, Expression End, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitSliceExpression(node: this);
    }
}

/// <summary>
/// Expression that selects between two values based on a boolean condition.
/// Represents the ternary conditional operator (if condition then true_value else false_value).
/// </summary>
/// <param name="Condition">Boolean expression to evaluate for selection</param>
/// <param name="TrueExpression">Expression to evaluate and return if condition is true</param>
/// <param name="FalseExpression">Expression to evaluate and return if condition is false</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Conditional expression behavior:
/// <list type="bullet">
/// <item>Short-circuit evaluation: only one branch is evaluated</item>
/// <item>Type compatibility: both branches must have compatible types</item>
/// <item>Nested conditionals: can be chained for complex logic</item>
/// <item>Expression context: can be used anywhere a value is expected</item>
/// </list>
/// </remarks>
public record ConditionalExpression(
    Expression Condition,
    Expression TrueExpression,
    Expression FalseExpression,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitConditionalExpression(node: this);
    }
}

/// <summary>
/// Expression representing a block that evaluates to a value.
/// The block consists of statements followed by a final expression that becomes the block's value.
/// </summary>
/// <param name="Value">The expression that the block evaluates to</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Block expressions are used for:
/// <list type="bullet">
/// <item>Inline if-else expressions: if condition then expr1 else expr2</item>
/// <item>Multi-statement computations that produce a value</item>
/// <item>Scoped variable declarations that contribute to a result</item>
/// </list>
/// </remarks>
public record BlockExpression(Expression Value, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitBlockExpression(node: this);
    }
}

/// <summary>
/// Expression that chains multiple comparison operations in a single statement.
/// Allows natural mathematical notation like a &lt; b &lt; c instead of a &lt; b and b &lt; c.
/// </summary>
/// <param name="Operands">List of expressions to compare (minimum 2 required)</param>
/// <param name="Operators">List of comparison operators between operands (one fewer than operands)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Chained comparison features:
/// <list type="bullet">
/// <item>Mathematical notation: 1 &lt; x &lt; 10, a >= b == c</item>
/// <item>Short-circuit evaluation: stops on first false comparison</item>
/// <item>Mixed operators: supports different operators in the same chain</item>
/// <item>Type checking: ensures compatible types across all comparisons</item>
/// </list>
/// </remarks>
public record ChainedComparisonExpression(
    List<Expression> Operands,
    List<BinaryOperator> Operators,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitChainedComparisonExpression(node: this);
    }
}

/// <summary>
/// Expression that creates a sequence of values between start and end bounds.
/// Supports both simple ranges and ranges with custom step values.
/// </summary>
/// <param name="Start">Expression that evaluates to the starting value of the range</param>
/// <param name="End">Expression that evaluates to the ending value of the range (inclusive)</param>
/// <param name="Step">Optional expression for the step size between values (default is 1)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Range expression variants:
/// <list type="bullet">
/// <item>Simple range: (0 to 10) creates sequence [0, 1, 2, ..., 10]</item>
/// <item>Step range: (0 to 10 by 2) creates [0, 2, 4, 6, 8, 10]</item>
/// <item>Reverse range: (10 downto 0) creates [10, 9, 8, ..., 0]</item>
/// <item>Reverse with step: (10 downto 0 by 2) creates [10, 8, 6, 4, 2, 0]</item>
/// <item>Iterable: can be used in for loops and collection operations</item>
/// </list>
/// </remarks>
public record RangeExpression(
    Expression Start,
    Expression End,
    Expression? Step,
    bool IsDescending,
    SourceLocation Location,
    bool IsExclusive = false) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitRangeExpression(node: this);
    }
}

/// <summary>
/// Expression that creates an anonymous function with parameters and a body expression.
/// Represents closures that can capture variables from their defining scope.
/// </summary>
/// <param name="Parameters">List of parameters the lambda accepts</param>
/// <param name="Body">Expression that forms the body of the lambda (returned value)</param>
/// <param name="Captures">Optional explicit captures from 'given' clause (null = implicit capture)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Lambda expression features:
/// <list type="bullet">
/// <item>Anonymous functions: (x, y) => x + y</item>
/// <item>Closure capture: can access variables from enclosing scope</item>
/// <item>Explicit captures: x given (lo, hi) => lo &lt;= x &lt; hi</item>
/// <item>Type inference: parameter and return types often inferred</item>
/// <item>Higher-order functions: can be passed as arguments or returned</item>
/// <item>Itertools: enables select, where, aggregate functions</item>
/// </list>
/// </remarks>
public record LambdaExpression(
    List<Parameter> Parameters,
    Expression Body,
    List<string>? Captures,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitLambdaExpression(node: this);
    }
}

#endregion

#region Function Call and Access Expressions

/// <summary>
/// Represents a parameter definition for functions, methods, and lambdas.
/// Includes optional type annotation and default value for flexible parameter handling.
/// </summary>
/// <param name="Name">Parameter name used for binding in the function body</param>
/// <param name="Type">Optional type annotation; if null, type is inferred from usage</param>
/// <param name="DefaultValue">Optional default value expression; allows parameter to be omitted in calls</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Parameter flexibility supports various declaration patterns:
/// <list type="bullet">
/// <item>Simple: param</item>
/// <item>Typed: param: s32</item>
/// <item>With default: param = 42</item>
/// <item>Fully specified: param: s32 = 42</item>
/// </list>
/// </remarks>
public record Parameter(
    string Name,
    TypeExpression? Type,
    Expression? DefaultValue,
    SourceLocation Location,
    bool IsVariadic = false);

#endregion

#region Type Expressions

/// <summary>
/// Expression that represents a type reference in type annotations and declarations.
/// Supports simple types, generic types, and complex type constructions.
/// </summary>
/// <param name="Name">Base type name (s32, Text, MyClass, etc.)</param>
/// <param name="GenericArguments">Optional list of type arguments for generic types</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Type expression patterns:
/// <list type="bullet">
/// <item>Primitive types: s32, f64, bool, letter</item>
/// <item>Entity types: Text, MyClass, Point</item>
/// <item>Generic types: List[s32], Dictionary[Text, s32]</item>
/// <item>Nested generics: List[Dictionary[Text, s32]]</item>
/// </list>
/// </remarks>
public record TypeExpression(
    string Name,
    List<TypeExpression>? GenericArguments,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitTypeExpression(node: this);
    }
}

/// <summary>
/// Expression that performs explicit type conversion between compatible types.
/// Supports both function-style and method-style conversion syntax.
/// </summary>
/// <param name="TargetType">Name of the target type to convert to</param>
/// <param name="Expression">Source expression to convert</param>
/// <param name="IsMethodStyle">true for method-style (x.s32!()), false for function-style (s32!(x))</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Type conversion styles:
/// <list type="bullet">
/// <item>Function-style: s32!(3.14), bool!(1)</item>
/// <item>Method-style: 3.14.s32!(), 1.bool!()</item>
/// <item>Safety: explicit conversions may fail at runtime</item>
/// <item>Checked: conversion failures throw exceptions</item>
/// </list>
/// Both styles are semantically equivalent; choice is stylistic preference.
/// </remarks>
public record TypeConversionExpression(
    string TargetType,
    Expression Expression,
    bool IsMethodStyle,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitTypeConversionExpression(node: this);
    }
}

#endregion

#region Memory Operation Expressions

/// <summary>
/// Expression representing generic method calls with type parameters.
/// Used for generic operations like read!&lt;T&gt;() and write!&lt;T&gt;().
/// </summary>
/// <param name="Object">Expression representing the object being called</param>
/// <param name="MethodName">Name of the generic method</param>
/// <param name="TypeArguments">List of type arguments for the generic method</param>
/// <param name="Arguments">List of argument expressions passed to the method</param>
/// <param name="IsMemoryOperation">Whether this method call uses ! syntax (memory operation)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Generic method calls enable type-safe slice operations:
/// <list type="bullet">
/// <item>buffer.read!&lt;s32&gt;(0) - read s32 at offset 0</item>
/// <item>buffer.write!&lt;f64&gt;(8, 3.14) - write f64 at offset 8</item>
/// <item>Type arguments specify the data type for the operation</item>
/// </list>
/// </remarks>
public record GenericMethodCallExpression(
    Expression Object,
    string MethodName,
    List<TypeExpression> TypeArguments,
    List<Expression> Arguments,
    bool IsMemoryOperation,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitGenericMethodCallExpression(node: this);
    }

    /// <summary>
    /// When true, this call is a collection literal constructor (e.g., List[S64](1, 2, 3)).
    /// Codegen should emit $create() + repeated add/add_last calls instead of a normal type constructor.
    /// </summary>
    public bool IsCollectionLiteral { get; set; }
}

/// <summary>
/// Expression representing generic member access with type parameters.
/// Used for accessing generic properties or member variables with type specification.
/// </summary>
/// <param name="Object">Expression representing the object being accessed</param>
/// <param name="MemberName">Name of the generic member</param>
/// <param name="TypeArguments">List of type arguments for the generic member</param>
/// <param name="Location">Source location information</param>
public record GenericMemberExpression(
    Expression Object,
    string MemberName,
    List<TypeExpression> TypeArguments,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitGenericMemberExpression(node: this);
    }
}

#endregion

#region Ownership Transfer Expressions

/// <summary>
/// Expression that transfers ownership of an entity (RazorForge only).
/// The steal keyword moves ownership from the source to the destination,
/// invalidating the source (it becomes a deadref).
/// </summary>
/// <param name="Operand">Expression being stolen from (must be stealable)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Steal expression rules:
/// <list type="bullet">
/// <item>Can steal: raw entities, Shared&lt;T&gt;, Tracked&lt;T&gt;</item>
/// <item>Cannot steal: Viewed&lt;T&gt;, Hijacked&lt;T&gt;, Inspected&lt;T&gt;, Seized&lt;T&gt;, Snatched&lt;T&gt;</item>
/// <item>After stealing, source becomes deadref (using it is a build error)</item>
/// <item>Used for: ownership transfer (steal node), container push (list.push(steal node)),
/// and consuming iteration (for item in steal list)</item>
/// </list>
/// </remarks>
public record StealExpression(Expression Operand, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitStealExpression(node: this);
    }
}

#endregion

#region Async Expressions

/// <summary>
/// Expression that waits for a suspended computation to complete.
/// Used in suspended/threaded routines to await Task-like values.
/// </summary>
/// <param name="Operand">The suspended computation to wait for</param>
/// <param name="Timeout">Optional timeout duration (with 'within' keyword)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Waitfor expression examples:
/// <list type="bullet">
/// <item>waitfor task - wait for task to complete</item>
/// <item>waitfor task within 5s - wait with 5 second timeout</item>
/// <item>waitfor http.get(url) within 30s - wait for HTTP request with timeout</item>
/// </list>
/// Waitfor can only be used inside suspended/threaded routines.
/// </remarks>
public record WaitforExpression(Expression Operand, Expression? Timeout, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitWaitforExpression(node: this);
    }
}

/// <summary>
/// Represents a single task dependency in an 'after' clause.
/// Specifies a Lookup&lt;T&gt; variable to depend on and an optional binding name.
/// </summary>
/// <param name="DependencyExpr">The Lookup&lt;T&gt; variable to depend on</param>
/// <param name="BindingName">Optional 'as' binding name for the unwrapped value (null if no 'as')</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Task dependency examples:
/// <list type="bullet">
/// <item>after a - dependency without value binding (ordering only)</item>
/// <item>after a as val - dependency with value binding</item>
/// <item>after (a, b) as (va, vb) - tuple dependencies with bindings</item>
/// </list>
/// The binding name introduces a variable of type T (unwrapped from Lookup&lt;T&gt;)
/// that is only valid within the associated waitfor expression.
/// </remarks>
public record TaskDependency(Expression DependencyExpr, string? BindingName, SourceLocation Location);

/// <summary>
/// Expression that waits for a suspended computation with explicit task dependencies.
/// Extends waitfor with 'after' clause for declarative task dependency graphs.
/// </summary>
/// <param name="Dependencies">List of 'after' dependencies (empty means no dependencies)</param>
/// <param name="Operand">The suspended computation to wait for</param>
/// <param name="Timeout">Optional timeout duration (with 'within' keyword)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Dependent waitfor expression examples:
/// <list type="bullet">
/// <item>after a as val waitfor step2!(val) within 5s - single dependency with timeout</item>
/// <item>after (a, b) as (va, vb) waitfor step3!(va, vb) - multiple dependencies</item>
/// <item>after a waitfor step2!() - ordering-only dependency (no value binding)</item>
/// </list>
/// If any dependency fails (error/None), dependent tasks are not spawned (short-circuit).
/// The result type is Lookup&lt;R&gt; where R is the return type of the awaited task.
/// </remarks>
public record DependentWaitforExpression(
    List<TaskDependency> Dependencies,
    Expression Operand,
    Expression? Timeout,
    SourceLocation Location
) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitDependentWaitforExpression(node: this);
    }
}

#endregion

#region Back Index Expressions

/// <summary>
/// Expression that creates an index from the end of a sequence.
/// The ^ prefix operator creates a back-index value that counts from the end.
/// </summary>
/// <param name="Operand">The index offset from the end (^1 = last, ^2 = second to last)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Back index expression examples:
/// <list type="bullet">
/// <item>^1 - last element</item>
/// <item>^2 - second to last element</item>
/// <item>list[^1] - access last element of list</item>
/// <item>text[^3] - access third to last character</item>
/// </list>
/// The operand must be a non-negative integer. ^0 is valid but refers to "one past the end".
/// Used with IndexExpression for end-relative indexing.
/// </remarks>
public record BackIndexExpression(Expression Operand, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitBackIndexExpression(node: this);
    }
}

#endregion

#region Pattern Matching Expressions

/// <summary>
/// Expression that performs pattern matching with 'is' or 'isnot' operators.
/// Supports type checking, variable binding, and destructuring.
/// </summary>
/// <param name="Expression">The expression being matched against the pattern</param>
/// <param name="Pattern">The pattern to match (TypePattern, DestructuringPattern, etc. from Statements.cs)</param>
/// <param name="IsNegated">True for 'isnot', false for 'is'</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Pattern matching expression examples:
/// <list type="bullet">
/// <item>value is s32 - type check</item>
/// <item>value is Text t - type check with binding</item>
/// <item>value is Point (x, y) - destructuring</item>
/// <item>value isnot None - negated type check</item>
/// </list>
/// Note: 'isnot' cannot have bindings or destructuring (what would you bind to if not matched?).
/// </remarks>
public record IsPatternExpression(
    Expression Expression,
    Pattern Pattern,
    bool IsNegated,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitIsPatternExpression(node: this);
    }
}

/// <summary>Flags test kind: is (has flags), isnot (does not have flags), isonly (exact match)</summary>
public enum FlagsTestKind { Is, IsNot, IsOnly }

/// <summary>Connective for combining flag names in a flags test</summary>
public enum FlagsTestConnective { And, Or }

/// <summary>
/// Expression that tests whether a flags value has, lacks, or exactly matches specific flags.
/// </summary>
/// <param name="Subject">The expression being tested (must be a flags type)</param>
/// <param name="Kind">The kind of test: is, isnot, or isonly</param>
/// <param name="TestFlags">The flag names being tested for</param>
/// <param name="Connective">How the flags are combined: and (all required) or or (any required)</param>
/// <param name="ExcludedFlags">Optional flags to exclude with 'but' (only valid with And connective)</param>
/// <param name="Location">Source location information</param>
public record FlagsTestExpression(
    Expression Subject,
    FlagsTestKind Kind,
    List<string> TestFlags,
    FlagsTestConnective Connective,
    List<string>? ExcludedFlags,
    SourceLocation Location) : Expression(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitFlagsTestExpression(node: this);
    }
}

/// <summary>
/// Pattern matching expression that evaluates to a value based on pattern matching.
/// Similar to Rust's match expression or Kotlin's when expression.
/// </summary>
/// <param name="Expression">The value being matched against patterns</param>
/// <param name="Clauses">The list of pattern-body pairs to evaluate</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// When expression examples:
/// <list type="bullet">
/// <item>return when x { 0 => "zero", 1 => "one", else => "many" }</item>
/// <item>var desc = when status { is ACTIVE => "Running", else => "Not running" }</item>
/// </list>
/// The body of each clause must evaluate to a value of the same type.
/// </remarks>
public record WhenExpression(
    Expression? Expression,
    List<WhenClause> Clauses,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitWhenExpression(node: this);
    }
}

#endregion
