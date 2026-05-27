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
                if (expr is not CallExpression ce ||
                    ce.ResolvedRoutine is not { OwnerType: ProtocolTypeInfo } pr)
                    return;
                if (!RoutineHasDefaultImplBody(routine: pr)) return;
                if (ce.Callee is not MemberExpression mem) return;
                if (mem.Object.ResolvedType is not { } recvType) return;

                TypeInfo impl = UnwrapWrappers(t: recvType);
                if (impl is ProtocolTypeInfo) return; // implementer still unresolved

                var key = (pr.RegistryKey, impl.FullName);
                if (_synthesized.ContainsKey(key: key)) return;

                RoutineInfo synthesized = SynthesizePerImplementer(protocolRoutine: pr, implementer: impl);

                Statement? clonedBody = CloneProtocolRoutineBody(protocolRoutine: pr, implementer: impl,
                    synthesized: synthesized);
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
                ctx.InstantiatedGenericBodies[key: synthesized.RegistryKey] = new MonomorphizedBody(
                    Ast: WrapInShellDecl(name: synthesized.Name, body: clonedBody, info: synthesized),
                    Info: synthesized,
                    TypeSubs: new Dictionary<string, TypeInfo> { ["me"] = impl },
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
                if (expr is not CallExpression ce ||
                    ce.ResolvedRoutine is not { OwnerType: ProtocolTypeInfo } pr)
                    return;
                if (!RoutineHasDefaultImplBody(routine: pr)) return;
                if (ce.Callee is not MemberExpression mem) return;
                if (mem.Object.ResolvedType is not { } recvType) return;
                TypeInfo impl = UnwrapWrappers(t: recvType);
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

    private RoutineInfo SynthesizePerImplementer(RoutineInfo protocolRoutine, TypeInfo implementer)
    {
        // Clone parameters/return with `Me` substituted to the implementer. For most
        // protocol-default-impls (e.g. Iterable[Text].join(separator: Text) -> ?Text),
        // parameters/return don't reference Me; the substitution mainly matters for `me` inside the body.
        var subs = new Dictionary<string, TypeInfo> { ["Me"] = implementer };
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
        RoutineInfo synthesized)
    {
        Statement? originalBody = GetDefaultImplBody(routine: protocolRoutine);
        if (originalBody == null)
            return null;

        // GenericAstRewriter sets ctx.ParamTypes["me"] from enclosingRoutine.OwnerType, so passing
        // the synthesized routine (OwnerType = implementer) automatically rebinds `me` (the receiver)
        // to the implementer. `Me` (the typename) is substituted via typeSubs below; nested member
        // calls in the body then re-resolve against the implementer's methods.
        var typeSubs = new Dictionary<string, TypeInfo> { ["Me"] = implementer };
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

    private static TypeInfo SubstituteMe(TypeInfo t, Dictionary<string, TypeInfo> subs)
    {
        if (t is GenericParameterTypeInfo gp && subs.TryGetValue(key: gp.Name, value: out TypeInfo? sub))
            return sub;
        // TODO: recurse into TypeArguments for composite types like List[Me]. First-cut shortcut.
        return t;
    }

}
