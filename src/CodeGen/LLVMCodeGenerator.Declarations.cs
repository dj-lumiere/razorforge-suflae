namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Symbols;
using SemanticAnalysis.Types;
using SyntaxTree;

/// <summary>
/// Type and function declaration generation.
/// </summary>
public partial class LLVMCodeGenerator
{
    #region Entity Type Generation

    /// <summary>
    /// Generates the LLVM struct type for an entity.
    /// Entity = reference type, heap-allocated.
    /// Variables of entity type are pointers to this struct.
    /// </summary>
    /// <param name="entity">The entity type info.</param>
    private void GenerateEntityType(EntityTypeInfo entity)
    {
        string typeName = GetEntityTypeName(entity);

        // Skip if already generated
        if (_generatedTypes.Contains(typeName))
        {
            return;
        }
        _generatedTypes.Add(typeName);

        // Build the struct type
        var memberVariableTypes = new List<string>();
        foreach (var memberVariable in entity.MemberVariables)
        {
            string memberVariableType = GetLLVMType(memberVariable.Type);
            memberVariableTypes.Add(memberVariableType);
        }

        // Handle empty entities (no member variables)
        if (memberVariableTypes.Count == 0)
        {
            // Empty struct needs at least a dummy byte for addressability
            EmitLine(_typeDeclarations, $"{typeName} = type {{ i8 }}");
        }
        else
        {
            string memberVars = string.Join(", ", memberVariableTypes);
            EmitLine(_typeDeclarations, $"{typeName} = type {{ {memberVars} }}");
        }

        // Add member variable comment for documentation
        if (entity.MemberVariables.Count > 0)
        {
            var sb = new StringBuilder();
            sb.Append($"; {typeName} member variables: ");
            for (int i = 0; i < entity.MemberVariables.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append($"{i}={entity.MemberVariables[i].Name}");
            }
            EmitLine(_typeDeclarations, sb.ToString());
        }
    }

    #endregion

    #region Record Type Generation

    /// <summary>
    /// Generates the LLVM struct type for a record.
    /// Record = value type, stack-allocated, copy semantics.
    /// Single-member-variable wrappers are unwrapped to their underlying intrinsic.
    /// </summary>
    /// <param name="record">The record type info.</param>
    private void GenerateRecordType(RecordTypeInfo record)
    {
        // Backend-annotated and single-member-variable wrappers don't need struct types
        if (record.HasDirectBackendType || record.IsSingleMemberVariableWrapper)
        {
            return;
        }

        string typeName = GetRecordTypeName(record);

        // Skip if already generated
        if (!_generatedTypes.Add(typeName))
        {
            return;
        }

        // Build the struct type
        var memberVariableTypes = new List<string>();
        foreach (var memberVariable in record.MemberVariables)
        {
            string memberVariableType = GetLLVMType(memberVariable.Type);
            memberVariableTypes.Add(memberVariableType);
        }

        // Handle empty records
        if (memberVariableTypes.Count == 0)
        {
            EmitLine(_typeDeclarations, $"{typeName} = type {{ }}");
        }
        else
        {
            string memberVars = string.Join(", ", memberVariableTypes);
            EmitLine(_typeDeclarations, $"{typeName} = type {{ {memberVars} }}");
        }

        // Add member variable comment
        if (record.MemberVariables.Count > 0)
        {
            var sb = new StringBuilder();
            sb.Append($"; {typeName} member variables: ");
            for (int i = 0; i < record.MemberVariables.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append($"{i}={record.MemberVariables[i].Name}");
            }
            EmitLine(_typeDeclarations, sb.ToString());
        }
    }

    #endregion

    #region Resident Type Generation

    /// <summary>
    /// Generates the LLVM struct type for a resident.
    /// Resident = fixed-size reference type, persistent memory.
    /// Like entity but with compile-time known size.
    /// </summary>
    /// <param name="resident">The resident type info.</param>
    private void GenerateResidentType(ResidentTypeInfo resident)
    {
        string typeName = GetResidentTypeName(resident);

        // Skip if already generated
        if (_generatedTypes.Contains(typeName))
        {
            return;
        }
        _generatedTypes.Add(typeName);

        // Build the struct type
        var memberVariableTypes = new List<string>();
        foreach (var memberVariable in resident.MemberVariables)
        {
            string memberVariableType = GetLLVMType(memberVariable.Type);
            memberVariableTypes.Add(memberVariableType);
        }

        // Handle empty residents
        if (memberVariableTypes.Count == 0)
        {
            EmitLine(_typeDeclarations, $"{typeName} = type {{ i8 }}");
        }
        else
        {
            string memberVars = string.Join(", ", memberVariableTypes);
            EmitLine(_typeDeclarations, $"{typeName} = type {{ {memberVars} }}");
        }

        // Add member variable comment
        if (resident.MemberVariables.Count > 0)
        {
            var sb = new StringBuilder();
            sb.Append($"; {typeName} member variables: ");
            for (int i = 0; i < resident.MemberVariables.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append($"{i}={resident.MemberVariables[i].Name}");
            }
            EmitLine(_typeDeclarations, sb.ToString());
        }
    }

