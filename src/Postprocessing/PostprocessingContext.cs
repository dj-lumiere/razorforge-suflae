using Compiler.Resolution;
using Compiler.Targeting;
using SyntaxTree;

namespace Compiler.Postprocessing;

/// <summary>
/// Shared context for Phase 7 postprocessing work.
/// </summary>
public sealed class PostprocessingContext
{
    public TypeRegistry Registry { get; }

    public TargetConfig Target { get; }

    public RfBuildMode BuildMode { get; }

    /// <summary>
    /// Pre-transformed bodies for error-handling variant routines, keyed by RoutineInfo.RegistryKey.
    /// Written by Phase 4 ErrorHandlingVariantPass; Phase 7 passes lower their expressions in-place.
    /// </summary>
    public Dictionary<string, Statement> VariantBodies { get; }

    public PostprocessingContext(TypeRegistry registry,
        Dictionary<string, Statement>? variantBodies = null,
        TargetConfig? target = null,
        RfBuildMode buildMode = RfBuildMode.Debug)
    {
        Registry = registry;
        VariantBodies = variantBodies ?? new Dictionary<string, Statement>();
        Target = target ?? TargetConfig.ForCurrentHost();
        BuildMode = buildMode;
    }
}
