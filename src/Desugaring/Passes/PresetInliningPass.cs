using System.Collections.Generic;
using System.Linq;
using Compiler.Resolution;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Desugaring.Passes;

/// <summary>
/// Substitutes <c>IdentifierExpression</c> nodes for preset constants with the
/// preset's literal value expression.
///
/// <para>Must run <em>first</em> in the per-file desugaring pipeline so that all
/// subsequent passes (operator lowering, f-string lowering, etc.) operate on
/// concrete literal values rather than named identifiers. For example,</para>
/// <code>
/// preset RUNS: S64 = 20_s64
/// for _ in 0..RUNS ?? for _ in 0..S64(20)
/// </code>
/// <para>Needs <see cref="VariableInfo.PresetValue"/> to be set on preset entries in
/// the <see cref="TypeRegistry"/>, which happens during
/// <c>CollectPresetDeclaration</c> in Phase 3.</para>
/// </summary>
internal sealed class PresetInliningPass(DesugaringContext ctx)
{
    // Presets are GLOBAL by default (public constants like `S64_MAX`/`DECIMAL_PI` are part of the Core
    // prelude and usable everywhere). A `secret preset` is FILE-PRIVATE: inlinable only inside the file
    // that declares it. `_ownPresetNames` is the set of preset names declared in the file currently
    // being lowered — a secret preset from another file is NOT in it, so it is left un-inlined and a bare
    // identifier that merely shares a name (e.g. a user `record B` vs a stdlib `secret preset B`) resolves
    // as its own declaration. Null means "no scoping" (compiler-synthesized variant bodies).
    private Dictionary<string, PresetDeclaration>? _ownPresets;

    /// <summary>Every <c>preset</c> declared in the current file, keyed by name (public or secret).</summary>
    private static Dictionary<string, PresetDeclaration> CollectOwnPresets(Program program)
    {
        var map = new Dictionary<string, PresetDeclaration>(comparer: System.StringComparer.Ordinal);
        foreach (ISyntaxTreeNode decl in program.Declarations)
            if (decl is PresetDeclaration preset)
                map[key: preset.Name] = preset;
        return map;
    }

    /// <summary>
    /// Runs this compiler phase over its configured input.
    /// </summary>
    public void Run(Program program)
    {
        _ownPresets = CollectOwnPresets(program: program);
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
    /// Inlines presets in all synthesized bodies in <see cref="DesugaringContext.VariantBodies"/>.
    /// Called from <see cref="DesugaringPipeline.RunGlobal"/> after variant bodies are generated.
    /// </summary>
    public void RunOnVariantBodies()
    {
        // Synthesized variant bodies are compiler-generated and not tied to a source file. They never
        // reference file-private secret presets, so an empty own-set is correct (public presets still
        // inline via the registry).
        _ownPresets = new Dictionary<string, PresetDeclaration>(comparer: System.StringComparer.Ordinal);
        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            Statement body = ctx.VariantBodies[key];
            Statement lowered = LowerStatement(body);
            if (!ReferenceEquals(lowered, body))
                ctx.VariantBodies[key] = lowered;
        }
    }

    /// <summary>
    /// Lower member list as part of this compiler phase.
    /// </summary>
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

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Lower statement as part of this compiler phase.
    /// </summary>
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

            case VariantReturnStatement { Value: not null } variantRet:
            {
                Expression v = LowerExpression(variantRet.Value);
                return ReferenceEquals(v, variantRet.Value)
                    ? stmt
                    : variantRet with { Value = v };
            }

            case AssignmentStatement assign:
            {
                Expression val = LowerExpression(assign.Value);
                return ReferenceEquals(val, assign.Value)
                    ? stmt
                    : assign with { Value = val };
            }

            case DeclarationStatement { Declaration: VariableDeclaration { Initializer: not null } vd } ds:
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
                Statement? fb = us.FallbackBody != null ? LowerStatement(us.FallbackBody) : null;
                return ReferenceEquals(body, us.Body) && ReferenceEquals(fb, us.FallbackBody)
                    ? stmt
                    : us with { Body = body, FallbackBody = fb };
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

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Lower expression as part of this compiler phase.
    /// </summary>
    private Expression LowerExpression(Expression expr)
    {
        // -----------------------------------------------------------------------------
        if (expr is IdentifierExpression id)
        {
            VariableInfo? v = ctx.Registry.LookupVariable(id.Name);

            // A `secret preset` is file-private: only inline it inside the file that declares it. A
            // cross-file reference is left un-inlined — since it stays a bare identifier that resolves
            // to a preset, the backend-entry validator flags it (RF-S958), so a secret constant cannot
            // silently be used from another file. Public presets always inline.
            if (v is { IsPreset: true, IsSecret: true }
                && _ownPresets is not null
                && !_ownPresets.ContainsKey(key: id.Name))
            {
                return expr;
            }

            if (v is { IsPreset: true, PresetValue: not null })
            {
                // Aggregate (Array[T,N]) presets are NOT inlined: substituting the whole list
                // literal at every use site rebuilds the array per reference (codegen lowered it to
                // a heap List rebuilt element-by-element — the fun_bench OOM). Keep the identifier so
                // codegen emits a single `@preset.*` constant global and indexes into it.
                if (v.IsPresettableAggregate)
                    return expr;

                // Carry the Phase-5 ResolvedType from the identifier onto the inlined value.
                // This ensures operator-lowering and other subsequent passes see the correct type.
                TypeInfo? resolvedType = id.ResolvedType ?? v.PresetValue.ResolvedType;
                return v.PresetValue is LiteralExpression lit
                    ? lit with { ResolvedType = resolvedType }
                    : v.PresetValue;
            }
            return expr;
        }

        // -----------------------------------------------------------------------------
        if (expr is LiteralExpression or TypeExpression or TypeIdExpression)
            return expr;

        // -----------------------------------------------------------------------------
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
                // A bare-identifier callee is a TYPE constructor or a routine name — never a preset
                // value (presets are scalars/aggregates, not callable). Inlining it would rewrite
                // `Foo(a: 1)` into `<literal>(a: 1)` when a preset happens to share the name `Foo`
                // (e.g. a user `record B` colliding with stdlib `preset B`). So skip the callee when it
                // is a bare identifier; still lower a member/other callee (e.g. `SOME_PRESET.bit_count()`
                // whose Object may be a preset) and the argument list.
                Expression callee = call.Callee is IdentifierExpression
                    ? call.Callee
                    : LowerExpression(call.Callee);
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

            // LambdaExpression, WithExpression, WhenExpression, DependentWaitforExpression,
            // CarrierPayloadExpression: presets are extremely unlikely in these positions.
            default:
                return expr;
        }
    }

    /// <summary>
    /// Lower expression list as part of this compiler phase.
    /// </summary>
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
}
