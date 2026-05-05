using Compiler.Desugaring;
using Compiler.Instantiation;
using Compiler.Resolution;
using SyntaxTree;
using TypeModel.Types;

namespace Compiler.Postprocessing.Passes;

/// <summary>
/// Phase 6b: scans all program bodies for runtime dispatch call sites and pre-registers
/// the required dispatch stubs in a dictionary.
/// Running this before codegen means codegen discovers NO new stubs during emit — stub
/// registration is owned by this phase, not the IR emitter.
/// </summary>
public sealed class RuntimeDispatchRegistrationPass(TypeRegistry registry)
{
    /// <summary>
    /// Dispatch stubs keyed by protocol full name and method name.
    /// </summary>
    private readonly Dictionary<string, RuntimeDispatchEntry> _result = new();

    /// <summary>
    /// Scans user, stdlib, synthesized, and monomorphized bodies for protocol-dispatch call sites.
    /// </summary>
    public IReadOnlyDictionary<string, RuntimeDispatchEntry> Run(
        IEnumerable<(Program Program, string Path, string Module)> userPrograms,
        IReadOnlyDictionary<string, Statement> variantBodies,
        IReadOnlyDictionary<string, MonomorphizedBody> instantiatedGenericBodies)
    {
        foreach ((Program program, _, _) in userPrograms)
            ScanProgram(program);

        foreach ((Program program, _, _) in registry.StdlibPrograms)
            ScanProgram(program);

        foreach ((_, Statement body) in variantBodies)
            ScanStatement(body);

        foreach ((_, MonomorphizedBody mono) in instantiatedGenericBodies)
            ScanStatement(mono.Ast.Body);

        return _result;
    }

    /// <summary>
    /// Scans routine bodies declared directly in one parsed program.
    /// </summary>
    private void ScanProgram(Program program)
    {
        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            if (node is RoutineDeclaration routine)
                ScanStatement(routine.Body);
        }
    }

    /// <summary>
    /// Recursively scans statements that can contain call expressions requiring runtime dispatch.
    /// </summary>
    private void ScanStatement(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement block:
                foreach (Statement s in block.Statements) ScanStatement(s);
                break;
            case ExpressionStatement es:
                ScanExpression(es.Expression);
                break;
            case ReturnStatement ret:
                if (ret.Value != null) ScanExpression(ret.Value);
                break;
            case AssignmentStatement assign:
                ScanExpression(assign.Target);
                ScanExpression(assign.Value);
                break;
            case DeclarationStatement { Declaration: VariableDeclaration vd }:
                if (vd.Initializer != null) ScanExpression(vd.Initializer);
                break;
            case IfStatement ifs:
                ScanExpression(ifs.Condition);
                ScanStatement(ifs.ThenBranch);
                if (ifs.ElseBranch != null) ScanStatement(ifs.ElseBranch);
                break;
            case LoopStatement loop:
                ScanStatement(loop.Body);
                break;
            case WhenStatement ws:
                ScanExpression(ws.Expression);
                foreach (WhenClause clause in ws.Clauses) ScanStatement(clause.Body);
                break;
            case DiscardStatement discard:
                ScanExpression(discard.Expression);
                break;
        }
    }

    /// <summary>
    /// Recursively scans expressions and registers protocol call sites already classified by semantic analysis.
    /// </summary>
    private void ScanExpression(Expression expr)
    {
        switch (expr)
        {
            case CallExpression call:
                if (call.LoweringKind == CallLoweringKind.RuntimeDispatch &&
                    call.ResolvedRoutine?.OwnerType is ProtocolTypeInfo proto)
                {
                    Register(proto, call.ResolvedRoutine.Name);
                }
                ScanExpression(call.Callee);
                foreach (Expression arg in call.Arguments) ScanExpression(arg);
                break;
            case MemberExpression mem:
                ScanExpression(mem.Object);
                break;
            case BinaryExpression bin:
                ScanExpression(bin.Left);
                ScanExpression(bin.Right);
                break;
            case UnaryExpression un:
                ScanExpression(un.Operand);
                break;
            case NamedArgumentExpression named:
                ScanExpression(named.Value);
                break;
            case IndexExpression idx:
                ScanExpression(idx.Object);
                ScanExpression(idx.Index);
                break;
        }
    }

    /// <summary>
    /// Adds the dispatch entry for one protocol method and captures the currently known implementers.
    /// </summary>
    private void Register(ProtocolTypeInfo protocol, string methodName)
    {
        string key = $"{protocol.FullName}.{methodName}";
        _result.TryAdd(key: key,
            value: new RuntimeDispatchEntry(
                Protocol: protocol,
                MethodName: methodName,
                KnownImplementers: registry.GetProtocolImplementors(protocol: protocol)));
    }
}
