namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Symbols;
using SemanticAnalysis.Types;
using SyntaxTree;

public partial class LLVMCodeGenerator
{
    private void GenerateFunctionDeclaration(RoutineInfo routine, string? nameOverride = null)
    {
        string funcName = nameOverride ?? MangleFunctionName(routine: routine);

        // Skip if already generated
        if (_generatedFunctions.Contains(item: funcName))
        {
            return;
        }

        // Skip declarations that reference unresolved generic parameter types —
        // these produce invalid LLVM IR (e.g., Maybe[BTreeDictNode[K, V]] instead of concrete types)
        if (routine.Parameters.Any(predicate: p =>
                p.Type != null && ContainsGenericParameter(type: p.Type)) ||
            routine.ReturnType != null && ContainsGenericParameter(type: routine.ReturnType) ||
            routine.OwnerType != null && ContainsGenericParameter(type: routine.OwnerType))
        {
            return;
        }

        _generatedFunctions.Add(item: funcName);

        // Build parameter list
        var paramTypes = new List<string>();

        // For methods, add implicit 'me' parameter first
        // Skip 'me' for $create routines (static factories)
        bool isCreator = routine.Name.Contains(value: "$create");
        if (routine.OwnerType != null && !isCreator)
        {
            // $setitem! on records: me is passed by pointer so mutations propagate to caller
            if (IsRecordSetItem(routine: routine))
            {
                paramTypes.Add(item: "ptr");
            }
            else
            {
                string meType = GetParameterLLVMType(type: routine.OwnerType);
                paramTypes.Add(item: meType);
            }
        }

        // Add explicit parameters
        // For external("C") functions, F16 (half) must be passed as i16 (C ABI uses integer register)
        bool isCExtern = routine.CallingConvention == "C";
        paramTypes.AddRange(collection: routine.Parameters.Select(selector: param =>
        {
            string t = GetParameterLLVMType(type: param.Type);
            return isCExtern && t == "half"
                ? "i16"
                : t;
        }));

        // Ensure record type definitions exist for parameter and return types
        foreach (ParameterInfo param in routine.Parameters)
        {
            if (param.Type is RecordTypeInfo paramRecord && !paramRecord.HasDirectBackendType &&
                !paramRecord.IsSingleMemberVariableWrapper && !paramRecord.IsGenericDefinition)
            {
                GenerateRecordType(record: paramRecord);
            }
        }

        if (routine.ReturnType is RecordTypeInfo returnRecord &&
            !returnRecord.HasDirectBackendType && !returnRecord.IsSingleMemberVariableWrapper &&
            !returnRecord.IsGenericDefinition)
        {
            GenerateRecordType(record: returnRecord);
        }

        // Get return type
        string returnType = routine.ReturnType != null
            ? GetLLVMType(type: routine.ReturnType)
            : "void";
        // Emitting and failable routines return Maybe[T] = { i64, ptr } at IR level
        if (routine.AsyncStatus == AsyncStatus.Emitting || routine.IsFailable)
        {
            returnType = "{ i64, ptr }";
        }

        if (isCExtern && returnType == "half")
        {
            returnType = "i16";
        }

        // On Windows x64 MSVC ABI, C structs > 8 bytes are returned via hidden sret pointer.
        // LLVM IR passes {i64, i64} in registers (RAX:RDX) but C expects sret, causing ABI mismatch.
        // Detect this case and emit the declaration with sret convention.
        bool needsSret = isCExtern && NeedsCExternSret(routine: routine);
        if (needsSret)
        {
            // Change declaration: void @func(ptr sret(%RecordType), original_params...)
            paramTypes.Insert(index: 0, item: $"ptr sret({returnType})");
            string parameters = string.Join(separator: ", ", values: paramTypes);
            EmitLine(sb: _functionDeclarations, line: $"declare void @{funcName}({parameters})");
        }
        else
        {
            // Normal declaration
            string parameters = string.Join(separator: ", ", values: paramTypes);
            EmitLine(sb: _functionDeclarations,
                line: $"declare {returnType} @{funcName}({parameters})");
        }
    }

