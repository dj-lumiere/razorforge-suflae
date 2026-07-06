using System;
using System.Linq;
using System.Text;
using Compiler.Postprocessing;
using Compiler.Synthesis;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// Statement code generation for return, throw, absent, and variant-return paths.
/// </summary>
public partial class LlvmCodeGenerator
{
    private const string TracePop = "  call void @_rf_trace_pop()";

    #region Return Statements

    private void EmitReturn(StringBuilder sb, ReturnStatement ret)
    {
        if (ret.Value == null)
        {
            EmitNullValueReturn(sb: sb);
            return;
        }

        string earlyType = _currentRoutineReturnType != null
            ? GetLlvmType(type: _currentRoutineReturnType)
            : "void";
        if (earlyType == "void")
        {
            EmitBlankExpressionReturn(sb: sb);
            return;
        }

        TypeInfo? retValType = GetExpressionType(expr: ret.Value);
        if (retValType is CrashableTypeInfo && _currentRoutineIsFailable)
        {
            EmitThrow(sb: sb, throwStmt: new ThrowStatement(Error: ret.Value, Location: ret.Location));
            return;
        }

        string value = EmitExpression(sb: sb, expr: ret.Value);
        TypeInfo? retType = _currentRoutineReturnType ?? GetExpressionType(expr: ret.Value);
        if (retType == null)
            throw new InvalidOperationException(message: "Cannot determine return type for return statement");

        string llvmType = GetLlvmType(type: retType);

        string? returnedVarName = ret.Value is IdentifierExpression id &&
                                  _localEntityVars.Any(predicate: e => e.Name == id.Name)
            ? id.Name
            : null;
        EmitRcRecordCleanup(sb: sb);
        EmitEntityCleanup(sb: sb, returnedVarName: returnedVarName);
        if (_traceCurrentRoutine)
            EmitLine(sb: sb, line: TracePop);

        TypeInfo? exprType = GetExpressionType(expr: ret.Value);
        if (IsMaybeType(type: retType) && value != "zeroinitializer" &&
            (exprType == null || !IsMaybeType(type: exprType)))
        {
            EmitMaybeWrappedReturn(sb: sb, retType: retType, innerValue: value);
            return;
        }

        // Indirect (sret) return: the struct value is stored through the hidden %sret pointer and
        // the function returns void (see _currentReturnViaSret / GenerateRoutineDefinition).
        if (_currentReturnViaSret)
        {
            EmitLine(sb: sb, line: $"  store {llvmType} {value}, ptr %sret");
            EmitLine(sb: sb, line: "  ret void");
            return;
        }
        // Coerced (Phase 2) return: reinterpret the struct value into its ABI register type.
        if (_currentReturnCoerceType != null)
        {
            string coerced = CoerceStructToAbi(sb: sb, structValue: value, structLlvm: llvmType,
                abiType: _currentReturnCoerceType);
            EmitLine(sb: sb, line: $"  ret {_currentReturnCoerceType} {coerced}");
            return;
        }
        EmitLine(sb: sb, line: $"  ret {llvmType} {value}");
    }

    private void EmitNullValueReturn(StringBuilder sb)
    {
        EmitRcRecordCleanup(sb: sb);
        EmitEntityCleanup(sb: sb, returnedVarName: null);
        if (_traceCurrentRoutine)
            EmitLine(sb: sb, line: TracePop);
        if (_currentRoutineReturnType == null)
        {
            EmitLine(sb: sb, line: "  ret void");
            return;
        }
        string retLlvmType = GetLlvmType(type: _currentRoutineReturnType);
        if (retLlvmType == "void")
            EmitLine(sb: sb, line: "  ret void");
        else
        {
            string retZero = GetZeroValue(type: _currentRoutineReturnType);
            EmitLine(sb: sb, line: $"  ret {retLlvmType} {retZero}");
        }
    }

