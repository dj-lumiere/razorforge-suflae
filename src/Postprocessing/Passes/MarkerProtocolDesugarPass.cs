using System.Collections.Generic;
using System.Linq;
using Compiler.Instantiation;
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
        _referringProto = _registry.LookupType(Compiler.Resolution.RuntimeContract.Referring) as ProtocolTypeInfo;
        _controllingProto = _registry.LookupType(Compiler.Resolution.RuntimeContract.Controlling) as ProtocolTypeInfo;
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
        // Variants share their Parameters list reference with the original routine, so
        // mutating the original also mutates the variant's params (and thus its computed
        // RegistryKey). Capture old keys up front, mutate, then re-key downstream tables.
        var oldKeys = new Dictionary<RoutineInfo, string>(capacity: _snapshot.Count);
        foreach (RoutineInfo r in _snapshot.Keys)
            oldKeys[r] = r.RegistryKey;

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

        // Re-key downstream tables whose keys depended on pre-mutation parameter types:
        // VariantBodies / SynthesizedBodies, and the registry's `_routines` index (so
        // codegen's LookupRoutine(newKey) finds the routine in Phase C).
        foreach ((RoutineInfo r, string oldKey) in oldKeys)
        {
            string newKey = r.RegistryKey;
            if (oldKey == newKey) continue;
            ReKey(_variantBodies, oldKey, newKey);
            ReKey(_synthesizedBodies, oldKey, newKey);
            _registry.RegisterRoutine(routine: r);
        }
    }

    /// <summary>
    /// Catches routine resolutions created AFTER the initial snapshot (e.g. by
    /// GenericMonomorphizationPass during Phase 6/7). Walks the live registry, rewrites
    /// any surviving Referring[T]/Controlling[T] params in-place to inner T, and re-keys
    /// the resolutions dict so callers can look up the routine under its new RegistryKey.
    /// Safe to call multiple times — idempotent on already-rewritten routines.
    /// </summary>
    public void RescanLateResolutions()
    {
        var pendingResolutions = new List<(RoutineInfo Routine, string OldKey)>();
        foreach (RoutineInfo r in _registry.GetAllRoutineResolutions())
            if (HasMarkerParam(r)) pendingResolutions.Add((r, r.RegistryKey));

        var pendingRoutines = new List<(RoutineInfo Routine, string OldKey)>();
        foreach (RoutineInfo r in _registry.GetAllRoutines())
            if (HasMarkerParam(r)) pendingRoutines.Add((r, r.RegistryKey));

        foreach ((RoutineInfo r, string _) in pendingResolutions) RewriteParams(r);
        foreach ((RoutineInfo r, string _) in pendingRoutines) RewriteParams(r);

        foreach ((RoutineInfo r, string oldKey) in pendingResolutions)
        {
            string newKey = r.RegistryKey;
            if (oldKey == newKey) continue;
            _registry.UnregisterRoutineResolution(oldKey);
            _registry.RegisterRoutineResolution(r);
            ReKey(_variantBodies, oldKey, newKey);
            ReKey(_synthesizedBodies, oldKey, newKey);
        }
        foreach ((RoutineInfo r, string oldKey) in pendingRoutines)
        {
            string newKey = r.RegistryKey;
            if (oldKey == newKey) continue;
            _registry.RegisterRoutine(routine: r);
            ReKey(_variantBodies, oldKey, newKey);
            ReKey(_synthesizedBodies, oldKey, newKey);
        }
    }

    /// <summary>
    /// Rewrites Referring[T]/Controlling[T] params on each MonomorphizedBody.Info in place
    /// to their inner T. GMP creates these RoutineInfos in Phase 6 from the gen-def's param
    /// types via plain `ResolveSubstitutedType` (which does not peel marker wrappers), so
    /// the body.Info used at definition emission keeps wrappers — while call-site mangling
    /// uses the registry's already-rewritten resolution, producing a name divergence and a
    /// linker error. Rewriting body.Info here brings both ends back in sync.
    /// Returns a map oldKey -> newKey for re-keying live-routine sets that referenced the
    /// pre-rewrite RegistryKey.
    /// </summary>
    public Dictionary<string, string> RewriteInstantiatedBodyInfos(
        Dictionary<string, MonomorphizedBody> bodies)
    {
        var keyMap = new Dictionary<string, string>();
        var pending = new List<(string OldKey, RoutineInfo Info)>();
        foreach ((string key, MonomorphizedBody body) in bodies)
        {
            // Peel any leaked marker params first, then check key drift. Some bodies
            // arrive here already peeled (their Info.Parameters were mutated in place by
            // earlier passes) but their dict key was computed pre-peel — re-key those too.
            if (HasMarkerParam(body.Info)) RewriteParams(body.Info);
            if (body.Info.RegistryKey != key) pending.Add((key, body.Info));
        }

        foreach ((string oldKey, RoutineInfo info) in pending)
        {
            string newKey = info.RegistryKey;
            if (newKey == oldKey) continue;
            MonomorphizedBody body = bodies[oldKey];
            bodies.Remove(oldKey);
            bodies[newKey] = body;
            keyMap[oldKey] = newKey;
        }

        return keyMap;
    }

    private bool HasMarkerParam(RoutineInfo r)
    {
        for (int i = 0; i < r.Parameters.Count; i++)
            if (ClassifyMarker(r.Parameters[i].Type) != MarkerKind.None) return true;
        return false;
    }

    private void RewriteParams(RoutineInfo r)
    {
        for (int i = 0; i < r.Parameters.Count; i++)
        {
            ParameterInfo p = r.Parameters[i];
            if (ClassifyMarker(p.Type) == MarkerKind.None) continue;
            TypeInfo? inner = GetInnerT(p.Type);
            if (inner == null) continue;
            r.Parameters[i] = p.WithSubstitutedType(inner);
        }
    }

    private static void ReKey(Dictionary<string, Statement>? dict, string oldKey, string newKey)
    {
        if (dict == null) return;
        if (!dict.TryGetValue(oldKey, out Statement? body)) return;
        dict.Remove(oldKey);
        dict[newKey] = body;
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
        if ((t.Name == Compiler.Resolution.RuntimeContract.Referring || t.Name == Compiler.Resolution.RuntimeContract.Controlling)
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
            case DeclarationStatement { Declaration: VariableDeclaration { Initializer: not null } vd }:
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
                if (us.FallbackBody != null) WalkStatement(us.FallbackBody);
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
                && (mem.MemberName == "$refer" || mem.MemberName == "$control"))
                continue;

            string methodName = mk.Kind == MarkerKind.Control ? "$control" : "$refer";
            var coerced = new CallExpression(
                Callee: new MemberExpression(
                    Object: valueExpr,
                    MemberName: methodName,
                    Location: valueExpr.Location),
                Arguments: [],
                Location: valueExpr.Location)
            {
                ResolvedType = mk.InnerType,
                IsInFlight = true,
                IsSynthesizedLowering = true
            };

            if (arg is NamedArgumentExpression na)
                call.Arguments[argIdx] = na with { Value = coerced };
            else
                call.Arguments[argIdx] = coerced;
        }
    }
}