using System.Text;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// Expression code generation: allocation, member variable access, method calls, operators.
/// </summary>
public partial class LlvmCodeGenerator
{
    /// <summary>
    /// Emits code for any expression node.
    /// </summary>
    /// <param name="sb">The builder receiving emitted LLVM IR.</param>
    /// <param name="expr">The expression to emit.</param>
    /// <returns>The temporary value name produced for the expression.</returns>
    private string EmitExpression(StringBuilder sb, Expression expr)
    {
        return expr switch
        {
            // TODO: This should be eliminated by lowering pass
            LiteralExpression literal => EmitLiteral(sb: sb, literal: literal),
            IdentifierExpression identifier => EmitIdentifier(sb: sb, identifier: identifier),
            // TODO: This should be eliminated by lowering pass
            MemberExpression memberAccess => EmitMemberVariableAccess(sb: sb, expr: memberAccess),
            CreatorExpression constructor => EmitConstructorCall(sb: sb, expr: constructor),
            CallExpression call => EmitCall(sb: sb, call: call),
            // TODO: This should be eliminated by lowering pass
            BinaryExpression binary => EmitBinaryOp(sb: sb, binary: binary),
            // TODO: This should be eliminated by lowering pass
            UnaryExpression unary => EmitUnaryOp(sb: sb, unary: unary),
            GenericMethodCallExpression gmc => EmitGmceFallback(sb: sb, gmc: gmc),
            // Array[T,N] and BitArray[N] are inline IR constructs (insertvalue); all other
            // collection literals must be lowered to CreatorExpression + add calls before codegen.
            ListLiteralExpression list when IsArrayOrBitArrayLiteral(list.ResolvedType) =>
                EmitListLiteral(sb: sb, list: list),
            // TODO: This should be eliminated by lowering pass
            CarrierPayloadExpression payload => EmitCarrierPayloadExpression(sb: sb,
                payload: payload),
            // Named arguments appear inside synthesized AST bodies (e.g., me.$eq(you: you)).
            // The name is irrelevant to codegen -> just emit the inner value positionally.
            NamedArgumentExpression named => EmitExpression(sb: sb, expr: named.Value),
            _ => throw new NotImplementedException(
                message: $"Expression type not implemented: {expr.GetType().Name}")
        };
    }

    /// <summary>
    /// Generates code for an identifier expression (variable reference).
    /// </summary>
    private string EmitIdentifier(StringBuilder sb, IdentifierExpression identifier)
    {
        // Const generic value: identifier retains its original param name (e.g., "N")
        // Resolve via _typeSubstitutions to get the numeric value
        if (_typeSubstitutions != null &&
            _typeSubstitutions.TryGetValue(key: identifier.Name, value: out TypeInfo? subType) &&
            subType is ConstGenericValueTypeInfo constVal)
        {
            return constVal.Value.ToString();
        }

        // Presets must be inlined before backend entry. Codegen should not read declaration AST
        // to recover their values on demand.
        if (_registry.LookupVariable(name: identifier.Name) is { IsPreset: true })
        {
            throw new InvalidOperationException(
                message:
                $"Preset identifier '{identifier.Name}' reached LLVM codegen. PresetInliningPass must inline presets before backend entry.");
        }

        // Check if this is a module-level global variable
        if (_globalVariables.TryGetValue(key: identifier.Name, value: out TypeInfo? globalType) &&
            _globalVariableLlvmNames.TryGetValue(key: identifier.Name,
                value: out string? globalLlvm))
        {
            string globalLlvmType = GetLlvmType(type: globalType);
            string tmp2 = NextTemp();
            EmitLine(sb: sb, line: $"  {tmp2} = load {globalLlvmType}, ptr {globalLlvm}");
            return tmp2;
        }

        if (identifier.ResolvedType is RoutineTypeInfo routineType && TryResolveRoutineReference(
                name: identifier.Name,
                routineType: routineType,
                routine: out RoutineInfo? routine))
        {
            return $"@{MangleRoutineName(routine)}";
        }

        // Look up the variable in local variables first
        if (!_localVariables.TryGetValue(key: identifier.Name, value: out TypeInfo? varType))
        {
            throw new InvalidOperationException(
                message: $"Unknown identifier '{identifier.Name}'");
        }

        // Variables are stored in allocas (%name.addr), need to load them
        // Use unique LLVM name to handle shadowing
        string llvmName =
            _localVarLlvmNames.TryGetValue(key: identifier.Name, value: out string? unique)
                ? unique
                : identifier.Name;
        string llvmType = GetLlvmType(type: varType);
        string tmp = NextTemp();
        EmitLine(sb: sb, line: $"  {tmp} = load {llvmType}, ptr %{llvmName}.addr");
        return tmp;
    }

