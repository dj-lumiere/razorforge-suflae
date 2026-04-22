namespace SemanticVerification;

using Enums;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using SyntaxTree;
using Compiler.Diagnostics;
using TypeSymbol = TypeModel.Types.TypeInfo;

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
                // Skip external/LLVM-only routines — their PassStatement bodies have nothing to analyze
                if (func.Body is not PassStatement)
                    AnalyzeFunctionBody(routine: func);
                break;

            case RecordDeclaration record:
            {
                TypeSymbol? recordType = _registry.LookupType(name: record.Name);
                AnalyzeTypeMembers(members: record.Members, ownerType: recordType);
                break;
            }

            case EntityDeclaration entity:
            {
                TypeSymbol? entityType = _registry.LookupType(name: entity.Name);
                AnalyzeTypeMembers(members: entity.Members, ownerType: entityType);
                break;
            }

            case VariableDeclaration varDecl:
                AnalyzeVariableDeclaration(varDecl: varDecl);
                break;
        }
    }

    private void AnalyzeTypeMembers(List<Declaration> members, TypeSymbol? ownerType)
    {
        TypeSymbol? prevType = _currentType;
        _currentType = ownerType;
        foreach (Declaration member in members)
        {
            if (member is RoutineDeclaration method && method.Body is not PassStatement)
            {
                AnalyzeFunctionBody(routine: method);
            }
        }
        _currentType = prevType;
    }

    private void AnalyzeFunctionBody(RoutineDeclaration routine)
    {
        // Construct the base name matching how the routine was registered.
        string baseName;
        if (_currentType != null)
        {
            // Member routine inside type body: OwnerType.Name + "." + routine.Name
            baseName = $"{_currentType.Name}.{routine.Name}";
        }
        else if (routine.Name.Contains(value: '.'))
        {
            // Extension method syntax (e.g., "List[T].add_last"):
            // Resolve OwnerType to get canonical name, then append method name
            int dotIndex = routine.Name.IndexOf(value: '.');
            string typeName = routine.Name[..dotIndex];
            string methodName = routine.Name[(dotIndex + 1)..];

            // Always strip generic params first (e.g., "Stack[T]" → "Stack") to look up
            // the generic definition, not a resolution cache entry.
            string lookupName = typeName.Contains(value: '[')
                ? typeName[..typeName.IndexOf(value: '[')]
                : typeName;
            TypeSymbol? ownerType = LookupTypeWithImports(name: lookupName);

            baseName = ownerType != null
                ? $"{ownerType.Name}.{methodName}"
                : routine.Name;
        }
        else
        {
            // Top-level function: Module.Name (if module set), else just Name
            string? module = GetCurrentModuleName();
            baseName = string.IsNullOrEmpty(value: module)
                ? routine.Name
                : $"{module}.{routine.Name}";
        }

        // Look up by RegistryKey (BaseName + param types) for overload disambiguation,
        // then fall back to BaseName for the first-overload-wins entry.
        // Set up generic parameter context so ResolveType recognizes T, U, etc.
        // (mirrors Phase 2.5 registration in Signatures.cs)
        RoutineInfo? prevRoutine = _currentRoutine;
        _currentRoutine = new RoutineInfo(name: baseName)
        {
            GenericParameters = routine.GenericParameters
        };

        RoutineInfo? routineInfo = null;
        if (routine.Parameters.Count > 0)
        {
            IEnumerable<string> paramTypeNames = routine.Parameters
                                                        .Select(selector: p =>
                                                         {
                                                             if (p.Type == null)
                                                             {
                                                                 return "";
                                                             }

                                                             TypeSymbol resolved =
                                                                 ResolveType(
                                                                     typeExpr: p.Type);
                                                             if (resolved is ErrorTypeInfo)
                                                             {
                                                                 return p.Type.Name ?? "";
                                                             }

                                                             // Varargs params are stored as List[T] in the registry
                                                             // (mirrors the wrapping in Signatures.cs Phase 2)
                                                             if (p.IsVariadic)
                                                             {
                                                                 TypeSymbol? listDef =
                                                                     _registry.LookupType(
                                                                         name: "List");
                                                                 if (listDef != null)
                                                                 {
                                                                     resolved =
                                                                         _registry
                                                                            .GetOrCreateResolution(
                                                                                 genericDef:
                                                                                 listDef,
                                                                                 typeArguments:
                                                                                 [resolved]);
                                                                 }
                                                             }

                                                             return resolved.Name;
                                                         })
                                                        .Where(predicate: n =>
                                                             !string.IsNullOrEmpty(value: n));
            string registryKey =
                $"{baseName}#{string.Join(separator: ",", values: paramTypeNames)}";
            routineInfo = _registry.LookupRoutine(fullName: registryKey);
        }

        _currentRoutine = prevRoutine;

        routineInfo ??= _registry.LookupRoutine(fullName: baseName);
        if (routineInfo == null)
        {
            ReportError(code: SemanticDiagnosticCode.UnresolvedRoutineBody,
                message:
                $"Routine '{baseName}' body could not be matched to a registered declaration.",
                location: routine.Location);
            return;
        }

        RoutineInfo? previousRoutine = _currentRoutine;
        _currentRoutine = routineInfo;

        // Deadref tracking is per-routine — clear carries from previous routines.
        _deadrefVariables.Clear();

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

        // @innate routines have compiler-supplied bodies — skip analysis entirely.
        if (routine.Annotations.Contains(item: "innate"))
        {
            if (wasDangerImplicit) _dangerBlockDepth = 0;
            _registry.ExitScope();
            _currentRoutine = previousRoutine;
            return;
        }

        // Analyze body statement
        AnalyzeStatement(statement: routine.Body);

        if (wasDangerImplicit)
        {
            _dangerBlockDepth = 0;
        }

        // Infer Blank return type if no annotation was given and no return value was found.
        // null is a transient "not yet inferred" state — after body analysis it must be resolved.
        routineInfo.ReturnType ??= _registry.LookupType(name: "Blank");

        // Validate that non-void routines return on all paths (#144)
        if (routineInfo.ReturnType is { IsBlank: false } &&
            !StatementAlwaysTerminates(statement: routine.Body))
        {
            ReportError(code: SemanticDiagnosticCode.MissingReturn,
                message:
                $"Routine '{routine.Name}' has return type '{routineInfo.ReturnType.Name}' but not all code paths return a value.",
                location: routine.Location);
        }

        // Failable routine with no throw/absent — warn rather than error (#218).
        // Valid for protocol conformance: the contract requires !, but this type never fails.
        if (routineInfo is
            { IsFailable: true, HasThrow: false, HasAbsent: false, HasFailableCalls: false })
        {
            ReportWarning(code: SemanticWarningCode.FailableRoutineNeverCrashes,
                message:
                $"Failable routine '{routine.Name}!' contains neither 'throw' nor 'absent'. " +
                "Remove '!' or add a failure path (safe to suppress when implementing a protocol requirement).",
                location: routine.Location);
        }

        // Store routine body for error handling variant generation (Phase 5).
        // Only store if the body actually has throw/absent/failable-calls — routines
        // implemented via @llvm_ir have no such AST nodes and can't have variants generated.
        if (routineInfo.IsFailable &&
            (routineInfo.HasThrow || routineInfo.HasAbsent || routineInfo.HasFailableCalls))
        {
            StoreRoutineBody(routine: routineInfo, body: routine.Body);
        }

        // #161: Report undismantled Lookup variables at routine scope exit
        foreach ((string Name, SourceLocation Location) pending in _pendingLookupVars)
        {
            ReportError(code: SemanticDiagnosticCode.LookupNotDismantled,
                message:
                $"Lookup variable '{pending.Name}' must be dismantled before end of scope. " +
                "Use 'when', '??', or 'if is' to handle the lookup result.",
                location: pending.Location);
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

            case LoopStatement loopStmt:
                _registry.EnterScope(kind: ScopeKind.Loop, name: "loop");
                AnalyzeStatement(statement: loopStmt.Body);
                _registry.ExitScope();
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

            case UsingStatement usingStmt:
                AnalyzeUsingStatement(usingStmt: usingStmt);
                break;

            default:
                ReportWarning(code: SemanticWarningCode.UnknownStatementType,
                    message: $"Unknown statement type: {statement.GetType().Name}",
                    location: statement.Location);
                break;
        }
    }

    #endregion

    #region Statement Analysis Methods

    private void AnalyzeBlockStatement(BlockStatement block)
    {
        if (block.Statements.Count == 0)
        {
            ReportError(code: SemanticDiagnosticCode.EmptyBlockWithoutPass,
                message: "Empty block requires 'pass' keyword.",
                location: block.Location);
            return;
        }

        _registry.EnterScope(kind: ScopeKind.Block, name: null);

        foreach (Statement stmt in block.Statements)
        {
            // #58: Check if previous statement declared a variant that hasn't been dismantled
            if (_lastDeclaredVariantVar is { } pendingVariant)
            {
                bool isDismantling = stmt is WhenStatement when &&
                                     when.Expression is IdentifierExpression id &&
                                     id.Name == pendingVariant.Name;

                if (!isDismantling)
                {
                    ReportError(code: SemanticDiagnosticCode.VariantNotDismantled,
                        message:
                        $"Variant variable '{pendingVariant.Name}' must be dismantled immediately with 'when'. " +
                        "Variants cannot be used after other statements.",
                        location: pendingVariant.Location);
                }

                _lastDeclaredVariantVar = null;
            }

            AnalyzeStatement(statement: stmt);
        }

        // #58: Check if the last statement declared a variant without a subsequent when
        if (_lastDeclaredVariantVar is { } trailingVariant)
        {
            ReportError(code: SemanticDiagnosticCode.VariantNotDismantled,
                message:
                $"Variant variable '{trailingVariant.Name}' must be dismantled immediately with 'when'. " +
                "Variants cannot be used after other statements.",
                location: trailingVariant.Location);
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
                ReportWarning(code: SemanticWarningCode.UnexpectedDeclaration,
                    message:
                    $"Unexpected declaration in statement context: {decl.Declaration.GetType().Name}",
                    location: decl.Location);
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
                ReportError(code: SemanticDiagnosticCode.VariableNeedsTypeOrInitializer,
                    message:
                    $"Variable '{varDecl.Name}' requires either a type annotation or an initializer.",
                    location: varDecl.Location);
                varType = ErrorTypeInfo.Instance;
            }
        }

        // If we have both type annotation and initializer, verify compatibility
        if (varDecl is { Type: not null, Initializer: not null })
        {
            TypeSymbol initType =
                AnalyzeExpression(expression: varDecl.Initializer, expectedType: varType);
            if (!IsAssignableTo(source: initType, target: varType))
            {
                ReportError(code: SemanticDiagnosticCode.VariableInitializerTypeMismatch,
                    message:
                    $"Cannot assign value of type '{initType.Name}' to variable of type '{varType.Name}'.",
                    location: varDecl.Location);
            }
        }

        // RazorForge: Entity bare assignment prohibition
        // `var b = a` where `a` is an entity is a build error (must use .share() or steal)
        // Only applies to bare identifier references, not creator calls or function returns
        if (_registry.Language == Language.RazorForge &&
            varDecl.Initializer is IdentifierExpression && varType is EntityTypeInfo)
        {
            ReportError(code: SemanticDiagnosticCode.BareEntityAssignment,
                message:
                $"Cannot directly assign entity of type '{varType.Name}' to variable '{varDecl.Name}'. " +
                "Use '.share()' for shared ownership or 'steal' for ownership transfer.",
                location: varDecl.Location);
        }

        // Variant copy prohibition: `var box2 = box1` is not allowed
        // Variants must be dismantled immediately with pattern matching
        // Binding from routine calls (`var result = make_shape()`) is allowed
        if (varDecl.Initializer is IdentifierExpression && varType is VariantTypeInfo)
        {
            ReportError(code: SemanticDiagnosticCode.VariantCopyNotAllowed,
                message:
                $"Variant type '{varType.Name}' cannot be copied to variable '{varDecl.Name}'. " +
                "Variants must be dismantled immediately with pattern matching.",
                location: varDecl.Location);
        }

        // #96: Claimed[T] cannot be copied or aliased — exclusive lock token
        if (varDecl.Initializer is IdentifierExpression && IsClaimedType(type: varType))
        {
            ReportError(code: SemanticDiagnosticCode.ClaimedCopyNotAllowed,
                message: $"Cannot copy or alias 'Claimed[T]' variable to '{varDecl.Name}'. " +
                         "Claimed tokens are exclusive and cannot be duplicated — use the original variable directly.",
                location: varDecl.Location);
        }

        // #81: Result/Lookup cannot be copied from variable to variable
        // `var r = check_parse!(data)` then `when r` is allowed (call result)
        // `var r2 = r1` where r1: Result[T] is not allowed (variable copy)
        if (varDecl.Initializer is IdentifierExpression &&
            IsCarrierType(type: varType) && !IsMaybeType(type: varType))
        {
            ReportError(code: SemanticDiagnosticCode.ErrorHandlingTypeStoredInVariable,
                message: $"'{varType.Name}' cannot be copied to another variable. " +
                         "Dismantle it immediately with 'when', '??', or 'if is'.",
                location: varDecl.Location);
        }

        // #57: The 'global' keyword is only valid for entity types, unless @thread_local is present
        // (thread-local globals may hold value types for per-thread state like counters).
        bool isThreadLocalAnnotated = varDecl.Annotations?.Any(a => a == "thread_local") == true;
        if (varDecl.Storage == StorageClass.Global && varType is not EntityTypeInfo &&
            varType is not ErrorTypeInfo && !isThreadLocalAnnotated)
        {
            ReportError(code: SemanticDiagnosticCode.GlobalOnlyForEntities,
                message:
                $"The 'global' keyword is only valid for entity type variables, not for type '{varType.Name}'. " +
                "Use '@thread_local global' for per-thread value type storage.",
                location: varDecl.Location);
        }

        // @thread_local is only valid on global variable declarations
        if (varDecl.Annotations != null &&
            varDecl.Annotations.Any(a => a == "thread_local") &&
            varDecl.Storage != StorageClass.Global)
        {
            ReportError(code: SemanticDiagnosticCode.ThreadLocalOnNonGlobal,
                message: "'@thread_local' is only valid on global variable declarations.",
                location: varDecl.Location);
        }

        // Register variable in current scope
        // A new declaration shadows any prior steal of the same name in this scope.
        _deadrefVariables.Remove(item: varDecl.Name);

        bool declared = _registry.DeclareVariable(name: varDecl.Name, type: varType);

        if (!declared)
        {
            ReportError(code: SemanticDiagnosticCode.VariableRedeclaration,
                message: $"Variable '{varDecl.Name}' is already declared in this scope.",
                location: varDecl.Location);
        }

        // #19: Propagate lock policy from share[Policy]() to the declared variable
        if (_lastSharePolicy != null && varDecl.Initializer is GenericMethodCallExpression
            {
                MethodName: "share"
            })
        {
            _variableLockPolicies[key: varDecl.Name] = _lastSharePolicy.Value.Policy;
            _lastSharePolicy = null;
        }

        // #58: Track variant variable declaration for immediate dismantling check
        if (varType is VariantTypeInfo && varDecl.Initializer is not IdentifierExpression)
        {
            _lastDeclaredVariantVar = (varDecl.Name, varDecl.Location);
        }

        // #161: Track Lookup variables that must be dismantled before scope exit
        if (GetCarrierBaseName(type: varType) == "Lookup" &&
            varDecl.Initializer is not IdentifierExpression)
        {
            _pendingLookupVars.Add(item: (varDecl.Name, varDecl.Location));
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
                ReportWarning(code: SemanticWarningCode.UnusedTaskResult,
                    message: $"Task result from '{routineName}()' is not awaited. " +
                             "Use 'waitfor' to await the result, or 'discard' to explicitly ignore it.",
                    location: call.Location);
            }
            else
            {
                ReportWarning(code: SemanticWarningCode.UnusedRoutineReturnValue,
                    message: $"Return value of '{routineName}()' ({exprType.Name}) is unused. " +
                             "Use 'discard' to explicitly ignore the return value, or assign it to a variable.",
                    location: call.Location);
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
                            location: assign.Location);
                    }
                }
            }

            // Check that RHS is a tuple with matching arity
            if (rhsType is TupleTypeInfo tupleType)
            {
                if (tupleLhs.Elements.Count != tupleType.ElementTypes.Count)
                {
                    ReportError(code: SemanticDiagnosticCode.DestructuringArityMismatch,
                        message:
                        $"Tuple destructuring has {tupleLhs.Elements.Count} targets but the value has {tupleType.ElementTypes.Count} elements.",
                        location: assign.Location);
                }
            }

            return;
        }

        TypeSymbol targetType = AnalyzeExpression(expression: assign.Target);
        TypeSymbol valueType = AnalyzeExpression(expression: assign.Value);

        // Check if target is assignable (variable, member variable, or index)
        if (!IsAssignableTarget(target: assign.Target))
        {
            ReportError(code: SemanticDiagnosticCode.InvalidAssignmentTarget,
                message: "Invalid assignment target.",
                location: assign.Target.Location);
            return;
        }

        // Check modifiability
        if (assign.Target is IdentifierExpression id)
        {
            VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
            if (varInfo is { IsModifiable: false })
            {
                ReportError(code: SemanticDiagnosticCode.AssignmentToImmutable,
                    message: $"Cannot assign to preset variable '{id.Name}'.",
                    location: assign.Location);
            }
        }

        // Validate member variable write access (setter visibility)
        if (assign.Target is MemberExpression member)
        {
            TypeSymbol objectType = AnalyzeExpression(expression: member.Object);

            // Read-only wrapper types (Viewed, Inspected) cannot be written through
            if (IsReadOnlyWrapper(type: objectType))
            {
                ReportError(code: SemanticDiagnosticCode.WriteThroughReadOnlyWrapper,
                    message:
                    $"Cannot write to member '{member.PropertyName}' through read-only wrapper '{objectType.Name}'. " +
                    "Use Grasped[T] for exclusive write access or Claimed[T] for locked write access.",
                    location: assign.Location);
            }

            ValidateMemberVariableWriteAccess(objectType: objectType,
                memberVariableName: member.PropertyName,
                location: assign.Location);

            // Preset enforcement: cannot assign to member variables of preset variables
            if (member.Object is IdentifierExpression memberVariableTarget)
            {
                VariableInfo? targetVar =
                    _registry.LookupVariable(name: memberVariableTarget.Name);
                if (targetVar is { IsModifiable: false })
                {
                    ReportError(code: SemanticDiagnosticCode.MemberVariableAssignmentOnImmutable,
                        message:
                        $"Cannot assign to member variable '{member.PropertyName}' of preset variable '{memberVariableTarget.Name}'.",
                        location: assign.Location);
                }
            }

            // Check if we're in a @readonly method trying to modify 'me'
            if (_currentRoutine is { IsReadOnly: true } &&
                member.Object is IdentifierExpression { Name: "me" })
            {
                ReportError(code: SemanticDiagnosticCode.ModificationInReadonlyMethod,
                    message:
                    $"Cannot modify member variable '{member.PropertyName}' in a @readonly method. " +
                    "Use @writable or @migratable to allow modifications.",
                    location: assign.Location);
            }
        }

        // #81: Result/Lookup cannot be copied from variable to variable via assignment
        if (assign.Value is IdentifierExpression &&
            IsCarrierType(type: valueType) && !IsMaybeType(type: valueType))
        {
            ReportError(code: SemanticDiagnosticCode.ErrorHandlingTypeStoredInVariable,
                message: $"'{valueType.Name}' cannot be copied to another variable. " +
                         "Dismantle it immediately with 'when', '??', or 'if is'.",
                location: assign.Location);
        }

        // Check type compatibility
        if (!IsAssignableTo(source: valueType, target: targetType))
        {
            ReportError(code: SemanticDiagnosticCode.AssignmentTypeMismatch,
                message:
                $"Cannot assign value of type '{valueType.Name}' to target of type '{targetType.Name}'.",
                location: assign.Location);
        }
    }

    #endregion
}
