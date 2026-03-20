namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Symbols;
using SemanticAnalysis.Types;
using SyntaxTree;

/// <summary>
/// Expression code generation: allocation, member variable access, method calls, operators.
/// </summary>
public partial class LLVMCodeGenerator
{
    #region Entity Allocation

    /// <summary>
    /// Generates code to allocate a new entity instance.
    /// Entity allocation:
    /// 1. Call rf_alloc(size) to get heap memory
    /// 2. Initialize all member variables to zero/default values
    /// 3. Return pointer to the entity
    /// </summary>
    /// <param name="sb">StringBuilder to emit code to.</param>
    /// <param name="entity">The entity type to allocate.</param>
    /// <param name="memberVariableValues">Optional field initializer values (in member variable order).</param>
    /// <returns>The temporary variable holding the entity pointer.</returns>
    private string EmitEntityAllocation(StringBuilder sb, EntityTypeInfo entity, List<string>? memberVariableValues = null)
    {
        string typeName = GetEntityTypeName(entity);
        int size = CalculateEntitySize(entity);

        // Allocate memory
        string rawPtr = NextTemp();
        EmitLine(sb, $"  {rawPtr} = call ptr @rf_alloc(i64 {size})");

        // Initialize member variables
        for (int i = 0; i < entity.MemberVariables.Count; i++)
        {
            var memberVariable = entity.MemberVariables[i];
            string memberVariableType = GetLLVMType(memberVariable.Type);

            // Get member variable pointer using GEP
            string memberVariablePtr = NextTemp();
            EmitLine(sb, $"  {memberVariablePtr} = getelementptr {typeName}, ptr {rawPtr}, i32 0, i32 {i}");

            // Get value to store
            string value;
            if (memberVariableValues != null && i < memberVariableValues.Count)
            {
                value = memberVariableValues[i];
            }
            else
            {
                value = GetZeroValue(memberVariable.Type);
            }

            // Store the value
            EmitLine(sb, $"  store {memberVariableType} {value}, ptr {memberVariablePtr}");
        }

        return rawPtr;
    }

    /// <summary>
    /// Generates code for a constructor call expression.
    /// </summary>
    /// <param name="sb">StringBuilder to emit code to.</param>
    /// <param name="expr">The constructor call expression.</param>
    /// <returns>The temporary variable holding the result.</returns>
    private string EmitConstructorCall(StringBuilder sb, CreatorExpression expr)
    {
        // Look up the type
        TypeInfo? type = _registry.LookupType(expr.TypeName);
        if (type == null)
        {
            throw new InvalidOperationException($"Unknown type in constructor: {expr.TypeName}");
        }

        return type switch
        {
            EntityTypeInfo entity => EmitEntityConstruction(sb, entity, expr),
            RecordTypeInfo record => EmitRecordConstruction(sb, record, expr),
            _ => throw new InvalidOperationException($"Cannot construct type: {type.Category}")
        };
    }

    /// <summary>
    /// Generates code to construct an entity with member variable values.
    /// </summary>
    private string EmitEntityConstruction(StringBuilder sb, EntityTypeInfo entity, CreatorExpression expr)
    {
        // Evaluate all member variable value expressions first
        var memberVariableValues = new List<string>();
        foreach (var (_, fieldExpr) in expr.MemberVariables)
        {
            string value = EmitExpression(sb, fieldExpr);
            memberVariableValues.Add(value);
        }

        // Allocate and initialize
        return EmitEntityAllocation(sb, entity, memberVariableValues);
    }

    /// <summary>
    /// Generates code to construct a record (value type).
    /// </summary>
    private string EmitRecordConstruction(StringBuilder sb, RecordTypeInfo record, CreatorExpression expr)
    {
        // Backend-annotated or single-member-variable wrapper: just return the inner value
        if ((record.HasDirectBackendType || record.IsSingleMemberVariableWrapper) && expr.MemberVariables.Count <= 1)
        {
            return EmitExpression(sb, expr.MemberVariables[0].Value);
        }

        // Multi-member-variable record: build struct value
        string typeName = GetRecordTypeName(record);

        // Start with undef and insert each member variable
        string result = "undef";
        for (int i = 0; i < expr.MemberVariables.Count && i < record.MemberVariables.Count; i++)
        {
            string value = EmitExpression(sb, expr.MemberVariables[i].Value);
            string memberVariableType = GetLLVMType(record.MemberVariables[i].Type);

            string newResult = NextTemp();
            EmitLine(sb, $"  {newResult} = insertvalue {typeName} {result}, {memberVariableType} {value}, {i}");
            result = newResult;
        }

        return result;
    }

    #endregion

    #region Field Access

    /// <summary>
    /// Generates code to read a member variable from an entity/record.
    /// For entities: GEP + load
    /// For records: extractvalue
    /// </summary>
    /// <param name="sb">StringBuilder to emit code to.</param>
    /// <param name="expr">The member access expression.</param>
    /// <returns>The temporary variable holding the member variable value.</returns>
    private string EmitMemberVariableAccess(StringBuilder sb, MemberExpression expr)
    {
        // Evaluate the target expression
        string target = EmitExpression(sb, expr.Object);

        // Get the target type
        TypeInfo? targetType = GetExpressionType(expr.Object);
        if (targetType == null)
        {
            throw new InvalidOperationException("Cannot determine type of member variable access target");
        }

        return targetType switch
        {
            EntityTypeInfo entity => EmitEntityMemberVariableRead(sb, target, entity, expr.PropertyName),
            RecordTypeInfo record => EmitRecordMemberVariableRead(sb, target, record, expr.PropertyName),
            ResidentTypeInfo resident => EmitResidentMemberVariableRead(sb, target, resident, expr.PropertyName),
            _ => throw new InvalidOperationException($"Cannot access member variable on type: {targetType.Category}")
        };
    }

    /// <summary>
    /// Generates code to read a member variable from an entity (pointer type).
    /// Uses GEP to get member variable address, then load.
    /// </summary>
    private string EmitEntityMemberVariableRead(StringBuilder sb, string entityPtr, EntityTypeInfo entity, string memberVariableName)
    {
        // Find member variable index
        int memberVariableIndex = -1;
        MemberVariableInfo? memberVariable = null;
        for (int i = 0; i < entity.MemberVariables.Count; i++)
        {
            if (entity.MemberVariables[i].Name == memberVariableName)
            {
                memberVariableIndex = i;
                memberVariable = entity.MemberVariables[i];
                break;
            }
        }

        if (memberVariableIndex < 0 || memberVariable == null)
        {
            throw new InvalidOperationException($"Member variable '{memberVariableName}' not found on entity '{entity.Name}'");
        }

        string typeName = GetEntityTypeName(entity);
        string memberVariableType = GetLLVMType(memberVariable.Type);

        // GEP to get member variable pointer
        string memberVariablePtr = NextTemp();
        EmitLine(sb, $"  {memberVariablePtr} = getelementptr {typeName}, ptr {entityPtr}, i32 0, i32 {memberVariableIndex}");

        // Load the member variable value
        string value = NextTemp();
        EmitLine(sb, $"  {value} = load {memberVariableType}, ptr {memberVariablePtr}");

        return value;
    }

    /// <summary>
    /// Generates code to read a member variable from a record (value type).
    /// Uses extractvalue instruction.
    /// </summary>
    private string EmitRecordMemberVariableRead(StringBuilder sb, string recordValue, RecordTypeInfo record, string memberVariableName)
    {
        // Backend-annotated or single-member-variable wrapper: the value IS the field
        if (record.HasDirectBackendType || record.IsSingleMemberVariableWrapper)
        {
            return recordValue;
        }

        // Find member variable index
        int memberVariableIndex = -1;
        MemberVariableInfo? memberVariable = null;
        for (int i = 0; i < record.MemberVariables.Count; i++)
        {
            if (record.MemberVariables[i].Name == memberVariableName)
            {
                memberVariableIndex = i;
                memberVariable = record.MemberVariables[i];
                break;
            }
        }

        if (memberVariableIndex < 0 || memberVariable == null)
        {
            throw new InvalidOperationException($"Member variable '{memberVariableName}' not found on record '{record.Name}'");
        }

        string typeName = GetRecordTypeName(record);
        string memberVariableType = GetLLVMType(memberVariable.Type);

        // Extract the member variable value
        string value = NextTemp();
        EmitLine(sb, $"  {value} = extractvalue {typeName} {recordValue}, {memberVariableIndex}");

        return value;
    }

    /// <summary>
    /// Generates code to read a member variable from a resident (like entity).
    /// </summary>
    private string EmitResidentMemberVariableRead(StringBuilder sb, string residentPtr, ResidentTypeInfo resident, string memberVariableName)
    {
        // Find member variable index
        int memberVariableIndex = -1;
        MemberVariableInfo? memberVariable = null;
        for (int i = 0; i < resident.MemberVariables.Count; i++)
        {
            if (resident.MemberVariables[i].Name == memberVariableName)
            {
                memberVariableIndex = i;
                memberVariable = resident.MemberVariables[i];
                break;
            }
        }

        if (memberVariableIndex < 0 || memberVariable == null)
        {
            throw new InvalidOperationException($"Member variable '{memberVariableName}' not found on resident '{resident.Name}'");
        }

        string typeName = GetResidentTypeName(resident);
        string memberVariableType = GetLLVMType(memberVariable.Type);

        // GEP to get member variable pointer
        string memberVariablePtr = NextTemp();
        EmitLine(sb, $"  {memberVariablePtr} = getelementptr {typeName}, ptr {residentPtr}, i32 0, i32 {memberVariableIndex}");

        // Load the member variable value
        string value = NextTemp();
        EmitLine(sb, $"  {value} = load {memberVariableType}, ptr {memberVariablePtr}");

        return value;
    }

    #endregion

    #region Field Write

