using SemanticAnalysis.Enums;

namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis;
using SemanticAnalysis.Symbols;
using SemanticAnalysis.Types;
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

    /// <summary>Wrapper type base names for member forwarding in codegen.</summary>
    private static readonly HashSet<string> _wrapperTypeNames =
    [
        "Viewed", "Hijacked", "Retained", "Tracked", "Inspected", "Seized", "Shared",
        "Marked", "Snatched"
    ];

    /// <summary>The user program ASTs to generate code for (single-file or multi-file).</summary>
    private readonly IReadOnlyList<(Program Program, string FilePath, string Module)>
        _userPrograms;

    /// <summary>The stdlib programs to include routine bodies from.</summary>
    private readonly IReadOnlyList<(Program Program, string FilePath, string Module)>
        _stdlibPrograms;

    /// <summary>Output buffer for type declarations.</summary>
    private readonly StringBuilder _typeDeclarations = new();

    /// <summary>Output buffer for global declarations (constants, presets).</summary>
    private readonly StringBuilder _globalDeclarations = new();

    /// <summary>Output buffer for function declarations.</summary>
    private readonly StringBuilder _functionDeclarations = new();

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

    /// <summary>Counter for generating unique string constant names.</summary>
    private int _stringCounter;

    /// <summary>Counter for generating unique C string constant names.</summary>
    private int _cstrCounter;

    /// <summary>Map of string values to their global constant names (for deduplication).</summary>
    private readonly Dictionary<string, string> _stringConstants = new();

    /// <summary>Set of already-declared native functions to avoid duplicate declarations.</summary>
    private readonly HashSet<string> _declaredNativeFunctions = [];

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

    /// <summary>Type parameter substitution map for generic monomorphization (e.g., "T" → Character).</summary>
    private Dictionary<string, TypeInfo>? _typeSubstitutions;

    /// <summary>
    /// Pending generic monomorphizations: mangled function name → info needed to compile.
    /// Populated by EmitMethodCall/EmitGenericMethodCall when a resolved generic method is called.
    /// </summary>
    private readonly Dictionary<string, MonomorphizationEntry> _pendingMonomorphizations = new();

    /// <summary>
    /// Pending protocol dispatch stubs: mangled name → info needed to generate forwarding stub.
    /// Populated by EmitMethodCall when a call targets a protocol-typed receiver.
    /// </summary>
    private readonly Dictionary<string, ProtocolDispatchInfo> _pendingProtocolDispatches = new();

    /// <summary>Entry for a pending protocol dispatch stub.</summary>
    private record ProtocolDispatchInfo(ProtocolTypeInfo Protocol, string MethodName);

    /// <summary>Entry for a pending generic monomorphization.</summary>
    private record MonomorphizationEntry(
        RoutineInfo GenericMethod,
        TypeInfo ResolvedOwnerType,
        Dictionary<string, TypeInfo> TypeSubstitutions,
        string GenericAstName,
        Dictionary<string, TypeInfo>? MethodTypeSubstitutions = null);

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

    /// <summary>OS virtual memory page size in bytes.</summary>
    private readonly int _pageSize;

    /// <summary>CPU cache line size in bytes.</summary>
    private readonly int _cacheLineSize;

    /// <summary>Byte size of a collection header: { ptr data, i64 count, i64 capacity }.</summary>
    private readonly int _collectionHeaderSizeBytes;

    /// <summary>Byte size of a Data entity: { i64 type_id, ptr data_ptr, i64 data_size }.</summary>
    private readonly int _dataEntitySizeBytes;

    /// <summary>Alloca address for the emit slot in emitting routines (null outside emitting context).</summary>
    private string? _emitSlotAddr;

    /// <summary>LLVM type of the emit slot value.</summary>
    private string? _emitSlotType;

    /// <summary>Whether the current function being generated is failable (has ! suffix, can return absent).</summary>
    private bool _currentRoutineIsFailable;

    /// <summary>Whether the current function is a generated try_/check_/lookup_ variant body.
    /// In this context, throw statements return Maybe[T] with present=false (zeroinitializer) instead of crashing.</summary>
    private bool _currentVariantIsTry;

    /// <summary>The routine currently being emitted (for source_routine() / source_module() injection).</summary>
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
        RfBuildMode buildMode = RfBuildMode.Debug) : this(userPrograms:
        [(program, program.Location.FileName, "")],
        registry: registry,
        stdlibPrograms: stdlibPrograms,
        target: target,
        buildMode: buildMode)
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
        RfBuildMode buildMode = RfBuildMode.Debug)
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
        _buildMode = buildMode;
        _pointerBitWidth = _target.PointerBitWidth;
        _pointerSizeBytes = _target.PointerBitWidth / 8;
        _targetTriple = _target.Triple;
        _dataLayout = _target.DataLayout;
        _pageSize = _target.PageSize;
        _cacheLineSize = _target.CacheLineSize;
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
    private static TypeInfo? GetGenericBase(TypeInfo type)
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
    private static string? GetGenericBaseName(TypeInfo type)
    {
        return GetGenericBase(type: type)
          ?.Name;
    }

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

        // Generate record types (value types)
        foreach (TypeInfo type in _registry.GetTypesByCategory(category: TypeCategory.Record))
        {
            if (type is RecordTypeInfo { IsGenericDefinition: false } record)
            {
                if (record.TypeArguments != null &&
                    record.TypeArguments.Any(predicate: ContainsGenericParameter))
                {
                    continue;
                }

                GenerateRecordType(record: record);
            }
        }

        // Generate choice types (enums → single-member-variable records)
        foreach (TypeInfo type in _registry.GetTypesByCategory(category: TypeCategory.Choice))
        {
            if (type is ChoiceTypeInfo choice)
            {
                GenerateChoiceType(choice: choice);
            }
        }

        // Error handling types (Result[T], Lookup[T], Maybe[T]) use anonymous { i64, ptr }
        // so no named struct definitions are needed.

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

        // Unified loop: compile stdlib bodies, monomorphize generics, and generate
        // synthesized routines together. Each phase can introduce new declarations that
        // the other phases need to handle. All three are idempotent (they check
        // _generatedFunctionDefs before emitting), so calling them repeatedly is safe.
        int prevDefCount;
        int iterations = 0;
        const int maxIterations = 100;
        do
        {
            prevDefCount = _generatedFunctionDefs.Count;

            // Phase A: Compile stdlib routine bodies for referenced routines
            foreach ((Program program, string _, string module) in _stdlibPrograms)
            {
                foreach (IAstNode decl in program.Declarations)
                {
                    if (decl is RoutineDeclaration routine)
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
        } while (_generatedFunctionDefs.Count > prevDefCount);
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

        // Type declarations
        if (_typeDeclarations.Length > 0)
        {
            output.AppendLine(value: "; Type declarations");
            output.Append(value: _typeDeclarations);
            output.AppendLine();
        }

        // Global declarations
        if (_globalDeclarations.Length > 0)
        {
            output.AppendLine(value: "; Global declarations");
            output.Append(value: _globalDeclarations);
            output.AppendLine();
        }

        // Function declarations — filter out any that now have definitions
        if (_functionDeclarations.Length > 0)
        {
            output.AppendLine(value: "; Function declarations");
            foreach (string line in _functionDeclarations.ToString()
                                                         .Split(separator: '\n'))
            {
                // Skip declare lines for functions that have definitions
                if (line.StartsWith(value: "declare ") && _generatedFunctionDefs.Count > 0)
                {
                    // Extract function name from "declare ... @funcName(...)" or "declare ... @"quoted name"(...)"
                    int atIdx = line.IndexOf(value: '@');
                    if (atIdx > 0)
                    {
                        string declaredName;
                        if (atIdx + 1 < line.Length && line[atIdx + 1] == '"')
                        {
                            // Quoted identifier: @"..." — find closing quote
                            int closeQuoteIdx = line.IndexOf(value: '"', startIndex: atIdx + 2);
                            declaredName = closeQuoteIdx > atIdx + 1
                                ? line[(atIdx + 1)..(closeQuoteIdx + 1)]
                                : "";
                        }
                        else
                        {
                            // Unquoted identifier: find first (
                            int parenIdx = line.IndexOf(value: '(', startIndex: atIdx);
                            declaredName = parenIdx > atIdx ? line[(atIdx + 1)..parenIdx] : "";
                        }

                        if (declaredName != "" && _generatedFunctionDefs.Contains(item: declaredName))
                        {
                            continue; // Skip — this function has a define
                        }
                    }
                }

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
    private void RecordMonomorphization(string mangledName, RoutineInfo genericMethod,
        TypeInfo resolvedOwnerType, Dictionary<string, TypeInfo>? methodTypeArgs = null)
    {
        if (_pendingMonomorphizations.ContainsKey(key: mangledName))
        {
            return;
        }

        // Generic parameter owner methods (e.g., routine T.view() called on Point)
        // The owner is GenericParameterTypeInfo("T"), resolved to a concrete type
        if (genericMethod.OwnerType is GenericParameterTypeInfo genParam)
        {
            var typeSubs = new Dictionary<string, TypeInfo>
            {
                [key: genParam.Name] = resolvedOwnerType
            };
            // Merge method-level type args
            if (methodTypeArgs != null)
            {
                foreach ((string key, TypeInfo value) in methodTypeArgs)
                {
                    typeSubs[key: key] = value;
                }
            }

            string genericAstName = $"{genParam.Name}.{genericMethod.Name}";
            _pendingMonomorphizations[key: mangledName] = new MonomorphizationEntry(
                GenericMethod: genericMethod,
                ResolvedOwnerType: resolvedOwnerType,
                TypeSubstitutions: typeSubs,
                GenericAstName: genericAstName,
                MethodTypeSubstitutions: methodTypeArgs);
            return;
        }

        // Protocol-owned methods (e.g., Iterable[T].enumerate() called on List[S64])
        // AST name must use the protocol owner, not the concrete receiver type
        if (genericMethod.OwnerType is ProtocolTypeInfo protocolOwner &&
            protocolOwner.GenericParameters is { Count: > 0 })
        {
            var typeSubs = new Dictionary<string, TypeInfo>();
            // Map protocol's generic params using receiver's type arguments
            if (resolvedOwnerType.TypeArguments != null)
            {
                for (int i = 0;
                     i < protocolOwner.GenericParameters.Count &&
                     i < resolvedOwnerType.TypeArguments.Count;
                     i++)
                {
                    typeSubs[key: protocolOwner.GenericParameters[index: i]] =
                        resolvedOwnerType.TypeArguments[index: i];
                }
            }

            if (methodTypeArgs != null)
            {
                foreach ((string key, TypeInfo value) in methodTypeArgs)
                {
                    typeSubs[key: key] = value;
                }
            }

            string protoParamList =
                string.Join(separator: ", ", values: protocolOwner.GenericParameters);
            string genericAstName = $"{protocolOwner.Name}[{protoParamList}].{genericMethod.Name}";

            _pendingMonomorphizations[key: mangledName] = new MonomorphizationEntry(
                GenericMethod: genericMethod,
                ResolvedOwnerType: resolvedOwnerType,
                TypeSubstitutions: typeSubs,
                GenericAstName: genericAstName,
                MethodTypeSubstitutions: methodTypeArgs);
            return;
        }

        // Build the generic AST name: "List[T].add_last" from owner's generic definition name + method name
        TypeInfo? genericDef = resolvedOwnerType switch
        {
            EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition,
            RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition,
            _ => null
        };

        if (genericDef != null && genericDef.GenericParameters != null)
        {
            // Build type substitution map: e.g., { "T" → Character }
            var typeSubs2 = new Dictionary<string, TypeInfo>();
            if (resolvedOwnerType.TypeArguments != null)
            {
                for (int i = 0;
                     i < genericDef.GenericParameters.Count &&
                     i < resolvedOwnerType.TypeArguments.Count;
                     i++)
                {
                    typeSubs2[key: genericDef.GenericParameters[index: i]] =
                        resolvedOwnerType.TypeArguments[index: i];
                }
            }

            // Merge method-level type args (e.g., U → S32 for double-generic methods)
            if (methodTypeArgs != null)
            {
                foreach ((string key, TypeInfo value) in methodTypeArgs)
                {
                    typeSubs2[key: key] = value;
                }
            }

            // Build generic AST name: e.g., "List[T].add_last"
            string genericParamList =
                string.Join(separator: ", ", values: genericDef.GenericParameters);
            string genericAstName2 = $"{genericDef.Name}[{genericParamList}].{genericMethod.Name}";

            _pendingMonomorphizations[key: mangledName] = new MonomorphizationEntry(
                GenericMethod: genericMethod,
                ResolvedOwnerType: resolvedOwnerType,
                TypeSubstitutions: typeSubs2,
                GenericAstName: genericAstName2,
                MethodTypeSubstitutions: methodTypeArgs);
            return;
        }

        // Method-level generics on a non-generic owner (e.g., Text.$create[T](from: List[T]))
        // Owner type is concrete (Text), but method itself has generic parameters
        if (methodTypeArgs != null && methodTypeArgs.Count > 0 &&
            genericMethod.IsGenericDefinition)
        {
            var typeSubs3 = new Dictionary<string, TypeInfo>(dictionary: methodTypeArgs);
            string genericAstName3 = genericMethod.BaseName; // e.g., "Text.$create"
            _pendingMonomorphizations[key: mangledName] = new MonomorphizationEntry(
                GenericMethod: genericMethod,
                ResolvedOwnerType: resolvedOwnerType,
                TypeSubstitutions: typeSubs3,
                GenericAstName: genericAstName3,
                MethodTypeSubstitutions: methodTypeArgs);
        }
    }

    #endregion
}
