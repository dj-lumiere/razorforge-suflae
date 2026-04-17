using Compiler.Lexer;
using SemanticVerification.Symbols;
using SemanticVerification.Types;
using SyntaxTree;

namespace Compiler.Desugaring.Passes;

/// <summary>
/// Lowers <see cref="WhenStatement"/>s whose clauses all use simple or carrier-type patterns
/// into plain <see cref="IfStatement"/> chains. Runs last in the per-file pipeline
/// (after <see cref="ControlFlowLoweringPass"/> and <see cref="ExpressionLoweringPass"/>).
///
/// <para>Lowerable patterns (all subject types):</para>
/// <list type="bullet">
///   <item><see cref="WildcardPattern"/> — always matches, no binding.</item>
///   <item><see cref="LiteralPattern"/> — emits <c>subject == value</c>.</item>
///   <item><see cref="IdentifierPattern"/> — always matches, binds subject to name.</item>
///   <item><see cref="ExpressionPattern"/> — boolean expression, subject is ignored.</item>
///   <item><see cref="ComparisonPattern"/> — emits <c>subject op value</c>.</item>
///   <item><see cref="ElsePattern"/> — always matches, optional binding (see below).</item>
///   <item><see cref="GuardPattern"/> — inner condition AND guard; inner must be lowerable.</item>
/// </list>
///
/// <para>Lowerable patterns (Maybe subjects):</para>
/// <list type="bullet">
///   <item><see cref="NonePattern"/> on <c>Maybe[T record]</c> → <c>not subject.present</c>.</item>
///   <item><see cref="TypePattern"/> T on <c>Maybe[T record]</c> → <c>subject.present</c>,
///         binding → <c>subject.value</c>.</item>
///   <item><see cref="ElsePattern"/> on <c>Maybe[T record]</c> → binding → <c>subject.value</c>.</item>
/// </list>
///
/// <para>Lowerable patterns (flags subjects):</para>
/// <list type="bullet">
///   <item><see cref="FlagsPattern"/> → <see cref="FlagsTestExpression"/> condition.</item>
/// </list>
///
/// <para>Lowerable patterns (record subjects — destructuring):</para>
/// <list type="bullet">
///   <item><see cref="DestructuringPattern"/> with all named simple bindings → always matches,
///         binds <c>var name = subject.field</c> for each binding.</item>
///   <item><see cref="TypeDestructuringPattern"/> with all named simple bindings → same, plus
///         type check is always true when SA validated the subject type.</item>
/// </list>
///
/// <para>Lowerable patterns (Result/Lookup and user variant subjects):</para>
/// <list type="bullet">
///   <item><see cref="TypePattern"/> <c>Blank</c> → <c>subject.type_id == 0</c>.</item>
///   <item><see cref="TypePattern"/> T → <c>subject.type_id == FNV-1a(T.FullName)</c>,
///         optional binding → <see cref="CarrierPayloadExpression"/>.</item>
///   <item><see cref="ElsePattern"/> with binding on Result/Lookup → <see cref="CarrierPayloadExpression"/>.</item>
/// </list>
///
/// <para>Lowerable patterns (Maybe[T entity] subjects):</para>
/// <list type="bullet">
///   <item><see cref="NonePattern"/> on <c>Maybe[T entity]</c> → <c>subject.value.is_none()</c>.</item>
///   <item><see cref="TypePattern"/> T on <c>Maybe[T entity]</c> → <c>not subject.value.is_none()</c>,
///         binding → <c>subject.value.read()</c>.</item>
///   <item><see cref="ElsePattern"/> with binding on <c>Maybe[T entity]</c> → binding → <c>subject.value.read()</c>.</item>
/// </list>
///
/// <para>Left unchanged for codegen's <c>EmitWhen</c>:</para>
/// <list type="bullet">
///   <item><see cref="NegatedTypePattern"/> on user variant.</item>
///   <item><see cref="CrashablePattern"/> — expanded by <see cref="CrashableExpansionPass"/> before this pass;
///         any remaining instances pass through to codegen.</item>
///   <item><see cref="VariantPattern"/> (type-based; use <see cref="TypePattern"/> instead).</item>
///   <item><see cref="DestructuringPattern"/>/<see cref="TypeDestructuringPattern"/> with nested patterns.</item>
/// </list>
/// </summary>
internal sealed class PatternLoweringPass(DesugaringContext ctx)
{
    private int _tempCount;

    private string NextTempName(string prefix) => $"_pl_{prefix}_{_tempCount++}";

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

    // ─── Statement walker ─────────────────────────────────────────────────────────

