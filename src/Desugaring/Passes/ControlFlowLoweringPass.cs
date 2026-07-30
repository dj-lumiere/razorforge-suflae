using System.Collections.Generic;
using System.Linq;
using Compiler.Tokenizer;
using Compiler.Postprocessing.Passes;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Desugaring.Passes;

/// <summary>
/// Lowers all loop constructs to the <see cref="LoopStatement"/> primitive so the code
/// generator only needs to handle one loop form via <c>EmitLoop</c>.
///
/// <para><b>while</b> -> loop+if-break:</para>
/// <code>
/// while cond { body }
///  ??
/// loop { if !cond { break }; body }
/// </code>
///
/// <para><b>for v in iterable</b> -> loop+when:</para>
/// <code>
///  {
/// var _lf_iter_N = iterable.$iter()
/// loop { when _lf_iter_N.try_emit() { is None -> break; else var v -> body } }
/// }
/// </code>
///
/// <para><b>for (a, b) in pairs</b> (tuple destructuring) -> same loop shape, else
/// body prepends positional member-access bindings:</para>
/// <code>
///  {
/// var _lf_iter_N = pairs.$iter()
/// loop {
/// when _lf_iter_N.try_emit() {
/// is None -> break
/// else var _lf_elem_M -> { var a = _lf_elem_M.item0; var b = _lf_elem_M.item1; body }
///  }
///  }
/// }
/// </code>
///
/// <para><b>for x in iterable else { alt }</b> (for-else) -> exhaustion flag:</para>
/// <code>
///  {
/// var _lf_exhausted_N: Bool = false
/// var _lf_iter_N = iterable.$iter()
/// loop {
/// when _lf_iter_N.try_emit() {
/// is None -> { _lf_exhausted_N = true; break }
/// else var x -> body
///  }
///  }
/// if _lf_exhausted_N { alt }
/// }
/// </code>
///
/// <para>Range-based loops (<c>for x in 0 to n</c>) are also covered: the
/// <c>RangeExpression</c> iterable is converted to <c>Range[T](...)</c> by
/// <see cref="ExpressionLoweringPass"/> (which runs after this pass).</para>
/// </summary>
internal sealed class ControlFlowLoweringPass(DesugaringContext ctx)
{
    /// <summary>
    /// Tracks the iter count while this compiler phase runs.
    /// </summary>
    private int _iterCount;

