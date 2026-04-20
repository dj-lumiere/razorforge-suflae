using Compiler.Instantiation;
using Compiler.Postprocessing.Passes;
using Compiler.Targeting;
using TypeModel.Enums;
using SemanticVerification.Enums;

namespace Compiler.CodeGen;

using System.Text;
using TypeModel.Symbols;
using TypeModel.Types;
using SyntaxTree;

public partial class LlvmCodeGenerator
{
    private string GetImplicitMeParameterDeclaration(RoutineInfo routine, bool includeName)
    {
        if (routine.OwnerType == null)
        {
            throw new InvalidOperationException(message: "Implicit 'me' requested for routine without owner type.");
        }

        // $setitem! on records: me is passed by pointer so the caller's alloca is mutated in-place.
        // The parameter is always "ptr %me.addr" regardless of the underlying value type.
        if (IsRecordSetItem(routine: routine))
        {
            string nameSuffix = includeName ? " %me.addr" : string.Empty;
            return $"ptr{nameSuffix}";
        }

        string meType = GetParameterLlvmType(type: routine.OwnerType);
        string attrs = GetImplicitMeParameterAttributes(routine: routine);
        string nameSuffix2 = includeName ? " %me" : string.Empty;

        return string.IsNullOrEmpty(value: attrs)
            ? $"{meType}{nameSuffix2}"
            : $"{meType} {attrs}{nameSuffix2}";
    }

    private static string GetImplicitMeParameterAttributes(RoutineInfo routine)
    {
        // Exclusive owner/borrow: no other live reference can alias this pointer
        if (routine.OwnerType is WrapperTypeInfo { Name: "Owned" or "Grasped" })
        {
            return routine.ModificationCategory == ModificationCategory.Readonly
                ? "noalias readonly"
                : "noalias";
        }

        if (routine.ModificationCategory != ModificationCategory.Readonly)
        {
            return string.Empty;
        }

        return routine.OwnerType switch
        {
            EntityTypeInfo or WrapperTypeInfo => "readonly",
            _ => string.Empty
        };
    }

