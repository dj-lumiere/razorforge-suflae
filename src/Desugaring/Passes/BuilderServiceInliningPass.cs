namespace Compiler.Desugaring.Passes;

using Compiler.Lexer;
using Compiler.Resolution;
using SemanticVerification;
using SemanticVerification.Enums;
using SemanticVerification.Types;
using SyntaxTree;

/// <summary>
/// Global pass — folds compile-time-constant BuilderService per-type calls to literal
/// expressions, eliminating runtime function calls to synthesized stubs.
///
/// <para>Runs in three contexts:</para>
/// <list type="number">
///   <item>Per-program pass (<see cref="Run"/>) — handles non-generic user and stdlib bodies.</item>
///   <item>VariantBodies sweep (<see cref="RunOnVariantBodies"/>) — after <c>WiredRoutinePass</c>.</item>
///   <item>Pre-monomorphized bodies sweep (<see cref="RunOnPreMonomorphizedBodies"/>) — after
///         <c>GenericMonomorphizationPass</c>.</item>
/// </list>
///
/// <para>Covered constant routines: <c>data_size</c>, <c>type_id</c>, <c>type_name</c>,
/// <c>module_name</c>, <c>full_type_name</c>, <c>member_variable_count</c>,
/// <c>is_generic</c>, <c>type_kind</c>.</para>
///
/// <para>For bodies with unbound generic type parameters (e.g. <c>T.data_size()</c> before
/// monomorphization), <see cref="GenericAstRewriter"/> folds these during its substitution
/// rewrite. This pass handles the concrete-type residue left in pre-built or non-generic bodies.</para>
/// </summary>
internal sealed class BuilderServiceInliningPass
{
    private readonly TypeRegistry _registry;

    private readonly DesugaringContext? _ctx;

    /// <summary>Creates a pass instance backed by a full <see cref="DesugaringContext"/>.</summary>
    internal BuilderServiceInliningPass(DesugaringContext ctx)
    {
        _ctx = ctx;
        _registry = ctx.Registry;
    }

    /// <summary>
    /// Creates a pass instance backed directly by a <see cref="TypeRegistry"/>.
    /// Used by the CodeGen planner to fold residual BS calls in slow-path monomorphized bodies
    /// (those bodies live in <c>MonomorphizationPlanner.MonomorphizedBodies</c>, which is outside
    /// the desugaring context and not seen by <see cref="RunOnPreMonomorphizedBodies"/>).
    /// </summary>
    internal BuilderServiceInliningPass(TypeRegistry registry) => _registry = registry;

    private static readonly HashSet<string> _foldableRoutines = new(StringComparer.Ordinal)
    {
        "data_size",
        "type_id",
        "type_name",
        "module_name",
        "full_type_name",
        "member_variable_count",
        "is_generic",
        "type_kind"
    };

    /// <summary>
    /// Returns true if <paramref name="routineName"/> is a BuilderService constant routine that
    /// can be folded to a literal. Used by <see cref="GenericAstRewriter"/> to gate its own fold.
    /// </summary>
    internal static bool IsFoldable(string routineName) =>
        _foldableRoutines.Contains(routineName);

    private static readonly SourceLocation _loc =
        new(FileName: "<bs-inline>", Line: 0, Column: 0, Position: 0);

    // Lazily looked up once per pass instance
    private TypeInfo? _u64Type;
    private TypeInfo? _s64Type;
    private TypeInfo? _textType;
    private TypeInfo? _boolType;
    private TypeInfo? _byteSizeType;

    // Set per-body in RunOnMonomorphizedBodies / RunOnPreMonomorphizedBodies so that
    // ResolveReceiverType can resolve unbound generic params (e.g. T → Core.Byte).
    private Dictionary<string, TypeInfo>? _currentTypeSubs;

    // ── Public entry points ────────────────────────────────────────────────

    /// <summary>
    /// Runs the pass on all routine declarations in <paramref name="program"/>.
    /// </summary>
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