    // For check_/try_ variant wrappers with Blank (void) return, emit success carrier.
    private void EmitBlankExpressionReturn(StringBuilder sb)
    {
        EmitRcRecordCleanup(sb: sb);
        EmitEntityCleanup(sb: sb, returnedVarName: null);
        if (_traceCurrentRoutine)
            EmitLine(sb: sb, line: TracePop);
        if (_currentEmittingRoutine?.FailableVariant == FailableVariant.Check &&
            _currentRoutineReturnType != null)
        {
            string carrier = GetResultCarrierLlvmType(valueType: _currentRoutineReturnType);
            EmitLine(sb: sb, line: $"  ret {carrier} zeroinitializer");
        }
        else if (_currentEmittingRoutine?.FailableVariant == FailableVariant.TryBool)
        {
            EmitLine(sb: sb, line: "  ret i1 false");
        }
        else
        {
            EmitLine(sb: sb, line: "  ret void");
        }
    }

    private void EmitMaybeWrappedReturn(StringBuilder sb, TypeInfo retType, string innerValue)
    {
        TypeInfo innerType = retType.TypeArguments is { Count: > 0 }
            ? retType.TypeArguments[index: 0]
            : retType;
        string carrierType = GetLlvmType(type: retType);
        string innerLlvm = innerType is EntityTypeInfo ? "ptr" : GetLlvmType(type: innerType);
        // Maybe `present` (field 0) is a Bool, stored as i8 (see GetFieldStorageLlvmType).
        string v0 = NextTemp();
        EmitLine(sb: sb, line: $"  {v0} = insertvalue {carrierType} zeroinitializer, i8 1, 0");
        string v1 = NextTemp();
        EmitLine(sb: sb, line: $"  {v1} = insertvalue {carrierType} {v0}, {innerLlvm} {innerValue}, 1");
        EmitLine(sb: sb, line: $"  ret {carrierType} {v1}");
    }

    private bool IsEntityConstructorCall(Expression? expr)
    {
        return expr switch
        {
            CreatorExpression { ConstructedType: EntityTypeInfo } or
                ListLiteralExpression or SetLiteralExpression
                or DictLiteralExpression => true,
            CreatorExpression => true,
            CallExpression { ConstructedType: EntityTypeInfo } => true,
            CallExpression { Callee: IdentifierExpression id } =>
                _registry.LookupType(name: id.Name) is EntityTypeInfo,
            _ => false
        };
    }

    private void EmitEntityCleanup(StringBuilder sb, string? returnedVarName)
    {
        // Scope-exit teardown of owned locals is lowered into the AST as explicit
        // `local.$destroy()` calls by ScopeTeardownLoweringPass (Phase 7), so codegen emits none.
        //
        // The entity self-free (freeing the heap allocation backing `me`) is ALSO lowered into the
        // synthesized `$destroy` body as `me.hijack().invalidate()` (see
        // WiredRoutinePass.BuildEntitySelfFree). Codegen used to additionally emit a raw
        // `rf_invalidate(me)` here for synthesized entity `$destroy`, but that DUPLICATED the
        // AST-level free → every synthesized entity `$destroy` double-freed `me` (ASan: "double-free"
        // / glibc "double free in tcache"), crashing programs that destroy an owned entity at scope
        // exit (e.g. `using x.modify() as g`). The AST free is the single source of truth, so this is
        // now a no-op; the parameters are kept for call-site compatibility.
        _ = sb;
        _ = returnedVarName;
    }

    #endregion

    #region Throw / Absent / Becomes

