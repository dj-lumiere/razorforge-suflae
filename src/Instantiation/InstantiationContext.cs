using Compiler.Resolution;
using Compiler.Targeting;
using SyntaxTree;

namespace Compiler.Instantiation;

/// <summary>
/// Shared context for Phase 6 generic instantiation work.
/// This is scaffold-only for now; existing monomorphization still lives elsewhere.
/// </summary>
public sealed class InstantiationContext
{
    public TypeRegistry Registry { get; }

    public IReadOnlyList<(Program Program, string FilePath, string Module)> UserPrograms { get; }

    public IReadOnlyDictionary<string, Statement> RoutineBodies { get; }

    public Dictionary<string, Statement> VariantBodies { get; }

    public Dictionary<string, MonomorphizedBody> PreMonomorphizedBodies { get; }

    public TargetConfig Target { get; }

    public RfBuildMode BuildMode { get; }

    /// <summary>
    /// Canonical keys for reachable concrete generic types discovered during collection.
    /// </summary>
    public HashSet<string> ReachableGenericTypes { get; } = [];

    /// <summary>
    /// Canonical keys for reachable concrete generic routines discovered during collection.
    /// </summary>
    public HashSet<string> ReachableGenericRoutines { get; } = [];

    public InstantiationContext(TypeRegistry registry,
        IReadOnlyList<(Program Program, string FilePath, string Module)> userPrograms,
        IReadOnlyDictionary<string, Statement> routineBodies,
        Dictionary<string, Statement>? variantBodies = null,
        Dictionary<string, MonomorphizedBody>? preMonomorphizedBodies = null,
        TargetConfig? target = null,
        RfBuildMode buildMode = RfBuildMode.Debug)
    {
        Registry = registry;
        UserPrograms = userPrograms;
        RoutineBodies = routineBodies;
        VariantBodies = variantBodies ?? [];
        PreMonomorphizedBodies = preMonomorphizedBodies ?? [];
        Target = target ?? TargetConfig.ForCurrentHost();
        BuildMode = buildMode;
    }
}
