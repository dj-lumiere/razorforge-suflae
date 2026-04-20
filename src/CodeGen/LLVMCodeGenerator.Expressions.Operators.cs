namespace Compiler.CodeGen;

using System.Text;
using TypeModel.Symbols;
using TypeModel.Types;
using SyntaxTree;

/// <summary>
/// Expression code generation for unary, binary, and intrinsic operator lowering.
/// </summary>
public partial class LlvmCodeGenerator
{
    private string EmitCompoundAssignment(StringBuilder sb, CompoundAssignmentExpression compound)
    {
        // Synthesize a BinaryExpression for the operation
        var binaryExpr = new BinaryExpression(Left: compound.Target,
            Operator: compound.Operator,
            Right: compound.Value,
            Location: compound.Location);
        string result = EmitBinaryOp(sb: sb, binary: binaryExpr);

        // Store the result back to the target
        switch (compound.Target)
        {
            case IdentifierExpression id:
                EmitVariableAssignment(sb: sb, varName: id.Name, value: result);
                break;
            case MemberExpression member:
                EmitMemberVariableAssignment(sb: sb, member: member, value: result);
                break;
            case IndexExpression index:
                EmitIndexAssignment(sb: sb, index: index, value: result);
                break;
        }

        return result;
    }

    /// <summary>
    /// Generates code for a binary operation.
    /// Only handles operators that are NOT desugared to method calls by the parser.
    /// Arithmetic/comparison/bitwise operators are desugared to $add, $eq, etc.
    /// </summary>
    private string EmitBinaryOp(StringBuilder sb, BinaryExpression binary)
    {
        return binary.Operator switch
        {
            BinaryOperator.And => IsFlagsBinaryOp(binary: binary)
                ? EmitFlagsCombine(sb: sb, binary: binary)
                : EmitShortCircuitAnd(sb: sb, binary: binary),
            BinaryOperator.Or => EmitShortCircuitOr(sb: sb, binary: binary),
            BinaryOperator.Identical =>
                EmitIdentityComparison(sb: sb, binary: binary, cmpOp: "eq"),
            BinaryOperator.NotIdentical => EmitIdentityComparison(sb: sb,
                binary: binary,
                cmpOp: "ne"),
            BinaryOperator.Assign => EmitBinaryAssign(sb: sb, binary: binary),
            BinaryOperator.But => EmitBitClear(sb: sb, binary: binary),
            BinaryOperator.In => EmitContainsCall(sb: sb, binary: binary, methodName: "$contains"),
            BinaryOperator.NotIn => EmitContainsCall(sb: sb,
                binary: binary,
                methodName: "$notcontains"),
            BinaryOperator.Is => EmitChoiceIs(sb: sb, binary: binary, cmpOp: "eq"),
            BinaryOperator.IsNot => EmitChoiceIs(sb: sb, binary: binary, cmpOp: "ne"),
            BinaryOperator.Obeys => EmitCompileTimeConstant(value: "true"),
            BinaryOperator.Disobeys => EmitCompileTimeConstant(value: "false"),
            // Arithmetic, comparison, bitwise operators — normally desugared to method
            // calls by the semantic analyzer, but stdlib bodies are raw AST, so we
            // handle them directly for primitive types.
            _ => EmitPrimitiveBinaryOp(sb: sb, binary: binary)
        };
    }

