using System;
using System.Collections.Generic;
using System.Linq;
using Compiler.Resolution;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using TypeInfo = TypeModel.Types.TypeInfo;

namespace Compiler.Instantiation.Passes;

/// <summary>
/// Lowers protocol default-impl routines to per-implementer routines at the AST level.
///
/// A protocol-extension routine such as
/// <code>routine Iterable[Text].join(separator: Text) -> ?Text</code>
/// carries a real body whose <c>me</c> is typed as the protocol itself. The body's
/// nested calls (e.g. <c>for part in me</c> -> <c>me.$iter()</c> / <c>me.$next!()</c>)
/// cannot resolve statically against an abstract protocol owner; codegen needs the
/// concrete implementer.
///
/// Approach (per Rust's trait-default-method monomorphization):
/// for every call site that resolves to a protocol-default-impl routine with a body,
/// synthesize a per-(protocol-routine, implementer) routine whose owner is the
/// implementer. The synthesized routine reuses the protocol routine's AST body with
/// <c>me</c> rebound to the implementer; nested calls then re-resolve against the
/// implementer's methods through the standard GMP path.
///
/// Pipeline placement: runs inside <see cref="GenericClosurePass"/> before GMP, so the
/// synthesized routines flow through the normal monomorphization machinery and land
/// in <see cref="InstantiationContext.InstantiatedGenericBodies"/>.
///
/// Codegen is unmodified — by the time it sees the call site, <c>ResolvedRoutine</c>
/// already points at the implementer-owned synthesized routine, so the existing
/// mangling and emission paths "just work" (symbol owner = implementer).
/// </summary>
internal sealed class ProtocolDefaultImplLoweringPass(InstantiationContext ctx)
{
    /// <summary>(protocolRoutineKey, implementerFullName) -> synthesized per-implementer routine.</summary>
    private readonly Dictionary<(string ProtoKey, string ImplKey), RoutineInfo> _synthesized =
        new();

    /// <summary>
    /// Synthesizes per-implementer protocol-default-impl bodies for every call site reachable from
    /// the current body set, iterating to a local fixed point. Returns <c>true</c> if any new body
    /// was synthesized — the caller re-runs GMP + this pass so that protocol-default calls appearing
    /// only inside GMP-monomorphized bodies (e.g. <c>source.List()</c> inside <c>ReverseIterator.$iter</c>)
    /// are also lowered.
    /// </summary>
    /// <summary>
    /// Bodies already scanned (by reference). Persists across Run() calls AND across this Run's
    /// fixed-point rounds so a post-GMP re-run only walks bodies synthesized/monomorphized since the
    /// last pass — re-walking the entire (post-GMP) body set every round is the dominant cost and made
    /// the PDIL→GMP fixed point unusably slow. A body's protocol-default call sites are resolvable on
    /// first scan (monomorphized receivers are concrete), so re-scanning never surfaces new work.
    /// </summary>
    private readonly HashSet<Statement> _walkedBodies =
        new(comparer: ReferenceEqualityComparer.Instance);

    public bool Run()
    {
        // Iterate until fixed point: synthesizing per-implementer bodies may surface new
        // call sites (the cloned body has its own protocol-default-impl calls). Each round processes
        // only the bodies not yet scanned, so the work is bounded by the delta.
        bool changed;
        bool synthesizedAny = false;
        do
        {
            var freshBodies = EnumerateLiveRoutineBodies()
                .Where(predicate: b => !_walkedBodies.Contains(item: b))
                .ToList();
            if (freshBodies.Count == 0) break;

            changed = DiscoverAndSynthesize(bodies: freshBodies);
            if (changed)
            {
                synthesizedAny = true;
                RewriteCallSites(bodies: freshBodies);
            }
            foreach (Statement b in freshBodies) _walkedBodies.Add(item: b);
        } while (changed);
        return synthesizedAny;
    }