    /// <summary>
    /// Checks if an external("C") function returns a struct type that needs sret on Windows x64.
    /// On MSVC ABI, C structs > 8 bytes are returned via a hidden first pointer parameter (sret).
    /// LLVM's {i64, i64} return convention uses RAX:RDX registers, which doesn't match.
    /// </summary>
    private bool NeedsCExternSret(RoutineInfo routine)
    {
        if (routine.ReturnType == null)
        {
            return false;
        }

        // Only record types that map to LLVM struct types (not intrinsics or single-wrapper) can need sret
        string llvmType = GetLLVMType(type: routine.ReturnType);
        if (!llvmType.StartsWith(value: "%Record.") && !llvmType.StartsWith(value: "%\"Record.") &&
            !llvmType.StartsWith(value: "%Tuple.") && !llvmType.StartsWith(value: "%\"Tuple."))
        {
            return false;
        }

        // On Windows x64 MSVC ABI, structs > 8 bytes use sret.
        // TODO: On System V x86_64 (GCC/Linux), the threshold is > 16 bytes (RAX:RDX).
        //       Structs 9–16 bytes are register-returned on Linux but sret on Windows.
        //       This needs a target ABI config when cross-platform support is added.
        int size = GetTypeSize(type: routine.ReturnType);
        return size > 8;
    }

