namespace Compilers.Analysis;

using Enums;
using Symbols;
using Types;
using Shared.AST;
using global::RazorForge.Diagnostics;
using TypeSymbol = Types.TypeInfo;

/// <summary>
/// Phase 3: Statement analysis.
/// </summary>
public sealed partial class SemanticAnalyzer
{
    #region Phase 3: Body Analysis

    /// <summary>
    /// Analyzes routine bodies and expressions for type correctness.
    /// </summary>
    /// <param name="program">The program to analyze.</param>
    private void AnalyzeBodies(Program program)
    {
        foreach (IAstNode declaration in program.Declarations)
        {
            AnalyzeDeclaration(node: declaration);
        }
    }

    private void AnalyzeDeclaration(IAstNode node)
    {
        switch (node)
        {
            case RoutineDeclaration func:
                AnalyzeFunctionBody(routine: func);
                break;

            case RecordDeclaration record:
                AnalyzeTypeMembers(members: record.Members);
                break;

            case EntityDeclaration entity:
                AnalyzeTypeMembers(members: entity.Members);
                break;

            case ResidentDeclaration resident:
                AnalyzeTypeMembers(members: resident.Members);
                break;
        }
    }

    private void AnalyzeTypeMembers(List<Declaration> members)
    {
        foreach (Declaration member in members)
        {
            if (member is RoutineDeclaration method)
            {
                AnalyzeFunctionBody(routine: method);
            }
        }
    }

    private void AnalyzeFunctionBody(RoutineDeclaration routine)
    {
        // The AST already stores names without the '!' suffix
        // (e.g., "get!" is stored as Name="get", IsFailable=true)
        RoutineInfo? routineInfo = _registry.LookupRoutine(fullName: routine.Name);
        if (routineInfo == null)
        {
            return;
        }

        RoutineInfo? previousRoutine = _currentRoutine;
        _currentRoutine = routineInfo;

        _registry.EnterScope(kind: ScopeKind.Function, name: routine.Name);

        // Declare parameters in scope
        foreach (ParameterInfo param in routineInfo.Parameters)
        {
            _registry.DeclareVariable(name: param.Name, type: param.Type, isMutable: false);
        }

        // Analyze body statement
        AnalyzeStatement(statement: routine.Body);

        // Store routine body for error handling variant generation (Phase 5)
        if (routineInfo.IsFailable)
        {
            StoreRoutineBody(routine: routineInfo, body: routine.Body);
        }

        _registry.ExitScope();

        _currentRoutine = previousRoutine;
    }

    private void AnalyzeStatement(Statement statement)
    {
        switch (statement)
        {
            case BlockStatement block:
                AnalyzeBlockStatement(block: block);
                break;

            case DeclarationStatement decl:
                AnalyzeDeclarationStatement(decl: decl);
                break;

            case ExpressionStatement expr:
                AnalyzeExpressionStatement(expr: expr);
                break;

            case AssignmentStatement assign:
                AnalyzeAssignmentStatement(assign: assign);
                break;

            case IfStatement ifStmt:
                AnalyzeIfStatement(ifStmt: ifStmt);
                break;

            case WhileStatement whileStmt:
                AnalyzeWhileStatement(whileStmt: whileStmt);
                break;

            case ForStatement forStmt:
                AnalyzeForStatement(forStmt: forStmt);
                break;

            case WhenStatement whenStmt:
                AnalyzeWhenStatement(whenStmt: whenStmt);
                break;

            case ReturnStatement ret:
                AnalyzeReturnStatement(ret: ret);
                break;

            case BecomesStatement becomesStmt:
                AnalyzeBecomesStatement(becomesStmt: becomesStmt);
                break;

            case ThrowStatement throwStmt:
                AnalyzeThrowStatement(throwStmt: throwStmt);
                break;

            case AbsentStatement absent:
                AnalyzeAbsentStatement(absent: absent);
                break;

            case BreakStatement breakStmt:
                AnalyzeBreakStatement(breakStmt: breakStmt);
                break;

            case ContinueStatement continueStmt:
                AnalyzeContinueStatement(continueStmt: continueStmt);
                break;

            case PassStatement:
                // Pass is a no-op statement with no type analysis needed
                break;

            case DestructuringStatement destruct:
                AnalyzeDestructuringStatement(destruct: destruct);
                break;

            case DiscardStatement discard:
                AnalyzeDiscardStatement(discard: discard);
                break;

            case DangerStatement danger:
                AnalyzeDangerStatement(danger: danger);
                break;

            case ViewingStatement viewing:
                AnalyzeViewingStatement(viewing: viewing);
                break;

            case HijackingStatement hijacking:
                AnalyzeHijackingStatement(hijacking: hijacking);
                break;

            case InspectingStatement inspecting:
                AnalyzeInspectingStatement(inspecting: inspecting);
                break;

            case SeizingStatement seizing:
                AnalyzeSeizingStatement(seizing: seizing);
                break;

            default:
                ReportWarning(
                    SemanticWarningCode.UnknownStatementType,
                    $"Unknown statement type: {statement.GetType().Name}",
                    statement.Location);
                break;
        }
    }