    /// <summary>
    /// Attempts to resolve routine reference and reports whether it succeeded.
    /// </summary>
    private bool TryResolveRoutineReference(string name, RoutineTypeInfo routineType,
        out RoutineInfo? routine)
    {
        routine = null;
        string bareName = name.EndsWith(value: '!')
            ? name[..^1]
            : name;
        string? moduleName = _currentEmittingRoutine?.OwnerType?.Module ??
                             _currentEmittingRoutine?.Module;

        IReadOnlyList<TypeInfo> paramTypes = routineType.ParameterTypes.ToList();

        if (moduleName != null && !bareName.Contains(value: '.'))
        {
            routine = _registry.LookupRoutineOverload(baseName: $"{moduleName}.{bareName}",
                argTypes: paramTypes);
            routine ??= _registry.LookupRoutine(fullName: $"{moduleName}.{bareName}");
        }

        routine ??= _registry.LookupRoutineOverload(baseName: bareName, argTypes: paramTypes);
        routine ??= _registry.LookupRoutine(fullName: bareName);
        routine ??=
            _registry.LookupRoutineByName(name: bareName, isFailable: routineType.IsFailable);
        return routine != null;
    }

    /// <summary>
    /// Generates code for a function/method call.
    /// Handles both standalone function calls and method calls on objects.
    /// </summary>
    private string EmitCall(StringBuilder sb, CallExpression call)
    {
        // C29: Safety guard -> semantic analyzer already errors on runtime dispatch in RF mode,
        // but if we somehow reach codegen with Runtime dispatch, trap instead of emitting bad code
        if (call.ResolvedDispatch == DispatchStrategy.Runtime)
        {
            EmitLine(sb: sb, line: "  call void @llvm.trap()");
            EmitLine(sb: sb, line: "  unreachable");
            return "undef";
        }

        return call.Callee switch
        {
            // Determine if this is a method call (callee is MemberExpression) or standalone function call
            MemberExpression member => EmitMemberRoutineCall(sb: sb,
                member: member,
                arguments: call.Arguments,
                resolvedRoutine: call.ResolvedRoutine,
                typeArguments: call.TypeArguments,
                loweringKind: call.LoweringKind),
            IdentifierExpression id => EmitRoutineCall(sb: sb,
                req: new RoutineCallRequest(FunctionName: id.Name,
                    Arguments: call.Arguments,
                    ResolvedRoutine: call.ResolvedRoutine,
                    ResolvedReturnType: call.ResolvedType,
                    TypeArguments: call.TypeArguments,
                    LoweringKind: call.LoweringKind,
                    ConstructedType: call.ConstructedType)),
            _ => throw new NotImplementedException(
                message: $"Cannot emit call for callee type: {call.Callee.GetType().Name}")
        };
    }

    /// <summary>
    /// Emits an inline primitive type conversion (trunc/zext/sext/fpcast) for @llvm types.
    /// Used for calls like U8(val), S32(val), F64(val), etc.
    /// </summary>
    private string EmitPrimitiveTypeConversion(StringBuilder sb, string targetTypeName,
        Expression arg, TypeInfo targetType)
    {
        string argValue = EmitExpression(sb: sb, expr: arg);
        TypeInfo? argType = GetExpressionType(expr: arg);
        return EmitBackendScalarCast(sb: sb,
            value: argValue,
            sourceType: argType,
            targetType: targetType);
    }

