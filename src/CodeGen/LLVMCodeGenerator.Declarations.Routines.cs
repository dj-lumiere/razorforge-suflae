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
        // Record the reference: if we are emitting a routine body, this routine is a real callee and
        // must itself be emitted. Gated on _emittingRoutineBody so the broad declaration pre-pass
        // (which declares much of the registry up front) does not mark everything referenced.
        if (_emittingRoutineBody)
            _referencedKeys.Add(item: routine.RegistryKey);

        string funcName = nameOverride ?? MangleRoutineName(routine: routine);

        // Skip if already generated
        if (_generatedRoutines.Contains(item: funcName))
        {
            return;
        }

        // Skip @innate routines — compile-time stubs, never have bodies, must not reach codegen
        if (routine.Annotations.Contains(value: "innate"))
            return;

        // Skip declarations on generic-definition owner types (e.g. T, List[T]) —
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
            // By-ref struct-record thread arg: the worker receives a pointer to the spawner's cell.
            if (IsByRefThreadArg(routine: routine, param: param)) return "ptr";
            // ABI-Indirect struct value arg: passed as a hidden byval pointer-to-copy.
            if (ParameterPassedByval(routine: routine, paramType: param.Type))
                return $"ptr byval({GetLlvmType(type: param.Type)})";
            // ABI-Coerce small struct value arg: passed reinterpreted as an integer register form.
            if (ParameterCoerceType(routine: routine, paramType: param.Type) is { } coerceArg)
                return coerceArg;
            string t = GetParameterLlvmType(type: param.Type);
            if (isCExtern && t == "half") return "i16";
            string attrs = GetExplicitParameterAttributes(type: param.Type);
            return string.IsNullOrEmpty(attrs) ? t : $"{t} {attrs}";
        }));

        // Ensure record type definitions exist for parameter and return types
        foreach (ParameterInfo param in routine.Parameters)
        {
            if (param.Type is RecordTypeInfo { HasDirectBackendType: false, IsGenericDefinition: false } paramRecord)
            {
                GenerateRecordType(record: paramRecord);
            }
        }

        if (routine.ReturnType is RecordTypeInfo { HasDirectBackendType: false, IsGenericDefinition: false } returnRecord)
        {
            GenerateRecordType(record: returnRecord);
        }

        // Get return type
        string returnType = routine.ReturnType != null
            ? GetLlvmType(type: routine.ReturnType)
            : "void";
        if (routine.FailableVariant == FailableVariant.Lookup)
        {
            // Lookup[Blank] degenerates to Result[Blank]: a Blank value payload makes the
            // "found vs not-found" distinction meaningless, so use the Result carrier instead.
            returnType = routine.ReturnType?.IsBlank == true
                ? GetResultCarrierLlvmType(valueType: routine.ReturnType!)
                : GetLookupCarrierLlvmType(valueType: routine.ReturnType!);
        }
        else if (routine.FailableVariant == FailableVariant.Check)
        {
            returnType = GetResultCarrierLlvmType(valueType: routine.ReturnType!);
        }
        else if (routine.FailableVariant == FailableVariant.TryBool)
        {
            returnType = "i1";
        }

        if (isCExtern && returnType == "half")
        {
            returnType = "i16";
        }

        // Struct returns classified Indirect by the target ABI go through a hidden sret pointer.
        // For external("C") this matches the platform C ABI (Win-x64 MSVC: structs > 8 bytes);
        // for RF routines it is the ABI boundary-coercion return form. The declaration, definition,
        // every return, and every call site must agree — see ReturnsViaSret / _currentReturnViaSret.
        bool needsSret = isCExtern
            ? NeedsCExternSret(routine: routine)
            : ReturnsViaSret(routine: routine);
        // Phase 2: a small struct return is coerced to an integer register form — the declared
        // return type becomes that, matching the define/return/call sites. (Not for C externs.)
        string? declCoerceReturn = isCExtern || needsSret ? null : ReturnCoerceType(routine: routine);
        if (needsSret)
        {
            // Change declaration: void @func(ptr sret(%RecordType), original_params...)
            paramTypes.Insert(index: 0, item: $"ptr sret({returnType})");
            string parameters = string.Join(separator: ", ", values: paramTypes);
            _rfRoutineDeclarations[key: funcName] = $"declare void @{funcName}({parameters})";
        }
        else if (declCoerceReturn != null)
        {
            string parameters = string.Join(separator: ", ", values: paramTypes);
            _rfRoutineDeclarations[key: funcName] =
                $"declare {declCoerceReturn} @{funcName}({parameters})";
        }
        else
        {
            string parameters = string.Join(separator: ", ", values: paramTypes);
            string returnPrefix = isCreator && returnType == "ptr" ? "noalias " : "";
            _rfRoutineDeclarations[key: funcName] =
                $"declare {returnPrefix}{returnType} @{funcName}({parameters})";
        }

        // Over-prune tripwire: this declare was emitted because an emitted body references the
        // routine, and it is a real RF routine (not a bodyless C extern), so it MUST also be
        // defined. Record it so the post-fixpoint check in GenerateRoutineDefinitions can catch a
        // reachability over-prune (a called routine whose body got dropped) as a located codegen
        // error rather than a downstream linker "undefined symbol". Gated on _emittingRoutineBody
        // so the broad declaration pre-pass — which declares much of the registry but references
        // nothing — does not enroll dead routines.
        if (_emittingRoutineBody && !isCExtern)
        {
            _expectedBodyNames.Add(item: funcName);
        }
    }

    /// <summary>
    /// Returns the LLVM named-struct type for a lifted lambda's closure environment —
    /// <c>%"Closure.&lt;liftedName&gt;" = type { ptr, &lt;capture types&gt; }</c> — declaring it on
    /// first use. Field 0 is the function pointer; the captured values follow in
    /// <see cref="RoutineInfo.ClosureCaptures"/> order.
    /// </summary>
    /// <param name="lambda">The lifted lambda's RoutineInfo; its Name and ClosureCaptures determine the struct name and fields.</param>
    private string ClosureStructName(RoutineInfo lambda)
    {
        string name = $"%\"Closure.{lambda.Name}\"";
        if (_typeDeclarationsClosure.ContainsKey(key: name))
        {
            return name;
        }

        var fields = new List<string> { "ptr" };
        if (lambda.ClosureCaptures != null)
        {
            foreach ((string _, TypeInfo capType) in lambda.ClosureCaptures)
                fields.Add(item: GetLlvmType(type: capType));
        }
        _typeDeclarationsClosure[key: name] =
            $"{name} = type {{ {string.Join(separator: ", ", values: fields)} }}\n";
        return name;
    }

    private void GenerateRoutineDefinition(RoutineDeclaration routine,
        RoutineInfo? preResolvedInfo = null, string? nameOverride = null,
        string? moduleContext = null)
    {
        // The binding attached at registration is authoritative — it is the exact RoutineInfo this
        // declaration was registered as, so it needs no name re-parsing or module-blind owner lookup.
        RoutineInfo? routineInfo = preResolvedInfo ?? routine.ResolvedInfo;

        if (routineInfo == null)
        {
            // Look up the routine info from the registry
            // For module-qualified names like "Console.show", the registry key may be
            // "IO.show" (module.name). Try full AST name first, then short name lookup.
            string baseName = routine.Name;

            // MEMBER decl in a known module: resolve OWNER-SCOPED to this module FIRST. A bare
            // `LookupRoutine("Box.destroy")` returns a first-wins entry, so when two modules each
            // declare `record Box` with a `$destroy`/0-param method, this module's body would be
            // emitted under the OTHER module's symbol — leaving this module's symbol undefined (the
            // module-scoped-type over-prune). The overload block below only rescues >0-param methods;
            // 0-param ones (`$destroy()`, `bump()`) must be pinned here, before the bare lookup.
            int memberDot = baseName.IndexOf(value: '.');
            if (!string.IsNullOrEmpty(value: moduleContext) && memberDot > 0)
            {
                string ownerSeg = baseName[..memberDot];
                int obrk = ownerSeg.IndexOf(value: '[');
                if (obrk > 0) ownerSeg = ownerSeg[..obrk];
                TypeInfo? scopedOwner = _registry.LookupType(name: $"{moduleContext}.{ownerSeg}");
                if (scopedOwner != null)
                {
                    routineInfo = _registry.LookupMethod(type: scopedOwner,
                        methodName: baseName[(memberDot + 1)..]);
                }
            }

            routineInfo ??= _registry.LookupRoutine(fullName: baseName);
            // Module-level routine (no dot): prefer the module-qualified key so that two modules'
            // same-named routines (e.g. each of several imported test modules with a `start`) each
            // bind to their OWN RoutineInfo. Without this, the bare LookupRoutineByName fallback
            // below returns a first-wins entry and this module's body is emitted under another
            // module's symbol.
            if (routineInfo == null && !string.IsNullOrEmpty(value: moduleContext) &&
                !baseName.Contains(value: '.'))
            {
                routineInfo = _registry.LookupRoutine(fullName: $"{moduleContext}.{baseName}");
            }
            if (routineInfo == null)
            {
                int dotIdx = baseName.IndexOf(value: '.');
                if (dotIdx > 0)
                {
                    // Member declaration (e.g. "UnpackedFloat[M, L, W].cbrt"). Resolve scoped
                    // to the owner type FIRST: the owner-qualified LookupRoutine above can miss
                    // when the AST name carries generic params (BaseName drops them), and a bare
                    // short-name lookup could otherwise bind this method's body to a same-named
                    // free/external routine of a DIFFERENT owner.
                    string ownerPart = baseName[..dotIdx];
                    int bracketIdx = ownerPart.IndexOf(value: '[');
                    if (bracketIdx > 0) ownerPart = ownerPart[..bracketIdx];
                    string shortName = baseName[(dotIdx + 1)..];
                    // MODULE-SCOPED owner resolution: prefer the emitting program's own module so two
                    // modules that each declare `record Box` bind this decl to the CORRECT `Mod.Box`.
                    // A bare `LookupType("Box")` returns a first-wins entry, emitting this module's body
                    // under the other module's symbol (leaving THIS module's symbol undefined — the
                    // module-scoped-type over-prune the harness caught).
                    TypeInfo? ownerType = (!string.IsNullOrEmpty(value: moduleContext)
                        ? _registry.LookupType(name: $"{moduleContext}.{ownerPart}")
                        : null) ?? _registry.LookupType(name: ownerPart);
                    if (ownerType != null)
                        routineInfo = _registry.LookupMethod(type: ownerType, methodName: shortName);
                    routineInfo ??= _registry.LookupRoutine(fullName: shortName) ??
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
                    RoutineInfo? overload = null;

                    // Member declaration (Owner.method): disambiguate OWNER-SCOPED. The base-name
                    // path below fails here — routineInfo.BaseName for a member routine is the bare
                    // "F64.create" (no module prefix), but overloads register under
                    // "Core.F64.create#…", and LookupRoutineOverload's Core-prefix fallback is
                    // disabled whenever the base name contains a '.' (which member names always do).
                    // So it would silently fall back to the first-registered overload, ignoring the
                    // arg types we just computed. LookupMethodOverload collects the owner type's
                    // member candidates and matches positionally by arg type instead.
                    int dotIdx = routine.Name.IndexOf(value: '.');
                    if (dotIdx > 0)
                    {
                        string ownerPart = routine.Name[..dotIdx];
                        int bracketIdx = ownerPart.IndexOf(value: '[');
                        if (bracketIdx > 0) ownerPart = ownerPart[..bracketIdx];
                        string shortName = routine.Name[(dotIdx + 1)..];
                        // Module-scoped owner (see the resolution above) so the overload is matched on
                        // THIS module's same-named type, not a first-wins cross-module one.
                        TypeInfo? ownerType = (!string.IsNullOrEmpty(value: moduleContext)
                            ? _registry.LookupType(name: $"{moduleContext}.{ownerPart}")
                            : null) ?? _registry.LookupType(name: ownerPart);
                        if (ownerType != null)
                            overload = _registry.LookupMethodOverload(type: ownerType,
                                methodName: shortName, argTypes: astParamTypes);
                    }

                    overload ??=
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

        // Reachability gate: when LiveRoutineKeys is populated, skip routines not reachable
        // from program entry points. Otherwise codegen emits every module-level routine
        // (including unreached helpers) and any callee they reference must also be defined,
        // producing spurious linker errors for stdlib methods the program never actually uses.
        // Lifted lambdas are exempted: LambdaLiftingPass runs in Phase 7, after reachability
        // in Phase 6, so their keys aren't in the live set even when referenced by address
        // from a reachable enclosing routine.
        if (_liveRoutineKeys.Count > 0
            && !_liveRoutineKeys.Contains(item: routineInfo.RegistryKey)
            && !routineInfo.IsLambda)
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

        // Closure ABI: every lifted lambda receives its closure object as a hidden leading
        // `ptr %__cl` parameter (uniform whether or not it captures), so indirect calls through a
        // Routine value can always pass the closure. The body's prologue loads captures from it.
        if (routineInfo.IsLambda)
        {
            paramList.Add(item: "ptr %__cl");
        }

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
            let byRefThreadArg = IsByRefThreadArg(routine: routineInfo, param: param)
            // ABI-Indirect struct value arg: arrives as `ptr byval(%T)` — a pointer to the callee's
            // private copy. Like a by-ref thread arg, `%<name>.addr` doubles as the struct's lvalue
            // address so the prologue skips the alloca/store copy (the byval copy already exists).
            let byval = !byRefThreadArg && ParameterPassedByval(routine: routineInfo, paramType: param.Type)
            // ABI-Coerce small struct value arg: arrives as an integer register value (`%<name>`);
            // the prologue reconstructs the struct into `%<name>.addr`. Plain value name (not .addr).
            let coerce = !byRefThreadArg && !byval
                ? ParameterCoerceType(routine: routineInfo, paramType: param.Type)
                : null
            let paramType = byRefThreadArg ? "ptr"
                : byval ? $"ptr byval({GetLlvmType(type: param.Type)})"
                : coerce ?? GetParameterLlvmType(type: param.Type)
            let paramAttrs = byRefThreadArg || byval || coerce != null
                ? string.Empty
                : GetExplicitParameterAttributes(type: param.Type)
            let emittedName = byRefThreadArg || byval ? $"{param.Name}.addr"
                : param.Name == "entry" ? "entry_" : param.Name
            select string.IsNullOrEmpty(paramAttrs)
                ? $"{paramType} %{emittedName}"
                : $"{paramType} {paramAttrs} %{emittedName}");

        // Get return type
        string returnType = routineInfo.ReturnType != null
            ? GetLlvmType(type: routineInfo.ReturnType)
            : "void";
        if (routineInfo.FailableVariant == FailableVariant.Lookup)
        {
            returnType = GetLookupCarrierLlvmType(valueType: routineInfo.ReturnType!);
        }
        else if (routineInfo.FailableVariant == FailableVariant.Check)
        {
            returnType = GetResultCarrierLlvmType(valueType: routineInfo.ReturnType!);
        }
        else if (routineInfo.FailableVariant == FailableVariant.TryBool)
        {
            returnType = "i1";
        }

        // Struct returns classified Indirect by the ABI are returned through a hidden sret pointer:
        // prepend `ptr sret(%T) %sret` and make the header return void; every `return` in the body
        // then stores through %sret (see EmitReturn). _currentReturnViaSret is read during body
        // emission, so set it before GenerateRoutineBody and restore after.
        bool prevReturnViaSret = _currentReturnViaSret;
        string? prevReturnCoerce = _currentReturnCoerceType;
        _currentReturnViaSret = ReturnsViaSret(routine: routineInfo);
        // Phase 2: a small struct return is coerced to an integer register form; the header returns
        // that type and every `return` reinterprets the struct into it (see EmitReturn).
        _currentReturnCoerceType = _currentReturnViaSret ? null : ReturnCoerceType(routine: routineInfo);
        if (_currentReturnViaSret)
        {
            paramList.Insert(index: 0, item: $"ptr sret({returnType}) %sret");
        }

        // Start function — save position so we can rollback on error
        string parameters = string.Join(separator: ", ", values: paramList);
        int savedLength = _functionDefinitions.Length;
        int savedTempCounter = _tempCounter;

        bool isInline = routineInfo.Annotations.Contains(value: "inline");
        string headerReturnType = _currentReturnViaSret ? "void"
            : _currentReturnCoerceType ?? returnType;
        string returnPrefix =
            !_currentReturnViaSret && isCreator && returnType == "ptr" ? "noalias " : "";
        string funcAttrs = isInline ? " alwaysinline" : "";
        // `@no_optimize` emits `noinline optnone` — a per-routine optimization barrier for routines
        // that an LLVM backend pass miscompiles. Currently the softfloat gamma cores (F128.lgamma/
        // tgamma_unchecked + UnpackedFloat.lgamma_core + f512_lgamma_core): LLVM 21's InstCombine
        // miscompiles them at -O2+ (found via opt -opt-bisect-limit; -O0 is correct). The routine
        // runs unoptimized but correct; see the memory note on the pending proper IR-dodge fix.
        if (routineInfo.Annotations.Contains(value: "no_optimize"))
            funcAttrs += " noinline optnone";
        string defineHeader =
            $"define {returnPrefix}{headerReturnType} @{funcName}({parameters}){funcAttrs} {{";
        _generatedRoutineDefHeaders[key: funcName] = defineHeader;
        RecordDebugSubprogram(funcName: funcName, location: routineInfo.Location);
        _currentDbgLoc = null; // reset the Layer-2 location cursor at each routine boundary
        EmitLine(sb: _functionDefinitions, line: defineHeader);
        EmitLine(sb: _functionDefinitions, line: "entry:");
        var bodyBuilder = new StringBuilder();

        try
        {
            // Stub routines (no AST body — e.g. BuilderService.page_size() declared without a
            // body) get their synthesized body from WiredRoutinePass via _synthesizedBodies.
            // Without this, codegen falls through GenerateRoutineBody on a null AST and emits
            // an empty function returning zero/null — every page_size() call returns 0,
            // every target_os() returns null ptr, and `show(f"target_os: {os}")` AVs in
            // CStr.create(from: null).
            Statement effectiveBody = routine.Body;
            // Stub routines (declared without a body, like BuilderService.page_size()) get
            // their synthesized body from WiredRoutinePass via _synthesizedBodies. The parser
            // produces an empty BlockStatement for missing bodies, so check both null and empty.
            bool isStubBody = effectiveBody is null
                || effectiveBody is BlockStatement { Statements.Count: 0 };
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
            _generatedRoutineDefHeaders.Remove(key: funcName);
            _generatedRoutines.Remove(item: funcName);
            throw;
        }

        // End function
        EmitLine(sb: _functionDefinitions, line: "}");
        EmitLine(sb: _functionDefinitions, line: "");
        _currentReturnViaSret = prevReturnViaSret;
        _currentReturnCoerceType = prevReturnCoerce;
    }

    /// <summary>
    /// Generates code for a function body.
    /// Emits statements and ensures proper termination.
    /// </summary>
    private void GenerateRoutineBody(StringBuilder sb, Statement body, RoutineInfo routine) // NOSONAR S3776
    {
        // Mark that we are emitting a body: every routine declared (referenced) from here on is a
        // real callee of an emitted routine, so it must itself be emitted. Save/restore in case a
        // body's emission ever re-enters this method.
        bool prevEmittingBody = _emittingRoutineBody;
        _emittingRoutineBody = true;
        try
        {
        // Clear local variables for this function
        _localVariables.Clear();
        _localVarLlvmNames.Clear();
        _varNameCounts.Clear();
        _localEntityVars.Clear();
        _cfNodes.Clear();
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
            if (IsByRefMeReceiver(routine: routine))
            {
                // Struct-record `me` is passed by reference: %me.addr IS the function parameter
                // (the caller's storage pointer). No alloca/store — mutations and address-taking
                // (hijack/get_address) reach the caller's variable directly.
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
            // By-ref struct-record thread arg: `%<name>.addr` IS the parameter (a pointer to the
            // spawner's cell), exactly like a by-ref `me`. No alloca/store — field/method access
            // resolves through the address and reaches the shared cell directly.
            if (IsByRefThreadArg(routine: routine, param: param))
            {
                _localVariables[key: param.Name] = param.Type;
                continue;
            }

            // ABI-Indirect (byval) struct value param: `%<name>.addr` IS the pointer to the callee's
            // private copy the backend materialized — no alloca/store, field access reads/writes
            // through it directly (mirrors the by-ref thread-arg path, but value-semantic).
            if (ParameterPassedByval(routine: routine, paramType: param.Type))
            {
                _localVariables[key: param.Name] = param.Type;
                continue;
            }

            // Use "entry_" instead of "entry" to avoid conflict with the entry: block label
            string emittedParamName = param.Name == "entry" ? "entry_" : param.Name;
            string paramPtr = $"%{param.Name}.addr";

            // ABI-Coerce small struct value param: the parameter arrives as an integer register
            // value. Allocate a slot of the ABI type (≥ the struct), store the register value into
            // it, and bind `%<name>.addr` to it — field access then reads the struct out (the slot is
            // large enough). No managed-entity teardown applies (records only).
            string? coerceType = ParameterCoerceType(routine: routine, paramType: param.Type);
            if (coerceType != null)
            {
                EmitEntryAlloca(llvmName: paramPtr, llvmType: coerceType);
                EmitLine(sb: sb,
                    line: $"  store {coerceType} %{emittedParamName}, ptr {paramPtr}");
                _localVariables[key: param.Name] = param.Type;
                continue;
            }

            // Parameters are passed as value, create a local copy
            string llvmType = GetLlvmType(type: param.Type);
            EmitEntryAlloca(llvmName: paramPtr, llvmType: llvmType);
            EmitLine(sb: sb,
                line: $"  store {llvmType} %{emittedParamName}, ptr {paramPtr}");
            _localVariables[key: param.Name] = param.Type;

            // A bound-entity parameter is a consuming parameter: ownership was transferred in
            // via `steal` at the call site (SA requires the verb — see the BareEntityAssignment
            // check in AnalyzeCallArguments), so this routine is the new sole owner and must tear
            // it down at scope exit, exactly like a local entity `var`. Borrows arrive as
            // Referring/Controlling/Viewing/Modifying wrappers (RecordTypeInfo), never as bare
            // EntityTypeInfo, so they are correctly excluded. If the body re-transfers ownership
            // (`steal r` into a call/field), ConsumeTransferredLocalOwnership removes it from this
            // set by name, preventing a double-free.
            if (param.Type is EntityTypeInfo)
            {
                _localEntityVars.Add(item: (param.Name, paramPtr));
            }
        }

        // Closure prologue: load each captured value out of the closure object (the hidden `%__cl`
        // parameter) into a local so the body references it as an ordinary variable. The closure
        // layout is `{ ptr fn, capture0, capture1, ... }`; captures start at field index 1.
        if (routine.IsLambda && routine.ClosureCaptures is { Count: > 0 } closureCaptures)
        {
            string clStruct = ClosureStructName(lambda: routine);
            for (int i = 0; i < closureCaptures.Count; i++)
            {
                (string capName, TypeInfo capType) = closureCaptures[index: i];
                string capLlvm = GetLlvmType(type: capType);
                string capPtr = NextTemp();
                EmitLine(sb: sb,
                    line: $"  {capPtr} = getelementptr {clStruct}, ptr %__cl, i32 0, i32 {i + 1}");
                string capVal = NextTemp();
                EmitLine(sb: sb, line: $"  {capVal} = load {capLlvm}, ptr {capPtr}");
                string capAddr = $"%{capName}.addr";
                EmitEntryAlloca(llvmName: capAddr, llvmType: capLlvm);
                EmitLine(sb: sb, line: $"  store {capLlvm} {capVal}, ptr {capAddr}");
                _localVariables[key: capName] = capType;
            }
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
            switch (routine.FailableVariant)
            {
                case FailableVariant.Check:
                {
                    string carrier = GetResultCarrierLlvmType(valueType: routine.ReturnType!);
                    EmitLine(sb: sb, line: $"  ret {carrier} zeroinitializer");
                    break;
                }
                case FailableVariant.TryBool:
                    EmitLine(sb: sb, line: "  ret i1 false");
                    break;
                default:
                    EmitLine(sb: sb, line: "  ret void");
                    break;
            }
        }
        else if (_currentReturnViaSret)
        {
            // Indirect (sret) return: the header is void, so store the zero struct through the
            // hidden %sret pointer and return void (matches the by-value path's zero fallthrough).
            string zeroValue = GetZeroValue(type: routine.ReturnType!);
            EmitLine(sb: sb, line: $"  store {retType} {zeroValue}, ptr %sret");
            EmitLine(sb: sb, line: "  ret void");
        }
        else if (_currentReturnCoerceType != null)
        {
            // Coerced (Phase 2) return: the header returns the ABI integer type; zero fills it.
            EmitLine(sb: sb, line: $"  ret {_currentReturnCoerceType} zeroinitializer");
        }
        else
        {
            string zeroValue = GetZeroValue(type: routine.ReturnType!);
            EmitLine(sb: sb, line: $"  ret {retType} {zeroValue}");
        }
        }
        finally
        {
            _emittingRoutineBody = prevEmittingBody;
        }
    }

    /// <summary>
    /// Mangles a function name to be LLVM-compatible.
    /// </summary>
    internal static string MangleRoutineName(RoutineInfo routine)
    {
        // All routines with parameters are disambiguated by parameter type. Overloads
        // sharing only a name (e.g. LocalMoment.sub(Duration) vs $sub(LocalMoment),
        // or $hash() vs $hash(k0, k1)) collapse to the same symbol otherwise and the
        // linker arbitrarily picks one definition, mis-typing every call site.
        static bool ShouldDisambiguateByParameterTypes(RoutineInfo candidate) =>
            candidate.Parameters.Count > 0;

        // Failability is a routine PROPERTY (IsFailable), never part of the symbol name. The `!`
        // is stripped from every mangled symbol — `foo()` and `foo!()` with the same params are a
        // duplication error (RegistryKey excludes failability), so `owner.name(params)` is already
        // a unique symbol and the bang would only be decorative. Kept as a no-op wrapper so the
        // owner-case call sites below read uniformly.
        static string Bang(string name, bool failable) => name;

        // Structured attribute prefix — the routine's PROPERTIES (kind, wired-ness, failability,
        // async mode, storage) are obfuscated into a bracketed list so the name itself carries only
        // the module-qualified raw identifier. E.g. `[member, wired] Core.Address.create(...)`,
        // `[independent, crashable] Foo.parse(...)`. External("C") routines are EXEMPT (they keep the
        // raw C symbol so the LLVM declare links against the native lib), so this is not called there.
        static string AttrPrefix(RoutineInfo r)
        {
            var attrs = new List<string> { r.OwnerType != null ? "member" : "independent" };
            if (r.IsCommon) attrs.Add(item: "common");
            if (r.IsWiredMemberRoutine) attrs.Add(item: "wired");
            if (r.IsFailable) attrs.Add(item: "crashable");
            if (r.IsDangerous) attrs.Add(item: "dangerous");
            // Visibility is an attribute too. A member of a `secret` (module-private) type is itself
            // module-private regardless of its own modifier (owner-secrecy cap), so decorate `secret`
            // when EITHER the routine or its owner type is secret. `open` is the default → not emitted.
            // (`posted` is member-variable-only — routines are only secret/open/external.)
            bool ownerSecret = r.OwnerType is { Visibility: VisibilityModifier.Secret };
            if (r.Visibility == VisibilityModifier.Secret || ownerSecret) attrs.Add(item: "secret");
            if (r.IsSuspended) attrs.Add(item: "suspended");
            else if (r.IsThreaded) attrs.Add(item: "threaded");
            attrs.Sort();
            return $"[{string.Join(separator: ", ", values: attrs)}] ";
        }

        // Labeled parameter list — `(label: Core.Type, …)` — the label participates in overload
        // identity (RazorForge dispatches on named args), so it belongs in the mangled symbol.
        static string LabeledParams(RoutineInfo r) =>
            "(" + string.Join(separator: ", ",
                values: r.Parameters.Select(selector: p => $"{p.Name}: {p.Type.FullName}")) + ")";

        // Lambda closures: [lambda]filename:line:col!(paramTypes)
        if (routine.IsLambda)
        {
            string fileName =
                Path.GetFileName(path: routine.Location?.FileName ?? "[unknown]");
            int line = routine.Location?.Line ?? 0;
            int col = routine.Location?.Column ?? 0;
            string paramTypes = string.Join(separator: ",",
                values: routine.Parameters.Select(selector: p => p.Type.Name));
            string lambdaName = Bang(name: $"[lambda]{fileName}:{line}:{col}", failable: routine.IsFailable);
            return Q(name: $"{lambdaName}({paramTypes})");
        }

        // External("C") functions use the raw C symbol name — no module prefix,
        // so that LLVM IR symbols match the actual C linker symbols.
        if (routine.CallingConvention == "C")
        {
            return Q(name: Bang(name: SanitizeLlvmName(name: routine.Name),
                failable: routine.IsFailable));
        }

        string name = SanitizeLlvmName(name: routine.Name);
        if (routine.OwnerType == null)
        {
            // Top-level: `[independent, …] Module.name(typeargs)(label: Type, …)`.
            // BaseName preserves the module-qualified form; attributes + labeled params carry the
            // former `!`/`$`/decoration.
            string fullName = AttrPrefix(r: routine) + SanitizeLlvmName(name: routine.BaseName);

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

            fullName += LabeledParams(r: routine);
            return Q(name: fullName);
        }

        // Common (type-level static) routines: `[member, common, …] Module.Type.name(label: Type, …)`.
        if (routine.IsCommon)
        {
            string typeName = routine.OwnerType.FullName;
            return Q(name: $"{AttrPrefix(r: routine)}{typeName}.{name}{LabeledParams(r: routine)}");
        }

        // Method: `[member, wired?, crashable?, …] Module.OwnerType.name(label: Type, …)`
        // (OwnerType.FullName includes module). The `$`/`!` are gone from the name — they are in the
        // attribute prefix.
        string ownerTypeName = routine.OwnerType.FullName;
        string baseName = AttrPrefix(r: routine) + $"{ownerTypeName}.{name}";

        // Method-level type arguments (e.g., Hijacked[U64].recast_as[BTreeListNode[S64]]).
        // Distinct from owner type args already in OwnerType.FullName.
        if (routine.TypeArguments is { Count: > 0 } methodTypeArgs)
        {
            // Only include type args that aren't already in the owner's type arg list to
            // avoid duplicating owner generics (method.TypeArguments may be a superset).
            // Also drop bare GenericParameterTypeInfo entries: when SignatureResolver leaves
            // an owner-level param (e.g. T) in routine.TypeArguments and the owner is already
            // monomorphized to a concrete type, the bare T would survive the name-based
            // dedup and mangle as `Type[Concrete].method[T]` — producing an undefined symbol.
            //
            // Additionally drop entries whose Name matches one of the owner gen-def's
            // GenericParameters (e.g. "T" for SortedSet[T]). When TransferSubstitutedTypeArguments
            // or SubstituteMethodForOwner forwards a stale leftover TypeInfo named "T" that is
            // *not* a GenericParameterTypeInfo (some passes wrap the owner-leak in a non-GPTI),
            // the GPTI check above doesn't catch it. Matching by name closes that hole.
            var ownerArgs = routine.OwnerType.TypeArguments ?? [];
            TypeInfo? ownerGenDef = routine.OwnerType switch
            {
                RecordTypeInfo r => r.GenericDefinition ?? r,
                EntityTypeInfo e => e.GenericDefinition ?? e,
                ProtocolTypeInfo p => p.GenericDefinition ?? p,
                _ => routine.OwnerType
            };
            List<string> ownerGenDefParamNames = ownerGenDef?.GenericParameters ?? [];
            var methodOnlyArgs = methodTypeArgs
                .Where(predicate: a => a is not GenericParameterTypeInfo
                    && !ownerArgs.Any(predicate: o => o.FullName == a.FullName)
                    && !ownerGenDefParamNames.Contains(item: a.Name))
                .ToList();
            if (methodOnlyArgs.Count > 0)
            {
                string typeArgSuffix = string.Join(separator: ",",
                    values: methodOnlyArgs.Select(selector: t => t.FullName));
                baseName = $"{baseName}[{typeArgSuffix}]";
            }
        }

        // Labeled parameter list — `(label: Type, …)` — always appended (even empty `()`); the label
        // is part of overload identity. Uses MangleParamTypeName for wrapper-forwarder inner-generic
        // param mapping.
        baseName += "(" + string.Join(separator: ", ",
            values: routine.Parameters.Select(
                selector: p => $"{p.Name}: {MangleParamTypeName(routine: routine, paramType: p.Type)}")) + ")";

        return Q(name: baseName);
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
            && routine.OwnerType?.TypeArguments is { Count: > 0 and 1 } ownerArgs
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

    // Failability is a routine PROPERTY, never part of the symbol — the `!` is not appended.
    // (Retained as a pass-through for the unresolved/C-call fallback path so its call sites read
    // uniformly with MangleRoutineName, which also strips the bang.)
    internal static string DecorateRoutineSymbolName(string baseName, bool isFailable)
    {
        return baseName;
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
        return routine.Name.Contains(value: "create") ||
               routine.Name is "try_create" or "check_create" or "lookup_create" ||
               routine.OriginalName?.Contains(value: "create") == true;
    }

    private string GetImplicitMeParameterDeclaration(RoutineInfo routine, bool includeName)
    {
        if (routine.OwnerType == null)
        {
            throw new InvalidOperationException(message: "Implicit 'me' requested for routine without owner type.");
        }

        if (IsByRefMeReceiver(routine: routine))
        {
            // Struct-record `me` is a pointer to the caller's storage (named %me.addr), so the
            // parameter doubles as the field-access base — no alloca/store prologue needed.
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
        // Exclusive me-params get `noalias`. Two cases qualify:
        //   - bare entity (bound T can't be duplicated, so the me pointer is exclusive
        //     at the call boundary by the entity-ownership rule),
        //   - `Modifying[T]` (scope-bound exclusive borrow — its definition).
        bool isExclusive = routine.OwnerType is EntityTypeInfo
                           || routine.OwnerType is WrapperTypeInfo { Name: Compiler.Resolution.RuntimeContract.Modifying };
        if (isExclusive)
        {
            return routine.MutationCategory == MutationCategory.Readonly
                ? "noalias readonly"
                : "noalias";
        }

        if (routine.MutationCategory != MutationCategory.Readonly)
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
        || type is WrapperTypeInfo { Name: Compiler.Resolution.RuntimeContract.Modifying }
            ? "noalias"
            : string.Empty;

    /// <summary>
    /// Checks if an external("C") function returns a struct type that must be called with an
    /// explicit sret pointer to match the platform C ABI.
    /// Win64 (MSVC): aggregates larger than 8 bytes return via a hidden sret pointer.
    /// SysV x86-64 / AAPCS64: aggregates up to 16 bytes return in registers (RAX:RDX / x0:x1),
    /// which LLVM's natural aggregate-return lowering already matches — forcing sret there
    /// shifts every C argument by one slot and leaves the result alloca unwritten
    /// (the Linux-CI D128 "1/3 = garbage" bug); only >16-byte aggregates go through memory.
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
        return _target.TargetOS == "windows" ? size > 8 : size > 16;
    }

    /// <summary>
    /// Whether a record's <c>me</c> is passed by reference (a <c>ptr</c> to the caller's storage)
    /// rather than by value. This is a purely type-level decision — no per-method special cases:
    /// <list type="bullet">
    /// <item><b>By reference</b> — every <i>storage-backed</i> record: a struct record (no
    /// <c>@llvm</c> backend) or an <c>@llvm</c> record whose backend is an <i>aggregate</i>
    /// (<c>[N x T]</c>, i.e. <c>Array[T,N]</c> / <c>BitArray[N]</c>). By-ref lets any method mutate
    /// in place and take stable addresses (hijack/get_address, atomics, C FFI), and avoids copying
    /// the aggregate on every call.</item>
    /// <item><b>By value</b> — only <i>scalar</i> <c>@llvm</c> records (<c>iN</c>, <c>fN</c>,
    /// <c>ptr</c>: numerics, <c>Bool</c>, <c>Hijacked</c>, …). The value <i>is</i> the machine
    /// register their operators feed to LLVM intrinsics (<c>add i64 %me, %you</c>), so a pointer
    /// would be wrong. These are pure values and never mutate <c>me</c> in place, so "needs by-value"
    /// and "mutates in place" never overlap.</item>
    /// </list>
    /// Entities are already by-ref via their pointer ABI. This replaces the old <c>$setitem</c>
    /// name-check: <c>Array.setitem</c> is by-ref because Array is aggregate-backed, like every
    /// other Array method — not because of its name.
    /// </summary>
    internal static bool IsByRefMeRecord(TypeInfo? ownerType) => ownerType switch
    {
        // Struct record: no @llvm backend -> storage-backed -> by-ref.
        RecordTypeInfo { HasDirectBackendType: false } => true,
        // @llvm record: by-ref iff the backend is an aggregate ([N x T]); scalars stay by-value.
        RecordTypeInfo { HasDirectBackendType: true, BackendType: { } bt } => bt.StartsWith(value: '['),
        _ => false
    };

    private static bool IsByRefMeReceiver(RoutineInfo routine) =>
        IsByRefMeRecord(ownerType: routine.OwnerType);

    /// <summary>
    /// A <b>thread-shareable</b> record argument to a <c>threaded routine</c> is passed BY
    /// REFERENCE: the worker's parameter is a pointer to the spawner's storage, so every worker
    /// that receives the same cell operates on one address (the basis of <c>Atomic[T]</c>
    /// cross-thread sharing). This mirrors the by-ref <c>me</c> convention — the parameter doubles
    /// as the field/method-access base, no alloca/store copy.
    /// <para>
    /// Only types that carry their own synchronization (<c>Atomic</c>/<c>Shared</c>/<c>Watched</c>)
    /// are shared this way. Every OTHER record falls through to the normal by-value parameter path
    /// (an independent copy is materialised in the worker's prologue), so unsynchronized state can
    /// never silently alias across the thread boundary. Plain scalar value types
    /// (numerics, <c>Hijacked</c>, ...) were always by value. SA (RF-S632) rejects by-ref records
    /// that are neither shareable nor trivially copyable, so they never reach codegen.
    /// </para>
    /// </summary>
    private static bool IsByRefThreadArg(RoutineInfo routine, ParameterInfo param) =>
        routine.AsyncStatus == AsyncStatus.Threaded &&
        IsByRefMeRecord(ownerType: param.Type) &&
        IsThreadShareableType(type: param.Type);

    /// <summary>
    /// True when a type carries its own cross-thread synchronization — the atomic / shared-ownership
    /// wrappers <c>Atomic[T]</c>, <c>Shared[T,P]</c>, <c>Watched[T,P]</c>. These may be passed by
    /// reference across a thread boundary; everything else is copied. Mirrors the SA-side
    /// <c>IsThreadShareable</c>.
    /// </summary>
    private static bool IsThreadShareableType(TypeInfo? type) =>
        type != null &&
        GetGenericBaseNameStatic(type: type) is Compiler.Resolution.RuntimeContract.Atomic or Compiler.Resolution.RuntimeContract.Shared or Compiler.Resolution.RuntimeContract.Watched;

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
