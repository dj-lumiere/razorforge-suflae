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
        // Clear local variables and emit slot for this function
        _localVariables.Clear();
        _currentBlock = "entry";
        _emitSlotAddr = null;
        _emitSlotType = null;

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
            EntityTypeInfo or ResidentTypeInfo or WrapperTypeInfo => "null",
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
    /// Generates LLVM IR bodies for all synthesized routines.
    /// These routines are registered by the semantic analyzer (Phase 2.55/2.6) with IsSynthesized = true
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
                case "__eq__":
                    EmitSynthesizedEq(routine, funcName);
                    break;
                case "Text":
                    EmitSynthesizedText(routine, funcName, includeSecret: false);
                    break;
                case "to_debug":
                    EmitSynthesizedText(routine, funcName, includeSecret: true);
                    break;
                case "hash":
                    EmitSynthesizedHash(routine, funcName);
                    break;
                case "S64":
                    EmitSynthesizedIdentityCast(routine, funcName, "i64");
                    break;
                case "U64":
                    EmitSynthesizedIdentityCast(routine, funcName, "i64");
                    break;
                case "id":
                    EmitSynthesizedId(routine, funcName);
                    break;
                case "copy!":
                    EmitSynthesizedCopy(routine, funcName);
                    break;
                case "__create__":
                    EmitSynthesizedTextCreate(routine, funcName);
                    break;
                case "__create__!":
                    EmitSynthesizedChoiceCreateFromText(routine, funcName);
                    break;
                case "__same__":
                    EmitSynthesizedSame(routine, funcName);
                    break;
                case "__notsame__":
                    EmitSynthesizedNotSame(routine, funcName);
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
    /// Emits the body for a synthesized __same__ routine: pointer identity comparison.
    /// </summary>
    private void EmitSynthesizedSame(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);
        string youType = routine.Parameters.Count > 0
            ? GetParameterLLVMType(routine.Parameters[0].Type)
            : meType;

        EmitLine(_functionDefinitions, $"define i1 @{funcName}({meType} %me, {youType} %you) {{");
        EmitLine(_functionDefinitions, "entry:");
        EmitLine(_functionDefinitions, $"  %same = icmp eq {meType} %me, %you");
        EmitLine(_functionDefinitions, "  ret i1 %same");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits the body for a synthesized __notsame__ routine: not __same__(me, you).
    /// </summary>
    private void EmitSynthesizedNotSame(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);
        string youType = routine.Parameters.Count > 0
            ? GetParameterLLVMType(routine.Parameters[0].Type)
            : meType;

        EmitLine(_functionDefinitions, $"define i1 @{funcName}({meType} %me, {youType} %you) {{");
        EmitLine(_functionDefinitions, "entry:");
        EmitLine(_functionDefinitions, $"  %same = icmp ne {meType} %me, %you");
        EmitLine(_functionDefinitions, "  ret i1 %same");
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

    /// <summary>
    /// Emits the body for a synthesized __eq__ routine.
    /// For records: AND chain of field-by-field __eq__ calls (or icmp for primitives).
    /// For entities/residents: field-by-field equality via GEP load + comparison.
    /// </summary>
    private void EmitSynthesizedEq(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);
        string youType = routine.Parameters.Count > 0
            ? GetParameterLLVMType(routine.Parameters[0].Type)
            : meType;
        string youName = routine.Parameters.Count > 0
            ? routine.Parameters[0].Name
            : "you";

        EmitLine(_functionDefinitions, $"define i1 @{funcName}({meType} %me, {youType} %{youName}) {{");
        EmitLine(_functionDefinitions, "entry:");

        // Reset temp counter for clean output
        int savedTempCounter = _tempCounter;

        switch (routine.OwnerType)
        {
            case RecordTypeInfo { HasDirectBackendType: true } record:
            {
                // Single backend type: direct icmp
                string llvmType = record.LlvmType;
                string result = NextTemp();
                EmitLine(_functionDefinitions, $"  {result} = icmp eq {llvmType} %me, %{youName}");
                EmitLine(_functionDefinitions, $"  ret i1 {result}");
                break;
            }
            case RecordTypeInfo { IsSingleMemberVariableWrapper: true } record:
            {
                // Single-member-variable wrapper: compare the underlying value
                string llvmType = GetLLVMType(record.UnderlyingIntrinsic!);
                string result = NextTemp();
                EmitLine(_functionDefinitions, $"  {result} = icmp eq {llvmType} %me, %{youName}");
                EmitLine(_functionDefinitions, $"  ret i1 {result}");
                break;
            }
            case RecordTypeInfo record:
            {
                // Multi-field record: extractvalue each field, compare, AND chain
                if (record.MemberVariables.Count == 0)
                {
                    EmitLine(_functionDefinitions, "  ret i1 true");
                    break;
                }

                string typeName = GetRecordTypeName(record);
                string accum = "true";
                for (int i = 0; i < record.MemberVariables.Count; i++)
                {
                    var mv = record.MemberVariables[i];
                    string fieldType = GetLLVMType(mv.Type);
                    string meField = NextTemp();
                    string youField = NextTemp();
                    EmitLine(_functionDefinitions, $"  {meField} = extractvalue {typeName} %me, {i}");
                    EmitLine(_functionDefinitions, $"  {youField} = extractvalue {typeName} %{youName}, {i}");

                    string cmpResult = EmitFieldEquality(mv.Type, fieldType, meField, youField);

                    if (accum == "true")
                    {
                        accum = cmpResult;
                    }
                    else
                    {
                        string andResult = NextTemp();
                        EmitLine(_functionDefinitions, $"  {andResult} = and i1 {accum}, {cmpResult}");
                        accum = andResult;
                    }
                }
                EmitLine(_functionDefinitions, $"  ret i1 {accum}");
                break;
            }
            case EntityTypeInfo entity:
            {
                // Entity: GEP + load each field from both pointers, compare
                if (entity.MemberVariables.Count == 0)
                {
                    // No fields — pointer equality
                    string result = NextTemp();
                    EmitLine(_functionDefinitions, $"  {result} = icmp eq ptr %me, %{youName}");
                    EmitLine(_functionDefinitions, $"  ret i1 {result}");
                    break;
                }

                string typeName = GetEntityTypeName(entity);
                string accum = "true";
                for (int i = 0; i < entity.MemberVariables.Count; i++)
                {
                    var mv = entity.MemberVariables[i];
                    string fieldType = GetLLVMType(mv.Type);
                    string meFp = NextTemp();
                    string youFp = NextTemp();
                    string meField = NextTemp();
                    string youField = NextTemp();
                    EmitLine(_functionDefinitions, $"  {meFp} = getelementptr {typeName}, ptr %me, i32 0, i32 {i}");
                    EmitLine(_functionDefinitions, $"  {meField} = load {fieldType}, ptr {meFp}");
                    EmitLine(_functionDefinitions, $"  {youFp} = getelementptr {typeName}, ptr %{youName}, i32 0, i32 {i}");
                    EmitLine(_functionDefinitions, $"  {youField} = load {fieldType}, ptr {youFp}");

                    string cmpResult = EmitFieldEquality(mv.Type, fieldType, meField, youField);

                    if (accum == "true")
                    {
                        accum = cmpResult;
                    }
                    else
                    {
                        string andResult = NextTemp();
                        EmitLine(_functionDefinitions, $"  {andResult} = and i1 {accum}, {cmpResult}");
                        accum = andResult;
                    }
                }
                EmitLine(_functionDefinitions, $"  ret i1 {accum}");
                break;
            }
            case ResidentTypeInfo resident:
            {
                // Resident: same as entity (GEP + load)
                if (resident.MemberVariables.Count == 0)
                {
                    string result = NextTemp();
                    EmitLine(_functionDefinitions, $"  {result} = icmp eq ptr %me, %{youName}");
                    EmitLine(_functionDefinitions, $"  ret i1 {result}");
                    break;
                }

                string typeName = GetResidentTypeName(resident);
                string accum = "true";
                for (int i = 0; i < resident.MemberVariables.Count; i++)
                {
                    var mv = resident.MemberVariables[i];
                    string fieldType = GetLLVMType(mv.Type);
                    string meFp = NextTemp();
                    string youFp = NextTemp();
                    string meField = NextTemp();
                    string youField = NextTemp();
                    EmitLine(_functionDefinitions, $"  {meFp} = getelementptr {typeName}, ptr %me, i32 0, i32 {i}");
                    EmitLine(_functionDefinitions, $"  {meField} = load {fieldType}, ptr {meFp}");
                    EmitLine(_functionDefinitions, $"  {youFp} = getelementptr {typeName}, ptr %{youName}, i32 0, i32 {i}");
                    EmitLine(_functionDefinitions, $"  {youField} = load {fieldType}, ptr {youFp}");

                    string cmpResult = EmitFieldEquality(mv.Type, fieldType, meField, youField);

                    if (accum == "true")
                    {
                        accum = cmpResult;
                    }
                    else
                    {
                        string andResult = NextTemp();
                        EmitLine(_functionDefinitions, $"  {andResult} = and i1 {accum}, {cmpResult}");
                        accum = andResult;
                    }
                }
                EmitLine(_functionDefinitions, $"  ret i1 {accum}");
                break;
            }
            default:
                EmitLine(_functionDefinitions, "  ret i1 false");
                break;
        }

        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits a field-level equality comparison.
    /// For primitive/backend types: icmp eq. For complex types: call __eq__.
    /// </summary>
    private string EmitFieldEquality(TypeInfo fieldType, string llvmType, string meField, string youField)
    {
        // Primitive/intrinsic types and backend-annotated records use icmp/fcmp
        if (fieldType is IntrinsicTypeInfo ||
            fieldType is RecordTypeInfo { HasDirectBackendType: true } ||
            fieldType is RecordTypeInfo { IsSingleMemberVariableWrapper: true } ||
            fieldType is ChoiceTypeInfo)
        {
            string cmpResult = NextTemp();
            if (llvmType is "float" or "double" or "half" or "fp128")
                EmitLine(_functionDefinitions, $"  {cmpResult} = fcmp oeq {llvmType} {meField}, {youField}");
            else
                EmitLine(_functionDefinitions, $"  {cmpResult} = icmp eq {llvmType} {meField}, {youField}");
            return cmpResult;
        }

        // Entity/resident types: pointer equality
        if (fieldType is EntityTypeInfo or ResidentTypeInfo)
        {
            string cmpResult = NextTemp();
            EmitLine(_functionDefinitions, $"  {cmpResult} = icmp eq ptr {meField}, {youField}");
            return cmpResult;
        }

        // Complex types: call their __eq__ method
        string eqName = $"{MangleTypeName(fieldType.Name)}___eq__";
        string result = NextTemp();
        EmitLine(_functionDefinitions, $"  {result} = call i1 @{eqName}({llvmType} {meField}, {llvmType} {youField})");
        return result;
    }

    /// <summary>
    /// Emits the body for a synthesized Text() or to_debug() routine.
    /// Concatenates "TypeName(" + field.Text() calls + ")" via Text.concat.
    /// to_debug includes secret fields; Text() excludes them.
    /// </summary>
    private void EmitSynthesizedText(RoutineInfo routine, string funcName, bool includeSecret)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);

        EmitLine(_functionDefinitions, $"define ptr @{funcName}({meType} %me) {{");
        EmitLine(_functionDefinitions, "entry:");

        int savedTempCounter = _tempCounter;

        string typeName = routine.OwnerType.Name;

        // Get fields to include
        IReadOnlyList<MemberVariableInfo>? fields = routine.OwnerType switch
        {
            RecordTypeInfo rec => rec.MemberVariables,
            EntityTypeInfo ent => ent.MemberVariables,
            ResidentTypeInfo res => res.MemberVariables,
            _ => null
        };

        // For choice/flags, just return the type name + tag value as text
        if (routine.OwnerType is ChoiceTypeInfo or FlagsTypeInfo || fields == null)
        {
            // Return "TypeName"
            string nameStr = EmitSynthesizedStringLiteral(typeName);
            EmitLine(_functionDefinitions, $"  ret ptr {nameStr}");
            EmitLine(_functionDefinitions, "}");
            EmitLine(_functionDefinitions, "");
            return;
        }

        // Filter out secret fields for Text() (but include for to_debug)
        var visibleFields = new List<(MemberVariableInfo MV, int Index)>();
        for (int i = 0; i < fields.Count; i++)
        {
            if (includeSecret || fields[i].Visibility != VisibilityModifier.Secret)
                visibleFields.Add((fields[i], i));
        }

        // Build "TypeName(field1=..., field2=...)"
        // Start with "TypeName("
        string prefix = $"{typeName}(";
        string current = EmitSynthesizedStringLiteral(prefix);

        for (int vi = 0; vi < visibleFields.Count; vi++)
        {
            var (mv, fieldIdx) = visibleFields[vi];
            string fieldType = GetLLVMType(mv.Type);

            // Add "fieldName=" prefix
            string fieldPrefix = vi > 0 ? $", {mv.Name}=" : $"{mv.Name}=";
            string fieldPrefixStr = EmitSynthesizedStringLiteral(fieldPrefix);

            // Concat field prefix
            string withPrefix = NextTemp();
            EmitLine(_functionDefinitions, $"  {withPrefix} = call ptr @Text_concat(ptr {current}, ptr {fieldPrefixStr})");
            current = withPrefix;

            // Get field value
            string fieldValue;
            switch (routine.OwnerType)
            {
                case RecordTypeInfo { HasDirectBackendType: true }:
                case RecordTypeInfo { IsSingleMemberVariableWrapper: true }:
                    fieldValue = "%me";
                    break;
                case RecordTypeInfo rec:
                {
                    string recTypeName = GetRecordTypeName(rec);
                    fieldValue = NextTemp();
                    EmitLine(_functionDefinitions, $"  {fieldValue} = extractvalue {recTypeName} %me, {fieldIdx}");
                    break;
                }
                case EntityTypeInfo ent:
                {
                    string entTypeName = GetEntityTypeName(ent);
                    string fp = NextTemp();
                    EmitLine(_functionDefinitions, $"  {fp} = getelementptr {entTypeName}, ptr %me, i32 0, i32 {fieldIdx}");
                    fieldValue = NextTemp();
                    EmitLine(_functionDefinitions, $"  {fieldValue} = load {fieldType}, ptr {fp}");
                    break;
                }
                case ResidentTypeInfo res:
                {
                    string resTypeName = GetResidentTypeName(res);
                    string fp = NextTemp();
                    EmitLine(_functionDefinitions, $"  {fp} = getelementptr {resTypeName}, ptr %me, i32 0, i32 {fieldIdx}");
                    fieldValue = NextTemp();
                    EmitLine(_functionDefinitions, $"  {fieldValue} = load {fieldType}, ptr {fp}");
                    break;
                }
                default:
                    fieldValue = "%me";
                    break;
            }

            // Convert field value to Text via Text.__create__ or .Text()
            string fieldText = EmitSynthesizedValueToText(mv.Type, fieldType, fieldValue);

            // Concat field text
            string withField = NextTemp();
            EmitLine(_functionDefinitions, $"  {withField} = call ptr @Text_concat(ptr {current}, ptr {fieldText})");
            current = withField;
        }

        // Append closing ")"
        string suffix = EmitSynthesizedStringLiteral(")");
        string result = NextTemp();
        EmitLine(_functionDefinitions, $"  {result} = call ptr @Text_concat(ptr {current}, ptr {suffix})");

        EmitLine(_functionDefinitions, $"  ret ptr {result}");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits code to convert a value to Text for synthesized Text()/to_debug() routines.
    /// Calls Text.__create__(from: T) on the value's type.
    /// </summary>
    private string EmitSynthesizedValueToText(TypeInfo fieldType, string llvmType, string value)
    {
        string createName = $"Text___create__";
        string textResult = NextTemp();
        EmitLine(_functionDefinitions, $"  {textResult} = call ptr @{createName}({llvmType} {value})");
        return textResult;
    }

    /// <summary>
    /// Emits a string literal for use in synthesized routines and returns its global name.
    /// </summary>
    private string EmitSynthesizedStringLiteral(string value)
    {
        if (_stringConstants.TryGetValue(value, out string? existing))
            return existing;

        string constName = $"@.str.{_stringCounter++}";
        _stringConstants[value] = constName;

        string escaped = EscapeStringForLLVM(value);
        int byteLength = System.Text.Encoding.UTF8.GetByteCount(value) + 1;

        EmitLine(_globalDeclarations, $"{constName} = private unnamed_addr constant [{byteLength} x i8] c\"{escaped}\\00\"");
        return constName;
    }

    /// <summary>
    /// Emits the body for a synthesized hash() routine.
    /// Record: XOR chain of field.hash() calls.
    /// Choice/Flags: value * 2654435761 (Knuth multiplicative hash).
    /// </summary>
    private void EmitSynthesizedHash(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);

        EmitLine(_functionDefinitions, $"define i64 @{funcName}({meType} %me) {{");
        EmitLine(_functionDefinitions, "entry:");

        switch (routine.OwnerType)
        {
            case ChoiceTypeInfo:
            case FlagsTypeInfo:
            {
                // hash = me * 2654435761 (Knuth multiplicative hash constant)
                string result = NextTemp();
                EmitLine(_functionDefinitions, $"  {result} = mul i64 %me, 2654435761");
                EmitLine(_functionDefinitions, $"  ret i64 {result}");
                break;
            }
            case RecordTypeInfo { HasDirectBackendType: true }:
            case RecordTypeInfo { IsSingleMemberVariableWrapper: true }:
            {
                // Single-value: call hash on the value directly
                string hashName = $"{MangleTypeName(routine.OwnerType.Name)}_hash";
                // For primitive wrappers, hash the underlying value
                string result = NextTemp();
                EmitLine(_functionDefinitions, $"  {result} = mul i64 %me, 2654435761");
                EmitLine(_functionDefinitions, $"  ret i64 {result}");
                break;
            }
            case RecordTypeInfo record:
            {
                if (record.MemberVariables.Count == 0)
                {
                    EmitLine(_functionDefinitions, "  ret i64 0");
                    break;
                }

                string typeName = GetRecordTypeName(record);
                string accum = "0";
                for (int i = 0; i < record.MemberVariables.Count; i++)
                {
                    var mv = record.MemberVariables[i];
                    string fieldType = GetLLVMType(mv.Type);
                    string field = NextTemp();
                    EmitLine(_functionDefinitions, $"  {field} = extractvalue {typeName} %me, {i}");

                    // Call field.hash()
                    string fieldHashName = $"{MangleTypeName(mv.Type.Name)}_hash";
                    string fieldHash = NextTemp();
                    EmitLine(_functionDefinitions, $"  {fieldHash} = call i64 @{fieldHashName}({fieldType} {field})");

                    if (accum == "0")
                    {
                        accum = fieldHash;
                    }
                    else
                    {
                        string xorResult = NextTemp();
                        EmitLine(_functionDefinitions, $"  {xorResult} = xor i64 {accum}, {fieldHash}");
                        accum = xorResult;
                    }
                }
                EmitLine(_functionDefinitions, $"  ret i64 {accum}");
                break;
            }
            default:
                EmitLine(_functionDefinitions, "  ret i64 0");
                break;
        }

        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits the body for a synthesized S64() or U64() routine.
    /// Choice/Flags are backed by i64, so just return %me directly.
    /// </summary>
    private void EmitSynthesizedIdentityCast(RoutineInfo routine, string funcName, string llvmRetType)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);

        EmitLine(_functionDefinitions, $"define {llvmRetType} @{funcName}({meType} %me) {{");
        EmitLine(_functionDefinitions, "entry:");
        EmitLine(_functionDefinitions, $"  ret {llvmRetType} %me");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits the body for a synthesized id() routine on entities/residents.
    /// Returns the pointer cast to i64.
    /// </summary>
    private void EmitSynthesizedId(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        EmitLine(_functionDefinitions, $"define i64 @{funcName}(ptr %me) {{");
        EmitLine(_functionDefinitions, "entry:");
        string result = NextTemp();
        EmitLine(_functionDefinitions, $"  {result} = ptrtoint ptr %me to i64");
        EmitLine(_functionDefinitions, $"  ret i64 {result}");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits the body for a synthesized copy!() routine on entities.
    /// Allocates new memory and copies all fields via GEP load/store.
    /// </summary>
    private void EmitSynthesizedCopy(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType is not EntityTypeInfo entity) return;

        string typeName = GetEntityTypeName(entity);
        int size = CalculateEntitySize(entity);

        EmitLine(_functionDefinitions, $"define ptr @{funcName}(ptr %me) {{");
        EmitLine(_functionDefinitions, "entry:");

        // Allocate new entity
        string newPtr = NextTemp();
        EmitLine(_functionDefinitions, $"  {newPtr} = call ptr @rf_alloc(i64 {size})");

        // Copy each field
        for (int i = 0; i < entity.MemberVariables.Count; i++)
        {
            var mv = entity.MemberVariables[i];
            string fieldType = GetLLVMType(mv.Type);

            // Load from source
            string srcFp = NextTemp();
            EmitLine(_functionDefinitions, $"  {srcFp} = getelementptr {typeName}, ptr %me, i32 0, i32 {i}");
            string val = NextTemp();
            EmitLine(_functionDefinitions, $"  {val} = load {fieldType}, ptr {srcFp}");

            // Store to dest
            string dstFp = NextTemp();
            EmitLine(_functionDefinitions, $"  {dstFp} = getelementptr {typeName}, ptr {newPtr}, i32 0, i32 {i}");
            EmitLine(_functionDefinitions, $"  store {fieldType} {val}, ptr {dstFp}");
        }

        EmitLine(_functionDefinitions, $"  ret ptr {newPtr}");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits the body for a synthesized Text.__create__(from: T) routine.
    /// Calls T.Text() on the argument.
    /// </summary>
    private void EmitSynthesizedTextCreate(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null || routine.Parameters.Count == 0) return;

        var paramType = routine.Parameters[0].Type;
        string paramLlvmType = GetParameterLLVMType(paramType);
        string paramName = routine.Parameters[0].Name;

        // Text.__create__(from: T) → T.Text()
        string textMethodName = $"{MangleTypeName(paramType.Name)}_Text";

        EmitLine(_functionDefinitions, $"define ptr @{funcName}(ptr %me, {paramLlvmType} %{paramName}) {{");
        EmitLine(_functionDefinitions, "entry:");
        string result = NextTemp();
        EmitLine(_functionDefinitions, $"  {result} = call ptr @{textMethodName}({paramLlvmType} %{paramName})");
        EmitLine(_functionDefinitions, $"  ret ptr {result}");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits the body for a synthesized __create__!(from: Text) on choice types.
    /// For now: traps (full text→tag requires runtime string comparison).
    /// </summary>
    private void EmitSynthesizedChoiceCreateFromText(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);

        EmitLine(_functionDefinitions, $"define i64 @{funcName}({meType} %me, ptr %from) {{");
        EmitLine(_functionDefinitions, "entry:");

        // Declare llvm.trap if not already declared
        if (_declaredNativeFunctions.Add("llvm.trap"))
        {
            EmitLine(_functionDeclarations, "declare void @llvm.trap() noreturn nounwind");
        }

        EmitLine(_functionDefinitions, "  call void @llvm.trap()");
        EmitLine(_functionDefinitions, "  unreachable");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    #endregion
}
