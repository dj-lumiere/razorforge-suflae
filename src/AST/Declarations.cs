namespace SyntaxTree;

#region Base Declaration Types

/// <summary>
/// Base entity for all declaration nodes in the AST.
/// Declarations introduce new names into scopes and define program structure.
/// </summary>
/// <param name="Location">Source location information for error reporting and debugging</param>
/// <remarks>
/// Declarations form the structural backbone of programs:
/// <list type="bullet">
/// <item>Variables: local and global state</item>
/// <item>Functions: executable procedures and routines</item>
/// <item>Types: classes, structs, enums, variants, traits</item>
/// <item>Modules: imports and module organization</item>
/// </list>
/// </remarks>
public abstract record Declaration(SourceLocation Location) : AstNode(Location: Location);

/// <summary>
/// Empty placeholder declaration used inside type bodies.
/// Equivalent to Python's 'pass' statement but as a declaration.
/// </summary>
/// <param name="Location">Source location information</param>
public record PassDeclaration(SourceLocation Location) : Declaration(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        // PassDeclaration is a no-op, just return default
        return default!;
    }
}

/// <summary>
/// Generic constraint declaration for type parameters.
/// Specifies requirements that generic type arguments must satisfy.
/// </summary>
/// <param name="ParameterName">The name of the generic type parameter (e.g., "T")</param>
/// <param name="ConstraintType">The type of constraint (obeys, record, entity)</param>
/// <param name="ConstraintTypes">List of types that the parameter must satisfy</param>
/// <remarks>
/// Supports various constraint syntaxes:
/// <list type="bullet">
/// <item>Protocol constraint: where T obeys Comparable</item>
/// <item>Value type constraint: where T is record</item>
/// <item>Reference type constraint: where T is entity</item>
/// <item>Resident type constraint: where T is resident</item>
/// </list>
/// </remarks>
public record GenericConstraintDeclaration(
    string ParameterName,
    ConstraintKind ConstraintType,
    List<TypeExpression>? ConstraintTypes = null,
    SourceLocation Location = default);


#endregion

#region Variable and Function Declarations

/// <summary>
/// Variable declaration: var name: Type = initializer
/// Introduces a new variable binding in the current scope.
/// </summary>
/// <param name="Name">Variable identifier name</param>
/// <param name="Type">Optional type annotation; if null, type is inferred from initializer</param>
/// <param name="Initializer">Optional initial value expression</param>
/// <param name="Visibility">Access control modifier (public, published, internal, private)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Variable declarations support various patterns:
/// <list type="bullet">
/// <item>Type inference: var x = 42 (infers s64 for RazorForge/Integer for Suflae)</item>
/// <item>Explicit typing: var x: s64 = 42</item>
/// <item>Uninitialized (requires type): var x: s32</item>
/// <item>Published: published var x = 42 (public read, private write)</item>
/// </list>
///
/// Visibility levels:
/// <list type="bullet">
/// <item>public - read/write from anywhere</item>
/// <item>published - public read, private write</item>
/// <item>internal - read/write within module</item>
/// <item>private - read/write within file</item>
/// </list>
/// </remarks>
public record VariableDeclaration(
    string Name,
    TypeExpression? Type,
    Expression? Initializer,
    VisibilityModifier Visibility,
    SourceLocation Location,
    StorageClass Storage = StorageClass.None) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitVariableDeclaration(node: this);
    }
}

/// <summary>
/// Function/routine declaration that defines executable code blocks.
/// Represents both traditional functions and RazorForge "routines".
/// </summary>
/// <param name="Name">Function identifier name</param>
/// <param name="Parameters">List of parameter definitions</param>
/// <param name="ReturnType">Optional return type; null for void/procedure functions</param>
/// <param name="Body">Function body statement (typically a BlockStatement)</param>
/// <param name="Visibility">Access control modifier</param>
/// <param name="Annotations">Decorators like @inline for properties</param>
/// <param name="Location">Source location information</param>
/// <param name="GenericParameters">Optional list of generic type parameter names</param>
/// <remarks>
/// Function declarations support:
/// <list type="bullet">
/// <item>Generic functions: routine sort[T](items: List[T])</item>
/// <item>Default parameters: routine greet(name: text = "World")</item>
/// <item>Type inference: parameters and return types can be inferred</item>
/// <item>Annotations: @inline,etc.</item>
/// <item>Visibility: public, private, internal</item>
/// <item>Suspended: async function that can use waitfor</item>
/// </list>
/// </remarks>
public record RoutineDeclaration(
    string Name,
    List<Parameter> Parameters,
    TypeExpression? ReturnType,
    Statement Body,
    VisibilityModifier Visibility,
    List<string> Annotations, // For decorators like @inline, @readonly
    SourceLocation Location,
    List<string>? GenericParameters = null,
    List<GenericConstraintDeclaration>? GenericConstraints = null,
    bool IsFailable = false,
    StorageClass Storage = StorageClass.None,
    AsyncStatus Async = AsyncStatus.None,
    bool IsDangerous = false)
    : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitFunctionDeclaration(node: this);
    }
}

