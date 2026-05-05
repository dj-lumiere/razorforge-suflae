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
            EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");

        TypeInfo? exprType = GetExpressionType(expr: ret.Value);
        if (IsMaybeType(type: retType) && value != "zeroinitializer" &&
            (exprType == null || !IsMaybeType(type: exprType)))
        {
            EmitMaybeWrappedReturn(sb: sb, retType: retType, innerValue: value);
            return;
        }
        EmitLine(sb: sb, line: $"  ret {llvmType} {value}");
    }

    private void EmitNullValueReturn(StringBuilder sb)
    {
        EmitRcRecordCleanup(sb: sb);
        EmitEntityCleanup(sb: sb, returnedVarName: null);
        if (_traceCurrentRoutine)
            EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");
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
            EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");
        if (_currentEmittingRoutine?.AsyncStatus == AsyncStatus.CheckVariant &&
            _currentRoutineReturnType != null)
        {
            string carrier = GetResultCarrierLlvmType(valueType: _currentRoutineReturnType);
            EmitLine(sb: sb, line: $"  ret {carrier} zeroinitializer");
        }
        else if (_currentEmittingRoutine?.AsyncStatus == AsyncStatus.TryBoolVariant)
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
        string v0 = NextTemp();
        EmitLine(sb: sb, line: $"  {v0} = insertvalue {carrierType} zeroinitializer, i1 1, 0");
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
        foreach ((string name, string llvmAddr) in _localEntityVars)
        {
            if (name == returnedVarName)
            {
                continue;
            }

            string loaded = NextTemp();
            EmitLine(sb: sb, line: $"  {loaded} = load ptr, ptr {llvmAddr}");
            string asInt = NextTemp();
            EmitLine(sb: sb, line: $"  {asInt} = ptrtoint ptr {loaded} to i64");
            EmitLine(sb: sb, line: $"  call void @rf_invalidate(i64 {asInt})");
        }
    }

    #endregion

    #region Throw / Absent / Becomes

    private void EmitVariantReturn(StringBuilder sb, VariantReturnStatement variantRet)
    {
        TypeInfo returnType = _currentEmittingRoutine!.ReturnType!;
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
                    EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");
                EmitLine(sb: sb, line: $"  ret {carrier} zeroinitializer");
                break;
            }
            case VariantSiteKind.FromAbsent:
                EmitRcRecordCleanup(sb: sb);
                if (_traceCurrentRoutine)
                    EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");
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
            EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");

        bool isBlank = variantRet.Value is IdentifierExpression { Name: "Blank" };

        if (isCrashable)
        {
            EmitExpression(sb: sb, expr: variantRet.Value!);
            EmitLine(sb: sb, line: $"  ret {carrier} zeroinitializer");
        }
        else if (variantRet.Value == null || isBlank)
        {
            string v0 = NextTemp();
            EmitLine(sb: sb, line: $"  {v0} = insertvalue {carrier} zeroinitializer, i1 1, 0");
            EmitLine(sb: sb, line: $"  ret {carrier} {v0}");
        }
        else
        {
            string value = EmitExpression(sb: sb, expr: variantRet.Value);
            string innerLlvm = innerType is EntityTypeInfo ? "ptr" : GetLlvmType(type: innerType);
            string v0 = NextTemp();
            EmitLine(sb: sb, line: $"  {v0} = insertvalue {carrier} zeroinitializer, i1 1, 0");
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
                    EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");
                EmitLine(sb: sb, line: "  ret i1 false");
                break;
            }
            case VariantSiteKind.FromAbsent:
                EmitRcRecordCleanup(sb: sb);
                if (_traceCurrentRoutine)
                    EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");
                EmitLine(sb: sb, line: "  ret i1 false");
                break;
            case VariantSiteKind.FromReturn:
                EmitRcRecordCleanup(sb: sb);
                EmitEntityCleanup(sb: sb, returnedVarName: null);
                if (_traceCurrentRoutine)
                    EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");
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
                    EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");
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
                    EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");
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
        string errorVal = isEmptyRec || variantRet.Value == null
            ? "zeroinitializer"
            : EmitExpression(sb: sb, expr: variantRet.Value!);

        ulong errTypeId = errType != null ? TypeIdHelper.ComputeTypeId(fullName: errType.FullName) : 0;
        string errDataAddr = EmitErrorDataAddress(sb: sb, errorType: errType ?? innerType,
            errorVal: errorVal, isEmptyRecord: isEmptyRec);

        EmitRcRecordCleanup(sb: sb);
        if (_traceCurrentRoutine)
            EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");

        string v0 = NextTemp();
        EmitLine(sb: sb, line: $"  {v0} = insertvalue {carrier} zeroinitializer, i64 {errTypeId}, 0");
        string v1 = NextTemp();
        EmitLine(sb: sb, line: $"  {v1} = insertvalue {carrier} {v0}, i64 {errDataAddr}, 1");
        EmitLine(sb: sb, line: $"  ret {carrier} {v1}");
    }

    // Shared by Lookup.FromReturn and Check.FromReturn — only the carrier type string differs.
    private void EmitResultCarrierFromReturn(StringBuilder sb, VariantReturnStatement variantRet,
        TypeInfo innerType, string carrier)
    {
        TypeInfo? retType = variantRet.Value != null ? GetExpressionType(expr: variantRet.Value) : null;
        bool isCrashable = retType is CrashableTypeInfo;
        string? returnedVarName = variantRet.Value is IdentifierExpression retId &&
                                  _localEntityVars.Any(predicate: e => e.Name == retId.Name)
            ? retId.Name
            : null;
        EmitRcRecordCleanup(sb: sb);
        EmitEntityCleanup(sb: sb, returnedVarName: returnedVarName);
        if (_traceCurrentRoutine)
            EmitLine(sb: sb, line: "  call void @_rf_trace_pop()");

        bool isBlank = variantRet.Value is IdentifierExpression { Name: "Blank" };

        if (isCrashable)
        {
            string errorVal = EmitExpression(sb: sb, expr: variantRet.Value!);
            ulong errTypeId = TypeIdHelper.ComputeTypeId(fullName: retType!.FullName);
            string errDataAddr = EmitErrorDataAddress(sb: sb, errorType: retType,
                errorVal: errorVal, isEmptyRecord: false);
            string v0 = NextTemp();
            EmitLine(sb: sb, line: $"  {v0} = insertvalue {carrier} zeroinitializer, i64 {errTypeId}, 0");
            string v1 = NextTemp();
            EmitLine(sb: sb, line: $"  {v1} = insertvalue {carrier} {v0}, i64 {errDataAddr}, 1");
            EmitLine(sb: sb, line: $"  ret {carrier} {v1}");
        }
        else if (variantRet.Value == null || isBlank)
        {
            EmitLine(sb: sb, line: $"  ret {carrier} zeroinitializer");
        }
        else
        {
            string value = EmitExpression(sb: sb, expr: variantRet.Value);
            ulong validId = TypeIdHelper.ComputeTypeId(fullName: innerType.FullName);
            string v0 = NextTemp();
            EmitLine(sb: sb, line: $"  {v0} = insertvalue {carrier} zeroinitializer, i64 {validId}, 0");
            string v1 = NextTemp();
            string dataVal = EmitSuccessDataAddress(sb: sb, innerType: innerType, value: value);
            EmitLine(sb: sb, line: $"  {v1} = insertvalue {carrier} {v0}, i64 {dataVal}, 1");
            EmitLine(sb: sb, line: $"  ret {carrier} {v1}");
        }
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

            string textPtr = NextTemp();
            EmitLine(sb: sb,
                line: $"  {textPtr} = call ptr @{mangledCrash}({llvmReceiverType} {errorVal})");

            string lettersPtr = NextTemp();
            EmitLine(sb: sb, line: $"  {lettersPtr} = load ptr, ptr {textPtr}");
            string dataField = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {dataField} = getelementptr {{ptr, i64, i64}}, ptr {lettersPtr}, i32 0, i32 0");
            dataPtr = NextTemp();
            EmitLine(sb: sb, line: $"  {dataPtr} = load ptr, ptr {dataField}");
            string countField = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {countField} = getelementptr {{ptr, i64, i64}}, ptr {lettersPtr}, i32 0, i32 1");
            msgLen = NextTemp();
            EmitLine(sb: sb, line: $"  {msgLen} = load i64, ptr {countField}");
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

    private string EmitSuccessDataAddress(StringBuilder sb, TypeInfo innerType, string value)
    {
        if (innerType is EntityTypeInfo)
        {
            string asInt = NextTemp();
            EmitLine(sb: sb, line: $"  {asInt} = ptrtoint ptr {value} to i64");
            return asInt;
        }

        string innerLlvm = GetLlvmType(type: innerType);
        if (innerLlvm == "i64") return value;

        // Pointer-typed values (wrapper types like Owned[T], Retained[T], etc.) must use
        // ptrtoint, not zext -- zext is only valid for integer types.
        if (innerLlvm == "ptr")
        {
            string asInt = NextTemp();
            EmitLine(sb: sb, line: $"  {asInt} = ptrtoint ptr {value} to i64");
            return asInt;
        }

        bool needsHeapAlloc = innerLlvm.StartsWith(value: '%')
                              || innerLlvm.StartsWith(value: '{')
                              || innerLlvm is "i128" or "fp128";
        if (needsHeapAlloc)
        {
            string sizePtr = NextTemp();
            string sizeVal = NextTemp();
            EmitLine(sb: sb, line: $"  {sizePtr} = getelementptr {innerLlvm}, ptr null, i32 1");
            EmitLine(sb: sb, line: $"  {sizeVal} = ptrtoint ptr {sizePtr} to i64");
            string heapPtr = NextTemp();
            EmitLine(sb: sb, line: $"  {heapPtr} = call ptr @rf_allocate_dynamic(i64 {sizeVal})");
            EmitLine(sb: sb, line: $"  store {innerLlvm} {value}, ptr {heapPtr}");
            string asInt = NextTemp();
            EmitLine(sb: sb, line: $"  {asInt} = ptrtoint ptr {heapPtr} to i64");
            return asInt;
        }

        if (innerLlvm is "double" or "float" or "half")
        {
            int bits = innerLlvm switch { "double" => 64, "float" => 32, _ => 16 };
            string intType = $"i{bits}";
            string bc = NextTemp();
            EmitLine(sb: sb, line: $"  {bc} = bitcast {innerLlvm} {value} to {intType}");
            if (bits == 64) return bc;
            string ze = NextTemp();
            EmitLine(sb: sb, line: $"  {ze} = zext {intType} {bc} to i64");
            return ze;
        }

        string zext = NextTemp();
        EmitLine(sb: sb, line: $"  {zext} = zext {innerLlvm} {value} to i64");
        return zext;
    }

    private string EmitErrorDataAddress(StringBuilder sb, TypeInfo errorType, string errorVal,
        bool isEmptyRecord)
    {
        if (isEmptyRecord)
        {
            return "0";
        }

        if (errorType is EntityTypeInfo or CrashableTypeInfo)
        {
            string addrInt = NextTemp();
            EmitLine(sb: sb, line: $"  {addrInt} = ptrtoint ptr {errorVal} to i64");
            return addrInt;
        }

        string llvmErrType = GetLlvmType(type: errorType);

        string sizePtr = NextTemp();
        string sizeVal = NextTemp();
        EmitLine(sb: sb, line: $"  {sizePtr} = getelementptr {llvmErrType}, ptr null, i32 1");
        EmitLine(sb: sb, line: $"  {sizeVal} = ptrtoint ptr {sizePtr} to i64");

        string heapPtr = NextTemp();
        EmitLine(sb: sb, line: $"  {heapPtr} = call ptr @rf_allocate_dynamic(i64 {sizeVal})");
        EmitLine(sb: sb, line: $"  store {llvmErrType} {errorVal}, ptr {heapPtr}");

        string addrInt2 = NextTemp();
        EmitLine(sb: sb, line: $"  {addrInt2} = ptrtoint ptr {heapPtr} to i64");
        return addrInt2;
    }

    private void EmitAbsent(StringBuilder sb)
    {
        if (_currentRoutineIsFailable)
        {
            EmitRcRecordCleanup(sb: sb);
            EmitLine(sb: sb,
                line: "  call void @rf_crash(i64 0, i64 0, i64 0, i64 0, i32 0, i32 0, i64 0, i64 0)");
            EmitLine(sb: sb, line: "  unreachable");
            return;
        }

        TypeInfo absentRetType = _currentEmittingRoutine!.ReturnType!;
        string absentCarrierType = GetLlvmType(type: absentRetType);
        EmitRcRecordCleanup(sb: sb);
        EmitLine(sb: sb, line: $"  ret {absentCarrierType} zeroinitializer");
    }

    #endregion
}
