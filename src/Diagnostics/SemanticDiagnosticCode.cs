namespace SemanticAnalysis.Diagnostics;

/// <summary>
/// Semantic diagnostic codes for RazorForge (RF-S prefix).
/// Covers semantic analysis errors including type checking, scope resolution,
/// and language-specific validation.
///
/// Code ranges:
/// - RF-S001-RF-S049: Literal and Identifier Errors
/// - RF-S050-RF-S099: Binary and Unary Operator Errors
/// - RF-S100-RF-S149: Type Resolution Errors
/// - RF-S150-RF-S199: Generic and Constraint Errors
/// - RF-S200-RF-S249: Statement Errors
/// - RF-S250-RF-S299: Assignment and Mutability Errors
/// - RF-S300-RF-S349: Control Flow and Return Errors
/// - RF-S350-RF-S399: Pattern Matching Errors
/// - RF-S400-RF-S449: Declaration and Visibility Errors
/// - RF-S450-RF-S499: Member Access Errors
/// - RF-S500-RF-S549: Call and Argument Errors
/// - RF-S550-RF-S599: Collection Literal Errors
/// - RF-S600-RF-S649: Memory Token Errors (RazorForge-specific)
/// - RF-S650-RF-S699: Modification Inference Errors
/// - RF-S700-RF-S749: Protocol Conformance Errors
/// - RF-S750-RF-S799: Error Handling Errors (throw/absent)
/// - RF-S800-RF-S849: Language Restriction Errors
/// - RF-S850-RF-S899: Intrinsic and Native Call Errors
/// - RF-S900-RF-S949: Concurrency and Task Dependency Errors
/// </summary>
public enum SemanticDiagnosticCode
{
    // ═══════════════════════════════════════════════════════════════════════════
    // LITERAL AND IDENTIFIER ERRORS (RF-S001 - RF-S049)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Unknown literal type encountered during parsing.</summary>
    UnknownLiteralType = 1,

    /// <summary>Type referenced in literal is not defined in the type registry.</summary>
    LiteralTypeNotDefined = 2,

    /// <summary>Failed to parse a numeric literal using native parsing library.</summary>
    NumericLiteralParseFailed = 3,

    /// <summary>Invalid Integer literal format.</summary>
    InvalidIntegerLiteral = 4,

    /// <summary>Invalid Decimal literal format.</summary>
    InvalidDecimalLiteral = 5,

    /// <summary>'me' keyword used outside a type method context.</summary>
    MeOutsideTypeMethod = 6,

    /// <summary>Referenced identifier is not declared in the current scope.</summary>
    UnknownIdentifier = 7,

    /// <summary>Identifier shadows another identifier in outer scope.</summary>
    IdentifierShadowing = 8,

    /// <summary>Variable with this name already declared in the same scope.</summary>
    VariableRedeclaration = 9,

    /// <summary>Integer literal value overflows the target type's range.</summary>
    IntegerLiteralOverflow = 10,

    /// <summary>Float literal value overflows the target type's range.</summary>
    FloatLiteralOverflow = 11,

    /// <summary>Duration literal value overflows (exceeds maximum representable duration).</summary>
    DurationLiteralOverflow = 12,

    /// <summary>Byte size literal value overflows (exceeds maximum representable size).</summary>
    ByteSizeLiteralOverflow = 13,

    /// <summary>Failed to parse imaginary literal for complex number.</summary>
    ImaginaryLiteralParseFailed = 14,

    /// <summary>Invalid base prefix in numeric literal (expected 0x, 0o, or 0b).</summary>
    InvalidNumericBase = 15,

    // ═══════════════════════════════════════════════════════════════════════════
    // BINARY AND UNARY OPERATOR ERRORS (RF-S050 - RF-S099)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Logical operator (and/or) requires boolean operands.</summary>
    LogicalOperatorRequiresBool = 50,

    /// <summary>Logical 'not' operator requires a boolean operand.</summary>
    LogicalNotRequiresBool = 55,

    /// <summary>Negation operator requires a numeric operand.</summary>
    NegationRequiresNumeric = 56,

    /// <summary>Bitwise 'not' operator requires an integer operand.</summary>
    BitwiseNotRequiresInteger = 57,

    /// <summary>Identity operator (===, !==) only works with entity types.</summary>
    IdentityOperatorRequiresReference = 58,

    /// <summary>Comparison operator cannot be used with variant types.</summary>
    ComparisonOnVariantType = 59,

