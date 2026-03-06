namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Symbols;
using SemanticAnalysis.Types;
using SyntaxTree;

/// <summary>
/// Expression code generation: allocation, field access, method calls, operators.
/// </summary>
public partial class LLVMCodeGenerator
{
    #region Entity Allocation

    /// <summary>
    /// Generates code to allocate a new entity instance.
    /// Entity allocation:
    /// 1. Call rf_alloc(size) to get heap memory
    /// 2. Initialize all fields to zero/default values
    /// 3. Return pointer to the entity
    /// </summary>
    /// <param name="sb">StringBuilder to emit code to.</param>
    /// <param name="entity">The entity type to allocate.</param>
    /// <param name="fieldValues">Optional field initializer values (in field order).</param>
    /// <returns>The temporary variable holding the entity pointer.</returns>
    private string EmitEntityAllocation(StringBuilder sb, EntityTypeInfo entity, List<string>? fieldValues = null)
    {
        string typeName = GetEntityTypeName(entity);
        int size = CalculateEntitySize(entity);

        // Allocate memory
        string rawPtr = NextTemp();
        EmitLine(sb, $"  {rawPtr} = call ptr @rf_alloc(i64 {size})");

        // Initialize fields
        for (int i = 0; i < entity.Fields.Count; i++)
        {
            var field = entity.Fields[i];
            string fieldType = GetLLVMType(field.Type);

            // Get field pointer using GEP
            string fieldPtr = NextTemp();
            EmitLine(sb, $"  {fieldPtr} = getelementptr {typeName}, ptr {rawPtr}, i32 0, i32 {i}");

            // Get value to store
            string value;
            if (fieldValues != null && i < fieldValues.Count)
            {
                value = fieldValues[i];
            }
            else
            {
                value = GetZeroValue(field.Type);
            }

            // Store the value
            EmitLine(sb, $"  store {fieldType} {value}, ptr {fieldPtr}");
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
    /// Generates code to construct an entity with field values.
    /// </summary>
    private string EmitEntityConstruction(StringBuilder sb, EntityTypeInfo entity, CreatorExpression expr)
    {
        // Evaluate all field value expressions first
        var fieldValues = new List<string>();
        foreach (var (_, fieldExpr) in expr.Fields)
        {
            string value = EmitExpression(sb, fieldExpr);
            fieldValues.Add(value);
        }

        // Allocate and initialize
        return EmitEntityAllocation(sb, entity, fieldValues);
    }

    /// <summary>
    /// Generates code to construct a record (value type).
    /// </summary>
    private string EmitRecordConstruction(StringBuilder sb, RecordTypeInfo record, CreatorExpression expr)
    {
        // Single-field wrapper: just return the inner value
        if (record.IsSingleFieldWrapper && expr.Fields.Count == 1)
        {
            return EmitExpression(sb, expr.Fields[0].Value);
        }

        // Multi-field record: build struct value
        string typeName = GetRecordTypeName(record);

        // Start with undef and insert each field
        string result = "undef";
        for (int i = 0; i < expr.Fields.Count && i < record.Fields.Count; i++)
        {
            string value = EmitExpression(sb, expr.Fields[i].Value);
            string fieldType = GetLLVMType(record.Fields[i].Type);

            string newResult = NextTemp();
            EmitLine(sb, $"  {newResult} = insertvalue {typeName} {result}, {fieldType} {value}, {i}");
            result = newResult;
        }

        return result;
    }

    #endregion

    #region Field Access

    /// <summary>
    /// Generates code to read a field from an entity/record.
    /// For entities: GEP + load
    /// For records: extractvalue
    /// </summary>
    /// <param name="sb">StringBuilder to emit code to.</param>
    /// <param name="expr">The member access expression.</param>
    /// <returns>The temporary variable holding the field value.</returns>
    private string EmitFieldAccess(StringBuilder sb, MemberExpression expr)
    {
        // Evaluate the target expression
        string target = EmitExpression(sb, expr.Object);

        // Get the target type
        TypeInfo? targetType = GetExpressionType(expr.Object);
        if (targetType == null)
        {
            throw new InvalidOperationException("Cannot determine type of field access target");
        }

        return targetType switch
        {
            EntityTypeInfo entity => EmitEntityFieldRead(sb, target, entity, expr.PropertyName),
            RecordTypeInfo record => EmitRecordFieldRead(sb, target, record, expr.PropertyName),
            ResidentTypeInfo resident => EmitResidentFieldRead(sb, target, resident, expr.PropertyName),
            _ => throw new InvalidOperationException($"Cannot access field on type: {targetType.Category}")
        };
    }

    /// <summary>
    /// Generates code to read a field from an entity (pointer type).
    /// Uses GEP to get field address, then load.
    /// </summary>
    private string EmitEntityFieldRead(StringBuilder sb, string entityPtr, EntityTypeInfo entity, string fieldName)
    {
        // Find field index
        int fieldIndex = -1;
        FieldInfo? field = null;
        for (int i = 0; i < entity.Fields.Count; i++)
        {
            if (entity.Fields[i].Name == fieldName)
            {
                fieldIndex = i;
                field = entity.Fields[i];
                break;
            }
        }

        if (fieldIndex < 0 || field == null)
        {
            throw new InvalidOperationException($"Field '{fieldName}' not found on entity '{entity.Name}'");
        }

        string typeName = GetEntityTypeName(entity);
        string fieldType = GetLLVMType(field.Type);

        // GEP to get field pointer
        string fieldPtr = NextTemp();
        EmitLine(sb, $"  {fieldPtr} = getelementptr {typeName}, ptr {entityPtr}, i32 0, i32 {fieldIndex}");

        // Load the field value
        string value = NextTemp();
        EmitLine(sb, $"  {value} = load {fieldType}, ptr {fieldPtr}");

        return value;
    }

    /// <summary>
    /// Generates code to read a field from a record (value type).
    /// Uses extractvalue instruction.
    /// </summary>
    private string EmitRecordFieldRead(StringBuilder sb, string recordValue, RecordTypeInfo record, string fieldName)
    {
        // Single-field wrapper: the value IS the field
        if (record.IsSingleFieldWrapper)
        {
            return recordValue;
        }

        // Find field index
        int fieldIndex = -1;
        FieldInfo? field = null;
        for (int i = 0; i < record.Fields.Count; i++)
        {
            if (record.Fields[i].Name == fieldName)
            {
                fieldIndex = i;
                field = record.Fields[i];
                break;
            }
        }

        if (fieldIndex < 0 || field == null)
        {
            throw new InvalidOperationException($"Field '{fieldName}' not found on record '{record.Name}'");
        }

        string typeName = GetRecordTypeName(record);
        string fieldType = GetLLVMType(field.Type);

        // Extract the field value
        string value = NextTemp();
        EmitLine(sb, $"  {value} = extractvalue {typeName} {recordValue}, {fieldIndex}");

        return value;
    }

    /// <summary>
    /// Generates code to read a field from a resident (like entity).
    /// </summary>
    private string EmitResidentFieldRead(StringBuilder sb, string residentPtr, ResidentTypeInfo resident, string fieldName)
    {
        // Find field index
        int fieldIndex = -1;
        FieldInfo? field = null;
        for (int i = 0; i < resident.Fields.Count; i++)
        {
            if (resident.Fields[i].Name == fieldName)
            {
                fieldIndex = i;
                field = resident.Fields[i];
                break;
            }
        }

        if (fieldIndex < 0 || field == null)
        {
            throw new InvalidOperationException($"Field '{fieldName}' not found on resident '{resident.Name}'");
        }

        string typeName = GetResidentTypeName(resident);
        string fieldType = GetLLVMType(field.Type);

        // GEP to get field pointer
        string fieldPtr = NextTemp();
        EmitLine(sb, $"  {fieldPtr} = getelementptr {typeName}, ptr {residentPtr}, i32 0, i32 {fieldIndex}");

        // Load the field value
        string value = NextTemp();
        EmitLine(sb, $"  {value} = load {fieldType}, ptr {fieldPtr}");

        return value;
    }

    #endregion

    #region Field Write

    /// <summary>
    /// Generates code to write a field on an entity.
    /// </summary>
    private void EmitEntityFieldWrite(StringBuilder sb, string entityPtr, EntityTypeInfo entity, string fieldName, string value)
    {
        // Find field index
        int fieldIndex = -1;
        FieldInfo? field = null;
        for (int i = 0; i < entity.Fields.Count; i++)
        {
            if (entity.Fields[i].Name == fieldName)
            {
                fieldIndex = i;
                field = entity.Fields[i];
                break;
            }
        }

        if (fieldIndex < 0 || field == null)
        {
            throw new InvalidOperationException($"Field '{fieldName}' not found on entity '{entity.Name}'");
        }

        string typeName = GetEntityTypeName(entity);
        string fieldType = GetLLVMType(field.Type);

        // GEP to get field pointer
        string fieldPtr = NextTemp();
        EmitLine(sb, $"  {fieldPtr} = getelementptr {typeName}, ptr {entityPtr}, i32 0, i32 {fieldIndex}");

        // Store the value
        EmitLine(sb, $"  store {fieldType} {value}, ptr {fieldPtr}");
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
            MemberExpression memberAccess => EmitFieldAccess(sb, memberAccess),
            OptionalMemberExpression => throw new NotImplementedException("Optional chaining (?.) codegen not yet implemented"),
            CreatorExpression constructor => EmitConstructorCall(sb, constructor),
            CallExpression call => EmitCall(sb, call),
            BinaryExpression binary => EmitBinaryOp(sb, binary),
            UnaryExpression unary => EmitUnaryOp(sb, unary),
            IntrinsicCallExpression intrinsic => EmitIntrinsicCall(sb, intrinsic),
            NativeCallExpression native => EmitNativeCall(sb, native),
            ConditionalExpression cond => EmitConditional(sb, cond),
            IndexExpression index => EmitIndexAccess(sb, index),
            SliceExpression slice => EmitSliceAccess(sb, slice),
            RangeExpression range => EmitRange(sb, range),
            StealExpression steal => EmitSteal(sb, steal),
            TupleLiteralExpression tuple => EmitTupleLiteral(sb, tuple),
            InsertedTextExpression => throw new NotImplementedException("Text insertion codegen not yet implemented"),
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
    /// </summary>
    private string EmitBinaryOp(StringBuilder sb, BinaryExpression binary)
    {
        // TODO: Implement binary operators
        // For now, placeholder
        return "0";
    }

    /// <summary>
    /// Generates code for a unary operation.
    /// </summary>
    private string EmitUnaryOp(StringBuilder sb, UnaryExpression unary)
    {
        // TODO: Implement unary operators
        // For now, placeholder
        return "0";
    }

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
            IntrinsicCallExpression intrinsic => GetIntrinsicReturnType(intrinsic),
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
    /// Gets the return type of an intrinsic call expression.
    /// </summary>
    private TypeInfo? GetIntrinsicReturnType(IntrinsicCallExpression intrinsic)
    {
        // The return type is encoded in the type argument
        // e.g., @intrinsic.add<i64> returns i64
        if (intrinsic.TypeArguments.Count > 0)
        {
            string typeArg = intrinsic.TypeArguments[0];
            // Map intrinsic type names to registry type names
            string typeName = typeArg.ToLowerInvariant() switch
            {
                "i8" => "i8",
                "i16" => "i16",
                "i32" => "i32",
                "i64" => "i64",
                "i128" => "i128",
                "half" => "F16",
                "float" => "F32",
                "double" => "F64",
                "fp128" => "F128",
                "i1" => "Bool",
                _ => typeArg
            };
            return _registry.LookupType(typeName);
        }
        return null;
    }

    /// <summary>
    /// Gets the type of a literal expression from its token type.
    /// </summary>
    private TypeInfo? GetLiteralType(LiteralExpression literal)
    {
        string? typeName = literal.LiteralType switch
        {
            Compiler.Lexer.TokenType.S8Literal => "S8",
            Compiler.Lexer.TokenType.S16Literal => "S16",
            Compiler.Lexer.TokenType.S32Literal => "S32",
            Compiler.Lexer.TokenType.S64Literal => "S64",
            Compiler.Lexer.TokenType.S128Literal => "S128",
            Compiler.Lexer.TokenType.U8Literal => "U8",
            Compiler.Lexer.TokenType.U16Literal => "U16",
            Compiler.Lexer.TokenType.U32Literal => "U32",
            Compiler.Lexer.TokenType.U64Literal => "U64",
            Compiler.Lexer.TokenType.U128Literal => "U128",
            Compiler.Lexer.TokenType.F16Literal => "F16",
            Compiler.Lexer.TokenType.F32Literal => "F32",
            Compiler.Lexer.TokenType.F64Literal => "F64",
            Compiler.Lexer.TokenType.F128Literal => "F128",
            Compiler.Lexer.TokenType.D32Literal => "D32",
            Compiler.Lexer.TokenType.D64Literal => "D64",
            Compiler.Lexer.TokenType.D128Literal => "D128",
            Compiler.Lexer.TokenType.True or Compiler.Lexer.TokenType.False => "Bool",
            Compiler.Lexer.TokenType.TextLiteral => "Text",
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

        FieldInfo? field = targetType switch
        {
            EntityTypeInfo e => e.LookupField(member.PropertyName),
            RecordTypeInfo r => r.LookupField(member.PropertyName),
            ResidentTypeInfo res => res.LookupField(member.PropertyName),
            _ => null
        };

        return field?.Type;
    }

    #endregion

    #region Intrinsic Calls

    /// <summary>
    /// Generates code for an intrinsic call expression.
    /// Intrinsics map directly to LLVM IR instructions.
    /// </summary>
    /// <param name="sb">StringBuilder to emit code to.</param>
    /// <param name="intrinsic">The intrinsic call expression.</param>
    /// <returns>The temporary variable holding the result.</returns>
    private string EmitIntrinsicCall(StringBuilder sb, IntrinsicCallExpression intrinsic)
    {
        // Evaluate all arguments first
        var args = new List<string>();
        foreach (var arg in intrinsic.Arguments)
        {
            args.Add(EmitExpression(sb, arg));
        }

        // Get the LLVM type from type arguments
        if (intrinsic.TypeArguments.Count == 0)
        {
            throw new InvalidOperationException($"Intrinsic call to '{intrinsic.IntrinsicName}' requires type arguments");
        }
        string llvmType = MapIntrinsicTypeToLLVM(intrinsic.TypeArguments[0]);

        string result = NextTemp();

        // Map intrinsic name to LLVM instruction
        string name = intrinsic.IntrinsicName;

        // Arithmetic operations
        if (name == "add.wrapping" || name == "add")
        {
            string op = IsFloatLLVMType(llvmType) ? "fadd" : "add";
            EmitLine(sb, $"  {result} = {op} {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "sub.wrapping" || name == "sub")
        {
            string op = IsFloatLLVMType(llvmType) ? "fsub" : "sub";
            EmitLine(sb, $"  {result} = {op} {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "mul.wrapping" || name == "mul")
        {
            string op = IsFloatLLVMType(llvmType) ? "fmul" : "mul";
            EmitLine(sb, $"  {result} = {op} {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "div.wrapping" || name == "div")
        {
            // For floats, always fdiv; for ints this is tricky - assume signed
            string op = IsFloatLLVMType(llvmType) ? "fdiv" : "sdiv";
            EmitLine(sb, $"  {result} = {op} {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "sdiv")
        {
            EmitLine(sb, $"  {result} = sdiv {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "udiv")
        {
            EmitLine(sb, $"  {result} = udiv {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "srem")
        {
            EmitLine(sb, $"  {result} = srem {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "urem")
        {
            EmitLine(sb, $"  {result} = urem {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "frem")
        {
            EmitLine(sb, $"  {result} = frem {llvmType} {args[0]}, {args[1]}");
        }
        // Bitwise operations
        else if (name == "and")
        {
            EmitLine(sb, $"  {result} = and {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "or")
        {
            EmitLine(sb, $"  {result} = or {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "xor")
        {
            EmitLine(sb, $"  {result} = xor {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "not")
        {
            EmitLine(sb, $"  {result} = xor {llvmType} {args[0]}, -1");
        }
        // Shift operations
        else if (name == "shl")
        {
            EmitLine(sb, $"  {result} = shl {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "lshr")
        {
            EmitLine(sb, $"  {result} = lshr {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "ashr")
        {
            EmitLine(sb, $"  {result} = ashr {llvmType} {args[0]}, {args[1]}");
        }
        // Integer comparisons
        else if (name == "icmp.eq")
        {
            EmitLine(sb, $"  {result} = icmp eq {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "icmp.ne")
        {
            EmitLine(sb, $"  {result} = icmp ne {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "icmp.slt")
        {
            EmitLine(sb, $"  {result} = icmp slt {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "icmp.sle")
        {
            EmitLine(sb, $"  {result} = icmp sle {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "icmp.sgt")
        {
            EmitLine(sb, $"  {result} = icmp sgt {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "icmp.sge")
        {
            EmitLine(sb, $"  {result} = icmp sge {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "icmp.ult")
        {
            EmitLine(sb, $"  {result} = icmp ult {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "icmp.ule")
        {
            EmitLine(sb, $"  {result} = icmp ule {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "icmp.ugt")
        {
            EmitLine(sb, $"  {result} = icmp ugt {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "icmp.uge")
        {
            EmitLine(sb, $"  {result} = icmp uge {llvmType} {args[0]}, {args[1]}");
        }
        // Float comparisons
        else if (name == "fcmp.oeq")
        {
            EmitLine(sb, $"  {result} = fcmp oeq {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "fcmp.one")
        {
            EmitLine(sb, $"  {result} = fcmp one {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "fcmp.olt")
        {
            EmitLine(sb, $"  {result} = fcmp olt {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "fcmp.ole")
        {
            EmitLine(sb, $"  {result} = fcmp ole {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "fcmp.ogt")
        {
            EmitLine(sb, $"  {result} = fcmp ogt {llvmType} {args[0]}, {args[1]}");
        }
        else if (name == "fcmp.oge")
        {
            EmitLine(sb, $"  {result} = fcmp oge {llvmType} {args[0]}, {args[1]}");
        }
        // Type conversions
        else if (name == "sext")
        {
            string fromType = MapIntrinsicTypeToLLVM(intrinsic.TypeArguments[0]);
            string toType = MapIntrinsicTypeToLLVM(intrinsic.TypeArguments[1]);
            EmitLine(sb, $"  {result} = sext {fromType} {args[0]} to {toType}");
        }
        else if (name == "zext")
        {
            string fromType = MapIntrinsicTypeToLLVM(intrinsic.TypeArguments[0]);
            string toType = MapIntrinsicTypeToLLVM(intrinsic.TypeArguments[1]);
            EmitLine(sb, $"  {result} = zext {fromType} {args[0]} to {toType}");
        }
        else if (name == "trunc")
        {
            string fromType = MapIntrinsicTypeToLLVM(intrinsic.TypeArguments[0]);
            string toType = MapIntrinsicTypeToLLVM(intrinsic.TypeArguments[1]);
            EmitLine(sb, $"  {result} = trunc {fromType} {args[0]} to {toType}");
        }
        else if (name == "bitcast")
        {
            string fromType = MapIntrinsicTypeToLLVM(intrinsic.TypeArguments[0]);
            string toType = MapIntrinsicTypeToLLVM(intrinsic.TypeArguments[1]);
            EmitLine(sb, $"  {result} = bitcast {fromType} {args[0]} to {toType}");
        }
        else if (name == "fpext")
        {
            string fromType = MapIntrinsicTypeToLLVM(intrinsic.TypeArguments[0]);
            string toType = MapIntrinsicTypeToLLVM(intrinsic.TypeArguments[1]);
            EmitLine(sb, $"  {result} = fpext {fromType} {args[0]} to {toType}");
        }
        else if (name == "fptrunc")
        {
            string fromType = MapIntrinsicTypeToLLVM(intrinsic.TypeArguments[0]);
            string toType = MapIntrinsicTypeToLLVM(intrinsic.TypeArguments[1]);
            EmitLine(sb, $"  {result} = fptrunc {fromType} {args[0]} to {toType}");
        }
        else if (name == "sitofp")
        {
            string fromType = MapIntrinsicTypeToLLVM(intrinsic.TypeArguments[0]);
            string toType = MapIntrinsicTypeToLLVM(intrinsic.TypeArguments[1]);
            EmitLine(sb, $"  {result} = sitofp {fromType} {args[0]} to {toType}");
        }
        else if (name == "uitofp")
        {
            string fromType = MapIntrinsicTypeToLLVM(intrinsic.TypeArguments[0]);
            string toType = MapIntrinsicTypeToLLVM(intrinsic.TypeArguments[1]);
            EmitLine(sb, $"  {result} = uitofp {fromType} {args[0]} to {toType}");
        }
        else if (name == "fptosi")
        {
            string fromType = MapIntrinsicTypeToLLVM(intrinsic.TypeArguments[0]);
            string toType = MapIntrinsicTypeToLLVM(intrinsic.TypeArguments[1]);
            EmitLine(sb, $"  {result} = fptosi {fromType} {args[0]} to {toType}");
        }
        else if (name == "fptoui")
        {
            string fromType = MapIntrinsicTypeToLLVM(intrinsic.TypeArguments[0]);
            string toType = MapIntrinsicTypeToLLVM(intrinsic.TypeArguments[1]);
            EmitLine(sb, $"  {result} = fptoui {fromType} {args[0]} to {toType}");
        }
        // Memory operations
        else if (name == "load")
        {
            EmitLine(sb, $"  {result} = load {llvmType}, ptr {args[0]}");
        }
        else if (name == "store")
        {
            // Store doesn't return a value
            EmitLine(sb, $"  store {llvmType} {args[0]}, ptr {args[1]}");
            return args[0]; // Return stored value for chaining
        }
        // Bit operations
        else if (name == "bitwidth")
        {
            // Return the bit width of the type as a constant
            int bitWidth = GetTypeBitWidth(llvmType);
            return bitWidth.ToString();
        }
        else if (name == "ctlz")
        {
            // Count leading zeros
            EmitLine(sb, $"  {result} = call {llvmType} @llvm.ctlz.{llvmType}({llvmType} {args[0]}, i1 false)");
        }
        else if (name == "cttz")
        {
            // Count trailing zeros
            EmitLine(sb, $"  {result} = call {llvmType} @llvm.cttz.{llvmType}({llvmType} {args[0]}, i1 false)");
        }
        else if (name == "ctpop")
        {
            // Population count (count 1 bits)
            EmitLine(sb, $"  {result} = call {llvmType} @llvm.ctpop.{llvmType}({llvmType} {args[0]})");
        }
        // Overflow-detecting operations
        else if (name == "add.overflow")
        {
            string overflowType = $"{{ {llvmType}, i1 }}";
            string intrinsicName = llvmType.StartsWith("i") ? $"llvm.sadd.with.overflow.{llvmType}" : throw new NotImplementedException();
            EmitLine(sb, $"  {result} = call {overflowType} @{intrinsicName}({llvmType} {args[0]}, {llvmType} {args[1]})");
        }
        else if (name == "sub.overflow")
        {
            string overflowType = $"{{ {llvmType}, i1 }}";
            string intrinsicName = $"llvm.ssub.with.overflow.{llvmType}";
            EmitLine(sb, $"  {result} = call {overflowType} @{intrinsicName}({llvmType} {args[0]}, {llvmType} {args[1]})");
        }
        else if (name == "mul.overflow")
        {
            string overflowType = $"{{ {llvmType}, i1 }}";
            string intrinsicName = $"llvm.smul.with.overflow.{llvmType}";
            EmitLine(sb, $"  {result} = call {overflowType} @{intrinsicName}({llvmType} {args[0]}, {llvmType} {args[1]})");
        }
        // Saturating arithmetic
        else if (name == "add.saturate")
        {
            string intrinsicName = IsSignedLLVMType(llvmType)
                ? $"llvm.sadd.sat.{llvmType}"
                : $"llvm.uadd.sat.{llvmType}";
            EmitLine(sb, $"  {result} = call {llvmType} @{intrinsicName}({llvmType} {args[0]}, {llvmType} {args[1]})");
        }
        else if (name == "sub.saturate")
        {
            string intrinsicName = IsSignedLLVMType(llvmType)
                ? $"llvm.ssub.sat.{llvmType}"
                : $"llvm.usub.sat.{llvmType}";
            EmitLine(sb, $"  {result} = call {llvmType} @{intrinsicName}({llvmType} {args[0]}, {llvmType} {args[1]})");
        }
        else if (name == "mul.saturate")
        {
            // LLVM doesn't have a direct mul.sat intrinsic, so we implement it manually
            // For now, use wrapping multiplication (TODO: implement proper saturation)
            EmitLine(sb, $"  {result} = mul {llvmType} {args[0]}, {args[1]}");
        }
        // Remainder
        else if (name is "rem.wrapping" or "rem")
        {
            string op = IsFloatLLVMType(llvmType) ? "frem" : (IsSignedLLVMType(llvmType) ? "srem" : "urem");
            EmitLine(sb, $"  {result} = {op} {llvmType} {args[0]}, {args[1]}");
        }
        // Absolute value
        else if (name == "abs")
        {
            if (IsFloatLLVMType(llvmType))
            {
                EmitLine(sb, $"  {result} = call {llvmType} @llvm.fabs.{llvmType}({llvmType} {args[0]})");
            }
            else
            {
                EmitLine(sb, $"  {result} = call {llvmType} @llvm.abs.{llvmType}({llvmType} {args[0]}, i1 false)");
            }
        }
        // Bit manipulation
        else if (name == "bitreverse")
        {
            EmitLine(sb, $"  {result} = call {llvmType} @llvm.bitreverse.{llvmType}({llvmType} {args[0]})");
        }
        else if (name == "bswap")
        {
            EmitLine(sb, $"  {result} = call {llvmType} @llvm.bswap.{llvmType}({llvmType} {args[0]})");
        }
        else
        {
            throw new NotImplementedException($"Intrinsic not implemented: @intrinsic.{name}");
        }

        return result;
    }

    /// <summary>
    /// Maps an intrinsic type argument to the corresponding LLVM type.
    /// </summary>
    private static string MapIntrinsicTypeToLLVM(string intrinsicType)
    {
        return intrinsicType.ToLowerInvariant() switch
        {
            // Standard integer types
            "i1" => "i1",
            "i8" => "i8",
            "i16" => "i16",
            "i32" => "i32",
            "i64" => "i64",
            "i128" => "i128",

            // Pointer-sized types
            "iptr" or "uptr" => "i64", // TODO: Make platform-dependent

            // Floating-point types
            "half" => "half",
            "float" => "float",
            "double" => "double",
            "fp128" => "fp128",

            // Pointer type
            "ptr" => "ptr",

            // Default: return as-is
            _ => intrinsicType
        };
    }

    /// <summary>
    /// Checks if an LLVM type is a floating-point type.
    /// </summary>
    private static bool IsFloatLLVMType(string llvmType)
    {
        return llvmType is "half" or "float" or "double" or "fp128";
    }

    /// <summary>
    /// Checks if an LLVM type is a signed integer type.
    /// For intrinsics, we assume i* types are signed unless specified.
    /// </summary>
    private static bool IsSignedLLVMType(string llvmType)
    {
        // By convention, i* types are treated as signed
        // u* types would be unsigned, but LLVM doesn't distinguish at the type level
        return llvmType.StartsWith("i");
    }

    /// <summary>
    /// Gets the bit width of an LLVM type.
    /// </summary>
    private static int GetTypeBitWidth(string llvmType)
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
            "ptr" => 64, // TODO: Make platform-dependent
            _ => throw new InvalidOperationException($"Unknown LLVM type for bitwidth: {llvmType}")
        };
    }

    #endregion

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

        // TODO: Determine element type from target type
        // For now, assume i32 element type
        string result = NextTemp();
        string elemPtr = NextTemp();

        EmitLine(sb, $"  {elemPtr} = getelementptr i32, ptr {target}, i64 {indexValue}");
        EmitLine(sb, $"  {result} = load i32, ptr {elemPtr}");

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

        // TODO: Emit call to __getslice__ method on target type
        // For now, return the target as placeholder
        _ = start;
        _ = end;
        return target;
    }

    /// <summary>
    /// Generates code for a range expression.
    /// </summary>
    private string EmitRange(StringBuilder sb, RangeExpression range)
    {
        // TODO: Implement range expression (create Range struct)
        string start = EmitExpression(sb, range.Start);
        EmitExpression(sb, range.End);

        // For now, just return the start value
        return start;
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
    /// - ValueTuple: stack-allocated struct with fields item0, item1, ...
    /// - Tuple: heap-allocated entity with fields item0, item1, ...
    ///
    /// The semantic analyzer determines which type to use based on element types.
    /// </remarks>
    private string EmitTupleLiteral(StringBuilder sb, TupleLiteralExpression tuple)
    {
        // TODO: Full implementation needs to:
        // 1. Check ResolvedType to determine if ValueTuple or Tuple
        // 2. For ValueTuple: allocate on stack, initialize fields
        // 3. For Tuple: allocate on heap via rf_alloc, initialize fields
        // 4. Return pointer to the tuple

        // For now, evaluate all elements and return a placeholder
        foreach (var element in tuple.Elements)
        {
            EmitExpression(sb, element);
        }

        throw new NotImplementedException("Tuple literal code generation not yet implemented");
    }

    #endregion

    #region Native Calls

    /// <summary>
    /// Generates code for a native function call (@native.*).
    /// Native calls are direct C ABI calls to external functions.
    /// Argument types are inferred from the expressions at the call site.
    /// </summary>
    /// <param name="sb">StringBuilder to emit code to.</param>
    /// <param name="native">The native call expression.</param>
    /// <returns>The temporary variable holding the result (or "undef" for void).</returns>
    private string EmitNativeCall(StringBuilder sb, NativeCallExpression native)
    {
        string funcName = native.FunctionName;

        // Evaluate all arguments and collect their types
        var argValues = new List<string>();
        var argTypes = new List<string>();

        foreach (var arg in native.Arguments)
        {
            string value = EmitExpression(sb, arg);
            argValues.Add(value);

            TypeInfo? argType = GetExpressionType(arg);
            if (argType == null)
            {
                throw new InvalidOperationException($"Cannot determine type for argument in native call to '{funcName}'");
            }
            argTypes.Add(GetLLVMType(argType));
        }

        // Ensure the native function is declared with the inferred signature
        string returnType = "ptr"; // Default return type for native functions
        DeclareNativeFunction(funcName, argTypes, returnType);

        // Emit the call
        string result = NextTemp();
        string args = BuildCallArgs(argTypes, argValues);
        EmitLine(sb, $"  {result} = call {returnType} @{funcName}({args})");
        return result;
    }

    /// <summary>
    /// Declares a native function with the given signature if not already declared.
    /// </summary>
    private void DeclareNativeFunction(string funcName, List<string> argTypes, string returnType)
    {
        if (!_declaredNativeFunctions.Add(funcName))
        {
            return;
        }

        string argList = string.Join(", ", argTypes);
        EmitLine(_functionDeclarations, $"declare {returnType} @{funcName}({argList})");
    }

    #endregion
}
