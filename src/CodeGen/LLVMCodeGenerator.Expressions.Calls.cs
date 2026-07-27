using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// Expression code generation for routine calls and compound assignment.
/// </summary>
public partial class LlvmCodeGenerator
{
    private const string CreateMethodName = "$create";

    /// <summary>
    /// Emit routine call as part of this compiler phase.
    /// </summary>
    private string EmitRoutineCall(StringBuilder sb, RoutineCallRequest req)
    {
        (string functionName, List<Expression> arguments, RoutineInfo? resolvedRoutine, TypeInfo? resolvedReturnType, List<TypeExpression>? typeArguments, CallLoweringKind loweringKind, TypeInfo? constructedType) = req;
        // Synthesized bodies (e.g. $hash, $eq, $cmp) are built programmatically and never
        // pass through SemanticVerifier, so they arrive with Unknown. Treat as DirectRoutine.
        if (loweringKind == CallLoweringKind.Unknown)
            loweringKind = CallLoweringKind.DirectRoutine;

        functionName = NormalizeRoutineCallName(functionName: functionName,
            out bool isFailableCallSyntax);

        string? intrinsicCall = TryEmitRecoveredFreeIntrinsicCall(sb: sb,
            functionName: functionName,
            resolvedRoutine: resolvedRoutine,
            arguments: arguments,
            typeArguments: typeArguments,
            resolvedReturnType: resolvedReturnType);
        if (intrinsicCall != null)
        {
            return intrinsicCall;
        }

        // Indirect call through a local Routine-typed variable (e.g., compare(a: x, b: y) where
        // 'compare' is a parameter of type Routine[(T, T), Bool]). The variable holds a CLOSURE
        // pointer `{ fn_ptr, captures... }`: load the function pointer from field 0 and pass the
        // closure pointer as the hidden leading argument (the uniform lambda ABI).
        if (_localVariables.TryGetValue(key: functionName, value: out TypeInfo? localType) &&
            localType is RoutineTypeInfo routineTypeInfo)
        {
            string llvmName =
                _localVarLlvmNames.GetValueOrDefault(functionName, functionName);
            string clVal = NextTemp();
            EmitLine(sb: sb, line: $"  {clVal} = load ptr, ptr %{llvmName}.addr");
            string fpVal = NextTemp();
            EmitLine(sb: sb, line: $"  {fpVal} = load ptr, ptr {clVal}");

            var fpArgValues = new List<string> { clVal };
            var fpArgTypes = new List<string> { "ptr" };
            foreach (Expression arg in arguments)
            {
                string v = EmitExpression(sb: sb, expr: arg);
                fpArgValues.Add(item: v);
                // Use GetParameterLlvmType (named struct form) to match how the callee was declared
                // and how the argument value was loaded. GetExpressionLlvmType uses BackendRepr
                // inline expansion which produces anonymous structs that don't match named SSA values.
                TypeInfo? argType = GetExpressionType(expr: arg);
                fpArgTypes.Add(item: argType != null
                    ? GetParameterLlvmType(type: argType)
                    : GetExpressionLlvmType(expr: arg));
            }

            string retLlvm = routineTypeInfo.ReturnType != null
                ? GetLlvmType(type: routineTypeInfo.ReturnType)
                : "void";

            string callArgs = BuildCallArgs(types: fpArgTypes, values: fpArgValues);
            if (retLlvm == "void")
            {
                EmitLine(sb: sb, line: $"  call void {fpVal}({callArgs})");
                return "undef";
            }

            string result = NextTemp();
            EmitLine(sb: sb,
                line: $"  {result} = call {retLlvm} {fpVal}({callArgs})");
            return result;
        }

        // When SA resolved this entity construction to a user-declared `$create` (non-synthesized),
        // the inline memberwise cases below must NOT intercept it — fall through to the routine-call
        // path so the user `$create` body actually runs. The synthesized memberwise creator and the
        // base-case construction inside `$create` carry a null/synth resolvedRoutine and still inline.
        bool routesToUserCreate = resolvedRoutine is
        {
            IsSynthesized: false, Name: "$create" or "$create!"
        } && constructedType is EntityTypeInfo;

        switch (loweringKind)
        {
            // ValueConversion (`x.D128()`-style casts) is NOT inlined here: it falls through to the
            // routine-call path, which resolves `Target.$create(from: source)` and calls it. The
            // creator's body is the conversion (scalar cast for primitives, BID/IEEE encode for
            // carrier records) — the backend must not re-decide it with a scalar cast.
            case CallLoweringKind.CollectionConstruction when constructedType != null:
                return EmitCollectionLiteralConstructor(sb: sb,
                    resolvedType: constructedType,
                    arguments: arguments);
            case CallLoweringKind.TypeConstructor or CallLoweringKind.WrapperConstruction when constructedType is RecordTypeInfo
            {
                HasDirectBackendType: true
            } directRecord && arguments.Count == 1 &&
                ShouldInlineDirectBackendConstruction(record: directRecord,
                    arg: arguments[index: 0],
                    resolvedRoutine: resolvedRoutine):
                return EmitRecordConstruction(sb: sb, record: directRecord, arguments: arguments);
            case CallLoweringKind.TypeConstructor or CallLoweringKind.WrapperConstruction when constructedType is RecordTypeInfo
                {
                    MemberVariables.Count: > 0
                } ctorRecord &&
                arguments.Count == ctorRecord.MemberVariables.Count && arguments.All(
                    predicate: a =>
                        a is NamedArgumentExpression namedArg &&
                        ctorRecord.MemberVariables.Any(
                            predicate: mv => mv.Name == namedArg.Name)):
                return EmitRecordConstruction(sb: sb, record: ctorRecord, arguments: arguments);
            case CallLoweringKind.TypeConstructor or CallLoweringKind.WrapperConstruction when !routesToUserCreate && constructedType is EntityTypeInfo
                {
                    MemberVariables.Count: > 0
                } ctorEntity &&
                arguments.Count == ctorEntity.MemberVariables.Count && arguments.All(
                    predicate: a =>
                        a is NamedArgumentExpression namedArg &&
                        ctorEntity.MemberVariables.Any(
                            predicate: mv => mv.Name == namedArg.Name)):
                return EmitEntityConstruction(sb: sb, entity: ctorEntity, arguments: arguments);
            case CallLoweringKind.TypeConstructor or CallLoweringKind.WrapperConstruction when constructedType is CrashableTypeInfo ctorCrashable &&
                arguments.Count == ctorCrashable.MemberVariables.Count && arguments.All(
                    predicate: a => ctorCrashable.MemberVariables.Count == 0 ||
                                    a is NamedArgumentExpression namedArg &&
                                    ctorCrashable.MemberVariables.Any(
                                        predicate: mv => mv.Name == namedArg.Name)):
                return EmitCrashableConstruction(sb: sb, crashable: ctorCrashable,
                    arguments: arguments);
        }

        ValidateAnnotatedConstructorOrConversion(functionName: functionName,
            arguments: arguments,
            loweringKind: loweringKind,
            constructedType: constructedType);

        // Use semantic analyzer's resolved routine if available (e.g., generic overload)
        // Otherwise look up the routine -> try full name first, then short name fallback
        RoutineInfo? routine = ResolveInitialFreeCallRoutine(functionName: functionName,
            isFailableCallSyntax: isFailableCallSyntax,
            resolvedRoutine: resolvedRoutine,
            typeArguments: typeArguments,
            arguments: arguments);

        // If not found as a routine, check if it's a type name
        if (routine == null)
        {
            TypeInfo? calledType = LookupTypeInCurrentModule(name: functionName);
            if (calledType != null)
            {
                // Direct named-field construction: when all arg names match field names exactly,
                // emit struct construction directly (avoids $create infinite recursion).
                // e.g., CStr(ptr: from_ptr) inside CStr.$create body
                if (calledType is RecordTypeInfo { MemberVariables.Count: > 0 } record &&
                    arguments.Count == record.MemberVariables.Count && arguments.All(
                        predicate: a =>
                            a is NamedArgumentExpression named &&
                            record.MemberVariables.Any(predicate: mv => mv.Name == named.Name)))
                {
                    return EmitRecordConstruction(sb: sb, record: record, arguments: arguments);
                }

                // Zero-field record construction (e.g. `ReadOnly()` — a `record ... pass`, reached
                // monomorphized from `P()` where P=ReadOnly): no fields to initialize and no user
                // `$create`. Materialize the empty struct value directly. Without this the call
                // falls through to a spurious `call void @TypeName()` to an undefined symbol (LINKERR).
                if (calledType is RecordTypeInfo { MemberVariables.Count: 0 } emptyRecord &&
                    arguments.Count == 0)
                {
                    return EmitRecordConstruction(sb: sb, record: emptyRecord, arguments: arguments);
                }

                if (!routesToUserCreate &&
                    calledType is EntityTypeInfo { MemberVariables.Count: > 0 } entity &&
                    arguments.Count == entity.MemberVariables.Count && arguments.All(
                        predicate: a =>
                            a is NamedArgumentExpression named2 &&
                            entity.MemberVariables.Any(predicate: mv => mv.Name == named2.Name)))
                {
                    return EmitEntityConstruction(sb: sb, entity: entity, arguments: arguments);
                }

                if (calledType is CrashableTypeInfo crashable &&
                    arguments.Count == crashable.MemberVariables.Count && arguments.All(
                        predicate: a => crashable.MemberVariables.Count == 0 ||
                                        a is NamedArgumentExpression named3 &&
                                        crashable.MemberVariables.Any(
                                            predicate: mv => mv.Name == named3.Name)))
                {
                    return EmitCrashableConstruction(sb: sb, crashable: crashable,
                        arguments: arguments);
                }

                // Zero-arg entity construction -> try $create() first, then null
                if (calledType is EntityTypeInfo && arguments.Count == 0)
                {
                    string createName = $"{calledType.Name}.$create";
                    RoutineInfo? creator = _registry.LookupRoutineOverload(baseName: createName,
                        argTypes: new List<TypeInfo>());
                    if (!(creator is { Parameters.Count: 0 }))
                    {
                        throw new InvalidOperationException(
                            $"No zero-arg '$create' found for entity type '{calledType.Name}'. " +
                            "Entity types require a '$create' routine for zero-argument construction.");
                    }
                }

                // Try $create overload -> this covers conversion constructors
                // (e.g., CStr(from: text) -> CStr.$create(from: Text))
                var semanticArgTypes = new List<TypeInfo>();
                foreach (Expression arg in arguments)
                {
                    TypeInfo? t = GetExpressionType(expr: arg);
                    if (t != null)
                    {
                        semanticArgTypes.Add(item: t);
                    }
                }

                // If calledType is a generic definition and explicit typeArguments were provided
                // (e.g., Hijacked[U64](addr)), resolve to the concrete instance so we find the
                // monomorphized $create whose OwnerType is Hijacked[U64], not Hijacked.
                TypeInfo creatorOwnerType = calledType;
                if (calledType.IsGenericDefinition && typeArguments is { Count: > 0 })
                {
                    var resolvedArgs = typeArguments
                        .Select(selector: ta => ResolveTypeExpression(typeExpr: ta))
                        .Where(predicate: t => t != null)
                        .Cast<TypeInfo>()
                        .ToList();
                    if (resolvedArgs.Count == typeArguments.Count)
                    {
                        creatorOwnerType = _registry.GetOrCreateResolution(
                            genericDef: calledType,
                            typeArguments: resolvedArgs);
                    }
                }

                routine = _registry.LookupMethodOverload(type: creatorOwnerType,
                              methodName: CreateMethodName,
                              argTypes: semanticArgTypes) ??
                    _registry.LookupRoutineOverload(
                        baseName: $"{creatorOwnerType.FullName}.{CreateMethodName}",
                        argTypes: semanticArgTypes) ??
                    _registry.LookupRoutineOverload(baseName: $"{calledType.Name}.{CreateMethodName}",
                        argTypes: semanticArgTypes);
                if (routine == null &&
                    calledType is RecordTypeInfo { MemberVariables.Count: 1 } singleRecord && arguments is [NamedArgumentExpression])
                {
                    // For single-field records where arg name doesn't match field name
                    // (e.g., Character(codepoint: val) where field is 'value')
                    return EmitRecordConstruction(sb: sb,
                        record: singleRecord,
                        arguments: arguments);
                }
            }
        }

        // A `threaded routine` call spawns an OS thread; the expression value is an Agent[T]
        // handle (kind THREAD).
        if (routine is { AsyncStatus: AsyncStatus.Threaded })
        {
            return EmitThreadedSpawn(sb: sb, routine: routine, arguments: arguments);
        }

        // A `suspended routine` call creates a coroutine and yields an Agent[T] handle (kind CORO).
        if (routine is { AsyncStatus: AsyncStatus.Suspended })
        {
            return EmitSuspendedSpawn(sb: sb, routine: routine, arguments: arguments);
        }

        // Evaluate arguments and bind them to parameters. RazorForge evaluates arguments in
        // PARAMETER-DECLARATION order regardless of the call-site writing order; named arguments
        // may be reordered and may skip middle parameters that have defaults. So: pre-collect the
        // written-order argument types for overload normalization (no emission yet), bind each
        // written argument to its declared slot (by name, else positionally), then emit slot-by-
        // slot in declaration order — supplying defaults for unprovided slots. Emitting in
        // declaration order also makes argument side effects run in declaration order, not writing
        // order. (Previously args were emitted positionally by writing order, which silently
        // miscompiled reordered named calls and misaligned middle-omitted named defaults.)
        var argValues = new List<string>();
        var argTypes = new List<string>();
        var argTypeInfos = new List<TypeInfo>();

        // Written-order argument types for overload normalization. Mirrors the CPtr handling below
        // so a bare-routine -> CPtr reference contributes the CPtr type (it never has its own
        // expression type).
        var writtenArgTypes = new List<TypeInfo>();
        for (int argIdx = 0; argIdx < arguments.Count; argIdx++)
        {
            Expression arg = arguments[index: argIdx];
            Expression argInner = arg is NamedArgumentExpression namedArg ? namedArg.Value : arg;
            TypeInfo? paramTy = arg is NamedArgumentExpression na
                ? routine?.Parameters.FirstOrDefault(predicate: p => p.Name == na.Name)?.Type
                : routine != null && argIdx < routine.Parameters.Count
                    ? routine.Parameters[index: argIdx].Type
                    : null;
            if (paramTy?.Name == "CPtr"
                && argInner is IdentifierExpression cptrRef
                && _registry.LookupRoutineByName(name: cptrRef.Name) is not null)
            {
                writtenArgTypes.Add(item: paramTy);
                continue;
            }

            TypeInfo? argType = GetExpressionType(expr: arg);
            if (argType == null)
            {
                throw new InvalidOperationException(
                    message:
                    $"Cannot determine type for argument in function call to '{functionName}'");
            }
            writtenArgTypes.Add(item: argType);
        }

        routine = NormalizeResolvedRoutineReference(routine: routine,
            receiverType: null,
            returnType: resolvedReturnType,
            argTypes: writtenArgTypes);

        if (routine != null)
        {
            int paramCount = routine.Parameters.Count;

            // Bind each written argument to its declared parameter slot (named by name, else by
            // position). Unmatched names fall back to position defensively (SA validates names).
            var slotArg = new Expression?[paramCount];
            for (int argIdx = 0; argIdx < arguments.Count; argIdx++)
            {
                Expression a = arguments[index: argIdx];
                int p = argIdx;
                if (a is NamedArgumentExpression na)
                {
                    p = -1;
                    for (int k = 0; k < paramCount; k++)
                    {
                        if (routine.Parameters[index: k].Name == na.Name)
                        {
                            p = k;
                            break;
                        }
                    }

                    if (p < 0)
                    {
                        p = argIdx;
                    }
                }

                if (p >= 0 && p < paramCount)
                {
                    slotArg[p] = a;
                }
            }

            // Emit slot-by-slot in declaration order: provided argument (evaluated here) or default.
            for (int p = 0; p < paramCount; p++)
            {
                ParameterInfo param = routine.Parameters[index: p];
                Expression? bound = slotArg[p];
                if (bound != null)
                {
                    // FFI routine -> CPtr coercion: a bare top-level routine name passed where a
                    // CPtr is expected lowers to the routine's bare C function pointer (its native
                    // symbol already has C ABI; no closure thunk). SA gates this to non-capturing
                    // references.
                    Expression argInner =
                        bound is NamedArgumentExpression nb ? nb.Value : bound;
                    if (param.Type?.Name == "CPtr"
                        && argInner is IdentifierExpression routineRef
                        && _registry.LookupRoutineByName(name: routineRef.Name) is { } refRoutine)
                    {
                        GenerateRoutineDeclaration(routine: refRoutine);
                        argValues.Add(item: $"@{MangleRoutineName(routine: refRoutine)}");
                        argTypeInfos.Add(item: param.Type);
                        argTypes.Add(item: "ptr");
                        continue;
                    }

                    string value = EmitExpression(sb: sb, expr: bound);
                    TypeInfo? argType = GetExpressionType(expr: bound);
                    if (argType == null)
                    {
                        throw new InvalidOperationException(
                            message:
                            $"Cannot determine type for argument in function call to '{functionName}'");
                    }

                    (string coercedValue, string coercedType) = CoerceCallArgumentToParameter(
                        sb: sb,
                        argValue: value,
                        actualType: argType,
                        parameterType: param.Type,
                        callee: routine);
                    argValues.Add(item: coercedValue);
                    argTypes.Add(item: coercedType);
                    argTypeInfos.Add(item: argType);
                }
                else if (param.HasDefaultValue)
                {
                    string value = EmitParameterDefault(sb: sb, param: param);
                    argValues.Add(item: value);
                    argTypeInfos.Add(item: param.Type);
                    argTypes.Add(item: GetParameterLlvmType(type: param.Type));
                }
                else
                {
                    // No argument and no default: SA should have rejected this call. Stop rather
                    // than fabricate a value and emit a malformed call.
                    break;
                }
            }
        }
        else
        {
            // Unresolved/dynamic callee: no parameter info to bind against — emit in writing order.
            for (int argIdx = 0; argIdx < arguments.Count; argIdx++)
            {
                Expression arg = arguments[index: argIdx];
                string value = EmitExpression(sb: sb, expr: arg);
                argValues.Add(item: value);

                TypeInfo? argType = GetExpressionType(expr: arg);
                if (argType == null)
                {
                    throw new InvalidOperationException(
                        message:
                        $"Cannot determine type for argument in function call to '{functionName}'");
                }

                argTypeInfos.Add(item: argType);
                argTypes.Add(item: GetLlvmType(type: argType));
            }
        }

        // Inside monomorphized bodies, an unresolved generic parameter on a non-generic
        // routine's expected parameter type indicates the pipeline failed to substitute.
        // Plain wrapper coercions (e.g. Text → Referring[Text]) are not pipeline bugs.
        if (_typeSubstitutions != null && routine is { GenericDefinition: null, IsGenericDefinition: false } && argValues.Count > 0 && routine.Parameters.Count > 0)
        {
            TypeInfo? expectedType = routine.Parameters[index: 0].Type;
            if (expectedType != null)
            {
                expectedType = ApplyTypeSubstitutions(type: expectedType);
            }

            if (expectedType != null && ContainsGenericParameter(type: expectedType))
            {
                RoutineInfo? genericOverload = _registry.LookupGenericOverload(name: routine.Name);
                if (genericOverload?.GenericParameters is { Count: > 0 })
                {
                    throw new InvalidOperationException(
                        $"Generic free-function resolution for '{genericOverload.BaseName}' reached LLVM codegen. " +
                        "Resolve it during semantic analysis/instantiation.");
                }
            }
        }

        // Build the call
        string mangledName = routine != null
            ? MangleRoutineName(routine: routine)
            : DecorateRoutineSymbolName(baseName: SanitizeLlvmName(name: functionName),
                isFailable: isFailableCallSyntax);

        if (_typeSubstitutions != null && routine?.OwnerType is { IsGenericDefinition: true })
        {
            string subsDump = string.Join(", ",
                _typeSubstitutions.Select(kv => $"{kv.Key}->{kv.Value.FullName}"));
            string tyArgs = typeArguments == null
                ? "<null>"
                : string.Join(",", typeArguments.Select(t => t.Name));
            throw new InvalidOperationException(
                $"Generic owner routine '{routine.OwnerType.FullName}.{routine.Name}' reached LLVM codegen. " +
                $"Resolve the concrete owner during instantiation. " +
                $"[functionName={functionName}, typeArgs=[{tyArgs}], subs={{{subsDump}}}, inRoutine={_currentEmittingRoutine?.Name}]");
        }

        // Ensure the function is declared (generates 'declare' and tracks in _generatedRoutines)
        if (routine != null)
        {
            GenerateRoutineDeclaration(routine: routine);
        }
        else
        {
            _generatedRoutines.Add(item: mangledName);
        }

        // For external("C") functions, F16 (half) params must be bitcast to i16 (C ABI)
        bool isCExtern = routine is { CallingConvention: "C" };
        if (isCExtern)
        {
            for (int i = 0; i < argTypes.Count; i++)
            {
                if (argTypes[index: i] == "half")
                {
                    string bits = NextTemp();
                    EmitLine(sb: sb,
                        line: $"  {bits} = bitcast half {argValues[index: i]} to i16");
                    argValues[index: i] = bits;
                    argTypes[index: i] = "i16";
                }
            }
        }

        string returnType = routine?.ReturnType != null
            ? GetLlvmType(type: routine.ReturnType)
            : "void";
        // Failable routines return T directly -> they crash on failure, no carrier needed

        string callReturnType = isCExtern && returnType == "half"
            ? "i16"
            : returnType;

        // Struct returns classified Indirect by the ABI come back through a hidden sret pointer:
        // external("C") returning structs > 8 bytes (Win-x64 MSVC), or an RF routine whose return
        // is ABI-Indirect. The declaration, definition, and every return already agree (see
        // ReturnsViaSret); the call must pass the result slot as the first argument and load it back.
        bool needsSret = routine != null &&
            (isCExtern ? NeedsCExternSret(routine: routine) : ReturnsViaSret(routine: routine));
        if (needsSret)
        {
            // Allocate space for the result, pass as sret pointer, call as void, then load
            string sretPtr = NextTemp();
            EmitEntryAlloca(llvmName: sretPtr, llvmType: returnType);
            // Insert sret pointer as first argument
            argTypes.Insert(index: 0, item: $"ptr sret({returnType})");
            argValues.Insert(index: 0, item: sretPtr);
            string args = BuildCallArgs(types: argTypes, values: argValues);
            EmitLine(sb: sb, line: $"  call void @{mangledName}({args})");
            ConsumeTransferredCallOwnership(arguments: arguments);
            // Load the result from the sret allocation
            string result = NextTemp();
            EmitLine(sb: sb, line: $"  {result} = load {returnType}, ptr {sretPtr}");
            return result;
        }

        // Coerced (Phase 2) struct return: the callee returns the ABI integer form; call it as that,
        // then reinterpret the result back into the struct value.
        string? calleeCoerce = routine != null && !isCExtern ? ReturnCoerceType(routine: routine) : null;
        if (calleeCoerce != null)
        {
            string result = NextTemp();
            string args = BuildCallArgs(types: argTypes, values: argValues);
            EmitLine(sb: sb, line: $"  {result} = call {calleeCoerce} @{mangledName}({args})");
            ConsumeTransferredCallOwnership(arguments: arguments);
            return CoerceAbiToStruct(sb: sb, abiValue: result, abiType: calleeCoerce,
                structLlvm: returnType);
        }

        if (callReturnType == "void")
        {
            // Void return - no result
            string args = BuildCallArgs(types: argTypes, values: argValues);
            EmitLine(sb: sb, line: $"  call void @{mangledName}({args})");
            ConsumeTransferredCallOwnership(arguments: arguments);
            return "undef";
        }
        else
        {
            // Has return value
            string result = NextTemp();
            string args = BuildCallArgs(types: argTypes, values: argValues);
            EmitLine(sb: sb, line: $"  {result} = call {callReturnType} @{mangledName}({args})");
            ConsumeTransferredCallOwnership(arguments: arguments);
            // For external("C") F16 return, bitcast i16 back to half
            if (isCExtern && returnType == "half" && callReturnType == "i16")
            {
                string halfResult = NextTemp();
                EmitLine(sb: sb, line: $"  {halfResult} = bitcast i16 {result} to half");
                return halfResult;
            }

            return result;
        }
    }

