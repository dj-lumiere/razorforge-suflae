using System.Collections.Generic;
using System.Linq;
using Compiler.Resolution;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Postprocessing.Passes;

/// <summary>
/// Rewrites Referring[T]/Controlling[T] parameter types to the inner entity T
/// and injects implicit .$refer()/.$control() coercion at matching call-site arguments.
/// After this pass runs, no marker-protocol types remain in routine signatures or
/// argument expression types — bodies see entity T directly, and call sites pass
/// in-flight ?T produced by the wrapper's $refer/$control implementation.
/// </summary>
internal sealed class MarkerProtocolDesugarPass
{
    private enum MarkerKind { None, Refer, Control }

    private readonly TypeRegistry _registry;
    private readonly Dictionary<RoutineInfo, List<ParamInfo>> _snapshot = new();
    private readonly Dictionary<string, Statement>? _variantBodies;
    private readonly Dictionary<string, Statement>? _synthesizedBodies;
    private readonly ProtocolTypeInfo? _referringProto;
    private readonly ProtocolTypeInfo? _controllingProto;

    private readonly record struct ParamInfo(int Index, string Name, MarkerKind Kind, TypeInfo InnerType);

    internal MarkerProtocolDesugarPass(PostprocessingContext ctx)
    {
        _registry = ctx.Registry;
        _variantBodies = ctx.VariantBodies;
        _synthesizedBodies = ctx.SynthesizedBodies;
        _referringProto = _registry.LookupType("Referring") as ProtocolTypeInfo;
        _controllingProto = _registry.LookupType("Controlling") as ProtocolTypeInfo;
        Snapshot();
    }

    private MarkerKind ClassifyMarker(TypeInfo? t)
    {
        if (t is not ProtocolTypeInfo p) return MarkerKind.None;
        ProtocolTypeInfo def = p.GenericDefinition ?? p;
        if (ReferenceEquals(def, _controllingProto)) return MarkerKind.Control;
        if (ReferenceEquals(def, _referringProto)) return MarkerKind.Refer;
        return MarkerKind.None;
    }

    private static TypeInfo? GetInnerT(TypeInfo t)
    {
        if (t is ProtocolTypeInfo { TypeArguments: { Count: > 0 } args })
            return args[0];
        return null;
    }

    private void Snapshot()
    {
        SnapshotFrom(_registry.GetAllRoutines());
        SnapshotFrom(_registry.GetAllRoutineResolutions());
    }

    private void SnapshotFrom(IEnumerable<RoutineInfo> routines)
    {
        foreach (RoutineInfo r in routines)
        {
            if (_snapshot.ContainsKey(r)) continue;
            List<ParamInfo>? markers = null;
            for (int i = 0; i < r.Parameters.Count; i++)
            {
                ParameterInfo p = r.Parameters[i];
                MarkerKind k = ClassifyMarker(p.Type);
                if (k == MarkerKind.None) continue;
                TypeInfo? inner = GetInnerT(p.Type);
                if (inner == null) continue;
                markers ??= [];
                markers.Add(new ParamInfo(i, p.Name, k, inner));
            }
            if (markers != null) _snapshot[r] = markers;
        }
    }

    public void Run(Program program)
    {
        foreach (ISyntaxTreeNode decl in program.Declarations)
        {
            switch (decl)
            {
                case RoutineDeclaration r:
                    WalkStatement(r.Body);
                    break;
                case EntityDeclaration e:
                    foreach (SyntaxTree.Declaration m in e.Members)
                        if (m is RoutineDeclaration rr) WalkStatement(rr.Body);
                    break;
                case RecordDeclaration rec:
                    foreach (SyntaxTree.Declaration m in rec.Members)
                        if (m is RoutineDeclaration rr) WalkStatement(rr.Body);
                    break;
                case CrashableDeclaration cr:
                    foreach (SyntaxTree.Declaration m in cr.Members)
                        if (m is RoutineDeclaration rr) WalkStatement(rr.Body);
                    break;
            }
        }
    }

    public void RunOnVariantBodies()
    {
        if (_variantBodies == null) return;
        foreach (Statement body in _variantBodies.Values) WalkStatement(body);
    }

    public void RunOnSynthesizedBodies()
    {
        if (_synthesizedBodies == null) return;
        foreach (Statement body in _synthesizedBodies.Values) WalkStatement(body);
    }

    public void RunOnStatements(IEnumerable<Statement> statements)
    {
        foreach (Statement s in statements) WalkStatement(s);
    }