    /// <summary>Cannot compare values of incompatible types.</summary>
    IncompatibleComparisonTypes = 60,

    /// <summary>Type does not support ordering comparisons.</summary>
    OrderingNotSupported = 61,

    /// <summary>The '!=' operator cannot be used in comparison chains.</summary>
    NotEqualInComparisonChain = 62,

    /// <summary>Cannot mix ascending and descending operators in a comparison chain.</summary>
    MixedComparisonChainDirection = 63,

    /// <summary>BackIndex operator '^' requires an integer operand.</summary>
    BackIndexRequiresInteger = 64,

    /// <summary>Binary operator method not found on type.</summary>
    BinaryOperatorNotFound = 65,

    /// <summary>Unary operator method not found on type.</summary>
    UnaryOperatorNotFound = 66,

    /// <summary>Waitfor 'within' clause requires a Duration type.</summary>
    WaitforTimeoutNotDuration = 67,

    /// <summary>Real-to-Complex promotion only allowed for addition and subtraction.</summary>
    RealComplexPromotionInvalid = 68,

    /// <summary>BackIndex (^n) operator only valid in subscript/slice context.</summary>
    BackIndexOutsideSubscript = 69,

    /// <summary>Slice must have both bounds, no step, and produces a read-only view.</summary>
    SliceInvalidUsage = 70,

    // ═══════════════════════════════════════════════════════════════════════════
    // TYPE RESOLUTION ERRORS (RF-S100 - RF-S149)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Referenced type is not defined.</summary>
    UnknownType = 100,

    /// <summary>Type is not a generic type but was used with type arguments.</summary>
    TypeNotGeneric = 101,

    /// <summary>Wrong number of type arguments provided for generic type.</summary>
    WrongTypeArgumentCount = 102,

    /// <summary>Type not found during conversion expression.</summary>
    UnknownConversionTargetType = 103,

    /// <summary>Only protocols can be used with 'obeys' keyword.</summary>
    NotAProtocol = 104,

    /// <summary>Cannot resolve import - module not found.</summary>
    ModuleNotFound = 105,

    /// <summary>Circular type reference detected.</summary>
    CircularTypeReference = 106,

    /// <summary>Module cannot import itself.</summary>
    SelfImport = 107,

    /// <summary>Circular import detected between modules.</summary>
    CircularImport = 108,

    /// <summary>Source file not found.</summary>
    SourceFileNotFound = 109,

    /// <summary>Cannot import file from different language.</summary>
    LanguageMismatch = 110,

    /// <summary>Parse error during building.</summary>
    ParseError = 111,

    /// <summary>General build error.</summary>
    CompilationError = 112,

    /// <summary>Two imports expose the same symbol name in the current scope.</summary>
    ImportNameCollision = 113,

    /// <summary>Import declarations must appear before other declarations in a file.</summary>
    ImportPositionViolation = 114,

    /// <summary>Cannot import non-open symbol from another module.</summary>
    ImportNonOpenSymbol = 115,

    // ═══════════════════════════════════════════════════════════════════════════
    // GENERIC AND CONSTRAINT ERRORS (RF-S150 - RF-S199)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Type does not implement required protocol from constraint.</summary>
    ProtocolConstraintViolation = 150,

    /// <summary>Type is not a value type (record) as required by constraint.</summary>
    ValueTypeConstraintViolation = 151,

    /// <summary>Type is not a reference type (entity) as required by constraint.</summary>
    ReferenceTypeConstraintViolation = 152,

    /// <summary>Type is not a routine type as required by constraint.</summary>
    RoutineTypeConstraintViolation = 154,

    /// <summary>Type is not a choice type as required by constraint.</summary>
    ChoiceTypeConstraintViolation = 155,

    /// <summary>Type is not a variant type as required by constraint.</summary>
    VariantTypeConstraintViolation = 156,

    /// <summary>Invalid const generic type.</summary>
    InvalidConstGenericType = 158,

    /// <summary>Const generic type mismatch.</summary>
    ConstGenericTypeMismatch = 159,

    /// <summary>Type is not in the allowed set for type equality constraint.</summary>
    TypeEqualityConstraintViolation = 160,

    /// <summary>Type does not satisfy 'from' constraint.</summary>
    FromConstraintViolation = 161,

    /// <summary>Type parameter in constraint is not declared on the type or function.</summary>
    UnknownTypeParameterInConstraint = 163,

