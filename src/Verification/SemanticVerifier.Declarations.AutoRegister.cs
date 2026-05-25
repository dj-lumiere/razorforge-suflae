using Compiler.Synthesis;
using SyntaxTree;

namespace Verification;

public sealed partial class SemanticVerifier
{
    #region Phase 2.55: Auto-Register Builder-Generated Member Routines

    private void AutoRegisterWiredRoutines()
    {
        // Detect whether any user program imports BuilderService. When absent we skip
        // resolving List[FieldInfo]/List[ProtocolInfo]/List[RoutineInfo]/Dict[Text,Data];
        // those resolutions otherwise drag in the full BTreeListNode/Owned/Array/
        // ArrayIterator closure for every type via GMP, even when the user never calls
        // a BuilderService routine.
        bool builderServiceImported = false;
        foreach ((Program program, _, _) in _registry.UserPrograms)
        {
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                if (node is ImportDeclaration { ModulePath: "BuilderService" })
                {
                    builderServiceImported = true;
                    break;
                }
            }
            if (builderServiceImported) break;
        }

        new AutoWiredRegistrationPass(_registry).Run(builderServiceImported: builderServiceImported);
    }

    #endregion
}
