using Builder.Instantiation;
using Builder.LlvmEmit;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using Builder.Verification;
using Builder.Verification.Results;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

#pragma warning disable xUnit1004
namespace RazorForge.Tests.Perf;

/// <summary>
/// Resident-JIT incremental Phase 0a (see internal-wiki/RESIDENT-JIT-INCREMENTAL-V0.5.md §2A.5): validates
/// <see cref="LlvmEmitter.GenerateBase"/> — the NON-PRUNED precompiled stdlib base. The load-bearing
/// correctness property is that the base is a SUPERSET of any pruned build's stdlib defines: a delta module
/// only ever emits an extern <c>declare</c> for a symbol it doesn't define, so every such symbol MUST be
/// present in the base or JIT-link fails with an undefined symbol.
/// </summary>
public sealed partial class BaseEmissionTests
{
    [GeneratedRegex(pattern: @",? ?\d+")]
    private static partial Regex ArityDigitsRegex();

    private readonly ITestOutputHelper _out;
    public BaseEmissionTests(ITestOutputHelper output)
    {
        _out = output;
    }

    private const string Trivial = """
                                   module Bench
                                   import IO/Console
                                   routine start()
                                     show("hi")
                                     return
                                   """;

    private static Program Parse(string src, string file)
    {
        return new Builder.Parser.Parser(
            tokens: new Tokenizer(source: src, fileName: file, language: Language.RazorForge)
               .Tokenize(),
            language: Language.RazorForge,
            fileName: file).Parse();
    }

    /// <summary>The mangled symbol defined by each `define ...` line (the first <c>@"..."</c>/<c>@ident</c>).</summary>
    private static HashSet<string> DefinedSymbols(string ll)
    {
        return SymbolsOnLinesStartingWith(ll: ll, prefix: "define ");
    }

    /// <summary>The mangled symbol referenced by each `declare ...` line (an extern reference).</summary>
    private static HashSet<string> DeclaredSymbols(string ll)
    {
        return SymbolsOnLinesStartingWith(ll: ll, prefix: "declare ");
    }

    private static HashSet<string> SymbolsOnLinesStartingWith(string ll, string prefix)
    {
        var set = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (string line in ll.Split(separator: '\n'))
        {
            if (!line.StartsWith(value: prefix, comparisonType: StringComparison.Ordinal))
            {
                continue;
            }

            int at = line.IndexOf(value: '@');
            if (at < 0)
            {
                continue;
            }

            string sym;
            if (at + 1 < line.Length && line[index: at + 1] == '"')
            {
                int end = line.IndexOf(value: '"', startIndex: at + 2);
                if (end < 0)
                {
                    continue;
                }

                sym = line.Substring(startIndex: at + 1,
                    length: end - at); // includes surrounding quotes
            }
            else
            {
                int end = line.IndexOf(value: '(', startIndex: at);
                if (end < 0)
                {
                    continue;
                }

                sym = line.Substring(startIndex: at + 1, length: end - at - 1)
                          .Trim();
            }

            set.Add(item: sym);
        }

        return set;
    }

    /// <summary>The normal whole-program (pruned) build — what ships today.</summary>
    private static string PrunedBuild(AnalysisResult r)
    {
        return new LlvmEmitter(userPrograms: r.Registry.UserPrograms,
            registry: r.Registry,
            options: new LlvmEmitterOptions
            {
                StdlibPrograms = r.Registry.StdlibPrograms,
                SynthesizedBodies = r.SynthesizedBodies,
                InstantiatedGenericBodies = r.InstantiatedGenericBodies,
                LiveRoutineKeys = r.LiveRoutineKeys,
                MaySuspendRoutineKeys = r.MaySuspendRoutineKeys
            }).Generate();
    }

    /// <summary>The delta build: user code with the base's symbols marked resident (⇒ declare, not define).</summary>
    private static string DeltaBuild(AnalysisResult r, IReadOnlyCollection<string> residentSymbols)
    {
        return new LlvmEmitter(userPrograms: r.Registry.UserPrograms,
            registry: r.Registry,
            options: new LlvmEmitterOptions
            {
                StdlibPrograms = r.Registry.StdlibPrograms,
                SynthesizedBodies = r.SynthesizedBodies,
                InstantiatedGenericBodies = r.InstantiatedGenericBodies,
                LiveRoutineKeys = r.LiveRoutineKeys,
                MaySuspendRoutineKeys = r.MaySuspendRoutineKeys,
                ResidentSymbols = residentSymbols
            }).Generate();
    }