    // ═══════════════════════════════════════════════════════════════════════════
    // STATEMENT ERRORS (RF-S200 - RF-S249)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Variable declaration requires type annotation or initializer.</summary>
    VariableNeedsTypeOrInitializer = 200,

    /// <summary>Cannot assign initializer type to declared variable type.</summary>
    VariableInitializerTypeMismatch = 201,

    /// <summary>If condition must be boolean.</summary>
    IfConditionNotBool = 203,

    /// <summary>While condition must be boolean.</summary>
    WhileConditionNotBool = 204,

    /// <summary>Type is not iterable for for-loop (does not follow Iterable protocol).</summary>
    TypeNotIterable = 205,

    /// <summary>Unknown statement type encountered.</summary>
    UnknownStatementType = 206,

    /// <summary>Break statement outside of loop.</summary>
    BreakOutsideLoop = 207,

    /// <summary>Continue statement outside of loop.</summary>
    ContinueOutsideLoop = 208,

    /// <summary>Using statement requires disposable type.</summary>
    UsingRequiresDisposable = 209,

    /// <summary>Warning: Routine call's return value is unused. Use 'discard' to explicitly ignore it.</summary>
    UnusedReturnValue = 210,

    /// <summary>Empty block or type body requires 'pass' keyword.</summary>
    EmptyBlockWithoutPass = 211,

    /// <summary>Destructuring pattern arity doesn't match tuple element count.</summary>
    DestructuringArityMismatch = 220,

    // ═══════════════════════════════════════════════════════════════════════════
    // ASSIGNMENT AND MUTABILITY ERRORS (RF-S250 - RF-S299)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Invalid assignment target (not a variable, member variable, or index).</summary>
    InvalidAssignmentTarget = 250,

    /// <summary>Cannot reassign a preset variable.</summary>
    AssignmentToImmutable = 251,

    /// <summary>Cannot assign value type to target type.</summary>
    AssignmentTypeMismatch = 252,

    /// <summary>Cannot assign to published member variable from outside defining file.</summary>
    AssignmentToPublished = 253,

    /// <summary>Compound assignment operator not supported on type.</summary>
    CompoundAssignmentNotSupported = 254,

    /// <summary>Attempting to modify 'me' member variable in a @readonly method.</summary>
    ModificationInReadonlyMethod = 255,

    /// <summary>Cannot call modifying method on preset variable.</summary>
    ModifyingCallOnImmutable = 256,

    /// <summary>Cannot assign to member variable of preset variable.</summary>
    MemberVariableAssignmentOnImmutable = 257,

    /// <summary>Preset initializer must be a build-time constant expression.</summary>
    PresetNotConstant = 258,

    // ═══════════════════════════════════════════════════════════════════════════
    // CONTROL FLOW AND RETURN ERRORS (RF-S300 - RF-S349)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Return statement outside of function context.</summary>
    ReturnOutsideFunction = 300,

    /// <summary>Return type does not match function's declared return type.</summary>
    ReturnTypeMismatch = 301,

    /// <summary>Conditional expression requires boolean condition.</summary>
    ConditionalNotBool = 302,

    /// <summary>Conditional expression branches have incompatible types.</summary>
    ConditionalBranchTypeMismatch = 303,

    /// <summary>When expression branches have incompatible types.</summary>
    WhenBranchTypeMismatch = 304,

    /// <summary>Missing return statement in non-void function.</summary>
    MissingReturn = 305,

    /// <summary>Becomes statement outside of expression block.</summary>
    BecomesOutsideExpressionBlock = 306,

    /// <summary>Multi-statement block in when expression requires 'becomes' to specify result.</summary>
    WhenExpressionBlockMissingBecomes = 307,

    /// <summary>Single-expression when branch should use '=>' syntax instead of block with 'becomes'.</summary>
    SingleExpressionBranchUsesBecomes = 308,



    // ═══════════════════════════════════════════════════════════════════════════
    // PATTERN MATCHING ERRORS (RF-S350 - RF-S399)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Guard expression must be boolean.</summary>
    PatternGuardNotBool = 350,

    /// <summary>Expression pattern must be boolean.</summary>
    ExpressionPatternNotBool = 351,

    /// <summary>Cannot match variant pattern against non-variant type.</summary>
    VariantPatternOnNonVariant = 352,

    /// <summary>Variant type does not have the specified member type.</summary>
    VariantCaseNotFound = 353,

