using TypeModel.Enums;
using Verification;

namespace RazorForge.Tests.Meta;

/// <summary>
/// Validates that all stdlib routine bodies pass semantic verification.
/// Mirrors the `validate-stdlib` CLI command.
/// </summary>
public sealed class StdlibValidationTests
{
    [Fact]
    public void RazorForge_Stdlib_Validates()
    {
        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        var errors = analyzer.ValidateStdlibBodies();
        Assert.Empty(errors);
    }

    [Fact]
    public void Suflae_Stdlib_Validates()
    {
        var analyzer = new SemanticVerifier(language: Language.Suflae);
        var errors = analyzer.ValidateStdlibBodies();
        Assert.Empty(errors);
    }
}