    /// <summary>
    /// Generates code to write a member variable on an entity.
    /// </summary>
    private void EmitEntityMemberVariableWrite(StringBuilder sb, string entityPtr, EntityTypeInfo entity, string memberVariableName, string value)
    {
        // Find member variable index
        int memberVariableIndex = -1;
        MemberVariableInfo? memberVariable = null;
        for (int i = 0; i < entity.MemberVariables.Count; i++)
        {
            if (entity.MemberVariables[i].Name == memberVariableName)
            {
                memberVariableIndex = i;
                memberVariable = entity.MemberVariables[i];
                break;
            }
        }

        if (memberVariableIndex < 0 || memberVariable == null)
        {
            throw new InvalidOperationException($"Member variable '{memberVariableName}' not found on entity '{entity.Name}'");
        }

        string typeName = GetEntityTypeName(entity);
        string memberVariableType = GetLLVMType(memberVariable.Type);

        // GEP to get member variable pointer
        string memberVariablePtr = NextTemp();
        EmitLine(sb, $"  {memberVariablePtr} = getelementptr {typeName}, ptr {entityPtr}, i32 0, i32 {memberVariableIndex}");

        // Store the value
        EmitLine(sb, $"  store {memberVariableType} {value}, ptr {memberVariablePtr}");
    }

    #endregion

    #region Expression Dispatch

    /// <summary>
    /// Main expression dispatch - generates code for any expression type.
    /// </summary>
    /// <param name="sb">StringBuilder to emit code to.</param>
    /// <param name="expr">The expression to generate code for.</param>
    /// <returns>The temporary variable holding the expression result.</returns>
    private string EmitExpression(StringBuilder sb, Expression expr)
    {
        return expr switch
        {
            LiteralExpression literal => EmitLiteral(sb, literal),
            IdentifierExpression identifier => EmitIdentifier(sb, identifier),
            MemberExpression memberAccess => EmitMemberVariableAccess(sb, memberAccess),
            OptionalMemberExpression optMember => EmitOptionalMemberAccess(sb, optMember),
            CreatorExpression constructor => EmitConstructorCall(sb, constructor),
            CallExpression call => EmitCall(sb, call),
            BinaryExpression binary => EmitBinaryOp(sb, binary),
            UnaryExpression unary => EmitUnaryOp(sb, unary),
            ConditionalExpression cond => EmitConditional(sb, cond),
            IndexExpression index => EmitIndexAccess(sb, index),
            SliceExpression slice => EmitSliceAccess(sb, slice),
            RangeExpression range => EmitRange(sb, range),
            StealExpression steal => EmitSteal(sb, steal),
            TupleLiteralExpression tuple => EmitTupleLiteral(sb, tuple),
            GenericMethodCallExpression generic => EmitGenericMethodCall(sb, generic),
            InsertedTextExpression inserted => EmitInsertedText(sb, inserted),
            ListLiteralExpression list => EmitListLiteral(sb, list),
            FlagsTestExpression flagsTest => EmitFlagsTest(sb, flagsTest),
            _ => throw new NotImplementedException($"Expression type not implemented: {expr.GetType().Name}")
        };
    }

    /// <summary>
    /// Generates code for a literal expression.
    /// </summary>
    private string EmitLiteral(StringBuilder sb, LiteralExpression literal)
    {
        return literal.Value switch
        {
            int i => i.ToString(),
            long l => l.ToString(),
            double d => d.ToString("G17"),
            float f => f.ToString("G9"),
            bool b => b ? "true" : "false",
            string s => EmitStringLiteral(sb, s),
            null => "null",
            _ => literal.Value.ToString() ?? "0"
        };
    }

    /// <summary>
    /// Generates code for a string literal.
    /// Emits a global constant and returns a pointer to it.
    /// </summary>
    private string EmitStringLiteral(StringBuilder sb, string value)
    {
        // Check if we've already emitted this string
        if (_stringConstants.TryGetValue(value, out string? existingName))
        {
            return existingName;
        }

        // Generate a unique name for this string constant
        string constName = $"@.str.{_stringCounter++}";
        _stringConstants[value] = constName;

        // Escape the string for LLVM IR
        string escaped = EscapeStringForLLVM(value);
        int byteLength = Encoding.UTF8.GetByteCount(value) + 1; // +1 for null terminator

        // Emit global constant (null-terminated for C interop)
        EmitLine(_globalDeclarations, $"{constName} = private unnamed_addr constant [{byteLength} x i8] c\"{escaped}\\00\"");

        return constName;
    }