    /// <summary>Variant member (None) has no payload to destructure.</summary>
    VariantCaseNoPayload = 354,

    /// <summary>Choice type does not have the specified case.</summary>
    ChoiceCaseNotFound = 355,

    /// <summary>Pattern match is not exhaustive.</summary>
    NonExhaustiveMatch = 356,

    /// <summary>Pattern type does not match subject type.</summary>
    PatternTypeMismatch = 357,

    /// <summary>Duplicate pattern in match.</summary>
    DuplicatePattern = 358,

    // ═══════════════════════════════════════════════════════════════════════════
    // DECLARATION AND VISIBILITY ERRORS (RF-S400 - RF-S449)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Type with this name is already defined.</summary>
    DuplicateTypeDefinition = 400,

    /// <summary>Module 'Core' is reserved for standard library.</summary>
    ReservedModuleCore = 401,

    /// <summary>Cannot access secret member from outside its defining file.</summary>
    SecretMemberAccess = 403,

    /// <summary> Cannot set posted member from outside its defining file. </summary>
    PostedMemberAccess = 404,

    /// <summary>Cannot define generated operator when base operator exists.</summary>
    GeneratedOperatorOverride = 405,

    /// <summary>Duplicate routine definition.</summary>
    DuplicateRoutineDefinition = 406,

    /// <summary>Duplicate member variable definition.</summary>
    DuplicateMemberVariableDefinition = 407,

    /// <summary>Invalid visibility modifier for this context.</summary>
    InvalidVisibilityModifier = 408,

    /// <summary>Routine name uses reserved prefix (try_, check_, lookup_).</summary>
    ReservedRoutinePrefix = 409,

    /// <summary>Routine name uses reserved '$' prefix but is not a known wired method.</summary>
    UnknownWiredRoutine = 410,

    /// <summary>Type defines an operator method but does not follow the required protocol.</summary>
    OperatorWithoutProtocol = 411,

    /// <summary>Record member variable has a type that is not a value type (entities, wrappers, tokens cannot be stored in records).</summary>
    RecordContainsNonValueType = 412,

    /// <summary>Entity cannot be directly assigned from another variable. Must use .share() or steal.</summary>
    BareEntityAssignment = 413,

    /// <summary>Variant type cannot be stored in member variables.</summary>
    VariantMemberVariableNotAllowed = 414,

    /// <summary>Variant type cannot be used as a parameter type.</summary>
    VariantParameterNotAllowed = 415,

    /// <summary>Variant type cannot have methods or follow protocols.</summary>
    VariantMethodNotAllowed = 416,

    /// <summary>Choice cases must have either all explicit values or all implicit values.</summary>
    ChoiceMixedValues = 417,

    /// <summary>Arithmetic operators cannot be used on choice types.</summary>
    ArithmeticOnChoiceType = 418,

    /// <summary>Choice case value exceeds S64 range.</summary>
    ChoiceCaseValueOverflow = 419,

    /// <summary>Duplicate choice case value.</summary>
    ChoiceDuplicateValue = 420,

    /// <summary>Discard target must be a routine call, not an arbitrary expression.</summary>
    InvalidDiscardTarget = 421,

    /// <summary>Variant type cannot be copied from one variable to another.</summary>
    VariantCopyNotAllowed = 422,

    /// <summary>Variant type cannot be reassigned to a variable.</summary>
    VariantReassignmentNotAllowed = 423,

    /// <summary>Flags type has more than 64 members (U64 bitmask limit).</summary>
    FlagsTooManyMembers = 424,

    /// <summary>Flags member name is duplicated.</summary>
    FlagsDuplicateMember = 425,

    /// <summary>Arithmetic operators cannot be used on flags types.</summary>
    ArithmeticOnFlagsType = 426,

    /// <summary>Custom operator overloading is not allowed on flags types.</summary>
    FlagsCustomOperatorNotAllowed = 427,

    /// <summary>'or' connective on flags is only valid in test context (is ... or), not in assignment.</summary>
    FlagsOrInAssignment = 428,

    /// <summary>'isonly' rejects 'or' and 'but' connectives — only 'and' is valid.</summary>
    FlagsIsOnlyRejectsOrBut = 429,

    /// <summary>Flags member not found in the flags type.</summary>
    FlagsMemberNotFound = 430,

    /// <summary>Operand is not a flags type for a flags operator.</summary>
    FlagsTypeMismatch = 431,