    /// <summary>
    /// Emits a primitive binary operation (arithmetic, comparison, bitwise, shift)
    /// directly as LLVM instructions. Used for stdlib bodies where operator
    /// desugaring to method calls hasn't been applied.
    /// </summary>
    private string EmitPrimitiveBinaryOp(StringBuilder sb, BinaryExpression binary)
    {
        string left = EmitExpression(sb: sb, expr: binary.Left);
        string right = EmitExpression(sb: sb, expr: binary.Right);
        TypeInfo? leftType = GetExpressionType(expr: binary.Left);
        string llvmType = leftType != null
            ? GetLlvmType(type: leftType)
            : "i64";
        bool isUnsigned = IsUnsignedIntegerType(type: leftType);
        bool isFloat = llvmType is "half" or "float" or "double" or "fp128";
        bool isSoftwareType = llvmType.StartsWith(value: "%Record.") ||
                              llvmType.StartsWith(value: "%Tuple.") ||
                              llvmType.StartsWith(value: "{") || llvmType == "ptr";

        // For comparison operators that return i1 (Bool)
        string? cmpInstr = isSoftwareType
            ? null
            : binary.Operator switch
            {
                BinaryOperator.Equal when isFloat => "fcmp oeq",
                BinaryOperator.Equal => "icmp eq",
                BinaryOperator.NotEqual when isFloat => "fcmp une",
                BinaryOperator.NotEqual => "icmp ne",
                BinaryOperator.Less when isFloat => "fcmp olt",
                BinaryOperator.Less when isUnsigned => "icmp ult",
                BinaryOperator.Less => "icmp slt",
                BinaryOperator.LessEqual when isFloat => "fcmp ole",
                BinaryOperator.LessEqual when isUnsigned => "icmp ule",
                BinaryOperator.LessEqual => "icmp sle",
                BinaryOperator.Greater when isFloat => "fcmp ogt",
                BinaryOperator.Greater when isUnsigned => "icmp ugt",
                BinaryOperator.Greater => "icmp sgt",
                BinaryOperator.GreaterEqual when isFloat => "fcmp oge",
                BinaryOperator.GreaterEqual when isUnsigned => "icmp uge",
                BinaryOperator.GreaterEqual => "icmp sge",
                _ => null
            };

        TypeInfo? rightType = GetExpressionType(expr: binary.Right);

        if (cmpInstr != null)
        {
            // Coerce operands to a common type when widths differ.
            // LLVM requires both icmp/fcmp operands to have identical types.
            string rightLlvmTypeCmp = rightType != null ? GetLlvmType(type: rightType) : llvmType;
            string cmpType = llvmType;
            if (rightLlvmTypeCmp != llvmType && !isFloat)
            {
                int leftBitsCmp = GetTypeBitWidth(llvmType: llvmType);
                int rightBitsCmp = GetTypeBitWidth(llvmType: rightLlvmTypeCmp);
                if (rightBitsCmp > leftBitsCmp)
                {
                    // Widen left to right's type
                    cmpType = rightLlvmTypeCmp;
                    string widened = NextTemp();
                    string widenOp = isUnsigned ? "zext" : "sext";
                    EmitLine(sb: sb, line: $"  {widened} = {widenOp} {llvmType} {left} to {cmpType}");
                    left = widened;
                }
                else if (leftBitsCmp > rightBitsCmp)
                {
                    // Widen right to left's type
                    bool rightUnsigned = IsUnsignedIntegerType(type: rightType);
                    string widenOp = rightUnsigned ? "zext" : "sext";
                    string widened = NextTemp();
                    EmitLine(sb: sb, line: $"  {widened} = {widenOp} {rightLlvmTypeCmp} {right} to {cmpType}");
                    right = widened;
                }
            }

            string cmpResult = NextTemp();
            EmitLine(sb: sb, line: $"  {cmpResult} = {cmpInstr} {cmpType} {left}, {right}");
            return cmpResult;
        }

        // Dispatch checked integer arithmetic through the registered failable wired method.
        // Wrapping operators (AddWrap/SubtractWrap/MultiplyWrap), floats, and software types
        // (D32/D64/D128) all bypass this path.
        if (!isFloat && !isSoftwareType)
        {
            string? checkedName = binary.Operator switch
            {
                BinaryOperator.Add => "$add",
                BinaryOperator.Subtract => "$sub",
                BinaryOperator.Multiply => "$mul",
                BinaryOperator.Power => "$pow",
                BinaryOperator.FloorDivide => "$floordiv",
                BinaryOperator.Modulo => "$mod",
                _ => null
            };

            if (checkedName != null && leftType != null)
            {
                RoutineInfo? method = _registry.LookupMethod(type: leftType,
                                         methodName: checkedName,
                                         isFailable: true);
                if (method != null)
                {
                    string funcName = MangleFunctionName(routine: method);
                    GenerateFunctionDeclaration(routine: method, nameOverride: funcName);
                    string retType = method.ReturnType != null
                        ? GetLlvmType(type: method.ReturnType) : llvmType;
                    string result = NextTemp();
                    EmitLine(sb: sb,
                        line: $"  {result} = call {retType} @{funcName}({llvmType} {left}, {llvmType} {right})");
                    return result;
                }
            }
        }

        // Arithmetic and bitwise operators (skip for software-implemented types like D32, D64, D128)
        string? arithInstr = isSoftwareType
            ? null
            : binary.Operator switch
            {
                BinaryOperator.Add when isFloat => "fadd",
                BinaryOperator.Add => "add",
                BinaryOperator.Subtract when isFloat => "fsub",
                BinaryOperator.Subtract => "sub",
                BinaryOperator.Multiply when isFloat => "fmul",
                BinaryOperator.Multiply => "mul",
                // TrueDivide (/): floats desugar to $truediv! via OperatorLoweringPass; for
                // integer types (no $truediv method), the raw BinaryExpression still reaches here.
                BinaryOperator.TrueDivide when isFloat => "fdiv",
                BinaryOperator.TrueDivide when isUnsigned => "udiv",
                BinaryOperator.TrueDivide => "sdiv",
                BinaryOperator.FloorDivide when isUnsigned => "udiv",
                BinaryOperator.FloorDivide => "sdiv",
                BinaryOperator.Modulo when isFloat => "frem",
                BinaryOperator.Modulo when isUnsigned => "urem",
                BinaryOperator.Modulo => "srem",
                BinaryOperator.AddWrap => "add",
                BinaryOperator.SubtractWrap => "sub",
                BinaryOperator.MultiplyWrap => "mul",
                BinaryOperator.BitwiseAnd => "and",
                BinaryOperator.BitwiseOr => "or",
                BinaryOperator.BitwiseXor => "xor",
                BinaryOperator.ArithmeticLeftShift => "shl",
                BinaryOperator.ArithmeticRightShift when isUnsigned => "lshr",
                BinaryOperator.ArithmeticRightShift => "ashr",
                BinaryOperator.LogicalLeftShift => "shl",
                BinaryOperator.LogicalRightShift => "lshr",
                _ => null
            };

        if (arithInstr != null)
        {
            // Ensure right operand matches left operand's type width
            string rightLlvmType = rightType != null
                ? GetLlvmType(type: rightType)
                : llvmType;
            if (rightLlvmType != llvmType)
            {
                // Truncate or extend right operand to match left type
                string cast = NextTemp();
                int leftBits = GetTypeBitWidth(llvmType: llvmType);
                int rightBits = GetTypeBitWidth(llvmType: rightLlvmType);
                if (rightBits > leftBits)
                {
                    EmitLine(sb: sb,
                        line: $"  {cast} = trunc {rightLlvmType} {right} to {llvmType}");
                }
                else if (isUnsigned)
                {
                    EmitLine(sb: sb,
                        line: $"  {cast} = zext {rightLlvmType} {right} to {llvmType}");
                }
                else
                {
                    EmitLine(sb: sb,
                        line: $"  {cast} = sext {rightLlvmType} {right} to {llvmType}");
                }

                right = cast;
            }

            string result = NextTemp();
            EmitLine(sb: sb, line: $"  {result} = {arithInstr} {llvmType} {left}, {right}");
            return result;
        }

        // Fall back to method call for types with software-implemented arithmetic (e.g., D32, D64, D128)
        // and for unchecked operators (which dispatch through stdlib methods with nsw/nuw IR).
        string? methodName = binary.Operator switch
        {
            BinaryOperator.Add => "$add",
            BinaryOperator.Subtract => "$sub",
            BinaryOperator.Multiply => "$mul",
            BinaryOperator.TrueDivide => "$truediv",
            BinaryOperator.FloorDivide => "$floordiv",
            BinaryOperator.Modulo => "$mod",
            BinaryOperator.Equal => "$eq",
            BinaryOperator.NotEqual => "$ne",
            BinaryOperator.Less => "$lt",
            BinaryOperator.LessEqual => "$le",
            BinaryOperator.Greater => "$gt",
            BinaryOperator.GreaterEqual => "$ge",
            // Unchecked operators dispatch through stdlib methods
            BinaryOperator.AddUnchecked => "$add_unchecked",
            BinaryOperator.SubtractUnchecked => "$sub_unchecked",
            BinaryOperator.MultiplyUnchecked => "$mul_unchecked",
            BinaryOperator.TrueDivideUnchecked => "$truediv_unchecked",
            BinaryOperator.FloorDivideUnchecked => "$floordiv_unchecked",
            BinaryOperator.ModuloUnchecked => "$mod_unchecked",
            _ => null
        };
        if (methodName != null && leftType != null)
        {
            RoutineInfo? method = _registry.LookupMethodOverload(type: leftType,
                                     methodName: methodName,
                                     argTypes: [rightType]) ??
                                 _registry.LookupMethod(type: leftType, methodName: methodName);
            if (method != null)
            {
                // For generic resolutions (e.g., Hijacked[Point].$eq), use the concrete type name
                // and record monomorphization so the specialized function body gets generated
                string funcName;
                if (leftType.IsGenericResolution && method.OwnerType is
                        { IsGenericDefinition: true } or { IsGenericResolution: true })
                {
                    funcName =
                        Q(name: $"{leftType.FullName}.{SanitizeLlvmName(name: methodName)}");
                    RecordMonomorphization(mangledName: funcName,
                        genericMethod: method,
                        resolvedOwnerType: leftType);
                }
                else
                {
                    funcName = MangleFunctionName(routine: method);
                }

                GenerateFunctionDeclaration(routine: method, nameOverride: funcName);
                string retType = method.ReturnType != null
                    ? GetLlvmType(type: method.ReturnType)
                    : llvmType;
                string result = NextTemp();
                EmitLine(sb: sb,
                    line:
                    $"  {result} = call {retType} @{funcName}({llvmType} {left}, {llvmType} {right})");
                return result;
            }
        }

        throw new NotImplementedException(
            message: $"Binary operator '{binary.Operator}' not supported for type '{leftType?.Name}' " +
                     $"(left={binary.Left.GetType().Name}, right={binary.Right.GetType().Name}, loc={binary.Location})");
    }

