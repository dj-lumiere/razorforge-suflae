namespace Compiler.CodeGen;

using SyntaxTree;
using Lexer;

/// <summary>
/// Deep-clones a RoutineDeclaration AST, replacing all occurrences of generic type
/// parameter names with their concrete substitutions. This allows codegen to work
/// with a fully-resolved AST where no generic parameters remain.
/// </summary>
internal static class GenericAstRewriter
{
    /// <summary>
    /// Rewrites a generic routine declaration by substituting all type parameter references
    /// with concrete type names. Returns a deep clone — the original is not modified.
    /// </summary>
    public static RoutineDeclaration Rewrite(RoutineDeclaration routine, IReadOnlyDictionary<string, string> subs)
    {
        var rewrittenParams = routine.Parameters.Select(p => RewriteParameter(p, subs)).ToList();
        var rewrittenReturnType = routine.ReturnType != null ? RewriteType(routine.ReturnType, subs) : null;
        var rewrittenBody = RewriteStatement(routine.Body, subs);

        return routine with
        {
            Parameters = rewrittenParams,
            ReturnType = rewrittenReturnType,
            Body = rewrittenBody,
            GenericParameters = null // No longer generic after substitution
        };
    }

    private static Parameter RewriteParameter(Parameter param, IReadOnlyDictionary<string, string> subs)
    {
        var rewrittenType = param.Type != null ? RewriteType(param.Type, subs) : null;
        var rewrittenDefault = param.DefaultValue != null ? RewriteExpression(param.DefaultValue, subs) : null;
        return param with { Type = rewrittenType, DefaultValue = rewrittenDefault };
    }

    #region Type Rewriting

    private static TypeExpression RewriteType(TypeExpression type, IReadOnlyDictionary<string, string> subs)
    {
        string name = subs.TryGetValue(type.Name, out var sub) ? sub : type.Name;
        var args = type.GenericArguments?.Select(a => RewriteType(a, subs)).ToList();
        if (name == type.Name && args == null && type.GenericArguments == null)
            return type; // No change
        return type with { Name = name, GenericArguments = args };
    }

    #endregion

    #region Expression Rewriting