    /// <summary>
    /// Decides whether a 1-arg construction of a `@llvm("...")` record should be inlined as
    /// a scalar cast / reinterpret instead of dispatching to its `$create` routine.
    /// Inline when no $create was resolved, OR when the resolved routine's parameter LLVM
    /// type differs from the wrapper's backend type (a scalar primitive cast like U64(s8)).
    /// Otherwise call the routine — same LLVM type with a resolved $create indicates a real
    /// conversion (e.g. CStr.$create(from: Referring[Text])), which a reinterpret would skip.
    /// </summary>
    private bool ShouldInlineDirectBackendConstruction(RecordTypeInfo record, Expression arg,
        RoutineInfo? resolvedRoutine)
    {
        // No creator resolved -> memberwise / synthesized construction; inline the backend value.
        if (resolvedRoutine == null) return true;

        TypeInfo? argType = GetExpressionType(expr: arg);
        if (argType == null) return false;

        // When SA resolved a real `$create(from: argType)` (or a reference-wrapper of argType),
        // that routine IS the conversion: its body does the correct thing for every backend
        // shape — a scalar cast for @llvm primitives, a real BID/IEEE encode for carrier records
        // (F128/F256/D32/D64/D128/Decimal store a bit-ENCODING, not the value), a UTF-8 encode
        // for CStr(Referring[Text]), and so on. Honor it; never inline a scalar cast, which would
        // bypass the encoding and corrupt carriers (e.g. `D128(42)` as a raw i128 decodes to
        // 4.2E-6175). The backend must not re-decide a conversion the resolver already settled.
        if (resolvedRoutine is { IsSynthesized: false, Name: "$create" or "$create!", Parameters.Count: 1 })
        {
            TypeInfo? paramType = resolvedRoutine.Parameters[index: 0].Type;
            if (paramType != null &&
                (paramType.FullName == argType.FullName ||
                 paramType.TypeArguments is { Count: 1 } pta && pta[index: 0].FullName == argType.FullName))
            {
                return false;
            }
        }

        // Otherwise the resolved routine is synthesized or a mismatched overload (e.g. SA's
        // synthesized U64(Address) landing on U64.$create(S8)). A direct backend reinterpret /
        // scalar cast is the right lowering when the LLVM shapes coincide (no-op reinterpret) or
        // when the source is itself @llvm-primitive; a non-primitive source must go through its
        // routine.
        if (GetLlvmType(type: record) == GetLlvmType(type: argType)) return true;
        return argType is RecordTypeInfo { HasDirectBackendType: true };
    }