    /// <summary>
    /// Emits an 'is' pattern expression (e.g., 'value is None', 'value isnot None').
    /// For Maybe/ErrorHandling types, checks the tag field.
    /// </summary>
    private string EmitIsPattern(StringBuilder sb, IsPatternExpression isPattern)
    {
        string operand = EmitExpression(sb: sb, expr: isPattern.Expression);

        bool isNoneCheck = isPattern.Pattern is NonePattern or TypePattern { Type.Name: "None" };
        bool isBlankCheck = isPattern.Pattern is TypePattern { Type.Name: "Blank" };

        if (!isNoneCheck && !isBlankCheck)
        {
            throw new NotImplementedException(
                message:
                $"IsPatternExpression pattern type not implemented: {isPattern.Pattern.GetType().Name}");
        }

        TypeInfo? operandType = GetExpressionType(expr: isPattern.Expression);
        if (operandType == null || !IsCarrierType(type: operandType))
        {
            throw new NotImplementedException(
                message: "'is None'/'is Blank' expressions currently require Maybe/Result/Lookup operands.");
        }

        if (isNoneCheck && !IsMaybeType(type: operandType))
        {
            throw new NotImplementedException(
                message: "'is None' is only valid for Maybe[T] carriers.");
        }

        if (isBlankCheck && IsMaybeType(type: operandType))
        {
            throw new NotImplementedException(
                message: "'is Blank' is only valid for Result[T]/Lookup[T] carriers.");
        }

        string maybeType = GetLlvmType(type: operandType!);
        string allocaPtr = NextTemp();
        EmitEntryAlloca(llvmName: allocaPtr, llvmType: maybeType);
        EmitLine(sb: sb, line: $"  store {maybeType} {operand}, ptr {allocaPtr}");

        string field0PtrIs = NextTemp();
        EmitLine(sb: sb,
            line: $"  {field0PtrIs} = getelementptr {maybeType}, ptr {allocaPtr}, i32 0, i32 0");

        // Maybe { i1, T } / Result / Lookup: field 0 is a tag integer.
        // Entity Maybe uses same { i1, ptr } layout as record Maybe since C118.
        string tagTypeIs = GetCarrierTagType(kind: GetCarrierKind(type: operandType!));
        string expectedTagIs = isNoneCheck
            ? "0"
            : ComputeTypeId(fullName: "Blank").ToString();
        string tagIs = NextTemp();
        EmitLine(sb: sb, line: $"  {tagIs} = load {tagTypeIs}, ptr {field0PtrIs}");
        string result = NextTemp();
        EmitLine(sb: sb,
            line: isPattern.IsNegated
                ? $"  {result} = icmp ne {tagTypeIs} {tagIs}, {expectedTagIs}"
                : $"  {result} = icmp eq {tagTypeIs} {tagIs}, {expectedTagIs}");

        return result;

    }

