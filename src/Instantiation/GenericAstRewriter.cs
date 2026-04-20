namespace Compiler.Instantiation;

using Compiler.Postprocessing;
using Compiler.Postprocessing.Passes;
using Lexer;
using Resolution;
using TypeModel.Types;
using SyntaxTree;

/// <summary>
/// Deep-clones a RoutineDeclaration AST, replacing all occurrences of generic type
/// parameter names with their concrete substitutions. This allows codegen to work
/// with a fully-resolved AST where no generic parameters remain.
/// </summary>
// TODO: This should be handled on the synthesis level
internal static class GenericAstRewriter
{
    /// <summary>
    /// Rewrites a generic routine declaration by substituting all type parameter references
    /// with concrete type names. Returns a deep clone ??the original is not modified.
    /// <para>
    /// When <paramref name="typeSubs"/> and <paramref name="registry"/> are provided, the
    /// rewriter also sets <see cref="Expression.ResolvedType"/> on every cloned expression
    /// whose original <c>ResolvedType</c> was a <see cref="GenericParameterTypeInfo"/> or a
    /// generic resolution containing generic parameters. This removes the need for
    /// <c>_typeSubstitutions</c> fallback lookups during codegen emission.
    /// </para>
    /// </summary>
    public static RoutineDeclaration Rewrite(RoutineDeclaration routine,
        IReadOnlyDictionary<string, string> subs,
        IReadOnlyDictionary<string, TypeInfo>? typeSubs = null,
        TypeRegistry? registry = null)
    {
        var ctx = typeSubs != null && registry != null
            ? new RewriteContext(subs, typeSubs, registry)
            : new RewriteContext(subs, null, null);

        var rewrittenParams = routine.Parameters
                                     .Select(selector: p => RewriteParameter(param: p, ctx: ctx))
                                     .ToList();
        TypeExpression? rewrittenReturnType = routine.ReturnType != null
            ? RewriteType(type: routine.ReturnType, ctx: ctx)
            : null;
        Statement rewrittenBody = RewriteStatement(stmt: routine.Body, ctx: ctx);

        return routine with
        {
            Parameters = rewrittenParams,
            ReturnType = rewrittenReturnType,
            Body = rewrittenBody,
            GenericParameters = null // No longer generic after substitution
        };
    }

    private static Parameter RewriteParameter(Parameter param, RewriteContext ctx)
    {
        TypeExpression? rewrittenType = param.Type != null
            ? RewriteType(type: param.Type, ctx: ctx)
            : null;
        Expression? rewrittenDefault = param.DefaultValue != null
            ? RewriteExpression(expr: param.DefaultValue, ctx: ctx)
            : null;
        return param with { Type = rewrittenType, DefaultValue = rewrittenDefault };
    }

    // ?�?�?� RewriteContext ?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�

    /// <summary>
    /// Carries the string-name substitution map (always present) and the optional
    /// TypeInfo substitution map + registry (used for ResolvedType annotation).
    /// </summary>
    private sealed class RewriteContext(
        IReadOnlyDictionary<string, string> stringSubs,
        IReadOnlyDictionary<string, TypeInfo>? typeSubs,
        TypeRegistry? registry)
    {
        public IReadOnlyDictionary<string, string> StringSubs { get; } = stringSubs;
        public IReadOnlyDictionary<string, TypeInfo>? TypeSubs { get; } = typeSubs;
        public TypeRegistry? Registry { get; } = registry;

        /// <summary>
        /// Resolves a <see cref="TypeInfo"/> through the substitution map. Returns null
        /// when the registry is not available or the type has no substitution.
        /// </summary>
        public TypeInfo? ResolveType(TypeInfo? original)
        {
            if (original == null || TypeSubs == null || Registry == null)
                return null;

            // Direct generic parameter substitution: T ??S64
            if (original is GenericParameterTypeInfo gp &&
                TypeSubs.TryGetValue(key: gp.Name, value: out TypeInfo? direct))
                return direct;

            // Generic resolution with substitutable type arguments: List[T] ??List[S64]
            if (original is { IsGenericResolution: true, TypeArguments: not null })
            {
                bool anyChanged = false;
                var newArgs = new List<TypeInfo>(capacity: original.TypeArguments.Count);
                foreach (TypeInfo arg in original.TypeArguments)
                {
                    TypeInfo? resolved = ResolveType(original: arg);
                    if (resolved != null && !ReferenceEquals(objA: resolved, objB: arg))
                    {
                        newArgs.Add(item: resolved);
                        anyChanged = true;
                    }
                    else
                    {
                        newArgs.Add(item: arg);
                    }
                }
                if (anyChanged)
                {
                    TypeInfo? genericBase = original switch
                    {
                        RecordTypeInfo { GenericDefinition: { } d } => d,
                        EntityTypeInfo { GenericDefinition: { } d } => d,
                        ProtocolTypeInfo { GenericDefinition: { } d } => d,
                        VariantTypeInfo { GenericDefinition: { } d } => d,
                        _ => null
                    };
                    if (genericBase != null)
                        return Registry.GetOrCreateResolution(genericDef: genericBase,
                            typeArguments: newArgs);
                }
            }

            // WrapperTypeInfo (Hijacked[T] ??Hijacked[S64], or Hijacked[S64] ??stays): always
            // resolve to the real RecordTypeInfo so LLVM mangled names use "Core.Hijacked[S64]"
            // (from RecordTypeInfo.FullName) rather than "Hijacked[Core.S64]" (WrapperTypeInfo
            // with Module=null). The WrapperTypeInfo.FullName appends inner.FullName which includes
            // the module prefix, producing the wrong "Hijacked[Core.S64]" format.
            if (original is WrapperTypeInfo wrapper)
            {
                var newWrapperArgs = new List<TypeInfo>(capacity: wrapper.TypeArguments?.Count ?? 1);
                foreach (TypeInfo arg in wrapper.TypeArguments ?? [])
                {
                    TypeInfo? resolved = ResolveType(original: arg);
                    newWrapperArgs.Add(item: resolved != null && !ReferenceEquals(objA: resolved, objB: arg)
                        ? resolved
                        : arg);
                }

                if (Registry != null)
                {
                    TypeInfo? wrapperDef = Registry.LookupType(name: wrapper.Name);
                    if (wrapperDef is { IsGenericDefinition: true })
                    {
                        return Registry.GetOrCreateResolution(genericDef: wrapperDef,
                            typeArguments: newWrapperArgs);
                    }
                }
            }

            return null;
        }
    }

    #region Type Rewriting