    /// <summary>
    /// Resident-JIT incremental (B) lazy-on-demand M1 SPIKE — the "emit ONE routine on demand" primitive.
    /// Proves an LlvmEmitter fed a SINGLE instantiated body (+ residentSymbols = every OTHER emitted symbol)
    /// produces a valid module that DEFINES exactly that one routine and DECLARES its callees extern — the
    /// per-symbol module an ORC custom definition generator will materialize on an unresolved-symbol callback.
    /// </summary>
    [Fact]
    public void EmitOneRoutine_DefinesTargetDeclaresCallees()
    {
        AnalysisResult r =
            new SemanticVerifier(language: Language.RazorForge).Analyze(
                program: Parse(src: Trivial, file: "bench.rf"));
        Assert.Empty(collection: r.Errors);
        Builder.Lowering.Passes.CancellationInstrumentationPass.Run(
            programs: r.Registry.UserPrograms,
            instantiatedBodies: r.InstantiatedGenericBodies,
            maySuspendKeys: r.MaySuspendRoutineKeys,
            registry: r.Registry);

        // The full reachable set (bare mangled names) — the "everything already materialized" universe.
        (string _, IReadOnlyCollection<string> allDefs) = new LlvmEmitter(
            userPrograms: new List<(Program, string, string)>(),
            registry: r.Registry,
            options: new LlvmEmitterOptions
            {
                StdlibPrograms = r.Registry.StdlibPrograms,
                SynthesizedBodies = r.SynthesizedBodies,
                InstantiatedGenericBodies = r.InstantiatedGenericBodies
            }).GenerateBase();
        var allDefsSet = new HashSet<string>(collection: allDefs, comparer: StringComparer.Ordinal);

        // Pick one real (non-synthesized, non-sentinel) stdlib body to materialize alone.
        KeyValuePair<string, MonomorphizedBody> target = r.InstantiatedGenericBodies.First(
            predicate: kv => !kv.Value.IsSynthesized &&
                             kv.Value.Ast.Body is BlockStatement { Statements.Count: > 0 } &&
                             kv.Value.Info.OwnerType is not { IsGenericDefinition: true } &&
                             allDefsSet.Contains(item: LlvmEmitter.MangleRoutineName(routine: kv.Value.Info)));
        string targetName = LlvmEmitter.MangleRoutineName(routine: target.Value.Info);

        // residentSymbols = every OTHER emitted symbol ⇒ this module defines ONLY the target.
        var resident = new HashSet<string>(collection: allDefs, comparer: StringComparer.Ordinal);
        resident.Remove(item: targetName);

        string oneIr = new LlvmEmitter(userPrograms: new List<(Program, string, string)>(),
            registry: r.Registry,
            options: new LlvmEmitterOptions
            {
                StdlibPrograms = r.Registry.StdlibPrograms,
                SynthesizedBodies = r.SynthesizedBodies,
                InstantiatedGenericBodies =
                    new Dictionary<string, MonomorphizedBody> { [key: target.Key] = target.Value },
                ResidentSymbols = resident
            }).Generate();

        HashSet<string> defs = DefinedSymbols(ll: oneIr);
        // RF-mangled defines are quoted (`@"[member] …"`); bare-name defines (_rf_trace_push, …) are the
        // shared-runtime shadow-stack HELPERS the emitter inlines into every module regardless of the body
        // set. Those are the GLOBALS-analogue-of-C4 concern (define once in the main module, extern in the
        // per-routine on-demand modules) — handled at the ORC wiring step, NOT part of the one-routine body set.
        var rfDefs = defs.Where(predicate: d => d.StartsWith(value: '"')).ToList();
        _out.WriteLine(message: $"target={targetName}");
        _out.WriteLine(message: $"one-routine module: rf-defines={rfDefs.Count} (bare-runtime={defs.Count - rfDefs.Count}), chars={oneIr.Length}");
        _out.WriteLine(message: "RF-DEFINES:\n  " + string.Join(separator: "\n  ", values: rfDefs));

        // The primitive defines EXACTLY the target RF routine, nothing resident re-defined, no @main.
        Assert.DoesNotContain(expectedSubstring: "define i32 @main(", actualString: oneIr);
        Assert.Single(collection: rfDefs);
        Assert.Equal(expected: targetName.Trim(trimChar: '"'),
            actual: rfDefs[index: 0].Trim(trimChar: '"'));
    }