    /// <summary>
    /// Emits choice case comparison (is / isnot).
    /// Left operand is a choice value (i32 tag), right operand is a choice case identifier.
    /// </summary>
    private string EmitChoiceIs(StringBuilder sb, BinaryExpression binary, string cmpOp)
    {
        string left = EmitExpression(sb: sb, expr: binary.Left);

        // Try to resolve RHS as a known choice case identifier
        if (binary.Right is IdentifierExpression id)
        {
            (ChoiceTypeInfo ChoiceType, ChoiceCaseInfo CaseInfo)? choiceCase =
                _registry.LookupChoiceCase(caseName: id.Name);
            if (choiceCase != null)
            {
                string result = NextTemp();
                EmitLine(sb: sb,
                    line:
                    $"  {result} = icmp {cmpOp} i32 {left}, {choiceCase.Value.CaseInfo.ComputedValue}");
                return result;
            }
        }

        // Fallback: evaluate RHS as an expression (e.g., qualified access Direction.NORTH)
        string right = EmitExpression(sb: sb, expr: binary.Right);
        string fallbackResult = NextTemp();
        EmitLine(sb: sb, line: $"  {fallbackResult} = icmp {cmpOp} i32 {left}, {right}");
        return fallbackResult;
    }

    /// <summary>
    /// Emits short-circuit AND: evaluate left, branch, phi merge.
    /// </summary>
    private string EmitShortCircuitAnd(StringBuilder sb, BinaryExpression binary)
    {
        string left = EmitExpression(sb: sb, expr: binary.Left);

        string rhsLabel = NextLabel(prefix: "and_rhs");
        string endLabel = NextLabel(prefix: "and_end");
        string leftBlock = _currentBlock;

        EmitLine(sb: sb, line: $"  br i1 {left}, label %{rhsLabel}, label %{endLabel}");

        EmitLine(sb: sb, line: $"{rhsLabel}:");
        _currentBlock = rhsLabel;
        string right = EmitExpression(sb: sb, expr: binary.Right);
        string rightBlock = _currentBlock;
        EmitLine(sb: sb, line: $"  br label %{endLabel}");

        EmitLine(sb: sb, line: $"{endLabel}:");
        _currentBlock = endLabel;
        string result = NextTemp();
        EmitLine(sb: sb,
            line: $"  {result} = phi i1 [ false, %{leftBlock} ], [ {right}, %{rightBlock} ]");
        return result;
    }

