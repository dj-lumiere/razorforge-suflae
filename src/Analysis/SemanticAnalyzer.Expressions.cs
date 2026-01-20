namespace Compilers.Analysis;

using Enums;
using Results;
using Compilers.Shared.Analysis.Native;
using Symbols;
using Types;
using Shared.Lexer;
using Shared.AST;
using global::RazorForge.Diagnostics;
using TypeSymbol = Types.TypeInfo;

/// <summary>
/// Phase 3: Expression analysis.
/// </summary>
public sealed partial class SemanticAnalyzer
{
    #region Expression Analysis

    /// <summary>
    /// Analyzes an expression and returns its resolved type.
    /// Also sets the ResolvedType property on the expression.
    /// </summary>
    /// <param name="expression">The expression to analyze.</param>
    /// <param name="expectedType">Optional expected type for contextual inference (e.g., return type, parameter type).</param>
    /// <returns>The resolved type of the expression.</returns>
    private TypeSymbol AnalyzeExpression(Expression expression, TypeSymbol? expectedType = null)
    {
        TypeSymbol resultType = expression switch
        {
            LiteralExpression literal => AnalyzeLiteralExpression(literal: literal, expectedType: expectedType),
            IdentifierExpression id => AnalyzeIdentifierExpression(id: id),
            BinaryExpression binary => AnalyzeBinaryExpression(binary: binary),
            UnaryExpression unary => AnalyzeUnaryExpression(unary: unary),
            CallExpression call => AnalyzeCallExpression(call: call),
            MemberExpression member => AnalyzeMemberExpression(member: member),
            IndexExpression index => AnalyzeIndexExpression(index: index),
            ConditionalExpression cond => AnalyzeConditionalExpression(cond: cond),
            LambdaExpression lambda => AnalyzeLambdaExpression(lambda: lambda),
            RangeExpression range => AnalyzeRangeExpression(range: range),
            ConstructorExpression ctor => AnalyzeConstructorExpression(ctor: ctor),
            ListLiteralExpression list => AnalyzeListLiteralExpression(list: list),
            SetLiteralExpression set => AnalyzeSetLiteralExpression(set: set),
            DictLiteralExpression dict => AnalyzeDictLiteralExpression(dict: dict),
            TypeConversionExpression conv => AnalyzeTypeConversionExpression(conv: conv),
            ChainedComparisonExpression chain => AnalyzeChainedComparisonExpression(chain: chain),
            BlockExpression block => AnalyzeBlockExpression(block: block),
            WithExpression with => AnalyzeWithExpression(with: with),
            NamedArgumentExpression named => AnalyzeExpression(expression: named.Value),
            GenericMethodCallExpression generic => AnalyzeGenericMethodCallExpression(generic: generic),
            GenericMemberExpression genericMember => AnalyzeGenericMemberExpression(genericMember: genericMember),
            IntrinsicCallExpression intrinsic => AnalyzeIntrinsicCallExpression(intrinsic: intrinsic),
            NativeCallExpression native => AnalyzeNativeCallExpression(native: native),
            IsPatternExpression isPat => AnalyzeIsPatternExpression(isPat: isPat),
            StealExpression steal => AnalyzeStealExpression(steal: steal),
            BackIndexExpression back => AnalyzeBackIndexExpression(back: back),
            TypeExpression typeExpr => ResolveType(typeExpr: typeExpr),
            WhenExpression whenExpr => AnalyzeWhenExpression(when: whenExpr),
            WaitforExpression waitfor => AnalyzeWaitforExpression(waitfor: waitfor),
            _ => HandleUnknownExpression(expression: expression)
        };

        // Set the resolved type directly (no conversion needed)
        expression.ResolvedType = resultType;
        return resultType;
    }

    private TypeSymbol AnalyzeLiteralExpression(LiteralExpression literal, TypeSymbol? expectedType = null)
    {
        // Map token type to the corresponding type (PascalCase)
        string? typeName = literal.LiteralType switch
        {
            // Signed integers
            TokenType.S8Literal => "S8",
            TokenType.S16Literal => "S16",
            TokenType.S32Literal => "S32",
            TokenType.S64Literal => "S64",
            TokenType.S128Literal => "S128",
            TokenType.SAddrLiteral => "SAddr",

            // Unsigned integers
            TokenType.U8Literal => "U8",
            TokenType.U16Literal => "U16",
            TokenType.U32Literal => "U32",
            TokenType.U64Literal => "U64",
            TokenType.U128Literal => "U128",
            TokenType.UAddrLiteral => "UAddr",

            // Floating-point
            TokenType.F16Literal => "F16",
            TokenType.F32Literal => "F32",
            TokenType.F64Literal => "F64",
            TokenType.F128Literal => "F128",

            // Decimal floating-point
            TokenType.D32Literal => "D32",
            TokenType.D64Literal => "D64",
            TokenType.D128Literal => "D128",

            // Arbitrary precision
            TokenType.Integer => "Integer",
            TokenType.Decimal => "Decimal",

            // Boolean
            TokenType.True or TokenType.False => "Bool",

            // Text and characters
            TokenType.TextLiteral => "Text",
            TokenType.BytesLiteral => "Bytes",
            TokenType.BytesRawLiteral => "Bytes",
            TokenType.ByteLetterLiteral => "Byte",
            TokenType.LetterLiteral => "Letter",

            // Memory size literals (all map to MemorySize type)
            TokenType.ByteLiteral or
            TokenType.KilobyteLiteral or TokenType.KibibyteLiteral or
            TokenType.MegabyteLiteral or TokenType.MebibyteLiteral or
            TokenType.GigabyteLiteral or TokenType.GibibyteLiteral => "MemorySize",

            // Duration literals (all map to Duration type)
            TokenType.WeekLiteral or TokenType.DayLiteral or
            TokenType.HourLiteral or TokenType.MinuteLiteral or
            TokenType.SecondLiteral or TokenType.MillisecondLiteral or
            TokenType.MicrosecondLiteral or TokenType.NanosecondLiteral => "Duration",

            // Unknown literal type - error
            _ => null
        };

        // Report error for unknown literal types
        if (typeName == null)
        {
            ReportError(
                SemanticDiagnosticCode.UnknownLiteralType,
                $"Unknown literal type '{literal.LiteralType}'.",
                literal.Location);
            return ErrorTypeInfo.Instance;
        }

        // Contextual type inference for unsuffixed integer literals
        // If expected type is an integer type and literal is S64 (default unsuffixed), infer to expected type
        if (expectedType != null && literal.LiteralType == TokenType.S64Literal && IsFixedWidthIntegerType(expectedType))
        {
            // Check if the literal value fits in the expected type
            if (LiteralFitsInType(literal, expectedType))
            {
                typeName = expectedType.Name;
            }
        }

        // Parse and validate deferred numeric types using native libraries
        if (literal.Value is string rawValue)
        {
            ParsedLiteral? parsed = ParseDeferredLiteral(literal: literal, rawValue: rawValue);
            if (parsed != null)
            {
                _parsedLiterals[literal.Location] = parsed;
            }
        }

        TypeSymbol? type = _registry.LookupType(name: typeName);
        if (type == null)
        {
            ReportError(
                SemanticDiagnosticCode.LiteralTypeNotDefined,
                $"Type '{typeName}' is not defined.",
                literal.Location);
            return ErrorTypeInfo.Instance;
        }

        return type;
    }

    /// <summary>
    /// Checks if a type is a fixed-width integer type (S8-S128, U8-U128, SAddr, UAddr).
    /// </summary>
    private static bool IsFixedWidthIntegerType(TypeSymbol type)
    {
        return type.Name is "S8" or "S16" or "S32" or "S64" or "S128"
            or "U8" or "U16" or "U32" or "U64" or "U128"
            or "SAddr" or "UAddr";
    }