    private Statement LowerStatement(Statement stmt)
    {
        switch (stmt)
        {
            case WhenStatement w:
                return LowerWhen(when: w);

            case BlockStatement b:
            {
                bool changed = false;
                var stmts = new List<Statement>(capacity: b.Statements.Count);
                foreach (Statement s in b.Statements)
                {
                    Statement n = LowerStatement(stmt: s);
                    stmts.Add(item: n);
                    if (!ReferenceEquals(n, s)) changed = true;
                }

                return changed ? b with { Statements = stmts } : b;
            }

            case IfStatement ifs:
            {
                Statement then = LowerStatement(stmt: ifs.ThenStatement);
                Statement? elseS = ifs.ElseStatement != null
                    ? LowerStatement(stmt: ifs.ElseStatement)
                    : null;
                bool changed = !ReferenceEquals(then, ifs.ThenStatement)
                               || !ReferenceEquals(elseS, ifs.ElseStatement);
                return changed
                    ? ifs with { ThenStatement = then, ElseStatement = elseS }
                    : ifs;
            }

            case WhileStatement w:
            {
                Statement body = LowerStatement(stmt: w.Body);
                Statement? elseB = w.ElseBranch != null
                    ? LowerStatement(stmt: w.ElseBranch)
                    : null;
                bool changed = !ReferenceEquals(body, w.Body)
                               || !ReferenceEquals(elseB, w.ElseBranch);
                return changed
                    ? w with { Body = body, ElseBranch = elseB }
                    : w;
            }

            case LoopStatement loop:
            {
                Statement body = LowerStatement(stmt: loop.Body);
                return ReferenceEquals(body, loop.Body) ? loop : loop with { Body = body };
            }

            case ForStatement f:
            {
                // ForStatements not lowered by ControlFlowLoweringPass (range/tuple/else forms)
                // pass through; still recurse into their bodies.
                Statement body = LowerStatement(stmt: f.Body);
                Statement? elseB = f.ElseBranch != null
                    ? LowerStatement(stmt: f.ElseBranch)
                    : null;
                bool changed = !ReferenceEquals(body, f.Body)
                               || !ReferenceEquals(elseB, f.ElseBranch);
                return changed
                    ? f with { Body = body, ElseBranch = elseB }
                    : f;
            }

            case UsingStatement u:
            {
                Statement body = LowerStatement(stmt: u.Body);
                return !ReferenceEquals(body, u.Body) ? u with { Body = body } : u;
            }

            case DangerStatement d:
            {
                Statement lowered = LowerStatement(stmt: d.Body);
                return !ReferenceEquals(lowered, d.Body)
                    ? d with { Body = (BlockStatement)lowered }
                    : d;
            }

            default:
                return stmt;
        }
    }

    // ─── WhenStatement lowering ───────────────────────────────────────────────────