    private void EmitVariantReturn(StringBuilder sb, VariantReturnStatement variantRet)
    {
        TypeInfo returnType = _currentEmittingRoutine!.ReturnType!;

        // Passthrough: Value already has the variant carrier type — emit and return as-is.
        if (variantRet.SiteKind == VariantSiteKind.FromVariantPassthrough)
        {
            string passVal = EmitExpression(sb: sb, expr: variantRet.Value!);
            EmitRcRecordCleanup(sb: sb);
            EmitEntityCleanup(sb: sb, returnedVarName: null);
            if (_traceCurrentRoutine)
                EmitLine(sb: sb, line: TracePop);
            EmitLine(sb: sb, line: $"  ret {GetLlvmType(type: returnType)} {passVal}");
            return;
        }

        TypeInfo innerType = (variantRet.VariantKind == ErrorHandlingVariantKind.Try
                              || IsCarrierType(type: returnType))
            ? returnType.TypeArguments![index: 0]
            : returnType;

        switch (variantRet.VariantKind)
        {
            case ErrorHandlingVariantKind.Try:
                EmitTryVariantReturn(sb: sb, variantRet: variantRet, innerType: innerType);
                break;
            case ErrorHandlingVariantKind.TryBool:
                EmitTryBoolVariantReturn(sb: sb, variantRet: variantRet);
                break;
            case ErrorHandlingVariantKind.Lookup:
                EmitLookupVariantReturn(sb: sb, variantRet: variantRet, innerType: innerType);
                break;
            case ErrorHandlingVariantKind.Check:
                EmitCheckVariantReturn(sb: sb, variantRet: variantRet, innerType: innerType);
                break;
            default:
                throw new InvalidOperationException(
                    message: $"Unhandled VariantReturnStatement: {variantRet.VariantKind}/{variantRet.SiteKind}");
        }
    }

    private void EmitTryVariantReturn(StringBuilder sb, VariantReturnStatement variantRet, TypeInfo innerType)
    {
        string carrier = GetMaybeCarrierLlvmType(valueType: innerType);
        switch (variantRet.SiteKind)
        {
            case VariantSiteKind.FromThrow:
            {
                if (variantRet.Value != null)
                {
                    TypeInfo? errType = GetExpressionType(expr: variantRet.Value);
                    bool isEmptyRec = errType is null or RecordTypeInfo { MemberVariables.Count: 0 }
                        or CrashableTypeInfo { MemberVariables.Count: 0 };
                    if (!isEmptyRec)
                        EmitExpression(sb: sb, expr: variantRet.Value);
                }
                EmitRcRecordCleanup(sb: sb);
                if (_traceCurrentRoutine)
                    EmitLine(sb: sb, line: TracePop);
                EmitLine(sb: sb, line: $"  ret {carrier} zeroinitializer");
                break;
            }
            case VariantSiteKind.FromAbsent:
                EmitRcRecordCleanup(sb: sb);
                if (_traceCurrentRoutine)
                    EmitLine(sb: sb, line: TracePop);
                EmitLine(sb: sb, line: $"  ret {carrier} zeroinitializer");
                break;
            case VariantSiteKind.FromReturn:
                EmitTryFromReturn(sb: sb, variantRet: variantRet, innerType: innerType, carrier: carrier);
                break;
            default:
                throw new InvalidOperationException(
                    message: $"Unhandled SiteKind for Try variant: {variantRet.SiteKind}");
        }
    }

    // "return ErrorType(...)" in a Try variant is absent (error dropped); bare return is present.
    private void EmitTryFromReturn(StringBuilder sb, VariantReturnStatement variantRet,
        TypeInfo innerType, string carrier)
    {
        TypeInfo? retType = variantRet.Value != null ? GetExpressionType(expr: variantRet.Value) : null;
        bool isCrashable = retType is CrashableTypeInfo;
        string? returnedVarName = variantRet.Value is IdentifierExpression tryId &&
                                  _localEntityVars.Any(predicate: e => e.Name == tryId.Name)
            ? tryId.Name
            : null;
        EmitRcRecordCleanup(sb: sb);
        EmitEntityCleanup(sb: sb, returnedVarName: returnedVarName);
        if (_traceCurrentRoutine)
            EmitLine(sb: sb, line: TracePop);

        bool isBlank = variantRet.Value is IdentifierExpression { Name: "Blank" };

        if (isCrashable)
        {
            EmitExpression(sb: sb, expr: variantRet.Value!);
            EmitLine(sb: sb, line: $"  ret {carrier} zeroinitializer");
        }
        else if (variantRet.Value == null || isBlank)
        {
            // Maybe `present` (field 0) is a Bool, stored as i8.
            string v0 = NextTemp();
            EmitLine(sb: sb, line: $"  {v0} = insertvalue {carrier} zeroinitializer, i8 1, 0");
            EmitLine(sb: sb, line: $"  ret {carrier} {v0}");
        }
        else
        {
            string value = EmitExpression(sb: sb, expr: variantRet.Value);
            string innerLlvm = innerType is EntityTypeInfo ? "ptr" : GetLlvmType(type: innerType);
            // Maybe `present` (field 0) is a Bool, stored as i8.
            string v0 = NextTemp();
            EmitLine(sb: sb, line: $"  {v0} = insertvalue {carrier} zeroinitializer, i8 1, 0");
            string v1 = NextTemp();
            EmitLine(sb: sb, line: $"  {v1} = insertvalue {carrier} {v0}, {innerLlvm} {value}, 1");
            EmitLine(sb: sb, line: $"  ret {carrier} {v1}");
        }
    }