    /// <summary>
    /// Checks if an integer literal value fits within the range of the target type.
    /// </summary>
    private static bool LiteralFitsInType(LiteralExpression literal, TypeSymbol targetType)
    {
        // Get the numeric value from the literal
        if (literal.Value is not long value)
        {
            // For string-stored values (large numbers), we'd need more sophisticated checking
            // For now, allow inference and let runtime handle overflow
            return true;
        }

        return targetType.Name switch
        {
            "S8" => value is >= sbyte.MinValue and <= sbyte.MaxValue,
            "S16" => value is >= short.MinValue and <= short.MaxValue,
            "S32" => value is >= int.MinValue and <= int.MaxValue,
            "S64" => true, // Any long fits in S64
            "S128" => true, // Any long fits in S128
            "U8" => value is >= 0 and <= byte.MaxValue,
            "U16" => value is >= 0 and <= ushort.MaxValue,
            "U32" => value is >= 0 and <= uint.MaxValue,
            "U64" => value >= 0, // Any non-negative long fits in U64
            "U128" => value >= 0,
            "SAddr" or "UAddr" => true, // System-dependent, allow for now
            _ => false
        };
    }

    /// <summary>
    /// Parses a deferred numeric literal using native libraries.
    /// Called for f128, d32, d64, d128, Integer, and Decimal literals.
    /// </summary>
    /// <param name="literal">The literal expression.</param>
    /// <param name="rawValue">The raw string value to parse.</param>
    /// <returns>The parsed literal, or null if parsing failed.</returns>
    private ParsedLiteral? ParseDeferredLiteral(LiteralExpression literal, string rawValue)
    {
        try
        {
            return literal.LiteralType switch
            {
                TokenType.F128Literal => ParseF128Literal(literal: literal, rawValue: rawValue),
                TokenType.D32Literal => ParseD32Literal(literal: literal, rawValue: rawValue),
                TokenType.D64Literal => ParseD64Literal(literal: literal, rawValue: rawValue),
                TokenType.D128Literal => ParseD128Literal(literal: literal, rawValue: rawValue),
                TokenType.Integer => ParseIntegerLiteral(literal: literal, rawValue: rawValue),
                TokenType.Decimal => ParseDecimalLiteral(literal: literal, rawValue: rawValue),
                _ => null
            };
        }
        catch (Exception ex)
        {
            ReportError(SemanticDiagnosticCode.NumericLiteralParseFailed, $"Failed to parse numeric literal '{rawValue}': {ex.Message}", literal.Location);
            return null;
        }
    }

    private ParsedLiteral ParseF128Literal(LiteralExpression literal, string rawValue)
    {
        NumericLiteralParser.F128 result = NumericLiteralParser.ParseF128(str: rawValue);
        return new ParsedF128(Location: literal.Location, Lo: result.Lo, Hi: result.Hi);
    }

    private ParsedLiteral ParseD32Literal(LiteralExpression literal, string rawValue)
    {
        NumericLiteralParser.D32 result = NumericLiteralParser.ParseD32(str: rawValue);
        return new ParsedD32(Location: literal.Location, Value: result.Value);
    }

    private ParsedLiteral ParseD64Literal(LiteralExpression literal, string rawValue)
    {
        NumericLiteralParser.D64 result = NumericLiteralParser.ParseD64(str: rawValue);
        return new ParsedD64(Location: literal.Location, Value: result.Value);
    }

    private ParsedLiteral ParseD128Literal(LiteralExpression literal, string rawValue)
    {
        NumericLiteralParser.D128 result = NumericLiteralParser.ParseD128(str: rawValue);
        return new ParsedD128(Location: literal.Location, Lo: result.Lo, Hi: result.Hi);
    }

    private ParsedLiteral ParseIntegerLiteral(LiteralExpression literal, string rawValue)
    {
        (byte[] bytes, int sign) = NumericLiteralParser.ParseIntegerToBytes(str: rawValue);
        if (bytes.Length == 0)
        {
            ReportError(SemanticDiagnosticCode.InvalidIntegerLiteral, $"Invalid Integer literal: '{rawValue}'", literal.Location);
            return new ParsedInteger(Location: literal.Location, Limbs: [], Sign: 0, Exponent: 0);
        }

        return new ParsedInteger(Location: literal.Location, Limbs: bytes, Sign: sign, Exponent: 0);
    }

    private ParsedLiteral ParseDecimalLiteral(LiteralExpression literal, string rawValue)
    {
        (string value, int sign, int exponent, int significantDigits, bool isInteger) =
            NumericLiteralParser.ParseDecimalInfo(str: rawValue);

        if (string.IsNullOrEmpty(value: value))
        {
            ReportError(SemanticDiagnosticCode.InvalidDecimalLiteral, $"Invalid Decimal literal: '{rawValue}'", literal.Location);
            return new ParsedDecimal(Location: literal.Location, StringValue: rawValue, Sign: 0, Exponent: 0, SignificantDigits: 0, IsInteger: false);
        }

        return new ParsedDecimal(Location: literal.Location, StringValue: value, Sign: sign, Exponent: exponent, SignificantDigits: significantDigits, IsInteger: isInteger);
    }