    private Statement LowerWhen(WhenStatement when)
    {
        // Recurse into clause bodies first (handles nested WhenStatements).
        bool clauseChanged = false;
        var loweredClauses = new List<WhenClause>(capacity: when.Clauses.Count);
        foreach (WhenClause c in when.Clauses)
        {
            Statement lBody = LowerStatement(stmt: c.Body);
            if (!ReferenceEquals(lBody, c.Body))
            {
                loweredClauses.Add(item: c with { Body = lBody });
                clauseChanged = true;
            }
            else
            {
                loweredClauses.Add(item: c);
            }
        }

        // Subject-less when (Expression == null): each arm is an ExpressionPattern (bool guard)
        // or ElsePattern. Lower directly to an if/else chain.
        if (when.Expression == null)
            return LowerSubjectlessWhen(loweredClauses: loweredClauses, loc: when.Location);

        TypeInfo? subjectType = when.Expression.ResolvedType;

        if (!IsLowerable(when: when, subjectType: subjectType))
        {
            // Leave for codegen's EmitWhen; update clauses if any bodies changed.
            return clauseChanged ? when with { Clauses = loweredClauses } : when;
        }

        // ── Lowerable: transform to if/else chain ────────────────────────────────
        SourceLocation loc = when.Location;
        var hoisted = new List<Statement>();

        // Hoist non-trivial subject to a temp var to avoid re-evaluation.
        Expression subject = when.Expression;
        if (subject is not (IdentifierExpression or LiteralExpression))
        {
            string subjName = NextTempName(prefix: "subj");
            AddTempVar(hoisted: hoisted, name: subjName, typeHint: subjectType,
                initializer: subject, loc: loc);
            subject = new IdentifierExpression(Name: subjName, Location: loc)
            {
                ResolvedType = subjectType
            };
        }

        // Pre-scan: determine if the else arm on a carrier is narrowed to the inner type.
        // An else arm is narrowed when ALL non-T alternatives are covered by prior clauses.
        bool isElseNarrowed = false;
        if (IsResultOrLookup(subjectType) && subjectType!.TypeArguments?.Count > 0)
        {
            bool seenBlank = loweredClauses.Any(
                c => c.Pattern is TypePattern { Type.Name: "Blank" });
            int totalCrashable = ctx.Registry.GetAllTypes().OfType<CrashableTypeInfo>().Count();
            int seenCrashablePatterns = loweredClauses.Count(
                c => c.Pattern is TypePattern tp2 && tp2.Type.ResolvedType is CrashableTypeInfo);
            isElseNarrowed = seenBlank && seenCrashablePatterns >= totalCrashable;
        }
        else if (IsMaybeRecord(subjectType) || IsMaybeEntity(subjectType))
        {
            isElseNarrowed = loweredClauses.Any(c => c.Pattern is NonePattern
                || c.Pattern is TypePattern { Type.Name: "None" });
        }
        else if (IsResultType(subjectType) && subjectType!.TypeArguments?.Count > 0)
        {
            // Result[T]: else narrowed when all Crashable types are handled (no Blank in Result).
            int totalCrashable = ctx.Registry.GetAllTypes().OfType<CrashableTypeInfo>().Count();
            int seenCrashablePatterns = loweredClauses.Count(
                c => c.Pattern is TypePattern tp2 && tp2.Type.ResolvedType is CrashableTypeInfo);
            isElseNarrowed = seenCrashablePatterns >= totalCrashable;
        }

        // Build if/else chain via right-fold (last clause to first).
        // An always-matching clause (null condition) becomes the final else and
        // discards any chain built from subsequent unreachable clauses.
        Statement? chain = null;
        for (int i = loweredClauses.Count - 1; i >= 0; i--)
        {
            WhenClause clause = loweredClauses[index: i];
            (Expression? cond, Statement? binding) =
                GetPatternCondition(pattern: clause.Pattern, subject: subject,
                    subjectType: subjectType, isElseNarrowed: isElseNarrowed);

            Statement body = clause.Body;
            if (binding != null)
            {
                // Prepend binding(s) inside the branch body.
                // GenerateDestructuringBindings returns a BlockStatement when there are multiple
                // bindings — flatten those into the outer block so they share scope with the body.
                if (binding is BlockStatement multiBindBlock)
                {
                    var stmts = new List<Statement>(capacity: multiBindBlock.Statements.Count + 1);
                    stmts.AddRange(multiBindBlock.Statements);
                    stmts.Add(item: body);
                    body = new BlockStatement(Statements: stmts, Location: loc);
                }
                else
                {
                    body = new BlockStatement(Statements: [binding, body], Location: loc);
                }
            }

            if (cond == null)
            {
                // Always-matching clause: becomes the unconditional else.
                chain = body;
            }
            else
            {
                chain = new IfStatement(
                    Condition: cond,
                    ThenStatement: body,
                    ElseStatement: chain,
                    Location: loc);
            }
        }

        Statement result = chain ?? new BlockStatement(Statements: [], Location: loc);

        if (hoisted.Count == 0) return result;
        hoisted.Add(item: result);
        return new BlockStatement(Statements: hoisted, Location: loc);
    }

    // ─── Lowerable check ─────────────────────────────────────────────────────────

    /// <summary>
    /// Lowers a subject-less <c>when</c> (each arm is an <see cref="ExpressionPattern"/> bool guard
    /// or an <see cref="ElsePattern"/>) into an if/else chain.
    /// </summary>
    private static Statement LowerSubjectlessWhen(List<WhenClause> loweredClauses,
        SourceLocation loc)
    {
        // Build if/else chain via right-fold (last clause first).
        Statement? chain = null;
        for (int i = loweredClauses.Count - 1; i >= 0; i--)
        {
            WhenClause clause = loweredClauses[i];
            Statement body = clause.Body;
            switch (clause.Pattern)
            {
                case ElsePattern:
                case WildcardPattern:
                    // Always-matching: becomes the unconditional else.
                    chain = body;
                    break;

                case ExpressionPattern ep:
                {
                    chain = new IfStatement(
                        Condition: ep.Expression,
                        ThenStatement: body,
                        ElseStatement: chain,
                        Location: loc);
                    break;
                }

                default:
                    // Unexpected pattern type in subject-less when — leave as-is (safety fallback).
                    chain = new IfStatement(
                        Condition: new LiteralExpression(
                            Value: true, LiteralType: TokenType.True, Location: loc),
                        ThenStatement: body,
                        ElseStatement: chain,
                        Location: loc);
                    break;
            }
        }

        return chain ?? new BlockStatement(Statements: [], Location: loc);
    }