    /// <summary>
    /// Generates code for a method call on an object.
    /// The object becomes the implicit 'me' parameter.
    /// </summary>
    private string EmitMemberRoutineCall(StringBuilder sb, MemberExpression member,
        List<Expression> arguments, RoutineInfo? resolvedRoutine = null,
        List<TypeExpression>? typeArguments = null,
        CallLoweringKind loweringKind = CallLoweringKind.Unknown)
    {
        // Synthesized bodies (e.g. $hash, $eq, $cmp) are built programmatically and never
        // pass through SemanticVerifier, so they arrive with Unknown. Treat as DirectMemberRoutine.
        if (loweringKind == CallLoweringKind.Unknown)
            loweringKind = CallLoweringKind.DirectMemberRoutine;

        // Dynamic call through a callable FIELD on the receiver (e.g. `me.predicate(item)` in
        // a stdlib iterator emitter, where `predicate` is a `secret predicate: Routine[(T,), Bool]`
        // field). SA classifies these as DynamicCall. There is no method named `predicate`; load
        // the stored function pointer from the field and call it indirectly — mirroring the
        // free-call indirect path for Routine-typed locals/params (see EmitRoutineCall).
        // SA also stamps DynamicCall on its generic fallback for calls it couldn't resolve to a
        // concrete routine (e.g. `deque.first()` where `first` is an ordinary method returning S64);
        // those are NOT field invocations, so only take this path when the member is genuinely a
        // Routine-typed value — otherwise fall through to normal method resolution.
        if (loweringKind == CallLoweringKind.DynamicCall
            && (member.ResolvedType ?? GetMemberType(member: member)) is RoutineTypeInfo)
        {
            return EmitDynamicMemberFieldCall(sb: sb, member: member, arguments: arguments);
        }

        // Intercept `<entity>.roam_trace_ref()` / `.roam_free_ref()` (cycle-collector hook intrinsics):
        // materialize a closure over the receiver entity's synthesized `$roam_trace_impl` /
        // `$roam_free_impl` — an unbound member-routine value (methods aren't referenceable in surface
        // syntax, so the stdlib routine has a `cptr_none()` fallback body that this replaces). The
        // receiver (`data.as_entity()`) is a pure reinterpret and is intentionally not emitted. The
        // concrete entity type is available here (post-monomorphization), unlike in earlier passes
        // where the receiver type is still the generic parameter. See v0.4.x-cycle-collector.md §9.3.
        if ((member.PropertyName == "roam_trace_ref" || member.PropertyName == "roam_free_ref")
            && arguments.Count == 0
            && GetExpressionType(expr: member.Object) is EntityTypeInfo roamRecvEntity)
        {
            string implName = member.PropertyName == "roam_trace_ref"
                ? "$roam_trace_impl"
                : "$roam_free_impl";
            RoutineInfo? impl = _registry.GetMethodsForType(type: roamRecvEntity)
                .FirstOrDefault(predicate: m => m.Name == implName && m.Parameters.Count == 0);
            if (impl != null)
                return EmitRoutineValueClosure(sb: sb, routine: impl);
        }

        // Intercept var_name() -> inline the variable name from the receiver expression
        if (member.PropertyName == "var_name" && arguments.Count == 0)
        {
            string varName = member.Object is IdentifierExpression varId
                ? varId.Name
                : "<expr>";
            return EmitStringLiteral(sb: sb, value: varName);
        }

        // Intercept `record.get_address()` -> emit `ptrtoint ptr %<receiver-lvalue> to i64`
        // directly, using the caller's lvalue address rather than the body's broken
        // struct->ptr bitcast (records' `me` is a by-value copy whose address lives in the
        // callee's frame). Supported receiver forms:
        //   - identifier x                  -> %x.addr
        //   - member chain obj.field[.f...] -> GEP into the root lvalue, chained per field
        // Entity receivers fall through to the regular call path — `me` for entities is
        // already a ptr, so the stdlib body works. Index access (arr[i].get_address()) is
        // deferred to post-v0.0.1a (requires per-collection `$getitem_addr`).
        if (member.PropertyName == "get_address" && arguments.Count == 0)
        {
            TypeInfo? receiverTypeForIntercept = GetExpressionType(expr: member.Object);
            // Intercept only for struct-typed records — records that ARE pointer-shaped
            // (@llvm("ptr") records like CPtr, Hijacked[T], Viewing[T], Modifying[T]) have
            // their own working bodies that return the wrapped pointer value, not the
            // storage address of the wrapper itself.
            if (receiverTypeForIntercept is RecordTypeInfo { HasDirectBackendType: false })
            {
                string lvaluePtr = EmitLvalueAddress(sb: sb, expr: member.Object);
                string addrTemp = NextTemp();
                EmitLine(sb: sb,
                    line: $"  {addrTemp} = ptrtoint ptr {lvaluePtr} to i64");
                return addrTemp;
            }
        }

        // Intercept `record.hijack()` -> emit the caller's lvalue address directly as the
        // resulting `Hijacked[T]` (which is `@llvm("ptr")`). The stdlib body
        // `Hijacked[T](me.get_address())` runs in a callee frame where `me` is a by-value
        // copy of the record; the address it would capture dies as soon as `hijack` returns,
        // making subsequent `.extract()`/`.inject()` operate on dead stack. Intercepting at
        // the caller keeps the Hijacked bound to the caller's storage. Same lvalue-shape
        // restrictions and pointer-shaped-record exclusion as the `get_address` intercept.
        if (member.PropertyName == Compiler.Resolution.RuntimeContract.RawPointer.Hijack && arguments.Count == 0)
        {
            TypeInfo? receiverTypeForHijack = GetExpressionType(expr: member.Object);
            if (receiverTypeForHijack is RecordTypeInfo { HasDirectBackendType: false } || receiverTypeForHijack is RecordTypeInfo { HasDirectBackendType: true } primShape
                   && primShape.BackendType != "ptr")
            {
                string lvaluePtr = EmitLvalueAddress(sb: sb, expr: member.Object);
                return lvaluePtr;
            }
        }

        (string receiver, TypeInfo? receiverType) = ResolveMemberRoutineCallReceiver(sb: sb,
            member: member);

        switch (receiverType)
        {
            case null:
            {
                string objDesc = member.Object switch
                {
                    IdentifierExpression id => $"identifier '{id.Name}'",
                    CallExpression { Callee: MemberExpression m2 } c =>
                        $"call .{m2.PropertyName}() (ResolvedType={c.ResolvedType?.Name ?? "null"})",
                    _ => member.Object.GetType().Name
                };
                throw new InvalidOperationException(
                    message: $"Cannot determine receiver type for method call .{member.PropertyName} on {objDesc}");
            }
            // WrapperTypeInfo (e.g., Hijacked[Byte]) has FullName="Hijacked[Core.Byte]" (Module=null,
            // inner FullName used for type args) which LookupMethod can't resolve and emits a wrong
            // mangled name. Always normalize to the real RecordTypeInfo (FullName="Core.Hijacked[Byte]")
            // so both LookupMethod and LLVM name mangling work correctly.
            case WrapperTypeInfo wrapperReceiver:
            {
                TypeInfo? wrapperDef = _registry.LookupType(name: wrapperReceiver.Name);
                if (wrapperDef is { IsGenericDefinition: true } &&
                    wrapperReceiver.TypeArguments is { Count: > 0 })
                {
                    receiverType = _registry.GetOrCreateResolution(genericDef: wrapperDef,
                        typeArguments: wrapperReceiver.TypeArguments);
                }

                break;
            }
        }

        // Transparent protocol (e.g., Referring[Text] with no declared methods): dispatch through
        // the first concrete type argument T. Both representations are ptr in LLVM, so no cast needed.
        if (receiverType is ProtocolTypeInfo { Methods.Count: 0, TypeArguments.Count: > 0 } transparentProto)
        {
            receiverType = transparentProto.TypeArguments![index: 0]!;
        }

        bool isFailableMethodCall = member.PropertyName.EndsWith(value: '!');
        string methodName = isFailableMethodCall
            ? member.PropertyName[..^1]
            : member.PropertyName;

        RoutineInfo? method = ResolveInitialMemberRoutineCall(receiverType: receiverType,
            methodName: methodName,
            isFailableMethodCall: isFailableMethodCall,
            resolvedRoutine: resolvedRoutine);

        // Member-conversion call (`x.U64()`, `"42".S32!()`): SA classified it as a
        // TypeConstructor and stamped the resolved `$create`/`$create!` (see #78 in
        // SemanticVerifier.Expressions.Calls — LoweringKind=TypeConstructor is set only when a
        // creator was found, so `method` is guaranteed non-null here). The receiver is the
        // conversion SOURCE: it becomes the `from:` argument, NOT an implicit `me`. Emit the
        // resolved creator call directly — no re-resolution, no inline scalar-cast heuristic. The
        // numeric `$create` bodies do the real cast (e.g. U64.$create(from: U8) = zero_extend),
        // which is also why F128 is correct here: its i128 backend is an IEEE bit carrier, so a
        // scalar cast would reinterpret integer bits as float bits (the old s128→F128 NaN bug).
        if (loweringKind == CallLoweringKind.TypeConstructor && method != null)
        {
            string convMangled = MangleRoutineName(routine: method);
            GenerateRoutineDeclaration(routine: method);
            string convRetTy = method.ReturnType != null
                ? GetLlvmType(type: method.ReturnType)
                : "ptr";
            string convSrcLlvm = GetLlvmType(type: receiverType);
            string convSrcVal = receiver;
            if (ReceiverPassedByRef(receiverType: receiverType))
            {
                convSrcVal = NextTemp();
                EmitLine(sb: sb,
                    line: $"  {convSrcVal} = load {convSrcLlvm}, ptr {receiver}");
            }

            string convResult = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {convResult} = call {convRetTy} @{convMangled}({convSrcLlvm} {convSrcVal})");
            return convResult;
        }

        // Inspecting[T, P] / Claiming[T, P] are `@llvm("ptr")` tokens whose pointer targets the shared
        // ShareController[T, P], NOT the guarded entity. When the resolved method is a FORWARDED entity
        // method (owned by the inner T — e.g. `c.bump()`), the callee's `me` must be the entity, so
        // project the receiver through `controller.data`. Token-own methods ($enter/$exit/$refer/
        // $control/$represent/$diagnose/$destroy, owned by the token itself) keep the controller ptr.
        if (method is { OwnerType: { } methodOwner } &&
            receiverType is RecordTypeInfo tokenRec &&
            GetGenericBaseName(type: tokenRec) is Compiler.Resolution.RuntimeContract.Inspecting or Compiler.Resolution.RuntimeContract.Claiming &&
            tokenRec.TypeArguments is { Count: > 1 } &&
            tokenRec.TypeArguments[index: 0] is EntityTypeInfo tokenInner &&
            methodOwner.FullName == tokenInner.FullName)
        {
            string policyName = tokenRec.TypeArguments[index: 1].FullName;
            TypeInfo? ctrlType =
                _registry.LookupType(name: $"ShareController[{tokenInner.FullName}, {policyName}]")
                ?? _registry.LookupType(
                    name: $"Core.ShareController[{tokenInner.FullName}, {policyName}]");
            if (ctrlType is EntityTypeInfo ctrlEntity)
            {
                receiver = EmitEntityMemberVariableRead(sb: sb,
                    entityPtr: receiver,
                    entity: ctrlEntity,
                    memberVariableName: "data");
            }
        }

        // Member-conversions (`obj.Text()`, `index.U64!()`) are handled above via the
        // TypeConstructor intercept using the SA-stamped `$create`. Any DirectMemberRoutine that
        // still reaches here with no resolved method is a semantic-verifier contract violation.
        if (method == null && loweringKind is CallLoweringKind.DirectMemberRoutine)
        {
            throw new InvalidOperationException(
                $"Method call .{member.PropertyName} on {receiverType.FullName} reached codegen " +
                $"with loweringKind={loweringKind} but no resolved method. Semantic verifier" +
                $" must resolve this.");
        }

        // SA contract: a member call either resolves to a concrete routine (stamped on the call)
        // or is rejected (RF-S458 for `.field()` typos, the dynamic-field `ptr` closure call is
        // classified DynamicCall and handled above). A non-null resolvedRoutine that codegen can't
        // re-find is a registry bug, not a fallback to paper over. The former zero-arg field-read
        // fallback ("`obj.field()` means read the field") was removed: `.field` (access) and
        // `.field()` (call) are distinct forms, so calling a data member is now an SA error, not a
        // silent field read (task #23 — codegen emits the resolved routine, it does not rediscover
        // intent).
        if (method == null && resolvedRoutine != null)
        {
            throw new InvalidOperationException(
                $"SA-resolved routine '{resolvedRoutine.RegistryKey}' could not be located as a " +
                $"method on {receiverType.FullName}.{member.PropertyName} during codegen.");
        }

        // Build argument list: receiver first, then explicit arguments.
        // Skip the receiver for routines that don't take an implicit `me`:
        //   - creators (`$create`) — owner-scoped but no receiver in the param list
        //   - common routines — explicitly declared without `me`
        // Prepending a phantom receiver for these shifts every actual argument by one
        // slot in the LLVM call, corrupting all reads (e.g. Moment.$create(year:2026,...)
        // saw year=zeroinitializer-cast and emitted timestamps in the wrong century).
        bool methodTakesReceiver =
            !(method?.IsCommon == true || method?.Name == CreateMethodName);
        var argValues = methodTakesReceiver
            ? new List<string> { receiver }
            : new List<string>();
        var argTypes = methodTakesReceiver
            ? new List<string>
            {
                ReceiverPassedByRef(receiverType: receiverType)
                    ? "ptr"
                    : GetParameterLlvmType(type: receiverType)
            }
            : new List<string>();
        var argTypeInfos = methodTakesReceiver
            ? new List<TypeInfo> { receiverType }
            : new List<TypeInfo>();

        // Collect explicit argument TYPES in writing order (for overload resolution below). The
        // VALUES are emitted later, in parameter-declaration order, so member-call arguments
        // evaluate in declaration order — matching free routines — regardless of the call-site
        // writing order. argValues/argTypes hold only the receiver for now; the reordered slot loop
        // (or the unresolved-method fallback) rebuilds them.
        foreach (Expression arg in arguments)
        {
            TypeInfo? argType = GetExpressionType(expr: arg);
            if (argType == null)
            {
                throw new InvalidOperationException(
                    message:
                    $"Cannot determine type for argument in method call to '{member.PropertyName}'");
            }

            argTypeInfos.Add(item: argType);
        }

        // Synthesized/lowered bodies (programmatic $eq/$cmp/$hash, operator-lowered calls) never
        // pass through SemanticVerifier, so they arrive without a stamped ResolvedRoutine. Once the
        // concrete argument types are known, resolve the exact overload here so failable operators
        // like $add!/$sub! do not degrade to undecorated placeholder symbols (Core.S32.$add). This
        // is resolution for SA-bypassing bodies, NOT intent-rediscovery on user calls — every
        // SA-analyzed member call is already stamped or rejected (RF-S458). The former bare
        // `LookupMethod(name)` that resolved a non-failable name to its failable variant was
        // removed: that failability-masking is now an SA error (`obj.foo()` when only `foo!`
        // exists), so codegen no longer needs to paper over it (task #23).
        int receiverSkip = methodTakesReceiver ? 1 : 0;
        if (method == null)
        {
            var concreteArgTypes = argTypeInfos.Skip(count: receiverSkip).ToList();
            method = concreteArgTypes.Count > 0
                ? _registry.LookupMethodOverload(type: receiverType,
                    methodName: methodName,
                    argTypes: concreteArgTypes)
                : null;

            method ??= _registry.LookupMethod(type: receiverType,
                methodName: methodName,
                isFailable: isFailableMethodCall);
        }

        method = NormalizeResolvedRoutineReference(routine: method,
            receiverType: receiverType,
            returnType: null,
            argTypes: argTypeInfos.Skip(receiverSkip).ToList());

        // Last-chance: method-generic on a concrete owner (e.g., Array[T,N].$getitem[I]).
        // Neither OLP nor GenericAstRewriter may have resolved it; infer I from the actual
        // call-site argument types and request monomorphization now.
        RoutineInfo? genericMethodForInference = method switch
        {
            { IsGenericDefinition: true, GenericParameters.Count: > 0 } genericDefMethod =>
                genericDefMethod,
            { GenericDefinition: { GenericParameters.Count: > 0 } genericDefinition }
                when RoutineHasUnresolvedTypeArguments(routine: method) => genericDefinition,
            _ => null
        };

        if (genericMethodForInference is { OwnerType: not GenericParameterTypeInfo and not ProtocolTypeInfo and not null } &&
            !genericMethodForInference.OwnerType.IsGenericDefinition)
        {
            var mArgTypes = argTypeInfos.Skip(count: receiverSkip).ToList();
            Dictionary<string, TypeInfo>? inferred = InferMemberRoutineTypeArgs(
                genericMethod: genericMethodForInference, argTypes: mArgTypes);
            if (inferred != null &&
                genericMethodForInference.GenericParameters!.All(predicate: gp =>
                    inferred.ContainsKey(key: gp) &&
                    inferred[key: gp] is not ErrorTypeInfo and not GenericParameterTypeInfo))
            {
                var orderedArgs = genericMethodForInference.GenericParameters!
                    .Select(selector: gp => inferred[key: gp])
                    .ToList();
                method = _registry.GetOrCreateRoutineResolution(genericDef: genericMethodForInference,
                    typeArguments: orderedArgs);
            }
        }

        // LLVM intrinsic template method call (e.g., buf.read![U8](offset)) — emits its own
        // arguments (and reorders named args internally), so it bypasses the deferred slot loop
        // below. Checked here, after the method is fully resolved, so the slot loop never emits its
        // arguments a second time.
        if (method?.LlvmIrTemplate != null)
        {
            return EmitLlvmIntrinsicCall(sb: sb, routine: method,
                receiver: receiver, arguments: arguments, typeArguments: typeArguments,
                resolvedReturnType: member.ResolvedType);
        }

        // Emit explicit arguments in PARAMETER-DECLARATION order, supplying defaults for any
        // unprovided slot (same contract as EmitRoutineCall's free-function fill — SA only
        // VALIDATES that unbound parameters have defaults; materializing them is codegen's job).
        // Named arguments may be written out of order or may skip middle parameters that have
        // defaults; binding each value to its declared slot fixes the silent miscompile of
        // reordered named calls (e.g. `k.sub3(c:1, a:100, b:10)`) and the misalignment of
        // middle-omitted named defaults. Because emission happens HERE, in declaration order, the
        // arguments' side effects also run in declaration order (matching free routines). The
        // receiver (if present) stays at index 0.
        if (method is { IsGenericDefinition: false })
        {
            int paramCount = method.Parameters.Count;

            // Bind each written explicit argument to its declared slot (named by name, else by
            // position). Unmatched names fall back to position defensively (SA validates names).
            var slotArgIndex = new int[paramCount];
            for (int s = 0; s < paramCount; s++)
            {
                slotArgIndex[s] = -1;
            }

            for (int j = 0; j < arguments.Count; j++)
            {
                Expression a = arguments[index: j];
                int p = j;
                if (a is NamedArgumentExpression na)
                {
                    p = -1;
                    for (int k = 0; k < paramCount; k++)
                    {
                        if (method.Parameters[index: k].Name == na.Name)
                        {
                            p = k;
                            break;
                        }
                    }

                    if (p < 0)
                    {
                        p = j;
                    }
                }

                if (p >= 0 && p < paramCount)
                {
                    slotArgIndex[p] = j;
                }
            }

            var reorderedValues = new List<string>();
            var reorderedTypes = new List<string>();
            var reorderedTypeInfos = new List<TypeInfo>();
            if (methodTakesReceiver)
            {
                reorderedValues.Add(item: argValues[index: 0]);
                reorderedTypes.Add(item: argTypes[index: 0]);
                reorderedTypeInfos.Add(item: argTypeInfos[index: 0]);
            }

            for (int p = 0; p < paramCount; p++)
            {
                ParameterInfo param = method.Parameters[index: p];
                int boundArg = slotArgIndex[p];
                if (boundArg >= 0)
                {
                    // Emit the bound argument HERE (in declaration order) so its side effects run
                    // in declaration order.
                    Expression boundExpr = arguments[index: boundArg];
                    string boundValue = EmitExpression(sb: sb, expr: boundExpr);
                    TypeInfo? boundType = GetExpressionType(expr: boundExpr);
                    if (boundType == null)
                    {
                        throw new InvalidOperationException(
                            message:
                            $"Cannot determine type for argument in method call to '{member.PropertyName}'");
                    }

                    reorderedValues.Add(item: boundValue);
                    reorderedTypes.Add(item: GetLlvmType(type: boundType));
                    reorderedTypeInfos.Add(item: boundType);
                    continue;
                }

                if (!param.HasDefaultValue)
                {
                    // No argument and no default: SA should have rejected this. Stop rather than
                    // fabricate a value and emit a malformed call.
                    break;
                }

                string value = EmitParameterDefault(sb: sb, param: param);
                reorderedValues.Add(item: value);
                reorderedTypeInfos.Add(item: param.Type);
                reorderedTypes.Add(item: GetParameterLlvmType(type: param.Type));
            }

            argValues = reorderedValues;
            argTypes = reorderedTypes;
            argTypeInfos = reorderedTypeInfos;
        }
        else
        {
            // Method unresolved or still a generic definition — the declaration-order slot loop
            // doesn't apply (no parameter list to bind against, or this is a synthesized/operator
            // body with positional args). Emit explicit arguments in writing order so the call (or
            // the error path below) has its values. (argTypeInfos already holds their types.)
            foreach (Expression arg in arguments)
            {
                string value = EmitExpression(sb: sb, expr: arg);
                TypeInfo? argType = GetExpressionType(expr: arg);
                if (argType == null)
                {
                    throw new InvalidOperationException(
                        message:
                        $"Cannot determine type for argument in method call to '{member.PropertyName}'");
                }

                argValues.Add(item: value);
                argTypes.Add(item: GetLlvmType(type: argType));
            }
        }

        // Build the call -> for resolved generic types (e.g., List[Character].add_last),
        // use the resolved type name even if the method was found via the base type
        string mangledName;
        if (typeArguments is { Count: > 0 } && method != null)
        {
            if (method is { IsGenericDefinition: true, GenericParameters: { Count: > 0 } gParams } &&
                gParams.Count == typeArguments.Count)
            {
                var resolvedTypeArgs = typeArguments
                    .Select(selector: ta => ResolveTypeExpression(typeExpr: ta))
                    .Where(predicate: t => t != null)
                    .Cast<TypeInfo>()
                    .ToList();
                if (resolvedTypeArgs.Count == typeArguments.Count)
                {
                    method = _registry.GetOrCreateRoutineResolution(genericDef: method,
                        typeArguments: resolvedTypeArgs);
                }
            }

            if (method.IsGenericDefinition)
            {
                throw new InvalidOperationException(
                    $"Explicit method generic call '{receiverType.FullName}.{member.PropertyName}' reached LLVM codegen unresolved.");
            }

            mangledName = MangleRoutineName(routine: method);
        }
        else if (method != null)
        {
            // When the method is fully concrete (non-generic owner, concrete type),
            // MangleRoutineName produces the correct name directly -> no registry re-lookup needed.
            // Fall back to ResolveMemberRoutine only when the carried routine still has a generic/universal owner
            // (e.g., owner is GenericParameterTypeInfo or the generic definition itself), in which case
            // we re-derive from the concrete receiverType.
            if (method is { IsGenericDefinition: false, OwnerType: not GenericParameterTypeInfo and not { IsGenericDefinition: true } })
            {
                mangledName = MangleRoutineName(routine: method);
            }
            else
            {
                // Owner is still generic -> re-derive concrete method from receiverType.
                ResolvedMemberRoutine? resolved = ResolveMemberRoutine(receiverType: receiverType,
                    methodName: method.Name,
                    isFailable: method.IsFailable);
                mangledName = resolved?.MangledName ??
                    Q(name: DecorateRoutineSymbolName(
                        baseName: $"{receiverType.FullName}.{SanitizeLlvmName(name: member.PropertyName)}",
                        isFailable: method.IsFailable));
            }
        }
        else
        {
            throw new InvalidOperationException(
                $"Method '{member.PropertyName}' on '{receiverType.FullName}' could not be resolved after all re-lookup attempts. " +
                $"loweringKind={loweringKind}, resolvedRoutine={(resolvedRoutine?.RegistryKey ?? "<null>")}. " +
                $"Routine: {_currentEmittingRoutine?.Name ?? "<unknown>"} (owner: {_currentEmittingRoutine?.OwnerType?.Name ?? "none"}).");
        }

        // Ensure the method is declared (so the multi-pass stdlib loop can compile its body)
        // Skip for protocol-owned methods -> they can't be declared with protocol types in LLVM IR
        // the monomorphized version (with concrete receiver type) will generate its own declaration.
        if (method is { OwnerType: not ProtocolTypeInfo })
        {
            GenerateRoutineDeclaration(routine: method);
        }

        // Use the semantic-layer-resolved return type.
        // Universal method (OwnerType = GenericParameterTypeInfo "T"): substitute T -> receiverType
        // BEFORE applying outer _typeSubstitutions -> the outer context may map T to something else
 // (e.g., T -> S64 in add_first[T=S64]), which would corrupt the universal T in Retained[T].
        TypeInfo? resolvedReturnType = method?.ReturnType;
        if (resolvedReturnType != null)
        {
            if (method?.OwnerType is GenericParameterTypeInfo universalOwnerParam)
            {
                resolvedReturnType = SubstituteGenericParamInType(
                    type: resolvedReturnType,
                    paramName: universalOwnerParam.Name,
                    concreteType: receiverType);
            }
            else
            {
                resolvedReturnType = ApplyTypeSubstitutions(type: resolvedReturnType);
            }
        }

        // For resolved generic methods, also emit a declaration with the resolved name
        if (!_generatedRoutines.Contains(item: mangledName))
        {
            if (method != null)
            {
                GenerateRoutineDeclaration(routine: method, nameOverride: mangledName);
            }
            else
            {
                string retType = resolvedReturnType != null
                    ? GetLlvmType(type: resolvedReturnType)
                    : "void";
                _rfRoutineDeclarations[key: mangledName] =
                    $"declare {retType} @{mangledName}({string.Join(separator: ", ", values: argTypes)})";
                _generatedRoutines.Add(item: mangledName);
            }
        }

        // Coerce explicit struct value args to byval (the ABI-Indirect arg form) before the call.
        // The receiver, when present, occupies index 0 of the arg lists; explicit args follow and
        // map 1:1 to method.Parameters. Only byval is applied here — other coercions keep their
        // existing handling so member-call behavior is otherwise unchanged.
        if (method != null)
        {
            int recvOffset = methodTakesReceiver ? 1 : 0;
            int explicitCount =
                Math.Min(val1: argValues.Count - recvOffset, val2: method.Parameters.Count);
            for (int i = 0; i < explicitCount; i++)
            {
                int ai = i + recvOffset;
                if (TryCoerceArgToByval(sb: sb, argValue: argValues[index: ai],
                        actualType: argTypeInfos[index: ai],
                        parameterType: method.Parameters[index: i].Type,
                        callee: method,
                        out string bv, out string bt))
                {
                    argValues[index: ai] = bv;
                    argTypes[index: ai] = bt;
                }
                else if (TryCoerceArgToRegister(sb: sb, argValue: argValues[index: ai],
                             parameterType: method.Parameters[index: i].Type,
                             callee: method,
                             out string rv, out string rt))
                {
                    argValues[index: ai] = rv;
                    argTypes[index: ai] = rt;
                }
            }
        }

        string returnType = resolvedReturnType != null
            ? GetLlvmType(type: resolvedReturnType)
            : "void";

        // ABI-Indirect struct return: the callee returns through a hidden sret pointer (declared via
        // GenerateRoutineDeclaration above, which agrees through ReturnsViaSret). Pass the result
        // slot as the first argument, call as void, then load the struct back.
        if (method != null && ReturnsViaSret(routine: method))
        {
            string sretPtr = NextTemp();
            EmitEntryAlloca(llvmName: sretPtr, llvmType: returnType);
            argTypes.Insert(index: 0, item: $"ptr sret({returnType})");
            argValues.Insert(index: 0, item: sretPtr);
            string sretArgs = BuildCallArgs(types: argTypes, values: argValues);
            EmitLine(sb: sb, line: $"  call void @{mangledName}({sretArgs})");
            ConsumeTransferredCallOwnership(arguments: arguments);
            string sretResult = NextTemp();
            EmitLine(sb: sb, line: $"  {sretResult} = load {returnType}, ptr {sretPtr}");
            return sretResult;
        }

        // Coerced (Phase 2) struct return: call as the ABI integer form, reinterpret back to struct.
        string? methodCoerce = method != null ? ReturnCoerceType(routine: method) : null;
        if (methodCoerce != null)
        {
            string args = BuildCallArgs(types: argTypes, values: argValues);
            string r = NextTemp();
            EmitLine(sb: sb, line: $"  {r} = call {methodCoerce} @{mangledName}({args})");
            ConsumeTransferredCallOwnership(arguments: arguments);
            return CoerceAbiToStruct(sb: sb, abiValue: r, abiType: methodCoerce,
                structLlvm: returnType);
        }

        if (returnType == "void")
        {
            string args = BuildCallArgs(types: argTypes, values: argValues);
            EmitLine(sb: sb, line: $"  call void @{mangledName}({args})");
            ConsumeTransferredCallOwnership(arguments: arguments);
            return "undef";
        }
        else
        {
            string result = NextTemp();
            string args = BuildCallArgs(types: argTypes, values: argValues);
            EmitLine(sb: sb, line: $"  {result} = call {returnType} @{mangledName}({args})");
            ConsumeTransferredCallOwnership(arguments: arguments);
            return result;
        }
    }

