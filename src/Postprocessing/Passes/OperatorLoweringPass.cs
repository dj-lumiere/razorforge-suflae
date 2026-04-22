using Compiler.Desugaring;
using TypeModel.Types;
using SyntaxTree;

using Compiler.Postprocessing;
namespace Compiler.Postprocessing.Passes;

/// <summary>
/// Lowers operator-sugar expressions to plain method call nodes.
/// Runs after <see cref="ExpressionLoweringPass"/> in the per-file pipeline.
///
/// <para>Transformations:</para>
/// <list type="bullet">
///   <item><see cref="IndexExpression"/> (<c>obj[i]</c>) ??
///         <c>obj.$getitem!(i)</c> ??failable method call.</item>
///   <item><see cref="SliceExpression"/> (<c>obj[a..b]</c>) ??
///         <c>obj.$getslice(from: a, to: b)</c>.</item>
///   <item><see cref="GenericMemberExpression"/> (<c>obj.field[i]</c>, parser quirk) ??
///         <c>MemberExpression(obj, field)</c> + <c>IndexExpression</c> ??<c>$getitem!</c>.</item>
///   <item><see cref="BinaryExpression"/> with an overloadable operator ??
///         <c>left.$method(you: right)</c>. Membership operators reverse operands:
///         <c>x in coll</c> ??<c>coll.$contains(x)</c>.</item>
///   <item><see cref="UnaryExpression"/> with <c>!!</c> (<see cref="UnaryOperator.ForceUnwrap"/>) ??
///         <c>operand.$unwrap()</c> ??always lowered, even in stdlib bodies (which bypass
///         <see cref="ExpressionLoweringPass"/>).</item>
///   <item><see cref="UnaryExpression"/> with a wired method (<c>-</c>, <c>~</c>) ??
///         <c>operand.$neg()</c> / <c>operand.$bitnot()</c> when the method is resolved.</item>
/// </list>
///
/// <para>Only the <em>value</em> side of <see cref="AssignmentStatement"/> is lowered.
/// Indexed-assignment targets (<c>arr[i] = val</c>) remain as <see cref="IndexExpression"/>
/// so codegen's <c>EmitAssignment</c> can dispatch to <c>$setitem!</c>.</para>
/// </summary>
internal sealed class OperatorLoweringPass(PostprocessingContext ctx)
{
    public void Run(Program program)
    {
        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[i])
            {
                case RoutineDeclaration r:
                {
                    Statement newBody = LowerStatement(r.Body);
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
            Statement newBody = LowerStatement(m.Body);
            if (!ReferenceEquals(newBody, m.Body))
                members[j] = m with { Body = newBody };
        }
    }

    // ?�?� Statement lowering ?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�

