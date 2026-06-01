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

    public void Run()
    {
        // Iterate until fixed point: synthesizing per-implementer bodies may surface new
        // call sites (the cloned body has its own protocol-default-impl calls).
        bool changed;
        do
        {
            changed = DiscoverAndSynthesize();
            if (changed) RewriteCallSites();
        } while (changed);
    }

    /// <summary>
    /// Walks every reachable routine body looking for calls to protocol-default-impl routines.
    /// For each unique (protocolRoutine, implementer) pair not yet synthesized, creates the
    /// per-implementer RoutineInfo, clones the AST body, registers both with the registry
    /// and the instantiation context.
    /// </summary>
    private bool DiscoverAndSynthesize()
    {
        bool added = false;
        foreach (Statement body in EnumerateLiveRoutineBodies())
        {
            AstWalker.WalkExpressions(root: body, visit: expr =>
            {
                if (expr is not CallExpression ce) return;
                if (!TryResolveProtocolDefaultImpl(ce: ce, protoRoutine: out RoutineInfo? pr,
                        implementer: out TypeInfo? implOrNull) || pr == null || implOrNull == null)
                    return;
                TypeInfo impl = implOrNull;

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
                var bodySubs = new Dictionary<string, TypeInfo>(protoSubs) { ["me"] = impl };
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
    /// Second-pass walk: rebinds every <c>ResolvedRoutine</c> that still points at a
    /// protocol-default-impl routine over to the corresponding per-implementer synthesized routine.
    /// </summary>
    private void RewriteCallSites()
    {
        foreach (Statement body in EnumerateLiveRoutineBodies())
        {
            AstWalker.WalkExpressions(root: body, visit: expr =>
            {
                if (expr is not CallExpression ce) return;
                if (!TryResolveProtocolDefaultImpl(ce: ce, protoRoutine: out RoutineInfo? pr,
                        implementer: out TypeInfo? impl) || pr == null || impl == null)
                    return;
                if (_synthesized.TryGetValue(key: (pr.RegistryKey, impl.FullName),
                        value: out RoutineInfo? newRoutine))
                {
                    ce.ResolvedRoutine = newRoutine;
                }
            });
        }
    }

    // -------- Helpers --------

    /// <summary>
    /// Identifies a call to a protocol default-impl (extension) routine and the concrete implementer
    /// it's invoked on. Handles two shapes:
    /// <list type="bullet">
    ///   <item>Direct: <c>ResolvedRoutine.OwnerType</c> is the protocol (not yet re-homed).</item>
    ///   <item>Re-homed: SA already rewrote the owner to the concrete implementer (e.g.
    ///   <c>List[Text].enumerate</c>), but the routine's <c>GenericDefinition</c> is still the
    ///   protocol-owned body source (<c>Iterable[T].enumerate</c>). The earlier owner-only check
    ///   missed this, leaving the call referencing an undefined implementer-owned symbol.</item>
    /// </list>
    /// Returns the protocol body source as <paramref name="protoRoutine"/> so synthesis keys both
    /// shapes to the same per-implementer routine.
    /// </summary>
    private bool TryResolveProtocolDefaultImpl(CallExpression ce, out RoutineInfo? protoRoutine,
        out TypeInfo? implementer)
    {
        protoRoutine = null;
        implementer = null;
        if (ce.ResolvedRoutine is not { } rr) return false;
        if (ce.Callee is not MemberExpression mem) return false;
        if (mem.Object.ResolvedType is not { } recvType) return false;
        TypeInfo impl = UnwrapWrappers(t: recvType);
        if (impl is ProtocolTypeInfo) return false; // implementer still unresolved

        RoutineInfo? proto = null;
        if (rr.OwnerType is ProtocolTypeInfo && RoutineHasDefaultImplBody(routine: rr))
            proto = rr;
        else if (rr.GenericDefinition is { OwnerType: ProtocolTypeInfo } gd &&
                 RoutineHasDefaultImplBody(routine: rr))
            proto = gd;

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
        foreach (MonomorphizedBody mb in ctx.InstantiatedGenericBodies.Values)
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
        // Strip Owned/Retained/Tracked/Viewed/Grasped/Hijacked/Referring/Controlling layers
        // to get at the implementer record/entity.
        while (t is WrapperTypeInfo w)
        {
            t = w.InnerType;
        }
        return t;
    }

    private RoutineInfo SynthesizePerImplementer(RoutineInfo protocolRoutine, TypeInfo implementer,
        Dictionary<string, TypeInfo> protoSubs)
    {
        // Clone parameters/return with `Me` substituted to the implementer AND the protocol's own
        // generic params bound from the implementer's conformance (e.g. T→Text). The latter matters
        // for signatures like Iterable[T].enumerate() -> ?EnumerateIterator[T], whose return would
        // otherwise leak the protocol element param.
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
