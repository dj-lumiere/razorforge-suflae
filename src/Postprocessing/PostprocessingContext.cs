using System.Collections.Generic;
using Compiler.Resolution;
using Compiler.Targeting;
using SyntaxTree;

namespace Compiler.Postprocessing;

/// <summary>
/// Shared context for Phase 7 postprocessing work.
/// </summary>
public sealed class PostprocessingContext
{
    /// <summary>
    /// Semantic registry used for type and routine lookups while lowering.
    /// </summary>
    public TypeRegistry Registry { get; }

    /// <summary>
    /// Target platform information used when lowering platform-dependent constructs.
    /// </summary>
    public TargetConfig Target { get; }

    /// <summary>
    /// Build mode used by BuilderService and other compile-time metadata lowering.
    /// </summary>
    public RfBuildMode BuildMode { get; }

    /// <summary>
    /// Pre-transformed bodies for error-handling variant routines, keyed by RoutineInfo.RegistryKey.
    /// Written by Phase 4 ErrorHandlingVariantPass; Phase 7 passes lower their expressions in-place.
    /// </summary>
    public Dictionary<string, Statement> VariantBodies { get; }

    /// <summary>
    /// AST bodies for compiler-generated derived operator routines (ne, lt, le, gt, ge, notcontains),
    /// keyed by RoutineInfo.RegistryKey. Written by Phase 2.6 DerivedOperatorPass.
    /// Phase 7 CallOverloadResolutionPass runs on these to classify all CallExpression nodes.
    /// </summary>
    public Dictionary<string, Statement>? SynthesizedBodies { get; }

    /// <summary>
    /// Monomorphized generic routine instances (keyed by RegistryKey). Exposed so late lowering passes
    /// (e.g. VariantReturnLoweringPass) can rewrite carrier-return sites inside concrete instances too.
    /// </summary>
    public Dictionary<string, Compiler.Instantiation.MonomorphizedBody>? MonomorphizedBodies { get; }

    /// <summary>
    /// Initializes shared state for the Phase 7 lowering pipeline.
    /// </summary>
    public PostprocessingContext(TypeRegistry registry,
        Dictionary<string, Statement>? variantBodies = null,
        Dictionary<string, Statement>? synthesizedBodies = null,
        TargetConfig? target = null,
        RfBuildMode buildMode = RfBuildMode.Debug,
        Dictionary<string, Compiler.Instantiation.MonomorphizedBody>? monomorphizedBodies = null)
    {
        Registry = registry;
        VariantBodies = variantBodies ?? new Dictionary<string, Statement>();
        SynthesizedBodies = synthesizedBodies;
        Target = target ?? TargetConfig.ForCurrentHost();
        BuildMode = buildMode;
        MonomorphizedBodies = monomorphizedBodies;
    }
}
