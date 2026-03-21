namespace SemanticAnalysis;

using Enums;
using Symbols;
using Types;
using SyntaxTree;
using Diagnostics;
using TypeSymbol = Types.TypeInfo;

#region Numeric Type Classification

/// <summary>
/// Classification of numeric types for type checking purposes.
/// </summary>
internal enum NumericTypeKind
{
    /// <summary>Not a numeric type.</summary>
    None,

    /// <summary>Signed fixed-width integers (s8, s16, s32, s64, s128).</summary>
    SignedInteger,

    /// <summary>Unsigned fixed-width integers (u8, u16, u32, u64, u128).</summary>
    UnsignedInteger,

    /// <summary>System-dependent signed integer (saddr).</summary>
    SignedAddress,

    /// <summary>System-dependent unsigned integer (uaddr).</summary>
    UnsignedAddress,

    /// <summary>Binary floating point (f16, f32, f64, f128).</summary>
    BinaryFloat,

    /// <summary>Decimal floating point (d32, d64, d128).</summary>
    DecimalFloat,

    /// <summary>Arbitrary precision integer (Suflae Integer).</summary>
    ArbitraryInteger,

    /// <summary>Arbitrary precision decimal (Suflae Decimal).</summary>
    ArbitraryDecimal,

    /// <summary>Exact rational number (Suflae Fraction).</summary>
    Fraction
}

#endregion

/// <summary>
/// Helper methods for analysis.
/// </summary>
public sealed partial class SemanticAnalyzer
{
    #region Helper Methods for Analysis

    /// <summary>
    /// Validates argument count and types for a routine call against the routine's parameter list.
    /// Reports errors for too-few arguments, too-many arguments (on non-variadic routines), and type mismatches.
    /// </summary>
    private void AnalyzeCallArguments(RoutineInfo routine, List<Expression> arguments, SourceLocation location)
    {
        IReadOnlyList<ParameterInfo> parameters = routine.Parameters;
        int totalParams = parameters.Count;

        // Phase 1: Validate named argument ordering and build parameter bindings.
        // Each entry maps parameter index → argument expression.
        bool seenNamed = false;
        var boundParams = new Dictionary<int, Expression>();
        int positionalIndex = 0;

        // S510: Routines with 2+ non-me parameters require all arguments to be named.
        // This prevents argument-swap bugs at call sites. Variadic routines are exempt
        // because their extra positional args don't map to named parameters.
        int nonMeParamCount = parameters.Count(predicate: p => p.Name != "me");
        bool requiresNamedArgs = nonMeParamCount >= 2 && !routine.IsVariadic;

        foreach (Expression arg in arguments)
        {
            if (arg is NamedArgumentExpression named)
            {
                seenNamed = true;

                // Look up parameter by name
                int paramIndex = -1;
                for (int j = 0; j < totalParams; j++)
                {
                    if (parameters[j].Name == named.Name)
                    {
                        paramIndex = j;
                        break;
                    }
                }

                if (paramIndex == -1)
                {
                    // S505: Unknown named argument
                    ReportError(
                        SemanticDiagnosticCode.UnknownNamedArgument,
                        $"'{routine.Name}' has no parameter named '{named.Name}'.",
                        named.Location);
                    AnalyzeExpression(expression: named.Value);
                }
                else if (boundParams.ContainsKey(key: paramIndex))
                {
                    // S506: Duplicate named argument (parameter already bound)
                    ReportError(
                        SemanticDiagnosticCode.DuplicateNamedArgument,
                        $"Parameter '{named.Name}' of '{routine.Name}' is already bound.",
                        named.Location);
                    AnalyzeExpression(expression: named.Value);
                }
                else
                {
                    boundParams[key: paramIndex] = named.Value;
                }
            }
            else
            {
                if (requiresNamedArgs)
                {
                    // S510: Named argument enforcement — subsumes S507
                    ReportError(
                        SemanticDiagnosticCode.NamedArgumentRequired,
                        $"Routine '{routine.Name}' has {nonMeParamCount} parameters — all arguments must be named.",
                        arg.Location);
                }
                else if (seenNamed)
                {
                    // S507: Positional argument after named argument
                    ReportError(
                        SemanticDiagnosticCode.PositionalAfterNamed,
                        $"Positional argument cannot appear after named arguments in call to '{routine.Name}'.",
                        arg.Location);
                }

                if (positionalIndex < totalParams)
                {
                    if (boundParams.ContainsKey(key: positionalIndex))
                    {
                        // S506: Positional arg collides with earlier named arg that bound this slot
                        ReportError(
                            SemanticDiagnosticCode.DuplicateNamedArgument,
                            $"Parameter '{parameters[positionalIndex].Name}' of '{routine.Name}' is already bound.",
                            arg.Location);
                    }
                    else
                    {
                        boundParams[key: positionalIndex] = arg;
                    }
                }
                else if (!routine.IsVariadic)
                {
                    // Extra positional arg beyond parameter count — handled by count check below
                    boundParams[key: positionalIndex] = arg;
                }
                else
                {
                    // Variadic extra argument — just analyze it
                    AnalyzeExpression(expression: arg);
                }

                positionalIndex++;
            }
        }

        // Phase 2: Check argument count against required parameters.
        int requiredParams = parameters.Count(p => !p.HasDefaultValue);
        int unboundRequired = 0;
        for (int i = 0; i < totalParams; i++)
        {
            if (!boundParams.ContainsKey(key: i) && !parameters[i].HasDefaultValue)
            {
                unboundRequired++;
            }
        }

        if (unboundRequired > 0)
        {
            if (requiredParams == totalParams)
            {
                ReportError(
                    SemanticDiagnosticCode.TooFewArguments,
                    $"'{routine.Name}' expects {totalParams} argument(s), but got {arguments.Count}.",
                    location);
            }
            else
            {
                ReportError(
                    SemanticDiagnosticCode.TooFewArguments,
                    $"'{routine.Name}' expects at least {requiredParams} argument(s), but got {arguments.Count}.",
                    location);
            }
        }
        else if (positionalIndex > totalParams && !routine.IsVariadic)
        {
            ReportError(
                SemanticDiagnosticCode.TooManyArguments,
                $"'{routine.Name}' expects at most {totalParams} argument(s), but got {arguments.Count}.",
                location);
        }

        // Phase 3: Type-check each bound argument against its parameter.
        foreach (KeyValuePair<int, Expression> binding in boundParams)
        {
            if (binding.Key >= totalParams)
            {
                // Extra positional beyond params (already reported as TooManyArguments)
                AnalyzeExpression(expression: binding.Value);
                continue;
            }

            ParameterInfo param = parameters[binding.Key];
            TypeSymbol paramType = param.Type;

            Expression argExpr = binding.Value;
            TypeSymbol argType = AnalyzeExpression(expression: argExpr, expectedType: paramType);

            if (argType.Category == TypeCategory.Error || paramType.Category == TypeCategory.Error)
            {
                continue;
            }

            if (!IsAssignableTo(source: argType, target: paramType))
            {
                ReportError(
                    SemanticDiagnosticCode.ArgumentTypeMismatch,
                    $"Argument '{param.Name}' of '{routine.Name}': cannot convert '{argType.Name}' to '{paramType.Name}'.",
                    argExpr.Location);
            }
        }
    }

    /// <summary>
    /// Returns true if the expression can appear on the left-hand side of an assignment.
    /// Valid assignment targets are identifiers, member accesses, and index expressions.
    /// </summary>
    private bool IsAssignableTarget(Expression target)
    {
        return target is IdentifierExpression
            or MemberExpression
            or IndexExpression;
    }