    private void EmitTryBoolVariantReturn(StringBuilder sb, VariantReturnStatement variantRet)
    {
        switch (variantRet.SiteKind)
        {
            case VariantSiteKind.FromThrow:
            {
                if (variantRet.Value != null)
                {
                    TypeInfo? errType = GetExpressionType(expr: variantRet.Value);
                    bool isEmptyRec = errType is RecordTypeInfo { MemberVariables.Count: 0 }
                        or CrashableTypeInfo { MemberVariables.Count: 0 };
                    if (!isEmptyRec)
                        EmitExpression(sb: sb, expr: variantRet.Value);
                }
                EmitRcRecordCleanup(sb: sb);
                if (_traceCurrentRoutine)
                    EmitLine(sb: sb, line: TracePop);
                EmitLine(sb: sb, line: "  ret i1 false");
                break;
            }
            case VariantSiteKind.FromAbsent:
                EmitRcRecordCleanup(sb: sb);
                if (_traceCurrentRoutine)
                    EmitLine(sb: sb, line: TracePop);
                EmitLine(sb: sb, line: "  ret i1 false");
                break;
            case VariantSiteKind.FromReturn:
                EmitRcRecordCleanup(sb: sb);
                EmitEntityCleanup(sb: sb, returnedVarName: null);
                if (_traceCurrentRoutine)
                    EmitLine(sb: sb, line: TracePop);
                EmitLine(sb: sb, line: "  ret i1 true");
                break;
            default:
                throw new InvalidOperationException(
                    message: $"Unhandled SiteKind for TryBool variant: {variantRet.SiteKind}");
        }
    }

    private void EmitLookupVariantReturn(StringBuilder sb, VariantReturnStatement variantRet, TypeInfo innerType)
    {
        string carrier = GetLookupCarrierLlvmType(valueType: innerType);
        switch (variantRet.SiteKind)
        {
            case VariantSiteKind.FromThrow:
                EmitErrorCarrierReturn(sb: sb, variantRet: variantRet, innerType: innerType, carrier: carrier);
                break;
            case VariantSiteKind.FromAbsent:
                EmitRcRecordCleanup(sb: sb);
                if (_traceCurrentRoutine)
                    EmitLine(sb: sb, line: TracePop);
                EmitLine(sb: sb, line: $"  ret {carrier} zeroinitializer");
                break;
            case VariantSiteKind.FromReturn:
                EmitResultCarrierFromReturn(sb: sb, variantRet: variantRet, innerType: innerType, carrier: carrier);
                break;
            default:
                throw new InvalidOperationException(
                    message: $"Unhandled SiteKind for Lookup variant: {variantRet.SiteKind}");
        }
    }

