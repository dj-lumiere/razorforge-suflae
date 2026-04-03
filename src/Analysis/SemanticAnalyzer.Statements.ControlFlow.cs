namespace SemanticAnalysis;

using Enums;
using Symbols;
using Types;
using SyntaxTree;
using Diagnostics;
using TypeSymbol = Types.TypeInfo;

public sealed partial class SemanticAnalyzer
{
    private void AnalyzeIfStatement(IfStatement ifStmt)
    {
        TypeSymbol conditionType = AnalyzeExpression(expression: ifStmt.Condition);

        // Condition must be boolean
        if (!IsBoolType(type: conditionType))
        {
            ReportError(code: SemanticDiagnosticCode.IfConditionNotBool,
                message: $"If condition must be boolean, got '{conditionType.Name}'.",
                location: ifStmt.Condition.Location);
        }

        // Extract narrowing info from condition
        NarrowingInfo? narrowing = TryExtractNarrowingFromCondition(condition: ifStmt.Condition);

        // Analyze then branch (with narrowing if applicable)
        if (narrowing?.ThenBranchType != null)
        {
            _registry.EnterScope(kind: ScopeKind.Block, name: "if_then");
            _registry.NarrowVariable(name: narrowing.VariableName,
                narrowedType: narrowing.ThenBranchType);
            AnalyzeStatement(statement: ifStmt.ThenStatement);
            _registry.ExitScope();
        }
        else
        {
            AnalyzeStatement(statement: ifStmt.ThenStatement);
        }

        // Analyze else branch if present (with inverse narrowing if applicable)
        if (ifStmt.ElseStatement != null)
        {
            if (narrowing?.ElseBranchType != null)
            {
                _registry.EnterScope(kind: ScopeKind.Block, name: "if_else");
                _registry.NarrowVariable(name: narrowing.VariableName,
                    narrowedType: narrowing.ElseBranchType);
                AnalyzeStatement(statement: ifStmt.ElseStatement);
                _registry.ExitScope();
            }
            else
            {
                AnalyzeStatement(statement: ifStmt.ElseStatement);
            }
        }

        // Guard clause narrowing: if the then branch definitely exits,
        // apply else narrowing to the remainder of the current scope
        if (ifStmt.ElseStatement == null && narrowing?.ElseBranchType != null &&
            HasDefiniteExit(statement: ifStmt.ThenStatement))
        {
            _registry.NarrowVariable(name: narrowing.VariableName,
                narrowedType: narrowing.ElseBranchType);
        }
    }

    private void AnalyzeWhileStatement(WhileStatement whileStmt)
    {
        TypeSymbol conditionType = AnalyzeExpression(expression: whileStmt.Condition);

        // Condition must be boolean
        if (!IsBoolType(type: conditionType))
        {
            ReportError(code: SemanticDiagnosticCode.WhileConditionNotBool,
                message: $"While condition must be boolean, got '{conditionType.Name}'.",
                location: whileStmt.Condition.Location);
        }

        // Analyze loop body
        _registry.EnterScope(kind: ScopeKind.Loop, name: "while");
        AnalyzeStatement(statement: whileStmt.Body);
        _registry.ExitScope();
    }