    /// <summary>
    /// Emits short-circuit OR: evaluate left, branch, phi merge.
    /// </summary>
    private string EmitShortCircuitOr(StringBuilder sb, BinaryExpression binary)
    {
        string left = EmitExpression(sb: sb, expr: binary.Left);

        string rhsLabel = NextLabel(prefix: "or_rhs");
        string endLabel = NextLabel(prefix: "or_end");
        string leftBlock = _currentBlock;

        EmitLine(sb: sb, line: $"  br i1 {left}, label %{endLabel}, label %{rhsLabel}");

        EmitLine(sb: sb, line: $"{rhsLabel}:");
        _currentBlock = rhsLabel;
        string right = EmitExpression(sb: sb, expr: binary.Right);
        string rightBlock = _currentBlock;
        EmitLine(sb: sb, line: $"  br label %{endLabel}");

        EmitLine(sb: sb, line: $"{endLabel}:");
        _currentBlock = endLabel;
        string result = NextTemp();
        EmitLine(sb: sb,
            line: $"  {result} = phi i1 [ true, %{leftBlock} ], [ {right}, %{rightBlock} ]");
        return result;
    }

    /// <summary>
    /// Emits identity comparison (=== / !==) using pointer comparison.
    /// </summary>
    private string EmitIdentityComparison(StringBuilder sb, BinaryExpression binary, string cmpOp)
    {
        string left = EmitExpression(sb: sb, expr: binary.Left);
        string right = EmitExpression(sb: sb, expr: binary.Right);
        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = icmp {cmpOp} ptr {left}, {right}");
        return result;
    }