    [Fact]
    public void GenerateBase_And_Delta_CoverPrunedBuild_WithTinyDelta()
    {
        // Cold analyze retains StdlibPrograms (the warm/snapshot path drops them).
        AnalysisResult r =
            new SemanticVerifier(language: Language.RazorForge).Analyze(
                program: Parse(src: Trivial, file: "bench.rf"));
        Assert.Empty(collection: r.Errors);
        Builder.Lowering.Passes.CancellationInstrumentationPass.Run(
            programs: r.Registry.UserPrograms,
            instantiatedBodies: r.InstantiatedGenericBodies,
            maySuspendKeys: r.MaySuspendRoutineKeys,
            registry: r.Registry);

        // BASE: empty user programs + null live set (⇒ non-pruned) + stdlib present. Runs to completion
        // over the full stdlib (the non-pruned-emission risk) and emits no @main.
        var baseGen = new LlvmEmitter(userPrograms: new List<(Program, string, string)>(),
            registry: r.Registry,
            options: new LlvmEmitterOptions
            {
                StdlibPrograms = r.Registry.StdlibPrograms,
                SynthesizedBodies = r.SynthesizedBodies,
                InstantiatedGenericBodies = r.InstantiatedGenericBodies
            });
        (string baseIr, IReadOnlyCollection<string> baseSyms) = baseGen.GenerateBase();

        Assert.False(condition: string.IsNullOrWhiteSpace(value: baseIr));
        Assert.DoesNotContain(expectedSubstring: "define i32 @main(", actualString: baseIr);
        Assert.True(condition: baseSyms.Count > 100,
            userMessage:
            $"base should emit hundreds of stdlib symbols non-pruned, got {baseSyms.Count}");

        // DELTA: user build with base symbols resident. C4 makes it declare (not define) resident symbols;
        // if the over-prune tripwire misfired on a resident symbol this would THROW here.
        string deltaIr = DeltaBuild(r: r, residentSymbols: baseSyms);

        HashSet<string> baseDefs = DefinedSymbols(ll: baseIr);
        HashSet<string> deltaDefs = DefinedSymbols(ll: deltaIr);
        HashSet<string> prunedDefs = DefinedSymbols(ll: PrunedBuild(r: r));

        // Correctness: base ∪ delta covers everything the pruned whole-program build defines.
        HashSet<string> union = new(collection: baseDefs, comparer: StringComparer.Ordinal);
        union.UnionWith(other: deltaDefs);
        var uncovered = prunedDefs.Except(second: union)
                                  .OrderBy(keySelector: s => s)
                                  .ToList();
        _out.WriteLine(
            message:
            $"base={baseDefs.Count}  delta={deltaDefs.Count}  pruned={prunedDefs.Count}  uncovered={uncovered.Count}");
        _out.WriteLine(message: "DELTA defines:\n  " + string.Join(separator: "\n  ",
            values: deltaDefs.OrderBy(keySelector: s => s)));
        if (uncovered.Count > 0)
        {
            _out.WriteLine(message: "UNCOVERED:\n  " +
                                    string.Join(separator: "\n  ",
                                        values: uncovered.Take(count: 40)));
        }

        Assert.True(condition: uncovered.Count == 0,
            userMessage: $"{uncovered.Count} pruned define(s) in NEITHER base nor delta: " +
                         string.Join(separator: ", ", values: uncovered.Take(count: 10)));

        // The payoff: the delta is tiny — user routines + the handful of user-triggered stdlib variants
        // (e.g. the show(value,end) overload the call site synthesizes), NOT the whole stdlib.
        Assert.True(condition: deltaDefs.Count < 40,
            userMessage:
            $"delta should be tiny (user + user-triggered variants), got {deltaDefs.Count}");
    }