    private void EmitCheckVariantReturn(StringBuilder sb, VariantReturnStatement variantRet, TypeInfo innerType)
    {
        string carrier = GetResultCarrierLlvmType(valueType: innerType);
        switch (variantRet.SiteKind)
        {
            case VariantSiteKind.FromThrow:
                EmitErrorCarrierReturn(sb: sb, variantRet: variantRet, innerType: innerType, carrier: carrier);
                break;
            case VariantSiteKind.FromAbsent:
                EmitRcRecordCleanup(sb: sb);
                if (_traceCurrentRoutine)
                    EmitLine(sb: sb, line: TracePop);
                EmitLine(sb: sb, line: $"  ret {carrier} zeroinitializer");
                break;
            case VariantSiteKind.FromReturn:
                EmitResultCarrierFromReturn(sb: sb, variantRet: variantRet, innerType: innerType, carrier: carrier);
                break;
            default:
                throw new InvalidOperationException(
                    message: $"Unhandled SiteKind for Check variant: {variantRet.SiteKind}");
        }
    }

    // Shared by Lookup.FromThrow and Check.FromThrow — only the carrier type string differs.
    private void EmitErrorCarrierReturn(StringBuilder sb, VariantReturnStatement variantRet,
        TypeInfo innerType, string carrier)
    {
        TypeInfo? errType = variantRet.Value != null ? GetExpressionType(expr: variantRet.Value) : null;
        bool isEmptyRec = errType is null or RecordTypeInfo { MemberVariables.Count: 0 }
            or CrashableTypeInfo { MemberVariables.Count: 0 };
        string? errorVal = isEmptyRec || variantRet.Value == null
            ? null
            : EmitExpression(sb: sb, expr: variantRet.Value!);

        ulong errTypeId = errType != null ? TypeIdHelper.ComputeTypeId(fullName: errType.FullName) : 0;

        EmitRcRecordCleanup(sb: sb);
        if (_traceCurrentRoutine)
            EmitLine(sb: sb, line: TracePop);

        EmitInlineCarrierReturn(sb: sb, carrier: carrier, typeId: errTypeId,
            payloadType: errType, payloadVal: errorVal);
    }

    // Shared by Lookup.FromReturn and Check.FromReturn — only the carrier type string differs.
    private void EmitResultCarrierFromReturn(StringBuilder sb, VariantReturnStatement variantRet,
        TypeInfo innerType, string carrier)
    {
        TypeInfo? retType = variantRet.Value != null ? GetExpressionType(expr: variantRet.Value) : null;
        string? returnedVarName = variantRet.Value is IdentifierExpression retId &&
                                  _localEntityVars.Any(predicate: e => e.Name == retId.Name)
            ? retId.Name
            : null;
        bool isBlank = variantRet.Value is IdentifierExpression { Name: "Blank" };

        if (variantRet.Value == null || isBlank)
        {
            EmitRcRecordCleanup(sb: sb);
            EmitEntityCleanup(sb: sb, returnedVarName: returnedVarName);
            if (_traceCurrentRoutine)
                EmitLine(sb: sb, line: TracePop);
            EmitLine(sb: sb, line: $"  ret {carrier} zeroinitializer");
            return;
        }

        string value = EmitExpression(sb: sb, expr: variantRet.Value);
        bool isCrashable = retType is CrashableTypeInfo;
        TypeInfo payloadType = isCrashable ? retType! : innerType;
        ulong typeId = TypeIdHelper.ComputeTypeId(
            fullName: (isCrashable ? retType! : innerType).FullName);

        EmitRcRecordCleanup(sb: sb);
        EmitEntityCleanup(sb: sb, returnedVarName: returnedVarName);
        if (_traceCurrentRoutine)
            EmitLine(sb: sb, line: TracePop);

        EmitInlineCarrierReturn(sb: sb, carrier: carrier, typeId: typeId,
            payloadType: payloadType, payloadVal: value);
    }

