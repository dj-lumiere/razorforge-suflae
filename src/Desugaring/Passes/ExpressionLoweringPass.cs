using Compiler.Lexer;
using SemanticVerification.Symbols;
using SemanticVerification.Types;
using SyntaxTree;

namespace Compiler.Desugaring.Passes;

/// <summary>
/// Lowers high-level expression constructs to simpler ANF-style statement+expression forms.
/// Runs last in the per-file desugaring pipeline (after ControlFlowLoweringPass).
///
/// Sub-transformations applied in-order during a single recursive walk:
/// <list type="bullet">
///   <item>1a. Chained comparisons: <c>a &lt; b &lt; c</c> → <c>(a &lt; b) and (b &lt; c)</c></item>
///   <item>1b. None-coalescing: <c>a ?? b</c> → temp vars + WhenStatement (preserves lazy eval)</item>
///   <item>1c. Force-unwrap: <c>a!!</c> → <c>a.$unwrap()</c> — handled by <see cref="OperatorLoweringPass"/>
///         so that stdlib bodies (which bypass this pass) are also covered.</item>
///   <item>1d. Optional member access: <c>a?.prop</c> → temp vars + WhenStatement</item>
/// </list>
///
/// Hoisting transforms (1b, 1d) use ANF lifting: they return a list of statements
/// to splice before the containing statement plus a replacement <see cref="IdentifierExpression"/>.
/// </summary>
internal sealed class ExpressionLoweringPass(DesugaringContext ctx)
{
    private int _tempCount;

    private string NextTempName(string prefix) => $"_{prefix}_{_tempCount++}";