    /// <summary>
    /// Walks the supplied routine bodies looking for calls to protocol-default-impl routines.
    /// For each unique (protocolRoutine, implementer) pair not yet synthesized, creates the
    /// per-implementer RoutineInfo, clones the AST body, registers both with the registry
    /// and the instantiation context.
    /// </summary>
    private bool DiscoverAndSynthesize(IReadOnlyList<Statement> bodies)
    {
        bool added = false;
        foreach (Statement body in bodies)
        {
            AstWalker.WalkExpressions(root: body, visit: expr =>
            {
                // Handle both the lowered call form and the still-generic method-call form: explicit
                // method type args (e.g. `select_many[S64](…)`) reach PDIL as a
                // GenericMethodCallExpression because GenericCallLoweringPass runs AFTER PDIL. Without
                // this its per-implementer body (List[S64].select_many) is never synthesized.
                if (!TryGetProtocolDefaultCallParts(expr: expr,
                        resolvedRoutine: out RoutineInfo? rr0, receiverType: out TypeInfo? recvType0,
                        rebind: out _))
                    return;
                if (!TryResolveProtocolDefaultImpl(resolvedRoutine: rr0, receiverResolvedType: recvType0,
                        protoRoutine: out RoutineInfo? pr,
                        implementer: out TypeInfo? implOrNull) || pr == null || implOrNull == null)
                    return;
                TypeInfo impl = implOrNull;

                // Method-generic resolution (e.g. `List[Text].zip[S64, List[S64]]`): the call already
                // resolved to a fully-concrete resolution `rr` whose method type-arguments bind the
                // protocol body's method generics. SynthesizePerImplementer drops method generics, so
                // instead generate the body FOR `rr` directly — substituting the protocol's own params
                // (T→Text) AND the method generics (U→S64, S2→List[S64]) — keyed by rr's own
                // RegistryKey (which is exactly the symbol the call site emits).
                RoutineInfo rr = rr0!;
                // pr.GenericParameters lists the protocol owner's params first (e.g. T) then the
                // method-level params (U, S2); rr.TypeArguments holds only the method args. Align them
                // from the END so the trailing method generics bind, while the owner param (T) is
                // supplied separately by BuildProtocolGenericSubs (T→Text from the conformance).
                if (rr.TypeArguments is { Count: > 0 } methodArgs &&
                    pr.GenericParameters is { Count: > 0 } allParams &&
                    methodArgs.Count <= allParams.Count)
                {
                    Dictionary<string, TypeInfo> fullSubs = BuildProtocolGenericSubs(
                        protocolRoutine: pr, implementer: impl);
                    int offset = allParams.Count - methodArgs.Count;
                    for (int i = 0; i < methodArgs.Count; i++)
                        fullSubs[key: allParams[index: offset + i]] = methodArgs[index: i];

                    // Build the per-implementer routine with Me + the method generics substituted in
                    // its signature (rr's own ReturnType still carries ProtocolSelf `Me`, which would
                    // trip codegen's ContainsGenericParameter guard and silently skip emission). Carry
                    // rr.TypeArguments so it mangles to the same symbol the call site emits.
                    RoutineInfo mgInfo = SynthesizePerImplementer(protocolRoutine: pr, implementer: impl,
                        protoSubs: fullSubs, typeArguments: methodArgs);

                    // Guard on the key we actually store under (mgInfo's), or the fixed-point loop
                    // never converges (re-adding every iteration).
                    if (ctx.InstantiatedGenericBodies.ContainsKey(key: mgInfo.RegistryKey)) return;

                    Statement? mgBody = CloneProtocolRoutineBody(protocolRoutine: pr, implementer: impl,
                        synthesized: mgInfo, protoSubs: fullSubs);
                    if (mgBody == null) return;

                    ctx.LiveRoutineKeys.Add(item: mgInfo.RegistryKey);
                    var mgSubs = new Dictionary<string, TypeInfo>(fullSubs)
                    {
                        ["me"] = impl,
                        ["Me"] = impl
                    };
                    ctx.InstantiatedGenericBodies[key: mgInfo.RegistryKey] = new MonomorphizedBody(
                        Ast: WrapInShellDecl(name: mgInfo.Name, body: mgBody, info: mgInfo),
                        Info: mgInfo,
                        TypeSubs: mgSubs,
                        VariantStatus: null,
                        VariantInnerType: null,
                        IsSynthesized: false);
                    added = true;
                    return;
                }

                var key = (pr.RegistryKey, impl.FullName);
                if (_synthesized.ContainsKey(key: key)) return;

                // Bind the protocol's own generic params (e.g. Iterable[T].enumerate's `T`) from the
                // implementer's conformance (`List[Text] obeys Iterable[Text]` ⇒ T=Text), so the
                // synthesized body and signature don't leak the protocol element param.
                Dictionary<string, TypeInfo> protoSubs =
                    BuildProtocolGenericSubs(protocolRoutine: pr, implementer: impl);

                RoutineInfo synthesized = SynthesizePerImplementer(protocolRoutine: pr, implementer: impl,
                    protoSubs: protoSubs);

                Statement? clonedBody = CloneProtocolRoutineBody(protocolRoutine: pr, implementer: impl,
                    synthesized: synthesized, protoSubs: protoSubs);
                if (clonedBody == null) return;

                _synthesized[key: key] = synthesized;
                ctx.Registry.RegisterRoutine(routine: synthesized);
                ctx.LiveRoutineKeys.Add(item: synthesized.RegistryKey);

                // Stash the cloned AST as a monomorphized body so codegen's normal
                // InstantiatedGenericBodies sweep picks it up under the implementer-owned key.
                // IsSynthesized=false: this body has a real AST cloned from the stdlib
                // protocol-default-impl body and must flow through every lowering pass
                // (ControlFlow, FString, Pattern, Expression, Operator, ...). Several
                // RunOnInstantiatedGenericBodies methods skip IsSynthesized=true entries
                // assuming there is no AST to walk, which is not the case here.
                // "me" is the receiver value binding; "Me" maps ProtocolSelf (Name "Me") to the
                // implementer so codegen's type substitution resolves `Me`-typed constructions
                // (e.g. `EnumerateIterator[T, Me]`) instead of leaking ProtocolSelf.
                var bodySubs = new Dictionary<string, TypeInfo>(protoSubs)
                {
                    ["me"] = impl,
                    ["Me"] = impl
                };
                ctx.InstantiatedGenericBodies[key: synthesized.RegistryKey] = new MonomorphizedBody(
                    Ast: WrapInShellDecl(name: synthesized.Name, body: clonedBody, info: synthesized),
                    Info: synthesized,
                    TypeSubs: bodySubs,
                    VariantStatus: null,
                    VariantInnerType: null,
                    IsSynthesized: false);
                added = true;
            });
        }
        return added;
    }