    /// <summary>
    /// Emit backend scalar cast as part of this compiler phase.
    /// </summary>
    private string EmitBackendScalarCast(StringBuilder sb, string value, TypeInfo? sourceType,
        TypeInfo targetType)
    {
        string targetLlvm = GetLlvmType(type: targetType);
        string sourceLlvm = sourceType != null
            ? GetLlvmType(type: sourceType)
            : targetLlvm;

        if (sourceLlvm == targetLlvm)
        {
            return value;
        }

        if (targetLlvm == "ptr" && sourceLlvm != "ptr")
        {
            string cast = NextTemp();
            EmitLine(sb: sb, line: $"  {cast} = inttoptr {sourceLlvm} {value} to ptr");
            return cast;
        }

        if (targetLlvm != "ptr" && sourceLlvm == "ptr")
        {
            string cast = NextTemp();
            EmitLine(sb: sb, line: $"  {cast} = ptrtoint ptr {value} to {targetLlvm}");
            return cast;
        }

        if (TryGetLlvmIntegerWidth(sourceLlvm, out int sourceIntBits) &&
            TryGetLlvmIntegerWidth(targetLlvm, out int targetIntBits))
        {
            string integerResult = NextTemp();
            if (sourceIntBits > targetIntBits)
            {
                EmitLine(sb: sb,
                    line: $"  {integerResult} = trunc {sourceLlvm} {value} to {targetLlvm}");
            }
            else if (sourceIntBits < targetIntBits)
            {
                string op = IsUnsignedIntegerType(type: targetType)
                    ? "zext"
                    : "sext";
                EmitLine(sb: sb,
                    line: $"  {integerResult} = {op} {sourceLlvm} {value} to {targetLlvm}");
            }
            else
            {
                EmitLine(sb: sb,
                    line: $"  {integerResult} = bitcast {sourceLlvm} {value} to {targetLlvm}");
            }

            return integerResult;
        }

        bool sourceIsFloat = sourceLlvm is "half" or "float" or "double" or "fp128";
        bool targetIsFloat = targetLlvm is "half" or "float" or "double" or "fp128";
        bool targetUnsigned = IsUnsignedIntegerType(type: targetType);

        string result = NextTemp();
        if (sourceIsFloat && targetIsFloat)
        {
            string op = GetTypeBitWidth(llvmType: sourceLlvm) >
                        GetTypeBitWidth(llvmType: targetLlvm)
                ? "fptrunc"
                : "fpext";
            EmitLine(sb: sb, line: $"  {result} = {op} {sourceLlvm} {value} to {targetLlvm}");
        }
        else if (sourceIsFloat)
        {
            string op = targetUnsigned
                ? "fptoui"
                : "fptosi";
            EmitLine(sb: sb, line: $"  {result} = {op} {sourceLlvm} {value} to {targetLlvm}");
        }
        else if (targetIsFloat)
        {
            bool sourceUnsigned = IsUnsignedIntegerType(type: sourceType);
            string op = sourceUnsigned
                ? "uitofp"
                : "sitofp";
            EmitLine(sb: sb, line: $"  {result} = {op} {sourceLlvm} {value} to {targetLlvm}");
        }
        else
        {
            int srcBits = GetTypeBitWidth(llvmType: sourceLlvm);
            int dstBits = GetTypeBitWidth(llvmType: targetLlvm);
            if (srcBits > dstBits)
            {
                EmitLine(sb: sb, line: $"  {result} = trunc {sourceLlvm} {value} to {targetLlvm}");
            }
            else if (srcBits < dstBits)
            {
                string op = targetUnsigned
                    ? "zext"
                    : "sext";
                EmitLine(sb: sb, line: $"  {result} = {op} {sourceLlvm} {value} to {targetLlvm}");
            }
            else
            {
                EmitLine(sb: sb,
                    line: $"  {result} = bitcast {sourceLlvm} {value} to {targetLlvm}");
            }
        }

        return result;
    }

    /// <summary>
    /// Attempts to get LLVM integer width and reports whether it succeeded.
    /// </summary>
    private static bool TryGetLlvmIntegerWidth(string llvmType, out int bitWidth)
    {
        bitWidth = 0;
        if (!llvmType.StartsWith('i') || llvmType.Length < 2)
        {
            return false;
        }

        return int.TryParse(llvmType.AsSpan(start: 1), out bitWidth);
    }

    /// <summary>
    /// Emits a primitive type cast (trunc/zext/sext/bitcast) from one LLVM primitive type to another.
    /// Used when an explicitly typed variable declaration has an initializer of a different type.
    /// </summary>
    private string EmitPrimitiveCast(StringBuilder sb, string value, string fromLlvm,
        string toLlvm)
    {
        if (fromLlvm == toLlvm) return value;

        bool fromIsFloat = fromLlvm is "half" or "float" or "double" or "fp128";
        bool toIsFloat = toLlvm is "half" or "float" or "double" or "fp128";

        string cast = NextTemp();
        if (fromIsFloat && toIsFloat)
        {
            string op = GetTypeBitWidth(llvmType: fromLlvm) > GetTypeBitWidth(llvmType: toLlvm)
                ? "fptrunc"
                : "fpext";
            EmitLine(sb: sb, line: $"  {cast} = {op} {fromLlvm} {value} to {toLlvm}");
        }
        else if (fromIsFloat)
        {
            EmitLine(sb: sb, line: $"  {cast} = fptosi {fromLlvm} {value} to {toLlvm}");
        }
        else if (toIsFloat)
        {
            EmitLine(sb: sb, line: $"  {cast} = sitofp {fromLlvm} {value} to {toLlvm}");
        }
        else
        {
            int srcBits = GetTypeBitWidth(llvmType: fromLlvm);
            int dstBits = GetTypeBitWidth(llvmType: toLlvm);
            string op = "bitcast";
            if (srcBits > dstBits)
            {
                op = "trunc";
            }
            else if (srcBits < dstBits)
            {
                op = "zext";
            }

            EmitLine(sb: sb, line: $"  {cast} = {op} {fromLlvm} {value} to {toLlvm}");
        }

        return cast;
    }