    /// <summary>
    /// Returns true if a value of type <paramref name="source"/> can be assigned to a variable of type <paramref name="target"/>.
    /// Handles error types (to suppress cascading errors), generic resolution matching, and protocol conformance.
    /// No implicit numeric or widening conversions are performed.
    /// </summary>
    private bool IsAssignableTo(TypeSymbol source, TypeSymbol target)
    {
        // Same type
        if (source.Name == target.Name)
        {
            return true;
        }

        // Error types are assignable to anything (to reduce cascading errors)
        if (source.Category == TypeCategory.Error || target.Category == TypeCategory.Error)
        {
            return true;
        }

        // Generic type matching - check if resolution matches definition
        if (target.IsGenericDefinition && source.IsGenericResolution)
        {
            string baseName = GetBaseTypeName(typeName: source.Name);
            if (baseName == target.Name)
            {
                return true;
            }
        }

        // Protocol conformance - if target is a protocol, check if source implements it
        if (target.Category == TypeCategory.Protocol)
        {
            return ImplementsProtocol(type: source, protocolName: target.Name);
        }

        // No implicit conversions - all type conversions must be explicit via creator syntax
        return false;
    }

    /// <summary>
    /// Gets the base type name without generic arguments.
    /// </summary>
    private static string GetBaseTypeName(string typeName)
    {
        int genericIndex = typeName.IndexOf(value: '[');
        return genericIndex >= 0 ? typeName[..genericIndex] : typeName;
    }

    /// <summary>Returns true if the type is the built-in <c>Bool</c> type.</summary>
    private bool IsBoolType(TypeSymbol type)
    {
        return type.Name is "Bool";
    }

    /// <summary>Returns true if the type is any numeric type (integer, binary float, or decimal float).</summary>
    private bool IsNumericType(TypeSymbol type)
    {
        return IsIntegerType(type: type) || IsFloatType(type: type) || IsDecimalType(type: type);
    }

    /// <summary>
    /// Returns true if the type implements the <c>Integral</c> protocol (i.e., is a fixed-width or
    /// arbitrary-precision integer type such as s32, u64, uaddr, or Suflae's Integer).
    /// </summary>
    private bool IsIntegerType(TypeSymbol type)
    {
        // Check if type obeys the Integral protocol
        return ImplementsProtocol(type: type, protocolName: "Integral");
    }

    /// <summary>
    /// Returns true if the type implements the <c>BinaryFP</c> protocol (i.e., is a binary
    /// floating-point type such as f32 or f64).
    /// </summary>
    private bool IsFloatType(TypeSymbol type)
    {
        // Check if type obeys the Floating protocol (binary floats)
        return ImplementsProtocol(type: type, protocolName: "BinaryFP");
    }

    /// <summary>
    /// Returns true if the type implements the <c>DecimalFP</c> protocol (i.e., is a decimal
    /// floating-point type such as d64 or Suflae's Decimal).
    /// </summary>
    private bool IsDecimalType(TypeSymbol type)
    {
        // Check if type obeys the DecimalFloating protocol
        return ImplementsProtocol(type: type, protocolName: "DecimalFP");
    }

    /// <summary>Returns true if the type is a complex number type (C32, C64, C128, Complex).</summary>
    private static bool IsComplexType(TypeSymbol type)
    {
        return type.Name is "C32" or "C64" or "C128" or "Complex";
    }

    /// <summary>
    /// Checks if a type supports a specific binary operator by looking up the operator method.
    /// </summary>
    private bool SupportsOperator(TypeSymbol type, BinaryOperator op)
    {
        string? methodName = op.GetMethodName();
        if (methodName == null)
        {
            return false;
        }

        // Try both regular and crashable (!) versions
        return _registry.LookupRoutine(fullName: $"{type.Name}.{methodName}") != null
            || _registry.LookupRoutine(fullName: $"{type.Name}.{methodName}!") != null;
    }

    /// <summary>
    /// Checks if an operator is a comparison operator that returns Bool.
    /// Includes both identity operators and overloadable comparison/membership operators.
    /// Note: ThreeWayComparator (&lt;=&gt;) returns ComparisonSign, not Bool, so it is excluded.
    /// </summary>
    private static bool IsComparisonOperator(BinaryOperator op)
    {
        return op is BinaryOperator.Equal or BinaryOperator.NotEqual
            or BinaryOperator.Less or BinaryOperator.LessEqual
            or BinaryOperator.Greater or BinaryOperator.GreaterEqual
            or BinaryOperator.In or BinaryOperator.NotIn
            or BinaryOperator.Identical or BinaryOperator.NotIdentical
            or BinaryOperator.Is or BinaryOperator.IsNot
            or BinaryOperator.Obeys or BinaryOperator.NotObeys;
    }

    /// <summary>Returns true if the operator is a short-circuit logical operator (<c>and</c> or <c>or</c>).</summary>
    private bool IsLogicalOperator(BinaryOperator op)
    {
        return op is BinaryOperator.And or BinaryOperator.Or;
    }

    /// <summary>
    /// Operator dunder methods that choices are NOT allowed to define or call.
    /// Choices do not support any operators — use 'is' for case matching.
    /// </summary>
    private static readonly HashSet<string> OperatorDunders =
    [
        // Arithmetic
        "__add__", "__sub__", "__mul__", "__truediv__", "__floordiv__", "__mod__", "__pow__",
        // Wrapping arithmetic
        "__add_wrap__", "__sub_wrap__", "__mul_wrap__", "__pow_wrap__",
        // Clamping arithmetic
        "__add_clamp__", "__sub_clamp__", "__mul_clamp__", "__truediv_clamp__", "__pow_clamp__",
        // Comparison
        "__eq__", "__ne__", "__lt__", "__le__", "__gt__", "__ge__", "__cmp__",
        // Bitwise
        "__and__", "__or__", "__xor__",
        "__ashl__", "__ashr__", "__lshl__", "__lshr__",
        // Unary
        "__neg__", "__not__",
        // Membership
        "__contains__", "__notcontains__",
        // Indexing
        "__getitem__", "__setitem__",
        // Iteration
        "__seq__", "__next__",
        // Context management
        "__enter__", "__exit__"
    ];

    /// <summary>Returns true if the given method name is an operator dunder (e.g., <c>__add__</c>, <c>__eq__</c>).</summary>
    private static bool IsOperatorDunder(string name)
    {
        return OperatorDunders.Contains(value: name);
    }