    /// <summary>Enumeration type (choice, variant, flags) must have at least one member/case.</summary>
    EmptyEnumerationBody = 433,

    /// <summary>Variant must be dismantled immediately with 'when' — cannot be used after other statements.</summary>
    VariantNotDismantled = 434,

    // ═══════════════════════════════════════════════════════════════════════════
    // MEMBER ACCESS ERRORS (RF-S450 - RF-S499)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Type does not have the specified member.</summary>
    MemberNotFound = 450,

    /// <summary>Type does not support member variable initialization.</summary>
    TypeNotMemberVariableInitializable = 451,

    /// <summary>Duplicate member variable initializer in creator expression.</summary>
    DuplicateMemberVariableInitializer = 452,

    /// <summary>Type does not have the specified member variable.</summary>
    MemberVariableNotFound = 453,

    /// <summary>Cannot assign type to member variable of different type.</summary>
    MemberVariableTypeMismatch = 454,

    /// <summary>Missing required member variable in creator.</summary>
    MissingRequiredMemberVariable = 455,

    /// <summary>Cannot call writable method through read-only wrapper.</summary>
    WritableMethodThroughReadOnlyWrapper = 456,

    /// <summary>'with' expression requires a record type.</summary>
    WithExpressionNotRecord = 457,

    /// <summary>Method not found on type.</summary>
    MethodNotFound = 458,

    /// <summary>Common routine called on object or vice versa.</summary>
    CommonRoutineMismatch = 459,

    /// <summary>Wired routine ($-prefixed) called directly by user code.</summary>
    DirectWiredRoutineCall = 460,

    // ═══════════════════════════════════════════════════════════════════════════
    // CALL AND ARGUMENT ERRORS (RF-S500 - RF-S549)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Wrong number of arguments provided (too few).</summary>
    TooFewArguments = 500,

    /// <summary>Wrong number of arguments provided (too many).</summary>
    TooManyArguments = 501,

    /// <summary>Argument type does not match parameter type.</summary>
    ArgumentTypeMismatch = 502,

    /// <summary>Range bounds must be numeric types.</summary>
    RangeBoundsNotNumeric = 503,

    /// <summary>Type is not callable.</summary>
    TypeNotCallable = 504,

    /// <summary>Named argument not found in parameter list.</summary>
    UnknownNamedArgument = 505,

    /// <summary>Duplicate named argument.</summary>
    DuplicateNamedArgument = 506,

    /// <summary>Positional argument after named argument.</summary>
    PositionalAfterNamed = 507,

    /// <summary>Index expression requires integer index.</summary>
    IndexNotInteger = 508,

    /// <summary>Type does not support indexing.</summary>
    TypeNotIndexable = 509,

    /// <summary>Routine with 2+ parameters requires named arguments at call site.</summary>
    NamedArgumentRequired = 510,

    /// <summary>Runtime dispatch not supported in RazorForge — mixed-type protocol varargs require all arguments to be the same concrete type.</summary>
    RuntimeDispatchNotSupported = 511,

    // ═══════════════════════════════════════════════════════════════════════════
    // COLLECTION LITERAL ERRORS (RF-S550 - RF-S599)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>List element type mismatch.</summary>
    ListElementTypeMismatch = 550,

    /// <summary>Cannot infer element type from empty list literal.</summary>
    EmptyListNoTypeAnnotation = 551,

    /// <summary>Cannot infer element type from empty set literal.</summary>
    EmptySetNoTypeAnnotation = 552,

    /// <summary>Cannot infer types from empty dict literal.</summary>
    EmptyDictNoTypeAnnotation = 553,

    /// <summary>Dict key type mismatch.</summary>
    DictKeyTypeMismatch = 554,

    /// <summary>Dict value type mismatch.</summary>
    DictValueTypeMismatch = 555,

    /// <summary>Set element type mismatch.</summary>
    SetElementTypeMismatch = 556,

    /// <summary>Collection literal constructor argument count mismatch (e.g., ValueList[S64, 4] given 3 args).</summary>
    ArgumentCountMismatch = 557,

    // ═══════════════════════════════════════════════════════════════════════════
    // MEMORY TOKEN ERRORS - RAZORFORGE SPECIFIC (RF-S600 - RF-S649)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Cannot return scope-bound token from routine.</summary>
    TokenReturnNotAllowed = 600,