    private static bool IsLowerable(WhenStatement when, TypeInfo? subjectType) =>
        when.Clauses.All(predicate: c => IsLowerablePattern(pattern: c.Pattern,
            subjectType: subjectType));

    private static bool IsLowerablePattern(Pattern pattern, TypeInfo? subjectType) =>
        pattern switch
        {
            WildcardPattern   => true,
            LiteralPattern    => true,
            IdentifierPattern => true,
            ExpressionPattern => true,
            ComparisonPattern => true,
            FlagsPattern      => subjectType is FlagsTypeInfo,

            // ElsePattern with binding on Result/Lookup or Maybe[T entity]: lowerable.
            ElsePattern ep    => !ep.VariableName.HasValue()
                                 || IsResultOrLookup(subjectType)
                                 || IsMaybeEntity(subjectType),

            NonePattern => IsMaybeRecord(subjectType) || IsMaybeEntity(subjectType),

            // Maybe[T record] TypePattern: lowerable (uses bool present field, if/else is optimal)
            TypePattern when IsMaybeRecord(subjectType) => true,

            // Maybe[T entity] TypePattern: lowerable (null-ptr check via is_none())
            TypePattern when IsMaybeEntity(subjectType) => true,

            // "is Crashable" on Result/Lookup: NOT lowerable — needs "tag != 0 && tag != validId"
            // range check in codegen (EmitCrashablePatternMatch). The naive lowering would emit
            // type_id == ComputeTypeId("Crashable") which never matches concrete error type_ids.
            TypePattern tp when IsResultOrLookup(subjectType) && tp.Type.Name == "Crashable" => false,

            // Result/Lookup and user variant TypePatterns: lowerable — condition is type_id == constant.
            TypePattern when IsResultOrLookup(subjectType) || subjectType is VariantTypeInfo => true,

            DestructuringPattern dp
                when subjectType is RecordTypeInfo && IsAllNamedSimpleBindings(dp.Bindings)
                => true,

            TypeDestructuringPattern tdp
                when subjectType is RecordTypeInfo && IsAllNamedSimpleBindings(tdp.Bindings)
                => true,

            GuardPattern gp => IsLowerablePattern(pattern: gp.InnerPattern,
                subjectType: subjectType),

            _ => false
        };

    // ─── Pattern → (condition, binding) ──────────────────────────────────────────

