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
        // Skip 'me' for $create routines (static factories)
        bool isCreator = IsCreatorRoutine(routine: routine);
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
        // Skip 'me' for $create routines (static factories) and void/Blank owner types
        bool isCreator = IsCreatorRoutine(routine: routineInfo);
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
            GenerateRoutineBody(sb: bodyBuilder, body: routine.Body, routine: routineInfo);
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
    private void GenerateRoutineBody(StringBuilder sb, Statement body, RoutineInfo routine)
    {
        // Clear local variables for this function
        _localVariables.Clear();
        _localVarLlvmNames.Clear();
        _varNameCounts.Clear();
        _localEntityVars.Clear();
        _localRcRecordVars.Clear();
        _localRetainedVars.Clear();
        _currentBlock = "entry";
        _currentRoutineEntryAllocas.Clear();
        _emittedAllocaNames.Clear();

        // Set current function return type for use in EmitReturn
        _currentRoutineReturnType = routine.ReturnType;
        _currentRoutineIsFailable = routine.IsFailable;

        // Track current routine for source_routine() / source_module() injection
        _currentEmittingRoutine = routine;

        // Register implicit 'me' parameter for methods (skip for $create static factories)
        if (routine.OwnerType != null && !IsCreatorRoutine(routine: routine))
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
        static bool ShouldDisambiguateByParameterTypes(RoutineInfo candidate)
        {
            if (candidate.Parameters.Count == 0)
            {
                return false;
            }

            return candidate.Name == "$create" || candidate.OriginalName != null;
        }

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
                values: routine.Parameters.Select(selector: p => p.Type.FullName));
            baseName = $"{baseName}({paramTypes})";
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

}
