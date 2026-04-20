namespace SemanticVerification;

using Compiler.Synthesis;

public sealed partial class SemanticAnalyzer
{
    #region Phase 2.55: Auto-Register Builder-Generated Member Routines

    private void AutoRegisterWiredRoutines()
    {
        new AutoWiredRegistrationPass(_registry).Run();
    }

    #endregion
}
