using System.Collections.Generic;
using System.Linq;
using Compiler.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Verification.Enums;

namespace Verification;

using TypeSymbol = TypeInfo;

public sealed partial class SemanticVerifier
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
                            ? tupleType.ElementTypes[index: i]
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

        // #130/#148: Duplicate pattern detection
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

        // Track handled patterns for narrowing the else clause
        bool handledNone = false;
        bool handledBlank = false;
        bool handledCrashable = false;

        foreach (WhenClause clause in whenStmt.Clauses)
        {
            _registry.EnterScope(kind: ScopeKind.Block, name: "when_clause");

            // Track which patterns are handled (before the else clause).
            // Maybe[T] and Lookup[T] absent state is matched by `is None`.
            // Result[T] has no absent state; only Crashable | T.
            string? carrierBase = GetCarrierBaseName(type: matchedType);
            bool carrierUsesNoneForAbsent = carrierBase is "Maybe" or "Lookup";
            if (carrierUsesNoneForAbsent && IsNonePattern(pattern: clause.Pattern))
            {
                handledNone = true;
            }
            else if (carrierBase == "Result" && IsBlankPattern(pattern: clause.Pattern))
            {
                handledBlank = true;
            }
            else if (IsCrashablePattern(pattern: clause.Pattern))
            {
                handledCrashable = true;
            }

            switch (clause.Pattern)
            {
                case ElsePattern elsePat when IsCarrierType(type: matchedType):
                {
                    // Compute narrowed type for else clause binding
                    TypeSymbol? narrowedType = ComputeNarrowedType(type: matchedType,
                        eliminateNone: handledNone,
                        eliminateBlank: handledBlank,
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

        // Check exhaustiveness for enumerable types (choice, variant, error-handling, Bool)
        if (matchedType is ChoiceTypeInfo or VariantTypeInfo || IsCarrierType(type: matchedType) ||
            IsBoolType(type: matchedType))
        {
            bool hasCatchAll = whenStmt.Clauses.Any(predicate: c =>
                c.Pattern is WildcardPattern or ElsePattern or IdentifierPattern);

            if (hasCatchAll)
            {
                _exhaustiveWhens.Add(item: whenStmt);
            }
            else
            {
                ExhaustivenessResult exhaustiveness = CheckExhaustiveness(
                    clauses: whenStmt.Clauses,
                    matchedType: matchedType);

                if (exhaustiveness.IsExhaustive)
                {
                    _exhaustiveWhens.Add(item: whenStmt);
                }
                else
                {
                    string missing = exhaustiveness.MissingCases.Count > 0
                        ? $" Missing cases: {string.Join(separator: ", ", values: exhaustiveness.MissingCases)}."
                        : "";

                    // #89: Result/Lookup missing Crashable catch-all is an error, not a warning
                    if (IsCarrierType(type: matchedType) && !IsMaybeType(type: matchedType) &&
                        exhaustiveness.MissingCases.Contains(item: "Crashable"))
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
            // Bare `return` is normalized by BlankReturnNormalizationPass to `return Blank`
            // with Location == ret.Location. Treat these as unreachable-path terminators:
            // skip the type check so exhaustive when-else blocks can end with bare `return`.
            bool isNormalizedBareReturn =
                ret.Value is IdentifierExpression { Name: "Blank" } blankId &&
                blankId.Location == ret.Location;

            // Pass expected return type for contextual literal inference
            TypeSymbol returnType = AnalyzeExpression(expression: ret.Value,
                expectedType: _currentRoutine.ReturnType);

            // Validate that tokens cannot be returned (RazorForge only)
            ValidateNotTokenReturnType(type: returnType, location: ret.Location);

            if (!isNormalizedBareReturn &&
                _currentRoutine.ReturnType != null && !IsAssignableTo(source: returnType,
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
        if (_currentRoutine == null)
        {
            ReportWarning(code: SemanticWarningCode.ThrowAbsentInNonFailable,
                message: "Throw statement outside any routine.",
                location: throwStmt.Location);
            return;
        }

        // `pierce` (IsFatal) is a fatal, uncatchable crash: it does NOT require the routine to be
        // failable and generates no variants. Only the recoverable `throw` needs the `!` contract.
        if (!throwStmt.IsFatal && !_currentRoutine.IsFailable)
        {
            ReportError(code: SemanticDiagnosticCode.ThrowOutsideFailableFunction,
                message: "Throw statement in a non-failable routine: add '!' suffix to signal " +
                         "callers and enable safe variant generation, or use `pierce` for an " +
                         "uncatchable fatal crash.",
                location: throwStmt.Location);
        }

        TypeSymbol errorType = AnalyzeExpression(expression: throwStmt.Error);

        // Only `crashable`-kind types are throwable errors. The `crashable` keyword implicitly
        // confers the Crashable contract; no other type kind may obey it (enforced at the
        // declaration site — see ValidateTypeProtocolImplementation), so there is no longer an
        // explicit-`obeys Crashable` path for records/entities. `Error`/ErrorTypeInfo are the
        // catch-all error references used by generic error handling.
        bool isCrashable = errorType.Category == TypeCategory.Crashable ||
                           errorType is ErrorTypeInfo ||
                           errorType.Name == "Error";
        if (!isCrashable)
        {
            ReportError(code: SemanticDiagnosticCode.ThrowNotCrashable,
                message:
                $"Only `crashable`-kind types can be thrown, got '{errorType.Name}' ({errorType.Category}). " +
                "Declare the error type with the `crashable` keyword.",
                location: throwStmt.Error.Location);
        }

        // Mark routine as having throw statements (for variant generation). A `pierce` never triggers
        // variant generation — it is a crash, not a recoverable failure.
        if (!throwStmt.IsFatal && _currentRoutine.IsFailable)
            _currentRoutine.HasThrow = true;
    }

    private void AnalyzeAbsentStatement(AbsentStatement absent)
    {
        if (_currentRoutine == null || !_currentRoutine.IsFailable)
        {
            ReportError(code: SemanticDiagnosticCode.AbsentOutsideFailableFunction,
                message:
                "Absent statement in a non-failable routine — add '!' suffix to signal callers and enable safe variant generation.",
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
        // discard must target a routine call, not an arbitrary expression like a literal or variable.
        // Explicit-generic calls (`f[T](...)`) parse to GenericMethodCallExpression — also a call.
        if (discard.Expression is not (CallExpression or GenericMethodCallExpression))
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
        // Mark the resource node so a multi-threaded access token (Inspecting/Claiming) produced
        // directly here is accepted; the same token produced anywhere else is rejected (RF-S629),
        // keeping its lock strictly `using`-scoped. Save/restore to support nested `using`.
        ISyntaxTreeNode? previousUsingResource = _usingResourceNode;
        _usingResourceNode = usingStmt.Resource;
        // Analyze the resource expression to get its type
        TypeSymbol resourceType = AnalyzeExpression(expression: usingStmt.Resource);
        _usingResourceNode = previousUsingResource;

        // Readers-XOR-writer (RF-S630): if this `using` opens an MT access token on a named Shared
        // handle, check it against the holds already live in the enclosing `using` scopes on the SAME
        // handle. A writer (`claim`) conflicts with any other hold; readers (`inspect`) coexist.
        // The hold is pushed for the duration of the body and popped on exit, so only OVERLAPPING
        // scopes conflict (sequential `using`s on the same handle are fine).
        string accessBase = GetBaseTypeName(typeName: resourceType.Name);
        bool opensAccessToken = accessBase is "Inspecting" or "Claiming";
        string? accessHandle = opensAccessToken
            ? ExtractAccessReceiverName(resource: usingStmt.Resource)
            : null;
        if (accessHandle != null)
        {
            bool isWriter = accessBase == "Claiming";
            int accessIdentity = GetOrAssignHandleIdentity(path: accessHandle);
            foreach ((string Handle, int Identity, bool IsWriter, SourceLocation Location) hold
                     in _activeAccessHolds)
            {
                // Two holds touch the same memory when they resolve to the same controller identity
                // (aliased handles — `s` and `s2 = s.share()`) OR when their syntactic paths overlap
                // on a field boundary (a parent handle and one of its sub-handles).
                bool sameMemory = hold.Identity == accessIdentity ||
                                  PathsOverlap(a: hold.Handle, b: accessHandle);
                if (!sameMemory || (!isWriter && !hold.IsWriter))
                    continue;
                string newKind = isWriter ? "claim()" : "inspect()";
                string heldKind = hold.IsWriter ? "claim()" : "inspect()";
                string overlapNote = hold.Handle == accessHandle
                    ? "the same shared handle"
                    : hold.Identity == accessIdentity
                        ? $"the aliased handle '{hold.Handle}' (same shared data)"
                        : $"the overlapping handle '{hold.Handle}'";
                ReportError(code: SemanticDiagnosticCode.ReadersXorWriter,
                    message:
                    $"'{newKind}' on '{accessHandle}' conflicts with an active '{heldKind}' on " +
                    $"{overlapNote} in an enclosing 'using' scope. A writer ('claim') excludes all other " +
                    "access; readers ('inspect') may coexist only with other readers.",
                    location: usingStmt.Location);
                break;
            }

            _activeAccessHolds.Add(
                item: (accessHandle, accessIdentity, isWriter, usingStmt.Location));
        }

        // The bound variable type defaults to the resource type, but may be overridden
        // by $enter's return type when it returns non-void.
        TypeSymbol boundType = resourceType;

        // A `using` target must obey `Enterable` — the protocol that declares the `$enter`/`$exit`
        // scope-management contract. Conformance (not just the presence of `$enter`/`$exit` by name)
        // is the gate, so being `using`-able is an explicit, checked capability.
        if (_registry.Language == Language.RazorForge)
        {
            if (!ImplementsProtocol(type: resourceType, protocolName: "Enterable"))
            {
                ReportError(code: SemanticDiagnosticCode.UsingTargetMissingEnterExit,
                    message:
                    $"Using target of type '{resourceType.Name}' must obey 'Enterable' (which provides " +
                    "'$enter'/'$exit') for scope-managed resource access.",
                    location: usingStmt.Location);
            }
            else
            {
                // The bound variable's type is `$enter`'s return type when non-void (pass-through).
                // LookupMethod handles generic fallback (Viewing[Point].$enter -> Viewing.$enter).
                RoutineInfo? enterMethod =
                    _registry.LookupMethod(type: resourceType, methodName: "$enter");
                if (enterMethod?.ReturnType is { IsBlank: false } enterReturn)
                    boundType = enterReturn;
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

        // Pop the MT access hold now that the scope has closed (readers-XOR-writer, RF-S630).
        if (accessHandle != null)
            _activeAccessHolds.RemoveAt(index: _activeAccessHolds.Count - 1);
    }

    /// <summary>
    /// Extracts a path key for the Shared handle of an `inspect`/`claim` access expression — the
    /// receiver of `s.inspect()` / `s.claim()`, as a dotted path so distinct fields are distinct
    /// handles: `s` → "s", `s.a` → "s.a". This makes `s.a.claim()` and `s.b.claim()` independent
    /// (both claimable in one scope) while `s.a` claimed twice still conflicts. Returns null for
    /// receivers that aren't a pure identifier/field path (indexing, call results), which the
    /// readers-XOR-writer check conservatively skips (no false positives).
    /// </summary>
    private static string? ExtractAccessReceiverName(Expression resource)
    {
        return resource is CallExpression { Callee: MemberExpression { Object: var receiver } }
            ? BuildAccessPath(expr: receiver)
            : null;
    }

    /// <summary>Whether two access paths overlap — equal, or one a prefix of the other on a field
    /// boundary (`s.a` overlaps `s.a.x` and `s`, but not `s.b` or `s.ab`). Overlapping paths name
    /// memory where one contains the other, so concurrent access to them conflicts.</summary>
    private static bool PathsOverlap(string a, string b)
    {
        return a == b || a.StartsWith(value: b + ".") || b.StartsWith(value: a + ".");
    }

    /// <summary>Builds a dotted path for an identifier/field-access chain (`s.a.b` → "s.a.b"), or
    /// null if the chain bottoms out on anything else (indexing, a call, a literal).</summary>
    private static string? BuildAccessPath(Expression expr)
    {
        return expr switch
        {
            IdentifierExpression id => id.Name,
            MemberExpression { Object: var inner, PropertyName: var prop } =>
                BuildAccessPath(expr: inner) is { } prefix ? $"{prefix}.{prop}" : null,
            _ => null
        };
    }

    /// <summary>Returns the controller identity for a Shared/Watched handle path. A path bound to a
    /// tracked handle (recorded at its <c>var</c> declaration, see
    /// <see cref="RecordSharedHandleIdentity"/>) reuses its identity; an untracked path is assigned a
    /// fresh unique identity on first use and remembered, so repeated uses of the same path match
    /// (the readers-XOR-writer check then degrades to per-path keying — the pre-aliasing behaviour —
    /// for handles whose origin it can't see).</summary>
    private int GetOrAssignHandleIdentity(string path)
    {
        if (_sharedHandleIdentity.TryGetValue(key: path, out int id))
            return id;
        id = _nextSharedHandleIdentity++;
        _sharedHandleIdentity[key: path] = id;
        return id;
    }

    /// <summary>Records the controller identity of a freshly declared Shared/Watched handle from its
    /// initializer, so later aliases and access-token receivers resolve to the same controller:
    /// <list type="bullet">
    /// <item>a fresh Arc (<c>node.share[P]()</c> — a generic-call) mints a NEW identity;</item>
    /// <item>a clone (<c>s.share()</c>/<c>s.watch()</c>) or a plain copy (<c>var s2 = s</c>)
    /// INHERITS the source handle's identity;</item>
    /// <item>anything else gets a fresh identity (conservative — a missed alias only weakens the
    /// check, never a false positive).</item>
    /// </list></summary>
    private void RecordSharedHandleIdentity(string name, Expression? initializer)
    {
        int identity = initializer switch
        {
            // Fresh Arc: `node.share[P]()` / `node.watch[P]()` — an explicit-generic call.
            GenericMethodCallExpression { MethodName: "share" or "watch" } =>
                _nextSharedHandleIdentity++,
            // Clone: `s.share()` / `s.watch()` — inherit the receiver handle's identity.
            CallExpression
                {
                    Callee: MemberExpression
                    {
                        Object: var receiver, PropertyName: "share" or "watch"
                    }
                } when BuildAccessPath(expr: receiver) is { } recvPath =>
                GetOrAssignHandleIdentity(path: recvPath),
            // Plain copy: `var s2 = s` — inherit (usually blocked by the copy-verb rule, handled
            // here for completeness).
            IdentifierExpression copySource =>
                GetOrAssignHandleIdentity(path: copySource.Name),
            _ => _nextSharedHandleIdentity++
        };
        _sharedHandleIdentity[key: name] = identity;
    }
}
