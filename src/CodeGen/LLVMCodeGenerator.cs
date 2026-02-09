using Compilers.Analysis.Enums;

namespace Compilers.CodeGen;

using System.Text;
using Analysis;
using Analysis.Types;
using Shared.AST;

/// <summary>
/// LLVM IR code generator for RazorForge and Suflae.
/// Consumes a fully-typed AST from the semantic analyzer.
/// </summary>
public partial class LLVMCodeGenerator
{
    #region Fields

    /// <summary>The type registry from semantic analysis.</summary>
    private readonly TypeRegistry _registry;

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

    /// <summary>Set of already-generated function definitions to avoid duplicates.</summary>
    private readonly HashSet<string> _generatedFunctionDefs = [];

    /// <summary>The return type of the current function being generated.</summary>
    private TypeInfo? _currentFunctionReturnType;

    #endregion

    #region Constructor

    /// <summary>
    /// Creates a new LLVM code generator.
    /// </summary>
    /// <param name="program">The program AST to generate code for.</param>
    /// <param name="registry">The type registry from semantic analysis.</param>
    /// <param name="stdlibPrograms">Optional stdlib programs for intrinsic routine definitions.</param>
    public LLVMCodeGenerator(Program program, TypeRegistry registry, IReadOnlyList<(Program Program, string FilePath, string Module)>? stdlibPrograms = null)
    {
        _program = program;
        _registry = registry;
        _stdlibPrograms = stdlibPrograms ?? [];
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

        // Generate choice types (enums → single-field records)
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
    /// Only emits 'declare' for external/imported routines that don't have bodies.
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
            // Skip generic definitions and routines with unresolved types
            if (routine.IsGenericDefinition || HasErrorTypes(routine))
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
    private static bool HasErrorTypes(Analysis.Symbols.RoutineInfo routine)
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

        // Then, generate stdlib routine definitions (for intrinsic operations)
        // This allows S64.__add__ etc. to have their bodies compiled
        // We wrap each in try-catch since some stdlib routines may have parse errors
        foreach (var (program, _, _) in _stdlibPrograms)
        {
            foreach (var decl in program.Declarations)
            {
                if (decl is RoutineDeclaration routine)
                {
                    try
                    {
                        GenerateFunctionDefinition(routine);
                    }
                    catch
                    {
                        // Skip stdlib routines that have codegen errors
                        // (likely due to parse errors in stdlib files)
                    }
                }
            }
        }
    }

    /// <summary>
    /// Generates runtime support functions (allocation, deallocation, etc.).
    /// Only declares functions that haven't already been declared via @native.* calls.
    /// </summary>
    private void GenerateRuntimeSupport()
    {
        EmitLine(_functionDeclarations, "; Runtime support functions");

        // Declare only if not already declared via @native.* calls
        if (_declaredNativeFunctions.Add("rf_alloc"))
        {
            EmitLine(_functionDeclarations, "declare ptr @rf_alloc(i64)");
        }
        if (_declaredNativeFunctions.Add("rf_free"))
        {
            EmitLine(_functionDeclarations, "declare void @rf_free(ptr)");
        }
        if (_declaredNativeFunctions.Add("rf_console_print"))
        {
            EmitLine(_functionDeclarations, "declare void @rf_console_print(ptr, i64)");
        }
        if (_declaredNativeFunctions.Add("rf_console_print_line"))
        {
            EmitLine(_functionDeclarations, "declare void @rf_console_print_line(ptr, i64)");
        }

        EmitLine(_functionDeclarations, "");
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

        // Function declarations
        if (_functionDeclarations.Length > 0)
        {
            output.AppendLine("; Function declarations");
            output.Append(_functionDeclarations);
            output.AppendLine();
        }

        // Function definitions
        if (_functionDefinitions.Length > 0)
        {
            output.AppendLine("; Function definitions");
            output.Append(_functionDefinitions);
        }

        return output.ToString();
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
    private static void EmitLine(StringBuilder sb, string line)
    {
        sb.AppendLine(line);
    }

    #endregion
}
