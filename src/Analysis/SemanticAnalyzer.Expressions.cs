namespace SemanticAnalysis;

using Enums;
using Symbols;
using Types;
using SyntaxTree;
using Diagnostics;
using TypeSymbol = Types.TypeInfo;

/// <summary>
/// Phase 3: Expression analysis.
/// </summary>
public sealed partial class SemanticAnalyzer
{
    /// <summary>
    /// Collection types that support literal constructor syntax: TypeName(elem1, elem2, ...)
    /// </summary>
    private static readonly HashSet<string> CollectionLiteralTypes =
        new(comparer: StringComparer.Ordinal)
        {
            "List",
            "Set",
            "Dict",
            "Deque",
            "BitList",
            "SortedSet",
            "SortedList",
            "SortedDict",
            "ValueList",
            "ValueBitList",
            "PriorityQueue"
        };

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
            LiteralExpression literal => AnalyzeLiteralExpression(literal: literal,
                expectedType: expectedType),
            IdentifierExpression id => AnalyzeIdentifierExpression(id: id),
            CompoundAssignmentExpression compound => AnalyzeCompoundAssignment(compound: compound),
            BinaryExpression binary => AnalyzeBinaryExpression(binary: binary),
            UnaryExpression unary => AnalyzeUnaryExpression(unary: unary),
            CallExpression call => AnalyzeCallExpression(call: call),
            MemberExpression member => AnalyzeMemberExpression(member: member),
            OptionalMemberExpression optMember => AnalyzeOptionalMemberExpression(
                optMember: optMember),
            IndexExpression index => AnalyzeIndexExpression(index: index),
            SliceExpression slice => AnalyzeSliceExpression(slice: slice),
            ConditionalExpression cond => AnalyzeConditionalExpression(cond: cond),
            LambdaExpression lambda => AnalyzeLambdaExpression(lambda: lambda),
            RangeExpression range => AnalyzeRangeExpression(range: range),
            CreatorExpression creator => AnalyzeCreatorExpression(creator: creator),
            ListLiteralExpression list => AnalyzeListLiteralExpression(list: list,
                expectedType: expectedType),
            SetLiteralExpression set => AnalyzeSetLiteralExpression(set: set,
                expectedType: expectedType),
            DictLiteralExpression dict => AnalyzeDictLiteralExpression(dict: dict,
                expectedType: expectedType),
            TupleLiteralExpression tuple => AnalyzeTupleLiteralExpression(tuple: tuple,
                expectedType: expectedType),
            TypeConversionExpression conv => AnalyzeTypeConversionExpression(conv: conv),
            ChainedComparisonExpression chain => AnalyzeChainedComparisonExpression(chain: chain),
            BlockExpression block => AnalyzeBlockExpression(block: block),
            WithExpression with => AnalyzeWithExpression(with: with),
            NamedArgumentExpression named => AnalyzeExpression(expression: named.Value),
            DictEntryLiteralExpression dictEntry => AnalyzeDictEntryLiteralExpression(
                dictEntry: dictEntry,
                expectedType: expectedType),
            GenericMethodCallExpression generic => AnalyzeGenericMethodCallExpression(
                generic: generic),
            GenericMemberExpression genericMember => AnalyzeGenericMemberExpression(
                genericMember: genericMember),
            IsPatternExpression isPat => AnalyzeIsPatternExpression(isPat: isPat),
            FlagsTestExpression flagsTest => AnalyzeFlagsTestExpression(flagsTest: flagsTest),
            StealExpression steal => AnalyzeStealExpression(steal: steal),
            BackIndexExpression back => AnalyzeBackIndexExpression(back: back),
            TypeExpression typeExpr => ResolveType(typeExpr: typeExpr),
            WhenExpression whenExpr => AnalyzeWhenExpression(when: whenExpr),
            WaitforExpression waitfor => AnalyzeWaitforExpression(waitfor: waitfor),
            DependentWaitforExpression depWaitfor => AnalyzeDependentWaitforExpression(
                depWaitfor: depWaitfor),
            InsertedTextExpression insertedText => AnalyzeInsertedTextExpression(
                insertedText: insertedText),
            _ => HandleUnknownExpression(expression: expression)
        };

        // Set the resolved type directly (no conversion needed)
        expression.ResolvedType = resultType;
        return resultType;
    }

    private TypeSymbol AnalyzeIdentifierExpression(IdentifierExpression id)
    {
        // Special identifiers
        if (id.Name == "me")
        {
            // First check if we're inside a type body
            if (_currentType != null)
            {
                return _currentType;
            }

            // For extension methods (routine Type.method), check the routine's owner type
            if (_currentRoutine?.OwnerType != null)
            {
                // Generic type parameter owners (e.g., T in "routine T.view()") —
                // return the GenericParameterTypeInfo directly, no registry lookup needed
                if (_currentRoutine.OwnerType is GenericParameterTypeInfo)
                {
                    return _currentRoutine.OwnerType;
                }

                // Re-lookup to get the updated type with resolved protocols/member variables
                TypeSymbol? ownerType = _registry.LookupType(name: _currentRoutine.OwnerType.Name);
                if (ownerType != null)
                {
                    return ownerType;
                }
            }

            ReportError(code: SemanticDiagnosticCode.MeOutsideTypeMethod,
                message: "'me' can only be used inside a type method.",
                location: id.Location);
            return ErrorTypeInfo.Instance;
        }

        if (id.Name == "None")
        {
            // None represents Maybe.None - return a generic Maybe type
            return ErrorHandlingTypeInfo.WellKnown.MaybeDefinition;
        }

        // Try to look up as variable first
        VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
        // Try current module prefix for presets (e.g., "MY_CONST" → "MyModule.MY_CONST")
        if (varInfo == null && _currentModuleName != null && !id.Name.Contains(value: '.'))
        {
            varInfo = _registry.LookupVariable(name: $"{_currentModuleName}.{id.Name}");
        }

        if (varInfo != null)
        {
            // #11: Deadref tracking — report error if variable was invalidated by steal
            if (_deadrefVariables.Contains(item: id.Name))
            {
                ReportError(code: SemanticDiagnosticCode.UseAfterSteal,
                    message:
                    $"Variable '{id.Name}' is a deadref — it was invalidated by a previous 'steal' or ownership transfer. " +
                    "The variable can no longer be used.",
                    location: id.Location);
                return ErrorTypeInfo.Instance;
            }

            // Check for type narrowing (e.g., after "unless x is None")
            TypeSymbol? narrowed = _registry.GetNarrowedType(name: id.Name);
            return narrowed ?? varInfo.Type;
        }

        // Try to look up as choice case (SCREAMING_SNAKE_CASE identifiers like ME_SMALL, SAME)
        (ChoiceTypeInfo ChoiceType, ChoiceCaseInfo CaseInfo)? choiceCase =
            _registry.LookupChoiceCase(caseName: id.Name);
        if (choiceCase.HasValue)
        {
            return choiceCase.Value.ChoiceType;
        }

        // Try to look up as routine (function reference)
        // Strip '!' suffix for failable routine references (e.g., "stop!" → "stop")
        string routineLookupName = id.Name.EndsWith(value: '!')
            ? id.Name[..^1]
            : id.Name;
        RoutineInfo? routine = _registry.LookupRoutine(fullName: routineLookupName);
        // Try current module prefix (e.g., "infinite_loop" → "HelloWorld.infinite_loop")
        if (routine == null && _currentModuleName != null &&
            !routineLookupName.Contains(value: '.'))
        {
            routine = _registry.LookupRoutine(
                fullName: $"{_currentModuleName}.{routineLookupName}");
        }

        if (routine != null)
        {
            // Return the function type for first-class function references
            return GetRoutineType(routine: routine);
        }

        // Try to look up as type (for static access)
        TypeSymbol? type = LookupTypeWithImports(name: id.Name);
        if (type != null)
        {
            return type;
        }

        ReportError(code: SemanticDiagnosticCode.UnknownIdentifier,
            message: $"Unknown identifier '{id.Name}'.",
            location: id.Location);
        return ErrorTypeInfo.Instance;
    }

    /// <summary>
    /// Analyzes binary expressions that remain as BinaryExpression nodes after parsing.
    /// Note: Most arithmetic, comparison, and bitwise operators are desugared to method calls
    /// in the parser (e.g., a + b → a.$add(b)). This method only handles operators that
    /// are NOT desugared:
    /// - Assignment (=)
    /// - Logical operators (and, or) — require short-circuit evaluation
    /// - Identity operators (===, !==)
    /// - Membership/type operators (in, notin, is, isnot, obeys, disobeys)
    /// - None coalescing (??) — requires short-circuit evaluation
    /// </summary>
    private TypeSymbol AnalyzeBinaryExpression(BinaryExpression binary)
    {
        TypeSymbol leftType = AnalyzeExpression(expression: binary.Left);
        TypeSymbol rightType = AnalyzeExpression(expression: binary.Right);

        // Handle assignment operator
        if (binary.Operator == BinaryOperator.Assign)
        {
            return AnalyzeAssignmentExpression(target: binary.Left,
                value: binary.Right,
                targetType: leftType,
                valueType: rightType,
                location: binary.Location);
        }

        // Handle flags removal operator (but) — removes flags from a value
        if (binary.Operator == BinaryOperator.But)
        {
            if (leftType is not FlagsTypeInfo)
            {
                ReportError(code: SemanticDiagnosticCode.FlagsTypeMismatch,
                    message:
                    $"'but' operator requires a flags type on the left side, but got '{leftType.Name}'.",
                    location: binary.Location);
                return ErrorTypeInfo.Instance;
            }

            if (rightType is not FlagsTypeInfo)
            {
                ReportError(code: SemanticDiagnosticCode.FlagsTypeMismatch,
                    message:
                    $"'but' operator requires a flags type on the right side, but got '{rightType.Name}'.",
                    location: binary.Location);
                return ErrorTypeInfo.Instance;
            }

            if (leftType.Name != rightType.Name)
            {
                ReportError(code: SemanticDiagnosticCode.FlagsTypeMismatch,
                    message:
                    $"'but' operator requires both operands to be the same flags type, but got '{leftType.Name}' and '{rightType.Name}'.",
                    location: binary.Location);
                return ErrorTypeInfo.Instance;
            }

            return leftType;
        }

        // #128: 'or' cannot be used to combine flags outside of is/isnot/isonly tests
        if (binary.Operator == BinaryOperator.Or &&
            (leftType is FlagsTypeInfo || rightType is FlagsTypeInfo))
        {
            ReportError(code: SemanticDiagnosticCode.FlagsOrInAssignment,
                message:
                "Cannot use 'or' to combine flags values. Use 'is FLAG_A or FLAG_B' for testing, " +
                "or separate flag assignments.",
                location: binary.Location);
            return leftType;
        }

        // Check for operator prohibitions on choice and flags types
        // Choices do not support ANY overloadable operators — use 'is' for case matching
        // Flags do not support arithmetic/comparison/bitwise operators — use 'is'/'isnot'/'but'
        {
            string? operatorMethod = binary.Operator.GetMethodName();
            if (operatorMethod != null)
            {
                if (leftType is ChoiceTypeInfo)
                {
                    ReportError(code: SemanticDiagnosticCode.ArithmeticOnChoiceType,
                        message:
                        $"Operator '{binary.Operator.ToStringRepresentation()}' cannot be used with choice type '{leftType.Name}'. Use 'is' for case matching.",
                        location: binary.Location);
                    return ErrorTypeInfo.Instance;
                }

                if (leftType is FlagsTypeInfo)
                {
                    ReportError(code: SemanticDiagnosticCode.ArithmeticOnFlagsType,
                        message:
                        $"Operator '{binary.Operator.ToStringRepresentation()}' cannot be used with flags type '{leftType.Name}'. Use 'is'/'isnot'/'but' for flag operations.",
                        location: binary.Location);
                    return ErrorTypeInfo.Instance;
                }
            }
        }

        // #117: Fixed-width numeric types must match exactly (S32 + S64 = error)
        // System types (Address) are exempt
        if (leftType.Name != rightType.Name && IsFixedWidthNumericType(type: leftType) &&
            IsFixedWidthNumericType(type: rightType) && !IsLogicalOperator(op: binary.Operator) &&
            !IsComparisonOperator(op: binary.Operator))
        {
            ReportError(code: SemanticDiagnosticCode.FixedWidthTypeMismatch,
                message:
                $"Fixed-width type mismatch: '{leftType.Name}' and '{rightType.Name}'. Explicit conversion required.",
                location: binary.Location);
            return ErrorTypeInfo.Instance;
        }

        // Flags combination: A and B → bitwise OR (combines flags)
        if (binary.Operator == BinaryOperator.And && leftType is FlagsTypeInfo &&
            leftType.Name == rightType.Name)
        {
            return leftType;
        }

        // Handle logical operators (and, or) — require bool operands, return bool
        // These are not desugared because they need short-circuit evaluation
        if (IsLogicalOperator(op: binary.Operator))
        {
            if (!IsBoolType(type: leftType) || !IsBoolType(type: rightType))
            {
                ReportError(code: SemanticDiagnosticCode.LogicalOperatorRequiresBool,
                    message:
                    $"Logical operator '{binary.Operator.ToStringRepresentation()}' requires boolean operands.",
                    location: binary.Location);
            }

            return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;
        }

        // Handle comparison operators — all return Bool
        // Includes overloadable (==, !=, <, <=, >, >=, in, notin) and non-overloadable (===, !==, is, isnot, obeys, disobeys)
        if (IsComparisonOperator(op: binary.Operator))
        {
            ValidateComparisonOperands(left: leftType,
                right: rightType,
                op: binary.Operator,
                location: binary.Location);
            return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;
        }

        // Handle none coalescing operator (??)
        // Not desugared because it needs short-circuit evaluation for built-in types
        if (binary.Operator == BinaryOperator.NoneCoalesce)
        {
            if (leftType is ErrorHandlingTypeInfo coalesceError)
            {
                return coalesceError.ValueType;
            }

            // User type — look up $unwrap_or method
            RoutineInfo? unwrapOrMethod =
                _registry.LookupMethod(type: leftType, methodName: "$unwrap_or");
            if (unwrapOrMethod != null)
            {
                return unwrapOrMethod.ReturnType ?? rightType;
            }

            ReportError(code: SemanticDiagnosticCode.TypeDoesNotSupportOperator,
                message:
                $"Type '{leftType.Name}' does not support the '??' operator. " +
                "Implement '$unwrap_or(default: T) -> T' to enable none coalescing.",
                location: binary.Location);
            return ErrorTypeInfo.Instance;
        }

        // Validate RHS type against the operator method's parameter type
        {
            string? methodName = binary.Operator.GetMethodName();
            if (methodName != null)
            {
                RoutineInfo? method =
                    _registry.LookupMethodOverload(type: leftType,
                        methodName: methodName,
                        argTypes: [rightType]) ??
                    _registry.LookupMethod(type: leftType, methodName: methodName);
                if (method is { Parameters.Count: > 0 })
                {
                    TypeSymbol paramType = method.Parameters[index: 0].Type;

                    // Substitute Me → leftType for protocol-sourced methods
                    if (paramType is ProtocolSelfTypeInfo)
                    {
                        paramType = leftType;
                    }

                    if (!IsAssignableTo(source: rightType, target: paramType))
                    {
                        ReportError(code: SemanticDiagnosticCode.ArgumentTypeMismatch,
                            message:
                            $"Operator '{binary.Operator.ToStringRepresentation()}': cannot convert '{rightType.Name}' to '{paramType.Name}'.",
                            location: binary.Location);
                        return ErrorTypeInfo.Instance;
                    }

                    // Return the method's actual return type instead of blindly returning leftType
                    TypeSymbol returnType = method.ReturnType ?? leftType;
                    if (returnType is ProtocolSelfTypeInfo)
                    {
                        returnType = leftType;
                    }

                    return returnType;
                }
            }
        }

        // Default: return left type
        // This handles any edge cases that might slip through
        return leftType;
    }

    /// <summary>
    /// Analyzes an assignment expression (target = value).
    /// Validates mutability, member variable access, and type compatibility.
    /// </summary>
    /// <param name="target">The assignment target expression.</param>
    /// <param name="value">The value being assigned.</param>
    /// <param name="targetType">The resolved type of the target.</param>
    /// <param name="valueType">The resolved type of the value.</param>
    /// <param name="location">Source location for error reporting.</param>
    /// <returns>The type of the assignment expression (same as target type).</returns>
    private TypeSymbol AnalyzeAssignmentExpression(Expression target, Expression value,
        TypeSymbol targetType, TypeSymbol valueType, SourceLocation location)
    {
        // #173: Tuple assignment destructuring — (a, b) = (b, a)
        if (target is TupleLiteralExpression tupleLhs)
        {
            // Verify all elements of the LHS tuple are assignable targets
            foreach (Expression element in tupleLhs.Elements)
            {
                if (!IsAssignableTarget(target: element))
                {
                    ReportError(code: SemanticDiagnosticCode.InvalidAssignmentTarget,
                        message:
                        "All elements of tuple destructuring must be assignable targets (variables, member accesses, or indices).",
                        location: element.Location);
                }

                // Check modifiability for identifier elements
                if (element is IdentifierExpression elemId)
                {
                    VariableInfo? varInfo = _registry.LookupVariable(name: elemId.Name);
                    if (varInfo is { IsModifiable: false })
                    {
                        ReportError(code: SemanticDiagnosticCode.AssignmentToImmutable,
                            message: $"Cannot assign to preset variable '{elemId.Name}'.",
                            location: location);
                    }
                }
            }

            // Check that RHS is a tuple with matching arity
            if (valueType is TupleTypeInfo tupleType)
            {
                if (tupleLhs.Elements.Count != tupleType.ElementTypes.Count)
                {
                    ReportError(code: SemanticDiagnosticCode.DestructuringArityMismatch,
                        message:
                        $"Tuple destructuring has {tupleLhs.Elements.Count} targets but the value has {tupleType.ElementTypes.Count} elements.",
                        location: location);
                }
            }

            return targetType;
        }

        // Check if target is assignable (variable, member variable, or index)
        if (!IsAssignableTarget(target: target))
        {
            ReportError(code: SemanticDiagnosticCode.InvalidAssignmentTarget,
                message: "Invalid assignment target.",
                location: target.Location);
            return targetType;
        }

        // Check modifiability for variable assignments
        if (target is IdentifierExpression id)
        {
            VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
            if (varInfo is { IsModifiable: false })
            {
                ReportError(code: SemanticDiagnosticCode.AssignmentToImmutable,
                    message: $"Cannot assign to preset variable '{id.Name}'.",
                    location: location);
            }
        }

        // Validate member variable write access (setter visibility)
        if (target is MemberExpression member)
        {
            TypeSymbol objectType = AnalyzeExpression(expression: member.Object);

            // Read-only wrapper types (Viewed, Inspected) cannot be written through
            if (IsReadOnlyWrapper(type: objectType))
            {
                ReportError(code: SemanticDiagnosticCode.WriteThroughReadOnlyWrapper,
                    message:
                    $"Cannot write to member '{member.PropertyName}' through read-only wrapper '{objectType.Name}'. " +
                    "Use Hijacked[T] for exclusive write access or Seized[T] for locked write access.",
                    location: location);
            }

            ValidateMemberVariableWriteAccess(objectType: objectType,
                memberVariableName: member.PropertyName,
                location: location);

            // Check if we're in a @readonly method trying to modify 'me'
            if (_currentRoutine is { IsReadOnly: true } &&
                member.Object is IdentifierExpression { Name: "me" })
            {
                ReportError(code: SemanticDiagnosticCode.ModificationInReadonlyMethod,
                    message:
                    $"Cannot modify member variable '{member.PropertyName}' in a @readonly method. " +
                    "Use @writable or @migratable to allow modifications.",
                    location: location);
            }
        }

        // Check modifiability for index assignments
        if (target is IndexExpression index)
        {
            // The object being indexed must be modifiable
            if (index.Object is IdentifierExpression indexedVar)
            {
                VariableInfo? varInfo = _registry.LookupVariable(name: indexedVar.Name);
                if (varInfo is { IsModifiable: false })
                {
                    ReportError(code: SemanticDiagnosticCode.AssignmentToImmutable,
                        message: $"Cannot assign to index of preset variable '{indexedVar.Name}'.",
                        location: location);
                }
            }
        }

        // RazorForge: Entity bare assignment prohibition
        // `b = a` where `a` is a bare identifier of entity type is a build error
        if (_registry.Language == Language.RazorForge && value is IdentifierExpression &&
            valueType is EntityTypeInfo)
        {
            ReportError(code: SemanticDiagnosticCode.BareEntityAssignment,
                message: $"Cannot directly assign entity of type '{valueType.Name}'. " +
                         "Use '.share()' for shared ownership or 'steal' for ownership transfer.",
                location: location);
        }

        // Check type compatibility
        if (!IsAssignableTo(source: valueType, target: targetType))
        {
            ReportError(code: SemanticDiagnosticCode.AssignmentTypeMismatch,
                message:
                $"Cannot assign value of type '{valueType.Name}' to target of type '{targetType.Name}'.",
                location: location);
        }

        // Variant reassignment prohibition: variants cannot be reassigned
        // Variants must be dismantled immediately with pattern matching
        if (valueType is VariantTypeInfo)
        {
            ReportError(code: SemanticDiagnosticCode.VariantReassignmentNotAllowed,
                message: $"Variant type '{valueType.Name}' cannot be reassigned. " +
                         "Variants must be dismantled immediately with pattern matching.",
                location: location);
        }

        // #42: ??= narrowing — `a ??= b` is expanded to `a = a ?? b`
        // When assigning `target = target ?? default` where target is Maybe[T],
        // narrow the variable to T after the coalescing assignment.
        if (target is IdentifierExpression narrowId &&
            value is BinaryExpression { Operator: BinaryOperator.NoneCoalesce } &&
            targetType is ErrorHandlingTypeInfo { Kind: ErrorHandlingKind.Maybe } maybeType)
        {
            _registry.NarrowVariable(name: narrowId.Name, narrowedType: maybeType.ValueType);
        }

        // Assignment expression returns the target type
        return targetType;
    }

    /// <summary>
    /// Analyzes a compound assignment expression (e.g., a += b).
    /// Dispatch order: (0) verify target is var, (1) try in-place wired ($iadd) → Blank,
    /// (2) fallback to create-and-assign ($add) for non-entity types, (3) error if neither.
    /// </summary>
    private TypeSymbol AnalyzeCompoundAssignment(CompoundAssignmentExpression compound)
    {
        TypeSymbol targetType = AnalyzeExpression(expression: compound.Target);
        TypeSymbol valueType = AnalyzeExpression(expression: compound.Value);

        // Step 0: Verify target is assignable and modifiable
        if (!IsAssignableTarget(target: compound.Target))
        {
            ReportError(code: SemanticDiagnosticCode.InvalidAssignmentTarget,
                message: "Invalid compound assignment target.",
                location: compound.Target.Location);
            return targetType;
        }

        if (compound.Target is IdentifierExpression id)
        {
            VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
            if (varInfo is { IsModifiable: false })
            {
                ReportError(code: SemanticDiagnosticCode.AssignmentToImmutable,
                    message: $"Cannot assign to preset variable '{id.Name}'.",
                    location: compound.Location);
            }
        }

        if (compound.Target is MemberExpression member)
        {
            TypeSymbol objectType = AnalyzeExpression(expression: member.Object);
            ValidateMemberVariableWriteAccess(objectType: objectType,
                memberVariableName: member.PropertyName,
                location: compound.Location);

            if (_currentRoutine is { IsReadOnly: true } &&
                member.Object is IdentifierExpression { Name: "me" })
            {
                ReportError(code: SemanticDiagnosticCode.ModificationInReadonlyMethod,
                    message:
                    $"Cannot modify member variable '{member.PropertyName}' in a @readonly method. " +
                    "Use @writable or @migratable to allow modifications.",
                    location: compound.Location);
            }
        }

        if (compound.Target is IndexExpression index)
        {
            if (index.Object is IdentifierExpression indexedVar)
            {
                VariableInfo? varInfo = _registry.LookupVariable(name: indexedVar.Name);
                if (varInfo is { IsModifiable: false })
                {
                    ReportError(code: SemanticDiagnosticCode.AssignmentToImmutable,
                        message: $"Cannot assign to index of preset variable '{indexedVar.Name}'.",
                        location: compound.Location);
                }
            }
        }

        // #67: Cannot use compound assignment on read-only token (Viewed or Inspected)
        if (targetType is WrapperTypeInfo { IsReadOnly: true } readOnlyWrapper)
        {
            ReportError(code: SemanticDiagnosticCode.CompoundAssignmentOnReadOnlyToken,
                message:
                $"Cannot use compound assignment on read-only token '{readOnlyWrapper.Name}'. " +
                "Read-only tokens (Viewed, Inspected) do not allow modifications.",
                location: compound.Location);
            return ErrorTypeInfo.Instance;
        }

        // Don't try dispatch on error types (prevent cascade)
        if (targetType.Category == TypeCategory.Error)
        {
            return targetType;
        }

        // Choice types cannot use compound assignment — choices do not support operators
        if (targetType is ChoiceTypeInfo)
        {
            ReportError(code: SemanticDiagnosticCode.ArithmeticOnChoiceType,
                message:
                $"Operator '{compound.Operator.ToStringRepresentation()}=' cannot be used with choice type '{targetType.Name}'. " +
                "Choice types do not support operators. Use 'is' for case matching.",
                location: compound.Location);
            return ErrorTypeInfo.Instance;
        }

        // #134: Flags types cannot use arithmetic or compound assignment operators
        if (targetType is FlagsTypeInfo)
        {
            ReportError(code: SemanticDiagnosticCode.ArithmeticOnFlagsType,
                message:
                $"Operator '{compound.Operator.ToStringRepresentation()}=' cannot be used with flags type '{targetType.Name}'. " +
                "Use 'but' to remove flags and 'is'/'isnot'/'isonly' to test flags.",
                location: compound.Location);
            return ErrorTypeInfo.Instance;
        }

        string? inPlaceMethod = compound.Operator.GetInPlaceMethodName();
        string? regularMethod = compound.Operator.GetMethodName();
        bool isEntity = targetType is EntityTypeInfo;

        // Step 1: Try in-place wired ($iadd, etc.)
        if (inPlaceMethod != null)
        {
            RoutineInfo? inPlaceRoutine =
                _registry.LookupRoutine(fullName: $"{targetType.Name}.{inPlaceMethod}");
            if (inPlaceRoutine != null)
            {
                // In-place method found — returns Blank (modifies in-place)
                return _registry.LookupType(name: "Blank") ?? ErrorTypeInfo.Instance;
            }
        }

        // Step 2: Fallback to create-and-assign (NOT for entities — bare assignment prohibited)
        if (!isEntity && regularMethod != null)
        {
            RoutineInfo? regularRoutine =
                _registry.LookupRoutine(fullName: $"{targetType.Name}.{regularMethod}");
            if (regularRoutine != null)
            {
                // Create-and-assign: a = a.$add(b) — returns target type
                TypeSymbol returnType = regularRoutine.ReturnType ?? targetType;
                if (!IsAssignableTo(source: returnType, target: targetType))
                {
                    ReportError(code: SemanticDiagnosticCode.AssignmentTypeMismatch,
                        message:
                        $"Compound assignment: return type '{returnType.Name}' of '{regularMethod}' " +
                        $"is not assignable to target type '{targetType.Name}'.",
                        location: compound.Location);
                }

                return targetType;
            }
        }

        // Step 3: Error — neither in-place nor fallback available
        string opSymbol = compound.Operator.ToStringRepresentation();
        if (isEntity)
        {
            ReportError(code: SemanticDiagnosticCode.CompoundAssignmentNotSupported,
                message:
                $"Entity type '{targetType.Name}' requires in-place operator '{inPlaceMethod}' for " +
                $"compound assignment '{opSymbol}='. Define '{inPlaceMethod}' or use explicit method calls.",
                location: compound.Location);
        }
        else
        {
            ReportError(code: SemanticDiagnosticCode.CompoundAssignmentNotSupported,
                message:
                $"Type '{targetType.Name}' does not support compound assignment '{opSymbol}='. " +
                $"Define '{inPlaceMethod}' or '{regularMethod}' to enable this operation.",
                location: compound.Location);
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeUnaryExpression(UnaryExpression unary)
    {
        TypeSymbol operandType = AnalyzeExpression(expression: unary.Operand);

        switch (unary.Operator)
        {
            case UnaryOperator.Not:
                if (!IsBoolType(type: operandType))
                {
                    ReportError(code: SemanticDiagnosticCode.LogicalNotRequiresBool,
                        message: "Logical 'not' operator requires a boolean operand.",
                        location: unary.Location);
                }

                return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;

            case UnaryOperator.Minus:
                if (!IsNumericType(type: operandType))
                {
                    ReportError(code: SemanticDiagnosticCode.NegationRequiresNumeric,
                        message: "Negation operator requires a numeric operand.",
                        location: unary.Location);
                }

                return operandType;

            case UnaryOperator.BitwiseNot:
                if (!IsIntegerType(type: operandType))
                {
                    ReportError(code: SemanticDiagnosticCode.BitwiseNotRequiresInteger,
                        message: "Bitwise 'not' operator requires an integer operand.",
                        location: unary.Location);
                }

                return operandType;

            case UnaryOperator.ForceUnwrap:
                if (operandType is ErrorHandlingTypeInfo forceUnwrapError)
                {
                    return forceUnwrapError.ValueType;
                }

                // User type — look up $unwrap method
                {
                    RoutineInfo? unwrapMethod =
                        _registry.LookupMethod(type: operandType, methodName: "$unwrap");
                    if (unwrapMethod != null)
                    {
                        return unwrapMethod.ReturnType ?? ErrorTypeInfo.Instance;
                    }

                    ReportError(code: SemanticDiagnosticCode.TypeDoesNotSupportOperator,
                        message:
                        $"Type '{operandType.Name}' does not support the '!!' operator. " +
                        "Implement '$unwrap() -> T' to enable force unwrap.",
                        location: unary.Location);
                    return ErrorTypeInfo.Instance;
                }

            default:
                return operandType;
        }
    }

    #endregion
}