    /// <summary>Cannot store scope-bound token in member variable.</summary>
    TokenMemberVariableNotAllowed = 601,

    /// <summary>Cannot use scope-bound token as variant payload.</summary>
    TokenVariantPayloadNotAllowed = 602,

    /// <summary>Cannot pass same exclusive token multiple times in single call.</summary>
    ExclusiveTokenDuplicate = 603,

    /// <summary>Cannot steal scope-bound token.</summary>
    StealScopeBoundToken = 604,

    /// <summary>Cannot steal Snatched type.</summary>
    StealSnatched = 605,

    /// <summary>Cannot capture scope-bound token in lambda.</summary>
    LambdaCaptureToken = 606,

    /// <summary>Cannot capture raw entity in lambda.</summary>
    LambdaCaptureRawEntity = 607,

    /// <summary>Nested hijacking is not allowed - cannot hijack a member of an already-hijacked object.</summary>
    NestedHijackingNotAllowed = 608,

    /// <summary>Dangerous routine called outside a danger! block.</summary>
    DangerousCallOutsideDangerBlock = 609,

    /// <summary>Lambda captures variable without declaring it in 'given' clause.</summary>
    LambdaCaptureWithoutGiven = 610,

    /// <summary>Using target must have $enter/$exit for resource management.</summary>
    UsingTargetMissingEnterExit = 612,

    /// <summary>Using-bound token cannot escape the using block scope.</summary>
    UsingTokenScopeEscape = 613,

    /// <summary>Using-bound resource cannot escape the using block scope.</summary>
    UsingResourceScopeEscape = 614,

    /// <summary>Variable is a deadref after steal or ownership transfer — cannot be used.</summary>
    UseAfterSteal = 615,

    /// <summary>Partial access on entity (e.g., entity.field.view()) is not allowed.</summary>
    PartialAccessOnEntity = 616,

    /// <summary>Cannot downgrade token permission (e.g., .view() on Hijacked/Seized).</summary>
    TokenDowngradeProhibited = 618,

    /// <summary>Same entity cannot be hijacked multiple times in one call.</summary>
    HijackDuplicateInCall = 620,

    /// <summary>Cannot hijack an already-hijacked token.</summary>
    ReHijackingProhibited = 621,

    /// <summary>Migratable operation on collection during iteration is not allowed.</summary>
    MigratableDuringIteration = 625,

    /// <summary>Seized[T] cannot be copied or aliased.</summary>
    SeizedCopyNotAllowed = 626,

    /// <summary>Cannot write to member variable through read-only wrapper (Viewed, Inspected).</summary>
    WriteThroughReadOnlyWrapper = 631,

    /// <summary>Snatched[T] method calls require danger! block.</summary>
    SnatchedRequiresDanger = 627,

    /// <summary>.snatch() on Shared/Tracked requires danger! block.</summary>
    SnatchRequiresDanger = 628,

    /// <summary>inspect!() only valid with MultiRead lock policy.</summary>
    InspectRequiresMultiRead = 629,

    /// <summary>ReadOnly lock policy does not support seize!() or inspect!().</summary>
    ReadOnlyRejectsLocking = 630,

    // ═══════════════════════════════════════════════════════════════════════════
    // MODIFICATION INFERENCE ERRORS (RF-S650 - RF-S699)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Modifying method requires ! token.</summary>
    ModifyingMethodRequiresToken = 650,

    /// <summary>Cannot call modifying method through read-only token.</summary>
    ModifyingMethodThroughReadOnlyToken = 651,

    /// <summary>Cannot call migratable method through exclusive token.</summary>
    MigratableMethodThroughExclusiveToken = 652,

    /// <summary>Modification category conflict detected.</summary>
    ModificationCategoryConflict = 653,

    // ═══════════════════════════════════════════════════════════════════════════
    // PROTOCOL CONFORMANCE ERRORS (RF-S700 - RF-S749)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Thrown value must implement Crashable protocol.</summary>
    ThrowNotCrashable = 700,

    /// <summary>Only record types can be thrown (error types must be records).</summary>
    ThrowRequiresRecordType = 701,

    /// <summary>Missing required protocol method implementation.</summary>
    MissingProtocolMethod = 702,

    /// <summary>Protocol method signature mismatch.</summary>
    ProtocolMethodSignatureMismatch = 703,

    /// <summary>@generated or @innate annotation used outside a protocol routine declaration.</summary>
    InvalidGeneratedInnatePlacement = 704,