    public void Run(Program program)
    {
        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[i])
            {
                case RoutineDeclaration r:
                {
                    Statement newBody = LowerStatementFull(r.Body);
                    if (!ReferenceEquals(newBody, r.Body))
                        program.Declarations[i] = r with { Body = newBody };
                    break;
                }

                case EntityDeclaration e:
                    LowerMemberList(e.Members);
                    break;

                case RecordDeclaration rec:
                    LowerMemberList(rec.Members);
                    break;

                case CrashableDeclaration cr:
                    LowerMemberList(cr.Members);
                    break;
            }
        }
    }

    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int j = 0; j < members.Count; j++)
        {
            if (members[j] is not RoutineDeclaration m) continue;
            Statement newBody = LowerStatementFull(m.Body);
            if (!ReferenceEquals(newBody, m.Body))
                members[j] = m with { Body = newBody };
        }
    }

    // ─── Statement lowering ──────────────────────────────────────────────────────

    /// <summary>
    /// Fully lowers a statement, wrapping in a <see cref="BlockStatement"/> if hoisted
    /// statements need to precede it.
    /// </summary>
    private Statement LowerStatementFull(Statement stmt)
    {
        var (hoisted, lowered) = LowerStatement(stmt);
        if (hoisted.Count == 0) return lowered;

        var stmts = new List<Statement>(capacity: hoisted.Count + 1);
        stmts.AddRange(hoisted);
        stmts.Add(lowered);
        return new BlockStatement(Statements: stmts, Location: stmt.Location);
    }

    /// <summary>
    /// Lowers a statement, returning any statements that must be prepended before it.
    /// </summary>
    private (List<Statement> Hoisted, Statement Lowered) LowerStatement(Statement stmt)
    {
        switch (stmt)
        {
            // ── Compound: recurse into children ────────────────────────────────

            case BlockStatement b:
            {
                List<Statement> loweredList = LowerStatementList(b.Statements);
                if (ReferenceEquals(loweredList, b.Statements)) return ([], b);
                return ([], b with { Statements = loweredList });
            }

            case IfStatement ifs:
            {
                var (condH, loweredCond) = LowerExpr(ifs.Condition);
                Statement then = LowerStatementFull(ifs.ThenStatement);
                Statement? elseS = ifs.ElseStatement != null
                    ? LowerStatementFull(ifs.ElseStatement)
                    : null;
                bool changed = !ReferenceEquals(loweredCond, ifs.Condition)
                               || !ReferenceEquals(then, ifs.ThenStatement)
                               || !ReferenceEquals(elseS, ifs.ElseStatement);
                if (!changed && condH.Count == 0) return ([], stmt);
                return (condH,
                    ifs with { Condition = loweredCond, ThenStatement = then, ElseStatement = elseS });
            }

            case WhileStatement w:
            {
                var (condH, loweredCond) = LowerExpr(w.Condition);
                Statement body = LowerStatementFull(w.Body);
                Statement? elseB = w.ElseBranch != null
                    ? LowerStatementFull(w.ElseBranch)
                    : null;
                bool changed = !ReferenceEquals(loweredCond, w.Condition)
                               || !ReferenceEquals(body, w.Body)
                               || !ReferenceEquals(elseB, w.ElseBranch);
                if (!changed && condH.Count == 0) return ([], stmt);
                return (condH, w with { Condition = loweredCond, Body = body, ElseBranch = elseB });
            }

            case LoopStatement loop:
            {
                Statement body = LowerStatementFull(loop.Body);
                if (ReferenceEquals(body, loop.Body)) return ([], stmt);
                return ([], loop with { Body = body });
            }

            case WhenStatement w:
            {
                var (subjH, loweredSubj) = LowerExpr(w.Expression);
                bool clauseChanged = false;
                var clauses = new List<WhenClause>(capacity: w.Clauses.Count);
                foreach (WhenClause c in w.Clauses)
                {
                    Statement lBody = LowerStatementFull(c.Body);
                    if (!ReferenceEquals(lBody, c.Body))
                    {
                        clauses.Add(c with { Body = lBody });
                        clauseChanged = true;
                    }
                    else
                    {
                        clauses.Add(c);
                    }
                }

                bool changed = !ReferenceEquals(loweredSubj, w.Expression) || clauseChanged;
                if (!changed && subjH.Count == 0) return ([], stmt);
                return (subjH, w with { Expression = loweredSubj, Clauses = clauses });
            }

            case UsingStatement u:
            {
                var (hoisted, loweredRes) = LowerExpr(u.Resource);
                Statement body = LowerStatementFull(u.Body);
                bool changed = !ReferenceEquals(loweredRes, u.Resource)
                               || !ReferenceEquals(body, u.Body);
                if (!changed && hoisted.Count == 0) return ([], stmt);
                return (hoisted, u with { Resource = loweredRes, Body = body });
            }

            case DangerStatement d:
            {
                Statement lowered = LowerStatementFull(d.Body);
                if (ReferenceEquals(lowered, d.Body)) return ([], stmt);
                return ([], d with { Body = (BlockStatement)lowered });
            }

            // ── Simple: lower the contained expressions ─────────────────────────

            case AssignmentStatement asgn:
            {
                var (hoisted, loweredVal) = LowerExpr(asgn.Value);
                if (hoisted.Count == 0 && ReferenceEquals(loweredVal, asgn.Value)) return ([], stmt);
                return (hoisted, asgn with { Value = loweredVal });
            }

            case DeclarationStatement { Declaration: VariableDeclaration vd } decl
                when vd.Initializer != null:
            {
                var (hoisted, loweredInit) = LowerExpr(vd.Initializer);
                if (hoisted.Count == 0 && ReferenceEquals(loweredInit, vd.Initializer))
                    return ([], stmt);
                return (hoisted, decl with { Declaration = vd with { Initializer = loweredInit } });
            }

            case ReturnStatement { Value: not null } ret:
            {
                var (hoisted, loweredVal) = LowerExpr(ret.Value);
                if (hoisted.Count == 0 && ReferenceEquals(loweredVal, ret.Value)) return ([], stmt);
                return (hoisted, ret with { Value = loweredVal });
            }

            case ExpressionStatement { Expression: CompoundAssignmentExpression } es:
            {
                // Compound assignment in statement position: the result value is discarded.
                // LowerExpr for the fallback path returns (hoisted=[AssignmentStatement], residual=LHS).
                // Don't emit the residual as a bare expression statement.
                var (hoisted, loweredExpr) = LowerExpr(es.Expression);
                if (hoisted.Count == 0)
                    return ([], es with { Expression = loweredExpr });
                if (hoisted.Count == 1) return ([], hoisted[0]);
                return ([], new BlockStatement(hoisted, es.Location));
            }

            case ExpressionStatement es:
            {
                var (hoisted, loweredExpr) = LowerExpr(es.Expression);
                if (hoisted.Count == 0 && ReferenceEquals(loweredExpr, es.Expression)) return ([], stmt);
                return (hoisted, es with { Expression = loweredExpr });
            }

            case DiscardStatement ds:
            {
                var (hoisted, loweredExpr) = LowerExpr(ds.Expression);
                if (hoisted.Count == 0 && ReferenceEquals(loweredExpr, ds.Expression)) return ([], stmt);
                return (hoisted, ds with { Expression = loweredExpr });
            }

            case BecomesStatement bs:
            {
                var (hoisted, loweredVal) = LowerExpr(bs.Value);
                if (hoisted.Count == 0 && ReferenceEquals(loweredVal, bs.Value)) return ([], stmt);
                return (hoisted, bs with { Value = loweredVal });
            }

            case ThrowStatement t:
            {
                var (hoisted, loweredErr) = LowerExpr(t.Error);
                if (hoisted.Count == 0 && ReferenceEquals(loweredErr, t.Error)) return ([], stmt);
                return (hoisted, t with { Error = loweredErr });
            }

            // D-AST-7: recurse into variant return value expressions.
            case VariantReturnStatement { Value: not null } vrs:
            {
                var (hoisted, loweredVal) = LowerExpr(vrs.Value);
                if (hoisted.Count == 0 && ReferenceEquals(loweredVal, vrs.Value)) return ([], stmt);
                return (hoisted, vrs with { Value = loweredVal });
            }

            default:
                return ([], stmt);
        }
    }

    /// <summary>
    /// Lowers a flat statement list, splicing in hoisted statements at each site.
    /// Returns the original list if no changes were made (preserving reference identity).
    /// </summary>
    private List<Statement> LowerStatementList(List<Statement> stmts)
    {
        var result = new List<Statement>(capacity: stmts.Count);
        bool anyChanged = false;

        foreach (Statement stmt in stmts)
        {
            var (hoisted, lowered) = LowerStatement(stmt);
            if (hoisted.Count > 0 || !ReferenceEquals(lowered, stmt))
                anyChanged = true;
            result.AddRange(hoisted);
            result.Add(lowered);
        }

        return anyChanged ? result : stmts;
    }

    // ─── Expression lowering ─────────────────────────────────────────────────────

    /// <summary>
    /// Lowers an expression, returning:
    /// <list type="bullet">
    ///   <item>A list of statements to hoist before the containing statement.</item>
    ///   <item>A replacement expression (often the original, or a temp-var ref).</item>
    /// </list>
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerExpr(Expression expr)
    {
        switch (expr)
        {
            // ── Step 1a: chained comparisons ─────────────────────────────────────
            case ChainedComparisonExpression chain:
                return LowerChainedComparison(chain);

            // ── Step 1b: none-coalescing (??) ────────────────────────────────────
            case BinaryExpression { Operator: BinaryOperator.NoneCoalesce } binary:
                return LowerNoneCoalesce(binary);

            // ── Step 1c: force-unwrap (!!) — handled by OperatorLoweringPass ────────
            // !! is desugared to operand.$unwrap() in OperatorLoweringPass so that
            // stdlib bodies (which bypass ExpressionLoweringPass) are also covered.

            // ── Step 1d: optional member access (?.) ─────────────────────────────
            case OptionalMemberExpression optMember:
                return LowerOptionalMember(optMember);

            // ── Step 1e: flags combination (and/but on FlagsTypeInfo) ─────────────
            case BinaryExpression { Operator: BinaryOperator.And or BinaryOperator.But } flagsBin
                when flagsBin.Left.ResolvedType is FlagsTypeInfo:
                return LowerFlagsCombination(flagsBin);

            // ── Step 1f: carrier absence checks (is None / is Blank) ─────────────
            case IsPatternExpression ipe:
                return LowerIsPatternExpression(ipe);

            // ── Recursive descent for all other node types ────────────────────────

            case BinaryExpression bin:
            {
                var (leftH, loweredLeft) = LowerExpr(bin.Left);
                var (rightH, loweredRight) = LowerExpr(bin.Right);
                var hoisted = Concat(leftH, rightH);
                if (hoisted.Count == 0
                    && ReferenceEquals(loweredLeft, bin.Left)
                    && ReferenceEquals(loweredRight, bin.Right))
                    return ([], expr);
                return (hoisted, bin with { Left = loweredLeft, Right = loweredRight });
            }

            case UnaryExpression unary:
            {
                var (h, lowered) = LowerExpr(unary.Operand);
                if (h.Count == 0 && ReferenceEquals(lowered, unary.Operand)) return ([], expr);
                return (h, unary with { Operand = lowered });
            }

            case CallExpression call:
            {
                var hoisted = new List<Statement>();
                var (calleeH, loweredCallee) = LowerExpr(call.Callee);
                hoisted.AddRange(calleeH);

                var args = new List<Expression>(capacity: call.Arguments.Count);
                bool argsChanged = false;
                foreach (Expression arg in call.Arguments)
                {
                    // Preserve NamedArgumentExpression wrappers — codegen uses arg names to detect
                    // direct field constructors (e.g., Point(x: 1, y: 2) vs CStr(from: v)).
                    // Only lower the inner value expression, not the wrapper itself.
                    if (arg is NamedArgumentExpression namedArg)
                    {
                        var (h, loweredValue) = LowerExpr(namedArg.Value);
                        hoisted.AddRange(h);
                        Expression loweredNamed = ReferenceEquals(loweredValue, namedArg.Value)
                            ? namedArg
                            : namedArg with { Value = loweredValue };
                        args.Add(loweredNamed);
                        if (!ReferenceEquals(loweredNamed, arg)) argsChanged = true;
                    }
                    else
                    {
                        var (h, lowered) = LowerExpr(arg);
                        hoisted.AddRange(h);
                        args.Add(lowered);
                        if (!ReferenceEquals(lowered, arg)) argsChanged = true;
                    }
                }

                if (hoisted.Count == 0 && !argsChanged
                    && ReferenceEquals(loweredCallee, call.Callee))
                    return ([], expr);
                return (hoisted, call with { Callee = loweredCallee, Arguments = args });
            }

            case MemberExpression mem:
            {
                var (h, lowered) = LowerExpr(mem.Object);
                if (h.Count == 0 && ReferenceEquals(lowered, mem.Object)) return ([], expr);
                return (h, mem with { Object = lowered });
            }

            case IndexExpression idx:
            {
                var (objH, loweredObj) = LowerExpr(idx.Object);
                var (idxH, loweredIdx) = LowerExpr(idx.Index);
                var hoisted = Concat(objH, idxH);
                if (hoisted.Count == 0
                    && ReferenceEquals(loweredObj, idx.Object)
                    && ReferenceEquals(loweredIdx, idx.Index))
                    return ([], expr);
                return (hoisted, idx with { Object = loweredObj, Index = loweredIdx });
            }

            case NamedArgumentExpression named:
                // Strip the wrapper — after SA the argument is already in its correct position.
                return LowerExpr(named.Value);

            case CreatorExpression creator:
            {
                var hoisted = new List<Statement>();
                var members = new List<(string Name, Expression Value)>(
                    capacity: creator.MemberVariables.Count);
                bool changed = false;
                foreach ((string name, Expression value) in creator.MemberVariables)
                {
                    var (h, lowered) = LowerExpr(value);
                    hoisted.AddRange(h);
                    members.Add((name, lowered));
                    if (!ReferenceEquals(lowered, value)) changed = true;
                }

                if (!changed && hoisted.Count == 0) return ([], expr);
                return (hoisted, creator with { MemberVariables = members });
            }

            case WithExpression withExpr:
                return LowerWithExpression(withExpr);

            case GenericMethodCallExpression gmc:
            {
                var hoisted = new List<Statement>();
                var (objH, loweredObj) = LowerExpr(gmc.Object);
                hoisted.AddRange(objH);

                var args = new List<Expression>(capacity: gmc.Arguments.Count);
                bool argsChanged = false;
                foreach (Expression arg in gmc.Arguments)
                {
                    var (h, lowered) = LowerExpr(arg);
                    hoisted.AddRange(h);
                    args.Add(lowered);
                    if (!ReferenceEquals(lowered, arg)) argsChanged = true;
                }

                if (hoisted.Count == 0 && !argsChanged
                    && ReferenceEquals(loweredObj, gmc.Object))
                    return ([], expr);
                return (hoisted, gmc with { Object = loweredObj, Arguments = args });
            }

            case CompoundAssignmentExpression compound:
            {
                string? inPlaceName = compound.Operator.GetInPlaceMethodName();
                var (targetH, loweredTarget) = LowerExpr(compound.Target);
                var (valueH, loweredValue) = LowerExpr(compound.Value);
                var hoisted = new List<Statement>(capacity: targetH.Count + valueH.Count + 1);
                hoisted.AddRange(targetH);
                hoisted.AddRange(valueH);
                SourceLocation loc = compound.Location;
                // Try in-place method first ($iadd, $isub, etc.)
                if (inPlaceName != null && loweredTarget.ResolvedType != null &&
                    ctx.Registry.LookupMethod(type: loweredTarget.ResolvedType, methodName: inPlaceName) != null)
                {
                    var inPlaceCall = new CallExpression(
                        Callee: new MemberExpression(
                            Object: loweredTarget,
                            PropertyName: inPlaceName,
                            Location: loc),
                        Arguments: [new NamedArgumentExpression(Name: "you", Value: loweredValue, Location: loc)],
                        Location: loc) { ResolvedType = compound.ResolvedType };
                    return (hoisted, inPlaceCall);
                }
                // Fallback: hoist x = x OP y; return x
                var binExpr = new BinaryExpression(
                    Left: loweredTarget,
                    Operator: compound.Operator,
                    Right: loweredValue,
                    Location: loc) { ResolvedType = compound.ResolvedType };
                hoisted.Add(new AssignmentStatement(Target: loweredTarget, Value: binExpr, Location: loc));
                return (hoisted, loweredTarget);
            }

            case StealExpression steal:
            {
                var (h, lowered) = LowerExpr(steal.Operand);
                if (h.Count == 0 && ReferenceEquals(lowered, steal.Operand)) return ([], expr);
                return (h, steal with { Operand = lowered });
            }

            case InsertedTextExpression ftext:
            {
                var hoisted = new List<Statement>();
                var parts = new List<InsertedTextPart>(capacity: ftext.Parts.Count);
                bool changed = false;
                foreach (InsertedTextPart part in ftext.Parts)
                {
                    if (part is ExpressionPart ep)
                    {
                        var (h, lowered) = LowerExpr(ep.Expression);
                        hoisted.AddRange(h);
                        parts.Add(ep with { Expression = lowered });
                        if (!ReferenceEquals(lowered, ep.Expression)) changed = true;
                    }
                    else
                    {
                        parts.Add(part);
                    }
                }

                if (!changed && hoisted.Count == 0) return ([], expr);
                return (hoisted, ftext with { Parts = parts });
            }

            case SliceExpression slice:
            {
                var (startH, loweredStart) = LowerExpr(slice.Start);
                var (endH, loweredEnd) = LowerExpr(slice.End);
                var hoisted = Concat(startH, endH);
                if (hoisted.Count == 0
                    && ReferenceEquals(loweredStart, slice.Start)
                    && ReferenceEquals(loweredEnd, slice.End))
                    return ([], expr);
                return (hoisted, slice with { Start = loweredStart, End = loweredEnd });
            }

            case ConditionalExpression cond:
            {
                // D-AST-6: hoist to var _cif_N: T; if cond { _cif_N = a } else { _cif_N = b }
                // Skip hoisting if the result type is unknown (e.g., unanalyzed stdlib bodies).
                // Without a type we cannot emit a VarDeclarationStatement, and the generated
                // IfStatement assigning to the temp would crash codegen.
                if (cond.ResolvedType == null)
                    return ([], expr);

                var (condH, loweredCond) = LowerExpr(cond.Condition);
                var (trueH, loweredTrue) = LowerExpr(cond.TrueExpression);
                var (falseH, loweredFalse) = LowerExpr(cond.FalseExpression);

                TypeInfo? resultType = cond.ResolvedType;
                string tempName = NextTempName("cif");
                SourceLocation loc = cond.Location;

                var hoisted = new List<Statement>(capacity: condH.Count + 2);
                hoisted.AddRange(condH);
                AddTempVarUninit(hoisted, tempName, resultType, loc);

                Expression tempRef = MakeRef(tempName, resultType, loc);

                Statement thenBody = trueH.Count > 0
                    ? new BlockStatement(
                        Statements: [..trueH,
                            new AssignmentStatement(Target: tempRef, Value: loweredTrue,
                                Location: loc)],
                        Location: loc)
                    : new AssignmentStatement(Target: tempRef, Value: loweredTrue, Location: loc);

                Statement elseBody = falseH.Count > 0
                    ? new BlockStatement(
                        Statements: [..falseH,
                            new AssignmentStatement(Target: tempRef, Value: loweredFalse,
                                Location: loc)],
                        Location: loc)
                    : new AssignmentStatement(Target: tempRef, Value: loweredFalse, Location: loc);

                hoisted.Add(new IfStatement(
                    Condition: loweredCond,
                    ThenStatement: thenBody,
                    ElseStatement: elseBody,
                    Location: loc));

                return (hoisted, tempRef);
            }

            case TupleLiteralExpression tuple:
            {
                var hoisted = new List<Statement>();
                var elems = new List<Expression>(capacity: tuple.Elements.Count);
                bool changed = false;
                foreach (Expression el in tuple.Elements)
                {
                    var (h, lowered) = LowerExpr(el);
                    hoisted.AddRange(h);
                    elems.Add(lowered);
                    if (!ReferenceEquals(lowered, el)) changed = true;
                }

                if (!changed && hoisted.Count == 0) return ([], expr);
                return (hoisted, tuple with { Elements = elems });
            }

            case ListLiteralExpression list:
            {
                var hoisted = new List<Statement>();
                var elems = new List<Expression>(capacity: list.Elements.Count);
                bool changed = false;
                foreach (Expression el in list.Elements)
                {
                    var (h, lowered) = LowerExpr(el);
                    hoisted.AddRange(h);
                    elems.Add(lowered);
                    if (!ReferenceEquals(lowered, el)) changed = true;
                }

                if (!changed && hoisted.Count == 0) return ([], expr);
                return (hoisted, list with { Elements = elems });
            }

            case FlagsTestExpression flagsTest:
            {
                var (subjH, loweredSubj) = LowerExpr(flagsTest.Subject);
                SourceLocation loc = flagsTest.Location;
                TypeInfo? u64Type = ctx.Registry.LookupType(name: "U64");
                TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");
                if (loweredSubj.ResolvedType is not FlagsTypeInfo flagsType
                    || u64Type == null || boolType == null)
                    return (subjH, flagsTest with { Subject = loweredSubj });

                ulong testMask = 0;
                foreach (string flagName in flagsTest.TestFlags)
                {
                    FlagsMemberInfo? m = flagsType.Members.FirstOrDefault(x => x.Name == flagName);
                    if (m != null) testMask |= 1UL << m.BitPosition;
                }
                ulong excludedMask = 0;
                if (flagsTest.ExcludedFlags != null)
                {
                    foreach (string flagName in flagsTest.ExcludedFlags)
                    {
                        FlagsMemberInfo? m = flagsType.Members.FirstOrDefault(x => x.Name == flagName);
                        if (m != null) excludedMask |= 1UL << m.BitPosition;
                    }
                }

                var maskLit = new LiteralExpression(
                    Value: testMask, LiteralType: TokenType.U64Literal, Location: loc)
                    { ResolvedType = u64Type };
                var zeroLit = new LiteralExpression(
                    Value: 0UL, LiteralType: TokenType.U64Literal, Location: loc)
                    { ResolvedType = u64Type };

                Expression bitResult = flagsTest.Kind switch
                {
                    FlagsTestKind.Is when flagsTest.Connective == FlagsTestConnective.And =>
                        new BinaryExpression(
                            Left: new BinaryExpression(
                                Left: loweredSubj, Operator: BinaryOperator.BitwiseAnd,
                                Right: maskLit, Location: loc) { ResolvedType = u64Type },
                            Operator: BinaryOperator.Equal, Right: maskLit, Location: loc)
                            { ResolvedType = boolType },
                    FlagsTestKind.Is =>
                        new BinaryExpression(
                            Left: new BinaryExpression(
                                Left: loweredSubj, Operator: BinaryOperator.BitwiseAnd,
                                Right: maskLit, Location: loc) { ResolvedType = u64Type },
                            Operator: BinaryOperator.NotEqual, Right: zeroLit, Location: loc)
                            { ResolvedType = boolType },
                    FlagsTestKind.IsNot =>
                        new BinaryExpression(
                            Left: new BinaryExpression(
                                Left: loweredSubj, Operator: BinaryOperator.BitwiseAnd,
                                Right: maskLit, Location: loc) { ResolvedType = u64Type },
                            Operator: BinaryOperator.NotEqual, Right: maskLit, Location: loc)
                            { ResolvedType = boolType },
                    FlagsTestKind.IsOnly =>
                        new BinaryExpression(
                            Left: loweredSubj, Operator: BinaryOperator.Equal,
                            Right: maskLit, Location: loc) { ResolvedType = boolType },
                    _ =>
                        new BinaryExpression(
                            Left: new BinaryExpression(
                                Left: loweredSubj, Operator: BinaryOperator.BitwiseAnd,
                                Right: maskLit, Location: loc) { ResolvedType = u64Type },
                            Operator: BinaryOperator.Equal, Right: maskLit, Location: loc)
                            { ResolvedType = boolType }
                };

                if (excludedMask > 0)
                {
                    var excLit = new LiteralExpression(
                        Value: excludedMask, LiteralType: TokenType.U64Literal, Location: loc)
                        { ResolvedType = u64Type };
                    var excCheck = new BinaryExpression(
                        Left: new BinaryExpression(
                            Left: loweredSubj, Operator: BinaryOperator.BitwiseAnd,
                            Right: excLit, Location: loc) { ResolvedType = u64Type },
                        Operator: BinaryOperator.Equal, Right: zeroLit, Location: loc)
                        { ResolvedType = boolType };
                    bitResult = new BinaryExpression(
                        Left: bitResult, Operator: BinaryOperator.And,
                        Right: excCheck, Location: loc) { ResolvedType = boolType };
                }

                return (subjH, bitResult);
            }

            case RangeExpression range:
            {
                var (startH, loweredStart) = LowerExpr(range.Start);
                var (endH, loweredEnd) = LowerExpr(range.End);
                var hoisted = Concat(startH, endH);
                SourceLocation loc = range.Location;
                TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");
                TypeInfo? elemType = loweredStart.ResolvedType ?? loweredEnd.ResolvedType;

                Expression stepExpr;
                if (range.Step != null)
                {
                    var (stepH, loweredStep) = LowerExpr(range.Step);
                    hoisted = Concat(hoisted, stepH);
                    stepExpr = loweredStep;
                }
                else
                {
                    stepExpr = new LiteralExpression(
                        Value: 1L, LiteralType: TokenType.S64Literal, Location: loc)
                        { ResolvedType = elemType };
                }

                var inclusiveLit = new LiteralExpression(
                    Value: !range.IsExclusive,
                    LiteralType: !range.IsExclusive ? TokenType.True : TokenType.False,
                    Location: loc) { ResolvedType = boolType };

                // Build TypeArguments from resolved element type so EmitConstructorCall
                // uses the concrete Range[T] definition instead of the generic definition.
                // Prefer the type arg from the resolved Range[T] type, then fall back to
                // the inferred element type from the start/end sub-expressions.
                TypeInfo? resolvedElem = range.ResolvedType?.TypeArguments is { Count: > 0 }
                    ? range.ResolvedType.TypeArguments[0]
                    : elemType;

                // If we still have no element type (e.g. stdlib bodies without SA annotation),
                // leave the RangeExpression as-is so EmitRange in codegen handles it — that path
                // infers the element type from the LLVM literal type (see GetLiteralType).
                if (resolvedElem == null)
                    return (hoisted, range with
                    {
                        Start = loweredStart,
                        End = loweredEnd,
                        Step = range.Step != null ? stepExpr : null
                    });

                List<TypeExpression> typeArgs = [TypeInfoToExpr(type: resolvedElem, loc: loc)];

                return (hoisted, new CreatorExpression(
                    TypeName: "Range",
                    TypeArguments: typeArgs,
                    MemberVariables: [
                        ("start", loweredStart),
                        ("end", loweredEnd),
                        ("step", stepExpr),
                        ("inclusive", inclusiveLit)
                    ],
                    Location: loc) { ResolvedType = range.ResolvedType });
            }

            case WhenExpression whenExpr:
            {
                // D-AST-10: hoist when-expression to var _wres_N: T; WhenStatement; replace with _wres_N.
                // Skip hoisting if the result type is unknown (e.g., unanalyzed stdlib bodies).
                if (whenExpr.ResolvedType == null)
                    return ([], expr);

                TypeInfo? resultType = whenExpr.ResolvedType;
                string tempName = NextTempName("wres");
                SourceLocation loc = whenExpr.Location;

                var hoisted = new List<Statement>();

                // Lower the subject expression if present.
                Expression? loweredSubject = null;
                if (whenExpr.Expression != null)
                {
                    var (subjH, ls) = LowerExpr(whenExpr.Expression);
                    hoisted.AddRange(subjH);
                    loweredSubject = ls;
                }

                // Declare result temp.
                AddTempVarUninit(hoisted, tempName, resultType, loc);
                Expression tempRef = MakeRef(tempName, resultType, loc);

                // Build new clauses: body of each clause becomes body + assignment to _wres_N.
                var clauses = new List<WhenClause>(capacity: whenExpr.Clauses.Count);
                foreach (WhenClause c in whenExpr.Clauses)
                {
                    // The clause body is an expression — wrap in ExpressionStatement or
                    // AssignmentStatement. If the body is a BlockExpression, extract its last
                    // expression as the value; otherwise treat the clause body directly.
                    Statement clauseBody;
                    if (c.Body is ExpressionStatement { Expression: var clauseExpr })
                    {
                        var (h, loweredClauseExpr) = LowerExpr(clauseExpr);
                        Statement assignment = new AssignmentStatement(
                            Target: tempRef, Value: loweredClauseExpr, Location: loc);
                        clauseBody = h.Count > 0
                            ? new BlockStatement(
                                Statements: [..h, assignment],
                                Location: loc)
                            : assignment;
                    }
                    else
                    {
                        // Body is already a statement; run LowerStatementFull on it.
                        clauseBody = LowerStatementFull(c.Body);
                    }
                    clauses.Add(c with { Body = clauseBody });
                }

                hoisted.Add(new WhenStatement(
                    Expression: loweredSubject ?? whenExpr.Expression!,
                    Clauses: clauses,
                    Location: loc));

                return (hoisted, tempRef);
            }

            default:
                // LiteralExpression, IdentifierExpression, TypeExpression, LambdaExpression,
                // DictLiteralExpression, SetLiteralExpression, BlockExpression,
                // TypeConversionExpression, GenericMemberExpression, DictEntryLiteralExpression:
                // no sub-expressions that need lowering.
                return ([], expr);
        }
    }

    // ─── Specific hoisting lowerings ─────────────────────────────────────────────

    /// <summary>
    /// 1e. Lowers flags combination operators to plain bitwise operations:
    /// <list type="bullet">
    ///   <item><c>a and b</c> (union of active bits)      → <c>BitwiseOr(a, b)</c></item>
    ///   <item><c>a but b</c> (bit clear: a &amp; ~b)     → <c>BitwiseAnd(a, BitwiseNot(b))</c></item>
    /// </list>
    /// Codegen emits <c>or i64</c> / <c>and i64 … xor i64 …, -1</c> for these via
    /// <c>EmitPrimitiveBinaryOp</c>, making <c>EmitFlagsCombine</c> / <c>EmitBitClear</c> dead.
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerFlagsCombination(BinaryExpression binary)
    {
        var (leftH, loweredLeft) = LowerExpr(binary.Left);
        var (rightH, loweredRight) = LowerExpr(binary.Right);
        var hoisted = Concat(leftH, rightH);

        FlagsTypeInfo flagsType = (FlagsTypeInfo)binary.Left.ResolvedType!;

        Expression lowered;
        if (binary.Operator == BinaryOperator.And)
        {
            // flags and flags → bitwise OR (union of active bits)
            lowered = new BinaryExpression(
                Left: loweredLeft,
                Operator: BinaryOperator.BitwiseOr,
                Right: loweredRight,
                Location: binary.Location) { ResolvedType = flagsType };
        }
        else
        {
            // flags but flags → bitwise AND with NOT of right (bit clear)
            var notRight = new UnaryExpression(
                Operator: UnaryOperator.BitwiseNot,
                Operand: loweredRight,
                Location: binary.Location) { ResolvedType = flagsType };
            lowered = new BinaryExpression(
                Left: loweredLeft,
                Operator: BinaryOperator.BitwiseAnd,
                Right: notRight,
                Location: binary.Location) { ResolvedType = flagsType };
        }

        return (hoisted, lowered);
    }

    /// <summary>
    /// 1f. Lowers <c>x is None</c> / <c>x is Blank</c> / their negated forms for carriers:
    /// <list type="bullet">
    ///   <item><c>Maybe[T record] is None</c>  →  <c>not x.present</c></item>
    ///   <item><c>Maybe[T record] isnot None</c>  →  <c>x.present</c></item>
    ///   <item><c>Lookup[T] is Blank</c>  →  <c>x.type_id == 0_u64</c></item>
    ///   <item><c>Lookup[T] isnot Blank</c>  →  <c>x.type_id != 0_u64</c></item>
    /// </list>
    /// <c>Maybe[T entity]</c> absence checks are NOT lowered here (require Snatched null compare);
    /// they fall through unchanged for <c>EmitIsPattern</c> in codegen.
    /// </summary>
    /// <summary>
    /// Lowers <c>base with .field1 = v1, .field2 = v2</c> into a record constructor call
    /// <c>RecordType(field1: v1, field2: base.field1_copy, ...)</c>.
    /// Only handles simple (non-nested, non-index) updates on RecordTypeInfo.
    /// Falls through unchanged for unsupported shapes.
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerWithExpression(WithExpression withExpr)
    {
        var (baseHoisted, loweredBase) = LowerExpr(withExpr.Base);
        SourceLocation loc = withExpr.Location;

        TypeInfo? baseType = withExpr.Base.ResolvedType;
        if (baseType is not RecordTypeInfo recordType)
        {
            // Not a record — pass through unchanged.
            if (ReferenceEquals(loweredBase, withExpr.Base) && baseHoisted.Count == 0)
                return ([], withExpr);
            return (baseHoisted, withExpr with { Base = loweredBase });
        }

        // Hoist base to a temp if it isn't a trivial identifier (avoid double-eval).
        var hoisted = new List<Statement>(baseHoisted);
        Expression baseRef = loweredBase;
        if (loweredBase is not IdentifierExpression)
        {
            string tempName = NextTempName(prefix: "with_base");
            AddTempVar(hoisted: hoisted, name: tempName, typeHint: baseType,
                initializer: loweredBase, loc: loc);
            baseRef = new IdentifierExpression(Name: tempName, Location: loc)
                { ResolvedType = baseType };
        }

        // Build a dictionary of simple (single-segment path) overrides.
        var overrides = new Dictionary<string, Expression>(StringComparer.Ordinal);
        bool allSimple = true;
        foreach ((List<string>? path, Expression? idx, Expression value) in withExpr.Updates)
        {
            if (path is [string singleField] && idx == null)
            {
                var (valH, loweredVal) = LowerExpr(value);
                hoisted.AddRange(valH);
                overrides[singleField] = loweredVal;
            }
            else
            {
                allSimple = false;
                break;
            }
        }

        if (!allSimple)
        {
            // Nested paths or index updates — not yet lowered; pass through.
            return (hoisted, withExpr with { Base = baseRef });
        }

        // Build CreatorExpression with all record fields, overrides take priority.
        var members = new List<(string Name, Expression Value)>(
            capacity: recordType.MemberVariables.Count);
        foreach (MemberVariableInfo field in recordType.MemberVariables)
        {
            if (overrides.TryGetValue(key: field.Name, value: out Expression? overrideVal))
            {
                members.Add((field.Name, overrideVal));
            }
            else
            {
                var fieldAccess = new MemberExpression(
                    Object: baseRef, PropertyName: field.Name, Location: loc)
                    { ResolvedType = field.Type };
                members.Add((field.Name, fieldAccess));
            }
        }

        // Determine type name and type arguments for the creator.
        string typeName = recordType.GenericDefinition?.Name ?? recordType.Name;
        List<TypeExpression>? typeArgs = null;
        if (recordType.GenericDefinition != null)
        {
            // Build type arguments from the concrete substitutions.
            var genericDef = recordType.GenericDefinition;
            typeArgs = [];
            foreach (string paramName in genericDef.GenericParameters)
            {
                TypeInfo? argType = recordType.MemberVariables
                    .Select(f => f.Type)
                    .FirstOrDefault(t => t?.Name != paramName);
                // Fallback: just emit the concrete record type as-is
                typeArgs = null;
                break;
            }
        }

        var creator = new CreatorExpression(
            TypeName: typeName,
            TypeArguments: typeArgs,
            MemberVariables: members,
            Location: loc)
            { ResolvedType = baseType };

        return (hoisted, creator);
    }

    private (List<Statement> Hoisted, Expression Expr) LowerIsPatternExpression(
        IsPatternExpression ipe)
    {
        TypeInfo? operandType = ipe.Expression.ResolvedType;
        bool isNoneCheck = ipe.Pattern is NonePattern or TypePattern { Type.Name: "None" };
        bool isBlankCheck = ipe.Pattern is TypePattern { Type.Name: "Blank" };

        // Lower the operand expression first.
        var (hoisted, loweredExpr) = LowerExpr(ipe.Expression);

        TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");
        TypeInfo? u64Type  = ctx.Registry.LookupType(name: "U64");

        // Maybe[T record]: x is None → not x.present; x isnot None → x.present
        if (isNoneCheck && IsMaybeRecord(operandType))
        {
            var presentAccess = new MemberExpression(
                Object: loweredExpr, PropertyName: "present", Location: ipe.Location)
            {
                ResolvedType = boolType
            };
            Expression result = ipe.IsNegated
                ? presentAccess
                : new UnaryExpression(
                    Operator: UnaryOperator.Not,
                    Operand: presentAccess,
                    Location: ipe.Location) { ResolvedType = boolType };
            return (hoisted, result);
        }

        // Result/Lookup: x is Blank → x.type_id == 0_u64; x isnot Blank → x.type_id != 0_u64
        if (isBlankCheck && IsResultOrLookup(operandType))
        {
            var typeIdAccess = new MemberExpression(
                Object: loweredExpr, PropertyName: "type_id", Location: ipe.Location)
            {
                ResolvedType = u64Type
            };
            var zero = new LiteralExpression(
                Value: 0UL,
                LiteralType: Compiler.Lexer.TokenType.U64Literal,
                Location: ipe.Location) { ResolvedType = u64Type };
            Expression cmp = new BinaryExpression(
                Left: typeIdAccess,
                Operator: ipe.IsNegated ? BinaryOperator.NotEqual : BinaryOperator.Equal,
                Right: zero,
                Location: ipe.Location) { ResolvedType = boolType };
            return (hoisted, cmp);
        }

        // D-AST-11: user VariantTypeInfo — x is T → x.type_id == FNV-1a(T.FullName)
        if (ipe.Pattern is TypePattern { } tp && operandType is VariantTypeInfo)
        {
            TypeInfo? targetType = tp.Type.ResolvedType
                ?? ctx.Registry.LookupType(name: tp.Type.Name);
            // Blank: type_id == 0
            if (tp.Type.Name == "Blank" || targetType?.Name == "Blank")
            {
                var typeIdAccess = new MemberExpression(
                    Object: loweredExpr, PropertyName: "type_id", Location: ipe.Location)
                { ResolvedType = u64Type };
                var zero = new LiteralExpression(
                    Value: 0UL,
                    LiteralType: TokenType.U64Literal,
                    Location: ipe.Location) { ResolvedType = u64Type };
                Expression cmp0 = new BinaryExpression(
                    Left: typeIdAccess,
                    Operator: ipe.IsNegated ? BinaryOperator.NotEqual : BinaryOperator.Equal,
                    Right: zero,
                    Location: ipe.Location) { ResolvedType = boolType };
                return (hoisted, cmp0);
            }

            // Specific member type: type_id == FNV-1a(fullName)
            if (targetType != null)
            {
                ulong typeId = TypeIdHelper.ComputeTypeId(fullName: targetType.FullName);
                var typeIdAccess = new MemberExpression(
                    Object: loweredExpr, PropertyName: "type_id", Location: ipe.Location)
                { ResolvedType = u64Type };
                var constant = new LiteralExpression(
                    Value: typeId,
                    LiteralType: TokenType.U64Literal,
                    Location: ipe.Location) { ResolvedType = u64Type };
                BinaryOperator op = ipe.IsNegated ? BinaryOperator.NotEqual : BinaryOperator.Equal;
                Expression cmpT = new BinaryExpression(
                    Left: typeIdAccess,
                    Operator: op,
                    Right: constant,
                    Location: ipe.Location) { ResolvedType = boolType };
                return (hoisted, cmpT);
            }
        }

        // Flags type: `p is FLAG` → `(p & mask) != 0`; `p isnot FLAG` → `(p & mask) == 0`
        if (ipe.Pattern is TypePattern flagsTp && operandType is FlagsTypeInfo flagsType2)
        {
            TypeInfo? u64Type2 = ctx.Registry.LookupType(name: "U64");
            TypeInfo? boolType2 = ctx.Registry.LookupType(name: "Bool");
            if (u64Type2 != null && boolType2 != null)
            {
                FlagsMemberInfo? member = flagsType2.Members.FirstOrDefault(
                    m => m.Name == flagsTp.Type.Name);
                ulong mask = member != null ? 1UL << member.BitPosition : 0UL;
                var maskLit2 = new LiteralExpression(
                    Value: mask, LiteralType: TokenType.U64Literal, Location: ipe.Location)
                    { ResolvedType = u64Type2 };
                var zeroLit2 = new LiteralExpression(
                    Value: 0UL, LiteralType: TokenType.U64Literal, Location: ipe.Location)
                    { ResolvedType = u64Type2 };
                Expression bitAnd = new BinaryExpression(
                    Left: loweredExpr, Operator: BinaryOperator.BitwiseAnd,
                    Right: maskLit2, Location: ipe.Location) { ResolvedType = u64Type2 };
                Expression cmpFlags = new BinaryExpression(
                    Left: bitAnd,
                    Operator: ipe.IsNegated ? BinaryOperator.Equal : BinaryOperator.NotEqual,
                    Right: zeroLit2,
                    Location: ipe.Location) { ResolvedType = boolType2 };
                return (hoisted, cmpFlags);
            }
        }

        // Not lowerable (Maybe[T entity] or other): pass through, but recurse operand.
        if (ReferenceEquals(loweredExpr, ipe.Expression) && hoisted.Count == 0)
            return ([], ipe);
        return (hoisted, ipe with { Expression = loweredExpr });
    }

    /// <summary>
    /// 1a. Lowers a chained comparison <c>a &lt; b &lt; c</c> to
    /// <c>(a &lt; b) and (b &lt; c)</c>, hoisting complex middle operands.
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerChainedComparison(
        ChainedComparisonExpression chain)
    {
        SourceLocation loc = chain.Location;
        var hoisted = new List<Statement>();

        // Lower all operands, accumulating any of their own hoisted stmts.
        var operands = new List<Expression>(capacity: chain.Operands.Count);
        foreach (Expression op in chain.Operands)
        {
            var (h, lowered) = LowerExpr(op);
            hoisted.AddRange(h);
            operands.Add(lowered);
        }

        // Hoist middle operands (index 1 … n-2) that are not trivially pure,
        // to prevent double-evaluation.
        for (int i = 1; i < operands.Count - 1; i++)
        {
            Expression mid = operands[i];
            if (mid is IdentifierExpression or LiteralExpression) continue;

            string tempName = NextTempName("cmp_mid");
            TypeInfo? midType = mid.ResolvedType;

            var varDecl = new VariableDeclaration(
                Name: tempName,
                Type: midType != null ? TypeInfoToExpr(midType, loc) : null,
                Initializer: mid,
                Visibility: VisibilityModifier.Secret,
                Location: loc);
            hoisted.Add(new DeclarationStatement(Declaration: varDecl, Location: loc));

            var tempRef = new IdentifierExpression(Name: tempName, Location: loc)
            {
                ResolvedType = midType
            };
            operands[i] = tempRef;
        }

        // Build pairwise comparisons, chained with 'and'.
        TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");

        Expression result = new BinaryExpression(
            Left: operands[0],
            Operator: chain.Operators[0],
            Right: operands[1],
            Location: loc)
        {
            ResolvedType = boolType
        };

        for (int i = 1; i < chain.Operators.Count; i++)
        {
            Expression pairCmp = new BinaryExpression(
                Left: operands[i],
                Operator: chain.Operators[i],
                Right: operands[i + 1],
                Location: loc)
            {
                ResolvedType = boolType
            };

            result = new BinaryExpression(
                Left: result,
                Operator: BinaryOperator.And,
                Right: pairCmp,
                Location: loc)
            {
                ResolvedType = boolType
            };
        }

        return (hoisted, result);
    }

    /// <summary>
    /// 1b. Lowers <c>a ?? b</c> to:
    /// <code>
    ///   var _car_N = a
    ///   var _qq_N: T
    ///   when _car_N
    ///     is None/Blank → _qq_N = b
    ///     else v        → _qq_N = v
    ///   // replacement: _qq_N
    /// </code>
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerNoneCoalesce(BinaryExpression binary)
    {
        SourceLocation loc = binary.Location;
        TypeInfo? carrierType = binary.Left.ResolvedType;
        TypeInfo? valueType = binary.ResolvedType; // T = the inner type

        // Skip hoisting if types are unknown (e.g., unanalyzed stdlib bodies).
        if (carrierType == null || valueType == null)
            return ([], binary);

        string carName = NextTempName("car");
        string qqName = NextTempName("qq");
        string valName = NextTempName("val");

        var hoisted = new List<Statement>();

        // Lower both sides first, collecting any of their own hoisted stmts.
        var (leftH, loweredLeft) = LowerExpr(binary.Left);
        var (rightH, loweredRight) = LowerExpr(binary.Right);
        hoisted.AddRange(leftH);

        // var _car_N = a
        AddTempVar(hoisted, carName, carrierType, loweredLeft, loc);

        // var _qq_N: T  (uninitialized; type annotation gives codegen the LLVM type)
        AddTempVarUninit(hoisted, qqName, valueType, loc);

        Expression carRef = MakeRef(carName, carrierType, loc);
        Expression qqRef = MakeRef(qqName, valueType, loc);
        Expression valRef = MakeRef(valName, valueType, loc);

        // None/Blank clause: prepend any hoisting from the right operand, then assign.
        var noneBody = new List<Statement>(capacity: rightH.Count + 1);
        noneBody.AddRange(rightH);
        noneBody.Add(new AssignmentStatement(Target: qqRef, Value: loweredRight, Location: loc));

        var whenStmt = new WhenStatement(
            Expression: carRef,
            Clauses:
            [
                new WhenClause(
                    Pattern: MakeAbsencePattern(carrierType, loc),
                    Body: new BlockStatement(Statements: noneBody, Location: loc),
                    Location: loc),
                new WhenClause(
                    Pattern: new ElsePattern(VariableName: valName, Location: loc),
                    Body: new AssignmentStatement(Target: qqRef, Value: valRef, Location: loc),
                    Location: loc)
            ],
            Location: loc);
        hoisted.Add(whenStmt);

        return (hoisted, MakeRef(qqName, valueType, loc));
    }

    /// <summary>
    /// 1d. Lowers <c>a?.prop</c> to:
    /// <code>
    ///   var _car_N = a
    ///   var _om_N: Maybe[PropType]
    ///   when _car_N
    ///     is None/Blank → _om_N = None   (zeroinitializer via IdentifierExpression("None"))
    ///     else v        → _om_N = v.prop  (auto-wrapped if needed by codegen)
    ///   // replacement: _om_N
    /// </code>
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerOptionalMember(
        OptionalMemberExpression optMember)
    {
        SourceLocation loc = optMember.Location;
        TypeInfo? carrierType = optMember.Object.ResolvedType;
        TypeInfo? resultType = optMember.ResolvedType; // Maybe[PropType]

        // Skip hoisting if types are unknown (e.g., unanalyzed stdlib bodies).
        if (carrierType == null || resultType == null)
            return ([], optMember);

        string carName = NextTempName("car");
        string omName = NextTempName("om");
        string valName = NextTempName("val");

        var hoisted = new List<Statement>();

        var (objH, loweredObj) = LowerExpr(optMember.Object);
        hoisted.AddRange(objH);

        AddTempVar(hoisted, carName, carrierType, loweredObj, loc);
        AddTempVarUninit(hoisted, omName, resultType, loc);

        Expression carRef = MakeRef(carName, carrierType, loc);
        Expression omRef = MakeRef(omName, resultType, loc);

        // Inner type for member access
        TypeInfo? innerType = carrierType?.TypeArguments?[0];
        Expression valRef = MakeRef(valName, innerType, loc);

        // val.prop
        TypeInfo? propType = resultType?.TypeArguments?[0];
        var memberAccess = new MemberExpression(
            Object: valRef,
            PropertyName: optMember.PropertyName,
            Location: loc)
        {
            ResolvedType = propType
        };

        // None literal (absent Maybe) — codegen treats "None" identifier as zeroinitializer
        var noneLiteral = new IdentifierExpression(Name: "None", Location: loc)
        {
            ResolvedType = resultType
        };

        var whenStmt = new WhenStatement(
            Expression: carRef,
            Clauses:
            [
                new WhenClause(
                    Pattern: MakeAbsencePattern(carrierType, loc),
                    Body: new AssignmentStatement(
                        Target: omRef, Value: noneLiteral, Location: loc),
                    Location: loc),
                new WhenClause(
                    Pattern: new ElsePattern(VariableName: valName, Location: loc),
                    Body: new AssignmentStatement(
                        Target: omRef, Value: memberAccess, Location: loc),
                    Location: loc)
            ],
            Location: loc);
        hoisted.Add(whenStmt);

        return (hoisted, MakeRef(omName, resultType, loc));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static List<Statement> Concat(List<Statement> a, List<Statement> b)
    {
        if (a.Count == 0) return b;
        if (b.Count == 0) return a;
        var result = new List<Statement>(capacity: a.Count + b.Count);
        result.AddRange(a);
        result.AddRange(b);
        return result;
    }

    /// <summary>Adds <c>var name = initializer</c> to <paramref name="hoisted"/>.</summary>
    private static void AddTempVar(
        List<Statement> hoisted, string name, TypeInfo? typeHint,
        Expression initializer, SourceLocation loc)
    {
        var decl = new VariableDeclaration(
            Name: name,
            Type: typeHint != null ? TypeInfoToExpr(typeHint, loc) : null,
            Initializer: initializer,
            Visibility: VisibilityModifier.Secret,
            Location: loc);
        hoisted.Add(new DeclarationStatement(Declaration: decl, Location: loc));
    }

    /// <summary>Adds <c>var name: T</c> (no initializer) to <paramref name="hoisted"/>.</summary>
    private static void AddTempVarUninit(
        List<Statement> hoisted, string name, TypeInfo? typeHint, SourceLocation loc)
    {
        if (typeHint == null) return; // can't emit without a type; leave to codegen fallback
        var decl = new VariableDeclaration(
            Name: name,
            Type: TypeInfoToExpr(typeHint, loc),
            Initializer: null,
            Visibility: VisibilityModifier.Secret,
            Location: loc);
        hoisted.Add(new DeclarationStatement(Declaration: decl, Location: loc));
    }

    /// <summary>
    /// Creates an <see cref="IdentifierExpression"/> for a synthetic temp variable.
    /// </summary>
    private static Expression MakeRef(string name, TypeInfo? resolvedType, SourceLocation loc)
    {
        return new IdentifierExpression(Name: name, Location: loc)
        {
            ResolvedType = resolvedType
        };
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> is <c>Maybe[T]</c> where T is a record/value type
    /// (the two-field variant with <c>present</c> and <c>value</c> fields).
    /// </summary>
    private static bool IsMaybeRecord(TypeInfo? type)
    {
        if (type == null) return false;
        string baseName = type switch
        {
            RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition.Name,
            _ => type.Name
        };
        if (baseName != "Maybe") return false;
        if (type.TypeArguments is not { Count: > 0 }) return false;
        return type.TypeArguments[0] is not EntityTypeInfo;
    }

    /// <summary>Returns true if the type is <c>Result[T]</c> or <c>Lookup[T]</c>.</summary>
    private static bool IsResultOrLookup(TypeInfo? type)
    {
        if (type == null) return false;
        string baseName = type switch
        {
            RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition.Name,
            _ => type.Name
        };
        return baseName is "Result" or "Lookup";
    }

    /// <summary>
    /// Returns the appropriate absence pattern for the carrier:
    /// <c>NonePattern</c> for Maybe[T], <c>TypePattern("Blank")</c> for Result/Lookup.
    /// </summary>
    private static Pattern MakeAbsencePattern(TypeInfo? carrierType, SourceLocation loc)
    {
        // Maybe is identified by name prefix "Maybe"
        string? baseName = carrierType switch
        {
            RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition.Name,
            _ => carrierType?.Name
        };

        if (baseName == "Maybe")
            return new NonePattern(Location: loc);

        // Result, Lookup, or unknown — use Blank type pattern
        return new TypePattern(
            Type: new TypeExpression(Name: "Blank", GenericArguments: null, Location: loc),
            VariableName: null,
            Bindings: null,
            Location: loc);
    }

    /// <summary>
    /// Converts a <see cref="TypeInfo"/> to a <see cref="TypeExpression"/> suitable for
    /// use as a variable type annotation in a synthetic <see cref="VariableDeclaration"/>.
    /// </summary>
    private static TypeExpression TypeInfoToExpr(TypeInfo type, SourceLocation loc)
    {
        // For generic resolutions, use the base definition name (not the resolved "Maybe[S64]").
        string baseName = type switch
        {
            RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition.Name,
            EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition.Name,
            _ => type.IsGenericResolution && type.Name.Contains(value: '[')
                ? type.Name[..type.Name.IndexOf(value: '[')]
                : type.Name
        };

        List<TypeExpression>? args = type.TypeArguments is { Count: > 0 }
            ? type.TypeArguments.Select(selector: a => TypeInfoToExpr(a, loc)).ToList()
            : null;

        return new TypeExpression(Name: baseName, GenericArguments: args, Location: loc);
    }

    /// <summary>
    /// D-AST-7: Runs expression lowering on all synthesized variant bodies in
    /// <see cref="DesugaringContext.VariantBodies"/> so that hoisting transforms
    /// (conditional expressions, when expressions, etc.) are applied to variant bodies
    /// generated by <see cref="ErrorHandlingVariantPass"/>.
    /// </summary>
    public void RunOnVariantBodies()
    {
        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            Statement body = ctx.VariantBodies[key];
            Statement lowered = LowerStatementFull(stmt: body);
            if (!ReferenceEquals(lowered, body))
                ctx.VariantBodies[key] = lowered;
        }
    }
}
