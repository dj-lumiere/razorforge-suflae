namespace Compiler.Instantiation.Passes;

/// <summary>
/// Future Phase 6 pass: normalize wrapper/entity-specialized generic resolutions so
/// later phases see one canonical concrete representation.
/// </summary>
internal sealed class GenericCanonicalizationPass(InstantiationContext ctx)
{
    public void Run()
    {
        // Remaining work:
        // Canonicalize duplicate concrete identities (wrapper facade vs record def,
        // entity specialization vs primary generic definition, etc.).
    }
}