    /// <summary>
    /// Returns the if-condition expression and optional binding statement for a lowerable pattern.
    /// A <c>null</c> condition means the pattern always matches (becomes the final <c>else</c>).
    /// </summary>
    private (Expression? Cond, Statement? Binding) GetPatternCondition(
        Pattern pattern, Expression subject, TypeInfo? subjectType,
        bool isElseNarrowed = false)
    {
        SourceLocation loc = pattern.Location;
        TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");

        switch (pattern)
        {
            case WildcardPattern:
                return (null, null);

            case LiteralPattern lit:
            {
                var cond = new BinaryExpression(
                    Left: subject,
                    Operator: BinaryOperator.Equal,
                    Right: new LiteralExpression(
                        Value: lit.Value,
                        LiteralType: lit.LiteralType,
                        Location: loc) { ResolvedType = subject.ResolvedType },
                    Location: loc)
                {
                    ResolvedType = boolType
                };
                return (cond, null);
            }

            case IdentifierPattern id:
                // Always matches; bind the subject to the identifier name.
                return (null, MakeBinding(name: id.Name, value: subject, loc: id.Location));

            case ExpressionPattern ep:
                // Boolean expression — subject is not referenced.
                return (ep.Expression, null);

            case ComparisonPattern cmp:
            {
                var cond = new BinaryExpression(
                    Left: subject,
                    Operator: TokenTypeToBinaryOp(op: cmp.Operator),
                    Right: cmp.Value,
                    Location: loc)
                {
                    ResolvedType = boolType
                };
                return (cond, null);
            }

            case ElsePattern ep:
            {
                Statement? binding = null;
                if (ep.VariableName != null)
                {
                    Expression bindValue;
                    if (IsMaybeRecord(subjectType))
                    {
                        // Maybe[T record]: bind to inner .value field.
                        bindValue = MakeMemberAccess(subject: subject, field: "value",
                            fieldType: subjectType!.TypeArguments![0], loc: loc);
                    }
                    else if (IsMaybeEntity(subjectType) && subjectType!.TypeArguments?.Count > 0)
                    {
                        // Maybe[T entity]: else arm binds to the inner entity via .value.read()
                        TypeInfo innerType = subjectType.TypeArguments[0];
                        bindValue = MakeEntityMaybeRead(subject: subject, subjectType: subjectType,
                            entityType: innerType, loc: loc);
                    }
                    else if (IsResultOrLookup(subjectType) && subjectType!.TypeArguments?.Count > 0
                             && isElseNarrowed)
                    {
                        // Result/Lookup: truly narrowed-to-T else arm — extract payload.
                        // Only when all non-T arms (Blank + all Crashable types) are handled.
                        TypeInfo innerType = subjectType.TypeArguments[0];
                        bindValue = MakeCarrierPayload(subject: subject, innerType: innerType,
                            loc: loc);
                    }
                    else
                    {
                        bindValue = subject;
                    }
                    binding = MakeBinding(name: ep.VariableName, value: bindValue, loc: loc);
                }

                return (null, binding);
            }

            case GuardPattern gp:
            {
                (Expression? innerCond, Statement? innerBinding) =
                    GetPatternCondition(pattern: gp.InnerPattern, subject: subject,
                        subjectType: subjectType);

                // Combine inner condition with guard: (inner AND guard), or just guard.
                Expression guardCond = innerCond != null
                    ? new BinaryExpression(
                        Left: innerCond,
                        Operator: BinaryOperator.And,
                        Right: gp.Guard,
                        Location: loc)
                      {
                          ResolvedType = boolType
                      }
                    : gp.Guard;

                return (guardCond, innerBinding);
            }

            // ── Carrier-type patterns ────────────────────────────────────────────

            case NonePattern:
                // Reached for Maybe[T record] and Maybe[T entity] (gated by IsLowerablePattern).
                if (IsMaybeEntity(subjectType))
                    return (MakeIsNoneCall(subject: subject, subjectType: subjectType!, loc: loc), null);
                // Maybe[T record]: use the present flag.
                return (MakeNotPresent(subject: subject, loc: loc, boolType: boolType), null);

            case TypePattern tp when IsMaybeEntity(subjectType):
            {
                // Maybe[T entity]: non-null check via is_none(), binding via .value.read()
                TypeInfo innerType = subjectType!.TypeArguments![0];
                Expression isNone = MakeIsNoneCall(subject: subject, subjectType: subjectType, loc: loc);
                Expression cond = new UnaryExpression(
                    Operator: UnaryOperator.Not,
                    Operand: isNone,
                    Location: loc)
                {
                    ResolvedType = boolType
                };
                Statement? binding = tp.VariableName != null
                    ? MakeBinding(
                        name: tp.VariableName,
                        value: MakeEntityMaybeRead(subject: subject, subjectType: subjectType,
                            entityType: innerType, loc: loc),
                        loc: loc)
                    : null;
                return (cond, binding);
            }

            case TypePattern tp when IsMaybeRecord(subjectType):
            {
                // Maybe[T record] presence check, optional binding to .value.
                TypeInfo? innerType = subjectType!.TypeArguments![0];
                Expression cond = MakePresentAccess(subject: subject, loc: loc);
                Statement? binding = tp.VariableName != null
                    ? MakeBinding(
                        name: tp.VariableName,
                        value: MakeMemberAccess(subject: subject, field: "value",
                            fieldType: innerType, loc: loc),
                        loc: loc)
                    : null;
                return (cond, binding);
            }

            case TypePattern tp when IsResultOrLookup(subjectType) || subjectType is VariantTypeInfo:
            {
                TypeInfo? u64Type = ctx.Registry.LookupType(name: "U64");
                TypeInfo? targetType = tp.Type.ResolvedType
                    ?? ctx.Registry.LookupType(name: tp.Type.Name);

                // Blank: type_id == 0
                if (tp.Type.Name == "Blank" || targetType?.Name == "Blank")
                    return (MakeTypeIdIsZero(subject: subject, loc: loc, boolType: boolType,
                        u64Type: u64Type), null);

                // Specific type: type_id == FNV-1a(type.FullName)
                string fullName = targetType?.FullName ?? tp.Type.Name;
                ulong typeId = ComputeTypeId(fullName: fullName);
                Expression cond = MakeTypeIdEquals(subject: subject, typeId: typeId, loc: loc,
                    boolType: boolType, u64Type: u64Type);

                Statement? binding = null;
                if (tp.VariableName != null && targetType != null)
                {
                    binding = MakeBinding(
                        name: tp.VariableName,
                        value: MakeCarrierPayload(subject: subject, innerType: targetType,
                            loc: loc),
                        loc: loc);
                }
                return (cond, binding);
            }

            // ── Flags patterns ───────────────────────────────────────────────────

            case FlagsPattern fp:
            {
                FlagsTestKind kind = fp.IsExact
                    ? FlagsTestKind.IsOnly
                    : FlagsTestKind.Is;
                var cond = new FlagsTestExpression(
                    Subject: subject,
                    Kind: kind,
                    TestFlags: fp.FlagNames,
                    Connective: fp.Connective,
                    ExcludedFlags: fp.ExcludedFlags,
                    Location: loc)
                {
                    ResolvedType = boolType
                };
                return (cond, null);
            }

            // ── Record destructuring patterns ─────────────────────────────────────

            case DestructuringPattern dp when subjectType is RecordTypeInfo rec:
                return (null, GenerateDestructuringBindings(bindings: dp.Bindings,
                    subject: subject, recordType: rec, loc: loc));

            case TypeDestructuringPattern tdp when subjectType is RecordTypeInfo rec:
                // Type check is always true (SA validated the subject type); just bind fields.
                return (null, GenerateDestructuringBindings(bindings: tdp.Bindings,
                    subject: subject, recordType: rec, loc: loc));

            default:
                // Non-lowerable pattern — unreachable if IsLowerable was checked correctly.
                return (null, null);
        }
    }

