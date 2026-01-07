namespace Compilers.CodeGen;

using System.Text;
using Compilers.Analysis.Symbols;
using Compilers.Analysis.Types;
using Compilers.Shared.AST;

/// <summary>
/// Statement code generation: control flow, assignments, declarations, returns.
/// </summary>
public partial class LLVMCodeGenerator
{
    #region Statement Dispatch

    /// <summary>
    /// Main statement dispatch - generates code for any statement type.
    /// Returns true if the statement is a terminator (return, break, continue, throw).
    /// </summary>
    /// <param name="sb">StringBuilder to emit code to.</param>
    /// <param name="stmt">The statement to generate code for.</param>
    /// <returns>True if the statement terminates the current block.</returns>
    private bool EmitStatement(StringBuilder sb, Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement block:
                return EmitBlock(sb, block);

            case ExpressionStatement expr:
                EmitExpression(sb, expr.Expression);
                return false;

            case DeclarationStatement decl:
                EmitDeclarationStatement(sb, decl);
                return false;

            case AssignmentStatement assign:
                EmitAssignment(sb, assign);
                return false;

            case ReturnStatement ret:
                EmitReturn(sb, ret);
                return true; // Return terminates the block

            case IfStatement ifStmt:
                return EmitIf(sb, ifStmt);

            case WhileStatement whileStmt:
                EmitWhile(sb, whileStmt);
                return false;

            case ForStatement forStmt:
                EmitFor(sb, forStmt);
                return false;

            case BreakStatement:
                EmitBreak(sb);
                return true; // Break terminates the block

            case ContinueStatement:
                EmitContinue(sb);
                return true; // Continue terminates the block

            case PassStatement:
                // No-op, nothing to emit
                return false;

            case DangerStatement danger:
                // danger! block - just emit the body
                return EmitBlock(sb, danger.Body);

            case WhenStatement whenStmt:
                EmitWhen(sb, whenStmt);
                return false;

            default:
                throw new NotImplementedException($"Statement type not implemented: {stmt.GetType().Name}");
        }
    }

    /// <summary>
    /// Emits all statements in a block.
    /// Returns true if the block terminates (any statement is a terminator).
    /// </summary>
    private bool EmitBlock(StringBuilder sb, BlockStatement block)
    {
        foreach (var stmt in block.Statements)
        {
            if (EmitStatement(sb, stmt))
            {
                return true; // Block terminated early
            }
        }
        return false;
    }

    #endregion

    #region Variable Declarations

    /// <summary>
    /// Emits code for a declaration statement.
    /// Handles variable declarations with alloca + store.
    /// </summary>
    private void EmitDeclarationStatement(StringBuilder sb, DeclarationStatement decl)
    {
        if (decl.Declaration is VariableDeclaration varDecl)
        {
            EmitVariableDeclaration(sb, varDecl);
        }
        // Other declaration types (function, type) are handled at module level
    }

    /// <summary>
    /// Emits code for a variable declaration.
    /// Creates stack allocation and optionally stores initial value.
    /// </summary>
    private void EmitVariableDeclaration(StringBuilder sb, VariableDeclaration varDecl)
    {
        // Determine the type
        TypeInfo? varType = null;
        if (varDecl.Type != null)
        {
            varType = ResolveTypeExpression(varDecl.Type);
        }
        else if (varDecl.Initializer != null)
        {
            varType = GetExpressionType(varDecl.Initializer);
        }

        if (varType == null)
        {
            throw new InvalidOperationException($"Cannot determine type for variable '{varDecl.Name}'");
        }

        string llvmType = GetLLVMType(varType);

        // Allocate stack space
        string varPtr = $"%{varDecl.Name}.addr";
        EmitLine(sb, $"  {varPtr} = alloca {llvmType}");

        // Register local variable for identifier lookup
        _localVariables[varDecl.Name] = varType;

        // Store initial value if present
        if (varDecl.Initializer != null)
        {
            string value = EmitExpression(sb, varDecl.Initializer);
            EmitLine(sb, $"  store {llvmType} {value}, ptr {varPtr}");
        }
    }

    /// <summary>
    /// Resolves a type expression to a TypeInfo.
    /// </summary>
    private TypeInfo? ResolveTypeExpression(TypeExpression typeExpr)
    {
        return _registry.LookupType(typeExpr.Name);
    }

    #endregion

    #region Assignments

    /// <summary>
    /// Emits code for an assignment statement.
    /// Handles simple variable assignment and field assignment.
    /// </summary>
    private void EmitAssignment(StringBuilder sb, AssignmentStatement assign)
    {
        // Evaluate the value first
        string value = EmitExpression(sb, assign.Value);

        // Determine target type and emit store
        switch (assign.Target)
        {
            case IdentifierExpression id:
                EmitVariableAssignment(sb, id.Name, value);
                break;

            case MemberExpression member:
                EmitFieldAssignment(sb, member, value);
                break;

            case IndexExpression index:
                EmitIndexAssignment(sb, index, value);
                break;

            default:
                throw new NotImplementedException($"Assignment target not implemented: {assign.Target.GetType().Name}");
        }
    }

    /// <summary>
    /// Emits a store to a local variable.
    /// </summary>
    private void EmitVariableAssignment(StringBuilder sb, string varName, string value)
    {
        if (!_localVariables.TryGetValue(varName, out var varType))
        {
            throw new InvalidOperationException($"Variable '{varName}' not found");
        }

        string llvmType = GetLLVMType(varType);
        EmitLine(sb, $"  store {llvmType} {value}, ptr %{varName}.addr");
    }

    /// <summary>
    /// Emits a store to a field.
    /// </summary>
    private void EmitFieldAssignment(StringBuilder sb, MemberExpression member, string value)
    {
        // Evaluate the object
        string target = EmitExpression(sb, member.Object);
        TypeInfo? targetType = GetExpressionType(member.Object);

        if (targetType is EntityTypeInfo entity)
        {
            EmitEntityFieldWrite(sb, target, entity, member.PropertyName, value);
        }
        else
        {
            throw new InvalidOperationException($"Cannot assign to field on type: {targetType?.Name}");
        }
    }

    /// <summary>
    /// Emits a store to an indexed location.
    /// </summary>
    private void EmitIndexAssignment(StringBuilder sb, IndexExpression index, string value)
    {
        string target = EmitExpression(sb, index.Object);
        string indexValue = EmitExpression(sb, index.Index);

        // TODO: Determine element type from target type
        string elemPtr = NextTemp();
        EmitLine(sb, $"  {elemPtr} = getelementptr i32, ptr {target}, i64 {indexValue}");
        EmitLine(sb, $"  store i32 {value}, ptr {elemPtr}");
    }

    #endregion

    #region Return Statements

    /// <summary>
    /// Emits code for a return statement.
    /// </summary>
    private void EmitReturn(StringBuilder sb, ReturnStatement ret)
    {
        if (ret.Value == null)
        {
            EmitLine(sb, "  ret void");
        }
        else
        {
            string value = EmitExpression(sb, ret.Value);
            // Use expression type if available, otherwise fall back to current function's return type
            TypeInfo? retType = GetExpressionType(ret.Value) ?? _currentFunctionReturnType;
            if (retType == null)
            {
                throw new InvalidOperationException("Cannot determine return type for return statement");
            }
            string llvmType = GetLLVMType(retType);
            EmitLine(sb, $"  ret {llvmType} {value}");
        }
    }

    #endregion

    #region Control Flow - If/Else

    /// <summary>
    /// Emits code for an if statement with optional else branch.
    /// Returns true if both branches terminate (meaning the if as a whole terminates).
    /// </summary>
    private bool EmitIf(StringBuilder sb, IfStatement ifStmt)
    {
        string condition = EmitExpression(sb, ifStmt.Condition);

        string thenLabel = NextLabel("if_then");
        string endLabel = NextLabel("if_end");

        if (ifStmt.ElseBranch != null)
        {
            string elseLabel = NextLabel("if_else");
            EmitLine(sb, $"  br i1 {condition}, label %{thenLabel}, label %{elseLabel}");

            // Then branch
            EmitLine(sb, $"{thenLabel}:");
            bool thenTerminated = EmitStatement(sb, ifStmt.ThenBranch);
            if (!thenTerminated)
            {
                EmitLine(sb, $"  br label %{endLabel}");
            }

            // Else branch
            EmitLine(sb, $"{elseLabel}:");
            bool elseTerminated = EmitStatement(sb, ifStmt.ElseBranch);
            if (!elseTerminated)
            {
                EmitLine(sb, $"  br label %{endLabel}");
            }

            // If both branches terminated, the end block is unreachable
            // but we still need to emit it for LLVM (it will be dead code eliminated)
            if (thenTerminated && elseTerminated)
            {
                // Both branches return - the if statement as a whole terminates
                // Still emit end label but mark that we terminated
                EmitLine(sb, $"{endLabel}:");
                return true;
            }

            // End block is reachable from at least one branch
            EmitLine(sb, $"{endLabel}:");
            return false;
        }
        else
        {
            EmitLine(sb, $"  br i1 {condition}, label %{thenLabel}, label %{endLabel}");

            // Then branch
            EmitLine(sb, $"{thenLabel}:");
            bool thenTerminated = EmitStatement(sb, ifStmt.ThenBranch);
            if (!thenTerminated)
            {
                EmitLine(sb, $"  br label %{endLabel}");
            }

            // End block (always reachable via the else path, even if then returns)
            EmitLine(sb, $"{endLabel}:");
            return false; // If without else never fully terminates
        }
    }

    #endregion

    #region Control Flow - While Loop

    /// <summary>
    /// Stack of loop labels for break/continue.
    /// </summary>
    private readonly Stack<(string ContinueLabel, string BreakLabel)> _loopStack = new();

    /// <summary>
    /// Emits code for a while loop.
    /// </summary>
    private void EmitWhile(StringBuilder sb, WhileStatement whileStmt)
    {
        string condLabel = NextLabel("while_cond");
        string bodyLabel = NextLabel("while_body");
        string endLabel = NextLabel("while_end");

        // Push loop labels for break/continue
        _loopStack.Push((condLabel, endLabel));

        // Jump to condition
        EmitLine(sb, $"  br label %{condLabel}");

        // Condition block
        EmitLine(sb, $"{condLabel}:");
        string condition = EmitExpression(sb, whileStmt.Condition);
        EmitLine(sb, $"  br i1 {condition}, label %{bodyLabel}, label %{endLabel}");

        // Body block
        EmitLine(sb, $"{bodyLabel}:");
        bool bodyTerminated = EmitStatement(sb, whileStmt.Body);
        if (!bodyTerminated)
        {
            EmitLine(sb, $"  br label %{condLabel}");
        }

        // End block
        EmitLine(sb, $"{endLabel}:");

        // Pop loop labels
        _loopStack.Pop();
    }

    #endregion

    #region Control Flow - For Loop

    /// <summary>
    /// Emits code for a for loop.
    /// for x in iterable { body } becomes:
    ///   iterator = iterable.__iter__()
    ///   while iterator.__has_next__() { x = iterator.__next__(); body }
    /// </summary>
    private void EmitFor(StringBuilder sb, ForStatement forStmt)
    {
        // For now, implement a simple numeric range-based for loop
        // TODO: Implement proper iterator protocol

        string condLabel = NextLabel("for_cond");
        string bodyLabel = NextLabel("for_body");
        string incrLabel = NextLabel("for_incr");
        string endLabel = NextLabel("for_end");

        // Push loop labels for break/continue
        _loopStack.Push((incrLabel, endLabel));

        // Evaluate the iterable
        string iterable = EmitExpression(sb, forStmt.Iterable);

        // TODO: For now, assume it's a range and just jump to condition
        EmitLine(sb, $"  br label %{condLabel}");

        // Condition block - check if iterator has more elements
        EmitLine(sb, $"{condLabel}:");
        // TODO: Implement proper iterator checking
        // For now, just use unconditional true as placeholder
        EmitLine(sb, $"  br i1 true, label %{bodyLabel}, label %{endLabel}");

        // Body block
        EmitLine(sb, $"{bodyLabel}:");
        bool bodyTerminated = EmitStatement(sb, forStmt.Body);
        if (!bodyTerminated)
        {
            EmitLine(sb, $"  br label %{incrLabel}");
        }

        // Increment block
        EmitLine(sb, $"{incrLabel}:");
        // TODO: Implement iterator advancement
        EmitLine(sb, $"  br label %{condLabel}");

        // End block
        EmitLine(sb, $"{endLabel}:");

        // Pop loop labels
        _loopStack.Pop();
    }

    #endregion

    #region Control Flow - Break/Continue

    /// <summary>
    /// Emits code for a break statement.
    /// </summary>
    private void EmitBreak(StringBuilder sb)
    {
        if (_loopStack.Count == 0)
        {
            throw new InvalidOperationException("Break statement outside of loop");
        }

        var (_, breakLabel) = _loopStack.Peek();
        EmitLine(sb, $"  br label %{breakLabel}");
    }

    /// <summary>
    /// Emits code for a continue statement.
    /// </summary>
    private void EmitContinue(StringBuilder sb)
    {
        if (_loopStack.Count == 0)
        {
            throw new InvalidOperationException("Continue statement outside of loop");
        }

        var (continueLabel, _) = _loopStack.Peek();
        EmitLine(sb, $"  br label %{continueLabel}");
    }

    #endregion

    #region Control Flow - When (Pattern Matching)

    /// <summary>
    /// Emits code for a when statement (pattern matching).
    /// </summary>
    private void EmitWhen(StringBuilder sb, WhenStatement whenStmt)
    {
        // Evaluate the subject expression once
        string subject = EmitExpression(sb, whenStmt.Expression);
        string endLabel = NextLabel("when_end");

        // Generate labels for each clause
        var clauseLabels = new List<string>();
        for (int i = 0; i < whenStmt.Clauses.Count; i++)
        {
            clauseLabels.Add(NextLabel($"when_case{i}"));
        }

        // Jump to first clause
        if (clauseLabels.Count > 0)
        {
            EmitLine(sb, $"  br label %{clauseLabels[0]}");
        }
        else
        {
            EmitLine(sb, $"  br label %{endLabel}");
        }

        // Emit each clause
        for (int i = 0; i < whenStmt.Clauses.Count; i++)
        {
            var clause = whenStmt.Clauses[i];
            string currentLabel = clauseLabels[i];
            string nextLabel = i + 1 < clauseLabels.Count ? clauseLabels[i + 1] : endLabel;

            EmitLine(sb, $"{currentLabel}:");

            // Emit pattern matching code
            string bodyLabel = NextLabel($"when_body{i}");
            EmitPatternMatch(sb, subject, clause.Pattern, bodyLabel, nextLabel);

            // Emit body
            EmitLine(sb, $"{bodyLabel}:");
            bool bodyTerminated = EmitStatement(sb, clause.Body);
            if (!bodyTerminated)
            {
                EmitLine(sb, $"  br label %{endLabel}");
            }
        }

        // End block
        EmitLine(sb, $"{endLabel}:");
    }

    /// <summary>
    /// Emits code for pattern matching.
    /// Branches to matchLabel if pattern matches, failLabel otherwise.
    /// </summary>
    private void EmitPatternMatch(StringBuilder sb, string subject, Pattern pattern, string matchLabel, string failLabel)
    {
        switch (pattern)
        {
            case LiteralPattern lit:
                // Compare subject with literal value (already parsed by parser)
                string litValue = lit.Value?.ToString() ?? "0";
                string result = NextTemp();
                // TODO: Determine proper type from context
                EmitLine(sb, $"  {result} = icmp eq i32 {subject}, {litValue}");
                EmitLine(sb, $"  br i1 {result}, label %{matchLabel}, label %{failLabel}");
                break;

            case WildcardPattern:
            case ElsePattern:
                // Always matches - unconditional branch
                EmitLine(sb, $"  br label %{matchLabel}");
                break;

            case IdentifierPattern id:
                // Bind value to variable and always match
                // TODO: Store subject into variable id.Name
                EmitLine(sb, $"  br label %{matchLabel}");
                break;

            case TypePattern typePattern:
                // TODO: Implement runtime type checking
                if (typePattern.VariableName != null)
                {
                    // TODO: Bind subject to variable
                }
                EmitLine(sb, $"  br label %{matchLabel}");
                break;

            case VariantPattern variant:
                EmitVariantPatternMatch(sb, subject, variant, matchLabel, failLabel);
                break;

            case GuardPattern guardPattern:
                EmitGuardPatternMatch(sb, subject, guardPattern, matchLabel, failLabel);
                break;

            case NonePattern:
                // Check if subject is None (null pointer)
                string noneCmp = NextTemp();
                EmitLine(sb, $"  {noneCmp} = icmp eq ptr {subject}, null");
                EmitLine(sb, $"  br i1 {noneCmp}, label %{matchLabel}, label %{failLabel}");
                break;

            case CrashablePattern:
                // TODO: Check if subject is an error type
                EmitLine(sb, $"  br label %{failLabel}");
                break;

            case ExpressionPattern exprPattern:
                // Expression pattern: evaluate condition directly
                string condition = EmitExpression(sb, exprPattern.Expression);
                EmitLine(sb, $"  br i1 {condition}, label %{matchLabel}, label %{failLabel}");
                break;

            default:
                // Unknown pattern - always match
                EmitLine(sb, $"  br label %{matchLabel}");
                break;
        }
    }

    /// <summary>
    /// Emits code for variant pattern matching (is CASE payload).
    /// </summary>
    private void EmitVariantPatternMatch(StringBuilder sb, string subject, VariantPattern variant, string matchLabel, string failLabel)
    {
        // Extract tag from variant (first field)
        string tagPtr = NextTemp();
        string tag = NextTemp();
        EmitLine(sb, $"  {tagPtr} = getelementptr {{ i32 }}, ptr {subject}, i32 0, i32 0");
        EmitLine(sb, $"  {tag} = load i32, ptr {tagPtr}");

        // Compare tag with expected value
        // TODO: Look up tag value from variant type info
        int expectedTag = 0; // Placeholder
        string cmp = NextTemp();
        EmitLine(sb, $"  {cmp} = icmp eq i32 {tag}, {expectedTag}");
        EmitLine(sb, $"  br i1 {cmp}, label %{matchLabel}, label %{failLabel}");
    }

    /// <summary>
    /// Emits code for guard pattern matching (pattern if condition).
    /// </summary>
    private void EmitGuardPatternMatch(StringBuilder sb, string subject, GuardPattern guardPattern, string matchLabel, string failLabel)
    {
        // First check inner pattern
        string guardCheck = NextLabel("guard_check");
        EmitPatternMatch(sb, subject, guardPattern.InnerPattern, guardCheck, failLabel);

        // Then check guard condition
        EmitLine(sb, $"{guardCheck}:");
        string guardResult = EmitExpression(sb, guardPattern.Guard);
        EmitLine(sb, $"  br i1 {guardResult}, label %{matchLabel}, label %{failLabel}");
    }

    #endregion
}
