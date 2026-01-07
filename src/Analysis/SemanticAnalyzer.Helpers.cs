namespace Compilers.Analysis;

using Compilers.Analysis.Enums;
using Compilers.Analysis.Symbols;
using Compilers.Analysis.Types;
using Compilers.Shared.AST;
using TypeSymbol = Compilers.Analysis.Types.TypeInfo;

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
                    message: $"'{routine.Name}' expects {totalParams} argument(s), but got {arguments.Count}.",
                    location: location);
            }
            else
            {
                ReportError(
                    message: $"'{routine.Name}' expects at least {requiredParams} argument(s), but got {arguments.Count}.",
                    location: location);
            }
        }
        else if (arguments.Count > totalParams && !routine.IsVariadic)
        {
            ReportError(
                message: $"'{routine.Name}' expects at most {totalParams} argument(s), but got {arguments.Count}.",
                location: location);
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
                    message: $"Argument {i + 1} of '{routine.Name}': cannot convert '{argType.Name}' to '{paramType.Name}'.",
                    location: arg.Location);
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
    /// Checks if a type supports the true division operator (/).
    /// True division is NOT supported on native integers - they must use floor division (//).
    /// </summary>
    private bool SupportsTrueDivision(TypeSymbol type)
    {
        // Check if the type has the __truediv__ method
        return _registry.LookupRoutine(fullName: $"{type.Name}.__truediv__") != null
            || _registry.LookupRoutine(fullName: $"{type.Name}.__truediv__!") != null;
    }

    /// <summary>
    /// Checks if a type supports floor division operator (//).
    /// </summary>
    private bool SupportsFloorDivision(TypeSymbol type)
    {
        return _registry.LookupRoutine(fullName: $"{type.Name}.__floordiv__") != null
            || _registry.LookupRoutine(fullName: $"{type.Name}.__floordiv__!") != null;
    }

    /// <summary>
    /// Checks if a type supports overflow-handling arithmetic operators (+%, -%, *%, etc.).
    /// These are only valid for fixed-width integer types.
    /// </summary>
    private bool SupportsOverflowArithmetic(TypeSymbol type, string operatorSuffix)
    {
        // Check for the specific overflow variant method
        // operatorSuffix is "wrap", "sat", or "checked"
        string[] baseOps = ["__add_", "__sub_", "__mul_"];
        foreach (string baseOp in baseOps)
        {
            string methodName = $"{type.Name}.{baseOp}{operatorSuffix}__";
            if (_registry.LookupRoutine(fullName: methodName) != null)
            {
                return true;
            }
        }

        return false;
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

    private bool IsComparisonOperator(BinaryOperator op)
    {
        return op is BinaryOperator.Equal or BinaryOperator.NotEqual
            or BinaryOperator.Identical or BinaryOperator.NotIdentical
            or BinaryOperator.Less or BinaryOperator.LessEqual
            or BinaryOperator.Greater or BinaryOperator.GreaterEqual
            or BinaryOperator.ThreeWayComparator
            or BinaryOperator.In or BinaryOperator.NotIn
            or BinaryOperator.Is or BinaryOperator.IsNot
            or BinaryOperator.Follows or BinaryOperator.NotFollows;
    }

    private bool IsLogicalOperator(BinaryOperator op)
    {
        return op is BinaryOperator.And or BinaryOperator.Or;
    }

    private void ValidateComparisonOperands(TypeSymbol left, TypeSymbol right, BinaryOperator op, SourceLocation location)
    {
        // Identity operators (===, !==) only work on entity/resident types
        if (op is BinaryOperator.Identical or BinaryOperator.NotIdentical)
        {
            if (left.Category is not (TypeCategory.Entity or TypeCategory.Resident) ||
                right.Category is not (TypeCategory.Entity or TypeCategory.Resident))
            {
                ReportError(
                    message: $"Identity operator '{op.ToStringRepresentation()}' can only be used with entity or resident types, not '{left.Name}' and '{right.Name}'.",
                    location: location);
            }

            return;
        }

        // Variants cannot use equality or ordering operators
        if (left.Category == TypeCategory.Variant || right.Category == TypeCategory.Variant)
        {
            if (op is not (BinaryOperator.Is or BinaryOperator.IsNot))
            {
                ReportError(
                    message: $"Comparison operator '{op.ToStringRepresentation()}' cannot be used with variant types. Use 'is' or 'isnot' for pattern matching.",
                    location: location);
            }

            return;
        }

        // Check that types are compatible (same type or error type)
        if (!IsAssignableTo(source: left, target: right) && !IsAssignableTo(source: right, target: left))
        {
            ReportError(
                message: $"Cannot compare values of incompatible types '{left.Name}' and '{right.Name}'.",
                location: location);
        }

        // For ordering operators, verify the type is comparable (has __cmp__ or individual comparison methods)
        if (op is BinaryOperator.Less or BinaryOperator.LessEqual or BinaryOperator.Greater or BinaryOperator.GreaterEqual)
        {
            if (!SupportsOperator(type: left, op: op))
            {
                ReportError(
                    message: $"Type '{left.Name}' does not support ordering comparisons.",
                    location: location);
            }
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
                    ReportError(
                        message: "Cannot mix ascending (<, <=) and descending (>, >=) operators in a comparison chain.",
                        location: location);
                    return;
                }

                isAscending = true;
            }
            else if (opIsDescending)
            {
                if (isAscending == true)
                {
                    ReportError(
                        message: "Cannot mix ascending (<, <=) and descending (>, >=) operators in a comparison chain.",
                        location: location);
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
        // Range returns the element type
        if (iterableType.Name == "Range")
        {
            return _registry.LookupType(name: "s32") ?? ErrorTypeInfo.Instance;
        }

        // Generic collections return their type argument
        if (iterableType.TypeArguments is { Count: > 0 })
        {
            return iterableType.TypeArguments[0];
        }

        // Text returns letter type
        if (iterableType.Name == "Text")
        {
            return _registry.LookupType(name: "letter") ?? ErrorTypeInfo.Instance;
        }

        // Look for iterate() method
        RoutineInfo? iterator = _registry.LookupRoutine(fullName: $"{iterableType.Name}.iterate");
        if (iterator?.ReturnType?.TypeArguments is { Count: > 0 })
        {
            return iterator.ReturnType.TypeArguments[0];
        }

        ReportError(
            message: $"Type '{iterableType.Name}' is not iterable.",
            location: location);
        return ErrorTypeInfo.Instance;
    }

    private bool ImplementsProtocol(TypeSymbol type, string protocolName)
    {
        // Get the protocol type
        TypeSymbol? protocol = _registry.LookupType(name: protocolName);
        if (protocol == null || protocol.Category != TypeCategory.Protocol)
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

            if (CheckParentProtocols(proto: parent, targetName: targetName))
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

        // Check parameter count (excluding implicit 'me' parameter)
        int expectedParamCount = protoMethod.ParameterTypes.Count;
        int actualParamCount = typeMethod.Parameters.Count;

        // Type methods have 'me' as first parameter if instance method
        if (typeMethod.Kind == RoutineKind.Method && actualParamCount > 0)
        {
            actualParamCount--;
        }

        if (actualParamCount != expectedParamCount)
        {
            return false;
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

    private TypeSymbol CreateViewedType(TypeSymbol innerType)
    {
        TypeSymbol? viewedDef = _registry.LookupType(name: "Viewed");
        if (viewedDef != null)
        {
            return _registry.GetOrCreateInstantiation(genericDef: viewedDef, typeArguments: [innerType]);
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol CreateHijackedType(TypeSymbol innerType)
    {
        TypeSymbol? hijackedDef = _registry.LookupType(name: "Hijacked");
        if (hijackedDef != null)
        {
            return _registry.GetOrCreateInstantiation(genericDef: hijackedDef, typeArguments: [innerType]);
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol CreateInspectedType(TypeSymbol innerType)
    {
        TypeSymbol? inspectedDef = _registry.LookupType(name: "Inspected");
        if (inspectedDef != null)
        {
            return _registry.GetOrCreateInstantiation(genericDef: inspectedDef, typeArguments: [innerType]);
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol CreateSeizedType(TypeSymbol innerType)
    {
        TypeSymbol? seizedDef = _registry.LookupType(name: "Seized");
        if (seizedDef != null)
        {
            return _registry.GetOrCreateInstantiation(genericDef: seizedDef, typeArguments: [innerType]);
        }

        return ErrorTypeInfo.Instance;
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
                message: $"Cannot call writable method '{method.Name}' through read-only wrapper '{wrapperName}<T>'. " +
                $"Only @readonly methods are accessible.",
                location: location);
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
                message: $"Cannot return {GetTokenKindDescription(type: type)} from a routine. Tokens are inline-only and cannot escape their scope.",
                location: location);
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
                message: $"Cannot store {GetTokenKindDescription(type: type)} in field '{fieldName}'. Tokens are inline-only and cannot be stored.",
                location: location);
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
                    message: $"Cannot pass the same {baseName} token '{exprKey}' multiple times in a single call. Exclusive tokens require unique access.",
                    location: location);
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
                message: $"Cannot use {GetTokenKindDescription(type: type)} as payload for variant case '{caseName}'. Tokens are inline-only and cannot be stored in variants.",
                location: location);
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
            VisibilityModifier.Private => 0,
            VisibilityModifier.Internal => 1,
            VisibilityModifier.InternalPrivateSet => 1, // Getter level is internal
            VisibilityModifier.Public => 2,
            VisibilityModifier.PublicInternalSet => 2, // Getter level is public
            VisibilityModifier.PublicPrivateSet => 2, // Getter level is public
            VisibilityModifier.Global => 2,
            VisibilityModifier.Common => 2,
            VisibilityModifier.Imported => 2,
            _ => 2
        };
    }

    /// <summary>
    /// Validates that a field's setter visibility is equal to or more restrictive than its getter visibility.
    /// The 6 valid combinations are:
    /// 1. public (getter: public, setter: public)
    /// 2. public internal(set) (getter: public, setter: internal)
    /// 3. public private(set) (getter: public, setter: private)
    /// 4. internal (getter: internal, setter: internal)
    /// 5. internal private(set) (getter: internal, setter: private)
    /// 6. private (getter: private, setter: private)
    /// </summary>
    /// <param name="getterVisibility">The getter/read visibility.</param>
    /// <param name="setterVisibility">The setter/write visibility (null means same as getter).</param>
    /// <param name="fieldName">The field name for error messages.</param>
    /// <param name="location">Source location for error reporting.</param>
    private void ValidateGetterSetterVisibility(
        VisibilityModifier getterVisibility,
        VisibilityModifier? setterVisibility,
        string fieldName,
        SourceLocation location)
    {
        // If no setter visibility specified, it defaults to getter visibility (valid)
        if (setterVisibility == null)
        {
            return;
        }

        int getterLevel = GetVisibilityLevel(visibility: getterVisibility);
        int setterLevel = GetVisibilityLevel(visibility: setterVisibility.Value);

        // Setter must be equal to or more restrictive than getter
        // More restrictive = lower level number
        if (setterLevel > getterLevel)
        {
            ReportError(
                message: $"Invalid visibility combination for field '{fieldName}': setter visibility ({setterVisibility.Value}) cannot be less restrictive than getter visibility ({getterVisibility}). " +
                         "Valid combinations: public, public internal(set), public private(set), internal, internal private(set), private.",
                location: location);
        }
    }

    #endregion

    #region Access Modifier Enforcement

    /// <summary>
    /// Checks if access to a field is allowed from the current context.
    /// </summary>
    /// <param name="field">The field being accessed.</param>
    /// <param name="isWrite">Whether this is a write access (assignment).</param>
    /// <param name="accessLocation">Source location of the access site.</param>
    private void ValidateFieldAccess(FieldInfo field, bool isWrite, SourceLocation accessLocation)
    {
        VisibilityModifier visibility = isWrite
            ? field.EffectiveSetterVisibility
            : field.Visibility;

        ValidateMemberAccess(
            visibility: visibility,
            memberKind: "field",
            memberName: field.Name,
            ownerType: field.Owner,
            memberLocation: field.Location,
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
            memberLocation: routine.Location,
            accessLocation: accessLocation);
    }

    /// <summary>
    /// Validates access to a member based on visibility rules.
    /// </summary>
    /// <param name="visibility">The visibility modifier of the member.</param>
    /// <param name="memberKind">The kind of member (field, method, etc.) for error messages.</param>
    /// <param name="memberName">The name of the member.</param>
    /// <param name="ownerType">The type that owns this member, if any.</param>
    /// <param name="memberLocation">Source location where the member is defined.</param>
    /// <param name="accessLocation">Source location of the access site.</param>
    private void ValidateMemberAccess(
        VisibilityModifier visibility,
        string memberKind,
        string memberName,
        TypeSymbol? ownerType,
        SourceLocation? memberLocation,
        SourceLocation accessLocation)
    {
        switch (visibility)
        {
            case VisibilityModifier.Private:
                // Private members are accessible within the same file
                if (!IsAccessingFromSameFile(memberLocation: memberLocation, accessLocation: accessLocation))
                {
                    string typeName = ownerType?.Name ?? "type";
                    ReportError(
                        message: $"Cannot access private {memberKind} '{memberName}' of '{typeName}' from outside its defining file.",
                        location: accessLocation);
                }
                break;

            case VisibilityModifier.Internal:
            case VisibilityModifier.InternalPrivateSet:
                // Internal members are accessible within the same namespace
                if (!IsAccessingFromSameNamespace(memberNamespace: ownerType?.Namespace))
                {
                    string typeName = ownerType?.Name ?? "type";
                    ReportError(
                        message: $"Cannot access internal {memberKind} '{memberName}' of '{typeName}' from outside the namespace.",
                        location: accessLocation);
                }
                break;

            case VisibilityModifier.Public:
            case VisibilityModifier.PublicInternalSet:
            case VisibilityModifier.PublicPrivateSet:
            case VisibilityModifier.Global:
            case VisibilityModifier.Common:
            case VisibilityModifier.Imported:
                // Public/Global/Common/Imported members are accessible from anywhere
                break;
        }
    }

    /// <summary>
    /// Checks if the access site is in the same file as where the member is defined.
    /// </summary>
    private static bool IsAccessingFromSameFile(SourceLocation? memberLocation, SourceLocation accessLocation)
    {
        if (memberLocation == null)
        {
            return true; // Unknown location, allow access
        }

        return string.Equals(
            a: memberLocation.FileName,
            b: accessLocation.FileName,
            comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if the current access context is within the same namespace as the member.
    /// Module = Namespace exactly (sub-namespaces are different modules).
    /// </summary>
    private bool IsAccessingFromSameNamespace(string? memberNamespace)
    {
        string? currentNamespace = GetCurrentNamespace();

        // If both are in no namespace, they're in the same "namespace"
        if (string.IsNullOrEmpty(value: memberNamespace) && string.IsNullOrEmpty(value: currentNamespace))
        {
            return true;
        }

        // If either is null/empty but not both, they're not in the same namespace
        if (string.IsNullOrEmpty(value: memberNamespace) || string.IsNullOrEmpty(value: currentNamespace))
        {
            return false;
        }

        // Module = Namespace exactly - sub-namespaces are different modules
        return currentNamespace == memberNamespace;
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
}