    /// <summary>Cannot override an @innate protocol routine.</summary>
    InnateOverrideNotAllowed = 705,

    // ═══════════════════════════════════════════════════════════════════════════
    // ERROR HANDLING ERRORS (RF-S750 - RF-S799)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Throw statement only allowed in failable functions.</summary>
    ThrowOutsideFailableFunction = 750,

    /// <summary>Absent statement only allowed in failable functions.</summary>
    AbsentOutsideFailableFunction = 751,

    /// <summary>Error during variant generation.</summary>
    VariantGenerationError = 752,

    /// <summary>Crashable function called without error handling.</summary>
    UnhandledCrashableCall = 753,

    /// <summary>Error handling type (Result/Lookup) cannot be used as parameter type.</summary>
    ErrorHandlingTypeAsParameter = 754,

    /// <summary>Error handling type (Result/Lookup) cannot be used as member variable type.</summary>
    ErrorHandlingTypeAsMemberVariable = 755,

    /// <summary>Failable routine (!) contains neither 'throw' nor 'absent'.</summary>
    FailableWithoutThrowOrAbsent = 756,

    /// <summary>@crash_only annotation applied to a non-failable routine.</summary>
    CrashOnlyOnNonFailable = 757,

    /// <summary>Result/Lookup stored in variable beyond immediate dismantling.</summary>
    ErrorHandlingTypeStoredInVariable = 758,

    /// <summary>The 'global' keyword is only valid for entity type variables.</summary>
    GlobalOnlyForEntities = 760,

    /// <summary>Variant member cannot be a nested variant, Result[T], Lookup[T], token, or duplicate type.</summary>
    VariantCaseContainsInvalidType = 764,

    /// <summary>Index operators ($getitem/$setitem) are only valid on entities.</summary>
    IndexOperatorTypeKindRestriction = 765,

    /// <summary>Cannot use compound assignment on a read-only token (Viewed or Inspected).</summary>
    CompoundAssignmentOnReadOnlyToken = 766,

    /// <summary>Fixed-width numeric types must match exactly; explicit conversion required.</summary>
    FixedWidthTypeMismatch = 767,

    /// <summary>Varargs parameter must be the first parameter in the parameter list.</summary>
    VarargsNotFirst = 768,

    /// <summary>Only one varargs parameter is allowed per routine.</summary>
    VarargsMultiple = 769,

    /// <summary>Method-chain syntax only works with single-argument constructors.</summary>
    MethodChainMultiArg = 770,

    /// <summary>Protocol mutation contract violated: implementation does not match protocol's mutation category.</summary>
    ProtocolMutationContractViolation = 771,

    /// <summary>Cannot use multiple protocols directly as a type; use generic parameter with 'requires' clause.</summary>
    MultipleProtocolAsType = 772,

    /// <summary>Type does not implement the required protocol.</summary>
    ProtocolNotImplemented = 773,

    /// <summary>Pattern order violation: unreachable or misordered pattern in when statement.</summary>
    PatternOrderViolation = 774,

    /// <summary>Invalid combination of visibility modifiers (e.g., both open and secret).</summary>
    InvalidVisibilityCombination = 775,

    /// <summary>Conflicting mutation category annotations (e.g., both @readonly and @writable).</summary>
    MutationCategoryConflict = 776,

    /// <summary>Annotation arguments must be build-time constants.</summary>
    AnnotationArgNotConstant = 777,

    /// <summary>Cannot modify secret member variable in a 'with' expression.</summary>
    WithSecretMemberProhibited = 778,

    /// <summary>Annotation arguments must be compile-time constant literals or identifiers.</summary>
    AnnotationArgNotLiteral = 784,

    /// <summary>Deprecated — ValueTuple distinction removed, all tuples are inline structs.</summary>
    ValueTupleContainmentViolation = 779,

    /// <summary>Result, Lookup, variants, and tokens cannot be boxed to Data.</summary>
    DataBoxingProhibited = 781,

    // ═══════════════════════════════════════════════════════════════════════════
    // LANGUAGE RESTRICTION ERRORS (RF-S800 - RF-S849)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Feature not available in Suflae.</summary>
    FeatureNotInSuflae = 800,

    /// <summary>Danger blocks cannot be nested.</summary>
    NestedDangerBlock = 801,

    /// <summary>Blank cannot be used as a generic type argument (it has no value).</summary>
    BlankAsTypeArgument = 805,