    #endregion

    #region Choice Type Generation

    /// <summary>
    /// Generates the LLVM type for a choice (enum).
    /// Choice = record with single integer member variable (tag value).
    /// </summary>
    /// <param name="choice">The choice type info.</param>
    private void GenerateChoiceType(ChoiceTypeInfo choice)
    {
        string typeName = GetChoiceTypeName(choice);

        // Skip if already generated
        if (_generatedTypes.Contains(typeName))
        {
            return;
        }
        _generatedTypes.Add(typeName);

        // Choice is just an i64 (tag value)
        EmitLine(_typeDeclarations, $"{typeName} = type {{ i64 }}");

        // Add case values as comments
        var sb = new StringBuilder();
        sb.Append($"; {typeName} cases: ");
        for (int i = 0; i < choice.Cases.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var c = choice.Cases[i];
            sb.Append($"{c.Name}={c.ComputedValue}");
        }
        EmitLine(_typeDeclarations, sb.ToString());
    }

    #endregion

    #region Variant Type Generation

    /// <summary>
    /// Generates the LLVM type for a variant (tagged union).
    /// Variant = record with tag (i32) + payload (sized to largest case).
    /// </summary>
    /// <param name="variant">The variant type info.</param>
    private void GenerateVariantType(VariantTypeInfo variant)
    {
        string typeName = GetVariantTypeName(variant);

        // Skip if already generated
        if (_generatedTypes.Contains(typeName))
        {
            return;
        }
        _generatedTypes.Add(typeName);

        // Calculate max payload size
        int maxPayloadSize = 0;
        foreach (var variantCase in variant.Cases)
        {
            if (variantCase.PayloadType != null)
            {
                int payloadSize = GetTypeSize(variantCase.PayloadType);
                maxPayloadSize = Math.Max(maxPayloadSize, payloadSize);
            }
        }

        // Variant is { i32 tag, [N x i8] payload }
        // We use i8 array for the union to allow any payload type
        if (maxPayloadSize > 0)
        {
            EmitLine(_typeDeclarations, $"{typeName} = type {{ i32, [{maxPayloadSize} x i8] }}");
        }
        else
        {
            // No payloads - just the tag
            EmitLine(_typeDeclarations, $"{typeName} = type {{ i32 }}");
        }

        // Add case info as comments
        var sb = new StringBuilder();
        sb.Append($"; {typeName} cases: ");
        for (int i = 0; i < variant.Cases.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var c = variant.Cases[i];
            if (c.PayloadType != null)
            {
                sb.Append($"{c.Name}({c.PayloadType.Name})={c.TagValue}");
            }
            else
            {
                sb.Append($"{c.Name}={c.TagValue}");
            }
        }
        EmitLine(_typeDeclarations, sb.ToString());
    }

    #endregion

    #region Function Declaration Generation

    /// <summary>
    /// Generates the LLVM function declaration (signature only).
    /// </summary>
    /// <param name="routine">The routine info.</param>
    private void GenerateFunctionDeclaration(RoutineInfo routine)
    {
        string funcName = MangleFunctionName(routine);

        // Skip if already generated
        if (_generatedFunctions.Contains(funcName))
        {
            return;
        }
        _generatedFunctions.Add(funcName);

        // Build parameter list
        var paramTypes = new List<string>();

        // For methods, add implicit 'me' parameter first
        if (routine.OwnerType != null)
        {
            string meType = GetParameterLLVMType(routine.OwnerType);
            paramTypes.Add(meType);
        }

        // Add explicit parameters
        paramTypes.AddRange(routine.Parameters.Select(param => GetParameterLLVMType(param.Type)));

        // Get return type
        string returnType = routine.ReturnType != null
            ? GetLLVMType(routine.ReturnType)
            : "void";

        // Build declaration
        string parameters = string.Join(", ", paramTypes);
        EmitLine(_functionDeclarations, $"declare {returnType} @{funcName}({parameters})");
    }