    private TypeSymbol AnalyzeIdentifierExpression(IdentifierExpression id)
    {
        // Special identifiers
        if (id.Name == "me")
        {
            if (_currentType != null)
            {
                return _currentType;
            }

            ReportError(SemanticDiagnosticCode.MeOutsideTypeMethod, "'me' can only be used inside a type method.", id.Location);
            return ErrorTypeInfo.Instance;
        }

        if (id.Name == "None")
        {
            // None represents Maybe.None - return a generic Maybe type
            return ErrorHandlingTypeInfo.WellKnown.MaybeDefinition;
        }

        // Try to look up as variable first
        VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
        if (varInfo != null)
        {
            return varInfo.Type;
        }

        // Try to look up as routine (function reference)
        RoutineInfo? routine = _registry.LookupRoutine(fullName: id.Name);
        if (routine != null)
        {
            // Return the function type for first-class function references
            return GetRoutineType(routine);
        }

        // Try to look up as type (for static access)
        TypeSymbol? type = _registry.LookupType(name: id.Name);
        if (type != null)
        {
            return type;
        }

        ReportError(SemanticDiagnosticCode.UnknownIdentifier, $"Unknown identifier '{id.Name}'.", id.Location);
        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeBinaryExpression(BinaryExpression binary)
    {
        TypeSymbol leftType = AnalyzeExpression(expression: binary.Left);
        TypeSymbol rightType = AnalyzeExpression(expression: binary.Right);

        // Handle assignment operator
        if (binary.Operator == BinaryOperator.Assign)
        {
            return AnalyzeAssignmentExpression(target: binary.Left, value: binary.Right, targetType: leftType, valueType: rightType, location: binary.Location);
        }

        // Handle comparison operators - always return bool
        if (IsComparisonOperator(op: binary.Operator))
        {
            ValidateComparisonOperands(left: leftType, right: rightType, op: binary.Operator, location: binary.Location);
            return _registry.LookupType(name: "bool") ?? ErrorTypeInfo.Instance;
        }

        // Handle logical operators - require bool operands, return bool
        if (IsLogicalOperator(op: binary.Operator))
        {
            if (!IsBoolType(type: leftType) || !IsBoolType(type: rightType))
            {
                ReportError(SemanticDiagnosticCode.LogicalOperatorRequiresBool, $"Logical operator '{binary.Operator.ToStringRepresentation()}' requires boolean operands.", binary.Location);
            }

            return _registry.LookupType(name: "bool") ?? ErrorTypeInfo.Instance;
        }

        // Validate division operators
        if (binary.Operator == BinaryOperator.TrueDivide)
        {
            if (!SupportsTrueDivision(type: leftType))
            {
                ReportError(
                    SemanticDiagnosticCode.TrueDivisionNotSupported,
                    $"True division operator '/' is not supported on '{leftType.Name}'. Use floor division '//' for integers.",
                    binary.Location);
            }
        }
        else if (binary.Operator == BinaryOperator.FloorDivide)
        {
            if (!SupportsFloorDivision(type: leftType))
            {
                ReportError(
                    SemanticDiagnosticCode.FloorDivisionNotSupported,
                    $"Floor division operator '//' is not supported on '{leftType.Name}'.",
                    binary.Location);
            }
        }

        // Validate overflow arithmetic operators (+%, -%, *%, +^, -^, *^, +?, -?, *?)
        if (IsOverflowOperator(op: binary.Operator, out string? overflowKind))
        {
            if (!SupportsOverflowArithmetic(type: leftType, operatorSuffix: overflowKind))
            {
                ReportError(
                    SemanticDiagnosticCode.OverflowOperatorNotSupported,
                    $"Overflow arithmetic operator '{binary.Operator.ToStringRepresentation()}' is only supported on fixed-width integer types, not '{leftType.Name}'.",
                    binary.Location);
            }
        }

        // Validate operand types match (no implicit conversions)
        if (!IsAssignableTo(source: rightType, target: leftType) && !IsAssignableTo(source: leftType, target: rightType))
        {
            // Allow error types to pass through
            if (leftType.Category != TypeCategory.Error && rightType.Category != TypeCategory.Error)
            {
                ReportError(
                    SemanticDiagnosticCode.BinaryOperatorTypeMismatch,
                    $"Binary operator '{binary.Operator.ToStringRepresentation()}' cannot be applied to operands of type '{leftType.Name}' and '{rightType.Name}'.",
                    binary.Location);
            }
        }

        // Handle arithmetic/bitwise operators - look for operator method
        string? methodName = binary.Operator.GetMethodName();
        if (methodName != null)
        {
            // Try to find operator method on left type (both regular and crashable versions)
            RoutineInfo? opMethod = _registry.LookupRoutine(fullName: $"{leftType.Name}.{methodName}")
                ?? _registry.LookupRoutine(fullName: $"{leftType.Name}.{methodName}!");

            if (opMethod?.ReturnType != null)
            {
                return opMethod.ReturnType;
            }

            // If no method found but types are numeric, return left type (basic inference)
            if (IsNumericType(type: leftType) && IsNumericType(type: rightType))
            {
                // Checked operators return Maybe<T>
                if (IsCheckedOperator(op: binary.Operator))
                {
                    TypeSymbol? maybeDef = _registry.LookupType(name: "Maybe");
                    if (maybeDef != null)
                    {
                        return _registry.GetOrCreateInstantiation(genericDef: maybeDef, typeArguments: [leftType]);
                    }
                }

                return leftType;
            }
        }

        // Handle none coalescing operator
        if (binary.Operator == BinaryOperator.NoneCoalesce)
        {
            // Returns the non-optional type
            return rightType;
        }

        // Default: return left type
        return leftType;
    }

    /// <summary>
    /// Analyzes an assignment expression (target = value).
    /// Validates mutability, field access, and type compatibility.
    /// </summary>
    /// <param name="target">The assignment target expression.</param>
    /// <param name="value">The value being assigned.</param>
    /// <param name="targetType">The resolved type of the target.</param>
    /// <param name="valueType">The resolved type of the value.</param>
    /// <param name="location">Source location for error reporting.</param>
    /// <returns>The type of the assignment expression (same as target type).</returns>
    private TypeSymbol AnalyzeAssignmentExpression(
        Expression target,
        Expression value,
        TypeSymbol targetType,
        TypeSymbol valueType,
        SourceLocation location)
    {
        // Check if target is assignable (variable, field, or index)
        if (!IsAssignableTarget(target: target))
        {
            ReportError(
                SemanticDiagnosticCode.InvalidAssignmentTarget,
                "Invalid assignment target.",
                target.Location);
            return targetType;
        }

        // Check mutability for variable assignments
        if (target is IdentifierExpression id)
        {
            VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
            if (varInfo is { IsMutable: false })
            {
                ReportError(
                    SemanticDiagnosticCode.AssignmentToImmutable,
                    $"Cannot assign to immutable variable '{id.Name}'.",
                    location);
            }
        }

        // Validate field write access (setter visibility)
        if (target is MemberExpression member)
        {
            TypeSymbol objectType = AnalyzeExpression(expression: member.Object);
            ValidateFieldWriteAccess(objectType: objectType, fieldName: member.PropertyName, location: location);
        }

        // Check mutability for index assignments (let vs var)
        if (target is IndexExpression index)
        {
            // The object being indexed must be mutable
            if (index.Object is IdentifierExpression indexedVar)
            {
                VariableInfo? varInfo = _registry.LookupVariable(name: indexedVar.Name);
                if (varInfo is { IsMutable: false })
                {
                    ReportError(
                        SemanticDiagnosticCode.AssignmentToImmutable,
                        $"Cannot assign to index of immutable variable '{indexedVar.Name}'.",
                        location);
                }
            }
        }

        // Check type compatibility
        if (!IsAssignableTo(source: valueType, target: targetType))
        {
            ReportError(
                SemanticDiagnosticCode.AssignmentTypeMismatch,
                $"Cannot assign value of type '{valueType.Name}' to target of type '{targetType.Name}'.",
                location);
        }

        // Assignment expression returns the target type
        return targetType;
    }

    /// <summary>
    /// Checks if an operator is an overflow-handling operator and returns the overflow kind.
    /// </summary>
    private static bool IsOverflowOperator(BinaryOperator op, out string? overflowKind)
    {
        overflowKind = op switch
        {
            BinaryOperator.AddWrap or BinaryOperator.SubtractWrap or
            BinaryOperator.MultiplyWrap or BinaryOperator.PowerWrap => "wrap",

            BinaryOperator.AddSaturate or BinaryOperator.SubtractSaturate or
            BinaryOperator.MultiplySaturate or BinaryOperator.PowerSaturate => "sat",

            BinaryOperator.AddChecked or BinaryOperator.SubtractChecked or
            BinaryOperator.MultiplyChecked or BinaryOperator.FloorDivideChecked or
            BinaryOperator.ModuloChecked or BinaryOperator.PowerChecked => "checked",

            _ => null
        };

        return overflowKind != null;
    }

    /// <summary>
    /// Checks if an operator is a checked operator that returns Maybe&lt;T&gt;.
    /// </summary>
    private static bool IsCheckedOperator(BinaryOperator op)
    {
        return op is BinaryOperator.AddChecked or BinaryOperator.SubtractChecked or
            BinaryOperator.MultiplyChecked or BinaryOperator.FloorDivideChecked or
            BinaryOperator.ModuloChecked or BinaryOperator.PowerChecked or
            BinaryOperator.ArithmeticLeftShiftChecked;
    }

    private TypeSymbol AnalyzeUnaryExpression(UnaryExpression unary)
    {
        TypeSymbol operandType = AnalyzeExpression(expression: unary.Operand);

        switch (unary.Operator)
        {
            case UnaryOperator.Not:
                if (!IsBoolType(type: operandType))
                {
                    ReportError(SemanticDiagnosticCode.LogicalNotRequiresBool, "Logical 'not' operator requires a boolean operand.", unary.Location);
                }

                return _registry.LookupType(name: "bool") ?? ErrorTypeInfo.Instance;

            case UnaryOperator.Minus:
                if (!IsNumericType(type: operandType))
                {
                    ReportError(SemanticDiagnosticCode.NegationRequiresNumeric, "Negation operator requires a numeric operand.", unary.Location);
                }

                return operandType;

            case UnaryOperator.BitwiseNot:
                if (!IsIntegerType(type: operandType))
                {
                    ReportError(SemanticDiagnosticCode.BitwiseNotRequiresInteger, "Bitwise 'not' operator requires an integer operand.", unary.Location);
                }

                return operandType;

            default:
                return operandType;
        }
    }

    private TypeSymbol AnalyzeCallExpression(CallExpression call)
    {
        // Get the callee type/routine
        if (call.Callee is IdentifierExpression id)
        {
            RoutineInfo? routine = _registry.LookupRoutine(fullName: id.Name);
            if (routine != null)
            {
                // Validate routine access
                ValidateRoutineAccess(routine: routine, accessLocation: call.Location);

                AnalyzeCallArguments(routine: routine, arguments: call.Arguments, location: call.Location);

                // Validate exclusive token uniqueness (cannot pass same Hijacked/Seized twice)
                ValidateExclusiveTokenUniqueness(arguments: call.Arguments, location: call.Location);

                return routine.ReturnType ?? ErrorTypeInfo.Instance;
            }

            // Could be a type constructor
            TypeSymbol? type = _registry.LookupType(name: id.Name);
            if (type != null)
            {
                // Constructor call - also validate token uniqueness
                foreach (Expression arg in call.Arguments)
                {
                    AnalyzeExpression(expression: arg);
                }

                ValidateExclusiveTokenUniqueness(arguments: call.Arguments, location: call.Location);
                return type;
            }
        }

        if (call.Callee is MemberExpression member)
        {
            TypeSymbol objectType = AnalyzeExpression(expression: member.Object);
            RoutineInfo? method = _registry.LookupRoutine(fullName: $"{objectType.Name}.{member.PropertyName}");
            if (method != null)
            {
                // Validate method access
                ValidateRoutineAccess(routine: method, accessLocation: call.Location);

                AnalyzeCallArguments(routine: method, arguments: call.Arguments, location: call.Location);

                // Validate exclusive token uniqueness (cannot pass same Hijacked/Seized twice)
                ValidateExclusiveTokenUniqueness(arguments: call.Arguments, location: call.Location);

                return method.ReturnType ?? ErrorTypeInfo.Instance;
            }
        }

        // Analyze callee expression (lambda or other callable)
        TypeSymbol calleeType = AnalyzeExpression(expression: call.Callee);

        // Analyze arguments
        foreach (Expression arg in call.Arguments)
        {
            AnalyzeExpression(expression: arg);
        }

        // Validate exclusive token uniqueness for dynamic calls too
        ValidateExclusiveTokenUniqueness(arguments: call.Arguments, location: call.Location);

        return calleeType;
    }

    private TypeSymbol AnalyzeMemberExpression(MemberExpression member)
    {
        TypeSymbol objectType = AnalyzeExpression(expression: member.Object);

        // Look up the field/property on the type
        if (objectType is RecordTypeInfo record)
        {
            FieldInfo? field = record.LookupField(fieldName: member.PropertyName);
            if (field != null)
            {
                // Validate field access (read access)
                ValidateFieldAccess(field: field, isWrite: false, accessLocation: member.Location);
                return field.Type;
            }
        }
        else if (objectType is EntityTypeInfo entity)
        {
            FieldInfo? field = entity.LookupField(fieldName: member.PropertyName);
            if (field != null)
            {
                // Validate field access (read access)
                ValidateFieldAccess(field: field, isWrite: false, accessLocation: member.Location);
                return field.Type;
            }
        }
        else if (objectType is ResidentTypeInfo resident)
        {
            FieldInfo? field = resident.LookupField(fieldName: member.PropertyName);
            if (field != null)
            {
                // Validate field access (read access)
                ValidateFieldAccess(field: field, isWrite: false, accessLocation: member.Location);
                return field.Type;
            }
        }
        // Wrapper type forwarding: Viewed<T>, Hijacked<T>, Shared<T>, etc.
        else if (IsWrapperType(type: objectType))
        {
            // Try to forward field access to the inner type
            FieldInfo? innerField = LookupFieldOnWrapperInnerType(wrapperType: objectType, fieldName: member.PropertyName);
            if (innerField != null)
            {
                // Validate field access on the inner type
                ValidateFieldAccess(field: innerField, isWrite: false, accessLocation: member.Location);
                return innerField.Type;
            }

            // Try to forward method access to the inner type
            RoutineInfo? innerMethod = LookupMethodOnWrapperInnerType(wrapperType: objectType, methodName: member.PropertyName);
            if (innerMethod != null)
            {
                // Validate read-only wrapper restrictions
                ValidateReadOnlyWrapperMethodAccess(wrapperType: objectType, method: innerMethod, location: member.Location);
                // Validate method access
                ValidateRoutineAccess(routine: innerMethod, accessLocation: member.Location);
                return innerMethod.ReturnType ?? ErrorTypeInfo.Instance;
            }
        }

        // Could be a method reference - use LookupMethod which handles generic instantiations
        RoutineInfo? method = _registry.LookupMethod(type: objectType, methodName: member.PropertyName);
        if (method != null)
        {
            // Validate method access
            ValidateRoutineAccess(routine: method, accessLocation: member.Location);

            // For generic instantiations, substitute type parameters in return type
            TypeSymbol? returnType = method.ReturnType;
            if (returnType != null && objectType is { IsGenericInstantiation: true, TypeArguments: not null })
            {
                returnType = SubstituteTypeParameters(type: returnType, genericType: objectType);
            }

            return returnType ?? ErrorTypeInfo.Instance;
        }

        ReportError(SemanticDiagnosticCode.MemberNotFound, $"Type '{objectType.Name}' does not have a member '{member.PropertyName}'.", member.Location);
        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeIndexExpression(IndexExpression index)
    {
        TypeSymbol objectType = AnalyzeExpression(expression: index.Object);
        AnalyzeExpression(expression: index.Index);

        // Look for __getitem__ method
        RoutineInfo? getItem = _registry.LookupRoutine(fullName: $"{objectType.Name}.__getitem__");
        if (getItem?.ReturnType != null)
        {
            return getItem.ReturnType;
        }

        // For generic types like List<T>, return the element type
        if (objectType.TypeArguments is { Count: > 0 })
        {
            return objectType.TypeArguments[0];
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeConditionalExpression(ConditionalExpression cond)
    {
        TypeSymbol conditionType = AnalyzeExpression(expression: cond.Condition);

        if (!IsBoolType(type: conditionType))
        {
            ReportError(SemanticDiagnosticCode.ConditionalNotBool, $"Conditional expression requires a boolean condition, got '{conditionType.Name}'.", cond.Condition.Location);
        }

        TypeSymbol trueType = AnalyzeExpression(expression: cond.TrueExpression);
        TypeSymbol falseType = AnalyzeExpression(expression: cond.FalseExpression);

        // Both branches must be compatible
        if (!IsAssignableTo(source: trueType, target: falseType) && !IsAssignableTo(source: falseType, target: trueType))
        {
            ReportError(SemanticDiagnosticCode.ConditionalBranchTypeMismatch, $"Conditional expression branches have incompatible types: '{trueType.Name}' and '{falseType.Name}'.", cond.Location);
        }

        // Return the common type (for now, use the true branch type)
        return trueType;
    }

    private TypeSymbol AnalyzeLambdaExpression(LambdaExpression lambda)
    {
        // Collect variables from enclosing scope that might be captured
        var enclosingScopeVariables = _registry.GetAllVariablesInScope();

        _registry.EnterScope(kind: ScopeKind.Function, name: "lambda");

        // Register lambda parameters and collect their types
        var parameterNames = new HashSet<string>();
        var parameterTypes = new List<TypeSymbol>();
        foreach (Parameter param in lambda.Parameters)
        {
            TypeSymbol paramType = param.Type != null
                ? ResolveType(typeExpr: param.Type)
                : ErrorTypeInfo.Instance;

            _registry.DeclareVariable(name: param.Name, type: paramType, isMutable: false);
            parameterNames.Add(item: param.Name);
            parameterTypes.Add(paramType);
        }

        // Analyze body and get return type
        TypeSymbol returnType = AnalyzeExpression(expression: lambda.Body);

        // Validate captured variables (RazorForge only)
        // Lambda bodies can reference variables from enclosing scope - these are captures
        ValidateLambdaCaptures(
            lambda: lambda,
            enclosingScopeVariables: enclosingScopeVariables,
            parameterNames: parameterNames);

        _registry.ExitScope();

        // Create a proper function type: (ParamTypes) -> ReturnType
        return _registry.GetOrCreateRoutineType(
            parameterTypes: parameterTypes,
            returnType: returnType,
            isFailable: false);
    }

    /// <summary>
    /// Validates that lambda captures don't include forbidden types.
    /// In RazorForge, lambdas cannot capture:
    /// - Memory tokens (Viewed, Hijacked, Inspected, Seized) - scope-bound
    /// - Raw entities - must use handles for capture
    /// </summary>
    /// <param name="lambda">The lambda expression being analyzed.</param>
    /// <param name="enclosingScopeVariables">Variables available in the enclosing scope.</param>
    /// <param name="parameterNames">Names of lambda parameters (not captures).</param>
    private void ValidateLambdaCaptures(
        LambdaExpression lambda,
        IReadOnlyDictionary<string, VariableInfo> enclosingScopeVariables,
        HashSet<string> parameterNames)
    {
        // Find all identifier expressions in the lambda body
        var identifiers = CollectIdentifiers(expression: lambda.Body);

        foreach (IdentifierExpression id in identifiers)
        {
            // Skip if it's a parameter (not a capture)
            if (parameterNames.Contains(item: id.Name))
            {
                continue;
            }

            // Skip special identifiers
            if (id.Name is "me" or "none")
            {
                continue;
            }

            // Check if this identifier refers to a captured variable
            if (enclosingScopeVariables.TryGetValue(key: id.Name, out VariableInfo? varInfo))
            {
                // Validate that the captured type is allowed
                ValidateCapturedType(varName: id.Name, varType: varInfo.Type, location: id.Location);
            }
        }
    }

    /// <summary>
    /// Validates that a captured variable's type is allowed in lambda captures.
    /// </summary>
    /// <param name="varName">Name of the captured variable.</param>
    /// <param name="varType">Type of the captured variable.</param>
    /// <param name="location">Source location for error reporting.</param>
    private void ValidateCapturedType(string varName, TypeSymbol varType, SourceLocation location)
    {
        // Check for memory tokens (scope-bound, cannot be captured)
        if (IsMemoryToken(type: varType))
        {
            string tokenKind = GetMemoryTokenKind(type: varType);
            ReportError(
                SemanticDiagnosticCode.LambdaCaptureToken,
                $"Cannot capture '{varName}' of type '{tokenKind}' in lambda - " +
                $"scope-bound tokens cannot escape their scope. " +
                $"Use a handle type (Shared<T> or Tracked<T>) instead.",
                location);
            return;
        }

        // Check for raw entities (must use handles for capture)
        if (IsRawEntityType(type: varType))
        {
            ReportError(
                SemanticDiagnosticCode.LambdaCaptureRawEntity,
                $"Cannot capture raw entity '{varName}' of type '{varType.Name}' in lambda - " +
                $"raw entities cannot be captured. " +
                $"Wrap in a handle type (Shared<T> or Tracked<T>) before capturing.",
                location);
        }
    }

    /// <summary>
    /// Checks if a type is a raw entity (not wrapped in a handle or token).
    /// </summary>
    private bool IsRawEntityType(TypeSymbol type)
    {
        // Raw entities are entity types that are not wrapped
        return type.Category == TypeCategory.Entity
            && !IsMemoryToken(type: type)
            && !IsStealableHandle(type: type)
            && !IsSnatched(type: type);
    }

    /// <summary>
    /// Collects all identifier expressions in an expression tree.
    /// </summary>
    private static List<IdentifierExpression> CollectIdentifiers(Expression expression)
    {
        var identifiers = new List<IdentifierExpression>();
        CollectIdentifiersRecursive(expression: expression, identifiers: identifiers);
        return identifiers;
    }

    /// <summary>
    /// Recursively collects identifier expressions.
    /// </summary>
    private static void CollectIdentifiersRecursive(Expression expression, List<IdentifierExpression> identifiers)
    {
        switch (expression)
        {
            case IdentifierExpression id:
                identifiers.Add(item: id);
                break;

            case BinaryExpression binary:
                CollectIdentifiersRecursive(expression: binary.Left, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: binary.Right, identifiers: identifiers);
                break;

            case UnaryExpression unary:
                CollectIdentifiersRecursive(expression: unary.Operand, identifiers: identifiers);
                break;

            case StealExpression steal:
                CollectIdentifiersRecursive(expression: steal.Operand, identifiers: identifiers);
                break;

            case BackIndexExpression back:
                CollectIdentifiersRecursive(expression: back.Operand, identifiers: identifiers);
                break;

            case CallExpression call:
                CollectIdentifiersRecursive(expression: call.Callee, identifiers: identifiers);
                foreach (Expression arg in call.Arguments)
                {
                    CollectIdentifiersRecursive(expression: arg, identifiers: identifiers);
                }
                break;

            case MemberExpression member:
                CollectIdentifiersRecursive(expression: member.Object, identifiers: identifiers);
                break;

            case IndexExpression index:
                CollectIdentifiersRecursive(expression: index.Object, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: index.Index, identifiers: identifiers);
                break;

            case ConditionalExpression cond:
                CollectIdentifiersRecursive(expression: cond.Condition, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: cond.TrueExpression, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: cond.FalseExpression, identifiers: identifiers);
                break;

            case LambdaExpression:
                // Don't descend into nested lambdas - they have their own capture context
                break;

            case RangeExpression range:
                CollectIdentifiersRecursive(expression: range.Start, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: range.End, identifiers: identifiers);
                if (range.Step != null)
                {
                    CollectIdentifiersRecursive(expression: range.Step, identifiers: identifiers);
                }
                break;

            case ConstructorExpression ctor:
                foreach ((_, Expression value) in ctor.Fields)
                {
                    CollectIdentifiersRecursive(expression: value, identifiers: identifiers);
                }
                break;

            case ListLiteralExpression list:
                foreach (Expression elem in list.Elements)
                {
                    CollectIdentifiersRecursive(expression: elem, identifiers: identifiers);
                }
                break;

            case SetLiteralExpression set:
                foreach (Expression elem in set.Elements)
                {
                    CollectIdentifiersRecursive(expression: elem, identifiers: identifiers);
                }
                break;

            case DictLiteralExpression dict:
                foreach ((Expression key, Expression value) in dict.Pairs)
                {
                    CollectIdentifiersRecursive(expression: key, identifiers: identifiers);
                    CollectIdentifiersRecursive(expression: value, identifiers: identifiers);
                }
                break;

            case BlockExpression block:
                CollectIdentifiersRecursive(expression: block.Value, identifiers: identifiers);
                break;

            case WithExpression with:
                CollectIdentifiersRecursive(expression: with.Base, identifiers: identifiers);
                foreach ((_, Expression value) in with.Updates)
                {
                    CollectIdentifiersRecursive(expression: value, identifiers: identifiers);
                }
                break;

            case IsPatternExpression isPat:
                CollectIdentifiersRecursive(expression: isPat.Expression, identifiers: identifiers);
                break;

            case NamedArgumentExpression named:
                CollectIdentifiersRecursive(expression: named.Value, identifiers: identifiers);
                break;

            case GenericMethodCallExpression generic:
                CollectIdentifiersRecursive(expression: generic.Object, identifiers: identifiers);
                foreach (Expression arg in generic.Arguments)
                {
                    CollectIdentifiersRecursive(expression: arg, identifiers: identifiers);
                }
                break;

            case GenericMemberExpression genericMember:
                CollectIdentifiersRecursive(expression: genericMember.Object, identifiers: identifiers);
                break;

            case IntrinsicCallExpression intrinsic:
                foreach (Expression arg in intrinsic.Arguments)
                {
                    CollectIdentifiersRecursive(expression: arg, identifiers: identifiers);
                }
                break;

            case NativeCallExpression native:
                foreach (Expression arg in native.Arguments)
                {
                    CollectIdentifiersRecursive(expression: arg, identifiers: identifiers);
                }
                break;

            case TypeConversionExpression conv:
                CollectIdentifiersRecursive(expression: conv.Expression, identifiers: identifiers);
                break;

            case ChainedComparisonExpression chain:
                foreach (Expression operand in chain.Operands)
                {
                    CollectIdentifiersRecursive(expression: operand, identifiers: identifiers);
                }
                break;

            // Literal expressions and type expressions have no identifiers to collect
            case LiteralExpression:
            case TypeExpression:
                break;
        }
    }

    private TypeSymbol AnalyzeRangeExpression(RangeExpression range)
    {
        TypeSymbol startType = AnalyzeExpression(expression: range.Start);
        TypeSymbol endType = AnalyzeExpression(expression: range.End);

        if (range.Step != null)
        {
            AnalyzeExpression(expression: range.Step);
        }

        // Range types must be compatible
        if (!IsNumericType(type: startType) || !IsNumericType(type: endType))
        {
            ReportError(SemanticDiagnosticCode.RangeBoundsNotNumeric, "Range bounds must be numeric types.", range.Location);
        }

        // Return Range type
        return _registry.LookupType(name: "Range") ?? ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeConstructorExpression(ConstructorExpression ctor)
    {
        TypeSymbol? type = _registry.LookupType(name: ctor.TypeName);
        if (type == null)
        {
            ReportError(SemanticDiagnosticCode.UnknownType, $"Unknown type '{ctor.TypeName}'.", ctor.Location);
            return ErrorTypeInfo.Instance;
        }

        // Handle generic type arguments
        if (ctor.TypeArguments is { Count: > 0 })
        {
            var typeArgs = new List<TypeSymbol>();
            foreach (TypeExpression typeArg in ctor.TypeArguments)
            {
                typeArgs.Add(item: ResolveType(typeExpr: typeArg));
            }

            type = _registry.GetOrCreateInstantiation(genericDef: type, typeArguments: typeArgs);
        }

        // Validate field initializers
        ValidateConstructorFields(type: type, fields: ctor.Fields, location: ctor.Location);

        return type;
    }

    /// <summary>
    /// Validates constructor field initializers:
    /// - Each provided field exists on the type
    /// - Value types are assignable to field types
    /// - No duplicate field assignments
    /// - All required fields are provided
    /// </summary>
    private void ValidateConstructorFields(
        TypeSymbol type,
        List<(string Name, Expression Value)> fields,
        SourceLocation location)
    {
        // Get the type's fields
        IReadOnlyList<FieldInfo>? typeFields = type switch
        {
            RecordTypeInfo record => record.Fields,
            EntityTypeInfo entity => entity.Fields,
            ResidentTypeInfo resident => resident.Fields,
            _ => null
        };

        if (typeFields == null)
        {
            if (fields.Count > 0)
            {
                ReportError(
                    SemanticDiagnosticCode.TypeNotFieldInitializable,
                    $"Type '{type.Name}' does not support field initialization.",
                    location);
            }
            return;
        }

        // Build a lookup for expected fields
        var fieldLookup = new Dictionary<string, FieldInfo>();
        foreach (FieldInfo field in typeFields)
        {
            fieldLookup[field.Name] = field;
        }

        // Track which fields have been provided (to detect duplicates and missing fields)
        var providedFields = new HashSet<string>();

        // Validate each provided field
        foreach ((string fieldName, Expression value) in fields)
        {
            // Check for duplicates
            if (!providedFields.Add(fieldName))
            {
                ReportError(
                    SemanticDiagnosticCode.DuplicateFieldInitializer,
                    $"Duplicate field initializer for '{fieldName}'.",
                    value.Location);
                continue;
            }

            // Check if field exists
            if (!fieldLookup.TryGetValue(fieldName, out FieldInfo? expectedField))
            {
                ReportError(
                    SemanticDiagnosticCode.FieldNotFound,
                    $"Type '{type.Name}' does not have a field named '{fieldName}'.",
                    value.Location);
                AnalyzeExpression(expression: value); // Still analyze the value
                continue;
            }

            // Analyze value with expected type for contextual inference
            TypeSymbol fieldType = expectedField.Type;

            // For generic instantiations, substitute type parameters in field type
            if (type is { IsGenericInstantiation: true, TypeArguments: not null })
            {
                fieldType = SubstituteTypeParameters(type: fieldType, genericType: type);
            }

            TypeSymbol valueType = AnalyzeExpression(expression: value, expectedType: fieldType);

            // Check type compatibility
            if (!IsAssignableTo(source: valueType, target: fieldType))
            {
                ReportError(
                    SemanticDiagnosticCode.FieldTypeMismatch,
                    $"Cannot assign '{valueType.Name}' to field '{fieldName}' of type '{fieldType.Name}'.",
                    value.Location);
            }
        }

        // Check for missing required fields (fields without default values)
        foreach (FieldInfo field in typeFields)
        {
            if (!providedFields.Contains(field.Name) && !field.HasDefaultValue)
            {
                ReportError(
                    SemanticDiagnosticCode.MissingRequiredField,
                    $"Missing required field '{field.Name}' in constructor for '{type.Name}'.",
                    location);
            }
        }
    }

    private TypeSymbol AnalyzeListLiteralExpression(ListLiteralExpression list)
    {
        TypeSymbol? elementType = null;

        if (list.ElementType != null)
        {
            elementType = ResolveType(typeExpr: list.ElementType);
        }
        else if (list.Elements.Count > 0)
        {
            // Infer from first element
            elementType = AnalyzeExpression(expression: list.Elements[0]);

            // Validate all elements have compatible types
            for (int i = 1; i < list.Elements.Count; i++)
            {
                TypeSymbol elemType = AnalyzeExpression(expression: list.Elements[i]);
                if (!IsAssignableTo(source: elemType, target: elementType))
                {
                    ReportError(SemanticDiagnosticCode.ListElementTypeMismatch, $"List element type mismatch: expected '{elementType.Name}', got '{elemType.Name}'.", list.Elements[i].Location);
                }
            }
        }
        else
        {
            ReportError(SemanticDiagnosticCode.EmptyListNoTypeAnnotation, "Cannot infer element type from empty list literal without type annotation.", list.Location);
            elementType = ErrorTypeInfo.Instance;
        }

        // Return List<T> type
        TypeSymbol? listDef = _registry.LookupType(name: "List");
        if (listDef != null && elementType != null)
        {
            return _registry.GetOrCreateInstantiation(genericDef: listDef, typeArguments: [elementType]);
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeSetLiteralExpression(SetLiteralExpression set)
    {
        TypeSymbol? elementType = null;

        if (set.ElementType != null)
        {
            elementType = ResolveType(typeExpr: set.ElementType);
        }
        else if (set.Elements.Count > 0)
        {
            elementType = AnalyzeExpression(expression: set.Elements[0]);
        }
        else
        {
            ReportError(SemanticDiagnosticCode.EmptySetNoTypeAnnotation, "Cannot infer element type from empty set literal without type annotation.", set.Location);
            elementType = ErrorTypeInfo.Instance;
        }

        // Analyze all elements
        foreach (Expression elem in set.Elements)
        {
            AnalyzeExpression(expression: elem);
        }

        // Return Set<T> type
        TypeSymbol? setDef = _registry.LookupType(name: "Set");
        if (setDef != null && elementType != null)
        {
            return _registry.GetOrCreateInstantiation(genericDef: setDef, typeArguments: [elementType]);
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeDictLiteralExpression(DictLiteralExpression dict)
    {
        TypeSymbol? keyType = null;
        TypeSymbol? valueType = null;

        if (dict is { KeyType: not null, ValueType: not null })
        {
            keyType = ResolveType(typeExpr: dict.KeyType);
            valueType = ResolveType(typeExpr: dict.ValueType);
        }
        else if (dict.Pairs.Count > 0)
        {
            keyType = AnalyzeExpression(expression: dict.Pairs[0].Key);
            valueType = AnalyzeExpression(expression: dict.Pairs[0].Value);
        }
        else
        {
            ReportError(SemanticDiagnosticCode.EmptyDictNoTypeAnnotation, "Cannot infer types from empty dict literal without type annotation.", dict.Location);
            keyType = ErrorTypeInfo.Instance;
            valueType = ErrorTypeInfo.Instance;
        }

        // Analyze all pairs
        foreach ((Expression Key, Expression Value) pair in dict.Pairs)
        {
            AnalyzeExpression(expression: pair.Key);
            AnalyzeExpression(expression: pair.Value);
        }

        // Return Dict<K, V> type
        TypeSymbol? dictDef = _registry.LookupType(name: "Dict");
        if (dictDef != null && keyType != null && valueType != null)
        {
            return _registry.GetOrCreateInstantiation(genericDef: dictDef, typeArguments: [keyType, valueType]);
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeTypeConversionExpression(TypeConversionExpression conv)
    {
        AnalyzeExpression(expression: conv.Expression);

        TypeSymbol? targetType = _registry.LookupType(name: conv.TargetType);
        if (targetType == null)
        {
            ReportError(SemanticDiagnosticCode.UnknownConversionTargetType, $"Unknown conversion target type '{conv.TargetType}'.", conv.Location);
            return ErrorTypeInfo.Instance;
        }

        return targetType;
    }

    private TypeSymbol AnalyzeChainedComparisonExpression(ChainedComparisonExpression chain)
    {
        // Validate that operators don't mix ascending and descending
        ValidateComparisonChain(chain: chain, location: chain.Location);

        // Analyze all operands and validate comparisons between consecutive pairs
        var operandTypes = new List<TypeSymbol>();
        foreach (Expression operand in chain.Operands)
        {
            operandTypes.Add(item: AnalyzeExpression(expression: operand));
        }

        // Validate each comparison pair
        for (int i = 0; i < chain.Operators.Count; i++)
        {
            ValidateComparisonOperands(
                left: operandTypes[i],
                right: operandTypes[i + 1],
                op: chain.Operators[i],
                location: chain.Location);
        }

        // Chained comparisons always return bool
        return _registry.LookupType(name: "bool") ?? ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeBlockExpression(BlockExpression block)
    {
        // Block expression evaluates to its contained value expression
        return AnalyzeExpression(expression: block.Value);
    }

    private TypeSymbol AnalyzeWithExpression(WithExpression with)
    {
        TypeSymbol baseType = AnalyzeExpression(expression: with.Base);

        // Validate that base is a record type
        if (baseType.Category != TypeCategory.Record)
        {
            ReportError(SemanticDiagnosticCode.WithExpressionNotRecord, $"'with' expression requires a record type, got '{baseType.Name}'.", with.Location);
        }

        // Analyze update expressions
        foreach ((string FieldName, Expression Value) update in with.Updates)
        {
            AnalyzeExpression(expression: update.Value);
            // TODO: Validate field exists and types match
        }

        // Returns the same type as the base
        return baseType;
    }

    /// <summary>
    /// Analyzes a when expression (pattern matching expression).
    /// Returns the common type of all branch results.
    /// </summary>
    private TypeSymbol AnalyzeWhenExpression(WhenExpression when)
    {
        // Analyze the matched expression
        TypeSymbol matchedType = AnalyzeExpression(expression: when.Expression);

        TypeSymbol? resultType = null;
        bool hasElse = false;

        foreach (WhenClause clause in when.Clauses)
        {
            // Analyze the pattern
            AnalyzePattern(pattern: clause.Pattern, matchedType: matchedType);

            // Check for else clause
            if (clause.Pattern is WildcardPattern or ElsePattern)
            {
                hasElse = true;
            }

            // When expressions require expression bodies that return values
            // The Body is a Statement, but for expressions it should typically be an ExpressionStatement
            if (clause.Body is ExpressionStatement exprStmt)
            {
                TypeSymbol branchType = AnalyzeExpression(expression: exprStmt.Expression);

                if (resultType == null)
                {
                    resultType = branchType;
                }
                else if (!IsAssignableTo(source: branchType, target: resultType))
                {
                    ReportError(
                        SemanticDiagnosticCode.WhenBranchTypeMismatch,
                        $"When expression branches have incompatible types: '{resultType.Name}' and '{branchType.Name}'.",
                        clause.Body.Location);
                }
            }
            else if (clause.Body is ReturnStatement ret && ret.Value != null)
            {
                // Allow return statements in when expressions
                TypeSymbol branchType = AnalyzeExpression(expression: ret.Value);

                if (resultType == null)
                {
                    resultType = branchType;
                }
            }
            else if (clause.Body is BlockStatement block)
            {
                // For block statements, analyze and try to infer result type from last statement
                foreach (Statement stmt in block.Statements)
                {
                    AnalyzeStatement(statement: stmt);
                }
                // Block in expression context - result type is harder to determine
                // For now, keep previous result type
            }
            else
            {
                // Analyze as regular statement
                AnalyzeStatement(statement: clause.Body);
            }
        }

        // Warn if no else clause (expression may not cover all cases)
        if (!hasElse)
        {
            ReportWarning(
                SemanticWarningCode.NonExhaustiveWhen,
                "When expression may not cover all cases - consider adding an 'else' clause.",
                when.Location);
        }

        return resultType ?? ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeGenericMethodCallExpression(GenericMethodCallExpression generic)
    {
        TypeSymbol objectType = AnalyzeExpression(expression: generic.Object);

        // Resolve type arguments
        var typeArgs = new List<TypeSymbol>();
        foreach (TypeExpression typeArg in generic.TypeArguments)
        {
            typeArgs.Add(item: ResolveType(typeExpr: typeArg));
        }

        // Look up the method
        RoutineInfo? method = _registry.LookupRoutine(fullName: $"{objectType.Name}.{generic.MethodName}");
        if (method?.ReturnType != null)
        {
            // Substitute type arguments in return type
            return method.ReturnType;
        }

        // Analyze arguments
        foreach (Expression arg in generic.Arguments)
        {
            AnalyzeExpression(expression: arg);
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeGenericMemberExpression(GenericMemberExpression genericMember)
    {
        AnalyzeExpression(expression: genericMember.Object);

        // Resolve type arguments
        foreach (TypeExpression typeArg in genericMember.TypeArguments)
        {
            ResolveType(typeExpr: typeArg);
        }

        // Look up the member with type arguments
        // TODO: Implement proper generic member resolution

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeIntrinsicCallExpression(IntrinsicCallExpression intrinsic)
    {
        // Intrinsic calls require being in a danger block
        if (!InDangerBlock)
        {
            ReportError(
                SemanticDiagnosticCode.IntrinsicOutsideDanger,
                $"Intrinsic call '@intrinsic.{intrinsic.IntrinsicName}' can only be used inside a danger block.",
                intrinsic.Location);
        }

        foreach (Expression arg in intrinsic.Arguments)
        {
            AnalyzeExpression(expression: arg);
        }

        // Return type depends on the specific intrinsic
        // For now, return based on type arguments if available
        if (intrinsic.TypeArguments.Count > 0)
        {
            TypeSymbol? type = _registry.LookupType(name: intrinsic.TypeArguments[0]);
            if (type != null)
            {
                return type;
            }
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeNativeCallExpression(NativeCallExpression native)
    {
        // Native calls require being in a danger block
        if (!InDangerBlock)
        {
            ReportError(
                SemanticDiagnosticCode.NativeOutsideDanger,
                $"Native call '@native.{native.FunctionName}' can only be used inside a danger block.",
                native.Location);
        }

        foreach (Expression arg in native.Arguments)
        {
            AnalyzeExpression(expression: arg);
        }

        // Native calls return platform-dependent types
        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeIsPatternExpression(IsPatternExpression isPat)
    {
        TypeSymbol exprType = AnalyzeExpression(expression: isPat.Expression);

        // Analyze the pattern (may bind variables)
        AnalyzePattern(pattern: isPat.Pattern, matchedType: exprType);

        // 'is' expressions always return bool
        return _registry.LookupType(name: "bool") ?? ErrorTypeInfo.Instance;
    }

    private TypeSymbol HandleUnknownExpression(Expression expression)
    {
        ReportWarning(SemanticWarningCode.UnknownExpressionType, $"Unknown expression type: {expression.GetType().Name}", expression.Location);
        return ErrorTypeInfo.Instance;
    }

    /// <summary>
    /// Substitutes type parameters in a type based on a generic instantiation.
    /// For example, if genericType is List&lt;S32&gt; and type is T, returns S32.
    /// </summary>
    /// <param name="type">The type that may contain type parameters.</param>
    /// <param name="genericType">The instantiated generic type providing type argument bindings.</param>
    /// <returns>The substituted type.</returns>
    private TypeSymbol SubstituteTypeParameters(TypeSymbol type, TypeSymbol genericType)
    {
        if (genericType.TypeArguments == null || genericType.TypeArguments.Count == 0)
        {
            return type;
        }

        // Get the generic definition to find type parameter names
        TypeSymbol? genericDef = GetGenericDefinition(instantiation: genericType);
        if (genericDef == null)
        {
            return type;
        }

        // Build a mapping from type parameter names to actual types
        IReadOnlyList<string>? typeParamNames = genericDef.GenericParameters;
        if (typeParamNames == null || typeParamNames.Count != genericType.TypeArguments.Count)
        {
            return type;
        }

        var substitutions = new Dictionary<string, TypeSymbol>();
        for (int i = 0; i < typeParamNames.Count; i++)
        {
            substitutions[typeParamNames[i]] = genericType.TypeArguments[i];
        }

        return SubstituteWithMapping(type: type, substitutions: substitutions);
    }

    /// <summary>
    /// Gets the generic definition from an instantiation.
    /// </summary>
    private TypeSymbol? GetGenericDefinition(TypeSymbol instantiation)
    {
        if (!instantiation.IsGenericInstantiation)
        {
            return null;
        }

        // Extract base name (e.g., "List" from "List<S32>")
        string baseName = GetBaseTypeName(typeName: instantiation.Name);
        return _registry.LookupType(name: baseName);
    }

    /// <summary>
    /// Substitutes type parameters using a mapping.
    /// </summary>
    private TypeSymbol SubstituteWithMapping(TypeSymbol type, Dictionary<string, TypeSymbol> substitutions)
    {
        // Direct type parameter replacement
        if (substitutions.TryGetValue(type.Name, out TypeSymbol? replacement))
        {
            return replacement;
        }

        // For generic instantiations, recursively substitute in type arguments
        if (type is { IsGenericInstantiation: true, TypeArguments: not null })
        {
            var substitutedArgs = new List<TypeSymbol>();
            bool anyChanged = false;

            foreach (TypeSymbol arg in type.TypeArguments)
            {
                TypeSymbol substitutedArg = SubstituteWithMapping(type: arg, substitutions: substitutions);
                substitutedArgs.Add(substitutedArg);
                if (!ReferenceEquals(substitutedArg, arg))
                {
                    anyChanged = true;
                }
            }

            if (anyChanged)
            {
                // Create a new instantiation with substituted arguments
                TypeSymbol? baseDef = GetGenericDefinition(instantiation: type);
                if (baseDef != null)
                {
                    return _registry.GetOrCreateInstantiation(genericDef: baseDef, typeArguments: substitutedArgs);
                }
            }
        }

        return type;
    }

    /// <summary>
    /// Analyzes a steal expression (RazorForge only).
    /// Validates that the operand can be stolen and returns the stolen type.
    /// </summary>
    /// <param name="steal">The steal expression to analyze.</param>
    /// <returns>The type of the stolen value.</returns>
    /// <remarks>
    /// Stealable types:
    /// - Raw entities (direct entity references)
    /// - Shared&lt;T&gt; (shared ownership handle)
    /// - Tracked&lt;T&gt; (reference-counted handle)
    ///
    /// Non-stealable types (compile error):
    /// - Viewed&lt;T&gt; (read-only token, scope-bound)
    /// - Hijacked&lt;T&gt; (exclusive token, scope-bound)
    /// - Inspected&lt;T&gt; (thread-safe read token, scope-bound)
    /// - Seized&lt;T&gt; (thread-safe exclusive token, scope-bound)
    /// - Snatched&lt;T&gt; (internal ownership, not for user code)
    /// </remarks>
    private TypeSymbol AnalyzeStealExpression(StealExpression steal)
    {
        // Analyze the operand
        TypeSymbol operandType = AnalyzeExpression(expression: steal.Operand);

        // Check if the type is a memory token (cannot be stolen)
        if (IsMemoryToken(type: operandType))
        {
            string tokenKind = GetMemoryTokenKind(type: operandType);
            ReportError(
                SemanticDiagnosticCode.StealScopeBoundToken,
                $"Cannot steal '{tokenKind}' - scope-bound tokens cannot be stolen. " +
                $"Only raw entities, Shared<T>, and Tracked<T> can be stolen.",
                steal.Location);
            return operandType;
        }

        // Check for Snatched<T> (internal ownership, not for user code)
        if (IsSnatched(type: operandType))
        {
            ReportError(
                SemanticDiagnosticCode.StealSnatched,
                "Cannot steal 'Snatched<T>' - internal ownership type cannot be stolen.",
                steal.Location);
            return operandType;
        }

        // For Shared<T> or Tracked<T>, return the inner type
        if (IsStealableHandle(type: operandType))
        {
            // Unwrap the handle to get the inner type
            if (operandType.TypeArguments is { Count: > 0 })
            {
                return operandType.TypeArguments[0];
            }
        }

        // For raw entities (not wrapped), return the same type
        // The steal operation moves ownership, making the source a deadref
        return operandType;
    }

    /// <summary>
    /// Checks if a type is a memory token (Viewed, Hijacked, Inspected, Seized).
    /// Memory tokens are scope-bound and cannot be stolen.
    /// </summary>
    private static bool IsMemoryToken(TypeSymbol type)
    {
        return type.Name is "Viewed" or "Hijacked" or "Inspected" or "Seized"
            || (type.Name.StartsWith(value: "Viewed<") ||
                type.Name.StartsWith(value: "Hijacked<") ||
                type.Name.StartsWith(value: "Inspected<") ||
                type.Name.StartsWith(value: "Seized<"));
    }

    /// <summary>
    /// Gets the kind of memory token for error messages.
    /// </summary>
    private static string GetMemoryTokenKind(TypeSymbol type)
    {
        if (type.Name.StartsWith(value: "Viewed")) return "Viewed<T>";
        if (type.Name.StartsWith(value: "Hijacked")) return "Hijacked<T>";
        if (type.Name.StartsWith(value: "Inspected")) return "Inspected<T>";
        if (type.Name.StartsWith(value: "Seized")) return "Seized<T>";
        return type.Name;
    }

    /// <summary>
    /// Checks if a type is Snatched&lt;T&gt; (internal ownership type).
    /// </summary>
    private static bool IsSnatched(TypeSymbol type)
    {
        return type.Name == "Snatched" || type.Name.StartsWith(value: "Snatched<");
    }

    /// <summary>
    /// Checks if a type is a stealable handle (Shared&lt;T&gt; or Tracked&lt;T&gt;).
    /// </summary>
    private static bool IsStealableHandle(TypeSymbol type)
    {
        return type.Name is "Shared" or "Tracked"
            || type.Name.StartsWith(value: "Shared<")
            || type.Name.StartsWith(value: "Tracked<");
    }

    /// <summary>
    /// Analyzes a backindex expression (^n = index from end).
    /// Validates that the operand is a non-negative integer type.
    /// </summary>
    /// <param name="back">The back index expression to analyze.</param>
    /// <returns>The BackIndex type.</returns>
    /// <remarks>
    /// BackIndex expressions create indices that count from the end of a sequence:
    /// - ^1 = last element
    /// - ^2 = second to last element
    /// - ^0 = one past the end (valid for slicing, not indexing)
    ///
    /// Used with IndexExpression for end-relative indexing: list[^1], text[^3]
    /// </remarks>
    private TypeSymbol AnalyzeBackIndexExpression(BackIndexExpression back)
    {
        // Analyze the operand
        TypeSymbol operandType = AnalyzeExpression(expression: back.Operand);

        // Validate that the operand is an integer type
        if (!IsIntegerType(type: operandType))
        {
            ReportError(
                SemanticDiagnosticCode.BackIndexRequiresInteger,
                $"BackIndex operator '^' requires an integer operand, got '{operandType.Name}'.",
                back.Location);
        }

        // Return a BackIndex type (or UAddr as the underlying representation)
        // BackIndex is conceptually a wrapper around an offset from the end
        TypeSymbol? backIndexType = _registry.LookupType(name: "BackIndex");
        if (backIndexType != null)
        {
            return backIndexType;
        }

        // Fallback: return UAddr as the index representation
        return _registry.LookupType(name: "UAddr") ?? operandType;
    }

    /// <summary>
    /// Creates a RoutineTypeInfo from a RoutineInfo for first-class function references.
    /// </summary>
    /// <param name="routine">The routine to create a type for.</param>
    /// <returns>The function type representing this routine's signature.</returns>
    private RoutineTypeInfo GetRoutineType(RoutineInfo routine)
    {
        // Extract parameter types from ParameterInfo
        List<TypeSymbol> parameterTypes = routine.Parameters
            .Select(selector: p => p.Type)
            .ToList();

        // Get return type (null means Blank/void)
        TypeSymbol? returnType = routine.ReturnType;

        // Create or retrieve the cached function type
        return _registry.GetOrCreateRoutineType(
            parameterTypes: parameterTypes,
            returnType: returnType,
            isFailable: routine.IsFailable);
    }

    /// <summary>
    /// Analyzes a waitfor expression (async/concurrency).
    /// Waits for an async operation to complete, optionally with a timeout.
    /// </summary>
    /// <param name="waitfor">The waitfor expression to analyze.</param>
    /// <returns>The type of the awaited value.</returns>
    private TypeSymbol AnalyzeWaitforExpression(WaitforExpression waitfor)
    {
        // Analyze the operand (the async operation to wait for)
        TypeSymbol operandType = AnalyzeExpression(expression: waitfor.Operand);

        // Analyze optional timeout expression
        if (waitfor.Timeout != null)
        {
            TypeSymbol timeoutType = AnalyzeExpression(expression: waitfor.Timeout);

            // Validate that timeout is a Duration type
            TypeSymbol? durationType = _registry.LookupType(name: "Duration");
            if (durationType != null && !IsAssignableTo(source: timeoutType, target: durationType))
            {
                ReportError(
                    SemanticDiagnosticCode.WaitforTimeoutNotDuration,
                    $"Waitfor 'until' clause requires a Duration, got '{timeoutType.Name}'.",
                    waitfor.Timeout.Location);
            }
        }

        // TODO: Validate that we're inside a suspended/threaded routine

        // The result type is the inner type of the async operation
        // For now, return the operand type directly
        return operandType;
    }

    #endregion
}