    private Statement LowerStatement(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement b:
            {
                List<Statement> stmts = LowerStatementList(b.Statements);
                return ReferenceEquals(stmts, b.Statements) ? stmt : b with { Statements = stmts };
            }

            case IfStatement ifs:
            {
                Expression cond = LowerExpression(ifs.Condition);
                Statement then = LowerStatement(ifs.ThenStatement);
                Statement? elseS = ifs.ElseStatement != null
                    ? LowerStatement(ifs.ElseStatement)
                    : null;
                bool changed = !ReferenceEquals(cond, ifs.Condition)
                               || !ReferenceEquals(then, ifs.ThenStatement)
                               || !ReferenceEquals(elseS, ifs.ElseStatement);
                return changed
                    ? ifs with { Condition = cond, ThenStatement = then, ElseStatement = elseS }
                    : stmt;
            }

            case WhileStatement w:
            {
                Expression cond = LowerExpression(w.Condition);
                Statement body = LowerStatement(w.Body);
                Statement? elseB = w.ElseBranch != null ? LowerStatement(w.ElseBranch) : null;
                bool changed = !ReferenceEquals(cond, w.Condition)
                               || !ReferenceEquals(body, w.Body)
                               || !ReferenceEquals(elseB, w.ElseBranch);
                return changed ? w with { Condition = cond, Body = body, ElseBranch = elseB } : stmt;
            }

            case LoopStatement loop:
            {
                Statement body = LowerStatement(loop.Body);
                return ReferenceEquals(body, loop.Body) ? stmt : loop with { Body = body };
            }

            case ForStatement f:
            {
                Expression iterable = LowerExpression(f.Iterable);
                Statement body = LowerStatement(f.Body);
                Statement? elseBranch = f.ElseBranch != null ? LowerStatement(f.ElseBranch) : null;
                bool changed = !ReferenceEquals(iterable, f.Iterable)
                               || !ReferenceEquals(body, f.Body)
                               || !ReferenceEquals(elseBranch, f.ElseBranch);
                return changed ? f with { Iterable = iterable, Body = body, ElseBranch = elseBranch } : stmt;
            }

            case WhenStatement w:
            {
                Expression subj = LowerExpression(w.Expression);
                var clauses = new List<WhenClause>(capacity: w.Clauses.Count);
                bool clauseChanged = false;
                foreach (WhenClause c in w.Clauses)
                {
                    Statement lBody = LowerStatement(c.Body);
                    // Also lower expression patterns (e.g. ChainedComparisonExpression guards)
                    Pattern lPattern = c.Pattern is ExpressionPattern ep
                        ? ep with { Expression = LowerExpression(ep.Expression) }
                        : c.Pattern;
                    bool patternChanged = !ReferenceEquals(lPattern, c.Pattern);
                    if (!ReferenceEquals(lBody, c.Body) || patternChanged)
                    {
                        clauses.Add(c with { Body = lBody, Pattern = lPattern });
                        clauseChanged = true;
                    }
                    else
                    {
                        clauses.Add(c);
                    }
                }

                bool changed = !ReferenceEquals(subj, w.Expression) || clauseChanged;
                return changed ? w with { Expression = subj, Clauses = clauses } : stmt;
            }

            case UsingStatement u:
            {
                Expression res = LowerExpression(u.Resource);
                Statement body = LowerStatement(u.Body);
                bool changed = !ReferenceEquals(res, u.Resource) || !ReferenceEquals(body, u.Body);
                return changed ? u with { Resource = res, Body = body } : stmt;
            }

            case DangerStatement d:
            {
                Statement body = LowerStatement(d.Body);
                return ReferenceEquals(body, d.Body)
                    ? stmt
                    : d with { Body = (BlockStatement)body };
            }

            case AssignmentStatement asgn:
            {
                // Only lower the value, not the target.
                // Indexed-assignment targets (arr[i] = val) stay as IndexExpression so
                // codegen's EmitAssignment can dispatch to $setitem!.
                Expression val = LowerExpression(asgn.Value);
                return ReferenceEquals(val, asgn.Value) ? stmt : asgn with { Value = val };
            }

            case DeclarationStatement { Declaration: VariableDeclaration vd } decl
                when vd.Initializer != null:
            {
                Expression init = LowerExpression(vd.Initializer);
                return ReferenceEquals(init, vd.Initializer)
                    ? stmt
                    : decl with { Declaration = vd with { Initializer = init } };
            }

            case ReturnStatement { Value: not null } ret:
            {
                Expression val = LowerExpression(ret.Value);
                return ReferenceEquals(val, ret.Value) ? stmt : ret with { Value = val };
            }

            case VariantReturnStatement { Value: not null } vrs:
            {
                Expression val = LowerExpression(vrs.Value);
                return ReferenceEquals(val, vrs.Value) ? stmt : vrs with { Value = val };
            }

            case ExpressionStatement es:
            {
                Expression expr = LowerExpression(es.Expression);
                return ReferenceEquals(expr, es.Expression) ? stmt : es with { Expression = expr };
            }

            case DiscardStatement ds:
            {
                Expression expr = LowerExpression(ds.Expression);
                return ReferenceEquals(expr, ds.Expression) ? stmt : ds with { Expression = expr };
            }

            case BecomesStatement bs:
            {
                Expression val = LowerExpression(bs.Value);
                return ReferenceEquals(val, bs.Value) ? stmt : bs with { Value = val };
            }

            case ThrowStatement t:
            {
                Expression err = LowerExpression(t.Error);
                return ReferenceEquals(err, t.Error) ? stmt : t with { Error = err };
            }

            default:
                return stmt;
        }
    }

    private List<Statement> LowerStatementList(List<Statement> stmts)
    {
        var result = new List<Statement>(capacity: stmts.Count);
        bool anyChanged = false;
        foreach (Statement stmt in stmts)
        {
            Statement lowered = LowerStatement(stmt);
            result.Add(lowered);
            if (!ReferenceEquals(lowered, stmt)) anyChanged = true;
        }

        return anyChanged ? result : stmts;
    }

    // ?�?� Expression lowering ?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�

    /// <summary>
    /// Lowers the INTERIOR of an assignment target while preserving the outermost
    /// node type so <c>EmitBinaryAssign</c> can still dispatch on it:
    /// <list type="bullet">
    ///   <item><c>MemberExpression(obj, prop)</c> ??lower <c>obj</c>; keep outer MemberExpression.</item>
    ///   <item><c>IndexExpression(coll, idx)</c>  ??lower <c>coll</c> and <c>idx</c>; keep outer IndexExpression.</item>
    ///   <item><c>IdentifierExpression</c>         ??unchanged.</item>
    /// </list>
    /// This is needed because assignment targets like <c>node!!.field = v</c> or
    /// <c>coll[expr!!] = v</c> have <c>!!</c> (ForceUnwrap) nested inside the target,
    /// which must be lowered to <c>$unwrap()</c> even though the outer shape must remain.
    /// </summary>
    private Expression LowerAssignTarget(Expression target)
    {
        switch (target)
        {
            case MemberExpression mem:
            {
                Expression obj = LowerExpression(mem.Object);
                return ReferenceEquals(obj, mem.Object) ? target : mem with { Object = obj };
            }
            case IndexExpression idx:
            {
                Expression obj = LowerExpression(idx.Object);
                Expression index = LowerExpression(idx.Index);
                return ReferenceEquals(obj, idx.Object) && ReferenceEquals(index, idx.Index)
                    ? target
                    : idx with { Object = obj, Index = index };
            }
            default:
                return target;
        }
    }

    private Expression LowerExpression(Expression expr)
    {
        switch (expr)
        {
            // ?�?� IndexExpression ??obj.$getitem!(idx) ?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�
            case IndexExpression idx:
            {
                Expression loweredObj = LowerExpression(idx.Object);
                Expression loweredIdx = LowerExpression(idx.Index);

                // Determine failable suffix: look up $getitem on the target type.
                // Default to "!" (failable) ??all stdlib collections use failable $getitem!.
                string propertyName = "$getitem!";
                TypeInfo? targetType = idx.Object.ResolvedType;
                if (targetType != null)
                {
                    var getItem =
                        ctx.Registry.LookupMethod(type: targetType, methodName: "$getitem");
                    if (getItem != null)
                        propertyName = getItem.IsFailable ? "$getitem!" : "$getitem";
                }

                var member = new MemberExpression(
                    Object: loweredObj,
                    PropertyName: propertyName,
                    Location: idx.Location);
                return new CallExpression(
                    Callee: member,
                    Arguments: [loweredIdx],
                    Location: idx.Location)
                {
                    ResolvedType = idx.ResolvedType
                };
            }

            // ?�?� SliceExpression ??obj.$getslice(from: a, to: b) ?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�
            case SliceExpression slice:
            {
                Expression loweredObj = LowerExpression(slice.Object);
                Expression loweredStart = LowerExpression(slice.Start);
                Expression loweredEnd = LowerExpression(slice.End);

                var member = new MemberExpression(
                    Object: loweredObj,
                    PropertyName: "$getslice",
                    Location: slice.Location);
                return new CallExpression(
                    Callee: member,
                    Arguments:
                    [
                        new NamedArgumentExpression(
                            Name: "from",
                            Value: loweredStart,
                            Location: slice.Location),
                        new NamedArgumentExpression(
                            Name: "to",
                            Value: loweredEnd,
                            Location: slice.Location)
                    ],
                    Location: slice.Location)
                {
                    ResolvedType = slice.ResolvedType
                };
            }

            // ?�?� GenericMemberExpression ??member + index ??$getitem! ?�?�?�?�?�?�?�?�?�?�?�?�?�?�
            // Parser quirk: obj.field[i] is parsed as GenericMemberExpression(obj, "field", [i]).
            // TypeArguments are index expressions in disguise; lower to IndexExpression then recurse.
            case GenericMemberExpression gme when gme.TypeArguments.Count > 0:
            {
                Expression loweredObj = LowerExpression(gme.Object);
                var memberExpr = new MemberExpression(
                    Object: loweredObj,
                    PropertyName: gme.MemberName,
                    Location: gme.Location)
                {
                    ResolvedType = gme.ResolvedType
                };

                // Use first type-arg name as identifier (the index variable).
                var idxExpr = new IdentifierExpression(
                    Name: gme.TypeArguments[0].Name,
                    Location: gme.TypeArguments[0].Location)
                {
                    ResolvedType = gme.TypeArguments[0].ResolvedType
                };

                var indexExpr = new IndexExpression(
                    Object: memberExpr,
                    Index: idxExpr,
                    Location: gme.Location)
                {
                    ResolvedType = gme.ResolvedType
                };

                // Recurse ??IndexExpression case above converts to $getitem! call.
                return LowerExpression(indexExpr);
            }

            case GenericMemberExpression gme:
            {
                // No type arguments ??plain member access; just lower the object.
                Expression loweredObj = LowerExpression(gme.Object);
                return ReferenceEquals(loweredObj, gme.Object)
                    ? expr
                    : new MemberExpression(
                        Object: loweredObj,
                        PropertyName: gme.MemberName,
                        Location: gme.Location)
                    {
                        ResolvedType = gme.ResolvedType
                    };
            }

            // ?�?� ChainedComparisonExpression ??AND-chain of pairwise comparisons ?�?�
            // e.g. a < b < c ??(a < b) and (b < c)
            // Middle operands may be evaluated twice; acceptable here since chained
            // comparisons in stdlib bodies use trivially pure expressions (identifiers/literals).
            case ChainedComparisonExpression chain:
            {
                TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");

                // Lower all operands
                var operands = new List<Expression>(capacity: chain.Operands.Count);
                foreach (Expression operand in chain.Operands)
                    operands.Add(LowerExpression(operand));

                // Build pairwise comparisons
                Expression result = new BinaryExpression(
                    Left: operands[0],
                    Operator: chain.Operators[0],
                    Right: operands[1],
                    Location: chain.Location)
                { ResolvedType = boolType };

                for (int i = 1; i < chain.Operators.Count; i++)
                {
                    Expression pairCmp = new BinaryExpression(
                        Left: operands[i],
                        Operator: chain.Operators[i],
                        Right: operands[i + 1],
                        Location: chain.Location)
                    { ResolvedType = boolType };

                    result = new BinaryExpression(
                        Left: result,
                        Operator: BinaryOperator.And,
                        Right: pairCmp,
                        Location: chain.Location)
                    { ResolvedType = boolType };
                }

                // Recurse so pairwise BinaryExpression nodes created above are also lowered.
                return LowerExpression(result);
            }

            // ?�?� BinaryExpression ??receiver.$method(you: arg) ?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�
            // Operators with GetMethodName() == null (And, Or, Is, Identical, But, ...)
            // are not overloadable and stay as BinaryExpression for codegen.

            case BinaryExpression bin:
            {
                string? methodName = bin.Operator.GetMethodName();

                if (methodName == null)
                {
                    // For Assign: lower the right side, and lower the INTERIOR of the left side
                    // (e.g., the Object of a MemberExpression, or the Object/Index of an
                    // IndexExpression).  The outermost left-side node must stay as-is so that
                    // EmitBinaryAssign can dispatch on its type (MemberExpression ??field write,
                    // IndexExpression ??$setitem!).  Lowering the entire left would convert
                    // IndexExpression ??CallExpression($getitem!), breaking setitem dispatch.
                    if (bin.Operator == BinaryOperator.Assign)
                    {
                        Expression rhs = LowerExpression(bin.Right);
                        Expression lhs = LowerAssignTarget(bin.Left);
                        return ReferenceEquals(rhs, bin.Right) && ReferenceEquals(lhs, bin.Left)
                            ? expr
                            : bin with { Left = lhs, Right = rhs };
                    }

                    Expression left0 = LowerExpression(bin.Left);
                    Expression right0 = LowerExpression(bin.Right);
                    return ReferenceEquals(left0, bin.Left) && ReferenceEquals(right0, bin.Right)
                        ? expr
                        : bin with { Left = left0, Right = right0 };
                }

                Expression left = LowerExpression(bin.Left);
                Expression right = LowerExpression(bin.Right);

                // Membership operators reverse receiver/argument: x in coll ??coll.$contains(x)
                bool isReversed = bin.Operator is BinaryOperator.In or BinaryOperator.NotIn;
                Expression receiver = isReversed ? right : left;
                Expression argument = isReversed ? left : right;

                // Look up the exact overload (by arg type) to get failable suffix, param name, and
                // ResolvedRoutine. LookupMethodOverload disambiguates e.g. Moment.$sub(Moment)->Duration
                // from Moment.$sub(Duration)->Moment. Setting ResolvedRoutine tells codegen which
                // overload to call without performing its own (potentially ambiguous) lookup.
                TypeInfo? receiverType = receiver.ResolvedType;
                TypeInfo? argType = argument.ResolvedType;
                TypeModel.Symbols.RoutineInfo? resolvedMethod = null;
                if (receiverType != null)
                {
                    resolvedMethod = argType != null
                        ? ctx.Registry.LookupMethodOverload(type: receiverType, methodName: methodName,
                            argTypes: [argType])
                        : ctx.Registry.LookupMethod(type: receiverType, methodName: methodName);
                }

                // Flags types have no $bitand/$bitor/$bitnot/$eq/$ne method bodies ??codegen handles
                // them as direct LLVM instructions (bitwise or icmp eq/ne on the underlying i64).
                // Skip method-call lowering so the BinaryExpression stays and codegen emits the
                // instruction directly, avoiding infinite recursion in the generated $eq/$ne body.
                if (receiverType is FlagsTypeInfo
                    && methodName is "$bitand" or "$bitor" or "$bitxor" or "$eq" or "$ne")
                {
                    return ReferenceEquals(left, bin.Left) && ReferenceEquals(right, bin.Right)
                        ? expr
                        : bin with { Left = left, Right = right };
                }

                // Choice $eq/$ne bodies use BinaryOperator.Is (not Equal), so they never reach
                // this point. No skip needed for choice types.

                // Always lower to a method call ??even when the method isn't in the registry
                // (e.g., stdlib bodies where ResolvedType is null).  When ResolvedRoutine is null,
                // codegen's EmitMethodCall resolves the method at emission time using the receiver's
                // LLVM-inferred type; it will also retry with isFailable:null to find $add! etc.
                string callName = resolvedMethod != null
                    ? (resolvedMethod.IsFailable ? methodName + "!" : methodName)
                    : methodName;   // no suffix when method unknown ??EmitMethodCall will find it
                string paramName = resolvedMethod?.Parameters.Count > 0
                    ? resolvedMethod.Parameters[0].Name
                    : "you";

                var binCallee = new MemberExpression(
                    Object: receiver,
                    PropertyName: callName,
                    Location: bin.Location);

                return new CallExpression(
                    Callee: binCallee,
                    Arguments: [new NamedArgumentExpression(Name: paramName, Value: argument, Location: bin.Location)],
                    Location: bin.Location)
                { ResolvedType = bin.ResolvedType, ResolvedRoutine = resolvedMethod };
            }

            // ?�?� ForceUnwrap (!!) ??operand.$unwrap() ?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�
            // Always lower to a CallExpression ??never fall back to UnaryExpression.
            // This runs for both user code (where ExpressionLoweringPass has already
            // run but no longer handles ForceUnwrap) and stdlib bodies (which bypass
            // ExpressionLoweringPass).  ResolvedType may be null for stdlib bodies;
            // codegen infers the return type from the $unwrap method definition.

            case UnaryExpression { Operator: UnaryOperator.ForceUnwrap } forceUnwrap:
            {
                Expression operand = LowerExpression(forceUnwrap.Operand);
                TypeInfo? operandType = operand.ResolvedType;
                TypeModel.Symbols.RoutineInfo? unwrapMethod = operandType != null
                    ? ctx.Registry.LookupMethod(type: operandType, methodName: "$unwrap")
                    : null;
                return new CallExpression(
                    Callee: new MemberExpression(
                        Object: operand,
                        PropertyName: "$unwrap",
                        Location: forceUnwrap.Location),
                    Arguments: [],
                    Location: forceUnwrap.Location)
                { ResolvedType = forceUnwrap.ResolvedType, ResolvedRoutine = unwrapMethod };
            }

            // ?�?� UnaryExpression ??operand.$method() ?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�
            // Not, Steal ??no wired method, stay as UnaryExpression.

            case UnaryExpression unary:
            {
                string? methodName = unary.Operator.GetMethodName();
                Expression operand = LowerExpression(unary.Operand);

                if (methodName == null)
                {
                    return ReferenceEquals(operand, unary.Operand)
                        ? expr
                        : unary with { Operand = operand };
                }

                TypeInfo? operandType = operand.ResolvedType;

                // Flags types have no $bitnot method body ??codegen handles it via EmitBitwiseNot.
                // Skip method-call lowering so the UnaryExpression passes through unchanged.
                if (operandType is FlagsTypeInfo
                    && methodName == "$bitnot")
                {
                    return ReferenceEquals(operand, unary.Operand)
                        ? expr
                        : unary with { Operand = operand };
                }

                TypeModel.Symbols.RoutineInfo? resolvedUnaryMethod = null;
                if (operandType != null)
                {
                    resolvedUnaryMethod = ctx.Registry.LookupMethodOverload(type: operandType,
                        methodName: methodName, argTypes: []);
                }

                // Always lower to a method call ??even when method isn't resolved
                // (e.g., stdlib bodies with no ResolvedType on operands).
                string callName = resolvedUnaryMethod != null
                    ? (resolvedUnaryMethod.IsFailable ? methodName + "!" : methodName)
                    : methodName;

                var unaryCallee = new MemberExpression(
                    Object: operand,
                    PropertyName: callName,
                    Location: unary.Location);

                return new CallExpression(
                    Callee: unaryCallee,
                    Arguments: [],
                    Location: unary.Location)
                { ResolvedType = unary.ResolvedType, ResolvedRoutine = resolvedUnaryMethod };
            }

            case CallExpression call:
            {
                Expression callee = LowerExpression(call.Callee);
                var args = new List<Expression>(capacity: call.Arguments.Count);
                bool argsChanged = false;
                foreach (Expression arg in call.Arguments)
                {
                    Expression lowered = LowerExpression(arg);
                    args.Add(lowered);
                    if (!ReferenceEquals(lowered, arg)) argsChanged = true;
                }

                return !argsChanged && ReferenceEquals(callee, call.Callee)
                    ? expr
                    : call with { Callee = callee, Arguments = args };
            }

            case MemberExpression mem:
            {
                Expression obj = LowerExpression(mem.Object);
                return ReferenceEquals(obj, mem.Object) ? expr : mem with { Object = obj };
            }

            case NamedArgumentExpression named:
            {
                Expression val = LowerExpression(named.Value);
                return ReferenceEquals(val, named.Value) ? expr : named with { Value = val };
            }

            case CreatorExpression creator:
            {
                var members = new List<(string Name, Expression Value)>(
                    capacity: creator.MemberVariables.Count);
                bool changed = false;
                foreach ((string name, Expression value) in creator.MemberVariables)
                {
                    Expression lowered = LowerExpression(value);
                    members.Add((name, lowered));
                    if (!ReferenceEquals(lowered, value)) changed = true;
                }

                return changed ? creator with { MemberVariables = members } : expr;
            }

            case WithExpression withExpr:
            {
                Expression loweredBase = LowerExpression(withExpr.Base);
                var updates =
                    new List<(List<string>? Path, Expression? Index, Expression Value)>(
                        capacity: withExpr.Updates.Count);
                bool changed = !ReferenceEquals(loweredBase, withExpr.Base);
                foreach ((List<string>? path, Expression? index, Expression value) in
                         withExpr.Updates)
                {
                    Expression loweredVal = LowerExpression(value);
                    updates.Add((path, index, loweredVal));
                    if (!ReferenceEquals(loweredVal, value)) changed = true;
                }

                return changed ? withExpr with { Base = loweredBase, Updates = updates } : expr;
            }

            case GenericMethodCallExpression gmc:
            {
                Expression obj = LowerExpression(gmc.Object);
                var args = new List<Expression>(capacity: gmc.Arguments.Count);
                bool argsChanged = false;
                foreach (Expression arg in gmc.Arguments)
                {
                    Expression lowered = LowerExpression(arg);
                    args.Add(lowered);
                    if (!ReferenceEquals(lowered, arg)) argsChanged = true;
                }

                return !argsChanged && ReferenceEquals(obj, gmc.Object)
                    ? expr
                    : gmc with { Object = obj, Arguments = args };
            }

            case CompoundAssignmentExpression compound:
            {
                Expression val = LowerExpression(compound.Value);
                return ReferenceEquals(val, compound.Value)
                    ? expr
                    : compound with { Value = val };
            }

            case StealExpression steal:
                // steal is a type-system-only annotation; strip the wrapper.
                return LowerExpression(steal.Operand);

            case InsertedTextExpression ftext:
            {
                var parts = new List<InsertedTextPart>(capacity: ftext.Parts.Count);
                bool changed = false;
                foreach (InsertedTextPart part in ftext.Parts)
                {
                    if (part is ExpressionPart ep)
                    {
                        Expression lowered = LowerExpression(ep.Expression);
                        parts.Add(ep with { Expression = lowered });
                        if (!ReferenceEquals(lowered, ep.Expression)) changed = true;
                    }
                    else
                    {
                        parts.Add(part);
                    }
                }

                return changed ? ftext with { Parts = parts } : expr;
            }

            case ConditionalExpression cond:
            {
                Expression condExpr = LowerExpression(cond.Condition);
                return ReferenceEquals(condExpr, cond.Condition)
                    ? expr
                    : cond with { Condition = condExpr };
            }

            case TupleLiteralExpression tuple:
            {
                var elems = new List<Expression>(capacity: tuple.Elements.Count);
                bool changed = false;
                foreach (Expression el in tuple.Elements)
                {
                    Expression lowered = LowerExpression(el);
                    elems.Add(lowered);
                    if (!ReferenceEquals(lowered, el)) changed = true;
                }

                return changed ? tuple with { Elements = elems } : expr;
            }

            case ListLiteralExpression list:
            {
                var elems = new List<Expression>(capacity: list.Elements.Count);
                bool changed = false;
                foreach (Expression el in list.Elements)
                {
                    Expression lowered = LowerExpression(el);
                    elems.Add(lowered);
                    if (!ReferenceEquals(lowered, el)) changed = true;
                }

                return changed ? list with { Elements = elems } : expr;
            }

            default:
                // LiteralExpression, IdentifierExpression, TypeExpression, RangeExpression,
                // LambdaExpression, DictLiteralExpression, SetLiteralExpression,
                // DictEntryLiteralExpression, CarrierPayloadExpression, TypeIdExpression, etc.
                return expr;
        }
    }

    /// <summary>
    /// Lowers operator expressions in all synthesized bodies stored in <see cref="PostprocessingContext.VariantBodies"/>.
    /// Called once from <see cref="DesugaringPipeline.RunGlobal"/> after <c>WiredRoutinePass</c> has
    /// populated <c>VariantBodies</c>.
    /// </summary>
    public void RunOnVariantBodies()
    {
        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            Statement body = ctx.VariantBodies[key];
            Statement lowered = LowerStatement(body);
            if (!ReferenceEquals(lowered, body))
                ctx.VariantBodies[key] = lowered;
        }
    }
}