    /// <summary>
    /// Second-pass walk over the supplied bodies: rebinds every <c>ResolvedRoutine</c> that still
    /// points at a protocol-default-impl routine over to the corresponding per-implementer routine.
    /// Only the bodies scanned this round need rebinding — a call site to a routine synthesized this
    /// round lives in one of those bodies (it is what triggered the synthesis).
    /// </summary>
    private void RewriteCallSites(IReadOnlyList<Statement> bodies)
    {
        foreach (Statement body in bodies)
        {
            AstWalker.WalkExpressions(root: body, visit: expr =>
            {
                if (!TryGetProtocolDefaultCallParts(expr: expr,
                        resolvedRoutine: out RoutineInfo? rr, receiverType: out TypeInfo? recvType,
                        rebind: out Action<RoutineInfo> rebind))
                    return;
                if (!TryResolveProtocolDefaultImpl(resolvedRoutine: rr, receiverResolvedType: recvType,
                        protoRoutine: out RoutineInfo? pr,
                        implementer: out TypeInfo? impl) || pr == null || impl == null)
                    return;
                if (_synthesized.TryGetValue(key: (pr.RegistryKey, impl.FullName),
                        value: out RoutineInfo? newRoutine))
                {
                    rebind(newRoutine);
                }
            });
        }
    }

    // -------- Helpers --------

    /// <summary>
    /// Extracts the resolved routine, receiver type, and a rebind callback from a call-shaped
    /// expression — either a lowered <see cref="CallExpression"/> (callee is a MemberExpression) or a
    /// still-generic <see cref="GenericMethodCallExpression"/> (explicit method type args, not yet
    /// lowered by GenericCallLoweringPass, which runs after this pass). Returns false for anything else.
    /// </summary>
    /// <param name="expr">The expression to inspect.</param>
    /// <param name="resolvedRoutine">On success, the resolved routine from the call site.</param>
    /// <param name="receiverType">On success, the concrete receiver type the call dispatches on.</param>
    /// <param name="rebind">On success, a callback to replace the call's resolved routine with the synthesized one.</param>
    private static bool TryGetProtocolDefaultCallParts(Expression expr,
        out RoutineInfo? resolvedRoutine, out TypeInfo? receiverType, out Action<RoutineInfo> rebind)
    {
        switch (expr)
        {
            case CallExpression { Callee: MemberExpression mem } ce:
                resolvedRoutine = ce.ResolvedRoutine;
                receiverType = mem.Object.ResolvedType;
                rebind = r => ce.ResolvedRoutine = r;
                return true;
            case GenericMethodCallExpression gmc:
                resolvedRoutine = gmc.ResolvedRoutine;
                receiverType = gmc.Object.ResolvedType;
                rebind = r => gmc.ResolvedRoutine = r;
                return true;
            default:
                resolvedRoutine = null;
                receiverType = null;
                rebind = static _ => { };
                return false;
        }
    }

