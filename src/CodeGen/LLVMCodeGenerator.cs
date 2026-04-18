using SemanticVerification.Enums;

namespace Compiler.CodeGen;

using System.Text;
using Compiler.Desugaring;
using Compiler.Resolution;
using Compiler.Targeting;
using SemanticVerification;
using SemanticVerification.Symbols;
using SemanticVerification.Types;
using SyntaxTree;

/// <summary>
/// LLVM IR code generator for RazorForge and Suflae.
/// Consumes a fully-typed AST from the semantic analyzer.
/// </summary>
public partial class LLVMCodeGenerator
{
    #region Fields

    /// <summary>The type registry from semantic analysis.</summary>
    private readonly TypeRegistry _registry;

    /// <summary>AST bodies for compiler-generated derived operators, keyed by RoutineInfo.RegistryKey.</summary>
    private IReadOnlyDictionary<string, Statement> _synthesizedBodies = new Dictionary<string, Statement>();

    /// <summary>Wrapper type base names for member forwarding in codegen.</summary>
    private static readonly HashSet<string> _wrapperTypeNames =
    [
        "Viewed", "Hijacked", "Retained", "Tracked", "Inspected", "Seized", "Shared",
        "Marked", "Snatched", "Owned"
    ];

    /// <summary>The user program ASTs to generate code for (single-file or multi-file).</summary>
    private readonly IReadOnlyList<(Program Program, string FilePath, string Module)>
        _userPrograms;

    /// <summary>The stdlib programs to include routine bodies from.</summary>
    private readonly IReadOnlyList<(Program Program, string FilePath, string Module)>
        _stdlibPrograms;

    /// <summary>
    /// Type declarations bucketed by kind and sorted lexicographically within each bucket.
    /// Emitted in category order: record → choice → variant → entity → crashable.
    /// Key = mangled LLVM type name; value = full declaration text (struct line + comment line).
    /// </summary>
    private readonly SortedDictionary<string, string> _typeDeclarationsRecord = new();
    private readonly SortedDictionary<string, string> _typeDeclarationsChoice = new();
    private readonly SortedDictionary<string, string> _typeDeclarationsFlags = new();
    private readonly SortedDictionary<string, string> _typeDeclarationsVariant = new();
    private readonly SortedDictionary<string, string> _typeDeclarationsEntity = new();
    private readonly SortedDictionary<string, string> _typeDeclarationsCrashable = new();

    /// <summary>Output buffer for global declarations (constants, presets).</summary>
    private readonly StringBuilder _globalDeclarations = new();

    /// <summary>Output buffer for native/extern function declarations (always emitted).</summary>
    private readonly StringBuilder _functionDeclarations = new();

    /// <summary>
    /// RF function forward declarations keyed by mangled name.
    /// Entries whose name is in <see cref="_generatedFunctionDefs"/> are suppressed at output
    /// time to avoid declare+define conflicts in the same LLVM module.
    /// </summary>
    private readonly Dictionary<string, string> _rfFunctionDeclarations = new();

    /// <summary>Output buffer for function definitions.</summary>
    private readonly StringBuilder _functionDefinitions = new();

    /// <summary>Output buffer for auxiliary top-level helper function definitions.</summary>
    private readonly StringBuilder _auxFunctionDefinitions = new();

    /// <summary>Counter for generating unique temporary variable names.</summary>
    private int _tempCounter;

    /// <summary>Counter for generating unique label names.</summary>
    private int _labelCounter;

    /// <summary>Set of already-generated type declarations to avoid duplicates.</summary>
    private readonly HashSet<string> _generatedTypes = [];

    /// <summary>Set of already-generated function declarations to avoid duplicates.</summary>
    private readonly HashSet<string> _generatedFunctions = [];

    /// <summary>Counter for generating unique lambda function names.</summary>
    private int _lambdaCounter;

    /// <summary>Counter for generating unique string constant names.</summary>
    private int _stringCounter;

    /// <summary>Counter for generating unique C string constant names.</summary>
    private int _cstrCounter;

    /// <summary>Map of string values to their global constant names (for deduplication).</summary>
    private readonly Dictionary<string, string> _stringConstants = new();

    /// <summary>Set of already-declared native functions to avoid duplicate declarations.</summary>
    private readonly HashSet<string> _declaredNativeFunctions = [];

    /// <summary>Map of global variable names to their types (module-level 'global' declarations).</summary>
    private readonly Dictionary<string, TypeInfo> _globalVariables = new();

    /// <summary>Map of global variable names to their LLVM global symbol names (e.g. "@MyMod.x").</summary>
    private readonly Dictionary<string, string> _globalVariableLlvmNames = new();

    /// <summary>Map of local variable names to their types for the current function.</summary>
    private readonly Dictionary<string, TypeInfo> _localVariables = new();

    /// <summary>Map of source variable names to unique LLVM variable names (handles shadowing).</summary>
    private readonly Dictionary<string, string> _localVarLLVMNames = new();

    /// <summary>Counter for deduplicating variable names within a function.</summary>
    private readonly Dictionary<string, int> _varNameCounts = new();

    /// <summary>List of local entity variables (name, LLVM addr name) for auto-cleanup.</summary>
    private readonly List<(string Name, string LLVMAddr)> _localEntityVars = new();

    /// <summary>List of local record variables with RC wrapper fields for retain/release.</summary>
    private readonly List<(string Name, string LLVMAddr, RecordTypeInfo RecordType)>
        _localRCRecordVars = new();

    /// <summary>Set of already-generated function definitions to avoid duplicates.</summary>
    private readonly HashSet<string> _generatedFunctionDefs = [];

    /// <summary>Set of generated threaded worker helper definitions.</summary>
    private readonly HashSet<string> _generatedThreadWorkerDefs = [];

    /// <summary>The return type of the current function being generated.</summary>
    private TypeInfo? _currentFunctionReturnType;

    /// <summary>The label of the current basic block (for phi node generation).</summary>
    private string _currentBlock = "entry";

    /// <summary>Function-entry alloca instructions emitted once per function.</summary>
    private readonly StringBuilder _currentFunctionEntryAllocas = new();

    /// <summary>Tracks alloca names already emitted for the current function to prevent duplicates.</summary>
    private readonly HashSet<string> _emittedAllocaNames = new();

    /// <summary>Type parameter substitution map for generic monomorphization (e.g., "T" → Character).</summary>
    private Dictionary<string, TypeInfo>? _typeSubstitutions;