    /// <summary>
    /// Rewrites every snapshotted routine's marker-typed parameters to their inner T.
    /// Run after all call-site injection is complete.
    /// </summary>
    public void RewriteAllSignatures()
    {
        foreach ((RoutineInfo r, List<ParamInfo> markers) in _snapshot)
        {
            foreach (ParamInfo m in markers)
            {
                if (m.Index >= r.Parameters.Count) continue;
                ParameterInfo p = r.Parameters[m.Index];
                if (ClassifyMarker(p.Type) == MarkerKind.None) continue;
                r.Parameters[m.Index] = p.WithSubstitutedType(m.InnerType);
            }
        }
    }

    /// <summary>
    /// Rewrites Referring[T]/Controlling[T] parameter type expressions on AST routine
    /// declarations to inner T. Codegen looks up routines from the AST by parameter type
    /// name, so the AST must agree with the RoutineInfo signature after RewriteAllSignatures.
    /// </summary>
    public static void RewriteAstSignatures(Program program)
    {
        foreach (ISyntaxTreeNode decl in program.Declarations)
        {
            switch (decl)
            {
                case RoutineDeclaration r:
                    RewriteParams(r);
                    break;
                case EntityDeclaration e:
                    foreach (SyntaxTree.Declaration m in e.Members)
                        if (m is RoutineDeclaration rr) RewriteParams(rr);
                    break;
                case RecordDeclaration rec:
                    foreach (SyntaxTree.Declaration m in rec.Members)
                        if (m is RoutineDeclaration rr) RewriteParams(rr);
                    break;
                case CrashableDeclaration cr:
                    foreach (SyntaxTree.Declaration m in cr.Members)
                        if (m is RoutineDeclaration rr) RewriteParams(rr);
                    break;
            }
        }
    }

    private static void RewriteParams(RoutineDeclaration r)
    {
        for (int i = 0; i < r.Parameters.Count; i++)
        {
            Parameter p = r.Parameters[i];
            TypeExpression? inner = UnwrapMarker(p.Type);
            if (inner != null) r.Parameters[i] = p with { Type = inner };
        }
    }

    private static TypeExpression? UnwrapMarker(TypeExpression? t)
    {
        if (t == null) return null;
        if ((t.Name == "Referring" || t.Name == "Controlling")
            && t.GenericArguments is { Count: > 0 } args)
            return args[0];
        return null;
    }

    private TypeInfo? UnwrapMarkerTypeInfo(TypeInfo? t)
    {
        if (t is not ProtocolTypeInfo p) return null;
        ProtocolTypeInfo def = p.GenericDefinition ?? p;
        if (!ReferenceEquals(def, _referringProto) && !ReferenceEquals(def, _controllingProto))
            return null;
        if (p.TypeArguments is { Count: > 0 } args) return args[0];
        return null;
    }

    // -----------------------------------------------------------------------------