    // ─── Carrier expression builders ─────────────────────────────────────────────

    /// <summary>Builds <c>not subject.present</c> for Maybe absence check.</summary>
    private Expression MakeNotPresent(Expression subject, SourceLocation loc, TypeInfo? boolType)
    {
        return new UnaryExpression(
            Operator: UnaryOperator.Not,
            Operand: MakePresentAccess(subject: subject, loc: loc),
            Location: loc)
        {
            ResolvedType = boolType
        };
    }

    /// <summary>Builds <c>subject.present</c> member access (Bool).</summary>
    private Expression MakePresentAccess(Expression subject, SourceLocation loc)
    {
        TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");
        return new MemberExpression(Object: subject, PropertyName: "present", Location: loc)
        {
            ResolvedType = boolType
        };
    }

    /// <summary>Builds a member-access expression with a known field type.</summary>
    private static MemberExpression MakeMemberAccess(Expression subject, string field,
        TypeInfo? fieldType, SourceLocation loc)
    {
        return new MemberExpression(Object: subject, PropertyName: field, Location: loc)
        {
            ResolvedType = fieldType
        };
    }

    /// <summary>Builds <c>subject.type_id == 0_u64</c> for a Blank/absent check on Result/Lookup.</summary>
    private Expression MakeTypeIdIsZero(Expression subject, SourceLocation loc,
        TypeInfo? boolType, TypeInfo? u64Type)
    {
        var typeIdAccess = MakeMemberAccess(subject: subject, field: "type_id",
            fieldType: u64Type, loc: loc);
        var zero = new LiteralExpression(Value: 0UL, LiteralType: TokenType.U64Literal, Location: loc)
        {
            ResolvedType = u64Type
        };
        return new BinaryExpression(Left: typeIdAccess, Operator: BinaryOperator.Equal,
            Right: zero, Location: loc)
        {
            ResolvedType = boolType
        };
    }

    /// <summary>Builds <c>subject.type_id == typeId_u64</c> for a specific Result/Lookup arm.</summary>
    private Expression MakeTypeIdEquals(Expression subject, ulong typeId, SourceLocation loc,
        TypeInfo? boolType, TypeInfo? u64Type)
    {
        var typeIdAccess = MakeMemberAccess(subject: subject, field: "type_id",
            fieldType: u64Type, loc: loc);
        var constant = new LiteralExpression(Value: typeId, LiteralType: TokenType.U64Literal, Location: loc)
        {
            ResolvedType = u64Type
        };
        return new BinaryExpression(Left: typeIdAccess, Operator: BinaryOperator.Equal,
            Right: constant, Location: loc)
        {
            ResolvedType = boolType
        };
    }

    /// <summary>
    /// Builds a <see cref="CarrierPayloadExpression"/> that extracts the inner value
    /// from the carrier's <c>data_address</c> field cast to <paramref name="innerType"/>.
    /// </summary>
    private static CarrierPayloadExpression MakeCarrierPayload(Expression subject,
        TypeInfo innerType, SourceLocation loc)
    {
        return new CarrierPayloadExpression(
            Carrier: subject,
            ConcreteType: TypeInfoToExpr(type: innerType, loc: loc),
            Location: loc)
        {
            ResolvedType = innerType
        };
    }