    #endregion

    #region Statement Analysis Methods

    private void AnalyzeBlockStatement(BlockStatement block)
    {
        _registry.EnterScope(kind: ScopeKind.Block, name: null);

        foreach (Statement stmt in block.Statements)
        {
            AnalyzeStatement(statement: stmt);
        }

        _registry.ExitScope();
    }

    private void AnalyzeDeclarationStatement(DeclarationStatement decl)
    {
        switch (decl.Declaration)
        {
            case VariableDeclaration varDecl:
                AnalyzeVariableDeclaration(varDecl: varDecl);
                break;

            case RoutineDeclaration func:
                // Nested function declaration
                AnalyzeFunctionBody(routine: func);
                break;

            default:
                ReportWarning(
                    SemanticWarningCode.UnexpectedDeclaration,
                    $"Unexpected declaration in statement context: {decl.Declaration.GetType().Name}",
                    decl.Location);
                break;
        }
    }

    private void AnalyzeVariableDeclaration(VariableDeclaration varDecl)
    {
        TypeSymbol varType;

        if (varDecl.Type != null)
        {
            // Explicit type annotation
            varType = ResolveType(typeExpr: varDecl.Type);
        }
        else if (varDecl.Initializer != null)
        {
            // Type inference from initializer
            varType = AnalyzeExpression(expression: varDecl.Initializer);
        }
        else
        {
            ReportError(
                SemanticDiagnosticCode.VariableNeedsTypeOrInitializer,
                $"Variable '{varDecl.Name}' requires either a type annotation or an initializer.",
                varDecl.Location);
            varType = ErrorTypeInfo.Instance;
        }

        // If we have both type annotation and initializer, verify compatibility
        if (varDecl is { Type: not null, Initializer: not null })
        {
            TypeSymbol initType = AnalyzeExpression(expression: varDecl.Initializer);
            if (!IsAssignableTo(source: initType, target: varType))
            {
                ReportError(
                    SemanticDiagnosticCode.VariableInitializerTypeMismatch,
                    $"Cannot assign value of type '{initType.Name}' to variable of type '{varType.Name}'.",
                    varDecl.Location);
            }
        }

        // RazorForge: Entity bare assignment prohibition
        // `let b = a` where `a` is an entity is a compile error (must use .share() or steal)
        // Only applies to bare identifier references, not constructor calls or function returns
        if (_registry.Language == Language.RazorForge
            && varDecl.Initializer is IdentifierExpression
            && varType is EntityTypeInfo)
        {
            ReportError(
                SemanticDiagnosticCode.BareEntityAssignment,
                $"Cannot directly assign entity of type '{varType.Name}' to variable '{varDecl.Name}'. " +
                "Use '.share()' for shared ownership or 'steal' for ownership transfer.",
                varDecl.Location);
        }

        // Variant copy prohibition: `let box2 = box1` is not allowed
        // Variants must be dismantled immediately with pattern matching
        // Binding from routine calls (`let result = make_shape()`) is allowed
        if (varDecl.Initializer is IdentifierExpression && varType is VariantTypeInfo)
        {
            ReportError(
                SemanticDiagnosticCode.VariantCopyNotAllowed,
                $"Variant type '{varType.Name}' cannot be copied to variable '{varDecl.Name}'. " +
                "Variants must be dismantled immediately with pattern matching.",
                varDecl.Location);
        }

        // Register variable in current scope
        bool declared = _registry.DeclareVariable(
            name: varDecl.Name,
            type: varType,
            isMutable: varDecl.IsMutable);

        if (!declared)
        {
            ReportError(
                SemanticDiagnosticCode.VariableRedeclaration,
                $"Variable '{varDecl.Name}' is already declared in this scope.",
                varDecl.Location);
        }
    }

