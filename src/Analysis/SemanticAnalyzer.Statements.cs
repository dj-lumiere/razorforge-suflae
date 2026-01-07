namespace Compilers.Analysis;

using Compilers.Analysis.Enums;
using Compilers.Analysis.Scopes;
using Compilers.Analysis.Symbols;
using Compilers.Analysis.Types;
using Compilers.Shared.AST;
using TypeSymbol = Compilers.Analysis.Types.TypeInfo;

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

            case BreakStatement:
            case ContinueStatement:
            case PassStatement:
                // These are simple control flow statements with no type analysis needed
                break;

            case DestructuringStatement destruct:
                AnalyzeDestructuringStatement(destruct: destruct);
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
                    message: $"Unknown statement type: {statement.GetType().Name}",
                    location: statement.Location);
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
                    message: $"Unexpected declaration in statement context: {decl.Declaration.GetType().Name}",
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
            ReportError(
                message: $"Variable '{varDecl.Name}' requires either a type annotation or an initializer.",
                location: varDecl.Location);
            varType = ErrorTypeInfo.Instance;
        }

        // If we have both type annotation and initializer, verify compatibility
        if (varDecl.Type != null && varDecl.Initializer != null)
        {
            TypeSymbol initType = AnalyzeExpression(expression: varDecl.Initializer);
            if (!IsAssignableTo(source: initType, target: varType))
            {
                ReportError(
                    message: $"Cannot assign value of type '{initType.Name}' to variable of type '{varType.Name}'.",
                    location: varDecl.Location);
            }
        }

        // Register variable in current scope
        _registry.DeclareVariable(
            name: varDecl.Name,
            type: varType,
            isMutable: varDecl.IsMutable);
    }

    private void AnalyzeExpressionStatement(ExpressionStatement expr)
    {
        // Analyze the expression for side effects and type validation
        AnalyzeExpression(expression: expr.Expression);
    }

    private void AnalyzeAssignmentStatement(AssignmentStatement assign)
    {
        TypeSymbol targetType = AnalyzeExpression(expression: assign.Target);
        TypeSymbol valueType = AnalyzeExpression(expression: assign.Value);

        // Check if target is assignable (variable, field, or index)
        if (!IsAssignableTarget(target: assign.Target))
        {
            ReportError(
                message: "Invalid assignment target.",
                location: assign.Target.Location);
            return;
        }

        // Check mutability
        if (assign.Target is IdentifierExpression id)
        {
            VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
            if (varInfo is { IsMutable: false })
            {
                ReportError(
                    message: $"Cannot assign to immutable variable '{id.Name}'.",
                    location: assign.Location);
            }
        }

        // Validate field write access (setter visibility)
        if (assign.Target is MemberExpression member)
        {
            TypeSymbol objectType = AnalyzeExpression(expression: member.Object);
            ValidateFieldWriteAccess(objectType: objectType, fieldName: member.PropertyName, location: assign.Location);
        }

        // Check type compatibility
        if (!IsAssignableTo(source: valueType, target: targetType))
        {
            ReportError(
                message: $"Cannot assign value of type '{valueType.Name}' to target of type '{targetType.Name}'.",
                location: assign.Location);
        }
    }

    private void AnalyzeIfStatement(IfStatement ifStmt)
    {
        TypeSymbol conditionType = AnalyzeExpression(expression: ifStmt.Condition);

        // Condition must be boolean
        if (!IsBoolType(type: conditionType))
        {
            ReportError(
                message: $"If condition must be boolean, got '{conditionType.Name}'.",
                location: ifStmt.Condition.Location);
        }

        // Analyze then branch
        AnalyzeStatement(statement: ifStmt.ThenStatement);

        // Analyze else branch if present
        if (ifStmt.ElseStatement != null)
        {
            AnalyzeStatement(statement: ifStmt.ElseStatement);
        }
    }

    private void AnalyzeWhileStatement(WhileStatement whileStmt)
    {
        TypeSymbol conditionType = AnalyzeExpression(expression: whileStmt.Condition);

        // Condition must be boolean
        if (!IsBoolType(type: conditionType))
        {
            ReportError(
                message: $"While condition must be boolean, got '{conditionType.Name}'.",
                location: whileStmt.Condition.Location);
        }

        // Analyze loop body
        _registry.EnterScope(kind: ScopeKind.Block, name: "while");
        AnalyzeStatement(statement: whileStmt.Body);
        _registry.ExitScope();
    }

    private void AnalyzeForStatement(ForStatement forStmt)
    {
        _registry.EnterScope(kind: ScopeKind.Block, name: "for");

        // Analyze iterable expression
        TypeSymbol iterableType = AnalyzeExpression(expression: forStmt.Iterable);

        // Get element type from iterable
        TypeSymbol elementType = GetIterableElementType(iterableType: iterableType, location: forStmt.Location);

        // Declare loop variable with element type
        _registry.DeclareVariable(
            name: forStmt.Variable,
            type: elementType,
            isMutable: false); // Loop variables are immutable

        // Analyze loop body
        AnalyzeStatement(statement: forStmt.Body);

        _registry.ExitScope();
    }

    private void AnalyzeWhenStatement(WhenStatement whenStmt)
    {
        TypeSymbol matchedType = AnalyzeExpression(expression: whenStmt.Expression);

        foreach (WhenClause clause in whenStmt.Clauses)
        {
            _registry.EnterScope(kind: ScopeKind.Block, name: "when_clause");

            // Analyze pattern and bind variables
            AnalyzePattern(pattern: clause.Pattern, matchedType: matchedType);

            // Analyze clause body
            AnalyzeStatement(statement: clause.Body);

            _registry.ExitScope();
        }
    }

    private void AnalyzeReturnStatement(ReturnStatement ret)
    {
        if (_currentRoutine == null)
        {
            ReportError(
                message: "Return statement outside of function.",
                location: ret.Location);
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
                    message: $"Cannot return value of type '{returnType.Name}' from function expecting '{_currentRoutine.ReturnType.Name}'.",
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
            ReportError(
                message: "Throw statement is only allowed in failable functions (marked with !).",
                location: throwStmt.Location);
            return;
        }

        TypeSymbol errorType = AnalyzeExpression(expression: throwStmt.Error);

        // Error must implement Crashable protocol
        if (!ImplementsProtocol(type: errorType, protocolName: "Crashable"))
        {
            ReportError(
                message: $"Thrown value must implement Crashable protocol, got '{errorType.Name}'.",
                location: throwStmt.Error.Location);
        }

        // Mark routine as having throw statements (for variant generation)
        _currentRoutine.HasThrow = true;
    }

    private void AnalyzeAbsentStatement(AbsentStatement absent)
    {
        if (_currentRoutine == null || !_currentRoutine.IsFailable)
        {
            ReportError(
                message: "Absent statement is only allowed in failable functions (marked with !).",
                location: absent.Location);
            return;
        }

        // Mark routine as having absent statements (for variant generation)
        _currentRoutine.HasAbsent = true;
    }

    private void AnalyzeDestructuringStatement(DestructuringStatement destruct)
    {
        TypeSymbol initType = AnalyzeExpression(expression: destruct.Initializer);

        // Analyze the destructuring pattern and bind variables
        AnalyzeDestructuringPattern(pattern: destruct.Pattern, sourceType: initType, isMutable: destruct.IsMutable);
    }

    private void AnalyzeDangerStatement(DangerStatement danger)
    {
        if (_registry.Language == Language.Suflae)
        {
            ReportError(
                message: "Danger blocks are not available in Suflae.",
                location: danger.Location);
            return;
        }

        // Danger blocks cannot be nested
        if (InDangerBlock)
        {
            ReportError(
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

    private void AnalyzeViewingStatement(ViewingStatement viewing)
    {
        if (_registry.Language == Language.Suflae)
        {
            ReportError(
                message: "Viewing blocks are not available in Suflae.",
                location: viewing.Location);
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
                message: "Hijacking blocks are not available in Suflae.",
                location: hijacking.Location);
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
                message: "Inspecting blocks are not available in Suflae.",
                location: inspecting.Location);
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
                message: "Seizing blocks are not available in Suflae.",
                location: seizing.Location);
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