    /// <summary>
    /// Returns the <c>Snatched[T]</c> field type from a <c>Maybe[T entity]</c> record's <c>value</c> field.
    /// Returns null if the type is not a recognized entity Maybe.
    /// </summary>
    private static TypeInfo? GetEntityMaybeSnatchedType(TypeInfo subjectType)
    {
        if (subjectType is not RecordTypeInfo rec) return null;
        MemberVariableInfo? valueField = rec.LookupMemberVariable(memberVariableName: "value");
        return valueField?.Type;
    }

    /// <summary>Builds <c>subject.value.is_none()</c> — the absence check for <c>Maybe[T entity]</c>.</summary>
    private Expression MakeIsNoneCall(Expression subject, TypeInfo subjectType, SourceLocation loc)
    {
        TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");
        TypeInfo? snatchedType = GetEntityMaybeSnatchedType(subjectType: subjectType);
        var valueAccess = new MemberExpression(Object: subject, PropertyName: "value", Location: loc)
        {
            ResolvedType = snatchedType
        };
        var isNoneMember = new MemberExpression(Object: valueAccess, PropertyName: "is_none",
            Location: loc)
        {
            ResolvedType = boolType
        };
        return new CallExpression(Callee: isNoneMember, Arguments: [], Location: loc)
        {
            ResolvedType = boolType
        };
    }

    /// <summary>Builds <c>subject.value.read()</c> — extracts the entity from <c>Maybe[T entity]</c>.</summary>
    private static Expression MakeEntityMaybeRead(Expression subject, TypeInfo subjectType,
        TypeInfo entityType, SourceLocation loc)
    {
        TypeInfo? snatchedType = GetEntityMaybeSnatchedType(subjectType: subjectType);
        var valueAccess = new MemberExpression(Object: subject, PropertyName: "value", Location: loc)
        {
            ResolvedType = snatchedType
        };
        var readMember = new MemberExpression(Object: valueAccess, PropertyName: "read", Location: loc)
        {
            ResolvedType = entityType
        };
        return new CallExpression(Callee: readMember, Arguments: [], Location: loc)
        {
            ResolvedType = entityType
        };
    }

    private static ulong ComputeTypeId(string fullName) =>
        TypeIdHelper.ComputeTypeId(fullName: fullName);

    /// <summary>
    /// Generates binding statements for a destructuring pattern applied to a record subject.
    /// Returns a single <see cref="DeclarationStatement"/> for one binding, or a
    /// <see cref="BlockStatement"/> for multiple (caller must flatten into the outer block).
    /// </summary>
    private Statement GenerateDestructuringBindings(
        List<DestructuringBinding> bindings, Expression subject, RecordTypeInfo recordType,
        SourceLocation loc)
    {
        var stmts = new List<Statement>(capacity: bindings.Count);
        foreach (DestructuringBinding b in bindings)
        {
            if (b.MemberVariableName == null || b.BindingName == null) continue;
            var member = recordType.LookupMemberVariable(memberVariableName: b.MemberVariableName);
            if (member == null) continue;
            Expression fieldAccess = MakeMemberAccess(subject: subject,
                field: b.MemberVariableName, fieldType: member.Type, loc: loc);
            stmts.Add(item: MakeBinding(name: b.BindingName, value: fieldAccess, loc: loc));
        }

        return stmts.Count == 1
            ? stmts[0]
            : new BlockStatement(Statements: stmts, Location: loc);
    }

    // ─── Type classification helpers ─────────────────────────────────────────────