    private void AnalyzeExpressionStatement(ExpressionStatement expr)
    {
        // Analyze the expression for side effects and type validation
        TypeSymbol exprType = AnalyzeExpression(expression: expr.Expression);

        // Check if this is a call expression with a non-Blank return value
        // If so, warn that the return value is unused (use 'discard' to explicitly ignore)
        if (expr.Expression is CallExpression call && !exprType.IsBlank)
        {
            // Get a readable name for the routine being called
            string routineName = call.Callee switch
            {
                IdentifierExpression id => id.Name,
                MemberExpression member => member.PropertyName,
                _ => "routine"
            };

            ReportWarning(
                SemanticWarningCode.UnusedRoutineReturnValue,
                $"Return value of '{routineName}()' ({exprType.Name}) is unused. " +
                "Use 'discard' to explicitly ignore the return value, or assign it to a variable.",
                call.Location);
        }
    }

    private void AnalyzeAssignmentStatement(AssignmentStatement assign)
    {
        TypeSymbol targetType = AnalyzeExpression(expression: assign.Target);
        TypeSymbol valueType = AnalyzeExpression(expression: assign.Value);

        // Check if target is assignable (variable, field, or index)
        if (!IsAssignableTarget(target: assign.Target))
        {
            ReportError(
                SemanticDiagnosticCode.InvalidAssignmentTarget,
                "Invalid assignment target.",
                assign.Target.Location);
            return;
        }

        // Check mutability
        if (assign.Target is IdentifierExpression id)
        {
            VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
            if (varInfo is { IsMutable: false })
            {
                ReportError(
                    SemanticDiagnosticCode.AssignmentToImmutable,
                    $"Cannot assign to immutable variable '{id.Name}'.",
                    assign.Location);
            }
        }

        // Validate field write access (setter visibility)
        if (assign.Target is MemberExpression member)
        {
            TypeSymbol objectType = AnalyzeExpression(expression: member.Object);
            ValidateFieldWriteAccess(objectType: objectType, fieldName: member.PropertyName, location: assign.Location);

            // Check if we're in a @readonly method trying to mutate 'me'
            if (_currentRoutine is { IsReadOnly: true } &&
                member.Object is IdentifierExpression { Name: "me" })
            {
                ReportError(
                    SemanticDiagnosticCode.MutationInReadonlyMethod,
                    $"Cannot mutate field '{member.PropertyName}' in a @readonly method. " +
                    "Use @writable or @migratable to allow mutations.",
                    assign.Location);
            }
        }

        // Check type compatibility
        if (!IsAssignableTo(source: valueType, target: targetType))
        {
            ReportError(
                SemanticDiagnosticCode.AssignmentTypeMismatch,
                $"Cannot assign value of type '{valueType.Name}' to target of type '{targetType.Name}'.",
                assign.Location);
        }
    }

