using System.Collections.Generic;
using System.Linq;
using Compiler.Tokenizer;
using SyntaxTree;

namespace Compiler.Postprocessing.Passes;

/// <summary>
/// Lowers domain-specific literal tokens to equivalent record constructor expressions
/// before codegen. After this pass, codegen never sees ByteSize, Duration, Character,
/// or ByteLetter literals -> only <see cref="CreatorExpression"/> nodes.
///
/// <para>Handled token types:</para>
/// <list type="bullet">
/// <item>ByteSize (<c>64kib</c>, <c>100mb</c>, ?? -> <c>ByteSize(value: bytes_u64)</c></item>
/// <item>Duration (<c>5s</c>, <c>100ms</c>, ?? -> <c>Duration(seconds: s64, nanoseconds: u32)</c></item>
/// <item>Character (<c>'a'</c>) -> <c>Character(from: codepoint_u32)</c></item>
/// <item>ByteLetter (<c>b'x'</c>) -> <c>Byte(from: byte_u8)</c></item>
/// </list>
///
/// <para><c>Bytes</c> literals (<c>b"..."</c>) are not lowered here -> they produce global-constant
/// entity allocations and remain in codegen.</para>
/// </summary>
internal sealed class LiteralLoweringPass
{
    private readonly Dictionary<string, Statement>? _variantBodies;

    /// <summary>
    /// Initializes a new instance with the dependencies required for its compiler phase.
    /// </summary>
    internal LiteralLoweringPass(PostprocessingContext ctx)
    {
        _variantBodies = ctx.VariantBodies;
    }

