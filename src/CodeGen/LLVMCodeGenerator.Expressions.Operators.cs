namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Symbols;
using SemanticAnalysis.Types;
using SyntaxTree;

/// <summary>
/// Expression code generation for unary, binary, and intrinsic operator lowering.
/// </summary>
public partial class LLVMCodeGenerator
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
            BinaryOperator.NoneCoalesce => IsErrorHandlingType(type: GetExpressionType(expr: binary.Left))
                ? EmitNoneCoalesce(sb: sb, binary: binary)
                : EmitUserUnwrapOr(sb: sb, binary: binary),
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
        // TODO: This is going to be stdlib operator overloading and thus does not need hardcoding
        string left = EmitExpression(sb: sb, expr: binary.Left);
        string right = EmitExpression(sb: sb, expr: binary.Right);
        TypeInfo? leftType = GetExpressionType(expr: binary.Left);
        string llvmType = leftType != null
            ? GetLLVMType(type: leftType)
            : "i64";
        string typeName = leftType?.Name ?? "";
        bool isUnsigned = typeName is "U8" or "U16" or "U32" or "U64" or "U128" or "Address";
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

        if (cmpInstr != null)
        {
            string cmpResult = NextTemp();
            EmitLine(sb: sb, line: $"  {cmpResult} = {cmpInstr} {llvmType} {left}, {right}");
            return cmpResult;
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

        TypeInfo? rightType = GetExpressionType(expr: binary.Right);

        if (arithInstr != null)
        {
            // Ensure right operand matches left operand's type width
            string rightLlvmType = rightType != null
                ? GetLLVMType(type: rightType)
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
                // For generic resolutions (e.g., Snatched[Point].$eq), use the concrete type name
                // and record monomorphization so the specialized function body gets generated
                string funcName;
                if (leftType.IsGenericResolution && method.OwnerType is
                        { IsGenericDefinition: true })
                {
                    funcName =
                        Q(name: $"{leftType.FullName}.{SanitizeLLVMName(name: methodName)}");
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
                    ? GetLLVMType(type: method.ReturnType)
                    : llvmType;
                string result = NextTemp();
                EmitLine(sb: sb,
                    line:
                    $"  {result} = call {retType} @{funcName}({llvmType} {left}, {llvmType} {right})");
                return result;
            }
        }

        throw new NotImplementedException(
            message: $"Binary operator '{binary.Operator}' not supported for type '{typeName}' " +
                     $"(left={binary.Left.GetType().Name}, right={binary.Right.GetType().Name}, loc={binary.Location})");
    }

    /// <summary>
    /// Emits an 'is' pattern expression (e.g., 'value is None', 'value isnot None').
    /// For Maybe/ErrorHandling types, checks the tag field.
    /// </summary>
    /// <summary>
    /// Handles GenericMemberExpression — the parser produces this for ambiguous expr.member[args]
    /// patterns. In most cases this is actually member access followed by indexing (e.g.,
    /// me.buckets[i].clear()), not a generic type reference. Desugar to member access + index access.
    /// </summary>
    private string EmitGenericMemberExpression(StringBuilder sb, GenericMemberExpression gme)
    {
        // Build the member access: gme.Object.gme.MemberName
        var memberExpr = new MemberExpression(Object: gme.Object,
            PropertyName: gme.MemberName,
            Location: gme.Location);

        // The "type arguments" are actually index expressions. Use the first one.
        if (gme.TypeArguments.Count > 0)
        {
            var indexExpr = new IdentifierExpression(Name: gme.TypeArguments[index: 0].Name,
                Location: gme.TypeArguments[index: 0].Location);

            var indexAccess = new IndexExpression(Object: memberExpr,
                Index: indexExpr,
                Location: gme.Location);

            return EmitIndexAccess(sb: sb, index: indexAccess);
        }

        // No type arguments — just emit as member access
        return EmitMemberVariableAccess(sb: sb, expr: memberExpr);
    }

    private string EmitIsPattern(StringBuilder sb, IsPatternExpression isPattern)
    {
        string operand = EmitExpression(sb: sb, expr: isPattern.Expression);

        // Handle NonePattern or TypePattern with type name "None" — check Maybe tag == 0
        bool isNoneCheck = isPattern.Pattern is NonePattern ||
                           isPattern.Pattern is TypePattern tp && tp.Type.Name == "None";

        if (isNoneCheck)
        {
            // For Maybe types, check tag == 0 (ABSENT/None)
            // Use the proper LLVM type for the operand (named or anonymous)
            TypeInfo? operandType = GetExpressionType(expr: isPattern.Expression);
            string maybeType = operandType != null
                ? GetLLVMType(type: operandType)
                : "{ i64, ptr }";

            string allocaPtr = NextTemp();
            EmitEntryAlloca(llvmName: allocaPtr, llvmType: maybeType);
            EmitLine(sb: sb, line: $"  store {maybeType} {operand}, ptr {allocaPtr}");

            string tagPtr = NextTemp();
            string tag = NextTemp();
            EmitLine(sb: sb,
                line: $"  {tagPtr} = getelementptr {maybeType}, ptr {allocaPtr}, i32 0, i32 0");
            EmitLine(sb: sb, line: $"  {tag} = load i64, ptr {tagPtr}");

            string result = NextTemp();
            if (isPattern.IsNegated)
            {
                EmitLine(sb: sb,
                    line: $"  {result} = icmp ne i64 {tag}, 0"); // isnot None → tag != 0
            }
            else
            {
                EmitLine(sb: sb, line: $"  {result} = icmp eq i64 {tag}, 0"); // is None → tag == 0
            }

            return result;
        }

        throw new NotImplementedException(
            message:
            $"IsPatternExpression pattern type not implemented: {isPattern.Pattern.GetType().Name}");
    }

    /// <summary>
    /// Emits a chained comparison (e.g., 0xC0u8 &lt;= b0 &lt;= 0xDFu8).
    /// Evaluates middle operands once and ANDs all pairwise comparisons.
    /// </summary>
    private string EmitChainedComparison(StringBuilder sb, ChainedComparisonExpression chain)
    {
        // Evaluate all operands (middle ones evaluated only once)
        var values = new List<string>();
        foreach (Expression operand in chain.Operands)
        {
            values.Add(item: EmitExpression(sb: sb, expr: operand));
        }

        // Emit pairwise comparisons, AND results together
        string result = "";
        for (int i = 0; i < chain.Operators.Count; i++)
        {
            TypeInfo? leftType = GetExpressionType(expr: chain.Operands[index: i]);
            string llvmType = leftType != null
                ? GetLLVMType(type: leftType)
                : "i64";
            string typeName = leftType?.Name ?? "";
            bool isUnsigned = typeName is "U8" or "U16" or "U32" or "U64" or "U128" or "Address";
            bool isFloat = llvmType is "half" or "float" or "double" or "fp128";

            string cmpInstr = chain.Operators[index: i] switch
            {
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
                BinaryOperator.Equal => isFloat
                    ? "fcmp oeq"
                    : "icmp eq",
                BinaryOperator.NotEqual => isFloat
                    ? "fcmp une"
                    : "icmp ne",
                _ => throw new InvalidOperationException(
                    message:
                    $"Unsupported chained comparison operator: {chain.Operators[index: i]}")
            };

            string cmp = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {cmp} = {cmpInstr} {llvmType} {values[index: i]}, {values[index: i + 1]}");

            if (result == "")
            {
                result = cmp;
            }
            else
            {
                string andResult = NextTemp();
                EmitLine(sb: sb, line: $"  {andResult} = and i1 {result}, {cmp}");
                result = andResult;
            }
        }

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
            ? GetLLVMType(type: type)
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
            ?? Q(name: $"{collectionType.FullName}.{SanitizeLLVMName(name: methodName)}");

        if (resolved != null)
        {
            GenerateFunctionDeclaration(routine: resolved.Routine);
        }

        var argValues = new List<string> { collection, element };
        var argTypes = new List<string> { GetParameterLLVMType(type: collectionType) };

        TypeInfo? elemType = GetExpressionType(expr: binary.Left);
        argTypes.Add(item: elemType != null
            ? GetLLVMType(type: elemType)
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
    /// Minus and BitwiseNot are emitted as method calls to $neg / $bitnot
    /// so the stdlib bodies (which call LLVM intrinsics) do the actual work.
    /// </summary>
    private string EmitUnaryOp(StringBuilder sb, UnaryExpression unary)
    {
        return unary.Operator switch
        {
            UnaryOperator.Not => EmitLogicalNot(sb: sb, unary: unary),
            UnaryOperator.Minus => EmitUnaryMethodCall(sb: sb, unary: unary, methodName: "$neg"),
            UnaryOperator.BitwiseNot => EmitBitwiseNot(sb: sb, unary: unary),
            UnaryOperator.Steal => EmitExpression(sb: sb, expr: unary.Operand),
            UnaryOperator.ForceUnwrap => IsErrorHandlingType(type: GetExpressionType(expr: unary.Operand))
                ? EmitForceUnwrap(sb: sb, unary: unary)
                : EmitUnaryMethodCall(sb: sb, unary: unary, methodName: "$unwrap"),
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
            string llvmType = GetLLVMType(type: operandType);
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
    /// Emits user-type none coalescing: left.$unwrap_or(right) — eager evaluation.
    /// </summary>
    private string EmitUserUnwrapOr(StringBuilder sb, BinaryExpression binary)
    {
        string left = EmitExpression(sb: sb, expr: binary.Left);
        string right = EmitExpression(sb: sb, expr: binary.Right);
        TypeInfo? leftType = GetExpressionType(expr: binary.Left);

        if (leftType == null)
        {
            throw new InvalidOperationException(
                message: "Cannot determine left operand type for '??' operator");
        }

        RoutineInfo? method = _registry.LookupMethod(type: leftType, methodName: "$unwrap_or");

        var argValues = new List<string> { left, right };
        var argTypes = new List<string> { GetParameterLLVMType(type: leftType) };

        TypeInfo? rightType = GetExpressionType(expr: binary.Right);
        argTypes.Add(item: rightType != null
            ? GetLLVMType(type: rightType)
            : "i64");

        string mangledName = method != null
            ? MangleFunctionName(routine: method)
            : Q(name: $"{leftType.FullName}.{SanitizeLLVMName(name: "$unwrap_or")}");

        string returnType = method?.ReturnType != null
            ? GetLLVMType(type: method.ReturnType)
            : rightType != null
                ? GetLLVMType(type: rightType)
                : "i64";

        if (method != null)
        {
            GenerateFunctionDeclaration(routine: method);
        }

        string result = NextTemp();
        string args = BuildCallArgs(types: argTypes, values: argValues);
        EmitLine(sb: sb, line: $"  {result} = call {returnType} @{mangledName}({args})");
        return result;
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
            ?? Q(name: $"{operandType.Name}.{SanitizeLLVMName(name: methodName)}");

        string returnType = resolved?.Routine.ReturnType != null
            ? GetLLVMType(type: resolved.Routine.ReturnType)
            : GetLLVMType(type: operandType);

        var argValues = new List<string> { operand };
        var argTypes = new List<string> { GetParameterLLVMType(type: operandType) };

        string result = NextTemp();
        string args = BuildCallArgs(types: argTypes, values: argValues);
        EmitLine(sb: sb, line: $"  {result} = call {returnType} @{mangledName}({args})");
        return result;
    }

    /// <summary>
    /// Checks if a type is a built-in error handling type (Maybe/Result/Lookup).
    /// Handles both direct ErrorHandlingTypeInfo and generic instances resolved as RecordTypeInfo.
    /// </summary>
    private bool IsErrorHandlingType(TypeInfo? type)
    {
        if (type is ErrorHandlingTypeInfo)
        {
            return true;
        }

        if (type != null)
        {
            string? baseName = GetGenericBaseName(type: type);
            return baseName is "Maybe" or "Result" or "Lookup";
        }

        return false;
    }
}