#endregion

#region Type Declarations

/// <summary>
/// Entity (class) declaration that defines reference types with inheritance.
/// Represents object-oriented classes with member variables, methods, and inheritance.
/// </summary>
/// <param name="Name">Class identifier name</param>
/// <param name="GenericParameters">Optional list of generic type parameter names</param>
/// <param name="Protocols">List of protocols to implement (obeys)</param>
/// <param name="Members">List of member declarations (member variables, methods, properties)</param>
/// <param name="Visibility">Access control modifier</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Entity declarations support:
/// <list type="bullet">
/// <item>Single protocol implementation: entity Dog obeys Animal</item>
/// <item>Multiple protocol implementation: entity Dog obeys Nameable, Trainable</item>
/// <item>Generic classes: entity Container[T]</item>
/// <item>Member visibility: public, private, internal member variables/methods</item>
/// <item>Creators: defined as special routine methods</item>
/// </list>
/// </remarks>
public record EntityDeclaration(
    string Name,
    List<string>? GenericParameters,
    List<TypeExpression> Protocols,
    List<Declaration> Members,
    VisibilityModifier Visibility,
    SourceLocation Location,
    List<GenericConstraintDeclaration>? GenericConstraints = null,
    bool HasPassBody = false)
    : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitEntityDeclaration(node: this);
    }
}

/// <summary>
/// Record (struct) declaration that defines value types with structural equality.
/// Represents data structures with value semantics.
/// </summary>
/// <param name="Name">Record identifier name</param>
/// <param name="GenericParameters">Optional list of generic type parameter names</param>
/// <param name="Protocols">List of protocols to implement (obeys)</param>
/// <param name="Members">List of member declarations (member variables and methods)</param>
/// <param name="Visibility">Access control modifier</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Record declarations provide:
/// <list type="bullet">
/// <item>Value semantics: equality based on member variable values, not identity</item>
/// <item>Immutability: member variables are always readonly</item>
/// <item>Stack allocation: stored inline rather than heap-allocated</item>
/// <item>Generic records: record Point[T](x: T, y: T)</item>
/// <item>Copy creators: automatic with-expressions support</item>
/// </list>
/// </remarks>
public record RecordDeclaration(
    string Name,
    List<string>? GenericParameters,
    List<TypeExpression> Protocols,
    List<Declaration> Members,
    VisibilityModifier Visibility,
    SourceLocation Location,
    List<GenericConstraintDeclaration>? GenericConstraints = null,
    bool HasPassBody = false,
    List<string>? Annotations = null) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitRecordDeclaration(node: this);
    }
}

/// <summary>
/// Resident declaration for permanent fixed-size reference types.
/// Residents combine record's fixed size with entity's reference semantics.
/// </summary>
/// <param name="Name">Resident identifier name</param>
/// <param name="GenericParameters">Optional list of generic type parameter names</param>
/// <param name="Protocols">List of protocols to implement (obeys)</param>
/// <param name="Members">Field and method declarations within the resident</param>
/// <param name="Visibility">Access control modifier</param>
/// <param name="Location">Source location information</param>
/// <param name="GenericConstraints">Optional generic type constraints (where clause)</param>
/// <remarks>
/// Residents are permanent, foundational objects with:
/// <list type="bullet">
/// <item>Fixed size at build time (like records)</item>
/// <item>Reference semantics (like entities)</item>
/// <item>Internal mutability (like entities)</item>
/// <item>Persistent memory allocation</item>
/// <item>Stable memory addresses (no moving GC)</item>
/// </list>
/// </remarks>
public record ResidentDeclaration(
    string Name,
    List<string>? GenericParameters,
    List<TypeExpression> Protocols,
    List<Declaration> Members,
    VisibilityModifier Visibility,
    SourceLocation Location,
    List<GenericConstraintDeclaration>? GenericConstraints = null,
    bool HasPassBody = false) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitResidentDeclaration(node: this);
    }
}

