using System.Collections.Generic;
using System.Linq;
using Compiler.CodeGen;
using Compiler.Instantiation;
using Compiler.Resolution;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using Verification;

namespace Compiler.Postprocessing.Passes;

/// <summary>
/// Moves the implicit RC-wrapper copy-verb bump (<c>retain</c>/<c>track</c>/<c>share</c>/<c>watch</c>/
/// <c>roam</c>) out of codegen and into a real AST call, mirroring
/// <see cref="RoamedSpawnPromotionLoweringPass"/> (statement insertion) and
/// <see cref="RoamedProjectionLoweringPass"/> (immutable node construction).
///
/// <para>Previously codegen inserted the retain-side refcount bump with NO surface AST call at three
/// sites: a var-binding / reassignment of a record whose fields are RC wrappers (one bump per RC
/// field, matching <c>RecordTypeInfo.HasRCFields</c>) and a <c>Roamed[T]</c> ENTITY-field write (one
/// <c>roam</c> on the stored handle). This pass makes each an explicit
/// <c>&lt;target&gt;.&lt;field&gt;.retain()</c> / <c>&lt;target&gt;.roam()</c>
/// <see cref="ExpressionStatement"/> inserted immediately AFTER the binding statement, referencing the
/// just-stored target lvalue — the same value codegen used to load back from the alloca. The verbs
/// return the handle and only mutate the shared controller, so a trailing side-effecting statement is
/// exact-parity with the old codegen.</para>
///
/// <para>The RELEASE side stays in codegen (reassignment-overwrite is not a scope exit). Scope-exit
/// teardown is already AST-lowered by <c>ScopeTeardownLoweringPass</c>. Reachability already seeds the
/// copy verbs for every live RC wrapper via <c>ImplicitCallContract.ForLiveType</c>, so the targets
/// are live/monomorphized. Runs right after <see cref="RecordCopyLoweringPass"/>.</para>
///
/// <para><b>Bump-count parity is refcount-critical</b> (an extra bump leaks, a missing one
/// double-frees): exactly one verb call per RC field per record copy, and exactly one <c>roam</c> per
/// Roamed entity-field write — identical to the removed codegen sites.</para>
/// </summary>
internal sealed class RcRetainLoweringPass(PostprocessingContext ctx)
{
    private TypeRegistry Registry => ctx.Registry;

    /// <summary>Inserts RC copy-verb bumps across a whole program.</summary>
    public void Run(Program program)
    {
        foreach (SyntaxTree.Declaration decl in program.Declarations)
        {
            LowerDeclaration(decl);
        }
    }