    /// <summary>
    /// Phase 0a (a') JIT-COMPLETENESS oracle (see RESIDENT-JIT-INCREMENTAL-V0.5.md ★ MAJOR FINDING): the
    /// non-pruned base PARSES clean (bugs #1/#2/#3 fixed) yet still fails to JIT because it DECLARES far more
    /// than it DEFINES — a large transitive tail of instantiations/variants/derives the empty-program analyze
    /// never materialized. The coverage test misses this (it only checks base∪delta ⊇ the small PRUNED set).
    /// This oracle measures <c>declared − defined</c> directly, split into RF-mangled symbols (quoted — MUST
    /// be defined by the base or JIT-materialization fails) vs bare runtime externs (rf_/llvm./__/lowercase C
    /// — legitimately resolved from the runtime DLL, NOT the base's job to define). Drive the RF-mangled gap
    /// to zero to make the non-pruned base JIT-complete. Pure string analysis — no libLLVM, runs in CI.
    /// Skip'd until the (a') closure-materialization work lands (currently ~782 RF-mangled gap, dominated by
    /// const-generic Array[T,N]/from_literal variadic machinery + UnpackedFloat[U,UN] per-width transcendentals
    /// — generic instantiations the empty-program analyze never monomorphized). Run locally to measure the gap.
    /// With SeedAllStdlibRoutines + the LiveBackendType fix + removing dead list-BuilderQuery reflection
    /// routines, the gap is currently ~317 (down from 782). The remaining tail is a scattered set of real
    /// generic instantiations (const-generic Array[U&lt;w&gt;,N] trace-buffer support, List[enum].from_literal
    /// data tables, add_range, Maybe[BTreeNode].destroy) referenced by concrete stdlib bodies but not yet
    /// materialized — the genuine (a') closure tail, now free of BuilderQuery artifacts.
    /// </summary>
    [Fact(Skip =
        "WIP resident-JIT base define-completeness oracle. Base BUILDS at gap=102/defined=12309 (was 695). ISOLATED-BUILD primitive (GenericClosurePass.RunIsolatedTail, post-fixpoint, build-one-no-drain) closes: entity self-free tail (call-driven closure) + all per-type LIFECYCLE HOOKS (destroy/roam_free/roam_trace) built on every registered concrete+wrapper instance (bounded, self-contained; the call-driven closure discovers the leaf callees field.destroy/cyclic_visit/Hijacked.cyclic_trace_buffer; skip unfolded-buildtime Array[U8,${...}] carriers). Remaining 102 = represent(98)+serialize: represent is a force-seeded display closure that does NOT converge (its callees escape the registry — adding it took gap 140->367); needs delta-definition or codegen-materialize. base+delta coverage proven by GenerateBase_And_Delta_CoverPrunedBuild_WithTinyDelta. See .claude-memory/base-completeness-const-generic-array-gap.md.")]
    public void GenerateBase_Standalone_DefineCompleteness()
    {
        var baseSa =
            new SemanticVerifier(language: Language.RazorForge) { SeedAllStdlibRoutines = true };
        AnalysisResult baseR =
            baseSa.Analyze(program: Parse(src: "module Base\nroutine start()\n  return",
                file: "base.rf"));
        Assert.Empty(collection: baseR.Errors);
        Builder.Lowering.Passes.CancellationInstrumentationPass.Run(
            programs: baseR.Registry.UserPrograms,
            instantiatedBodies: baseR.InstantiatedGenericBodies,
            maySuspendKeys: baseR.MaySuspendRoutineKeys,
            registry: baseR.Registry);

        var baseGen = new LlvmEmitter(userPrograms: new List<(Program, string, string)>(),
            registry: baseR.Registry,
            options: new LlvmEmitterOptions
            {
                StdlibPrograms = baseR.Registry.StdlibPrograms,
                SynthesizedBodies = baseR.SynthesizedBodies,
                InstantiatedGenericBodies = baseR.InstantiatedGenericBodies
            });
        (string baseIr, _) = baseGen.GenerateBase();
        File.WriteAllText(path: Path.Combine(path1: Path.GetTempPath(), path2: "rf_base.ll"),
            contents: baseIr);

        HashSet<string> defined = DefinedSymbols(ll: baseIr);
        HashSet<string> declared = DeclaredSymbols(ll: baseIr);
        // gap = referenced-but-not-defined. A quoted symbol ("[member] ...", "$...") is RF-emitted code that
        // the base OWNS (must define); a bare ident is a runtime extern resolved from the runtime DLL.
        var gap = declared.Except(second: defined)
                          .OrderBy(keySelector: s => s)
                          .ToList();
        var rfGap = gap.Where(predicate: s => s.StartsWith(value: '"'))
                       .ToList();
        var externGap = gap.Where(predicate: s => !s.StartsWith(value: '"'))
                           .ToList();

        _out.WriteLine(
            message:
            $"base: defined={defined.Count} declared={declared.Count} gap={gap.Count} (rf-mangled={rfGap.Count}, bare-extern={externGap.Count})");
        // Category histogram: collapse arity/type args so we see the SHAPE of the gap, not 700 near-dupes.
        var byShape = rfGap.Select(selector: s => ArityDigitsRegex()
                               .Replace(input: s, replacement: "N"))
                           .GroupBy(keySelector: s => s)
                           .Select(selector: g => (Shape: g.Key, Count: g.Count()))
                           .OrderByDescending(keySelector: g => g.Count)
                           .ToList();
        _out.WriteLine(message: $"RF-MANGLED GAP by shape ({byShape.Count} distinct shapes):\n  " +
                                string.Join(separator: "\n  ",
                                    values: byShape.Take(count: 60)
                                                   .Select(
                                                        selector: g =>
                                                            $"{g.Count,4}  {g.Shape}")));
        _out.WriteLine(message: "RF-MANGLED GAP (raw, first 60):\n  " +
                                string.Join(separator: "\n  ", values: rfGap.Take(count: 60)));
        _out.WriteLine(message: "BARE-EXTERN GAP (runtime DLL resolves these — expected):\n  " +
                                string.Join(separator: "\n  ", values: externGap.Take(count: 40)));

        Assert.True(condition: rfGap.Count == 0,
            userMessage:
            $"{rfGap.Count} RF-mangled symbol(s) referenced but never defined by the non-pruned base " +
            $"(JIT would 'Failed to materialize' these): {string.Join(separator: ", ", values: rfGap.Take(count: 12))}");
    }

