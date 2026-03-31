namespace SemanticAnalysis;

using Enums;
using Results;
using Native;
using Symbols;
using Types;
using Compiler.Lexer;
using SyntaxTree;
using Diagnostics;
using TypeSymbol = Types.TypeInfo;

public sealed partial class SemanticAnalyzer
{
    private TypeSymbol AnalyzeListLiteralExpression(ListLiteralExpression list,
        TypeSymbol? expectedType = null)
    {
        // Extract expected element type from List[X] expected type
        TypeSymbol? expectedElementType = null;
        if (expectedType is { IsGenericResolution: true, TypeArguments.Count: 1 } &&
            expectedType.Name.StartsWith(value: "List["))
        {
            expectedElementType = expectedType.TypeArguments![index: 0];
        }

        TypeSymbol? elementType = null;

        if (list.ElementType != null)
        {
            elementType = ResolveType(typeExpr: list.ElementType);
        }
        else if (list.Elements.Count > 0)
        {
            // Infer from first element, propagating expected element type
            elementType = AnalyzeExpression(expression: list.Elements[index: 0],
                expectedType: expectedElementType);

            // Validate all elements have compatible types
            // Use inferred element type as context for subsequent elements (e.g., [] in [[1,2], []])
            TypeSymbol elemExpected = expectedElementType ?? elementType;
            for (int i = 1; i < list.Elements.Count; i++)
            {
                TypeSymbol elemType = AnalyzeExpression(expression: list.Elements[index: i],
                    expectedType: elemExpected);
                if (!IsAssignableTo(source: elemType, target: elementType))
                {
                    ReportError(code: SemanticDiagnosticCode.ListElementTypeMismatch,
                        message:
                        $"List element type mismatch: expected '{elementType.Name}', got '{elemType.Name}'.",
                        location: list.Elements[index: i].Location);
                }
            }
        }
        else if (expectedElementType != null)
        {
            // Empty list with expected type from context — use it
            elementType = expectedElementType;
        }
        else
        {
            ReportError(code: SemanticDiagnosticCode.EmptyListNoTypeAnnotation,
                message:
                "Cannot infer element type from empty list literal without type annotation.",
                location: list.Location);
            elementType = ErrorTypeInfo.Instance;
        }

        // Return List<T> type
        TypeSymbol? listDef = _registry.LookupType(name: "List");
        if (listDef != null && elementType != null)
        {
            return _registry.GetOrCreateResolution(genericDef: listDef,
                typeArguments: [elementType]);
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeSetLiteralExpression(SetLiteralExpression set,
        TypeSymbol? expectedType = null)
    {
        // Extract expected element type from Set[X] expected type
        TypeSymbol? expectedElementType = null;
        if (expectedType is { IsGenericResolution: true, TypeArguments.Count: 1 } &&
            expectedType.Name.StartsWith(value: "Set["))
        {
            expectedElementType = expectedType.TypeArguments![index: 0];
        }

        TypeSymbol? elementType = null;

        if (set.ElementType != null)
        {
            elementType = ResolveType(typeExpr: set.ElementType);
        }
        else if (set.Elements.Count > 0)
        {
            elementType = AnalyzeExpression(expression: set.Elements[index: 0],
                expectedType: expectedElementType);
        }
        else if (expectedElementType != null)
        {
            // Empty set with expected type from context — use it
            elementType = expectedElementType;
        }
        else
        {
            ReportError(code: SemanticDiagnosticCode.EmptySetNoTypeAnnotation,
                message:
                "Cannot infer element type from empty set literal without type annotation.",
                location: set.Location);
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
            return _registry.GetOrCreateResolution(genericDef: setDef,
                typeArguments: [elementType]);
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeDictLiteralExpression(DictLiteralExpression dict,
        TypeSymbol? expectedType = null)
    {
        // Extract expected key/value types from Dict[K, V] expected type
        TypeSymbol? expectedKeyType = null;
        TypeSymbol? expectedValueType = null;
        if (expectedType is { IsGenericResolution: true, TypeArguments.Count: 2 } &&
            expectedType.Name.StartsWith(value: "Dict["))
        {
            expectedKeyType = expectedType.TypeArguments![index: 0];
            expectedValueType = expectedType.TypeArguments![index: 1];
        }

        TypeSymbol? keyType = null;
        TypeSymbol? valueType = null;

        if (dict is { KeyType: not null, ValueType: not null })
        {
            keyType = ResolveType(typeExpr: dict.KeyType);
            valueType = ResolveType(typeExpr: dict.ValueType);
        }
        else if (dict.Pairs.Count > 0)
        {
            keyType = AnalyzeExpression(expression: dict.Pairs[index: 0].Key,
                expectedType: expectedKeyType);
            valueType = AnalyzeExpression(expression: dict.Pairs[index: 0].Value,
                expectedType: expectedValueType);
        }
        else if (expectedKeyType != null && expectedValueType != null)
        {
            // Empty dict with expected types from context — use them
            keyType = expectedKeyType;
            valueType = expectedValueType;
        }
        else
        {
            ReportError(code: SemanticDiagnosticCode.EmptyDictNoTypeAnnotation,
                message: "Cannot infer types from empty dict literal without type annotation.",
                location: dict.Location);
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
            return _registry.GetOrCreateResolution(genericDef: dictDef,
                typeArguments: [keyType, valueType]);
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeDictEntryLiteralExpression(DictEntryLiteralExpression dictEntry,
        TypeSymbol? expectedType = null)
    {
        // Extract expected key/value types from tuple expected type (used by collection constructors)
        TypeSymbol? expectedKeyType = null;
        TypeSymbol? expectedValueType = null;
        if (expectedType is TupleTypeInfo { ElementTypes.Count: 2 } expectedTuple)
        {
            expectedKeyType = expectedTuple.ElementTypes[index: 0];
            expectedValueType = expectedTuple.ElementTypes[index: 1];
        }

        TypeSymbol keyType =
            AnalyzeExpression(expression: dictEntry.Key, expectedType: expectedKeyType);
        TypeSymbol valueType =
            AnalyzeExpression(expression: dictEntry.Value, expectedType: expectedValueType);

        // Resolve to DictEntry[K, V]
        TypeSymbol? dictEntryDef = _registry.LookupType(name: "DictEntry");
        if (dictEntryDef != null)
        {
            return _registry.GetOrCreateResolution(genericDef: dictEntryDef,
                typeArguments: [keyType, valueType]);
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeTupleLiteralExpression(TupleLiteralExpression tuple,
        TypeSymbol? expectedType = null)
    {
        // Extract per-element expected types from tuple expected type
        IReadOnlyList<TypeInfo>? expectedElementTypes = null;
        if (expectedType is TupleTypeInfo expectedTuple &&
            expectedTuple.ElementTypes.Count == tuple.Elements.Count)
        {
            expectedElementTypes = expectedTuple.ElementTypes;
        }

        // Analyze all element expressions
        var elementTypes = new List<TypeSymbol>();
        for (int i = 0; i < tuple.Elements.Count; i++)
        {
            TypeSymbol? elemExpected = expectedElementTypes?[index: i];
            TypeSymbol elementType = AnalyzeExpression(expression: tuple.Elements[index: i],
                expectedType: elemExpected);
            elementTypes.Add(item: elementType);
        }

        // Empty tuples are not allowed - use Blank instead
        if (elementTypes.Count == 0)
        {
            ReportError(code: SemanticDiagnosticCode.UnknownType,
                message: "Empty tuples are not allowed. Use 'Blank' for the unit type.",
                location: tuple.Location);
            return ErrorTypeInfo.Instance;
        }

        return _registry.GetOrCreateTupleType(elementTypes: elementTypes);
    }

    private TypeSymbol AnalyzeTypeConversionExpression(TypeConversionExpression conv)
    {
        AnalyzeExpression(expression: conv.Expression);

        TypeSymbol? targetType = LookupTypeWithImports(name: conv.TargetType);
        if (targetType == null)
        {
            ReportError(code: SemanticDiagnosticCode.UnknownConversionTargetType,
                message: $"Unknown conversion target type '{conv.TargetType}'.",
                location: conv.Location);
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
            ValidateComparisonOperands(left: operandTypes[index: i],
                right: operandTypes[index: i + 1],
                op: chain.Operators[index: i],
                location: chain.Location);
        }

        // Chained comparisons always return bool
        return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeBlockExpression(BlockExpression block)
    {
        // Block expression evaluates to its contained value expression
        return AnalyzeExpression(expression: block.Value);
    }

    private TypeSymbol AnalyzeWithExpression(WithExpression with)
    {
        TypeSymbol baseType = AnalyzeExpression(expression: with.Base);

        // 'with' expressions are only valid on record types
        if (baseType.Category != TypeCategory.Record)
        {
            ReportError(code: SemanticDiagnosticCode.WithExpressionNotRecord,
                message: $"'with' expression requires a record type, got '{baseType.Name}'.",
                location: with.Location);
        }

        // Analyze update expressions
        foreach ((List<string>? fieldPath, Expression? index, Expression value) in with.Updates)
        {
            // Analyze index expression if present
            if (index != null)
            {
                AnalyzeExpression(expression: index);
            }

            AnalyzeExpression(expression: value);

            // #45: Cannot modify secret member variables in 'with' expression
            if (fieldPath is { Count: > 0 } && baseType is RecordTypeInfo recordType)
            {
                MemberVariableInfo? memberInfo =
                    recordType.LookupMemberVariable(memberVariableName: fieldPath[index: 0]);
                if (memberInfo is { Visibility: VisibilityModifier.Secret })
                {
                    ReportError(code: SemanticDiagnosticCode.WithSecretMemberProhibited,
                        message:
                        $"Cannot modify secret member variable '{fieldPath[index: 0]}' in 'with' expression.",
                        location: with.Location);
                }
            }
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
        // Analyze the matched expression (Bool for subject-less when — arms are conditions)
        TypeSymbol matchedType = when.Expression != null
            ? AnalyzeExpression(expression: when.Expression)
            : _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;

        // #88: Pattern order enforcement — else/wildcard must be last
        {
            bool seenElse = false;
            foreach (WhenClause clause in when.Clauses)
            {
                if (seenElse)
                {
                    ReportError(code: SemanticDiagnosticCode.PatternOrderViolation,
                        message: "Unreachable pattern after 'else' or wildcard.",
                        location: clause.Pattern.Location);
                }

                if (clause.Pattern is ElsePattern or WildcardPattern)
                {
                    seenElse = true;
                }
            }
        }

        // #130/#148: Duplicate pattern detection
        {
            var seenPatterns = new HashSet<string>();
            foreach (WhenClause clause in when.Clauses)
            {
                string? patternKey = GetPatternKey(pattern: clause.Pattern);
                if (patternKey != null && !seenPatterns.Add(item: patternKey))
                {
                    ReportError(code: SemanticDiagnosticCode.DuplicatePattern,
                        message: $"Duplicate pattern: {patternKey}.",
                        location: clause.Pattern.Location);
                }
            }
        }

        TypeSymbol? resultType = null;
        bool hasElse = false;

        foreach (WhenClause clause in when.Clauses)
        {
            _registry.EnterScope(kind: ScopeKind.Block, name: "when_clause");

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
                    ReportError(code: SemanticDiagnosticCode.WhenBranchTypeMismatch,
                        message:
                        $"When expression branches have incompatible types: '{resultType.Name}' and '{branchType.Name}'.",
                        location: clause.Body.Location);
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
                // For block statements in when expressions, we need to validate 'becomes' usage
                // and extract the result type from the becomes statement
                BecomesStatement? becomesStmt = null;
                int statementCount = 0;

                foreach (Statement stmt in block.Statements)
                {
                    AnalyzeStatement(statement: stmt);
                    statementCount++;

                    if (stmt is BecomesStatement becomes)
                    {
                        becomesStmt = becomes;
                    }
                }

                if (becomesStmt != null)
                {
                    // Found a becomes statement - check if it's a single-statement block
                    if (statementCount == 1)
                    {
                        // Block contains only 'becomes expr' - should use => syntax instead
                        ReportError(code: SemanticDiagnosticCode.SingleExpressionBranchUsesBecomes,
                            message:
                            "Single-expression when branch should use '=>' syntax instead of block with 'becomes'.",
                            location: becomesStmt.Location);
                    }

                    // Extract the result type from the becomes expression (already analyzed via AnalyzeStatement)
                    TypeSymbol branchType =
                        becomesStmt.Value.ResolvedType ?? ErrorTypeInfo.Instance;

                    if (resultType == null)
                    {
                        resultType = branchType;
                    }
                    else if (!IsAssignableTo(source: branchType, target: resultType))
                    {
                        ReportError(code: SemanticDiagnosticCode.WhenBranchTypeMismatch,
                            message:
                            $"When expression branches have incompatible types: '{resultType.Name}' and '{branchType.Name}'.",
                            location: becomesStmt.Location);
                    }
                }
                else if (statementCount > 0)
                {
                    // Multi-statement block without 'becomes' in a when expression
                    ReportError(code: SemanticDiagnosticCode.WhenExpressionBlockMissingBecomes,
                        message:
                        "Multi-statement block in when expression requires 'becomes' to specify the result value.",
                        location: block.Location);
                }
            }
            else
            {
                // Analyze as regular statement
                AnalyzeStatement(statement: clause.Body);
            }

            _registry.ExitScope();
        }

        // Check exhaustiveness — when expressions MUST produce a value for all inputs
        if (!hasElse)
        {
            ExhaustivenessResult exhaustiveness = CheckExhaustiveness(
                clauses: when.Clauses,
                matchedType: matchedType);

            if (!exhaustiveness.IsExhaustive)
            {
                string missing = exhaustiveness.MissingCases.Count > 0
                    ? $" Missing cases: {string.Join(separator: ", ", values: exhaustiveness.MissingCases)}."
                    : "";
                ReportError(code: SemanticDiagnosticCode.NonExhaustiveMatch,
                    message:
                    $"When expression is not exhaustive — all possible values must be handled.{missing}",
                    location: when.Location);
            }
        }

        return resultType ?? ErrorTypeInfo.Instance;
    }

    /// <summary>
    /// Infers type arguments for a generic routine from call arguments.
    /// Returns the inferred type arguments, or null if inference fails.
    /// </summary>
    private IReadOnlyList<TypeSymbol>? InferGenericTypeArguments(RoutineInfo genericRoutine,
        IReadOnlyList<Expression> arguments)
    {
        if (genericRoutine.GenericParameters == null ||
            genericRoutine.GenericParameters.Count == 0)
        {
            return null;
        }

        var typeArgs = new TypeSymbol?[genericRoutine.GenericParameters.Count];

        int argCount = Math.Min(val1: genericRoutine.Parameters.Count, val2: arguments.Count);
        for (int i = 0; i < argCount; i++)
        {
            TypeSymbol paramType = genericRoutine.Parameters[index: i].Type;

            // For variadic params, unwrap List[T] to get T for inference
            if (genericRoutine.Parameters[index: i].IsVariadicParam && paramType is
                    { IsGenericResolution: true, TypeArguments: [var elemType, ..] })
            {
                paramType = elemType;
            }

            if (paramType is GenericParameterTypeInfo)
            {
                int idx = genericRoutine.GenericParameters
                                        .ToList()
                                        .IndexOf(item: paramType.Name);
                if (idx >= 0 && typeArgs[idx] == null)
                {
                    Expression arg = arguments[index: i] is NamedArgumentExpression na
                        ? na.Value
                        : arguments[index: i];
                    TypeSymbol argType = AnalyzeExpression(expression: arg);
                    if (argType != ErrorTypeInfo.Instance)
                    {
                        typeArgs[idx] = argType;
                    }
                }
            }
        }

        // All type args must be inferred
        for (int i = 0; i < typeArgs.Length; i++)
        {
            if (typeArgs[i] == null)
            {
                return null;
            }
        }

        return typeArgs!;
    }
}
