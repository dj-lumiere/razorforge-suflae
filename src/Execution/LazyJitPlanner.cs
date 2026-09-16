using Builder.Instantiation;
using Builder.LlvmEmit;
using Builder.Targeting;
using Builder.Verification.Results;
using SyntaxTree;

namespace Builder.Execution;

/// <summary>The analysis pieces codegen consumes, captured just before <c>Generate()</c> so the resident-JIT
/// incremental (B) path can build a MAIN module + a per-routine on-demand materializer instead of emitting the
/// whole program eagerly. Everything here is already type-annotated + monomorphized by the front pipeline.</summary>
internal sealed record LazyJitInputs(
    List<(SyntaxTree.Program Program, string FilePath, string Module)> UserPrograms,
    AnalysisResult Result,
    TargetConfig Target,
    RfBuildMode BuildMode,
    string? EntryModule);

/// <summary>
/// Resident-JIT incremental (B) M3: turns <see cref="LazyJitInputs"/> into a fully-lazy JIT plan — the MAIN
/// module (@main + user routines + shared runtime globals; every stdlib callee an extern <c>declare</c>) plus
/// a <c>materialize(name)</c> delegate the ORC custom definition generator calls on demand. Each on-demand
/// routine is served from the disk <see cref="RoutineIrCache"/> when possible (cross-run reuse), else
/// codegen'd as an external-linkage one-routine module and cached. Wires M1a/M2a/M2b into one entry point.
/// </summary>
internal static class LazyJitPlanner
{
    /// <summary>The module-name segments that mark a routine as user-dependent (never cached): the distinct
    /// modules of the user programs plus the entry module.</summary>
    public static List<string> UserModuleSegments(
        List<(SyntaxTree.Program Program, string FilePath, string Module)> userPrograms, string? entryModule)
    {
        var segs = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach ((SyntaxTree.Program _, string _, string module) in userPrograms)
        {
            if (module.Length > 0)
            {
                segs.Add(item: module);
            }
        }

        if (entryModule is { Length: > 0 })
        {
            segs.Add(item: entryModule);
        }

        return segs.ToList();
    }

    /// <summary>The Phase-9 skip predicate (resident-JIT incremental (B) §2A.2②): true for a monomorphized
    /// instance whose one-routine IR is already on disk in <paramref name="cache"/>. Mangles the instance's
    /// name exactly as <see cref="Build"/>'s materializer does (unquoted, matching the disk key), so
    /// check-skip ⟺ codegen-skip by construction. Consumed via <c>SemanticVerifier.SkipInstanceCheckIfIrCached</c>.
    /// Lives here (not in the daemon) so the LlvmEmit mangling stays in the JIT layer.</summary>
    public static Func<TypeModel.Symbols.RoutineInfo, bool> IrCachedPredicate(RoutineIrCache cache)
    {
        return info => cache.Has(
            mangledName: LlvmEmitter.MangleRoutineName(routine: info).Trim(trimChar: '"'));
    }

    /// <summary>Builds the (mainIr, materialize) plan. <paramref name="cache"/> is consulted+populated per
    /// on-demand routine; pass one keyed by the stdlib+compiler fingerprint.</summary>
    public static (string mainIr, Func<string, string?> materialize) Build(LazyJitInputs inputs,
        RoutineIrCache cache)
    {
        AnalysisResult r = inputs.Result;

        // name → body index (unquoted key, matching ORC symbol names + declare content) + the quoted forms
        // (matching IsResident / MangleRoutineName output) for the per-routine module's resident set.
        var index = new Dictionary<string, MonomorphizedBody>(comparer: StringComparer.Ordinal);
        var allQuoted = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (MonomorphizedBody b in r.InstantiatedGenericBodies.Values)
        {
            string mangled = LlvmEmitter.MangleRoutineName(routine: b.Info);
            index[key: mangled.Trim(trimChar: '"')] = b;
            allQuoted.Add(item: mangled);
        }

        // MAIN: @main + user routines + trace globals DEFINED (residentSymbols empty ⇒ not delta); no stdlib
        // bodies (empty InstantiatedGenericBodies) ⇒ every stdlib callee is an extern declare the generator fills.
        string mainIr = new LlvmEmitter(userPrograms: inputs.UserPrograms,
            registry: r.Registry,
            options: new LlvmEmitterOptions
            {
                StdlibPrograms = r.Registry.StdlibPrograms,
                Target = inputs.Target,
                BuildMode = inputs.BuildMode,
                SynthesizedBodies = r.SynthesizedBodies,
                InstantiatedGenericBodies = new Dictionary<string, MonomorphizedBody>(),
                LiveRoutineKeys = r.LiveRoutineKeys,
                MaySuspendRoutineKeys = r.MaySuspendRoutineKeys
            }) { EntryModule = inputs.EntryModule }.Generate();

        string? Materialize(string name)
        {
            if (!index.TryGetValue(key: name, value: out MonomorphizedBody? body))
            {
                return null; // not ours — rf_* runtime resolves via the process-search generator
            }

            if (cache.TryGet(mangledName: name, ir: out string cached))
            {
                return cached;
            }

            var resident = new HashSet<string>(collection: allQuoted, comparer: StringComparer.Ordinal);
            resident.Remove(item: LlvmEmitter.MangleRoutineName(routine: body.Info));
            string ir = new LlvmEmitter(userPrograms: new List<(SyntaxTree.Program, string, string)>(),
                registry: r.Registry,
                options: new LlvmEmitterOptions
                {
                    StdlibPrograms = r.Registry.StdlibPrograms,
                    Target = inputs.Target,
                    BuildMode = inputs.BuildMode,
                    SynthesizedBodies = r.SynthesizedBodies,
                    InstantiatedGenericBodies =
                        new Dictionary<string, MonomorphizedBody> { [key: name] = body },
                    ResidentSymbols = resident,
                    ForExternalJitModule = true
                }).Generate();
            cache.Put(mangledName: name, ir: ir);
            return ir;
        }

        return (mainIr, Materialize);
    }
}