    /// <summary>
    /// Validates comparison operands for type compatibility and operator support.
    /// Called from both AnalyzeBinaryExpression (for non-desugared operators like ===, is, obeys)
    /// and AnalyzeChainedComparisonExpression (for chained comparisons like a &lt; b &lt; c).
    /// </summary>
    private void ValidateComparisonOperands(TypeSymbol left, TypeSymbol right, BinaryOperator op, SourceLocation location)
    {
        // Identity operators (===, !==) only work on entity/resident types
        if (op is BinaryOperator.Identical or BinaryOperator.NotIdentical)
        {
            if (left.Category is not (TypeCategory.Entity or TypeCategory.Resident) ||
                right.Category is not (TypeCategory.Entity or TypeCategory.Resident))
            {
                ReportError(
                    SemanticDiagnosticCode.IdentityOperatorRequiresReference,
                    $"Identity operator '{op.ToStringRepresentation()}' can only be used with entity or resident types, not '{left.Name}' and '{right.Name}'.",
                    location);
            }

            return;
        }

        // Variants cannot use equality or ordering operators (only 'is' and 'isnot')
        if (left.Category == TypeCategory.Variant || right.Category == TypeCategory.Variant)
        {
            if (op is not (BinaryOperator.Is or BinaryOperator.IsNot))
            {
                ReportError(
                    SemanticDiagnosticCode.ComparisonOnVariantType,
                    $"Comparison operator '{op.ToStringRepresentation()}' cannot be used with variant types. Use 'is' or 'isnot' for pattern matching.",
                    location);
            }

            return;
        }

        // Check that types are compatible (same type or error type)
        if (!IsAssignableTo(source: left, target: right) && !IsAssignableTo(source: right, target: left))
        {
            ReportError(
                SemanticDiagnosticCode.IncompatibleComparisonTypes,
                $"Cannot compare values of incompatible types '{left.Name}' and '{right.Name}'.",
                location);
        }

        // For ordering/equality operators in chained comparisons, verify the type supports them
        // Note: For single comparisons, these are desugared to method calls in the parser.
        // This validation only runs for chained comparisons (a < b < c) where operators are NOT desugared.
        if (op is not (BinaryOperator.Less or BinaryOperator.LessEqual or BinaryOperator.Greater
            or BinaryOperator.GreaterEqual or BinaryOperator.Equal))
        {
            return;
        }

        if (!SupportsOperator(type: left, op: op))
        {
            ReportError(
                SemanticDiagnosticCode.OrderingNotSupported,
                $"Type '{left.Name}' does not support comparison operator '{op.ToStringRepresentation()}'.",
                location);
        }
    }

    /// <summary>
    /// Validates that a chained comparison expression uses operators in a consistent direction.
    /// Valid patterns:
    /// - All ascending: a &lt; b &lt; c, a &lt;= b &lt; c, a == b &lt; c
    /// - All descending: a &gt; b &gt; c, a &gt;= b &gt; c, a == b &gt; c
    /// - Equality only: a == b == c
    /// Invalid: mixing ascending and descending (a &lt; b &gt; c)
    /// </summary>
    private void ValidateComparisonChain(ChainedComparisonExpression chain, SourceLocation location)
    {
        if (chain.Operators.Count < 2)
        {
            return; // No chain to validate
        }

        bool? isAscending = null;

        foreach (BinaryOperator op in chain.Operators)
        {
            // Equality operators are direction-neutral
            if (op == BinaryOperator.Equal)
            {
                continue;
            }

            // NotEqual cannot be used in chains
            if (op == BinaryOperator.NotEqual)
            {
                ReportError(
                    SemanticDiagnosticCode.NotEqualInComparisonChain,
                    "The '!=' operator cannot be used in comparison chains.",
                    location);
                return;
            }

            bool opIsAscending = op is BinaryOperator.Less or BinaryOperator.LessEqual;
            bool opIsDescending = op is BinaryOperator.Greater or BinaryOperator.GreaterEqual;

            if (opIsAscending)
            {
                if (isAscending == false)
                {
                    ReportError(
                        SemanticDiagnosticCode.MixedComparisonChainDirection,
                        "Cannot mix ascending (<, <=) and descending (>, >=) operators in a comparison chain.",
                        location);
                    return;
                }

                isAscending = true;
            }
            else if (opIsDescending)
            {
                if (isAscending == true)
                {
                    ReportError(
                        SemanticDiagnosticCode.MixedComparisonChainDirection,
                        "Cannot mix ascending (<, <=) and descending (>, >=) operators in a comparison chain.",
                        location);
                    return;
                }

                isAscending = false;
            }
        }
    }

    /// <summary>
    /// Returns the wider of two numeric types for binary expression type inference.
    /// Because the language has no implicit numeric conversions, both operands must already
    /// be the same type; this method simply returns the left operand's type.
    /// </summary>
    private TypeSymbol GetWiderNumericType(TypeSymbol left, TypeSymbol right)
    {
        // With no implicit conversions, types must match exactly
        // This method is now only called as a fallback when both types are numeric
        // Return left type since they should be the same
        return left;
    }