    private bool TryResolveProtocolDefaultImpl(RoutineInfo? resolvedRoutine,
        TypeInfo? receiverResolvedType, out RoutineInfo? protoRoutine, out TypeInfo? implementer)
    {
        protoRoutine = null;
        implementer = null;
        if (resolvedRoutine is not { } rr) return false;
        if (receiverResolvedType is not { } recvType) return false;
        TypeInfo impl = UnwrapWrappers(t: recvType);
        if (impl is ProtocolTypeInfo) return false; // implementer still unresolved

        // Walk the GenericDefinition chain to find a protocol-owned default-impl body. One level
        // covers the re-homed owner-resolved form (List[Text].enumerate -> Iterable[T].enumerate);
        // a method-generic RESOLUTION needs two (List[Text].zip[S64,List[S64]] ->
        // List[Text].zip[U,S2] -> Iterable[T].zip[U,S2]).
        RoutineInfo? proto = null;
        for (RoutineInfo? cur = rr; cur != null; cur = cur.GenericDefinition)
        {
            if (cur.OwnerType is ProtocolTypeInfo && RoutineHasDefaultImplBody(routine: cur))
            {
                proto = cur;
                break;
            }
        }

        if (proto == null) return false;
        protoRoutine = proto;
        implementer = impl;
        return true;
    }

    /// <summary>
    /// Maps the protocol's own generic parameters to the implementer's conformance type arguments.
    /// e.g. for <c>Iterable[T].enumerate</c> synthesized onto <c>List[Text]</c> (which obeys
    /// <c>Iterable[Text]</c>), returns <c>{ T → Text }</c>. Empty when the protocol is non-generic
    /// or no matching conformance is found.
    /// </summary>
    private static Dictionary<string, TypeInfo> BuildProtocolGenericSubs(RoutineInfo protocolRoutine,
        TypeInfo implementer)
    {
        var subs = new Dictionary<string, TypeInfo>();
        if (protocolRoutine.OwnerType is not ProtocolTypeInfo protoOwner) return subs;
        ProtocolTypeInfo protoDef = protoOwner.GenericDefinition ?? protoOwner;
        if (protoDef.GenericParameters is not { Count: > 0 } pParams) return subs;

        List<TypeInfo>? protocols = implementer switch
        {
            EntityTypeInfo e => e.ImplementedProtocols,
            RecordTypeInfo r => r.ImplementedProtocols,
            _ => null
        };
        if (protocols == null) return subs;

        foreach (TypeInfo p in protocols)
        {
            // Match the conformance to the same protocol by generic-definition identity (no name
            // string-munging): `Iterable[Text]`'s def is the same `Iterable` def the routine owns.
            TypeInfo pDef = GenericDefOf(t: p) ?? p;
            if (!ReferenceEquals(objA: pDef, objB: protoDef) && pDef.Name != protoDef.Name) continue;
            if (p.TypeArguments is { Count: > 0 } args)
            {
                for (int i = 0; i < pParams.Count && i < args.Count; i++)
                    subs[key: pParams[index: i]] = args[index: i];
            }
            break;
        }
        return subs;
    }

    /// <summary>
    /// Yields every routine body to be scanned: user program routines (incl. members of
    /// entity/record declarations), stdlib routine bodies indexed by registry key, and
    /// monomorphized bodies already produced (so cascade calls are also rewritten).
    /// </summary>
    private IEnumerable<Statement> EnumerateLiveRoutineBodies()
    {
        foreach ((Program prog, _, _) in ctx.UserPrograms)
        {
            foreach (Statement s in WalkDeclarationsForBodies(prog: prog))
                yield return s;
        }
        foreach (Statement s in ctx.RoutineBodies.Values)
        {
            yield return s;
        }
        // Snapshot: DiscoverAndSynthesize adds to ctx.InstantiatedGenericBodies as it walks, which
        // would invalidate a live enumerator (this matters on the post-GMP PDIL re-run, when the map
        // is already populated). New bodies from this pass are picked up by the outer fixed-point loop.
        foreach (MonomorphizedBody mb in ctx.InstantiatedGenericBodies.Values.ToList())
        {
            yield return mb.Ast.Body;
        }
    }