    private static Expression RewriteExpression(Expression expr, IReadOnlyDictionary<string, string> subs)
    {
        return expr switch
        {
            TypeExpression te => RewriteType(te, subs),

            GenericMethodCallExpression gmc => gmc with
            {
                Object = RewriteExpression(gmc.Object, subs),
                TypeArguments = gmc.TypeArguments.Select(a => RewriteType(a, subs)).ToList(),
                Arguments = gmc.Arguments.Select(a => RewriteExpression(a, subs)).ToList()
            },

            CreatorExpression creator => creator with
            {
                TypeName = subs.TryGetValue(creator.TypeName, out var cSub) ? cSub : creator.TypeName,
                TypeArguments = creator.TypeArguments?.Select(a => RewriteType(a, subs)).ToList(),
                MemberVariables = creator.MemberVariables
                    .Select(mv => (mv.Name, Value: RewriteExpression(mv.Value, subs))).ToList()
            },

            TypeConversionExpression tce => tce with
            {
                TargetType = subs.TryGetValue(tce.TargetType, out var tSub) ? tSub : tce.TargetType,
                Expression = RewriteExpression(tce.Expression, subs)
            },

            ListLiteralExpression lle => lle with
            {
                Elements = lle.Elements.Select(e => RewriteExpression(e, subs)).ToList(),
                ElementType = lle.ElementType != null ? RewriteType(lle.ElementType, subs) : null
            },

            SetLiteralExpression sle => sle with
            {
                Elements = sle.Elements.Select(e => RewriteExpression(e, subs)).ToList(),
                ElementType = sle.ElementType != null ? RewriteType(sle.ElementType, subs) : null
            },

            DictLiteralExpression dle => dle with
            {
                Pairs = dle.Pairs.Select(p => (Key: RewriteExpression(p.Key, subs), Value: RewriteExpression(p.Value, subs))).ToList(),
                KeyType = dle.KeyType != null ? RewriteType(dle.KeyType, subs) : null,
                ValueType = dle.ValueType != null ? RewriteType(dle.ValueType, subs) : null
            },

            IsPatternExpression ipe => ipe with
            {
                Expression = RewriteExpression(ipe.Expression, subs),
                Pattern = RewritePattern(ipe.Pattern, subs)
            },

            CallExpression call => call with
            {
                Callee = RewriteExpression(call.Callee, subs),
                Arguments = call.Arguments.Select(a => RewriteExpression(a, subs)).ToList()
            },

            MemberExpression me => me with
            {
                Object = RewriteExpression(me.Object, subs)
            },

            OptionalMemberExpression ome => ome with
            {
                Object = RewriteExpression(ome.Object, subs)
            },

            GenericMemberExpression gme => gme with
            {
                Object = RewriteExpression(gme.Object, subs),
                TypeArguments = gme.TypeArguments.Select(a => RewriteType(a, subs)).ToList()
            },

            IndexExpression idx => idx with
            {
                Object = RewriteExpression(idx.Object, subs),
                Index = RewriteExpression(idx.Index, subs)
            },

            SliceExpression slice => slice with
            {
                Object = RewriteExpression(slice.Object, subs),
                Start = RewriteExpression(slice.Start, subs),
                End = RewriteExpression(slice.End, subs)
            },

            BinaryExpression bin => bin with
            {
                Left = RewriteExpression(bin.Left, subs),
                Right = RewriteExpression(bin.Right, subs)
            },

            UnaryExpression un => un with
            {
                Operand = RewriteExpression(un.Operand, subs)
            },

            CompoundAssignmentExpression ca => ca with
            {
                Target = RewriteExpression(ca.Target, subs),
                Value = RewriteExpression(ca.Value, subs)
            },

            ConditionalExpression cond => cond with
            {
                Condition = RewriteExpression(cond.Condition, subs),
                TrueExpression = RewriteExpression(cond.TrueExpression, subs),
                FalseExpression = RewriteExpression(cond.FalseExpression, subs)
            },

            BlockExpression block => block with
            {
                Value = RewriteExpression(block.Value, subs)
            },

            LambdaExpression lambda => lambda with
            {
                Parameters = lambda.Parameters.Select(p => RewriteParameter(p, subs)).ToList(),
                Body = RewriteExpression(lambda.Body, subs)
            },

            TupleLiteralExpression tuple => tuple with
            {
                Elements = tuple.Elements.Select(e => RewriteExpression(e, subs)).ToList()
            },

            RangeExpression range => range with
            {
                Start = RewriteExpression(range.Start, subs),
                End = RewriteExpression(range.End, subs),
                Step = range.Step != null ? RewriteExpression(range.Step, subs) : null
            },

            ChainedComparisonExpression chain => chain with
            {
                Operands = chain.Operands.Select(o => RewriteExpression(o, subs)).ToList()
            },

            WithExpression we => we with
            {
                Base = RewriteExpression(we.Base, subs),
                Updates = we.Updates
                    .Select(u => (u.MemberVariablePath, u.Index != null ? RewriteExpression(u.Index, subs) : (Expression?)null, RewriteExpression(u.Value, subs)))
                    .ToList()
            },

            InsertedTextExpression ite => ite with
            {
                Parts = ite.Parts.Select(p => RewriteInsertedTextPart(p, subs)).ToList()
            },

            NamedArgumentExpression nae => nae with
            {
                Value = RewriteExpression(nae.Value, subs)
            },

            StealExpression steal => steal with
            {
                Operand = RewriteExpression(steal.Operand, subs)
            },

            WaitforExpression wf => wf with
            {
                Operand = RewriteExpression(wf.Operand, subs),
                Timeout = wf.Timeout != null ? RewriteExpression(wf.Timeout, subs) : null
            },

            DependentWaitforExpression dwf => dwf with
            {
                Dependencies = dwf.Dependencies.Select(d => d with
                {
                    DependencyExpr = RewriteExpression(d.DependencyExpr, subs)
                }).ToList(),
                Operand = RewriteExpression(dwf.Operand, subs),
                Timeout = dwf.Timeout != null ? RewriteExpression(dwf.Timeout, subs) : null
            },

            BackIndexExpression bi => bi with
            {
                Operand = RewriteExpression(bi.Operand, subs)
            },

            WhenExpression we => we with
            {
                Expression = we.Expression != null ? RewriteExpression(we.Expression, subs) : null,
                Clauses = we.Clauses.Select(c => RewriteWhenClause(c, subs)).ToList()
            },

            FlagsTestExpression fte => fte with
            {
                Subject = RewriteExpression(fte.Subject, subs)
            },

            // Leaf nodes — no children to rewrite
            LiteralExpression or IdentifierExpression => expr,

            _ => expr // Unknown expression type — return as-is
        };
    }

    private static InsertedTextPart RewriteInsertedTextPart(InsertedTextPart part, IReadOnlyDictionary<string, string> subs)
    {
        return part switch
        {
            ExpressionPart ep => ep with { Expression = RewriteExpression(ep.Expression, subs) },
            _ => part // TextPart has no expressions
        };
    }

    #endregion

    #region Statement Rewriting