    /// <summary>
    /// Emits assignment as an expression (evaluates right, stores into left's alloca).
    /// </summary>
    private string EmitBinaryAssign(StringBuilder sb, BinaryExpression binary)
    {
        string value = EmitExpression(sb: sb, expr: binary.Right);

        if (binary.Left is IdentifierExpression id)
        {
            EmitVariableAssignment(sb: sb, varName: id.Name, value: value);
        }
        else if (binary.Left is MemberExpression member)
        {
            EmitMemberVariableAssignment(sb: sb,
                member: member,
                value: value,
                valueType: GetExpressionType(expr: binary.Right));
            // Move semantics: if the RHS is an RC wrapper variable, transferring it into an
            // entity field transfers ownership — remove it from scope-exit tracking so the
            // scope-exit cleanup doesn't double-release it.
            if (binary.Right is IdentifierExpression { Name: var srcRcName })
            {
                _localRetainedVars.RemoveAll(match: e => e.Name == srcRcName);
            }
        }
        else if (binary.Left is IndexExpression index)
        {
            EmitIndexAssignment(sb: sb, index: index, value: value);
        }
        else
        {
            throw new NotImplementedException(
                message:
                $"Assignment target not implemented for expression type: {binary.Left.GetType().Name}");
        }

        return value;
    }

    /// <summary>
    /// Emits bit clear: left &amp; ~right (flags 'but' operator).
    /// </summary>
    private string EmitBitClear(StringBuilder sb, BinaryExpression binary)
    {
        string left = EmitExpression(sb: sb, expr: binary.Left);
        string right = EmitExpression(sb: sb, expr: binary.Right);

        TypeInfo? type = GetExpressionType(expr: binary.Left);
        string llvmType = type != null
            ? GetLlvmType(type: type)
            : "i64";

        string inverted = NextTemp();
        EmitLine(sb: sb, line: $"  {inverted} = xor {llvmType} {right}, -1");
        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = and {llvmType} {left}, {inverted}");
        return result;
    }

    /// <summary>
    /// Checks whether a binary expression is a flags combination (both operands are FlagsTypeInfo).
    /// </summary>
    private bool IsFlagsBinaryOp(BinaryExpression binary)
    {
        return GetExpressionType(expr: binary.Left) is FlagsTypeInfo;
    }

    /// <summary>
    /// Emits flags combination: left | right (bitwise OR of two flags values).
    /// </summary>
    private string EmitFlagsCombine(StringBuilder sb, BinaryExpression binary)
    {
        string left = EmitExpression(sb: sb, expr: binary.Left);
        string right = EmitExpression(sb: sb, expr: binary.Right);
        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = or i64 {left}, {right}");
        return result;
    }

    /// <summary>
    /// Emits 'in' / 'notin' by calling the right operand's $contains / $notcontains method.
    /// </summary>
    private string EmitContainsCall(StringBuilder sb, BinaryExpression binary, string methodName)
    {
        // 'x in collection' → collection.$contains(x)
        string collection = EmitExpression(sb: sb, expr: binary.Right);
        string element = EmitExpression(sb: sb, expr: binary.Left);

        TypeInfo? collectionType = GetExpressionType(expr: binary.Right);
        if (collectionType == null)
        {
            throw new InvalidOperationException(
                message: "Cannot determine collection type for 'in'/'notin' operator");
        }

        ResolvedMethod? resolved = ResolveMethod(receiverType: collectionType, methodName: methodName);
        string mangledName = resolved?.MangledName
            ?? Q(name: $"{collectionType.FullName}.{SanitizeLlvmName(name: methodName)}");

        if (resolved != null)
        {
            GenerateFunctionDeclaration(routine: resolved.Routine, nameOverride: resolved.MangledName);
            if (collectionType.IsGenericResolution)
            {
                RecordMonomorphization(mangledName: resolved.MangledName,
                    genericMethod: resolved.Routine,
                    resolvedOwnerType: collectionType);
            }
        }

        var argValues = new List<string> { collection, element };
        var argTypes = new List<string> { GetParameterLlvmType(type: collectionType) };

        TypeInfo? elemType = GetExpressionType(expr: binary.Left);
        argTypes.Add(item: elemType != null
            ? GetLlvmType(type: elemType)
            : "i64");

        string result = NextTemp();
        string args = BuildCallArgs(types: argTypes, values: argValues);
        EmitLine(sb: sb, line: $"  {result} = call i1 @{mangledName}({args})");
        return result;
    }