    /// <summary>
    /// Resolves the element type produced by iterating over <paramref name="iterableType"/>.
    /// The type must implement the <c>Sequenceable</c> protocol, whose <c>__seq__</c> returns a <c>SequenceEmitter[T]</c>.
    /// The element type is taken from the return type of the <c>__seq__</c> method or the type's first generic argument.
    /// Reports an error and returns <see cref="ErrorTypeInfo"/> if the type is not iterable or the element type cannot be determined.
    /// </summary>
    private TypeSymbol GetSequenceableElementType(TypeSymbol iterableType, SourceLocation location)
    {
        // Type must follow the Sequenceable protocol
        bool obeysSequenceable = ImplementsProtocol(type: iterableType, protocolName: "Sequenceable");

        if (!obeysSequenceable)
        {
            ReportError(
                SemanticDiagnosticCode.TypeNotIterable,
                $"Type '{iterableType.Name}' is not sequenceable. Types must follow the " +
                $"'Sequenceable' protocol to be used in for-in loops.",
                location);
            return ErrorTypeInfo.Instance;
        }

        // Look for __seq__ method to get element type from SequenceEmitter[T] return type
        RoutineInfo? seqMethod = _registry.LookupRoutine(fullName: $"{iterableType.Name}.__seq__");
        if (seqMethod?.ReturnType?.TypeArguments is { Count: > 0 })
        {
            return seqMethod.ReturnType.TypeArguments[0];
        }

        // Fallback to type arguments if __seq__ method not found but protocol is implemented
        if (iterableType.TypeArguments is { Count: > 0 })
        {
            return iterableType.TypeArguments[0];
        }

        ReportError(
            SemanticDiagnosticCode.TypeNotIterable,
            $"Cannot determine element type for '{iterableType.Name}'. The __seq__ method must return SequenceEmitter[T].",
            location);
        return ErrorTypeInfo.Instance;
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> implements the named protocol.
    /// Checks explicit protocol declarations, parent protocol chains, and structural conformance
    /// (i.e., whether the type has all required methods of the protocol).
    /// </summary>
    private bool ImplementsProtocol(TypeSymbol type, string protocolName)
    {
        // Get the protocol type
        TypeSymbol? protocol = LookupTypeWithImports(name: protocolName);
        if (protocol is not { Category: TypeCategory.Protocol })
        {
            return false;
        }

        // Get the list of implemented protocols for this type
        IReadOnlyList<TypeSymbol>? implementedProtocols = type switch
        {
            RecordTypeInfo record => record.ImplementedProtocols,
            EntityTypeInfo entity => entity.ImplementedProtocols,
            ResidentTypeInfo resident => resident.ImplementedProtocols,
            _ => null
        };

        if (implementedProtocols == null)
        {
            return false;
        }

        // Check if the protocol is directly declared
        foreach (TypeSymbol implemented in implementedProtocols)
        {
            if (implemented.Name == protocolName || GetBaseTypeName(typeName: implemented.Name) == protocolName)
            {
                return true;
            }

            // Check parent protocols recursively
            if (implemented is ProtocolTypeInfo proto && CheckParentProtocols(proto: proto, targetName: protocolName))
            {
                return true;
            }
        }

        // Check if the type has all required methods of the protocol (structural conformance)
        if (protocol is ProtocolTypeInfo protoType)
        {
            return CheckStructuralConformance(type: type, protocol: protoType);
        }

        return false;
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> explicitly declares conformance to the named protocol
    /// via <c>obeys</c>. Unlike <see cref="ImplementsProtocol"/>, this does NOT fall back to
    /// structural conformance, making it suitable for marker protocols like ConstCompatible.
    /// </summary>
    private bool ExplicitlyImplementsProtocol(TypeSymbol type, string protocolName)
    {
        IReadOnlyList<TypeSymbol>? implementedProtocols = type switch
        {
            RecordTypeInfo record => record.ImplementedProtocols,
            EntityTypeInfo entity => entity.ImplementedProtocols,
            ResidentTypeInfo resident => resident.ImplementedProtocols,
            _ => null
        };

        if (implementedProtocols == null)
        {
            return false;
        }

        foreach (TypeSymbol implemented in implementedProtocols)
        {
            if (implemented.Name == protocolName || GetBaseTypeName(typeName: implemented.Name) == protocolName)
            {
                return true;
            }

            if (implemented is ProtocolTypeInfo proto && CheckParentProtocols(proto: proto, targetName: protocolName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if any parent protocol matches the target.
    /// </summary>
    private bool CheckParentProtocols(ProtocolTypeInfo proto, string targetName)
    {
        foreach (ProtocolTypeInfo parent in proto.ParentProtocols)
        {
            if (parent.Name == targetName || GetBaseTypeName(typeName: parent.Name) == targetName)
            {
                return true;
            }

            // Re-lookup parent from registry to get the latest version with populated ParentProtocols,
            // since immutable type updates may leave stale references in the hierarchy.
            ProtocolTypeInfo latestParent = parent;
            if (parent.ParentProtocols.Count == 0)
            {
                TypeSymbol? looked = _registry.LookupType(name: parent.Name);
                if (looked is ProtocolTypeInfo latest)
                {
                    latestParent = latest;
                }
            }

            if (CheckParentProtocols(proto: latestParent, targetName: targetName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a type structurally conforms to a protocol by having all required methods.
    /// </summary>
    private bool CheckStructuralConformance(TypeSymbol type, ProtocolTypeInfo protocol)
    {
        foreach (ProtocolMethodInfo requiredMethod in protocol.Methods)
        {
            // Skip methods with default implementations
            if (requiredMethod.HasDefaultImplementation)
            {
                continue;
            }

            // Look for the method on the type
            RoutineInfo? typeMethod = _registry.LookupMethod(type: type, methodName: requiredMethod.Name);
            if (typeMethod == null)
            {
                // Also check with failable suffix
                if (requiredMethod.IsFailable)
                {
                    typeMethod = _registry.LookupMethod(type: type, methodName: requiredMethod.Name + "!");
                }

                if (typeMethod == null)
                {
                    return false;
                }
            }

            // Verify method signature matches (basic check)
            if (!MethodSignatureMatches(typeMethod: typeMethod, protoMethod: requiredMethod))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks if a type's method signature matches a protocol method signature.
    /// </summary>
    private bool MethodSignatureMatches(RoutineInfo typeMethod, ProtocolMethodInfo protoMethod)
    {
        // Check failable matches
        if (typeMethod.IsFailable != protoMethod.IsFailable)
        {
            return false;
        }

        // Check parameter count (excluding 'me' parameter if present)
        // In-body methods have explicit 'me' as first parameter
        // Extension methods don't include 'me' in the parameter list
        int expectedParamCount = protoMethod.ParameterTypes.Count;
        bool hasMeParam = typeMethod.Parameters.Count > 0 && typeMethod.Parameters[0].Name == "me";
        int actualParamCount = typeMethod.Parameters.Count - (hasMeParam ? 1 : 0);

        if (actualParamCount != expectedParamCount)
        {
            return false;
        }

        // Check parameter types - skip 'me' if present
        int startIndex = hasMeParam ? 1 : 0;
        for (int i = 0; i < expectedParamCount; i++)
        {
            TypeSymbol expectedType = protoMethod.ParameterTypes[i];
            TypeSymbol actualType = typeMethod.Parameters[startIndex + i].Type;

            // Handle protocol self type (Me) - should match the implementing type
            if (expectedType is ProtocolSelfTypeInfo)
            {
                // 'Me' in protocol should match the owner type of the method
                if (typeMethod.OwnerType != null && !TypesMatch(actualType, typeMethod.OwnerType))
                {
                    return false;
                }
            }
            else if (!TypesMatch(actualType, expectedType))
            {
                return false;
            }
        }

        // Check return type (if specified)
        if (protoMethod.ReturnType != null && typeMethod.ReturnType != null)
        {
            if (!IsAssignableTo(source: typeMethod.ReturnType, target: protoMethod.ReturnType))
            {
                return false;
            }
        }
        else if ((protoMethod.ReturnType == null) != (typeMethod.ReturnType == null))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if two types match for protocol signature comparison.
    /// </summary>
    private bool TypesMatch(TypeSymbol actual, TypeSymbol expected)
    {
        // Exact name match
        if (actual.Name == expected.Name)
        {
            return true;
        }

        // Handle ProtocolSelfTypeInfo in expected position
        if (expected is ProtocolSelfTypeInfo)
        {
            // 'Me' matches the owner type - handled by caller
            return true;
        }

        // Handle generic resolutions
        if (expected.IsGenericDefinition && actual.IsGenericResolution)
        {
            string baseName = GetBaseTypeName(typeName: actual.Name);
            if (baseName == expected.Name)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Creates or retrieves a <c>Viewed&lt;T&gt;</c> wrapper type for the given inner type.
    /// <c>Viewed</c> is a read-only, single-threaded access token.
    /// </summary>
    private TypeSymbol CreateViewedType(TypeSymbol innerType)
    {
        TypeSymbol? viewedDef = _registry.LookupType(name: "Viewed");
        if (viewedDef != null)
        {
            return _registry.GetOrCreateResolution(genericDef: viewedDef, typeArguments: [innerType]);
        }

        // Synthesize wrapper type if not defined in the program
        return _registry.GetOrCreateWrapperType(
            wrapperName: "Viewed",
            innerType: innerType,
            isReadOnly: true);
    }

    /// <summary>
    /// Creates or retrieves a <c>Hijacked&lt;T&gt;</c> wrapper type for the given inner type.
    /// <c>Hijacked</c> is an exclusive write, single-threaded access token.
    /// </summary>
    private TypeSymbol CreateHijackedType(TypeSymbol innerType)
    {
        TypeSymbol? hijackedDef = _registry.LookupType(name: "Hijacked");
        if (hijackedDef != null)
        {
            return _registry.GetOrCreateResolution(genericDef: hijackedDef, typeArguments: [innerType]);
        }

        // Synthesize wrapper type if not defined in the program
        return _registry.GetOrCreateWrapperType(
            wrapperName: "Hijacked",
            innerType: innerType,
            isReadOnly: false);
    }

    /// <summary>
    /// Creates or retrieves an <c>Inspected&lt;T&gt;</c> wrapper type for the given inner type.
    /// <c>Inspected</c> is a read-only, multi-threaded (shared) access token.
    /// </summary>
    private TypeSymbol CreateInspectedType(TypeSymbol innerType)
    {
        TypeSymbol? inspectedDef = _registry.LookupType(name: "Inspected");
        if (inspectedDef != null)
        {
            return _registry.GetOrCreateResolution(genericDef: inspectedDef, typeArguments: [innerType]);
        }

        // Synthesize wrapper type if not defined in the program
        return _registry.GetOrCreateWrapperType(
            wrapperName: "Inspected",
            innerType: innerType,
            isReadOnly: true);
    }

    /// <summary>
    /// Creates or retrieves a <c>Seized&lt;T&gt;</c> wrapper type for the given inner type.
    /// <c>Seized</c> is an exclusive write, multi-threaded access token.
    /// </summary>
    private TypeSymbol CreateSeizedType(TypeSymbol innerType)
    {
        TypeSymbol? seizedDef = _registry.LookupType(name: "Seized");
        if (seizedDef != null)
        {
            return _registry.GetOrCreateResolution(genericDef: seizedDef, typeArguments: [innerType]);
        }

        // Synthesize wrapper type if not defined in the program
        return _registry.GetOrCreateWrapperType(
            wrapperName: "Seized",
            innerType: innerType,
            isReadOnly: false);
    }

    /// <summary>
    /// Checks if a hijacking source expression would result in nested hijacking.
    /// Nested hijacking occurs when trying to hijack a member of an already-hijacked object.
    /// </summary>
    /// <param name="source">The source expression for the hijacking statement.</param>
    /// <returns>True if this would be a nested hijacking, false otherwise.</returns>
    private bool IsNestedHijacking(Expression source)
    {
        // Check if source is a member access expression (e.g., p.child)
        if (source is not MemberExpression member)
        {
            return false;
        }

        // Check if the object being accessed is an identifier
        if (member.Object is not IdentifierExpression id)
        {
            // Could be a chained member access, check recursively
            return IsNestedHijacking(source: member.Object);
        }

        // Look up the variable and check if its type is Hijacked<T>
        VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
        if (varInfo == null)
        {
            return false;
        }

        // Check if the variable's type is Hijacked<T>
        return IsHijackedType(type: varInfo.Type);
    }

    /// <summary>
    /// Checks if a type is a Hijacked&lt;T&gt; token type.
    /// </summary>
    private static bool IsHijackedType(TypeSymbol type)
    {
        // Check if the type name is "Hijacked" (for resolved generic types)
        return type.Name == "Hijacked";
    }

    /// <summary>
    /// Checks if a type is a Seized&lt;T&gt; token type.
    /// </summary>
    private static bool IsSeizedType(TypeSymbol type)
    {
        return type.Name == "Seized";
    }

    /// <summary>
    /// Checks if a type is a Shared&lt;T&gt; handle type.
    /// </summary>
    private static bool IsSharedType(TypeSymbol type)
    {
        return type.Name == "Shared";
    }

    /// <summary>
    /// Checks if a type is a Tracked&lt;T&gt; handle type.
    /// </summary>
    private static bool IsTrackedType(TypeSymbol type)
    {
        return type.Name == "Tracked";
    }

    /// <summary>
    /// Checks if a type is a resident type.
    /// </summary>
    private static bool IsResidentType(TypeSymbol type)
    {
        return type is ResidentTypeInfo;
    }

    #endregion

    #region Wrapper Type Forwarding

    /// <summary>
    /// All wrapper types that transparently forward to their inner type.
    /// </summary>
    private static readonly HashSet<string> WrapperTypes =
    [
        "Viewed",     // Read-only single-threaded token
        "Hijacked",   // Exclusive write single-threaded token
        "Inspected",  // Read-only multi-threaded token
        "Seized",     // Exclusive write multi-threaded token
        "Shared",     // Reference-counted handle
        "Tracked",    // Weak reference handle
        "Snatched"    // Unsafe raw pointer handle
    ];

    /// <summary>
    /// Read-only wrapper types that can only access @readonly methods.
    /// </summary>
    private static readonly HashSet<string> ReadOnlyWrapperTypes =
    [
        "Viewed",     // Read-only single-threaded token
        "Inspected"   // Read-only multi-threaded token
    ];

    /// <summary>
    /// Checks if a type is a wrapper type (Viewed, Hijacked, Shared, etc.).
    /// </summary>
    /// <param name="type">The type to check.</param>
    /// <returns>True if the type is a wrapper type.</returns>
    private bool IsWrapperType(TypeSymbol type)
    {
        string baseName = GetBaseTypeName(typeName: type.Name);
        return WrapperTypes.Contains(value: baseName);
    }

    /// <summary>
    /// Checks if a wrapper type is read-only (Viewed, Inspected).
    /// </summary>
    /// <param name="type">The wrapper type to check.</param>
    /// <returns>True if the wrapper is read-only.</returns>
    private bool IsReadOnlyWrapper(TypeSymbol type)
    {
        string baseName = GetBaseTypeName(typeName: type.Name);
        return ReadOnlyWrapperTypes.Contains(value: baseName);
    }

    /// <summary>
    /// Gets the inner type from a wrapper type (e.g., T from Viewed&lt;T&gt;).
    /// </summary>
    /// <param name="wrapperType">The wrapper type.</param>
    /// <returns>The inner type, or null if not a wrapper or no type arguments.</returns>
    private TypeSymbol? GetWrapperInnerType(TypeSymbol wrapperType)
    {
        if (!IsWrapperType(type: wrapperType))
        {
            return null;
        }

        // Wrapper types have their inner type as the first type argument
        if (wrapperType.TypeArguments is { Count: > 0 })
        {
            return wrapperType.TypeArguments[0];
        }

        return null;
    }

    /// <summary>
    /// Tries to look up a member variable on the inner type of a wrapper type.
    /// </summary>
    /// <param name="wrapperType">The wrapper type.</param>
    /// <param name="memberVariableName">The name of the member variable to look up.</param>
    /// <returns>The member variable info if found, null otherwise.</returns>
    private MemberVariableInfo? LookupMemberVariableOnWrapperInnerType(TypeSymbol wrapperType, string memberVariableName)
    {
        TypeSymbol? innerType = GetWrapperInnerType(wrapperType: wrapperType);
        if (innerType == null)
        {
            return null;
        }

        return innerType switch
        {
            RecordTypeInfo record => record.LookupMemberVariable(memberVariableName: memberVariableName),
            EntityTypeInfo entity => entity.LookupMemberVariable(memberVariableName: memberVariableName),
            ResidentTypeInfo resident => resident.LookupMemberVariable(memberVariableName: memberVariableName),
            _ => null
        };
    }

    /// <summary>
    /// Tries to look up a method on the inner type of a wrapper type.
    /// </summary>
    /// <param name="wrapperType">The wrapper type.</param>
    /// <param name="methodName">The name of the method to look up.</param>
    /// <returns>The routine info if found, null otherwise.</returns>
    private RoutineInfo? LookupMethodOnWrapperInnerType(TypeSymbol wrapperType, string methodName)
    {
        TypeSymbol? innerType = GetWrapperInnerType(wrapperType: wrapperType);
        if (innerType == null)
        {
            return null;
        }

        // Look up the method on the inner type
        return _registry.LookupRoutine(fullName: $"{innerType.Name}.{methodName}");
    }

    /// <summary>
    /// Validates that a method can be called through a read-only wrapper.
    /// Read-only wrappers (Viewed, Inspected) can only call @readonly methods.
    /// </summary>
    /// <param name="wrapperType">The wrapper type being used.</param>
    /// <param name="method">The method being called.</param>
    /// <param name="location">Source location for error reporting.</param>
    private void ValidateReadOnlyWrapperMethodAccess(
        TypeSymbol wrapperType,
        RoutineInfo method,
        SourceLocation location)
    {
        if (!IsReadOnlyWrapper(type: wrapperType))
        {
            return; // Modifiable wrappers can access all methods
        }

        // Read-only wrappers can only access @readonly methods
        if (!method.IsReadOnly)
        {
            string wrapperName = GetBaseTypeName(typeName: wrapperType.Name);
            ReportError(
                SemanticDiagnosticCode.WritableMethodThroughReadOnlyWrapper,
                $"Cannot call writable method '{method.Name}' through read-only wrapper '{wrapperName}[T]'. " +
                $"Only @readonly methods are accessible.",
                location);
        }
    }

    #endregion

    #region Memory Token Validation

    /// <summary>
    /// Token types that cannot be returned from routines or stored in member variables.
    /// These are inline-only access tokens that must stay within their scope.
    /// </summary>
    private static readonly HashSet<string> InlineOnlyTokenTypes =
    [
        "Viewed",    // Read-only single-threaded token
        "Hijacked",  // Exclusive write single-threaded token
        "Inspected", // Read-only multi-threaded token
        "Seized"     // Exclusive write multi-threaded token
    ];

    /// <summary>
    /// Token types that require uniqueness validation (cannot be passed twice in same call).
    /// </summary>
    private static readonly HashSet<string> ExclusiveTokenTypes =
    [
        "Hijacked", // Cannot pass same Hijacked token twice
        "Seized"    // Cannot pass same Seized token twice
    ];

    /// <summary>
    /// Checks if a type is an inline-only token type (Viewed, Hijacked, Inspected, Seized).
    /// These tokens cannot be returned from routines or stored in member variables.
    /// </summary>
    private bool IsInlineOnlyTokenType(TypeSymbol type)
    {
        string baseName = GetBaseTypeName(typeName: type.Name);
        return InlineOnlyTokenTypes.Contains(value: baseName);
    }

    /// <summary>
    /// Checks if a type is an exclusive token type (Hijacked, Seized).
    /// These tokens cannot be passed multiple times in the same call.
    /// </summary>
    private bool IsExclusiveTokenType(TypeSymbol type)
    {
        string baseName = GetBaseTypeName(typeName: type.Name);
        return ExclusiveTokenTypes.Contains(value: baseName);
    }

    /// <summary>
    /// Gets the token kind for display in error messages.
    /// </summary>
    private static string GetTokenKindDescription(TypeSymbol type)
    {
        string baseName = GetBaseTypeName(typeName: type.Name);
        return baseName switch
        {
            "Viewed" => "read-only token (Viewed)",
            "Hijacked" => "exclusive write token (Hijacked)",
            "Inspected" => "shared read token (Inspected)",
            "Seized" => "exclusive shared write token (Seized)",
            _ => "token"
        };
    }

    /// <summary>
    /// Validates that a type is not an inline-only token when used as a return type.
    /// </summary>
    private void ValidateNotTokenReturnType(TypeSymbol type, SourceLocation location)
    {
        if (_registry.Language != Language.RazorForge)
        {
            return; // Token validation only applies to RazorForge
        }

        if (IsInlineOnlyTokenType(type: type))
        {
            ReportError(
                SemanticDiagnosticCode.TokenReturnNotAllowed,
                $"Cannot return {GetTokenKindDescription(type: type)} from a routine. Tokens are inline-only and cannot escape their scope.",
                location);
        }
    }

    /// <summary>
    /// Validates that a type is not an inline-only token when used as a member variable type.
    /// </summary>
    private void ValidateNotTokenMemberVariableType(TypeSymbol type, string memberVariableName, SourceLocation location)
    {
        if (_registry.Language != Language.RazorForge)
        {
            return; // Token validation only applies to RazorForge
        }

        if (IsInlineOnlyTokenType(type: type))
        {
            ReportError(
                SemanticDiagnosticCode.TokenMemberVariableNotAllowed,
                $"Cannot store {GetTokenKindDescription(type: type)} in member variable '{memberVariableName}'. Tokens are inline-only and cannot be stored.",
                location);
        }
    }

    /// <summary>
    /// Validates that exclusive tokens (Hijacked, Seized) are not passed multiple times in a single call.
    /// </summary>
    private void ValidateExclusiveTokenUniqueness(List<Expression> arguments, SourceLocation location)
    {
        if (_registry.Language != Language.RazorForge)
        {
            return; // Token validation only applies to RazorForge
        }

        // Track which exclusive token expressions we've seen
        var seenExclusiveTokens = new HashSet<string>();

        foreach (Expression arg in arguments)
        {
            // Get the expression's type
            if (arg.ResolvedType == null)
            {
                continue;
            }

            // Convert AST TypeInfo back to get the type name
            string typeName = arg.ResolvedType.Name;
            string baseName = GetBaseTypeName(typeName: typeName);

            if (!ExclusiveTokenTypes.Contains(value: baseName))
            {
                continue;
            }

            // Get a string representation of the expression for uniqueness checking
            string exprKey = GetExpressionKey(expression: arg);
            if (string.IsNullOrEmpty(value: exprKey))
            {
                continue;
            }

            if (seenExclusiveTokens.Contains(value: exprKey))
            {
                ReportError(
                    SemanticDiagnosticCode.ExclusiveTokenDuplicate,
                    $"Cannot pass the same {baseName} token '{exprKey}' multiple times in a single call. Exclusive tokens require unique access.",
                    location);
            }
            else
            {
                seenExclusiveTokens.Add(item: exprKey);
            }
        }
    }

    /// <summary>
    /// Gets a string key representing an expression for uniqueness checking.
    /// Returns null for complex expressions that can't be easily tracked.
    /// </summary>
    private static string? GetExpressionKey(Expression expression)
    {
        return expression switch
        {
            IdentifierExpression id => id.Name,
            MemberExpression member => $"{GetExpressionKey(expression: member.Object)}.{member.PropertyName}",
            _ => null
        };
    }

    /// <summary>
    /// Validates that a type is not an inline-only token when used as a variant case payload.
    /// </summary>
    private void ValidateNotTokenVariantPayload(TypeSymbol type, string caseName, SourceLocation location)
    {
        if (_registry.Language != Language.RazorForge)
        {
            return; // Token validation only applies to RazorForge
        }

        if (IsInlineOnlyTokenType(type: type))
        {
            ReportError(
                SemanticDiagnosticCode.TokenVariantPayloadNotAllowed,
                $"Cannot use {GetTokenKindDescription(type: type)} as payload for variant case '{caseName}'. Tokens are inline-only and cannot be stored in variants.",
                location);
        }
    }

    #endregion

    #region Getter/Setter Visibility Validation

    /// <summary>
    /// Gets the numeric access level for visibility comparison.
    /// Lower values are more restrictive.
    /// </summary>
    private static int GetVisibilityLevel(VisibilityModifier visibility)
    {
        return visibility switch
        {
            VisibilityModifier.Secret => 0,
            VisibilityModifier.Posted => 1, // Posted has open read access
            VisibilityModifier.Open => 1,
            VisibilityModifier.External => 1,
            _ => 1
        };
    }

    /// <summary>
    /// Validates that a member variable's setter visibility is equal to or more restrictive than its getter visibility.
    /// The 3 valid combinations are:
    /// 1. open (getter: open, setter: open)
    /// 2. posted (getter: open, setter: secret)
    /// 3. secret (read/write secret)
    /// </summary>
    /// <remarks>
    /// With the simplified three-level visibility system, no validation is needed.
    /// The visibility level directly determines both read and write access:
    /// - open: read/write from anywhere
    /// - posted: open read, secret write
    /// - secret: read/write within module
    /// </remarks>

    #endregion

    #region Access Modifier Enforcement

    /// <summary>
    /// Gets the effective visibility for member variable write access.
    /// For posted member variables, write access is secret (only owner can write).
    /// </summary>
    /// <param name="memberVariable">The member variable to check.</param>
    /// <returns>The effective visibility for write access.</returns>
    private static VisibilityModifier GetEffectiveWriteVisibility(MemberVariableInfo memberVariable)
    {
        // Posted member variables have open read but secret write
        return memberVariable.Visibility == VisibilityModifier.Posted
            ? VisibilityModifier.Secret
            : memberVariable.Visibility;
    }

    /// <summary>
    /// Checks if access to a member variable is allowed from the current context.
    /// </summary>
    /// <param name="memberVariable">The member variable being accessed.</param>
    /// <param name="isWrite">Whether this is a write access (assignment).</param>
    /// <param name="accessLocation">Source location of the access site.</param>
    private void ValidateMemberVariableAccess(MemberVariableInfo memberVariable, bool isWrite, SourceLocation accessLocation)
    {
        // Posted member variables: open read, module-only write
        if (isWrite && memberVariable.Visibility == VisibilityModifier.Posted
                    && !IsAccessingFromSameModule(memberModule: memberVariable.Owner?.Module))
        {
            string typeName = memberVariable.Owner?.Name ?? "type";
            ReportError(
                SemanticDiagnosticCode.PostedMemberAccess,
                $"Cannot write to posted member variable '{memberVariable.Name}' of '{typeName}' from outside its module.",
                accessLocation);
            return;
        }

        // For posted member variables, write access is restricted to secret (module only)
        VisibilityModifier visibility = isWrite
            ? GetEffectiveWriteVisibility(memberVariable: memberVariable)
            : memberVariable.Visibility;

        ValidateMemberAccess(
            visibility: visibility,
            memberKind: "member variable",
            memberName: memberVariable.Name,
            ownerType: memberVariable.Owner,
            accessLocation: accessLocation);
    }

    /// <summary>
    /// Checks if access to a routine is allowed from the current context.
    /// </summary>
    /// <param name="routine">The routine being accessed.</param>
    /// <param name="accessLocation">Source location of the access site.</param>
    private void ValidateRoutineAccess(RoutineInfo routine, SourceLocation accessLocation)
    {
        ValidateMemberAccess(
            visibility: routine.Visibility,
            memberKind: routine.Kind switch
            {
                RoutineKind.Creator => "creator",
                RoutineKind.MemberRoutine => "member routine",
                _ => "routine"
            },
            memberName: routine.Name,
            ownerType: routine.OwnerType,
            accessLocation: accessLocation);

        // Dangerous routines can only be called inside danger! blocks
        if (routine.IsDangerous && !InDangerBlock)
        {
            ReportError(
                SemanticDiagnosticCode.DangerousCallOutsideDangerBlock,
                $"Dangerous routine '{routine.Name}' can only be called inside a 'danger!' block.",
                accessLocation);
        }
    }

    /// <summary>
    /// Validates access to a member based on visibility rules.
    /// </summary>
    /// <param name="visibility">The visibility modifier of the member.</param>
    /// <param name="memberKind">The kind of member (member variable, method, etc.) for error messages.</param>
    /// <param name="memberName">The name of the member.</param>
    /// <param name="ownerType">The type that owns this member, if any.</param>
    /// <param name="accessLocation">Source location of the access site.</param>
    private void ValidateMemberAccess(
        VisibilityModifier visibility,
        string memberKind,
        string memberName,
        TypeSymbol? ownerType,
        SourceLocation accessLocation)
    {
        switch (visibility)
        {
            case VisibilityModifier.Secret:
                // Secret members are accessible within the same module
                if (!IsAccessingFromSameModule(memberModule: ownerType?.Module))
                {
                    string typeName = ownerType?.Name ?? "type";
                    ReportError(
                        SemanticDiagnosticCode.SecretMemberAccess,
                        $"Cannot access secret {memberKind} '{memberName}' of '{typeName}' from outside its module.",
                        accessLocation);
                }
                break;

            case VisibilityModifier.Posted:
            case VisibilityModifier.Open:
            case VisibilityModifier.External:
                // Open/Posted/External members are accessible from anywhere for reading
                break;
        }
    }

    /// <summary>
    /// Checks if the current access context is within the same module as the member.
    /// Module comparison is exact (sub-modules are different modules).
    /// </summary>
    private bool IsAccessingFromSameModule(string? memberModule)
    {
        string? currentModuleName = GetCurrentModuleName();

        // If both are in no module, they're in the same module
        if (string.IsNullOrEmpty(value: memberModule) && string.IsNullOrEmpty(value: currentModuleName))
        {
            return true;
        }

        // If either is null/empty but not both, they're not in the same module
        if (string.IsNullOrEmpty(value: memberModule) || string.IsNullOrEmpty(value: currentModuleName))
        {
            return false;
        }

        // Module comparison is exact - sub-modules are different modules
        return currentModuleName == memberModule;
    }

    /// <summary>
    /// Validates write access to a member variable, checking setter visibility.
    /// </summary>
    /// <param name="objectType">The type of the object being accessed.</param>
    /// <param name="memberVariableName">The name of the member variable being written.</param>
    /// <param name="location">The source location of the write.</param>
    private void ValidateMemberVariableWriteAccess(TypeSymbol objectType, string memberVariableName, SourceLocation location)
    {
        MemberVariableInfo? memberVariable = objectType switch
        {
            RecordTypeInfo record => record.LookupMemberVariable(memberVariableName: memberVariableName),
            EntityTypeInfo entity => entity.LookupMemberVariable(memberVariableName: memberVariableName),
            ResidentTypeInfo resident => resident.LookupMemberVariable(memberVariableName: memberVariableName),
            _ => null
        };

        if (memberVariable != null)
        {
            ValidateMemberVariableAccess(memberVariable: memberVariable, isWrite: true, accessLocation: location);
        }
    }

    #endregion

    #region Stdlib File Detection

    /// <summary>
    /// Checks whether a file path is inside the stdlib directory.
    /// Used to allow stdlib files to use reserved features (e.g., module Core).
    /// </summary>
    private bool IsStdlibFile(string filePath)
    {
        string? stdlibPath = _registry.StdlibPath;
        if (string.IsNullOrEmpty(stdlibPath) || string.IsNullOrEmpty(filePath))
            return false;

        string normalizedFile = Path.GetFullPath(filePath);
        string normalizedStdlib = Path.GetFullPath(stdlibPath);
        return normalizedFile.StartsWith(normalizedStdlib, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Type Narrowing

    /// <summary>
    /// Information about type narrowing extracted from a condition expression.
    /// </summary>
    private record NarrowingInfo(string VariableName, TypeSymbol? ThenBranchType, TypeSymbol? ElseBranchType);

    /// <summary>
    /// Attempts to extract type narrowing information from a condition expression.
    /// Handles patterns like "x is None", "x isnot None", "Not(x is None)".
    /// </summary>
    private NarrowingInfo? TryExtractNarrowingFromCondition(Expression condition)
    {
        // Handle: x is None / x is Crashable / x isnot None / x isnot Crashable
        if (condition is IsPatternExpression isPat)
        {
            return ExtractFromIsPattern(isPat: isPat);
        }

        // Handle desugared unless: Not(x is None) → if Not(condition) { ... }
        if (condition is UnaryExpression { Operator: UnaryOperator.Not, Operand: IsPatternExpression innerIsPat })
        {
            // Negating the condition swaps then/else narrowing
            NarrowingInfo? inner = ExtractFromIsPattern(isPat: innerIsPat);
            if (inner == null) return null;
            return new NarrowingInfo(inner.VariableName, inner.ElseBranchType, inner.ThenBranchType);
        }

        return null;
    }

    /// <summary>
    /// Extracts narrowing info from an IsPatternExpression.
    /// </summary>
    private NarrowingInfo? ExtractFromIsPattern(IsPatternExpression isPat)
    {
        // The expression must be a simple identifier
        if (isPat.Expression is not IdentifierExpression id) return null;

        // Look up the variable to get its current type
        VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
        if (varInfo == null) return null;

        // Check for existing narrowing
        TypeSymbol varType = _registry.GetNarrowedType(name: id.Name) ?? varInfo.Type;

        bool eliminateNone = IsNonePattern(pattern: isPat.Pattern);
        bool eliminateCrashable = IsCrashablePattern(pattern: isPat.Pattern);

        if (!eliminateNone && !eliminateCrashable) return null;

        TypeSymbol? narrowedType = ComputeNarrowedType(
            type: varType,
            eliminateNone: eliminateNone,
            eliminateCrashable: eliminateCrashable);

        if (narrowedType == null) return null;

        if (isPat.IsNegated)
        {
            // "x isnot None" → then branch gets the narrowed type
            return new NarrowingInfo(id.Name, ThenBranchType: narrowedType, ElseBranchType: null);
        }

        // "x is None" → else branch gets the narrowed type
        return new NarrowingInfo(id.Name, ThenBranchType: null, ElseBranchType: narrowedType);
    }

    /// <summary>
    /// Checks if a pattern represents a None check.
    /// The parser creates TypePattern(type: "None") rather than NonePattern.
    /// </summary>
    private static bool IsNonePattern(Pattern pattern)
    {
        return pattern is NonePattern
            or TypePattern { Type.Name: "None" };
    }

    /// <summary>
    /// Checks if a pattern represents a Crashable check.
    /// The parser creates TypePattern(type: "Crashable") rather than CrashablePattern.
    /// </summary>
    private static bool IsCrashablePattern(Pattern pattern)
    {
        return pattern is CrashablePattern
            or TypePattern { Type.Name: "Crashable" };
    }

    /// <summary>
    /// Checks if a pattern is a generic Crashable catch-all (not a specific error type).
    /// 'is Crashable e' is a catch-all; 'is FileNotFoundError e' is not.
    /// </summary>
    private static bool IsCrashableCatchAll(Pattern pattern)
    {
        return pattern is CrashablePattern { ErrorType: null }
            or TypePattern { Type.Name: "Crashable" };
    }

    /// <summary>
    /// Resolves the RoutineInfo for a call expression's callee.
    /// Returns null if the callee cannot be resolved to a known routine.
    /// The parser appends '!' to failable call names, so we strip it for lookup.
    /// </summary>
    private RoutineInfo? ResolveCalledRoutine(CallExpression call)
    {
        switch (call.Callee)
        {
            case IdentifierExpression id:
            {
                string name = id.Name;
                // Parser appends '!' to failable call identifiers (e.g., "parse!")
                // but routines are registered without it (e.g., "parse")
                string baseName = name.EndsWith('!') ? name[..^1] : name;
                return _registry.LookupRoutine(fullName: baseName)
                    ?? _registry.LookupRoutine(fullName: name)
                    ?? LookupRoutineWithImports(name: baseName)
                    ?? LookupRoutineWithImports(name: name);
            }
            case MemberExpression member:
            {
                TypeSymbol objectType = AnalyzeExpression(expression: member.Object);
                return _registry.LookupRoutine(fullName: $"{objectType.Name}.{member.PropertyName}");
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Computes the narrowed type after eliminating None and/or Crashable possibilities.
    /// </summary>
    /// <returns>The narrowed type, or null if narrowing is not possible.</returns>
    private static TypeSymbol? ComputeNarrowedType(TypeSymbol type, bool eliminateNone, bool eliminateCrashable)
    {
        if (type is not ErrorHandlingTypeInfo ehType) return null;

        return ehType.Kind switch
        {
            // Maybe<T>: eliminate None → T
            ErrorHandlingKind.Maybe when eliminateNone => ehType.ValueType,

            // Result<T>: eliminate Crashable → T
            ErrorHandlingKind.Result when eliminateCrashable => ehType.ValueType,

            // Lookup<T>: must eliminate both None and Crashable → T
            ErrorHandlingKind.Lookup when eliminateNone && eliminateCrashable => ehType.ValueType,

            // Partial elimination on Lookup is not sufficient
            _ => null
        };
    }

    /// <summary>
    /// Checks if a statement always produces a return value (return, throw, absent, becomes).
    /// Used for missing-return validation (#144).
    /// Unlike <see cref="HasDefiniteExit"/>, this does not count break/continue as terminating,
    /// since they exit loops but don't return a value from the routine.
    /// </summary>
    private static bool StatementAlwaysTerminates(Statement statement)
    {
        return statement switch
        {
            ReturnStatement => true,
            ThrowStatement => true,
            AbsentStatement => true,
            BecomesStatement => true,
            BlockStatement block => block.Statements.Count > 0
                                    && StatementAlwaysTerminates(statement: block.Statements[^1]),
            IfStatement { ElseStatement: not null } ifStmt =>
                StatementAlwaysTerminates(statement: ifStmt.ThenStatement)
                && StatementAlwaysTerminates(statement: ifStmt.ElseStatement),
            WhenStatement whenStmt =>
                whenStmt.Clauses.Count > 0
                && whenStmt.Clauses.Any(c => c.Pattern is ElsePattern or WildcardPattern)
                && whenStmt.Clauses.All(c => StatementAlwaysTerminates(statement: c.Body)),
            _ => false
        };
    }

    /// <summary>
    /// Checks if a statement always exits the current scope (return, throw, absent, break, continue).
    /// Used for guard clause narrowing.
    /// </summary>
    private static bool HasDefiniteExit(Statement statement)
    {
        return statement switch
        {
            ReturnStatement => true,
            ThrowStatement => true,
            AbsentStatement => true,
            BreakStatement => true,
            ContinueStatement => true,
            BlockStatement block => block.Statements.Count > 0
                                    && HasDefiniteExit(statement: block.Statements[^1]),
            IfStatement { ElseStatement: not null } ifStmt =>
                HasDefiniteExit(statement: ifStmt.ThenStatement)
                && HasDefiniteExit(statement: ifStmt.ElseStatement),
            _ => false
        };
    }

    /// <summary>
    /// Checks if a type is valid for resident member variables.
    /// Residents can contain: records, primitives (intrinsic wrappers), Snatched[T], other residents, choices, flags.
    /// </summary>
    /// <summary>
    /// Fixed-width numeric type names (excludes system-dependent SAddr/UAddr).
    /// </summary>
    private static readonly HashSet<string> FixedWidthNumericTypeNames =
    [
        "S8", "S16", "S32", "S64", "S128",
        "U8", "U16", "U32", "U64", "U128",
        "F16", "F32", "F64", "F128",
        "D32", "D64", "D128"
    ];

    /// <summary>
    /// Returns true if the type is a fixed-width numeric type (excludes SAddr/UAddr).
    /// </summary>
    private static bool IsFixedWidthNumericType(TypeInfo type)
    {
        return FixedWidthNumericTypeNames.Contains(item: type.Name);
    }

    private static bool IsValidResidentFieldType(TypeInfo type)
    {
        return type switch
        {
            RecordTypeInfo record => true, // includes primitive wrappers like Text, S32, Bool
            ResidentTypeInfo => true,
            ChoiceTypeInfo => true,
            FlagsTypeInfo => true,
            IntrinsicTypeInfo => true, // raw primitives
            WrapperTypeInfo { Name: "Snatched" } => true,
            _ => false
        };
    }

    #endregion
}