    private void AnalyzeForStatement(ForStatement forStmt)
    {
        _registry.EnterScope(kind: ScopeKind.Loop, name: "for");

        // Analyze iterable expression
        TypeSymbol iterableType = AnalyzeExpression(expression: forStmt.Iterable);

        // Get element type from iterable
        TypeSymbol elementType =
            GetIterableElementType(iterableType: iterableType, location: forStmt.Location);

        // Handle either simple variable or destructuring pattern
        if (forStmt.Variable != null)
        {
            // Simple variable binding: for item in items
            _registry.DeclareVariable(name: forStmt.Variable, type: elementType);
        }
        else if (forStmt.VariablePattern != null)
        {
            // Destructuring pattern: for (index, item) in items.enumerate()
            if (elementType is TupleTypeInfo tupleType)
            {
                // Check arity match
                int bindingCount = forStmt.VariablePattern.Bindings.Count;
                if (bindingCount != tupleType.Arity)
                {
                    ReportError(code: SemanticDiagnosticCode.DestructuringArityMismatch,
                        message:
                        $"Destructuring pattern has {bindingCount} bindings but tuple has {tupleType.Arity} elements.",
                        location: forStmt.VariablePattern.Location);
                }

                // Declare each binding with its corresponding tuple element type
                for (int i = 0; i < forStmt.VariablePattern.Bindings.Count; i++)
                {
                    DestructuringBinding binding = forStmt.VariablePattern.Bindings[index: i];
                    if (binding.BindingName != null)
                    {
                        TypeSymbol bindingType = i < tupleType.Arity
                            ? (TypeSymbol)tupleType.ElementTypes[index: i]
                            : ErrorTypeInfo.Instance;
                        _registry.DeclareVariable(name: binding.BindingName, type: bindingType);
                    }
                }
            }
            else
            {
                // Non-tuple type with destructuring pattern
                ReportError(code: SemanticDiagnosticCode.DestructuringArityMismatch,
                    message:
                    $"Cannot destructure non-tuple type '{elementType.Name}' in for loop.",
                    location: forStmt.VariablePattern.Location);
                // Still declare variables with error type so analysis can continue
                foreach (DestructuringBinding binding in forStmt.VariablePattern.Bindings)
                {
                    if (binding.BindingName != null)
                    {
                        _registry.DeclareVariable(name: binding.BindingName,
                            type: ErrorTypeInfo.Instance);
                    }
                }
            }
        }

        // #22: Track active iteration source for migratable-during-iteration check
        string? iterationSourceName = forStmt.Iterable is IdentifierExpression iterSource
            ? iterSource.Name
            : null;

        if (iterationSourceName != null)
        {
            _activeIterationSources.Add(item: iterationSourceName);
        }

        // Analyze loop body
        AnalyzeStatement(statement: forStmt.Body);

        if (iterationSourceName != null)
        {
            _activeIterationSources.Remove(item: iterationSourceName);
        }

        _registry.ExitScope();
    }

