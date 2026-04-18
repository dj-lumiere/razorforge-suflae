namespace Compiler.CodeGen;

using System.Text;
using SemanticVerification.Symbols;
using SemanticVerification.Types;
using SyntaxTree;

/// <summary>
/// Expression code generation for conditional and flow-oriented expressions.
/// </summary>
public partial class LLVMCodeGenerator
{
    /// <summary>
    /// Emits a lambda expression as an internal auxiliary function and returns its function pointer.
    /// Only non-capturing lambdas are supported (captures require a closure struct, not yet implemented).
    /// </summary>
    private string EmitLambdaExpression(LambdaExpression lambda)
    {
        if (lambda.ResolvedType is not RoutineTypeInfo routineType)
        {
            throw new InvalidOperationException(
                message: "Lambda expression has no resolved RoutineTypeInfo.");
        }

        string lambdaName = $"\"lambda.{_lambdaCounter++}\"";

        // Build parameter list (use param names from AST, types from resolved RoutineTypeInfo)
        var paramDecls = new List<string>();
        for (int i = 0; i < lambda.Parameters.Count; i++)
        {
            string paramName = lambda.Parameters[index: i].Name;
            TypeInfo paramType = i < routineType.ParameterTypes.Count
                ? routineType.ParameterTypes[index: i]
                : ErrorTypeInfo.Instance;
            string llvmType = GetLLVMType(type: paramType);
            paramDecls.Add(item: $"{llvmType} %{paramName}");
        }

        string retLlvmType = routineType.ReturnType != null
            ? GetLLVMType(type: routineType.ReturnType)
            : "void";
        string paramStr = string.Join(separator: ", ", values: paramDecls);

        // Save current function state
        Dictionary<string, TypeInfo> savedLocals = new(_localVariables);
        Dictionary<string, string> savedLocalLLVM = new(_localVarLLVMNames);
        Dictionary<string, int> savedVarCounts = new(_varNameCounts);
        List<(string Name, string LLVMAddr)> savedEntityVars = new(_localEntityVars);
        List<(string Name, string LLVMAddr, RecordTypeInfo RecordType)> savedRCVars =
            new(_localRCRecordVars);
        List<(string Name, string LLVMAddr, RecordTypeInfo RecordType)> savedRetainedVars =
            new(_localRetainedVars);
        HashSet<string> savedEmittedAllocas = new(_emittedAllocaNames);
        TypeInfo? savedRetType = _currentFunctionReturnType;
        bool savedIsFailable = _currentRoutineIsFailable;
        RoutineInfo? savedRoutine = _currentEmittingRoutine;
        string savedBlock = _currentBlock;
        int savedTempCounter = _tempCounter;
        string savedEntryAllocas = _currentFunctionEntryAllocas.ToString();
        bool savedTrace = _traceCurrentRoutine;

        // Set up clean state for lambda body
        _localVariables.Clear();
        _localVarLLVMNames.Clear();
        _varNameCounts.Clear();
        _localEntityVars.Clear();
        _localRCRecordVars.Clear();
        _localRetainedVars.Clear();
        _emittedAllocaNames.Clear();
        _currentFunctionReturnType = routineType.ReturnType;
        _currentRoutineIsFailable = false;
        _currentEmittingRoutine = null;
        _currentBlock = "entry";
        _currentFunctionEntryAllocas.Clear();
        _traceCurrentRoutine = false;

        // Register lambda parameters as local variables (alloca/store style)
        var bodyBuilder = new StringBuilder();
        for (int i = 0; i < lambda.Parameters.Count; i++)
        {
            string paramName = lambda.Parameters[index: i].Name;
            TypeInfo paramType = i < routineType.ParameterTypes.Count
                ? routineType.ParameterTypes[index: i]
                : ErrorTypeInfo.Instance;
            string llvmType = GetLLVMType(type: paramType);
            string addrName = $"%{paramName}.addr";
            _currentFunctionEntryAllocas.AppendLine(value: $"  {addrName} = alloca {llvmType}, align 8");
            _emittedAllocaNames.Add(item: addrName);
            bodyBuilder.AppendLine(value: $"  store {llvmType} %{paramName}, ptr {addrName}");
            _localVariables[key: paramName] = paramType;
        }

        // Emit body expression
        string resultVal = EmitExpression(sb: bodyBuilder, expr: lambda.Body);
        bodyBuilder.AppendLine(value: $"  ret {retLlvmType} {resultVal}");

        // Emit the lambda function to aux definitions
        EmitLine(sb: _auxFunctionDefinitions,
            line: $"define internal {retLlvmType} @{lambdaName}({paramStr}) {{");
        EmitLine(sb: _auxFunctionDefinitions, line: "entry:");
        _auxFunctionDefinitions.Append(value: _currentFunctionEntryAllocas);
        _auxFunctionDefinitions.Append(value: bodyBuilder);
        EmitLine(sb: _auxFunctionDefinitions, line: "}");
        EmitLine(sb: _auxFunctionDefinitions, line: "");

        // Restore state
        _localVariables.Clear();
        foreach (KeyValuePair<string, TypeInfo> kv in savedLocals)
        {
            _localVariables[key: kv.Key] = kv.Value;
        }

        _localVarLLVMNames.Clear();
        foreach (KeyValuePair<string, string> kv in savedLocalLLVM)
        {
            _localVarLLVMNames[key: kv.Key] = kv.Value;
        }

        _varNameCounts.Clear();
        foreach (KeyValuePair<string, int> kv in savedVarCounts)
        {
            _varNameCounts[key: kv.Key] = kv.Value;
        }

        _localEntityVars.Clear();
        _localEntityVars.AddRange(collection: savedEntityVars);
        _localRCRecordVars.Clear();
        _localRCRecordVars.AddRange(collection: savedRCVars);
        _localRetainedVars.Clear();
        _localRetainedVars.AddRange(collection: savedRetainedVars);

        _emittedAllocaNames.Clear();
        foreach (string name in savedEmittedAllocas)
        {
            _emittedAllocaNames.Add(item: name);
        }

        _currentFunctionReturnType = savedRetType;
        _currentRoutineIsFailable = savedIsFailable;
        _currentEmittingRoutine = savedRoutine;
        _currentBlock = savedBlock;
        _tempCounter = savedTempCounter;
        _currentFunctionEntryAllocas.Clear();
        _currentFunctionEntryAllocas.Append(value: savedEntryAllocas);
        _traceCurrentRoutine = savedTrace;

        return $"@{lambdaName}";
    }