    private static TypeExpression RewriteType(TypeExpression type,
        RewriteContext ctx)
    {
        string name = ctx.StringSubs.TryGetValue(key: type.Name, value: out string? sub)
            ? sub
            : type.Name;
        var args = type.GenericArguments
                      ?.Select(selector: a => RewriteType(type: a, ctx: ctx))
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
        RewriteContext ctx)
    {
        Expression result = expr switch
        {
            TypeExpression te => RewriteType(type: te, ctx: ctx),

            GenericMethodCallExpression gmc => gmc with
            {
                Object = RewriteExpression(expr: gmc.Object, ctx: ctx),
                TypeArguments = gmc.TypeArguments
                                   .Select(selector: a => RewriteType(type: a, ctx: ctx))
                                   .ToList(),
                Arguments = gmc.Arguments
                               .Select(selector: a => RewriteExpression(expr: a, ctx: ctx))
                               .ToList()
            },

            CreatorExpression creator => creator with
            {
                TypeName = ctx.StringSubs.TryGetValue(key: creator.TypeName, value: out string? cSub)
                    ? cSub
                    : creator.TypeName,
                TypeArguments = creator.TypeArguments
                                      ?.Select(selector: a => RewriteType(type: a, ctx: ctx))
                                       .ToList(),
                MemberVariables = creator.MemberVariables
                                         .Select(selector: mv => (mv.Name,
                                              Value: RewriteExpression(expr: mv.Value,
                                                  ctx: ctx)))
                                         .ToList()
            },

            TypeConversionExpression tce => tce with
            {
                TargetType = ctx.StringSubs.TryGetValue(key: tce.TargetType, value: out string? tSub)
                    ? tSub
                    : tce.TargetType,
                Expression = RewriteExpression(expr: tce.Expression, ctx: ctx)
            },

            ListLiteralExpression lle => lle with
            {
                Elements = lle.Elements
                              .Select(selector: e => RewriteExpression(expr: e, ctx: ctx))
                              .ToList(),
                ElementType = lle.ElementType != null
                    ? RewriteType(type: lle.ElementType, ctx: ctx)
                    : null
            },

            SetLiteralExpression sle => sle with
            {
                Elements = sle.Elements
                              .Select(selector: e => RewriteExpression(expr: e, ctx: ctx))
                              .ToList(),
                ElementType = sle.ElementType != null
                    ? RewriteType(type: sle.ElementType, ctx: ctx)
                    : null
            },

            DictLiteralExpression dle => dle with
            {
                Pairs = dle.Pairs
                           .Select(selector: p => (Key: RewriteExpression(expr: p.Key, ctx: ctx),
                                Value: RewriteExpression(expr: p.Value, ctx: ctx)))
                           .ToList(),
                KeyType = dle.KeyType != null
                    ? RewriteType(type: dle.KeyType, ctx: ctx)
                    : null,
                ValueType = dle.ValueType != null
                    ? RewriteType(type: dle.ValueType, ctx: ctx)
                    : null
            },

            IsPatternExpression ipe => ipe with
            {
                Expression = RewriteExpression(expr: ipe.Expression, ctx: ctx),
                Pattern = RewritePattern(pattern: ipe.Pattern, ctx: ctx)
            },

            // Fold T.BS_ROUTINE() ??compile-time literal during monomorphization.
            // After substituting T ??Byte (or S64 etc.), the identifier is now a concrete
            // type name. BuilderServiceInliningPass handles the static/concrete cases;
            // this fold handles the residual case where the receiver name still matches a
            // type-param string substitution (e.g. the receiver is IdentifierExpression("T")
            // and stringSubs["T"] = "Core.Byte" ??the name hasn't been rewritten yet when
            // the switch arm fires).
            CallExpression { Callee: MemberExpression { PropertyName: var bsName } bsCallee,
                Arguments: { Count: 0 } } bsCall
                when ctx.Registry != null && BuilderServiceInliningPass.IsFoldable(bsName)
                => TryFoldBsCallViaStringSubs(
                       callee: bsCallee, location: bsCall.Location, ctx: ctx)
                   ?? (Expression)(bsCall with
                   {
                       Callee = RewriteExpression(expr: bsCall.Callee, ctx: ctx),
                       Arguments = [],
                       TypeArguments = null
                   }),

            CallExpression call => call with
            {
                Callee = RewriteExpression(expr: call.Callee, ctx: ctx),
                Arguments = call.Arguments
                                .Select(selector: a => RewriteExpression(expr: a, ctx: ctx))
                                .ToList(),
                TypeArguments = call.TypeArguments
                                    ?.Select(selector: ta => RewriteType(type: ta, ctx: ctx))
                                     .ToList()
            },

            MemberExpression me => me with
            {
                Object = RewriteExpression(expr: me.Object, ctx: ctx)
            },

            OptionalMemberExpression ome => ome with
            {
                Object = RewriteExpression(expr: ome.Object, ctx: ctx)
            },

            GenericMemberExpression gme => gme with
            {
                Object = RewriteExpression(expr: gme.Object, ctx: ctx),
                TypeArguments = gme.TypeArguments
                                   .Select(selector: a => RewriteType(type: a, ctx: ctx))
                                   .ToList()
            },

            IndexExpression idx => idx with
            {
                Object = RewriteExpression(expr: idx.Object, ctx: ctx),
                Index = RewriteExpression(expr: idx.Index, ctx: ctx)
            },

            SliceExpression slice => slice with
            {
                Object = RewriteExpression(expr: slice.Object, ctx: ctx),
                Start = RewriteExpression(expr: slice.Start, ctx: ctx),
                End = RewriteExpression(expr: slice.End, ctx: ctx)
            },

            BinaryExpression bin => bin with
            {
                Left = RewriteExpression(expr: bin.Left, ctx: ctx),
                Right = RewriteExpression(expr: bin.Right, ctx: ctx)
            },

            UnaryExpression un => un with
            {
                Operand = RewriteExpression(expr: un.Operand, ctx: ctx)
            },

            CompoundAssignmentExpression ca => ca with
            {
                Target = RewriteExpression(expr: ca.Target, ctx: ctx),
                Value = RewriteExpression(expr: ca.Value, ctx: ctx)
            },

            ConditionalExpression cond => cond with
            {
                Condition = RewriteExpression(expr: cond.Condition, ctx: ctx),
                TrueExpression = RewriteExpression(expr: cond.TrueExpression, ctx: ctx),
                FalseExpression = RewriteExpression(expr: cond.FalseExpression, ctx: ctx)
            },

            BlockExpression block => block with
            {
                Value = RewriteExpression(expr: block.Value, ctx: ctx)
            },

            LambdaExpression lambda => lambda with
            {
                Parameters = lambda.Parameters
                                   .Select(selector: p => RewriteParameter(param: p, ctx: ctx))
                                   .ToList(),
                Body = RewriteExpression(expr: lambda.Body, ctx: ctx)
            },

            TupleLiteralExpression tuple => tuple with
            {
                Elements = tuple.Elements
                                .Select(selector: e => RewriteExpression(expr: e, ctx: ctx))
                                .ToList()
            },

            RangeExpression range => range with
            {
                Start = RewriteExpression(expr: range.Start, ctx: ctx),
                End = RewriteExpression(expr: range.End, ctx: ctx),
                Step = range.Step != null
                    ? RewriteExpression(expr: range.Step, ctx: ctx)
                    : null
            },

            ChainedComparisonExpression chain => chain with
            {
                Operands = chain.Operands
                                .Select(selector: o => RewriteExpression(expr: o, ctx: ctx))
                                .ToList()
            },

            WithExpression we => we with
            {
                Base = RewriteExpression(expr: we.Base, ctx: ctx),
                Updates = we.Updates
                            .Select(selector: u => (u.MemberVariablePath, u.Index != null
                                     ? RewriteExpression(expr: u.Index, ctx: ctx)
                                     : (Expression?)null,
                                 RewriteExpression(expr: u.Value, ctx: ctx)))
                            .ToList()
            },

            InsertedTextExpression ite => ite with
            {
                Parts = ite.Parts
                           .Select(selector: p => RewriteInsertedTextPart(part: p, ctx: ctx))
                           .ToList()
            },

            DictEntryLiteralExpression del => del with
            {
                Key = RewriteExpression(expr: del.Key, ctx: ctx),
                Value = RewriteExpression(expr: del.Value, ctx: ctx)
            },

            WaitforExpression wf => wf with
            {
                Operand = RewriteExpression(expr: wf.Operand, ctx: ctx),
                Timeout = wf.Timeout != null
                    ? RewriteExpression(expr: wf.Timeout, ctx: ctx)
                    : null
            },

            DependentWaitforExpression dwf => dwf with
            {
                Dependencies = dwf.Dependencies
                                  .Select(selector: d => d with
                                   {
                                       DependencyExpr =
                                       RewriteExpression(expr: d.DependencyExpr, ctx: ctx)
                                   })
                                  .ToList(),
                Operand = RewriteExpression(expr: dwf.Operand, ctx: ctx),
                Timeout = dwf.Timeout != null
                    ? RewriteExpression(expr: dwf.Timeout, ctx: ctx)
                    : null
            },

            BackIndexExpression bi => bi with
            {
                Operand = RewriteExpression(expr: bi.Operand, ctx: ctx)
            },

            WhenExpression we => we with
            {
                Expression = we.Expression != null
                    ? RewriteExpression(expr: we.Expression, ctx: ctx)
                    : null,
                Clauses = we.Clauses
                            .Select(selector: c => RewriteWhenClause(clause: c, ctx: ctx))
                            .ToList()
            },

            FlagsTestExpression fte => fte with
            {
                Subject = RewriteExpression(expr: fte.Subject, ctx: ctx)
            },

            // Carrier pattern expressions (generated by CrashableExpansionPass + PatternLoweringPass)
            CarrierPayloadExpression cpe => cpe with
            {
                Carrier = RewriteExpression(expr: cpe.Carrier, ctx: ctx)
            },

            // Leaf nodes ??no children to rewrite
            LiteralExpression => expr,

            _ => expr // Unknown expression type ??return as-is
        };

        // Annotate the cloned expression's ResolvedType with the substituted concrete type.
        // This lets codegen's GetExpressionType() return the correct type without falling back
        // on _typeSubstitutions (the mutable global-state fallback).
        if (!ReferenceEquals(result, expr))
        {
            TypeInfo? resolved = ctx.ResolveType(original: expr.ResolvedType);
            if (resolved != null)
                result.ResolvedType = resolved;
        }

        return result;
    }

