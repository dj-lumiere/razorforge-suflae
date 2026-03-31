namespace SemanticAnalysis;

using Enums;
using Symbols;
using Types;
using SyntaxTree;
using Diagnostics;
using TypeSymbol = Types.TypeInfo;

public sealed partial class SemanticAnalyzer
{
    private TypeSymbol AnalyzeGenericMethodCallExpression(GenericMethodCallExpression generic)
    {
        TypeSymbol objectType = AnalyzeExpression(expression: generic.Object);

        // Resolve type arguments
        var typeArgs = new List<TypeSymbol>();
        foreach (TypeExpression typeArg in generic.TypeArguments)
        {
            typeArgs.Add(item: ResolveType(typeExpr: typeArg));
        }

        // #19: Track lock policy from lock![Policy]() on entities
        if (generic.MethodName == "lock" && generic.IsMemoryOperation && typeArgs.Count > 0 &&
            generic.Object is IdentifierExpression lockTarget)
        {
            _variableLockPolicies[key: lockTarget.Name] = typeArgs[index: 0].Name;
        }

        // #19: Track lock policy from share[Policy]() on entities — stored temporarily
        // on the source variable; propagated to the declared variable in AnalyzeVariableDeclaration
        if (generic.MethodName == "share" && typeArgs.Count > 0 &&
            generic.Object is IdentifierExpression shareTarget)
        {
            _lastSharePolicy = (shareTarget.Name, typeArgs[index: 0].Name);
        }

        // Check if this is a generic type constructor call (e.g., Snatched[U8](addr))
        // The parser creates GenericMethodCallExpression for both Type[Args](args) and obj.method[Args](args)
        if (generic.Object is IdentifierExpression typeId && objectType is TypeInfo typeInfo &&
            typeInfo.IsGenericDefinition && typeId.Name == generic.MethodName)
        {
            // Resolve the generic type with the provided type arguments
            TypeInfo resolvedType = _registry.GetOrCreateResolution(genericDef: typeInfo,
                typeArguments: typeArgs.Cast<TypeInfo>()
                                       .ToList());

            // Build expected element type from explicit generic type args
            TypeSymbol? expectedElemType = null;
            bool isMapType = typeId.Name is "Dict" or "SortedDict" or "PriorityQueue";
            if (isMapType && typeArgs.Count >= 2)
            {
                expectedElemType = _registry.GetOrCreateTupleType(elementTypes:
                    [(TypeInfo)typeArgs[index: 0], (TypeInfo)typeArgs[index: 1]]);
            }
            else if (typeArgs.Count >= 1)
            {
                expectedElemType = typeArgs[index: 0];
            }

            // Analyze constructor arguments
            foreach (Expression arg in generic.Arguments)
            {
                AnalyzeExpression(expression: arg, expectedType: expectedElemType);
            }

            // Collection literal constructor with explicit type args: List[S64](1, 2, 3)
            if (CollectionLiteralTypes.Contains(item: typeId.Name) &&
                generic.Arguments.Count > 0 &&
                generic.Arguments.All(predicate: a => a is not NamedArgumentExpression))
            {
                generic.IsCollectionLiteral = true;

                // ValueList[T, N] / ValueBitList[N] — argument count must match N
                if (typeId.Name == "ValueList" && typeArgs.Count >= 2)
                {
                    // N is the second type arg (a numeric constant type)
                    string nStr = typeArgs[index: 1].Name;
                    if (ulong.TryParse(s: nStr, result: out ulong n) &&
                        (ulong)generic.Arguments.Count != n)
                    {
                        ReportError(code: SemanticDiagnosticCode.ArgumentCountMismatch,
                            message:
                            $"ValueList[{typeArgs[index: 0].Name}, {n}] constructor requires exactly {n} arguments, got {generic.Arguments.Count}.",
                            location: generic.Location);
                    }
                }
                else if (typeId.Name == "ValueBitList" && typeArgs.Count >= 1)
                {
                    string nStr = typeArgs[index: 0].Name;
                    if (ulong.TryParse(s: nStr, result: out ulong n) &&
                        (ulong)generic.Arguments.Count != n)
                    {
                        ReportError(code: SemanticDiagnosticCode.ArgumentCountMismatch,
                            message:
                            $"ValueBitList[{n}] constructor requires exactly {n} arguments, got {generic.Arguments.Count}.",
                            location: generic.Location);
                    }
                }
            }

            ValidateExclusiveTokenUniqueness(arguments: generic.Arguments,
                location: generic.Location);
            return resolvedType;
        }

        // Look up the method on receiver type — LookupMethod handles generic resolutions
        RoutineInfo? method =
            _registry.LookupMethod(type: objectType, methodName: generic.MethodName);
        if (method != null)
        {
            foreach (Expression arg in generic.Arguments)
            {
                AnalyzeExpression(expression: arg);
            }

            ValidateExclusiveTokenUniqueness(arguments: generic.Arguments,
                location: generic.Location);

            if (method.ReturnType == null)
            {
                return _registry.LookupType(name: "Blank") ?? ErrorTypeInfo.Instance;
            }

            TypeSymbol returnType = method.ReturnType;

            // Substitute method's own generic params (U from obtain_as[U])
            // GenericParameters includes both owner-level (T) and method-level (U) params,
            // but typeArgs only contains method-level args from the call site.
            // Compute the offset to skip owner-level params when indexing into typeArgs.
            if (method.GenericParameters != null)
            {
                int ownerParamCount = objectType is
                    { IsGenericResolution: true, TypeArguments: not null }
                    ? objectType.TypeArguments.Count
                    : 0;

                // Direct param (return type is just U)
                if (returnType is GenericParameterTypeInfo)
                {
                    int paramIndex = method.GenericParameters
                                           .ToList()
                                           .IndexOf(item: returnType.Name);
                    int adjustedIndex = paramIndex - ownerParamCount;
                    if (adjustedIndex >= 0 && adjustedIndex < typeArgs.Count &&
                        typeArgs[index: adjustedIndex] is TypeInfo resolved)
                    {
                        return resolved;
                    }
                }

                // Resolution containing method's params (e.g., Snatched[U])
                if (returnType is { IsGenericResolution: true, TypeArguments: not null })
                {
                    var substitutedArgs = new List<TypeInfo>();
                    bool anySubstituted = false;
                    foreach (TypeInfo typeArg in returnType.TypeArguments)
                    {
                        int idx = method.GenericParameters
                                        .ToList()
                                        .IndexOf(item: typeArg.Name);
                        int adjustedIdx = idx - ownerParamCount;
                        if (adjustedIdx >= 0 && adjustedIdx < typeArgs.Count &&
                            typeArgs[index: adjustedIdx] is TypeInfo sub)
                        {
                            substitutedArgs.Add(item: sub);
                            anySubstituted = true;
                        }
                        else
                        {
                            substitutedArgs.Add(item: typeArg);
                        }
                    }

                    if (anySubstituted)
                    {
                        string baseName = returnType.Name;
                        int bracketIdx = baseName.IndexOf(value: '[');
                        if (bracketIdx > 0)
                        {
                            baseName = baseName[..bracketIdx];
                        }

                        TypeInfo? genericDef = _registry.LookupType(name: baseName);
                        if (genericDef != null)
                        {
                            return _registry.GetOrCreateResolution(genericDef: genericDef,
                                typeArguments: substitutedArgs);
                        }
                    }
                }
            }

            return returnType;
        }

        // Standalone generic function call (e.g., ptrtoint[Point, Address](p), snatched_none[T]())
        // The object is an identifier that resolves to a routine, not a type or variable
        if (generic.Object is IdentifierExpression funcId)
        {
            RoutineInfo? routine = _registry.LookupRoutine(fullName: funcId.Name) ??
                                   _registry.LookupRoutineByName(name: funcId.Name);
            if (routine != null)
            {
                foreach (Expression arg in generic.Arguments)
                {
                    AnalyzeExpression(expression: arg);
                }

                if (routine.ReturnType == null)
                {
                    return _registry.LookupType(name: "Blank") ?? ErrorTypeInfo.Instance;
                }

                // Substitute generic type parameters in return type
                TypeInfo returnType = routine.ReturnType;
                if (returnType is GenericParameterTypeInfo && routine.GenericParameters != null)
                {
                    int paramIndex = routine.GenericParameters
                                            .ToList()
                                            .IndexOf(item: returnType.Name);
                    if (paramIndex >= 0 && paramIndex < typeArgs.Count &&
                        typeArgs[index: paramIndex] is TypeInfo resolved)
                    {
                        return resolved;
                    }
                }

                // Return type is a generic resolution (e.g., Snatched[T] → Snatched[U8])
                if (returnType.IsGenericResolution && returnType.TypeArguments != null &&
                    routine.GenericParameters != null)
                {
                    var substitutedArgs = new List<TypeInfo>();
                    bool anySubstituted = false;
                    foreach (TypeInfo typeArg in returnType.TypeArguments)
                    {
                        int idx = routine.GenericParameters
                                         .ToList()
                                         .IndexOf(item: typeArg.Name);
                        if (idx >= 0 && idx < typeArgs.Count &&
                            typeArgs[index: idx] is TypeInfo sub)
                        {
                            substitutedArgs.Add(item: sub);
                            anySubstituted = true;
                        }
                        else
                        {
                            substitutedArgs.Add(item: typeArg);
                        }
                    }

                    if (anySubstituted)
                    {
                        string baseName = returnType.Name;
                        int bracketIdx = baseName.IndexOf(value: '[');
                        if (bracketIdx > 0)
                        {
                            baseName = baseName[..bracketIdx];
                        }

                        TypeInfo? genericDef = _registry.LookupType(name: baseName);
                        if (genericDef != null)
                        {
                            return _registry.GetOrCreateResolution(genericDef: genericDef,
                                typeArguments: substitutedArgs);
                        }
                    }
                }

                return returnType;
            }
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
        TypeSymbol objectType = AnalyzeExpression(expression: genericMember.Object);

        // Resolve type arguments
        foreach (TypeExpression typeArg in genericMember.TypeArguments)
        {
            ResolveType(typeExpr: typeArg);
        }

        // Look up the member on the object type
        if (objectType is TypeInfo objTypeInfo)
        {
            IReadOnlyList<MemberVariableInfo>? memberVars = objTypeInfo switch
            {
                EntityTypeInfo e => e.MemberVariables,
                RecordTypeInfo r => r.MemberVariables,
                _ => null
            };
            MemberVariableInfo? memberVar =
                memberVars?.FirstOrDefault(predicate: mv => mv.Name == genericMember.MemberName);
            if (memberVar != null)
            {
                // Member found — the [args] are indexing into the member's value.
                // Analyze the "type arguments" as expressions (they're actually index values).
                foreach (TypeExpression typeArg in genericMember.TypeArguments)
                {
                    // The type arg's Name is actually a variable name — analyze it as identifier
                    if (typeArg.Name != null)
                    {
                        AnalyzeExpression(expression: new IdentifierExpression(Name: typeArg.Name,
                            Location: typeArg.Location));
                    }
                }

                // Determine the element type of the member's collection type
                TypeInfo? memberType = memberVar.Type;
                if (memberType is { TypeArguments: { Count: > 0 } })
                {
                    // e.g., List[SortedDict[K,V]] ��� element is SortedDict[K,V]
                    return memberType.TypeArguments[index: 0];
                }

                // If the member type has a $getitem method, use its return type
                RoutineInfo? getItem =
                    _registry.LookupMethod(type: memberType, methodName: "$getitem") ??
                    _registry.LookupMethod(type: memberType, methodName: "$getitem!");
                if (getItem?.ReturnType != null)
                {
                    return getItem.ReturnType;
                }

                return memberType ?? ErrorTypeInfo.Instance;
            }
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeIsPatternExpression(IsPatternExpression isPat)
    {
        TypeSymbol exprType = AnalyzeExpression(expression: isPat.Expression);

        // Analyze the pattern (may bind variables)
        AnalyzePattern(pattern: isPat.Pattern, matchedType: exprType);

        // 'is' expressions always return bool
        return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeFlagsTestExpression(FlagsTestExpression flagsTest)
    {
        TypeSymbol subjectType = AnalyzeExpression(expression: flagsTest.Subject);

        if (subjectType.Category == TypeCategory.Error)
        {
            return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;
        }

        if (subjectType is not FlagsTypeInfo flagsType)
        {
            ReportError(code: SemanticDiagnosticCode.FlagsTypeMismatch,
                message:
                $"Flags test operators (is/isnot/isonly) require a flags type, but got '{subjectType.Name}'.",
                location: flagsTest.Location);
            return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;
        }

        // #133: isonly rejects 'or' and 'but'
        if (flagsTest.Kind == FlagsTestKind.IsOnly)
        {
            if (flagsTest.Connective == FlagsTestConnective.Or)
            {
                ReportError(code: SemanticDiagnosticCode.FlagsIsOnlyRejectsOrBut,
                    message:
                    "'isonly' cannot be used with 'or'. Use 'and' to specify the exact set of flags.",
                    location: flagsTest.Location);
            }

            if (flagsTest.ExcludedFlags is { Count: > 0 })
            {
                ReportError(code: SemanticDiagnosticCode.FlagsIsOnlyRejectsOrBut,
                    message:
                    "'isonly' cannot be used with 'but'. Specify the exact set of flags directly.",
                    location: flagsTest.Location);
            }
        }

        // Validate each flag name exists in the type
        foreach (string flagName in flagsTest.TestFlags)
        {
            if (flagsType.Members.All(predicate: m => m.Name != flagName))
            {
                ReportError(code: SemanticDiagnosticCode.FlagsMemberNotFound,
                    message:
                    $"Flags type '{flagsType.Name}' does not have a member named '{flagName}'.",
                    location: flagsTest.Location);
            }
        }

        // Validate excluded flags too
        if (flagsTest.ExcludedFlags != null)
        {
            foreach (string flagName in flagsTest.ExcludedFlags)
            {
                if (flagsType.Members.All(predicate: m => m.Name != flagName))
                {
                    ReportError(code: SemanticDiagnosticCode.FlagsMemberNotFound,
                        message:
                        $"Flags type '{flagsType.Name}' does not have a member named '{flagName}'.",
                        location: flagsTest.Location);
                }
            }
        }

        return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;
    }

    private TypeSymbol HandleUnknownExpression(Expression expression)
    {
        ReportWarning(code: SemanticWarningCode.UnknownExpressionType,
            message: $"Unknown expression type: {expression.GetType().Name}",
            location: expression.Location);
        return ErrorTypeInfo.Instance;
    }

    /// <summary>
    /// Analyzes an inserted text expression (f-string).
    /// Validates all embedded expressions and returns Text type.
    /// </summary>
}