    private void AnalyzeIfStatement(IfStatement ifStmt)
    {
        TypeSymbol conditionType = AnalyzeExpression(expression: ifStmt.Condition);

        // Condition must be boolean
        if (!IsBoolType(type: conditionType))
        {
            ReportError(
                SemanticDiagnosticCode.IfConditionNotBool,
                $"If condition must be boolean, got '{conditionType.Name}'.",
                ifStmt.Condition.Location);
        }

        // Extract narrowing info from condition
        NarrowingInfo? narrowing = TryExtractNarrowingFromCondition(condition: ifStmt.Condition);

        // Analyze then branch (with narrowing if applicable)
        if (narrowing?.ThenBranchType != null)
        {
            _registry.EnterScope(kind: ScopeKind.Block, name: "if_then");
            _registry.NarrowVariable(name: narrowing.VariableName, narrowedType: narrowing.ThenBranchType);
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
                _registry.NarrowVariable(name: narrowing.VariableName, narrowedType: narrowing.ElseBranchType);
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
        if (ifStmt.ElseStatement == null
            && narrowing?.ElseBranchType != null
            && HasDefiniteExit(statement: ifStmt.ThenStatement))
        {
            _registry.NarrowVariable(name: narrowing.VariableName, narrowedType: narrowing.ElseBranchType);
        }
    }

    private void AnalyzeWhileStatement(WhileStatement whileStmt)
    {
        TypeSymbol conditionType = AnalyzeExpression(expression: whileStmt.Condition);

        // Condition must be boolean
        if (!IsBoolType(type: conditionType))
        {
            ReportError(
                SemanticDiagnosticCode.WhileConditionNotBool,
                $"While condition must be boolean, got '{conditionType.Name}'.",
                whileStmt.Condition.Location);
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
        TypeSymbol elementType = GetIterableElementType(iterableType: iterableType, location: forStmt.Location);

        // Handle either simple variable or destructuring pattern
        if (forStmt.Variable != null)
        {
            // Simple variable binding: for item in items
            _registry.DeclareVariable(
                name: forStmt.Variable,
                type: elementType,
                isMutable: false); // Loop variables are immutable
        }
        else if (forStmt.VariablePattern != null)
        {
            // Destructuring pattern: for (index, item) in items.enumerate()
            // For tuple destructuring, we need to extract element types from the tuple
            // For now, we'll declare each binding with the element type (to be refined with proper tuple handling)
            foreach (DestructuringBinding binding in forStmt.VariablePattern.Bindings)
            {
                if (binding.BindingName != null)
                {
                    _registry.DeclareVariable(
                        name: binding.BindingName,
                        type: elementType, // TODO: Extract proper tuple element types
                        isMutable: false);
                }
            }
        }

        // Analyze loop body
        AnalyzeStatement(statement: forStmt.Body);

        _registry.ExitScope();
    }

    private void AnalyzeWhenStatement(WhenStatement whenStmt)
    {
        TypeSymbol matchedType = AnalyzeExpression(expression: whenStmt.Expression);

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
                    TypeSymbol? narrowedType = ComputeNarrowedType(
                        type: ehType,
                        eliminateNone: handledNone,
                        eliminateCrashable: handledCrashable);

                    if (narrowedType != null && elsePat.VariableName != null)
                    {
                        // Declare with narrowed type instead of original matchedType
                        DeclarePatternVariable(
                            name: elsePat.VariableName,
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

        // Check exhaustiveness for enumerable types (choice, variant, mutant, error-handling)
        if (matchedType is ChoiceTypeInfo or VariantTypeInfo or MutantTypeInfo or ErrorHandlingTypeInfo)
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
                    ReportWarning(
                        SemanticWarningCode.NonExhaustiveWhen,
                        $"When statement may not cover all cases of '{matchedType.Name}'.{missing}",
                        whenStmt.Location);
                }
            }
        }
    }

    private void AnalyzeReturnStatement(ReturnStatement ret)
    {
        if (_currentRoutine == null)
        {
            ReportError(
                SemanticDiagnosticCode.ReturnOutsideFunction,
                "Return statement outside of function.",
                ret.Location);
            return;
        }

        if (ret.Value != null)
        {
            // Pass expected return type for contextual literal inference
            TypeSymbol returnType = AnalyzeExpression(expression: ret.Value, expectedType: _currentRoutine.ReturnType);

            // Validate that tokens cannot be returned (RazorForge only)
            ValidateNotTokenReturnType(type: returnType, location: ret.Location);

            if (_currentRoutine.ReturnType != null &&
                !IsAssignableTo(source: returnType, target: _currentRoutine.ReturnType))
            {
                ReportError(
                    SemanticDiagnosticCode.ReturnTypeMismatch,
                    $"Cannot return value of type '{returnType.Name}' from function expecting '{_currentRoutine.ReturnType.Name}'.",
                    ret.Location);
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
            ReportError(
                SemanticDiagnosticCode.ThrowOutsideFailableFunction,
                "Throw statement is only allowed in failable functions (marked with !).",
                throwStmt.Location);
            return;
        }

        TypeSymbol errorType = AnalyzeExpression(expression: throwStmt.Error);

        // Error must implement Crashable protocol
        if (!ImplementsProtocol(type: errorType, protocolName: "Crashable"))
        {
            ReportError(
                SemanticDiagnosticCode.ThrowNotCrashable,
                $"Thrown value must implement Crashable protocol, got '{errorType.Name}'.",
                throwStmt.Error.Location);
        }

        // Mark routine as having throw statements (for variant generation)
        _currentRoutine.HasThrow = true;
    }

    private void AnalyzeAbsentStatement(AbsentStatement absent)
    {
        if (_currentRoutine == null || !_currentRoutine.IsFailable)
        {
            ReportError(
                SemanticDiagnosticCode.AbsentOutsideFailableFunction,
                "Absent statement is only allowed in failable functions (marked with !).",
                absent.Location);
            return;
        }

        // Mark routine as having absent statements (for variant generation)
        _currentRoutine.HasAbsent = true;
    }

    private void AnalyzeBreakStatement(BreakStatement breakStmt)
    {
        if (!_registry.CurrentScope.IsInLoop)
        {
            ReportError(
                SemanticDiagnosticCode.BreakOutsideLoop,
                "Break statement is only allowed inside a loop.",
                breakStmt.Location);
        }
    }

    private void AnalyzeContinueStatement(ContinueStatement continueStmt)
    {
        if (!_registry.CurrentScope.IsInLoop)
        {
            ReportError(
                SemanticDiagnosticCode.ContinueOutsideLoop,
                "Continue statement is only allowed inside a loop.",
                continueStmt.Location);
        }
    }

    private void AnalyzeDestructuringStatement(DestructuringStatement destruct)
    {
        TypeSymbol initType = AnalyzeExpression(expression: destruct.Initializer);

        // Analyze the destructuring pattern and bind variables
        AnalyzeDestructuringPattern(pattern: destruct.Pattern, sourceType: initType, isMutable: destruct.IsMutable);
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
            ReportError(
                SemanticDiagnosticCode.InvalidDiscardTarget,
                "'discard' can only be used with routine calls. " +
                "Use 'discard some_routine()' to explicitly ignore a return value.",
                discard.Location);
        }

        // Analyze the expression - this validates the expression and checks for errors
        // The result is intentionally discarded
        AnalyzeExpression(expression: discard.Expression);
    }

    private void AnalyzeDangerStatement(DangerStatement danger)
    {
        if (_registry.Language == Language.Suflae)
        {
            ReportError(
                SemanticDiagnosticCode.FeatureNotInSuflae,
                "Danger blocks are not available in Suflae.",
                danger.Location);
            return;
        }

        // Danger blocks cannot be nested
        if (InDangerBlock)
        {
            ReportError(
                SemanticDiagnosticCode.NestedDangerBlock,
                "Danger blocks cannot be nested.",
                danger.Location);
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

    private void AnalyzeViewingStatement(ViewingStatement viewing)
    {
        if (_registry.Language == Language.Suflae)
        {
            ReportError(
                SemanticDiagnosticCode.FeatureNotInSuflae,
                "Viewing blocks are not available in Suflae.",
                viewing.Location);
            return;
        }

        TypeSymbol sourceType = AnalyzeExpression(expression: viewing.Source);

        _registry.EnterScope(kind: ScopeKind.Block, name: "viewing");

        // Create Viewed<T> token type
        TypeSymbol tokenType = CreateViewedType(innerType: sourceType);

        _registry.DeclareVariable(
            name: viewing.Token,
            type: tokenType,
            isMutable: false);

        AnalyzeBlockStatement(block: viewing.Body);

        _registry.ExitScope();
    }

    private void AnalyzeHijackingStatement(HijackingStatement hijacking)
    {
        if (_registry.Language == Language.Suflae)
        {
            ReportError(
                SemanticDiagnosticCode.FeatureNotInSuflae,
                "Hijacking blocks are not available in Suflae.",
                hijacking.Location);
            return;
        }

        // Check for nested hijacking - cannot hijack a member of an already-hijacked object
        if (IsNestedHijacking(source: hijacking.Source))
        {
            ReportError(
                SemanticDiagnosticCode.NestedHijackingNotAllowed,
                "Nested hijacking is not allowed. Cannot hijack a member of an already-hijacked object.",
                hijacking.Location);
            return;
        }

        TypeSymbol sourceType = AnalyzeExpression(expression: hijacking.Source);

        _registry.EnterScope(kind: ScopeKind.Block, name: "hijacking");

        // Create Hijacked<T> token type
        TypeSymbol tokenType = CreateHijackedType(innerType: sourceType);

        _registry.DeclareVariable(
            name: hijacking.Token,
            type: tokenType,
            isMutable: false);

        AnalyzeBlockStatement(block: hijacking.Body);

        _registry.ExitScope();
    }

    private void AnalyzeInspectingStatement(InspectingStatement inspecting)
    {
        if (_registry.Language == Language.Suflae)
        {
            ReportError(
                SemanticDiagnosticCode.FeatureNotInSuflae,
                "Inspecting blocks are not available in Suflae.",
                inspecting.Location);
            return;
        }

        TypeSymbol sourceType = AnalyzeExpression(expression: inspecting.Source);

        _registry.EnterScope(kind: ScopeKind.Block, name: "inspecting");

        // Create Inspected<T> token type
        TypeSymbol tokenType = CreateInspectedType(innerType: sourceType);

        _registry.DeclareVariable(
            name: inspecting.Token,
            type: tokenType,
            isMutable: false);

        AnalyzeBlockStatement(block: inspecting.Body);

        _registry.ExitScope();
    }

    private void AnalyzeSeizingStatement(SeizingStatement seizing)
    {
        if (_registry.Language == Language.Suflae)
        {
            ReportError(
                SemanticDiagnosticCode.FeatureNotInSuflae,
                "Seizing blocks are not available in Suflae.",
                seizing.Location);
            return;
        }

        TypeSymbol sourceType = AnalyzeExpression(expression: seizing.Source);

        _registry.EnterScope(kind: ScopeKind.Block, name: "seizing");

        // Create Seized<T> token type
        TypeSymbol tokenType = CreateSeizedType(innerType: sourceType);

        _registry.DeclareVariable(
            name: seizing.Token,
            type: tokenType,
            isMutable: false);

        AnalyzeBlockStatement(block: seizing.Body);

        _registry.ExitScope();
    }

    #endregion
}