    private void WalkStatement(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement block:
                foreach (Statement s in block.Statements) WalkStatement(s);
                break;
            case IfStatement ifs:
                WalkExpression(ifs.Condition);
                WalkStatement(ifs.ThenStatement);
                if (ifs.ElseStatement != null) WalkStatement(ifs.ElseStatement);
                break;
            case WhileStatement w:
                WalkExpression(w.Condition);
                WalkStatement(w.Body);
                break;
            case LoopStatement loop:
                WalkStatement(loop.Body);
                break;
            case ForStatement f:
                WalkExpression(f.Iterable);
                WalkStatement(f.Body);
                break;
            case WhenStatement ws:
                WalkExpression(ws.Expression);
                foreach (WhenClause c in ws.Clauses) WalkStatement(c.Body);
                break;
            case ReturnStatement { Value: not null } ret:
                WalkExpression(ret.Value);
                break;
            case AssignmentStatement assign:
                WalkExpression(assign.Target);
                WalkExpression(assign.Value);
                break;
            case DeclarationStatement { Declaration: VariableDeclaration vd } when vd.Initializer != null:
                WalkExpression(vd.Initializer);
                break;
            case ExpressionStatement es:
                WalkExpression(es.Expression);
                break;
            case DiscardStatement ds:
                WalkExpression(ds.Expression);
                break;
            case ThrowStatement ts:
                WalkExpression(ts.Error);
                break;
            case VariantReturnStatement { Value: not null } vrs:
                WalkExpression(vrs.Value);
                break;
            case BecomesStatement bs:
                WalkExpression(bs.Value);
                break;
            case UsingStatement us:
                WalkStatement(us.Body);
                break;
            case DangerStatement danger:
                WalkStatement(danger.Body);
                break;
        }
    }

    // Walk for side effects: retags expr.ResolvedType from Referring[T]/Controlling[T]
    // to inner T so codegen sees no marker types anywhere in expression metadata.
    private void WalkExpression(Expression expr)
    {
        TypeInfo? unwrapped = UnwrapMarkerTypeInfo(expr.ResolvedType);
        if (unwrapped != null) expr.ResolvedType = unwrapped;
        switch (expr)
        {
            case CallExpression call:
                WalkExpression(call.Callee);
                foreach (Expression a in call.Arguments) WalkExpression(a);
                break;
            case BinaryExpression bin:
                WalkExpression(bin.Left);
                WalkExpression(bin.Right);
                break;
            case UnaryExpression un:
                WalkExpression(un.Operand);
                break;
            case MemberExpression mem:
                WalkExpression(mem.Object);
                break;
            case OptionalMemberExpression om:
                WalkExpression(om.Object);
                break;
            case NamedArgumentExpression named:
                WalkExpression(named.Value);
                break;
            case IndexExpression idx:
                WalkExpression(idx.Object);
                WalkExpression(idx.Index);
                break;
            case SliceExpression slc:
                WalkExpression(slc.Object);
                WalkExpression(slc.Start);
                WalkExpression(slc.End);
                break;
            case TypeConversionExpression conv:
                WalkExpression(conv.Expression);
                break;
            case StealExpression st:
                WalkExpression(st.Operand);
                break;
            case GenericMethodCallExpression gmc:
                WalkExpression(gmc.Object);
                foreach (Expression a in gmc.Arguments) WalkExpression(a);
                break;
            case GenericMemberExpression gm:
                WalkExpression(gm.Object);
                break;
            case IsPatternExpression ip:
                WalkExpression(ip.Expression);
                break;
            case FlagsTestExpression ft:
                WalkExpression(ft.Subject);
                break;
            case ChainedComparisonExpression cc:
                foreach (Expression op in cc.Operands) WalkExpression(op);
                break;
            case CompoundAssignmentExpression ca:
                WalkExpression(ca.Target);
                WalkExpression(ca.Value);
                break;
            case RangeExpression rg:
                WalkExpression(rg.Start);
                WalkExpression(rg.End);
                if (rg.Step != null) WalkExpression(rg.Step);
                break;
            case ConditionalExpression cd:
                WalkExpression(cd.Condition);
                WalkExpression(cd.TrueExpression);
                WalkExpression(cd.FalseExpression);
                break;
            case TupleLiteralExpression tl:
                foreach (Expression e in tl.Elements) WalkExpression(e);
                break;
            case ListLiteralExpression ll:
                foreach (Expression e in ll.Elements) WalkExpression(e);
                break;
            case SetLiteralExpression sl:
                foreach (Expression e in sl.Elements) WalkExpression(e);
                break;
            case DictLiteralExpression dl:
                foreach ((Expression k, Expression v) in dl.Pairs)
                {
                    WalkExpression(k);
                    WalkExpression(v);
                }
                break;
            case CreatorExpression ce:
                foreach ((_, Expression v) in ce.MemberVariables) WalkExpression(v);
                break;
            case InsertedTextExpression it:
                foreach (InsertedTextPart part in it.Parts)
                    if (part is ExpressionPart ep) WalkExpression(ep.Expression);
                break;
        }
    }

    private void InjectCoercionsForCall(CallExpression call)
    {
        RoutineInfo? target = call.ResolvedRoutine;
        if (target == null) return;
        if (!_snapshot.TryGetValue(target, out List<ParamInfo>? markers)) return;

        for (int argIdx = 0; argIdx < call.Arguments.Count; argIdx++)
        {
            Expression arg = call.Arguments[argIdx];
            int paramIdx;
            Expression valueExpr;
            if (arg is NamedArgumentExpression named)
            {
                paramIdx = -1;
                for (int j = 0; j < target.Parameters.Count; j++)
                {
                    if (target.Parameters[j].Name == named.Name) { paramIdx = j; break; }
                }
                if (paramIdx < 0) continue;
                valueExpr = named.Value;
            }
            else
            {
                paramIdx = argIdx;
                valueExpr = arg;
            }

            ParamInfo? marker = null;
            foreach (ParamInfo m in markers)
                if (m.Index == paramIdx) { marker = m; break; }
            if (marker is not { } mk) continue;

            // Skip if arg already coerces to inner T explicitly.
            if (valueExpr is CallExpression { Callee: MemberExpression mem }
                && (mem.PropertyName == "$refer" || mem.PropertyName == "$control"))
                continue;

            string methodName = mk.Kind == MarkerKind.Control ? "$control" : "$refer";
            var coerced = new CallExpression(
                Callee: new MemberExpression(
                    Object: valueExpr,
                    PropertyName: methodName,
                    Location: valueExpr.Location),
                Arguments: [],
                Location: valueExpr.Location)
            {
                ResolvedType = mk.InnerType,
                IsInFlight = true
            };

            if (arg is NamedArgumentExpression na)
                call.Arguments[argIdx] = na with { Value = coerced };
            else
                call.Arguments[argIdx] = coerced;
        }
    }
}