    /// <summary>
    /// Generates the LLVM function definition (with body).
    /// </summary>
    /// <param name="routine">The routine declaration from AST.</param>
    private void GenerateFunctionDefinition(RoutineDeclaration routine)
    {
        // Look up the routine info from registry
        string baseName = routine.Name;
        RoutineInfo? routineInfo = _registry.LookupRoutine(baseName);

        if (routineInfo == null || routineInfo.IsGenericDefinition)
        {
            return; // Skip generic definitions or unresolved routines
        }

        // Skip routines with error types in their signature
        if (HasErrorTypes(routineInfo))
        {
            return;
        }

        string funcName = MangleFunctionName(routineInfo);

        // Skip if already generated (prevents duplicates between user program and stdlib)
        if (!_generatedFunctionDefs.Add(funcName))
        {
            return;
        }

        // Also mark as generated in declarations set to prevent declare/define conflicts
        _generatedFunctions.Add(funcName);

        // Build parameter list with names
        var paramList = new List<string>();

        // For methods, add implicit 'me' parameter first
        if (routineInfo.OwnerType != null)
        {
            string meType = GetParameterLLVMType(routineInfo.OwnerType);
            paramList.Add($"{meType} %me");
        }

        // Add explicit parameters
        paramList.AddRange(from param in routineInfo.Parameters let paramType = GetParameterLLVMType(param.Type) select $"{paramType} %{param.Name}");

        // Get return type
        string returnType = routineInfo.ReturnType != null
            ? GetLLVMType(routineInfo.ReturnType)
            : "void";

        // Start function
        string parameters = string.Join(", ", paramList);
        EmitLine(_functionDefinitions, $"define {returnType} @{funcName}({parameters}) {{");
        EmitLine(_functionDefinitions, "entry:");

        // Generate body
        GenerateFunctionBody(routine.Body, routineInfo);

        // End function
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Generates code for a function body.
    /// Emits statements and ensures proper termination.
    /// </summary>
    private void GenerateFunctionBody(Statement body, RoutineInfo routine)
    {
        // Clear local variables for this function
        _localVariables.Clear();
        _currentBlock = "entry";

        // Set current function return type for use in EmitReturn
        _currentFunctionReturnType = routine.ReturnType;

        // Register parameters as local variables
        foreach (var param in routine.Parameters)
        {
            // Parameters are passed by value, create a local copy
            string paramPtr = $"%{param.Name}.addr";
            string llvmType = GetLLVMType(param.Type);
            EmitLine(_functionDefinitions, $"  {paramPtr} = alloca {llvmType}");
            EmitLine(_functionDefinitions, $"  store {llvmType} %{param.Name}, ptr {paramPtr}");
            _localVariables[param.Name] = param.Type;
        }

        // Emit the body statements
        EmitStatement(_functionDefinitions, body);

        // Ensure function is properly terminated
        // Check if the last instruction was a terminator (ret, br, etc.)
        // If not, add a default return
        if (EndsWithTerminator(_functionDefinitions))
        {
            return;
        }

        if (routine.ReturnType == null)
        {
            EmitLine(_functionDefinitions, "  ret void");
        }
        else
        {
            string returnType = GetLLVMType(routine.ReturnType);
            string zeroValue = GetZeroValue(routine.ReturnType);
            EmitLine(_functionDefinitions, $"  ret {returnType} {zeroValue}");
        }
    }

    /// <summary>
    /// Checks if the StringBuilder ends with a terminator instruction.
    /// </summary>
    private static bool EndsWithTerminator(StringBuilder sb)
    {
        string content = sb.ToString();
        string[] lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return false;

        string lastLine = lines[^1].Trim();
        return lastLine.StartsWith("ret ") ||
               lastLine.StartsWith("br ") ||
               lastLine.StartsWith("unreachable");
    }

    /// <summary>
    /// Gets the zero/default value for a type.
    /// </summary>
    private string GetZeroValue(TypeInfo type)
    {
        return type switch
        {
            IntrinsicTypeInfo intrinsic => intrinsic.Name switch
            {
                "@intrinsic.i1" => "false",
                "@intrinsic.f16" or "@intrinsic.f32" or "@intrinsic.f64" or "@intrinsic.f128" => "0.0",
                "@intrinsic.ptr" => "null",
                _ => "0"
            },
            RecordTypeInfo { HasDirectBackendType: true } record =>
                GetZeroValueForLlvmType(record.BackendType!),
            RecordTypeInfo { IsSingleMemberVariableWrapper: true } record =>
                GetZeroValue(record.UnderlyingIntrinsic!),
            EntityTypeInfo or ResidentTypeInfo => "null",
            _ => "zeroinitializer"
        };
    }

    /// <summary>
    /// Gets the zero value for an LLVM type string (from @llvm annotation).
    /// </summary>
    private static string GetZeroValueForLlvmType(string llvmType)
    {
        return llvmType switch
        {
            "i1" => "false",
            "half" or "float" or "double" or "fp128" => "0.0",
            "ptr" => "null",
            _ => "0"
        };
    }

    /// <summary>
    /// Mangles a function name to be LLVM-compatible.
    /// </summary>
    private static string MangleFunctionName(RoutineInfo routine)
    {
        if (routine.OwnerType == null)
        {
            return routine.Name;
        }

        string typeName = MangleTypeName(routine.OwnerType.Name);
        // Use underscore separator for C ABI compatibility
        return $"{typeName}_{routine.Name}";
    }

    #endregion

    #region Synthesized Routine Generation

    /// <summary>
    /// Generates LLVM IR bodies for synthesized routines (__ne__, __lt__, __le__, __gt__, __ge__).
    /// These routines are registered by the semantic analyzer (Phase 2.6) with IsSynthesized = true
    /// but have no AST body — their IR is emitted directly here.
    /// </summary>
    private void GenerateSynthesizedRoutines()
    {
        foreach (var routine in _registry.GetAllRoutines())
        {
            if (!routine.IsSynthesized)
                continue;

            // Skip generic definitions and routines with error types
            if (routine.IsGenericDefinition || HasErrorTypes(routine))
                continue;

            // Only handle the comparison operators we know how to synthesize
            if (routine.Name is not ("__ne__" or "__lt__" or "__le__" or "__gt__" or "__ge__"))
                continue;

            string funcName = MangleFunctionName(routine);

            // Skip if already generated
            if (!_generatedFunctionDefs.Add(funcName))
                continue;

            // Also mark in declarations set to prevent declare/define conflicts
            _generatedFunctions.Add(funcName);

            switch (routine.Name)
            {
                case "__ne__":
                    EmitSynthesizedNe(routine, funcName);
                    break;
                case "__lt__":
                    EmitSynthesizedCmpDerived(routine, funcName, -1, "eq");
                    break;
                case "__le__":
                    EmitSynthesizedCmpDerived(routine, funcName, 1, "ne");
                    break;
                case "__gt__":
                    EmitSynthesizedCmpDerived(routine, funcName, 1, "eq");
                    break;
                case "__ge__":
                    EmitSynthesizedCmpDerived(routine, funcName, -1, "ne");
                    break;
            }
        }
    }

    /// <summary>
    /// Emits the body for a synthesized __ne__ routine: not __eq__(me, you).
    /// </summary>
    private void EmitSynthesizedNe(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);

        // Build parameter types for the 'you' parameter
        string youType = routine.Parameters.Count > 0
            ? GetParameterLLVMType(routine.Parameters[0].Type)
            : meType;
        string youName = routine.Parameters.Count > 0
            ? routine.Parameters[0].Name
            : "you";

        // Look up the __eq__ function name on the same owner type
        string eqFuncName = $"{MangleTypeName(routine.OwnerType.Name)}___eq__";

        EmitLine(_functionDefinitions, $"define i1 @{funcName}({meType} %me, {youType} %{youName}) {{");
        EmitLine(_functionDefinitions, "entry:");
        EmitLine(_functionDefinitions, $"  %eq = call i1 @{eqFuncName}({meType} %me, {youType} %{youName})");
        EmitLine(_functionDefinitions, "  %ne = xor i1 %eq, true");
        EmitLine(_functionDefinitions, "  ret i1 %ne");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits the body for a synthesized comparison operator derived from __cmp__.
    /// E.g., __lt__ = __cmp__(me, you) == -1 (ME_SMALL).
    /// </summary>
    private void EmitSynthesizedCmpDerived(RoutineInfo routine, string funcName, long tagValue, string cmpOp)
    {
        if (routine.OwnerType == null) return;

        // Look up the actual tag value from the registry if ComparisonSign is defined
        long resolvedTag = tagValue;
        string caseName = tagValue switch
        {
            -1 => "ME_SMALL",
            0 => "SAME",
            1 => "ME_LARGE",
            _ => ""
        };
        if (caseName.Length > 0)
        {
            var choiceCase = _registry.LookupChoiceCase(caseName);
            if (choiceCase != null)
            {
                resolvedTag = choiceCase.Value.CaseInfo.ComputedValue;
            }
        }

        string meType = GetParameterLLVMType(routine.OwnerType);

        string youType = routine.Parameters.Count > 0
            ? GetParameterLLVMType(routine.Parameters[0].Type)
            : meType;
        string youName = routine.Parameters.Count > 0
            ? routine.Parameters[0].Name
            : "you";

        // Look up the __cmp__ function name on the same owner type
        string cmpFuncName = $"{MangleTypeName(routine.OwnerType.Name)}___cmp__";

        EmitLine(_functionDefinitions, $"define i1 @{funcName}({meType} %me, {youType} %{youName}) {{");
        EmitLine(_functionDefinitions, "entry:");
        EmitLine(_functionDefinitions, $"  %cmp = call i64 @{cmpFuncName}({meType} %me, {youType} %{youName})");
        EmitLine(_functionDefinitions, $"  %result = icmp {cmpOp} i64 %cmp, {resolvedTag}");
        EmitLine(_functionDefinitions, "  ret i1 %result");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    #endregion
}
