using TypeModel.Symbols;

namespace Verification;

/// <summary>
/// Shared call-classification logic used by both the semantic verifier (during SA) and
/// <see cref="Compiler.Postprocessing.Passes.CallOverloadResolutionPass"/> (post-instantiation).
/// Centralised here so the two phases always agree on which <see cref="CallLoweringKind"/>
/// a resolved routine maps to.
/// </summary>
internal static class CallClassifier
{
    /// <summary>
    /// Performs the classify standalone routine call step for this compiler phase.
    /// </summary>
    internal static CallLoweringKind ClassifyStandaloneRoutineCall(RoutineInfo routine)
    {
        if (routine.LlvmIrTemplate != null)
            return CallLoweringKind.LlvmIntrinsic;

        if (routine.IsSynthesized && BuilderInfoProvider.IsBuilderServiceStandalone(name: routine.Name))
            return CallLoweringKind.BuilderIntrinsic;

        return CallLoweringKind.DirectRoutine;
    }

    /// <summary>
    /// Performs the classify method call step for this compiler phase.
    /// </summary>
    internal static CallLoweringKind ClassifyMethodCall(RoutineInfo method)
    {
        if (method.LlvmIrTemplate != null)
            return CallLoweringKind.LlvmIntrinsic;

        if (method.IsSynthesized && BuilderInfoProvider.IsBuilderServiceRoutine(name: method.Name))
            return CallLoweringKind.BuilderIntrinsic;

        return CallLoweringKind.DirectMemberRoutine;
    }
}