    /// <summary>
    /// Planner that owns all pending and pre-rewritten monomorphization entries.
    /// Replaces the old inline <c>_pendingMonomorphizations</c> dictionary and the
    /// related helper methods (<c>RecordMonomorphization</c>, <c>FindGenericAstRoutine</c>,
    /// <c>BuildResolvedRoutineInfo</c>, <c>ResolveSubstitutedType</c>).
    /// </summary>
    private readonly MonomorphizationPlanner _planner;

    /// <summary>
    /// Pending protocol dispatch stubs: mangled name → info needed to generate forwarding stub.
    /// Populated by EmitMethodCall when a call targets a protocol-typed receiver.
    /// </summary>
    private readonly Dictionary<string, ProtocolDispatchInfo> _pendingProtocolDispatches = new();

    // ─── Removed fields (moved to MonomorphizationPlanner) ───────────────────
    // _pendingMonomorphizations  →  _planner.PendingMonomorphizations
    // MonomorphizationEntry      →  MonomorphizationEntry (same file, now standalone)
    // RecordMonomorphization     →  _planner.Record()

    /// <summary>Entry for a pending protocol dispatch stub.</summary>
    private record ProtocolDispatchInfo(ProtocolTypeInfo Protocol, string MethodName);

    /// <summary>
    /// Maps local variable names that were bound via "when is Protocol x" pattern matching
    /// to their type_id alloca name (e.g., "err" → "%err.typeid.addr").
    /// Used by EmitMethodCall to pass the type_id for runtime protocol dispatch.
    /// </summary>
    private readonly Dictionary<string, string> _protocolTypeIdAllocas = new();

    /// <summary>Bundles a method lookup result with fully-resolved context for codegen emission.</summary>
    private record ResolvedMethod(
        RoutineInfo Routine,
        TypeInfo OwnerType,
        bool IsFailable,
        IReadOnlyList<string>? ModulePath,
        string MangledName,
        bool IsMonomorphized,
        Dictionary<string, TypeInfo>? MethodTypeArgs
    );

    /// <summary>Target platform configuration (triple, data layout, page size, etc.).</summary>
    private readonly TargetConfig _target;

    /// <summary>Requested build optimization mode.</summary>
    private readonly RfBuildMode _buildMode;

    /// <summary>Pointer bit width for the target platform (64 for x86_64, 32 for x86).</summary>
    private readonly int _pointerBitWidth;

    /// <summary>Pointer size in bytes, derived from <see cref="_pointerBitWidth"/>.</summary>
    private readonly int _pointerSizeBytes;

    /// <summary>LLVM target triple for the current platform.</summary>
    private readonly string _targetTriple;

    /// <summary>LLVM data layout string for the current platform.</summary>
    private readonly string _dataLayout;

    /// <summary>Byte size of a collection header: { ptr data, i64 count, i64 capacity }.</summary>
    private readonly int _collectionHeaderSizeBytes;

    /// <summary>Byte size of a Data entity: { i64 type_id, ptr data_ptr, i64 data_size }.</summary>
    private readonly int _dataEntitySizeBytes;

    /// <summary>Whether the current function being generated is failable (has ! suffix, can return absent).</summary>
    private bool _currentRoutineIsFailable;

    /// <summary>The routine currently being compiled (for source_routine() / source_module() injection).</summary>
    private RoutineInfo? _currentEmittingRoutine;

    #endregion

    #region Constructor

    /// <summary>
    /// Creates a new LLVM code generator for a single user program.
    /// </summary>
    /// <param name="program">The program AST to generate code for.</param>
    /// <param name="registry">The type registry from semantic analysis.</param>
    /// <param name="stdlibPrograms">Optional stdlib programs for intrinsic routine definitions.</param>
    /// <param name="target">Target platform configuration (defaults to current host).</param>
    /// <param name="buildMode">Build optimization mode (defaults to Debug).</param>
    public LLVMCodeGenerator(Program program, TypeRegistry registry,
        IReadOnlyList<(Program Program, string FilePath, string Module)>? stdlibPrograms = null,
        TargetConfig? target = null,
        RfBuildMode buildMode = RfBuildMode.Debug,
        IReadOnlyDictionary<string, Statement>? synthesizedBodies = null,
        IReadOnlyDictionary<string, Desugaring.MonomorphizedBody>? preMonomorphizedBodies = null)
        : this(userPrograms: [(program, program.Location.FileName, "")],
        registry: registry,
        stdlibPrograms: stdlibPrograms,
        target: target,
        buildMode: buildMode,
        synthesizedBodies: synthesizedBodies,
        preMonomorphizedBodies: preMonomorphizedBodies)
    {
    }

    /// <summary>
    /// Creates a new LLVM code generator for multiple user programs (multi-file build).
    /// </summary>
    /// <param name="userPrograms">The user program ASTs with file paths and module names.</param>
    /// <param name="registry">The type registry from semantic analysis.</param>
    /// <param name="stdlibPrograms">Optional stdlib programs for intrinsic routine definitions.</param>
    /// <param name="target">Target platform configuration (defaults to current host).</param>
    /// <param name="buildMode">Build optimization mode (defaults to Debug).</param>
    public LLVMCodeGenerator(
        IReadOnlyList<(Program Program, string FilePath, string Module)> userPrograms,
        TypeRegistry registry,
        IReadOnlyList<(Program Program, string FilePath, string Module)>? stdlibPrograms = null,
        TargetConfig? target = null,
        RfBuildMode buildMode = RfBuildMode.Debug,
        IReadOnlyDictionary<string, Statement>? synthesizedBodies = null,
        IReadOnlyDictionary<string, Desugaring.MonomorphizedBody>? preMonomorphizedBodies = null)
    {
        _target = target ?? TargetConfig.ForCurrentHost();
        if (_target.PointerBitWidth != 64)
        {
            throw new ArgumentException(
                message: $"Only 64-bit targets are currently supported (got {_target.PointerBitWidth}).",
                paramName: nameof(target));
        }

        _userPrograms = userPrograms;
        _registry = registry;
        _stdlibPrograms = stdlibPrograms ?? [];
        if (synthesizedBodies != null) _synthesizedBodies = synthesizedBodies;
        _planner = new MonomorphizationPlanner(
            registry: registry,
            userPrograms: _userPrograms,
            stdlibPrograms: _stdlibPrograms,
            preMonomorphizedBodies: preMonomorphizedBodies);
        _buildMode = buildMode;
        _pointerBitWidth = _target.PointerBitWidth;
        _pointerSizeBytes = _target.PointerBitWidth / 8;
        _targetTriple = _target.Triple;
        _dataLayout = _target.DataLayout;
        // Runtime object layout sizes — derived from field types, not from type definitions yet
        _collectionHeaderSizeBytes = _pointerSizeBytes + 8 + 8; // ptr + i64 count + i64 capacity
        _dataEntitySizeBytes =
            8 + _pointerSizeBytes + 8; // i64 type_id + ptr data_ptr + i64 data_size
    }