    /// <summary>
    /// Phase 0a (a') hardening harness: build a STANDALONE stdlib base (empty user program, so its
    /// instantiations are stdlib-internal only — no user-triggered pollution) and PARSE-validate the base
    /// IR. No native execution, so this isolates genuine malformed-emission bugs in non-pruned stdlib
    /// routines. NEEDS libLLVM staged next to the test binary (parse is an LLVM call) → Skip'd in CI.
    /// </summary>
    [Fact(Skip =
        "Local Phase 0a (a') hardening harness: needs libLLVM staged next to the test binary (parse is an LLVM call). Currently FAILS — non-pruned base surfaces a genuine stdlib-internal monomorphization gap: List[Bytes].duplicate() calls abstract Core.Copyable.duplicate() (ptr) instead of concrete Bytes.duplicate(). See RESIDENT-JIT doc Phase 0a finding.")]
    public void GenerateBase_Standalone_ParsesValid()
    {
        AnalysisResult baseR = new SemanticVerifier(language: Language.RazorForge).Analyze(
            program: Parse(src: "module Base\nroutine start()\n  return", file: "base.rf"));
        Assert.Empty(collection: baseR.Errors);
        Builder.Lowering.Passes.CancellationInstrumentationPass.Run(
            programs: baseR.Registry.UserPrograms,
            instantiatedBodies: baseR.InstantiatedGenericBodies,
            maySuspendKeys: baseR.MaySuspendRoutineKeys,
            registry: baseR.Registry);

        var baseGen = new LlvmEmitter(userPrograms: new List<(Program, string, string)>(),
            registry: baseR.Registry,
            options: new LlvmEmitterOptions
            {
                StdlibPrograms = baseR.Registry.StdlibPrograms,
                SynthesizedBodies = baseR.SynthesizedBodies,
                InstantiatedGenericBodies = baseR.InstantiatedGenericBodies
            });
        (string baseIr, IReadOnlyCollection<string> baseSyms) = baseGen.GenerateBase();

        bool ok = Builder.Execution.OrcJitExecutor.TryParseIr(llvmIr: baseIr, error: out string? err);
        _out.WriteLine(
            message:
            $"standalone base: syms={baseSyms.Count} chars={baseIr.Length} parse={(ok ? "OK" : err)}");
        Assert.True(condition: ok, userMessage: $"standalone base IR failed to parse: {err}");
    }

