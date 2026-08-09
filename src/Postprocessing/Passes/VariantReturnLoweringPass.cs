using System.Collections.Generic;
using System.Linq;
using Compiler.Instantiation;
using Compiler.Synthesis;
using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Postprocessing.Passes;

/// <summary>
/// Phase 7: lowers compiler-synthesized <see cref="VariantReturnStatement"/> nodes into ordinary AST
/// so codegen needs no carrier-construction special-casing (and the dump shows no <c>#carrier</c>
/// pseudo-op).
///
/// STAGE 1 (current): only the <see cref="ErrorHandlingVariantKind.TryBool"/> variant, whose carrier
/// is a plain <c>Bool</c>. A <c>FromReturn</c> site becomes <c>return true</c>; a
/// <c>FromThrow</c>/<c>FromAbsent</c> site becomes <c>return false</c>. The thrown error is discarded
/// in a TryBool context (the routine returns Bool, never the error) — matching codegen's
/// <c>EmitTryBoolVariantReturn</c>, which drops it — so it is simply never constructed: nothing to
/// clean up, no leak. Try (Maybe) / Check (Result) / Lookup carriers still lower in codegen and are
/// handled by later stages.
/// </summary>
internal sealed class VariantReturnLoweringPass(PostprocessingContext ctx)
{
    private readonly Dictionary<string, Statement>? _variantBodies = ctx.VariantBodies;

    /// <summary>Return type (the concrete carrier, e.g. <c>Maybe[S64]</c>) of the routine whose body
    /// is being lowered — needed to construct the carrier record.</summary>
    private TypeInfo? _carrierReturn;

    private Dictionary<string, TypeInfo?>? _returnByKey;
    private Dictionary<string, TypeInfo?> ReturnByKey => _returnByKey ??=
        ctx.Registry.GetAllRoutines()
            .GroupBy(r => r.RegistryKey)
            .ToDictionary(g => g.Key, g => g.First().ReturnType);

    private TypeInfo? _boolType;
    private TypeInfo? BoolType => _boolType ??= ctx.Registry.LookupType(name: "Bool");

    private TypeInfo? _u64Type;
    private TypeInfo? U64Type => _u64Type ??= ctx.Registry.LookupType(name: "U64");

    /// <summary>A <c>Bool</c>-typed literal for a carrier's <c>present</c> flag.</summary>
    private LiteralExpression BoolLiteral(bool value, SourceLocation loc) =>
        new LiteralExpression(Value: value,
            LiteralType: value ? TokenType.True : TokenType.False,
            Location: loc)
        {
            ResolvedType = BoolType
        };

    /// <summary>A <c>U64</c>-typed literal (used for a carrier's <c>type_id</c> tag).</summary>
    private LiteralExpression U64Literal(ulong value, SourceLocation loc) =>
        new LiteralExpression(Value: value, LiteralType: TokenType.U64Literal, Location: loc)
        {
            ResolvedType = U64Type
        };

    /// <summary>Builds `return Carrier(type_id: …, payload: …)` for a Result/Lookup carrier.
    /// A null payload is omitted so the record's memberwise builder zero-fills it (the absent state).</summary>
    private Statement MakeCarrierReturn(RecordTypeInfo carrier, ulong typeId, Expression? payload,
        SourceLocation loc)
    {
        var members = new List<(string Name, Expression Value)> { ("type_id", U64Literal(typeId, loc)) };
        if (payload != null)
            members.Add(("payload", payload));
        return new ReturnStatement(
            Value: new CreatorExpression(TypeName: carrier.Name, TypeArguments: null,
                MemberVariables: members, Location: loc) { ResolvedType = carrier },
            Location: loc);
    }

