namespace Compiler.CodeGen;

using System.Text;
using SyntaxTree;

public partial class LlvmCodeGenerator
{
    private bool EmitIf(StringBuilder sb, IfStatement ifStmt)
    {
        string condition = EmitExpression(sb: sb, expr: ifStmt.Condition);

        string thenLabel = NextLabel(prefix: "if_then");
        string endLabel = NextLabel(prefix: "if_end");

        if (ifStmt.ElseBranch != null)
        {
            string elseLabel = NextLabel(prefix: "if_else");
            EmitLine(sb: sb, line: $"  br i1 {condition}, label %{thenLabel}, label %{elseLabel}");

            // Then branch
            EmitLine(sb: sb, line: $"{thenLabel}:");
            bool thenTerminated = EmitStatement(sb: sb, stmt: ifStmt.ThenBranch);
            if (!thenTerminated)
            {
                EmitLine(sb: sb, line: $"  br label %{endLabel}");
            }

            // Else branch
            EmitLine(sb: sb, line: $"{elseLabel}:");
            bool elseTerminated = EmitStatement(sb: sb, stmt: ifStmt.ElseBranch);
            if (!elseTerminated)
            {
                EmitLine(sb: sb, line: $"  br label %{endLabel}");
            }

            // If both branches terminated, the end block is unreachable
            // but we still need to emit it for LLVM (it will be dead code eliminated)
            if (thenTerminated && elseTerminated)
            {
                // Both branches return - the if statement as a whole terminates
                // Emit end label + unreachable (dead block must still have a terminator)
                EmitLine(sb: sb, line: $"{endLabel}:");
                EmitLine(sb: sb, line: $"  unreachable");
                return true;
            }

            // End block is reachable from at least one branch
            EmitLine(sb: sb, line: $"{endLabel}:");
            return false;
        }
        else
        {
            EmitLine(sb: sb, line: $"  br i1 {condition}, label %{thenLabel}, label %{endLabel}");

            // Then branch
            EmitLine(sb: sb, line: $"{thenLabel}:");
            bool thenTerminated = EmitStatement(sb: sb, stmt: ifStmt.ThenBranch);
            if (!thenTerminated)
            {
                EmitLine(sb: sb, line: $"  br label %{endLabel}");
            }

            // End block (always reachable via the else path, even if then returns)
            EmitLine(sb: sb, line: $"{endLabel}:");
            return false; // If without else never fully terminates
        }
    }

    /// <summary>
    /// Stack of loop labels for break/continue.
    /// </summary>
    private readonly Stack<(string ContinueLabel, string BreakLabel)> _loopStack = new();

    /// <summary>
    /// Emits code for a loop statement (infinite loop primitive).
    /// Unconditional back-edge: continue → loop header, break → end.
    /// </summary>
    private void EmitLoop(StringBuilder sb, LoopStatement loopStmt)
    {
        string bodyLabel = NextLabel(prefix: "loop_body");
        string endLabel = NextLabel(prefix: "loop_end");

        // Push loop labels: continue → body header, break → end
        _loopStack.Push(item: (bodyLabel, endLabel));

        // Jump to body
        EmitLine(sb: sb, line: $"  br label %{bodyLabel}");

        // Body block
        EmitLine(sb: sb, line: $"{bodyLabel}:");
        bool bodyTerminated = EmitStatement(sb: sb, stmt: loopStmt.Body);
        if (!bodyTerminated)
        {
            EmitLine(sb: sb, line: $"  br label %{bodyLabel}");
        }

        // End block
        EmitLine(sb: sb, line: $"{endLabel}:");

        _loopStack.Pop();
    }


    /// <summary>
    /// Emits code for a break statement.
    /// </summary>
    private void EmitBreak(StringBuilder sb)
    {
        if (_loopStack.Count == 0)
        {
            throw new InvalidOperationException(message: "Break statement outside of loop");
        }

        (_, string breakLabel) = _loopStack.Peek();
        EmitLine(sb: sb, line: $"  br label %{breakLabel}");
    }

    /// <summary>
    /// Emits code for a continue statement.
    /// </summary>
    private void EmitContinue(StringBuilder sb)
    {
        if (_loopStack.Count == 0)
        {
            throw new InvalidOperationException(message: "Continue statement outside of loop");
        }

        (string continueLabel, _) = _loopStack.Peek();
        EmitLine(sb: sb, line: $"  br label %{continueLabel}");
    }
}
