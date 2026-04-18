using Compiler.Resolution;
using Compiler.Targeting;
using SemanticVerification;
using SyntaxTree;

namespace Compiler.Desugaring;

/// <summary>
/// Shared context for all desugaring passes.
/// </summary>
public sealed class DesugaringContext
{
    /// <summary>The type registry from semantic analysis.</summary>
    public TypeRegistry Registry { get; }

    /// <summary>
    /// Routine bodies collected during Phase 5 body analysis, keyed by RoutineInfo.RegistryKey.
    /// Used by <see cref="Passes.ErrorHandlingVariantPass"/> to generate try_/check_/lookup_ variants.
    /// </summary>
    public IReadOnlyDictionary<string, Statement> RoutineBodies { get; }

    /// <summary>
    /// Pre-transformed bodies for error-handling variant routines, keyed by the variant
    /// RoutineInfo.RegistryKey. Written by <see cref="Passes.ErrorHandlingVariantPass"/>
    /// and consumed by codegen so that variant functions emit carrier construction without
    /// relying on mutable flag fields.
    /// </summary>
    public Dictionary<string, Statement> VariantBodies { get; } = new();

    /// <summary>
    /// Pre-rewritten monomorphized bodies produced by <see cref="Passes.GenericMonomorphizationPass"/>,
    /// keyed by the concrete <see cref="SemanticVerification.Symbols.RoutineInfo.RegistryKey"/>.
    /// Codegen checks this map before doing its own AST search and rewriting, so most
    /// generic method bodies are ready before the first IR line is emitted.
    /// </summary>
    public Dictionary<string, MonomorphizedBody> PreMonomorphizedBodies { get; } = new();

    /// <summary>Target platform — drives BuilderService platform constants.</summary>
    public TargetConfig Target { get; }

    /// <summary>Build mode — drives BuilderService.build_mode.</summary>
    public RfBuildMode BuildMode { get; }

    public DesugaringContext(TypeRegistry registry,
        IReadOnlyDictionary<string, Statement> routineBodies,
        TargetConfig? target = null,
        RfBuildMode buildMode = RfBuildMode.Debug)
    {
        Registry = registry;
        RoutineBodies = routineBodies;
        Target = target ?? TargetConfig.ForCurrentHost();
        BuildMode = buildMode;
    }
}