    /// <summary>
    /// Escapes a string for use in LLVM IR.
    /// </summary>
    private static string EscapeStringForLLVM(string value)
    {
        var sb = new StringBuilder();
        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\22");
                    break;
                case '\\':
                    sb.Append("\\5C");
                    break;
                case '\n':
                    sb.Append("\\0A");
                    break;
                case '\r':
                    sb.Append("\\0D");
                    break;
                case '\t':
                    sb.Append("\\09");
                    break;
                default:
                {
                    if (c < 32 || c > 126)
                    {
                        // Non-printable or non-ASCII: encode as hex bytes
                        byte[] bytes = Encoding.UTF8.GetBytes(c.ToString());
                        foreach (byte b in bytes)
                        {
                            sb.Append($"\\{b:X2}");
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Generates code for an identifier expression (variable reference).
    /// </summary>
    private string EmitIdentifier(StringBuilder sb, IdentifierExpression identifier)
    {
        // Check if this is a choice case (e.g., ME_SMALL, NORTH)
        var choiceCase = _registry.LookupChoiceCase(identifier.Name);
        if (choiceCase != null)
        {
            return choiceCase.Value.CaseInfo.ComputedValue.ToString();
        }

        // Look up the variable in local variables first
        if (!_localVariables.TryGetValue(identifier.Name, out var varType))
        {
            // Fallback for unknown identifiers (shouldn't happen after semantic analysis)
            return $"%{identifier.Name}";
        }

        // Variables are stored in allocas (%name.addr), need to load them
        string llvmType = GetLLVMType(varType);
        string tmp = NextTemp();
        EmitLine(sb, $"  {tmp} = load {llvmType}, ptr %{identifier.Name}.addr");
        return tmp;
    }

    /// <summary>
    /// Generates code for a function/method call.
    /// Handles both standalone function calls and method calls on objects.
    /// </summary>
    private string EmitCall(StringBuilder sb, CallExpression call)
    {
        return call.Callee switch
        {
            // Determine if this is a method call (callee is MemberExpression) or standalone function call
            MemberExpression member => EmitMethodCall(sb, member, call.Arguments),
            IdentifierExpression id => EmitFunctionCall(sb, id.Name, call.Arguments),
            _ => throw new NotImplementedException(
                $"Cannot emit call for callee type: {call.Callee.GetType().Name}")
        };
    }

    /// <summary>
    /// Generates code for a standalone function call.
    /// </summary>
    private string EmitFunctionCall(StringBuilder sb, string functionName, List<Expression> arguments)
    {
        // Look up the routine
        RoutineInfo? routine = _registry.LookupRoutine(functionName);

        // Evaluate all arguments
        var argValues = new List<string>();
        var argTypes = new List<string>();

        foreach (var arg in arguments)
        {
            string value = EmitExpression(sb, arg);
            argValues.Add(value);

            TypeInfo? argType = GetExpressionType(arg);
            if (argType == null)
            {
                throw new InvalidOperationException($"Cannot determine type for argument in function call to '{functionName}'");
            }
            argTypes.Add(GetLLVMType(argType));
        }

        // Build the call
        string mangledName = routine != null
            ? MangleFunctionName(routine)
            : functionName;

        string returnType = routine?.ReturnType != null
            ? GetLLVMType(routine.ReturnType)
            : "void";

        if (returnType == "void")
        {
            // Void return - no result
            string args = BuildCallArgs(argTypes, argValues);
            EmitLine(sb, $"  call void @{mangledName}({args})");
            return "undef"; // No meaningful return value
        }
        else
        {
            // Has return value
            string result = NextTemp();
            string args = BuildCallArgs(argTypes, argValues);
            EmitLine(sb, $"  {result} = call {returnType} @{mangledName}({args})");
            return result;
        }
    }

    /// <summary>
    /// Generates code for a method call on an object.
    /// The object becomes the implicit 'me' parameter.
    /// </summary>
    private string EmitMethodCall(StringBuilder sb, MemberExpression member, List<Expression> arguments)
    {
        // Evaluate the receiver (becomes 'me' parameter)
        string receiver = EmitExpression(sb, member.Object);
        TypeInfo? receiverType = GetExpressionType(member.Object);

        if (receiverType == null)
        {
            throw new InvalidOperationException("Cannot determine receiver type for method call");
        }

        // Look up the method
        string methodFullName = $"{receiverType.Name}.{member.PropertyName}";
        RoutineInfo? method = _registry.LookupRoutine(methodFullName);

        // Build argument list: receiver first, then explicit arguments
        var argValues = new List<string> { receiver };
        var argTypes = new List<string> { GetParameterLLVMType(receiverType) };

        foreach (var arg in arguments)
        {
            string value = EmitExpression(sb, arg);
            argValues.Add(value);

            TypeInfo? argType = GetExpressionType(arg);
            if (argType == null)
            {
                throw new InvalidOperationException($"Cannot determine type for argument in method call to '{member.PropertyName}'");
            }
            argTypes.Add(GetLLVMType(argType));
        }

        // Build the call
        string mangledName = method != null
            ? MangleFunctionName(method)
            : $"{MangleTypeName(receiverType.Name)}_{member.PropertyName}";

        string returnType = method?.ReturnType != null
            ? GetLLVMType(method.ReturnType)
            : "void";

        if (returnType == "void")
        {
            string args = BuildCallArgs(argTypes, argValues);
            EmitLine(sb, $"  call void @{mangledName}({args})");
            return "undef";
        }
        else
        {
            string result = NextTemp();
            string args = BuildCallArgs(argTypes, argValues);
            EmitLine(sb, $"  {result} = call {returnType} @{mangledName}({args})");
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
        return string.Join(", ", types.Select((t, i) => $"{t} {values[i]}"));
    }

    /// <summary>
    /// Generates code for a binary operation.
    /// Only handles operators that are NOT desugared to method calls by the parser.
    /// Arithmetic/comparison/bitwise operators are desugared to __add__, __eq__, etc.
    /// </summary>
    private string EmitBinaryOp(StringBuilder sb, BinaryExpression binary)
    {
        return binary.Operator switch
        {
            BinaryOperator.And => IsFlagsBinaryOp(binary) ? EmitFlagsCombine(sb, binary) : EmitShortCircuitAnd(sb, binary),
            BinaryOperator.Or => EmitShortCircuitOr(sb, binary),
            BinaryOperator.Identical => EmitIdentityComparison(sb, binary, "eq"),
            BinaryOperator.NotIdentical => EmitIdentityComparison(sb, binary, "ne"),
            BinaryOperator.Assign => EmitBinaryAssign(sb, binary),
            BinaryOperator.But => EmitBitClear(sb, binary),
            BinaryOperator.In => EmitContainsCall(sb, binary, "__contains__"),
            BinaryOperator.NotIn => EmitContainsCall(sb, binary, "__notcontains__"),
            BinaryOperator.Is => EmitChoiceIs(sb, binary, "eq"),
            BinaryOperator.IsNot => EmitChoiceIs(sb, binary, "ne"),
            BinaryOperator.Obeys => EmitCompileTimeConstant("true"),
            BinaryOperator.NotObeys => EmitCompileTimeConstant("false"),
            BinaryOperator.NoneCoalesce => EmitNoneCoalesce(sb, binary),
            _ => throw new NotImplementedException(
                $"Binary operator '{binary.Operator}' should have been desugared to a method call")
        };
    }

    /// <summary>
    /// Emits choice case comparison (is / isnot).
    /// Left operand is a choice value (i64 tag), right operand is a choice case identifier.
    /// </summary>
    private string EmitChoiceIs(StringBuilder sb, BinaryExpression binary, string cmpOp)
    {
        string left = EmitExpression(sb, binary.Left);

        // Try to resolve RHS as a known choice case identifier
        if (binary.Right is IdentifierExpression id)
        {
            var choiceCase = _registry.LookupChoiceCase(id.Name);
            if (choiceCase != null)
            {
                string result = NextTemp();
                EmitLine(sb, $"  {result} = icmp {cmpOp} i64 {left}, {choiceCase.Value.CaseInfo.ComputedValue}");
                return result;
            }
        }

        // Fallback: evaluate RHS as an expression (e.g., qualified access Direction.NORTH)
        string right = EmitExpression(sb, binary.Right);
        string fallbackResult = NextTemp();
        EmitLine(sb, $"  {fallbackResult} = icmp {cmpOp} i64 {left}, {right}");
        return fallbackResult;
    }

    /// <summary>
    /// Emits short-circuit AND: evaluate left, branch, phi merge.
    /// </summary>
    private string EmitShortCircuitAnd(StringBuilder sb, BinaryExpression binary)
    {
        string left = EmitExpression(sb, binary.Left);

        string rhsLabel = NextLabel("and_rhs");
        string endLabel = NextLabel("and_end");
        string leftBlock = _currentBlock;

        EmitLine(sb, $"  br i1 {left}, label %{rhsLabel}, label %{endLabel}");

        EmitLine(sb, $"{rhsLabel}:");
        _currentBlock = rhsLabel;
        string right = EmitExpression(sb, binary.Right);
        string rightBlock = _currentBlock;
        EmitLine(sb, $"  br label %{endLabel}");

        EmitLine(sb, $"{endLabel}:");
        _currentBlock = endLabel;
        string result = NextTemp();
        EmitLine(sb, $"  {result} = phi i1 [ false, %{leftBlock} ], [ {right}, %{rightBlock} ]");
        return result;
    }

    /// <summary>
    /// Emits short-circuit OR: evaluate left, branch, phi merge.
    /// </summary>
    private string EmitShortCircuitOr(StringBuilder sb, BinaryExpression binary)
    {
        string left = EmitExpression(sb, binary.Left);

        string rhsLabel = NextLabel("or_rhs");
        string endLabel = NextLabel("or_end");
        string leftBlock = _currentBlock;

        EmitLine(sb, $"  br i1 {left}, label %{endLabel}, label %{rhsLabel}");

        EmitLine(sb, $"{rhsLabel}:");
        _currentBlock = rhsLabel;
        string right = EmitExpression(sb, binary.Right);
        string rightBlock = _currentBlock;
        EmitLine(sb, $"  br label %{endLabel}");

        EmitLine(sb, $"{endLabel}:");
        _currentBlock = endLabel;
        string result = NextTemp();
        EmitLine(sb, $"  {result} = phi i1 [ true, %{leftBlock} ], [ {right}, %{rightBlock} ]");
        return result;
    }

    /// <summary>
    /// Emits identity comparison (=== / !==) using pointer comparison.
    /// </summary>
    private string EmitIdentityComparison(StringBuilder sb, BinaryExpression binary, string cmpOp)
    {
        string left = EmitExpression(sb, binary.Left);
        string right = EmitExpression(sb, binary.Right);
        string result = NextTemp();
        EmitLine(sb, $"  {result} = icmp {cmpOp} ptr {left}, {right}");
        return result;
    }

    /// <summary>
    /// Emits assignment as an expression (evaluates right, stores into left's alloca).
    /// </summary>
    private string EmitBinaryAssign(StringBuilder sb, BinaryExpression binary)
    {
        string value = EmitExpression(sb, binary.Right);

        if (binary.Left is IdentifierExpression id)
        {
            EmitVariableAssignment(sb, id.Name, value);
        }
        else if (binary.Left is MemberExpression member)
        {
            EmitMemberVariableAssignment(sb, member, value);
        }
        else
        {
            throw new NotImplementedException(
                $"Assignment target not implemented for expression type: {binary.Left.GetType().Name}");
        }

        return value;
    }

    /// <summary>
    /// Emits bit clear: left &amp; ~right (flags 'but' operator).
    /// </summary>
    private string EmitBitClear(StringBuilder sb, BinaryExpression binary)
    {
        string left = EmitExpression(sb, binary.Left);
        string right = EmitExpression(sb, binary.Right);

        TypeInfo? type = GetExpressionType(binary.Left);
        string llvmType = type != null ? GetLLVMType(type) : "i64";

        string inverted = NextTemp();
        EmitLine(sb, $"  {inverted} = xor {llvmType} {right}, -1");
        string result = NextTemp();
        EmitLine(sb, $"  {result} = and {llvmType} {left}, {inverted}");
        return result;
    }

    /// <summary>
    /// Checks whether a binary expression is a flags combination (both operands are FlagsTypeInfo).
    /// </summary>
    private bool IsFlagsBinaryOp(BinaryExpression binary)
    {
        return GetExpressionType(binary.Left) is FlagsTypeInfo;
    }

    /// <summary>
    /// Emits flags combination: left | right (bitwise OR of two flags values).
    /// </summary>
    private string EmitFlagsCombine(StringBuilder sb, BinaryExpression binary)
    {
        string left = EmitExpression(sb, binary.Left);
        string right = EmitExpression(sb, binary.Right);
        string result = NextTemp();
        EmitLine(sb, $"  {result} = or i64 {left}, {right}");
        return result;
    }

    /// <summary>
    /// Emits 'in' / 'notin' by calling the right operand's __contains__ / __notcontains__ method.
    /// </summary>
    private string EmitContainsCall(StringBuilder sb, BinaryExpression binary, string methodName)
    {
        // 'x in collection' → collection.__contains__(x)
        string collection = EmitExpression(sb, binary.Right);
        string element = EmitExpression(sb, binary.Left);

        TypeInfo? collectionType = GetExpressionType(binary.Right);
        if (collectionType == null)
        {
            throw new InvalidOperationException("Cannot determine collection type for 'in'/'notin' operator");
        }

        string methodFullName = $"{collectionType.Name}.{methodName}";
        RoutineInfo? method = _registry.LookupRoutine(methodFullName);

        var argValues = new List<string> { collection, element };
        var argTypes = new List<string> { GetParameterLLVMType(collectionType) };

        TypeInfo? elemType = GetExpressionType(binary.Left);
        argTypes.Add(elemType != null ? GetLLVMType(elemType) : "i64");

        string mangledName = method != null
            ? MangleFunctionName(method)
            : $"{MangleTypeName(collectionType.Name)}_{methodName}";

        string result = NextTemp();
        string args = BuildCallArgs(argTypes, argValues);
        EmitLine(sb, $"  {result} = call i1 @{mangledName}({args})");
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
    /// Minus and BitwiseNot are emitted as method calls to __neg__ / __not__
    /// so the stdlib bodies (which call LLVM intrinsics) do the actual work.
    /// </summary>
    private string EmitUnaryOp(StringBuilder sb, UnaryExpression unary)
    {
        return unary.Operator switch
        {
            UnaryOperator.Not => EmitLogicalNot(sb, unary),
            UnaryOperator.Minus => EmitUnaryMethodCall(sb, unary, "__neg__"),
            UnaryOperator.BitwiseNot => EmitUnaryMethodCall(sb, unary, "__not__"),
            UnaryOperator.Steal => EmitExpression(sb, unary.Operand),
            UnaryOperator.ForceUnwrap => EmitForceUnwrap(sb, unary),
            _ => throw new NotImplementedException(
                $"Unary operator '{unary.Operator}' codegen not implemented")
        };
    }

    /// <summary>
    /// Emits logical not: xor i1 %val, true.
    /// </summary>
    private string EmitLogicalNot(StringBuilder sb, UnaryExpression unary)
    {
        string operand = EmitExpression(sb, unary.Operand);
        string result = NextTemp();
        EmitLine(sb, $"  {result} = xor i1 {operand}, true");
        return result;
    }

    /// <summary>
    /// Emits a unary operator as a method call (e.g., -x → x.__neg__(), ~x → x.__not__()).
    /// </summary>
    private string EmitUnaryMethodCall(StringBuilder sb, UnaryExpression unary, string methodName)
    {
        string operand = EmitExpression(sb, unary.Operand);
        TypeInfo? operandType = GetExpressionType(unary.Operand);

        if (operandType == null)
        {
            throw new InvalidOperationException(
                $"Cannot determine operand type for unary operator '{unary.Operator}'");
        }

        string methodFullName = $"{operandType.Name}.{methodName}";
        RoutineInfo? method = _registry.LookupRoutine(methodFullName);

        // Build call: receiver (me) is the only argument
        var argValues = new List<string> { operand };
        var argTypes = new List<string> { GetParameterLLVMType(operandType) };

        string mangledName = method != null
            ? MangleFunctionName(method)
            : $"{MangleTypeName(operandType.Name)}_{methodName}";

        string returnType = method?.ReturnType != null
            ? GetLLVMType(method.ReturnType)
            : GetLLVMType(operandType);

        string result = NextTemp();
        string args = BuildCallArgs(argTypes, argValues);
        EmitLine(sb, $"  {result} = call {returnType} @{mangledName}({args})");
        return result;
    }

    #region Generic Method Calls (C21)

    /// <summary>
    /// Generates code for a generic method call expression.
    /// Handles LLVM intrinsic routines (CallingConvention == "llvm") by emitting
    /// LLVM IR instructions directly, and regular generic calls by resolving type
    /// arguments and calling the mangled function.
    /// </summary>
    private string EmitGenericMethodCall(StringBuilder sb, GenericMethodCallExpression generic)
    {
        // Resolve the receiver type and look up the method
        TypeInfo? receiverType = GetExpressionType(generic.Object);

        // Try method lookup: "Type.MethodName" for methods, or standalone "MethodName"
        string methodFullName = receiverType != null
            ? $"{receiverType.Name}.{generic.MethodName}"
            : generic.MethodName;

        RoutineInfo? method = _registry.LookupRoutine(methodFullName)
                              ?? _registry.LookupRoutine(generic.MethodName);

        // If this is an LLVM intrinsic, emit directly as LLVM IR
        if (method is { CallingConvention: "llvm" })
        {
            return EmitLlvmIntrinsicGenericCall(sb, generic, method);
        }

        // Otherwise, emit as a regular generic method call
        return EmitRegularGenericMethodCall(sb, generic, method, receiverType);
    }

    /// <summary>
    /// Emits an LLVM intrinsic generic call by resolving type arguments to LLVM types
    /// and delegating to EmitLlvmInstruction.
    /// </summary>
    private string EmitLlvmIntrinsicGenericCall(StringBuilder sb, GenericMethodCallExpression generic, RoutineInfo method)
    {
        // Evaluate all arguments
        var args = new List<string>();
        foreach (var arg in generic.Arguments)
        {
            args.Add(EmitExpression(sb, arg));
        }

        // Also evaluate the receiver if this is a method call (it becomes 'me')
        string? receiver = null;
        if (generic.Object is not IdentifierExpression)
        {
            receiver = EmitExpression(sb, generic.Object);
        }

        // Resolve type arguments to LLVM types
        var llvmTypeArgs = new List<string>();
        foreach (var typeArg in generic.TypeArguments)
        {
            llvmTypeArgs.Add(ResolveTypeExpressionToLLVM(typeArg));
        }

        if (llvmTypeArgs.Count == 0)
        {
            throw new InvalidOperationException(
                $"LLVM intrinsic call to '{generic.MethodName}' requires type arguments");
        }

        string llvmType = llvmTypeArgs[0];

        // Get the LLVM instruction name (may differ from routine name via @llvm_ir annotation)
        string instrName = GetLlvmIrName(method);

        // Build full arg list: receiver first if method call, then explicit args
        var allArgs = new List<string>();
        if (receiver != null)
        {
            allArgs.Add(receiver);
        }
        allArgs.AddRange(args);

        // All LLVM intrinsics use template molds with {holes} for substitution
        return EmitFromTemplate(sb, instrName, method, llvmTypeArgs, allArgs);
    }

    /// <summary>
    /// Emits LLVM IR from a template mold string with {hole} substitution.
    /// Supports multi-line templates (for overflow intrinsics, etc.).
    /// </summary>
    /// <param name="sb">StringBuilder to emit code to.</param>
    /// <param name="mold">The template mold string with {holes}.</param>
    /// <param name="method">The routine info for generic parameter name resolution.</param>
    /// <param name="llvmTypeArgs">Resolved LLVM type arguments.</param>
    /// <param name="args">Emitted argument values.</param>
    /// <returns>The last {result} temp, or args[0] if no {result} in any line.</returns>
    private string EmitFromTemplate(StringBuilder sb, string mold, RoutineInfo method,
        List<string> llvmTypeArgs, List<string> args)
    {
        string[] lines = mold.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string? lastResult = null;
        string? prevResult = null;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            string currentResult = NextTemp();
            bool hasResult = line.Contains("{result}");

            // Perform substitutions
            string substituted = line;

            // {result} → current SSA temp
            substituted = substituted.Replace("{result}", currentResult);

            // {prev} → previous line's {result}
            if (prevResult != null)
                substituted = substituted.Replace("{prev}", prevResult);

            // Named type parameters from GenericParameters: {T}, {From}, {To}, etc.
            // Must be done before parameter names to avoid collisions (e.g. {T} vs {type})
            if (method.GenericParameters != null)
            {
                for (int i = 0; i < method.GenericParameters.Count && i < llvmTypeArgs.Count; i++)
                {
                    string paramName = method.GenericParameters[i];
                    substituted = substituted.Replace($"{{{paramName}}}", llvmTypeArgs[i]);

                    // {sizeof T} → byte size
                    substituted = substituted.Replace($"{{sizeof {paramName}}}",
                        (GetTypeBitWidth(llvmTypeArgs[i]) / 8).ToString());
                }
            }

            // Named parameter substitution: {paramName} → args[i]
            // Parameter names come from method.Parameters, args are positional
            for (int i = 0; i < method.Parameters.Count && i < args.Count; i++)
            {
                string paramName = method.Parameters[i].Name;
                substituted = substituted.Replace($"{{{paramName}}}", args[i]);
            }

            EmitLine(sb, $"  {substituted}");

            if (hasResult)
            {
                prevResult = currentResult;
                lastResult = currentResult;
            }
        }

        // Return last {result} temp, or first arg if no {result} in template
        return lastResult ?? (args.Count > 0 ? args[0] : "undef");
    }

    /// <summary>
    /// Emits a regular (non-LLVM-intrinsic) generic method call.
    /// </summary>
    private string EmitRegularGenericMethodCall(StringBuilder sb, GenericMethodCallExpression generic,
        RoutineInfo? method, TypeInfo? receiverType)
    {
        // Evaluate the receiver
        string receiver = EmitExpression(sb, generic.Object);

        // Build argument list: receiver first (becomes 'me'), then explicit arguments
        var argValues = new List<string> { receiver };
        var argTypes = new List<string> { receiverType != null ? GetParameterLLVMType(receiverType) : "ptr" };

        foreach (var arg in generic.Arguments)
        {
            string value = EmitExpression(sb, arg);
            argValues.Add(value);

            TypeInfo? argType = GetExpressionType(arg);
            if (argType == null)
            {
                throw new InvalidOperationException(
                    $"Cannot determine type for argument in generic method call to '{generic.MethodName}'");
            }
            argTypes.Add(GetLLVMType(argType));
        }

        // Build the call
        string mangledName = method != null
            ? MangleFunctionName(method)
            : receiverType != null
                ? $"{MangleTypeName(receiverType.Name)}_{generic.MethodName}"
                : generic.MethodName;

        string returnType = method?.ReturnType != null
            ? GetLLVMType(method.ReturnType)
            : "void";

        if (returnType == "void")
        {
            string args = BuildCallArgs(argTypes, argValues);
            EmitLine(sb, $"  call void @{mangledName}({args})");
            return "undef";
        }
        else
        {
            string result = NextTemp();
            string args = BuildCallArgs(argTypes, argValues);
            EmitLine(sb, $"  {result} = call {returnType} @{mangledName}({args})");
            return result;
        }
    }

    /// <summary>
    /// Gets the LLVM IR instruction name for a routine, checking for @llvm_ir("name") annotation.
    /// Falls back to the routine name if no annotation is present.
    /// </summary>
    private static string GetLlvmIrName(RoutineInfo routine)
    {
        foreach (var annotation in routine.Annotations)
        {
            if (annotation.StartsWith("llvm_ir("))
            {
                // Extract: llvm_ir("name") → name
                int start = annotation.IndexOf('"') + 1;
                int end = annotation.LastIndexOf('"');
                if (start > 0 && end > start)
                {
                    return annotation[start..end];
                }
            }
        }

        return routine.Name;
    }

    /// <summary>
    /// Resolves a TypeExpression (AST node) to its LLVM type string.
    /// </summary>
    private string ResolveTypeExpressionToLLVM(TypeExpression typeExpr)
    {
        // Look up the type in the registry
        TypeInfo? type = _registry.LookupType(typeExpr.Name);
        if (type != null)
        {
            return GetLLVMType(type);
        }

        // Fall back: return the name as-is (assumes it's already an LLVM type name)
        return typeExpr.Name;
    }

    /// <summary>
    /// Gets the return type of a generic method call expression.
    /// </summary>
    private TypeInfo? GetGenericMethodCallReturnType(GenericMethodCallExpression generic)
    {
        TypeInfo? receiverType = GetExpressionType(generic.Object);

        string methodFullName = receiverType != null
            ? $"{receiverType.Name}.{generic.MethodName}"
            : generic.MethodName;

        RoutineInfo? method = _registry.LookupRoutine(methodFullName)
                              ?? _registry.LookupRoutine(generic.MethodName);

        return method?.ReturnType;
    }

    #endregion

    /// <summary>
    /// Gets the type of an expression (from semantic analysis metadata).
    /// </summary>
    private TypeInfo? GetExpressionType(Expression expr)
    {
        // First, check if the semantic analyzer has already resolved the type
        if (expr.ResolvedType != null)
        {
            return expr.ResolvedType;
        }

        // Fall back to inferring from the expression structure
        return expr switch
        {
            LiteralExpression literal => GetLiteralType(literal),
            IdentifierExpression id => _localVariables.TryGetValue(id.Name, out var varType) ? varType : _registry.LookupVariable(id.Name)?.Type,
            MemberExpression member => GetMemberType(member),
            CreatorExpression ctor => _registry.LookupType(ctor.TypeName),
            BinaryExpression binary => GetExpressionType(binary.Left), // Use left operand type
            UnaryExpression unary => GetExpressionType(unary.Operand),
            CallExpression call => GetCallReturnType(call),
            GenericMethodCallExpression generic => GetGenericMethodCallReturnType(generic),
            _ => null
        };
    }

    /// <summary>
    /// Gets the return type of a call expression.
    /// </summary>
    private TypeInfo? GetCallReturnType(CallExpression call)
    {
        string funcName = call.Callee switch
        {
            IdentifierExpression id => id.Name,
            MemberExpression member => member.PropertyName,
            _ => null
        } ?? string.Empty;

        var routine = _registry.LookupRoutine(funcName);
        return routine?.ReturnType;
    }

    /// <summary>
    /// Gets the type of a literal expression from its token type.
    /// </summary>
    private TypeInfo? GetLiteralType(LiteralExpression literal)
    {
        string? typeName = literal.LiteralType switch
        {
            Lexer.TokenType.S8Literal => "S8",
            Lexer.TokenType.S16Literal => "S16",
            Lexer.TokenType.S32Literal => "S32",
            Lexer.TokenType.S64Literal => "S64",
            Lexer.TokenType.S128Literal => "S128",
            Lexer.TokenType.U8Literal => "U8",
            Lexer.TokenType.U16Literal => "U16",
            Lexer.TokenType.U32Literal => "U32",
            Lexer.TokenType.U64Literal => "U64",
            Lexer.TokenType.U128Literal => "U128",
            Lexer.TokenType.F16Literal => "F16",
            Lexer.TokenType.F32Literal => "F32",
            Lexer.TokenType.F64Literal => "F64",
            Lexer.TokenType.F128Literal => "F128",
            Lexer.TokenType.D32Literal => "D32",
            Lexer.TokenType.D64Literal => "D64",
            Lexer.TokenType.D128Literal => "D128",
            Lexer.TokenType.True or Lexer.TokenType.False => "Bool",
            Lexer.TokenType.TextLiteral => "Text",
            _ => null
        };

        return typeName != null ? _registry.LookupType(typeName) : null;
    }

    /// <summary>
    /// Gets the type of a member access expression.
    /// </summary>
    private TypeInfo? GetMemberType(MemberExpression member)
    {
        TypeInfo? targetType = GetExpressionType(member.Object);
        if (targetType == null) return null;

        MemberVariableInfo? memberVariable = targetType switch
        {
            EntityTypeInfo e => e.LookupMemberVariable(member.PropertyName),
            RecordTypeInfo r => r.LookupMemberVariable(member.PropertyName),
            ResidentTypeInfo res => res.LookupMemberVariable(member.PropertyName),
            _ => null
        };

        return memberVariable?.Type;
    }

    #endregion

    /// <summary>
    /// Gets the bit width of an LLVM type.
    /// </summary>
    private int GetTypeBitWidth(string llvmType)
    {
        return llvmType switch
        {
            "i1" => 1,
            "i8" => 8,
            "i16" => 16,
            "i32" => 32,
            "i64" => 64,
            "i128" => 128,
            "half" => 16,
            "float" => 32,
            "double" => 64,
            "fp128" => 128,
            "ptr" => _pointerBitWidth,
            _ => throw new InvalidOperationException($"Unknown LLVM type for bitwidth: {llvmType}")
        };
    }

    #region Additional Expression Types

    /// <summary>
    /// Generates code for a conditional (ternary) expression.
    /// </summary>
    private string EmitConditional(StringBuilder sb, ConditionalExpression cond)
    {
        string condition = EmitExpression(sb, cond.Condition);

        string thenLabel = NextLabel("cond_then");
        string elseLabel = NextLabel("cond_else");
        string endLabel = NextLabel("cond_end");

        EmitLine(sb, $"  br i1 {condition}, label %{thenLabel}, label %{elseLabel}");

        // Then branch
        EmitLine(sb, $"{thenLabel}:");
        string thenValue = EmitExpression(sb, cond.TrueExpression);
        EmitLine(sb, $"  br label %{endLabel}");

        // Else branch
        EmitLine(sb, $"{elseLabel}:");
        string elseValue = EmitExpression(sb, cond.FalseExpression);
        EmitLine(sb, $"  br label %{endLabel}");

        // Merge with phi
        EmitLine(sb, $"{endLabel}:");
        string result = NextTemp();
        TypeInfo? resultType = GetExpressionType(cond.TrueExpression);
        if (resultType == null)
        {
            throw new InvalidOperationException("Cannot determine type for conditional expression");
        }
        string llvmType = GetLLVMType(resultType);
        EmitLine(sb, $"  {result} = phi {llvmType} [ {thenValue}, %{thenLabel} ], [ {elseValue}, %{elseLabel} ]");

        return result;
    }

    /// <summary>
    /// Generates code for an index access expression (e.g., list[i]).
    /// </summary>
    private string EmitIndexAccess(StringBuilder sb, IndexExpression index)
    {
        string target = EmitExpression(sb, index.Object);
        string indexValue = EmitExpression(sb, index.Index);

        // Resolve element type from container's generic type arguments
        TypeInfo? targetType = GetExpressionType(index.Object);
        string elemType = targetType switch
        {
            RecordTypeInfo r when r.TypeArguments.Count > 0 => GetLLVMType(r.TypeArguments[0]),
            EntityTypeInfo e when e.TypeArguments.Count > 0 => GetLLVMType(e.TypeArguments[0]),
            _ => throw new InvalidOperationException(
                $"Cannot determine element type for indexing on type: {targetType?.Name}")
        };

        string elemPtr = NextTemp();
        string result = NextTemp();
        EmitLine(sb, $"  {elemPtr} = getelementptr {elemType}, ptr {target}, i64 {indexValue}");
        EmitLine(sb, $"  {result} = load {elemType}, ptr {elemPtr}");

        return result;
    }

    /// <summary>
    /// Generates code for a slice expression: obj[start to end] → __getslice__(start, end)
    /// </summary>
    private string EmitSliceAccess(StringBuilder sb, SliceExpression slice)
    {
        string target = EmitExpression(sb, slice.Object);
        string start = EmitExpression(sb, slice.Start);
        string end = EmitExpression(sb, slice.End);

        TypeInfo? targetType = GetExpressionType(slice.Object);
        if (targetType == null)
        {
            throw new InvalidOperationException("Cannot determine type for slice target");
        }

        string methodFullName = $"{targetType.Name}.__getslice__";
        RoutineInfo? method = _registry.LookupRoutine(methodFullName);

        var argValues = new List<string> { target, start, end };
        var argTypes = new List<string> { GetParameterLLVMType(targetType), "i64", "i64" };

        string mangledName = method != null
            ? MangleFunctionName(method)
            : $"{MangleTypeName(targetType.Name)}___getslice__";

        string returnType = method?.ReturnType != null
            ? GetLLVMType(method.ReturnType)
            : GetParameterLLVMType(targetType);

        string result = NextTemp();
        string args = BuildCallArgs(argTypes, argValues);
        EmitLine(sb, $"  {result} = call {returnType} @{mangledName}({args})");
        return result;
    }

    /// <summary>
    /// Generates code for a range expression.
    /// </summary>
    private string EmitRange(StringBuilder sb, RangeExpression range)
    {
        // Emit start, end, step expressions
        string start = EmitExpression(sb, range.Start);
        string end = EmitExpression(sb, range.End);
        string step = range.Step != null ? EmitExpression(sb, range.Step) : "1";
        string isDescending = range.IsDescending ? "true" : "false";

        // Infer element type from start/end expressions (Range[T] is generic)
        TypeInfo? elemType = GetExpressionType(range.Start) ?? GetExpressionType(range.End);
        string elemLlvmType = elemType != null ? GetLLVMType(elemType) : "i64";

        // Try to use registered Range type, fall back to literal struct
        TypeInfo? rangeType = _registry.LookupType("Range");
        string structType;
        if (rangeType is RecordTypeInfo rangeRecord)
        {
            structType = GetRecordTypeName(rangeRecord);
        }
        else
        {
            structType = $"{{ {elemLlvmType}, {elemLlvmType}, {elemLlvmType}, i1 }}";
        }

        // Build struct via insertvalue chain: { start, end, step, is_descending }
        string v0 = NextTemp();
        EmitLine(sb, $"  {v0} = insertvalue {structType} undef, {elemLlvmType} {start}, 0");
        string v1 = NextTemp();
        EmitLine(sb, $"  {v1} = insertvalue {structType} {v0}, {elemLlvmType} {end}, 1");
        string v2 = NextTemp();
        EmitLine(sb, $"  {v2} = insertvalue {structType} {v1}, {elemLlvmType} {step}, 2");
        string v3 = NextTemp();
        EmitLine(sb, $"  {v3} = insertvalue {structType} {v2}, i1 {isDescending}, 3");

        return v3;
    }

    /// <summary>
    /// Generates code for a steal expression (ownership transfer).
    /// </summary>
    /// <remarks>
    /// The steal keyword transfers ownership from the source to the destination.
    /// At runtime, this is essentially a pass-through - the ownership tracking
    /// is handled at compile time by the semantic analyzer, which marks the
    /// source as a deadref after the steal.
    ///
    /// Stealable types:
    /// - Raw entities (ownership transferred)
    /// - Shared[T] (reference count transferred)
    /// - Tracked[T] (weak reference transferred)
    ///
    /// Non-stealable types (caught by semantic analyzer):
    /// - Scope-bound tokens (Viewed, Hijacked, Inspected, Seized)
    /// - Snatched[T] (internal ownership type)
    /// </remarks>
    private string EmitSteal(StringBuilder sb, StealExpression steal)
    {
        // Steal just evaluates the operand and passes the value through.
        // The semantic analyzer has already validated that:
        // 1. The operand is a stealable type
        // 2. The source will be marked as deadref after this point
        return EmitExpression(sb, steal.Operand);
    }

    /// <summary>
    /// Generates code for a tuple literal expression.
    /// Creates a ValueTuple (for pure value types) or Tuple (for mixed/reference types).
    /// </summary>
    /// <param name="sb">StringBuilder to emit code to.</param>
    /// <param name="tuple">The tuple literal expression.</param>
    /// <returns>The temporary variable holding the tuple pointer.</returns>
    /// <remarks>
    /// Tuple layout:
    /// - ValueTuple: stack-allocated struct with member variables item0, item1, ...
    /// - Tuple: heap-allocated entity with member variables item0, item1, ...
    ///
    /// The semantic analyzer determines which type to use based on element types.
    /// </remarks>
    private string EmitTupleLiteral(StringBuilder sb, TupleLiteralExpression tuple)
    {
        // Evaluate all element expressions
        var elemValues = new List<string>();
        var elemLLVMTypes = new List<string>();
        foreach (var element in tuple.Elements)
        {
            elemValues.Add(EmitExpression(sb, element));
            TypeInfo? elemType = GetExpressionType(element);
            elemLLVMTypes.Add(elemType != null ? GetLLVMType(elemType) : "i64");
        }

        // Resolve tuple type from semantic analysis
        TupleTypeInfo? tupleType = tuple.ResolvedType as TupleTypeInfo;

        if (tupleType == null || tupleType.Kind == TupleKind.Value || tupleType.Kind == TupleKind.Fixed)
        {
            // ValueTuple / FixedTuple: build struct via insertvalue chain
            string structType;
            if (tupleType != null)
            {
                structType = GetTupleTypeName(tupleType);
            }
            else
            {
                // Fall back to anonymous struct type
                structType = $"{{ {string.Join(", ", elemLLVMTypes)} }}";
            }

            string result = "undef";
            for (int i = 0; i < elemValues.Count; i++)
            {
                string newResult = NextTemp();
                EmitLine(sb, $"  {newResult} = insertvalue {structType} {result}, {elemLLVMTypes[i]} {elemValues[i]}, {i}");
                result = newResult;
            }

            return result;
        }
        else
        {
            // Reference Tuple: heap-allocate via rf_alloc, GEP + store each element
            int size = CalculateTupleSize(tupleType);
            string structType = GetTupleTypeName(tupleType);

            string rawPtr = NextTemp();
            EmitLine(sb, $"  {rawPtr} = call ptr @rf_alloc(i64 {size})");

            for (int i = 0; i < elemValues.Count; i++)
            {
                string elemPtr = NextTemp();
                EmitLine(sb, $"  {elemPtr} = getelementptr {structType}, ptr {rawPtr}, i32 0, i32 {i}");
                EmitLine(sb, $"  store {elemLLVMTypes[i]} {elemValues[i]}, ptr {elemPtr}");
            }

            return rawPtr;
        }
    }

    #endregion

    #region Error Handling Operators

    /// <summary>
    /// Emits the ?? (none coalesce) operator.
    /// If the left operand (ErrorHandlingTypeInfo) is VALID, extracts its value.
    /// Otherwise evaluates and returns the right operand as default.
    /// </summary>
    private string EmitNoneCoalesce(StringBuilder sb, BinaryExpression binary)
    {
        string left = EmitExpression(sb, binary.Left);
        TypeInfo? leftType = GetExpressionType(binary.Left);

        if (leftType is not ErrorHandlingTypeInfo errorType)
        {
            throw new InvalidOperationException("'??' operator requires ErrorHandlingTypeInfo on the left");
        }

        // Alloca and store the { i64, ptr } value
        string allocaPtr = NextTemp();
        EmitLine(sb, $"  {allocaPtr} = alloca {{ i64, ptr }}");
        EmitLine(sb, $"  store {{ i64, ptr }} {left}, ptr {allocaPtr}");

        // Extract tag (field 0)
        string tagPtr = NextTemp();
        string tag = NextTemp();
        EmitLine(sb, $"  {tagPtr} = getelementptr {{ i64, ptr }}, ptr {allocaPtr}, i32 0, i32 0");
        EmitLine(sb, $"  {tag} = load i64, ptr {tagPtr}");

        // Check tag == 1 (VALID)
        string isValid = NextTemp();
        EmitLine(sb, $"  {isValid} = icmp eq i64 {tag}, 1");

        string valLabel = NextLabel("coalesce_val");
        string rhsLabel = NextLabel("coalesce_rhs");
        string endLabel = NextLabel("coalesce_end");
        string leftBlock = _currentBlock;

        EmitLine(sb, $"  br i1 {isValid}, label %{valLabel}, label %{rhsLabel}");

        // Valid path: extract the value from the handle
        EmitLine(sb, $"{valLabel}:");
        _currentBlock = valLabel;
        string handlePtr = NextTemp();
        string handleVal = NextTemp();
        EmitLine(sb, $"  {handlePtr} = getelementptr {{ i64, ptr }}, ptr {allocaPtr}, i32 0, i32 1");
        EmitLine(sb, $"  {handleVal} = load ptr, ptr {handlePtr}");

        // Determine the value type T
        TypeInfo valueType = errorType.ValueType;
        string llvmValueType = GetLLVMType(valueType);

        // Load T from the handle pointer
        string validValue = NextTemp();
        EmitLine(sb, $"  {validValue} = load {llvmValueType}, ptr {handleVal}");
        string valBlock = _currentBlock;
        EmitLine(sb, $"  br label %{endLabel}");

        // RHS path: evaluate the default expression
        EmitLine(sb, $"{rhsLabel}:");
        _currentBlock = rhsLabel;
        string rhsValue = EmitExpression(sb, binary.Right);
        string rhsBlock = _currentBlock;
        EmitLine(sb, $"  br label %{endLabel}");

        // Merge with PHI
        EmitLine(sb, $"{endLabel}:");
        _currentBlock = endLabel;
        string result = NextTemp();
        EmitLine(sb, $"  {result} = phi {llvmValueType} [ {validValue}, %{valBlock} ], [ {rhsValue}, %{rhsBlock} ]");

        return result;
    }

    /// <summary>
    /// Emits the !! (force unwrap) operator.
    /// If the operand (ErrorHandlingTypeInfo) is VALID, extracts its value.
    /// Otherwise traps (crashes the program).
    /// </summary>
    private string EmitForceUnwrap(StringBuilder sb, UnaryExpression unary)
    {
        string operand = EmitExpression(sb, unary.Operand);
        TypeInfo? operandType = GetExpressionType(unary.Operand);

        if (operandType is not ErrorHandlingTypeInfo errorType)
        {
            throw new InvalidOperationException("'!!' operator requires ErrorHandlingTypeInfo operand");
        }

        // Declare llvm.trap if not already declared
        if (_declaredNativeFunctions.Add("llvm.trap"))
        {
            EmitLine(_functionDeclarations, "declare void @llvm.trap() noreturn nounwind");
        }

        // Alloca and store the { i64, ptr } value
        string allocaPtr = NextTemp();
        EmitLine(sb, $"  {allocaPtr} = alloca {{ i64, ptr }}");
        EmitLine(sb, $"  store {{ i64, ptr }} {operand}, ptr {allocaPtr}");

        // Extract tag (field 0)
        string tagPtr = NextTemp();
        string tag = NextTemp();
        EmitLine(sb, $"  {tagPtr} = getelementptr {{ i64, ptr }}, ptr {allocaPtr}, i32 0, i32 0");
        EmitLine(sb, $"  {tag} = load i64, ptr {tagPtr}");

        // Check tag == 1 (VALID)
        string isValid = NextTemp();
        EmitLine(sb, $"  {isValid} = icmp eq i64 {tag}, 1");

        string okLabel = NextLabel("unwrap_ok");
        string failLabel = NextLabel("unwrap_fail");

        EmitLine(sb, $"  br i1 {isValid}, label %{okLabel}, label %{failLabel}");

        // Fail path: trap
        EmitLine(sb, $"{failLabel}:");
        _currentBlock = failLabel;
        EmitLine(sb, $"  call void @llvm.trap()");
        EmitLine(sb, $"  unreachable");

        // OK path: extract the value from the handle
        EmitLine(sb, $"{okLabel}:");
        _currentBlock = okLabel;
        string handlePtr = NextTemp();
        string handleVal = NextTemp();
        EmitLine(sb, $"  {handlePtr} = getelementptr {{ i64, ptr }}, ptr {allocaPtr}, i32 0, i32 1");
        EmitLine(sb, $"  {handleVal} = load ptr, ptr {handlePtr}");

        // Load T from the handle pointer
        TypeInfo valueType = errorType.ValueType;
        string llvmValueType = GetLLVMType(valueType);
        string result = NextTemp();
        EmitLine(sb, $"  {result} = load {llvmValueType}, ptr {handleVal}");

        return result;
    }

    #endregion

    #region Optional Chaining

    /// <summary>
    /// Emits optional member access (?.): obj?.field
    /// If obj is null/none, produces a zero/null value. Otherwise performs normal member access.
    /// </summary>
    private string EmitOptionalMemberAccess(StringBuilder sb, OptionalMemberExpression optMember)
    {
        string obj = EmitExpression(sb, optMember.Object);
        TypeInfo? objType = GetExpressionType(optMember.Object);

        if (objType is ErrorHandlingTypeInfo errorType)
        {
            return EmitOptionalChainErrorHandling(sb, obj, errorType, optMember.PropertyName);
        }

        // Entity/Resident (pointer): null check
        return EmitOptionalChainPointer(sb, obj, objType, optMember.PropertyName);
    }

    /// <summary>
    /// Optional chaining on a pointer-based type (entity/resident): null check → member access or zero.
    /// </summary>
    private string EmitOptionalChainPointer(StringBuilder sb, string obj, TypeInfo? objType, string propertyName)
    {
        string nonNullLabel = NextLabel("optchain_nonnull");
        string nullLabel = NextLabel("optchain_null");
        string endLabel = NextLabel("optchain_end");
        string entryBlock = _currentBlock;

        // Null check
        string isNull = NextTemp();
        EmitLine(sb, $"  {isNull} = icmp eq ptr {obj}, null");
        EmitLine(sb, $"  br i1 {isNull}, label %{nullLabel}, label %{nonNullLabel}");

        // Non-null path: do normal member access
        EmitLine(sb, $"{nonNullLabel}:");
        _currentBlock = nonNullLabel;
        string memberValue = EmitMemberAccessOnType(sb, obj, objType, propertyName);
        string memberBlock = _currentBlock;

        // Determine result type from member access
        TypeInfo? resultType = GetMemberTypeFromOwner(objType, propertyName);
        string llvmResultType = resultType != null ? GetLLVMType(resultType) : "ptr";
        string zeroValue = resultType != null ? GetZeroValue(resultType) : "null";

        EmitLine(sb, $"  br label %{endLabel}");

        // Null path: return zero/null
        EmitLine(sb, $"{nullLabel}:");
        _currentBlock = nullLabel;
        EmitLine(sb, $"  br label %{endLabel}");

        // Merge
        EmitLine(sb, $"{endLabel}:");
        _currentBlock = endLabel;
        string result = NextTemp();
        EmitLine(sb, $"  {result} = phi {llvmResultType} [ {memberValue}, %{memberBlock} ], [ {zeroValue}, %{nullLabel} ]");

        return result;
    }

    /// <summary>
    /// Optional chaining on an ErrorHandlingTypeInfo: check VALID → extract value → member access, or zero.
    /// </summary>
    private string EmitOptionalChainErrorHandling(StringBuilder sb, string obj, ErrorHandlingTypeInfo errorType, string propertyName)
    {
        // Alloca and store the { i64, ptr } value
        string allocaPtr = NextTemp();
        EmitLine(sb, $"  {allocaPtr} = alloca {{ i64, ptr }}");
        EmitLine(sb, $"  store {{ i64, ptr }} {obj}, ptr {allocaPtr}");

        // Extract tag
        string tagPtr = NextTemp();
        string tag = NextTemp();
        EmitLine(sb, $"  {tagPtr} = getelementptr {{ i64, ptr }}, ptr {allocaPtr}, i32 0, i32 0");
        EmitLine(sb, $"  {tag} = load i64, ptr {tagPtr}");

        string isValid = NextTemp();
        EmitLine(sb, $"  {isValid} = icmp eq i64 {tag}, 1");

        string validLabel = NextLabel("optchain_valid");
        string invalidLabel = NextLabel("optchain_invalid");
        string endLabel = NextLabel("optchain_end");

        EmitLine(sb, $"  br i1 {isValid}, label %{validLabel}, label %{invalidLabel}");

        // Valid path: extract value and do member access
        EmitLine(sb, $"{validLabel}:");
        _currentBlock = validLabel;
        string handlePtr = NextTemp();
        string handleVal = NextTemp();
        EmitLine(sb, $"  {handlePtr} = getelementptr {{ i64, ptr }}, ptr {allocaPtr}, i32 0, i32 1");
        EmitLine(sb, $"  {handleVal} = load ptr, ptr {handlePtr}");

        TypeInfo valueType = errorType.ValueType;
        string llvmValueType = GetLLVMType(valueType);
        string innerValue = NextTemp();
        EmitLine(sb, $"  {innerValue} = load {llvmValueType}, ptr {handleVal}");

        // Now do member access on the extracted value
        string memberValue = EmitMemberAccessOnType(sb, innerValue, valueType, propertyName);
        string validBlock = _currentBlock;

        // Determine result type
        TypeInfo? resultType = GetMemberTypeFromOwner(valueType, propertyName);
        string llvmResultType = resultType != null ? GetLLVMType(resultType) : "ptr";
        string zeroValue = resultType != null ? GetZeroValue(resultType) : "null";

        EmitLine(sb, $"  br label %{endLabel}");

        // Invalid path
        EmitLine(sb, $"{invalidLabel}:");
        _currentBlock = invalidLabel;
        EmitLine(sb, $"  br label %{endLabel}");

        // Merge
        EmitLine(sb, $"{endLabel}:");
        _currentBlock = endLabel;
        string result = NextTemp();
        EmitLine(sb, $"  {result} = phi {llvmResultType} [ {memberValue}, %{validBlock} ], [ {zeroValue}, %{invalidLabel} ]");

        return result;
    }

    /// <summary>
    /// Performs member access on a value given its type, reusing existing member read logic.
    /// </summary>
    private string EmitMemberAccessOnType(StringBuilder sb, string value, TypeInfo? type, string propertyName)
    {
        return type switch
        {
            EntityTypeInfo entity => EmitEntityMemberVariableRead(sb, value, entity, propertyName),
            RecordTypeInfo record => EmitRecordMemberVariableRead(sb, value, record, propertyName),
            ResidentTypeInfo resident => EmitResidentMemberVariableRead(sb, value, resident, propertyName),
            _ => throw new InvalidOperationException($"Cannot access member on type: {type?.Name}")
        };
    }

    /// <summary>
    /// Gets the type of a member variable from the owning type.
    /// </summary>
    private TypeInfo? GetMemberTypeFromOwner(TypeInfo? ownerType, string memberName)
    {
        IReadOnlyList<MemberVariableInfo>? members = ownerType switch
        {
            EntityTypeInfo entity => entity.MemberVariables,
            RecordTypeInfo record => record.MemberVariables,
            ResidentTypeInfo resident => resident.MemberVariables,
            _ => null
        };

        if (members == null) return null;

        foreach (var m in members)
        {
            if (m.Name == memberName) return m.Type;
        }
        return null;
    }

    #endregion

    #region Text Insertion (F-Strings)

    /// <summary>
    /// Emits an f-string (InsertedTextExpression).
    /// Concatenates text and expression parts via Text.__create__ and Text.concat calls.
    /// </summary>
    private string EmitInsertedText(StringBuilder sb, InsertedTextExpression inserted)
    {
        if (inserted.Parts.Count == 0)
        {
            return EmitStringLiteral(sb, "");
        }

        // Convert each part to a ptr (Text value)
        var partValues = new List<string>();
        foreach (var part in inserted.Parts)
        {
            switch (part)
            {
                case TextPart textPart:
                    partValues.Add(EmitStringLiteral(sb, textPart.Text));
                    break;
                case ExpressionPart exprPart:
                    partValues.Add(EmitInsertedTextPart(sb, exprPart));
                    break;
            }
        }

        if (partValues.Count == 1)
        {
            return partValues[0];
        }

        // Chain concat calls: acc = acc.concat(next)
        string accumulator = partValues[0];
        for (int i = 1; i < partValues.Count; i++)
        {
            string concatName = "Text.concat";
            RoutineInfo? concatMethod = _registry.LookupRoutine(concatName);

            string mangledConcat = concatMethod != null
                ? MangleFunctionName(concatMethod)
                : "Text_concat";

            string concatResult = NextTemp();
            EmitLine(sb, $"  {concatResult} = call ptr @{mangledConcat}(ptr {accumulator}, ptr {partValues[i]})");
            accumulator = concatResult;
        }

        return accumulator;
    }

    /// <summary>
    /// Emits a single expression part of an f-string, handling format specifiers.
    /// </summary>
    private string EmitInsertedTextPart(StringBuilder sb, ExpressionPart exprPart)
    {
        string exprValue = EmitExpression(sb, exprPart.Expression);
        TypeInfo? exprType = GetExpressionType(exprPart.Expression);
        string? formatSpec = exprPart.FormatSpec;

        // Handle "=" specifier: emit "name=value" (variable name prefix)
        if (formatSpec == "=")
        {
            // Emit the variable name as a prefix: "varname="
            string varName = exprPart.Expression is IdentifierExpression id ? id.Name : "expr";
            string prefix = EmitStringLiteral(sb, $"{varName}=");
            string valueText = EmitValueToText(sb, exprValue, exprType, null);

            // Concat: prefix + valueText
            string concatName = "Text.concat";
            RoutineInfo? concatMethod = _registry.LookupRoutine(concatName);
            string mangledConcat = concatMethod != null
                ? MangleFunctionName(concatMethod)
                : "Text_concat";
            string result = NextTemp();
            EmitLine(sb, $"  {result} = call ptr @{mangledConcat}(ptr {prefix}, ptr {valueText})");
            return result;
        }

        // Handle "!d" specifier: call to_debug() instead of Text.__create__
        if (formatSpec == "!d" || formatSpec == "d")
        {
            string typeName = exprType?.Name ?? "Data";
            string debugName = $"{typeName}.to_debug";
            RoutineInfo? debugMethod = _registry.LookupRoutine(debugName);

            string mangledDebug = debugMethod != null
                ? MangleFunctionName(debugMethod)
                : $"{MangleTypeName(typeName)}_to_debug";

            string argType = exprType != null ? GetParameterLLVMType(exprType) : "i64";
            string result = NextTemp();
            EmitLine(sb, $"  {result} = call ptr @{mangledDebug}({argType} {exprValue})");
            return result;
        }

        // For all other format specifiers (D2, E3, b, h, etc.): call rf_format with spec string
        // If no spec or Text type, use default conversion
        return EmitValueToText(sb, exprValue, exprType, formatSpec);
    }

    /// <summary>
    /// Converts a value to Text, optionally applying a format specifier.
    /// </summary>
    private string EmitValueToText(StringBuilder sb, string value, TypeInfo? type, string? formatSpec)
    {
        // If already Text, return directly
        if (type?.Name == "Text")
        {
            return value;
        }

        // If format spec is provided, call rf_format(value, spec_ptr) runtime function
        if (formatSpec != null)
        {
            if (_declaredNativeFunctions.Add("rf_format"))
            {
                EmitLine(_functionDeclarations, "declare ptr @rf_format(i64, ptr)");
            }

            string specPtr = EmitStringLiteral(sb, formatSpec);

            // Bitcast/extend value to i64 for the generic format call
            string argType = type != null ? GetLLVMType(type) : "i64";
            string i64Value;
            if (argType == "i64")
            {
                i64Value = value;
            }
            else if (argType is "i1" or "i8" or "i16" or "i32")
            {
                i64Value = NextTemp();
                EmitLine(sb, $"  {i64Value} = sext {argType} {value} to i64");
            }
            else if (argType is "float")
            {
                string dbl = NextTemp();
                EmitLine(sb, $"  {dbl} = fpext float {value} to double");
                i64Value = NextTemp();
                EmitLine(sb, $"  {i64Value} = bitcast double {dbl} to i64");
            }
            else if (argType is "double")
            {
                i64Value = NextTemp();
                EmitLine(sb, $"  {i64Value} = bitcast double {value} to i64");
            }
            else
            {
                // ptr or other: ptrtoint
                i64Value = NextTemp();
                EmitLine(sb, $"  {i64Value} = ptrtoint ptr {value} to i64");
            }

            string result = NextTemp();
            EmitLine(sb, $"  {result} = call ptr @rf_format(i64 {i64Value}, ptr {specPtr})");
            return result;
        }

        // Default: call Text.__create__(from: value)
        string createName = "Text.__create__";
        RoutineInfo? createMethod = _registry.LookupRoutine(createName);

        string llvmArgType = type != null ? GetLLVMType(type) : "i64";
        string mangledName = createMethod != null
            ? MangleFunctionName(createMethod)
            : "Text___create__";

        string textResult = NextTemp();
        EmitLine(sb, $"  {textResult} = call ptr @{mangledName}({llvmArgType} {value})");
        return textResult;
    }

    #endregion

    #region List Literals

    /// <summary>
    /// Emits a list literal expression: [1, 2, 3]
    /// Allocates a List entity and adds each element via add_last.
    /// </summary>
    private string EmitListLiteral(StringBuilder sb, ListLiteralExpression list)
    {
        // Determine element type from ResolvedType or first element
        TypeInfo? listType = list.ResolvedType;
        TypeInfo? elemType = null;

        if (listType is EntityTypeInfo entity && entity.TypeArguments.Count > 0)
        {
            elemType = entity.TypeArguments[0];
        }
        else if (list.Elements.Count > 0)
        {
            elemType = GetExpressionType(list.Elements[0]);
        }

        string elemLLVMType = elemType != null ? GetLLVMType(elemType) : "i64";

        // Look up List type and its constructor/add_last method
        string listTypeName = listType != null ? listType.Name : $"List[{elemType?.Name ?? "S64"}]";
        string mangledListType = MangleTypeName(listTypeName);

        // Allocate the list via constructor or rf_alloc
        // Try to find a __create__ or use a fallback allocation
        string createName = $"{listTypeName}.__create__";
        RoutineInfo? createMethod = _registry.LookupRoutine(createName);

        string listPtr;
        if (createMethod != null)
        {
            string mangledCreate = MangleFunctionName(createMethod);
            listPtr = NextTemp();
            EmitLine(sb, $"  {listPtr} = call ptr @{mangledCreate}()");
        }
        else
        {
            // Fallback: allocate via rf_alloc with a reasonable default size
            // List entity needs at least: count (i64) + capacity (i64) + data ptr
            listPtr = NextTemp();
            EmitLine(sb, $"  {listPtr} = call ptr @rf_alloc(i64 24)");

            // Initialize count = 0, capacity = element count, data = alloc(n * elem_size)
            string countPtr = NextTemp();
            EmitLine(sb, $"  {countPtr} = getelementptr {{ i64, i64, ptr }}, ptr {listPtr}, i32 0, i32 0");
            EmitLine(sb, $"  store i64 0, ptr {countPtr}");

            string capPtr = NextTemp();
            EmitLine(sb, $"  {capPtr} = getelementptr {{ i64, i64, ptr }}, ptr {listPtr}, i32 0, i32 1");
            long capacity = Math.Max(list.Elements.Count, 4);
            EmitLine(sb, $"  store i64 {capacity}, ptr {capPtr}");

            int elemSize = elemType != null ? GetTypeSize(elemType) : 8;
            string dataPtr = NextTemp();
            EmitLine(sb, $"  {dataPtr} = call ptr @rf_alloc(i64 {capacity * elemSize})");
            string dataPtrSlot = NextTemp();
            EmitLine(sb, $"  {dataPtrSlot} = getelementptr {{ i64, i64, ptr }}, ptr {listPtr}, i32 0, i32 2");
            EmitLine(sb, $"  store ptr {dataPtr}, ptr {dataPtrSlot}");
        }

        // Add each element via add_last or direct store
        string addLastName = $"{listTypeName}.add_last";
        RoutineInfo? addLastMethod = _registry.LookupRoutine(addLastName);

        if (addLastMethod != null)
        {
            string mangledAddLast = MangleFunctionName(addLastMethod);
            foreach (var elem in list.Elements)
            {
                string elemValue = EmitExpression(sb, elem);
                EmitLine(sb, $"  call void @{mangledAddLast}(ptr {listPtr}, {elemLLVMType} {elemValue})");
            }
        }
        else
        {
            // Fallback: direct store into data buffer
            // Load data ptr, then GEP + store for each element
            string dataPtrSlot = NextTemp();
            EmitLine(sb, $"  {dataPtrSlot} = getelementptr {{ i64, i64, ptr }}, ptr {listPtr}, i32 0, i32 2");
            string dataBase = NextTemp();
            EmitLine(sb, $"  {dataBase} = load ptr, ptr {dataPtrSlot}");

            for (int i = 0; i < list.Elements.Count; i++)
            {
                string elemValue = EmitExpression(sb, list.Elements[i]);
                string elemPtr = NextTemp();
                EmitLine(sb, $"  {elemPtr} = getelementptr {elemLLVMType}, ptr {dataBase}, i64 {i}");
                EmitLine(sb, $"  store {elemLLVMType} {elemValue}, ptr {elemPtr}");
            }

            // Update count
            string countPtr = NextTemp();
            EmitLine(sb, $"  {countPtr} = getelementptr {{ i64, i64, ptr }}, ptr {listPtr}, i32 0, i32 0");
            EmitLine(sb, $"  store i64 {list.Elements.Count}, ptr {countPtr}");
        }

        return listPtr;
    }

    #endregion

    #region Flags Tests

    /// <summary>
    /// Emits a flags test expression: x is FLAG, x isnot FLAG, x isonly FLAG.
    /// FlagsTestExpression has Subject, Kind, TestFlags, Connective, and ExcludedFlags.
    /// </summary>
    private string EmitFlagsTest(StringBuilder sb, FlagsTestExpression flagsTest)
    {
        string subject = EmitExpression(sb, flagsTest.Subject);
        TypeInfo? subjectType = GetExpressionType(flagsTest.Subject);

        FlagsTypeInfo? flagsType = subjectType as FlagsTypeInfo;

        // Build the combined test mask from TestFlags
        ulong testMask = 0;
        foreach (string flagName in flagsTest.TestFlags)
        {
            testMask |= ResolveFlagBit(flagName, flagsType);
        }

        // Build the excluded mask from ExcludedFlags (if present)
        ulong excludedMask = 0;
        if (flagsTest.ExcludedFlags != null)
        {
            foreach (string flagName in flagsTest.ExcludedFlags)
            {
                excludedMask |= ResolveFlagBit(flagName, flagsType);
            }
        }

        string maskStr = testMask.ToString();

        return flagsTest.Kind switch
        {
            FlagsTestKind.Is => EmitFlagsIsTest(sb, subject, maskStr, flagsTest.Connective, excludedMask),
            FlagsTestKind.IsNot => EmitFlagsIsNotTest(sb, subject, maskStr),
            FlagsTestKind.IsOnly => EmitFlagsIsOnlyTest(sb, subject, maskStr),
            _ => throw new InvalidOperationException($"Unknown flags test kind: {flagsTest.Kind}")
        };
    }

    /// <summary>
    /// Resolves a flag member name to its bit value (1UL &lt;&lt; BitPosition).
    /// Falls back to 0 if not found.
    /// </summary>
    private static ulong ResolveFlagBit(string flagName, FlagsTypeInfo? flagsType)
    {
        if (flagsType == null) return 0;
        foreach (var member in flagsType.Members)
        {
            if (member.Name == flagName)
            {
                return 1UL << member.BitPosition;
            }
        }
        return 0;
    }

    /// <summary>
    /// x is A and B → (x &amp; mask) == mask (all flags set)
    /// x is A or B  → (x &amp; mask) != 0 (any flag set)
    /// x is A and B but C → ((x &amp; mask) == mask) &amp;&amp; ((x &amp; excludedMask) == 0)
    /// </summary>
    private string EmitFlagsIsTest(StringBuilder sb, string subject, string mask, FlagsTestConnective connective, ulong excludedMask)
    {
        string andResult = NextTemp();
        EmitLine(sb, $"  {andResult} = and i64 {subject}, {mask}");

        string cmpResult;
        if (connective == FlagsTestConnective.Or)
        {
            // Any flag set: (subject & mask) != 0
            cmpResult = NextTemp();
            EmitLine(sb, $"  {cmpResult} = icmp ne i64 {andResult}, 0");
        }
        else
        {
            // All flags set: (subject & mask) == mask
            cmpResult = NextTemp();
            EmitLine(sb, $"  {cmpResult} = icmp eq i64 {andResult}, {mask}");
        }

        // Handle 'but' exclusion
        if (excludedMask > 0)
        {
            string exclAnd = NextTemp();
            EmitLine(sb, $"  {exclAnd} = and i64 {subject}, {excludedMask}");
            string exclCmp = NextTemp();
            EmitLine(sb, $"  {exclCmp} = icmp eq i64 {exclAnd}, 0");
            // Combined: cmpResult && exclCmp
            string combined = NextTemp();
            EmitLine(sb, $"  {combined} = and i1 {cmpResult}, {exclCmp}");
            return combined;
        }

        return cmpResult;
    }

    /// <summary>
    /// x isnot A → (x &amp; mask) != mask (flag not fully set)
    /// </summary>
    private string EmitFlagsIsNotTest(StringBuilder sb, string subject, string mask)
    {
        string andResult = NextTemp();
        EmitLine(sb, $"  {andResult} = and i64 {subject}, {mask}");
        string result = NextTemp();
        EmitLine(sb, $"  {result} = icmp ne i64 {andResult}, {mask}");
        return result;
    }

    /// <summary>
    /// x isonly A and B → x == mask (exact match)
    /// </summary>
    private string EmitFlagsIsOnlyTest(StringBuilder sb, string subject, string mask)
    {
        string result = NextTemp();
        EmitLine(sb, $"  {result} = icmp eq i64 {subject}, {mask}");
        return result;
    }

    #endregion
}
