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

    /// <summary>Address-sized unsigned integer (Address).</summary>
    Address,

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
    #region Carrier Type Helpers

    /// <summary>
    /// Returns the base name ("Maybe", "Result", or "Lookup") for a carrier type,
    /// or null if the type is not a carrier type.
    /// Works for both generic definitions (name == "Maybe") and resolved instances (GenericDefinition.Name == "Maybe").
    /// </summary>
    private static string? GetCarrierBaseName(TypeSymbol type)
    {
        if (type is not RecordTypeInfo r)
        {
            return null;
        }

        string baseName = r.GenericDefinition?.Name ?? r.Name;
        return baseName is "Maybe" or "Result" or "Lookup" ? baseName : null;
    }

    /// <summary>
    /// Returns true if the type is a carrier type (Maybe, Result, or Lookup).
    /// </summary>
    private static bool IsCarrierType(TypeSymbol type) => GetCarrierBaseName(type: type) != null;

    /// <summary>
    /// Returns true if the type is a Maybe carrier type.
    /// </summary>
    private static bool IsMaybeType(TypeSymbol type) => GetCarrierBaseName(type: type) == "Maybe";

    #endregion

    #region Helper Methods for Analysis

    /// <summary>
    /// Validates argument count and types for a routine call against the routine's parameter list.
    /// Reports errors for too-few arguments, too-many arguments (on non-variadic routines), and type mismatches.
    /// </summary>
    private void AnalyzeCallArguments(RoutineInfo routine, List<Expression> arguments,
        SourceLocation location, TypeSymbol? callObjectType = null)
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
        int nonMeParamCount =
            parameters.Count(predicate: p => p.Name != "me" && !p.HasDefaultValue);
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
                    if (parameters[index: j].Name == named.Name)
                    {
                        paramIndex = j;
                        break;
                    }
                }

                if (paramIndex == -1)
                {
                    // S505: Unknown named argument
                    ReportError(code: SemanticDiagnosticCode.UnknownNamedArgument,
                        message: $"'{routine.Name}' has no parameter named '{named.Name}'.",
                        location: named.Location);
                    AnalyzeExpression(expression: named.Value);
                }
                else if (boundParams.ContainsKey(key: paramIndex))
                {
                    // S506: Duplicate named argument (parameter already bound)
                    ReportError(code: SemanticDiagnosticCode.DuplicateNamedArgument,
                        message: $"Parameter '{named.Name}' of '{routine.Name}' is already bound.",
                        location: named.Location);
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
                    ReportError(code: SemanticDiagnosticCode.NamedArgumentRequired,
                        message:
                        $"Routine '{routine.Name}' has {nonMeParamCount} parameters - all arguments must be named.",
                        location: arg.Location);
                }
                else if (seenNamed)
                {
                    // S507: Positional argument after named argument
                    ReportError(code: SemanticDiagnosticCode.PositionalAfterNamed,
                        message:
                        $"Positional argument cannot appear after named arguments in call to '{routine.Name}'.",
                        location: arg.Location);
                }

                // For variadic routines: once we reach the varargs parameter,
                // all subsequent positional args are varargs (don't advance past it).
                // Trailing params (sep, end) are only filled via named args or defaults.
                bool inVariadicSlot = routine.IsVariadic && positionalIndex > 0 &&
                                      positionalIndex - 1 < totalParams &&
                                      parameters[index: positionalIndex - 1].IsVariadicParam;

                if (inVariadicSlot)
                {
                    // Variadic extra argument — just analyze it
                    AnalyzeExpression(expression: arg);
                }
                else if (positionalIndex < totalParams)
                {
                    if (boundParams.ContainsKey(key: positionalIndex))
                    {
                        // S506: Positional arg collides with earlier named arg that bound this slot
                        ReportError(code: SemanticDiagnosticCode.DuplicateNamedArgument,
                            message:
                            $"Parameter '{parameters[index: positionalIndex].Name}' of '{routine.Name}' is already bound.",
                            location: arg.Location);
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

                if (!inVariadicSlot)
                {
                    positionalIndex++;
                }
            }
        }

        // Phase 2: Check argument count against required parameters.
        int requiredParams = parameters.Count(predicate: p => !p.HasDefaultValue);
        int unboundRequired = 0;
        for (int i = 0; i < totalParams; i++)
        {
            if (!boundParams.ContainsKey(key: i) && !parameters[index: i].HasDefaultValue)
            {
                unboundRequired++;
            }
        }

        if (unboundRequired > 0)
        {
            if (requiredParams == totalParams)
            {
                ReportError(code: SemanticDiagnosticCode.TooFewArguments,
                    message:
                    $"'{routine.Name}' expects {totalParams} argument(s), but got {arguments.Count}.",
                    location: location);
            }
            else
            {
                ReportError(code: SemanticDiagnosticCode.TooFewArguments,
                    message:
                    $"'{routine.Name}' expects at least {requiredParams} argument(s), but got {arguments.Count}.",
                    location: location);
            }
        }
        else if (positionalIndex > totalParams && !routine.IsVariadic)
        {
            ReportError(code: SemanticDiagnosticCode.TooManyArguments,
                message:
                $"'{routine.Name}' expects at most {totalParams} argument(s), but got {arguments.Count}.",
                location: location);
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

            ParameterInfo param = parameters[index: binding.Key];
            TypeSymbol paramType = param.Type;

            // For variadic parameters, type-check against the element type T, not List[T]
            if (param.IsVariadicParam && paramType is
                    { IsGenericResolution: true, TypeArguments: [var elemType, ..] })
            {
                paramType = elemType;
            }

            if (callObjectType != null &&
                routine.OwnerType is GenericParameterTypeInfo genParamOwner)
            {
                var substitutions = new Dictionary<string, TypeSymbol>
                {
                    [key: genParamOwner.Name] = callObjectType
                };
                paramType = SubstituteWithMapping(type: paramType, substitutions: substitutions);
            }

            Expression argExpr = binding.Value;
            TypeSymbol argType = AnalyzeExpression(expression: argExpr, expectedType: paramType);

            if (argType.Category == TypeCategory.Error || paramType.Category == TypeCategory.Error)
            {
                continue;
            }

            if (!IsAssignableTo(source: argType, target: paramType))
            {
                ReportError(code: SemanticDiagnosticCode.ArgumentTypeMismatch,
                    message:
                    $"Argument '{param.Name}' of '{routine.Name}': cannot convert '{argType.Name}' to '{paramType.Name}'.",
                    location: argExpr.Location);
            }
        }
    }

    /// <summary>
    /// Returns true if the expression can appear on the left-hand side of an assignment.
    /// Valid assignment targets are identifiers, member accesses, and index expressions.
    /// </summary>
    private bool IsAssignableTarget(Expression target)
    {
        return target is IdentifierExpression or MemberExpression or IndexExpression;
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

        // Reverse: generic definition assignable to its parameterized form within generic context.
        // e.g., 'me' has type 'Total' (generic def) but return expects 'Total[T]'.
        // Only allowed when all type args are unresolved generic parameters (not concrete types).
        if (source.IsGenericDefinition && target.IsGenericResolution &&
            target.TypeArguments != null &&
            target.TypeArguments.All(predicate: t => t is GenericParameterTypeInfo))
        {
            string baseName = GetBaseTypeName(typeName: target.Name);
            if (baseName == source.Name)
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
        return genericIndex >= 0
            ? typeName[..genericIndex]
            : typeName;
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

        // Use LookupMethod which handles generic resolutions (e.g., Snatched[Point].$eq)
        return _registry.LookupMethod(type: type, methodName: methodName) != null;
    }

    /// <summary>
    /// Checks if an operator is a comparison operator that returns Bool.
    /// Includes both identity operators and overloadable comparison/membership operators.
    /// Note: ThreeWayComparator (&lt;=&gt;) returns ComparisonSign, not Bool, so it is excluded.
    /// </summary>
    private static bool IsComparisonOperator(BinaryOperator op)
    {
        return op is BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.Less
            or BinaryOperator.LessEqual or BinaryOperator.Greater or BinaryOperator.GreaterEqual
            or BinaryOperator.In or BinaryOperator.NotIn or BinaryOperator.Identical
            or BinaryOperator.NotIdentical or BinaryOperator.Is or BinaryOperator.IsNot
            or BinaryOperator.Obeys or BinaryOperator.Disobeys;
    }

    /// <summary>Returns true if the operator is a short-circuit logical operator (<c>and</c> or <c>or</c>).</summary>
    private bool IsLogicalOperator(BinaryOperator op)
    {
        return op is BinaryOperator.And or BinaryOperator.Or;
    }

    /// <summary>
    /// Operator wired methods that choices are NOT allowed to define or call.
    /// Choices do not support any operators — use 'is' for case matching.
    /// </summary>
    private static readonly HashSet<string> OperatorWiredMethods =
    [
        // Arithmetic
        "$add", "$sub", "$mul", "$truediv", "$floordiv", "$mod", "$pow",
        // Wrapping arithmetic
        "$add_wrap", "$sub_wrap", "$mul_wrap", "$pow_wrap",
        // Clamping arithmetic
        "$add_clamp", "$sub_clamp", "$mul_clamp", "$truediv_clamp", "$pow_clamp",
        // Comparison
        "$eq", "$ne", "$lt", "$le", "$gt", "$ge", "$cmp",
        // Bitwise
        "$bitand", "$bitor", "$bitxor",
        "$ashl", "$ashr", "$lshl", "$lshr",
        // Unary
        "$neg", "$bitnot",
        // Membership
        "$contains", "$notcontains",
        // Indexing
        "$getitem", "$setitem",
        // Iteration
        "$iter", "$next",
        // Context management
        "$enter", "$exit"
    ];

    /// <summary>Returns true if the given method name is an operator wired (e.g., <c>$add</c>, <c>$eq</c>).</summary>
    private static bool IsOperatorWired(string name)
    {
        return OperatorWiredMethods.Contains(value: name);
    }

    /// <summary>
    /// Validates comparison operands for type compatibility and operator support.
    /// Called from both AnalyzeBinaryExpression (for non-desugared operators like ===, is, obeys)
    /// and AnalyzeChainedComparisonExpression (for chained comparisons like a &lt; b &lt; c).
    /// </summary>
    private void ValidateComparisonOperands(TypeSymbol left, TypeSymbol right, BinaryOperator op,
        SourceLocation location)
    {
        // Identity operators (===, !==) only work on entity types
        if (op is BinaryOperator.Identical or BinaryOperator.NotIdentical)
        {
            if (left.Category is not TypeCategory.Entity ||
                right.Category is not TypeCategory.Entity)
            {
                ReportError(code: SemanticDiagnosticCode.IdentityOperatorRequiresReference,
                    message:
                    $"Identity operator '{op.ToStringRepresentation()}' can only be used with entity types, not '{left.Name}' and '{right.Name}'.",
                    location: location);
            }

            return;
        }

        // Variants cannot use equality or ordering operators (only 'is' and 'isnot')
        if (left.Category == TypeCategory.Variant || right.Category == TypeCategory.Variant)
        {
            if (op is not (BinaryOperator.Is or BinaryOperator.IsNot))
            {
                ReportError(code: SemanticDiagnosticCode.ComparisonOnVariantType,
                    message:
                    $"Comparison operator '{op.ToStringRepresentation()}' cannot be used with variant types. Use 'is' or 'isnot' for pattern matching.",
                    location: location);
            }

            return;
        }

        // Membership operators (in, notin): check that right has $contains accepting left
        if (op is BinaryOperator.In or BinaryOperator.NotIn)
        {
            RoutineInfo? containsMethod =
                _registry.LookupMethod(type: right, methodName: "$contains");
            if (containsMethod == null)
            {
                ReportError(code: SemanticDiagnosticCode.IncompatibleComparisonTypes,
                    message:
                    $"Type '{right.Name}' does not support 'in'/'notin' (no $contains method).",
                    location: location);
            }

            return;
        }

        // Check that types are compatible (same type or error type)
        if (!IsAssignableTo(source: left, target: right) &&
            !IsAssignableTo(source: right, target: left))
        {
            ReportError(code: SemanticDiagnosticCode.IncompatibleComparisonTypes,
                message:
                $"Cannot compare values of incompatible types '{left.Name}' and '{right.Name}'.",
                location: location);
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
            ReportError(code: SemanticDiagnosticCode.OrderingNotSupported,
                message:
                $"Type '{left.Name}' does not support comparison operator '{op.ToStringRepresentation()}'.",
                location: location);
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
    private void ValidateComparisonChain(ChainedComparisonExpression chain,
        SourceLocation location)
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
                ReportError(code: SemanticDiagnosticCode.NotEqualInComparisonChain,
                    message: "The '!=' operator cannot be used in comparison chains.",
                    location: location);
                return;
            }

            bool opIsAscending = op is BinaryOperator.Less or BinaryOperator.LessEqual;
            bool opIsDescending = op is BinaryOperator.Greater or BinaryOperator.GreaterEqual;

            if (opIsAscending)
            {
                if (isAscending == false)
                {
                    ReportError(code: SemanticDiagnosticCode.MixedComparisonChainDirection,
                        message:
                        "Cannot mix ascending (<, <=) and descending (>, >=) operators in a comparison chain.",
                        location: location);
                    return;
                }

                isAscending = true;
            }
            else if (opIsDescending)
            {
                if (isAscending == true)
                {
                    ReportError(code: SemanticDiagnosticCode.MixedComparisonChainDirection,
                        message:
                        "Cannot mix ascending (<, <=) and descending (>, >=) operators in a comparison chain.",
                        location: location);
                    return;
                }

                isAscending = false;
            }
        }
    }

    /// <summary>
    /// Resolves the element type produced by iterating over <paramref name="iterableType"/>.
    /// The type must implement the <c>Iterable</c> protocol, whose <c>$iter</c> returns a <c>Iterator[T]</c>.
    /// The element type is taken from the return type of the <c>$iter</c> method or the type's first generic argument.
    /// Reports an error and returns <see cref="ErrorTypeInfo"/> if the type is not iterable or the element type cannot be determined.
    /// </summary>
    private TypeSymbol GetIterableElementType(TypeSymbol iterableType, SourceLocation location)
    {
        // Type must follow the Iterable protocol
        bool obeysIterable = ImplementsProtocol(type: iterableType, protocolName: "Iterable");

        // For generic resolution types, also check if the generic definition has $iter
        if (!obeysIterable && iterableType.IsGenericResolution)
        {
            RoutineInfo? seqMethod =
                _registry.LookupMethod(type: iterableType, methodName: "$iter");
            if (seqMethod != null)
            {
                obeysIterable = true;
            }
        }

        if (!obeysIterable)
        {
            ReportError(code: SemanticDiagnosticCode.TypeNotIterable,
                message: $"Type '{iterableType.Name}' is not iterable. Types must follow the " +
                         $"'Iterable' protocol to be used in for-in loops.",
                location: location);
            return ErrorTypeInfo.Instance;
        }

        // Strategy 1: Extract element type from Iterable[X] protocol conformance.
        // This correctly handles chained generics like EnumerateIterator[T] obeys Iterable[Tuple[S64, T]]
        IReadOnlyList<TypeSymbol>? protocols = iterableType switch
        {
            RecordTypeInfo record => record.ImplementedProtocols,
            EntityTypeInfo entity => entity.ImplementedProtocols,
            _ => null
        };

        if (protocols != null)
        {
            foreach (TypeSymbol proto in protocols)
            {
                if (GetBaseTypeName(typeName: proto.Name) == "Iterable" &&
                    proto.TypeArguments is { Count: > 0 })
                {
                    TypeInfo elementType = proto.TypeArguments[index: 0];

                    // Resolve generic parameters if the iterable is a generic resolution
                    if (iterableType is { IsGenericResolution: true, TypeArguments: not null })
                    {
                        TypeInfo? genericDef = iterableType switch
                        {
                            RecordTypeInfo r => r.GenericDefinition,
                            EntityTypeInfo e => e.GenericDefinition,
                            _ => null
                        };
                        if (genericDef?.GenericParameters != null)
                        {
                            var substitution = new Dictionary<string, TypeInfo>();
                            for (int i = 0;
                                 i < genericDef.GenericParameters.Count &&
                                 i < iterableType.TypeArguments.Count;
                                 i++)
                            {
                                substitution[key: genericDef.GenericParameters[index: i]] =
                                    iterableType.TypeArguments[index: i];
                            }

                            elementType = SubstituteTypeParams(type: elementType,
                                substitution: substitution);
                        }
                    }

                    return elementType;
                }
            }
        }

        // Strategy 2: Look for $iter method to get element type from Iterator[T] return type
        RoutineInfo? seqMethod2 = _registry.LookupRoutine(fullName: $"{iterableType.Name}.$iter");

        // Generic fallback: Range[S64].$iter → Range.$iter via LookupMethod
        if (seqMethod2 == null)
        {
            seqMethod2 = _registry.LookupMethod(type: iterableType, methodName: "$iter");
        }

        if (seqMethod2?.ReturnType?.TypeArguments is { Count: > 0 })
        {
            // Resolve generic type args: if return type arg is T and iterableType is Range[S64], resolve T → S64
            TypeInfo returnTypeArg = seqMethod2.ReturnType.TypeArguments[index: 0];
            if (returnTypeArg is GenericParameterTypeInfo && iterableType is
                    { IsGenericResolution: true, TypeArguments: not null })
            {
                TypeInfo? genericDef = iterableType switch
                {
                    RecordTypeInfo r => r.GenericDefinition,
                    EntityTypeInfo e => e.GenericDefinition,
                    _ => null
                };
                if (genericDef?.GenericParameters != null)
                {
                    int paramIndex = genericDef.GenericParameters
                                               .ToList()
                                               .IndexOf(item: returnTypeArg.Name);
                    if (paramIndex >= 0 && paramIndex < iterableType.TypeArguments.Count)
                    {
                        return iterableType.TypeArguments[index: paramIndex];
                    }
                }
            }

            return returnTypeArg;
        }

        // Fallback to type arguments if $iter method not found but protocol is implemented
        if (iterableType.TypeArguments is { Count: > 0 })
        {
            return iterableType.TypeArguments[index: 0];
        }

        ReportError(code: SemanticDiagnosticCode.TypeNotIterable,
            message:
            $"Cannot determine element type for '{iterableType.Name}'. The $iter method must return Iterator[T].",
            location: location);
        return ErrorTypeInfo.Instance;
    }

    #endregion

}