    /// <summary>
    /// Generates the LLVM function definition (with body).
    /// </summary>
    /// <param name="routine">The routine declaration from AST.</param>
    /// <param name="preResolvedInfo">Optional pre-resolved routine metadata.</param>
    /// <param name="nameOverride">Optional mangled name override.</param>
    private void GenerateFunctionDefinition(RoutineDeclaration routine,
        RoutineInfo? preResolvedInfo = null, string? nameOverride = null)
    {
        RoutineInfo? routineInfo = preResolvedInfo;

        if (routineInfo == null)
        {
            // Look up the routine info from registry
            // For module-qualified names like "Console.show", the registry key may be
            // "IO.show" (module.name). Try full AST name first, then short name lookup.
            string baseName = routine.Name;
            routineInfo = _registry.LookupRoutine(fullName: baseName);
            if (routineInfo == null)
            {
                int dotIdx = baseName.IndexOf(value: '.');
                if (dotIdx > 0)
                {
                    string shortName = baseName[(dotIdx + 1)..];
                    routineInfo = _registry.LookupRoutine(fullName: shortName) ??
                                  _registry.LookupRoutineByName(name: shortName);
                }
                else
                {
                    // No dot — try short name fallback (e.g., "show" → finds "IO.show")
                    routineInfo = _registry.LookupRoutineByName(name: baseName);
                }
            }

            // For overloaded routines, resolve the specific overload matching this AST
            if (routineInfo != null && routine.Parameters.Count > 0)
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
                    RoutineInfo? overload =
                        _registry.LookupRoutineOverload(baseName: routineInfo.BaseName,
                            argTypes: astParamTypes);
                    if (overload != null)
                    {
                        routineInfo = overload;
                    }
                }
            }
        }

        if (routineInfo == null || routineInfo.IsGenericDefinition ||
            routineInfo.OwnerType is GenericParameterTypeInfo)
        {
            return; // Skip generic definitions, unresolved routines, and generic-param-owner routines
        }

        // Skip routines with error types in their signature
        if (HasErrorTypes(routine: routineInfo))
        {
            return;
        }

        string funcName = nameOverride ?? MangleFunctionName(routine: routineInfo);

        // Skip if already generated (prevents duplicates between user program and stdlib)
        if (!_generatedFunctionDefs.Add(item: funcName))
        {
            return;
        }

        // Also mark as generated in declarations set to prevent declare/define conflicts
        _generatedFunctions.Add(item: funcName);

        // Build parameter list with names
        var paramList = new List<string>();

        // For methods, add implicit 'me' parameter first
        // Skip 'me' for $create routines (static factories)
        bool isCreator = routineInfo.Name.Contains(value: "$create");
        if (routineInfo.OwnerType != null && !isCreator)
        {
            // $setitem! on records: me is passed by pointer (named %me.addr)
            // so mutations propagate to the caller's alloca
            if (IsRecordSetItem(routine: routineInfo))
            {
                paramList.Add(item: "ptr %me.addr");
            }
            else
            {
                string meType = GetParameterLLVMType(type: routineInfo.OwnerType);
                paramList.Add(item: $"{meType} %me");
            }
        }

        // Add explicit parameters
        paramList.AddRange(collection:
            from param in routineInfo.Parameters
            let paramType = GetParameterLLVMType(type: param.Type)
            select $"{paramType} %{param.Name}");

        // Get return type
        string returnType = routineInfo.ReturnType != null
            ? GetLLVMType(type: routineInfo.ReturnType)
            : "void";
        // Emitting and failable routines return Maybe[T] = { i64, ptr } at IR level
        if (routineInfo.AsyncStatus == AsyncStatus.Emitting || routineInfo.IsFailable)
        {
            returnType = "{ i64, ptr }";
        }

        // Start function — save position so we can rollback on error
        string parameters = string.Join(separator: ", ", values: paramList);
        int savedLength = _functionDefinitions.Length;
        int savedTempCounter = _tempCounter;

        EmitLine(sb: _functionDefinitions,
            line: $"define {returnType} @{funcName}({parameters}) {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");

        // Generate body — if it throws, rollback and emit a stub function
        try
        {
            GenerateFunctionBody(body: routine.Body, routine: routineInfo);
        }
        catch (Exception ex)
        {
            // Log the error for debugging
            Console.Error.WriteLine(
                value: $"Warning: Codegen failed for '{funcName}': {ex.Message}");

            // Rollback any partial output from the failed body
            _functionDefinitions.Length = savedLength;
            _tempCounter = savedTempCounter;

            // Emit a stub function
            EmitLine(sb: _functionDefinitions,
                line: $"define {returnType} @{funcName}({parameters}) {{");
            EmitLine(sb: _functionDefinitions, line: "entry:");
            if (returnType == "void")
            {
                EmitLine(sb: _functionDefinitions, line: "  ret void");
            }
            else
            {
                EmitLine(sb: _functionDefinitions, line: $"  ret {returnType} zeroinitializer");
            }
        }

        // End function
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
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
        _localRCRecordVars.Clear();
        _currentBlock = "entry";
        _emitSlotAddr = null;
        _emitSlotType = null;

        // Set current function return type for use in EmitReturn
        _currentFunctionReturnType = routine.ReturnType;
        _currentRoutineIsFailable = routine.IsFailable;

        // Track current routine for source_routine() / source_module() injection
        _currentEmittingRoutine = routine;

        // Register implicit 'me' parameter for methods (skip for $create static factories)
        if (routine.OwnerType != null && !routine.Name.Contains(value: "$create"))
        {
            if (IsRecordSetItem(routine: routine))
            {
                // $setitem! on records: %me.addr IS the function parameter (caller's alloca pointer)
                // No alloca/store needed — mutations go directly to the caller's variable
                _localVariables[key: "me"] = routine.OwnerType;
            }
            else
            {
                string meType = GetParameterLLVMType(type: routine.OwnerType);
                EmitLine(sb: _functionDefinitions, line: $"  %me.addr = alloca {meType}");
                EmitLine(sb: _functionDefinitions, line: $"  store {meType} %me, ptr %me.addr");
                _localVariables[key: "me"] = routine.OwnerType;
            }
        }

        // Register parameters as local variables
        foreach (ParameterInfo param in routine.Parameters)
        {
            // Parameters are passed by value, create a local copy
            string paramPtr = $"%{param.Name}.addr";
            string llvmType = GetLLVMType(type: param.Type);
            EmitLine(sb: _functionDefinitions, line: $"  {paramPtr} = alloca {llvmType}");
            EmitLine(sb: _functionDefinitions,
                line: $"  store {llvmType} %{param.Name}, ptr {paramPtr}");
            _localVariables[key: param.Name] = param.Type;
        }

        // Emit stack trace push
        {
            string paramTypes = string.Join(separator: ", ",
                values: routine.Parameters.Select(selector: p => p.Type.Name));
            string failable = routine.IsFailable ? "!" : "";
            string routineName = $"{routine.BaseName}{failable}({paramTypes})";
            string fileName = routine.Location?.FileName ?? "<unknown>";
            int line = routine.Location?.Line ?? 0;
            int col = routine.Location?.Column ?? 0;
            string routineCStr = EmitCStringConstant(value: routineName);
            string fileCStr = EmitCStringConstant(value: fileName);
            string routineAsInt = NextTemp();
            string fileAsInt = NextTemp();
            EmitLine(sb: _functionDefinitions,
                line: $"  {routineAsInt} = ptrtoint ptr {routineCStr} to i64");
            EmitLine(sb: _functionDefinitions,
                line: $"  {fileAsInt} = ptrtoint ptr {fileCStr} to i64");
            EmitLine(sb: _functionDefinitions,
                line:
                $"  call void @rf_trace_push(i64 {routineAsInt}, i64 {fileAsInt}, i32 {line}, i32 {col})");
        }

        // Emit the body statements
        EmitStatement(sb: _functionDefinitions, stmt: body);

        // Ensure function is properly terminated
        // Check if the last instruction was a terminator (ret, br, etc.)
        // If not, add a default return
        if (EndsWithTerminator(sb: _functionDefinitions))
        {
            return;
        }

        EmitUsingCleanup(sb: _functionDefinitions);
        EmitRCRecordCleanup(sb: _functionDefinitions);
        EmitEntityCleanup(sb: _functionDefinitions, returnedVarName: null);
        EmitLine(sb: _functionDefinitions, line: "  call void @rf_trace_pop()");
        if (routine.ReturnType == null)
        {
            if (_currentRoutineIsFailable)
            {
                EmitLine(sb: _functionDefinitions, line: "  ret { i64, ptr } { i64 0, ptr null }");
            }
            else
            {
                EmitLine(sb: _functionDefinitions, line: "  ret void");
            }
        }
        else
        {
            string returnType = GetLLVMType(type: routine.ReturnType);
            string zeroValue = GetZeroValue(type: routine.ReturnType);
            EmitLine(sb: _functionDefinitions, line: $"  ret {returnType} {zeroValue}");
        }
    }

    /// <summary>
    /// Checks if the StringBuilder ends with a terminator instruction.
    /// </summary>
    /// <summary>
    /// Whether this routine is a $setitem on a record type (needs pass-by-pointer for me).
    /// </summary>
    private static bool IsRecordSetItem(RoutineInfo routine)
    {
        return routine.OwnerType is RecordTypeInfo && routine.Name.Contains(value: "$setitem");
    }

    private static bool EndsWithTerminator(StringBuilder sb)
    {
        string content = sb.ToString();
        string[] lines =
            content.Split(separator: '\n', options: StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return false;
        }

        string lastLine = lines[^1]
           .Trim();
        return lastLine.StartsWith(value: "ret ") || lastLine.StartsWith(value: "br ") ||
               lastLine.StartsWith(value: "unreachable");
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
                "@intrinsic.f16" or "@intrinsic.f32" or "@intrinsic.f64"
                    or "@intrinsic.f128" => "0.0",
                "@intrinsic.ptr" => "null",
                _ => "0"
            },
            RecordTypeInfo { HasDirectBackendType: true } record => GetZeroValueForLlvmType(
                llvmType: record.BackendType!),
            RecordTypeInfo { IsSingleMemberVariableWrapper: true } record => GetZeroValue(
                type: record.UnderlyingIntrinsic!),
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
        {
            return Q(name: SanitizeLLVMName(name: routine.Name));
        }

        string name = SanitizeLLVMName(name: routine.Name);
        if (routine.OwnerType == null)
        {
            // Top-level: Module.Name (BaseName preserves the old FullName format)
            string fullName = SanitizeLLVMName(name: routine.BaseName);

            // Generic instance: append type arguments (e.g., IO.show → IO.show#S64)
            if (routine.TypeArguments is { Count: > 0 })
            {
                string typeArgSuffix = string.Join(separator: ",",
                    values: routine.TypeArguments.Select(selector: t => t.Name));
                // Disambiguate variadic overloads (e.g., show...#Text vs show#Text)
                string variadicMarker = routine.IsVariadic
                    ? "..."
                    : "";
                fullName = $"{fullName}{variadicMarker}#{typeArgSuffix}";
            }

            return Q(name: fullName);
        }

        // Method: Module.OwnerType.Name (OwnerType.FullName includes module)
        string typeName = routine.OwnerType.FullName;
        string baseName = $"{typeName}.{name}";

        // Disambiguate $create overloads by first parameter type
        if (name == "$create" && routine.Parameters.Count > 0)
        {
            string firstParamType = routine.Parameters[index: 0].Type.Name;
            baseName = $"{baseName}#{firstParamType}";
        }

        return Q(name: baseName);
    }

    /// <summary>
    /// Sanitizes a name for use as an LLVM IR identifier.
    /// Replaces characters that are invalid in LLVM identifiers.
    /// </summary>
    private static string SanitizeLLVMName(string name)
    {
        return name.Replace(oldValue: "!", newValue: "");
    }


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

            foreach ((string mangledName, MonomorphizationEntry entry) in _pendingMonomorphizations
                        .ToList())
            {
                if (_generatedFunctionDefs.Contains(item: mangledName))
                {
                    continue;
                }

                // Find the generic AST body in stdlib programs
                // For $create overloads, pass first param type for disambiguation (e.g., SortedSet[T] vs U64)
                string? firstParamGenericType = null;
                if (entry.GenericMethod.Name == "$create" &&
                    entry.GenericMethod.Parameters.Count > 0)
                {
                    TypeInfo paramType = entry.GenericMethod.Parameters[index: 0].Type;
                    // Reverse-substitute concrete types back to generic params for AST matching
                    // e.g., SortedSet[S64] → SortedSet (the base name is enough to disambiguate)
                    firstParamGenericType = GetGenericBaseName(type: paramType) ?? paramType.Name;
                }

                RoutineDeclaration? astRoutine = FindGenericAstRoutine(
                    genericAstName: entry.GenericAstName,
                    expectedParamCount: entry.GenericMethod.Parameters.Count,
                    firstParamTypeHint: firstParamGenericType);

                // Fallback: try the concrete resolved name for non-generic specializations
                // e.g., "List[Byte].$create" for a concrete overload like List[Byte].$create(from: Bytes)
                if (astRoutine == null && entry.ResolvedOwnerType != null)
                {
                    string concreteName =
                        $"{entry.ResolvedOwnerType.Name}.{entry.GenericMethod.Name}";
                    astRoutine = FindGenericAstRoutine(
                        genericAstName: concreteName,
                        expectedParamCount: entry.GenericMethod.Parameters.Count,
                        firstParamTypeHint: firstParamGenericType);
                }

                if (astRoutine == null)
                {
                    // Synthesized routines (type_name, $ne, etc.) have no AST body —
                    // emit their bodies directly for the monomorphized type
                    if (entry.GenericMethod.IsSynthesized)
                    {
                        RoutineInfo synthInfo = BuildResolvedRoutineInfo(entry: entry);
                        EmitSynthesizedRoutineBody(routine: synthInfo, funcName: mangledName);
                        _generatedFunctionDefs.Add(item: mangledName);
                    }

                    continue;
                }

                // Build a resolved RoutineInfo with the correct owner type and substituted types
                RoutineInfo resolvedInfo = BuildResolvedRoutineInfo(entry: entry);

                // Build string substitution map and rewrite AST with concrete types
                var astSubs = new Dictionary<string, string>();
                foreach ((string paramName, TypeInfo typeInfo) in entry.TypeSubstitutions)
                {
                    astSubs[key: paramName] = typeInfo.Name;
                }

                RoutineDeclaration rewrittenAst =
                    GenericAstRewriter.Rewrite(routine: astRoutine, subs: astSubs);

                // Ensure entity/record type definitions are emitted for monomorphized types
                if (entry.ResolvedOwnerType is EntityTypeInfo ownerEntity)
                {
                    GenerateEntityType(entity: ownerEntity);
                }
                else if (entry.ResolvedOwnerType is RecordTypeInfo ownerRecord)
                {
                    GenerateRecordType(record: ownerRecord);
                }

                // Also ensure return type and parameter record types are defined
                if (resolvedInfo.ReturnType is RecordTypeInfo returnRecord &&
                    !returnRecord.HasDirectBackendType &&
                    !returnRecord.IsSingleMemberVariableWrapper &&
                    !returnRecord.IsGenericDefinition)
                {
                    GenerateRecordType(record: returnRecord);
                }

                foreach (ParameterInfo param in resolvedInfo.Parameters)
                {
                    if (param.Type is RecordTypeInfo paramRecord &&
                        !paramRecord.HasDirectBackendType &&
                        !paramRecord.IsSingleMemberVariableWrapper &&
                        !paramRecord.IsGenericDefinition)
                    {
                        GenerateRecordType(record: paramRecord);
                    }
                }

                // Keep _typeSubstitutions as fallback for ResolvedType metadata
                _typeSubstitutions = entry.TypeSubstitutions;
                try
                {
                    GenerateFunctionDefinition(routine: rewrittenAst,
                        preResolvedInfo: resolvedInfo,
                        nameOverride: mangledName);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        value:
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
    /// Finds the AST declaration for a generic routine across user and stdlib programs.
    /// </summary>
    private RoutineDeclaration? FindGenericAstRoutine(string genericAstName,
        int expectedParamCount = -1, string? firstParamTypeHint = null)
    {
        bool requireGeneric = genericAstName.EndsWith(value: "[generic]");
        string baseName = requireGeneric
            ? genericAstName[..genericAstName.IndexOf(value: "[generic]")]
            : genericAstName;

        RoutineDeclaration? firstMatch = null;

        bool MatchesRoutine(RoutineDeclaration routine)
        {
            if (routine.Name != baseName)
            {
                return false;
            }

            if (requireGeneric && routine.GenericParameters is not { Count: > 0 })
            {
                return false;
            }

            if (expectedParamCount >= 0 && routine.Parameters.Count != expectedParamCount)
            {
                return false;
            }

            return true;
        }

        bool MatchesParamType(RoutineDeclaration routine)
        {
            if (firstParamTypeHint == null || routine.Parameters.Count == 0)
            {
                return true;
            }

            string astParamType = routine.Parameters[index: 0].Type.Name;
            return astParamType.StartsWith(value: firstParamTypeHint);
        }

        foreach ((Program userProgram, string _, string _) in _userPrograms)
        {
            foreach (IAstNode decl in userProgram.Declarations)
            {
                if (decl is RoutineDeclaration routine && MatchesRoutine(routine: routine))
                {
                    firstMatch ??= routine;
                    if (MatchesParamType(routine: routine))
                    {
                        return routine;
                    }
                }
            }
        }

        foreach ((Program program, string _, string _) in _stdlibPrograms)
        {
            foreach (IAstNode decl in program.Declarations)
            {
                if (decl is RoutineDeclaration routine && MatchesRoutine(routine: routine))
                {
                    firstMatch ??= routine;
                    if (MatchesParamType(routine: routine))
                    {
                        return routine;
                    }
                }
            }
        }

        // Only fall back to firstMatch if we weren't filtering by param type,
        // otherwise we'd return the wrong overload (e.g., $create(U64) instead of $create(Bytes))
        if (firstMatch != null && firstParamTypeHint == null)
        {
            return firstMatch;
        }

        if (expectedParamCount >= 0)
        {
            return FindGenericAstRoutine(genericAstName: genericAstName,
                expectedParamCount: -1,
                firstParamTypeHint: firstParamTypeHint);
        }

        return null;
    }

    /// <summary>
    /// Resolves a type by applying generic substitutions for monomorphization.
    /// </summary>
    private TypeInfo ResolveSubstitutedType(TypeInfo type, Dictionary<string, TypeInfo> subs)
    {
        if (subs.TryGetValue(key: type.Name, value: out TypeInfo? sub))
        {
            return sub;
        }

        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            bool anySubstituted = false;
            var substitutedArgs = new List<TypeInfo>();
            foreach (TypeInfo arg in type.TypeArguments)
            {
                TypeInfo resolved = ResolveSubstitutedType(type: arg, subs: subs);
                substitutedArgs.Add(item: resolved);
                if (!ReferenceEquals(objA: resolved, objB: arg))
                {
                    anySubstituted = true;
                }
            }

            if (anySubstituted)
            {
                TypeInfo? genericBase = GetGenericBase(type: type);
                if (genericBase != null)
                {
                    return _registry.GetOrCreateResolution(genericDef: genericBase,
                        typeArguments: substitutedArgs);
                }
            }
        }

        if (type is { IsGenericDefinition: true, GenericParameters: not null } &&
            type.TypeArguments == null)
        {
            var typeArgs = type.GenericParameters
                               .Select(selector: gp =>
                                    subs.TryGetValue(key: gp, value: out TypeInfo? s)
                                        ? s
                                        : _registry.LookupType(name: gp))
                               .Where(predicate: t => t != null)
                               .ToList();
            if (typeArgs.Count == type.GenericParameters.Count)
            {
                return _registry.GetOrCreateResolution(genericDef: type, typeArguments: typeArgs!);
            }
        }

        return type;
    }

    /// <summary>
    /// Builds the concrete RoutineInfo used for a monomorphized generic routine.
    /// </summary>
    private RoutineInfo BuildResolvedRoutineInfo(MonomorphizationEntry entry)
    {
        RoutineInfo generic = entry.GenericMethod;
        Dictionary<string, TypeInfo> subs = entry.TypeSubstitutions;

        var resolvedParams = generic.Parameters
                                    .Select(selector: p =>
                                     {
                                         TypeInfo resolvedType =
                                             ResolveSubstitutedType(type: p.Type, subs: subs);
                                         return p.WithSubstitutedType(newType: resolvedType);
                                     })
                                    .ToList();

        TypeInfo? resolvedReturnType = generic.ReturnType;
        if (resolvedReturnType != null)
        {
            resolvedReturnType = ResolveSubstitutedType(type: resolvedReturnType, subs: subs);
        }

        return new RoutineInfo(name: generic.Name)
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
}