    // ?�?�?� BuilderService constant folding ?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�?�

    /// <summary>
    /// Attempts to fold <c>T.BS_ROUTINE()</c> to a literal expression when the receiver
    /// can be resolved through the string-substitution map (e.g. T ??"Core.Byte").
    /// This covers the monomorphization case where the receiver name hasn't been rewritten
    /// to the concrete identifier yet when the switch arm fires.
    /// Returns null if the type cannot be resolved (caller falls back to normal rewrite).
    /// </summary>
    private static Expression? TryFoldBsCallViaStringSubs(
        MemberExpression callee, SourceLocation location, RewriteContext ctx)
    {
        // Resolve the receiver name through the string substitution map first,
        // then fall back to the literal identifier name (already concrete).
        // Handles both IdentifierExpression("T") and TypeExpression("T") receivers ??
        // the RF parser may produce either depending on context.
        string? typeName = callee.Object switch
        {
            IdentifierExpression id when ctx.StringSubs.TryGetValue(
                key: id.Name, value: out string? sub) => sub,
            IdentifierExpression id => id.Name,
            TypeExpression te when ctx.StringSubs.TryGetValue(
                key: te.Name, value: out string? teSub) => teSub,
            TypeExpression te => te.Name,
            _ => null
        };
        if (typeName == null) return null;

        TypeInfo? typeInfo = ctx.Registry!.LookupType(name: typeName);
        if (typeInfo == null) return null;

        // Delegate actual folding to BuilderServiceInliningPass so both code paths
        // use identical constant-computation logic.
        TypeInfo? u64Type = ctx.Registry.LookupType(name: "U64");
        TypeInfo? s64Type = ctx.Registry.LookupType(name: "S64");
        TypeInfo? textType = ctx.Registry.LookupType(name: "Text");
        TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");
        TypeInfo? byteSizeType = ctx.Registry.LookupType(name: "ByteSize");

        return callee.PropertyName switch
        {
            "data_size" when u64Type != null && byteSizeType != null =>
                BuilderServiceInliningPass.MakeByteSizeCreatorPublic(
                    BuilderServiceInliningPass.CalculateDataSizeForType(typeInfo),
                    u64Type, byteSizeType, location),

            "type_id" when u64Type != null =>
                new LiteralExpression(
                    Value: TypeIdHelper.ComputeTypeId(typeInfo.FullName),
                    LiteralType: TokenType.U64Literal,
                    Location: location) { ResolvedType = u64Type },

            "type_name" when textType != null =>
                new LiteralExpression(
                    Value: typeInfo.Name,
                    LiteralType: TokenType.TextLiteral,
                    Location: location) { ResolvedType = textType },

            "module_name" when textType != null =>
                new LiteralExpression(
                    Value: typeInfo.Module ?? "",
                    LiteralType: TokenType.TextLiteral,
                    Location: location) { ResolvedType = textType },

            "full_type_name" when textType != null =>
                new LiteralExpression(
                    Value: string.IsNullOrEmpty(typeInfo.Module)
                        ? typeInfo.Name
                        : $"{typeInfo.Module}.{typeInfo.Name}",
                    LiteralType: TokenType.TextLiteral,
                    Location: location) { ResolvedType = textType },

            "member_variable_count" when s64Type != null =>
                new LiteralExpression(
                    Value: (long)(typeInfo switch
                    {
                        RecordTypeInfo r => r.MemberVariables.Count,
                        EntityTypeInfo e => e.MemberVariables.Count,
                        CrashableTypeInfo c => c.MemberVariables.Count,
                        TupleTypeInfo t => t.MemberVariables.Count,
                        ChoiceTypeInfo ch => ch.Cases.Count,
                        FlagsTypeInfo f => f.Members.Count,
                        VariantTypeInfo v => v.Members.Count,
                        _ => 0
                    }),
                    LiteralType: TokenType.S64Literal,
                    Location: location) { ResolvedType = s64Type },

            "is_generic" when boolType != null =>
                new LiteralExpression(
                    Value: typeInfo.IsGenericDefinition,
                    LiteralType: typeInfo.IsGenericDefinition ? TokenType.True : TokenType.False,
                    Location: location) { ResolvedType = boolType },

            _ => null
        };
    }

