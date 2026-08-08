using System;
using System.Collections.Generic;
using System.Linq;
using Compiler.Desugaring;
using Compiler.Instantiation;
using Compiler.Synthesis;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Postprocessing.Passes;

/// <summary>
/// Lowers operator-sugar expressions to plain method call nodes.
/// Runs after <see cref="ExpressionLoweringPass"/> in the per-file pipeline.
///
/// <para>Transformations:</para>
/// <list type="bullet">
///   <item><see cref="IndexExpression"/> (<c>obj[i]</c>) ??
///         <c>obj.getitem!(i)</c> -> failable method call.</item>
///   <item><see cref="GenericMemberExpression"/> (<c>obj.field[i]</c>, parser quirk) ??
///         <c>MemberExpression(obj, field)</c> + <c>IndexExpression</c> ??<c>getitem!</c>.</item>
///   <item><see cref="BinaryExpression"/> with an overloadable operator ??
///         <c>left.method(you: right)</c>. Membership operators reverse operands:
///         <c>x in coll</c> ??<c>coll.contains(x)</c>.</item>
///   <item><see cref="UnaryExpression"/> with <c>!!</c> (<see cref="UnaryOperator.ForceUnwrap"/>) ??
///         <c>operand.unwrap()</c> -> always lowered, even in stdlib bodies (which bypass
///         <see cref="ExpressionLoweringPass"/>).</item>
///   <item><see cref="UnaryExpression"/> with a wired method (<c>-</c>, <c>~</c>) ??
///         <c>operand.neg()</c> / <c>operand.bitnot()</c> when the method is resolved.</item>
/// </list>
///
/// <para>Only the <em>value</em> side of <see cref="AssignmentStatement"/> is lowered.
/// Indexed-assignment targets (<c>arr[i] = val</c>) remain as <see cref="IndexExpression"/>
/// so codegen's <c>EmitAssignment</c> can dispatch to <c>setitem!</c>.</para>
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

    //  Statement lowering

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

            case EachStatement f:
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
                Statement? fb = u.FallbackBody != null ? LowerStatement(u.FallbackBody) : null;
                bool changed = !ReferenceEquals(res, u.Resource) || !ReferenceEquals(body, u.Body)
                               || !ReferenceEquals(fb, u.FallbackBody);
                return changed ? u with { Resource = res, Body = body, FallbackBody = fb } : stmt;
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
                // codegen's EmitAssignment can dispatch to setitem!.
                Expression val = LowerExpression(asgn.Value);
                return ReferenceEquals(val, asgn.Value) ? stmt : asgn with { Value = val };
            }

            case DeclarationStatement { Declaration: VariableDeclaration { Initializer: not null } vd } decl:
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

    //  Expression lowering

    /// <summary>
    /// Lowers the INTERIOR of an assignment target while preserving the outermost
    /// node type so <c>EmitBinaryAssign</c> can still dispatch on it:
    /// <list type="bullet">
    ///   <item><c>MemberExpression(obj, prop)</c> -> lower <c>obj</c>; keep outer MemberExpression.</item>
    ///   <item><c>IndexExpression(coll, idx)</c>  -> lower <c>coll</c> and <c>idx</c>; keep outer IndexExpression.</item>
    ///   <item><c>IdentifierExpression</c>         -> unchanged.</item>
    /// </list>
    /// This is needed because assignment targets like <c>node!!.field = v</c> or
    /// <c>coll[expr!!] = v</c> have <c>!!</c> (ForceUnwrap) nested inside the target,
    /// which must be lowered to <c>unwrap()</c> even though the outer shape must remain.
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

                // Resolve setitem with method-level generic monomorphization (parallel to the
                // getitem! lowering path). Non-generic owners with method-level generics (e.g.
                // BitList.setitem![I]) need the resolved routine stashed so codegen can dispatch
                // to the monomorphized entry rather than hitting ResolveMethod's generic-def guard.
                RoutineInfo? resolvedSetItem = null;
                TypeInfo? targetType = obj.ResolvedType ?? idx.Object.ResolvedType;
                if (targetType != null)
                {
                    resolvedSetItem =
                        ctx.Registry.LookupMethod(type: targetType, methodName: "setitem");
                    if (resolvedSetItem != null)
                    {
                        var argTypes = new List<TypeInfo>();
                        TypeInfo? indexType = index.ResolvedType ?? idx.Index.ResolvedType;
                        if (indexType != null)
                            argTypes.Add(item: indexType);
                        resolvedSetItem = ResolveMethodGenericRoutine(
                            routine: resolvedSetItem,
                            argTypes: argTypes);
                    }
                }

                if (ReferenceEquals(obj, idx.Object) && ReferenceEquals(index, idx.Index) &&
                    resolvedSetItem == null)
                {
                    return target;
                }

                var rewritten = idx with { Object = obj, Index = index };
                rewritten.ResolvedType = idx.ResolvedType;
                rewritten.ResolvedSetItem = resolvedSetItem ?? idx.ResolvedSetItem;
                return rewritten;
            }
            default:
                return target;
        }
    }

    private Expression LowerExpression(Expression expr)
    {
        switch (expr)
        {
            // IndexExpression -> obj.getitem!(idx)
            case IndexExpression idx:
            {
                // Typewise type-receiver: `NumericSumAdd[T].method()` parses as
                // IndexExpression(Ident("NumericSumAdd"), Ident("T")) because the parser only
                // treats `Ident[...]` as a generic-method form when `]` is immediately followed
                // by `(`. SA recognizes the pattern and resolves the IndexExpression to the
                // generic resolution type — collapse to a bare typed identifier so MemberExpression
                // codegen sees a typewise receiver.
                string? GendefName(TypeInfo? t) => t switch
                {
                    RecordTypeInfo { GenericDefinition: { } d } => d.Name,
                    EntityTypeInfo { GenericDefinition: { } d } => d.Name,
                    ProtocolTypeInfo { GenericDefinition: { } d } => d.Name,
                    _ => null
                };
                if (idx is { Object: IdentifierExpression typeObjId, ResolvedType: { IsGenericResolution: true } resolvedTy } &&
                    (resolvedTy.Name == typeObjId.Name
                     || GendefName(resolvedTy) == typeObjId.Name))
                {
                    return new IdentifierExpression(
                        Name: typeObjId.Name,
                        Location: idx.Location)
                    {
                        ResolvedType = resolvedTy
                    };
                }

                Expression loweredObj = LowerExpression(idx.Object);
                Expression loweredIdx = LowerExpression(idx.Index);

                // Failability is a property, not part of the name — the property name is always
                // the bare `getitem`; codegen dispatches via ResolvedRoutine (which carries
                // IsFailable). Resolve the method to set ResolvedRoutine / lowering kind.
                const string propertyName = "getitem";
                RoutineInfo? resolvedGetItem = null;
                TypeInfo? targetType = idx.Object.ResolvedType;

                // Back-index desugaring: `coll[^n]` has a BackIndex-typed index. Collections only
                // expose `getitem!(index: U64)`, so rewrite the index to a forward U64 position
                // via `backIdx.resolve!(coll.count())` (which throws on out-of-range, matching the
                // old per-type BackIndex overload). The object is referenced twice — acceptable for
                // the common `var[^n]` case; a side-effecting receiver would evaluate twice.
                TypeInfo? rawIndexType = loweredIdx.ResolvedType ?? idx.Index.ResolvedType;
                if (targetType != null && rawIndexType is { Name: "BackIndex" })
                {
                    loweredIdx = BuildBackIndexResolve(loweredObj: loweredObj,
                        backIndex: loweredIdx, targetType: targetType, location: idx.Location);
                }

                if (targetType != null)
                {
                    TypeInfo? indexType = loweredIdx.ResolvedType ?? idx.Index.ResolvedType;
                    // Pick the `getitem` overload by the (now forward U64) index argument type.
                    resolvedGetItem = indexType != null
                        ? ctx.Registry.LookupMethodOverload(type: targetType,
                              methodName: "getitem", argTypes: [indexType])
                          ?? ctx.Registry.LookupMethod(type: targetType, methodName: "getitem")
                        : ctx.Registry.LookupMethod(type: targetType, methodName: "getitem");
                    if (resolvedGetItem != null && indexType != null)
                    {
                        resolvedGetItem = ResolveMethodGenericRoutine(routine: resolvedGetItem,
                            argTypes: [indexType]);
                    }
                }

                CallLoweringKind getitemKind = resolvedGetItem != null
                    ? ClassifyMethod(resolvedGetItem)
                    : targetType != null
                        ? CallLoweringKind.DirectMemberRoutine
                        : CallLoweringKind.Unknown;
                var member = new MemberExpression(
                    Object: loweredObj,
                    MemberName: propertyName,
                    Location: idx.Location);
                return new CallExpression(
                    Callee: member,
                    Arguments: [loweredIdx],
                    Location: idx.Location)
                {
                    ResolvedRoutine = resolvedGetItem,
                    ResolvedType = idx.ResolvedType,
                    LoweringKind = getitemKind
                };
            }

            //  GenericMemberExpression -> member + index ??getitem!
            // Parser quirk: obj.field[i] is parsed as GenericMemberExpression(obj, "field", [i]).
            // TypeArguments are index expressions in disguise; lower to IndexExpression then recurse.
            //
            // Exception: `Ident[T]` (type-with-type-args, e.g. `NumericSumAdd[T].identity_lazy()`)
            // is parsed as GenericMemberExpression(Ident, Ident.Name, [T]) — Object.Name == MemberName.
            // Those are real type args, not indices; lower to a plain MemberExpression so the
            // typewise receiver flows through codegen normally.
            case GenericMemberExpression { TypeArguments.Count: > 0 } gme when !(gme.Object is IdentifierExpression idObj && idObj.Name == gme.MemberName):
            {
                Expression loweredObj = LowerExpression(gme.Object);
                var memberExpr = new MemberExpression(
                    Object: loweredObj,
                    MemberName: gme.MemberName,
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

                // Recurse -> IndexExpression case above converts to getitem! call.
                return LowerExpression(indexExpr);
            }

            // Typewise receiver `Ident[T]` parsed as GenericMemberExpression(Ident, Ident.Name, [T]).
            // Collapse to a bare IdentifierExpression carrying the resolved type so the outer
            // MemberExpression (e.g. `.identity_lazy()`) sees an identifier with ResolvedType set
            // — that is how codegen detects typewise/common calls.
            case GenericMemberExpression { TypeArguments.Count: > 0, Object: IdentifierExpression typeIdent } gme when typeIdent.Name == gme.MemberName:
            {
                return new IdentifierExpression(
                    Name: gme.MemberName,
                    Location: gme.Location)
                {
                    ResolvedType = gme.ResolvedType ?? typeIdent.ResolvedType
                };
            }

            case GenericMemberExpression gme:
            {
                // No type arguments -> plain member access; just lower the object.
                Expression loweredObj = LowerExpression(gme.Object);
                return ReferenceEquals(loweredObj, gme.Object)
                    ? expr
                    : new MemberExpression(
                        Object: loweredObj,
                        MemberName: gme.MemberName,
                        Location: gme.Location)
                    {
                        ResolvedType = gme.ResolvedType
                    };
            }

            //  ChainedComparisonExpression -> AND-chain of pairwise comparisons
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

            //  BinaryExpression -> receiver.method(you: arg)
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
                    // EmitBinaryAssign can dispatch on its type (MemberExpression -> field write,
                    // IndexExpression ??setitem!).  Lowering the entire left would convert
                    // IndexExpression -> CallExpression(getitem!), breaking setitem dispatch.
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

                // Membership operators reverse receiver/argument: x in coll -> coll.contains(x)
                bool isReversed = bin.Operator is BinaryOperator.In or BinaryOperator.NotIn;
                Expression receiver = isReversed ? right : left;
                Expression argument = isReversed ? left : right;

                // Look up the exact overload (by arg type) to get failable suffix, param name, and
                // ResolvedRoutine. LookupMethodOverload disambiguates e.g. Moment.sub(Moment)->Duration
                // from Moment.sub(Duration)->Moment. Setting ResolvedRoutine tells codegen which
                // overload to call without performing its own (potentially ambiguous) lookup.
                TypeInfo? receiverType = receiver.ResolvedType;
                TypeInfo? argType = argument.ResolvedType;
                RoutineInfo? resolvedMethod = null;
                if (receiverType != null)
                {
                    resolvedMethod = argType != null
                        ? ctx.Registry.LookupMethodOverload(type: receiverType, methodName: methodName,
                            argTypes: [argType])
                        : ctx.Registry.LookupMethod(type: receiverType, methodName: methodName);
                    resolvedMethod ??= ctx.Registry.LookupMethod(type: receiverType,
                        methodName: methodName);
                    // If the non-failable form doesn't exist, try the failable form (sub -> sub!).
                    // Types like U64 only define sub! (underflow would be undefined behavior).
                    // The name is BARE; failability is structural — retry with isFailable: true.
                    if (resolvedMethod == null)
                    {
                        resolvedMethod = argType != null
                            ? ctx.Registry.LookupMethodOverload(type: receiverType,
                                methodName: methodName, argTypes: [argType])
                            : null;
                        resolvedMethod ??= ctx.Registry.LookupMethod(type: receiverType,
                            methodName: methodName, isFailable: true);
                    }

                    // Suflae wraps container locals as `Roamed[Dict]` / `Roamed[Set]` post-SA. A
                    // membership/comparison operator lowers to `receiver.contains(x)` / `.eq(x)` HERE in
                    // Phase 7 — after the wrapper-forwarder pass has frozen — so the method is unresolved
                    // on the wrapper. Resolve it against the UNWRAPPED inner type (exactly as SA did for
                    // an explicit `d.count()` while `d` was still the bare container before promotion) and
                    // stamp the inner method; codegen projects the Roamed receiver to the inner value for
                    // `me`, same as every other inner-method call on a Roamed container.
                    // The Roamed handle reaches here in EITHER representation the pipeline produces: a
                    // WrapperTypeInfo (SuflaeEntityLoweringPass.WrapInRoam) or a RecordTypeInfo (resolver-
                    // built). Extract the inner container type from whichever it is.
                    TypeInfo? innerRecv = receiverType switch
                    {
                        WrapperTypeInfo w
                            when Compiler.Resolution.TypeRegistry.GetRcWrapperBaseName(type: w) != null
                            => w.InnerType,
                        RecordTypeInfo r
                            when Compiler.Resolution.TypeRegistry.GetRcWrapperBaseName(type: r) != null
                                 && r.TypeArguments is { Count: >= 1 } ra
                            => ra[index: 0],
                        _ => null
                    };
                    if (resolvedMethod == null && innerRecv != null)
                    {
                        resolvedMethod = argType != null
                            ? ctx.Registry.LookupMethodOverload(type: innerRecv,
                                methodName: methodName, argTypes: [argType])
                            : null;
                        resolvedMethod ??= ctx.Registry.LookupMethod(type: innerRecv, methodName: methodName);
                        resolvedMethod ??= ctx.Registry.LookupMethod(type: innerRecv,
                            methodName: methodName, isFailable: true);
                        // NOTE: the Roamed receiver is NOT projected here. Resolving the inner method is
                        // enough — codegen's unified receiver projection (EmitMemberRoutineCall) wraps the
                        // Roamed handle in `raw_inner()` for every bare-`me` inner method uniformly, so the
                        // operator-lowered call, an index `d[i]`, and an explicit `d.count()` all funnel
                        // through the one projection site.
                    }
                }

                // Mixed fixed-width integer comparisons have no direct cross-width overloads in the
                // stdlib. Normalize both sides to a common width here so we lower to a concrete
                // same-type comparison instead of letting codegen fall back to an arbitrary overload.
                if (resolvedMethod == null &&
                    receiverType != null &&
                    argType != null &&
                    bin.Operator is BinaryOperator.Equal or BinaryOperator.NotEqual
                        or BinaryOperator.Less or BinaryOperator.LessEqual
                        or BinaryOperator.Greater or BinaryOperator.GreaterEqual &&
                    TryResolveCommonIntegerComparisonType(left: receiverType,
                        right: argType,
                        out TypeInfo? commonType))
                {
                    receiver = WrapNumericOperand(expr: receiver, targetType: commonType!);
                    argument = WrapNumericOperand(expr: argument, targetType: commonType!);
                    receiverType = commonType;
                    argType = commonType;
                    resolvedMethod = ctx.Registry.LookupMethodOverload(type: commonType!,
                        methodName: methodName,
                        argTypes: [commonType!]) ??
                                     ctx.Registry.LookupMethod(type: commonType!,
                                         methodName: methodName);
                }

                if (resolvedMethod is { Parameters.Count: > 0 } &&
                    bin.Operator is BinaryOperator.ArithmeticLeftShift
                        or BinaryOperator.ArithmeticRightShift
                        or BinaryOperator.LogicalLeftShift
                        or BinaryOperator.LogicalRightShift)
                {
                    TypeInfo paramType = resolvedMethod.Parameters[index: 0].Type;
                    if (argType != null &&
                        argType.FullName != paramType.FullName &&
                        TryGetFixedWidthIntegerInfo(type: argType, out _, out _) &&
                        TryGetFixedWidthIntegerInfo(type: paramType, out _, out _))
                    {
                        argument = WrapNumericOperand(expr: argument, targetType: paramType);
                    }
                }

                // Flags types have no bitand/bitor/bitnot/eq/ne method bodies -> codegen handles
                // them as direct LLVM instructions (bitwise or icmp eq/ne on the underlying i64).
                // Skip method-call lowering so the BinaryExpression stays and codegen emits the
                // instruction directly, avoiding infinite recursion in the generated eq/ne body.
                if (receiverType is FlagsTypeInfo
                    && methodName is "bitand" or "bitor" or "bitxor" or "eq" or "ne")
                {
                    return ReferenceEquals(left, bin.Left) && ReferenceEquals(right, bin.Right)
                        ? expr
                        : bin with { Left = left, Right = right };
                }

                // Choice eq/ne bodies use BinaryOperator.Is (not Equal), so they never reach
                // this point. No skip needed for choice types.

                // Always lower to a method call -> even when the method isn't in the registry
                // (e.g., stdlib bodies where ResolvedType is null).  When ResolvedRoutine is null,
                // codegen's EmitMethodCall resolves the method at emission time using the receiver's
                // LLVM-inferred type; it will also retry with isFailable:null to find add! etc.
                // Failability is structural on the callee — no `!` in the name. When the method is
                // unknown, IsFailable stays false and codegen's EmitMethodCall retries either form.
                bool binFailable = resolvedMethod?.IsFailable ?? false;
                string paramName = resolvedMethod?.Parameters.Count > 0
                    ? resolvedMethod.Parameters[0].Name
                    : "you";

                var binCallee = new MemberExpression(
                    Object: receiver,
                    MemberName: methodName,
                    Location: bin.Location) { IsFailable = binFailable };

                CallLoweringKind lk = resolvedMethod != null
                    ? ClassifyMethod(resolvedMethod)
                    : receiverType != null ? CallLoweringKind.DirectMemberRoutine
                    : CallLoweringKind.Unknown;

                return new CallExpression(
                    Callee: binCallee,
                    Arguments: [new NamedArgumentExpression(Name: paramName, Value: argument, Location: bin.Location)],
                    Location: bin.Location)
                { ResolvedType = bin.ResolvedType, ResolvedRoutine = resolvedMethod, LoweringKind = lk };
            }

            //  ForceUnwrap (!!) -> operand.unwrap()
            // Always lower to a CallExpression -> never fall back to UnaryExpression.
            // This runs for both user code (where ExpressionLoweringPass has already
            // run but no longer handles ForceUnwrap) and stdlib bodies (which bypass
            // ExpressionLoweringPass).  ResolvedType may be null for stdlib bodies;
            // codegen infers the return type from the unwrap method definition.

            case UnaryExpression { Operator: UnaryOperator.ForceUnwrap } forceUnwrap:
            {
                Expression operand = LowerExpression(forceUnwrap.Operand);
                TypeInfo? operandType = operand.ResolvedType;
                RoutineInfo? unwrapMethod = operandType != null
                    ? ctx.Registry.LookupMethod(type: operandType, methodName: "unwrap")
                    : null;
                CallLoweringKind unwrapKind = unwrapMethod != null
                    ? ClassifyMethod(unwrapMethod)
                    : operandType != null
                        ? CallLoweringKind.DirectMemberRoutine
                        : CallLoweringKind.Unknown;
                return new CallExpression(
                    Callee: new MemberExpression(
                        Object: operand,
                        MemberName: "unwrap",
                        Location: forceUnwrap.Location),
                    Arguments: [],
                    Location: forceUnwrap.Location)
                {
                    ResolvedType = forceUnwrap.ResolvedType,
                    ResolvedRoutine = unwrapMethod,
                    LoweringKind = unwrapKind
                };
            }

            //  UnaryExpression -> operand.method()
            // Not, Steal -> no wired method, stay as UnaryExpression.

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

                // Flags types have no bitnot method body -> codegen handles it via EmitBitwiseNot.
                // Skip method-call lowering so the UnaryExpression passes through unchanged.
                if (operandType is FlagsTypeInfo
                    && methodName == "bitnot")
                {
                    return ReferenceEquals(operand, unary.Operand)
                        ? expr
                        : unary with { Operand = operand };
                }

                RoutineInfo? resolvedUnaryMethod = null;
                if (operandType != null)
                {
                    resolvedUnaryMethod = ctx.Registry.LookupMethodOverload(type: operandType,
                        methodName: methodName, argTypes: []);
                    resolvedUnaryMethod ??= ctx.Registry.LookupMethod(type: operandType,
                        methodName: methodName);
                }

                // Always lower to a method call -> even when method isn't resolved
                // (e.g., stdlib bodies with no ResolvedType on operands).
                // Failability is structural on the callee — no `!` in the name.
                bool unaryFailable = resolvedUnaryMethod?.IsFailable ?? false;

                var unaryCallee = new MemberExpression(
                    Object: operand,
                    MemberName: methodName,
                    Location: unary.Location) { IsFailable = unaryFailable };

                CallLoweringKind unaryKind = resolvedUnaryMethod != null
                    ? ClassifyMethod(resolvedUnaryMethod)
                    : operandType != null
                        ? CallLoweringKind.DirectMemberRoutine
                        : CallLoweringKind.Unknown;

                return new CallExpression(
                    Callee: unaryCallee,
                    Arguments: [],
                    Location: unary.Location)
                {
                    ResolvedType = unary.ResolvedType,
                    ResolvedRoutine = resolvedUnaryMethod,
                    LoweringKind = unaryKind
                };
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
            {
                Expression loweredOperand = LowerExpression(steal.Operand);
                return ReferenceEquals(loweredOperand, steal.Operand)
                    ? expr
                    : steal with { Operand = loweredOperand };
            }

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

            case LambdaExpression lambda:
            {
                Expression loweredBody = LowerExpression(lambda.Body);
                return ReferenceEquals(loweredBody, lambda.Body)
                    ? expr
                    : lambda with { Body = loweredBody };
            }

            default:
                // LiteralExpression, IdentifierExpression, TypeExpression, RangeExpression,
                // DictLiteralExpression, SetLiteralExpression,
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

    /// <summary>
    /// Lowers operator expressions in instantiated generic routine bodies.
    /// Phase 6's <c>GenericMonomorphizationPass</c> populates <c>InstantiatedGenericBodies</c>
    /// AFTER the Phase 7 RunGlobal sweep has finished, so those bodies miss the regular
    /// per-program operator-lowering pass. Without this method, `me.size = me.size + 1_u64`
    /// inside a monomorphized routine reaches codegen as a bare <c>BinaryExpression(Add)</c>
    /// and trips the "must be lowered to a wired call" guard.
    /// Caller passes the map directly (PostprocessingContext doesn't hold it).
    /// </summary>
    public void RunOnInstantiatedGenericBodies(
        Dictionary<string, MonomorphizedBody> instantiatedGenericBodies)
    {
        foreach (string key in instantiatedGenericBodies.Keys.ToList())
        {
            MonomorphizedBody entry = instantiatedGenericBodies[key];
            if (entry.IsSynthesized) continue; // pure-synthesized: no AST to walk
            Statement lowered = LowerStatement(entry.Ast.Body);
            if (!ReferenceEquals(lowered, entry.Ast.Body))
                instantiatedGenericBodies[key] = entry with
                {
                    Ast = entry.Ast with { Body = lowered }
                };
        }
    }

    private RoutineInfo ResolveMethodGenericRoutine(RoutineInfo routine,
        List<TypeInfo> argTypes)
    {
        if (!routine.IsGenericDefinition || routine.GenericParameters == null)
        {
            return routine;
        }

        if (argTypes.Any(static t => t is ErrorTypeInfo or GenericParameterTypeInfo))
        {
            return routine;
        }

        var inferred = new TypeInfo?[routine.GenericParameters.Count];
        // Skip the implicit `me` receiver parameter — argTypes only contains the explicit
        // call-site arguments (index type, value type, etc.), so we must align them
        // against the non-me parameters to correctly infer method-level generics like I.
        int argIdx = 0;
        foreach (ParameterInfo param in routine.Parameters)
        {
            if (param.Name == "me") continue;
            if (argIdx >= argTypes.Count) break;
            InferMethodGenericArguments(paramType: param.Type,
                argType: argTypes[index: argIdx],
                genericParameters: routine.GenericParameters,
                inferred: inferred);
            argIdx++;
        }

        if (inferred.Any(predicate: t => t is null or ErrorTypeInfo or GenericParameterTypeInfo))
        {
            return routine;
        }

        return ctx.Registry.GetOrCreateRoutineResolution(genericDef: routine,
            typeArguments: inferred.Select(selector: t => t!).ToList());
    }

    private static void InferMethodGenericArguments(TypeInfo paramType, TypeInfo argType,
        List<string> genericParameters, TypeInfo?[] inferred)
    {
        if (paramType is GenericParameterTypeInfo)
        {
            int idx = genericParameters.ToList().IndexOf(item: paramType.Name);
            if (idx >= 0 && inferred[idx] == null)
            {
                inferred[idx] = argType;
            }

            return;
        }

        if (paramType is { TypeArguments: { Count: > 0 } paramArgs } &&
            argType is { TypeArguments: { Count: > 0 } argArgs } &&
            paramArgs.Count == argArgs.Count)
        {
            for (int i = 0; i < paramArgs.Count; i++)
            {
                InferMethodGenericArguments(paramType: paramArgs[index: i],
                    argType: argArgs[index: i],
                    genericParameters: genericParameters,
                    inferred: inferred);
            }
        }
    }

    private static Expression WrapNumericOperand(Expression expr, TypeInfo targetType)
    {
        if (expr.ResolvedType?.FullName == targetType.FullName)
        {
            return expr;
        }

        return new CreatorExpression(
            TypeName: targetType.Name,
            TypeArguments: null,
            MemberVariables:
            [
                ("from", expr)
            ],
            Location: expr.Location)
        {
            ResolvedType = targetType,
            ConstructedType = targetType
        };
    }

    private static CallLoweringKind ClassifyMethod(RoutineInfo method)
    {
        if (method.LlvmIrTemplate != null) return CallLoweringKind.LlvmIntrinsic;
        return CallLoweringKind.DirectMemberRoutine;
    }

    /// <summary>
    /// Builds the forward-index expression for a back-index subscript: given a receiver and a
    /// <c>BackIndex</c> value, produces <c>backIndex.resolve!(receiver.count())</c> — a resolved
    /// <c>U64</c> position. This is the call-site desugaring that replaces the per-collection
    /// <c>getitem!(index: BackIndex)</c> overloads; collections need only declare the
    /// <c>getitem!(index: U64)</c> form. <c>BackIndex.resolve!</c> throws on out-of-range, so the
    /// bounds semantics match the old overload.
    /// </summary>
    private Expression BuildBackIndexResolve(Expression loweredObj, Expression backIndex,
        TypeInfo targetType, SourceLocation location)
    {
        // receiver.count() -> U64
        RoutineInfo? countRoutine = ctx.Registry.LookupMethod(type: targetType, methodName: Resolution.RuntimeContract.Collection.Count);
        var countCall = new CallExpression(
            Callee: new MemberExpression(Object: loweredObj, MemberName: Resolution.RuntimeContract.Collection.Count,
                Location: location),
            Arguments: [],
            Location: location)
        {
            ResolvedRoutine = countRoutine,
            ResolvedType = countRoutine?.ReturnType,
            LoweringKind = countRoutine != null
                ? ClassifyMethod(countRoutine)
                : CallLoweringKind.DirectMemberRoutine
        };

        // backIndex.resolve!(count) -> U64 (failable: throws IndexOutOfBoundsError on overshoot)
        TypeInfo? backIndexType = backIndex.ResolvedType;
        RoutineInfo? resolveRoutine = backIndexType != null
            ? ctx.Registry.LookupMethod(type: backIndexType, methodName: Resolution.RuntimeContract.Resolve, isFailable: true)
            : null;
        return new CallExpression(
            Callee: new MemberExpression(Object: backIndex, MemberName: Resolution.RuntimeContract.Resolve,
                Location: location),
            Arguments: [countCall],
            Location: location)
        {
            ResolvedRoutine = resolveRoutine,
            ResolvedType = resolveRoutine?.ReturnType,
            LoweringKind = resolveRoutine != null
                ? ClassifyMethod(resolveRoutine)
                : CallLoweringKind.DirectMemberRoutine
        };
    }

    private bool TryResolveCommonIntegerComparisonType(TypeInfo left, TypeInfo right,
        out TypeInfo? commonType)
    {
        commonType = null;

        if (!TryGetFixedWidthIntegerInfo(type: left, out bool leftSigned, out int leftWidth) ||
            !TryGetFixedWidthIntegerInfo(type: right, out bool rightSigned, out int rightWidth))
        {
            return false;
        }

        bool targetSigned;
        int targetWidth;
        if (leftSigned == rightSigned)
        {
            targetSigned = leftSigned;
            targetWidth = Math.Max(val1: leftWidth, val2: rightWidth);
        }
        else if (leftSigned && leftWidth > rightWidth)
        {
            targetSigned = true;
            targetWidth = leftWidth;
        }
        else if (rightSigned && rightWidth > leftWidth)
        {
            targetSigned = true;
            targetWidth = rightWidth;
        }
        else
        {
            targetSigned = true;
            targetWidth = NextSignedWidth(minExclusive: Math.Max(val1: leftWidth, val2: rightWidth));
            if (targetWidth == 0)
            {
                return false;
            }
        }

        commonType = ctx.Registry.LookupType(name: $"{(targetSigned ? "S" : "U")}{targetWidth}");
        return commonType != null;
    }

    private static bool TryGetFixedWidthIntegerInfo(TypeInfo type, out bool signed, out int width)
    {
        signed = false;
        width = 0;

        if (string.IsNullOrEmpty(value: type.Name) || type.Name.Length < 2)
        {
            return false;
        }

        signed = type.Name[0] == 'S';
        if (!signed && type.Name[0] != 'U')
        {
            return false;
        }

        return int.TryParse(s: type.Name[1..], result: out width);
    }

    private static int NextSignedWidth(int minExclusive)
    {
        foreach (int candidate in new[] { 8, 16, 32, 64, 128 })
        {
            if (candidate > minExclusive)
            {
                return candidate;
            }
        }

        return 0;
    }
}
