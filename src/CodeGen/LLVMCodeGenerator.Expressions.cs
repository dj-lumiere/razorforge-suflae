namespace Compiler.CodeGen;

using System.Text;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using SyntaxTree;

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
            LiteralExpression literal => EmitLiteral(sb: sb, literal: literal),
            IdentifierExpression identifier => EmitIdentifier(sb: sb, identifier: identifier),
            MemberExpression memberAccess => EmitMemberVariableAccess(sb: sb, expr: memberAccess),
            CreatorExpression constructor => EmitConstructorCall(sb: sb, expr: constructor),
            CallExpression call => EmitCall(sb: sb, call: call),
            BinaryExpression binary => EmitBinaryOp(sb: sb, binary: binary),
            UnaryExpression unary => EmitUnaryOp(sb: sb, unary: unary),
            ConditionalExpression cond => EmitConditional(sb: sb, cond: cond),
            RangeExpression range => EmitRange(sb: sb, range: range),
            GenericMethodCallExpression generic => EmitGenericMethodCall(sb: sb, generic: generic),
            InsertedTextExpression inserted => EmitInsertedText(sb: sb, inserted: inserted),
            ListLiteralExpression list => EmitListLiteral(sb: sb, list: list),
            SetLiteralExpression set => EmitSetLiteral(sb: sb, set: set),
            DictLiteralExpression dict => EmitDictLiteral(sb: sb, dict: dict),
            FlagsTestExpression flagsTest => EmitFlagsTest(sb: sb, flagsTest: flagsTest),
            CompoundAssignmentExpression compound => EmitCompoundAssignment(sb: sb,
                compound: compound),
            IsPatternExpression isPattern => EmitIsPattern(sb: sb, isPattern: isPattern),
            WaitforExpression waitfor => EmitWaitforExpression(sb: sb, waitfor: waitfor),
            DictEntryLiteralExpression dictEntry => EmitDictEntryLiteral(sb: sb,
                dictEntry: dictEntry),
            CarrierPayloadExpression payload => EmitCarrierPayloadExpression(sb: sb,
                payload: payload),
            TupleLiteralExpression tuple => EmitTupleLiteral(sb: sb, tuple: tuple),
            // Named arguments appear inside synthesized AST bodies (e.g., me.$eq(you: you)).
            // The name is irrelevant to codegen — just emit the inner value positionally.
            NamedArgumentExpression named => EmitExpression(sb: sb, expr: named.Value),
            LambdaExpression => throw new InvalidOperationException(
                "LambdaExpression reached codegen before Phase 7 lambda lifting."),
            _ => throw new NotImplementedException(
                message: $"Expression type not implemented: {expr.GetType().Name}")
        };
    }

    /// <summary>
    /// Emits a tuple literal as an LLVM inline struct using <c>insertvalue</c>.
    /// e.g. <c>(result, overflow)</c> → <c>insertvalue { i64, i1 } ...</c>
    /// </summary>
    private string EmitTupleLiteral(StringBuilder sb, TupleLiteralExpression tuple)
    {
        TupleTypeInfo? tupleType = tuple.ResolvedType as TupleTypeInfo;
        if (tupleType == null)
            throw new InvalidOperationException("TupleLiteralExpression has no resolved TupleTypeInfo.");

        string llvmStructType = GetTupleTypeName(tuple: tupleType);

        string current = "undef";
        for (int i = 0; i < tuple.Elements.Count; i++)
        {
            string elemVal = EmitExpression(sb: sb, expr: tuple.Elements[index: i]);
            TypeInfo elemType = ResolveTypeSubstitution(type: tupleType.ElementTypes[index: i]);
            string elemLlvmType = GetLlvmType(type: elemType);
            string tmp = NextTemp();
            sb.AppendLine(
                $"  {tmp} = insertvalue {llvmStructType} {current}, {elemLlvmType} {elemVal}, {i}");
            current = tmp;
        }

        return current;
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

        // Check if this is a choice case (e.g., ME_SMALL, NORTH)
        (ChoiceTypeInfo ChoiceType, ChoiceCaseInfo CaseInfo)? choiceCase =
            _registry.LookupChoiceCase(caseName: identifier.Name);
        if (choiceCase != null)
        {
            return choiceCase.Value.CaseInfo.ComputedValue.ToString();
        }

        // Check if this is a preset (module-level constant, e.g., SET_INITIAL_BUCKETS)
        VariableInfo? presetVar = _registry.LookupVariable(name: identifier.Name);
        if (presetVar is { IsPreset: true })
        {
            // Find the preset's value expression from stdlib/user programs
            foreach ((Program program, string _, string _) in _stdlibPrograms.Concat(
                         second: _userPrograms))
            {
                foreach (IAstNode decl in program.Declarations)
                {
                    if (decl is PresetDeclaration preset && preset.Name == identifier.Name)
                    {
                        return EmitExpression(sb: sb, expr: preset.Value);
                    }
                }
            }
        }

        // Check if this is a module-level global variable
        if (_globalVariables.TryGetValue(key: identifier.Name, value: out TypeInfo? globalType) &&
            _globalVariableLlvmNames.TryGetValue(key: identifier.Name, value: out string? globalLlvm))
        {
            string globalLlvmType = GetLlvmType(type: globalType);
            string tmp2 = NextTemp();
            EmitLine(sb: sb, line: $"  {tmp2} = load {globalLlvmType}, ptr {globalLlvm}");
            return tmp2;
        }

        if (identifier.ResolvedType is RoutineTypeInfo routineType &&
            TryResolveRoutineReference(name: identifier.Name, routineType: routineType,
                routine: out RoutineInfo? routine))
        {
            return $"@{MangleFunctionName(routine)}";
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
        routine ??= _registry.LookupRoutineByName(name: bareName, isFailable: routineType.IsFailable);
        return routine != null;
    }

    /// <summary>
    /// Generates code for a function/method call.
    /// Handles both standalone function calls and method calls on objects.
    /// </summary>
    private string EmitCall(StringBuilder sb, CallExpression call)
    {
        // C29: Safety guard — semantic analyzer already errors on runtime dispatch in RF mode,
        // but if we somehow reach codegen with Runtime dispatch, trap instead of emitting bad code
        if (call.ResolvedDispatch == DispatchStrategy.Runtime)
        {
            EmitLine(sb: sb, line: "  call void @llvm.trap()");
            EmitLine(sb: sb, line: "  unreachable");
            return "undef";
        }

        // Intercept source location routines — emit constants from call site, no actual call
        if (call.Callee is IdentifierExpression { Name: var name } &&
            IsSourceLocationRoutine(name: name))
        {
            return EmitSourceLocationInline(sb: sb, routineName: name, location: call.Location);
        }

        return call.Callee switch
        {
            // Determine if this is a method call (callee is MemberExpression) or standalone function call
            MemberExpression member => EmitMethodCall(sb: sb,
                member: member,
                arguments: call.Arguments,
                resolvedRoutine: call.ResolvedRoutine,
                typeArguments: call.TypeArguments),
            IdentifierExpression id => EmitFunctionCall(sb: sb,
                functionName: id.Name,
                arguments: call.Arguments,
                resolvedRoutine: call.ResolvedRoutine,
                resolvedReturnType: call.ResolvedType),
            _ => throw new NotImplementedException(
                message: $"Cannot emit call for callee type: {call.Callee.GetType().Name}")
        };
    }

    /// <summary>
    /// Returns true if the function name is a source location routine that should be inlined at call site.
    /// </summary>
    private static bool IsSourceLocationRoutine(string name)
    {
        return name is "source_file" or "source_line" or "source_column" or "source_routine"
            or "source_module" or "source_text" or "caller_file" or "caller_line"
            or "caller_routine";
    }

    /// <summary>
    /// Emits a source location routine inline as a constant from the call site location.
    /// No actual function call is generated — the value is injected directly.
    /// </summary>
    private string EmitSourceLocationInline(StringBuilder sb, string routineName,
        SourceLocation location)
    {
        return routineName switch
        {
            "source_file" => EmitSynthesizedStringLiteral(value: location.FileName),
            "source_line" => $"{location.Line}",
            "source_column" => $"{location.Column}",
            "source_routine" => EmitSynthesizedStringLiteral(
                value: _currentEmittingRoutine?.Name ?? "<unknown>"),
            "source_module" => EmitSynthesizedStringLiteral(
                value: _currentEmittingRoutine?.OwnerType?.Module ??
                       _currentEmittingRoutine?.Module ?? "<unknown>"),
            "source_text" => EmitSynthesizedStringLiteral(value: "<expr>"),
            "caller_file" => EmitSynthesizedStringLiteral(value: location.FileName),
            "caller_line" => $"{location.Line}",
            "caller_routine" => EmitSynthesizedStringLiteral(
                value: _currentEmittingRoutine?.Name ?? "<unknown>"),
            _ => "undef"
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
        string targetLlvm = GetLlvmType(type: targetType);
        string sourceLlvm = argType != null
            ? GetLlvmType(type: argType)
            : targetLlvm;


        if (sourceLlvm == targetLlvm)
        {
            return argValue;
        }

        bool sourceIsFloat = sourceLlvm is "half" or "float" or "double" or "fp128";
        bool targetIsFloat = targetLlvm is "half" or "float" or "double" or "fp128";
        bool targetUnsigned = IsUnsignedIntegerType(type: targetType);

        string cast = NextTemp();
        if (sourceIsFloat && targetIsFloat)
        {
            string op = GetTypeBitWidth(llvmType: sourceLlvm) >
                        GetTypeBitWidth(llvmType: targetLlvm)
                ? "fptrunc"
                : "fpext";
            EmitLine(sb: sb, line: $"  {cast} = {op} {sourceLlvm} {argValue} to {targetLlvm}");
        }
        else if (sourceIsFloat)
        {
            string op = targetUnsigned
                ? "fptoui"
                : "fptosi";
            EmitLine(sb: sb, line: $"  {cast} = {op} {sourceLlvm} {argValue} to {targetLlvm}");
        }
        else if (targetIsFloat)
        {
            bool sourceUnsigned = IsUnsignedIntegerType(type: argType);
            string op = sourceUnsigned
                ? "uitofp"
                : "sitofp";
            EmitLine(sb: sb, line: $"  {cast} = {op} {sourceLlvm} {argValue} to {targetLlvm}");
        }
        else
        {
            int srcBits = GetTypeBitWidth(llvmType: sourceLlvm);
            int dstBits = GetTypeBitWidth(llvmType: targetLlvm);
            if (srcBits > dstBits)
            {
                EmitLine(sb: sb,
                    line: $"  {cast} = trunc {sourceLlvm} {argValue} to {targetLlvm}");
            }
            else if (targetUnsigned)
            {
                EmitLine(sb: sb, line: $"  {cast} = zext {sourceLlvm} {argValue} to {targetLlvm}");
            }
            else
            {
                EmitLine(sb: sb, line: $"  {cast} = sext {sourceLlvm} {argValue} to {targetLlvm}");
            }
        }

        return cast;
    }

    /// <summary>
    /// Emits a primitive type cast (trunc/zext/sext/bitcast) from one LLVM primitive type to another.
    /// Used when an explicitly typed variable declaration has an initializer of a different type.
    /// </summary>
    private string EmitPrimitiveCast(StringBuilder sb, string value, string fromLlvm, string toLlvm)
    {
        if (fromLlvm == toLlvm) return value;

        bool fromIsFloat = fromLlvm is "half" or "float" or "double" or "fp128";
        bool toIsFloat = toLlvm is "half" or "float" or "double" or "fp128";

        string cast = NextTemp();
        if (fromIsFloat && toIsFloat)
        {
            string op = GetTypeBitWidth(llvmType: fromLlvm) > GetTypeBitWidth(llvmType: toLlvm)
                ? "fptrunc" : "fpext";
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
            string op = srcBits > dstBits ? "trunc" : srcBits < dstBits ? "zext" : "bitcast";
            EmitLine(sb: sb, line: $"  {cast} = {op} {fromLlvm} {value} to {toLlvm}");
        }

        return cast;
    }
}