    /// <summary>
    /// Folds BS constant calls in all synthesized bodies in
    /// <see cref="DesugaringContext.VariantBodies"/>.
    /// Called from <see cref="DesugaringPipeline.RunGlobal"/> after variant bodies are generated.
    /// </summary>
    public void RunOnVariantBodies()
    {
        foreach (string key in _ctx!.VariantBodies.Keys.ToList())
        {
            Statement body = _ctx.VariantBodies[key];
            Statement lowered = LowerStatement(body);
            if (!ReferenceEquals(lowered, body))
                _ctx.VariantBodies[key] = lowered;
        }
    }

    /// <summary>
    /// Folds BS constant calls in all pre-monomorphized bodies in
    /// <see cref="DesugaringContext.PreMonomorphizedBodies"/>.
    /// Called from <see cref="DesugaringPipeline.RunGlobal"/> after
    /// <c>GenericMonomorphizationPass</c> populates the map.
    /// </summary>
    public void RunOnPreMonomorphizedBodies()
    {
        foreach (string key in _ctx!.PreMonomorphizedBodies.Keys.ToList())
        {
            MonomorphizedBody entry = _ctx.PreMonomorphizedBodies[key];
            if (entry.IsSynthesized) continue; // pure-synthesized: no AST to walk

            _currentTypeSubs = entry.TypeSubs;
            Statement lowered = LowerStatement(entry.Ast.Body);
            _currentTypeSubs = null;
            if (!ReferenceEquals(lowered, entry.Ast.Body))
                _ctx.PreMonomorphizedBodies[key] = entry with
                {
                    Ast = entry.Ast with { Body = lowered }
                };
        }
    }

    /// <summary>
    /// Folds BS constant calls in all pre-rewritten monomorphized bodies in
    /// <paramref name="bodies"/>. Called by the CodeGen planner after
    /// <c>PreRewriteAll</c> to fold any residual <c>Byte.data_size()</c>-style calls
    /// that <see cref="GenericAstRewriter"/> left unfolded (e.g. when the receiver was
    /// an <see cref="IdentifierExpression"/> whose name could not be looked up at rewrite
    /// time via the string-substitution map).
    /// </summary>
    internal void RunOnMonomorphizedBodies(IDictionary<string, MonomorphizedBody> bodies)
    {
        foreach (string key in bodies.Keys.ToList())
        {
            MonomorphizedBody entry = bodies[key];
            if (entry.IsSynthesized) continue;

            _currentTypeSubs = entry.TypeSubs;
            Statement lowered = LowerStatement(entry.Ast.Body);
            _currentTypeSubs = null;
            if (!ReferenceEquals(lowered, entry.Ast.Body))
                bodies[key] = entry with { Ast = entry.Ast with { Body = lowered } };
        }
    }

    // ── Member list helper ─────────────────────────────────────────────────

    private void LowerMemberList(List<Declaration> members)
    {
        for (int j = 0; j < members.Count; j++)
        {
            if (members[j] is not RoutineDeclaration m) continue;
            Statement newBody = LowerStatement(m.Body);
            if (!ReferenceEquals(newBody, m.Body))
                members[j] = m with { Body = newBody };
        }
    }

    // ── Statement walker ───────────────────────────────────────────────────