    /// <summary>
    /// Runs this compiler phase over its configured input.
    /// </summary>
    public void Run(Program program)
    {
        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[i])
            {
                case RoutineDeclaration r:
                {
                    Statement newBody = LowerStatement(stmt: r.Body);
                    if (!ReferenceEquals(newBody, r.Body))
                        program.Declarations[i] = r with { Body = newBody };
                    break;
                }

                case EntityDeclaration e:
                    LowerMemberList(members: e.Members);
                    break;

                case RecordDeclaration rec:
                    LowerMemberList(members: rec.Members);
                    break;

                case CrashableDeclaration cr:
                    LowerMemberList(members: cr.Members);
                    break;
            }
        }
    }

    /// <summary>
    /// Lower member list as part of this compiler phase.
    /// </summary>
    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int j = 0; j < members.Count; j++)
        {
            if (members[j] is not RoutineDeclaration m) continue;
            Statement newBody = LowerStatement(stmt: m.Body);
            if (!ReferenceEquals(newBody, m.Body))
                members[j] = m with { Body = newBody };
        }
    }

    /// <summary>
    /// Lower statement as part of this compiler phase.
    /// </summary>
    private Statement LowerStatement(Statement stmt)
    {
        switch (stmt)
        {
            case ForStatement f:
                return LowerFor(forStmt: f);

            case DestructuringStatement ds:
                return LowerDestructuring(destruct: ds);

            case BlockStatement b:
            {
                bool changed = false;
                var stmts = new List<Statement>(capacity: b.Statements.Count);
                foreach (Statement s in b.Statements)
                {
                    Statement n = LowerStatement(stmt: s);
                    // Splice DestructuringStatement's lowered block into the parent scope
                    // so its `var a = ...; var b = ...;` bindings are visible to later siblings.
                    if (s is DestructuringStatement && n is BlockStatement ds)
                        stmts.AddRange(collection: ds.Statements);
                    else
                        stmts.Add(item: n);
                    if (!ReferenceEquals(n, s)) changed = true;
                }
                return changed ? b with { Statements = stmts } : b;
            }

            case WhileStatement w:
                return LowerWhile(whileStmt: w);

            case LoopStatement loop:
            {
                Statement body = LowerStatement(stmt: loop.Body);
                if (ReferenceEquals(body, loop.Body)) return loop;
                return loop with { Body = body };
            }

            case IfStatement ifs:
            {
                Statement then = LowerStatement(stmt: ifs.ThenStatement);
                Statement? elseS = ifs.ElseStatement != null
                    ? LowerStatement(stmt: ifs.ElseStatement)
                    : null;
                bool tc = !ReferenceEquals(then, ifs.ThenStatement);
                bool ec = !ReferenceEquals(elseS, ifs.ElseStatement);
                return tc || ec ? ifs with { ThenStatement = then, ElseStatement = elseS } : ifs;
            }

            case WhenStatement w:
            {
                bool changed = false;
                var clauses = new List<WhenClause>(capacity: w.Clauses.Count);
                foreach (WhenClause c in w.Clauses)
                {
                    Statement body = LowerStatement(stmt: c.Body);
                    if (!ReferenceEquals(body, c.Body))
                    {
                        clauses.Add(item: c with { Body = body });
                        changed = true;
                    }
                    else
                    {
                        clauses.Add(item: c);
                    }
                }
                return changed ? w with { Clauses = clauses } : w;
            }

            case UsingStatement u:
            {
                Statement body = LowerStatement(stmt: u.Body);
                Statement? fb = u.FallbackBody != null ? LowerStatement(stmt: u.FallbackBody) : null;
                return !ReferenceEquals(body, u.Body) || !ReferenceEquals(fb, u.FallbackBody)
                    ? u with { Body = body, FallbackBody = fb }
                    : u;
            }

            case DangerStatement d:
            {
                // DangerStatement.Body is BlockStatement; LowerStatement on BlockStatement
                // always returns a BlockStatement so the cast is safe.
                Statement lowered = LowerStatement(stmt: d.Body);
                return !ReferenceEquals(lowered, d.Body)
                    ? d with { Body = (BlockStatement)lowered }
                    : d;
            }

            default:
                return stmt;
        }
    }

    /// <summary>
    /// Lowers <c>while cond { body }</c> to <c>loop { if !cond { break } body }</c>.
    /// The else branch (if present) is dropped -> while-else is not yet fully implemented.
    /// </summary>
    private LoopStatement LowerWhile(WhileStatement whileStmt)
    {
        SourceLocation loc = whileStmt.Location;
        Statement loweredBody = LowerStatement(stmt: whileStmt.Body);

        // Build: if !cond { break }
        Expression negCond = new UnaryExpression(
            Operator: UnaryOperator.Not,
            Operand: whileStmt.Condition,
            Location: loc);
        Statement guardBreak = new IfStatement(
            Condition: negCond,
            ThenStatement: new BlockStatement(
                Statements: [new BreakStatement(Location: loc)],
                Location: loc),
            ElseStatement: null,
            Location: loc);

        // Build: loop { if !cond { break }; body }
        Statement loopBody = loweredBody is BlockStatement block
            ? block with { Statements = [guardBreak, .. block.Statements] }
            : new BlockStatement(Statements: [guardBreak, loweredBody], Location: loc);

        return new LoopStatement(Body: loopBody, Location: loc);
    }

    /// <summary>
    /// Lowers <c>var (a, b, c) = expr</c> to a block:
    /// <code>
    /// var _ld_tmp_N = expr
    /// var a = _ld_tmp_N.item0
    /// var b = _ld_tmp_N.item1
    /// var c = _ld_tmp_N.item2
    /// </code>
    /// Tuple-only: positional bindings map by index to <c>itemI</c>. Wildcards (<c>_</c>) are skipped.
    /// </summary>
    private BlockStatement LowerDestructuring(DestructuringStatement destruct)
    {
        SourceLocation loc = destruct.Location;
        int n = _iterCount++;
        string tmpName = $"_ld_tmp_{n}";

        var stmts = new List<Statement>(capacity: destruct.Pattern.Bindings.Count + 1)
        {
            new DeclarationStatement(
                Declaration: new VariableDeclaration(
                    Name: tmpName,
                    Type: null,
                    Initializer: destruct.Initializer,
                    Visibility: VisibilityModifier.Secret,
                    Location: loc),
                Location: loc)
        };

        for (int i = 0; i < destruct.Pattern.Bindings.Count; i++)
        {
            DestructuringBinding binding = destruct.Pattern.Bindings[index: i];
            string bindName = binding.BindingName ?? binding.MemberVariableName ?? $"_ld_b{i}";
            if (bindName == "_") continue;

            stmts.Add(item: new DeclarationStatement(
                Declaration: new VariableDeclaration(
                    Name: bindName,
                    Type: null,
                    Initializer: new MemberExpression(
                        Object: new IdentifierExpression(Name: tmpName, Location: loc),
                        MemberName: $"item{i}",
                        Location: loc),
                    Visibility: VisibilityModifier.Secret,
                    Location: loc),
                Location: loc));
        }

        return new BlockStatement(Statements: stmts, Location: loc);
    }

    /// <summary>
    /// Lower for as part of this compiler phase.
    /// </summary>
    private BlockStatement LowerFor(ForStatement forStmt) // NOSONAR S3776
    {
        SourceLocation loc = forStmt.Location;
        int n = _iterCount++;
        string iterName = $"_lf_iter_{n}";

        // -----------------------------------------------------------------------------
        var iterCallExpr = new CallExpression(
            Callee: new MemberExpression(
                Object: forStmt.Iterable,
                MemberName: "$iter",
                Location: loc),
            Arguments: [],
            Location: loc) { IsSynthesizedLowering = true };

        var tryNextReceiver = new IdentifierExpression(Name: iterName, Location: loc);
        CallExpression tryNextCallExpr = new CallExpression(
            Callee: new MemberExpression(
                Object: tryNextReceiver,
                MemberName: Compiler.Resolution.RuntimeContract.TryEmit,
                Location: loc),
            Arguments: [],
            Location: loc) { IsSynthesizedLowering = true };

        // When running after SA (stdlib/variant bodies), annotate ResolvedType, ResolvedRoutine,
        // and LoweringKind on the $iter and try_emit calls so CallOverloadResolutionPass doesn't
        // need to re-classify them (which fails for instantiated bodies where the receiver variable
        // has no SA-annotated type), and so reachability marks the CONCRETE emitter's try_emit.
        // Skip ErrorTypeInfo: SA suppresses stdlib errors.
        if (forStmt.Iterable.ResolvedType is { } iterType and not ErrorTypeInfo)
        {
            RoutineInfo? iterMethod = ctx.Registry.LookupMethod(type: iterType, methodName: "$iter");
            if (iterMethod?.ReturnType is { } rawIteratorType)
            {
                // LookupMethod returns the generic-def `$iter`, whose ReturnType still carries the
                // owner's params (e.g. `?EnumerateEmitter[T, S/Iter]`). Substitute the concrete
                // owner's type args so `try_emit` resolves on the CONCRETE emitter
                // (`EnumerateEmitter[Text, ListEmitter[Text]]`); otherwise reachability marks the
                // unresolved-projection emitter's try_emit and the concrete one never generates.
                TypeInfo iteratorType = SubstituteForConcreteOwner(type: rawIteratorType,
                    owner: iterType);
                iterCallExpr.ResolvedRoutine = iterMethod;
                iterCallExpr.ResolvedType = iteratorType;
                // Carry the concrete emitter type onto the receiver so reachability/codegen see it.
                tryNextReceiver.ResolvedType = iteratorType;
                RoutineInfo? tryNextMethod =
                    ctx.Registry.LookupMethod(type: iteratorType, methodName: Compiler.Resolution.RuntimeContract.TryEmit);
                if (tryNextMethod != null)
                    tryNextCallExpr = tryNextCallExpr with
                    {
                        ResolvedRoutine = tryNextMethod,
                        LoweringKind = CallLoweringKind.DirectMemberRoutine,
                        ResolvedType = tryNextMethod.ReturnType ?? tryNextCallExpr.ResolvedType
                    };
            }
        }
        Expression tryNextCall = tryNextCallExpr;

        Statement iterVarStmt = new DeclarationStatement(
            Declaration: new VariableDeclaration(
                Name: iterName,
                Type: null,
                Initializer: iterCallExpr,
                Visibility: VisibilityModifier.Secret,
                Location: loc),
            Location: loc);

        // -----------------------------------------------------------------------------
        Statement loweredBody = LowerStatement(stmt: forStmt.Body);

        // -----------------------------------------------------------------------------
        Statement elseBody;
        string? elseVarName;

        if (forStmt.VariablePattern != null)
        {
            // Tuple destructuring: else var _lf_elem_M -> { var a = elem.item0; var b = elem.item1; ... body }
            string elemName = $"_lf_elem_{n}";
            elseVarName = elemName;

            // Prepend: var a = _lf_elem_M.item0, var b = _lf_elem_M.item1, ??
            var bindStmts = new List<Statement>(capacity: forStmt.VariablePattern.Bindings.Count + 1);
            for (int i = 0; i < forStmt.VariablePattern.Bindings.Count; i++)
            {
                DestructuringBinding binding = forStmt.VariablePattern.Bindings[index: i];
                string bindName = binding.BindingName ?? binding.MemberVariableName ?? $"_lf_b{i}";
                if (bindName == "_") continue;

                bindStmts.Add(item: new DeclarationStatement(
                    Declaration: new VariableDeclaration(
                        Name: bindName,
                        Type: null,
                        Initializer: new MemberExpression(
                            Object: new IdentifierExpression(Name: elemName, Location: loc),
                            MemberName: $"item{i}",
                            Location: loc),
                        Visibility: VisibilityModifier.Secret,
                        Location: loc),
                    Location: loc));
            }

            if (loweredBody is BlockStatement bodyBlock)
            {
                bindStmts.AddRange(collection: bodyBlock.Statements);
                elseBody = bodyBlock with { Statements = bindStmts };
            }
            else
            {
                bindStmts.Add(item: loweredBody);
                elseBody = new BlockStatement(Statements: bindStmts, Location: loc);
            }
        }
        else
        {
            // Simple variable or discard
            elseVarName = forStmt.Variable == "_" ? null : forStmt.Variable;
            elseBody    = loweredBody;
        }

        // -----------------------------------------------------------------------------
        Statement? elseBranchLowered = forStmt.ElseBranch != null
            ? LowerStatement(stmt: forStmt.ElseBranch)
            : null;

        Statement noneBody;
        if (elseBranchLowered != null)
        {
            // For-else: set exhausted flag, then break
            string exhaustedName = $"_lf_exhausted_{n}";
            noneBody = new BlockStatement(
                Statements:
                [
                    new AssignmentStatement(
                        Target: new IdentifierExpression(Name: exhaustedName, Location: loc),
                        Value: new LiteralExpression(Value: true, LiteralType: TokenType.True,
                            Location: loc),
                        Location: loc),
                    new BreakStatement(Location: loc)
                ],
                Location: loc);

            var noneClause = new WhenClause(Pattern: new NonePattern(Location: loc), Body: noneBody,
                Location: loc);
            var elseClause = new WhenClause(
                Pattern: new ElsePattern(VariableName: elseVarName, Location: loc),
                Body: elseBody, Location: loc);

            var whenStmt = new WhenStatement(Expression: tryNextCall,
                Clauses: [noneClause, elseClause], Location: loc);
            var loopStmt = new LoopStatement(
                Body: new BlockStatement(Statements: [whenStmt], Location: loc), Location: loc)
                { IsIteratorForLoop = true };

            // var _lf_exhausted_N: Bool = false
            Statement exhaustedVarStmt = new DeclarationStatement(
                Declaration: new VariableDeclaration(
                    Name: exhaustedName,
                    Type: new TypeExpression(Name: "Bool", GenericArguments: null, Location: loc),
                    Initializer: new LiteralExpression(Value: false, LiteralType: TokenType.False,
                        Location: loc),
                    Visibility: VisibilityModifier.Secret,
                    Location: loc),
                Location: loc);

            // if _lf_exhausted_N { alt }
            Statement exhaustionCheck = new IfStatement(
                Condition: new IdentifierExpression(Name: exhaustedName, Location: loc),
                ThenStatement: elseBranchLowered,
                ElseStatement: null,
                Location: loc);

            return new BlockStatement(
                Statements: [exhaustedVarStmt, iterVarStmt, loopStmt, exhaustionCheck],
                Location: loc);
        }
        else
        {
            // Plain for (no else branch)
            noneBody = new BlockStatement(
                Statements: [new BreakStatement(Location: loc)], Location: loc);

            var noneClause = new WhenClause(Pattern: new NonePattern(Location: loc), Body: noneBody,
                Location: loc);
            var elseClause = new WhenClause(
                Pattern: new ElsePattern(VariableName: elseVarName, Location: loc),
                Body: elseBody, Location: loc);

            var whenStmt = new WhenStatement(Expression: tryNextCall,
                Clauses: [noneClause, elseClause], Location: loc);
            var loopStmt = new LoopStatement(
                Body: new BlockStatement(Statements: [whenStmt], Location: loc), Location: loc)
                { IsIteratorForLoop = true };

            return new BlockStatement(Statements: [iterVarStmt, loopStmt], Location: loc);
        }
    }

    /// <summary>
    /// Substitutes a type with a concrete generic owner's type arguments (e.g. the generic-def
    /// <c>$iter</c> return <c>EnumerateEmitter[T, S/Iter]</c> for owner
    /// <c>EnumerateIterator[Text, List[Text]]</c> → <c>EnumerateEmitter[Text, ListEmitter[Text]]</c>).
    /// Resolves associated-type projections via <see cref="RecordTypeInfo.SubstituteType"/>.
    /// </summary>
    private static TypeInfo SubstituteForConcreteOwner(TypeInfo type, TypeInfo owner)
    {
        TypeInfo? def = owner switch
        {
            EntityTypeInfo e => e.GenericDefinition,
            RecordTypeInfo r => r.GenericDefinition,
            _ => null
        };
        if (def?.GenericParameters is { } defParams && owner.TypeArguments is { } args &&
            defParams.Count == args.Count && defParams.Count > 0)
        {
            var subs = new Dictionary<string, TypeInfo>();
            for (int i = 0; i < defParams.Count; i++)
            {
                subs[key: defParams[index: i]] = args[index: i];
            }
            return RecordTypeInfo.SubstituteType(type: type, substitution: subs);
        }
        return type;
    }

    /// <summary>
    /// Lowers control flow in all synthesized variant bodies in
    /// <see cref="DesugaringContext.VariantBodies"/>.
    /// Called from <see cref="DesugaringPipeline.RunGlobal"/> after variant bodies are generated,
    /// so that bodies copied from unlowered stdlib originals get their WhileStatement nodes
    /// converted to LoopStatement before OperatorLoweringPass and codegen see them.
    /// </summary>
    public void RunOnVariantBodies()
    {
        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            Statement body = ctx.VariantBodies[key];
            Statement lowered = LowerStatement(stmt: body);
            if (!ReferenceEquals(lowered, body))
                ctx.VariantBodies[key] = lowered;
        }
    }

    /// <summary>
    /// Lower ForStatement/DestructuringStatement etc. in monomorphized bodies that GMP
    /// produced from stdlib originals (which never went through Phase 4 desugaring).
    /// Notable consumer: <see cref="Compiler.Instantiation.Passes.ProtocolDefaultImplLoweringPass"/>,
    /// which clones stdlib protocol-default-impl bodies (e.g. <c>Iterable[Text].join</c>) into
    /// per-implementer routines; those clones contain raw `for` loops that codegen rejects.
    /// </summary>
    public void RunOnInstantiatedGenericBodies(
        IDictionary<string, Compiler.Instantiation.MonomorphizedBody> bodies)
    {
        foreach (string key in bodies.Keys.ToList())
        {
            Compiler.Instantiation.MonomorphizedBody mb = bodies[key];
            Statement lowered = LowerStatement(stmt: mb.Ast.Body);
            if (!ReferenceEquals(lowered, mb.Ast.Body))
                bodies[key] = mb with { Ast = mb.Ast with { Body = lowered } };
        }
    }
}