    private static IEnumerable<Statement> WalkDeclarationsForBodies(Program prog)
    {
        foreach (SyntaxTree.Declaration d in prog.Declarations)
        {
            switch (d)
            {
                case RoutineDeclaration r:
                    yield return r.Body;
                    break;
                case EntityDeclaration ed:
                    foreach (SyntaxTree.Declaration m in ed.Members)
                        if (m is RoutineDeclaration mr) yield return mr.Body;
                    break;
                case RecordDeclaration rd:
                    foreach (SyntaxTree.Declaration m in rd.Members)
                        if (m is RoutineDeclaration mr) yield return mr.Body;
                    break;
            }
        }
    }

    /// <summary>
    /// True when this protocol routine is a default-impl (real body) rather than an
    /// abstract protocol stub. Body presence is detected via <see cref="InstantiationContext.RoutineBodies"/>.
    /// </summary>
    private bool RoutineHasDefaultImplBody(RoutineInfo routine)
        => ctx.RoutineBodies.ContainsKey(key: routine.RegistryKey)
           || (routine.GenericDefinition != null &&
               ctx.RoutineBodies.ContainsKey(key: routine.GenericDefinition.RegistryKey));

    private Statement? GetDefaultImplBody(RoutineInfo routine)
    {
        if (ctx.RoutineBodies.TryGetValue(key: routine.RegistryKey, value: out Statement? b))
            return b;
        if (routine.GenericDefinition != null &&
            ctx.RoutineBodies.TryGetValue(key: routine.GenericDefinition.RegistryKey, value: out b))
            return b;
        return null;
    }

    private static TypeInfo UnwrapWrappers(TypeInfo t)
    {
        // Strip Retained/Tracked/Viewing/Modifying/Hijacked/Referring/Controlling layers
        // to get at the implementer record/entity.
        while (t is WrapperTypeInfo w)
        {
            t = w.InnerType;
        }
        return t;
    }

    private RoutineInfo SynthesizePerImplementer(RoutineInfo protocolRoutine, TypeInfo implementer,
        Dictionary<string, TypeInfo> protoSubs, List<TypeInfo>? typeArguments = null)
    {
        // Clone parameters/return with `Me` substituted to the implementer AND the protocol's own
        // generic params bound from the implementer's conformance (e.g. T→Text). The latter matters
        // for signatures like Iterable[T].enumerate() -> ?EnumerateIterator[T], whose return would
        // otherwise leak the protocol element param. For a method-generic instantiation (zip[U,S2]),
        // protoSubs also carries the bound method generics and <paramref name="typeArguments"/> the
        // concrete method args, so the synthesized routine mangles identically to the call site's
        // resolution symbol (e.g. `List[Text].zip[S64,List[S64]](List[S64])`).
        var subs = new Dictionary<string, TypeInfo>(protoSubs) { ["Me"] = implementer };
        var newParams = protocolRoutine.Parameters
            .Select(selector: p => p.WithSubstitutedType(newType: SubstituteMe(t: p.Type, subs: subs)))
            .ToList();

        TypeInfo? newRet = protocolRoutine.ReturnType != null
            ? SubstituteMe(t: protocolRoutine.ReturnType, subs: subs)
            : null;

        return new RoutineInfo(name: protocolRoutine.Name)
        {
            Kind = protocolRoutine.Kind,
            OwnerType = implementer,
            Parameters = newParams,
            ReturnType = newRet,
            IsFailable = protocolRoutine.IsFailable,
            IsSynthesized = true,
            TypeArguments = typeArguments,
            Location = protocolRoutine.Location
            // GenericDefinition is left null intentionally — this is not a generic instantiation
            // in the usual sense; the body is keyed in InstantiatedGenericBodies by RegistryKey.
        };
    }

    private Statement? CloneProtocolRoutineBody(RoutineInfo protocolRoutine, TypeInfo implementer,
        RoutineInfo synthesized, Dictionary<string, TypeInfo> protoSubs)
    {
        Statement? originalBody = GetDefaultImplBody(routine: protocolRoutine);
        if (originalBody == null)
            return null;

        // GenericAstRewriter sets ctx.ParamTypes["me"] from enclosingRoutine.OwnerType, so passing
        // the synthesized routine (OwnerType = implementer) automatically rebinds `me` (the receiver)
        // to the implementer. `Me` (the typename) is substituted via typeSubs below; nested member
        // calls in the body then re-resolve against the implementer's methods. The protocol's own
        // generic params (e.g. T→Text) are folded in so body types like EnumerateIterator[T] become
        // concrete (EnumerateIterator[Text]) and don't leak the element param into codegen.
        var typeSubs = new Dictionary<string, TypeInfo>(protoSubs) { ["Me"] = implementer };
        var stringSubs = typeSubs.ToDictionary(keySelector: kv => kv.Key,
            elementSelector: kv => kv.Value.FullName);
        Statement cloned = GenericAstRewriter.RewriteStatement(
            stmt: originalBody, subs: stringSubs, typeSubs: typeSubs,
            registry: ctx.Registry, enclosingRoutine: synthesized);

        // Stdlib bodies are stored raw (no SA annotation). `me` identifiers therefore have
        // ResolvedType=null after cloning, which blocks downstream lowering: ControlFlowLowering
        // can only set the synthesized `try_next` call's ResolvedType when `forStmt.Iterable`
        // (= `me`) carries one — without that, PatternLoweringPass sees subjectType=null and
        // refuses to fold the `is None / else var v` when-chain, leaking a NonePattern into codegen.
        AnnotateMeReferences(node: cloned, implementer: implementer);
        return cloned;
    }

