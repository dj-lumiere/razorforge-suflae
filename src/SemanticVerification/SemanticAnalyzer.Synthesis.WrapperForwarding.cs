namespace SemanticVerification;

using Compiler.Synthesis;
using TypeModel.Symbols;
using TypeModel.Types;
using TypeSymbol = TypeModel.Types.TypeInfo;

/// <summary>
/// Phase D synthesizer: delegates to <see cref="WrapperForwardingPass"/> for
/// transparent-forwarding routine synthesis on wrapper types.
/// </summary>
public sealed partial class SemanticAnalyzer
{
    /// <summary>Keyed by (wrapperDefName, methodName, isFailable) — caches synthesized forwarders.</summary>
    private readonly HashSet<string> _synthesizedForwarderKeys = [];

    /// <summary>Lazily initialized pass instance, shared across eager and lazy synthesis calls.</summary>
    private WrapperForwardingPass? _wrapperForwardingPass;

    private WrapperForwardingPass GetOrCreateWrapperForwardingPass()
    {
        return _wrapperForwardingPass ??= new WrapperForwardingPass(
            _registry, _synthesizedBodies, _synthesizedForwarderKeys);
    }

    /// <summary>
    /// Eagerly synthesizes forwarders on all concrete wrapper-type instantiations for every
    /// method found on their inner type.  Called after stdlib body analysis so that wrapper
    /// methods used only implicitly (e.g. release() via scope cleanup) are still forwarded.
    /// </summary>
    private void EagerSynthesizeAllWrapperForwarders()
    {
        GetOrCreateWrapperForwardingPass().RunEager();
    }

    /// <summary>
    /// Attempts to synthesize a forwarding routine on a wrapper type that delegates to
    /// a matching method on the wrapper's inner type T.
    /// </summary>
    private RoutineInfo? TrySynthesizeWrapperForwarder(TypeSymbol wrapperType,
        string methodName, bool isFailable)
    {
        return GetOrCreateWrapperForwardingPass().TrySynthesize(
            wrapperType: wrapperType,
            methodName: methodName,
            isFailable: isFailable);
    }
}
