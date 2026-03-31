namespace SemanticAnalysis;

using Enums;
using Symbols;
using Types;
using SyntaxTree;
using Diagnostics;
using TypeSymbol = Types.TypeInfo;

public sealed partial class SemanticAnalyzer
{
    private TypeSymbol AnalyzeMemberExpression(MemberExpression member)
    {
        TypeSymbol objectType = AnalyzeExpression(expression: member.Object);

        // Look up the member variable/property on the type
        if (objectType is RecordTypeInfo record)
        {
            MemberVariableInfo? memberVariable =
                record.LookupMemberVariable(memberVariableName: member.PropertyName);
            if (memberVariable != null)
            {
                // Validate member variable access (read access)
                ValidateMemberVariableAccess(memberVariable: memberVariable,
                    isWrite: false,
                    accessLocation: member.Location);
                return memberVariable.Type;
            }

            // Wrapper type forwarding for record-based wrappers (Viewed[T], Hijacked[T], etc.)
            if (IsWrapperType(type: objectType))
            {
                MemberVariableInfo? innerMemberVariable =
                    LookupMemberVariableOnWrapperInnerType(wrapperType: objectType,
                        memberVariableName: member.PropertyName);
                if (innerMemberVariable != null)
                {
                    ValidateMemberVariableAccess(memberVariable: innerMemberVariable,
                        isWrite: false,
                        accessLocation: member.Location);
                    return innerMemberVariable.Type;
                }

                RoutineInfo? innerMethod = LookupMethodOnWrapperInnerType(wrapperType: objectType,
                    methodName: member.PropertyName);
                if (innerMethod != null)
                {
                    ValidateReadOnlyWrapperMethodAccess(wrapperType: objectType,
                        method: innerMethod,
                        location: member.Location);
                    ValidateRoutineAccess(routine: innerMethod, accessLocation: member.Location);
                    return innerMethod.ReturnType ??
                           _registry.LookupType(name: "Blank") ?? ErrorTypeInfo.Instance;
                }
            }
        }
        else if (objectType is EntityTypeInfo entity)
        {
            MemberVariableInfo? memberVariable =
                entity.LookupMemberVariable(memberVariableName: member.PropertyName);
            if (memberVariable != null)
            {
                // Validate member variable access (read access)
                ValidateMemberVariableAccess(memberVariable: memberVariable,
                    isWrite: false,
                    accessLocation: member.Location);
                return memberVariable.Type;
            }
        }
        // Wrapper type forwarding: Viewed<T>, Hijacked<T>, Shared<T>, etc.
        else if (IsWrapperType(type: objectType))
        {
            // Try to forward member variable access to the inner type
            MemberVariableInfo? innerMemberVariable =
                LookupMemberVariableOnWrapperInnerType(wrapperType: objectType,
                    memberVariableName: member.PropertyName);
            if (innerMemberVariable != null)
            {
                // Validate member variable access on the inner type
                ValidateMemberVariableAccess(memberVariable: innerMemberVariable,
                    isWrite: false,
                    accessLocation: member.Location);
                return innerMemberVariable.Type;
            }

            // Try to forward method access to the inner type
            RoutineInfo? innerMethod = LookupMethodOnWrapperInnerType(wrapperType: objectType,
                methodName: member.PropertyName);
            if (innerMethod != null)
            {
                // Validate read-only wrapper restrictions
                ValidateReadOnlyWrapperMethodAccess(wrapperType: objectType,
                    method: innerMethod,
                    location: member.Location);
                // Validate method access
                ValidateRoutineAccess(routine: innerMethod, accessLocation: member.Location);
                // Return type is Blank if not specified
                return innerMethod.ReturnType ??
                       _registry.LookupType(name: "Blank") ?? ErrorTypeInfo.Instance;
            }
        }

        // Choice case member access: Color.RED → ChoiceTypeInfo
        if (objectType is ChoiceTypeInfo choice)
        {
            ChoiceCaseInfo? caseInfo =
                choice.Cases.FirstOrDefault(predicate: c => c.Name == member.PropertyName);
            if (caseInfo != null)
            {
                return choice; // Color.RED has type Color
            }

            // Fall through to method lookup — choice types can have methods
        }

        // Flags member access: Permissions.READ → FlagsTypeInfo
        if (objectType is FlagsTypeInfo flags)
        {
            FlagsMemberInfo? memberInfo =
                flags.Members.FirstOrDefault(predicate: m => m.Name == member.PropertyName);
            if (memberInfo != null)
            {
                return flags; // Permissions.READ has type Permissions
            }

            // Fall through to method lookup — flags types can have builder service methods
        }

        // Could be a method reference - use LookupMethod which handles generic resolutions
        // Strip '!' suffix from failable method calls (e.g., invalidate!() → invalidate)
        // The parser stores '!' in PropertyName, but routine declarations strip it (IsFailable = true)
        string lookupName = member.PropertyName.EndsWith(value: '!')
            ? member.PropertyName[..^1]
            : member.PropertyName;
        RoutineInfo? method = _registry.LookupMethod(type: objectType, methodName: lookupName);
        if (method != null)
        {
            // Validate method access
            ValidateRoutineAccess(routine: method, accessLocation: member.Location);

            TypeSymbol? returnType = method.ReturnType;

            // For methods on generic type parameters (e.g., routine T.view() → Viewed[T]),
            // substitute the generic parameter with the concrete receiver type
            if (returnType != null && method.OwnerType is GenericParameterTypeInfo genParamOwner)
            {
                var substitutions = new Dictionary<string, TypeSymbol>
                {
                    [key: genParamOwner.Name] = objectType
                };
                returnType = SubstituteWithMapping(type: returnType, substitutions: substitutions);
            }

            return returnType ?? _registry.LookupType(name: "Blank") ?? ErrorTypeInfo.Instance;
        }

        ReportError(code: SemanticDiagnosticCode.MemberNotFound,
            message: $"Type '{objectType.Name}' does not have a member '{member.PropertyName}'.",
            location: member.Location);
        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeOptionalMemberExpression(OptionalMemberExpression optMember)
    {
        // Analyze the object expression to get its type
        TypeSymbol objectType = AnalyzeExpression(expression: optMember.Object);

        // Delegate to regular member analysis for the property lookup
        // The result is wrapped in Maybe[T] since the access may produce none
        var regularMember = new MemberExpression(Object: optMember.Object,
            PropertyName: optMember.PropertyName,
            Location: optMember.Location);
        TypeSymbol memberType = AnalyzeMemberExpression(member: regularMember);

        return memberType;
    }

    private TypeSymbol AnalyzeIndexExpression(IndexExpression index)
    {
        TypeSymbol objectType = AnalyzeExpression(expression: index.Object);
        AnalyzeExpression(expression: index.Index);

        // Look for $getitem method — LookupMethod handles generic resolutions
        RoutineInfo? getItem = _registry.LookupMethod(type: objectType, methodName: "$getitem");
        if (getItem?.ReturnType != null)
        {
            return getItem.ReturnType;
        }

        // For generic types like List<T>, return the element type
        if (objectType.TypeArguments is { Count: > 0 })
        {
            return objectType.TypeArguments[index: 0];
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeSliceExpression(SliceExpression slice)
    {
        TypeSymbol objectType = AnalyzeExpression(expression: slice.Object);
        AnalyzeExpression(expression: slice.Start);
        AnalyzeExpression(expression: slice.End);

        // Look for $getslice method — LookupMethod handles generic resolutions
        RoutineInfo? getSlice = _registry.LookupMethod(type: objectType, methodName: "$getslice");
        if (getSlice?.ReturnType != null)
        {
            return getSlice.ReturnType;
        }

        // For generic types like List<T>, return the element type
        if (objectType.TypeArguments is { Count: > 0 })
        {
            return objectType.TypeArguments[index: 0];
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeConditionalExpression(ConditionalExpression cond)
    {
        // #145: Track nesting depth for deep conditional warning
        _conditionalNestingDepth++;
        if (_conditionalNestingDepth > 2)
        {
            ReportWarning(code: SemanticWarningCode.NestedConditionalExpression,
                message:
                "Deeply nested conditional expression. Consider using 'when' for readability.",
                location: cond.Location);
        }

        TypeSymbol conditionType = AnalyzeExpression(expression: cond.Condition);

        if (!IsBoolType(type: conditionType))
        {
            ReportError(code: SemanticDiagnosticCode.ConditionalNotBool,
                message:
                $"Conditional expression requires a boolean condition, got '{conditionType.Name}'.",
                location: cond.Condition.Location);
        }

        TypeSymbol trueType = AnalyzeExpression(expression: cond.TrueExpression);
        TypeSymbol falseType = AnalyzeExpression(expression: cond.FalseExpression);

        // Both branches must be compatible
        if (!IsAssignableTo(source: trueType, target: falseType) &&
            !IsAssignableTo(source: falseType, target: trueType))
        {
            ReportError(code: SemanticDiagnosticCode.ConditionalBranchTypeMismatch,
                message:
                $"Conditional expression branches have incompatible types: '{trueType.Name}' and '{falseType.Name}'.",
                location: cond.Location);
        }

        _conditionalNestingDepth--;

        // Return the common type (for now, use the true branch type)
        return trueType;
    }

    private TypeSymbol AnalyzeLambdaExpression(LambdaExpression lambda)
    {
        // Collect variables from enclosing scope that might be captured
        IReadOnlyDictionary<string, VariableInfo> enclosingScopeVariables =
            _registry.GetAllVariablesInScope();
        // Collect only local (function-level) variables — these require 'given' to capture
        IReadOnlyDictionary<string, VariableInfo> localScopeVariables =
            _registry.GetLocalScopeVariables();

        _registry.EnterScope(kind: ScopeKind.Function, name: "lambda");

        // Register lambda parameters and collect their types
        var parameterNames = new HashSet<string>();
        var parameterTypes = new List<TypeSymbol>();
        foreach (Parameter param in lambda.Parameters)
        {
            TypeSymbol paramType = param.Type != null
                ? ResolveType(typeExpr: param.Type)
                : ErrorTypeInfo.Instance;

            _registry.DeclareVariable(name: param.Name, type: paramType);
            parameterNames.Add(item: param.Name);
            parameterTypes.Add(item: paramType);
        }

        // Analyze body and get return type
        TypeSymbol returnType = AnalyzeExpression(expression: lambda.Body);

        // Validate captured variables (RazorForge only)
        // Lambda bodies can reference variables from enclosing scope - these are captures
        ValidateLambdaCaptures(lambda: lambda,
            enclosingScopeVariables: enclosingScopeVariables,
            localScopeVariables: localScopeVariables,
            parameterNames: parameterNames);

        _registry.ExitScope();

        // Create a proper function type: (ParamTypes) -> ReturnType
        return _registry.GetOrCreateRoutineType(parameterTypes: parameterTypes,
            returnType: returnType,
            isFailable: false);
    }

    /// <summary>
    /// Validates that lambda captures don't include forbidden types and that all
    /// local-scope captures are declared in the 'given' clause (RazorForge only).
    /// </summary>
    /// <param name="lambda">The lambda expression being analyzed.</param>
    /// <param name="enclosingScopeVariables">All variables available in the enclosing scope.</param>
    /// <param name="localScopeVariables">Variables from local (function-level) scopes only — require 'given'.</param>
    /// <param name="parameterNames">Names of lambda parameters (not captures).</param>
    private void ValidateLambdaCaptures(LambdaExpression lambda,
        IReadOnlyDictionary<string, VariableInfo> enclosingScopeVariables,
        IReadOnlyDictionary<string, VariableInfo> localScopeVariables,
        HashSet<string> parameterNames)
    {
        // Find all identifier expressions in the lambda body
        List<IdentifierExpression> identifiers = CollectIdentifiers(expression: lambda.Body);

        // Build set of given captures for quick lookup
        HashSet<string>? givenNames = lambda.Captures != null
            ? new HashSet<string>(collection: lambda.Captures)
            : null;

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
            if (enclosingScopeVariables.TryGetValue(key: id.Name,
                    value: out VariableInfo? varInfo))
            {
                // Validate that the captured type is allowed
                ValidateCapturedType(varName: id.Name,
                    varType: varInfo.Type,
                    location: id.Location);

                // Check 'given' clause enforcement for local captures (RazorForge only)
                if (_registry.Language == Language.RazorForge &&
                    localScopeVariables.ContainsKey(key: id.Name) && !varInfo.IsPreset)
                {
                    if (givenNames == null)
                    {
                        // No 'given' clause — implicit capture of local variable
                        ReportError(code: SemanticDiagnosticCode.LambdaCaptureWithoutGiven,
                            message:
                            $"Lambda captures local variable '{id.Name}' without declaring it in 'given' clause. " +
                            "All local captures must be explicit via 'given'.",
                            location: id.Location);
                    }
                    else if (!givenNames.Contains(item: id.Name))
                    {
                        // Has 'given' clause but this variable isn't in it
                        ReportError(code: SemanticDiagnosticCode.LambdaCaptureWithoutGiven,
                            message:
                            $"Lambda captures local variable '{id.Name}' but it is not listed in the 'given' clause.",
                            location: id.Location);
                    }
                }
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
            ReportError(code: SemanticDiagnosticCode.LambdaCaptureToken,
                message: $"Cannot capture '{varName}' of type '{tokenKind}' in lambda - " +
                         $"scope-bound tokens cannot escape their scope. " +
                         $"Use a handle type (Shared[T] or Tracked[T]) instead.",
                location: location);
            return;
        }

        // Check for raw entities (must use handles for capture)
        if (IsRawEntityType(type: varType))
        {
            ReportError(code: SemanticDiagnosticCode.LambdaCaptureRawEntity,
                message:
                $"Cannot capture raw entity '{varName}' of type '{varType.Name}' in lambda - " +
                $"raw entities cannot be captured. " +
                $"Wrap in a handle type (Shared[T] or Tracked[T]) before capturing.",
                location: location);
        }
    }

    /// <summary>
    /// Checks if a type is a raw entity (not wrapped in a handle or token).
    /// </summary>
    private bool IsRawEntityType(TypeSymbol type)
    {
        // Raw entities are entity types that are not wrapped
        return type.Category == TypeCategory.Entity && !IsMemoryToken(type: type) &&
               !IsStealableHandle(type: type) && !IsSnatched(type: type);
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
    private static void CollectIdentifiersRecursive(Expression expression,
        List<IdentifierExpression> identifiers)
    {
        switch (expression)
        {
            case IdentifierExpression id:
                identifiers.Add(item: id);
                break;

            case CompoundAssignmentExpression compound:
                CollectIdentifiersRecursive(expression: compound.Target, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: compound.Value, identifiers: identifiers);
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

            case SliceExpression slice:
                CollectIdentifiersRecursive(expression: slice.Object, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: slice.Start, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: slice.End, identifiers: identifiers);
                break;

            case ConditionalExpression cond:
                CollectIdentifiersRecursive(expression: cond.Condition, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: cond.TrueExpression,
                    identifiers: identifiers);
                CollectIdentifiersRecursive(expression: cond.FalseExpression,
                    identifiers: identifiers);
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

            case CreatorExpression creator:
                foreach ((_, Expression value) in creator.MemberVariables)
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

            case TupleLiteralExpression tuple:
                foreach (Expression elem in tuple.Elements)
                {
                    CollectIdentifiersRecursive(expression: elem, identifiers: identifiers);
                }

                break;

            case BlockExpression block:
                CollectIdentifiersRecursive(expression: block.Value, identifiers: identifiers);
                break;

            case WithExpression with:
                CollectIdentifiersRecursive(expression: with.Base, identifiers: identifiers);
                foreach ((_, Expression? index, Expression value) in with.Updates)
                {
                    if (index != null)
                    {
                        CollectIdentifiersRecursive(expression: index, identifiers: identifiers);
                    }

                    CollectIdentifiersRecursive(expression: value, identifiers: identifiers);
                }

                break;

            case IsPatternExpression isPat:
                CollectIdentifiersRecursive(expression: isPat.Expression,
                    identifiers: identifiers);
                break;

            case NamedArgumentExpression named:
                CollectIdentifiersRecursive(expression: named.Value, identifiers: identifiers);
                break;

            case DictEntryLiteralExpression dictEntry:
                CollectIdentifiersRecursive(expression: dictEntry.Key, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: dictEntry.Value, identifiers: identifiers);
                break;

            case GenericMethodCallExpression generic:
                CollectIdentifiersRecursive(expression: generic.Object, identifiers: identifiers);
                foreach (Expression arg in generic.Arguments)
                {
                    CollectIdentifiersRecursive(expression: arg, identifiers: identifiers);
                }

                break;

            case GenericMemberExpression genericMember:
                CollectIdentifiersRecursive(expression: genericMember.Object,
                    identifiers: identifiers);
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

        // #119: BackIndex (^n) cannot be used in Range expressions — only in subscript/slice context
        if (range.Start is BackIndexExpression)
        {
            ReportError(code: SemanticDiagnosticCode.BackIndexOutsideSubscript,
                message:
                "BackIndex (^n) cannot be used in Range expressions. Use it in subscript [^n] or slice [a to b] context instead.",
                location: range.Start.Location);
        }

        if (range.End is BackIndexExpression)
        {
            ReportError(code: SemanticDiagnosticCode.BackIndexOutsideSubscript,
                message:
                "BackIndex (^n) cannot be used in Range expressions. Use it in subscript [^n] or slice [a to b] context instead.",
                location: range.End.Location);
        }

        // Range types must be compatible
        if (!IsNumericType(type: startType) || !IsNumericType(type: endType))
        {
            ReportError(code: SemanticDiagnosticCode.RangeBoundsNotNumeric,
                message: "Range bounds must be numeric types.",
                location: range.Location);
        }

        // Return resolved Range[T] type with concrete element type
        TypeInfo? rangeGenericDef = _registry.LookupType(name: "Range");
        if (rangeGenericDef != null && startType is not ErrorTypeInfo)
        {
            return _registry.GetOrCreateResolution(genericDef: rangeGenericDef,
                typeArguments: new List<TypeInfo> { startType });
        }

        return rangeGenericDef ?? ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeCreatorExpression(CreatorExpression creator)
    {
        TypeSymbol? type = LookupTypeWithImports(name: creator.TypeName);
        if (type == null)
        {
            ReportError(code: SemanticDiagnosticCode.UnknownType,
                message: $"Unknown type '{creator.TypeName}'.",
                location: creator.Location);
            return ErrorTypeInfo.Instance;
        }

        // Handle generic type arguments
        if (creator.TypeArguments is { Count: > 0 })
        {
            var typeArgs = new List<TypeSymbol>();
            foreach (TypeExpression typeArg in creator.TypeArguments)
            {
                typeArgs.Add(item: ResolveType(typeExpr: typeArg));
            }

            ValidateGenericConstraints(genericDef: type,
                typeArgs: typeArgs,
                location: creator.Location);
            type = _registry.GetOrCreateResolution(genericDef: type, typeArguments: typeArgs);
        }

        // Validate member variable initializers
        ValidateCreatorMemberVariables(type: type,
            memberVariables: creator.MemberVariables,
            location: creator.Location);

        return type;
    }

    /// <summary>
    /// Validates creator member variable initializers:
    /// - Each provided member variable exists on the type
    /// - Value types are assignable to member variable types
    /// - No duplicate member variable assignments
    /// - All required member variables are provided
    /// </summary>
    private void ValidateCreatorMemberVariables(TypeSymbol type,
        List<(string Name, Expression Value)> memberVariables, SourceLocation location)
    {
        // Get the type's member variables
        IReadOnlyList<MemberVariableInfo>? typeMemberVariables = type switch
        {
            RecordTypeInfo record => record.MemberVariables,
            EntityTypeInfo entity => entity.MemberVariables,
            _ => null
        };

        if (typeMemberVariables == null)
        {
            if (memberVariables.Count > 0)
            {
                ReportError(code: SemanticDiagnosticCode.TypeNotMemberVariableInitializable,
                    message:
                    $"Type '{type.Name}' does not support member variable initialization.",
                    location: location);
            }

            return;
        }

        // Build a lookup for expected member variables
        var memberVariableLookup = new Dictionary<string, MemberVariableInfo>();
        foreach (MemberVariableInfo memberVariable in typeMemberVariables)
        {
            memberVariableLookup[key: memberVariable.Name] = memberVariable;
        }

        // Track which member variables have been provided (to detect duplicates and missing member variables)
        var providedMemberVariables = new HashSet<string>();

        // Validate each provided member variable
        foreach ((string memberVariableName, Expression value) in memberVariables)
        {
            // Check for duplicates
            if (!providedMemberVariables.Add(item: memberVariableName))
            {
                ReportError(code: SemanticDiagnosticCode.DuplicateMemberVariableInitializer,
                    message: $"Duplicate member variable initializer for '{memberVariableName}'.",
                    location: value.Location);
                continue;
            }

            // Check if member variable exists
            if (!memberVariableLookup.TryGetValue(key: memberVariableName,
                    value: out MemberVariableInfo? expectedMemberVariable))
            {
                ReportError(code: SemanticDiagnosticCode.MemberVariableNotFound,
                    message:
                    $"Type '{type.Name}' does not have a member variable named '{memberVariableName}'.",
                    location: value.Location);
                AnalyzeExpression(expression: value); // Still analyze the value
                continue;
            }

            // Analyze value with expected type for contextual inference
            TypeSymbol memberVariableType = expectedMemberVariable.Type;

            // For generic resolutions, substitute type parameters in member variable type
            if (type is { IsGenericResolution: true, TypeArguments: not null })
            {
                memberVariableType =
                    SubstituteTypeParameters(type: memberVariableType, genericType: type);
            }

            TypeSymbol valueType =
                AnalyzeExpression(expression: value, expectedType: memberVariableType);

            // Check type compatibility
            if (!IsAssignableTo(source: valueType, target: memberVariableType))
            {
                ReportError(code: SemanticDiagnosticCode.MemberVariableTypeMismatch,
                    message:
                    $"Cannot assign '{valueType.Name}' to member variable '{memberVariableName}' of type '{memberVariableType.Name}'.",
                    location: value.Location);
            }
        }

        // Check for missing required member variables (member variables without default values)
        foreach (MemberVariableInfo memberVariable in typeMemberVariables)
        {
            if (!providedMemberVariables.Contains(item: memberVariable.Name) &&
                !memberVariable.HasDefaultValue)
            {
                ReportError(code: SemanticDiagnosticCode.MissingRequiredMemberVariable,
                    message:
                    $"Missing required member variable '{memberVariable.Name}' in creator for '{type.Name}'.",
                    location: location);
            }
        }
    }
}
