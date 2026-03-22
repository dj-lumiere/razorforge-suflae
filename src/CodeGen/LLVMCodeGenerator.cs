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
        ["Viewed", "Hijacked", "Retained", "Watched", "Inspected", "Seized", "Shared",
            "Tracked", "Snatched"];

    /// <summary>The program AST to generate code for.</summary>
    private readonly Program _program;

    /// <summary>The stdlib programs to include routine bodies from.</summary>
    private readonly IReadOnlyList<(Program Program, string FilePath, string Module)> _stdlibPrograms;

    /// <summary>Output buffer for type declarations.</summary>
    private readonly StringBuilder _typeDeclarations = new();

    /// <summary>Output buffer for global declarations (constants, presets).</summary>
    private readonly StringBuilder _globalDeclarations = new();

    /// <summary>Output buffer for function declarations.</summary>
    private readonly StringBuilder _functionDeclarations = new();

    /// <summary>Output buffer for function definitions.</summary>
    private readonly StringBuilder _functionDefinitions = new();

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

    /// <summary>Set of already-generated function definitions to avoid duplicates.</summary>
    private readonly HashSet<string> _generatedFunctionDefs = [];

    /// <summary>The return type of the current function being generated.</summary>
    private TypeInfo? _currentFunctionReturnType;

    /// <summary>The label of the current basic block (for phi node generation).</summary>
    private string _currentBlock = "entry";

    /// <summary>Type parameter substitution map for generic monomorphization (e.g., "T" → Letter).</summary>
    private Dictionary<string, TypeInfo>? _typeSubstitutions;

    /// <summary>
    /// Pending generic monomorphizations: mangled function name → info needed to compile.
    /// Populated by EmitMethodCall/EmitGenericMethodCall when a resolved generic method is called.
    /// </summary>
    private readonly Dictionary<string, MonomorphizationEntry> _pendingMonomorphizations = new();

    /// <summary>Entry for a pending generic monomorphization.</summary>
    private record MonomorphizationEntry(
        RoutineInfo GenericMethod,
        TypeInfo ResolvedOwnerType,
        Dictionary<string, TypeInfo> TypeSubstitutions,
        string GenericAstName);

    /// <summary>Pointer bit width for the target platform (64 for x86_64, 32 for x86).</summary>
    private readonly int _pointerBitWidth;

    /// <summary>Alloca address for the emit slot in emitting routines (null outside emitting context).</summary>
    private string? _emitSlotAddr;

    /// <summary>LLVM type of the emit slot value.</summary>
    private string? _emitSlotType;

    /// <summary>The routine currently being emitted (for source_routine() / source_module() injection).</summary>
    private RoutineInfo? _currentEmittingRoutine;

    #endregion

    #region Constructor

    /// <summary>
    /// Creates a new LLVM code generator.
    /// </summary>
    /// <param name="program">The program AST to generate code for.</param>
    /// <param name="registry">The type registry from semantic analysis.</param>
    /// <param name="stdlibPrograms">Optional stdlib programs for intrinsic routine definitions.</param>
    /// <param name="pointerBitWidth">Pointer bit width for the target platform (64 for x86_64, 32 for x86).</param>
    public LLVMCodeGenerator(Program program, TypeRegistry registry, IReadOnlyList<(Program Program, string FilePath, string Module)>? stdlibPrograms = null, int pointerBitWidth = 64)
    {
        _program = program;
        _registry = registry;
        _stdlibPrograms = stdlibPrograms ?? [];
        _pointerBitWidth = pointerBitWidth;
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
        foreach (var type in _registry.GetTypesByCategory(TypeCategory.Entity))
        {
            if (type is EntityTypeInfo { IsGenericDefinition: false } entity)
            {
                GenerateEntityType(entity);
            }
        }

        // Generate record types (value types)
        foreach (var type in _registry.GetTypesByCategory(TypeCategory.Record))
        {
            if (type is RecordTypeInfo { IsGenericDefinition: false } record)
            {
                GenerateRecordType(record);
            }
        }

        // Generate resident types (fixed-size reference types) - RazorForge only
        foreach (var type in _registry.GetTypesByCategory(TypeCategory.Resident))
        {
            if (type is ResidentTypeInfo { IsGenericDefinition: false } resident)
            {
                GenerateResidentType(resident);
            }
        }

        // Generate choice types (enums → single-member-variable records)
        foreach (var type in _registry.GetTypesByCategory(TypeCategory.Choice))
        {
            if (type is ChoiceTypeInfo choice)
            {
                GenerateChoiceType(choice);
            }
        }

        // Generate variant types (tagged unions → tag + payload record)
        foreach (var type in _registry.GetTypesByCategory(TypeCategory.Variant))
        {
            if (type is VariantTypeInfo { IsGenericDefinition: false } variant)
            {
                GenerateVariantType(variant);
            }
        }
    }

    /// <summary>
    /// Generates LLVM function declarations (signatures only).
    /// Only emits 'declare' for external routines that don't have bodies.
    /// Routines with bodies (user program and stdlib) are handled by GenerateFunctionDefinitions().
    /// </summary>
    private void GenerateFunctionDeclarations()
    {
        // Build set of routine names that have bodies (in user program or stdlib)
        var routinesWithBodies = new HashSet<string>();

        // User program routines
        foreach (var decl in _program.Declarations)
        {
            if (decl is RoutineDeclaration routine)
            {
                routinesWithBodies.Add(routine.Name);
            }
        }

        // Stdlib routines with bodies
        foreach (var (program, _, _) in _stdlibPrograms)
        {
            foreach (var decl in program.Declarations)
            {
                if (decl is RoutineDeclaration routine)
                {
                    routinesWithBodies.Add(routine.Name);
                }
            }
        }

        foreach (var routine in _registry.GetAllRoutines())
        {
            // Skip generic definitions, routines with unresolved types,
            // and methods on generic owner types (e.g., Dict[K,V].count)
            if (routine.IsGenericDefinition || HasErrorTypes(routine)
                || routine.OwnerType is { IsGenericDefinition: true }
                || routine.OwnerType is GenericParameterTypeInfo)
            {
                continue;
            }

            // Skip synthesized routines (they will be emitted as 'define' by GenerateSynthesizedRoutines)
            if (routine.IsSynthesized)
            {
                continue;
            }

            // Skip routines that have bodies (they will be emitted as 'define' in GenerateFunctionDefinitions)
            string fullName = routine.OwnerType != null ? $"{routine.OwnerType.Name}.{routine.Name}" : routine.Name;
            if (routinesWithBodies.Contains(routine.Name) || routinesWithBodies.Contains(fullName))
            {
                continue;
            }

            // Only emit 'declare' for truly external routines
            GenerateFunctionDeclaration(routine);
        }
    }

    /// <summary>
    /// Checks if a routine has any error types in its signature.
    /// </summary>
    private static bool HasErrorTypes(SemanticAnalysis.Symbols.RoutineInfo routine)
    {
        // Check return type
        if (routine.ReturnType?.Category == TypeCategory.Error)
        {
            return true;
        }

        // Check parameter types
        foreach (var param in routine.Parameters)
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
        foreach (var decl in _program.Declarations)
        {
            if (decl is RoutineDeclaration routine)
            {
                GenerateFunctionDefinition(routine);
            }
        }

        // Then, generate stdlib routine definitions only for routines that were
        // declared (i.e., actually referenced). This avoids compiling every stdlib
        // routine, many of which use features not yet supported in codegen (presets, etc.)
        // Use a multi-pass approach: compiling one stdlib routine may reference others
        // (e.g., show() → CStr.__create__), so repeat until no new definitions are added.
        int prevDefCount;
        do
        {
            prevDefCount = _generatedFunctionDefs.Count;

            foreach (var (program, _, module) in _stdlibPrograms)
            {
                foreach (var decl in program.Declarations)
                {
                    if (decl is RoutineDeclaration routine)
                    {
                        // Look up routine info — try multiple keys:
                        // 1. Raw AST name (e.g., "show")
                        // 2. Module-qualified (e.g., "IO.show")
                        // 3. Short name fallback via LookupRoutineByName
                        // 4. Overload-based lookup using AST parameter types
                        var routineInfo = _registry.LookupRoutine(routine.Name);
                        if (routineInfo == null && !string.IsNullOrEmpty(module))
                        {
                            routineInfo = _registry.LookupRoutine($"{module}.{routine.Name}");
                        }
                        if (routineInfo == null)
                        {
                            int dotIdx = routine.Name.IndexOf('.');
                            if (dotIdx > 0)
                            {
                                string shortName = routine.Name[(dotIdx + 1)..];
                                routineInfo = _registry.LookupRoutine(shortName)
                                              ?? _registry.LookupRoutineByName(shortName);
                            }
                            else
                            {
                                routineInfo = _registry.LookupRoutineByName(routine.Name);
                            }
                        }

                        // For overloaded routines (e.g., __create__), try to find the
                        // specific overload matching this AST declaration's parameter types
                        if (routineInfo != null && routine.Parameters.Count > 0)
                        {
                            var astParamTypes = new List<SemanticAnalysis.Types.TypeInfo>();
                            foreach (var param in routine.Parameters)
                            {
                                if (param.Type != null)
                                {
                                    string typeName = param.Type.Name;
                                    if (param.Type.GenericArguments is { Count: > 0 })
                                        typeName = $"{typeName}[{string.Join(", ", param.Type.GenericArguments.Select(a => a.Name))}]";
                                    var t = _registry.LookupType(typeName);
                                    if (t != null) astParamTypes.Add(t);
                                }
                            }
                            if (astParamTypes.Count == routine.Parameters.Count)
                            {
                                var overload = _registry.LookupRoutineOverload(
                                    routineInfo.FullName, astParamTypes);
                                if (overload != null)
                                    routineInfo = overload;
                            }
                        }
                        if (routineInfo == null || routineInfo.IsGenericDefinition)
                            continue;
                        if (HasErrorTypes(routineInfo))
                            continue;

                        // Only generate definitions for routines that were declared
                        string funcName = MangleFunctionName(routineInfo);
                        if (!_generatedFunctions.Contains(funcName))
                            continue;

                        // Skip if already defined
                        if (_generatedFunctionDefs.Contains(funcName))
                            continue;

                        try
                        {
                            GenerateFunctionDefinition(routine, routineInfo);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Warning: Stdlib codegen failed for '{routine.Name}': {ex.Message}");
                        }
                    }
                }
            }
        } while (_generatedFunctionDefs.Count > prevDefCount);

        // Monomorphize generic methods: compile generic AST bodies with type substitutions
        MonomorphizeGenericMethods();

        // Finally, generate bodies for synthesized routines (__ne__, __lt__, __le__, __gt__, __ge__)
        GenerateSynthesizedRoutines();
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
        output.AppendLine("; ModuleID = 'razorforge_module'");
        output.AppendLine("source_filename = \"razorforge_module\"");
        output.AppendLine("target datalayout = \"e-m:w-p270:32:32-p271:32:32-p272:64:64-i64:64-f80:128-n8:16:32:64-S128\"");
        output.AppendLine("target triple = \"x86_64-pc-windows-msvc\"");
        output.AppendLine();

        // Type declarations
        if (_typeDeclarations.Length > 0)
        {
            output.AppendLine("; Type declarations");
            output.Append(_typeDeclarations);
            output.AppendLine();
        }

        // Global declarations
        if (_globalDeclarations.Length > 0)
        {
            output.AppendLine("; Global declarations");
            output.Append(_globalDeclarations);
            output.AppendLine();
        }

        // Function declarations — filter out any that now have definitions
        if (_functionDeclarations.Length > 0)
        {
            output.AppendLine("; Function declarations");
            foreach (string line in _functionDeclarations.ToString().Split('\n'))
            {
                // Skip declare lines for functions that have definitions
                if (line.StartsWith("declare ") && _generatedFunctionDefs.Count > 0)
                {
                    // Extract function name from "declare ... @funcName(...)"
                    int atIdx = line.IndexOf('@');
                    int parenIdx = line.IndexOf('(', atIdx > 0 ? atIdx : 0);
                    if (atIdx > 0 && parenIdx > atIdx)
                    {
                        string declaredName = line[(atIdx + 1)..parenIdx];
                        if (_generatedFunctionDefs.Contains(declaredName))
                            continue; // Skip — this function has a define
                    }
                }
                output.AppendLine(line);
            }
        }

        // Function definitions
        if (_functionDefinitions.Length > 0)
        {
            output.AppendLine("; Function definitions");
            output.Append(_functionDefinitions);
        }

        // Emit main() entry point that calls start()
        if (_generatedFunctionDefs.Contains("start"))
        {
            output.AppendLine("; Entry point");
            output.AppendLine("define i32 @main(i32 %argc, ptr %argv) {");
            output.AppendLine("entry:");
            output.AppendLine("  call void @start()");
            output.AppendLine("  ret i32 0");
            output.AppendLine("}");
        }

        // Normalize to Unix line endings (clang/LLVM requires LF, not CRLF)
        return output.ToString().Replace("\r\n", "\n").Replace("\r", "\n");
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
        sb.AppendLine(line);
        // Auto-track current basic block for PHI node predecessors
        if (line.Length > 0 && line[0] != ' ' && line.EndsWith(':'))
            _currentBlock = line[..^1];
    }

    /// <summary>
    /// Records a pending monomorphization for a resolved generic method call.
    /// Called from EmitMethodCall/EmitGenericMethodCall when a call targets a method on
    /// a resolved generic type (e.g., List[Letter].add_last).
    /// </summary>
    private void RecordMonomorphization(string mangledName, RoutineInfo genericMethod, TypeInfo resolvedOwnerType)
    {
        if (_pendingMonomorphizations.ContainsKey(mangledName))
            return;

        // Generic parameter owner methods (e.g., routine T.view() called on Point)
        // The owner is GenericParameterTypeInfo("T"), resolved to a concrete type
        if (genericMethod.OwnerType is GenericParameterTypeInfo genParam)
        {
            var typeSubs = new Dictionary<string, TypeInfo>
            {
                [genParam.Name] = resolvedOwnerType
            };
            string genericAstName = $"{genParam.Name}.{genericMethod.Name}";
            _pendingMonomorphizations[mangledName] = new MonomorphizationEntry(
                genericMethod, resolvedOwnerType, typeSubs, genericAstName);
            return;
        }

        // Build the generic AST name: "List[T].add_last" from owner's generic definition name + method name
        TypeInfo? genericDef = resolvedOwnerType switch
        {
            EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition,
            RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition,
            ResidentTypeInfo { GenericDefinition: not null } res => res.GenericDefinition,
            _ => null
        };

        if (genericDef == null || genericDef.GenericParameters == null)
            return;

        // Build type substitution map: e.g., { "T" → Letter }
        var typeSubs2 = new Dictionary<string, TypeInfo>();
        if (resolvedOwnerType.TypeArguments != null)
        {
            for (int i = 0; i < genericDef.GenericParameters.Count && i < resolvedOwnerType.TypeArguments.Count; i++)
            {
                typeSubs2[genericDef.GenericParameters[i]] = resolvedOwnerType.TypeArguments[i];
            }
        }

        // Build generic AST name: e.g., "List[T].add_last"
        string genericParamList = string.Join(", ", genericDef.GenericParameters);
        string genericAstName2 = $"{genericDef.Name}[{genericParamList}].{genericMethod.Name}";

        _pendingMonomorphizations[mangledName] = new MonomorphizationEntry(
            genericMethod, resolvedOwnerType, typeSubs2, genericAstName2);
    }

    #endregion
}