    /// <summary>
    /// Emit binary op as part of this compiler phase.
    /// </summary>
    private string EmitBinaryOp(StringBuilder sb, BinaryExpression binary)
    {
        // TODO: This should be done with member routine, not here
        return binary.Operator switch
        {
            BinaryOperator.And => throw new InvalidOperationException(
                $"BinaryExpression(And) must be lowered to ConditionalExpression by ExpressionLoweringPass before codegen. In routine: {_currentEmittingRoutine?.Name ?? "<unknown>"} (owner: {_currentEmittingRoutine?.OwnerType?.Name ?? "none"})"),
            BinaryOperator.Or => throw new InvalidOperationException(
                $"BinaryExpression(Or) must be lowered to ConditionalExpression by ExpressionLoweringPass before codegen. In routine: {_currentEmittingRoutine?.Name ?? "<unknown>"} (owner: {_currentEmittingRoutine?.OwnerType?.Name ?? "none"})"),
            BinaryOperator.Assign => EmitBinaryAssign(sb: sb, binary: binary),
            BinaryOperator.In => EmitContainsCall(sb: sb, binary: binary, methodName: "$contains"),
            BinaryOperator.NotIn => EmitContainsCall(sb: sb,
                binary: binary,
                methodName: "$notcontains"),
            BinaryOperator.Is => EmitChoiceIs(sb: sb, binary: binary, cmpOp: "eq"),
            BinaryOperator.IsNot => EmitChoiceIs(sb: sb, binary: binary, cmpOp: "ne"),
            BinaryOperator.Obeys => EmitCompileTimeConstant(value: "true"),
            BinaryOperator.Disobeys => EmitCompileTimeConstant(value: "false"),
            _ => throw new InvalidOperationException(
                $"BinaryExpression({binary.Operator}) must be lowered to a wired call before codegen " +
                $"(left={binary.Left.GetType().Name}, loc={binary.Location})")
        };
    }

    /// <summary>
    /// Emit binary assign as part of this compiler phase.
    /// </summary>
    private string EmitBinaryAssign(StringBuilder sb, BinaryExpression binary)
    {
        // TODO: This should be done with member routine, not here
        if (binary.Left is IndexExpression idxLhs)
        {
            EmitIndexAssignment(sb: sb, index: idxLhs, rhs: binary.Right);
            return "undef";
        }

        string value = EmitExpression(sb: sb, expr: binary.Right);

        switch (binary.Left)
        {
            case IdentifierExpression id:
                EmitVariableAssignment(sb: sb, varName: id.Name, value: value);
                break;
            case MemberExpression member:
            {
                EmitMemberVariableAssignment(sb: sb,
                    member: member,
                    value: value,
                    valueType: GetExpressionType(expr: binary.Right));
                if (binary.Right is IdentifierExpression { Name: var srcRcName })
                {
                    _localRetainedVars.RemoveAll(match: e => e.Name == srcRcName);
                }

                break;
            }
            default:
                throw new NotImplementedException(
                    message:
                    $"Assignment target not implemented for expression type: {binary.Left.GetType().Name}");
        }

        return value;
    }

    /// <summary>
    /// Emit contains call as part of this compiler phase.
    /// </summary>
    private string EmitContainsCall(StringBuilder sb, BinaryExpression binary, string methodName)
    {
        // TODO: This should be done with member routine, not here
        string collection = EmitExpression(sb: sb, expr: binary.Right);
        string element = EmitExpression(sb: sb, expr: binary.Left);

        TypeInfo? collectionType = GetExpressionType(expr: binary.Right);
        if (collectionType == null)
        {
            throw new InvalidOperationException(
                message: "Cannot determine collection type for 'in'/'notin' operator");
        }

        ResolvedMemberRoutine? resolved =
            ResolveMemberRoutine(receiverType: collectionType, methodName: methodName);
        string mangledName = resolved?.MangledName ??
                             Q(name:
                                 $"{collectionType.FullName}.{SanitizeLlvmName(name: methodName)}");

        if (resolved != null)
        {
            GenerateRoutineDeclaration(routine: resolved.Routine,
                nameOverride: resolved.MangledName);
        }

        var argValues = new List<string> { collection, element };
        var argTypes = new List<string> { GetParameterLlvmType(type: collectionType) };

        argTypes.Add(item: GetExpressionLlvmType(expr: binary.Left));

        string result = NextTemp();
        string args = BuildCallArgs(types: argTypes, values: argValues);
        EmitLine(sb: sb, line: $"  {result} = call i1 @{mangledName}({args})");
        return result;
    }