    /// <summary>
    /// Returns a compile-time constant value.
    /// </summary>
    private static string EmitCompileTimeConstant(string value)
    {
        return value;
    }

    /// <summary>
    /// Generates code for a unary operation.
    /// ForceUnwrap (!!) is fully desugared to $unwrap() by OperatorLoweringPass and
    /// never reaches this method. Minus and BitwiseNot fall through here only for
    /// stdlib bodies where OperatorLoweringPass cannot resolve the method (no ResolvedType).
    /// </summary>
    private string EmitUnaryOp(StringBuilder sb, UnaryExpression unary)
    {
        return unary.Operator switch
        {
            UnaryOperator.Not => EmitLogicalNot(sb: sb, unary: unary),
            UnaryOperator.Minus => EmitUnaryMethodCall(sb: sb, unary: unary, methodName: "$neg"),
            UnaryOperator.BitwiseNot => EmitBitwiseNot(sb: sb, unary: unary),
            UnaryOperator.Steal => EmitExpression(sb: sb, expr: unary.Operand),
            _ => throw new NotImplementedException(
                message: $"Unary operator '{unary.Operator}' codegen not implemented")
        };
    }

    /// <summary>
    /// Emits logical not: xor i1 %val, true.
    /// </summary>
    private string EmitLogicalNot(StringBuilder sb, UnaryExpression unary)
    {
        string operand = EmitExpression(sb: sb, expr: unary.Operand);
        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = xor i1 {operand}, true");
        return result;
    }

    /// <summary>
    /// Emits bitwise NOT: xor %val, -1 for integer types, falls back to method call for others.
    /// </summary>
    private string EmitBitwiseNot(StringBuilder sb, UnaryExpression unary)
    {
        string operand = EmitExpression(sb: sb, expr: unary.Operand);
        TypeInfo? operandType = GetExpressionType(expr: unary.Operand);
        if (operandType != null)
        {
            string llvmType = GetLlvmType(type: operandType);
            if (llvmType.StartsWith(value: "i") && llvmType != "i1")
            {
                string result = NextTemp();
                EmitLine(sb: sb, line: $"  {result} = xor {llvmType} {operand}, -1");
                return result;
            }
        }

        return EmitUnaryMethodCall(sb: sb, unary: unary, methodName: "$bitnot");
    }


    /// <summary>
    /// Emits a unary operator as a method call (e.g., -x → x.$neg(), ~x → x.$bitnot()).
    /// </summary>
    private string EmitUnaryMethodCall(StringBuilder sb, UnaryExpression unary, string methodName)
    {
        string operand = EmitExpression(sb: sb, expr: unary.Operand);
        TypeInfo? operandType = GetExpressionType(expr: unary.Operand);

        if (operandType == null)
        {
            throw new InvalidOperationException(
                message: $"Cannot determine operand type for unary operator '{unary.Operator}'");
        }

        ResolvedMethod? resolved = ResolveMethod(receiverType: operandType, methodName: methodName);
        string mangledName = resolved?.MangledName
            ?? Q(name: $"{operandType.Name}.{SanitizeLlvmName(name: methodName)}");

        string returnType = resolved?.Routine.ReturnType != null
            ? GetLlvmType(type: resolved.Routine.ReturnType)
            : GetLlvmType(type: operandType);

        var argValues = new List<string> { operand };
        var argTypes = new List<string> { GetParameterLlvmType(type: operandType) };

        string result = NextTemp();
        string args = BuildCallArgs(types: argTypes, values: argValues);
        EmitLine(sb: sb, line: $"  {result} = call {returnType} @{mangledName}({args})");
        return result;
    }

    /// <summary>
    /// Checks if a type is a built-in error handling type (Maybe/Result/Lookup).
    /// </summary>
    private static bool IsErrorHandlingType(TypeInfo? type)
    {
        if (type == null)
        {
            return false;
        }

        string? baseName = GetGenericBaseName(type: type);
        return baseName is "Maybe" or "Result" or "Lookup";
    }
}
