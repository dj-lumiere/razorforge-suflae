using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Compiler.Targeting;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using Verification.Enums;

namespace Compiler.CodeGen;

public partial class LlvmCodeGenerator
{
    private void GenerateRoutineDeclaration(RoutineInfo routine, string? nameOverride = null)
    {
        string funcName = nameOverride ?? MangleRoutineName(routine: routine);

        // Skip if already generated
        if (_generatedRoutines.Contains(item: funcName))
        {
            return;
        }

        // Skip @innate routines — compile-time stubs, never have bodies, must not reach codegen
        if (routine.Annotations.Contains(value: "innate"))
            return;

        // Skip declarations on generic-definition owner types (e.g. Owned[T], List[T]) —
        // these produce invalid LLVM IR; only monomorphized concrete instances should be declared
        if (routine.OwnerType?.IsGenericDefinition == true)
            return;

        // Skip declarations that reference unresolved generic parameter types —
        // these produce invalid LLVM IR (e.g., Maybe[BTreeDictNode[K, V]] instead of concrete types)
        if (routine.Parameters.Any(predicate: p =>
                ContainsGenericParameter(type: p.Type)) ||
            routine.ReturnType != null && ContainsGenericParameter(type: routine.ReturnType) ||
            routine.OwnerType != null && ContainsGenericParameter(type: routine.OwnerType))
        {
            return;
        }

        _generatedRoutines.Add(item: funcName);

        // Build parameter list
        var paramTypes = new List<string>();

        // For methods, add implicit 'me' parameter first
        // Skip 'me' for $create routines (static factories) and common (type-level) routines
        bool isCreator = IsCreatorRoutine(routine: routine);
        if (routine.OwnerType != null && !isCreator && !routine.IsCommon)
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
                !paramRecord.IsGenericDefinition)
            {
                GenerateRecordType(record: paramRecord);
            }
        }

        if (routine.ReturnType is RecordTypeInfo returnRecord &&
            !returnRecord.HasDirectBackendType &&
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
            // Lookup[Blank] degenerates to Result[Blank]: a Blank value payload makes the
            // "found vs not-found" distinction meaningless, so use the Result carrier instead.
            returnType = routine.ReturnType?.IsBlank == true
                ? GetResultCarrierLlvmType(valueType: routine.ReturnType!)
                : GetLookupCarrierLlvmType(valueType: routine.ReturnType!);
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
            _rfRoutineDeclarations[key: funcName] = $"declare void @{funcName}({parameters})";
        }
        else
        {
            string parameters = string.Join(separator: ", ", values: paramTypes);
            string returnPrefix = isCreator && returnType == "ptr" ? "noalias " : "";
            _rfRoutineDeclarations[key: funcName] =
                $"declare {returnPrefix}{returnType} @{funcName}({parameters})";
        }
    }

    /// <summary>
    /// Generates the LLVM function definition (with body).
    /// </summary>
    /// <param name="routine">The routine declaration from AST.</param>
    /// <param name="preResolvedInfo">Optional pre-resolved routine metadata.</param>
    /// <param name="nameOverride">Optional mangled name override.</param>
    private void GenerateRoutineDefinition(RoutineDeclaration routine,
        RoutineInfo? preResolvedInfo = null, string? nameOverride = null)
    {
        RoutineInfo? routineInfo = preResolvedInfo;

        if (routineInfo == null)
        {
            // Look up the routine info from the registry
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
                    // No dot — try short name fallback (e.g., "show" -> finds "IO.show")
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

        if (routineInfo.Parameters.Any(predicate: p => ContainsGenericParameter(type: p.Type)) ||
            routineInfo.ReturnType != null && ContainsGenericParameter(type: routineInfo.ReturnType) ||
            routineInfo.OwnerType != null && ContainsGenericParameter(type: routineInfo.OwnerType))
        {
            return;
        }

        // Skip routines with error types in their signature
        if (HasErrorTypes(routine: routineInfo))
        {
            return;
        }

        string funcName = nameOverride ?? MangleRoutineName(routine: routineInfo);

        // Skip if already generated (prevents duplicates between user program and stdlib)
        if (!_generatedRoutineDefs.Add(item: funcName))
        {
            return;
        }

        // Also mark as generated in declarations set to prevent declare/define conflicts
        _generatedRoutines.Add(item: funcName);

        // Build parameter list with names
        var paramList = new List<string>();

        // For methods, add implicit 'me' parameter first
        // Skip 'me' for $create routines (static factories), common (type-level) routines, and void/Blank owner types
        bool isCreator = IsCreatorRoutine(routine: routineInfo);
        if (routineInfo.OwnerType != null && !isCreator && !routineInfo.IsCommon)
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
            // Stub routines (no AST body — e.g. BuilderService.page_size() declared without a
            // body) get their synthesized body from WiredRoutinePass via _synthesizedBodies.
            // Without this, codegen falls through GenerateRoutineBody on a null AST and emits
            // an empty function returning zero/null — every page_size() call returns 0,
            // every target_os() returns null ptr, and `show(f"target_os: {os}")` AVs in
            // CStr.$create(from: null).
            Statement effectiveBody = routine.Body;
            // Stub routines (declared without a body, like BuilderService.page_size()) get
            // their synthesized body from WiredRoutinePass via _synthesizedBodies. The parser
            // produces an empty BlockStatement for missing bodies, so check both null and empty.
            bool isStubBody = effectiveBody is null
                || (effectiveBody is BlockStatement bs && bs.Statements.Count == 0);
            if (isStubBody
                && _synthesizedBodies.TryGetValue(key: routineInfo.RegistryKey,
                    value: out Statement? synthStub))
            {
                effectiveBody = synthStub;
            }
            if (effectiveBody != null)
            {
                GenerateRoutineBody(sb: bodyBuilder, body: effectiveBody, routine: routineInfo);
            }
            _functionDefinitions.Append(value: _currentRoutineEntryAllocas);
            _functionDefinitions.Append(value: bodyBuilder);
        }
        catch
        {
            // Rollback partial IR so the output stays well-formed, then re-throw so the
            // caller can decide whether to skip or abort compilation.
            _functionDefinitions.Length = savedLength;
            _tempCounter = savedTempCounter;
            _generatedRoutineDefs.Remove(item: funcName);
            _generatedRoutines.Remove(item: funcName);
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
    private void GenerateRoutineBody(StringBuilder sb, Statement body, RoutineInfo routine) // NOSONAR S3776
    {
        // Clear local variables for this function
        _localVariables.Clear();
        _localVarLlvmNames.Clear();
        _varNameCounts.Clear();
        _localEntityVars.Clear();
        _localRcRecordVars.Clear();
        _localRetainedVars.Clear();
        _currentRoutineEntryAllocas.Clear();
        _emittedAllocaNames.Clear();

        // Set current function return type for use in EmitReturn
        _currentRoutineReturnType = routine.ReturnType;
        _currentRoutineIsFailable = routine.IsFailable;

        // Track current routine for source_routine() / source_module() injection
        _currentEmittingRoutine = routine;

        // Register implicit 'me' parameter for methods (skip for $create static factories and common routines)
        if (routine.OwnerType != null && !IsCreatorRoutine(routine: routine) && !routine.IsCommon)
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
        // Entity $create that references `me`: allocate the entity at routine entry, bind
        // `me` to the fresh pointer, and let the body mutate via `me.field = …` / `return me`.
        // Canonical `return Type(field: …)` $create routines that never touch `me` skip this.
        else if (routine.OwnerType is EntityTypeInfo creatorEntity &&
                 IsCreatorRoutine(routine: routine) &&
                 MeReferenceScanner.Scan(body: body))
        {
            string mePtr = EmitEntityAllocation(sb: sb, entity: creatorEntity);
            EmitEntryAlloca(llvmName: "%me.addr", llvmType: "ptr");
            EmitLine(sb: sb, line: $"  store ptr {mePtr}, ptr %me.addr");
            _localVariables[key: "me"] = routine.OwnerType;
        }

        // Register parameters as local variables
        foreach (ParameterInfo param in routine.Parameters)
        {
            // Parameters are passed as value, create a local copy
            // Use "entry_" instead of "entry" to avoid conflict with the entry: block label
            string emittedParamName = param.Name == "entry" ? "entry_" : param.Name;
            string paramPtr = $"%{param.Name}.addr";
            string llvmType = GetLlvmType(type: param.Type);
            EmitEntryAlloca(llvmName: paramPtr, llvmType: llvmType);
            EmitLine(sb: sb,
                line: $"  store {llvmType} %{emittedParamName}, ptr {paramPtr}");
            _localVariables[key: param.Name] = param.Type;
        }

        // Emit stack trace push.
        // Synthesized routines have no source location and no user-visible name — skip them.
        // In Release, also skip @inline routines (implementation helpers that add noise).
        bool isInline = routine.Annotations.Contains(value: "inline");
        _traceCurrentRoutine = ShouldEmitTrace &&
                               !routine.IsSynthesized &&
                               !(_buildMode is RfBuildMode.Release && isInline);
        if (_traceCurrentRoutine)
        {
            string paramTypes = string.Join(separator: ", ",
                values: routine.Parameters.Select(selector: p => p.Type.FullName));
            string failable = routine.IsFailable ? "!" : "";
            string routineName = $"{routine.BaseName}{failable}({paramTypes})";
            string fileName = routine.Location?.FileName ?? "";
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

        EmitRcRecordCleanup(sb: sb);
        EmitEntityCleanup(sb: sb, returnedVarName: null);
        if (_traceCurrentRoutine)
            EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");
        string retType = routine.ReturnType != null
            ? GetLlvmType(type: routine.ReturnType)
            : "void";
        if (retType == "void")
        {
            // For check_/try_ variant wrappers with Blank return type, emit the success
            // carrier instead of ret void — the define header uses the carrier type.
            switch (routine.AsyncStatus)
            {
                case AsyncStatus.CheckVariant:
                {
                    string carrier = GetResultCarrierLlvmType(valueType: routine.ReturnType!);
                    EmitLine(sb: sb, line: $"  ret {carrier} zeroinitializer");
                    break;
                }
                case AsyncStatus.TryBoolVariant:
                    EmitLine(sb: sb, line: "  ret i1 false");
                    break;
                default:
                    EmitLine(sb: sb, line: "  ret void");
                    break;
            }
        }
        else
        {
            string zeroValue = GetZeroValue(type: routine.ReturnType!);
            EmitLine(sb: sb, line: $"  ret {retType} {zeroValue}");
        }
    }

    /// <summary>
    /// Mangles a function name to be LLVM-compatible.
    /// </summary>
    internal static string MangleRoutineName(RoutineInfo routine)
    {
        // All routines with parameters are disambiguated by parameter type. Overloads
        // sharing only a name (e.g. LocalMoment.$sub(Duration) vs $sub(LocalMoment),
        // or $hash() vs $hash(k0, k1)) collapse to the same symbol otherwise and the
        // linker arbitrarily picks one definition, mis-typing every call site.
        static bool ShouldDisambiguateByParameterTypes(RoutineInfo candidate) =>
            candidate.Parameters.Count > 0;

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

            // Generic instance: append type arguments (e.g., IO.show -> IO.show#S64)
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

            if (ShouldDisambiguateByParameterTypes(candidate: routine))
            {
                string paramTypes = string.Join(separator: ",",
                    values: routine.Parameters.Select(selector: p => p.Type.FullName));
                fullName = $"{fullName}({paramTypes})";
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

        // Method-level type arguments (e.g., Hijacked[U64].recast_as[BTreeListNode[S64]]).
        // Distinct from owner type args already in OwnerType.FullName.
        if (routine.TypeArguments is { Count: > 0 } methodTypeArgs)
        {
            // Only include type args that aren't already in the owner's type arg list to
            // avoid duplicating owner generics (method.TypeArguments may be a superset).
            var ownerArgs = routine.OwnerType.TypeArguments ?? [];
            var methodOnlyArgs = methodTypeArgs
                .Where(predicate: a => !ownerArgs.Any(predicate: o => o.FullName == a.FullName))
                .ToList();
            if (methodOnlyArgs.Count > 0)
            {
                string typeArgSuffix = string.Join(separator: ",",
                    values: methodOnlyArgs.Select(selector: t => t.FullName));
                baseName = $"{baseName}[{typeArgSuffix}]";
            }
        }

        // Disambiguate synthesized error-handling variants and creators by all
        // parameter types so overloads like try_create(S8) / try_create(Text) and
        // try_find(Character) / try_find(Referring[Text]) do not collapse.
        if (ShouldDisambiguateByParameterTypes(candidate: routine))
        {
            string paramTypes = string.Join(separator: ",",
                values: routine.Parameters.Select(
                    selector: p => MangleParamTypeName(routine: routine, paramType: p.Type)));
            baseName = $"{baseName}({paramTypes})";
        }

        return Q(name: DecorateRoutineSymbolName(baseName: baseName,
            isFailable: routine.IsFailable));
    }

    /// <summary>
    /// Maps a wrapper-forwarder's disambiguated inner generic param (e.g. `__rfwd_T__`,
    /// or the original `T` colliding with the wrapper's own param) to the concrete inner
    /// type argument for symbol mangling. Without this, a forwarder body for
    /// `Retained[ListNode[S64]].chain_contains(T)` emits as `chain_contains(__rfwd_T__)`
    /// while the rewritten call site emits `chain_contains(Core.S64)` — linker miss.
    /// </summary>
    private static string MangleParamTypeName(RoutineInfo routine, TypeInfo paramType)
    {
        if (routine.WrapperForwarderInnerGenericDef?.GenericParameters
                is { Count: > 0 } innerParamNames
            && routine.OwnerType?.TypeArguments is { Count: > 0 } ownerArgs
            && ownerArgs.Count == 1
            && ownerArgs[index: 0].TypeArguments is { } innerArgs
            && innerArgs.Count == innerParamNames.Count)
        {
            return MangleParamTypeFullName(type: paramType,
                innerParamNames: innerParamNames, innerArgs: innerArgs);
        }
        return paramType.FullName;
    }

    private static string MangleParamTypeFullName(TypeInfo type,
        List<string> innerParamNames, List<TypeInfo> innerArgs)
    {
        if (type is GenericParameterTypeInfo gp)
        {
            string lookup = gp.ForwarderOriginalName ?? gp.Name;
            int idx = innerParamNames.IndexOf(item: lookup);
            if (idx < 0) idx = innerParamNames.IndexOf(item: gp.Name);
            return idx >= 0 ? innerArgs[index: idx].FullName : type.FullName;
        }
        if (type.TypeArguments is { Count: > 0 } args)
        {
            string baseName = string.IsNullOrEmpty(value: type.Module)
                ? type.Name
                : $"{type.Module}.{type.Name}";
            if (type.Name.Contains(value: '['))
            {
                // Name already embeds the (unsubstituted) args. Substitute literally inside it
                // is brittle; fall back to per-arg recursion for the bracket portion.
                int br = type.Name.IndexOf(value: '[');
                baseName = string.IsNullOrEmpty(value: type.Module)
                    ? type.Name.Substring(startIndex: 0, length: br)
                    : $"{type.Module}.{type.Name.Substring(startIndex: 0, length: br)}";
            }
            string joined = string.Join(separator: ", ",
                values: args.Select(selector: a => MangleParamTypeFullName(
                    type: a, innerParamNames: innerParamNames, innerArgs: innerArgs)));
            return $"{baseName}[{joined}]";
        }
        return type.FullName;
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

    private static bool IsCreatorRoutine(RoutineInfo routine)
    {
        return routine.Name.Contains(value: "$create") ||
               routine.Name is "try_create" or "check_create" or "lookup_create" ||
               routine.OriginalName?.Contains(value: "$create") == true;
    }

    private string GetImplicitMeParameterDeclaration(RoutineInfo routine, bool includeName)
    {
        if (routine.OwnerType == null)
        {
            throw new InvalidOperationException(message: "Implicit 'me' requested for routine without owner type.");
        }

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
        // Exclusive me-params get `noalias`. Three cases qualify:
        //   - bare entity (post-Owned-retirement: bound T can't be duplicated, so
        //     the me pointer is exclusive at the call boundary by the
        //     entity-ownership rule),
        //   - legacy `Owned[T]` wrapper (transitional, while Owned still exists),
        //   - `Grasped[T]` (scope-bound exclusive borrow — its definition).
        bool isExclusive = routine.OwnerType is EntityTypeInfo
                           || routine.OwnerType is WrapperTypeInfo { Name: "Owned" or "Grasped" };
        if (isExclusive)
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
            WrapperTypeInfo => "readonly",
            _ => string.Empty
        };
    }

    private static string GetExplicitParameterAttributes(TypeInfo? type) =>
        type is EntityTypeInfo
        || type is WrapperTypeInfo { Name: "Owned" or "Grasped" }
            ? "noalias"
            : string.Empty;

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

        string llvmType = GetLlvmType(type: routine.ReturnType);
        if (!llvmType.StartsWith(value: "%Record.") && !llvmType.StartsWith(value: "%\"Record.") &&
            !llvmType.StartsWith(value: "%Tuple.") && !llvmType.StartsWith(value: "%\"Tuple."))
        {
            return false;
        }

        int size = GetTypeSize(type: routine.ReturnType);
        return size > 8;
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
    private static string GetZeroValue(TypeInfo type)
    {
        return type switch
        {
            RecordTypeInfo { HasDirectBackendType: true } record => GetZeroValueForLlvmType(
                llvmType: record.BackendType!),
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
            // Aggregate types ([N x T] arrays, {...} structs, %"Named" struct types) require
            // `zeroinitializer`, not the integer literal `0`. Without this, BitArray[N]() and
            // similar no-arg constructors emit `store [1 x i8] 0, ...` which opt rejects with
            // "integer constant must have integer type".
            _ when llvmType.Length > 0 &&
                   (llvmType[0] == '[' || llvmType[0] == '{' || llvmType[0] == '%') => "zeroinitializer",
            _ => "0"
        };
    }

}