    private string EmitConditional(StringBuilder sb, ConditionalExpression cond)
    {
        string condition = EmitExpression(sb: sb, expr: cond.Condition);

        string thenLabel = NextLabel(prefix: "cond_then");
        string elseLabel = NextLabel(prefix: "cond_else");
        string endLabel = NextLabel(prefix: "cond_end");

        EmitLine(sb: sb, line: $"  br i1 {condition}, label %{thenLabel}, label %{elseLabel}");

        // Then branch
        EmitLine(sb: sb, line: $"{thenLabel}:");
        string thenValue = EmitExpression(sb: sb, expr: cond.TrueExpression);
        EmitLine(sb: sb, line: $"  br label %{endLabel}");

        // Else branch
        EmitLine(sb: sb, line: $"{elseLabel}:");
        string elseValue = EmitExpression(sb: sb, expr: cond.FalseExpression);
        EmitLine(sb: sb, line: $"  br label %{endLabel}");

        // Merge with phi
        EmitLine(sb: sb, line: $"{endLabel}:");
        string result = NextTemp();
        TypeInfo? resultType = GetExpressionType(expr: cond.TrueExpression);
        if (resultType == null)
        {
            throw new InvalidOperationException(
                message: "Cannot determine type for conditional expression");
        }

        string llvmType = GetLLVMType(type: resultType);
        EmitLine(sb: sb,
            line:
            $"  {result} = phi {llvmType} [ {thenValue}, %{thenLabel} ], [ {elseValue}, %{elseLabel} ]");

        return result;
    }

