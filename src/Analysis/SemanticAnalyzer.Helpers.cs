namespace Compilers.Analysis;

using Enums;
using Symbols;
using Types;
using Shared.AST;
using global::RazorForge.Diagnostics;
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

    private void AnalyzeCallArguments(RoutineInfo routine, List<Expression> arguments, SourceLocation location)
    {
        IReadOnlyList<ParameterInfo> parameters = routine.Parameters;
        int requiredParams = parameters.Count(p => !p.HasDefaultValue);
        int totalParams = parameters.Count;

        // Check argument count
        if (arguments.Count < requiredParams)
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
        else if (arguments.Count > totalParams && !routine.IsVariadic)
        {
            ReportError(
                SemanticDiagnosticCode.TooManyArguments,
                $"'{routine.Name}' expects at most {totalParams} argument(s), but got {arguments.Count}.",
                location);
        }

        // Check argument types
        for (int i = 0; i < arguments.Count; i++)
        {
            Expression arg = arguments[i];

            // Skip type check for extra variadic arguments
            if (i >= totalParams)
            {
                AnalyzeExpression(expression: arg);
                continue;
            }

            ParameterInfo param = parameters[i];
            TypeSymbol paramType = param.Type;

            // Pass expected type for contextual inference (e.g., integer literals)
            TypeSymbol argType = AnalyzeExpression(expression: arg, expectedType: paramType);

            // Skip if either is an error type (to reduce cascading errors)
            if (argType.Category == TypeCategory.Error || paramType.Category == TypeCategory.Error)
            {
                continue;
            }

            if (!IsAssignableTo(source: argType, target: paramType))
            {
                ReportError(
                    SemanticDiagnosticCode.ArgumentTypeMismatch,
                    $"Argument {i + 1} of '{routine.Name}': cannot convert '{argType.Name}' to '{paramType.Name}'.",
                    arg.Location);
            }
        }
    }

    private bool IsAssignableTarget(Expression target)
    {
        return target is IdentifierExpression
            or MemberExpression
            or IndexExpression;
    }

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

        // Generic type matching - check if instantiation matches definition
        if (target.IsGenericDefinition && source.IsGenericInstantiation)
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

        // No implicit conversions - all type conversions must be explicit via constructor syntax
        return false;
    }

    /// <summary>
    /// Gets the base type name without generic arguments.
    /// </summary>
    private static string GetBaseTypeName(string typeName)
    {
        int genericIndex = typeName.IndexOf(value: '<');
        return genericIndex >= 0 ? typeName[..genericIndex] : typeName;
    }

    private bool IsBoolType(TypeSymbol type)
    {
        return type.Name is "Bool";
    }

    private bool IsNumericType(TypeSymbol type)
    {
        return IsIntegerType(type: type) || IsFloatType(type: type) || IsDecimalType(type: type);
    }

    private bool IsIntegerType(TypeSymbol type)
    {
        // Check if type follows the Integral protocol
        return ImplementsProtocol(type: type, protocolName: "Integral");
    }

    private bool IsFloatType(TypeSymbol type)
    {
        // Check if type follows the Floating protocol (binary floats)
        return ImplementsProtocol(type: type, protocolName: "BinaryFP");
    }

    private bool IsDecimalType(TypeSymbol type)
    {
        // Check if type follows the DecimalFloating protocol
        return ImplementsProtocol(type: type, protocolName: "DecimalFP");
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
    /// Checks if an operator is a non-desugared comparison operator.
    /// Note: ==, !=, &lt;, &lt;=, &gt;, &gt;=, &lt;=&gt;, in, notin are desugared to method calls in the parser.
    /// This only returns true for operators that remain as BinaryExpression nodes.
    /// </summary>
    private static bool IsComparisonOperator(BinaryOperator op)
    {
        return op is BinaryOperator.Identical or BinaryOperator.NotIdentical
            or BinaryOperator.Is or BinaryOperator.IsNot
            or BinaryOperator.Follows or BinaryOperator.NotFollows;
    }

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
        // Saturating arithmetic
        "__add_sat__", "__sub_sat__", "__mul_sat__", "__pow_sat__",
        // Checked arithmetic
        "__add_checked__", "__sub_checked__", "__mul_checked__",
        "__floordiv_checked__", "__mod_checked__", "__pow_checked__",
        // Comparison
        "__eq__", "__ne__", "__lt__", "__le__", "__gt__", "__ge__", "__cmp__",
        // Bitwise
        "__and__", "__or__", "__xor__",
        "__ashl__", "__ashl_checked__", "__ashr__", "__lshl__", "__lshr__",
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

    private static bool IsOperatorDunder(string name)
    {
        return OperatorDunders.Contains(value: name);
    }

    /// <summary>
    /// Validates comparison operands for type compatibility and operator support.
    /// Called from both AnalyzeBinaryExpression (for non-desugared operators like ===, is, follows)
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

    private TypeSymbol GetWiderNumericType(TypeSymbol left, TypeSymbol right)
    {
        // With no implicit conversions, types must match exactly
        // This method is now only called as a fallback when both types are numeric
        // Return left type since they should be the same
        return left;
    }

    private TypeSymbol GetIterableElementType(TypeSymbol iterableType, SourceLocation location)
    {
        // Type must follow both Sequential and SequenceGenerator protocols
        bool followsSequential = ImplementsProtocol(type: iterableType, protocolName: "Sequential");
        bool followsSequenceGenerator = ImplementsProtocol(type: iterableType, protocolName: "SequenceGenerator");

        if (!followsSequential || !followsSequenceGenerator)
        {
            ReportError(
                SemanticDiagnosticCode.TypeNotIterable,
                $"Type '{iterableType.Name}' is not iterable. Types must follow both 'Sequential' and 'SequenceGenerator' protocols to be used in for-in loops.",
                location);
            return ErrorTypeInfo.Instance;
        }

        // Look for __seq__ method to get element type from SequenceGenerator<T> return type
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
            $"Cannot determine element type for '{iterableType.Name}'. The __seq__ method must return SequenceGenerator<T>.",
            location);
        return ErrorTypeInfo.Instance;
    }

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

        // Handle generic instantiations
        if (expected.IsGenericDefinition && actual.IsGenericInstantiation)
        {
            string baseName = GetBaseTypeName(typeName: actual.Name);
            if (baseName == expected.Name)
            {
                return true;
            }
        }

        return false;
    }

    private TypeSymbol CreateViewedType(TypeSymbol innerType)
    {
        TypeSymbol? viewedDef = _registry.LookupType(name: "Viewed");
        if (viewedDef != null)
        {
            return _registry.GetOrCreateInstantiation(genericDef: viewedDef, typeArguments: [innerType]);
        }

        // Synthesize wrapper type if not defined in the program
        return _registry.GetOrCreateWrapperType(
            wrapperName: "Viewed",
            innerType: innerType,
            isReadOnly: true);
    }

    private TypeSymbol CreateHijackedType(TypeSymbol innerType)
    {
        TypeSymbol? hijackedDef = _registry.LookupType(name: "Hijacked");
        if (hijackedDef != null)
        {
            return _registry.GetOrCreateInstantiation(genericDef: hijackedDef, typeArguments: [innerType]);
        }

        // Synthesize wrapper type if not defined in the program
        return _registry.GetOrCreateWrapperType(
            wrapperName: "Hijacked",
            innerType: innerType,
            isReadOnly: false);
    }

    private TypeSymbol CreateInspectedType(TypeSymbol innerType)
    {
        TypeSymbol? inspectedDef = _registry.LookupType(name: "Inspected");
        if (inspectedDef != null)
        {
            return _registry.GetOrCreateInstantiation(genericDef: inspectedDef, typeArguments: [innerType]);
        }

        // Synthesize wrapper type if not defined in the program
        return _registry.GetOrCreateWrapperType(
            wrapperName: "Inspected",
            innerType: innerType,
            isReadOnly: true);
    }

    private TypeSymbol CreateSeizedType(TypeSymbol innerType)
    {
        TypeSymbol? seizedDef = _registry.LookupType(name: "Seized");
        if (seizedDef != null)
        {
            return _registry.GetOrCreateInstantiation(genericDef: seizedDef, typeArguments: [innerType]);
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
        // Check if the type name is "Hijacked" (for instantiated generic types)
        return type.Name == "Hijacked";
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
    /// Tries to look up a field on the inner type of a wrapper type.
    /// </summary>
    /// <param name="wrapperType">The wrapper type.</param>
    /// <param name="fieldName">The name of the field to look up.</param>
    /// <returns>The field info if found, null otherwise.</returns>
    private FieldInfo? LookupFieldOnWrapperInnerType(TypeSymbol wrapperType, string fieldName)
    {
        TypeSymbol? innerType = GetWrapperInnerType(wrapperType: wrapperType);
        if (innerType == null)
        {
            return null;
        }

        return innerType switch
        {
            RecordTypeInfo record => record.LookupField(fieldName: fieldName),
            EntityTypeInfo entity => entity.LookupField(fieldName: fieldName),
            ResidentTypeInfo resident => resident.LookupField(fieldName: fieldName),
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
            return; // Mutable wrappers can access all methods
        }

        // Read-only wrappers can only access @readonly methods
        if (!method.IsReadOnly)
        {
            string wrapperName = GetBaseTypeName(typeName: wrapperType.Name);
            ReportError(
                SemanticDiagnosticCode.WritableMethodThroughReadOnlyWrapper,
                $"Cannot call writable method '{method.Name}' through read-only wrapper '{wrapperName}<T>'. " +
                $"Only @readonly methods are accessible.",
                location);
        }
    }

    #endregion

    #region Memory Token Validation

    /// <summary>
    /// Token types that cannot be returned from routines or stored in fields.
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
    /// These tokens cannot be returned from routines or stored in fields.
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
    /// Validates that a type is not an inline-only token when used as a field type.
    /// </summary>
    private void ValidateNotTokenFieldType(TypeSymbol type, string fieldName, SourceLocation location)
    {
        if (_registry.Language != Language.RazorForge)
        {
            return; // Token validation only applies to RazorForge
        }

        if (IsInlineOnlyTokenType(type: type))
        {
            ReportError(
                SemanticDiagnosticCode.TokenFieldNotAllowed,
                $"Cannot store {GetTokenKindDescription(type: type)} in field '{fieldName}'. Tokens are inline-only and cannot be stored.",
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
    /// Validates that a field's setter visibility is equal to or more restrictive than its getter visibility.
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
    /// Gets the effective visibility for field write access.
    /// For posted fields, write access is secret (only owner can write).
    /// </summary>
    /// <param name="field">The field to check.</param>
    /// <returns>The effective visibility for write access.</returns>
    private static VisibilityModifier GetEffectiveWriteVisibility(FieldInfo field)
    {
        // Posted fields have open read but secret write
        return field.Visibility == VisibilityModifier.Posted
            ? VisibilityModifier.Secret
            : field.Visibility;
    }

    /// <summary>
    /// Checks if access to a field is allowed from the current context.
    /// </summary>
    /// <param name="field">The field being accessed.</param>
    /// <param name="isWrite">Whether this is a write access (assignment).</param>
    /// <param name="accessLocation">Source location of the access site.</param>
    private void ValidateFieldAccess(FieldInfo field, bool isWrite, SourceLocation accessLocation)
    {
        // For published fields, write access is restricted to private (owner only)
        VisibilityModifier visibility = isWrite
            ? GetEffectiveWriteVisibility(field: field)
            : field.Visibility;

        ValidateMemberAccess(
            visibility: visibility,
            memberKind: "field",
            memberName: field.Name,
            ownerType: field.Owner,
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
                RoutineKind.Constructor => "constructor",
                RoutineKind.Method => "method",
                _ => "routine"
            },
            memberName: routine.Name,
            ownerType: routine.OwnerType,
            accessLocation: accessLocation);
    }

    /// <summary>
    /// Validates access to a member based on visibility rules.
    /// </summary>
    /// <param name="visibility">The visibility modifier of the member.</param>
    /// <param name="memberKind">The kind of member (field, method, etc.) for error messages.</param>
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
                        SemanticDiagnosticCode.PrivateMemberAccess,
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
    /// Validates write access to a field, checking setter visibility.
    /// </summary>
    /// <param name="objectType">The type of the object being accessed.</param>
    /// <param name="fieldName">The name of the field being written.</param>
    /// <param name="location">The source location of the write.</param>
    private void ValidateFieldWriteAccess(TypeSymbol objectType, string fieldName, SourceLocation location)
    {
        FieldInfo? field = objectType switch
        {
            RecordTypeInfo record => record.LookupField(fieldName: fieldName),
            EntityTypeInfo entity => entity.LookupField(fieldName: fieldName),
            ResidentTypeInfo resident => resident.LookupField(fieldName: fieldName),
            _ => null
        };

        if (field != null)
        {
            ValidateFieldAccess(field: field, isWrite: true, accessLocation: location);
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

    #endregion
}