    #endregion

    #region Helpers

    /// <summary>Whether to emit rf_trace_push/rf_trace_pop calls for stack trace diagnostics.</summary>
    private bool ShouldEmitTrace => _buildMode is RfBuildMode.Debug or RfBuildMode.Release;

    /// <summary>
    /// Whether to emit trace push/pop for the currently-compiled routine.
    /// In Release, @inline routines are excluded — they are implementation details
    /// that inflate the shadow stack without adding navigable frames.
    /// In Debug, all routines are traced.
    /// </summary>
    private bool _traceCurrentRoutine;

    /// <summary>
    /// Looks up a type by name, trying the current routine's module-qualified name first,
    /// then falling back to the bare name. Mirrors SemanticAnalyzer.LookupTypeInCurrentModule.
    /// </summary>
    private TypeInfo? LookupTypeInCurrentModule(string name)
    {
        string? moduleName = _currentEmittingRoutine?.OwnerType?.Module ??
                             _currentEmittingRoutine?.Module;
        if (moduleName != null && !name.Contains(value: '.'))
        {
            TypeInfo? qualified = _registry.LookupType(name: $"{moduleName}.{name}");
            if (qualified != null)
            {
                return qualified;
            }
        }

        return _registry.LookupType(name: name);
    }

    /// <summary>
    /// Gets the generic definition for a resolved generic type, regardless of concrete subtype.
    /// Returns null for non-generic or non-resolved types.
    /// </summary>
    private static TypeInfo? GetGenericBase(TypeInfo type) =>
        GetGenericBaseStatic(type: type);

    /// <summary>
    /// Gets the generic definition for a resolved generic type. Exposed as
    /// <c>internal static</c> so <see cref="MonomorphizationPlanner"/> can call it.
    /// </summary>
    internal static TypeInfo? GetGenericBaseStatic(TypeInfo type)
    {
        return type switch
        {
            RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition,
            EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition,
            ProtocolTypeInfo { GenericDefinition: not null } p => p.GenericDefinition,
            VariantTypeInfo { GenericDefinition: not null } v => v.GenericDefinition,
            _ => null
        };
    }

    /// <summary>
    /// Gets the generic definition's name for a resolved generic type.
    /// Returns null for non-generic or non-resolved types.
    /// </summary>
    private static string? GetGenericBaseName(TypeInfo type) =>
        GetGenericBaseNameStatic(type: type);

    /// <summary>Exposed as <c>internal static</c> for <see cref="MonomorphizationPlanner"/>.</summary>
    internal static string? GetGenericBaseNameStatic(TypeInfo type) =>
        GetGenericBaseStatic(type: type)?.Name;

    /// <summary>Exposed as <c>internal static</c> for <see cref="MonomorphizationPlanner"/>.</summary>
    internal static bool IsMaybeTypeStatic(TypeInfo type) =>
        GetGenericBaseNameStatic(type: type) is "Maybe";

    #endregion

    #region Public API

    /// <summary>
    /// Generates LLVM IR for the entire program.
    /// </summary>
    /// <returns>The generated LLVM IR as a string.</returns>
    public string Generate()
    {
        // Phase 1: Generate all type declarations
        GenerateTypeDeclarations();

        // Phase 1b: Emit module-level global variable slots
        GenerateGlobalVariableDeclarations();

        // Phase 2: Generate function declarations (signatures)
        GenerateFunctionDeclarations();

        // Phase 3: Generate function definitions (bodies)
        GenerateFunctionDefinitions();

        // Phase 4: Generate runtime support (if needed)
        GenerateRuntimeSupport();

        // Combine all sections
        return BuildOutput();
    }

    #endregion

    #region Code Generation Phases

    /// <summary>
    /// Emits module-level LLVM global variable declarations for all top-level
    /// <c>global var</c> declarations in user programs.
    /// Entity globals are emitted as <c>ptr</c>-typed globals initialized to null.
    /// Declarations annotated with <c>@thread_local</c> use the <c>thread_local</c> prefix.
    /// Initializer expressions (if any) are collected and emitted in a private
    /// <c>@.rf_global_init</c> constructor registered via <c>@llvm.global_ctors</c>.
    /// </summary>
    private void GenerateGlobalVariableDeclarations()
    {
        var initStmts = new List<(string LlvmName, TypeInfo Type, SyntaxTree.Expression Init)>();

        foreach ((Program userProgram, string _, string module) in _userPrograms)
        {
            foreach (IAstNode decl in userProgram.Declarations)
            {
                if (decl is not VariableDeclaration varDecl ||
                    varDecl.Storage != StorageClass.Global)
                    continue;

                TypeInfo? varType = varDecl.Type != null
                    ? ResolveTypeExpression(typeExpr: varDecl.Type)
                    : null;
                if (varType == null) continue;

                bool isThreadLocal = varDecl.Annotations?.Any(a => a == "thread_local") == true;
                string prefix = isThreadLocal ? "thread_local global" : "global";

                // Entity globals are ptr-typed (heap-allocated)
                // Use module-qualified LLVM name to avoid collisions
                string llvmName = string.IsNullOrEmpty(value: module)
                    ? $"@{varDecl.Name}"
                    : $"@{module}.{varDecl.Name}";

                EmitLine(sb: _globalDeclarations,
                    line: $"{llvmName} = {prefix} ptr null");

                _globalVariables[key: varDecl.Name] = varType;
                _globalVariableLlvmNames[key: varDecl.Name] = llvmName;

                if (varDecl.Initializer != null)
                    initStmts.Add((LlvmName: llvmName, Type: varType, Init: varDecl.Initializer));
            }
        }

        if (initStmts.Count == 0) return;

        // Emit @.rf_global_init function that runs all global initializers
        EmitLine(sb: _globalDeclarations,
            line: "@llvm.global_ctors = appending global [1 x { i32, ptr, ptr }] " +
                  "[{ i32, ptr, ptr } { i32 65535, ptr @.rf_global_init, ptr null }]");

        var sb = _functionDefinitions;
        EmitLine(sb: sb, line: "define private void @.rf_global_init() {");
        EmitLine(sb: sb, line: "entry:");
        foreach ((string llvmName, TypeInfo type, SyntaxTree.Expression init) in initStmts)
        {
            string val = EmitExpression(sb: sb, expr: init);
            EmitLine(sb: sb, line: $"  store ptr {val}, ptr {llvmName}");
        }
        EmitLine(sb: sb, line: "  ret void");
        EmitLine(sb: sb, line: "}");
        EmitLine(sb: sb, line: "");
    }