    private static Statement RewriteStatement(Statement stmt, IReadOnlyDictionary<string, string> subs)
    {
        return stmt switch
        {
            BlockStatement block => block with
            {
                Statements = block.Statements.Select(s => RewriteStatement(s, subs)).ToList()
            },

            ExpressionStatement es => es with
            {
                Expression = RewriteExpression(es.Expression, subs)
            },

            DeclarationStatement ds => ds with
            {
                Declaration = RewriteDeclaration(ds.Declaration, subs)
            },

            AssignmentStatement assign => assign with
            {
                Target = RewriteExpression(assign.Target, subs),
                Value = RewriteExpression(assign.Value, subs)
            },

            ReturnStatement ret => ret with
            {
                Value = ret.Value != null ? RewriteExpression(ret.Value, subs) : null
            },

            BecomesStatement becomes => becomes with
            {
                Value = RewriteExpression(becomes.Value, subs)
            },

            IfStatement ifs => ifs with
            {
                Condition = RewriteExpression(ifs.Condition, subs),
                ThenStatement = RewriteStatement(ifs.ThenStatement, subs),
                ElseStatement = ifs.ElseStatement != null ? RewriteStatement(ifs.ElseStatement, subs) : null
            },

            WhileStatement ws => ws with
            {
                Condition = RewriteExpression(ws.Condition, subs),
                Body = RewriteStatement(ws.Body, subs),
                ElseBranch = ws.ElseBranch != null ? RewriteStatement(ws.ElseBranch, subs) : null
            },

            ForStatement fs => fs with
            {
                Sequenceable = RewriteExpression(fs.Sequenceable, subs),
                Body = RewriteStatement(fs.Body, subs),
                ElseBranch = fs.ElseBranch != null ? RewriteStatement(fs.ElseBranch, subs) : null
            },

            WhenStatement ws => ws with
            {
                Expression = RewriteExpression(ws.Expression, subs),
                Clauses = ws.Clauses.Select(c => RewriteWhenClause(c, subs)).ToList()
            },

            ThrowStatement ts => ts with
            {
                Error = RewriteExpression(ts.Error, subs)
            },

            DiscardStatement disc => disc with
            {
                Expression = RewriteExpression(disc.Expression, subs)
            },

            EmitStatement emit => emit with
            {
                Expression = RewriteExpression(emit.Expression, subs)
            },

            DangerStatement danger => danger with
            {
                Body = (BlockStatement)RewriteStatement(danger.Body, subs)
            },

            UsingStatement us => us with
            {
                Resource = RewriteExpression(us.Resource, subs),
                Body = RewriteStatement(us.Body, subs)
            },

            DestructuringStatement destruct => destruct with
            {
                Initializer = RewriteExpression(destruct.Initializer, subs)
            },

            // Leaf statements
            BreakStatement or ContinueStatement or PassStatement or AbsentStatement => stmt,

            _ => stmt
        };
    }

    private static WhenClause RewriteWhenClause(WhenClause clause, IReadOnlyDictionary<string, string> subs)
    {
        return clause with
        {
            Pattern = RewritePattern(clause.Pattern, subs),
            Body = RewriteStatement(clause.Body, subs)
        };
    }

    #endregion

    #region Pattern Rewriting

    private static Pattern RewritePattern(Pattern pattern, IReadOnlyDictionary<string, string> subs)
    {
        return pattern switch
        {
            TypePattern tp => tp with
            {
                Type = RewriteType(tp.Type, subs),
                Bindings = tp.Bindings?.Select(b => RewriteBinding(b, subs)).ToList()
            },

            NegatedTypePattern ntp => ntp with
            {
                Type = RewriteType(ntp.Type, subs)
            },

            TypeDestructuringPattern tdp => tdp with
            {
                Type = RewriteType(tdp.Type, subs),
                Bindings = tdp.Bindings.Select(b => RewriteBinding(b, subs)).ToList()
            },

            GuardPattern gp => gp with
            {
                InnerPattern = RewritePattern(gp.InnerPattern, subs),
                Guard = RewriteExpression(gp.Guard, subs)
            },

            ExpressionPattern ep => ep with
            {
                Expression = RewriteExpression(ep.Expression, subs)
            },

            ComparisonPattern cp => cp with
            {
                Value = RewriteExpression(cp.Value, subs)
            },

            VariantPattern vp => vp with
            {
                Bindings = vp.Bindings?.Select(b => RewriteBinding(b, subs)).ToList()
            },

            CrashablePattern crash => crash with
            {
                ErrorType = crash.ErrorType != null ? RewriteType(crash.ErrorType, subs) : null
            },

            DestructuringPattern dp => dp with
            {
                Bindings = dp.Bindings.Select(b => RewriteBinding(b, subs)).ToList()
            },

            // Leaf patterns
            LiteralPattern or IdentifierPattern or WildcardPattern
                or NonePattern or ElsePattern or FlagsPattern => pattern,

            _ => pattern
        };
    }

    private static DestructuringBinding RewriteBinding(DestructuringBinding binding, IReadOnlyDictionary<string, string> subs)
    {
        return binding with
        {
            NestedPattern = binding.NestedPattern != null ? RewritePattern(binding.NestedPattern, subs) : null
        };
    }

    #endregion

    #region Declaration Rewriting (for DeclarationStatements)

    private static Declaration RewriteDeclaration(Declaration decl, IReadOnlyDictionary<string, string> subs)
    {
        return decl switch
        {
            VariableDeclaration vd => vd with
            {
                Type = vd.Type != null ? RewriteType(vd.Type, subs) : null,
                Initializer = vd.Initializer != null ? RewriteExpression(vd.Initializer, subs) : null
            },

            _ => decl // Other declarations in statement context are rare
        };
    }

    #endregion
}
