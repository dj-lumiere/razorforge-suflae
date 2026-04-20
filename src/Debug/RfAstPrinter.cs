using System.Text;
using Compiler.Lexer;
using Compiler.Instantiation;
using Compiler.Resolution;
using TypeModel.Symbols;
using SyntaxTree;

namespace Builder;

/// <summary>
/// Prints the post-desugared AST back to RF-like source text for debugging.
/// Implements <see cref="IAstVisitor{T}"/> with <c>string</c> as the result type.
/// </summary>
public sealed class RfAstPrinter : IAstVisitor<string>
{
    private int _indent;
    private string I => new string(' ', _indent * 2);

    // ── Entry point ───────────────────────────────────────────────────────────

    /// <summary>
    /// Produces a human-readable dump of all user programs and synthesized bodies
    /// after the full desugaring pipeline has run.
    /// </summary>
    public string PrintMultiProgram(
        IEnumerable<(SyntaxTree.Program Program, string FilePath, string Module)> programs,
        IReadOnlyDictionary<string, Statement> synthesizedBodies,
        TypeRegistry registry,
        IEnumerable<(SyntaxTree.Program Program, string FilePath, string Module)>? stdlibPrograms = null,
        IReadOnlyDictionary<string, MonomorphizedBody>? preMonomorphizedBodies = null)
    {
        // Build RegistryKey → RoutineInfo for signature reconstruction.
        Dictionary<string, RoutineInfo> routineByKey = registry.GetAllRoutines()
            .Where(r => r.IsSynthesized)
            .GroupBy(r => r.RegistryKey)
            .ToDictionary(g => g.Key, g => g.First());

        var sb = new StringBuilder();

        if (stdlibPrograms != null)
        {
            sb.AppendLine("# ═══════════════════════════════════════════════════════");
            sb.AppendLine("# STDLIB");
            sb.AppendLine("# ═══════════════════════════════════════════════════════");
            sb.AppendLine();
            foreach ((SyntaxTree.Program prog, string filePath, string module) in stdlibPrograms)
            {
                sb.AppendLine($"# === stdlib module {module} ===");
                sb.AppendLine($"# file: {filePath}");
                sb.AppendLine();
                _indent = 0;
                sb.AppendLine(prog.Accept(this));
                sb.AppendLine();
            }
            sb.AppendLine("# ═══════════════════════════════════════════════════════");
            sb.AppendLine("# USER PROGRAMS");
            sb.AppendLine("# ═══════════════════════════════════════════════════════");
            sb.AppendLine();
        }

        foreach ((SyntaxTree.Program prog, string filePath, string module) in programs)
        {
            sb.AppendLine($"# === module {module} ===");
            sb.AppendLine($"# file: {filePath}");
            sb.AppendLine();
            _indent = 0;
            sb.AppendLine(prog.Accept(this));
            sb.AppendLine();
        }

        if (synthesizedBodies.Count > 0)
        {
            sb.AppendLine("# === SYNTHESIZED BODIES ===");
            foreach ((string key, Statement body) in synthesizedBodies)
            {
                _indent = 0;
                if (routineByKey.TryGetValue(key: key, value: out RoutineInfo? ri))
                    sb.AppendLine(FormatRoutineSignature(ri: ri));
                else
                    sb.AppendLine($"# {key}");
                sb.AppendLine(PrintBodyOf(body));
                sb.AppendLine();
            }
        }

        if (preMonomorphizedBodies is { Count: > 0 })
        {
            sb.AppendLine("# === MONOMORPHIZED BODIES ===");
            foreach ((string key, MonomorphizedBody mono) in preMonomorphizedBodies)
            {
                _indent = 0;
                if (mono.IsSynthesized)
                {
                    // Wired/synthesized routine — no AST body; note it for reference.
                    sb.AppendLine($"# {key} [synthesized, no AST body]");
                }
                else
                {
                    // Use RoutineInfo for a fully-qualified signature, then print the rewritten body.
                    sb.AppendLine(FormatRoutineSignature(ri: mono.Info));
                    sb.AppendLine(PrintBodyOf(mono.Ast.Body));
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string FormatRoutineSignature(RoutineInfo ri)
    {
        string ownerPrefix = ri.OwnerType != null ? $"{ri.OwnerType.FullName}." : "";
        string failable = ri.IsFailable ? "!" : "";
        string paramStr = ri.Parameters.Count == 0
            ? ""
            : string.Join(", ", ri.Parameters.Select(p => $"{p.Name}: {p.Type.FullName}"));
        // null ReturnType on a RoutineInfo means the routine returns Blank but SA never ran
        // on it (stdlib / synthesized routines). Show Blank rather than <ERROR>.
        string retStr = ri.ReturnType != null
            ? $" -> {ri.ReturnType.FullName}"
            : " -> Blank";
        string annotations = ri.DeclaredModification == SemanticVerification.Enums.ModificationCategory.Readonly
            ? "@readonly\n"
            : "";
        return $"{annotations}routine {ownerPrefix}{ri.Name}{failable}({paramStr}){retStr}";
    }

    // ── Indent helpers ────────────────────────────────────────────────────────

    /// <summary>Prints a list of statements at _indent+1.</summary>
    private string PrintBody(IEnumerable<Statement> stmts)
    {
        _indent++;
        var lines = stmts.Select(s => s.Accept(this)).ToList();
        _indent--;
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Prints a single statement that acts as a body (e.g. ThenStatement of an if).
    /// If the statement is a BlockStatement, its children are printed at _indent+1.
    /// Otherwise the statement itself is printed at _indent+1.
    /// </summary>
    private string PrintBodyOf(Statement stmt)
    {
        if (stmt is BlockStatement block)
            return PrintBody(block.Statements);
        _indent++;
        string result = stmt.Accept(this);
        _indent--;
        return result;
    }

    // ── Pattern helper ────────────────────────────────────────────────────────

    private string PrintPattern(Pattern p) => p switch
    {
        LiteralPattern lit => FormatLiteralValue(lit.Value, lit.LiteralType),
        TypePattern tp =>
            $"is {tp.Type.Accept(this)}{(tp.VariableName != null ? " " + tp.VariableName : "")}",
        NonePattern => "is None",
        CrashablePattern cp =>
            $"is {(cp.ErrorType != null ? cp.ErrorType.Accept(this) : "Crashable")}" +
            $"{(cp.VariableName != null ? " " + cp.VariableName : "")}",
        ElsePattern ep => ep.VariableName != null ? $"else {ep.VariableName}" : "else",
        WildcardPattern => "_",
        IdentifierPattern ip => ip.Name,
        ExpressionPattern ep => ep.Expression.Accept(this),
        GuardPattern gp => $"{PrintPattern(gp.InnerPattern)} where {gp.Guard.Accept(this)}",
        FlagsPattern fp =>
            $"is {string.Join(fp.Connective == FlagsTestConnective.And ? " and " : " or ", fp.FlagNames)}",
        _ => $"#{p.GetType().Name}"
    };

    private static string FormatLiteralValue(object value, TokenType literalType) => literalType switch
    {
        TokenType.TextLiteral => $"\"{EscapeText(value?.ToString() ?? "")}\"",
        TokenType.True => "true",
        TokenType.False => "false",
        TokenType.S8Literal => $"S8({StripSuffix(value, "s8")})",
        TokenType.S16Literal => $"S16({StripSuffix(value, "s16")})",
        TokenType.S32Literal => $"S32({StripSuffix(value, "s32")})",
        TokenType.S64Literal => $"S64({StripSuffix(value, "s64")})",
        TokenType.S128Literal => $"S128({StripSuffix(value, "s128")})",
        TokenType.U8Literal => $"U8({StripSuffix(value, "u8")})",
        TokenType.U16Literal => $"U16({StripSuffix(value, "u16")})",
        TokenType.U32Literal => $"U32({StripSuffix(value, "u32")})",
        TokenType.U64Literal => $"U64({StripSuffix(value, "u64")})",
        TokenType.U128Literal => $"U128({StripSuffix(value, "u128")})",
        TokenType.F16Literal => $"F16({StripSuffix(value, "f16")})",
        TokenType.F32Literal => $"F32({StripSuffix(value, "f32")})",
        TokenType.F64Literal => $"F64({StripSuffix(value, "f64")})",
        TokenType.F128Literal => $"F128({StripSuffix(value, "f128")})",
        TokenType.D32Literal => $"D32({StripSuffix(value, "d32")})",
        TokenType.D64Literal => $"D64({StripSuffix(value, "d64")})",
        TokenType.D128Literal => $"D128({StripSuffix(value, "d128")})",
        _ => value?.ToString() ?? "null"
    };

    /// <summary>
    /// Strips the type suffix (e.g. "s64") and any trailing separator underscores
    /// from a raw numeric token text. Handles both string values (raw token text)
    /// and already-parsed numeric values.
    /// </summary>
    private static string StripSuffix(object value, string suffix)
    {
        string s = value?.ToString() ?? "0";
        if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            s = s[..^suffix.Length].TrimEnd('_');
        return s;
    }

    private static string EscapeText(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ═════════════════════════════════════════════════════════════════════════
    // EXPRESSIONS
    // ═════════════════════════════════════════════════════════════════════════

    public string VisitLiteralExpression(LiteralExpression node) =>
        FormatLiteralValue(node.Value, node.LiteralType);

    public string VisitIdentifierExpression(IdentifierExpression node) => node.Name;

    public string VisitBinaryExpression(BinaryExpression node) =>
        $"({node.Left.Accept(this)} {node.Operator.ToStringRepresentation()} {node.Right.Accept(this)})";

    public string VisitUnaryExpression(UnaryExpression node)
    {
        string operand = node.Operand.Accept(this);
        string? opStr = node.Operator.ToStringRepresentation();
        if (node.Operator == UnaryOperator.ForceUnwrap)
            return $"{operand}!!";
        return opStr != null ? $"{opStr} {operand}" : $"#{node.Operator}({operand})";
    }

    public string VisitCompoundAssignmentExpression(CompoundAssignmentExpression node) =>
        $"{node.Target.Accept(this)} {node.Operator.ToStringRepresentation()}= {node.Value.Accept(this)}";

    public string VisitCallExpression(CallExpression node) =>
        $"{node.Callee.Accept(this)}({string.Join(", ", node.Arguments.Select(a => a.Accept(this)))})";

    public string VisitNamedArgumentExpression(NamedArgumentExpression node) =>
        $"{node.Name}: {node.Value.Accept(this)}";

    public string VisitMemberExpression(MemberExpression node) =>
        $"{node.Object.Accept(this)}.{node.PropertyName}";

    public string VisitOptionalMemberExpression(OptionalMemberExpression node) =>
        $"{node.Object.Accept(this)}?.{node.PropertyName}";

    public string VisitCreatorExpression(CreatorExpression node)
    {
        string typeArgs = node.TypeArguments != null && node.TypeArguments.Count > 0
            ? $"[{string.Join(", ", node.TypeArguments.Select(t => t.Accept(this)))}]"
            : "";
        string members = string.Join(", ",
            node.MemberVariables.Select(mv => $"{mv.Name}: {mv.Value.Accept(this)}"));
        return $"{node.TypeName}{typeArgs}({members})";
    }

    public string VisitTypeExpression(TypeExpression node)
    {
        if (node.GenericArguments == null || node.GenericArguments.Count == 0)
            return node.Name;
        string args = string.Join(", ", node.GenericArguments.Select(a => a.Accept(this)));
        return $"{node.Name}[{args}]";
    }

    public string VisitTypeConversionExpression(TypeConversionExpression node) =>
        node.IsMethodStyle
            ? $"{node.Expression.Accept(this)}.{node.TargetType}!()"
            : $"{node.TargetType}!({node.Expression.Accept(this)})";

    public string VisitInsertedTextExpression(InsertedTextExpression node)
    {
        var sb = new StringBuilder("f\"");
        foreach (InsertedTextPart part in node.Parts)
        {
            switch (part)
            {
                case TextPart tp:
                    sb.Append(tp.Text.Replace("{", "{{").Replace("}", "}}"));
                    break;
                case ExpressionPart ep:
                    string inner = ep.Expression.Accept(this);
                    if (ep.FormatSpec != null)
                        sb.Append($"{{{inner}:{ep.FormatSpec}}}");
                    else
                        sb.Append($"{{{inner}}}");
                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    public string VisitTypeIdExpression(TypeIdExpression node) =>
        $"#typeid({node.Type.Accept(this)})";

    public string VisitCarrierPayloadExpression(CarrierPayloadExpression node) =>
        $"#carrier_payload({node.Carrier.Accept(this)}, {node.ConcreteType.Accept(this)})";

    public string VisitIsPatternExpression(IsPatternExpression node)
    {
        string patStr = PrintPattern(node.Pattern);
        return node.IsNegated
            ? $"({node.Expression.Accept(this)} isnot {patStr[3..]})"  // strip "is "
            : $"({node.Expression.Accept(this)} {patStr})";
    }

    public string VisitDictEntryLiteralExpression(DictEntryLiteralExpression node) =>
        $"{node.Key.Accept(this)}: {node.Value.Accept(this)}";

    // Rarely appear post-desugaring — use fallback
    public string VisitListLiteralExpression(ListLiteralExpression node) =>
        $"[{string.Join(", ", node.Elements.Select(e => e.Accept(this)))}]";

    public string VisitSetLiteralExpression(SetLiteralExpression node) =>
        $"{{{string.Join(", ", node.Elements.Select(e => e.Accept(this)))}}}";

    public string VisitDictLiteralExpression(DictLiteralExpression node) =>
        $"{{{string.Join(", ", node.Pairs.Select(p => $"{p.Key.Accept(this)}: {p.Value.Accept(this)}"))}}}";

    public string VisitTupleLiteralExpression(TupleLiteralExpression node) =>
        node.Elements.Count == 1
            ? $"({node.Elements[0].Accept(this)},)"
            : $"({string.Join(", ", node.Elements.Select(e => e.Accept(this)))})";

    public string VisitWithExpression(WithExpression node)
    {
        var updates = node.Updates.Select(u =>
        {
            string path = u.MemberVariablePath != null
                ? string.Join(".", u.MemberVariablePath)
                : "";
            string idx = u.Index != null ? $"[{u.Index.Accept(this)}]" : "";
            string target = path.Length > 0 && idx.Length > 0
                ? $"{path}{idx}"
                : path.Length > 0 ? path : idx;
            return $"{target}: {u.Value.Accept(this)}";
        });
        return $"with({node.Base.Accept(this)}, {string.Join(", ", updates)})";
    }

    public string VisitIndexExpression(IndexExpression node) =>
        $"{node.Object.Accept(this)}[{node.Index.Accept(this)}]";

    public string VisitSliceExpression(SliceExpression node) =>
        $"{node.Object.Accept(this)}[{node.Start.Accept(this)} to {node.End.Accept(this)}]";

    public string VisitConditionalExpression(ConditionalExpression node) =>
        $"{node.TrueExpression.Accept(this)} if {node.Condition.Accept(this)} else {node.FalseExpression.Accept(this)}";

    public string VisitBlockExpression(BlockExpression node) =>
        node.Value.Accept(this);

    public string VisitChainedComparisonExpression(ChainedComparisonExpression node)
    {
        var sb = new StringBuilder(node.Operands[0].Accept(this));
        for (int i = 0; i < node.Operators.Count; i++)
        {
            sb.Append($" {node.Operators[i].ToStringRepresentation()} {node.Operands[i + 1].Accept(this)}");
        }
        return sb.ToString();
    }

    public string VisitRangeExpression(RangeExpression node)
    {
        string start = node.Start.Accept(this);
        string end = node.End.Accept(this);
        string keyword = node.IsDescending ? "downto" : node.IsExclusive ? "til" : "to";
        string step = node.Step != null ? $" by {node.Step.Accept(this)}" : "";
        return $"({start} {keyword} {end}{step})";
    }

    public string VisitLambdaExpression(LambdaExpression node)
    {
        string parms = string.Join(", ", node.Parameters.Select(p =>
            p.Type != null ? $"{p.Name}: {p.Type.Accept(this)}" : p.Name));
        return $"({parms}) => {node.Body.Accept(this)}";
    }

    public string VisitGenericMethodCallExpression(GenericMethodCallExpression node)
    {
        string typeArgs = node.TypeArguments.Count > 0
            ? $"[{string.Join(", ", node.TypeArguments.Select(t => t.Accept(this)))}]"
            : "";
        string args = string.Join(", ", node.Arguments.Select(a => a.Accept(this)));
        // Type constructor: Object and MethodName are the same identifier (e.g. SortedDict[S64, S64]())
        if (node.Object is IdentifierExpression id && id.Name == node.MethodName)
            return $"{node.MethodName}{typeArgs}({args})";
        // Generic method call on a receiver (e.g. buf.read![U8](offset))
        return $"{node.Object.Accept(this)}.{node.MethodName}{typeArgs}({args})";
    }

    public string VisitGenericMemberExpression(GenericMemberExpression node)
    {
        string typeArgs = node.TypeArguments.Count > 0
            ? $"[{string.Join(", ", node.TypeArguments.Select(t => t.Accept(this)))}]"
            : "";
        return $"{node.Object.Accept(this)}.{node.MemberName}{typeArgs}";
    }

    public string VisitFlagsTestExpression(FlagsTestExpression node)
    {
        string connective = node.Connective == FlagsTestConnective.Or ? " or " : " and ";
        string flags = string.Join(connective, node.TestFlags);
        string kind = node.Kind switch
        {
            FlagsTestKind.Is => "is",
            FlagsTestKind.IsNot => "isnot",
            FlagsTestKind.IsOnly => "isonly",
            _ => node.Kind.ToString().ToLower()
        };
        string excluded = node.ExcludedFlags is { Count: > 0 }
            ? $" but {string.Join(", ", node.ExcludedFlags)}"
            : "";
        return $"({node.Subject.Accept(this)} {kind} {flags}{excluded})";
    }

    public string VisitWhenExpression(WhenExpression node)
    {
        var sb = new StringBuilder();
        string subject = node.Expression != null ? $" {node.Expression.Accept(this)}" : "";
        sb.AppendLine($"when{subject}");
        _indent++;
        foreach (WhenClause clause in node.Clauses)
        {
            string patStr = PrintPattern(clause.Pattern);
            string body = clause.Body.Accept(this).TrimStart();
            sb.AppendLine($"{I}{patStr} => {body}");
        }
        _indent--;
        return sb.ToString().TrimEnd();
    }

    public string VisitStealExpression(StealExpression node) =>
        $"steal {node.Operand.Accept(this)}";

    public string VisitWaitforExpression(WaitforExpression node)
    {
        string timeout = node.Timeout != null ? $" within {node.Timeout.Accept(this)}" : "";
        return $"waitfor {node.Operand.Accept(this)}{timeout}";
    }

    public string VisitDependentWaitforExpression(DependentWaitforExpression node)
    {
        string deps = string.Join(", ", node.Dependencies.Select(d =>
            d.BindingName != null
                ? $"{d.DependencyExpr.Accept(this)} as {d.BindingName}"
                : d.DependencyExpr.Accept(this)));
        string timeout = node.Timeout != null ? $" within {node.Timeout.Accept(this)}" : "";
        return $"after {deps} waitfor {node.Operand.Accept(this)}{timeout}";
    }

    public string VisitBackIndexExpression(BackIndexExpression node) =>
        $"^{node.Operand.Accept(this)}";

    // ═════════════════════════════════════════════════════════════════════════
    // STATEMENTS
    // ═════════════════════════════════════════════════════════════════════════

    public string VisitExpressionStatement(ExpressionStatement node) =>
        $"{I}{node.Expression.Accept(this)}";

    public string VisitDeclarationStatement(DeclarationStatement node) =>
        node.Declaration.Accept(this);

    public string VisitVariableDeclaration(VariableDeclaration node)
    {
        string typeStr = node.Type != null ? $": {node.Type.Accept(this)}" : "";
        string initStr = node.Initializer != null ? $" = {node.Initializer.Accept(this)}" : "";
        return $"{I}var {node.Name}{typeStr}{initStr}";
    }

    public string VisitAssignmentStatement(AssignmentStatement node) =>
        $"{I}{node.Target.Accept(this)} = {node.Value.Accept(this)}";

    public string VisitReturnStatement(ReturnStatement node) =>
        node.Value != null
            ? $"{I}return {node.Value.Accept(this)}"
            : $"{I}return <ERROR>";

    public string VisitBecomesStatement(BecomesStatement node) =>
        $"{I}becomes {node.Value.Accept(this)}";

    public string VisitThrowStatement(ThrowStatement node) =>
        $"{I}throw {node.Error.Accept(this)}";

    public string VisitAbsentStatement(AbsentStatement node) => $"{I}absent";

    public string VisitPassStatement(PassStatement node) => $"{I}pass";

    public string VisitBreakStatement(BreakStatement node) => $"{I}break";

    public string VisitContinueStatement(ContinueStatement node) => $"{I}continue";

    public string VisitDiscardStatement(DiscardStatement node) =>
        $"{I}discard {node.Expression.Accept(this)}";

    public string VisitDestructuringStatement(DestructuringStatement node) =>
        $"{I}#Destructuring";

    public string VisitVariantReturnStatement(VariantReturnStatement node) =>
        $"{I}#variant_return({node.VariantKind}, {node.SiteKind}" +
        $"{(node.Value != null ? ", " + node.Value.Accept(this) : "")})";

    public string VisitBlockStatement(BlockStatement node) =>
        PrintBody(node.Statements);

    public string VisitIfStatement(IfStatement node)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{I}if {node.Condition.Accept(this)}");
        sb.Append(PrintBodyOf(node.ThenStatement));

        Statement? elseStmt = node.ElseStatement;
        while (elseStmt is IfStatement elif)
        {
            sb.AppendLine();
            sb.AppendLine($"{I}else if {elif.Condition.Accept(this)}");
            sb.Append(PrintBodyOf(elif.ThenStatement));
            elseStmt = elif.ElseStatement;
        }

        if (elseStmt != null)
        {
            sb.AppendLine();
            sb.AppendLine($"{I}else");
            sb.Append(PrintBodyOf(elseStmt));
        }

        return sb.ToString().TrimEnd();
    }

    public string VisitWhileStatement(WhileStatement node)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{I}while {node.Condition.Accept(this)}");
        sb.Append(PrintBodyOf(node.Body));
        if (node.ElseBranch != null)
        {
            sb.AppendLine();
            sb.AppendLine($"{I}else");
            sb.Append(PrintBodyOf(node.ElseBranch));
        }

        return sb.ToString().TrimEnd();
    }

    public string VisitLoopStatement(LoopStatement node)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{I}loop");
        sb.Append(PrintBodyOf(node.Body));
        return sb.ToString().TrimEnd();
    }

    public string VisitForStatement(ForStatement node) => $"{I}#ForStatement";

    public string VisitWhenStatement(WhenStatement node)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{I}when {node.Expression.Accept(this)}");
        _indent++;
        foreach (WhenClause clause in node.Clauses)
        {
            string patStr = PrintPattern(clause.Pattern);
            sb.AppendLine($"{I}{patStr} =>");
            sb.Append(PrintBodyOf(clause.Body));
            sb.AppendLine();
        }

        _indent--;
        return sb.ToString().TrimEnd();
    }

    public string VisitDangerStatement(DangerStatement node)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{I}danger");
        sb.Append(PrintBody(node.Body.Statements));
        return sb.ToString().TrimEnd();
    }

    public string VisitUsingStatement(UsingStatement node)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{I}using {node.Resource.Accept(this)} as {node.Name}");
        sb.Append(PrintBodyOf(node.Body));
        return sb.ToString().TrimEnd();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // DECLARATIONS
    // ═════════════════════════════════════════════════════════════════════════

    public string VisitFunctionDeclaration(RoutineDeclaration node)
    {
        var sb = new StringBuilder();
        string failStr = node.IsFailable ? "!" : "";
        string returnStr = node.ReturnType != null ? $" -> {node.ReturnType.Accept(this)}" : " -> <ERROR>";
        string paramsStr = string.Join(", ", node.Parameters.Select(p =>
            p.Type != null ? $"{p.Name}: {p.Type.Accept(this)}" : p.Name));
        sb.AppendLine($"{I}routine {node.Name}{failStr}({paramsStr}){returnStr}");
        sb.Append(PrintBodyOf(node.Body));
        return sb.ToString().TrimEnd();
    }

    public string VisitModuleDeclaration(ModuleDeclaration node) => $"{I}module {node.Path}";

    public string VisitImportDeclaration(ImportDeclaration node) => $"{I}import {node.ModulePath}";

    public string VisitRecordDeclaration(RecordDeclaration node)
    {
        string generics = node.GenericParameters is { Count: > 0 }
            ? $"[{string.Join(", ", node.GenericParameters)}]"
            : "";
        string protos = node.Protocols.Count > 0
            ? $" obeys {string.Join(", ", node.Protocols.Select(p => p.Accept(this)))}"
            : "";
        return PrintTypeDecl($"record {node.Name}{generics}{protos}", node.Members);
    }

    public string VisitEntityDeclaration(EntityDeclaration node)
    {
        string generics = node.GenericParameters is { Count: > 0 }
            ? $"[{string.Join(", ", node.GenericParameters)}]"
            : "";
        string protos = node.Protocols.Count > 0
            ? $" obeys {string.Join(", ", node.Protocols.Select(p => p.Accept(this)))}"
            : "";
        return PrintTypeDecl($"entity {node.Name}{generics}{protos}", node.Members);
    }

    public string VisitChoiceDeclaration(ChoiceDeclaration node)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{I}choice {node.Name}");
        _indent++;
        foreach (ChoiceCase c in node.Cases)
        {
            string valStr = c.Value != null ? $" = {c.Value.Accept(this)}" : "";
            sb.AppendLine($"{I}{c.Name}{valStr}");
        }
        foreach (RoutineDeclaration m in node.Methods)
            sb.AppendLine(m.Accept(this));
        _indent--;
        return sb.ToString().TrimEnd();
    }

    public string VisitFlagsDeclaration(FlagsDeclaration node)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{I}flags {node.Name}");
        _indent++;
        foreach (string m in node.Members)
            sb.AppendLine($"{I}{m}");
        _indent--;
        return sb.ToString().TrimEnd();
    }

    public string VisitVariantDeclaration(VariantDeclaration node)
    {
        string generics = node.GenericParameters is { Count: > 0 }
            ? $"[{string.Join(", ", node.GenericParameters)}]"
            : "";
        var sb = new StringBuilder();
        sb.AppendLine($"{I}variant {node.Name}{generics}");
        _indent++;
        foreach (VariantMember m in node.Members)
            sb.AppendLine($"{I}{m.Type.Accept(this)}");
        _indent--;
        return sb.ToString().TrimEnd();
    }

    public string VisitProtocolDeclaration(ProtocolDeclaration node)
    {
        string generics = node.GenericParameters is { Count: > 0 }
            ? $"[{string.Join(", ", node.GenericParameters)}]"
            : "";
        string parents = node.ParentProtocols.Count > 0
            ? $" obeys {string.Join(", ", node.ParentProtocols.Select(p => p.Accept(this)))}"
            : "";
        var sb = new StringBuilder();
        sb.AppendLine($"{I}protocol {node.Name}{generics}{parents}");
        _indent++;
        foreach (RoutineSignature sig in node.Methods)
        {
            string failStr = sig.Name.EndsWith('!') ? "" : "";   // name already has '!'
            string returnStr = sig.ReturnType != null
                ? $" -> {sig.ReturnType.Accept(this)}"
                : " -> Blank";
            string paramsStr = string.Join(", ", sig.Parameters.Select(p =>
                p.Type != null ? $"{p.Name}: {p.Type.Accept(this)}" : p.Name));
            sb.AppendLine($"{I}routine {sig.Name}({paramsStr}){returnStr}");
        }
        _indent--;
        return sb.ToString().TrimEnd();
    }

    public string VisitCrashableDeclaration(CrashableDeclaration node) =>
        PrintTypeDecl($"crashable {node.Name}", node.Members);

    /// <summary>
    /// Prints a type declaration header followed by its members indented one level.
    /// Returns just the header line when the member list is empty.
    /// </summary>
    private string PrintTypeDecl(string header, List<Declaration> members)
    {
        if (members.Count == 0)
            return $"{I}{header}";
        var sb = new StringBuilder();
        sb.AppendLine($"{I}{header}");
        _indent++;
        foreach (Declaration m in members)
            sb.AppendLine(m.Accept(this));
        _indent--;
        return sb.ToString().TrimEnd();
    }

    public string VisitDefineDeclaration(DefineDeclaration node) =>
        $"{I}define {node.OldName} as {node.NewName}";

    public string VisitExternalDeclaration(ExternalDeclaration node) =>
        $"{I}external {node.Name}";

    public string VisitExternalBlockDeclaration(ExternalBlockDeclaration node) =>
        $"{I}external block";

    public string VisitPresetDeclaration(PresetDeclaration node) =>
        $"{I}preset {node.Name}: {node.Type.Accept(this)} = {node.Value.Accept(this)}";

    // ═════════════════════════════════════════════════════════════════════════
    // PROGRAM
    // ═════════════════════════════════════════════════════════════════════════

    public string VisitProgram(SyntaxTree.Program node) =>
        string.Join("\n\n", node.Declarations
            .Where(d => d is not PassDeclaration)
            .Select(d => d.Accept(this)));
}