    private void AnalyzeWhenStatement(WhenStatement whenStmt)
    {
        TypeSymbol matchedType = AnalyzeExpression(expression: whenStmt.Expression);

        // #161: Mark Lookup variable as dismantled when targeted by 'when'
        if (whenStmt.Expression is IdentifierExpression whenTarget)
        {
            _pendingLookupVars.RemoveAll(match: v => v.Name == whenTarget.Name);
        }

        // #88: Pattern order enforcement — else/wildcard must be last, detect unreachable patterns
        {
            bool seenElse = false;
            foreach (WhenClause clause in whenStmt.Clauses)
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
            foreach (WhenClause clause in whenStmt.Clauses)
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

        // Track handled patterns for narrowing the else clause
        bool handledNone = false;
        bool handledCrashable = false;

        foreach (WhenClause clause in whenStmt.Clauses)
        {
            _registry.EnterScope(kind: ScopeKind.Block, name: "when_clause");

            // Track which patterns are handled (before the else clause)
            if (IsNonePattern(pattern: clause.Pattern))
            {
                handledNone = true;
            }
            else if (IsCrashablePattern(pattern: clause.Pattern))
            {
                handledCrashable = true;
            }

            switch (clause.Pattern)
            {
                case ElsePattern elsePat when matchedType is ErrorHandlingTypeInfo ehType:
                {
                    // Compute narrowed type for else clause binding
                    TypeSymbol? narrowedType = ComputeNarrowedType(type: ehType,
                        eliminateNone: handledNone,
                        eliminateCrashable: handledCrashable);

                    if (narrowedType != null && elsePat.VariableName != null)
                    {
                        // Declare with narrowed type instead of original matchedType
                        DeclarePatternVariable(name: elsePat.VariableName,
                            type: narrowedType,
                            location: elsePat.Location);
                        AnalyzeStatement(statement: clause.Body);
                        _registry.ExitScope();
                        continue;
                    }

                    break;
                }
            }

            // Analyze pattern and bind variables
            AnalyzePattern(pattern: clause.Pattern, matchedType: matchedType);

            // Analyze clause body
            AnalyzeStatement(statement: clause.Body);

            _registry.ExitScope();
        }

        // Check exhaustiveness for enumerable types (choice, variant, error-handling)
        if (matchedType is ChoiceTypeInfo or VariantTypeInfo or ErrorHandlingTypeInfo)
        {
            bool hasCatchAll = whenStmt.Clauses.Any(predicate: c =>
                c.Pattern is WildcardPattern or ElsePattern or IdentifierPattern);

            if (!hasCatchAll)
            {
                ExhaustivenessResult exhaustiveness = CheckExhaustiveness(
                    clauses: whenStmt.Clauses,
                    matchedType: matchedType);

                if (!exhaustiveness.IsExhaustive)
                {
                    string missing = exhaustiveness.MissingCases.Count > 0
                        ? $" Missing cases: {string.Join(separator: ", ", values: exhaustiveness.MissingCases)}."
                        : "";

                    // #89: Result/Lookup missing Crashable catch-all is an error, not a warning
                    if (matchedType is ErrorHandlingTypeInfo
                        {
                            Kind: ErrorHandlingKind.Result or ErrorHandlingKind.Lookup
                        } && exhaustiveness.MissingCases.Contains(item: "Crashable"))
                    {
                        ReportError(code: SemanticDiagnosticCode.NonExhaustiveMatch,
                            message:
                            $"Pattern match on '{matchedType.Name}' requires a 'Crashable' catch-all arm.{missing}",
                            location: whenStmt.Location);
                    }
                    else
                    {
                        ReportWarning(code: SemanticWarningCode.NonExhaustiveWhen,
                            message:
                            $"When statement may not cover all cases of '{matchedType.Name}'.{missing}",
                            location: whenStmt.Location);
                    }
                }
            }
        }
    }

    private void AnalyzeReturnStatement(ReturnStatement ret)
    {
        if (_currentRoutine == null)
        {
            ReportError(code: SemanticDiagnosticCode.ReturnOutsideFunction,
                message: "Return statement outside of function.",
                location: ret.Location);
            return;
        }

        if (ret.Value != null)
        {
            // Pass expected return type for contextual literal inference
            TypeSymbol returnType = AnalyzeExpression(expression: ret.Value,
                expectedType: _currentRoutine.ReturnType);

            // Validate that tokens cannot be returned (RazorForge only)
            ValidateNotTokenReturnType(type: returnType, location: ret.Location);

            if (_currentRoutine.ReturnType != null && !IsAssignableTo(source: returnType,
                    target: _currentRoutine.ReturnType))
            {
                ReportError(code: SemanticDiagnosticCode.ReturnTypeMismatch,
                    message:
                    $"Cannot return value of type '{returnType.Name}' from function expecting '{_currentRoutine.ReturnType.Name}'.",
                    location: ret.Location);
            }
        }
    }

    /// <summary>
    /// Analyzes a becomes statement (block result value).
    /// Becomes is used in multi-statement when/if branches to explicitly indicate the branch's result.
    /// </summary>
    private void AnalyzeBecomesStatement(BecomesStatement becomesStmt)
    {
        // Analyze the becomes expression
        // For now, we just validate the expression type - context validation
        // (checking that becomes appears in an appropriate block context) can be
        // added in a future phase when we track block expression contexts
        TypeSymbol becomesType = AnalyzeExpression(expression: becomesStmt.Value);

        // Validate that tokens cannot be block results (RazorForge only)
        ValidateNotTokenReturnType(type: becomesType, location: becomesStmt.Location);
    }

    private void AnalyzeThrowStatement(ThrowStatement throwStmt)
    {
        if (_currentRoutine == null || !_currentRoutine.IsFailable)
        {
            ReportError(code: SemanticDiagnosticCode.ThrowOutsideFailableFunction,
                message: "Throw statement is only allowed in failable functions (marked with !).",
                location: throwStmt.Location);
            return;
        }

        TypeSymbol errorType = AnalyzeExpression(expression: throwStmt.Error);

        // Error must explicitly obey Crashable — structural conformance is intentionally not used here,
        // as Crashable is a safety contract that requires an explicit declaration.
        if (!ExplicitlyImplementsProtocol(type: errorType, protocolName: "Crashable"))
        {
            ReportError(code: SemanticDiagnosticCode.ThrowNotCrashable,
                message:
                $"Thrown value must implement Crashable protocol, got '{errorType.Name}'.",
                location: throwStmt.Error.Location);
        }

        // Error types must be stack-safe named types that obey Crashable.
        // RazorForge stdlib already uses entity-backed errors such as IOError and InvalidValueError.
        if (errorType.Category != TypeCategory.Record &&
            errorType.Category != TypeCategory.Entity &&
            errorType is not ErrorTypeInfo &&
            errorType.Name != "Error")
        {
            ReportError(code: SemanticDiagnosticCode.ThrowRequiresRecordType,
                message:
                $"Only record or entity types can be thrown, got '{errorType.Name}' ({errorType.Category}). " +
                "Thrown errors must be named Crashable types.",
                location: throwStmt.Error.Location);
        }

        // Mark routine as having throw statements (for variant generation)
        _currentRoutine.HasThrow = true;
    }

    private void AnalyzeAbsentStatement(AbsentStatement absent)
    {
        if (_currentRoutine == null || !_currentRoutine.IsFailable)
        {
            ReportError(code: SemanticDiagnosticCode.AbsentOutsideFailableFunction,
                message: "Absent statement is only allowed in failable functions (marked with !).",
                location: absent.Location);
            return;
        }

        // Mark routine as having absent statements (for variant generation)
        _currentRoutine.HasAbsent = true;
    }

    private void AnalyzeBreakStatement(BreakStatement breakStmt)
    {
        if (!_registry.CurrentScope.IsInLoop)
        {
            ReportError(code: SemanticDiagnosticCode.BreakOutsideLoop,
                message: "Break statement is only allowed inside a loop.",
                location: breakStmt.Location);
        }
    }

    private void AnalyzeContinueStatement(ContinueStatement continueStmt)
    {
        if (!_registry.CurrentScope.IsInLoop)
        {
            ReportError(code: SemanticDiagnosticCode.ContinueOutsideLoop,
                message: "Continue statement is only allowed inside a loop.",
                location: continueStmt.Location);
        }
    }

    private void AnalyzeDestructuringStatement(DestructuringStatement destruct)
    {
        TypeSymbol initType = AnalyzeExpression(expression: destruct.Initializer);

        // Analyze the destructuring pattern and bind variables
        AnalyzeDestructuringPattern(pattern: destruct.Pattern, sourceType: initType);
    }

    /// <summary>
    /// Analyzes a discard statement (explicitly ignores a return value).
    /// Used to explicitly indicate that a routine's return value is intentionally ignored.
    /// </summary>
    private void AnalyzeDiscardStatement(DiscardStatement discard)
    {
        // discard must target a routine call, not an arbitrary expression like a literal or variable
        if (discard.Expression is not CallExpression)
        {
            ReportError(code: SemanticDiagnosticCode.InvalidDiscardTarget,
                message: "'discard' can only be used with routine calls. " +
                         "Use 'discard some_routine()' to explicitly ignore a return value.",
                location: discard.Location);
        }

        // Analyze the expression - this validates the expression and checks for errors
        // The result is intentionally discarded
        AnalyzeExpression(expression: discard.Expression);
    }

    /// <summary>
    /// #71: Validates emit statement — only allowed inside generator routines.
    /// </summary>
    private void AnalyzeEmitStatement(EmitStatement emitStmt)
    {
        // Analyze the emitted expression
        TypeSymbol emittedType = AnalyzeExpression(expression: emitStmt.Expression);

        // emit is only valid inside generator routines
        if (_currentRoutine == null)
        {
            ReportError(code: SemanticDiagnosticCode.EmitOutsideGenerator,
                message: "'emit' statement is only allowed inside generator routines.",
                location: emitStmt.Location);
            return;
        }

        // Mark current routine as generator
        _currentRoutineIsGenerator = true;

        // Validate return type is Sequence[T]
        if (_currentRoutine.ReturnType != null)
        {
            string returnName = GetBaseTypeName(typeName: _currentRoutine.ReturnType.Name);
            if (returnName != "Sequence")
            {
                ReportError(code: SemanticDiagnosticCode.GeneratorReturnType,
                    message:
                    $"Generator routine must return Sequence[T], not '{_currentRoutine.ReturnType.Name}'.",
                    location: emitStmt.Location);
            }
        }
    }

    private void AnalyzeDangerStatement(DangerStatement danger)
    {
        if (_registry.Language == Language.Suflae)
        {
            ReportError(code: SemanticDiagnosticCode.FeatureNotInSuflae,
                message: "Danger blocks are not available in Suflae.",
                location: danger.Location);
            return;
        }

        // Danger blocks cannot be nested
        if (InDangerBlock)
        {
            ReportError(code: SemanticDiagnosticCode.NestedDangerBlock,
                message: "Danger blocks cannot be nested.",
                location: danger.Location);
            return;
        }

        // Enter danger scope
        _registry.EnterScope(kind: ScopeKind.Block, name: "danger");
        _dangerBlockDepth = 1;

        try
        {
            AnalyzeBlockStatement(block: danger.Body);
        }
        finally
        {
            _dangerBlockDepth = 0;
            _registry.ExitScope();
        }
    }

    private void AnalyzeUsingStatement(UsingStatement usingStmt)
    {
        // Analyze the resource expression to get its type
        TypeSymbol resourceType = AnalyzeExpression(expression: usingStmt.Resource);

        // The bound variable type defaults to the resource type, but may be overridden
        // by $enter's return type when it returns non-void.
        TypeSymbol boundType = resourceType;

        // All using targets (tokens and resources alike) require $enter/$exit
        if (_registry.Language == Language.RazorForge)
        {
            // LookupMethod handles generic type fallback (e.g., Viewed[Point].$enter → Viewed.$enter)
            RoutineInfo? enterMethod =
                _registry.LookupMethod(type: resourceType, methodName: "$enter");
            RoutineInfo? exitMethod =
                _registry.LookupMethod(type: resourceType, methodName: "$exit");

            if (enterMethod == null || exitMethod == null)
            {
                ReportError(code: SemanticDiagnosticCode.UsingTargetMissingEnterExit,
                    message:
                    $"Using target of type '{resourceType.Name}' must implement '$enter' and '$exit' for resource management.",
                    location: usingStmt.Location);
            }
            else if (enterMethod.ReturnType != null && !enterMethod.ReturnType.IsBlank)
            {
                boundType = enterMethod.ReturnType;
            }
        }

        // Create a new scope for the using block
        _registry.EnterScope(kind: ScopeKind.Block, name: "using");

        // Declare the binding variable in the using scope
        _registry.DeclareVariable(name: usingStmt.Name, type: boundType);

        // Analyze the body
        AnalyzeStatement(statement: usingStmt.Body);

        // #171/#172: Token/resource scope escape — validate that the using-bound variable
        // is not returned or stored in outer scope (handled by ValidateNotTokenReturnType
        // for tokens, and conceptually enforced by scope exit for resources)

        _registry.ExitScope();
    }
}