    private static InsertedTextPart RewriteInsertedTextPart(InsertedTextPart part,
        RewriteContext ctx)
    {
        return part switch
        {
            ExpressionPart ep => ep with
            {
                Expression = RewriteExpression(expr: ep.Expression, ctx: ctx)
            },
            _ => part // TextPart has no expressions
        };
    }

    #endregion

    #region Statement Rewriting

    /// <summary>
    /// Public entry point: rewrites a pre-transformed variant body <see cref="Statement"/>
    /// by substituting all generic type parameter references with concrete names.
    /// Used by the monomorphization loop for variant bodies stored in <c>_synthesizedBodies</c>.
    /// </summary>
    public static Statement RewriteStatement(Statement stmt, Dictionary<string, string> subs)
        => RewriteStatement(stmt: stmt, ctx: new RewriteContext(subs, null, null));

    private static Statement RewriteStatement(Statement stmt,
        RewriteContext ctx)
    {
        return stmt switch
        {
            BlockStatement block => block with
            {
                Statements = block.Statements
                                  .Select(selector: s => RewriteStatement(stmt: s, ctx: ctx))
                                  .ToList()
            },

            ExpressionStatement es => es with
            {
                Expression = RewriteExpression(expr: es.Expression, ctx: ctx)
            },

            DeclarationStatement ds => ds with
            {
                Declaration = RewriteDeclaration(decl: ds.Declaration, ctx: ctx)
            },

            AssignmentStatement assign => assign with
            {
                Target = RewriteExpression(expr: assign.Target, ctx: ctx),
                Value = RewriteExpression(expr: assign.Value, ctx: ctx)
            },

            ReturnStatement ret => ret with
            {
                Value = ret.Value != null
                    ? RewriteExpression(expr: ret.Value, ctx: ctx)
                    : null
            },

            BecomesStatement becomes => becomes with
            {
                Value = RewriteExpression(expr: becomes.Value, ctx: ctx)
            },

            IfStatement ifs => ifs with
            {
                Condition = RewriteExpression(expr: ifs.Condition, ctx: ctx),
                ThenStatement = RewriteStatement(stmt: ifs.ThenStatement, ctx: ctx),
                ElseStatement = ifs.ElseStatement != null
                    ? RewriteStatement(stmt: ifs.ElseStatement, ctx: ctx)
                    : null
            },

            WhileStatement ws => ws with
            {
                Condition = RewriteExpression(expr: ws.Condition, ctx: ctx),
                Body = RewriteStatement(stmt: ws.Body, ctx: ctx),
                ElseBranch = ws.ElseBranch != null
                    ? RewriteStatement(stmt: ws.ElseBranch, ctx: ctx)
                    : null
            },

            LoopStatement ls => ls with { Body = RewriteStatement(stmt: ls.Body, ctx: ctx) },

            ForStatement fs => fs with
            {
                Iterable = RewriteExpression(expr: fs.Iterable, ctx: ctx),
                Body = RewriteStatement(stmt: fs.Body, ctx: ctx),
                ElseBranch = fs.ElseBranch != null
                    ? RewriteStatement(stmt: fs.ElseBranch, ctx: ctx)
                    : null
            },

            WhenStatement ws => ws with
            {
                Expression = RewriteExpression(expr: ws.Expression, ctx: ctx),
                Clauses = ws.Clauses
                            .Select(selector: c => RewriteWhenClause(clause: c, ctx: ctx))
                            .ToList()
            },

            ThrowStatement ts => ts with { Error = RewriteExpression(expr: ts.Error, ctx: ctx) },

            DiscardStatement disc => disc with
            {
                Expression = RewriteExpression(expr: disc.Expression, ctx: ctx)
            },

            DangerStatement danger => danger with
            {
                Body = (BlockStatement)RewriteStatement(stmt: danger.Body, ctx: ctx)
            },

            UsingStatement us => us with
            {
                Resource = RewriteExpression(expr: us.Resource, ctx: ctx),
                Body = RewriteStatement(stmt: us.Body, ctx: ctx)
            },

            DestructuringStatement destruct => destruct with
            {
                Initializer = RewriteExpression(expr: destruct.Initializer, ctx: ctx)
            },

            VariantReturnStatement vr => vr with
            {
                Value = vr.Value != null ? RewriteExpression(expr: vr.Value, ctx: ctx) : null
            },

            // Leaf statements
            BreakStatement or ContinueStatement or PassStatement or AbsentStatement => stmt,

            _ => stmt
        };
    }