    // -----------------------------------------------------------------------------

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
                    Statement newBody = LowerStatement(r.Body);
                    if (!ReferenceEquals(newBody, r.Body))
                        program.Declarations[i] = r with { Body = newBody };
                    break;
                }
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
    }

    /// <summary>
    /// Runs this compiler phase over its configured input.
    /// </summary>
    public void RunOnVariantBodies()
    {
        if (_variantBodies == null) return;
        foreach (string key in _variantBodies.Keys.ToList())
        {
            Statement body = _variantBodies[key];
            Statement lowered = LowerStatement(body);
            if (!ReferenceEquals(lowered, body))
                _variantBodies[key] = lowered;
        }
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Lower member list as part of this compiler phase.
    /// </summary>
    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int j = 0; j < members.Count; j++)
        {
            if (members[j] is not RoutineDeclaration m) continue;
            Statement newBody = LowerStatement(m.Body);
            if (!ReferenceEquals(newBody, m.Body))
                members[j] = m with { Body = newBody };
        }
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Lower statement as part of this compiler phase.
    /// </summary>
    private Statement LowerStatement(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement b:
            {
                bool changed = false;
                var list = new List<Statement>(b.Statements.Count);
                foreach (Statement s in b.Statements)
                {
                    Statement ns = LowerStatement(s);
                    list.Add(ns);
                    if (!ReferenceEquals(ns, s)) changed = true;
                }
                return changed ? b with { Statements = list } : stmt;
            }
            case IfStatement ifs:
            {
                Expression cond = LowerExpression(ifs.Condition);
                Statement then = LowerStatement(ifs.ThenStatement);
                Statement? elseS = ifs.ElseStatement != null ? LowerStatement(ifs.ElseStatement) : null;
                bool changed = !ReferenceEquals(cond, ifs.Condition)
                               || !ReferenceEquals(then, ifs.ThenStatement)
                               || !ReferenceEquals(elseS, ifs.ElseStatement);
                return changed ? ifs with { Condition = cond, ThenStatement = then, ElseStatement = elseS } : stmt;
            }
            case WhileStatement w:
            {
                Expression cond = LowerExpression(w.Condition);
                Statement body = LowerStatement(w.Body);
                bool changed = !ReferenceEquals(cond, w.Condition) || !ReferenceEquals(body, w.Body);
                return changed ? w with { Condition = cond, Body = body } : stmt;
            }
            case LoopStatement loop:
            {
                Statement body = LowerStatement(loop.Body);
                return ReferenceEquals(body, loop.Body) ? stmt : loop with { Body = body };
            }
            case ForStatement f:
            {
                Expression iter = LowerExpression(f.Iterable);
                Statement body = LowerStatement(f.Body);
                bool changed = !ReferenceEquals(iter, f.Iterable) || !ReferenceEquals(body, f.Body);
                return changed ? f with { Iterable = iter, Body = body } : stmt;
            }
            case WhenStatement ws:
            {
                Expression subject = LowerExpression(ws.Expression);
                bool changed = !ReferenceEquals(subject, ws.Expression);
                var clauses = new List<WhenClause>(ws.Clauses.Count);
                foreach (WhenClause c in ws.Clauses)
                {
                    Statement cb = LowerStatement(c.Body);
                    if (!ReferenceEquals(cb, c.Body)) changed = true;
                    clauses.Add(!ReferenceEquals(cb, c.Body) ? c with { Body = cb } : c);
                }
                return changed ? ws with { Expression = subject, Clauses = clauses } : stmt;
            }
            case ReturnStatement { Value: not null } ret:
            {
                Expression v = LowerExpression(ret.Value);
                return ReferenceEquals(v, ret.Value) ? stmt : ret with { Value = v };
            }
            case AssignmentStatement assign:
            {
                Expression val = LowerExpression(assign.Value);
                return ReferenceEquals(val, assign.Value) ? stmt : assign with { Value = val };
            }
            case DeclarationStatement { Declaration: VariableDeclaration vd } ds when vd.Initializer != null:
            {
                Expression init = LowerExpression(vd.Initializer);
                if (ReferenceEquals(init, vd.Initializer)) return stmt;
                return ds with { Declaration = vd with { Initializer = init } };
            }
            case ExpressionStatement es:
            {
                Expression e = LowerExpression(es.Expression);
                return ReferenceEquals(e, es.Expression) ? stmt : es with { Expression = e };
            }
            case DiscardStatement ds:
            {
                Expression e = LowerExpression(ds.Expression);
                return ReferenceEquals(e, ds.Expression) ? stmt : ds with { Expression = e };
            }
            case ThrowStatement ts:
            {
                Expression e = LowerExpression(ts.Error);
                return ReferenceEquals(e, ts.Error) ? stmt : ts with { Error = e };
            }
            case BecomesStatement bs:
            {
                Expression v = LowerExpression(bs.Value);
                return ReferenceEquals(v, bs.Value) ? stmt : bs with { Value = v };
            }
            case UsingStatement us:
            {
                Statement body = LowerStatement(us.Body);
                return ReferenceEquals(body, us.Body) ? stmt : us with { Body = body };
            }
            case DangerStatement danger:
            {
                Statement newBody = LowerStatement(danger.Body);
                if (!ReferenceEquals(newBody, danger.Body) && newBody is BlockStatement bs2)
                    return danger with { Body = bs2 };
                return stmt;
            }
            default:
                return stmt;
        }
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Lower expression as part of this compiler phase.
    /// </summary>
    private Expression LowerExpression(Expression expr)
    {
        if (expr is LiteralExpression literal)
        {
            Expression? lowered = TryLowerLiteral(literal);
            if (lowered != null) return lowered;
            return expr;
        }

        switch (expr)
        {
            case BinaryExpression bin:
            {
                Expression l = LowerExpression(bin.Left);
                Expression r = LowerExpression(bin.Right);
                return ReferenceEquals(l, bin.Left) && ReferenceEquals(r, bin.Right)
                    ? expr : bin with { Left = l, Right = r };
            }
            case UnaryExpression un:
            {
                Expression o = LowerExpression(un.Operand);
                return ReferenceEquals(o, un.Operand) ? expr : un with { Operand = o };
            }
            case CallExpression call:
            {
                Expression callee = LowerExpression(call.Callee);
                List<Expression> args = LowerExpressionList(call.Arguments);
                bool changed = !ReferenceEquals(callee, call.Callee) || !ReferenceEquals(args, call.Arguments);
                return changed ? call with { Callee = callee, Arguments = args } : expr;
            }
            case NamedArgumentExpression named:
            {
                Expression v = LowerExpression(named.Value);
                return ReferenceEquals(v, named.Value) ? expr : named with { Value = v };
            }
            case MemberExpression mem:
            {
                Expression o = LowerExpression(mem.Object);
                return ReferenceEquals(o, mem.Object) ? expr : mem with { Object = o };
            }
            case OptionalMemberExpression omem:
            {
                Expression o = LowerExpression(omem.Object);
                return ReferenceEquals(o, omem.Object) ? expr : omem with { Object = o };
            }
            case IndexExpression idx:
            {
                Expression o = LowerExpression(idx.Object);
                Expression i = LowerExpression(idx.Index);
                bool changed = !ReferenceEquals(o, idx.Object) || !ReferenceEquals(i, idx.Index);
                if (!changed) return expr;
                var rewritten = idx with { Object = o, Index = i };
                rewritten.ResolvedType = idx.ResolvedType;
                rewritten.ResolvedSetItem = idx.ResolvedSetItem;
                return rewritten;
            }
            case TypeConversionExpression conv:
            {
                Expression e = LowerExpression(conv.Expression);
                return ReferenceEquals(e, conv.Expression) ? expr : conv with { Expression = e };
            }
            case StealExpression steal:
            {
                Expression o = LowerExpression(steal.Operand);
                return ReferenceEquals(o, steal.Operand) ? expr : steal with { Operand = o };
            }
            case GenericMethodCallExpression gmc:
            {
                Expression obj = LowerExpression(gmc.Object);
                List<Expression> args = LowerExpressionList(gmc.Arguments);
                bool changed = !ReferenceEquals(obj, gmc.Object) || !ReferenceEquals(args, gmc.Arguments);
                return changed ? gmc with { Object = obj, Arguments = args } : expr;
            }
            case GenericMemberExpression gmem:
            {
                Expression o = LowerExpression(gmem.Object);
                return ReferenceEquals(o, gmem.Object) ? expr : gmem with { Object = o };
            }
            case IsPatternExpression ip:
            {
                Expression e = LowerExpression(ip.Expression);
                return ReferenceEquals(e, ip.Expression) ? expr : ip with { Expression = e };
            }
            case FlagsTestExpression flags:
            {
                Expression s = LowerExpression(flags.Subject);
                return ReferenceEquals(s, flags.Subject) ? expr : flags with { Subject = s };
            }
            case ChainedComparisonExpression chain:
            {
                List<Expression> operands = LowerExpressionList(chain.Operands);
                return ReferenceEquals(operands, chain.Operands) ? expr : chain with { Operands = operands };
            }
            case CompoundAssignmentExpression comp:
            {
                Expression target = LowerExpression(comp.Target);
                Expression value = LowerExpression(comp.Value);
                bool changed = !ReferenceEquals(target, comp.Target) || !ReferenceEquals(value, comp.Value);
                return changed ? comp with { Target = target, Value = value } : expr;
            }
            case RangeExpression range:
            {
                Expression start = LowerExpression(range.Start);
                Expression end = LowerExpression(range.End);
                Expression? step = range.Step != null ? LowerExpression(range.Step) : null;
                bool changed = !ReferenceEquals(start, range.Start)
                               || !ReferenceEquals(end, range.End)
                               || !ReferenceEquals(step, range.Step);
                return changed ? range with { Start = start, End = end, Step = step } : expr;
            }
            case ConditionalExpression cond:
            {
                Expression c = LowerExpression(cond.Condition);
                Expression t = LowerExpression(cond.TrueExpression);
                Expression f = LowerExpression(cond.FalseExpression);
                bool changed = !ReferenceEquals(c, cond.Condition)
                               || !ReferenceEquals(t, cond.TrueExpression)
                               || !ReferenceEquals(f, cond.FalseExpression);
                return changed ? cond with { Condition = c, TrueExpression = t, FalseExpression = f } : expr;
            }
            case TupleLiteralExpression tuple:
            {
                List<Expression> elems = LowerExpressionList(tuple.Elements);
                return ReferenceEquals(elems, tuple.Elements) ? expr : tuple with { Elements = elems };
            }
            case ListLiteralExpression list:
            {
                List<Expression> elems = LowerExpressionList(list.Elements);
                return ReferenceEquals(elems, list.Elements) ? expr : list with { Elements = elems };
            }
            case SetLiteralExpression set:
            {
                List<Expression> elems = LowerExpressionList(set.Elements);
                return ReferenceEquals(elems, set.Elements) ? expr : set with { Elements = elems };
            }
            case DictLiteralExpression dict:
            {
                bool changed = false;
                var pairs = new List<(Expression Key, Expression Value)>(dict.Pairs.Count);
                foreach ((Expression k, Expression v) in dict.Pairs)
                {
                    Expression lk = LowerExpression(k);
                    Expression lv = LowerExpression(v);
                    pairs.Add((lk, lv));
                    if (!ReferenceEquals(lk, k) || !ReferenceEquals(lv, v)) changed = true;
                }
                return changed ? dict with { Pairs = pairs } : expr;
            }
            case CreatorExpression creator:
            {
                bool changed = false;
                var members = new List<(string Name, Expression Value)>(creator.MemberVariables.Count);
                foreach ((string name, Expression value) in creator.MemberVariables)
                {
                    Expression v = LowerExpression(value);
                    members.Add((name, v));
                    if (!ReferenceEquals(v, value)) changed = true;
                }
                return changed ? creator with { MemberVariables = members } : expr;
            }
            case InsertedTextExpression fstr:
            {
                bool changed = false;
                var parts = new List<InsertedTextPart>(fstr.Parts.Count);
                foreach (InsertedTextPart part in fstr.Parts)
                {
                    if (part is ExpressionPart ep)
                    {
                        Expression e = LowerExpression(ep.Expression);
                        if (!ReferenceEquals(e, ep.Expression))
                        {
                            parts.Add(ep with { Expression = e });
                            changed = true;
                            continue;
                        }
                    }
                    parts.Add(part);
                }
                return changed ? fstr with { Parts = parts } : expr;
            }
            case BackIndexExpression back:
            {
                Expression o = LowerExpression(back.Operand);
                return ReferenceEquals(o, back.Operand) ? expr : back with { Operand = o };
            }
            case BlockExpression block:
            {
                Expression v = LowerExpression(block.Value);
                return ReferenceEquals(v, block.Value) ? expr : block with { Value = v };
            }
            case CarrierPayloadExpression cpe:
            {
                Expression c = LowerExpression(cpe.Carrier);
                return ReferenceEquals(c, cpe.Carrier) ? expr : cpe with { Carrier = c };
            }
            default:
                return expr;
        }
    }

    /// <summary>
    /// Lower expression list as part of this compiler phase.
    /// </summary>
    private List<Expression> LowerExpressionList(List<Expression> list)
    {
        bool changed = false;
        var result = new List<Expression>(list.Count);
        foreach (Expression e in list)
        {
            Expression le = LowerExpression(e);
            result.Add(le);
            if (!ReferenceEquals(le, e)) changed = true;
        }
        return changed ? result : list;
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Attempts to lower literal and reports whether it succeeded.
    /// </summary>
    private static CreatorExpression? TryLowerLiteral(LiteralExpression literal)
    {
        SourceLocation loc = literal.Location;

        switch (literal.Value)
        {
            case char ch:
                return MakeCharacterCreator(char.ConvertToUtf32(ch.ToString(), 0), loc);

            case string s when IsByteSizeLiteralType(literal.LiteralType):
                return MakeByteSizeCreator(s, loc);

            case string s when IsDurationLiteralType(literal.LiteralType):
                return MakeDurationCreator(s, literal.LiteralType, loc);

            case string s when literal.LiteralType == TokenType.CharacterLiteral:
                return MakeCharacterCreator(s.Length > 0 ? char.ConvertToUtf32(s, 0) : 0, loc);

            case string s when literal.LiteralType == TokenType.ByteLetterLiteral:
                return MakeByteCreator(s.Length > 0 ? s[0] & 0xFF : 0, loc);
        }

        return null;
    }

    /// <summary>
    /// Builds the make byte size creator used by later compiler work.
    /// </summary>
    private static CreatorExpression MakeByteSizeCreator(string text, SourceLocation loc)
    {
        ulong bytes = ComputeByteSizeValue(text);
        var valueLit = new LiteralExpression(Value: bytes.ToString(), LiteralType: TokenType.U64Literal, Location: loc);
        return new CreatorExpression("ByteSize", null, [("value", valueLit)], loc);
    }

    /// <summary>
    /// Builds the make duration creator used by later compiler work.
    /// </summary>
    private static CreatorExpression MakeDurationCreator(string text, TokenType literalType, SourceLocation loc)
    {
        (long seconds, long nanoseconds) = ComputeDurationValues(text, literalType);
        var secsLit = new LiteralExpression(Value: seconds.ToString(), LiteralType: TokenType.S64Literal, Location: loc);
        var nsLit = new LiteralExpression(Value: nanoseconds.ToString(), LiteralType: TokenType.U32Literal, Location: loc);
        return new CreatorExpression("Duration", null, [("seconds", secsLit), ("nanoseconds", nsLit)], loc);
    }

    /// <summary>
    /// Builds the make character creator used by later compiler work.
    /// </summary>
    private static CreatorExpression MakeCharacterCreator(int codepoint, SourceLocation loc)
    {
        var cpLit = new LiteralExpression(Value: codepoint.ToString(), LiteralType: TokenType.U32Literal, Location: loc);
        return new CreatorExpression("Character", null, [("from", cpLit)], loc);
    }

    /// <summary>
    /// Builds the make byte creator used by later compiler work.
    /// </summary>
    private static CreatorExpression MakeByteCreator(int byteValue, SourceLocation loc)
    {
        var byteLit = new LiteralExpression(Value: byteValue.ToString(), LiteralType: TokenType.U8Literal, Location: loc);
        return new CreatorExpression("Byte", null, [("from", byteLit)], loc);
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Initializes a new instance with the dependencies required for its compiler phase.
    /// </summary>
    private static readonly (string Suffix, ulong Multiplier)[] ByteSizeSuffixes =
    [
        ("gib", 1_073_741_824UL),
        ("mib", 1_048_576UL),
        ("kib", 1_024UL),
        ("gb", 1_000_000_000UL),
        ("mb", 1_000_000UL),
        ("kb", 1_000UL),
        ("b", 1UL)
    ];

    /// <summary>
    /// Performs the compute byte size value step for this compiler phase.
    /// </summary>
    private static ulong ComputeByteSizeValue(string text)
    {
        string lower = text.ToLowerInvariant();
        foreach ((string suffix, ulong multiplier) in ByteSizeSuffixes)
        {
            if (!lower.EndsWith(suffix)) continue;
            string numPart = text[..^suffix.Length].TrimEnd('_').Replace("_", "");
            if (ulong.TryParse(numPart, out ulong value))
                return value * multiplier;
            break;
        }
        return 0;
    }

    /// <summary>
    /// Initializes a new instance with the dependencies required for its compiler phase.
    /// </summary>
    private static (long Seconds, long Nanoseconds) ComputeDurationValues(string text, TokenType literalType)
    {
        const long nsPerMicrosecond = 1_000L;
        const long nsPerMillisecond = 1_000_000L;
        const long nsPerSecond = 1_000_000_000L;
        const long secondsPerMinute = 60L;
        const long secondsPerHour = 3_600L;
        const long secondsPerDay = 86_400L;
        const long secondsPerWeek = 604_800L;

        string numericPart = literalType switch
        {
            TokenType.MillisecondLiteral => text[..^2],
            TokenType.MicrosecondLiteral => text[..^2],
            TokenType.NanosecondLiteral => text[..^2],
            _ => text[..^1]
        };
        numericPart = numericPart.Replace("_", "");
        if (!long.TryParse(numericPart, out long value)) value = 0;

        long seconds = 0, nanoseconds = 0;
        switch (literalType)
        {
            case TokenType.WeekLiteral:       seconds = value * secondsPerWeek; break;
            case TokenType.DayLiteral:        seconds = value * secondsPerDay; break;
            case TokenType.HourLiteral:       seconds = value * secondsPerHour; break;
            case TokenType.MinuteLiteral:     seconds = value * secondsPerMinute; break;
            case TokenType.SecondLiteral:     seconds = value; break;
            case TokenType.MillisecondLiteral:
                seconds = value / 1_000L;
                nanoseconds = (value % 1_000L) * nsPerMillisecond;
                break;
            case TokenType.MicrosecondLiteral:
                seconds = value / 1_000_000L;
                nanoseconds = (value % 1_000_000L) * nsPerMicrosecond;
                break;
            case TokenType.NanosecondLiteral:
                seconds = value / nsPerSecond;
                nanoseconds = value % nsPerSecond;
                break;
        }
        return (seconds, nanoseconds);
    }

    /// <summary>
    /// Returns whether is byte size literal type applies in the current compiler context.
    /// </summary>
    private static bool IsByteSizeLiteralType(TokenType type) =>
        type is TokenType.ByteLiteral or TokenType.KilobyteLiteral
            or TokenType.KibibyteLiteral or TokenType.MegabyteLiteral
            or TokenType.MebibyteLiteral or TokenType.GigabyteLiteral
            or TokenType.GibibyteLiteral;

    /// <summary>
    /// Returns whether is duration literal type applies in the current compiler context.
    /// </summary>
    private static bool IsDurationLiteralType(TokenType type) =>
        type is TokenType.WeekLiteral or TokenType.DayLiteral
            or TokenType.HourLiteral or TokenType.MinuteLiteral
            or TokenType.SecondLiteral or TokenType.MillisecondLiteral
            or TokenType.MicrosecondLiteral or TokenType.NanosecondLiteral;
}
