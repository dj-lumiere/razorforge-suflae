namespace Compiler.CodeGen;

using SyntaxTree;

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
    public static RoutineDeclaration Rewrite(RoutineDeclaration routine,
        IReadOnlyDictionary<string, string> subs)
    {
        var rewrittenParams = routine.Parameters
                                     .Select(selector: p => RewriteParameter(param: p, subs: subs))
                                     .ToList();
        TypeExpression? rewrittenReturnType = routine.ReturnType != null
            ? RewriteType(type: routine.ReturnType, subs: subs)
            : null;
        Statement rewrittenBody = RewriteStatement(stmt: routine.Body, subs: subs);

        return routine with
        {
            Parameters = rewrittenParams,
            ReturnType = rewrittenReturnType,
            Body = rewrittenBody,
            GenericParameters = null // No longer generic after substitution
        };
    }

    private static Parameter RewriteParameter(Parameter param,
        IReadOnlyDictionary<string, string> subs)
    {
        TypeExpression? rewrittenType = param.Type != null
            ? RewriteType(type: param.Type, subs: subs)
            : null;
        Expression? rewrittenDefault = param.DefaultValue != null
            ? RewriteExpression(expr: param.DefaultValue, subs: subs)
            : null;
        return param with { Type = rewrittenType, DefaultValue = rewrittenDefault };
    }

    #region Type Rewriting

    private static TypeExpression RewriteType(TypeExpression type,
        IReadOnlyDictionary<string, string> subs)
    {
        string name = subs.TryGetValue(key: type.Name, value: out string? sub)
            ? sub
            : type.Name;
        var args = type.GenericArguments
                      ?.Select(selector: a => RewriteType(type: a, subs: subs))
                       .ToList();
        if (name == type.Name && args == null && type.GenericArguments == null)
        {
            return type; // No change
        }

        return type with { Name = name, GenericArguments = args };
    }

    #endregion

    #region Expression Rewriting

    private static Expression RewriteExpression(Expression expr,
        IReadOnlyDictionary<string, string> subs)
    {
        return expr switch
        {
            TypeExpression te => RewriteType(type: te, subs: subs),

            GenericMethodCallExpression gmc => gmc with
            {
                Object = RewriteExpression(expr: gmc.Object, subs: subs),
                TypeArguments = gmc.TypeArguments
                                   .Select(selector: a => RewriteType(type: a, subs: subs))
                                   .ToList(),
                Arguments = gmc.Arguments
                               .Select(selector: a => RewriteExpression(expr: a, subs: subs))
                               .ToList()
            },

            CreatorExpression creator => creator with
            {
                TypeName = subs.TryGetValue(key: creator.TypeName, value: out string? cSub)
                    ? cSub
                    : creator.TypeName,
                TypeArguments = creator.TypeArguments
                                      ?.Select(selector: a => RewriteType(type: a, subs: subs))
                                       .ToList(),
                MemberVariables = creator.MemberVariables
                                         .Select(selector: mv => (mv.Name,
                                              Value: RewriteExpression(expr: mv.Value,
                                                  subs: subs)))
                                         .ToList()
            },

            TypeConversionExpression tce => tce with
            {
                TargetType = subs.TryGetValue(key: tce.TargetType, value: out string? tSub)
                    ? tSub
                    : tce.TargetType,
                Expression = RewriteExpression(expr: tce.Expression, subs: subs)
            },

            ListLiteralExpression lle => lle with
            {
                Elements = lle.Elements
                              .Select(selector: e => RewriteExpression(expr: e, subs: subs))
                              .ToList(),
                ElementType = lle.ElementType != null
                    ? RewriteType(type: lle.ElementType, subs: subs)
                    : null
            },

            SetLiteralExpression sle => sle with
            {
                Elements = sle.Elements
                              .Select(selector: e => RewriteExpression(expr: e, subs: subs))
                              .ToList(),
                ElementType = sle.ElementType != null
                    ? RewriteType(type: sle.ElementType, subs: subs)
                    : null
            },

            DictLiteralExpression dle => dle with
            {
                Pairs = dle.Pairs
                           .Select(selector: p => (Key: RewriteExpression(expr: p.Key, subs: subs),
                                Value: RewriteExpression(expr: p.Value, subs: subs)))
                           .ToList(),
                KeyType = dle.KeyType != null
                    ? RewriteType(type: dle.KeyType, subs: subs)
                    : null,
                ValueType = dle.ValueType != null
                    ? RewriteType(type: dle.ValueType, subs: subs)
                    : null
            },

            IsPatternExpression ipe => ipe with
            {
                Expression = RewriteExpression(expr: ipe.Expression, subs: subs),
                Pattern = RewritePattern(pattern: ipe.Pattern, subs: subs)
            },

            CallExpression call => call with
            {
                Callee = RewriteExpression(expr: call.Callee, subs: subs),
                Arguments = call.Arguments
                                .Select(selector: a => RewriteExpression(expr: a, subs: subs))
                                .ToList()
            },

            MemberExpression me => me with
            {
                Object = RewriteExpression(expr: me.Object, subs: subs)
            },

            OptionalMemberExpression ome => ome with
            {
                Object = RewriteExpression(expr: ome.Object, subs: subs)
            },

            GenericMemberExpression gme => gme with
            {
                Object = RewriteExpression(expr: gme.Object, subs: subs),
                TypeArguments = gme.TypeArguments
                                   .Select(selector: a => RewriteType(type: a, subs: subs))
                                   .ToList()
            },

            IndexExpression idx => idx with
            {
                Object = RewriteExpression(expr: idx.Object, subs: subs),
                Index = RewriteExpression(expr: idx.Index, subs: subs)
            },

            SliceExpression slice => slice with
            {
                Object = RewriteExpression(expr: slice.Object, subs: subs),
                Start = RewriteExpression(expr: slice.Start, subs: subs),
                End = RewriteExpression(expr: slice.End, subs: subs)
            },

            BinaryExpression bin => bin with
            {
                Left = RewriteExpression(expr: bin.Left, subs: subs),
                Right = RewriteExpression(expr: bin.Right, subs: subs)
            },

            UnaryExpression un => un with
            {
                Operand = RewriteExpression(expr: un.Operand, subs: subs)
            },

            CompoundAssignmentExpression ca => ca with
            {
                Target = RewriteExpression(expr: ca.Target, subs: subs),
                Value = RewriteExpression(expr: ca.Value, subs: subs)
            },

            ConditionalExpression cond => cond with
            {
                Condition = RewriteExpression(expr: cond.Condition, subs: subs),
                TrueExpression = RewriteExpression(expr: cond.TrueExpression, subs: subs),
                FalseExpression = RewriteExpression(expr: cond.FalseExpression, subs: subs)
            },

            BlockExpression block => block with
            {
                Value = RewriteExpression(expr: block.Value, subs: subs)
            },

            LambdaExpression lambda => lambda with
            {
                Parameters = lambda.Parameters
                                   .Select(selector: p => RewriteParameter(param: p, subs: subs))
                                   .ToList(),
                Body = RewriteExpression(expr: lambda.Body, subs: subs)
            },

            TupleLiteralExpression tuple => tuple with
            {
                Elements = tuple.Elements
                                .Select(selector: e => RewriteExpression(expr: e, subs: subs))
                                .ToList()
            },

            RangeExpression range => range with
            {
                Start = RewriteExpression(expr: range.Start, subs: subs),
                End = RewriteExpression(expr: range.End, subs: subs),
                Step = range.Step != null
                    ? RewriteExpression(expr: range.Step, subs: subs)
                    : null
            },

            ChainedComparisonExpression chain => chain with
            {
                Operands = chain.Operands
                                .Select(selector: o => RewriteExpression(expr: o, subs: subs))
                                .ToList()
            },

            WithExpression we => we with
            {
                Base = RewriteExpression(expr: we.Base, subs: subs),
                Updates = we.Updates
                            .Select(selector: u => (u.MemberVariablePath, u.Index != null
                                     ? RewriteExpression(expr: u.Index, subs: subs)
                                     : (Expression?)null,
                                 RewriteExpression(expr: u.Value, subs: subs)))
                            .ToList()
            },

            InsertedTextExpression ite => ite with
            {
                Parts = ite.Parts
                           .Select(selector: p => RewriteInsertedTextPart(part: p, subs: subs))
                           .ToList()
            },

            NamedArgumentExpression nae => nae with
            {
                Value = RewriteExpression(expr: nae.Value, subs: subs)
            },

            DictEntryLiteralExpression del => del with
            {
                Key = RewriteExpression(expr: del.Key, subs: subs),
                Value = RewriteExpression(expr: del.Value, subs: subs)
            },

            StealExpression steal => steal with
            {
                Operand = RewriteExpression(expr: steal.Operand, subs: subs)
            },

            WaitforExpression wf => wf with
            {
                Operand = RewriteExpression(expr: wf.Operand, subs: subs),
                Timeout = wf.Timeout != null
                    ? RewriteExpression(expr: wf.Timeout, subs: subs)
                    : null
            },

            DependentWaitforExpression dwf => dwf with
            {
                Dependencies = dwf.Dependencies
                                  .Select(selector: d => d with
                                   {
                                       DependencyExpr =
                                       RewriteExpression(expr: d.DependencyExpr, subs: subs)
                                   })
                                  .ToList(),
                Operand = RewriteExpression(expr: dwf.Operand, subs: subs),
                Timeout = dwf.Timeout != null
                    ? RewriteExpression(expr: dwf.Timeout, subs: subs)
                    : null
            },

            BackIndexExpression bi => bi with
            {
                Operand = RewriteExpression(expr: bi.Operand, subs: subs)
            },

            WhenExpression we => we with
            {
                Expression = we.Expression != null
                    ? RewriteExpression(expr: we.Expression, subs: subs)
                    : null,
                Clauses = we.Clauses
                            .Select(selector: c => RewriteWhenClause(clause: c, subs: subs))
                            .ToList()
            },

            FlagsTestExpression fte => fte with
            {
                Subject = RewriteExpression(expr: fte.Subject, subs: subs)
            },

            // Leaf nodes — no children to rewrite
            LiteralExpression => expr,

            _ => expr // Unknown expression type — return as-is
        };
    }

    private static InsertedTextPart RewriteInsertedTextPart(InsertedTextPart part,
        IReadOnlyDictionary<string, string> subs)
    {
        return part switch
        {
            ExpressionPart ep => ep with
            {
                Expression = RewriteExpression(expr: ep.Expression, subs: subs)
            },
            _ => part // TextPart has no expressions
        };
    }

    #endregion

    #region Statement Rewriting

    private static Statement RewriteStatement(Statement stmt,
        IReadOnlyDictionary<string, string> subs)
    {
        return stmt switch
        {
            BlockStatement block => block with
            {
                Statements = block.Statements
                                  .Select(selector: s => RewriteStatement(stmt: s, subs: subs))
                                  .ToList()
            },

            ExpressionStatement es => es with
            {
                Expression = RewriteExpression(expr: es.Expression, subs: subs)
            },

            DeclarationStatement ds => ds with
            {
                Declaration = RewriteDeclaration(decl: ds.Declaration, subs: subs)
            },

            AssignmentStatement assign => assign with
            {
                Target = RewriteExpression(expr: assign.Target, subs: subs),
                Value = RewriteExpression(expr: assign.Value, subs: subs)
            },

            ReturnStatement ret => ret with
            {
                Value = ret.Value != null
                    ? RewriteExpression(expr: ret.Value, subs: subs)
                    : null
            },

            BecomesStatement becomes => becomes with
            {
                Value = RewriteExpression(expr: becomes.Value, subs: subs)
            },

            IfStatement ifs => ifs with
            {
                Condition = RewriteExpression(expr: ifs.Condition, subs: subs),
                ThenStatement = RewriteStatement(stmt: ifs.ThenStatement, subs: subs),
                ElseStatement = ifs.ElseStatement != null
                    ? RewriteStatement(stmt: ifs.ElseStatement, subs: subs)
                    : null
            },

            WhileStatement ws => ws with
            {
                Condition = RewriteExpression(expr: ws.Condition, subs: subs),
                Body = RewriteStatement(stmt: ws.Body, subs: subs),
                ElseBranch = ws.ElseBranch != null
                    ? RewriteStatement(stmt: ws.ElseBranch, subs: subs)
                    : null
            },

            ForStatement fs => fs with
            {
                Iterable = RewriteExpression(expr: fs.Iterable, subs: subs),
                Body = RewriteStatement(stmt: fs.Body, subs: subs),
                ElseBranch = fs.ElseBranch != null
                    ? RewriteStatement(stmt: fs.ElseBranch, subs: subs)
                    : null
            },

            WhenStatement ws => ws with
            {
                Expression = RewriteExpression(expr: ws.Expression, subs: subs),
                Clauses = ws.Clauses
                            .Select(selector: c => RewriteWhenClause(clause: c, subs: subs))
                            .ToList()
            },

            ThrowStatement ts => ts with { Error = RewriteExpression(expr: ts.Error, subs: subs) },

            DiscardStatement disc => disc with
            {
                Expression = RewriteExpression(expr: disc.Expression, subs: subs)
            },

            EmitStatement emit => emit with
            {
                Expression = RewriteExpression(expr: emit.Expression, subs: subs)
            },

            DangerStatement danger => danger with
            {
                Body = (BlockStatement)RewriteStatement(stmt: danger.Body, subs: subs)
            },

            UsingStatement us => us with
            {
                Resource = RewriteExpression(expr: us.Resource, subs: subs),
                Body = RewriteStatement(stmt: us.Body, subs: subs)
            },

            DestructuringStatement destruct => destruct with
            {
                Initializer = RewriteExpression(expr: destruct.Initializer, subs: subs)
            },

            // Leaf statements
            BreakStatement or ContinueStatement or PassStatement or AbsentStatement => stmt,

            _ => stmt
        };
    }

    private static WhenClause RewriteWhenClause(WhenClause clause,
        IReadOnlyDictionary<string, string> subs)
    {
        return clause with
        {
            Pattern = RewritePattern(pattern: clause.Pattern, subs: subs),
            Body = RewriteStatement(stmt: clause.Body, subs: subs)
        };
    }

    #endregion

    #region Pattern Rewriting

    private static Pattern RewritePattern(Pattern pattern,
        IReadOnlyDictionary<string, string> subs)
    {
        return pattern switch
        {
            TypePattern tp => tp with
            {
                Type = RewriteType(type: tp.Type, subs: subs),
                Bindings = tp.Bindings
                            ?.Select(selector: b => RewriteBinding(binding: b, subs: subs))
                             .ToList()
            },

            NegatedTypePattern ntp => ntp with { Type = RewriteType(type: ntp.Type, subs: subs) },

            TypeDestructuringPattern tdp => tdp with
            {
                Type = RewriteType(type: tdp.Type, subs: subs),
                Bindings = tdp.Bindings
                              .Select(selector: b => RewriteBinding(binding: b, subs: subs))
                              .ToList()
            },

            GuardPattern gp => gp with
            {
                InnerPattern = RewritePattern(pattern: gp.InnerPattern, subs: subs),
                Guard = RewriteExpression(expr: gp.Guard, subs: subs)
            },

            ExpressionPattern ep => ep with
            {
                Expression = RewriteExpression(expr: ep.Expression, subs: subs)
            },

            ComparisonPattern cp => cp with
            {
                Value = RewriteExpression(expr: cp.Value, subs: subs)
            },

            VariantPattern vp => vp with
            {
                Bindings = vp.Bindings
                            ?.Select(selector: b => RewriteBinding(binding: b, subs: subs))
                             .ToList()
            },

            CrashablePattern crash => crash with
            {
                ErrorType = crash.ErrorType != null
                    ? RewriteType(type: crash.ErrorType, subs: subs)
                    : null
            },

            DestructuringPattern dp => dp with
            {
                Bindings = dp.Bindings
                             .Select(selector: b => RewriteBinding(binding: b, subs: subs))
                             .ToList()
            },

            // Leaf patterns
            LiteralPattern or IdentifierPattern or WildcardPattern or NonePattern or ElsePattern
                or FlagsPattern => pattern,

            _ => pattern
        };
    }

    private static DestructuringBinding RewriteBinding(DestructuringBinding binding,
        IReadOnlyDictionary<string, string> subs)
    {
        return binding with
        {
            NestedPattern = binding.NestedPattern != null
                ? RewritePattern(pattern: binding.NestedPattern, subs: subs)
                : null
        };
    }

    #endregion

    #region Declaration Rewriting (for DeclarationStatements)

    private static Declaration RewriteDeclaration(Declaration decl,
        IReadOnlyDictionary<string, string> subs)
    {
        return decl switch
        {
            VariableDeclaration vd => vd with
            {
                Type = vd.Type != null
                    ? RewriteType(type: vd.Type, subs: subs)
                    : null,
                Initializer = vd.Initializer != null
                    ? RewriteExpression(expr: vd.Initializer, subs: subs)
                    : null
            },

            _ => decl // Other declarations in statement context are rare
        };
    }

    #endregion
}