    /// <summary>
    /// Generates code for a range expression.
    /// </summary>
    private string EmitRange(StringBuilder sb, RangeExpression range)
    {
        // Emit start, end, step expressions
        string start = EmitExpression(sb: sb, expr: range.Start);
        string end = EmitExpression(sb: sb, expr: range.End);
        string step = range.Step != null
            ? EmitExpression(sb: sb, expr: range.Step)
            : "1";
        // IsDescending is confusingly named: false = 'to' (inclusive), true = 'til' (exclusive)
        // Range record field 3 is 'inclusive': true for 'to', false for 'til'
        string isInclusive = range.IsDescending
            ? "false"
            : "true";

        // Infer element type from start/end expressions (Range[T] is generic)
        TypeInfo? elemType =
            GetExpressionType(expr: range.Start) ?? GetExpressionType(expr: range.End);
        string elemLlvmType = elemType != null
            ? GetLLVMType(type: elemType)
            : "i64";

        // Try to use registered Range type, resolved with element type
        TypeInfo? rangeGenericDef = _registry.LookupType(name: "Range");
        string structType;
        if (rangeGenericDef != null && elemType != null)
        {
            TypeInfo resolvedRange = _registry.GetOrCreateResolution(genericDef: rangeGenericDef,
                typeArguments: new List<TypeInfo> { elemType });
            structType = resolvedRange is RecordTypeInfo resolvedRecord
                ? GetRecordTypeName(record: resolvedRecord)
                : $"{{ {elemLlvmType}, {elemLlvmType}, {elemLlvmType}, i1 }}";
        }
        else if (rangeGenericDef is RecordTypeInfo rangeRecord)
        {
            structType = GetRecordTypeName(record: rangeRecord);
        }
        else
        {
            structType = $"{{ {elemLlvmType}, {elemLlvmType}, {elemLlvmType}, i1 }}";
        }

        // Build struct via insertvalue chain: { start, end, step, inclusive }
        string v0 = NextTemp();
        EmitLine(sb: sb,
            line: $"  {v0} = insertvalue {structType} zeroinitializer, {elemLlvmType} {start}, 0");
        string v1 = NextTemp();
        EmitLine(sb: sb, line: $"  {v1} = insertvalue {structType} {v0}, {elemLlvmType} {end}, 1");
        string v2 = NextTemp();
        EmitLine(sb: sb,
            line: $"  {v2} = insertvalue {structType} {v1}, {elemLlvmType} {step}, 2");
        string v3 = NextTemp();
        EmitLine(sb: sb, line: $"  {v3} = insertvalue {structType} {v2}, i1 {isInclusive}, 3");

        return v3;
    }

    /// <summary>
    /// Generates code for a tuple literal expression.
    /// Tuples are always inline LLVM structs built via insertvalue chain.
    /// </summary>
    private string EmitTupleLiteral(StringBuilder sb, TupleLiteralExpression tuple)
    {
        // Both ValueTuple and Tuple are inline LLVM structs (value semantics, never heap-allocated).
        // Tuple's entity elements require RC bump on copy / RC decrement on drop, but that
        // copy/drop emission is deferred to RecordCopyLoweringPass once it handles TupleTypeInfo.
        // Evaluate all element expressions
        var elemValues = new List<string>();
        var elemLLVMTypes = new List<string>();
        foreach (Expression element in tuple.Elements)
        {
            elemValues.Add(item: EmitExpression(sb: sb, expr: element));
            TypeInfo? elemType = GetExpressionType(expr: element);
            elemLLVMTypes.Add(item: elemType != null
                ? GetLLVMType(type: elemType)
                : "i64");
        }

        // Resolve tuple type from semantic analysis
        var tupleType = tuple.ResolvedType as TupleTypeInfo;

        string structType;
        if (tupleType != null)
        {
            structType = GetTupleTypeName(tuple: tupleType);
        }
        else
        {
            // Fall back to anonymous struct type
            structType = $"{{ {string.Join(separator: ", ", values: elemLLVMTypes)} }}";
        }

        string result = "zeroinitializer";
        for (int i = 0; i < elemValues.Count; i++)
        {
            string newResult = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {newResult} = insertvalue {structType} {result}, {elemLLVMTypes[index: i]} {elemValues[index: i]}, {i}");
            result = newResult;
        }

        return result;
    }


