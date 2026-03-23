namespace SemanticAnalysis;

using Enums;
using Symbols;
using Types;
using SyntaxTree;
using Diagnostics;
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

            case VariableDeclaration varDecl:
                AnalyzeVariableDeclaration(varDecl: varDecl);
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
        // Construct the full name matching how CollectFunctionDeclaration registered it.
        // Must replicate the exact same name resolution logic so the lookup succeeds.
        string fullName;
        if (_currentType != null)
        {
            // Member routine inside type body: OwnerType.Name + "." + routine.Name
            fullName = $"{_currentType.Name}.{routine.Name}";
        }
        else if (routine.Name.Contains(value: '.'))
        {
            // Member routine syntax (e.g., "List[T].add_last"):
            // Resolve OwnerType to get canonical name, then append method name
            int dotIndex = routine.Name.IndexOf(value: '.');
            string typeName = routine.Name[..dotIndex];
            string methodName = routine.Name[(dotIndex + 1)..];
            TypeSymbol? ownerType = LookupTypeWithImports(name: typeName);
            fullName = ownerType != null ? $"{ownerType.Name}.{methodName}" : routine.Name;
        }
        else
        {
            // Top-level function: Module.Name (if module set), else just Name
            string? module = GetCurrentModuleName();
            fullName = string.IsNullOrEmpty(value: module) ? routine.Name : $"{module}.{routine.Name}";
        }

        RoutineInfo? routineInfo = _registry.LookupRoutine(fullName: fullName);
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
            _registry.DeclareVariable(name: param.Name, type: param.Type);
        }

        // #169: dangerous routine implicit danger context
        bool wasDangerImplicit = false;
        if (routineInfo.IsDangerous && _dangerBlockDepth == 0)
        {
            _dangerBlockDepth = 1;
            wasDangerImplicit = true;
        }

        // Analyze body statement
        AnalyzeStatement(statement: routine.Body);

        if (wasDangerImplicit)
        {
            _dangerBlockDepth = 0;
        }

        // Validate that non-void routines return on all paths (#144)
        if (routineInfo.ReturnType != null &&
            routineInfo.ReturnType.Name != "Blank" &&
            !StatementAlwaysTerminates(statement: routine.Body))
        {
            ReportError(
                SemanticDiagnosticCode.MissingReturn,
                $"Routine '{routine.Name}' has return type '{routineInfo.ReturnType.Name}' but not all code paths return a value.",
                routine.Location);
        }

        // Failable routines must contain throw, absent, or call other failable routines (#77)
        if (routineInfo is { IsFailable: true, HasThrow: false, HasAbsent: false, HasFailableCalls: false })
        {
            ReportError(
                SemanticDiagnosticCode.FailableWithoutThrowOrAbsent,
                $"Failable routine '{routine.Name}!' contains neither 'throw' nor 'absent'. " +
                "Remove the '!' suffix or add error-handling statements.",
                routine.Location);
        }

        // Store routine body for error handling variant generation (Phase 5)
        if (routineInfo.IsFailable)
        {
            StoreRoutineBody(routine: routineInfo, body: routine.Body);
        }

        // #161: Report undismantled Lookup variables at routine scope exit
        foreach (var pending in _pendingLookupVars)
        {
            ReportError(
                SemanticDiagnosticCode.LookupNotDismantled,
                $"Lookup variable '{pending.Name}' must be dismantled before end of scope. " +
                "Use 'when', '??', or 'if is' to handle the lookup result.",
                pending.Location);
        }
        _pendingLookupVars.Clear();

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

            case EmitStatement emitStmt:
                AnalyzeEmitStatement(emitStmt: emitStmt);
                break;

            case DangerStatement danger:
                AnalyzeDangerStatement(danger: danger);
                break;

            case UsingStatement usingStmt:
                AnalyzeUsingStatement(usingStmt: usingStmt);
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
        if (block.Statements.Count == 0)
        {
            ReportError(
                SemanticDiagnosticCode.EmptyBlockWithoutPass,
                "Empty block requires 'pass' keyword.",
                block.Location);
            return;
        }

        _registry.EnterScope(kind: ScopeKind.Block, name: null);

        foreach (Statement stmt in block.Statements)
        {
            // #58: Check if previous statement declared a variant that hasn't been dismantled
            if (_lastDeclaredVariantVar is { } pendingVariant)
            {
                bool isDismantling = stmt is WhenStatement when
                    && when.Expression is IdentifierExpression id
                    && id.Name == pendingVariant.Name;

                if (!isDismantling)
                {
                    ReportError(
                        SemanticDiagnosticCode.VariantNotDismantled,
                        $"Variant variable '{pendingVariant.Name}' must be dismantled immediately with 'when'. " +
                        "Variants cannot be used after other statements.",
                        pendingVariant.Location);
                }

                _lastDeclaredVariantVar = null;
            }

            AnalyzeStatement(statement: stmt);
        }

        // #58: Check if the last statement declared a variant without a subsequent when
        if (_lastDeclaredVariantVar is { } trailingVariant)
        {
            ReportError(
                SemanticDiagnosticCode.VariantNotDismantled,
                $"Variant variable '{trailingVariant.Name}' must be dismantled immediately with 'when'. " +
                "Variants cannot be used after other statements.",
                trailingVariant.Location);
            _lastDeclaredVariantVar = null;
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
            // #37: Suflae falls back to Data when type inference is not possible
            if (_registry.Language == Language.Suflae)
            {
                varType = _registry.LookupType(name: "Data") ?? ErrorTypeInfo.Instance;
            }
            else
            {
                ReportError(
                    SemanticDiagnosticCode.VariableNeedsTypeOrInitializer,
                    $"Variable '{varDecl.Name}' requires either a type annotation or an initializer.",
                    varDecl.Location);
                varType = ErrorTypeInfo.Instance;
            }
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
        // `var b = a` where `a` is an entity is a build error (must use .share() or steal)
        // Only applies to bare identifier references, not creator calls or function returns
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

        // Variant copy prohibition: `var box2 = box1` is not allowed
        // Variants must be dismantled immediately with pattern matching
        // Binding from routine calls (`var result = make_shape()`) is allowed
        if (varDecl.Initializer is IdentifierExpression && varType is VariantTypeInfo)
        {
            ReportError(
                SemanticDiagnosticCode.VariantCopyNotAllowed,
                $"Variant type '{varType.Name}' cannot be copied to variable '{varDecl.Name}'. " +
                "Variants must be dismantled immediately with pattern matching.",
                varDecl.Location);
        }

        // #96: Seized[T] cannot be copied or aliased — exclusive lock token
        if (varDecl.Initializer is IdentifierExpression
            && IsSeizedType(type: varType))
        {
            ReportError(
                SemanticDiagnosticCode.SeizedCopyNotAllowed,
                $"Cannot copy or alias 'Seized[T]' variable to '{varDecl.Name}'. " +
                "Seized tokens are exclusive and cannot be duplicated — use the original variable directly.",
                varDecl.Location);
        }

        // #81: Result/Lookup cannot be copied from variable to variable
        // `var r = check_parse!(data)` then `when r` is allowed (call result)
        // `var r2 = r1` where r1: Result[T] is not allowed (variable copy)
        if (varDecl.Initializer is IdentifierExpression
            && varType is ErrorHandlingTypeInfo { Kind: ErrorHandlingKind.Result or ErrorHandlingKind.Lookup })
        {
            ReportError(
                SemanticDiagnosticCode.ErrorHandlingTypeStoredInVariable,
                $"'{varType.Name}' cannot be copied to another variable. " +
                "Dismantle it immediately with 'when', '??', or 'if is'.",
                varDecl.Location);
        }

        // #52: Residents can only be declared as global variables, not local variables
        if (varType is ResidentTypeInfo && _currentRoutine != null)
        {
            ReportError(
                SemanticDiagnosticCode.ResidentAsLocalVariable,
                $"Resident type '{varType.Name}' can only be declared as a global variable, not a local variable.",
                varDecl.Location);
        }

        // #57: The 'global' keyword is only valid for resident type variables
        if (varDecl.Storage == StorageClass.Global && varType is not ResidentTypeInfo && varType is not ErrorTypeInfo)
        {
            ReportError(
                SemanticDiagnosticCode.GlobalOnlyForResidents,
                $"The 'global' keyword is only valid for resident type variables, not for type '{varType.Name}'.",
                varDecl.Location);
        }

        // Register variable in current scope
        bool declared = _registry.DeclareVariable(
            name: varDecl.Name,
            type: varType);

        if (!declared)
        {
            ReportError(
                SemanticDiagnosticCode.VariableRedeclaration,
                $"Variable '{varDecl.Name}' is already declared in this scope.",
                varDecl.Location);
        }

        // #19: Propagate lock policy from share[Policy]() to the declared variable
        if (_lastSharePolicy != null && varDecl.Initializer is GenericMethodCallExpression { MethodName: "share" })
        {
            _variableLockPolicies[varDecl.Name] = _lastSharePolicy.Value.Policy;
            _lastSharePolicy = null;
        }

        // #58: Track variant variable declaration for immediate dismantling check
        if (varType is VariantTypeInfo && varDecl.Initializer is not IdentifierExpression)
        {
            _lastDeclaredVariantVar = (varDecl.Name, varDecl.Location);
        }

        // #161: Track Lookup variables that must be dismantled before scope exit
        if (varType is ErrorHandlingTypeInfo { Kind: ErrorHandlingKind.Lookup }
            && varDecl.Initializer is not IdentifierExpression)
        {
            _pendingLookupVars.Add((varDecl.Name, varDecl.Location));
        }
    }

    private void AnalyzeExpressionStatement(ExpressionStatement expr)
    {
        // Analyze the expression for side effects and type validation
        TypeSymbol exprType = AnalyzeExpression(expression: expr.Expression);

        // Note: UnhandledCrashableCall check moved to AnalyzeCallExpression to catch all contexts
        // (return values, assignments, nested expressions — not just expression statements)

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

            // #30: Specific warning for unused Task[T] results
            string baseReturnName = GetBaseTypeName(typeName: exprType.Name);
            if (baseReturnName == "Task")
            {
                ReportWarning(
                    SemanticWarningCode.UnusedTaskResult,
                    $"Task result from '{routineName}()' is not awaited. " +
                    "Use 'waitfor' to await the result, or 'discard' to explicitly ignore it.",
                    call.Location);
            }
            else
            {
                ReportWarning(
                    SemanticWarningCode.UnusedRoutineReturnValue,
                    $"Return value of '{routineName}()' ({exprType.Name}) is unused. " +
                    "Use 'discard' to explicitly ignore the return value, or assign it to a variable.",
                    call.Location);
            }
        }
    }

    private void AnalyzeAssignmentStatement(AssignmentStatement assign)
    {
        // #173: Tuple assignment destructuring — (a, b) = (b, a)
        if (assign.Target is TupleLiteralExpression tupleLhs)
        {
            TypeSymbol rhsType = AnalyzeExpression(expression: assign.Value);

            // Verify all elements of the LHS tuple are assignable targets
            foreach (Expression element in tupleLhs.Elements)
            {
                AnalyzeExpression(expression: element);
                if (!IsAssignableTarget(target: element))
                {
                    ReportError(
                        SemanticDiagnosticCode.InvalidAssignmentTarget,
                        "All elements of tuple destructuring must be assignable targets (variables, member accesses, or indices).",
                        element.Location);
                }

                // Check modifiability for identifier elements
                if (element is IdentifierExpression elemId)
                {
                    VariableInfo? varInfo = _registry.LookupVariable(name: elemId.Name);
                    if (varInfo is { IsModifiable: false })
                    {
                        ReportError(
                            SemanticDiagnosticCode.AssignmentToImmutable,
                            $"Cannot assign to preset variable '{elemId.Name}'.",
                            assign.Location);
                    }
                }
            }

            // Check that RHS is a tuple with matching arity
            if (rhsType is TupleTypeInfo tupleType)
            {
                if (tupleLhs.Elements.Count != tupleType.ElementTypes.Count)
                {
                    ReportError(
                        SemanticDiagnosticCode.DestructuringArityMismatch,
                        $"Tuple destructuring has {tupleLhs.Elements.Count} targets but the value has {tupleType.ElementTypes.Count} elements.",
                        assign.Location);
                }
            }

            return;
        }

        TypeSymbol targetType = AnalyzeExpression(expression: assign.Target);
        TypeSymbol valueType = AnalyzeExpression(expression: assign.Value);

        // Check if target is assignable (variable, member variable, or index)
        if (!IsAssignableTarget(target: assign.Target))
        {
            ReportError(
                SemanticDiagnosticCode.InvalidAssignmentTarget,
                "Invalid assignment target.",
                assign.Target.Location);
            return;
        }

        // Check modifiability
        if (assign.Target is IdentifierExpression id)
        {
            VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
            if (varInfo is { IsModifiable: false })
            {
                ReportError(
                    SemanticDiagnosticCode.AssignmentToImmutable,
                    $"Cannot assign to preset variable '{id.Name}'.",
                    assign.Location);
            }
        }

        // Validate member variable write access (setter visibility)
        if (assign.Target is MemberExpression member)
        {
            TypeSymbol objectType = AnalyzeExpression(expression: member.Object);

            // Read-only wrapper types (Viewed, Inspected) cannot be written through
            if (IsReadOnlyWrapper(type: objectType))
            {
                ReportError(
                    SemanticDiagnosticCode.WriteThroughReadOnlyWrapper,
                    $"Cannot write to member '{member.PropertyName}' through read-only wrapper '{objectType.Name}'. " +
                    "Use Hijacked[T] for exclusive write access or Seized[T] for locked write access.",
                    assign.Location);
            }

            ValidateMemberVariableWriteAccess(objectType: objectType, memberVariableName: member.PropertyName, location: assign.Location);

            // Preset enforcement: cannot assign to member variables of preset variables
            if (member.Object is IdentifierExpression memberVariableTarget)
            {
                VariableInfo? targetVar = _registry.LookupVariable(name: memberVariableTarget.Name);
                if (targetVar is { IsModifiable: false })
                {
                    ReportError(
                        SemanticDiagnosticCode.MemberVariableAssignmentOnImmutable,
                        $"Cannot assign to member variable '{member.PropertyName}' of preset variable '{memberVariableTarget.Name}'.",
                        assign.Location);
                }
            }

            // Check if we're in a @readonly method trying to modify 'me'
            if (_currentRoutine is { IsReadOnly: true } &&
                member.Object is IdentifierExpression { Name: "me" })
            {
                ReportError(
                    SemanticDiagnosticCode.ModificationInReadonlyMethod,
                    $"Cannot modify member variable '{member.PropertyName}' in a @readonly method. " +
                    "Use @writable or @migratable to allow modifications.",
                    assign.Location);
            }
        }

        // #81: Result/Lookup cannot be copied from variable to variable via assignment
        if (assign.Value is IdentifierExpression
            && valueType is ErrorHandlingTypeInfo { Kind: ErrorHandlingKind.Result or ErrorHandlingKind.Lookup })
        {
            ReportError(
                SemanticDiagnosticCode.ErrorHandlingTypeStoredInVariable,
                $"'{valueType.Name}' cannot be copied to another variable. " +
                "Dismantle it immediately with 'when', '??', or 'if is'.",
                assign.Location);
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

        // Analyze sequenceable expression
        TypeSymbol sequenceableType = AnalyzeExpression(expression: forStmt.Sequenceable);

        // Get element type from sequenceable
        TypeSymbol elementType = GetSequenceableElementType(iterableType: sequenceableType, location: forStmt.Location);

        // Handle either simple variable or destructuring pattern
        if (forStmt.Variable != null)
        {
            // Simple variable binding: for item in items
            _registry.DeclareVariable(
                name: forStmt.Variable,
                type: elementType);
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
                    ReportError(
                        SemanticDiagnosticCode.DestructuringArityMismatch,
                        $"Destructuring pattern has {bindingCount} bindings but tuple has {tupleType.Arity} elements.",
                        forStmt.VariablePattern.Location);
                }

                // Declare each binding with its corresponding tuple element type
                for (int i = 0; i < forStmt.VariablePattern.Bindings.Count; i++)
                {
                    DestructuringBinding binding = forStmt.VariablePattern.Bindings[i];
                    if (binding.BindingName != null)
                    {
                        TypeSymbol bindingType = i < tupleType.Arity
                            ? (TypeSymbol)tupleType.ElementTypes[i]
                            : ErrorTypeInfo.Instance;
                        _registry.DeclareVariable(name: binding.BindingName, type: bindingType);
                    }
                }
            }
            else
            {
                // Non-tuple type with destructuring pattern
                ReportError(
                    SemanticDiagnosticCode.DestructuringArityMismatch,
                    $"Cannot destructure non-tuple type '{elementType.Name}' in for loop.",
                    forStmt.VariablePattern.Location);
                // Still declare variables with error type so analysis can continue
                foreach (DestructuringBinding binding in forStmt.VariablePattern.Bindings)
                {
                    if (binding.BindingName != null)
                    {
                        _registry.DeclareVariable(name: binding.BindingName, type: ErrorTypeInfo.Instance);
                    }
                }
            }
        }

        // #22: Track active iteration source for migratable-during-iteration check
        string? iterationSourceName = forStmt.Sequenceable is IdentifierExpression iterSource
            ? iterSource.Name
            : null;

        if (iterationSourceName != null)
        {
            _activeIterationSources.Add(iterationSourceName);
        }

        // Analyze loop body
        AnalyzeStatement(statement: forStmt.Body);

        if (iterationSourceName != null)
        {
            _activeIterationSources.Remove(iterationSourceName);
        }

        _registry.ExitScope();
    }

    private void AnalyzeWhenStatement(WhenStatement whenStmt)
    {
        TypeSymbol matchedType = AnalyzeExpression(expression: whenStmt.Expression);

        // #161: Mark Lookup variable as dismantled when targeted by 'when'
        if (whenStmt.Expression is IdentifierExpression whenTarget)
        {
            _pendingLookupVars.RemoveAll(v => v.Name == whenTarget.Name);
        }

        // #88: Pattern order enforcement — else/wildcard must be last, detect unreachable patterns
        {
            bool seenElse = false;
            foreach (WhenClause clause in whenStmt.Clauses)
            {
                if (seenElse)
                {
                    ReportError(
                        SemanticDiagnosticCode.PatternOrderViolation,
                        "Unreachable pattern after 'else' or wildcard.",
                        clause.Pattern.Location);
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
                    ReportError(
                        SemanticDiagnosticCode.DuplicatePattern,
                        $"Duplicate pattern: {patternKey}.",
                        clause.Pattern.Location);
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
                    if (matchedType is ErrorHandlingTypeInfo { Kind: ErrorHandlingKind.Result or ErrorHandlingKind.Lookup }
                        && exhaustiveness.MissingCases.Contains(item: "Crashable"))
                    {
                        ReportError(
                            SemanticDiagnosticCode.NonExhaustiveMatch,
                            $"Pattern match on '{matchedType.Name}' requires a 'Crashable' catch-all arm.{missing}",
                            whenStmt.Location);
                    }
                    else
                    {
                        ReportWarning(
                            SemanticWarningCode.NonExhaustiveWhen,
                            $"When statement may not cover all cases of '{matchedType.Name}'.{missing}",
                            whenStmt.Location);
                    }
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

        // Error types must be records (#84)
        if (errorType.Category != TypeCategory.Record &&
            errorType is not ErrorTypeInfo &&
            errorType.Name != "Error")
        {
            ReportError(
                SemanticDiagnosticCode.ThrowRequiresRecordType,
                $"Only record types can be thrown, got '{errorType.Name}' ({errorType.Category}). " +
                "Error types must be records for safe stack-based error propagation.",
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
            ReportError(
                SemanticDiagnosticCode.EmitOutsideGenerator,
                "'emit' statement is only allowed inside generator routines.",
                emitStmt.Location);
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
                ReportError(
                    SemanticDiagnosticCode.GeneratorReturnType,
                    $"Generator routine must return Sequence[T], not '{_currentRoutine.ReturnType.Name}'.",
                    emitStmt.Location);
            }
        }
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

    private void AnalyzeUsingStatement(UsingStatement usingStmt)
    {
        // Analyze the resource expression to get its type
        TypeSymbol resourceType = AnalyzeExpression(expression: usingStmt.Resource);

        // #142: using target validation — must be a token (.view()/.hijack()/etc.) or disposable resource
        bool isTokenAccess = IsInlineOnlyTokenType(type: resourceType);

        // #32: For non-token using targets, validate __enter__/__exit__ exist
        if (!isTokenAccess && _registry.Language == Language.RazorForge)
        {
            bool hasEnter = _registry.LookupRoutine(fullName: $"{resourceType.Name}.__enter__") != null;
            bool hasExit = _registry.LookupRoutine(fullName: $"{resourceType.Name}.__exit__") != null;

            if (!hasEnter || !hasExit)
            {
                ReportError(
                    SemanticDiagnosticCode.UsingTargetMissingEnterExit,
                    $"Using target of type '{resourceType.Name}' must implement '__enter__' and '__exit__' for resource management, " +
                    "or be a token access expression (.view(), .hijack(), .inspect!(), .seize!()).",
                    usingStmt.Location);
            }
        }

        // Create a new scope for the using block
        _registry.EnterScope(kind: ScopeKind.Block, name: "using");

        // Declare the binding variable in the using scope
        _registry.DeclareVariable(
            name: usingStmt.Name,
            type: resourceType);

        // Analyze the body
        AnalyzeStatement(statement: usingStmt.Body);

        // #171/#172: Token/resource scope escape — validate that the using-bound variable
        // is not returned or stored in outer scope (handled by ValidateNotTokenReturnType
        // for tokens, and conceptually enforced by scope exit for resources)

        _registry.ExitScope();
    }

    #endregion
}