    /// <summary>Data? / Maybe&lt;Data&gt; is not allowed — Data already holds None natively.</summary>
    NullableDataProhibited = 806,

    /// <summary>Suflae cannot use C interop directly.</summary>
    SuflaeNoCInterop = 810,

    /// <summary>Only Level 3 expressions (identifiers, literals, member access, calls) allowed in f-text.</summary>
    FTextExpressionLevelRestriction = 815,

    /// <summary>Invalid f-text format specifier.</summary>
    InvalidFTextFormatSpec = 816,

    /// <summary>Lazy Sequence[T] cannot escape the scope of the token it was created from.</summary>
    SequenceScopeEscape = 820,

    /// <summary>'emit' statement only allowed in generator routines.</summary>
    EmitOutsideGenerator = 825,

    /// <summary>Generator routine must return Sequence[T] type.</summary>
    GeneratorReturnType = 826,

    /// <summary>Suflae untyped parameter defaults to Data.</summary>
    SuflaeImplicitDataType = 830,

    /// <summary>Suflae type inference falls back to Data.</summary>
    SuflaeDataFallback = 831,

    /// <summary>Routine cannot directly return Maybe&lt;T&gt;/Result&lt;T&gt;/Lookup&lt;T&gt; — use failable routines (!) instead.</summary>
    ErrorHandlingTypeAsReturnType = 807,

    /// <summary>Nested Maybe types (Maybe[Maybe[T]] / T??) are not allowed.</summary>
    NestedMaybeProhibited = 808,

    // ═══════════════════════════════════════════════════════════════════════════
    // INTRINSIC AND NATIVE CALL ERRORS (RF-S850 - RF-S899)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Intrinsic call only allowed inside danger block.</summary>
    IntrinsicOutsideDanger = 850,

    /// <summary>Native call only allowed inside danger block.</summary>
    NativeOutsideDanger = 851,

    /// <summary>Unknown intrinsic operation.</summary>
    UnknownIntrinsic = 852,

    /// <summary>Invalid intrinsic arguments.</summary>
    InvalidIntrinsicArguments = 853,

    // ═══════════════════════════════════════════════════════════════════════════
    // CONCURRENCY AND TASK DEPENDENCY ERRORS (RF-S900 - RF-S949)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Task dependency expression is not a Lookup&lt;T&gt; type.</summary>
    DependencyNotLookupType = 900,

    /// <summary>Lookup&lt;T&gt; value not dismantled before end of scope.</summary>
    LookupNotDismantled = 901,

    /// <summary>Waitfor expression used outside of suspended/threaded routine.</summary>
    WaitforOutsideSuspendedRoutine = 902,

    /// <summary>Non-leaf waitfor requires 'within' timeout clause.</summary>
    WaitforRequiresTimeout = 905,

    /// <summary>Dangerous external routine called without danger! block.</summary>
    DangerousExternalCallOutsideDanger = 910,

    /// <summary>Channel send() makes source variable a deadref.</summary>
    ChannelSendOwnershipTransfer = 915,

    /// <summary>BuilderService routine called without 'import BuilderService'.</summary>
    BuilderServiceImportRequired = 950,

}

public static class SemanticDiagnosticCodeExtensions
{
    /// <summary>
    /// Formats code as RF-Snnn (e.g., S001, S100)
    /// </summary>
    public static string ToCodeString(this SemanticDiagnosticCode code)
    {
        return $"S{(int)code:D3}";
    }

    /// <summary>
    /// Gets the error category for grouping and documentation.
    /// </summary>
    public static string GetCategory(this SemanticDiagnosticCode code)
    {
        return (int)code switch
        {
            < 50 => "Literal and Identifier",
            < 100 => "Operator",
            < 150 => "Type Resolution",
            < 200 => "Generic Constraint",
            < 250 => "Statement",
            < 300 => "Assignment",
            < 350 => "Control Flow",
            < 400 => "Pattern Matching",
            < 450 => "Declaration",
            < 500 => "Member Access",
            < 550 => "Call and Argument",
            < 600 => "Collection Literal",
            < 650 => "Memory Token",
            < 700 => "Modification",
            < 750 => "Protocol",
            < 800 => "Error Handling",
            < 850 => "Language Restriction",
            < 900 => "Intrinsic",
            < 950 => "Concurrency",
            _ => "Other"
        };
    }
}