    /// <summary>
    /// Resolves generic type parameters in a member's type using the owner's type arguments.
    /// Builds a substitution map from the owner and delegates to SubstituteTypeParams.
    /// </summary>
    private TypeInfo ResolveGenericMemberType(TypeInfo memberType, TypeInfo ownerType)
    {
        TypeInfo? ownerGenericDef = ownerType switch
        {
            RecordTypeInfo r => r.GenericDefinition,
            EntityTypeInfo e => e.GenericDefinition,
            _ => null
        };
        if (ownerGenericDef?.GenericParameters == null || ownerType.TypeArguments == null)
        {
            return memberType;
        }

        var subs = new Dictionary<string, TypeInfo>();
        for (int i = 0;
             i < ownerGenericDef.GenericParameters.Count && i < ownerType.TypeArguments.Count;
             i++)
        {
            subs[key: ownerGenericDef.GenericParameters[index: i]] =
                ownerType.TypeArguments[index: i];
        }

        if (subs.Count == 0)
        {
            return memberType;
        }

        return SubstituteTypeParams(type: memberType, substitutions: subs);
    }

    /// <summary>
    /// Emits code for a <see cref="CarrierPayloadExpression"/>: extracts field 1 (the data i64)
    /// from a Result/Lookup carrier and reinterprets it as the concrete type.
    ///
    /// <list type="bullet">
    ///   <item>Entity types: <c>inttoptr i64 → ptr</c> (pointer stored as i64).</item>
    ///   <item>Value types wider than i64: not expected (carrier stores ≤ 64-bit values).</item>
    ///   <item>Value types narrower than i64: truncate from i64 to the target LLVM type.</item>
    ///   <item>i64-sized value types: load directly.</item>
    /// </list>
    /// </summary>
    private string EmitCarrierPayloadExpression(StringBuilder sb,
        CarrierPayloadExpression payload)
    {
        // EmitExpression returns a loaded struct value (not a pointer); GEP needs a pointer.
        // Spill the carrier value to a temp alloca first.
        string carrierVal = EmitExpression(sb: sb, expr: payload.Carrier);

        TypeInfo carrierType = payload.Carrier.ResolvedType!;
        string carrierLlvmType = GetCarrierLLVMType(type: carrierType);

        string spillAddr = NextTemp();
        EmitLine(sb: sb, line: $"  {spillAddr} = alloca {carrierLlvmType}");
        EmitLine(sb: sb, line: $"  store {carrierLlvmType} {carrierVal}, ptr {spillAddr}");

        TypeInfo? concreteType = payload.ResolvedType
            ?? payload.ConcreteType.ResolvedType
            ?? _registry.LookupType(name: payload.ConcreteType.Name);

        // GEP field 1 (the data/address i64) from the carrier struct.
        string dataPtr = NextTemp();
        EmitLine(sb: sb,
            line: $"  {dataPtr} = getelementptr {carrierLlvmType}, ptr {spillAddr}, i32 0, i32 1");
        string dataI64 = NextTemp();
        EmitLine(sb: sb, line: $"  {dataI64} = load i64, ptr {dataPtr}");

        if (concreteType is EntityTypeInfo or CrashableTypeInfo)
        {
            // Entity / crashable: payload is a heap pointer stored as i64 → inttoptr
            string ptrVal = NextTemp();
            EmitLine(sb: sb, line: $"  {ptrVal} = inttoptr i64 {dataI64} to ptr");
            return ptrVal;
        }

        // Value type: zero-extend stored as i64; truncate/bitcast to target type.
        string llvmType = concreteType != null ? GetLLVMType(type: concreteType) : "i64";
        if (llvmType == "i64") return dataI64;

        // If the LLVM type is a pointer (protocol, opaque handle), use inttoptr — not trunc.
        if (llvmType == "ptr")
        {
            string ptrVal2 = NextTemp();
            EmitLine(sb: sb, line: $"  {ptrVal2} = inttoptr i64 {dataI64} to ptr");
            return ptrVal2;
        }

        string truncated = NextTemp();
        EmitLine(sb: sb, line: $"  {truncated} = trunc i64 {dataI64} to {llvmType}");
        return truncated;
    }
}