    /// <summary>
    /// Phase 0a step 3: actually JIT-and-run the base+delta split (single dylib, option-3 disposable
    /// client) and assert <c>@main</c> runs to a 0 exit. NEEDS <c>libLLVM.dll</c> staged next to the test
    /// binary AND runs the native RF runtime IN-PROCESS (scheduler threads, process-global runtime state),
    /// so it is Skip'd in CI/suite — run locally to validate execution.
    /// </summary>
    [Fact(Skip =
        "Local ORC-JIT run: base+delta JIT now PARSES clean (root declare fix); blocked on base DEFINE-completeness — main transitively needs the ~65 declared-but-undefined base symbols. See RESIDENT-JIT-INCREMENTAL-V0.5.md.")]
    public void JitAndRunSplit_BasePlusDelta_RunsMain()
    {
        // BASE: a full-closure analyze (SeedAllStdlibRoutines) so the resident base is JIT-complete —
        // it must DEFINE every stdlib symbol the delta will mark resident (extern-declare).
        var baseSa = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult baseR =
            baseSa.Analyze(program: Parse(src: "module Base\nroutine start()\n  return",
                file: "base.rf"));
        Assert.Empty(collection: baseR.Errors);
        Builder.Lowering.Passes.CancellationInstrumentationPass.Run(
            programs: baseR.Registry.UserPrograms,
            instantiatedBodies: baseR.InstantiatedGenericBodies,
            maySuspendKeys: baseR.MaySuspendRoutineKeys,
            registry: baseR.Registry);
        var baseGen = new LlvmEmitter(userPrograms: new List<(Program, string, string)>(),
            registry: baseR.Registry,
            options: new LlvmEmitterOptions
            {
                StdlibPrograms = baseR.Registry.StdlibPrograms,
                SynthesizedBodies = baseR.SynthesizedBodies,
                InstantiatedGenericBodies = baseR.InstantiatedGenericBodies
            });
        (string baseIr, IReadOnlyCollection<string> baseSyms) = baseGen.GenerateBase();

        // DELTA: the actual user program, with the base's symbols marked resident (⇒ extern declare).
        AnalysisResult r =
            new SemanticVerifier(language: Language.RazorForge).Analyze(
                program: Parse(src: Trivial, file: "bench.rf"));
        Assert.Empty(collection: r.Errors);
        Builder.Lowering.Passes.CancellationInstrumentationPass.Run(
            programs: r.Registry.UserPrograms,
            instantiatedBodies: r.InstantiatedGenericBodies,
            maySuspendKeys: r.MaySuspendRoutineKeys,
            registry: r.Registry);
        string deltaIr = DeltaBuild(r: r, residentSymbols: baseSyms);

        int rc = Builder.Execution.OrcJitExecutor.JitAndRunSplit(baseIr: baseIr,
            deltaIr: deltaIr,
            programName: "test",
            programArgs: Array.Empty<string>());
        _out.WriteLine(message: $"JitAndRunSplit exit code = {rc}");
        Assert.Equal(expected: 0, actual: rc);
    }