    private Statement LowerStatement(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement b:
            {
                bool changed = false;
                var list = new List<Statement>(b.Statements.Count);
                foreach (Statement s in b.Statements)
                {
                    Statement ns = LowerStatement(s);
                    list.Add(ns);
                    if (!ReferenceEquals(ns, s)) changed = true;
                }
                return changed ? b with { Statements = list } : stmt;
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
                bool changed = !ReferenceEquals(cond, w.Condition)
                               || !ReferenceEquals(body, w.Body);
                return changed ? w with { Condition = cond, Body = body } : stmt;
            }

            case LoopStatement loop:
            {
                Statement body = LowerStatement(loop.Body);
                return ReferenceEquals(body, loop.Body) ? stmt : loop with { Body = body };
            }

            case ForStatement f:
            {
                Expression iter = LowerExpression(f.Iterable);
                Statement body = LowerStatement(f.Body);
                bool changed = !ReferenceEquals(iter, f.Iterable)
                               || !ReferenceEquals(body, f.Body);
                return changed ? f with { Iterable = iter, Body = body } : stmt;
            }

            case WhenStatement ws:
            {
                Expression subject = LowerExpression(ws.Expression);
                bool changed = !ReferenceEquals(subject, ws.Expression);
                var clauses = new List<WhenClause>(ws.Clauses.Count);
                foreach (WhenClause c in ws.Clauses)
                {
                    Statement cb = LowerStatement(c.Body);
                    if (!ReferenceEquals(cb, c.Body)) changed = true;
                    clauses.Add(!ReferenceEquals(cb, c.Body) ? c with { Body = cb } : c);
                }
                return changed ? ws with { Expression = subject, Clauses = clauses } : stmt;
            }

            case ReturnStatement { Value: not null } ret:
            {
                Expression v = LowerExpression(ret.Value);
                return ReferenceEquals(v, ret.Value) ? stmt : ret with { Value = v };
            }

            case AssignmentStatement assign:
            {
                Expression val = LowerExpression(assign.Value);
                return ReferenceEquals(val, assign.Value)
                    ? stmt
                    : assign with { Value = val };
            }

            case DeclarationStatement { Declaration: VariableDeclaration vd } ds
                when vd.Initializer != null:
            {
                Expression init = LowerExpression(vd.Initializer);
                if (ReferenceEquals(init, vd.Initializer)) return stmt;
                return ds with { Declaration = vd with { Initializer = init } };
            }

            case ExpressionStatement es:
            {
                Expression e = LowerExpression(es.Expression);
                return ReferenceEquals(e, es.Expression) ? stmt : es with { Expression = e };
            }

            case DiscardStatement ds:
            {
                Expression e = LowerExpression(ds.Expression);
                return ReferenceEquals(e, ds.Expression) ? stmt : ds with { Expression = e };
            }

            case ThrowStatement ts:
            {
                Expression e = LowerExpression(ts.Error);
                return ReferenceEquals(e, ts.Error) ? stmt : ts with { Error = e };
            }

            case BecomesStatement bs:
            {
                Expression v = LowerExpression(bs.Value);
                return ReferenceEquals(v, bs.Value) ? stmt : bs with { Value = v };
            }

            case UsingStatement us:
            {
                Statement body = LowerStatement(us.Body);
                return ReferenceEquals(body, us.Body) ? stmt : us with { Body = body };
            }

            case DangerStatement danger:
            {
                Statement newBody = LowerStatement(danger.Body);
                if (!ReferenceEquals(newBody, danger.Body) && newBody is BlockStatement bs2)
                    return danger with { Body = bs2 };
                return stmt;
            }

            default:
                return stmt;
        }
    }

    // ── Expression walker ──────────────────────────────────────────────────

    private Expression LowerExpression(Expression expr)
    {
        // Leaf nodes: nothing to fold or recurse into.
        if (expr is LiteralExpression or TypeIdExpression) return expr;

        // ── BuilderService constant-call folding ──────────────────────────
        if (expr is CallExpression
            {
                Callee: MemberExpression { PropertyName: var routineName } bsCallee,
                Arguments: { Count: 0 }
            } bsCall
            && _foldableRoutines.Contains(routineName))
        {
            TypeInfo? receiverType = ResolveReceiverType(bsCallee.Object);
            if (receiverType != null)
            {
                Expression? folded = FoldBsCall(routineName, receiverType, bsCall.Location);
                if (folded != null) return folded;
            }
        }

        // ── Structural recursion ──────────────────────────────────────────
        switch (expr)
        {
            case BinaryExpression bin:
            {
                Expression l = LowerExpression(bin.Left);
                Expression r = LowerExpression(bin.Right);
                return ReferenceEquals(l, bin.Left) && ReferenceEquals(r, bin.Right)
                    ? expr : bin with { Left = l, Right = r };
            }

            case UnaryExpression un:
            {
                Expression o = LowerExpression(un.Operand);
                return ReferenceEquals(o, un.Operand) ? expr : un with { Operand = o };
            }

            case CallExpression call:
            {
                Expression callee = LowerExpression(call.Callee);
                List<Expression> args = LowerExpressionList(call.Arguments);
                bool changed = !ReferenceEquals(callee, call.Callee)
                               || !ReferenceEquals(args, call.Arguments);
                return changed ? call with { Callee = callee, Arguments = args } : expr;
            }

            case NamedArgumentExpression named:
            {
                Expression v = LowerExpression(named.Value);
                return ReferenceEquals(v, named.Value) ? expr : named with { Value = v };
            }

            case MemberExpression mem:
            {
                Expression o = LowerExpression(mem.Object);
                return ReferenceEquals(o, mem.Object) ? expr : mem with { Object = o };
            }

            case OptionalMemberExpression omem:
            {
                Expression o = LowerExpression(omem.Object);
                return ReferenceEquals(o, omem.Object) ? expr : omem with { Object = o };
            }

            case IndexExpression idx:
            {
                Expression o = LowerExpression(idx.Object);
                Expression i = LowerExpression(idx.Index);
                bool changed = !ReferenceEquals(o, idx.Object) || !ReferenceEquals(i, idx.Index);
                return changed ? idx with { Object = o, Index = i } : expr;
            }

            case SliceExpression slice:
            {
                Expression o = LowerExpression(slice.Object);
                Expression s = LowerExpression(slice.Start);
                Expression e = LowerExpression(slice.End);
                bool changed = !ReferenceEquals(o, slice.Object)
                               || !ReferenceEquals(s, slice.Start)
                               || !ReferenceEquals(e, slice.End);
                return changed ? slice with { Object = o, Start = s, End = e } : expr;
            }

            case TypeConversionExpression conv:
            {
                Expression e = LowerExpression(conv.Expression);
                return ReferenceEquals(e, conv.Expression) ? expr : conv with { Expression = e };
            }

            case StealExpression steal:
            {
                Expression o = LowerExpression(steal.Operand);
                return ReferenceEquals(o, steal.Operand) ? expr : steal with { Operand = o };
            }

            case GenericMethodCallExpression gmc:
            {
                Expression obj = LowerExpression(gmc.Object);
                List<Expression> args = LowerExpressionList(gmc.Arguments);
                bool changed = !ReferenceEquals(obj, gmc.Object)
                               || !ReferenceEquals(args, gmc.Arguments);
                return changed ? gmc with { Object = obj, Arguments = args } : expr;
            }

            case GenericMemberExpression gmem:
            {
                Expression o = LowerExpression(gmem.Object);
                return ReferenceEquals(o, gmem.Object) ? expr : gmem with { Object = o };
            }

            case IsPatternExpression ip:
            {
                Expression e = LowerExpression(ip.Expression);
                return ReferenceEquals(e, ip.Expression) ? expr : ip with { Expression = e };
            }

            case FlagsTestExpression flags:
            {
                Expression s = LowerExpression(flags.Subject);
                return ReferenceEquals(s, flags.Subject) ? expr : flags with { Subject = s };
            }

            case ChainedComparisonExpression chain:
            {
                List<Expression> operands = LowerExpressionList(chain.Operands);
                return ReferenceEquals(operands, chain.Operands)
                    ? expr : chain with { Operands = operands };
            }

            case CompoundAssignmentExpression comp:
            {
                Expression target = LowerExpression(comp.Target);
                Expression value = LowerExpression(comp.Value);
                bool changed = !ReferenceEquals(target, comp.Target)
                               || !ReferenceEquals(value, comp.Value);
                return changed ? comp with { Target = target, Value = value } : expr;
            }

            case RangeExpression range:
            {
                Expression start = LowerExpression(range.Start);
                Expression end = LowerExpression(range.End);
                Expression? step = range.Step != null ? LowerExpression(range.Step) : null;
                bool changed = !ReferenceEquals(start, range.Start)
                               || !ReferenceEquals(end, range.End)
                               || !ReferenceEquals(step, range.Step);
                return changed ? range with { Start = start, End = end, Step = step } : expr;
            }

            case ConditionalExpression cond:
            {
                Expression c = LowerExpression(cond.Condition);
                Expression t = LowerExpression(cond.TrueExpression);
                Expression f = LowerExpression(cond.FalseExpression);
                bool changed = !ReferenceEquals(c, cond.Condition)
                               || !ReferenceEquals(t, cond.TrueExpression)
                               || !ReferenceEquals(f, cond.FalseExpression);
                return changed
                    ? cond with { Condition = c, TrueExpression = t, FalseExpression = f }
                    : expr;
            }

            case TupleLiteralExpression tuple:
            {
                List<Expression> elems = LowerExpressionList(tuple.Elements);
                return ReferenceEquals(elems, tuple.Elements)
                    ? expr : tuple with { Elements = elems };
            }

            case ListLiteralExpression list:
            {
                List<Expression> elems = LowerExpressionList(list.Elements);
                return ReferenceEquals(elems, list.Elements)
                    ? expr : list with { Elements = elems };
            }

            case SetLiteralExpression set:
            {
                List<Expression> elems = LowerExpressionList(set.Elements);
                return ReferenceEquals(elems, set.Elements)
                    ? expr : set with { Elements = elems };
            }

            case DictLiteralExpression dict:
            {
                bool changed = false;
                var pairs = new List<(Expression Key, Expression Value)>(dict.Pairs.Count);
                foreach ((Expression k, Expression v) in dict.Pairs)
                {
                    Expression lk = LowerExpression(k);
                    Expression lv = LowerExpression(v);
                    pairs.Add((lk, lv));
                    if (!ReferenceEquals(lk, k) || !ReferenceEquals(lv, v)) changed = true;
                }
                return changed ? dict with { Pairs = pairs } : expr;
            }

            case CreatorExpression creator:
            {
                bool changed = false;
                var members = new List<(string Name, Expression Value)>(
                    creator.MemberVariables.Count);
                foreach ((string name, Expression value) in creator.MemberVariables)
                {
                    Expression v = LowerExpression(value);
                    members.Add((name, v));
                    if (!ReferenceEquals(v, value)) changed = true;
                }
                return changed ? creator with { MemberVariables = members } : expr;
            }

            case InsertedTextExpression fstr:
            {
                bool changed = false;
                var parts = new List<InsertedTextPart>(fstr.Parts.Count);
                foreach (InsertedTextPart part in fstr.Parts)
                {
                    if (part is ExpressionPart ep)
                    {
                        Expression e = LowerExpression(ep.Expression);
                        if (!ReferenceEquals(e, ep.Expression))
                        {
                            parts.Add(ep with { Expression = e });
                            changed = true;
                            continue;
                        }
                    }
                    parts.Add(part);
                }
                return changed ? fstr with { Parts = parts } : expr;
            }

            case BackIndexExpression back:
            {
                Expression o = LowerExpression(back.Operand);
                return ReferenceEquals(o, back.Operand) ? expr : back with { Operand = o };
            }

            case BlockExpression block:
            {
                Expression v = LowerExpression(block.Value);
                return ReferenceEquals(v, block.Value) ? expr : block with { Value = v };
            }

            case WaitforExpression wf:
            {
                Expression o = LowerExpression(wf.Operand);
                Expression? timeout = wf.Timeout != null ? LowerExpression(wf.Timeout) : null;
                bool changed = !ReferenceEquals(o, wf.Operand)
                               || !ReferenceEquals(timeout, wf.Timeout);
                return changed ? wf with { Operand = o, Timeout = timeout } : expr;
            }

            case CarrierPayloadExpression cpe:
            {
                Expression c = LowerExpression(cpe.Carrier);
                return ReferenceEquals(c, cpe.Carrier) ? expr : cpe with { Carrier = c };
            }

            default:
                return expr;
        }
    }

    private List<Expression> LowerExpressionList(List<Expression> list)
    {
        bool changed = false;
        var result = new List<Expression>(list.Count);
        foreach (Expression e in list)
        {
            Expression le = LowerExpression(e);
            result.Add(le);
            if (!ReferenceEquals(le, e)) changed = true;
        }
        return changed ? result : list;
    }

    // ── Type resolution ────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the <see cref="TypeInfo"/> for the receiver of a potential BS call.
    /// Returns null when the receiver is an unbound generic type parameter (can't fold).
    /// </summary>
    private TypeInfo? ResolveReceiverType(Expression receiver)
    {
        // 1. Use the expression's ResolvedType if it's a concrete (non-generic-param) type.
        if (receiver.ResolvedType is { } rt && rt is not GenericParameterTypeInfo)
            return rt;

        // 1b. ResolvedType is a generic param — look it up in the current body's TypeSubs
        //     (e.g. T in List[T].$getitem! body with TypeSubs {T → Core.Byte, I → Core.S64}).
        if (receiver.ResolvedType is GenericParameterTypeInfo gp
            && _currentTypeSubs != null
            && _currentTypeSubs.TryGetValue(gp.Name, out TypeInfo? subFromSubs))
            return subFromSubs;

        // 2. Look up an identifier as a type name (handles post-monomorphization identifiers
        //    like IdentifierExpression("Core.Byte") produced by GenericAstRewriter).
        if (receiver is IdentifierExpression { Name: var idName })
        {
            // If identifier name is a generic param name, resolve via TypeSubs first.
            if (_currentTypeSubs != null
                && _currentTypeSubs.TryGetValue(idName, out TypeInfo? subByName))
                return subByName;
            return _registry.LookupType(idName);
        }

        // 3. TypeExpression used as the receiver (e.g. in type-level method calls).
        if (receiver is TypeExpression { Name: var teName })
        {
            if (_currentTypeSubs != null
                && _currentTypeSubs.TryGetValue(teName, out TypeInfo? teSub))
                return teSub;
            return _registry.LookupType(teName);
        }

        return null;
    }

    // ── Constant computation ───────────────────────────────────────────────

    /// <summary>
    /// Returns a folded <see cref="LiteralExpression"/> for the given BuilderService constant
    /// routine on <paramref name="type"/>, or null if the routine is not supported or required
    /// types are not yet registered.
    /// </summary>
    private Expression? FoldBsCall(string routineName, TypeInfo type, SourceLocation loc)
    {
        EnsureTypes();
        switch (routineName)
        {
            case "data_size" when _u64Type != null && _byteSizeType != null:
                return MakeByteSizeCreator(CalculateDataSizeForType(type), _u64Type, _byteSizeType, loc);

            case "type_id" when _u64Type != null:
                return MakeLiteralU64(TypeIdHelper.ComputeTypeId(type.FullName), _u64Type, loc);

            case "type_name" when _textType != null:
                return MakeLiteralText(type.Name, _textType, loc);

            case "module_name" when _textType != null:
                return MakeLiteralText(type.Module ?? "", _textType, loc);

            case "full_type_name" when _textType != null:
            {
                string full = string.IsNullOrEmpty(type.Module)
                    ? type.Name
                    : $"{type.Module}.{type.Name}";
                return MakeLiteralText(full, _textType, loc);
            }

            case "member_variable_count" when _s64Type != null:
            {
                long count = type switch
                {
                    RecordTypeInfo r => r.MemberVariables.Count,
                    EntityTypeInfo e => e.MemberVariables.Count,
                    CrashableTypeInfo c => c.MemberVariables.Count,
                    TupleTypeInfo t => t.MemberVariables.Count,
                    ChoiceTypeInfo ch => ch.Cases.Count,
                    FlagsTypeInfo f => f.Members.Count,
                    VariantTypeInfo v => v.Members.Count,
                    _ => 0L
                };
                return MakeLiteralS64(count, _s64Type, loc);
            }

            case "is_generic" when _boolType != null:
            {
                bool isGen = type.IsGenericDefinition;
                return new LiteralExpression(
                    Value: isGen,
                    LiteralType: isGen ? TokenType.True : TokenType.False,
                    Location: loc) { ResolvedType = _boolType };
            }

            case "type_kind":
            {
                TypeInfo? tkType = _registry.LookupType(name: "TypeKind");
                if (tkType is not ChoiceTypeInfo tkChoice) return null;
                string caseName = type.Category switch
                {
                    TypeCategory.Record => "RECORD",
                    TypeCategory.Entity => "ENTITY",
                    TypeCategory.Crashable => "CRASHABLE",
                    TypeCategory.Choice => "CHOICE",
                    TypeCategory.Variant => "VARIANT",
                    TypeCategory.Flags => "FLAGS",
                    TypeCategory.Routine => "ROUTINE",
                    TypeCategory.Protocol => "PROTOCOL",
                    _ => "RECORD"
                };
                ChoiceCaseInfo? found = tkChoice.Cases.FirstOrDefault(c => c.Name == caseName);
                if (found == null) return null;
                // Emit as S64 literal with ResolvedType = TypeKind (choice), mirrors WiredRoutinePass.
                return MakeLiteralS64(found.ComputedValue, tkType, loc);
            }

            default:
                return null;
        }
    }

    private void EnsureTypes()
    {
        _u64Type ??= _registry.LookupType(name: "U64");
        _s64Type ??= _registry.LookupType(name: "S64");
        _textType ??= _registry.LookupType(name: "Text");
        _boolType ??= _registry.LookupType(name: "Bool");
        _byteSizeType ??= _registry.LookupType(name: "ByteSize");
    }

    // ── Literal factory helpers ────────────────────────────────────────────

    private static LiteralExpression MakeLiteralU64(ulong value, TypeInfo type, SourceLocation loc) =>
        new(Value: value, LiteralType: TokenType.U64Literal, Location: loc)
        { ResolvedType = type };

    private static CreatorExpression MakeByteSizeCreator(ulong value, TypeInfo u64Type,
        TypeInfo byteSizeType, SourceLocation loc) =>
        MakeByteSizeCreatorPublic(value, u64Type, byteSizeType, loc);

    internal static CreatorExpression MakeByteSizeCreatorPublic(ulong value, TypeInfo u64Type,
        TypeInfo byteSizeType, SourceLocation loc)
    {
        LiteralExpression u64Lit = MakeLiteralU64(value, u64Type, loc);
        return new CreatorExpression(
            TypeName: "ByteSize",
            TypeArguments: null,
            MemberVariables: [("value", u64Lit)],
            Location: loc)
        { ResolvedType = byteSizeType };
    }

    private static LiteralExpression MakeLiteralS64(long value, TypeInfo type, SourceLocation loc) =>
        new(Value: value, LiteralType: TokenType.S64Literal, Location: loc)
        { ResolvedType = type };

    private static LiteralExpression MakeLiteralText(string value, TypeInfo type, SourceLocation loc) =>
        new(Value: value, LiteralType: TokenType.TextLiteral, Location: loc)
        { ResolvedType = type };

    // ── Data size computation (mirrors WiredRoutinePass.CalculateDataSizeForType) ──

    /// <summary>
    /// Returns the byte size of <paramref name="type"/> as seen by collection pointer arithmetic.
    /// Mirrors <c>WiredRoutinePass.CalculateDataSizeForType</c> — keep in sync.
    /// </summary>
    internal static ulong CalculateDataSizeForType(TypeInfo type) => type switch
    {
        RecordTypeInfo { HasDirectBackendType: true } r => LlvmBackendTypeSize(r.BackendType!),
        RecordTypeInfo r => (ulong)(r.MemberVariables.Count * 8),
        ChoiceTypeInfo => 4,    // i32
        FlagsTypeInfo => 8,     // i64
        TupleTypeInfo t => (ulong)(t.ElementTypes.Count * 8),
        EntityTypeInfo => 8,    // heap pointer
        CrashableTypeInfo => 8, // heap pointer
        VariantTypeInfo v => (ulong)((v.Members.Count + 1) * 8), // tag + largest payload
        _ => 0
    };

    /// <summary>
    /// Returns the byte size of an LLVM scalar type string from an <c>@llvm("...")</c> annotation.
    /// Mirrors <c>WiredRoutinePass.LlvmBackendTypeSize</c> — keep in sync.
    /// </summary>
    private static ulong LlvmBackendTypeSize(string llvmType) => llvmType.Trim() switch
    {
        "void" => 0,
        "i1" or "i8" => 1,
        "i16" or "half" => 2,
        "i32" or "float" => 4,
        "i64" or "double" or "ptr" => 8,
        "i128" or "fp128" => 16,
        _ => 8 // unknown — assume pointer-sized
    };
}