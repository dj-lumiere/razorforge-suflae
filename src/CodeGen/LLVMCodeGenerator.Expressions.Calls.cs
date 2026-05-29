using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

        // Indirect call through a local function-pointer variable (e.g., compare(a: x, b: y)
        // where 'compare' is a parameter of type Routine[(T, T), Bool]).
        if (_localVariables.TryGetValue(key: functionName, value: out TypeInfo? localType) &&
            localType is RoutineTypeInfo routineTypeInfo)
        {
            string llvmName =
                _localVarLlvmNames.GetValueOrDefault(functionName, functionName);
            string fpVal = NextTemp();
            EmitLine(sb: sb, line: $"  {fpVal} = load ptr, ptr %{llvmName}.addr");

            var fpArgValues = new List<string>();
            var fpArgTypes = new List<string>();
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

        switch (loweringKind)
        {
            case CallLoweringKind.ValueConversion when arguments.Count == 1 &&
                                                       constructedType is RecordTypeInfo { HasDirectBackendType: true } &&
                                                       GetExpressionType(expr: arguments[index: 0]) is RecordTypeInfo { HasDirectBackendType: true }:
                // Both source and target must be @llvm primitive-typed for inline cast.
                // When source is non-primitive (e.g. Text in `F128!("3.14")`), fall through
                // to the routine lookup path so it resolves to `Target.$create!(from: src)`.
                return EmitPrimitiveTypeConversion(sb: sb,
                    arg: arguments[index: 0],
                    targetType: constructedType);
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
            case CallLoweringKind.TypeConstructor or CallLoweringKind.WrapperConstruction when constructedType is EntityTypeInfo
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

                if (calledType is EntityTypeInfo { MemberVariables.Count: > 0 } entity &&
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

        // Evaluate all arguments
        var argValues = new List<string>();
        var argTypes = new List<string>();
        var argTypeInfos = new List<TypeInfo>();

        foreach (Expression arg in arguments)
        {
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

        routine = NormalizeResolvedRoutineReference(routine: routine,
            receiverType: null,
            returnType: resolvedReturnType,
            argTypes: argTypeInfos);

        if (routine != null)
        {
            int explicitCount = Math.Min(val1: argValues.Count, val2: routine.Parameters.Count);
            for (int i = 0; i < explicitCount; i++)
            {
                (argValues[i], argTypes[i]) = CoerceCallArgumentToParameter(sb: sb,
                    argValue: argValues[i],
                    actualType: argTypeInfos[i],
                    parameterType: routine.Parameters[index: i].Type);
            }
        }

        // Supply default arguments for parameters not covered by explicit arguments
        if (routine != null)
        {
            for (int i = argValues.Count; i < routine.Parameters.Count; i++)
            {
                ParameterInfo param = routine.Parameters[index: i];
                if (param.HasDefaultValue)
                {
                    string value = EmitExpression(sb: sb, expr: param.DefaultValue!);
                    argValues.Add(item: value);
                    argTypeInfos.Add(item: param.Type);
                    argTypes.Add(item: GetParameterLlvmType(type: param.Type));
                }
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

        // On Windows x64 MSVC ABI, external("C") functions returning structs > 8 bytes
        // use a hidden sret pointer as the first parameter. We must match this convention.
        bool needsSret = isCExtern && routine != null && NeedsCExternSret(routine: routine);
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
            // Load the result from the sret allocation
            string result = NextTemp();
            EmitLine(sb: sb, line: $"  {result} = load {returnType}, ptr {sretPtr}");
            return result;
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
        if (resolvedRoutine == null) return true;

        TypeInfo? argType = GetExpressionType(expr: arg);
        if (argType == null) return false;

        string targetLlvm = GetLlvmType(type: record);
        string argLlvm = GetLlvmType(type: argType);
        if (argLlvm != targetLlvm)
        {
            // Differing LLVM types: only inline as a scalar cast when the source is also
            // @llvm-primitive (e.g. S64→S32 trunc, F32→F64 fpext). When the source is a
            // non-primitive carrier such as Text, inlining would emit a bogus
            // `ptrtoint ptr to fp128`; the real conversion lives in `$create!` and the
            // routine call must be honored instead.
            return argType is RecordTypeInfo { HasDirectBackendType: true };
        }

        // Same LLVM type. If the resolved routine's first param doesn't match the arg type,
        // SA picked the wrong overload (e.g. synthesized U64(Address) resolving to U64.$create(S8));
        // a no-op reinterpret is the correct lowering since LLVM types coincide.
        if (resolvedRoutine.Parameters.Count == 0) return true;
        TypeInfo? paramType = resolvedRoutine.Parameters[index: 0].Type;
        if (paramType == null) return false;
        if (paramType.FullName == argType.FullName) return false;
        // Referring[X] (protocol) / Possessed[X] / ... reference wrappers accept X as the
        // underlying value. The routine body does a real conversion (e.g.
        // CStr.$create(from: Referring[Text]) UTF-8-encodes a Text entity); inlining the
        // construction as a pointer reinterpret would skip that and pass the entity bytes
        // straight to rf_console_show — garbling. Check both wrapper and protocol shapes
        // since Referring[T] is a `protocol Referring[T]` (ProtocolTypeInfo), not a record.
        if (paramType.TypeArguments is { Count: 1 } pTypeArgs
            && pTypeArgs[0].FullName == argType.FullName)
            return false;
        return true;
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
            // (@llvm("ptr") records like CPtr, Hijacked[T], Viewed[T], Grasped[T]) have
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
        if (member.PropertyName == "hijack" && arguments.Count == 0)
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

        // Representable pattern: obj.Text() -> Text.$create(from: obj)
        // When the method name matches a registered type and no direct method exists,
        // route to TypeName.$create(from: receiver).
        // Strip '!' suffix for failable conversions (e.g., index.U64!() -> U64)
        // Guard: if SA stamped this call (loweringKind != Unknown), it must have resolved the method.
        // Reaching this fallback for SA-classified calls is a codegen contract violation.
        string conversionTypeName = member.PropertyName.EndsWith(value: '!')
            ? member.PropertyName[..^1]
            : member.PropertyName;
        switch (method)
        {
            case null when loweringKind is CallLoweringKind.DirectMemberRoutine:
                throw new InvalidOperationException(
                    $"Method call .{member.PropertyName} on {receiverType.FullName} reached codegen " +
                    $"with loweringKind={loweringKind} but no resolved method. Semantic verifier" +
                    $" must resolve this.");
            case null when arguments.Count == 0 &&
                           _registry.LookupType(name: conversionTypeName) != null:
            {
                // For @llvm primitive types where the source is also primitive,
                // emit inline conversion (trunc/zext/sext/fpcast) instead of a function call.
                // e.g., val.Address() -> inline zext/trunc.
                // Non-primitive sources (e.g., Text.S32!()) must go through $create.
                TypeInfo? targetType = _registry.LookupType(name: conversionTypeName);
                if (targetType is RecordTypeInfo { HasDirectBackendType: true } &&
                    receiverType is RecordTypeInfo { HasDirectBackendType: true })
                {
                    return EmitPrimitiveTypeConversion(sb: sb,
                        arg: member.Object,
                        targetType: targetType);
                }

                var argTypes2 = new List<TypeInfo> { receiverType };
                // $create is owner-scoped; LookupRoutineOverload only indexes free functions,
                // so use LookupMethodOverload to honor the receiver-type overload signature
                // (otherwise Text.S32!() resolves to S32.$create(from: S8), the first overload).
                RoutineInfo? creator =
                    _registry.LookupMethodOverload(type: targetType!,
                        methodName: CreateMethodName,
                        argTypes: argTypes2);
                string creatorName = $"{conversionTypeName}.{CreateMethodName}";
                creator ??= _registry.LookupRoutineOverload(baseName: creatorName,
                    argTypes: argTypes2);
                if (creator != null)
                {
                    // Non-generic path
                    string funcName = MangleRoutineName(routine: creator);
                    GenerateRoutineDeclaration(routine: creator);
                    string retType3 = creator.ReturnType != null
                        ? GetLlvmType(type: creator.ReturnType)
                        : "ptr";

                    string receiverLlvm3 = GetLlvmType(type: receiverType);
                    string result3 = NextTemp();
                    EmitLine(sb: sb,
                        line:
                        $"  {result3} = call {retType3} @{funcName}({receiverLlvm3} {receiver})");

                    return result3;
                }

                break;
            }
        }

        switch (method)
        {
            // For zero-argument methods on entity/record types, if the method name matches a field,
            // emit as a direct field access (common pattern: List[T].count() returns me.count)
            // Also applies when method is a generic definition that can't be monomorphized.
            // Guard: if SA provided resolvedRoutine, method must be non-null and concrete here.
            case null when resolvedRoutine != null:
                throw new InvalidOperationException(
                    $"SA-resolved routine '{resolvedRoutine.RegistryKey}' could not be located as a " +
                    $"method on {receiverType.FullName}.{member.PropertyName} during codegen.");
            case null when arguments.Count == 0:
            {
                switch (receiverType)
                {
                    case EntityTypeInfo entity when
                        entity.MemberVariables.Any(predicate: mv => mv.Name == member.PropertyName):
                        return EmitEntityMemberVariableRead(sb: sb,
                            entityPtr: receiver,
                            entity: entity,
                            memberVariableName: member.PropertyName);
                    case RecordTypeInfo record when
                        record.MemberVariables.Any(predicate: mv => mv.Name == member.PropertyName):
                        return EmitRecordMemberVariableRead(sb: sb,
                            recordValue: receiver,
                            record: record,
                            memberVariableName: member.PropertyName);
                }

                break;
            }
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
            ? new List<string> { GetParameterLlvmType(type: receiverType) }
            : new List<string>();
        var argTypeInfos = methodTakesReceiver
            ? new List<TypeInfo> { receiverType }
            : new List<TypeInfo>();

        foreach (Expression arg in arguments)
        {
            string value = EmitExpression(sb: sb, expr: arg);
            argValues.Add(item: value);

            TypeInfo? argType = GetExpressionType(expr: arg);
            if (argType == null)
            {
                throw new InvalidOperationException(
                    message:
                    $"Cannot determine type for argument in method call to '{member.PropertyName}'");
            }

            argTypeInfos.Add(item: argType);
            string llvmArgType = GetLlvmType(type: argType);
            argTypes.Add(item: llvmArgType);
        }

        // Some synthesized/lowered bodies still reach codegen without an attached ResolvedRoutine
        // on operator-style method calls. Once the concrete argument types are known, retry exact
        // overload lookup here so failable operators like $add!/$sub! do not degrade to
        // undecorated placeholder symbols such as Core.S32.$add.
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

            if (method == null && !isFailableMethodCall)
            {
                method ??= _registry.LookupMethod(type: receiverType,
                    methodName: methodName);
            }
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

        // Method-level generics on regular method calls (e.g., method has [T] on itself, not the owner)
        // Infer type args from concrete argument types and monomorphize.
        // Skip when typeArguments != null -> caller provided explicit type args (from GenericCallLoweringPass).
        // LLVM intrinsic template method call (e.g., buf.read![U8](offset)).
        if (method?.LlvmIrTemplate != null)
        {
            return EmitLlvmIntrinsicCall(sb: sb, routine: method,
                receiver: receiver, arguments: arguments, typeArguments: typeArguments,
                resolvedReturnType: member.ResolvedType);
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

        string returnType = resolvedReturnType != null
            ? GetLlvmType(type: resolvedReturnType)
            : "void";

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
        string argValue, TypeInfo actualType, TypeInfo parameterType)
    {
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
            !_localVariables.ContainsKey(key: typeId.Name))
        {
            TypeInfo? typeAsReceiver = GetExpressionType(expr: member.Object);
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

        string emittedReceiver = EmitExpression(sb: sb, expr: member.Object);
        TypeInfo? receiverType = GetExpressionType(expr: member.Object);
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
            case IdentifierExpression id:
            {
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

                // For entity parents, the parent's *value* is already a ptr to the entity
                // struct. For record parents, recurse to get the parent's storage address.
                string basePtr;
                List<MemberVariableInfo>? memberVars;
                string parentLlvmType;
                switch (parentType)
                {
                    case EntityTypeInfo entityParent:
                        basePtr = EmitExpression(sb: sb, expr: member.Object);
                        memberVars = entityParent.MemberVariables;
                        parentLlvmType = GetEntityTypeName(entity: entityParent);
                        break;
                    case RecordTypeInfo recordParent:
                        basePtr = EmitLvalueAddress(sb: sb, expr: member.Object);
                        memberVars = recordParent.MemberVariables;
                        parentLlvmType = GetRecordTypeName(record: recordParent);
                        break;
                    default:
                        throw new InvalidOperationException(
                            message:
                            $"Cannot take address of '.{member.PropertyName}' on a " +
                            $"'{parentType.Name}' (only entity or record parents are supported).");
                }

                int fieldIndex = -1;
                for (int i = 0; i < memberVars.Count; i++)
                {
                    if (memberVars[index: i].Name == member.PropertyName)
                    {
                        fieldIndex = i;
                        break;
                    }
                }
                if (fieldIndex < 0)
                {
                    throw new InvalidOperationException(
                        message:
                        $"Cannot take address of '.{member.PropertyName}' — no such field " +
                        $"on '{parentType.Name}'.");
                }

                string fieldPtr = NextTemp();
                EmitLine(sb: sb,
                    line: $"  {fieldPtr} = getelementptr {parentLlvmType}, ptr {basePtr}, i32 0, i32 {fieldIndex}");
                return fieldPtr;
            }
            default:
                throw new InvalidOperationException(
                    message:
                    $"Cannot take address of expression form '{expr.GetType().Name}'. " +
                    "Address-of is supported on named locals and field-access chains; " +
                    "index access (`arr[i].get_address()`) is deferred to post-v0.0.1a.");
        }
    }

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