    // Builds an inline-payload carrier ({ i64 type_id, [P x i8] payload }) and emits the ret.
    // payloadVal == null OR payloadType == null OR empty record => skip payload store; rely on
    // zeroinitializer for those bytes.
    private void EmitInlineCarrierReturn(StringBuilder sb, string carrier, ulong typeId,
        TypeInfo? payloadType, string? payloadVal)
    {
        string carrierSlot = NextTemp();
        EmitLine(sb: sb, line: $"  {carrierSlot} = alloca {carrier}");
        EmitLine(sb: sb,
            line: $"  store {carrier} zeroinitializer, ptr {carrierSlot}");

        string tagPtr = NextTemp();
        EmitLine(sb: sb,
            line: $"  {tagPtr} = getelementptr {carrier}, ptr {carrierSlot}, i32 0, i32 0");
        EmitLine(sb: sb, line: $"  store i64 {typeId}, ptr {tagPtr}");

        // Empty payload = no value to store (Blank, empty crashable) — distinguished from
        // primitive @llvm-annotated records (S64, U64, Bool) which have no member variables
        // but DO carry a value via their BackendType.
        bool isEmptyRecord = payloadType is RecordTypeInfo
            { MemberVariables.Count: 0, HasDirectBackendType: false };
        bool isEmptyCrashable = payloadType is CrashableTypeInfo { MemberVariables.Count: 0 };
        bool hasPayload = payloadVal != null && payloadType != null
            && !isEmptyRecord && !isEmptyCrashable;

        if (hasPayload)
        {
            string payloadPtr = NextTemp();
            EmitLine(sb: sb,
                line: $"  {payloadPtr} = getelementptr {carrier}, ptr {carrierSlot}, i32 0, i32 1");
            string storeType = payloadType is EntityTypeInfo or CrashableTypeInfo
                ? "ptr"
                : GetLlvmType(type: payloadType!);
            EmitLine(sb: sb, line: $"  store {storeType} {payloadVal}, ptr {payloadPtr}");
        }

        string loaded = NextTemp();
        EmitLine(sb: sb, line: $"  {loaded} = load {carrier}, ptr {carrierSlot}");
        EmitLine(sb: sb, line: $"  ret {carrier} {loaded}");
    }

    private void EmitThrow(StringBuilder sb, ThrowStatement throwStmt)
    {
        TypeInfo? errorType = GetExpressionType(expr: throwStmt.Error);
        string typeName = errorType?.Name ?? "UnknownError";

        bool isEmptyRecord = errorType is RecordTypeInfo { MemberVariables.Count: 0 };
        string errorVal;
        if (isEmptyRecord)
        {
            errorVal = "zeroinitializer";
        }
        else
        {
            errorVal = EmitExpression(sb: sb, expr: throwStmt.Error);
        }

        string dataPtr = "null";
        string msgLen = "0";
        ResolvedMemberRoutine? resolvedCrash = errorType != null
            ? ResolveMemberRoutine(receiverType: errorType, methodName: "crash_message")
            : null;
        if (resolvedCrash != null)
        {
            GenerateRoutineDeclaration(routine: resolvedCrash.Routine);
            string mangledCrash = resolvedCrash.MangledName;
            string llvmReceiverType = GetLlvmType(type: errorType!);

            // Text is `record Text { data: Hijacked[Character], count: U64, ctrl: ... }` —
            // returned by value as %Record.Text. Extract field 0 (codepoint ptr) and
            // field 1 (count) directly from the SSA struct.
            string textVal = NextTemp();
            EmitLine(sb: sb,
                line: $"  {textVal} = call %Record.Core.Text @{mangledCrash}({llvmReceiverType} {errorVal})");
            dataPtr = NextTemp();
            EmitLine(sb: sb,
                line: $"  {dataPtr} = extractvalue %Record.Core.Text {textVal}, 0");
            msgLen = NextTemp();
            EmitLine(sb: sb,
                line: $"  {msgLen} = extractvalue %Record.Core.Text {textVal}, 1");
        }

        string typeCStr = EmitCStringConstant(value: typeName);
        string fileCStr = EmitCStringConstant(value: throwStmt.Location.FileName);
        string typeNameAsInt = NextTemp();
        EmitLine(sb: sb, line: $"  {typeNameAsInt} = ptrtoint ptr {typeCStr} to i64");
        string fileAsInt = NextTemp();
        EmitLine(sb: sb, line: $"  {fileAsInt} = ptrtoint ptr {fileCStr} to i64");

        string msgDataAsInt;
        if (dataPtr == "null")
        {
            msgDataAsInt = "0";
        }
        else
        {
            msgDataAsInt = NextTemp();
            EmitLine(sb: sb, line: $"  {msgDataAsInt} = ptrtoint ptr {dataPtr} to i64");
        }

        EmitRcRecordCleanup(sb: sb);

        EmitLine(sb: sb,
            line:
            $"  call void @rf_crash(i64 {typeNameAsInt}, i64 {typeName.Length}, i64 {fileAsInt}, i64 {throwStmt.Location.FileName.Length}, i32 {throwStmt.Location.Line}, i32 {throwStmt.Location.Column}, i64 {msgDataAsInt}, i64 {msgLen})");
        EmitLine(sb: sb, line: "  unreachable");
    }