    /// <summary>
    /// Generates LLVM type declarations for all types in the registry.
    /// </summary>
    private void GenerateTypeDeclarations()
    {
        // Generate entity types (reference types, heap-allocated)
        foreach (TypeInfo type in _registry.GetTypesByCategory(category: TypeCategory.Entity))
        {
            if (type is EntityTypeInfo { IsGenericDefinition: false } entity)
            {
                // Skip resolutions with unresolved generic parameters at any depth
                // (e.g., List[BTreeSetNode[T]] where T is nested inside a type argument)
                if (entity.TypeArguments != null &&
                    entity.TypeArguments.Any(predicate: ContainsGenericParameter))
                {
                    continue;
                }

                GenerateEntityType(entity: entity);
            }
        }

        // Generate crashable types (always entity semantics — heap-allocated error types)
        foreach (TypeInfo type in _registry.GetTypesByCategory(category: TypeCategory.Crashable))
        {
            if (type is CrashableTypeInfo crashable)
                GenerateCrashableType(crashable: crashable);
        }

        // Generate record types (value types)
        foreach (TypeInfo type in _registry.GetTypesByCategory(category: TypeCategory.Record))
        {
            if (type is RecordTypeInfo { IsGenericDefinition: false } record)
            {
                if (record.TypeArguments != null &&
                    record.TypeArguments.Any(predicate: t => ContainsGenericParameter(t) || t is ErrorTypeInfo))
                {
                    continue;
                }

                GenerateRecordType(record: record);
            }
        }

        // Generate choice types (enums → i32 wrapper)
        foreach (TypeInfo type in _registry.GetTypesByCategory(category: TypeCategory.Choice))
        {
            if (type is ChoiceTypeInfo choice)
                GenerateChoiceType(choice: choice);
        }

        // Generate flags types (bitmask types → i64 wrapper)
        foreach (TypeInfo type in _registry.GetTypesByCategory(category: TypeCategory.Flags))
        {
            if (type is FlagsTypeInfo flags)
                GenerateFlagsType(flags: flags);
        }

        // Generate variant types (tagged unions → tag + payload record)
        foreach (TypeInfo type in _registry.GetTypesByCategory(category: TypeCategory.Variant))
        {
            if (type is VariantTypeInfo { IsGenericDefinition: false } variant)
            {
                GenerateVariantType(variant: variant);
            }
        }
    }

    /// <summary>
    /// Checks if a type contains unresolved generic parameters at any nesting depth.
    /// </summary>
    private static bool ContainsGenericParameter(TypeInfo type)
    {
        if (type is GenericParameterTypeInfo)
        {
            return true;
        }

        if (type.TypeArguments == null)
        {
            return false;
        }

        return type.TypeArguments.Any(predicate: ContainsGenericParameter);
    }

    /// <summary>
    /// Generates LLVM function declarations (signatures only).
    /// Only emits 'declare' for external routines that don't have bodies.
    /// Routines with bodies (user program and stdlib) are handled by GenerateFunctionDefinitions().
    /// </summary>
    private void GenerateFunctionDeclarations()
    {
        // Build set of routine names that have bodies (in user programs or stdlib)
        var routinesWithBodies = new HashSet<string>();

        // User program routines
        foreach ((Program userProgram, string _, string _) in _userPrograms)
        {
            foreach (IAstNode decl in userProgram.Declarations)
            {
                if (decl is RoutineDeclaration routine)
                {
                    routinesWithBodies.Add(item: routine.Name);
                }
            }
        }

        // Stdlib routines with bodies
        foreach ((Program program, string _, string _) in _stdlibPrograms)
        {
            foreach (IAstNode decl in program.Declarations)
            {
                if (decl is RoutineDeclaration routine)
                {
                    routinesWithBodies.Add(item: routine.Name);
                }
            }
        }

        foreach (RoutineInfo routine in _registry.GetAllRoutines())
        {
            // Skip generic definitions, routines with unresolved types,
            // and methods on generic owner types (e.g., Dict[K,V].count)
            if (routine.IsGenericDefinition || HasErrorTypes(routine: routine) ||
                routine.OwnerType is { IsGenericDefinition: true } ||
                routine.OwnerType is GenericParameterTypeInfo)
            {
                continue;
            }

            // Skip synthesized routines (they will be emitted as 'define' by GenerateSynthesizedRoutines)
            if (routine.IsSynthesized)
            {
                continue;
            }

            // Skip routines that have bodies (they will be emitted as 'define' in GenerateFunctionDefinitions)
            string fullName = routine.OwnerType != null
                ? $"{routine.OwnerType.Name}.{routine.Name}"
                : routine.Name;
            if (routinesWithBodies.Contains(item: routine.Name) ||
                routinesWithBodies.Contains(item: fullName))
            {
                continue;
            }

            // Only emit 'declare' for truly external routines
            GenerateFunctionDeclaration(routine: routine);
        }
    }