    /// <summary>
    /// Emit choice is as part of this compiler phase.
    /// </summary>
    private string EmitChoiceIs(StringBuilder sb, BinaryExpression binary, string cmpOp)
    {
        string left = EmitExpression(sb: sb, expr: binary.Left);
        string right = EmitExpression(sb: sb, expr: binary.Right);
        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = icmp {cmpOp} i32 {left}, {right}");
        return result;
    }

    /// <summary>
    /// Emit compile time constant as part of this compiler phase.
    /// </summary>
    private static string EmitCompileTimeConstant(string value)
    {
        return value;
    }

    /// <summary>
    /// Emit unary op as part of this compiler phase.
    /// </summary>
    private string EmitUnaryOp(StringBuilder sb, UnaryExpression unary)
    {
        // TODO: This should be done with member routine, not here
        return unary.Operator switch
        {
            UnaryOperator.Not => throw new InvalidOperationException(
                $"UnaryExpression(Not) must be lowered to ConditionalExpression by ExpressionLoweringPass before codegen. Routine: {_currentEmittingRoutine?.Name ?? "<unknown>"} (owner: {_currentEmittingRoutine?.OwnerType?.Name ?? "none"})"),
            UnaryOperator.Steal => EmitExpression(sb: sb, expr: unary.Operand),
            _ => throw new InvalidOperationException(
                $"UnaryExpression({unary.Operator}) must be lowered to a wired call before codegen")
        };
    }

    // -----------------------------------------------------------------------------

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
    /// <item>Entity types: <c>inttoptr i64 -> ptr</c> (pointer stored as i64).</item>
    /// <item>Value types wider than i64: not expected (carrier stores -> 64-bit values).</item>
    /// <item>Value types narrower than i64: truncate from i64 to the target LLVM type.</item>
    /// <item>i64-sized value types: load directly.</item>
    /// </list>
    /// </summary>
    private string EmitCarrierPayloadExpression(StringBuilder sb, CarrierPayloadExpression payload)
    {
        // EmitExpression returns a loaded struct value (not a pointer); GEP needs a pointer.
        // Spill the carrier value to a temp alloca first.
        string carrierVal = EmitExpression(sb: sb, expr: payload.Carrier);

        TypeInfo carrierType = payload.Carrier.ResolvedType!;
        string carrierLlvmType = GetCarrierLlvmType(type: carrierType);

        string spillAddr = NextTemp();
        EmitLine(sb: sb, line: $"  {spillAddr} = alloca {carrierLlvmType}");
        EmitLine(sb: sb, line: $"  store {carrierLlvmType} {carrierVal}, ptr {spillAddr}");

        TypeInfo? concreteType = payload.ResolvedType ?? payload.ConcreteType.ResolvedType ??
            _registry.LookupType(name: payload.ConcreteType.Name);

        // GEP field 1 (the data/address i64) from the carrier struct.
        string dataPtr = NextTemp();
        EmitLine(sb: sb,
            line: $"  {dataPtr} = getelementptr {carrierLlvmType}, ptr {spillAddr}, i32 0, i32 1");
        string dataI64 = NextTemp();
        EmitLine(sb: sb, line: $"  {dataI64} = load i64, ptr {dataPtr}");

        if (concreteType is EntityTypeInfo or CrashableTypeInfo)
        {
            // Entity / crashable: payload is a heap pointer stored as i64 -> inttoptr
            string ptrVal = NextTemp();
            EmitLine(sb: sb, line: $"  {ptrVal} = inttoptr i64 {dataI64} to ptr");
            return ptrVal;
        }

        // Value type: zero-extend stored as i64; truncate/bitcast to target type.
        string llvmType = concreteType != null
            ? GetLlvmType(type: concreteType)
            : "i64";
        if (llvmType == "i64") return dataI64;

        // If the LLVM type is a pointer (protocol, opaque handle), use inttoptr -> not trunc.
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