    /// <summary>
    /// Materializes a parameter's default value as an LLVM value at a call site, returning the value
    /// name. Parameter defaults are raw declaration-site AST that never pass through the lowering
    /// passes (PresetInliningPass / LiteralLoweringPass / ExpressionLoweringPass only rewrite routine
    /// BODIES), so this applies the same normalizations a body expression would have received from
    /// the pipeline: construct empty collection literals inline, inline preset-named defaults, stamp
    /// the parameter type onto bare literals, and normalize Undecided* literal tokens to the concrete
    /// form (EmitLiteral deliberately refuses Undecided* tokens). Shared by the free-routine and
    /// member-call default fill.
    /// </summary>
    private string EmitParameterDefault(StringBuilder sb, ParameterInfo param)
    {
        Expression defaultExpr = param.DefaultValue!;

        // Empty collection-literal default on an owned collection param: construct inline
        // (see TryEmitEmptyCollectionDefault) — these never pass through ExpressionLoweringPass.
        if (TryEmitEmptyCollectionDefault(sb: sb, paramType: param.Type,
                defaultValue: defaultExpr, out string collDefaultValue))
            return collDefaultValue;

        if (defaultExpr is IdentifierExpression presetId &&
            _registry.LookupVariable(presetId.Name) is
                { IsPreset: true, PresetValue: not null } presetVar)
        {
            defaultExpr = presetVar.PresetValue is LiteralExpression presetLit
                ? presetLit with
                {
                    ResolvedType = presetId.ResolvedType ??
                        presetVar.PresetValue.ResolvedType ?? param.Type
                }
                : presetVar.PresetValue;
        }

        if (defaultExpr is LiteralExpression { ResolvedType: null } bareLit)
            defaultExpr = bareLit with { ResolvedType = param.Type };

        defaultExpr = defaultExpr switch
        {
            LiteralExpression { LiteralType: TokenType.UndecidedInteger } undInt =>
                undInt with { LiteralType = TokenType.IntegerLiteral },
            LiteralExpression { LiteralType: TokenType.UndecidedDecimal } undDec =>
                undDec with
                {
                    LiteralType = param.Type.Name switch
                    {
                        "D32" => TokenType.D32Literal,
                        "D64" => TokenType.D64Literal,
                        "D128" => TokenType.D128Literal,
                        _ => TokenType.DecimalLiteral
                    }
                },
            _ => defaultExpr
        };

        return EmitExpression(sb: sb, expr: defaultExpr);
    }