    /// <summary>
    /// Checks if a routine has any error types in its signature.
    /// </summary>
    private static bool HasErrorTypes(RoutineInfo routine)
    {
        // Check return type
        if (routine.ReturnType?.Category == TypeCategory.Error)
        {
            return true;
        }

        // Check parameter types
        foreach (ParameterInfo param in routine.Parameters)
        {
            if (param.Type.Category == TypeCategory.Error)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Registers pending monomorphization entries for standalone user-defined variants
    /// (try_/lookup_/check_ on module-level failable routines). These routines have no AST body —
    /// MonomorphizeGenericMethods compiles them from the original failable routine's body.
    /// </summary>
    private void RegisterStandaloneUserVariants()
    {
        foreach (RoutineInfo routine in _registry.GetAllRoutines())
        {
            // Only generated variants (OriginalName set), no owner type (standalone)
            if (routine.OriginalName == null || routine.OwnerType != null)
                continue;

            string funcName = MangleFunctionName(routine: routine);
            if (!_generatedFunctions.Contains(item: funcName))
                continue;
            if (_planner.HasEntry(mangledName: funcName))
                continue;
            if (_generatedFunctionDefs.Contains(item: funcName))
                continue;

            // Register for compilation: GenericAstName not found → fallback to OriginalName
            _planner.AddDirectEntry(mangledName: funcName, entry: new MonomorphizationEntry(
                GenericMethod: routine,
                ResolvedOwnerType: null,
                TypeSubstitutions: new Dictionary<string, TypeInfo>(),
                GenericAstName: routine.Name,
                MethodTypeSubstitutions: null));
        }
    }

    /// <summary>
    /// Generates LLVM function definitions (with bodies).
    /// Includes both user program routines and stdlib routines (for intrinsics).
    /// </summary>
    private void GenerateFunctionDefinitions()
    {
        // First, generate user program routines (these take priority)
        foreach ((Program userProgram, string _, string _) in _userPrograms)
        {
            foreach (IAstNode decl in userProgram.Declarations)
            {
                if (decl is RoutineDeclaration routine)
                {
                    GenerateFunctionDefinition(routine: routine);
                }
            }
        }

        // Register standalone user-defined variants (try_/lookup_/check_) for compilation.
        // These routines are not in the AST — MonomorphizeGenericMethods compiles them from the
        // original failable routine's body with the appropriate carrier semantics.
        RegisterStandaloneUserVariants();

        // Pre-rewrite all pending monomorphization entries collected so far (from user program
        // emission above and from RegisterStandaloneUserVariants). Additional entries discovered
        // during the loop below are pre-rewritten at the top of each MonomorphizeGenericMethods call.
        _planner.PreRewriteAll(synthesizedBodies: _synthesizedBodies);

        // Unified loop: compile stdlib bodies, monomorphize generics, and generate
        // synthesized routines together. Each phase can introduce new declarations that
        // the other phases need to handle. All three are idempotent (they check
        // _generatedFunctionDefs before emitting), so calling them repeatedly is safe.
        int prevDefCount;
        int prevDeclCount;
        int iterations = 0;
        const int maxIterations = 100;
        do
        {
            prevDefCount = _generatedFunctionDefs.Count;
            prevDeclCount = _generatedFunctions.Count;

            // Phase A: Compile stdlib routine bodies for referenced routines
            foreach ((Program program, string _, string module) in _stdlibPrograms)
            {
                foreach (RoutineDeclaration routine in EnumerateStdlibRoutines(program: program))
                {
                        // Look up routine info — try multiple keys:
                        // 1. Raw AST name (e.g., "show")
                        // 2. Module-qualified (e.g., "IO.show")
                        // 3. Short name fallback via LookupRoutineByName
                        // 4. Overload-based lookup using AST parameter types
                        RoutineInfo? routineInfo = _registry.LookupRoutine(fullName: routine.Name);
                        if (routineInfo == null && !string.IsNullOrEmpty(value: module))
                        {
                            routineInfo =
                                _registry.LookupRoutine(fullName: $"{module}.{routine.Name}");
                        }

                        if (routineInfo == null)
                        {
                            int dotIdx = routine.Name.IndexOf(value: '.');
                            if (dotIdx > 0)
                            {
                                string shortName = routine.Name[(dotIdx + 1)..];
                                routineInfo = _registry.LookupRoutine(fullName: shortName) ??
                                              _registry.LookupRoutineByName(name: shortName);
                            }
                            else
                            {
                                routineInfo = _registry.LookupRoutineByName(name: routine.Name);
                            }
                        }

                        // For overloaded routines (e.g., $create), try to find the
                        // specific overload matching this AST declaration's parameter types.
                        // This includes 0-arg overloads — LookupRoutine returns an arbitrary
                        // overload, so we must disambiguate for all param counts.
                        if (routineInfo != null)
                        {
                            var astParamTypes = new List<TypeInfo>();
                            foreach (Parameter param in routine.Parameters)
                            {
                                if (param.Type != null)
                                {
                                    string typeName = param.Type.Name;
                                    if (param.Type.GenericArguments is { Count: > 0 })
                                    {
                                        typeName =
                                            $"{typeName}[{string.Join(separator: ", ", values: param.Type.GenericArguments.Select(selector: a => a.Name))}]";
                                    }

                                    TypeInfo? t = _registry.LookupType(name: typeName);
                                    if (t != null)
                                    {
                                        astParamTypes.Add(item: t);
                                    }
                                }
                            }

                            if (astParamTypes.Count == routine.Parameters.Count)
                            {
                                RoutineInfo? overload = _registry.LookupRoutineOverload(
                                    baseName: routineInfo.BaseName,
                                    argTypes: astParamTypes);
                                if (overload != null)
                                {
                                    routineInfo = overload;
                                }
                            }
                        }

                        // Ensure the resolved routine's failable flag matches the AST routine.
                        // When failable/non-failable overloads share the same name and parameter types
                        // (e.g., interpret_as_utf8() and interpret_as_utf8!()), they collide in
                        // the _routines dictionary under the same RegistryKey. The last registration
                        // wins, making the first invisible to LookupRoutine. Use LookupMethod
                        // (which indexes by owner type and preserves all overloads) to find the
                        // correct variant.
                        if (routineInfo != null && routineInfo.IsFailable != routine.IsFailable &&
                            routineInfo.OwnerType != null)
                        {
                            RoutineInfo? corrected = _registry.LookupMethod(
                                type: routineInfo.OwnerType,
                                methodName: routineInfo.Name,
                                isFailable: routine.IsFailable);
                            if (corrected != null)
                            {
                                routineInfo = corrected;
                            }
                        }

                        if (routineInfo == null || routineInfo.IsGenericDefinition)
                        {
                            continue;
                        }

                        if (HasErrorTypes(routine: routineInfo))
                        {
                            continue;
                        }

                        // Only generate definitions for routines that were declared
                        string funcName = MangleFunctionName(routine: routineInfo);
                        if (!_generatedFunctions.Contains(item: funcName))
                        {
                            continue;
                        }

                        // Skip if already defined
                        if (_generatedFunctionDefs.Contains(item: funcName))
                        {
                            continue;
                        }

                        try
                        {
                            GenerateFunctionDefinition(routine: routine,
                                preResolvedInfo: routineInfo);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine(
                                value:
                                $"Warning: Stdlib codegen failed for '{routine.Name}': {ex.Message}");
                        }
                    }
            }

            // Phase B: Monomorphize generic methods (compile generic AST bodies with type substitutions)
            MonomorphizeGenericMethods();

            // Phase C: Generate bodies for synthesized routines ($ne, $lt, $le, $gt, $ge, $represent, $diagnose)
            GenerateSynthesizedRoutines();

            // Phase D: Generate protocol dispatch stubs (forwarding from protocol method names to concrete implementations)
            GenerateProtocolDispatchStubs();

            iterations++;
            if (iterations >= maxIterations)
            {
                Console.Error.WriteLine(
                    value:
                    $"Warning: GenerateFunctionDefinitions reached {maxIterations} iterations, possible infinite loop");
                break;
            }
        } while (_generatedFunctionDefs.Count > prevDefCount
            || _generatedFunctions.Count > prevDeclCount);
    }

    /// <summary>
    /// Enumerates all compilable RoutineDeclaration nodes from a stdlib program, including
    /// routines nested inside CrashableDeclaration.Members (e.g., crash_message synthesized
    /// from the "message:" directive). Nested routines are yielded with their names prefixed
    /// by the owning type name (e.g., "DivisionByZeroError.crash_message") so Phase A's
    /// registry lookup can find the registered method.
    /// </summary>
    private static IEnumerable<RoutineDeclaration> EnumerateStdlibRoutines(Program program)
    {
        foreach (IAstNode decl in program.Declarations)
        {
            if (decl is RoutineDeclaration routine)
            {
                yield return routine;
            }
            else if (decl is CrashableDeclaration crashable)
            {
                foreach (Declaration member in crashable.Members)
                {
                    if (member is RoutineDeclaration memberRoutine)
                    {
                        yield return memberRoutine with { Name = $"{crashable.Name}.{memberRoutine.Name}" };
                    }
                }
            }
        }
    }

    /// <summary>
    /// Generates runtime support functions.
    /// External("C") routines from NativeDeclarations.rf are declared via GenerateFunctionDeclarations().
    /// </summary>
    private void GenerateRuntimeSupport()
    {
        // No-op: external("C") routines are handled by GenerateFunctionDeclarations()
        // via the TypeRegistry (registered from NativeDeclarations.rf).
    }

    /// <summary>
    /// Builds the final output by combining all sections.
    /// </summary>
    /// <returns>The complete LLVM IR module.</returns>
    private string BuildOutput()
    {
        var output = new StringBuilder();

        // Module header
        output.AppendLine(value: "; ModuleID = 'razorforge_module'");
        output.AppendLine(value: "source_filename = \"razorforge_module\"");
        output.AppendLine(handler: $"target datalayout = \"{_dataLayout}\"");
        output.AppendLine(handler: $"target triple = \"{_targetTriple}\"");
        output.AppendLine();

        // Type declarations — record → choice → variant → entity → crashable, each sorted by name
        bool anyTypes = _typeDeclarationsRecord.Count > 0
                     || _typeDeclarationsChoice.Count > 0
                     || _typeDeclarationsFlags.Count > 0
                     || _typeDeclarationsVariant.Count > 0
                     || _typeDeclarationsEntity.Count > 0
                     || _typeDeclarationsCrashable.Count > 0;
        if (anyTypes)
        {
            output.AppendLine(value: "; Type declarations");
            void EmitTypeSection(string header, SortedDictionary<string, string> bucket)
            {
                if (bucket.Count == 0) return;
                output.AppendLine(handler: $"; -- {header} --");
                foreach (string decl in bucket.Values) output.Append(value: decl);
            }
            EmitTypeSection(header: "records",    bucket: _typeDeclarationsRecord);
            EmitTypeSection(header: "choices",    bucket: _typeDeclarationsChoice);
            EmitTypeSection(header: "flags",      bucket: _typeDeclarationsFlags);
            EmitTypeSection(header: "variants",   bucket: _typeDeclarationsVariant);
            EmitTypeSection(header: "entities",   bucket: _typeDeclarationsEntity);
            EmitTypeSection(header: "crashables", bucket: _typeDeclarationsCrashable);
            output.AppendLine();
        }

        // Global declarations
        if (_globalDeclarations.Length > 0)
        {
            output.AppendLine(value: "; Global declarations");
            output.Append(value: _globalDeclarations);
            output.AppendLine();
        }

        // Native/extern function declarations (always emitted)
        if (_functionDeclarations.Length > 0)
        {
            output.AppendLine(value: "; Function declarations");
            output.Append(value: _functionDeclarations);
        }

        // RF function forward declarations — skip any that now have definitions
        foreach ((string name, string line) in _rfFunctionDeclarations)
        {
            if (!_generatedFunctionDefs.Contains(item: name))
            {
                output.AppendLine(value: line);
            }
        }

        // Inline shadow-stack helpers (only when tracing is on)
        if (ShouldEmitTrace)
        {
            output.AppendLine(value: "; Shadow stack (inline — no DLL call)");
            // 32-entry ring (power-of-2) — index masked with AND, no branch needed in push.
            // The printer clamps to the actual depth so only valid frames are shown.
            output.AppendLine(value: "@_rf_trace_stack = thread_local global [32 x { ptr, ptr, i32, i32 }] zeroinitializer");
            output.AppendLine(value: "@_rf_trace_depth = thread_local global i32 0");
            output.AppendLine();
            // push helper — branchless: mask index to [0,31] with AND
            output.AppendLine(value: "define private void @_rf_trace_push(ptr %r, ptr %f, i32 %ln, i32 %col) alwaysinline {");
            output.AppendLine(value: "entry:");
            output.AppendLine(value: "  %d = load i32, ptr @_rf_trace_depth");
            output.AppendLine(value: "  %idx32 = and i32 %d, 31");
            output.AppendLine(value: "  %idx = zext i32 %idx32 to i64");
            output.AppendLine(value: "  %slot = getelementptr inbounds [32 x { ptr, ptr, i32, i32 }], ptr @_rf_trace_stack, i64 0, i64 %idx");
            output.AppendLine(value: "  %p0 = getelementptr inbounds { ptr, ptr, i32, i32 }, ptr %slot, i32 0, i32 0");
            output.AppendLine(value: "  store ptr %r, ptr %p0");
            output.AppendLine(value: "  %p1 = getelementptr inbounds { ptr, ptr, i32, i32 }, ptr %slot, i32 0, i32 1");
            output.AppendLine(value: "  store ptr %f, ptr %p1");
            output.AppendLine(value: "  %p2 = getelementptr inbounds { ptr, ptr, i32, i32 }, ptr %slot, i32 0, i32 2");
            output.AppendLine(value: "  store i32 %ln, ptr %p2");
            output.AppendLine(value: "  %p3 = getelementptr inbounds { ptr, ptr, i32, i32 }, ptr %slot, i32 0, i32 3");
            output.AppendLine(value: "  store i32 %col, ptr %p3");
            output.AppendLine(value: "  %nd = add i32 %d, 1");
            output.AppendLine(value: "  store i32 %nd, ptr @_rf_trace_depth");
            output.AppendLine(value: "  ret void");
            output.AppendLine(value: "}");
            output.AppendLine();
            // pop helper — branchless: depth is always > 0 when pop is called (paired with push)
            output.AppendLine(value: "define private void @_rf_trace_pop() alwaysinline {");
            output.AppendLine(value: "entry:");
            output.AppendLine(value: "  %d = load i32, ptr @_rf_trace_depth");
            output.AppendLine(value: "  %nd = add i32 %d, -1");
            output.AppendLine(value: "  store i32 %nd, ptr @_rf_trace_depth");
            output.AppendLine(value: "  ret void");
            output.AppendLine(value: "}");
            output.AppendLine();
            // printer helper — passes exe TLS data to the DLL
            output.AppendLine(value: "declare void @rf_print_shadow_stack_data(ptr, i32)");
            output.AppendLine(value: "define private void @_rf_print_trace_stack() {");
            output.AppendLine(value: "entry:");
            output.AppendLine(value: "  %depth = load i32, ptr @_rf_trace_depth");
            output.AppendLine(value: "  call void @rf_print_shadow_stack_data(ptr @_rf_trace_stack, i32 %depth)");
            output.AppendLine(value: "  ret void");
            output.AppendLine(value: "}");
            output.AppendLine();
        }

        // Auxiliary helper definitions
        if (_auxFunctionDefinitions.Length > 0)
        {
            output.AppendLine(value: "; Auxiliary function definitions");
            output.Append(value: _auxFunctionDefinitions);
        }

        // Function definitions
        if (_functionDefinitions.Length > 0)
        {
            output.AppendLine(value: "; Function definitions");
            output.Append(value: _functionDefinitions);
        }

        // Emit main() entry point that calls the module's start() routine
        string? startFunc = _generatedFunctionDefs.FirstOrDefault(predicate: f =>
            f == "start" || f.EndsWith(value: ".start") || f.EndsWith(value: ".start\""));
        if (startFunc != null)
        {
            // Select trace mode: 2=shadow (debug+release), 1=platform (hardware faults only), 0=none (release-time/space)
            int traceMode = _buildMode switch
            {
                RfBuildMode.Debug or RfBuildMode.Release => 2,
                _ => 0
            };

            output.AppendLine(value: "declare void @__rf_set_trace_mode(i32)");
            if (ShouldEmitTrace)
                output.AppendLine(value: "declare void @rf_set_stack_printer(ptr)");
            output.AppendLine();
            output.AppendLine(value: "; Entry point");
            output.AppendLine(value: "define i32 @main(i32 %argc, ptr %argv) {");
            output.AppendLine(value: "entry:");
            output.AppendLine(value: "  call void @rf_runtime_init()");
            output.AppendLine(handler: $"  call void @__rf_set_trace_mode(i32 {traceMode})");
            if (ShouldEmitTrace)
                output.AppendLine(value: "  call void @rf_set_stack_printer(ptr @_rf_print_trace_stack)");
            output.AppendLine(handler: $"  call void @{startFunc}()");
            output.AppendLine(value: "  ret i32 0");
            output.AppendLine(value: "}");
        }

        // Normalize to Unix line endings (clang/LLVM requires LF, not CRLF)
        return output.ToString()
                     .Replace(oldValue: "\r\n", newValue: "\n")
                     .Replace(oldValue: "\r", newValue: "\n");
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Gets the next unique temporary variable name.
    /// </summary>
    /// <returns>A unique temporary name like %tmp0, %tmp1, etc.</returns>
    private string NextTemp()
    {
        return $"%tmp{_tempCounter++}";
    }

    /// <summary>
    /// Gets the next unique label name.
    /// </summary>
    /// <param name="prefix">Optional prefix for the label.</param>
    /// <returns>A unique label name.</returns>
    private string NextLabel(string prefix = "label")
    {
        return $"{prefix}{_labelCounter++}";
    }

    /// <summary>
    /// Emits a line to a StringBuilder.
    /// </summary>
    private void EmitLine(StringBuilder sb, string line)
    {
        sb.AppendLine(value: line);
        // Auto-track current basic block for PHI node predecessors
        if (line.Length > 0 && line[index: 0] != ' ' && line.EndsWith(value: ':'))
        {
            _currentBlock = line[..^1];
        }
    }

    /// <summary>
    /// Emits a function-local stack allocation into the current function's entry block.
    /// This avoids repeated stack growth when the source declaration appears inside loops.
    /// </summary>
    private void EmitEntryAlloca(string llvmName, string llvmType)
    {
        if (!_emittedAllocaNames.Add(item: llvmName))
        {
            return; // Already emitted for this function — pattern variables shared across when arms
        }

        EmitLine(sb: _currentFunctionEntryAllocas, line: $"  {llvmName} = alloca {llvmType}");
    }

    /// <summary>
    /// Emits a null-terminated C string as an LLVM global constant.
    /// Returns the global name (e.g., "@.cstr.0") which can be used as a ptr.
    /// </summary>
    private string EmitCStringConstant(string value)
    {
        string name = $"@.cstr.{_cstrCounter++}";
        byte[] utf8 = Encoding.UTF8.GetBytes(s: value + "\0");
        var sb = new StringBuilder();
        foreach (byte b in utf8)
        {
            if (b >= 0x20 && b < 0x7F && b != (byte)'\\' && b != (byte)'"')
            {
                sb.Append(value: (char)b);
            }
            else
            {
                sb.Append(handler: $"\\{b:X2}");
            }
        }

        EmitLine(sb: _globalDeclarations,
            line: $"{name} = private unnamed_addr constant [{utf8.Length} x i8] c\"{sb}\"");
        return name;
    }

    /// <summary>
    /// Infers method-level type arguments from concrete argument types.
    /// Returns a mapping of generic parameter names to concrete types, or null if inference fails.
    /// Only infers parameters that belong to the method itself (excludes owner-level params).
    /// </summary>
    private static Dictionary<string, TypeInfo>? InferMethodTypeArgs(RoutineInfo genericMethod,
        IReadOnlyList<TypeInfo> argTypes)
    {
        if (genericMethod.GenericParameters == null)
        {
            return null;
        }

        // Determine which params are method-level (exclude owner-level)
        var ownerParams = new HashSet<string>();
        if (genericMethod.OwnerType?.GenericParameters != null)
        {
            foreach (string gp in genericMethod.OwnerType.GenericParameters)
            {
                ownerParams.Add(item: gp);
            }
        }

        var methodParams = genericMethod.GenericParameters
                                        .Where(predicate: gp => !ownerParams.Contains(item: gp))
                                        .ToHashSet();
        if (methodParams.Count == 0)
        {
            return null;
        }

        var inferred = new Dictionary<string, TypeInfo>();

        for (int i = 0; i < genericMethod.Parameters.Count && i < argTypes.Count; i++)
        {
            TypeInfo paramType = genericMethod.Parameters[index: i].Type;
            TypeInfo argType = argTypes[index: i];
            InferFromTypes(paramType: paramType,
                argType: argType,
                methodParams: methodParams,
                inferred: inferred);
        }

        // Only succeed if ALL method-level params are inferred
        return inferred.Count == methodParams.Count
            ? inferred
            : null;
    }

    /// <summary>
    /// Recursively infers type argument mappings by matching a generic parameter type against a concrete type.
    /// Handles direct params (T → S64) and parameterized types (List[T] → List[S64]).
    /// </summary>
    private static void InferFromTypes(TypeInfo paramType, TypeInfo argType,
        HashSet<string> methodParams, Dictionary<string, TypeInfo> inferred)
    {
        // Case 1: Direct generic parameter (T → S64)
        if (paramType is GenericParameterTypeInfo && methodParams.Contains(item: paramType.Name))
        {
            inferred.TryAdd(key: paramType.Name, value: argType);
            return;
        }

        // Case 2: Generic resolution (List[T] → List[S64])
        // Match base types and recurse on type arguments
        if (paramType is { IsGenericResolution: true, TypeArguments: not null } &&
            argType is { IsGenericResolution: true, TypeArguments: not null } &&
            paramType.TypeArguments.Count == argType.TypeArguments.Count)
        {
            for (int i = 0; i < paramType.TypeArguments.Count; i++)
            {
                InferFromTypes(paramType: paramType.TypeArguments[index: i],
                    argType: argType.TypeArguments[index: i],
                    methodParams: methodParams,
                    inferred: inferred);
            }
        }
    }

    /// <summary>
    /// Looks up a method on a type and returns a fully-resolved bundle for codegen.
    /// Handles mangling, monomorphization recording, and method-level generic inference.
    /// </summary>
    private ResolvedMethod? ResolveMethod(TypeInfo receiverType, string methodName,
        bool? isFailable = null,
        IReadOnlyList<TypeInfo>? methodTypeArgs = null,
        IReadOnlyList<TypeInfo>? argTypes = null)
    {
        RoutineInfo? method = _registry.LookupMethod(type: receiverType,
            methodName: methodName,
            isFailable: isFailable);
        if (method == null) return null;

        string mangledName;
        bool isMonomorphized = false;
        Dictionary<string, TypeInfo>? resolvedMethodTypeArgs = null;

        // Infer method-level type args if not explicit
        if (methodTypeArgs != null && methodTypeArgs.Count > 0)
        {
            resolvedMethodTypeArgs = BuildMethodTypeArgDict(method: method, typeArgs: methodTypeArgs);
        }
        else if (argTypes != null && method.IsGenericDefinition)
        {
            resolvedMethodTypeArgs = InferMethodTypeArgs(genericMethod: method, argTypes: argTypes);
        }

        // Determine mangled name + monomorphization
        bool ownerIsGenericResolution = receiverType.IsGenericResolution;
        bool methodOwnerIsGeneric = method.OwnerType is { IsGenericDefinition: true }
            or { IsGenericResolution: true };
        bool methodOwnerIsGenericParam = method.OwnerType is GenericParameterTypeInfo;
        bool hasMethodTypeArgs = resolvedMethodTypeArgs is { Count: > 0 };

        if (ownerIsGenericResolution && methodOwnerIsGeneric || hasMethodTypeArgs ||
            methodOwnerIsGenericParam)
        {
            string baseName = $"{receiverType.FullName}.{SanitizeLLVMName(name: method.Name)}";
            // Disambiguate $create overloads by first parameter type (mirrors MangleFunctionName)
            if (method.Name == "$create" && method.Parameters.Count > 0)
            {
                baseName = $"{baseName}({method.Parameters[index: 0].Type.Name})";
            }

            mangledName = Q(name: DecorateRoutineSymbolName(baseName: baseName,
                isFailable: method.IsFailable));
            RecordMonomorphization(mangledName: mangledName,
                genericMethod: method,
                resolvedOwnerType: receiverType,
                methodTypeArgs: resolvedMethodTypeArgs);
            isMonomorphized = true;
        }
        else
        {
            mangledName = MangleFunctionName(routine: method);
        }

        return new ResolvedMethod(
            Routine: method,
            OwnerType: receiverType,
            IsFailable: method.IsFailable,
            ModulePath: method.ModulePath,
            MangledName: mangledName,
            IsMonomorphized: isMonomorphized,
            MethodTypeArgs: resolvedMethodTypeArgs
        );
    }

    /// <summary>
    /// Converts positional type arguments to a named dictionary keyed by method-level generic parameter names.
    /// </summary>
    private static Dictionary<string, TypeInfo>? BuildMethodTypeArgDict(
        RoutineInfo method, IReadOnlyList<TypeInfo> typeArgs)
    {
        if (method.GenericParameters == null) return null;

        var ownerParams = method.OwnerType?.GenericParameters?.ToHashSet() ?? [];
        var methodParams = method.GenericParameters
            .Where(predicate: gp => !ownerParams.Contains(item: gp))
            .ToList();

        if (methodParams.Count == 0 || typeArgs.Count == 0) return null;

        var dict = new Dictionary<string, TypeInfo>();
        for (int i = 0; i < methodParams.Count && i < typeArgs.Count; i++)
        {
            dict[key: methodParams[index: i]] = typeArgs[index: i];
        }
        return dict;
    }

    /// <summary>
    /// Records a pending monomorphization for a resolved generic method call.
    /// Called from EmitMethodCall/EmitGenericMethodCall when a call targets a method on
    /// a resolved generic type (e.g., List[Character].add_last).
    /// </summary>
    /// <summary>
    /// Delegates to <see cref="MonomorphizationPlanner.Record"/>. Kept as a thin wrapper so
    /// the 20+ call sites across codegen files don't need to be changed in this PR.
    /// </summary>
    private void RecordMonomorphization(string mangledName, RoutineInfo genericMethod,
        TypeInfo resolvedOwnerType, Dictionary<string, TypeInfo>? methodTypeArgs = null)
    {
        _planner.Record(mangledName: mangledName, genericMethod: genericMethod,
            resolvedOwnerType: resolvedOwnerType, methodTypeArgs: methodTypeArgs);
    }

    #endregion
}