    /// <summary>
    /// Returns the generic base name of a type (e.g., "Maybe" for <c>Maybe[S64]</c>).
    /// Returns the type's own name if it is not a resolved generic.
    /// </summary>
    private static string GetCarrierBaseName(TypeInfo type)
    {
        if (type is RecordTypeInfo { GenericDefinition: not null } r) return r.GenericDefinition.Name;
        if (type is EntityTypeInfo { GenericDefinition: not null } e) return e.GenericDefinition.Name;
        return type.Name;
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> is <c>Maybe[T]</c> where T is a record/value type
    /// (the two-field variant with an accessible <c>present</c> and <c>value</c> field).
    /// Entity-T Maybe has no <c>present</c> field and is not lowerable here.
    /// </summary>
    private static bool IsMaybeRecord(TypeInfo? type)
    {
        if (type == null) return false;
        if (GetCarrierBaseName(type: type) != "Maybe") return false;
        if (type.TypeArguments is not { Count: > 0 }) return false;
        // Entity-T Maybe only has `value: Snatched[T]`, no `present` field.
        return type.TypeArguments[0] is not EntityTypeInfo;
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> is <c>Maybe[T]</c> where T is an entity type
    /// (the single-field variant with only a <c>Snatched[T]</c> value field — no <c>present</c>).
    /// </summary>
    private static bool IsMaybeEntity(TypeInfo? type)
    {
        if (type == null) return false;
        if (GetCarrierBaseName(type: type) != "Maybe") return false;
        if (type.TypeArguments is not { Count: > 0 }) return false;
        return type.TypeArguments[0] is EntityTypeInfo;
    }

    /// <summary>Returns true if the type is <c>Result[T]</c> or <c>Lookup[T]</c>.</summary>
    private static bool IsResultOrLookup(TypeInfo? type)
    {
        if (type == null) return false;
        string baseName = GetCarrierBaseName(type: type);
        return baseName is "Result" or "Lookup";
    }

    private static bool IsResultType(TypeInfo? type)
    {
        if (type == null) return false;
        return GetCarrierBaseName(type: type) == "Result";
    }

    /// <summary>
    /// Returns true when every binding in the list has both a member name and a local alias
    /// and no nested pattern (i.e., it is a plain field → name binding).
    /// </summary>
    private static bool IsAllNamedSimpleBindings(List<DestructuringBinding> bindings) =>
        bindings.All(predicate: b =>
            b.MemberVariableName != null && b.BindingName != null && b.NestedPattern == null);

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static Statement MakeBinding(string name, Expression value, SourceLocation loc)
    {
        TypeInfo? type = value.ResolvedType;
        var decl = new VariableDeclaration(
            Name: name,
            Type: type != null ? TypeInfoToExpr(type: type, loc: loc) : null,
            Initializer: value,
            Visibility: VisibilityModifier.Secret,
            Location: loc);
        return new DeclarationStatement(Declaration: decl, Location: loc);
    }

    private static void AddTempVar(
        List<Statement> hoisted, string name, TypeInfo? typeHint,
        Expression initializer, SourceLocation loc)
    {
        var decl = new VariableDeclaration(
            Name: name,
            Type: typeHint != null ? TypeInfoToExpr(type: typeHint, loc: loc) : null,
            Initializer: initializer,
            Visibility: VisibilityModifier.Secret,
            Location: loc);
        hoisted.Add(item: new DeclarationStatement(Declaration: decl, Location: loc));
    }

    /// <summary>
    /// Converts a <see cref="TypeInfo"/> back to a <see cref="TypeExpression"/> for use
    /// in synthetic variable type annotations.
    /// </summary>
    private static TypeExpression TypeInfoToExpr(TypeInfo type, SourceLocation loc)
    {
        string baseName = type switch
        {
            RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition.Name,
            EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition.Name,
            _ => type.IsGenericResolution && type.Name.Contains(value: '[')
                ? type.Name[..type.Name.IndexOf(value: '[')]
                : type.Name
        };

        List<TypeExpression>? args = type.TypeArguments is { Count: > 0 }
            ? type.TypeArguments.Select(selector: a => TypeInfoToExpr(type: a, loc: loc)).ToList()
            : null;

        return new TypeExpression(Name: baseName, GenericArguments: args, Location: loc);
    }

    /// <summary>
    /// Maps a <see cref="TokenType"/> comparison operator (as used in
    /// <see cref="ComparisonPattern"/>) to the corresponding <see cref="BinaryOperator"/>.
    /// </summary>
    private static BinaryOperator TokenTypeToBinaryOp(TokenType op) =>
        op switch
        {
            TokenType.Equal            => BinaryOperator.Equal,
            TokenType.NotEqual         => BinaryOperator.NotEqual,
            TokenType.Less             => BinaryOperator.Less,
            TokenType.LessEqual        => BinaryOperator.LessEqual,
            TokenType.Greater          => BinaryOperator.Greater,
            TokenType.GreaterEqual     => BinaryOperator.GreaterEqual,
            TokenType.ReferenceEqual   => BinaryOperator.Identical,
            TokenType.ReferenceNotEqual => BinaryOperator.NotIdentical,
            _                          => BinaryOperator.Equal
        };

}

/// <summary>Extension to check for a non-null, non-empty string in one expression.</summary>
file static class StringExt
{
    public static bool HasValue(this string? s) => !string.IsNullOrEmpty(s);
}