    /// <summary>Inserts RC copy-verb bumps in synthesized variant bodies.</summary>
    public void RunOnVariantBodies()
    {
        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            LowerBody(ctx.VariantBodies[key]);
        }
    }

    /// <summary>
    /// Inserts RC copy-verb bumps into instantiated generic routine bodies. GMP populates these
    /// AFTER the Phase-8 sweep, so — like <see cref="RecordCopyLoweringPass.RunOnInstantiatedGenericBodies"/>
    /// — they must be re-lowered here. A binding of an RC-field record inside a monomorphized body
    /// (the case the old codegen bump covered post-mono) then gets its per-field retain. Bodies are
    /// mutated in place (block-statement lists), so no map rewrite is needed.
    /// </summary>
    public void RunOnInstantiatedGenericBodies(
        Dictionary<string, MonomorphizedBody> instantiatedGenericBodies)
    {
        foreach (string key in instantiatedGenericBodies.Keys.ToList())
        {
            MonomorphizedBody entry = instantiatedGenericBodies[key];
            if (entry.IsSynthesized) continue; // pure-synthesized: no AST to walk
            LowerBody(entry.Ast.Body);
        }
    }

    private void LowerDeclaration(SyntaxTree.Declaration decl)
    {
        switch (decl)
        {
            case RoutineDeclaration r:
                LowerBody(r.Body);
                break;
            case EntityDeclaration e:
                LowerMemberList(e.Members);
                break;
            case RecordDeclaration rec:
                LowerMemberList(rec.Members);
                break;
            case CrashableDeclaration cr:
                LowerMemberList(cr.Members);
                break;
        }
    }

    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        foreach (SyntaxTree.Declaration m in members)
        {
            if (m is RoutineDeclaration mr) LowerBody(mr.Body);
        }
    }

    private void LowerBody(Statement body)
    {
        if (body is BlockStatement block) LowerBlock(block);
    }

    // ---- Block rewrite (in place, mirroring RoamedSpawnPromotionLoweringPass) -------------------

    private void LowerBlock(BlockStatement block)
    {
        var rewritten = new List<Statement>(capacity: block.Statements.Count);
        foreach (Statement stmt in block.Statements)
        {
            RecurseInto(stmt);
            rewritten.Add(item: stmt);
            rewritten.AddRange(collection: CollectBumps(stmt));
        }
        block.Statements.Clear();
        block.Statements.AddRange(collection: rewritten);
    }

    // Gathers the copy-verb bump statements a binding statement's OWN store needs, inserted AFTER it.
    private List<Statement> CollectBumps(Statement stmt)
    {
        return stmt switch
        {
            DeclarationStatement { Declaration: VariableDeclaration vd } => DeclBumps(vd),
            AssignmentStatement a => AssignBumps(target: a.Target, value: a.Value),
            ExpressionStatement { Expression: BinaryExpression { Operator: BinaryOperator.Assign } b }
                => AssignBumps(target: b.Left, value: b.Right),
            _ => []
        };
    }

    private List<Statement> DeclBumps(VariableDeclaration vd)
    {
        if (vd.Initializer is null) return [];
        var target = new IdentifierExpression(Name: vd.Name, Location: vd.Location)
        {
            ResolvedType = vd.Initializer.ResolvedType
        };
        return RcFieldBumps(target: target);
    }

    // A record copy (var-decl / reassignment to a local) bumps each RC-wrapper field; a Roamed
    // ENTITY-field write bumps the stored handle. Both key off the TARGET lvalue codegen stored into.
    private List<Statement> AssignBumps(Expression target, Expression value)
    {
        if (target is MemberExpression member && IsRoamedEntityField(member))
        {
            _ = value;
            return RoamBump(target: member) is { } roam ? [roam] : [];
        }
        return RcFieldBumps(target: target);
    }

    // ---- RC-field record copy (Retained/Tracked/Shared/Watched fields) --------------------------

    private List<Statement> RcFieldBumps(Expression target)
    {
        if (target.ResolvedType is not RecordTypeInfo { HasRCFields: true } record) return [];
        var bumps = new List<Statement>();
        foreach (MemberVariableInfo field in record.MemberVariables)
        {
            if (TryFieldBump(target: target, field: field) is { } bump) bumps.Add(item: bump);
        }
        return bumps;
    }

    // Every RC-wrapper field bumps its OWN count via the unified copy verb `store` (Retained/Shared →
    // strong, Tracked/Watched → weak, Roamed → biased). This is per-kind symmetry with the field-release
    // side (EmitRcRecordRelease tears each field down through its own `destroy`→`release`).
    private Statement? TryFieldBump(Expression target, MemberVariableInfo field)
    {
        if (field.Type is not WrapperTypeInfo wrapper ||
            RuntimeContract.RcWrapperBaseNames.Contains(item: wrapper.Name) is false)
        {
            return null;
        }

        RoutineInfo? store = Registry.LookupMethod(type: wrapper, methodName: "share");
        if (store is null) return null;

        var fieldAccess = new MemberExpression(Object: target, MemberName: field.Name,
            Location: target.Location) { ResolvedType = wrapper };
        return MakeBumpStatement(receiver: fieldAccess, receiverType: wrapper, copy: store);
    }

    // ---- Roamed entity-field write --------------------------------------------------------------

    private bool IsRoamedEntityField(MemberExpression member) =>
        member.Object.ResolvedType is EntityTypeInfo &&
        member.ResolvedType is RecordTypeInfo rec &&
        LlvmCodeGenerator.GetGenericBaseNameStatic(type: rec) == RuntimeContract.Roamed;

    private Statement? RoamBump(MemberExpression target)
    {
        if (target.ResolvedType is not RecordTypeInfo rec) return null;
        RoutineInfo? store = Registry.LookupMethod(type: rec, methodName: "share");
        if (store is null) return null;
        return MakeBumpStatement(receiver: target, receiverType: rec, copy: store);
    }

    // ---- Shared node construction ---------------------------------------------------------------

    // Builds `receiver.<verb>()` as an ExpressionStatement. The verb returns the handle and only
    // mutates the shared controller, so the result is discarded — a pure side effect, exactly what
    // the old codegen bump did (it dropped the returned value too).
    private static Statement MakeBumpStatement(Expression receiver, TypeInfo receiverType,
        RoutineInfo copy)
    {
        var callee = new MemberExpression(Object: receiver, MemberName: copy.Name,
            Location: receiver.Location) { ResolvedType = receiverType };
        var call = new CallExpression(Callee: callee, Arguments: new List<Expression>(),
            Location: receiver.Location)
        {
            ResolvedRoutine = copy,
            ResolvedType = copy.ReturnType,
            LoweringKind = CallClassifier.ClassifyMethodCall(method: copy)
        };
        return new ExpressionStatement(Expression: call, Location: receiver.Location);
    }

    // ---- Nested-block recursion (mirrors RoamedSpawnPromotionLoweringPass) -----------------------

    private void RecurseInto(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement b:
                LowerBlock(b);
                break;
            case IfStatement i:
                RecurseStmt(i.ThenStatement);
                if (i.ElseStatement != null) RecurseStmt(i.ElseStatement);
                break;
            case WhileStatement w:
                RecurseStmt(w.Body);
                if (w.ElseBranch != null) RecurseStmt(w.ElseBranch);
                break;
            case LoopStatement l:
                RecurseStmt(l.Body);
                break;
            case EachStatement f:
                RecurseStmt(f.Body);
                if (f.ElseBranch != null) RecurseStmt(f.ElseBranch);
                break;
            case DangerStatement d:
                LowerBlock(d.Body);
                break;
            case UsingStatement u:
                RecurseStmt(u.Body);
                if (u.FallbackBody != null) RecurseStmt(u.FallbackBody);
                break;
            case WhenStatement whenStmt:
                foreach (WhenClause clause in whenStmt.Clauses) RecurseStmt(clause.Body);
                break;
        }
    }

    private void RecurseStmt(Statement stmt)
    {
        if (stmt is BlockStatement b) LowerBlock(b);
        else RecurseInto(stmt);
    }
}