    private static WhenClause RewriteWhenClause(WhenClause clause,
        RewriteContext ctx)
    {
        return clause with
        {
            Pattern = RewritePattern(pattern: clause.Pattern, ctx: ctx),
            Body = RewriteStatement(stmt: clause.Body, ctx: ctx)
        };
    }

    #endregion

    #region Pattern Rewriting

    private static Pattern RewritePattern(Pattern pattern,
        RewriteContext ctx)
    {
        return pattern switch
        {
            TypePattern tp => tp with
            {
                Type = RewriteType(type: tp.Type, ctx: ctx),
                Bindings = tp.Bindings
                            ?.Select(selector: b => RewriteBinding(binding: b, ctx: ctx))
                             .ToList()
            },

            NegatedTypePattern ntp => ntp with { Type = RewriteType(type: ntp.Type, ctx: ctx) },

            TypeDestructuringPattern tdp => tdp with
            {
                Type = RewriteType(type: tdp.Type, ctx: ctx),
                Bindings = tdp.Bindings
                              .Select(selector: b => RewriteBinding(binding: b, ctx: ctx))
                              .ToList()
            },

            GuardPattern gp => gp with
            {
                InnerPattern = RewritePattern(pattern: gp.InnerPattern, ctx: ctx),
                Guard = RewriteExpression(expr: gp.Guard, ctx: ctx)
            },

            ExpressionPattern ep => ep with
            {
                Expression = RewriteExpression(expr: ep.Expression, ctx: ctx)
            },

            ComparisonPattern cp => cp with
            {
                Value = RewriteExpression(expr: cp.Value, ctx: ctx)
            },

            VariantPattern vp => vp with
            {
                Bindings = vp.Bindings
                            ?.Select(selector: b => RewriteBinding(binding: b, ctx: ctx))
                             .ToList()
            },

            CrashablePattern crash => crash with
            {
                ErrorType = crash.ErrorType != null
                    ? RewriteType(type: crash.ErrorType, ctx: ctx)
                    : null
            },

            DestructuringPattern dp => dp with
            {
                Bindings = dp.Bindings
                             .Select(selector: b => RewriteBinding(binding: b, ctx: ctx))
                             .ToList()
            },

            // Leaf patterns
            LiteralPattern or IdentifierPattern or WildcardPattern or NonePattern or ElsePattern
                or FlagsPattern => pattern,

            _ => pattern
        };
    }

    private static DestructuringBinding RewriteBinding(DestructuringBinding binding,
        RewriteContext ctx)
    {
        return binding with
        {
            NestedPattern = binding.NestedPattern != null
                ? RewritePattern(pattern: binding.NestedPattern, ctx: ctx)
                : null
        };
    }

    #endregion

    #region Declaration Rewriting (for DeclarationStatements)

    private static Declaration RewriteDeclaration(Declaration decl,
        RewriteContext ctx)
    {
        return decl switch
        {
            VariableDeclaration vd => vd with
            {
                Type = vd.Type != null
                    ? RewriteType(type: vd.Type, ctx: ctx)
                    : null,
                Initializer = vd.Initializer != null
                    ? RewriteExpression(expr: vd.Initializer, ctx: ctx)
                    : null
            },

            _ => decl // Other declarations in statement context are rare
        };
    }

    #endregion
}