    /// <summary>
    /// Emits an indirect call through a callable FIELD on a receiver — the member form of
    /// EmitRoutineCall's local-variable indirect path. Used for stdlib iterator emitters that
    /// store a lambda in a field (e.g. <c>secret predicate: Routine[(T,), Bool]</c>) and invoke
    /// it as <c>me.predicate(item)</c>. Loads the function pointer from the field, then calls it
    /// indirectly with the supplied arguments.
    /// </summary>
    private string EmitDynamicMemberFieldCall(StringBuilder sb, MemberExpression member,
        List<Expression> arguments)
    {
        TypeInfo? calleeType = member.ResolvedType ?? GetMemberType(member: member);
        if (calleeType is not RoutineTypeInfo routineType)
        {
            throw new InvalidOperationException(
                $"DynamicCall on member '.{member.PropertyName}' but the field's type is " +
                $"'{calleeType?.FullName ?? "<null>"}', not a Routine type. " +
                $"Routine: {_currentEmittingRoutine?.Name ?? "<unknown>"} " +
                $"(owner: {_currentEmittingRoutine?.OwnerType?.Name ?? "none"}).");
        }

        // The field holds a CLOSURE pointer `{ fn_ptr, captures... }`. Load the closure, then the
        // function pointer from field 0, and pass the closure pointer as the hidden leading argument.
        string clVal = EmitMemberVariableAccess(sb: sb, expr: member);
        string fpVal = NextTemp();
        EmitLine(sb: sb, line: $"  {fpVal} = load ptr, ptr {clVal}");

        var fpArgValues = new List<string> { clVal };
        var fpArgTypes = new List<string> { "ptr" };
        foreach (Expression arg in arguments)
        {
            string v = EmitExpression(sb: sb, expr: arg);
            fpArgValues.Add(item: v);
            // Match the named-struct parameter form the callee was declared with (see the
            // local-variable indirect path in EmitRoutineCall for the rationale).
            TypeInfo? argType = GetExpressionType(expr: arg);
            fpArgTypes.Add(item: argType != null
                ? GetParameterLlvmType(type: argType)
                : GetExpressionLlvmType(expr: arg));
        }

        string retLlvm = routineType.ReturnType != null
            ? GetLlvmType(type: routineType.ReturnType)
            : "void";

        string callArgs = BuildCallArgs(types: fpArgTypes, values: fpArgValues);
        if (retLlvm == "void")
        {
            EmitLine(sb: sb, line: $"  call void {fpVal}({callArgs})");
            return "undef";
        }

        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = call {retLlvm} {fpVal}({callArgs})");
        return result;
    }