    /// <summary>
    /// Resident-JIT incremental (B) FULLY-LAZY on-demand M2a — proves `show("hi")` runs with NO pruning pass,
    /// NO base, and NO caller-side closure walk: a MAIN module (@main + user routines + trace globals; every
    /// stdlib callee an extern declare) plus an ORC custom definition generator that codegens each unresolved
    /// RF symbol's external-linkage one-routine module ON DEMAND as ORC resolves it. demand == liveness (only
    /// what runs is built — even fewer than the declare-closure). NEEDS libLLVM staged + runs the native
    /// runtime in-process → Skip'd in CI.
    /// </summary>
    [Fact(Skip =
        "Local ORC-JIT fully-lazy run: needs libLLVM staged + runs the native RF runtime in-process. PASSES locally — the ORC custom definition generator materializes each unresolved RF symbol's external-linkage one-routine module on demand; `show(\"hi\")` runs materializing only ~71 of ~360 routines (call-time lazy, no caller closure). See .claude-memory/resident-jit-pruned-base-works.md M2a.")]
    public void JitAndRunLazy_OnDemand_RunsMain()
    {
        AnalysisResult r =
            new SemanticVerifier(language: Language.RazorForge).Analyze(
                program: Parse(src: Trivial, file: "bench.rf"));
        Assert.Empty(collection: r.Errors);
        Builder.Lowering.Passes.CancellationInstrumentationPass.Run(
            programs: r.Registry.UserPrograms,
            instantiatedBodies: r.InstantiatedGenericBodies,
            maySuspendKeys: r.MaySuspendRoutineKeys,
            registry: r.Registry);

        // name → body index over every instantiated (stdlib/synthesized) body. A `declare`d symbol is the
        // UNQUOTED content of `@"..."`, while MangleRoutineName returns the IR-`@`-form that QUOTES names with
        // special chars — so key the index unquoted (to match declare-closure lookups) but keep the quoted form
        // for the emitter's resident set (IsResident compares against MangleRoutineName output).
        var index = new Dictionary<string, MonomorphizedBody>(comparer: StringComparer.Ordinal);
        var allQuoted = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (MonomorphizedBody b in r.InstantiatedGenericBodies.Values)
        {
            string mangled = LlvmEmitter.MangleRoutineName(routine: b.Info);
            index[key: mangled.Trim(trimChar: '"')] = b;
            allQuoted.Add(item: mangled);
        }

        // MAIN: @main + user routines + trace globals (defined, residentSymbols empty ⇒ deltaMode off); no
        // stdlib bodies (InstantiatedGenericBodies empty) ⇒ every stdlib callee is an extern declare.
        string mainIr = new LlvmEmitter(userPrograms: r.Registry.UserPrograms,
            registry: r.Registry,
            options: new LlvmEmitterOptions
            {
                StdlibPrograms = r.Registry.StdlibPrograms,
                SynthesizedBodies = r.SynthesizedBodies,
                InstantiatedGenericBodies = new Dictionary<string, MonomorphizedBody>(),
                LiveRoutineKeys = r.LiveRoutineKeys,
                MaySuspendRoutineKeys = r.MaySuspendRoutineKeys
            }).Generate();

        // materialize(name) → the ONE-routine external-linkage module defining `name` (or null when it isn't an
        // RF routine we own — rf_* runtime resolves via process-search). ForExternalJitModule makes the define
        // external (sibling modules can call it) and externs the shared trace globals (main owns them). The ORC
        // generator calls this ON DEMAND as it resolves each unresolved symbol — no caller-side closure walk.
        int materialized = 0;
        string? Materialize(string name)
        {
            if (!index.TryGetValue(key: name, value: out MonomorphizedBody? body))
            {
                return null;
            }

            var resident = new HashSet<string>(collection: allQuoted, comparer: StringComparer.Ordinal);
            resident.Remove(item: LlvmEmitter.MangleRoutineName(routine: body.Info));
            materialized++;
            return new LlvmEmitter(userPrograms: new List<(Program, string, string)>(),
                registry: r.Registry,
                options: new LlvmEmitterOptions
                {
                    StdlibPrograms = r.Registry.StdlibPrograms,
                    SynthesizedBodies = r.SynthesizedBodies,
                    InstantiatedGenericBodies =
                        new Dictionary<string, MonomorphizedBody> { [key: name] = body },
                    ResidentSymbols = resident,
                    ForExternalJitModule = true
                }).Generate();
        }

        int rc = Builder.Execution.OrcJitExecutor.JitAndRunLazy(mainIr: mainIr,
            materialize: Materialize,
            programName: "test",
            programArgs: Array.Empty<string>());

        // exit 0 (no crash, no "Symbols not found") + only the @main-reachable set materialized on demand
        // (demand == liveness: far fewer than the full set). The program's own `show("hi")` output goes to the
        // native runtime's stdout fd (bypasses C# Console), so the clean exit through the generator is the proof.
        _out.WriteLine(
            message:
            $"lazy-generator: {materialized} routines materialized on demand (of {index.Count} available), exit={rc}");
        Assert.Equal(expected: 0, actual: rc);
        Assert.True(condition: materialized > 0 && materialized < index.Count,
            userMessage:
            $"demand should materialize a SUBSET, got {materialized}/{index.Count}");
    }

