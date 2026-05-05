using Compiler.Synthesis;

namespace Verification;

public sealed partial class SemanticVerifier
{
    #region Phase 2.55: Auto-Register Builder-Generated Member Routines

    private void AutoRegisterWiredRoutines()
    {
        new AutoWiredRegistrationPass(_registry).Run();
    }

    #endregion
}