/// <summary>
/// Choice declaration that defines discriminated unions of named constants.
/// Represents enumeration types with optional associated values.
/// </summary>
/// <param name="Name">Choice identifier name</param>
/// <param name="Cases">List of choice variant definitions with optional values</param>
/// <param name="Methods">List of methods that can be called on choice values</param>
/// <param name="Visibility">Access control modifier</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Choice declarations support:
/// <list type="bullet">
/// <item>Simple enums: choice Status { Ok, Error, Pending }</item>
/// <item>Explicit values: choice HttpCode { Ok = 200, NotFound = 404 }</item>
/// <item>Methods: choices can have associated behavior</item>
/// <item>Pattern matching: used with when statements</item>
/// </list>
/// </remarks>
public record ChoiceDeclaration(
    string Name,
    List<ChoiceCase> Cases,
    List<RoutineDeclaration> Methods,
    VisibilityModifier Visibility,
    SourceLocation Location) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitChoiceDeclaration(node: this);
    }
}

/// <summary>
/// Flags declaration that defines combinable bitflag sets.
/// Each member represents a power-of-two bit flag backed by U64.
/// </summary>
/// <param name="Name">Flags type identifier name</param>
/// <param name="Members">List of UPPER_IDENTIFIER member names (builder assigns bit positions)</param>
/// <param name="Visibility">Access control modifier</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Flags declarations support:
/// <list type="bullet">
/// <item>Combining: perms = READ or WRITE</item>
/// <item>Testing: perms is READ</item>
/// <item>Exact match: perms isonly READ and WRITE</item>
/// <item>Removal: perms but WRITE</item>
/// <item>Max 64 members (U64 backing)</item>
/// </list>
/// </remarks>
public record FlagsDeclaration(
    string Name,
    List<string> Members,
    VisibilityModifier Visibility,
    SourceLocation Location) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitFlagsDeclaration(node: this);
    }
}

/// <summary>
/// Variant declaration that defines tagged unions with multiple possible value types.
/// Represents algebraic data types with multiple cases and associated data.
/// All member variables must be records or memory handles - safe, no danger! needed.
/// </summary>
/// <param name="Name">Variant identifier name</param>
/// <param name="GenericParameters">Optional list of generic type parameter names</param>
/// <param name="Cases">List of variant cases with associated types</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Variant declarations enable powerful type-safe unions:
/// <list type="bullet">
/// <item>Tagged unions: variant MyResult[T, E] { SUCCESS(T), TIMEOUT, ERROR(E) }</item>
/// <item>Pattern matching: exhaustive case analysis with when statements</item>
/// <item>Generic variants: variant MyOption[T] { OPTION1(T), OPTION2 }</item>
/// <item>Associated data: each case can carry different typed data</item>
/// </list>
/// </remarks>
public record VariantDeclaration(
    string Name,
    List<string>? GenericParameters,
    List<VariantCase> Cases,
    SourceLocation Location,
    List<GenericConstraintDeclaration>? GenericConstraints = null)
    : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitVariantDeclaration(node: this);
    }
}

/// <summary>
/// Protocol (trait/interface) declaration that defines behavioral contracts.
/// Specifies method signatures that implementing types must provide.
/// </summary>
/// <param name="Name">Protocol identifier name</param>
/// <param name="GenericParameters">Optional list of generic type parameter names</param>
/// <param name="ParentProtocols">List of parent protocols this protocol extends (obeys)</param>
/// <param name="Methods">List of method signatures (without implementations)</param>
/// <param name="Visibility">Access control modifier</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Protocol declarations enable polymorphism and code reuse:
/// <list type="bullet">
/// <item>Interface contracts: protocol Drawable { routine Me.draw() }</item>
/// <item>Generic protocols: protocol Comparable[T] { routine Me.__cmp__(you: Me) -> ComparisonSign }</item>
/// <item>Multiple implementation: types can implement multiple protocols</item>
/// <item>Default methods: protocols can provide default implementations</item>
/// <item>Protocol bounds: generic constraints (where T obeys Comparable)</item>
/// <item>Protocol inheritance: protocol DetailedPrintable obeys Printable { ... }</item>
/// </list>
/// </remarks>
public record ProtocolDeclaration(
    string Name,
    List<string>? GenericParameters,
    List<TypeExpression> ParentProtocols,
    List<RoutineSignature> Methods,
    VisibilityModifier Visibility,
    SourceLocation Location,
    List<GenericConstraintDeclaration>? GenericConstraints = null
    ) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitProtocolDeclaration(node: this);
    }
}

