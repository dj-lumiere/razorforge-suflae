namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Enums;
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

        // For generic resolutions with stale empty member variables (created before the generic
        // definition's members were populated), re-create from the now-complete definition.
        if (entity is { IsGenericResolution: true, MemberVariables.Count: 0, GenericDefinition: { MemberVariables.Count: > 0 } genDef }
            && entity.TypeArguments != null)
        {
            var refreshed = genDef.CreateInstance(entity.TypeArguments) as EntityTypeInfo;
            if (refreshed != null && refreshed.MemberVariables.Count > 0)
                entity = refreshed;
        }

        // Recursively ensure struct types for member variable types are defined
        EnsureMemberVariableTypesGenerated(entity.MemberVariables);

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

        // For generic resolutions with stale empty member variables, re-create from the definition
        if (record is { IsGenericResolution: true, MemberVariables.Count: 0, GenericDefinition: { MemberVariables.Count: > 0 } genDef }
            && record.TypeArguments != null)
        {
            var refreshed = genDef.CreateInstance(record.TypeArguments) as RecordTypeInfo;
            if (refreshed != null && refreshed.MemberVariables.Count > 0)
                record = refreshed;
        }

        // Recursively ensure struct types for member variable types are defined
        EnsureMemberVariableTypesGenerated(record.MemberVariables);

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

    /// <summary>
    /// Recursively ensures struct type definitions exist for member variable types.
    /// Handles nested generic resolutions that may not be in the registry (e.g.,
    /// Maybe[BTreeSetNode[S64]] created during member variable substitution).
    /// </summary>
    private void EnsureMemberVariableTypesGenerated(IReadOnlyList<MemberVariableInfo> memberVariables)
    {
        foreach (var mv in memberVariables)
        {
            switch (mv.Type)
            {
                case EntityTypeInfo { IsGenericDefinition: false } nestedEntity:
                    GenerateEntityType(nestedEntity);
                    break;
                case RecordTypeInfo { IsGenericDefinition: false, HasDirectBackendType: false, IsSingleMemberVariableWrapper: false } nestedRecord:
                    GenerateRecordType(nestedRecord);
                    break;
            }
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
    /// Generates the LLVM type for a variant (type-based tagged union).
    /// Variant = { i64 tag, [N x i8] payload } where N = max member size.
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
        foreach (var member in variant.Members)
        {
            if (!member.IsNone && member.Type != null)
            {
                int payloadSize = GetTypeSize(member.Type);
                maxPayloadSize = Math.Max(maxPayloadSize, payloadSize);
            }
        }

        // Variant is { i64 tag, [N x i8] payload }
        if (maxPayloadSize > 0)
        {
            EmitLine(_typeDeclarations, $"{typeName} = type {{ i64, [{maxPayloadSize} x i8] }}");
        }
        else
        {
            // No payloads (all None) - just the tag
            EmitLine(_typeDeclarations, $"{typeName} = type {{ i64 }}");
        }

        // Add member info as comments
        var sb = new StringBuilder();
        sb.Append($"; {typeName} members: ");
        for (int i = 0; i < variant.Members.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var m = variant.Members[i];
            sb.Append($"{m.Name}={m.TagValue}");
        }
        EmitLine(_typeDeclarations, sb.ToString());
    }

    #endregion

    #region Function Declaration Generation

    /// <summary>
    /// Generates the LLVM function declaration (signature only).
    /// </summary>
    /// <param name="routine">The routine info.</param>
    private void GenerateFunctionDeclaration(RoutineInfo routine, string? nameOverride = null)
    {
        string funcName = nameOverride ?? MangleFunctionName(routine);

        // Skip if already generated
        if (_generatedFunctions.Contains(funcName))
        {
            return;
        }

        // Skip declarations that reference unresolved generic parameter types —
        // these produce invalid LLVM IR (e.g., Maybe[BTreeDictNode[K, V]] instead of concrete types)
        if (routine.Parameters.Any(p => p.Type != null && ContainsGenericParameter(p.Type))
            || (routine.ReturnType != null && ContainsGenericParameter(routine.ReturnType))
            || (routine.OwnerType != null && ContainsGenericParameter(routine.OwnerType)))
        {
            return;
        }

        _generatedFunctions.Add(funcName);

        // Build parameter list
        var paramTypes = new List<string>();

        // For methods, add implicit 'me' parameter first
        // Skip 'me' for $create routines (static factories)
        bool isCreator = routine.Name.Contains("$create");
        if (routine.OwnerType != null && !isCreator)
        {
            // $setitem! on records: me is passed by pointer so mutations propagate to caller
            if (IsRecordSetItem(routine))
                paramTypes.Add("ptr");
            else
            {
                string meType = GetParameterLLVMType(routine.OwnerType);
                paramTypes.Add(meType);
            }
        }

        // Add explicit parameters
        // For external("C") functions, F16 (half) must be passed as i16 (C ABI uses integer register)
        bool isCExtern = routine.CallingConvention == "C";
        paramTypes.AddRange(routine.Parameters.Select(param =>
        {
            string t = GetParameterLLVMType(param.Type);
            return isCExtern && t == "half" ? "i16" : t;
        }));

        // Ensure record type definitions exist for parameter and return types
        foreach (var param in routine.Parameters)
        {
            if (param.Type is RecordTypeInfo paramRecord && !paramRecord.HasDirectBackendType
                && !paramRecord.IsSingleMemberVariableWrapper && !paramRecord.IsGenericDefinition)
                GenerateRecordType(paramRecord);
        }
        if (routine.ReturnType is RecordTypeInfo returnRecord && !returnRecord.HasDirectBackendType
            && !returnRecord.IsSingleMemberVariableWrapper && !returnRecord.IsGenericDefinition)
            GenerateRecordType(returnRecord);

        // Get return type
        string returnType = routine.ReturnType != null
            ? GetLLVMType(routine.ReturnType)
            : "void";
        // Emitting and failable routines return Maybe[T] = { i64, ptr } at IR level
        if (routine.AsyncStatus == AsyncStatus.Emitting || routine.IsFailable)
            returnType = "{ i64, ptr }";
        if (isCExtern && returnType == "half") returnType = "i16";

        // On Windows x64 MSVC ABI, C structs > 8 bytes are returned via hidden sret pointer.
        // LLVM IR passes {i64, i64} in registers (RAX:RDX) but C expects sret, causing ABI mismatch.
        // Detect this case and emit the declaration with sret convention.
        bool needsSret = isCExtern && NeedsCExternSret(routine);
        if (needsSret)
        {
            // Change declaration: void @func(ptr sret(%RecordType), original_params...)
            paramTypes.Insert(0, $"ptr sret({returnType})");
            string parameters = string.Join(", ", paramTypes);
            EmitLine(_functionDeclarations, $"declare void @{funcName}({parameters})");
        }
        else
        {
            // Normal declaration
            string parameters = string.Join(", ", paramTypes);
            EmitLine(_functionDeclarations, $"declare {returnType} @{funcName}({parameters})");
        }
    }

    /// <summary>
    /// Checks if an external("C") function returns a struct type that needs sret on Windows x64.
    /// On MSVC ABI, C structs > 8 bytes are returned via a hidden first pointer parameter (sret).
    /// LLVM's {i64, i64} return convention uses RAX:RDX registers, which doesn't match.
    /// </summary>
    private bool NeedsCExternSret(RoutineInfo routine)
    {
        if (routine.ReturnType == null) return false;
        // Only record types that map to LLVM struct types (not intrinsics or single-wrapper) can need sret
        string llvmType = GetLLVMType(routine.ReturnType);
        if (!llvmType.StartsWith("%Record.") && !llvmType.StartsWith("%\"Record.")
            && !llvmType.StartsWith("%Tuple.") && !llvmType.StartsWith("%\"Tuple."))
            return false;
        // On Windows x64 MSVC ABI, structs > 8 bytes use sret.
        // TODO: On System V x86_64 (GCC/Linux), the threshold is > 16 bytes (RAX:RDX).
        //       Structs 9–16 bytes are register-returned on Linux but sret on Windows.
        //       This needs a target ABI config when cross-platform support is added.
        int size = GetTypeSize(routine.ReturnType);
        return size > 8;
    }

    /// <summary>
    /// Generates the LLVM function definition (with body).
    /// </summary>
    /// <param name="routine">The routine declaration from AST.</param>
    private void GenerateFunctionDefinition(RoutineDeclaration routine, RoutineInfo? preResolvedInfo = null, string? nameOverride = null)
    {
        RoutineInfo? routineInfo = preResolvedInfo;

        if (routineInfo == null)
        {
            // Look up the routine info from registry
            // For module-qualified names like "Console.show", the registry key may be
            // "IO.show" (module.name). Try full AST name first, then short name lookup.
            string baseName = routine.Name;
            routineInfo = _registry.LookupRoutine(baseName);
            if (routineInfo == null)
            {
                int dotIdx = baseName.IndexOf('.');
                if (dotIdx > 0)
                {
                    string shortName = baseName[(dotIdx + 1)..];
                    routineInfo = _registry.LookupRoutine(shortName)
                                  ?? _registry.LookupRoutineByName(shortName);
                }
                else
                {
                    // No dot — try short name fallback (e.g., "show" → finds "IO.show")
                    routineInfo = _registry.LookupRoutineByName(baseName);
                }
            }

            // For overloaded routines, resolve the specific overload matching this AST
            if (routineInfo != null && routine.Parameters.Count > 0)
            {
                var astParamTypes = new List<TypeInfo>();
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
                    var overload = _registry.LookupRoutineOverload(routineInfo.FullName, astParamTypes);
                    if (overload != null)
                        routineInfo = overload;
                }
            }
        }

        if (routineInfo == null || routineInfo.IsGenericDefinition
            || routineInfo.OwnerType is GenericParameterTypeInfo)
        {
            return; // Skip generic definitions, unresolved routines, and generic-param-owner routines
        }

        // Skip routines with error types in their signature
        if (HasErrorTypes(routineInfo))
        {
            return;
        }

        string funcName = nameOverride ?? MangleFunctionName(routineInfo);

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
        // Skip 'me' for $create routines (static factories)
        bool isCreator = routineInfo.Name.Contains("$create");
        if (routineInfo.OwnerType != null && !isCreator)
        {
            // $setitem! on records: me is passed by pointer (named %me.addr)
            // so mutations propagate to the caller's alloca
            if (IsRecordSetItem(routineInfo))
                paramList.Add("ptr %me.addr");
            else
            {
                string meType = GetParameterLLVMType(routineInfo.OwnerType);
                paramList.Add($"{meType} %me");
            }
        }

        // Add explicit parameters
        paramList.AddRange(from param in routineInfo.Parameters let paramType = GetParameterLLVMType(param.Type) select $"{paramType} %{param.Name}");

        // Get return type
        string returnType = routineInfo.ReturnType != null
            ? GetLLVMType(routineInfo.ReturnType)
            : "void";
        // Emitting and failable routines return Maybe[T] = { i64, ptr } at IR level
        if (routineInfo.AsyncStatus == AsyncStatus.Emitting || routineInfo.IsFailable)
            returnType = "{ i64, ptr }";

        // Start function — save position so we can rollback on error
        string parameters = string.Join(", ", paramList);
        int savedLength = _functionDefinitions.Length;
        int savedTempCounter = _tempCounter;

        EmitLine(_functionDefinitions, $"define {returnType} @{funcName}({parameters}) {{");
        EmitLine(_functionDefinitions, "entry:");

        // Generate body — if it throws, rollback and emit a stub function
        try
        {
            GenerateFunctionBody(routine.Body, routineInfo);
        }
        catch (Exception ex)
        {
            // Log the error for debugging
            Console.Error.WriteLine($"Warning: Codegen failed for '{funcName}': {ex.Message}");

            // Rollback any partial output from the failed body
            _functionDefinitions.Length = savedLength;
            _tempCounter = savedTempCounter;

            // Emit a stub function
            EmitLine(_functionDefinitions, $"define {returnType} @{funcName}({parameters}) {{");
            EmitLine(_functionDefinitions, "entry:");
            if (returnType == "void")
                EmitLine(_functionDefinitions, "  ret void");
            else
                EmitLine(_functionDefinitions, $"  ret {returnType} zeroinitializer");
        }

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
        _localVarLLVMNames.Clear();
        _varNameCounts.Clear();
        _localEntityVars.Clear();
        _currentBlock = "entry";
        _emitSlotAddr = null;
        _emitSlotType = null;

        // Set current function return type for use in EmitReturn
        _currentFunctionReturnType = routine.ReturnType;
        _currentRoutineIsFailable = routine.IsFailable;

        // Track current routine for source_routine() / source_module() injection
        _currentEmittingRoutine = routine;

        // Register implicit 'me' parameter for methods (skip for $create static factories)
        if (routine.OwnerType != null && !routine.Name.Contains("$create"))
        {
            if (IsRecordSetItem(routine))
            {
                // $setitem! on records: %me.addr IS the function parameter (caller's alloca pointer)
                // No alloca/store needed — mutations go directly to the caller's variable
                _localVariables["me"] = routine.OwnerType;
            }
            else
            {
                string meType = GetParameterLLVMType(routine.OwnerType);
                EmitLine(_functionDefinitions, $"  %me.addr = alloca {meType}");
                EmitLine(_functionDefinitions, $"  store {meType} %me, ptr %me.addr");
                _localVariables["me"] = routine.OwnerType;
            }
        }

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

        // Emit stack trace push
        {
            string routineName = routine.FullName ?? routine.Name;
            string fileName = routine.Location?.FileName ?? "<unknown>";
            int line = routine.Location?.Line ?? 0;
            int col = routine.Location?.Column ?? 0;
            string routineCStr = EmitCStringConstant(routineName);
            string fileCStr = EmitCStringConstant(fileName);
            string routineAsInt = NextTemp();
            string fileAsInt = NextTemp();
            EmitLine(_functionDefinitions, $"  {routineAsInt} = ptrtoint ptr {routineCStr} to i64");
            EmitLine(_functionDefinitions, $"  {fileAsInt} = ptrtoint ptr {fileCStr} to i64");
            EmitLine(_functionDefinitions, $"  call void @rf_trace_push(i64 {routineAsInt}, i64 {fileAsInt}, i32 {line}, i32 {col})");
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

        EmitEntityCleanup(_functionDefinitions, null);
        EmitLine(_functionDefinitions, "  call void @rf_trace_pop()");
        if (routine.ReturnType == null)
        {
            if (_currentRoutineIsFailable)
                EmitLine(_functionDefinitions, "  ret { i64, ptr } { i64 0, ptr null }");
            else
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
    /// <summary>
    /// Whether this routine is a $setitem on a record type (needs pass-by-pointer for me).
    /// </summary>
    private static bool IsRecordSetItem(RoutineInfo routine)
        => routine.OwnerType is RecordTypeInfo && routine.Name.Contains("$setitem");

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
            EntityTypeInfo or WrapperTypeInfo => "null",
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
        // External("C") functions use the raw C symbol name — no module prefix,
        // so that LLVM IR symbols match the actual C linker symbols.
        if (routine.CallingConvention == "C")
            return Q(SanitizeLLVMName(routine.Name));

        string name = SanitizeLLVMName(routine.Name);
        if (routine.OwnerType == null)
        {
            // Top-level: Module.Name (FullName already handles this)
            string fullName = SanitizeLLVMName(routine.FullName);

            // Generic instance: append type arguments (e.g., IO.show → IO.show#S64)
            if (routine.TypeArguments is { Count: > 0 })
            {
                string typeArgSuffix = string.Join(",", routine.TypeArguments.Select(t => t.Name));
                fullName = $"{fullName}#{typeArgSuffix}";
            }

            return Q(fullName);
        }

        // Method: Module.OwnerType.Name (OwnerType.FullName includes module)
        string typeName = routine.OwnerType.FullName;
        string baseName = $"{typeName}.{name}";

        // Disambiguate $create overloads by first parameter type
        if (name == "$create" && routine.Parameters.Count > 0)
        {
            string firstParamType = routine.Parameters[0].Type.Name;
            baseName = $"{baseName}#{firstParamType}";
        }

        return Q(baseName);
    }

    /// <summary>
    /// Sanitizes a name for use as an LLVM IR identifier.
    /// Replaces characters that are invalid in LLVM identifiers.
    /// </summary>
    private static string SanitizeLLVMName(string name)
    {
        return name.Replace("!", "");
    }

    #endregion

    #region Synthesized Routine Generation

    /// <summary>
    /// Monomorphizes generic methods by compiling generic AST bodies with type substitutions.
    /// For each pending monomorphization (recorded by EmitMethodCall/EmitGenericMethodCall),
    /// finds the generic AST body from stdlib programs, sets type parameter substitutions,
    /// and compiles the body with the concrete types.
    /// </summary>
    private void MonomorphizeGenericMethods()
    {
        // Multi-pass: compiling one generic method may reference other generic methods
        // (e.g., List[Letter].add_last calls List[Letter].reserve)
        int prevDefCount;
        do
        {
            prevDefCount = _generatedFunctionDefs.Count;

            foreach (var (mangledName, entry) in _pendingMonomorphizations.ToList())
            {
                if (_generatedFunctionDefs.Contains(mangledName))
                    continue;

                // Find the generic AST body in stdlib programs
                // For $create overloads, pass first param type for disambiguation (e.g., SortedSet[T] vs U64)
                string? firstParamGenericType = null;
                if (entry.GenericMethod.Name == "$create" && entry.GenericMethod.Parameters.Count > 0)
                {
                    TypeInfo paramType = entry.GenericMethod.Parameters[0].Type;
                    // Reverse-substitute concrete types back to generic params for AST matching
                    // e.g., SortedSet[S64] → SortedSet (the base name is enough to disambiguate)
                    firstParamGenericType = paramType.Name.Contains('[')
                        ? paramType.Name[..paramType.Name.IndexOf('[')]
                        : paramType.Name;
                }
                RoutineDeclaration? astRoutine = FindGenericAstRoutine(entry.GenericAstName, entry.GenericMethod.Parameters.Count, firstParamGenericType);
                if (astRoutine == null)
                {
                    // Synthesized routines (type_name, $ne, etc.) have no AST body —
                    // emit their bodies directly for the monomorphized type
                    if (entry.GenericMethod.IsSynthesized)
                    {
                        var synthInfo = BuildResolvedRoutineInfo(entry);
                        EmitSynthesizedRoutineBody(synthInfo, mangledName);
                        _generatedFunctionDefs.Add(mangledName);
                    }
                    continue;
                }

                // Build a resolved RoutineInfo with the correct owner type and substituted types
                var resolvedInfo = BuildResolvedRoutineInfo(entry);

                // Build string substitution map and rewrite AST with concrete types
                var astSubs = new Dictionary<string, string>();
                foreach (var (paramName, typeInfo) in entry.TypeSubstitutions)
                    astSubs[paramName] = typeInfo.Name;
                var rewrittenAst = GenericAstRewriter.Rewrite(astRoutine, astSubs);

                // Ensure entity/record type definitions are emitted for monomorphized types
                if (entry.ResolvedOwnerType is EntityTypeInfo ownerEntity)
                    GenerateEntityType(ownerEntity);
                else if (entry.ResolvedOwnerType is RecordTypeInfo ownerRecord)
                    GenerateRecordType(ownerRecord);

                // Also ensure return type and parameter record types are defined
                if (resolvedInfo.ReturnType is RecordTypeInfo returnRecord
                    && !returnRecord.HasDirectBackendType && !returnRecord.IsSingleMemberVariableWrapper
                    && !returnRecord.IsGenericDefinition)
                    GenerateRecordType(returnRecord);
                foreach (var param in resolvedInfo.Parameters)
                {
                    if (param.Type is RecordTypeInfo paramRecord
                        && !paramRecord.HasDirectBackendType && !paramRecord.IsSingleMemberVariableWrapper
                        && !paramRecord.IsGenericDefinition)
                        GenerateRecordType(paramRecord);
                }

                // Keep _typeSubstitutions as fallback for ResolvedType metadata
                _typeSubstitutions = entry.TypeSubstitutions;
                try
                {
                    GenerateFunctionDefinition(rewrittenAst, resolvedInfo, mangledName);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"Warning: Monomorphization failed for '{mangledName}': {ex.Message}");
                }
                finally
                {
                    _typeSubstitutions = null;
                }
            }
        } while (_generatedFunctionDefs.Count > prevDefCount);
    }

    /// <summary>
    /// Finds a generic routine's AST declaration from stdlib programs.
    /// </summary>
    /// <param name="genericAstName">The generic AST name (e.g., "List[T].add_last").</param>
    /// <returns>The routine declaration if found, null otherwise.</returns>
    private RoutineDeclaration? FindGenericAstRoutine(string genericAstName, int expectedParamCount = -1,
        string? firstParamTypeHint = null)
    {
        // Check if we need a generic routine specifically (name ends with [generic] marker)
        bool requireGeneric = genericAstName.EndsWith("[generic]");
        string baseName = requireGeneric
            ? genericAstName[..genericAstName.IndexOf("[generic]")]
            : genericAstName;

        // Collect all matching candidates (when disambiguating by param type)
        RoutineDeclaration? firstMatch = null;

        bool MatchesRoutine(RoutineDeclaration routine)
        {
            if (routine.Name != baseName) return false;
            if (requireGeneric && routine.GenericParameters is not { Count: > 0 }) return false;
            if (expectedParamCount >= 0 && routine.Parameters.Count != expectedParamCount) return false;
            return true;
        }

        bool MatchesParamType(RoutineDeclaration routine)
        {
            if (firstParamTypeHint == null || routine.Parameters.Count == 0) return true;
            string astParamType = routine.Parameters[0].Type.Name;
            return astParamType.StartsWith(firstParamTypeHint);
        }

        // Search user programs first, then stdlib
        foreach (var (userProgram, _, _) in _userPrograms)
        {
            foreach (var decl in userProgram.Declarations)
            {
                if (decl is RoutineDeclaration routine && MatchesRoutine(routine))
                {
                    firstMatch ??= routine;
                    if (MatchesParamType(routine))
                        return routine;
                }
            }
        }

        foreach (var (program, _, _) in _stdlibPrograms)
        {
            foreach (var decl in program.Declarations)
            {
                if (decl is RoutineDeclaration routine && MatchesRoutine(routine))
                {
                    firstMatch ??= routine;
                    if (MatchesParamType(routine))
                        return routine;
                }
            }
        }

        // If param type hint didn't match any, fall back to first match
        if (firstMatch != null)
            return firstMatch;

        // If no exact parameter match found and we filtered by count, retry without filter
        if (expectedParamCount >= 0)
            return FindGenericAstRoutine(genericAstName, -1, firstParamTypeHint);

        return null;
    }

    /// <summary>
    /// Resolves a type by applying generic substitutions.
    /// Handles direct substitution (T → S64), parameterized types (List[T] → List[S64]),
    /// and generic definitions with substitutable params.
    /// </summary>
    private TypeInfo ResolveSubstitutedType(TypeInfo type, Dictionary<string, TypeInfo> subs)
    {
        // Direct substitution: T → S64
        if (subs.TryGetValue(type.Name, out var sub))
            return sub;

        // Parameterized: List[T] → List[S64] (substitute type arguments)
        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            bool anySubstituted = false;
            var substitutedArgs = new List<TypeInfo>();
            foreach (var arg in type.TypeArguments)
            {
                var resolved = ResolveSubstitutedType(arg, subs);
                substitutedArgs.Add(resolved);
                if (!ReferenceEquals(resolved, arg)) anySubstituted = true;
            }
            if (anySubstituted)
            {
                TypeInfo? genericBase = type switch
                {
                    RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition,
                    EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition,
                    ProtocolTypeInfo { GenericDefinition: not null } p => p.GenericDefinition,
                    _ => null
                };
                if (genericBase == null)
                {
                    string baseName = type.Name.Contains('[')
                        ? type.Name[..type.Name.IndexOf('[')]
                        : type.Name;
                    genericBase = _registry.LookupType(baseName);
                }
                if (genericBase != null)
                    return _registry.GetOrCreateResolution(genericBase, substitutedArgs);
            }
        }

        // Generic definition with substitutable params: List with GenericParameters ["T"]
        if (type is { IsGenericDefinition: true, GenericParameters: not null } && type.TypeArguments == null)
        {
            var typeArgs = type.GenericParameters
                .Select(gp => subs.TryGetValue(gp, out var s) ? s : _registry.LookupType(gp))
                .Where(t => t != null)
                .ToList();
            if (typeArgs.Count == type.GenericParameters.Count)
                return _registry.GetOrCreateResolution(type, typeArgs!);
        }

        return type;
    }

    /// <summary>
    /// Builds a resolved RoutineInfo for monomorphization.
    /// Creates a new RoutineInfo with the resolved owner type and substituted parameter/return types.
    /// </summary>
    private RoutineInfo BuildResolvedRoutineInfo(MonomorphizationEntry entry)
    {
        var generic = entry.GenericMethod;
        var subs = entry.TypeSubstitutions;

        // Substitute parameter types (handles both direct T→S64 and parameterized List[T]→List[S64])
        var resolvedParams = generic.Parameters
            .Select(p =>
            {
                TypeInfo resolvedType = ResolveSubstitutedType(p.Type, subs);
                return p.WithSubstitutedType(resolvedType);
            })
            .ToList();

        // Substitute return type
        TypeInfo? resolvedReturnType = generic.ReturnType;
        if (resolvedReturnType != null)
            resolvedReturnType = ResolveSubstitutedType(resolvedReturnType, subs);

        return new RoutineInfo(generic.Name)
        {
            Kind = generic.Kind,
            OwnerType = entry.ResolvedOwnerType,
            Parameters = resolvedParams,
            ReturnType = resolvedReturnType,
            IsFailable = generic.IsFailable,
            DeclaredModification = generic.DeclaredModification,
            ModificationCategory = generic.ModificationCategory,
            Visibility = generic.Visibility,
            Location = generic.Location,
            Module = generic.Module,
            Annotations = generic.Annotations,
            CallingConvention = generic.CallingConvention,
            IsVariadic = generic.IsVariadic,
            IsDangerous = generic.IsDangerous,
            Storage = generic.Storage,
            AsyncStatus = generic.AsyncStatus
        };
    }

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

            // Skip generic definitions, routines with error types,
            // and routines on generic owner types (e.g., List[T].$diagnose)
            if (routine.IsGenericDefinition || HasErrorTypes(routine)
                || routine.OwnerType is { IsGenericDefinition: true })
                continue;

            string funcName = MangleFunctionName(routine);

            // Only emit synthesized routines that were declared (actually referenced)
            if (!_generatedFunctions.Contains(funcName))
                continue;

            // Skip if already generated
            if (!_generatedFunctionDefs.Add(funcName))
                continue;

            // Mark in declarations set to prevent declare/define conflicts
            _generatedFunctions.Add(funcName);

            EmitSynthesizedRoutineBody(routine, funcName);
        }
    }

    /// <summary>
    /// Emits the LLVM IR body for a single synthesized routine.
    /// Used by both GenerateSynthesizedRoutines (for non-generic types) and
    /// MonomorphizeGenericMethods (for monomorphized generic types).
    /// </summary>
    private void EmitSynthesizedRoutineBody(RoutineInfo routine, string funcName)
    {
        switch (routine.Name)
        {
            case "$ne":
                EmitSynthesizedNe(routine, funcName);
                break;
            case "$notcontains":
                EmitSynthesizedNotContains(routine, funcName);
                break;
            case "$lt":
                EmitSynthesizedCmpDerived(routine, funcName, -1, "eq");
                break;
            case "$le":
                EmitSynthesizedCmpDerived(routine, funcName, 1, "ne");
                break;
            case "$gt":
                EmitSynthesizedCmpDerived(routine, funcName, 1, "eq");
                break;
            case "$ge":
                EmitSynthesizedCmpDerived(routine, funcName, -1, "ne");
                break;
            case "$eq":
                EmitSynthesizedEq(routine, funcName);
                break;
            case "$cmp":
                EmitSynthesizedCmp(routine, funcName);
                break;
            case "$represent":
                EmitSynthesizedText(routine, funcName, includeSecret: false);
                break;
            case "$diagnose":
                EmitSynthesizedText(routine, funcName, includeSecret: true);
                break;
            case "$hash":
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
            case "$create":
                if (routine.OwnerType?.Name == "Data")
                    EmitSynthesizedDataCreate(routine, funcName);
                else
                    EmitSynthesizedTextCreate(routine, funcName);
                break;
            case "$create!":
                EmitSynthesizedChoiceCreateFromText(routine, funcName);
                break;
            case "$same":
                EmitSynthesizedSame(routine, funcName);
                break;
            case "$notsame":
                EmitSynthesizedNotSame(routine, funcName);
                break;
            case "all_on":
                EmitSynthesizedAllOn(routine, funcName);
                break;
            case "all_off":
                EmitSynthesizedAllOff(routine, funcName);
                break;
            case "all_cases":
                EmitSynthesizedAllCases(routine, funcName);
                break;
            case "type_name":
                EmitSynthesizedTypeName(routine, funcName);
                break;
            case "type_kind":
                EmitSynthesizedTypeKind(routine, funcName);
                break;
            case "type_id":
                EmitSynthesizedTypeId(routine, funcName);
                break;
            case "module_name":
                EmitSynthesizedModuleName(routine, funcName);
                break;
            case "member_variable_count":
                EmitSynthesizedFieldCount(routine, funcName);
                break;
            case "is_generic":
                EmitSynthesizedIsGeneric(routine, funcName);
                break;
            case "protocols":
                EmitSynthesizedProtocols(routine, funcName);
                break;
            case "routine_names":
                EmitSynthesizedRoutineNames(routine, funcName);
                break;
            case "annotations":
                EmitSynthesizedAnnotations(routine, funcName);
                break;
            case "data_size":
                EmitSynthesizedDataSize(routine, funcName);
                break;
            case "member_variable_info":
                EmitSynthesizedMemberVariableInfo(routine, funcName);
                break;
            case "all_member_variables":
                EmitSynthesizedAllFields(routine, funcName);
                break;
            case "open_member_variables":
                EmitSynthesizedOpenFields(routine, funcName);
                break;
            // BuilderService platform/build info
            case "page_size":
                EmitSynthesizedBuilderServiceU64(routine, funcName, 4096);
                break;
            case "cache_line":
                EmitSynthesizedBuilderServiceU64(routine, funcName, 64);
                break;
            case "word_size":
                EmitSynthesizedBuilderServiceU64(routine, funcName, _pointerBitWidth / 8);
                break;
            case "target_os":
                EmitSynthesizedBuilderServiceText(routine, funcName, DetectTargetOS());
                break;
            case "target_arch":
                EmitSynthesizedBuilderServiceText(routine, funcName, DetectTargetArch());
                break;
            case "builder_version":
                EmitSynthesizedBuilderServiceText(routine, funcName, "0.1.0");
                break;
            case "build_mode":
#if DEBUG
                EmitSynthesizedBuilderServiceU64(routine, funcName, 0); // BuildMode.DEBUG
#else
                EmitSynthesizedBuilderServiceU64(routine, funcName, 1); // BuildMode.RELEASE
#endif
                break;
            case "build_timestamp":
                EmitSynthesizedBuilderServiceText(routine, funcName,
                    DateTime.UtcNow.ToString("o"));
                break;
            // New BuilderService per-type routines
            case "generic_args":
                EmitSynthesizedGenericArgs(routine, funcName);
                break;
            case "origin_module":
                EmitSynthesizedModuleName(routine, funcName);
                break;
            case "protocol_info":
                EmitSynthesizedProtocolInfo(routine, funcName);
                break;
            case "routine_info":
                EmitSynthesizedRoutineInfoList(routine, funcName);
                break;
            case "dependencies":
                EmitSynthesizedDependencies(routine, funcName);
                break;
            case "member_type_id":
                EmitSynthesizedMemberTypeId(routine, funcName);
                break;
            case "var_name":
                // var_name() is inlined at call site, emit a fallback returning "<unknown>"
                EmitSynthesizedBuilderServiceTextNoOwner(routine, funcName, "<unknown>");
                break;
        }
    }

    /// <summary>
    /// Emits the body for a synthesized $ne routine: not $eq(me, you).
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

        // Look up the $eq function name on the same owner type
        string eqFuncName = Q($"{routine.OwnerType.Name}.$eq");

        EmitLine(_functionDefinitions, $"define i1 @{funcName}({meType} %me, {youType} %{youName}) {{");
        EmitLine(_functionDefinitions, "entry:");
        EmitLine(_functionDefinitions, $"  %eq = call i1 @{eqFuncName}({meType} %me, {youType} %{youName})");
        EmitLine(_functionDefinitions, "  %ne = xor i1 %eq, true");
        EmitLine(_functionDefinitions, "  ret i1 %ne");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits the body for a synthesized $notcontains routine: not $contains(me, item).
    /// </summary>
    private void EmitSynthesizedNotContains(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);

        string itemType = routine.Parameters.Count > 0
            ? GetParameterLLVMType(routine.Parameters[0].Type)
            : meType;
        string itemName = routine.Parameters.Count > 0
            ? routine.Parameters[0].Name
            : "item";

        string containsFuncName = Q($"{routine.OwnerType.FullName}.$contains");

        EmitLine(_functionDefinitions, $"define i1 @{funcName}({meType} %me, {itemType} %{itemName}) {{");
        EmitLine(_functionDefinitions, "entry:");
        EmitLine(_functionDefinitions, $"  %contains = call i1 @{containsFuncName}({meType} %me, {itemType} %{itemName})");
        EmitLine(_functionDefinitions, "  %notcontains = xor i1 %contains, true");
        EmitLine(_functionDefinitions, "  ret i1 %notcontains");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits the body for a synthesized $same routine: pointer identity comparison.
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
    /// Emits the body for a synthesized $notsame routine: not $same(me, you).
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
    /// Emits the body for a synthesized comparison operator derived from $cmp.
    /// E.g., $lt = $cmp(me, you) == -1 (ME_SMALL).
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

        // Ensure $cmp is declared/generated for the owner type
        var cmpMethod = _registry.LookupMethod(routine.OwnerType, "$cmp");
        string cmpFuncName;
        if (cmpMethod != null)
        {
            cmpFuncName = MangleFunctionName(cmpMethod);
            // For synthesized $cmp (e.g., tuples), emit the define directly
            // since GenerateSynthesizedRoutines may have already iterated past it
            if (cmpMethod.IsSynthesized && !_generatedFunctions.Contains(cmpFuncName))
            {
                _generatedFunctions.Add(cmpFuncName);
                _generatedFunctionDefs.Add(cmpFuncName);
                EmitSynthesizedCmp(cmpMethod, cmpFuncName);
            }
            else
            {
                GenerateFunctionDeclaration(cmpMethod);
            }
        }
        else
        {
            cmpFuncName = Q($"{routine.OwnerType.Name}.$cmp");
        }

        EmitLine(_functionDefinitions, $"define i1 @{funcName}({meType} %me, {youType} %{youName}) {{");
        EmitLine(_functionDefinitions, "entry:");
        EmitLine(_functionDefinitions, $"  %cmp = call i64 @{cmpFuncName}({meType} %me, {youType} %{youName})");
        EmitLine(_functionDefinitions, $"  %result = icmp {cmpOp} i64 %cmp, {resolvedTag}");
        EmitLine(_functionDefinitions, "  ret i1 %result");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits the body for a synthesized $eq routine.
    /// For records: AND chain of field-by-field $eq calls (or icmp for primitives).
    /// For entities: field-by-field equality via GEP load + comparison.
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
            case TupleTypeInfo tuple:
            {
                if (tuple.ElementTypes.Count == 0)
                {
                    EmitLine(_functionDefinitions, "  ret i1 true");
                    break;
                }

                string tupleStructType = GetTupleTypeName(tuple);
                string accum = "true";
                for (int i = 0; i < tuple.ElementTypes.Count; i++)
                {
                    TypeInfo elemType = tuple.ElementTypes[i];
                    string elemLlvmType = GetLLVMType(elemType);
                    string meElem = NextTemp();
                    string youElem = NextTemp();
                    EmitLine(_functionDefinitions, $"  {meElem} = extractvalue {tupleStructType} %me, {i}");
                    EmitLine(_functionDefinitions, $"  {youElem} = extractvalue {tupleStructType} %{youName}, {i}");

                    string cmpResult = EmitFieldEquality(elemType, elemLlvmType, meElem, youElem);

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
    /// For primitive/backend types: icmp eq. For complex types: call $eq.
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

        // Entity types: pointer equality
        if (fieldType is EntityTypeInfo)
        {
            string cmpResult = NextTemp();
            EmitLine(_functionDefinitions, $"  {cmpResult} = icmp eq ptr {meField}, {youField}");
            return cmpResult;
        }

        // Complex types: call their $eq method
        string eqName = Q($"{fieldType.Name}.$eq");
        string result = NextTemp();
        EmitLine(_functionDefinitions, $"  {result} = call i1 @{eqName}({llvmType} {meField}, {llvmType} {youField})");
        return result;
    }

    /// <summary>
    /// Emits the body for a synthesized $cmp routine on tuples.
    /// Lexicographic comparison: compare element-by-element, return first non-SAME result.
    /// </summary>
    private void EmitSynthesizedCmp(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType is not TupleTypeInfo tuple) return;

        string tupleStructType = GetTupleTypeName(tuple);

        EmitLine(_functionDefinitions, $"define i64 @{funcName}({tupleStructType} %me, {tupleStructType} %you) {{");
        EmitLine(_functionDefinitions, "entry:");

        // Look up the SAME tag value (should be 0)
        long sameTag = 0;
        var sameCase = _registry.LookupChoiceCase("SAME");
        if (sameCase != null)
            sameTag = sameCase.Value.CaseInfo.ComputedValue;

        if (tuple.ElementTypes.Count == 0)
        {
            EmitLine(_functionDefinitions, $"  ret i64 {sameTag}");
            EmitLine(_functionDefinitions, "}");
            EmitLine(_functionDefinitions, "");
            return;
        }

        for (int i = 0; i < tuple.ElementTypes.Count; i++)
        {
            TypeInfo elemType = tuple.ElementTypes[i];
            string elemLlvmType = GetLLVMType(elemType);

            string meElem = NextTemp();
            string youElem = NextTemp();
            EmitLine(_functionDefinitions, $"  {meElem} = extractvalue {tupleStructType} %me, {i}");
            EmitLine(_functionDefinitions, $"  {youElem} = extractvalue {tupleStructType} %you, {i}");

            // Call element's $cmp (ensure it's declared)
            var elemCmpMethod = _registry.LookupMethod(elemType, "$cmp");
            if (elemCmpMethod != null)
                GenerateFunctionDeclaration(elemCmpMethod);
            string cmpName = elemCmpMethod != null ? MangleFunctionName(elemCmpMethod) : Q($"{elemType.Name}.$cmp");
            string cmpResult = NextTemp();
            EmitLine(_functionDefinitions, $"  {cmpResult} = call i64 @{cmpName}({elemLlvmType} {meElem}, {elemLlvmType} {youElem})");

            // Last element: just return
            if (i == tuple.ElementTypes.Count - 1)
            {
                EmitLine(_functionDefinitions, $"  ret i64 {cmpResult}");
            }
            else
            {
                // If not SAME, return immediately; otherwise continue to next element
                string isSame = NextTemp();
                string nextLabel = $"cmp{i + 1}";
                string retLabel = $"ret{i}";
                EmitLine(_functionDefinitions, $"  {isSame} = icmp eq i64 {cmpResult}, {sameTag}");
                EmitLine(_functionDefinitions, $"  br i1 {isSame}, label %{nextLabel}, label %{retLabel}");
                EmitLine(_functionDefinitions, "");
                EmitLine(_functionDefinitions, $"{retLabel}:");
                EmitLine(_functionDefinitions, $"  ret i64 {cmpResult}");
                EmitLine(_functionDefinitions, "");
                EmitLine(_functionDefinitions, $"{nextLabel}:");
            }
        }

        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits the body for a synthesized $represent() or $diagnose() routine.
    /// $represent: "TypeName(field: val, ...)" — open+posted fields only.
    /// $diagnose: "TypeName(field: val, ...)" for records/SF entities,
    ///               "TypeName@0xADDR(field: val, ...)" for RF entities (includes heap address).
    /// </summary>
    private void EmitSynthesizedText(RoutineInfo routine, string funcName, bool includeSecret)
    {
        if (routine.OwnerType == null) return;

        // Ensure rf_text_concat is declared (used for field concatenation)
        if (_declaredNativeFunctions.Add("rf_text_concat"))
            EmitLine(_functionDeclarations, "declare ptr @rf_text_concat(ptr, ptr)");

        string meType = GetParameterLLVMType(routine.OwnerType);

        EmitLine(_functionDefinitions, $"define ptr @{funcName}({meType} %me) {{");
        EmitLine(_functionDefinitions, "entry:");

        int savedTempCounter = _tempCounter;

        string typeName = routine.OwnerType.FullName;

        // Get fields to include
        IReadOnlyList<MemberVariableInfo>? fields = routine.OwnerType switch
        {
            RecordTypeInfo rec => rec.MemberVariables,
            EntityTypeInfo ent => ent.MemberVariables,
            _ => null
        };

        // Tuples: emit "(item0, item1, ...)" for represent, "ValueTuple/Tuple[T1, T2, ...](item0, item1, ...)" for diagnose
        if (routine.OwnerType is TupleTypeInfo tuple)
        {
            string tupleStructType = GetTupleTypeName(tuple);

            // Determine ValueTuple vs Tuple: any entity element → Tuple, all value types → ValueTuple
            bool isValueTuple = tuple.ElementTypes.All(t => t is not EntityTypeInfo);
            string tuplePrefix = isValueTuple ? "ValueTuple" : "Tuple";
            string tupleDisplayName = $"{tuplePrefix}[{string.Join(", ", tuple.ElementTypes.Select(t => t.Name))}]";

            // Start with "(" for represent, "ValueTuple/Tuple[T1, T2, ...](" for diagnose
            string openLiteral = includeSecret ? $"{tupleDisplayName}(" : "(";
            string tupleCur = EmitSynthesizedStringLiteral(openLiteral);

            for (int i = 0; i < tuple.ElementTypes.Count; i++)
            {
                TypeInfo elemTypeInfo = tuple.ElementTypes[i];
                string elemLlvmType = GetLLVMType(elemTypeInfo);

                // Add ", " separator after first element
                if (i > 0)
                {
                    string sep = EmitSynthesizedStringLiteral(", ");
                    string withSep = NextTemp();
                    EmitLine(_functionDefinitions, $"  {withSep} = call ptr @rf_text_concat(ptr {tupleCur}, ptr {sep})");
                    tupleCur = withSep;
                }

                // Extract element value
                string elemVal = NextTemp();
                EmitLine(_functionDefinitions, $"  {elemVal} = extractvalue {tupleStructType} %me, {i}");

                // Always use $represent for inner values (diagnose shows envelope, represent for contents)
                string elemText = EmitSynthesizedValueToText(elemTypeInfo, elemLlvmType, elemVal, useDiagnose: false);

                // Concat
                string withElem = NextTemp();
                EmitLine(_functionDefinitions, $"  {withElem} = call ptr @rf_text_concat(ptr {tupleCur}, ptr {elemText})");
                tupleCur = withElem;
            }

            // Append ")"
            string closeParen = EmitSynthesizedStringLiteral(")");
            string tupleResult = NextTemp();
            EmitLine(_functionDefinitions, $"  {tupleResult} = call ptr @rf_text_concat(ptr {tupleCur}, ptr {closeParen})");
            EmitLine(_functionDefinitions, $"  ret ptr {tupleResult}");
            EmitLine(_functionDefinitions, "}");
            EmitLine(_functionDefinitions, "");
            return;
        }

        // Choice: $represent → "CASE_NAME", $diagnose → "TypeName(CASE_NAME, value: N)"
        if (routine.OwnerType is ChoiceTypeInfo choiceOwner)
        {
            EmitChoiceRepresent(choiceOwner, includeSecret);
            EmitLine(_functionDefinitions, "}");
            EmitLine(_functionDefinitions, "");
            return;
        }

        // Flags: $represent → "FLAG1 and FLAG2", $diagnose → "TypeName(FLAG1 and FLAG2, value: N)"
        if (routine.OwnerType is FlagsTypeInfo flagsOwner)
        {
            EmitFlagsRepresent(flagsOwner, includeSecret);
            EmitLine(_functionDefinitions, "}");
            EmitLine(_functionDefinitions, "");
            return;
        }

        if (fields == null)
        {
            string nameStr = EmitSynthesizedStringLiteral(typeName);
            EmitLine(_functionDefinitions, $"  ret ptr {nameStr}");
            EmitLine(_functionDefinitions, "}");
            EmitLine(_functionDefinitions, "");
            return;
        }

        // Filter out secret fields for $represent (but include for $diagnose)
        var visibleFields = new List<(MemberVariableInfo MV, int Index)>();
        for (int i = 0; i < fields.Count; i++)
        {
            if (includeSecret || fields[i].Visibility != VisibilityModifier.Secret)
                visibleFields.Add((fields[i], i));
        }

        // Build "TypeName(field: val, ...)" or for RF entity $diagnose:
        // "TypeName@0xADDR(field: val, ...)"
        bool emitAddress = includeSecret
                           && routine.OwnerType is EntityTypeInfo
                           && _registry.Language == Language.RazorForge;

        string current;
        if (emitAddress)
        {
            // Emit "TypeName@0x" prefix, then format the pointer as hex
            if (_declaredNativeFunctions.Add("rf_format_address"))
                EmitLine(_functionDeclarations, "declare ptr @rf_format_address(ptr)");

            string typePrefix = EmitSynthesizedStringLiteral($"{typeName}@");
            string addrText = NextTemp();
            EmitLine(_functionDefinitions, $"  {addrText} = call ptr @rf_format_address(ptr %me)");
            string withAddr = NextTemp();
            EmitLine(_functionDefinitions, $"  {withAddr} = call ptr @rf_text_concat(ptr {typePrefix}, ptr {addrText})");
            string openParen = EmitSynthesizedStringLiteral("(");
            string withParen = NextTemp();
            EmitLine(_functionDefinitions, $"  {withParen} = call ptr @rf_text_concat(ptr {withAddr}, ptr {openParen})");
            current = withParen;
        }
        else
        {
            string prefix = $"{typeName}(";
            current = EmitSynthesizedStringLiteral(prefix);
        }

        for (int vi = 0; vi < visibleFields.Count; vi++)
        {
            var (mv, fieldIdx) = visibleFields[vi];
            string fieldType = GetLLVMType(mv.Type);

            // Add "[secret] fieldName: " or "fieldName: " prefix
            string secretTag = includeSecret && mv.Visibility == VisibilityModifier.Secret ? "[secret] " : "";
            string fieldPrefix = vi > 0 ? $", {secretTag}{mv.Name}: " : $"{secretTag}{mv.Name}: ";
            string fieldPrefixStr = EmitSynthesizedStringLiteral(fieldPrefix);

            // Concat field prefix
            string withPrefix = NextTemp();
            EmitLine(_functionDefinitions, $"  {withPrefix} = call ptr @rf_text_concat(ptr {current}, ptr {fieldPrefixStr})");
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
                default:
                    fieldValue = "%me";
                    break;
            }

            // Convert field value to Text (use $diagnose for diagnose mode)
            string fieldText = EmitSynthesizedValueToText(mv.Type, fieldType, fieldValue, useDiagnose: includeSecret);

            // Concat field text
            string withField = NextTemp();
            EmitLine(_functionDefinitions, $"  {withField} = call ptr @rf_text_concat(ptr {current}, ptr {fieldText})");
            current = withField;
        }

        // Append closing ")"
        string suffix = EmitSynthesizedStringLiteral(")");
        string result = NextTemp();
        EmitLine(_functionDefinitions, $"  {result} = call ptr @rf_text_concat(ptr {current}, ptr {suffix})");

        EmitLine(_functionDefinitions, $"  ret ptr {result}");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits code to convert a value to Text for synthesized $represent()/$diagnose() routines.
    /// When useDiagnose is false, calls Text.$create(from: T) which delegates to T.$represent().
    /// When useDiagnose is true, calls T.$diagnose() directly.
    /// </summary>
    private string EmitSynthesizedValueToText(TypeInfo fieldType, string llvmType, string value, bool useDiagnose = false)
    {
        // Text fields need no conversion
        if (fieldType.Name == "Text")
            return value;

        if (useDiagnose)
        {
            // Call T.$diagnose() directly
            RoutineInfo? diagRoutine = _registry.LookupRoutine($"{fieldType.FullName}.$diagnose");
            if (diagRoutine != null)
            {
                GenerateFunctionDeclaration(diagRoutine);
                string diagName = MangleFunctionName(diagRoutine);
                string diagResult = NextTemp();
                EmitLine(_functionDefinitions, $"  {diagResult} = call ptr @{diagName}({llvmType} {value})");
                return diagResult;
            }
            // Fall through to Text.$create if no $diagnose found
        }

        // Look up the Text.$create overload for this specific parameter type
        RoutineInfo? createRoutine = _registry.LookupRoutineOverload("Text.$create", [fieldType])
                                     ?? _registry.LookupRoutine("Text.$create");
        if (createRoutine != null)
            GenerateFunctionDeclaration(createRoutine);
        string createName = createRoutine != null
            ? MangleFunctionName(createRoutine)
            : "Text.$create";
        string textResult = NextTemp();
        EmitLine(_functionDefinitions, $"  {textResult} = call ptr @{createName}({llvmType} {value})");
        return textResult;
    }

    /// <summary>
    /// Emits choice $represent/$diagnose body.
    /// $represent: switch on value, return case name string (e.g., "READ").
    /// $diagnose: "TypeName(CASE_NAME, value: N)".
    /// </summary>
    private void EmitChoiceRepresent(ChoiceTypeInfo choice, bool includeSecret)
    {
        var sb = _functionDefinitions;
        string typeName = choice.FullName;

        if (choice.Cases.Count == 0)
        {
            string fallback = includeSecret
                ? EmitSynthesizedStringLiteral($"{typeName}(<unknown>, value: 0)")
                : EmitSynthesizedStringLiteral("<unknown>");
            EmitLine(sb, $"  ret ptr {fallback}");
            return;
        }

        // Switch on %me to get case name
        string defaultLabel = "sw.default";
        EmitLine(sb, $"  switch i64 %me, label %{defaultLabel} [");
        for (int i = 0; i < choice.Cases.Count; i++)
            EmitLine(sb, $"    i64 {choice.Cases[i].ComputedValue}, label %sw.case.{i}");
        EmitLine(sb, "  ]");

        // Emit each case block
        for (int i = 0; i < choice.Cases.Count; i++)
        {
            var c = choice.Cases[i];
            EmitLine(sb, $"sw.case.{i}:");
            if (includeSecret)
            {
                // $diagnose: "TypeName(CASE, value: N)"
                string diagStr = EmitSynthesizedStringLiteral(
                    $"{typeName}({c.Name}, value: {c.ComputedValue})");
                EmitLine(sb, $"  ret ptr {diagStr}");
            }
            else
            {
                // $represent: just "CASE"
                string caseStr = EmitSynthesizedStringLiteral(c.Name);
                EmitLine(sb, $"  ret ptr {caseStr}");
            }
        }

        // Default: unknown value — convert to text
        EmitLine(sb, $"{defaultLabel}:");
        if (includeSecret)
        {
            string prefix = EmitSynthesizedStringLiteral($"{typeName}(<unknown>, value: ");
            string suffix = EmitSynthesizedStringLiteral(")");
            string valText = EmitSynthesizedValueToText(
                _registry.LookupType("S64") ?? new RecordTypeInfo("S64"), "i64", "%me");
            string cat1 = NextTemp();
            EmitLine(sb, $"  {cat1} = call ptr @rf_text_concat(ptr {prefix}, ptr {valText})");
            string cat2 = NextTemp();
            EmitLine(sb, $"  {cat2} = call ptr @rf_text_concat(ptr {cat1}, ptr {suffix})");
            EmitLine(sb, $"  ret ptr {cat2}");
        }
        else
        {
            string valText = EmitSynthesizedValueToText(
                _registry.LookupType("S64") ?? new RecordTypeInfo("S64"), "i64", "%me");
            EmitLine(sb, $"  ret ptr {valText}");
        }
    }

    /// <summary>
    /// Emits flags $represent/$diagnose body.
    /// $represent: "FLAG1 and FLAG2" (active flags concatenated with " and ").
    /// $diagnose: "TypeName(FLAG1 and FLAG2, value: N)".
    /// </summary>
    private void EmitFlagsRepresent(FlagsTypeInfo flags, bool includeSecret)
    {
        var sb = _functionDefinitions;
        string typeName = flags.FullName;

        if (flags.Members.Count == 0)
        {
            string fallback = includeSecret
                ? EmitSynthesizedStringLiteral($"{typeName}(<none>, value: 0)")
                : EmitSynthesizedStringLiteral("<none>");
            EmitLine(sb, $"  ret ptr {fallback}");
            return;
        }

        // Build the flag names string by checking each bit
        string emptyStr = EmitSynthesizedStringLiteral("");
        string andSep = EmitSynthesizedStringLiteral(" and ");
        string current = emptyStr;
        string isFirst = "1"; // i1 tracking whether we've added any flag yet (1 = still first)

        for (int i = 0; i < flags.Members.Count; i++)
        {
            var member = flags.Members[i];
            long mask = 1L << member.BitPosition;

            // Check if this bit is set: (%me & mask) != 0
            string masked = NextTemp();
            EmitLine(sb, $"  {masked} = and i64 %me, {mask}");
            string isSet = NextTemp();
            EmitLine(sb, $"  {isSet} = icmp ne i64 {masked}, 0");

            // Branch: if set, add this name
            string setLabel = $"flag.set.{i}";
            string skipLabel = $"flag.skip.{i}";
            string mergeLabel = $"flag.merge.{i}";
            EmitLine(sb, $"  br i1 {isSet}, label %{setLabel}, label %{skipLabel}");

            // Set branch: concat separator + name
            EmitLine(sb, $"{setLabel}:");
            string nameStr = EmitSynthesizedStringLiteral(member.Name);

            // If not first, prepend " and "
            string needsSep = NextTemp();
            EmitLine(sb, $"  {needsSep} = icmp eq ptr {current}, {emptyStr}");
            string withSep = NextTemp();
            EmitLine(sb, $"  br i1 {needsSep}, label %{setLabel}.nosep, label %{setLabel}.sep");

            EmitLine(sb, $"{setLabel}.sep:");
            string catSep = NextTemp();
            EmitLine(sb, $"  {catSep} = call ptr @rf_text_concat(ptr {current}, ptr {andSep})");
            string catName1 = NextTemp();
            EmitLine(sb, $"  {catName1} = call ptr @rf_text_concat(ptr {catSep}, ptr {nameStr})");
            EmitLine(sb, $"  br label %{mergeLabel}");

            EmitLine(sb, $"{setLabel}.nosep:");
            string catName2 = NextTemp();
            EmitLine(sb, $"  {catName2} = call ptr @rf_text_concat(ptr {current}, ptr {nameStr})");
            EmitLine(sb, $"  br label %{mergeLabel}");

            // Skip branch
            EmitLine(sb, $"{skipLabel}:");
            EmitLine(sb, $"  br label %{mergeLabel}");

            // Merge: phi to pick the right string
            EmitLine(sb, $"{mergeLabel}:");
            string merged = NextTemp();
            EmitLine(sb, $"  {merged} = phi ptr [ {catName1}, %{setLabel}.sep ], [ {catName2}, %{setLabel}.nosep ], [ {current}, %{skipLabel} ]");
            current = merged;
        }

        // Handle zero value (no flags set)
        string isZero = NextTemp();
        EmitLine(sb, $"  {isZero} = icmp eq ptr {current}, {emptyStr}");
        string noneStr = EmitSynthesizedStringLiteral("<none>");
        string finalName = NextTemp();
        EmitLine(sb, $"  {finalName} = select i1 {isZero}, ptr {noneStr}, ptr {current}");

        if (includeSecret)
        {
            // $diagnose: "TypeName(FLAGS, value: N)"
            string prefix = EmitSynthesizedStringLiteral($"{typeName}(");
            string midStr = EmitSynthesizedStringLiteral(", value: ");
            string suffix = EmitSynthesizedStringLiteral(")");
            string valText = EmitSynthesizedValueToText(
                _registry.LookupType("S64") ?? new RecordTypeInfo("S64"), "i64", "%me");
            string cat1 = NextTemp();
            EmitLine(sb, $"  {cat1} = call ptr @rf_text_concat(ptr {prefix}, ptr {finalName})");
            string cat2 = NextTemp();
            EmitLine(sb, $"  {cat2} = call ptr @rf_text_concat(ptr {cat1}, ptr {midStr})");
            string cat3 = NextTemp();
            EmitLine(sb, $"  {cat3} = call ptr @rf_text_concat(ptr {cat2}, ptr {valText})");
            string cat4 = NextTemp();
            EmitLine(sb, $"  {cat4} = call ptr @rf_text_concat(ptr {cat3}, ptr {suffix})");
            EmitLine(sb, $"  ret ptr {cat4}");
        }
        else
        {
            // $represent: just the flag names
            EmitLine(sb, $"  ret ptr {finalName}");
        }
    }

    /// <summary>
    /// Emits a string literal for use in synthesized routines and returns its global name.
    /// </summary>
    private string EmitSynthesizedStringLiteral(string value)
    {
        // Delegate to EmitStringLiteral which handles UTF-32 Text constant emission
        return EmitStringLiteral(_functionDefinitions, value);
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
                string hashName = Q($"{routine.OwnerType.Name}.$hash");
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
                    string fieldHashName = Q($"{mv.Type.Name}.$hash");
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
            case TupleTypeInfo tuple:
            {
                if (tuple.ElementTypes.Count == 0)
                {
                    EmitLine(_functionDefinitions, "  ret i64 0");
                    break;
                }

                string tupleStructType = GetTupleTypeName(tuple);
                string accum = "0";
                for (int i = 0; i < tuple.ElementTypes.Count; i++)
                {
                    TypeInfo elemType = tuple.ElementTypes[i];
                    string elemLlvmType = GetLLVMType(elemType);
                    string elem = NextTemp();
                    EmitLine(_functionDefinitions, $"  {elem} = extractvalue {tupleStructType} %me, {i}");

                    string elemHashName = Q($"{elemType.Name}.$hash");
                    string elemHash = NextTemp();
                    EmitLine(_functionDefinitions, $"  {elemHash} = call i64 @{elemHashName}({elemLlvmType} {elem})");

                    if (accum == "0")
                    {
                        accum = elemHash;
                    }
                    else
                    {
                        string xorResult = NextTemp();
                        EmitLine(_functionDefinitions, $"  {xorResult} = xor i64 {accum}, {elemHash}");
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
    /// Emits the body for a synthesized id() routine on entities.
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
        EmitLine(_functionDefinitions, $"  {newPtr} = call ptr @rf_allocate_dynamic(i64 {size})");

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
    /// Emits the body for a synthesized Text.$create(from: T) routine.
    /// Calls T.Text() on the argument.
    /// </summary>
    private void EmitSynthesizedTextCreate(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null || routine.Parameters.Count == 0) return;

        var paramType = routine.Parameters[0].Type;
        string paramLlvmType = GetParameterLLVMType(paramType);
        string paramName = routine.Parameters[0].Name;

        // Text.$create(from: T) → T.$represent()
        // Look up and declare the $represent() method so it gets generated
        RoutineInfo? textMethod = _registry.LookupMethod(paramType, "$represent");
        if (textMethod != null)
            GenerateFunctionDeclaration(textMethod);
        string textMethodName = textMethod != null
            ? MangleFunctionName(textMethod)
            : Q($"{paramType.Name}.$represent");

        EmitLine(_functionDefinitions, $"define ptr @{funcName}({paramLlvmType} %{paramName}) {{");
        EmitLine(_functionDefinitions, "entry:");
        string result = NextTemp();
        EmitLine(_functionDefinitions, $"  {result} = call ptr @{textMethodName}({paramLlvmType} %{paramName})");
        EmitLine(_functionDefinitions, $"  ret ptr {result}");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits the body for a synthesized Data.$create(from: T) routine.
    /// Boxes a value into a Data entity (type_id, size, data_ptr).
    /// </summary>
    private void EmitSynthesizedDataCreate(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null || routine.Parameters.Count == 0) return;

        var paramType = routine.Parameters[0].Type;
        string paramLlvmType = GetParameterLLVMType(paramType);
        string paramName = routine.Parameters[0].Name;

        // Data entity layout: { i64 type_id, i64 size, ptr data_ptr } = 24 bytes
        string dataStructType = GetEntityTypeName((EntityTypeInfo)routine.OwnerType);

        ulong typeId = ComputeTypeId(paramType.FullName);
        long dataSize = ComputeDataSize(paramType);

        EmitLine(_functionDefinitions, $"define ptr @{funcName}({paramLlvmType} %{paramName}) {{");
        EmitLine(_functionDefinitions, "entry:");

        // Allocate Data entity (3 fields × 8 bytes = 24 bytes)
        string dataPtr = NextTemp();
        EmitLine(_functionDefinitions, $"  {dataPtr} = call ptr @rf_allocate_dynamic(i64 24)");

        // Store type_id (compile-time FNV-1a hash)
        string tidPtr = NextTemp();
        EmitLine(_functionDefinitions, $"  {tidPtr} = getelementptr {dataStructType}, ptr {dataPtr}, i32 0, i32 0");
        EmitLine(_functionDefinitions, $"  store i64 {typeId}, ptr {tidPtr}");

        // Store size
        string sizePtr = NextTemp();
        EmitLine(_functionDefinitions, $"  {sizePtr} = getelementptr {dataStructType}, ptr {dataPtr}, i32 0, i32 1");
        EmitLine(_functionDefinitions, $"  store i64 {dataSize}, ptr {sizePtr}");

        // Allocate memory for boxed value and copy
        string box = NextTemp();
        EmitLine(_functionDefinitions, $"  {box} = call ptr @rf_allocate_dynamic(i64 {dataSize})");
        EmitLine(_functionDefinitions, $"  store {paramLlvmType} %{paramName}, ptr {box}");

        // Store data_ptr
        string dptrPtr = NextTemp();
        EmitLine(_functionDefinitions, $"  {dptrPtr} = getelementptr {dataStructType}, ptr {dataPtr}, i32 0, i32 2");
        EmitLine(_functionDefinitions, $"  store ptr {box}, ptr {dptrPtr}");

        EmitLine(_functionDefinitions, $"  ret ptr {dataPtr}");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits the body for a synthesized $create!(from: Text) on choice types.
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

    /// <summary>
    /// Emits the body for a synthesized all_off() routine on flags types.
    /// Returns 0 (no flags set).
    /// </summary>
    private void EmitSynthesizedAllOff(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        EmitLine(_functionDefinitions, $"define i64 @{funcName}() {{");
        EmitLine(_functionDefinitions, "entry:");
        EmitLine(_functionDefinitions, "  ret i64 0");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits the body for a synthesized all_on() routine on flags types.
    /// Returns the OR of all member bit positions.
    /// </summary>
    private void EmitSynthesizedAllOn(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType is not FlagsTypeInfo flagsType) return;

        ulong mask = 0;
        foreach (var member in flagsType.Members)
        {
            mask |= 1UL << member.BitPosition;
        }

        EmitLine(_functionDefinitions, $"define i64 @{funcName}() {{");
        EmitLine(_functionDefinitions, "entry:");
        EmitLine(_functionDefinitions, $"  ret i64 {mask}");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits the body for a synthesized all_cases() routine on flags/choice types.
    /// Returns a List[Me] containing all cases.
    /// Layout: { i64 count, i64 capacity, ptr data } where data is an array of i64 values.
    /// </summary>
    private void EmitSynthesizedAllCases(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        // Collect all case values as i64 constants
        var caseValues = new List<long>();

        if (routine.OwnerType is ChoiceTypeInfo choiceType)
        {
            foreach (var c in choiceType.Cases)
            {
                caseValues.Add(c.ComputedValue);
            }
        }
        else if (routine.OwnerType is FlagsTypeInfo flagsType)
        {
            foreach (var member in flagsType.Members)
            {
                caseValues.Add((long)(1UL << member.BitPosition));
            }
        }
        else
        {
            return;
        }

        int count = caseValues.Count;
        int elemSize = 8; // i64

        var sb = _functionDefinitions;
        EmitLine(sb, $"define ptr @{funcName}() {{");
        EmitLine(sb, "entry:");

        // Allocate list header: { i64 count, i64 capacity, ptr data }
        string listPtr = NextTemp();
        EmitLine(sb, $"  {listPtr} = call ptr @rf_allocate_dynamic(i64 24)");

        // Allocate data array
        string dataPtr = NextTemp();
        EmitLine(sb, $"  {dataPtr} = call ptr @rf_allocate_dynamic(i64 {count * elemSize})");

        // Store count
        string countPtr = NextTemp();
        EmitLine(sb, $"  {countPtr} = getelementptr {{ i64, i64, ptr }}, ptr {listPtr}, i32 0, i32 0");
        EmitLine(sb, $"  store i64 {count}, ptr {countPtr}");

        // Store capacity
        string capPtr = NextTemp();
        EmitLine(sb, $"  {capPtr} = getelementptr {{ i64, i64, ptr }}, ptr {listPtr}, i32 0, i32 1");
        EmitLine(sb, $"  store i64 {count}, ptr {capPtr}");

        // Store data pointer
        string dataPtrSlot = NextTemp();
        EmitLine(sb, $"  {dataPtrSlot} = getelementptr {{ i64, i64, ptr }}, ptr {listPtr}, i32 0, i32 2");
        EmitLine(sb, $"  store ptr {dataPtr}, ptr {dataPtrSlot}");

        // Store each case value into the data array
        for (int i = 0; i < count; i++)
        {
            string elemPtr = NextTemp();
            EmitLine(sb, $"  {elemPtr} = getelementptr i64, ptr {dataPtr}, i64 {i}");
            EmitLine(sb, $"  store i64 {caseValues[i]}, ptr {elemPtr}");
        }

        EmitLine(sb, $"  ret ptr {listPtr}");
        EmitLine(sb, "}");
        EmitLine(sb, "");
    }

    /// <summary>
    /// Generates forwarding stubs for protocol method calls.
    /// When monomorphized code calls a method on a protocol-typed field (e.g., me.source.$iter()
    /// where source: Iterable[S64]), the emitted call targets "Core.Iterable[S64].$iter".
    /// This method generates a 'define' body that forwards to the concrete implementer
    /// (e.g., Core.List[S64].$iter).
    /// </summary>
    private void GenerateProtocolDispatchStubs()
    {
        foreach (var (mangledName, info) in _pendingProtocolDispatches)
        {
            // Skip if already defined (a previous iteration or other codegen path generated it)
            if (_generatedFunctionDefs.Contains(mangledName))
                continue;

            // Find the declaration to get param/return types
            // The declaration was emitted in EmitMethodCall: "declare <retType> @<mangledName>(<paramTypes>)"
            if (!_generatedFunctions.Contains(mangledName))
                continue;

            // Find concrete implementers of this protocol resolution
            string? concreteFunc = FindConcreteImplementer(info.Protocol, info.MethodName);
            if (concreteFunc == null || !_generatedFunctionDefs.Contains(concreteFunc))
                continue;

            // Parse the declaration to extract return type and parameter types
            string? declLine = FindDeclarationLine(mangledName);
            if (declLine == null)
                continue;

            // Parse: "declare <retType> @<name>(<params>)"
            int declareIdx = declLine.IndexOf("declare ");
            if (declareIdx < 0) continue;
            string afterDeclare = declLine[(declareIdx + 8)..];
            int atIdx = afterDeclare.IndexOf(" @");
            if (atIdx < 0) continue;
            string retType = afterDeclare[..atIdx].Trim();
            int openParen = afterDeclare.IndexOf('(');
            int closeParen = afterDeclare.LastIndexOf(')');
            if (openParen < 0 || closeParen < 0) continue;
            string paramList = afterDeclare[(openParen + 1)..closeParen].Trim();

            // Build parameter names
            var paramNames = new List<string>();
            var paramTypes = new List<string>();
            if (!string.IsNullOrEmpty(paramList))
            {
                // Split param types (handles types like { i64, ptr } that contain commas)
                paramTypes = SplitLlvmParams(paramList);
                for (int i = 0; i < paramTypes.Count; i++)
                    paramNames.Add(i == 0 ? "%self" : $"%arg{i}");
            }

            // Emit forwarding stub
            var sb = _functionDefinitions;
            string paramDefs = string.Join(", ",
                paramTypes.Select((t, i) => $"{t} {paramNames[i]}"));
            EmitLine(sb, $"define {retType} @{mangledName}({paramDefs}) {{");
            EmitLine(sb, "entry:");

            string callArgs = string.Join(", ",
                paramTypes.Select((t, i) => $"{t} {paramNames[i]}"));

            if (retType == "void")
            {
                EmitLine(sb, $"  call void @{concreteFunc}({callArgs})");
                EmitLine(sb, "  ret void");
            }
            else
            {
                EmitLine(sb, $"  %fwd = call {retType} @{concreteFunc}({callArgs})");
                EmitLine(sb, $"  ret {retType} %fwd");
            }
            EmitLine(sb, "}");
            EmitLine(sb, "");

            _generatedFunctionDefs.Add(mangledName);
        }
    }

    /// <summary>
    /// Finds the concrete implementation function for a protocol method.
    /// Searches all entity/record types for one that implements the given protocol
    /// and has a generated function body for the method.
    /// </summary>
    private string? FindConcreteImplementer(ProtocolTypeInfo protocol, string methodName)
    {
        var protocolDef = protocol.GenericDefinition ?? protocol;
        string protocolBaseName = protocolDef.Name;

        // Track whether we've already triggered a monomorphization for an uncompiled candidate
        bool triggered = false;

        // Search all entity/record types (including resolutions) for implementers
        var seen = new HashSet<string>();
        foreach (var type in _registry.GetTypesByCategory(SemanticAnalysis.Enums.TypeCategory.Entity)
            .Concat(_registry.GetTypesByCategory(SemanticAnalysis.Enums.TypeCategory.Record)))
        {
            if (type.IsGenericDefinition && protocol.TypeArguments == null) continue;
            if (!seen.Add(type.Name)) continue;

            IReadOnlyList<TypeInfo>? protocols = type switch
            {
                EntityTypeInfo e => e.ImplementedProtocols,
                RecordTypeInfo r => r.ImplementedProtocols,
                _ => null
            };
            if (protocols == null) continue;

            foreach (var impl in protocols)
            {
                // Check if this type implements the matching protocol
                // For generic types: List[T] obeys Iterable[T] → when T=S64, List[S64] obeys Iterable[S64]
                string implBaseName = impl.Name.Contains('[') ? impl.Name[..impl.Name.IndexOf('[')] : impl.Name;
                if (implBaseName != protocolBaseName) continue;

                // For resolved (non-generic-definition) types, verify the protocol type arguments match exactly.
                // Without this, EnumerateEmitter[S64] (obeys Iterator[Tuple[S64, S64]]) would
                // incorrectly match a search for Iterator[S64].
                if (!type.IsGenericDefinition && protocol.TypeArguments is { Count: > 0 } && impl.TypeArguments is { Count: > 0 })
                {
                    if (protocol.TypeArguments.Count != impl.TypeArguments.Count)
                        continue;
                    bool argsMatch = true;
                    for (int i = 0; i < protocol.TypeArguments.Count; i++)
                    {
                        if (protocol.TypeArguments[i].FullName != impl.TypeArguments[i].FullName)
                        {
                            argsMatch = false;
                            break;
                        }
                    }
                    if (!argsMatch) continue;
                }

                // Match: now determine the concrete type resolution
                TypeInfo concreteType = type;

                // If the type is a generic definition, resolve it using the protocol's type args
                if (type.IsGenericDefinition && protocol.TypeArguments is { Count: > 0 })
                {
                    // The protocol on the generic def has the same generic params (e.g., Iterable[T])
                    // We need to map protocol's T to the concrete type arg (e.g., S64)
                    // Then resolve the generic def with those args
                    var protocolGenDef = protocol.GenericDefinition ?? protocol;
                    if (protocolGenDef.GenericParameters is { Count: > 0 } && type.GenericParameters is { Count: > 0 })
                    {
                        // Build mapping: protocol param → concrete type arg
                        var mapping = new Dictionary<string, TypeInfo>();
                        for (int i = 0; i < protocolGenDef.GenericParameters.Count && i < protocol.TypeArguments.Count; i++)
                            mapping[protocolGenDef.GenericParameters[i]] = protocol.TypeArguments[i];

                        // Map type's generic params using the impl protocol's type args
                        // e.g., List[T] with Iterable[T]: T maps to protocol param T → S64
                        var typeArgs = new List<TypeInfo>();
                        if (impl.TypeArguments is { Count: > 0 })
                        {
                            foreach (var implArg in impl.TypeArguments)
                            {
                                if (implArg is GenericParameterTypeInfo gp && mapping.TryGetValue(gp.Name, out var concrete))
                                    typeArgs.Add(concrete);
                                else if (mapping.TryGetValue(implArg.Name, out var concrete2))
                                    typeArgs.Add(concrete2);
                                else
                                    typeArgs.Add(implArg);
                            }
                        }
                        else
                        {
                            // If impl has no type args, use protocol's type args directly
                            typeArgs.AddRange(protocol.TypeArguments);
                        }

                        if (typeArgs.Count == type.GenericParameters.Count)
                        {
                            concreteType = _registry.GetOrCreateResolution(type, typeArgs);
                        }
                        else
                            continue;
                    }
                    else
                        continue;
                }
                else if (type.IsGenericDefinition)
                    continue; // Can't resolve without type args

                // Check if the concrete method exists in generated functions
                string candidateName = Q($"{concreteType.FullName}.{SanitizeLLVMName(methodName)}");
                if (_generatedFunctionDefs.Contains(candidateName))
                    return candidateName;

                // Method not compiled yet — trigger monomorphization for ONE candidate so it
                // will be available in a subsequent iteration of the multi-pass loop.
                // Only trigger once (first match) to avoid cascading monomorphization of
                // all implementers (e.g., SetIterator, SkipEmitter, etc.) that aren't needed.
                if (!triggered)
                {
                    TypeInfo? genericDef = concreteType switch
                    {
                        EntityTypeInfo e => e.GenericDefinition,
                        RecordTypeInfo r => r.GenericDefinition,
                        _ => null
                    };
                    if (genericDef != null)
                    {
                        var genericMethod = _registry.LookupMethod(genericDef, methodName);
                        if (genericMethod != null && !_pendingMonomorphizations.ContainsKey(candidateName))
                        {
                            // Ensure entity type struct is defined for the concrete type
                            if (concreteType is EntityTypeInfo entityType)
                                GenerateEntityType(entityType);
                            RecordMonomorphization(candidateName, genericMethod, concreteType);
                            triggered = true;
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a declaration line for a given mangled function name.
    /// </summary>
    private string? FindDeclarationLine(string mangledName)
    {
        string searchTarget = $"@{mangledName}(";
        foreach (string line in _functionDeclarations.ToString().Split('\n'))
        {
            if (line.StartsWith("declare ") && line.Contains(searchTarget))
                return line;
        }
        return null;
    }

    /// <summary>
    /// Splits LLVM parameter types, handling nested braces (e.g., "{ i64, ptr }").
    /// </summary>
    private static List<string> SplitLlvmParams(string paramList)
    {
        var result = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < paramList.Length; i++)
        {
            if (paramList[i] == '{') depth++;
            else if (paramList[i] == '}') depth--;
            else if (paramList[i] == ',' && depth == 0)
            {
                result.Add(paramList[start..i].Trim());
                start = i + 1;
            }
        }
        if (start < paramList.Length)
            result.Add(paramList[start..].Trim());
        return result;
    }

    #endregion

    #region BuilderService Metadata Routines

    /// <summary>
    /// Emits the body for a synthesized type_name() routine.
    /// Returns the type's name as a Text constant.
    /// </summary>
    private void EmitSynthesizedTypeName(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);

        EmitLine(_functionDefinitions, $"define ptr @{funcName}({meType} %me) {{");
        EmitLine(_functionDefinitions, "entry:");
        string nameStr = EmitSynthesizedStringLiteral(routine.OwnerType.Name);
        EmitLine(_functionDefinitions, $"  ret ptr {nameStr}");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits the body for a synthesized type_kind() routine.
    /// Returns the TypeKind choice ordinal as an i64 constant.
    /// </summary>
    private void EmitSynthesizedTypeKind(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);
        // Map TypeCategory enum → TypeKind choice ordinals (from BuilderService.rf)
        long kindValue = routine.OwnerType.Category switch
        {
            TypeCategory.Record => 0,    // TypeKind.RECORD
            TypeCategory.Entity => 1,    // TypeKind.ENTITY
            TypeCategory.Choice => 2,    // TypeKind.CHOICE
            TypeCategory.Variant => 3,   // TypeKind.VARIANT
            TypeCategory.Flags => 4,     // TypeKind.FLAGS
            TypeCategory.Routine => 5,   // TypeKind.ROUTINE
            TypeCategory.Protocol => 7,  // TypeKind.PROTOCOL
            _ => 0                       // Default RECORD for internal types (Tuple, Wrapper, etc.)
        };

        EmitLine(_functionDefinitions, $"define i64 @{funcName}({meType} %me) {{");
        EmitLine(_functionDefinitions, "entry:");
        EmitLine(_functionDefinitions, $"  ret i64 {kindValue}");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits the body for a synthesized type_id() routine.
    /// Returns a unique build-time FNV-1a hash of the type's full name.
    /// </summary>
    private void EmitSynthesizedTypeId(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);
        ulong hash = ComputeTypeId(routine.OwnerType.FullName);

        EmitLine(_functionDefinitions, $"define i64 @{funcName}({meType} %me) {{");
        EmitLine(_functionDefinitions, "entry:");
        EmitLine(_functionDefinitions, $"  ret i64 {unchecked((long)hash)}");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits the body for a synthesized module_name() routine.
    /// Returns the module where the type is defined as a Text constant.
    /// </summary>
    private void EmitSynthesizedModuleName(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);
        string moduleName = routine.OwnerType.Module ?? "";

        EmitLine(_functionDefinitions, $"define ptr @{funcName}({meType} %me) {{");
        EmitLine(_functionDefinitions, "entry:");
        string moduleStr = EmitSynthesizedStringLiteral(moduleName);
        EmitLine(_functionDefinitions, $"  ret ptr {moduleStr}");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits the body for a synthesized field_count() routine.
    /// Returns the number of member variables as a U64 constant.
    /// </summary>
    private void EmitSynthesizedFieldCount(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);

        int count = routine.OwnerType switch
        {
            RecordTypeInfo rec => rec.MemberVariables.Count,
            EntityTypeInfo ent => ent.MemberVariables.Count,
            _ => 0
        };

        EmitLine(_functionDefinitions, $"define i64 @{funcName}({meType} %me) {{");
        EmitLine(_functionDefinitions, "entry:");
        EmitLine(_functionDefinitions, $"  ret i64 {count}");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits the body for a synthesized is_generic() routine.
    /// Returns true if the type has generic parameters.
    /// </summary>
    private void EmitSynthesizedIsGeneric(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);
        string value = routine.OwnerType.IsGenericDefinition ? "true" : "false";

        EmitLine(_functionDefinitions, $"define i1 @{funcName}({meType} %me) {{");
        EmitLine(_functionDefinitions, "entry:");
        EmitLine(_functionDefinitions, $"  ret i1 {value}");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Computes a unique type ID using FNV-1a hash of the full type name.
    /// </summary>
    private static ulong ComputeTypeId(string fullName)
    {
        ulong hash = 14695981039346656037UL; // FNV-1a offset basis
        foreach (byte b in Encoding.UTF8.GetBytes(fullName))
        {
            hash ^= b;
            hash *= 1099511628211UL; // FNV-1a prime
        }

        return hash;
    }

    /// <summary>
    /// Emits a List[Text] constant containing the given string values.
    /// Layout: { i64 count, i64 capacity, ptr data } where data is an array of ptr (Text strings).
    /// </summary>
    private void EmitSynthesizedStringList(string funcName, string meType, IReadOnlyList<string> values)
    {
        int count = values.Count;
        var sb = _functionDefinitions;

        EmitLine(sb, $"define ptr @{funcName}({meType} %me) {{");
        EmitLine(sb, "entry:");

        // Allocate list header: { i64 count, i64 capacity, ptr data }
        string listPtr = NextTemp();
        EmitLine(sb, $"  {listPtr} = call ptr @rf_allocate_dynamic(i64 24)");

        // Allocate data array (array of pointers, 8 bytes each)
        string dataPtr = NextTemp();
        EmitLine(sb, $"  {dataPtr} = call ptr @rf_allocate_dynamic(i64 {count * 8})");

        // Store count
        string countPtr = NextTemp();
        EmitLine(sb, $"  {countPtr} = getelementptr {{ i64, i64, ptr }}, ptr {listPtr}, i32 0, i32 0");
        EmitLine(sb, $"  store i64 {count}, ptr {countPtr}");

        // Store capacity
        string capPtr = NextTemp();
        EmitLine(sb, $"  {capPtr} = getelementptr {{ i64, i64, ptr }}, ptr {listPtr}, i32 0, i32 1");
        EmitLine(sb, $"  store i64 {count}, ptr {capPtr}");

        // Store data pointer
        string dataPtrSlot = NextTemp();
        EmitLine(sb, $"  {dataPtrSlot} = getelementptr {{ i64, i64, ptr }}, ptr {listPtr}, i32 0, i32 2");
        EmitLine(sb, $"  store ptr {dataPtr}, ptr {dataPtrSlot}");

        // Store each string pointer into the data array
        for (int i = 0; i < count; i++)
        {
            string strConst = EmitSynthesizedStringLiteral(values[i]);
            string elemPtr = NextTemp();
            EmitLine(sb, $"  {elemPtr} = getelementptr ptr, ptr {dataPtr}, i64 {i}");
            EmitLine(sb, $"  store ptr {strConst}, ptr {elemPtr}");
        }

        EmitLine(sb, $"  ret ptr {listPtr}");
        EmitLine(sb, "}");
        EmitLine(sb, "");
    }

    /// <summary>
    /// Emits the body for a synthesized protocols() routine.
    /// Returns a List[Text] of protocol names this type obeys.
    /// </summary>
    private void EmitSynthesizedProtocols(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);

        IReadOnlyList<TypeInfo>? protocols = routine.OwnerType switch
        {
            RecordTypeInfo rec => rec.ImplementedProtocols,
            EntityTypeInfo ent => ent.ImplementedProtocols,
            _ => null
        };

        var names = protocols?.Select(p => p.Name).ToList() ?? [];
        EmitSynthesizedStringList(funcName, meType, names);
    }

    /// <summary>
    /// Emits the body for a synthesized routine_names() routine.
    /// Returns a List[Text] of all member routine names for this type.
    /// </summary>
    private void EmitSynthesizedRoutineNames(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);

        var routineNames = _registry.GetMethodsForType(routine.OwnerType)
            .Select(r => r.Name)
            .Distinct()
            .ToList();

        EmitSynthesizedStringList(funcName, meType, routineNames);
    }

    /// <summary>
    /// Emits the body for a synthesized annotations() routine.
    /// Returns a List[Text] of build-time annotations on this type.
    /// Currently returns an empty list (type-level annotations not yet tracked on TypeInfo).
    /// </summary>
    private void EmitSynthesizedAnnotations(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);

        // Type-level annotations are not yet tracked on TypeInfo — return empty list
        EmitSynthesizedStringList(funcName, meType, []);
    }

    /// <summary>
    /// Emits the body for a synthesized data_size() routine.
    /// Returns the byte size of the type's data layout.
    /// </summary>
    private void EmitSynthesizedDataSize(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);
        long size = ComputeDataSize(routine.OwnerType);

        EmitLine(_functionDefinitions, $"define i64 @{funcName}({meType} %me) {{");
        EmitLine(_functionDefinitions, "entry:");
        EmitLine(_functionDefinitions, $"  ret i64 {size}");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits the body for a synthesized member_variable_info() routine.
    /// Returns a List[FieldInfo] where FieldInfo = { ptr name, ptr type_name, i64 visibility, i64 offset }.
    /// </summary>
    private void EmitSynthesizedMemberVariableInfo(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);

        IReadOnlyList<MemberVariableInfo>? fields = routine.OwnerType switch
        {
            RecordTypeInfo rec => rec.MemberVariables,
            EntityTypeInfo ent => ent.MemberVariables,
            _ => null
        };

        fields ??= [];
        int count = fields.Count;

        // FieldInfo layout: { ptr name, ptr type_name, i64 visibility, i64 offset }
        // Each element is 32 bytes (8 + 8 + 8 + 8)
        int elemSize = 32;

        var sb = _functionDefinitions;
        EmitLine(sb, $"define ptr @{funcName}({meType} %me) {{");
        EmitLine(sb, "entry:");

        // Allocate list header: { i64 count, i64 capacity, ptr data }
        string listPtr = NextTemp();
        EmitLine(sb, $"  {listPtr} = call ptr @rf_allocate_dynamic(i64 24)");

        // Allocate data array
        string dataPtr = NextTemp();
        EmitLine(sb, $"  {dataPtr} = call ptr @rf_allocate_dynamic(i64 {count * elemSize})");

        // Store count
        string countPtr = NextTemp();
        EmitLine(sb, $"  {countPtr} = getelementptr {{ i64, i64, ptr }}, ptr {listPtr}, i32 0, i32 0");
        EmitLine(sb, $"  store i64 {count}, ptr {countPtr}");

        // Store capacity
        string capPtr = NextTemp();
        EmitLine(sb, $"  {capPtr} = getelementptr {{ i64, i64, ptr }}, ptr {listPtr}, i32 0, i32 1");
        EmitLine(sb, $"  store i64 {count}, ptr {capPtr}");

        // Store data pointer
        string dataPtrSlot = NextTemp();
        EmitLine(sb, $"  {dataPtrSlot} = getelementptr {{ i64, i64, ptr }}, ptr {listPtr}, i32 0, i32 2");
        EmitLine(sb, $"  store ptr {dataPtr}, ptr {dataPtrSlot}");

        // Compute field byte offsets
        var fieldOffsets = ComputeFieldOffsets(routine.OwnerType);

        // Store each FieldInfo { ptr name, ptr type_name, i64 visibility, i64 offset }
        for (int i = 0; i < count; i++)
        {
            var field = fields[i];
            string nameStr = EmitSynthesizedStringLiteral(field.Name);
            string typeNameStr = EmitSynthesizedStringLiteral(field.Type.Name);
            long visibility = (long)field.Visibility;
            long offset = i < fieldOffsets.Count ? fieldOffsets[i] : 0;

            // GEP to element i in the data array (each element is { ptr, ptr, i64, i64 })
            string elemPtr = NextTemp();
            EmitLine(sb, $"  {elemPtr} = getelementptr {{ ptr, ptr, i64, i64 }}, ptr {dataPtr}, i64 {i}");

            // Store name (field 0)
            string nameSlot = NextTemp();
            EmitLine(sb, $"  {nameSlot} = getelementptr {{ ptr, ptr, i64, i64 }}, ptr {elemPtr}, i32 0, i32 0");
            EmitLine(sb, $"  store ptr {nameStr}, ptr {nameSlot}");

            // Store type_name (field 1)
            string typeSlot = NextTemp();
            EmitLine(sb, $"  {typeSlot} = getelementptr {{ ptr, ptr, i64, i64 }}, ptr {elemPtr}, i32 0, i32 1");
            EmitLine(sb, $"  store ptr {typeNameStr}, ptr {typeSlot}");

            // Store visibility (field 2)
            string visSlot = NextTemp();
            EmitLine(sb, $"  {visSlot} = getelementptr {{ ptr, ptr, i64, i64 }}, ptr {elemPtr}, i32 0, i32 2");
            EmitLine(sb, $"  store i64 {visibility}, ptr {visSlot}");

            // Store offset (field 3)
            string offSlot = NextTemp();
            EmitLine(sb, $"  {offSlot} = getelementptr {{ ptr, ptr, i64, i64 }}, ptr {elemPtr}, i32 0, i32 3");
            EmitLine(sb, $"  store i64 {offset}, ptr {offSlot}");
        }

        EmitLine(sb, $"  ret ptr {listPtr}");
        EmitLine(sb, "}");
        EmitLine(sb, "");
    }

    /// <summary>
    /// Emits the body for a synthesized all_fields() routine.
    /// Returns a Dict[Text, Data] with all field names mapped to their boxed values.
    /// </summary>
    private void EmitSynthesizedAllFields(RoutineInfo routine, string funcName)
    {
        EmitSynthesizedFieldsDict(routine, funcName, openOnly: false);
    }

    /// <summary>
    /// Emits the body for a synthesized open_fields() routine.
    /// Returns a Dict[Text, Data] with only open (public) field names mapped to their boxed values.
    /// </summary>
    private void EmitSynthesizedOpenFields(RoutineInfo routine, string funcName)
    {
        EmitSynthesizedFieldsDict(routine, funcName, openOnly: true);
    }

    /// <summary>
    /// Shared implementation for all_fields() and open_fields().
    /// Allocates a Dict[Text, Data], extracts field values from %me, boxes each to Data,
    /// and inserts into the dict.
    /// </summary>
    private void EmitSynthesizedFieldsDict(RoutineInfo routine, string funcName, bool openOnly)
    {
        if (routine.OwnerType == null) return;

        var sb = _functionDefinitions;
        string meType = GetParameterLLVMType(routine.OwnerType);

        // Get fields for this type
        IReadOnlyList<MemberVariableInfo> allFields = routine.OwnerType switch
        {
            RecordTypeInfo rec => rec.MemberVariables,
            EntityTypeInfo ent => ent.MemberVariables,
            _ => []
        };

        var fields = openOnly
            ? allFields.Where(f => f.Visibility == VisibilityModifier.Open).ToList()
            : allFields.ToList();

        // Look up Dict[Text, Data] creator and set method
        TypeInfo? dictDef = _registry.LookupType("Dict");
        TypeInfo? textType = _registry.LookupType("Text");
        TypeInfo? dataType = _registry.LookupType("Data");

        if (dictDef == null || textType == null || dataType == null)
        {
            // Dict or Data not available — emit a function that returns null
            EmitLine(sb, $"define ptr @{funcName}({meType} %me) {{");
            EmitLine(sb, "entry:");
            EmitLine(sb, "  ret ptr null");
            EmitLine(sb, "}");
            EmitLine(sb, "");
            return;
        }

        TypeInfo dictTextData = _registry.GetOrCreateResolution(
            genericDef: dictDef, typeArguments: [textType, dataType]);

        // Build names for Dict[Text, Data].$create() and set(key, value)
        string dictCreateName = Q($"{dictTextData.Name}.$create");
        string dictSetName = Q($"{dictTextData.Name}.set");

        // Data entity struct type for inline boxing
        string dataStructType = dataType is EntityTypeInfo dataEntity
            ? GetEntityTypeName(dataEntity)
            : "%Entity.Data";

        EmitLine(sb, $"define ptr @{funcName}({meType} %me) {{");
        EmitLine(sb, "entry:");

        // Allocate empty dict: %dict = call ptr @Dict_Text_Data_.$create()
        string dictPtr = NextTemp();
        EmitLine(sb, $"  {dictPtr} = call ptr @{dictCreateName}()");

        // Get the owner struct type name for GEP (not GetLLVMType, which returns "ptr" for entities)
        string structType = routine.OwnerType switch
        {
            EntityTypeInfo ent => GetEntityTypeName(ent),
            RecordTypeInfo rec => GetRecordTypeName(rec),
            _ => GetLLVMType(routine.OwnerType)
        };

        // For records (pass-by-value), we need a pointer to GEP into.
        // Alloca the value and store %me into it.
        bool isRecord = routine.OwnerType is RecordTypeInfo;
        bool hasBackendType = routine.OwnerType is RecordTypeInfo { HasDirectBackendType: true };
        string mePtr = "%me";
        if (isRecord)
        {
            string allocaType = hasBackendType ? meType : structType;
            string allocaPtr = NextTemp();
            EmitLine(sb, $"  {allocaPtr} = alloca {allocaType}");
            EmitLine(sb, $"  store {meType} %me, ptr {allocaPtr}");
            mePtr = allocaPtr;
        }

        // For each field: extract value, inline-box to Data, insert into dict
        for (int i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            string fieldLlvmType = GetLLVMType(field.Type);
            string nameStr = EmitSynthesizedStringLiteral(field.Name);

            // Extract field value from %me (use pointer for GEP)
            string fieldPtr;
            if (hasBackendType)
            {
                // Backend-typed records (Bool, S32, etc.) — the value IS the field, just use the alloca pointer
                fieldPtr = mePtr;
            }
            else
            {
                fieldPtr = NextTemp();
                EmitLine(sb, $"  {fieldPtr} = getelementptr {structType}, ptr {mePtr}, i32 0, i32 {field.Index}");
            }
            string fieldVal = NextTemp();
            EmitLine(sb, $"  {fieldVal} = load {fieldLlvmType}, ptr {fieldPtr}");

            // Inline Data boxing (avoids overload collision on Data_$create)
            ulong fieldTypeId = ComputeTypeId(field.Type.FullName);
            long fieldDataSize = ComputeDataSize(field.Type);

            // Allocate Data entity (3 fields × 8 bytes = 24 bytes)
            string boxed = NextTemp();
            EmitLine(sb, $"  {boxed} = call ptr @rf_allocate_dynamic(i64 24)");

            // Store type_id
            string tidSlot = NextTemp();
            EmitLine(sb, $"  {tidSlot} = getelementptr {dataStructType}, ptr {boxed}, i32 0, i32 0");
            EmitLine(sb, $"  store i64 {fieldTypeId}, ptr {tidSlot}");

            // Store size
            string sizeSlot = NextTemp();
            EmitLine(sb, $"  {sizeSlot} = getelementptr {dataStructType}, ptr {boxed}, i32 0, i32 1");
            EmitLine(sb, $"  store i64 {fieldDataSize}, ptr {sizeSlot}");

            // Allocate and store boxed value
            string valBox = NextTemp();
            EmitLine(sb, $"  {valBox} = call ptr @rf_allocate_dynamic(i64 {fieldDataSize})");
            EmitLine(sb, $"  store {fieldLlvmType} {fieldVal}, ptr {valBox}");

            // Store data_ptr
            string dptrSlot = NextTemp();
            EmitLine(sb, $"  {dptrSlot} = getelementptr {dataStructType}, ptr {boxed}, i32 0, i32 2");
            EmitLine(sb, $"  store ptr {valBox}, ptr {dptrSlot}");

            // Insert into dict: call dict.set(name, boxed)
            EmitLine(sb, $"  call void @{dictSetName}(ptr {dictPtr}, ptr {nameStr}, ptr {boxed})");
        }

        EmitLine(sb, $"  ret ptr {dictPtr}");
        EmitLine(sb, "}");
        EmitLine(sb, "");
    }

    /// <summary>
    /// Computes the byte size of a type's data layout using field sizes with alignment padding.
    /// </summary>
    private long ComputeDataSize(TypeInfo type)
    {
        // Entities are always pointer-referenced — their storage size is pointer size,
        // not the entity struct size. This matters for collections (e.g., List[Entity])
        // where elements are stored as pointers in the data buffer.
        if (type is EntityTypeInfo)
            return _pointerBitWidth / 8;

        IReadOnlyList<MemberVariableInfo>? fields = type switch
        {
            RecordTypeInfo { HasDirectBackendType: true } rec => null, // Use backend type size
            RecordTypeInfo { IsSingleMemberVariableWrapper: true } => null, // Use underlying size
            RecordTypeInfo rec => rec.MemberVariables,
            _ => null
        };

        // Simple types with known sizes
        if (fields == null)
        {
            return type switch
            {
                RecordTypeInfo { HasDirectBackendType: true } rec => LlvmTypeSizeBytes(rec.LlvmType),
                RecordTypeInfo { IsSingleMemberVariableWrapper: true } rec => LlvmTypeSizeBytes(rec.LlvmType),
                ChoiceTypeInfo => 8, // i64
                FlagsTypeInfo => 8,  // i64
                _ => _pointerBitWidth / 8 // Default to pointer size
            };
        }

        if (fields.Count == 0) return 1; // Empty struct = 1 byte (for addressability)

        // Compute struct layout with alignment padding
        long offset = 0;
        long maxAlign = 1;

        foreach (var field in fields)
        {
            long fieldSize = GetFieldByteSize(field.Type);
            long fieldAlign = GetFieldAlignment(field.Type);
            maxAlign = Math.Max(maxAlign, fieldAlign);

            // Align offset
            offset = (offset + fieldAlign - 1) / fieldAlign * fieldAlign;
            offset += fieldSize;
        }

        // Pad to struct alignment
        offset = (offset + maxAlign - 1) / maxAlign * maxAlign;
        return offset;
    }

    /// <summary>
    /// Computes the alignment requirement of a type.
    /// </summary>
    private long ComputeAlignSize(TypeInfo type)
    {
        IReadOnlyList<MemberVariableInfo>? fields = type switch
        {
            RecordTypeInfo { HasDirectBackendType: true } => null,
            RecordTypeInfo { IsSingleMemberVariableWrapper: true } => null,
            RecordTypeInfo rec => rec.MemberVariables,
            EntityTypeInfo ent => ent.MemberVariables,
            _ => null
        };

        if (fields == null)
        {
            return type switch
            {
                RecordTypeInfo { HasDirectBackendType: true } rec => LlvmTypeAlignment(rec.LlvmType),
                RecordTypeInfo { IsSingleMemberVariableWrapper: true } rec =>
                    LlvmTypeAlignment(rec.LlvmType),
                ChoiceTypeInfo => 8,
                FlagsTypeInfo => 8,
                _ => _pointerBitWidth / 8
            };
        }

        if (fields.Count == 0) return 1;

        long maxAlign = 1;
        foreach (var field in fields)
        {
            maxAlign = Math.Max(maxAlign, GetFieldAlignment(field.Type));
        }

        return maxAlign;
    }

    /// <summary>
    /// Gets the byte size of a field's type for layout computation.
    /// </summary>
    private long GetFieldByteSize(TypeInfo type)
    {
        string llvmType = GetLLVMType(type);
        return LlvmTypeSizeBytes(llvmType);
    }

    /// <summary>
    /// Gets the alignment of a field's type for layout computation.
    /// </summary>
    private long GetFieldAlignment(TypeInfo type)
    {
        string llvmType = GetLLVMType(type);
        return LlvmTypeAlignment(llvmType);
    }

    /// <summary>
    /// Maps an LLVM type string to its byte size.
    /// </summary>
    private long LlvmTypeSizeBytes(string llvmType)
    {
        return llvmType switch
        {
            "i1" => 1,
            "i8" => 1,
            "i16" => 2,
            "i32" => 4,
            "i64" => 8,
            "i128" => 16,
            "half" => 2,
            "float" => 4,
            "double" => 8,
            "fp128" => 16,
            "ptr" => _pointerBitWidth / 8,
            _ => _pointerBitWidth / 8 // Default to pointer size for struct/unknown types
        };
    }

    /// <summary>
    /// Maps an LLVM type string to its natural alignment.
    /// </summary>
    private long LlvmTypeAlignment(string llvmType)
    {
        return llvmType switch
        {
            "i1" => 1,
            "i8" => 1,
            "i16" => 2,
            "i32" => 4,
            "i64" => 8,
            "i128" => 16,
            "half" => 2,
            "float" => 4,
            "double" => 8,
            "fp128" => 16,
            "ptr" => _pointerBitWidth / 8,
            _ => _pointerBitWidth / 8
        };
    }

    /// <summary>
    /// Computes byte offsets for each field in a type (for FieldInfo.offset).
    /// </summary>
    private List<long> ComputeFieldOffsets(TypeInfo type)
    {
        IReadOnlyList<MemberVariableInfo>? fields = type switch
        {
            RecordTypeInfo rec => rec.MemberVariables,
            EntityTypeInfo ent => ent.MemberVariables,
            _ => null
        };

        if (fields == null || fields.Count == 0) return [];

        var offsets = new List<long>();
        long currentOffset = 0;

        foreach (var field in fields)
        {
            long fieldSize = ComputeDataSize(field.Type);
            long fieldAlign = ComputeAlignSize(field.Type);
            if (fieldAlign > 0)
            {
                long remainder = currentOffset % fieldAlign;
                if (remainder != 0) currentOffset += fieldAlign - remainder;
            }
            offsets.Add(currentOffset);
            currentOffset += fieldSize;
        }

        return offsets;
    }

    /// <summary>
    /// Emits the body for a synthesized generic_args() routine.
    /// Returns a List[Text] of generic type argument names.
    /// </summary>
    private void EmitSynthesizedGenericArgs(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);

        var args = routine.OwnerType.TypeArguments?.Select(t => t.Name).ToList()
                   ?? routine.OwnerType.GenericParameters?.ToList()
                   ?? [];
        EmitSynthesizedStringList(funcName, meType, args);
    }

    /// <summary>
    /// Emits the body for a synthesized protocol_info() routine.
    /// Returns a List[ProtocolInfo] where ProtocolInfo = { ptr name, ptr routine_names_list, i1 is_generated }.
    /// Currently emits a simplified version returning an empty list (full entity allocation deferred).
    /// </summary>
    private void EmitSynthesizedProtocolInfo(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);

        // For now, emit an empty list — full ProtocolInfo entity allocation is complex
        // and will be implemented when the BuilderService stdlib types are fully parsed
        EmitSynthesizedStringList(funcName, meType, []);
    }

    /// <summary>
    /// Emits the body for a synthesized routine_info() routine.
    /// Returns a List[RoutineInfo] — currently emits empty list (entity allocation deferred).
    /// </summary>
    private void EmitSynthesizedRoutineInfoList(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);

        // For now, emit an empty list — full RoutineInfo entity allocation is complex
        // and will be implemented when the BuilderService stdlib types are fully parsed
        EmitSynthesizedStringList(funcName, meType, []);
    }

    /// <summary>
    /// Emits the body for a synthesized dependencies() routine.
    /// Returns a List[Text] of module dependencies — currently empty.
    /// </summary>
    private void EmitSynthesizedDependencies(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);
        EmitSynthesizedStringList(funcName, meType, []);
    }

    /// <summary>
    /// Emits the body for a synthesized member_type_id(member_name: Text) routine.
    /// Returns the type ID (FNV-1a hash) for a named member variable, or 0 if not found.
    /// </summary>
    private void EmitSynthesizedMemberTypeId(RoutineInfo routine, string funcName)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);

        IReadOnlyList<MemberVariableInfo>? fields = routine.OwnerType switch
        {
            RecordTypeInfo rec => rec.MemberVariables,
            EntityTypeInfo ent => ent.MemberVariables,
            _ => null
        };
        fields ??= [];

        var sb = _functionDefinitions;
        EmitLine(sb, $"define i64 @{funcName}({meType} %me, ptr %member_name) {{");
        EmitLine(sb, "entry:");

        if (fields.Count == 0)
        {
            EmitLine(sb, "  ret i64 0");
        }
        else
        {
            // Ensure Text.$eq is available for string comparison
            RoutineInfo? textEq = _registry.LookupRoutine("Text.$eq");
            string eqFuncName = textEq != null ? MangleFunctionName(textEq) : "Text$_eq";
            // Add to _generatedFunctions so the synthesized pass emits its body
            _generatedFunctions.Add(eqFuncName);

            // For each field, compare the member_name string and return the type ID
            for (int i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                string nameStr = EmitSynthesizedStringLiteral(field.Name);
                ulong typeId = ComputeTypeId(field.Type.FullName);

                string cmpResult = NextTemp();
                EmitLine(sb, $"  {cmpResult} = call i1 @{eqFuncName}(ptr %member_name, ptr {nameStr})");
                string nextLabel = i < fields.Count - 1 ? $"check_{i + 1}" : "not_found";
                EmitLine(sb, $"  br i1 {cmpResult}, label %found_{i}, label %{nextLabel}");

                EmitLine(sb, $"found_{i}:");
                EmitLine(sb, $"  ret i64 {unchecked((long)typeId)}");

                if (i < fields.Count - 1)
                {
                    EmitLine(sb, $"check_{i + 1}:");
                }
            }

            EmitLine(sb, "not_found:");
            EmitLine(sb, "  ret i64 0");
        }

        EmitLine(sb, "}");
        EmitLine(sb, "");
    }

    /// <summary>
    /// Emits a synthesized routine that returns a Text constant (no owner type required).
    /// Used for var_name() fallback.
    /// </summary>
    private void EmitSynthesizedBuilderServiceTextNoOwner(RoutineInfo routine, string funcName, string value)
    {
        if (routine.OwnerType == null) return;

        string meType = GetParameterLLVMType(routine.OwnerType);
        string strConst = EmitSynthesizedStringLiteral(value);

        EmitLine(_functionDefinitions, $"define ptr @{funcName}({meType} %me) {{");
        EmitLine(_functionDefinitions, "entry:");
        EmitLine(_functionDefinitions, $"  ret ptr {strConst}");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    #endregion

    #region BuilderService Platform/Build Info

    /// <summary>
    /// Emits a BuilderService standalone routine that returns a U64 constant.
    /// </summary>
    private void EmitSynthesizedBuilderServiceU64(RoutineInfo routine, string funcName, long value)
    {
        EmitLine(_functionDefinitions, $"define i64 @{funcName}() {{");
        EmitLine(_functionDefinitions, "entry:");
        EmitLine(_functionDefinitions, $"  ret i64 {value}");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Emits a BuilderService standalone routine that returns a Text constant.
    /// </summary>
    private void EmitSynthesizedBuilderServiceText(RoutineInfo routine, string funcName, string value)
    {
        string strConst = EmitSynthesizedStringLiteral(value);

        EmitLine(_functionDefinitions, $"define ptr @{funcName}() {{");
        EmitLine(_functionDefinitions, "entry:");
        EmitLine(_functionDefinitions, $"  ret ptr {strConst}");
        EmitLine(_functionDefinitions, "}");
        EmitLine(_functionDefinitions, "");
    }

    /// <summary>
    /// Detects the target operating system name.
    /// </summary>
    private static string DetectTargetOS()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows))
            return "windows";
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Linux))
            return "linux";
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.OSX))
            return "macos";
        return "unknown";
    }

    /// <summary>
    /// Detects the target CPU architecture name.
    /// </summary>
    private static string DetectTargetArch()
    {
        return System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x86_64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.Arm64 => "aarch64",
            System.Runtime.InteropServices.Architecture.Arm => "arm",
            _ => "unknown"
        };
    }

    #endregion
}
