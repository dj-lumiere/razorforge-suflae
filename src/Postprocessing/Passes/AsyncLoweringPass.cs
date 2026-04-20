using SyntaxTree;

namespace Compiler.Postprocessing.Passes;

/// <summary>
/// Future Phase 7 pass: lower threaded/suspended/waitfor constructs into explicit
/// runtime-oriented IR-friendly AST forms.
/// </summary>
internal sealed class AsyncLoweringPass(PostprocessingContext ctx)
{
    public void Run(Program program)
    {
        // Remaining work:
        // Lower waitfor/after/within and coroutine state-machine forms after
        // liveness and verification information are available.
    }
}