#endregion

#region Import and Module Declarations

/// <summary>
/// Module declaration that establishes the module path for all symbols in a file.
/// Must appear at the top of a source file, before any other declarations.
/// </summary>
/// <param name="Path">Slash-separated module path (e.g., "Standard/Errors")</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Module declarations establish module identity:
/// <list type="bullet">
/// <item>Module path: module Standard/Errors</item>
/// <item>Nested modules: module Company/Project/Utils</item>
/// <item>Symbol qualification: types become fully qualified as Path.TypeName</item>
/// <item>Import resolution: other files import this module by path</item>
/// </list>
/// </remarks>
public record ModuleDeclaration(string Path, SourceLocation Location)
    : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitModuleDeclaration(node: this);
    }
}

/// <summary>
/// Import declaration that brings external modules and symbols into scope.
/// Enables code organization and dependency management.
/// </summary>
/// <param name="ModulePath">Slash-separated module path (e.g., "Standard/Collections/List")</param>
/// <param name="Alias">Optional alias for imported module (as collections)</param>
/// <param name="SpecificImports">Optional list of specific symbols to import ([List, Dict])</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Import declarations support various patterns:
/// <list type="bullet">
/// <item>Full module: import Standard/Collections</item>
/// <item>With alias: import Standard/Collections as col</item>
/// <item>Specific items: import Standard/Collections.[List, Dict]</item>
/// <item>Nested modules: import Company/Project/Utils/Helpers</item>
/// </list>
/// </remarks>
public record ImportDeclaration(
    string ModulePath,
    string? Alias, // as alias
    List<string>? SpecificImports, // [item1, item2]
    SourceLocation Location) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitImportDeclaration(node: this);
    }
}

#endregion

#region Supporting Types and Enums

/// <summary>
/// Specifies the async execution model for a routine.
/// </summary>
public enum AsyncStatus
{
    /// <summary>Regular synchronous routine</summary>
    None,

    /// <summary>
    /// Lightweight async (suspended) - cooperative multitasking using green threads.
    /// Best for I/O-bound operations. Uses waitfor to await results.
    /// </summary>
    Suspended,

    /// <summary>
    /// OS-level threading for CPU-bound parallel work.
    /// Uses real threads with higher overhead but true parallelism.
    /// </summary>
    Threaded
}

/// <summary>
/// Value assigning used within Choice (enum) declarations.
/// Represents a single named constant with optional explicit value.
/// </summary>
/// <param name="Name">Variant identifier name</param>
/// <param name="Value">Optional explicit integer value (e.g., OK: 200)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Choice can have explicit or implicit values:
/// <list type="bullet">
/// <item>Implicit: variants are numbered 0, 1, 2, ...</item>
/// <item>Explicit: OK: 200, ERROR: 404</item>
/// <item>Mixed: Some explicit, some implicit (increment from last)</item>
/// </list>
/// </remarks>
public record ChoiceCase(
    string Name,
    Expression? Value, // For explicit values like OK: 200 - semantic analyzer converts
    SourceLocation Location);

/// <summary>
/// Variant case definition used within Variant declarations.
/// Represents one possible case in a tagged union with associated type data.
/// </summary>
/// <param name="Name">Case identifier name</param>
/// <param name="AssociatedTypes">Optional list of types associated with this case</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Variant cases support associated data:
/// <list type="bullet">
/// <item>No data: NONE (unit case)</item>
/// <item>Single type: SINGLE(T) (single associated value)</item>
/// </list>
/// </remarks>
public record VariantCase(
    string Name,
    TypeExpression? AssociatedTypes,
    SourceLocation Location);