    /// <summary>
    /// Builds a comma-separated argument list for a call instruction.
    /// </summary>
    private static string BuildCallArgs(List<string> types, List<string> values)
    {
        if (types.Count != values.Count || types.Count == 0)
        {
            return "";
        }

        return string.Join(separator: ", ",
            values: types.Select(selector: (t, i) => $"{t} {values[index: i]}"));
    }

    private (string Value, string LlvmType) CoerceCallArgumentToParameter(StringBuilder sb,
        string argValue, TypeInfo actualType, TypeInfo parameterType, RoutineInfo callee)
    {
        // ABI-Indirect struct value arg: spill to a stack slot and pass `ptr byval(%T)`.
        if (TryCoerceArgToByval(sb: sb, argValue: argValue, actualType: actualType,
                parameterType: parameterType, callee: callee,
                out string byvalValue, out string byvalType))
        {
            return (byvalValue, byvalType);
        }

        // ABI-Coerce small struct value arg: reinterpret into the integer register form.
        if (TryCoerceArgToRegister(sb: sb, argValue: argValue, parameterType: parameterType,
                callee: callee, out string regValue, out string regType))
        {
            return (regValue, regType);
        }

        string expectedLlvm = GetParameterLlvmType(type: parameterType);
        string actualLlvm = GetLlvmType(type: actualType);
        if (actualLlvm == expectedLlvm)
        {
            return (argValue, expectedLlvm);
        }

        if (actualType is RecordTypeInfo
            {
                HasDirectBackendType: false,
                MemberVariables.Count: 1
            } record)
        {
            TypeInfo fieldType = record.MemberVariables[index: 0].Type;
            string fieldLlvm = GetParameterLlvmType(type: fieldType);
            if (fieldLlvm == expectedLlvm)
            {
                string extracted = NextTemp();
                EmitLine(sb: sb,
                    line: $"  {extracted} = extractvalue {actualLlvm} {argValue}, 0");
                return (extracted, expectedLlvm);
            }
        }

        return (argValue, expectedLlvm);
    }