    private static void AnnotateMeReferences(object? node, TypeInfo implementer)
    {
        AstWalker.WalkExpressions(root: node, visit: expr =>
        {
            if (expr is IdentifierExpression { Name: "me" } id)
            {
                id.ResolvedType = implementer;
            }
        });
    }

    private static RoutineDeclaration WrapInShellDecl(string name, Statement body, RoutineInfo info)
        => new(Name: name, Parameters: [], ReturnType: null, Body: body,
            Visibility: VisibilityModifier.Open, Annotations: [],
            Location: info.Location ?? new SourceLocation(FileName: "", Line: 0, Column: 0, Position: 0));

    private TypeInfo SubstituteMe(TypeInfo t, Dictionary<string, TypeInfo> subs)
    {
        if (t is GenericParameterTypeInfo gp && subs.TryGetValue(key: gp.Name, value: out TypeInfo? sub))
            return sub;

        // `Me` in a protocol-default-impl signature/body resolves to ProtocolSelf; bind it to the
        // implementer (subs["Me"]). Without this, a return type like `?EnumerateIterator[T, Me]`
        // keeps ProtocolSelf, which ContainsGenericParameter flags, making codegen skip the body.
        if (t is ProtocolSelfTypeInfo && subs.TryGetValue(key: "Me", value: out TypeInfo? meSub))
            return meSub;

        // RoutineTypeInfo keeps its parameter/return types in dedicated properties, NOT in
        // TypeArguments, so the generic recursion below misses them. Substitute each explicitly so
        // a lambda parameter like `transform: Routine[(T,), U]` becomes `Routine[(S64,), S64]`.
        // Without this the synthesized routine mangles to `...select(Routine[(T,), U])` and never
        // matches the call site's `...select(Routine[(S64,), S64])` → "undefined symbol" at codegen.
        if (t is RoutineTypeInfo rt)
        {
            var newParamTypes = rt.ParameterTypes
                .Select(selector: p => SubstituteMe(t: p, subs: subs))
                .ToList();
            TypeInfo? newReturn = rt.ReturnType != null
                ? SubstituteMe(t: rt.ReturnType, subs: subs)
                : null;
            return new RoutineTypeInfo(parameterTypes: newParamTypes, returnType: newReturn)
            {
                IsFailable = rt.IsFailable
            };
        }

        // Recurse into composite types (e.g. EnumerateIterator[T] → EnumerateIterator[Text],
        // List[Me] → List[List[Text]]) so the substituted param doesn't survive in a type argument.
        if (t.TypeArguments is { Count: > 0 } args)
        {
            bool changed = false;
            var newArgs = new List<TypeInfo>(capacity: args.Count);
            foreach (TypeInfo a in args)
            {
                TypeInfo na = SubstituteMe(t: a, subs: subs);
                changed |= !ReferenceEquals(objA: na, objB: a);
                newArgs.Add(item: na);
            }
            if (changed)
            {
                TypeInfo? def = GenericDefOf(t: t) ?? (t.IsGenericDefinition ? t : null);
                if (def != null)
                    return ctx.Registry.GetOrCreateResolution(genericDef: def, typeArguments: newArgs);
            }
        }
        return t;
    }

    /// <summary>Generic definition of a type, which lives on the concrete subtypes, not base TypeInfo.</summary>
    private static TypeInfo? GenericDefOf(TypeInfo t) => t switch
    {
        RecordTypeInfo r => r.GenericDefinition,
        EntityTypeInfo e => e.GenericDefinition,
        ProtocolTypeInfo p => p.GenericDefinition,
        _ => null
    };

}