    private void EmitAbsent(StringBuilder sb, AbsentStatement absentStmt)
    {
        if (_currentRoutineIsFailable)
        {
            // The original failable routine (not a try_/check_/lookup_ variant) treats `absent`
            // as a runtime crash with `AbsentValueError`. Use the same rf_crash shape as
            // EmitThrow so the error message + location aren't blank in the trace.
            // type name + filename go through @rf_crash as cstr (i64 = byte-data pointer + length).
            // The message goes as a UTF-32 codepoint buffer (Text data layout), so we load
            // field 0 (codepoint ptr) and field 1 (codepoint count) from a Text-formatted
            // string constant — same shape EmitThrow extracts from crash_message().
            const string typeName = "AbsentValueError";
            string message =
                $"Routine '{_currentEmittingRoutine?.BaseName ?? "<unknown>"}' signaled absent.";
            string typeCStr = EmitCStringConstant(value: typeName);
            string fileCStr = EmitCStringConstant(value: absentStmt.Location.FileName);
            string msgTextPtr = EmitStringLiteralGlobal(value: message);

            string typeNameAsInt = NextTemp();
            EmitLine(sb: sb, line: $"  {typeNameAsInt} = ptrtoint ptr {typeCStr} to i64");
            string fileAsInt = NextTemp();
            EmitLine(sb: sb, line: $"  {fileAsInt} = ptrtoint ptr {fileCStr} to i64");
            // Extract codepoint buffer + count from the Text-shaped global.
            string msgDataPtr = NextTemp();
            EmitLine(sb: sb, line: $"  {msgDataPtr} = load ptr, ptr {msgTextPtr}");
            string msgCountField = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {msgCountField} = getelementptr {{ptr, i64}}, ptr {msgTextPtr}, i32 0, i32 1");
            string msgCount = NextTemp();
            EmitLine(sb: sb, line: $"  {msgCount} = load i64, ptr {msgCountField}");
            string msgAsInt = NextTemp();
            EmitLine(sb: sb, line: $"  {msgAsInt} = ptrtoint ptr {msgDataPtr} to i64");

            EmitRcRecordCleanup(sb: sb);
            EmitLine(sb: sb,
                line:
                $"  call void @rf_crash(i64 {typeNameAsInt}, i64 {typeName.Length}, i64 {fileAsInt}, i64 {absentStmt.Location.FileName.Length}, i32 {absentStmt.Location.Line}, i32 {absentStmt.Location.Column}, i64 {msgAsInt}, i64 {msgCount})");
            EmitLine(sb: sb, line: "  unreachable");
            return;
        }

        TypeInfo absentRetType = _currentEmittingRoutine!.ReturnType!;
        string absentCarrierType = GetLlvmType(type: absentRetType);
        EmitRcRecordCleanup(sb: sb);
        // Balance the routine-entry trace_push. Missing this leaks a frame on the shadow stack
        // every time a `try_X` variant returns absent (which happens at every for-loop exit).
        // Subsequent `_rf_trace_update_loc` calls in the caller then update the leaked frame's
        // slot instead of the caller's, corrupting the stack trace.
        if (_traceCurrentRoutine)
            EmitLine(sb: sb, line: TracePop);
        EmitLine(sb: sb, line: $"  ret {absentCarrierType} zeroinitializer");
    }

    #endregion
}