    private static string GetExplicitParameterAttributes(TypeInfo? type) =>
        type is WrapperTypeInfo { Name: "Owned" or "Grasped" } ? "noalias" : string.Empty;

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
            paramTypes.Add(item: GetImplicitMeParameterDeclaration(routine: routine,
                includeName: false));
        }

        // Add explicit parameters
        // For external("C") functions, F16 (half) must be passed as i16 (C ABI uses integer register)
        bool isCExtern = routine.CallingConvention == "C";
        paramTypes.AddRange(collection: routine.Parameters.Select(selector: param =>
        {
            string t = GetParameterLlvmType(type: param.Type);
            if (isCExtern && t == "half") return "i16";
            string attrs = GetExplicitParameterAttributes(type: param.Type);
            return string.IsNullOrEmpty(attrs) ? t : $"{t} {attrs}";
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
            ? GetLlvmType(type: routine.ReturnType)
            : "void";
        if (routine.AsyncStatus == AsyncStatus.LookupVariant)
        {
            // TODO(C121): if routine.ReturnType is Blank, degenerate Lookup[Blank] → Result[Blank]
            // (absent state on a Blank lookup is semantically meaningless). Use GetResultCarrierLLVMType
            // and AsyncStatus.CheckVariant for such routines instead.
            returnType = GetLookupCarrierLlvmType(valueType: routine.ReturnType!);
        }
        else if (routine.AsyncStatus == AsyncStatus.CheckVariant)
        {
            returnType = GetResultCarrierLlvmType(valueType: routine.ReturnType!);
        }
        else if (routine.AsyncStatus == AsyncStatus.TryBoolVariant)
        {
            returnType = "i1";
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
            _rfFunctionDeclarations[key: funcName] = $"declare void @{funcName}({parameters})";
        }
        else
        {
            string parameters = string.Join(separator: ", ", values: paramTypes);
            string returnPrefix = isCreator && returnType == "ptr" ? "noalias " : "";
            _rfFunctionDeclarations[key: funcName] =
                $"declare {returnPrefix}{returnType} @{funcName}({parameters})";
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
        string llvmType = GetLlvmType(type: routine.ReturnType);
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
        // Skip 'me' for $create routines (static factories) and void/Blank owner types
        bool isCreator = routineInfo.Name.Contains(value: "$create");
        if (routineInfo.OwnerType != null && !isCreator)
        {
            string meParam = GetImplicitMeParameterDeclaration(routine: routineInfo,
                includeName: true);
            if (!meParam.StartsWith(value: "void", comparisonType: StringComparison.Ordinal))
            {
                paramList.Add(item: meParam);
            }
        }

        // Add explicit parameters
        // Sanitize names that conflict with LLVM's reserved block label "entry"
        paramList.AddRange(collection:
            from param in routineInfo.Parameters
            let paramType = GetParameterLlvmType(type: param.Type)
            let paramAttrs = GetExplicitParameterAttributes(type: param.Type)
            let emittedName = param.Name == "entry" ? "entry_" : param.Name
            select string.IsNullOrEmpty(paramAttrs)
                ? $"{paramType} %{emittedName}"
                : $"{paramType} {paramAttrs} %{emittedName}");

        // Get return type
        string returnType = routineInfo.ReturnType != null
            ? GetLlvmType(type: routineInfo.ReturnType)
            : "void";
        if (routineInfo.AsyncStatus == AsyncStatus.LookupVariant)
        {
            returnType = GetLookupCarrierLlvmType(valueType: routineInfo.ReturnType!);
        }
        else if (routineInfo.AsyncStatus == AsyncStatus.CheckVariant)
        {
            returnType = GetResultCarrierLlvmType(valueType: routineInfo.ReturnType!);
        }
        else if (routineInfo.AsyncStatus == AsyncStatus.TryBoolVariant)
        {
            returnType = "i1";
        }

        // Start function — save position so we can rollback on error
        string parameters = string.Join(separator: ", ", values: paramList);
        int savedLength = _functionDefinitions.Length;
        int savedTempCounter = _tempCounter;

        bool isInline = routineInfo.Annotations.Contains(value: "inline");
        string returnPrefix = isCreator && returnType == "ptr" ? "noalias " : "";
        string funcAttrs = isInline ? " alwaysinline" : "";
        EmitLine(sb: _functionDefinitions,
            line: $"define {returnPrefix}{returnType} @{funcName}({parameters}){funcAttrs} {{");
        EmitLine(sb: _functionDefinitions, line: "entry:");
        var bodyBuilder = new StringBuilder();

        try
        {
            GenerateFunctionBody(sb: bodyBuilder, body: routine.Body, routine: routineInfo);
            _functionDefinitions.Append(value: _currentFunctionEntryAllocas);
            _functionDefinitions.Append(value: bodyBuilder);
        }
        catch
        {
            // Rollback partial IR so the output stays well-formed, then re-throw so the
            // caller can decide whether to skip or abort compilation.
            _functionDefinitions.Length = savedLength;
            _tempCounter = savedTempCounter;
            _generatedFunctionDefs.Remove(item: funcName);
            _generatedFunctions.Remove(item: funcName);
            throw;
        }

        // End function
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
    }

    /// <summary>
    /// Generates code for a function body.
    /// Emits statements and ensures proper termination.
    /// </summary>
    private void GenerateFunctionBody(StringBuilder sb, Statement body, RoutineInfo routine)
    {
        // Clear local variables for this function
        _localVariables.Clear();
        _localVarLlvmNames.Clear();
        _varNameCounts.Clear();
        _localEntityVars.Clear();
        _localRcRecordVars.Clear();
        _localRetainedVars.Clear();
        _currentBlock = "entry";
        _currentFunctionEntryAllocas.Clear();
        _emittedAllocaNames.Clear();

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
                string meType = GetParameterLlvmType(type: routine.OwnerType);
                // Skip alloca/store for void me (Blank owner type — unit type, no data)
                if (meType != "void")
                {
                    EmitEntryAlloca(llvmName: "%me.addr", llvmType: meType);
                    EmitLine(sb: sb, line: $"  store {meType} %me, ptr %me.addr");
                }

                _localVariables[key: "me"] = routine.OwnerType;
            }
        }

        // Register parameters as local variables
        foreach (ParameterInfo param in routine.Parameters)
        {
            // Parameters are passed by value, create a local copy
            // Use "entry_" instead of "entry" to avoid conflict with the entry: block label
            string emittedParamName = param.Name == "entry" ? "entry_" : param.Name;
            string paramPtr = $"%{param.Name}.addr";
            string llvmType = GetLlvmType(type: param.Type);
            EmitEntryAlloca(llvmName: paramPtr, llvmType: llvmType);
            EmitLine(sb: sb,
                line: $"  store {llvmType} %{emittedParamName}, ptr {paramPtr}");
            _localVariables[key: param.Name] = param.Type;
        }

        // Emit stack trace push
        // In Release, skip @inline routines — they are implementation helpers that
        // add noise to the shadow stack without being meaningful call frames.
        bool isInline = routine.Annotations.Contains(value: "inline");
        _traceCurrentRoutine = ShouldEmitTrace && !(_buildMode is RfBuildMode.Release && isInline);
        if (_traceCurrentRoutine)
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
            EmitLine(sb: sb,
                line: $"  call void @_rf_trace_push(ptr {routineCStr}, ptr {fileCStr}, i32 {line}, i32 {col})");
        }

        // Emit the body statements — returns true if the block ends with a terminator
        bool terminated = EmitStatement(sb: sb, stmt: body);
        if (terminated)
        {
            return;
        }

        EmitUsingCleanup(sb: sb);
        EmitRcRecordCleanup(sb: sb);
        EmitEntityCleanup(sb: sb, returnedVarName: null);
        if (_traceCurrentRoutine)
            EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");
        string retType = routine.ReturnType != null
            ? GetLlvmType(type: routine.ReturnType)
            : "void";
        if (retType == "void")
        {
            EmitLine(sb: sb, line: "  ret void");
        }
        else
        {
            string zeroValue = GetZeroValue(type: routine.ReturnType!);
            EmitLine(sb: sb, line: $"  ret {retType} {zeroValue}");
        }
    }

    /// <summary>
    /// Whether this routine is a $setitem on a record type (needs pass-by-pointer for me).
    /// </summary>
    private static bool IsRecordSetItem(RoutineInfo routine)
    {
        return routine.OwnerType is RecordTypeInfo && routine.Name.Contains(value: "$setitem");
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
    internal static string MangleFunctionName(RoutineInfo routine)
    {
        // Lambda closures: [lambda]filename:line:col(paramTypes)
        if (routine.IsLambda)
        {
            string fileName =
                Path.GetFileName(path: routine.Location?.FileName ?? "[unknown]");
            int line = routine.Location?.Line ?? 0;
            int col = routine.Location?.Column ?? 0;
            string paramTypes = string.Join(separator: ",",
                values: routine.Parameters.Select(selector: p => p.Type.Name));
            return Q(name: DecorateRoutineSymbolName(
                baseName: $"[lambda]{fileName}:{line}:{col}({paramTypes})",
                isFailable: routine.IsFailable));
        }

        // External("C") functions use the raw C symbol name — no module prefix,
        // so that LLVM IR symbols match the actual C linker symbols.
        if (routine.CallingConvention == "C")
        {
            return Q(name: DecorateRoutineSymbolName(baseName: SanitizeLlvmName(name: routine.Name),
                isFailable: routine.IsFailable));
        }

        string name = SanitizeLlvmName(name: routine.Name);
        if (routine.OwnerType == null)
        {
            // Top-level: Module.Name (BaseName preserves the old FullName format)
            string fullName = SanitizeLlvmName(name: routine.BaseName);

            // Generic instance: append type arguments (e.g., IO.show → IO.show#S64)
            if (routine.TypeArguments is { Count: > 0 })
            {
                string typeArgSuffix = string.Join(separator: ",",
                    values: routine.TypeArguments.Select(selector: t => t.Name));
                // Disambiguate variadic overloads (e.g., show...#Text vs show#Text)
                string variadicMarker = routine.IsVariadic
                    ? "..."
                    : "";
                fullName = $"{fullName}({typeArgSuffix}{variadicMarker})";
            }

            return Q(name: DecorateRoutineSymbolName(baseName: fullName,
                isFailable: routine.IsFailable));
        }

        // Common (type-level static) routines: [common]Module.Type.name(paramTypes)
        if (routine.IsCommon)
        {
            string typeName = routine.OwnerType.FullName;
            string paramTypes = string.Join(separator: ",",
                values: routine.Parameters.Select(selector: p => p.Type.FullName));
            return Q(name: DecorateRoutineSymbolName(
                baseName: $"[common]{typeName}.{name}({paramTypes})",
                isFailable: routine.IsFailable));
        }

        // Method: Module.OwnerType.Name (OwnerType.FullName includes module)
        string ownerTypeName = routine.OwnerType.FullName;
        string baseName = $"{ownerTypeName}.{name}";

        // Disambiguate $create overloads by first parameter type (fully qualified)
        if (name == "$create" && routine.Parameters.Count > 0)
        {
            string firstParamType = routine.Parameters[index: 0].Type.FullName;
            baseName = $"{baseName}({firstParamType})";
        }

        return Q(name: DecorateRoutineSymbolName(baseName: baseName,
            isFailable: routine.IsFailable));
    }

    internal static string DecorateRoutineSymbolName(string baseName, bool isFailable)
    {
        return isFailable
            ? $"{baseName}!"
            : baseName;
    }

    /// <summary>
    /// Sanitizes a name for use as an LLVM IR identifier.
    /// Replaces characters that are invalid in LLVM identifiers.
    /// </summary>
    internal static string SanitizeLlvmName(string name)
    {
        return name.Replace(oldValue: "!", newValue: "");
    }


    /// <summary>
    /// <summary>
    /// Emits LLVM IR for all pending monomorphizations.
    /// <para>
    /// Bodies are pre-rewritten by <see cref="MonomorphizationPlanner.PreRewriteAll"/> before
    /// this method runs, so the inner loop has no AST search or substitution-map building.
    /// Any entries added <em>during</em> emission (late-discovered via <see cref="RecordMonomorphization"/>
    /// calls in expression emitters) fall back to on-demand rewriting via the planner.
    /// </para>
    /// </summary>
    private void MonomorphizeGenericMethods()
    {
        // Pre-rewrite any entries that were newly recorded since the last call
        _planner.PreRewriteAll(synthesizedBodies: _synthesizedBodies);

        // Primary path: emit all pre-rewritten bodies
        foreach ((string mangledName, MonomorphizedBody body) in _planner.MonomorphizedBodies.ToList())
        {
            if (_generatedFunctionDefs.Contains(item: mangledName))
                continue;

            EmitMonomorphizedBody(mangledName: mangledName, body: body);
        }

        // Fallback path: entries that arrived too late to be pre-rewritten
        // (e.g., discovered by protocol-dispatch or collection-literal emitters during
        // the current round). The outer fixed-point loop in GenerateFunctionDefinitions
        // re-invokes PreRewriteAll → MonomorphizeGenericMethods until convergence.
        foreach ((string mangledName, MonomorphizationEntry entry) in
                 _planner.PendingMonomorphizations.ToList())
        {
            if (_generatedFunctionDefs.Contains(item: mangledName))
                continue;
            if (_planner.MonomorphizedBodies.ContainsKey(key: mangledName))
                continue;

            // Entry was not yet pre-rewritten — rewrite on demand and emit
            MonomorphizedBody? onDemandBody = _planner.BuildBodyPublic(
                mangledName: mangledName,
                entry: entry,
                synthesizedBodies: _synthesizedBodies);
            if (onDemandBody != null)
            {
                _planner.MonomorphizedBodies[key: mangledName] = onDemandBody;
                // Fold any residual BS calls (e.g. Byte.data_size()) that GenericAstRewriter
                // left unfolded in on-demand-built bodies. Re-fold the full map so the new
                // entry is covered; already-folded entries are no-ops.
                new BuilderServiceInliningPass(_registry).RunOnMonomorphizedBodies(
                    _planner.MonomorphizedBodies);
                EmitMonomorphizedBody(mangledName: mangledName,
                    body: _planner.MonomorphizedBodies[key: mangledName]);
            }
        }
    }

    /// <summary>
    /// Emits the LLVM IR body for a single pre-rewritten monomorphization entry.
    /// </summary>
    private void EmitMonomorphizedBody(string mangledName, MonomorphizedBody body)
    {
        // Ensure LLVM type declarations exist for owner, return, and parameter types
        if (body.Info.OwnerType is EntityTypeInfo ownerEntity)
            GenerateEntityType(entity: ownerEntity);
        else if (body.Info.OwnerType is RecordTypeInfo ownerRecord)
            GenerateRecordType(record: ownerRecord);

        if (body.Info.ReturnType is RecordTypeInfo returnRecord &&
            !returnRecord.HasDirectBackendType &&
            !returnRecord.IsSingleMemberVariableWrapper &&
            !returnRecord.IsGenericDefinition)
        {
            GenerateRecordType(record: returnRecord);
        }

        foreach (ParameterInfo param in body.Info.Parameters)
        {
            if (param.Type is RecordTypeInfo paramRecord &&
                !paramRecord.HasDirectBackendType &&
                !paramRecord.IsSingleMemberVariableWrapper &&
                !paramRecord.IsGenericDefinition)
            {
                GenerateRecordType(record: paramRecord);
            }
        }

        // Set type substitution fallback for expressions whose ResolvedType still carries
        // generic parameter names (SA annotated the generic template, not the rewritten copy)
        _typeSubstitutions = body.TypeSubs;
        try
        {
            if (body.IsSynthesized)
            {
                _generatedFunctionDefs.Add(item: mangledName);
                _generatedFunctions.Add(item: mangledName);

                // If the body has a real AST (derived-operator case), use EmitSynthesizedBodyFromAst
                if (body.Ast.Body is not BlockStatement { Statements.Count: 0 })
                    EmitSynthesizedBodyFromAst(routine: body.Info, funcName: mangledName,
                        body: body.Ast.Body);
                else
                    EmitSynthesizedRoutineBody(routine: body.Info, funcName: mangledName);
            }
            else
            {
                GenerateFunctionDefinition(routine: body.Ast,
                    preResolvedInfo: body.Info,
                    nameOverride: mangledName);
            }
        }
        finally
        {
            _typeSubstitutions = null;
        }
    }

}