    /// <summary>
    /// Performs the consume transferred call ownership step for this compiler phase.
    /// </summary>
    private void ConsumeTransferredCallOwnership(IEnumerable<Expression> arguments)
    {
        foreach (Expression argument in arguments)
        {
            ConsumeTransferredLocalOwnership(expr: argument);
        }
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Normalize routine call name as part of this compiler phase.
    /// </summary>
    private static string NormalizeRoutineCallName(string functionName, out bool isFailableCallSyntax)
    {
        isFailableCallSyntax = functionName.EndsWith(value: '!');
        return isFailableCallSyntax
            ? functionName[..^1]
            : functionName;
    }

    /// <summary>
    /// Attempts to emit recovered free intrinsic call and reports whether it succeeded.
    /// </summary>
    private string? TryEmitRecoveredFreeIntrinsicCall(StringBuilder sb, string functionName,
        RoutineInfo? resolvedRoutine, List<Expression> arguments,
        List<TypeExpression>? typeArguments, TypeInfo? resolvedReturnType)
    {
        if (resolvedRoutine == null && typeArguments is { Count: > 0 })
        {
            RoutineInfo? intrinsicRoutine =
                _registry.LookupRoutine(fullName: functionName) ??
                _registry.LookupRoutineByName(name: functionName);
            if (intrinsicRoutine?.LlvmIrTemplate != null)
            {
                return EmitLlvmIntrinsicCall(sb: sb, routine: intrinsicRoutine,
                    receiver: null, arguments: arguments, typeArguments: typeArguments,
                    resolvedReturnType: resolvedReturnType);
            }
        }

        if (resolvedRoutine?.LlvmIrTemplate != null)
        {
            return EmitLlvmIntrinsicCall(sb: sb, routine: resolvedRoutine,
                receiver: null, arguments: arguments, typeArguments: typeArguments,
                resolvedReturnType: resolvedReturnType);
        }

        return null;
    }

    /// <summary>
    /// Validate annotated constructor or conversion as part of this compiler phase.
    /// </summary>
    private void ValidateAnnotatedConstructorOrConversion(string functionName,
        List<Expression> arguments, CallLoweringKind loweringKind, TypeInfo? constructedType)
    {
        if (loweringKind != CallLoweringKind.Unknown || constructedType != null ||
            arguments.Count != 1)
        {
            return;
        }

        TypeInfo? calledType = LookupTypeInCurrentModule(name: functionName);
        if (calledType is RecordTypeInfo { HasDirectBackendType: true })
        {
            throw new InvalidOperationException(
                $"Direct-backend conversion/constructor '{functionName}' reached LLVM codegen without lowering metadata. " +
                "Classify it during semantic analysis.");
        }

    }

    /// <summary>
    /// Resolves the initial free call routine from semantic compiler state.
    /// </summary>
    private RoutineInfo? ResolveInitialFreeCallRoutine(string functionName,
        bool isFailableCallSyntax, RoutineInfo? resolvedRoutine,
        List<TypeExpression>? typeArguments, List<Expression> arguments) // NOSONAR S3776
    {
        RoutineInfo? routine = resolvedRoutine ??
                               _registry.LookupRoutine(fullName: functionName,
                                   isFailable: isFailableCallSyntax) ??
                               _registry.LookupRoutineByName(name: functionName,
                                   isFailable: isFailableCallSyntax);

        if (routine is { OwnerType: null, IsGenericDefinition: true } &&
            typeArguments is { Count: > 0 })
        {
            var resolvedRoutineArgs = typeArguments
                                     .Select(selector: ta => ResolveTypeExpression(typeExpr: ta))
                                     .Where(predicate: t => t != null)
                                     .Cast<TypeInfo>()
                                     .ToList();

            if (routine.GenericParameters?.Count == resolvedRoutineArgs.Count)
            {
                routine = _registry.GetOrCreateRoutineResolution(genericDef: routine,
                    typeArguments: resolvedRoutineArgs);
            }
        }

        if (routine is { Name: CreateMethodName, OwnerType: { IsGenericDefinition: true } genOwner } &&
            typeArguments is { Count: > 0 })
        {
            var resolvedOwnerArgs = typeArguments
                                   .Select(selector: ta => ResolveTypeExpression(typeExpr: ta))
                                   .Where(predicate: t => t != null)
                                   .Cast<TypeInfo>()
                                   .ToList();
            if (resolvedOwnerArgs.Count == typeArguments.Count)
            {
                TypeInfo concreteOwner = _registry.GetOrCreateResolution(genericDef: genOwner,
                    typeArguments: resolvedOwnerArgs);
                var ctorArgTypes = new List<TypeInfo>();
                foreach (Expression arg in arguments)
                {
                    TypeInfo? type = GetExpressionType(expr: arg);
                    if (type != null)
                    {
                        ctorArgTypes.Add(item: type);
                    }
                }

                RoutineInfo? rebound = _registry.LookupMethodOverload(type: concreteOwner,
                                           methodName: CreateMethodName,
                                           argTypes: ctorArgTypes) ??
                                       _registry.LookupMethod(type: concreteOwner,
                                           methodName: CreateMethodName,
                                           isFailable: routine.IsFailable);
                if (rebound != null)
                {
                    routine = rebound;
                }
            }
        }

        return routine;
    }

    private (string Receiver, TypeInfo? ReceiverType) ResolveMemberRoutineCallReceiver(StringBuilder sb,
        MemberExpression member)
    {
        // Const-generic value receiver: `N.$represent()` where N is bound to a literal (e.g. 4
        // for `Array[S64, 4]`). Without this check, the typewise-receiver branch below treats N
        // as a type identifier and synthesizes a zero receiver — `Array.$diagnose` then prints
        // `count: 0` instead of the actual N. Substitute the const value before falling through.
        if (member.Object is IdentifierExpression constId
            && !_localVariables.ContainsKey(key: constId.Name)
            && _typeSubstitutions != null
            && _typeSubstitutions.TryGetValue(key: constId.Name, value: out TypeInfo? subType)
            && subType is ConstGenericValueTypeInfo constVal)
        {
            return (constVal.Value.ToString(), ResolveConstGenericUnderlyingType(constVal: constVal));
        }

        if (member.Object is IdentifierExpression typeId &&
            !_localVariables.ContainsKey(key: typeId.Name) &&
            ResolveAggregatePreset(name: typeId.Name) == null)
        {
            // Aggregate-preset receivers are NOT typewise/static receivers — they are by-ref values
            // whose storage is the `@preset.*` global. Fall through so EmitLvalueAddress returns it.
            // `common`/static calls on a bare TYPE name (e.g. `Real.zero()`, `Real(value: 2)` lowered
            // to `Real.$create(...)`) carry no value expression, so GetExpressionType is null on some
            // stdlib paths where SA didn't stamp the receiver's type. Resolve the type by name
            // (module-aware) before giving up — the synthesized zero receiver below is correct for a
            // static method (it has no `me` to read).
            TypeInfo? typeAsReceiver = GetExpressionType(expr: member.Object)
                ?? LookupTypeInCurrentModule(name: typeId.Name);
            if (typeAsReceiver == null)
            {
                throw new InvalidOperationException(
                    $"Typewise/common method receiver '{typeId.Name}' reached LLVM codegen without a semantic receiver type.");
            }

            string llvmType = GetLlvmType(type: typeAsReceiver);
            string receiver = "0";
            if (llvmType.StartsWith(value: '%') || llvmType.StartsWith(value: '{'))
            {
                receiver = "zeroinitializer";
            }
            else if (llvmType == "ptr")
            {
                receiver = "null";
            }
            return (receiver, typeAsReceiver);
        }

        TypeInfo? receiverType = GetExpressionType(expr: member.Object);
        if (ReceiverPassedByRef(receiverType: receiverType))
        {
            // Struct-record methods take `me` by reference: pass the receiver's storage address
            // (spilling an rvalue receiver to a temp), matching the by-ref `me` parameter ABI.
            return (EmitLvalueAddress(sb: sb, expr: member.Object), receiverType);
        }

        string emittedReceiver = EmitExpression(sb: sb, expr: member.Object);
        return (emittedReceiver, receiverType);
    }

    /// <summary>
    /// Computes a pointer to the storage of an lvalue expression — used by the
    /// `record.get_address()` intercept. Recurses through member-access chains; the root
    /// must be a named local/parameter (its alloca name resolves to <c>%name.addr</c>).
    /// Field walks emit one <c>getelementptr</c> per hop. Entity-rooted field chains use
    /// the entity ptr (already a pointer) as the GEP base; record-rooted chains use the
    /// root alloca's address.
    /// </summary>
    private string EmitLvalueAddress(StringBuilder sb, Expression expr) // NOSONAR S3776
    {
        switch (expr)
        {
            case NamedArgumentExpression named:
                // `f(name: lvalue)` — the address of the named argument is the address of its value.
                return EmitLvalueAddress(sb: sb, expr: named.Value);
            case IdentifierExpression id:
            {
                // Aggregate (Array[T,N]) preset: its storage IS the shared `@preset.*` constant
                // global, so its address is the global symbol — the by-ref `me` receiver and any
                // `.hijack()` read in place from there (no per-use copy).
                if (ResolveAggregatePreset(name: id.Name) is { } aggregatePreset)
                    return EmitOrGetPresetGlobal(preset: aggregatePreset);

                if (!_localVariables.ContainsKey(key: id.Name))
                {
                    throw new InvalidOperationException(
                        message:
                        $"Cannot take address of '{id.Name}' — not a local variable or " +
                        $"parameter. Bind it to a `var` first.");
                }
                string llvmName =
                    _localVarLlvmNames.TryGetValue(key: id.Name, value: out string? unique)
                        ? unique
                        : id.Name;
                return $"%{llvmName}.addr";
            }
            case MemberExpression member:
            {
                TypeInfo? parentType = GetExpressionType(expr: member.Object);
                if (parentType == null)
                {
                    throw new InvalidOperationException(
                        message:
                        $"Cannot determine type of parent expression for '.{member.PropertyName}' " +
                        "in address-of chain.");
                }

                // Resolve the field index and base pointer. For record parents this includes the
                // stale-carrier-shell refresh: some cached generic resolutions (e.g. a Maybe[Text]
                // pre-registered before Maybe's body was resolved) arrive with empty MemberVariables,
                // so we repopulate from the generic definition — exactly like the value-read path in
                // EmitRecordMemberVariableRead, keeping address-of and reads on the same field offsets.
                // Entity/crashable parents are already ptr values; records recurse for their storage.
                int fieldIndex = -1;
                string? parentLlvmType = null;
                string? basePtr = null;
                switch (parentType)
                {
                    case RecordTypeInfo recordParent:
                        fieldIndex = ResolveRecordFieldIndex(record: recordParent,
                            memberVariableName: member.PropertyName);
                        if (fieldIndex >= 0)
                        {
                            basePtr = EmitLvalueAddress(sb: sb, expr: member.Object);
                            parentLlvmType = GetRecordTypeName(record: recordParent);
                        }
                        break;
                    case EntityTypeInfo entityParent:
                        fieldIndex = IndexOfMemberVariable(
                            memberVariables: entityParent.MemberVariables, name: member.PropertyName);
                        if (fieldIndex >= 0)
                        {
                            basePtr = EmitExpression(sb: sb, expr: member.Object);
                            parentLlvmType = GetEntityTypeName(entity: entityParent);
                        }
                        break;
                    case CrashableTypeInfo crashableParent:
                        fieldIndex = IndexOfMemberVariable(
                            memberVariables: crashableParent.MemberVariables, name: member.PropertyName);
                        if (fieldIndex >= 0)
                        {
                            basePtr = EmitExpression(sb: sb, expr: member.Object);
                            parentLlvmType = GetCrashableTypeName(crashable: crashableParent);
                        }
                        break;
                }

                if (fieldIndex < 0 || basePtr == null || parentLlvmType == null)
                {
                    // Not a stored field at a known offset (a genuine rvalue member chain, or a parent
                    // kind with no struct fields): materialize the value and address the temporary.
                    return EmitSpillToTempAddress(sb: sb, expr: expr);
                }

                string fieldPtr = NextTemp();
                EmitLine(sb: sb,
                    line: $"  {fieldPtr} = getelementptr {parentLlvmType}, ptr {basePtr}, i32 0, i32 {fieldIndex}");
                return fieldPtr;
            }
            default:
                // Rvalue receiver (call result, constructor, literal, …): no stable storage exists,
                // so spill the value to a temp and return its address. Lets a by-ref record method
                // (or get_address/hijack) take the address of a temporary.
                return EmitSpillToTempAddress(sb: sb, expr: expr);
        }
    }

    /// <summary>Index of the named member variable, or -1 if absent.</summary>
    private static int IndexOfMemberVariable(List<MemberVariableInfo> memberVariables, string name)
    {
        for (int i = 0; i < memberVariables.Count; i++)
        {
            if (memberVariables[index: i].Name == name) return i;
        }
        return -1;
    }

    /// <summary>
    /// Finds a record field's index, refreshing <see cref="RecordTypeInfo.MemberVariables"/> from the
    /// generic definition when the resolution arrived as an empty carrier shell (e.g. a cached
    /// <c>Maybe[Text]</c> registered before <c>Maybe</c>'s body was resolved). Mirrors the fallback in
    /// <c>EmitRecordMemberVariableRead</c> so address-of and value reads agree on field offsets.
    /// </summary>
    private static int ResolveRecordFieldIndex(RecordTypeInfo record, string memberVariableName)
    {
        int idx = IndexOfMemberVariable(memberVariables: record.MemberVariables,
            name: memberVariableName);
        if (idx >= 0) return idx;

        if (record.GenericDefinition is RecordTypeInfo gdef && record.TypeArguments != null &&
            gdef.MemberVariables.Count > 0)
        {
            var fresh = (RecordTypeInfo)gdef.CreateInstance(typeArguments: record.TypeArguments);
            record.MemberVariables = fresh.MemberVariables;
            return IndexOfMemberVariable(memberVariables: record.MemberVariables,
                name: memberVariableName);
        }

        return -1;
    }

    /// <summary>
    /// Materializes an expression's value into a fresh entry alloca and returns the alloca pointer.
    /// Used by <see cref="EmitLvalueAddress"/> when an address is needed but the expression has no
    /// stable storage — a genuine rvalue (call result, constructor, literal). The temporary is a
    /// callee-local copy: correct for reads and value-identity operations, but a write through it
    /// would not reach an original (rvalues have none).
    /// </summary>
    private string EmitSpillToTempAddress(StringBuilder sb, Expression expr)
    {
        TypeInfo? exprType = GetExpressionType(expr: expr);
        if (exprType == null)
        {
            throw new InvalidOperationException(
                message:
                $"Cannot take address of expression form '{expr.GetType().Name}' — " +
                "unknown type, cannot spill to a temporary.");
        }

        string val = EmitExpression(sb: sb, expr: expr);
        string llvm = GetLlvmType(type: exprType);
        string slot = NextTemp();
        EmitEntryAlloca(llvmName: slot, llvmType: llvm);
        EmitLine(sb: sb, line: $"  store {llvm} {val}, ptr {slot}");
        return slot;
    }

    /// <summary>
    /// Whether a method receiver of this type is passed by reference (a <c>ptr</c> to its storage):
    /// storage-backed records — struct records (no <c>@llvm</c> backend) and aggregate-backed
    /// <c>@llvm</c> records (<c>[N x T]</c>, e.g. Array/BitArray). Shares the exact predicate with
    /// the callee-side <c>IsByRefMeReceiver</c> (via <c>IsByRefMeRecord</c>) so call sites pass the
    /// receiver's address and the matching <c>ptr</c> argument type. Scalar <c>@llvm</c> records stay
    /// by value.
    /// </summary>
    private static bool ReceiverPassedByRef(TypeInfo? receiverType) =>
        IsByRefMeRecord(ownerType: receiverType);

    /// <summary>
    /// Resolves the initial member routine call from semantic compiler state.
    /// </summary>
    private RoutineInfo? ResolveInitialMemberRoutineCall(TypeInfo receiverType, string methodName,
        bool isFailableMethodCall, RoutineInfo? resolvedRoutine)
    {
        if (resolvedRoutine != null) return resolvedRoutine;
        RoutineInfo? m = _registry.LookupMethod(type: receiverType,
            methodName: methodName,
            isFailable: isFailableMethodCall);
        // U64.$sub etc. only define the failable form; retry when the operator-lowered call
        // came in non-failable. Comment at the call site says codegen retries here.
        if (m == null && !isFailableMethodCall && methodName.StartsWith('$'))
        {
            m = _registry.LookupMethod(type: receiverType,
                methodName: methodName,
                isFailable: true);
        }
        return m;
    }

}

/// <summary>
/// Bundles the arguments for <see cref="LlvmCodeGenerator"/> free-function call emission.
/// Replaces the 8-parameter overload so callers pass a single context value.
/// </summary>
internal sealed record RoutineCallRequest(
    string FunctionName,
    List<Expression> Arguments,
    RoutineInfo? ResolvedRoutine,
    TypeInfo? ResolvedReturnType,
    List<TypeExpression>? TypeArguments,
    CallLoweringKind LoweringKind,
    TypeInfo? ConstructedType);