/// <summary>
/// Routine (function) signature used within Protocol (trait) declarations.
/// Specifies method contract without implementation.
/// </summary>
/// <param name="Name">Method identifier name</param>
/// <param name="Parameters">List of parameter definitions</param>
/// <param name="ReturnType">Optional return type; null for void methods</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Function signatures define the contract that implementers must fulfill:
/// <list type="bullet">
/// <item>Abstract methods: no body, just signature</item>
/// <item>Parameter names: used for documentation and named arguments</item>
/// <item>Type constraints: can include generic bounds</item>
/// <item>Default implementations: traits can provide default bodies</item>
/// </list>
/// </remarks>
public record RoutineSignature(
    string Name,
    List<Parameter> Parameters,
    TypeExpression? ReturnType,
    List<string>? Annotations,
    SourceLocation Location);

#endregion

#region Advanced Declarations

/// <summary>
/// Define declaration that creates an alias for an existing identifier.
/// Allows renaming symbols for disambiguation or clarity.
/// </summary>
/// <param name="OldName">Original identifier name to alias</param>
/// <param name="NewName">New identifier name to use</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Redefinition enables:
/// <list type="bullet">
/// <item>Disambiguation: define standard/List as StdList</item>
/// <item>Migration: gradually rename APIs</item>
/// <item>Backward compatibility: keep old names working</item>
/// </list>
/// </remarks>
public record DefineDeclaration(string OldName, string NewName, SourceLocation Location)
    : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitDefineDeclaration(node: this);
    }
}

/// <summary>
/// Preset declaration that defines a build-time constant value.
/// Similar to const in other languages but with RazorForge naming convention.
/// </summary>
/// <param name="Name">Constant identifier name</param>
/// <param name="Type">Type of the constant value</param>
/// <param name="Value">Constant expression that must be evaluable at build time</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Preset declarations provide build-time constants:
/// <list type="bullet">
/// <item>Simple constants: preset PI: f64 = 3.14159</item>
/// <item>Integer constants: preset MAX_SIZE: u32 = 1024u32</item>
/// <item>Pointer constants: preset cnullptr: uaddr = 0uaddr</item>
/// <item>Must be evaluable at build time</item>
/// </list>
/// </remarks>
public record PresetDeclaration(
    string Name,
    TypeExpression Type,
    Expression Value,
    SourceLocation Location) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitPresetDeclaration(node: this);
    }
}

/// <summary>
/// External function declaration that links to native runtime functions.
/// Used for declaring functions implemented in C or other native languages.
/// </summary>
/// <param name="Name">Name of the external function</param>
/// <param name="GenericParameters">Generic type parameters if the function is generic</param>
/// <param name="Parameters">Function parameters with types</param>
/// <param name="ReturnType">Return type of the function (null for void)</param>
/// <param name="CallingConvention">Calling convention ("C", "stdcall", "fastcall", etc.)</param>
/// <param name="IsVariadic">Whether the function accepts variable arguments (like C's printf with "...")</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// External declarations link RazorForge to native runtime:
/// <list type="bullet">
/// <item>external("C") routine malloc(size: uaddr) -> cptr&lt;cvoid&gt;</item>
/// <item>external("C") routine free(ptr: cptr&lt;cvoid&gt;)</item>
/// <item>external routine heap_alloc!(bytes: uaddr) -> uaddr (default C convention)</item>
/// <item>No function body - implementation provided by native runtime</item>
/// <item>Links to C functions at build time</item>
/// </list>
/// </remarks>
public record ExternalDeclaration(
    string Name,
    List<string>? GenericParameters,
    List<GenericConstraintDeclaration>? GenericConstraints,
    List<Parameter> Parameters,
    TypeExpression? ReturnType,
    string? CallingConvention,
    bool IsVariadic,
    List<string>? Annotations,
    bool IsDangerous,
    SourceLocation Location) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitExternalDeclaration(node: this);
    }
}

/// <summary>
/// Block form of external declarations grouping multiple routines under one calling convention.
/// <code>external("C") { routine foo() routine bar() }</code>
/// </summary>
public record ExternalBlockDeclaration(
    List<Declaration> Declarations,
    SourceLocation Location) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitExternalBlockDeclaration(node: this);
    }
}

#endregion