    /// <summary>Lowers routine bodies in a single program (user file or stdlib file).</summary>
    public void Run(Program program)
    {
        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[i])
            {
                case RoutineDeclaration routine:
                {
                    _carrierReturn = routine.ResolvedInfo?.ReturnType;
                    Statement lowered = Lower(statement: routine.Body);
                    if (!ReferenceEquals(objA: lowered, objB: routine.Body))
                        program.Declarations[i] = routine with { Body = lowered };
                    break;
                }
                case EntityDeclaration entity:
                    LowerMembers(members: entity.Members);
                    break;
                case RecordDeclaration record:
                    LowerMembers(members: record.Members);
                    break;
                case CrashableDeclaration crashable:
                    LowerMembers(members: crashable.Members);
                    break;
            }
        }
    }

    /// <summary>Lowers the synthesized try_/check_/lookup_ variant bodies.</summary>
    public void RunOnVariantBodies()
    {
        if (_variantBodies == null) return;
        foreach (string key in _variantBodies.Keys.ToList())
        {
            _carrierReturn = ReturnByKey.GetValueOrDefault(key: key);
            Statement lowered = Lower(statement: _variantBodies[key: key]);
            if (!ReferenceEquals(objA: lowered, objB: _variantBodies[key: key]))
                _variantBodies[key: key] = lowered;
        }
    }

    /// <summary>Lowers carrier-return sites inside monomorphized generic instances (e.g. a concrete
    /// <c>ListEmittable[Character].try_emit</c>), which are not part of the program/variant-body tracks.</summary>
    public void RunOnMonomorphizedBodies()
    {
        if (ctx.MonomorphizedBodies is not { } bodies) return;
        foreach (string key in bodies.Keys.ToList())
        {
            MonomorphizedBody mono = bodies[key: key];
            _carrierReturn = mono.Info.ReturnType;
            Statement lowered = Lower(statement: mono.Ast.Body);
            if (!ReferenceEquals(objA: lowered, objB: mono.Ast.Body))
                bodies[key: key] = mono with { Ast = mono.Ast with { Body = lowered } };
        }
    }

    private void LowerMembers(List<SyntaxTree.Declaration> members)
    {
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i] is not RoutineDeclaration routine) continue;
            _carrierReturn = routine.ResolvedInfo?.ReturnType;
            Statement lowered = Lower(statement: routine.Body);
            if (!ReferenceEquals(objA: lowered, objB: routine.Body))
                members[i] = routine with { Body = lowered };
        }
    }

    private Statement Lower(Statement statement)
    {
        switch (statement)
        {
            case VariantReturnStatement { VariantKind: ErrorHandlingVariantKind.TryBool } vr:
            {
                bool present = vr.SiteKind == VariantSiteKind.FromReturn;
                return new ReturnStatement(
                    Value: new LiteralExpression(
                        Value: present,
                        LiteralType: present ? TokenType.True : TokenType.False,
                        Location: vr.Location),
                    Location: vr.Location);
            }

            // Try → Maybe[T] (a plain `{present: Bool, value: T}` record) built with a real
            // CreatorExpression: present carries the value; throw / absent / return-a-crashable = absent.
            case VariantReturnStatement { VariantKind: ErrorHandlingVariantKind.Try } vr
                when _carrierReturn is RecordTypeInfo maybe:
            {
                if (vr.SiteKind == VariantSiteKind.FromVariantPassthrough && vr.Value != null)
                    return new ReturnStatement(Value: vr.Value, Location: vr.Location);

                bool present = vr.SiteKind == VariantSiteKind.FromReturn
                               && vr.Value?.ResolvedType is not CrashableTypeInfo;
                bool hasValue = present && vr.Value is not null
                                and not IdentifierExpression { Name: "None" };

                var members = new List<(string Name, Expression Value)>
                {
                    ("present", BoolLiteral(value: present, loc: vr.Location))
                };
                if (hasValue)
                    members.Add(("value", vr.Value!));

                return new ReturnStatement(
                    Value: new CreatorExpression(TypeName: maybe.Name, TypeArguments: null,
                        MemberVariables: members, Location: vr.Location) { ResolvedType = maybe },
                    Location: vr.Location);
            }

            // Check → Result[T] / Lookup → Lookup[T] (record { type_id: U64, payload: CPtr }): build the
            // record directly. type_id = FNV of the payload type (matches the reader); the payload is the
            // entity/error POINTER stored straight into the CPtr slot. Absent = type_id 0, payload zeroed.
            // Scalar payloads still need a reinterpret-to-CPtr, so those fall through to codegen for now.
            case VariantReturnStatement
            {
                VariantKind: ErrorHandlingVariantKind.Check or ErrorHandlingVariantKind.Lookup
            } vr when _carrierReturn is RecordTypeInfo carrier:
            {
                if (vr.SiteKind == VariantSiteKind.FromVariantPassthrough && vr.Value != null)
                    return new ReturnStatement(Value: vr.Value, Location: vr.Location);

                if (vr.SiteKind == VariantSiteKind.FromAbsent
                    || (vr.SiteKind == VariantSiteKind.FromReturn
                        && vr.Value is null or IdentifierExpression { Name: "None" }))
                    return MakeCarrierReturn(carrier: carrier, typeId: 0, payload: null, loc: vr.Location);

                // Any success/error payload: type_id = FNV of the payload type; the value is stored into
                // the CPtr slot (codegen reinterprets a scalar via inttoptr, an entity is already a ptr).
                if (vr.Value is { ResolvedType: { } payloadType })
                    return MakeCarrierReturn(carrier: carrier,
                        typeId: Compiler.TypeIdHelper.ComputeTypeId(fullName: payloadType.FullName),
                        payload: vr.Value, loc: vr.Location);

                // No resolved type on the value — leave for codegen.
                return statement;
            }

            case BlockStatement block:
            {
                List<Statement>? outStmts = null;
                for (int i = 0; i < block.Statements.Count; i++)
                {
                    Statement lowered = Lower(statement: block.Statements[i]);
                    if (!ReferenceEquals(objA: lowered, objB: block.Statements[i]) && outStmts == null)
                        outStmts = new List<Statement>(collection: block.Statements.Take(count: i));
                    outStmts?.Add(item: lowered);
                }
                return outStmts == null ? block : block with { Statements = outStmts };
            }

            case IfStatement ifs:
            {
                Statement then = Lower(statement: ifs.ThenStatement);
                Statement? els = ifs.ElseStatement != null ? Lower(statement: ifs.ElseStatement) : null;
                return ReferenceEquals(objA: then, objB: ifs.ThenStatement)
                       && ReferenceEquals(objA: els, objB: ifs.ElseStatement)
                    ? ifs
                    : ifs with { ThenStatement = then, ElseStatement = els };
            }

            case WhileStatement whileStmt:
            {
                Statement body = Lower(statement: whileStmt.Body);
                Statement? eb = whileStmt.ElseBranch != null ? Lower(statement: whileStmt.ElseBranch) : null;
                return ReferenceEquals(objA: body, objB: whileStmt.Body)
                       && ReferenceEquals(objA: eb, objB: whileStmt.ElseBranch)
                    ? whileStmt
                    : whileStmt with { Body = body, ElseBranch = eb };
            }

            case LoopStatement loop:
            {
                Statement body = Lower(statement: loop.Body);
                return ReferenceEquals(objA: body, objB: loop.Body) ? loop : loop with { Body = body };
            }

            case EachStatement each:
            {
                Statement body = Lower(statement: each.Body);
                Statement? eb = each.ElseBranch != null ? Lower(statement: each.ElseBranch) : null;
                return ReferenceEquals(objA: body, objB: each.Body)
                       && ReferenceEquals(objA: eb, objB: each.ElseBranch)
                    ? each
                    : each with { Body = body, ElseBranch = eb };
            }

            case WhenStatement whenStmt:
            {
                bool changed = false;
                var clauses = new List<WhenClause>(capacity: whenStmt.Clauses.Count);
                foreach (WhenClause clause in whenStmt.Clauses)
                {
                    Statement lb = Lower(statement: clause.Body);
                    clauses.Add(item: ReferenceEquals(objA: lb, objB: clause.Body)
                        ? clause
                        : clause with { Body = lb });
                    changed |= !ReferenceEquals(objA: lb, objB: clause.Body);
                }
                return changed ? whenStmt with { Clauses = clauses } : whenStmt;
            }

            case DangerStatement danger:
            {
                var body = (BlockStatement)Lower(statement: danger.Body);
                return ReferenceEquals(objA: body, objB: danger.Body) ? danger : danger with { Body = body };
            }

            case UsingStatement usingStmt:
            {
                Statement body = Lower(statement: usingStmt.Body);
                Statement? fb = usingStmt.FallbackBody != null ? Lower(statement: usingStmt.FallbackBody) : null;
                return ReferenceEquals(objA: body, objB: usingStmt.Body)
                       && ReferenceEquals(objA: fb, objB: usingStmt.FallbackBody)
                    ? usingStmt
                    : usingStmt with { Body = body, FallbackBody = fb };
            }

            default:
                return statement;
        }
    }
}