    /// <summary>
    /// Resident-JIT incremental (B) M2b — the CROSS-RUN win: a per-routine on-disk IR cache
    /// (<see cref="Builder.Execution.RoutineIrCache"/>) makes the fully-lazy JIT reuse each pure-stdlib
    /// routine's one-routine module across runs instead of re-codegen'ing it every time (M2a re-emits every
    /// reached routine each run). Runs `show("hi")` TWICE sharing one cache dir: run 1 is cold (every
    /// cacheable routine codegen'd + written), run 2 is warm (served from disk, ZERO codegen). Both exit 0.
    /// NEEDS libLLVM staged + runs the native runtime in-process → Skip'd in CI.
    /// </summary>
    [Fact(Skip =
        "Local ORC-JIT M2b IR-cache run: needs libLLVM staged + runs the native RF runtime in-process. PASSES locally — run 2 serves every pure-stdlib routine from the disk IR cache (0 codegen) while run 1 populated it (run1: 71 codegen/71 miss; run2: 0 codegen/71 hit). See .claude-memory/resident-jit-pruned-base-works.md M2b.")]
    public void JitAndRunLazy_IrCache_ReusesAcrossRuns()
    {
        AnalysisResult r =
            new SemanticVerifier(language: Language.RazorForge).Analyze(
                program: Parse(src: Trivial, file: "bench.rf"));
        Assert.Empty(collection: r.Errors);
        Builder.Lowering.Passes.CancellationInstrumentationPass.Run(
            programs: r.Registry.UserPrograms,
            instantiatedBodies: r.InstantiatedGenericBodies,
            maySuspendKeys: r.MaySuspendRoutineKeys,
            registry: r.Registry);

        var index = new Dictionary<string, MonomorphizedBody>(comparer: StringComparer.Ordinal);
        var allQuoted = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (MonomorphizedBody b in r.InstantiatedGenericBodies.Values)
        {
            string mangled = LlvmEmitter.MangleRoutineName(routine: b.Info);
            index[key: mangled.Trim(trimChar: '"')] = b;
            allQuoted.Add(item: mangled);
        }

        string mainIr = new LlvmEmitter(userPrograms: r.Registry.UserPrograms,
            registry: r.Registry,
            options: new LlvmEmitterOptions
            {
                StdlibPrograms = r.Registry.StdlibPrograms,
                SynthesizedBodies = r.SynthesizedBodies,
                InstantiatedGenericBodies = new Dictionary<string, MonomorphizedBody>(),
                LiveRoutineKeys = r.LiveRoutineKeys,
                MaySuspendRoutineKeys = r.MaySuspendRoutineKeys
            }).Generate();

        string EmitOne(string name, MonomorphizedBody body)
        {
            var resident = new HashSet<string>(collection: allQuoted, comparer: StringComparer.Ordinal);
            resident.Remove(item: LlvmEmitter.MangleRoutineName(routine: body.Info));
            return new LlvmEmitter(userPrograms: new List<(Program, string, string)>(),
                registry: r.Registry,
                options: new LlvmEmitterOptions
                {
                    StdlibPrograms = r.Registry.StdlibPrograms,
                    SynthesizedBodies = r.SynthesizedBodies,
                    InstantiatedGenericBodies =
                        new Dictionary<string, MonomorphizedBody> { [key: name] = body },
                    ResidentSymbols = resident,
                    ForExternalJitModule = true
                }).Generate();
        }

        // A fresh per-test cache dir (fixed fingerprint — the two runs share the same stdlib/compiler).
        string cacheDir = Path.Combine(path1: Path.GetTempPath(),
            path2: "rf_jit_ir_cache_test_" + Guid.NewGuid().ToString(format: "N"));

        int RunOnce(Builder.Execution.RoutineIrCache cache, out int codegens)
        {
            int cg = 0;
            string? Materialize(string name)
            {
                if (!index.TryGetValue(key: name, value: out MonomorphizedBody? body))
                {
                    return null;
                }

                if (cache.TryGet(mangledName: name, ir: out string cached))
                {
                    return cached;
                }

                cg++;
                string ir = EmitOne(name: name, body: body);
                cache.Put(mangledName: name, ir: ir);
                return ir;
            }

            int rc = Builder.Execution.OrcJitExecutor.JitAndRunLazy(mainIr: mainIr,
                materialize: Materialize,
                programName: "test",
                programArgs: Array.Empty<string>());
            codegens = cg;
            return rc;
        }

        try
        {
            // Run 1 (cold): every cacheable routine is codegen'd + written; nothing served from disk.
            var cache1 = new Builder.Execution.RoutineIrCache(fingerprint: "test-fp",
                userModuleSegments: new[] { "Bench" }, dir: cacheDir);
            int rc1 = RunOnce(cache: cache1, codegens: out int cg1);
            Assert.Equal(expected: 0, actual: rc1);
            Assert.Equal(expected: 0, actual: cache1.Hits);
            Assert.True(condition: cache1.Misses > 0, userMessage: "run 1 should populate the cache");

            // Run 2 (warm): the same pure-stdlib routines are served from the disk cache — ZERO fresh codegen.
            var cache2 = new Builder.Execution.RoutineIrCache(fingerprint: "test-fp",
                userModuleSegments: new[] { "Bench" }, dir: cacheDir);
            int rc2 = RunOnce(cache: cache2, codegens: out int cg2);
            _out.WriteLine(
                message:
                $"run1: codegens={cg1} misses={cache1.Misses} uncacheable={cache1.Uncacheable}; " +
                $"run2: codegens={cg2} hits={cache2.Hits} uncacheable={cache2.Uncacheable}");
            Assert.Equal(expected: 0, actual: rc2);
            Assert.True(condition: cache2.Hits > 0, userMessage: "run 2 should reuse cached IR");
            // Every cacheable routine run 2 reached was a disk hit (no fresh codegen for cacheable ones);
            // cg2 counts only uncacheable re-emits, which must be < run 1's total codegen.
            Assert.True(condition: cg2 < cg1,
                userMessage: $"run 2 should codegen fewer than run 1 (cg2={cg2}, cg1={cg1})");
        }
        finally
        {
            if (Directory.Exists(path: cacheDir))
            {
                Directory.Delete(path: cacheDir, recursive: true);
            }
        }
    }
}
