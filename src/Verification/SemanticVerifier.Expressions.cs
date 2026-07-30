using System;
using Compiler.Diagnostics;
using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Verification;

using TypeSymbol = TypeInfo;

/// <summary>
/// Phase 3: Expression analysis.
/// </summary>
public sealed partial class SemanticVerifier
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
            LiteralExpression literal => AnalyzeLiteralExpression(literal: literal,
                expectedType: expectedType),
            IdentifierExpression id => AnalyzeIdentifierExpression(id: id),
            CompoundAssignmentExpression compound => AnalyzeCompoundAssignment(compound: compound),
            BinaryExpression binary => AnalyzeBinaryExpression(binary: binary),
            UnaryExpression unary => AnalyzeUnaryExpression(unary: unary),
            CallExpression call => AnalyzeCallExpression(call: call, expectedType: expectedType),
            MemberExpression member => AnalyzeMemberExpression(member: member),
            OptionalMemberExpression optMember => AnalyzeOptionalMemberExpression(
                optMember: optMember),
            IndexExpression index => AnalyzeIndexExpression(index: index),
            ConditionalExpression cond => AnalyzeConditionalExpression(cond: cond),
            LambdaExpression lambda => AnalyzeLambdaExpression(lambda: lambda,
                expectedType: expectedType),
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
            NamedArgumentExpression named => AnalyzeExpression(expression: named.Value,
                expectedType: expectedType),
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
            InsertedTextExpression insertedText => AnalyzeInsertedTextExpression(
                insertedText: insertedText),
            _ => HandleUnknownExpression(expression: expression)
        };

        // Compiler-generated bodies are re-analyzed in a synthetic scope where some calls
        // cannot be re-resolved (generic-def owners, method-generic locals, wired routines,
        // type names outside their import snapshot). Their synthesizer/cloner annotations are
        // correct by construction — never make an annotation WORSE there: don't replace a good
        // annotation with <error>, and don't replace a concrete type with one that still
        // contains generic parameters (e.g. `sub[S8](...)` annotated S8 by real analysis must
        // not regress to T when the variant-body re-analysis fails to resolve `S8`).
        // Downgrading cascades: a degraded receiver kills resolution of every enclosing call,
        // and codegen then rejects the whole body ("Synthesized body codegen failed").
        if (_isInCompilerGeneratedBody
            && expression.ResolvedType is { } existingAnnotation and not ErrorTypeInfo
            && (resultType is ErrorTypeInfo
                || (ContainsUnresolvedGenericParameter(type: resultType)
                    && !ContainsUnresolvedGenericParameter(type: existingAnnotation))))
        {
            return existingAnnotation;
        }

        // Set the resolved type directly (no conversion needed)
        expression.ResolvedType = resultType;
        return resultType;
    }

    /// <summary>
    /// True when <paramref name="type"/> is, or structurally contains, an unresolved
    /// generic type parameter (a less-concrete annotation than any fully resolved type).
    /// </summary>
    private static bool ContainsUnresolvedGenericParameter(TypeSymbol type)
    {
        if (type is GenericParameterTypeInfo)
        {
            return true;
        }

        if (type.TypeArguments is { Count: > 0 } args)
        {
            foreach (TypeSymbol arg in args)
            {
                if (ContainsUnresolvedGenericParameter(type: arg))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private TypeSymbol AnalyzeIdentifierExpression(IdentifierExpression id) // NOSONAR S3776
    {
        switch (id.Name)
        {
            // Special identifiers
            // First check if we're inside a type body
            case "me" when _currentType != null:
                return _currentType;
            case "me" when _currentRoutine?.OwnerType == null:
                ReportError(code: SemanticDiagnosticCode.MeOutsideTypeMethod,
                    message: "'me' can only be used inside a type method.",
                    location: id.Location);
                return ErrorTypeInfo.Instance;
            // For extension methods (routine Type.method), check the routine's owner type
            case "me":
            {
                // Specialized-receiver member (e.g. `routine List[Agent[V]].gather!()`): `me` is the
                // resolved specialized receiver so member access like `me[i]` yields Agent[V], not the
                // generic def's raw element. (OwnerType stays the generic def for registration.)
                if (_currentRoutine.MeType != null)
                {
                    return _currentRoutine.MeType;
                }

                // Generic type parameter owners (e.g., T in "routine T.view()") —
                // return the GenericParameterTypeInfo directly, no registry lookup needed
                if (_currentRoutine.OwnerType is GenericParameterTypeInfo)
                {
                    return _currentRoutine.OwnerType;
                }

                // Re-lookup to get the updated type with resolved protocols/member variables.
                // Use the module-qualified FullName, not the bare Name: two modules can each declare
                // a `Point`, and a bare `LookupType("Point")` collapses to a first-wins short-name
                // match — binding `me` to the WRONG module's type (cross-module contamination).
                TypeSymbol? ownerType =
                    _registry.LookupType(name: _currentRoutine.OwnerType.FullName)
                    ?? _registry.LookupType(name: _currentRoutine.OwnerType.Name);
                if (ownerType != null)
                {
                    return ownerType;
                }

                break;
            }
            case "None":
                // None represents Maybe.None - return a generic Maybe type
                return _registry.LookupType(name: "Maybe") ?? ErrorTypeInfo.Instance;
        }

        // Flag-context resolution: when analyzing the RHS of `p isonly READ` (where p: Perm),
        // bare `READ` resolves against `Perm`'s flag members. The matching FlagsMemberInfo
        // bit is stashed on the identifier so ExpressionLoweringPass can emit the bitmask.
        if (_flagsContextStack.Count > 0 && id.Name.Length > 0 && !id.Name.Contains(value: '.'))
        {
            TypeSymbol flagsCtx = _flagsContextStack.Peek();
            if (flagsCtx is FlagsTypeInfo flagsTypeCtx)
            {
                FlagsMemberInfo? memberInfo = flagsTypeCtx.Members
                    .FirstOrDefault(predicate: m => m.Name == id.Name);
                if (memberInfo != null)
                {
                    id.ResolvedFlagsBit = memberInfo.BitPosition;
                    return flagsTypeCtx;
                }
            }
        }

        // Try to look up as variable first
        VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
        // Try current module prefix for presets (e.g., "MY_CONST" -> "MyModule.MY_CONST")
        if (varInfo == null && _currentModuleName != null && !id.Name.Contains(value: '.'))
        {
            varInfo = _registry.LookupVariable(name: $"{_currentModuleName}.{id.Name}");
        }

        if (varInfo != null)
        {
            // #11: Deadref tracking — report error if steal invalidated variable
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

        // Try to look up as type FIRST (for static access like `U64.data_size()`).
        // Types take precedence over routines when both share a bare name — bare type
        // references for static access (member access, type-as-value generic args, etc.)
        // are the common case; first-class routine references with name-collisions are rare
        // and can be disambiguated by qualified name if ever needed.
        TypeSymbol? type = LookupTypeWithImports(name: id.Name);
        if (type != null)
        {
            return type;
        }

        // Try to look up as routine (function reference)
        // Strip '!' suffix for failable routine references (e.g., "stop!" -> "stop")
        string routineLookupName = id.Name.EndsWith(value: '!')
            ? id.Name[..^1]
            : id.Name;
        RoutineInfo? routine = _registry.LookupRoutine(fullName: routineLookupName);
        // Try current module prefix (e.g., "infinite_loop" -> "HelloWorld.infinite_loop")
        if (routine == null && _currentModuleName != null &&
            !routineLookupName.Contains(value: '.'))
        {
            routine = _registry.LookupRoutine(
                fullName: $"{_currentModuleName}.{routineLookupName}");
        }

        // Generic free routines are indexed only in the generic-overload table, not under a plain
        // name key, so LookupRoutine misses them. A bare reference — e.g. the receiver identifier of
        // an explicit `gen_id[T](...)` call — must consult it too, or a generic free routine reads as
        // an unknown identifier when called from another module (concrete free routines resolve fine).
        routine ??= _registry.LookupGenericOverload(name: routineLookupName);

        if (routine != null)
        {
            // Return the function type for first-class function references
            return GetRoutineType(routine: routine);
        }

        // Try to look up as generic type parameter (e.g., T in "T.data_size()")
        if (IsGenericParameter(name: id.Name))
        {
            return new GenericParameterTypeInfo(name: id.Name);
        }

        // A `secret` type of this name exists but lives in another module (module-private): resolution
        // above deliberately hid it. Say so explicitly — "Unknown identifier" reads like a typo and
        // hides the real reason (the type is intentionally not exported).
        if (TryReportSecretTypeAccess(name: id.Name, location: id.Location))
        {
            return ErrorTypeInfo.Instance;
        }

        ReportError(code: SemanticDiagnosticCode.UnknownIdentifier,
            message:
            $"Unknown identifier '{id.Name}'.{DidYouMean(target: id.Name, candidates: IdentifierSuggestionCandidates())}",
            location: id.Location);
        return ErrorTypeInfo.Instance;
    }

    /// <summary>
    /// If a <c>secret</c> (module-private) type whose bare name matches <paramref name="name"/> exists
    /// in a module OTHER than the current one, resolution deliberately hid it — report a dedicated
    /// RF-S402 explaining that (instead of letting the caller emit a misleading "unknown type/identifier"
    /// that reads like a typo), and return true. Returns false when no such type exists.
    /// </summary>
    internal bool TryReportSecretTypeAccess(string name, SourceLocation location)
    {
        foreach (TypeInfo t in _registry.GetAllTypes())
        {
            if (t is { Visibility: VisibilityModifier.Secret }
                && t.Name == name
                && t.Module != _currentModuleName)
            {
                ReportError(code: SemanticDiagnosticCode.SecretTypeAccess,
                    message:
                    $"'{name}' is a secret (module-private) type of module '{t.Module}' " +
                    $"and cannot be used from module '{_currentModuleName ?? "?"}'.",
                    location: location);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Analyzes binary expressions that remain as BinaryExpression nodes after parsing.
    /// Note: Most arithmetic, comparison, and bitwise operators are desugared to method calls
    /// in the parser (e.g., a + b -> a.add(b)). This method only handles operators that
    /// are NOT desugared:
    /// - Assignment (=)
    /// - Logical operators (and, or) — require short-circuit evaluation
    /// - Membership/type operators (in, notin, is, isnot, obeys, disobeys)
    /// - None coalescing (??) — requires short-circuit evaluation
    /// </summary>
    private TypeSymbol AnalyzeBinaryExpression(BinaryExpression binary)
    {
        // Re-binding (lhs = rhs) revives a stolen-from identifier: clear deadref
        // BEFORE analyzing the LHS so the deadref-read check at line ~135 doesn't fire.
        if (binary is { Operator: BinaryOperator.Assign, Left: IdentifierExpression rebindId })
        {
            _deadrefVariables.Remove(item: rebindId.Name);
        }

        // `p isonly RHS` — analyze LHS first, then analyze RHS with flag-context so bare flag
        // names (READ, WRITE) and bare-name combinations (READ and WRITE) resolve against the
        // LHS flags type without needing the type qualifier.
        if (binary.Operator == BinaryOperator.IsOnly)
        {
            TypeSymbol lhsType = AnalyzeExpression(expression: binary.Left);
            TypeSymbol rhsType;
            if (lhsType is FlagsTypeInfo)
            {
                _flagsContextStack.Push(item: lhsType);
                try
                {
                    rhsType = AnalyzeExpression(expression: binary.Right);
                }
                finally
                {
                    _flagsContextStack.Pop();
                }
            }
            else
            {
                rhsType = AnalyzeExpression(expression: binary.Right);
                if (lhsType is not ErrorTypeInfo)
                {
                    ReportError(code: SemanticDiagnosticCode.FlagsTypeMismatch,
                        message:
                        $"'isonly' requires a flags value on the left side, but got '{lhsType.Name}'.",
                        location: binary.Location);
                }
            }

            if (lhsType is FlagsTypeInfo && rhsType is not ErrorTypeInfo &&
                rhsType.Name != lhsType.Name)
            {
                ReportError(code: SemanticDiagnosticCode.FlagsTypeMismatch,
                    message:
                    $"'isonly' requires both operands to be the same flags type, but got '{lhsType.Name}' and '{rhsType.Name}'.",
                    location: binary.Location);
            }

            return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;
        }

        // TODO: This should be done with not operator, but with member routines.
        TypeSymbol leftType = AnalyzeExpression(expression: binary.Left);
        // Pass leftType as expected for assignments so RHS literals like `none`
        // see the target's carrier-slot type as their contextual expected type.
        TypeSymbol rightType = binary.Operator == BinaryOperator.Assign
            ? AnalyzeExpression(expression: binary.Right, expectedType: leftType)
            : AnalyzeExpression(expression: binary.Right);

        // Re-infer unsuffixed integer literals against the typed peer so
        // comparisons like 'me.strong_count == 0' don't default the literal to S64.
        if (binary.Right is LiteralExpression { LiteralType: TokenType.IntegerLiteral or TokenType.S64Literal or TokenType.UndecidedInteger } &&
            IsFixedWidthIntegerType(type: leftType) && leftType.Name != rightType.Name)
        {
            rightType = AnalyzeExpression(expression: binary.Right, expectedType: leftType);
        }
        else if (binary.Left is LiteralExpression { LiteralType: TokenType.IntegerLiteral or TokenType.S64Literal or TokenType.UndecidedInteger } &&
                 IsFixedWidthIntegerType(type: rightType) && leftType.Name != rightType.Name)
        {
            leftType = AnalyzeExpression(expression: binary.Left, expectedType: rightType);
        }

        switch (binary.Operator)
        {
            // Handle assignment operator
            case BinaryOperator.Assign:
                return AnalyzeAssignmentExpression(target: binary.Left,
                    value: binary.Right,
                    targetType: leftType,
                    valueType: rightType,
                    location: binary.Location);
            // Handle flags removal operator (but) — removes flags from a value
            case BinaryOperator.But when leftType is not FlagsTypeInfo:
                ReportError(code: SemanticDiagnosticCode.FlagsTypeMismatch,
                    message:
                    $"'but' operator requires a flags type on the left side, but got '{leftType.Name}'.",
                    location: binary.Location);
                return ErrorTypeInfo.Instance;
            case BinaryOperator.But when rightType is not FlagsTypeInfo:
                ReportError(code: SemanticDiagnosticCode.FlagsTypeMismatch,
                    message:
                    $"'but' operator requires a flags type on the right side, but got '{rightType.Name}'.",
                    location: binary.Location);
                return ErrorTypeInfo.Instance;
            case BinaryOperator.But when leftType.Name != rightType.Name:
                ReportError(code: SemanticDiagnosticCode.FlagsTypeMismatch,
                    message:
                    $"'but' operator requires both operands to be the same flags type, but got '{leftType.Name}' and '{rightType.Name}'.",
                    location: binary.Location);
                return ErrorTypeInfo.Instance;
            case BinaryOperator.But:
                return leftType;
            // #128: 'or' cannot be used to combine flags outside is/isnot/isonly tests
            case BinaryOperator.Or when
                (leftType is FlagsTypeInfo || rightType is FlagsTypeInfo):
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
        string? operatorMethod = binary.Operator.GetMethodName();
        if (operatorMethod != null)
        {
            switch (leftType)
            {
                case ChoiceTypeInfo:
                    ReportError(code: SemanticDiagnosticCode.ArithmeticOnChoiceType,
                        message:
                        $"Operator '{binary.Operator.ToStringRepresentation()}' cannot be used with choice type '{leftType.Name}'. Use 'is' for case matching.",
                        location: binary.Location);
                    return ErrorTypeInfo.Instance;
                case FlagsTypeInfo
                    when binary.Operator is not (BinaryOperator.Equal or BinaryOperator.NotEqual):
                    ReportError(code: SemanticDiagnosticCode.ArithmeticOnFlagsType,
                        message:
                        $"Operator '{binary.Operator.ToStringRepresentation()}' cannot be used " +
                        $"with flags type '{leftType.Name}'. Use 'is'/'isnot'/'but'/'isonly' for " +
                        $"" +
                        $"flag" +
                        $" operations.",
                        location: binary.Location);
                    return ErrorTypeInfo.Instance;
            }
        }

        // #117: Fixed-width numeric types must match exactly (S32 + S64 = error)
        // System types (Address) are exempt. Shift operators are also exempt because
        // they intentionally use U32 for the shift amount regardless of the left-operand type.
        if (leftType.Name != rightType.Name && IsFixedWidthNumericType(type: leftType) &&
            IsFixedWidthNumericType(type: rightType) && !IsLogicalOperator(op: binary.Operator) &&
            !IsComparisonOperator(op: binary.Operator) && !IsShiftOperator(op: binary.Operator))
        {
            ReportError(code: SemanticDiagnosticCode.FixedWidthTypeMismatch,
                message:
                $"Fixed-width type mismatch: '{leftType.Name}' and '{rightType.Name}'. Explicit conversion required.",
                location: binary.Location);
            return ErrorTypeInfo.Instance;
        }

        switch (binary.Operator)
        {
            // S854: Unchecked operators require a danger block or @dangerous routine
            case BinaryOperator.AddUnchecked or BinaryOperator.SubtractUnchecked
                or BinaryOperator.MultiplyUnchecked or BinaryOperator.TrueDivideUnchecked
                or BinaryOperator.FloorDivideUnchecked or BinaryOperator.ModuloUnchecked
                or BinaryOperator.PowerUnchecked when !InDangerBlock:
                ReportError(code: SemanticDiagnosticCode.UncheckedOperatorOutsideDanger,
                    message: $"Unchecked operator '{binary.Operator.ToStringRepresentation()}' " +
                             "requires a 'danger' block or '@dangerous' routine.",
                    location: binary.Location);
                return ErrorTypeInfo.Instance;
            // Flags combination: A and B -> bitwise OR (combines flags)
            case BinaryOperator.And when leftType is FlagsTypeInfo &&
                                         leftType.Name == rightType.Name:
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
        // Includes overloadable (==, !=, <, <=, >, >=, in, notin) and non-overloadable (is, isnot, obeys, disobeys)
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
            if (IsCarrierType(type: leftType) && leftType.TypeArguments is { Count: > 0 })
            {
                return leftType.TypeArguments[index: 0];
            }

            // User type — look up $unwrap_or method
            RoutineInfo? unwrapOrMethod =
                _registry.LookupMethod(type: leftType, methodName: "unwrap_or");
            if (unwrapOrMethod != null)
            {
                return unwrapOrMethod.ReturnType ?? rightType;
            }

            ReportError(code: SemanticDiagnosticCode.TypeDoesNotSupportOperator,
                message: $"Type '{leftType.Name}' does not support the '??' operator. " +
                         "Implement '$unwrap_or(default: T) -> T' to enable none coalescing.",
                location: binary.Location);
            return ErrorTypeInfo.Instance;
        }

        // Validate RHS type against the operator method's parameter type
        string? methodName = binary.Operator.GetMethodName();
        if (methodName == null)
        {
            return leftType;
        }

        RoutineInfo? method =
            _registry.LookupMethodOverload(type: leftType,
                methodName: methodName,
                argTypes: [rightType]) ??
            _registry.LookupMethod(type: leftType, methodName: methodName);

        // Apply failable-call checking for integer arithmetic operators.
        // Floats (F16/F32/F64/F128) and software decimals (D32/D64/D128) are excluded
        // because the codegen emits raw float instructions (fadd/fmul/...) for them,
        // bypassing the checked dispatch path.
        bool isIntegerCheckedOp = method is { IsFailable: true } && leftType is RecordTypeInfo
                                  {
                                      HasDirectBackendType: true, LlvmType: { } ltIr
                                  } &&
                                  ltIr.StartsWith('i') && ltIr != "i1";
        if (isIntegerCheckedOp && _currentRoutine != null)
        {
            _currentRoutine.HasFailableCalls = true;
            if (!_currentRoutine.IsFailable && _currentRoutine.Name != "start" &&
                !_currentRoutine.IsSynthesized)
            {
                string opStr = binary.Operator.ToStringRepresentation();
                ReportWarning(code: SemanticWarningCode.UnhandledCrashableCall,
                    message: $"Operator '{opStr}' may throw. Either make the enclosing routine " +
                             "failable (!), use 'when' to handle the error, or use the wrapping " +
                             $"variant '{opStr}%' for silent overflow.",
                    location: binary.Location);
            }
        }

        if (method is not { Parameters.Count: > 0 })
        {
            return leftType;
        }

        TypeSymbol paramType = method.Parameters[index: 0].Type;

        // Substitute Me -> leftType for protocol-sourced methods
        if (paramType is ProtocolSelfTypeInfo)
        {
            paramType = leftType;
        }

        // Contextually infer unsuffixed integer literals against the operator
        // parameter type so stdlib/operator lowering does not inherit a stale S64.
        if (binary.Right is LiteralExpression { LiteralType: TokenType.IntegerLiteral or TokenType.S64Literal } &&
            IsFixedWidthIntegerType(type: paramType))
        {
            rightType = AnalyzeExpression(expression: binary.Right, expectedType: paramType);
        }

        bool allowIntegralShiftAmount = IsShiftOperator(op: binary.Operator) &&
                                        IsIntegerType(type: rightType) &&
                                        IsIntegerType(type: paramType);

        if (!allowIntegralShiftAmount && !IsAssignableTo(source: rightType, target: paramType))
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

        // Default: return left type
        // This handles any edge cases that might slip through
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
            if (valueType is TupleTypeInfo tupleType &&
                tupleLhs.Elements.Count != tupleType.ElementTypes.Count)
            {
                ReportError(code: SemanticDiagnosticCode.DestructuringArityMismatch,
                    message:
                    $"Tuple destructuring has {tupleLhs.Elements.Count} targets but the value has {tupleType.ElementTypes.Count} elements.",
                    location: location);
            }

            return targetType;
        }

        // Check if target is assignable (variable, member variable, or index)
        if (!IsAssignableTarget(target: target))
        {
            ReportError(code: SemanticDiagnosticCode.InvalidAssignmentTarget,
                message: "Invalid assignment target. Only variables, member accesses (e.g. obj.field), and indexed expressions (e.g. list[i]) can be assigned to.",
                location: target.Location);
            return targetType;
        }

        switch (target)
        {
            // Check modifiability for variable assignments
            case IdentifierExpression id:
            {
                VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
                if (varInfo is { IsModifiable: false })
                {
                    ReportError(code: SemanticDiagnosticCode.AssignmentToImmutable,
                        message: $"Cannot assign to preset variable '{id.Name}'.",
                        location: location);
                }

                // Suflae flow typing: reassigning an entity reference re-derives its nullability.
                if (_registry.Language == Language.Suflae && varInfo != null &&
                    IsEntityRefType(type: varInfo.Type))
                {
                    bool valueNullable = IsNullableEntityRead(expr: value);
                    if (varInfo.IsNullable)
                    {
                        // A nullable local: a possibly-none RHS re-nullifies it (shadowing any prior
                        // null-check); a non-null RHS proves it non-none for the rest of this flow.
                        if (valueNullable)
                        {
                            _registry.MarkVariableNullableAgain(name: id.Name);
                        }
                        else
                        {
                            _registry.MarkVariableNonNull(name: id.Name);
                        }
                    }
                    else if (valueNullable)
                    {
                        // A non-null local cannot take a possibly-none value.
                        ReportNullableIntoNonNull(target: $"variable '{id.Name}'", value: value,
                            optionalHint: $"{id.Name}: <Type>?");
                    }
                }

                break;
            }
            // Validate member variable write access (setter visibility)
            case MemberExpression member:
            {
                TypeSymbol objectType = AnalyzeExpression(expression: member.Object);

                // Read-only wrapper types (Viewing, Inspecting) cannot be written through
                if (IsReadOnlyWrapper(type: objectType))
                {
                    ReportError(code: SemanticDiagnosticCode.WriteThroughReadOnlyWrapper,
                        message:
                        $"Cannot write to member '{member.MemberName}' through read-only wrapper '{objectType.Name}'. " +
                        "Use Modifying[T] for exclusive write access or Claiming[T] for locked write access.",
                        location: location);
                }

                ValidateMemberVariableWriteAccess(objectType: objectType,
                    memberVariableName: member.MemberName,
                    location: location);

                // Suflae: a NON-NULLABLE entity field (`x: E`) rejects `o.x = <possibly-none>` — literal
                // `none` or an unchecked `E?` read. Only an optional field (`x: E?`) may hold a null Roamed
                // handle. Mirrors the construction check; the field's IsNullable is set in TypeBodyResolver.
                if (_registry.Language == Language.Suflae &&
                    objectType is EntityTypeInfo writeEntity &&
                    writeEntity.LookupMemberVariable(memberVariableName: member.MemberName) is
                        { IsNullable: false, Type: RecordTypeInfo
                            { GenericDefinition.Name: Compiler.Resolution.RuntimeContract.Roamed } } writeField &&
                    IsNullableEntityRead(expr: value))
                {
                    ReportNullableIntoNonNull(target: $"field '{writeField.Name}'",
                        value: value, optionalHint: $"{writeField.Name}: <Type>?");
                }

                // Check if we're in a @readonly method trying to modify 'me'
                if (_currentRoutine is { IsReadOnly: true } &&
                    member.Object is IdentifierExpression { Name: "me" })
                {
                    ReportError(code: SemanticDiagnosticCode.MutationInReadonlyMethod,
                        message:
                        $"Cannot mutate member variable '{member.MemberName}' in a @readonly method. " +
                        "Use @migratable to allow mutations.",
                        location: location);
                }

                break;
            }
            // Check modifiability for index assignments
            case IndexExpression index:
            {
                TypeSymbol indexedObjectType = AnalyzeExpression(expression: index.Object);

                // Failability: lookup $setitem on the indexed type and propagate `!` to caller.
                // `arr[i] = v` desugars to `arr.setitem!(i, v)` for failable indexers; a
                // non-failable caller must mark HasFailableCalls so its `!` decl is justified.
                TryGetTransparentProtocolTarget(type: indexedObjectType,
                    targetType: out TypeSymbol setLookupType);
                RoutineInfo? setItem = _registry.LookupMethod(type: setLookupType,
                    methodName: "setitem") ?? _registry.LookupMethod(type: setLookupType,
                    methodName: "setitem", isFailable: true);
                if (setItem is { IsFailable: true } && _currentRoutine != null)
                {
                    _currentRoutine.HasFailableCalls = true;
                    _currentRoutine.FailableCallees.Add(setItem);
                }

                if (IsReadOnlyTransparentProtocol(type: indexedObjectType))
                {
                    ReportError(code: SemanticDiagnosticCode.WriteThroughReadOnlyWrapper,
                        message:
                        $"Cannot write through index access on read-only protocol '{indexedObjectType.Name}'. " +
                        "Use Controlling[T] or a writable token instead.",
                        location: location);
                }

                // The object being indexed must be modifiable
                if (index.Object is IdentifierExpression indexedVar)
                {
                    VariableInfo? varInfo = _registry.LookupVariable(name: indexedVar.Name);
                    if (varInfo is { IsModifiable: false })
                    {
                        ReportError(code: SemanticDiagnosticCode.AssignmentToImmutable,
                            message:
                            $"Cannot assign to index of preset variable '{indexedVar.Name}'.",
                            location: location);
                    }
                }

                break;
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

        // Phase 1: warn when the RHS is a borrowed reference and the type is not trivially
        // copyable. See AnalyzeVariableDeclaration for the same rule applied to var initializers.
        if (_registry.Language == Language.RazorForge &&
            value is IdentifierExpression or MemberExpression &&
            !IsTriviallyCopyable(type: valueType))
        {
            var hint = FindNonTriviallyCopyableWrapper(type: valueType);
            if (hint != null)
            {
                string verb = NonTriviallyCopyableWrappers[key: hint.Value.Wrapper];
                string fieldNote = hint.Value.Path == "<value>"
                    ? $"value of type '{valueType.Name}' is a '{hint.Value.Wrapper}[…]' wrapper"
                    : $"field '{hint.Value.Path}' of type '{hint.Value.Wrapper}[…]'";
                ReportError(code: SemanticDiagnosticCode.ImplicitWrapperCopy,
                    message:
                    $"Implicit copy in assignment: {fieldNote} requires an explicit copy verb. " +
                    $"Spell out '{verb}' at the copy site, or reconstruct the record with each field's verb.",
                    location: location);
            }
        }

        // Check type compatibility
        if (!IsAssignableTo(source: valueType, target: targetType))
        {
            ReportError(code: SemanticDiagnosticCode.AssignmentTypeMismatch,
                message:
                $"Cannot assign value of type '{valueType.Name}' to target of type '{targetType.Name}'.",
                location: location);
        }

        // Variant reassignment is gated by Assignability (variant is Assignable iff every
        // member is). The general AssignmentTypeMismatch/Assignable checks above already
        // enforce this through the structural rule — no variant-specific check needed.

        // #42: ??= narrowing — `a ??= b` is expanded to `a = a ?? b`
        // When assigning `target = target ?? default` where target is Maybe[T],
        // narrow the variable to T after the coalescing assignment.
        if (target is IdentifierExpression narrowId &&
            value is BinaryExpression { Operator: BinaryOperator.NoneCoalesce } &&
            IsMaybeType(type: targetType) && targetType.TypeArguments is { Count: > 0 })
        {
            _registry.NarrowVariable(name: narrowId.Name,
                narrowedType: targetType.TypeArguments[index: 0]);
        }

        // Assignment expression returns the target type
        return targetType;
    }

    /// <summary>
    /// Analyzes a compound assignment expression (e.g., a += b).
    /// Dispatch order: (0) verify target is var, (1) try in-place wired ($iadd) -> Blank,
    /// (2) fallback to create-and-assign ($add), (3) error if neither exists.
    /// </summary>
    private TypeSymbol AnalyzeCompoundAssignment(CompoundAssignmentExpression compound) // NOSONAR S3776
    {
        TypeSymbol targetType = AnalyzeExpression(expression: compound.Target);
        // Analyze the RHS too — without this, constructor calls like `n += S64(5)`
        // never get classified as TypeConstructor and reach codegen unlowered (S959).
        AnalyzeExpression(expression: compound.Value);

        // Step 0: Verify target is assignable and modifiable
        if (!IsAssignableTarget(target: compound.Target))
        {
            ReportError(code: SemanticDiagnosticCode.InvalidAssignmentTarget,
                message: "Invalid compound assignment target. Only variables, member accesses (e.g. obj.field), and indexed expressions (e.g. list[i]) can be the target of `+=`, `-=`, etc.",
                location: compound.Target.Location);
            return targetType;
        }

        switch (compound.Target)
        {
            case IdentifierExpression id:
            {
                VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
                if (varInfo is { IsModifiable: false })
                {
                    ReportError(code: SemanticDiagnosticCode.AssignmentToImmutable,
                        message: $"Cannot assign to preset variable '{id.Name}'.",
                        location: compound.Location);
                }

                break;
            }
            case MemberExpression member:
            {
                TypeSymbol objectType = AnalyzeExpression(expression: member.Object);
                ValidateMemberVariableWriteAccess(objectType: objectType,
                    memberVariableName: member.MemberName,
                    location: compound.Location);

                if (_currentRoutine is { IsReadOnly: true } &&
                    member.Object is IdentifierExpression { Name: "me" })
                {
                    ReportError(code: SemanticDiagnosticCode.MutationInReadonlyMethod,
                        message:
                        $"Cannot mutate member variable '{member.MemberName}' in a @readonly method. " +
                        "Use @migratable to allow mutations.",
                        location: compound.Location);
                }

                break;
            }
            case IndexExpression index:
            {
                TypeSymbol indexedObjectType = AnalyzeExpression(expression: index.Object);
                if (IsReadOnlyTransparentProtocol(type: indexedObjectType))
                {
                    ReportError(code: SemanticDiagnosticCode.WriteThroughReadOnlyWrapper,
                        message:
                        $"Cannot write through index access on read-only protocol '{indexedObjectType.Name}'. " +
                        "Use Controlling[T] or a writable token instead.",
                        location: compound.Location);
                }

                if (index.Object is IdentifierExpression indexedVar)
                {
                    VariableInfo? varInfo = _registry.LookupVariable(name: indexedVar.Name);
                    if (varInfo is { IsModifiable: false })
                    {
                        ReportError(code: SemanticDiagnosticCode.AssignmentToImmutable,
                            message:
                            $"Cannot assign to index of preset variable '{indexedVar.Name}'.",
                            location: compound.Location);
                    }
                }

                break;
            }
        }

        // #67: Cannot use compound assignment on read-only token (Viewing or Inspecting)
        if (targetType is WrapperTypeInfo { IsReadOnly: true } readOnlyWrapper)
        {
            ReportError(code: SemanticDiagnosticCode.CompoundAssignmentOnReadOnlyToken,
                message:
                $"Cannot use compound assignment on read-only token '{readOnlyWrapper.Name}'. " +
                "Read-only tokens (Viewing, Inspecting) do not allow modifications.",
                location: compound.Location);
            return ErrorTypeInfo.Instance;
        }

        // Don't try dispatch on error types (prevent cascade)
        if (targetType.Category == TypeCategory.Error)
        {
            return targetType;
        }

        switch (targetType)
        {
            // Choice types cannot use compound assignment — choices do not support operators
            case ChoiceTypeInfo:
                ReportError(code: SemanticDiagnosticCode.ArithmeticOnChoiceType,
                    message:
                    $"Operator '{compound.Operator.ToStringRepresentation()}=' cannot be used with choice type '{targetType.Name}'. " +
                    "Choice types do not support operators. Use 'is' for case matching.",
                    location: compound.Location);
                return ErrorTypeInfo.Instance;
            // #134: Flags types cannot use arithmetic or compound assignment operators
            case FlagsTypeInfo:
                ReportError(code: SemanticDiagnosticCode.ArithmeticOnFlagsType,
                    message:
                    $"Operator '{compound.Operator.ToStringRepresentation()}=' cannot be used with flags type '{targetType.Name}'. " +
                    "Use 'but' to remove flags and 'is'/'isnot'/'isonly' to test flags.",
                    location: compound.Location);
                return ErrorTypeInfo.Instance;
        }

        string? inPlaceMethod = compound.Operator.GetInPlaceMethodName();
        string? regularMethod = compound.Operator.GetMethodName();

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

        // Step 2: Fallback to create-and-assign (a = a.add(b)) — not allowed for entity types
        if (targetType.Category == TypeCategory.Entity)
        {
            string opSymbol = compound.Operator.ToStringRepresentation();
            ReportError(code: SemanticDiagnosticCode.CompoundAssignmentNotSupported,
                message:
                $"Entity type '{targetType.Name}' does not support compound assignment '{opSymbol}='. " +
                $"Define in-place operator '{inPlaceMethod}' (with @migratable) to allow compound assignment.",
                location: compound.Location);
            return ErrorTypeInfo.Instance;
        }

        if (regularMethod == null)
        {
            string opSymbol = compound.Operator.ToStringRepresentation();
            ReportError(code: SemanticDiagnosticCode.CompoundAssignmentNotSupported,
                message:
                $"Type '{targetType.Name}' does not support compound assignment '{opSymbol}='. " +
                $"Define in-place operator '{inPlaceMethod}' or regular operator '{regularMethod}'.",
                location: compound.Location);

            return ErrorTypeInfo.Instance;
        }

        RoutineInfo? regularRoutine =
            _registry.LookupRoutine(fullName: $"{targetType.Name}.{regularMethod}");
        if (regularRoutine != null)
        {
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

        // Step 3: neither in-place nor regular operator found on this type.
        ReportError(code: SemanticDiagnosticCode.CompoundAssignmentNotSupported,
            message:
            $"Type '{targetType.Name}' does not support compound assignment '{compound.Operator.ToStringRepresentation()}='. " +
            $"Define in-place operator '{inPlaceMethod}' or regular operator '{regularMethod}'.",
            location: compound.Location);
        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeUnaryExpression(UnaryExpression unary) // NOSONAR S3776
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
                if (operandType != ErrorTypeInfo.Instance &&
                    !IsNumericType(type: operandType) &&
                    _registry.LookupMethod(type: operandType, methodName: "neg") == null)
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
                if (IsCarrierType(type: operandType) &&
                    operandType.TypeArguments is { Count: > 0 })
                {
                    TypeSymbol inner = operandType.TypeArguments[index: 0];
                    // `Maybe[T]!!` yields `Modifying[T]` — the unwrap
                    // is an exclusive scope-bound borrow, not a copy. Same LLVM repr (ptr), but
                    // typed as Modifying so the destructor scheduler skips it.
                    if (IsMaybeType(type: operandType) &&
                        IsOwnedOf(type: inner, out TypeSymbol ownedInner))
                    {
                        return _registry.GetOrCreateWrapperType(
                            wrapperName: Compiler.Resolution.RuntimeContract.Modifying,
                            innerType: ownedInner,
                            isReadOnly: false);
                    }
                    return inner;
                }

                // User type — look up $unwrap method
            {
                RoutineInfo? unwrapMethod =
                    _registry.LookupMethod(type: operandType, methodName: "unwrap");
                if (unwrapMethod != null)
                {
                    return unwrapMethod.ReturnType ?? ErrorTypeInfo.Instance;
                }

                ReportError(code: SemanticDiagnosticCode.TypeDoesNotSupportOperator,
                    message: $"Type '{operandType.Name}' does not support the '!!' operator. " +
                             "Implement '$unwrap() -> T' to enable force unwrap.",
                    location: unary.Location);
                return ErrorTypeInfo.Instance;
            }

            case UnaryOperator.Steal:
            default:
                return operandType;
        }
    }

    #endregion
}
