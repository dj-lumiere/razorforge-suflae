using System.Collections.Generic;
using System.Linq;
using Compiler.Instantiation;
using Compiler.Resolution;
using Compiler.Targeting;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;

namespace Verification;

public partial class SemanticVerifier
{
    /// <summary>
    /// Fully-processed stdlib state captured after a COMPLETE compile (Phases 1–7) of a minimal
    /// program. Restoring it lets a warm compile reuse the lowered / synthesized / monomorphized stdlib
    /// instead of reprocessing it every run (~5 s → ms). Holds the post-Phase-7 registry snapshot, the
    /// lowered stdlib program ASTs (read-only after lowering — safe to share across warm compiles), and
    /// the body dictionaries codegen consumes. This is the in-RAM state a compile daemon holds.
    /// </summary>
    public sealed class CompiledStdlibState
    {
        public required Language Language { get; init; }
        public required TypeRegistry.StdlibSnapshot Registry { get; init; }
        public required List<(Program Program, string FilePath, string Module)> StdlibPrograms { get; init; }
        public required Dictionary<string, (RoutineInfo Routine, Statement Body)> SynthesizedBodies { get; init; }
        public required Dictionary<string, Statement> VariantBodies { get; init; }
        public required Dictionary<string, MonomorphizedBody> InstantiatedGenericBodies { get; init; }
        public required Dictionary<string, Statement> RoutineBodies { get; init; }
    }

    /// <summary>
    /// Runs one full compile of a minimal program to fully process the stdlib, then captures the
    /// result. Call once (e.g. at daemon startup); reuse via the restore constructor for warm compiles.
    /// </summary>
    public static CompiledStdlibState CaptureCompiledStdlib(Language language)
    {
        var sa = new SemanticVerifier(language: language);
        var tokens = new Compiler.Tokenizer.Tokenizer(source: "module __snapshot__",
            fileName: "__snapshot__", language: language).Tokenize();
        var parser = new Compiler.Parser.Parser(tokens: tokens, language: language,
            fileName: "__snapshot__");
        sa.Analyze(program: parser.Parse());
        return sa.CaptureCompiledState();
    }

    private CompiledStdlibState CaptureCompiledState() => new()
    {
        Language = _registry.Language,
        Registry = _registry.CaptureSnapshot(),
        StdlibPrograms = new List<(Program, string, string)>(_registry.StdlibPrograms),
        SynthesizedBodies = new Dictionary<string, (RoutineInfo, Statement)>(_synthesizedBodies),
        VariantBodies = new Dictionary<string, Statement>(_variantBodies),
        InstantiatedGenericBodies =
            _instantiatedGenericBodies.ToDictionary(keySelector: kv => kv.Key, elementSelector: kv => kv.Value),
        RoutineBodies = new Dictionary<string, Statement>(_routineBodies),
    };

    /// <summary>
    /// Constructs a verifier pre-warmed from a full compiled-stdlib snapshot. Restores the lowered
    /// stdlib programs + body dicts and marks the registry to SKIP stdlib reprocessing, so a subsequent
    /// full <see cref="Analyze"/> only processes the user program (+ its incremental instantiations) and
    /// can codegen without redoing the ~5 s of stdlib desugaring/verification/monomorphization.
    /// </summary>
    public SemanticVerifier(Language language, CompiledStdlibState warm,
        TargetConfig? target = null, RfBuildMode buildMode = RfBuildMode.Debug)
    {
        _registry = new TypeRegistry(language: language, snapshot: warm.Registry);
        _registry.RestoreStdlibPrograms(programs: warm.StdlibPrograms);
        _registry.SkipStdlibReprocessing = true;
        _typeResolver = new TypeResolver(sa: this);
        _typeBodyResolver = new TypeBodyResolver(sa: this, typeResolver: _typeResolver);
        _signatureResolver = new SignatureResolver(sa: this, typeResolver: _typeResolver);
        _conformanceAnalyzer = new ProtocolConformanceAnalyzer(sa: this);
        _target = target ?? TargetConfig.ForCurrentHost();
        _buildMode = buildMode;
        _snapshotMode = true;

        // Seed the body dicts: the global synthesis passes skip already-present (stdlib) entries
        // (WiredRoutinePass / ErrorHandlingVariantPass gate on ContainsKey), and codegen consumes them.
        foreach (var kv in warm.SynthesizedBodies) _synthesizedBodies[kv.Key] = kv.Value;
        foreach (var kv in warm.RoutineBodies) _routineBodies[kv.Key] = kv.Value;
        _variantBodies = new Dictionary<string, Statement>(warm.VariantBodies);
        _instantiatedGenericBodies =
            new Dictionary<string, MonomorphizedBody>(warm.InstantiatedGenericBodies);
    }
}
