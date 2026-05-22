using System;
using System.Collections.Generic;
using System.Linq;
using Compiler.Tokenizer;
using Compiler.Postprocessing;
using Compiler.Postprocessing.Passes;
using Compiler.Resolution;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Instantiation;

/// <summary>
/// Deep-clones a RoutineDeclaration AST, replacing all occurrences of generic type
/// parameter names with their concrete substitutions. This allows codegen to work
/// with a fully-resolved AST where no generic parameters remain.
/// </summary>
internal static class GenericAstRewriter
{
    private const string CreateMethodName = "$create";
    /// <summary>
    /// Rewrites a generic routine declaration by substituting all type parameter references
    /// with concrete type names. Returns a deep clone -> the original is not modified.
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
        TypeRegistry? registry = null,
        RoutineInfo? enclosingRoutine = null)
    {
        var ctx = typeSubs != null && registry != null
            ? new RewriteContext(subs, typeSubs, registry)
            : new RewriteContext(subs, null, null);

        var rewrittenParams = routine.Parameters
                                     .Select(selector: p => RewriteParameter(param: p, ctx: ctx))
                                     .ToList();

        foreach (Parameter p in rewrittenParams)
        {
            TypeInfo? pType = p.Type?.ResolvedType ?? (p.Type != null ? ctx.ResolveTypeExpressionPublic(p.Type) : null);
            if (pType != null) ctx.ParamTypes[p.Name] = pType;
        }
        if (enclosingRoutine?.OwnerType is { } ownerType)
        {
            TypeInfo resolvedOwner = ctx.ResolveType(ownerType) ?? ownerType;
            ctx.ParamTypes["me"] = resolvedOwner;
        }
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

    //  RewriteContext

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
        /// Map of parameter name to substituted TypeInfo for the routine being rewritten.
        /// Populated by <see cref="Rewrite"/> and the public <c>RewriteStatement</c>
        /// overloads that accept a routine. Used to backfill <c>IdentifierExpression.ResolvedType</c>
        /// when SA failed to annotate a parameter reference (e.g. <c>you.address()</c> in
        /// <c>Hijacked[T].$cmp</c>'s generic-def body).
        /// </summary>
        public Dictionary<string, TypeInfo> ParamTypes { get; } = new();

        /// <summary>
        /// Resolves a <see cref="TypeInfo"/> through the substitution map. Returns null
        /// when the registry is not available or the type has no substitution.
        /// </summary>
        public TypeInfo? ResolveType(TypeInfo? original)
        {
            if (original == null || TypeSubs == null || Registry == null)
                return null;

            // Direct generic parameter substitution: T -> S64
            if (original is GenericParameterTypeInfo gp)
            {
                if (TypeSubs.TryGetValue(key: gp.Name, value: out TypeInfo? direct))
                    return direct;
                // Wrapper-forwarder rename fallback: the param carries the original inner-T
                // name as a structural marker (`ForwarderOriginalName`). At monomorphization
                // time the binding lives under that name, not under the disambiguated `Name`.
                if (gp.ForwarderOriginalName is { } originalInnerName
                    && TypeSubs.TryGetValue(key: originalInnerName, value: out TypeInfo? renamed))
                    return renamed;
            }

            // Generic resolution with substitutable type arguments: List[T] -> List[S64]
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
                    {
                        // Prefer cached resolution if already created — else CREATE one via
                        // GetOrCreateResolution so nested-generic args (e.g. Retained[ListNode[T]]
                        // with T → S64 producing Retained[ListNode[S64]]) actually get registered.
                        // TryGetResolution alone falls back to `original` if the registry hasn't
                        // seen the combination, leaving the inner type-arg substitution lost.
                        return Registry.TryGetResolution(genericDef: genericBase,
                                   typeArguments: newArgs)
                            ?? Registry.GetOrCreateResolution(genericDef: genericBase,
                                   typeArguments: newArgs);
                    }
                }
            }

            // Generic definition used as a concrete type (e.g., owner type List[T] where T -> S64).
            // This arises when SA annotates `me.ResolvedType = List[T]` (the generic def) and the
            // rewriter encounters it while building a concrete body. Substitute all params from
            // TypeSubs and look up the concrete resolution so downstream scanners see the right owner.
            if (original is { IsGenericDefinition: true, GenericParameters: not null } &&
                original.TypeArguments == null)
            {
                var typeArgs = new List<TypeInfo>(capacity: original.GenericParameters.Count);
                bool complete = true;
                foreach (string gpName in original.GenericParameters)
                {
                    if (TypeSubs.TryGetValue(key: gpName, value: out TypeInfo? subType))
                        typeArgs.Add(item: subType);
                    else { complete = false; break; }
                }
                if (complete && typeArgs.Count > 0)
                    return Registry.TryGetResolution(genericDef: original, typeArguments: typeArgs);
            }

            // WrapperTypeInfo (Hijacked[T] -> Hijacked[S64], or Hijacked[S64] -> stays): always
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

                if (Registry != null && newWrapperArgs.Count == 1)
                {
                    // Create-if-missing — body rewriting can encounter wrapper parameterizations
                    // (e.g., Hijacked[Owned[Text]]) that no earlier pass materialized. Without
                    // creation here, GMP never sees the type and codegen emits unresolved symbols.
                    return Registry.GetOrCreateWrapperType(wrapperName: wrapper.Name,
                        innerType: newWrapperArgs[0],
                        isReadOnly: wrapper.IsReadOnly);
                }
            }

            return null;
        }

        public RoutineInfo? ResolveRoutine(RoutineInfo? original, TypeInfo? expressionType = null,
            List<TypeInfo>? callArgTypes = null)
        {
            if (original == null || Registry == null)
                return null;

            TypeInfo? resolvedOwner = ResolveTypeForLookup(original.OwnerType);
            var resolvedParamTypes = original.Parameters
                                             .Select(selector =>
                                                  ResolveTypeForLookup(selector.Type) ??
                                                  selector.Type)
                                             .ToList();
            // Prefer concrete call-site arg types for method-generic inference — routine's own
            // Parameters still carry generic param refs (e.g., I) that ResolveTypeForLookup
            // can't concretize since they're method-level, not owner-level.
            List<TypeInfo> methodInferArgTypes = callArgTypes is { Count: > 0 }
                ? callArgTypes
                : resolvedParamTypes;

            if (resolvedOwner != null)
            {
                RoutineInfo? resolvedMethod = ResolveMethodOnConcreteOwner(ownerType: resolvedOwner,
                    methodName: original.Name,
                    argTypes: resolvedParamTypes,
                    isFailable: original.IsFailable);
                if (resolvedMethod != null)
                {
                    // If LookupMethod returned the generic-definition form of a method-generic
                    // routine (e.g., Array[T,N].$getitem[I]), monomorphize it using the
                    // substituted argument types so codegen gets a concrete routine, not a
                    // generic-def one.
                    if (resolvedMethod.IsGenericDefinition &&
                        resolvedMethod.GenericParameters is { Count: > 0 })
                    {
                        RoutineInfo? methodResolved = TryResolveMethodGeneric(
                            routine: resolvedMethod,
                            argTypes: methodInferArgTypes);
                        if (methodResolved != null)
                        {
                            return methodResolved;
                        }
                    }

                    if (resolvedMethod.OwnerType is not { IsGenericDefinition: true })
                    {
                        return resolvedMethod;
                    }
                }
            }

            if (original.Name == CreateMethodName)
            {
                TypeInfo? resolvedTarget = ResolveTypeForLookup(expressionType);
                if (resolvedTarget != null)
                {
                    RoutineInfo? resolvedCreator = ResolveMethodOnConcreteOwner(ownerType: resolvedTarget,
                        methodName: CreateMethodName,
                        argTypes: resolvedParamTypes,
                        isFailable: original.IsFailable);
                    resolvedCreator ??= Registry.LookupRoutineOverload(
                        baseName: $"{resolvedTarget.Name}.$create",
                        argTypes: resolvedParamTypes);
                    if (resolvedCreator?.OwnerType is { IsGenericDefinition: true })
                    {
                        resolvedCreator = null;
                    }
                    if (resolvedCreator != null)
                    {
                        return resolvedCreator;
                    }
                }
            }

            if (original.OwnerType == null)
            {
                RoutineInfo? instantiatedRoutine = TryInstantiateRoutine(original);
                if (instantiatedRoutine != null)
                {
                    return instantiatedRoutine;
                }

                RoutineInfo? resolvedRoutine =
                    Registry.LookupRoutineOverload(baseName: original.BaseName,
                        argTypes: resolvedParamTypes);
                if (resolvedRoutine != null && resolvedRoutine.IsFailable == original.IsFailable)
                {
                    return resolvedRoutine;
                }
            }

            if (original.IsGenericDefinition ||
                original.OwnerType is GenericParameterTypeInfo or { IsGenericDefinition: true })
            {
                return null;
            }

            return original;
        }

        // Mirrors OperatorLoweringPass.ResolveMethodGenericRoutine: infer method-level
        // generic arguments from call-site param types, then GetOrCreateRoutineResolution.
        private RoutineInfo? TryResolveMethodGeneric(RoutineInfo routine,
            List<TypeInfo> argTypes)
        {
            if (Registry == null || !routine.IsGenericDefinition ||
                routine.GenericParameters == null)
            {
                return null;
            }

            var inferred = new TypeInfo?[routine.GenericParameters.Count];
            int count = Math.Min(val1: routine.Parameters.Count, val2: argTypes.Count);
            for (int i = 0; i < count; i++)
            {
                InferMethodParam(paramType: routine.Parameters[index: i].Type,
                    argType: argTypes[index: i],
                    genericParams: routine.GenericParameters,
                    inferred: inferred);
            }

            if (inferred.Any(predicate: t => t == null))
            {
                return null;
            }

            return Registry.GetOrCreateRoutineResolution(genericDef: routine,
                typeArguments: inferred.Select(selector: t => t!).ToList());
        }

        private static void InferMethodParam(TypeInfo? paramType, TypeInfo argType,
            List<string> genericParams, TypeInfo?[] inferred) // NOSONAR S3776
        {
            if (paramType == null) return;
            if (paramType is GenericParameterTypeInfo gp)
            {
                for (int idx = 0; idx < genericParams.Count; idx++)
                {
                    if (genericParams[index: idx] == gp.Name)
                    {
                        if (inferred[idx] == null) inferred[idx] = argType;
                        break;
                    }
                }
                return;
            }

            if (paramType.TypeArguments is { Count: > 0 } pArgs &&
                argType.TypeArguments is { Count: > 0 } aArgs)
            {
                int n = Math.Min(val1: pArgs.Count, val2: aArgs.Count);
                for (int i = 0; i < n; i++)
                {
                    InferMethodParam(paramType: pArgs[index: i],
                        argType: aArgs[index: i],
                        genericParams: genericParams,
                        inferred: inferred);
                }
            }
        }

        private RoutineInfo? TryInstantiateRoutine(RoutineInfo routine)
        {
            RoutineInfo genericDefinition = routine.GenericDefinition ?? routine;
            if (!genericDefinition.IsGenericDefinition || genericDefinition.GenericParameters == null)
            {
                return null;
            }

            var resolvedTypeArgs = new List<TypeInfo>(capacity: genericDefinition.GenericParameters.Count);
            for (int i = 0; i < genericDefinition.GenericParameters.Count; i++)
            {
                TypeInfo? resolvedArg = null;
                if (routine.TypeArguments is { Count: > 0 } &&
                    i < routine.TypeArguments.Count)
                {
                    resolvedArg = ResolveTypeForLookup(routine.TypeArguments[index: i]);
                }

                resolvedArg ??= TypeSubs != null &&
                                TypeSubs.TryGetValue(key: genericDefinition.GenericParameters[index: i],
                                    value: out TypeInfo? substituted)
                    ? substituted
                    : null;

                if (resolvedArg == null || resolvedArg is GenericParameterTypeInfo)
                {
                    return null;
                }

                resolvedTypeArgs.Add(item: resolvedArg);
            }

            return Registry?.GetOrCreateRoutineResolution(genericDef: genericDefinition,
                typeArguments: resolvedTypeArgs);
        }

        private RoutineInfo? ResolveMethodOnConcreteOwner(TypeInfo ownerType, string methodName,
            List<TypeInfo> argTypes, bool isFailable)
        {
            string lookupMethodName = NormalizeMethodLookupName(methodName: methodName);
            RoutineInfo? overload = Registry!.LookupMethodOverload(type: ownerType,
                methodName: lookupMethodName,
                argTypes: argTypes);
            if (overload?.OwnerType is not { IsGenericDefinition: true })
            {
                return overload ?? Registry.LookupMethod(type: ownerType,
                    methodName: lookupMethodName,
                    isFailable: isFailable);
            }

            return Registry.LookupMethod(type: ownerType,
                methodName: lookupMethodName,
                isFailable: isFailable);
        }

        private static string NormalizeMethodLookupName(string methodName)
        {
            int dotIndex = methodName.LastIndexOf('.');
            return dotIndex >= 0 && dotIndex + 1 < methodName.Length
                ? methodName[(dotIndex + 1)..]
                : methodName;
        }

        public RoutineInfo? ResolveCallRoutine(CallExpression call, TypeInfo? expressionType,
            List<TypeInfo> callArgTypes)
        {
            if (Registry == null)
            {
                return null;
            }

            return call.Callee switch
            {
                MemberExpression member => ResolveMemberCallRoutine(member: member,
                    callArgTypes: callArgTypes),
                IdentifierExpression identifier => ResolveFreeCallRoutine(call: call,
                    identifier: identifier,
                    expressionType: expressionType,
                    callArgTypes: callArgTypes),
                _ => null
            };
        }

        private RoutineInfo? ResolveMemberCallRoutine(MemberExpression member,
            List<TypeInfo> callArgTypes)
        {
            TypeInfo? receiverType = ResolveTypeForLookup(member.Object.ResolvedType);
            if (receiverType == null)
            {
                return null;
            }

            string lookupName = member.PropertyName.EndsWith(value: '!')
                ? member.PropertyName[..^1]
                : member.PropertyName;
            return ResolveMethodOnConcreteOwner(ownerType: receiverType,
                methodName: lookupName,
                argTypes: callArgTypes,
                isFailable: member.PropertyName.EndsWith(value: '!'));
        }

        private RoutineInfo? ResolveFreeCallRoutine(CallExpression call,
            IdentifierExpression identifier,
            TypeInfo? expressionType, List<TypeInfo> callArgTypes)
        {
            string callName = identifier.Name.EndsWith(value: '!')
                ? identifier.Name[..^1]
                : identifier.Name;
            bool isFailable = identifier.Name.EndsWith(value: '!');

            RoutineInfo? InstantiateFreeRoutine(RoutineInfo candidate)
            {
                if (candidate.IsGenericDefinition && call.TypeArguments is { Count: > 0 })
                {
                    var resolvedTypeArgs = call.TypeArguments
                        .Select(selector => ResolveTypeExpression(typeExpr: selector))
                        .Where(predicate: t => t is not null and not ErrorTypeInfo)
                        .Cast<TypeInfo>()
                        .ToList();

                    if (candidate.GenericParameters?.Count == resolvedTypeArgs.Count)
                    {
                        return Registry.GetOrCreateRoutineResolution(genericDef: candidate,
                            typeArguments: resolvedTypeArgs);
                    }
                }

                return TryInstantiateRoutine(routine: candidate) ?? candidate;
            }

            RoutineInfo? routine = Registry!.LookupRoutineOverload(baseName: callName,
                argTypes: callArgTypes!);
            if (routine != null && routine.IsFailable == isFailable)
            {
                return InstantiateFreeRoutine(candidate: routine);
            }

            routine = Registry.LookupRoutine(fullName: callName, isFailable: isFailable) ??
                      Registry.LookupRoutineByName(name: callName, isFailable: isFailable);
            if (routine != null)
            {
                return InstantiateFreeRoutine(candidate: routine);
            }

            if (callName == CreateMethodName && expressionType != null)
            {
                TypeInfo? resolvedTarget = ResolveTypeForLookup(expressionType);
                if (resolvedTarget != null)
                {
                    return ResolveMethodOnConcreteOwner(ownerType: resolvedTarget,
                        methodName: CreateMethodName,
                        argTypes: callArgTypes,
                        isFailable: isFailable);
                }
            }

            return null;
        }

        public TypeInfo? ResolveTypeExpressionPublic(TypeExpression typeExpr) => ResolveTypeExpression(typeExpr);

        private TypeInfo? ResolveTypeExpression(TypeExpression typeExpr)
        {
            if (Registry == null)
            {
                return null;
            }

            if (TypeSubs != null && TypeSubs.TryGetValue(key: typeExpr.Name, value: out TypeInfo? substituted))
            {
                return substituted;
            }

            TypeInfo? baseType = Registry.LookupType(name: typeExpr.Name);
            if (baseType == null)
            {
                return null;
            }

            if (baseType.IsGenericDefinition && typeExpr.GenericArguments is { Count: > 0 })
            {
                var resolvedArgs = typeExpr.GenericArguments
                    .Select(selector => ResolveTypeExpression(typeExpr: selector))
                    .Where(predicate: t => t != null)
                    .Cast<TypeInfo>()
                    .ToList();

                if (baseType.GenericParameters?.Count == resolvedArgs.Count)
                {
                    return Registry.GetOrCreateResolution(genericDef: baseType,
                        typeArguments: resolvedArgs);
                }
            }

            return baseType;
        }

        private TypeInfo? ResolveTypeForLookup(TypeInfo? original)
        {
            if (original == null)
            {
                return null;
            }

            TypeInfo resolved = ResolveType(original: original) ?? original;
            if (resolved is WrapperTypeInfo wrapperType &&
                Registry != null &&
                Registry.LookupType(name: wrapperType.Name) is { IsGenericDefinition: true } wrapperDef &&
                wrapperType.TypeArguments is { Count: > 0 })
            {
                return Registry.TryGetResolution(genericDef: wrapperDef,
                    typeArguments: wrapperType.TypeArguments) ?? resolved;
            }

            return resolved;
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

            // Fold T.BS_ROUTINE() -> compile-time literal during monomorphization.
            // After substituting T -> Byte (or S64 etc.), the identifier is now a concrete
            // type name. BuilderServiceInliningPass handles the static/concrete cases:
            // this fold handles the residual case where the receiver name still matches a
            // type-param string substitution (e.g., the receiver is IdentifierExpression("T")
            // and stringSubs["T"] = "Core.Byte" -> the name hasn't been rewritten yet when
            // the switch arm fires).
            CallExpression { Callee: MemberExpression { PropertyName: var bsName } bsCallee,
                Arguments: { Count: 0 } } bsCall
                when ctx.Registry != null && BuilderServiceInliningPass.IsFoldable(bsName)
                => TryFoldBsCallViaStringSubs(
                       callee: bsCallee, location: bsCall.Location, ctx: ctx)
                   ?? bsCall with
                   {
                       Callee = RewriteExpression(expr: bsCall.Callee, ctx: ctx),
                       Arguments = [],
                       TypeArguments = null
                   },

            CallExpression call => ReclassifyIfNeeded(CloneCall(call, ctx), ctx),

            MemberExpression me => CloneMember(me, ctx),

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

            StealExpression steal => steal with
            {
                Operand = RewriteExpression(expr: steal.Operand, ctx: ctx)
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
                                     : null,
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

            NamedArgumentExpression nae => nae with { Value = RewriteExpression(expr: nae.Value, ctx: ctx) },

            IdentifierExpression identifier => identifier with { },

            // Leaf nodes -> no children to rewrite
            LiteralExpression => expr,

            _ => expr // Unknown expression type -> return as-is
        };

        // Annotate the cloned expression's ResolvedType with the substituted concrete type.
        // This lets codegen's GetExpressionType() return the correct type without falling back
        // on _typeSubstitutions (the mutable global-state fallback).
        if (!ReferenceEquals(result, expr))
        {
            TypeInfo? resolvedType = ctx.ResolveType(original: expr.ResolvedType) ?? expr.ResolvedType;

            // Const-generic identifiers (e.g. N in Array[T, N]) have ResolvedType=null in the
            // generic body because SA doesn't run on stdlib bodies before Phase 4.  After GMP
            // substitutes N -> ConstGenericValueTypeInfo("63"), annotate the rewritten identifier
            // so CallOverloadResolutionPass can resolve operator calls like N.$sub!(1u64).
            if (resolvedType == null &&
                result is IdentifierExpression cgIdent &&
                ctx.TypeSubs?.TryGetValue(key: cgIdent.Name, value: out TypeInfo? cgSub) == true &&
                cgSub is ConstGenericValueTypeInfo)
            {
                resolvedType = cgSub;
            }

            result.ResolvedType = resolvedType;

            TypeInfo? routineResultType = resolvedType ?? result.ResolvedType ?? expr.ResolvedType;
            switch (result)
            {
                case CallExpression call when call.ResolvedRoutine != null:
                {
                    call.ConstructedType =
                        ctx.ResolveType(original: call.ConstructedType) ??
                        call.ConstructedType ??
                        (expr is CallExpression originalCallWithRoutine
                            ? originalCallWithRoutine.ConstructedType
                            : null);
                    var callArgTypes = call.Arguments
                        .Select(selector: a =>
                            (a is NamedArgumentExpression nae ? nae.Value : a).ResolvedType)
                        .Where(predicate: t => t != null)
                        .Cast<TypeInfo>()
                        .ToList();
                    RoutineInfo? rewrittenRoutine = ctx.ResolveRoutine(
                        original: call.ResolvedRoutine,
                        expressionType: routineResultType,
                        callArgTypes: callArgTypes);
                    if (rewrittenRoutine == null ||
                        CallRoutineNeedsRebinding(routine: rewrittenRoutine))
                    {
                        rewrittenRoutine = ctx.ResolveCallRoutine(call: call,
                            expressionType: routineResultType,
                            callArgTypes: callArgTypes) ?? rewrittenRoutine;
                    }

                    call.ResolvedRoutine = rewrittenRoutine ?? call.ResolvedRoutine;
                    break;
                }

                case CallExpression call:
                {
                    call.ConstructedType =
                        ctx.ResolveType(original: call.ConstructedType) ??
                        call.ConstructedType ??
                        (expr is CallExpression originalCall
                            ? originalCall.ConstructedType
                            : null);
                    var callArgTypes = call.Arguments
                        .Select(selector: a =>
                            (a is NamedArgumentExpression nae ? nae.Value : a).ResolvedType)
                        .Where(predicate: t => t != null)
                        .Cast<TypeInfo>()
                        .ToList();
                    call.ResolvedRoutine = ctx.ResolveCallRoutine(call: call,
                        expressionType: routineResultType,
                        callArgTypes: callArgTypes) ?? call.ResolvedRoutine;
                    break;
                }

                case CreatorExpression creator:
                    creator.ConstructedType =
                        ctx.ResolveType(original: creator.ConstructedType) ??
                        creator.ConstructedType ??
                        (expr is CreatorExpression originalCreator
                            ? originalCreator.ConstructedType
                            : null);
                    if (creator.ResolvedCreatorRoutine != null)
                    {
                        // Rewriting `List[T](capacity: …)` → `List[S64](capacity: …)` updates
                        // TypeArguments/ConstructedType above, but `ResolvedCreatorRoutine`
                        // still points at the generic-def `List[T].$create`. Codegen reads
                        // `creatorRoutine.FullName` directly, so without this resolve the
                        // emitted call references the unsubstituted symbol and links fail.
                        creator.ResolvedCreatorRoutine = ctx.ResolveRoutine(
                            original: creator.ResolvedCreatorRoutine,
                            expressionType: creator.ConstructedType)
                            ?? creator.ResolvedCreatorRoutine;
                    }
                    break;

                case TypeConversionExpression conversion:
                    conversion.ConstructedType =
                        ctx.ResolveType(original: conversion.ConstructedType) ??
                        conversion.ConstructedType ??
                        (expr is TypeConversionExpression originalConversion
                            ? originalConversion.ConstructedType
                            : null);
                    break;

                case GenericMethodCallExpression genericCall when genericCall.ResolvedRoutine != null:
                    genericCall.ConstructedType =
                        ctx.ResolveType(original: genericCall.ConstructedType) ??
                        genericCall.ConstructedType ??
                        (expr is GenericMethodCallExpression originalGenericCall
                            ? originalGenericCall.ConstructedType
                            : null);
                    genericCall.ResolvedRoutine =
                        ctx.ResolveRoutine(original: genericCall.ResolvedRoutine,
                            expressionType: routineResultType) ?? genericCall.ResolvedRoutine;
                    break;
            }

            // Stdlib bodies are processed by SA on the generic definition, so some
            // intermediate call expressions (e.g. me.address() inside $cmp or $diagnose)
            // may arrive with ResolvedType=null when the SA annotation on the generic
            // body's call was not preserved through cloning.  If GMP resolved the routine
            // after the switch, propagate its ReturnType so downstream chained calls
            // (outer .$method() or CallOverloadResolutionPass) can see the receiver type.
            if (result.ResolvedType == null &&
                result is CallExpression { ResolvedRoutine.ReturnType: { } inferredReturnType })
            {
                result.ResolvedType = inferredReturnType;
            }
        }

        return result;
    }

    private static CallExpression CloneCall(CallExpression call, RewriteContext ctx)
    {
        Expression rewrittenCallee = RewriteExpression(expr: call.Callee, ctx: ctx);
        // `T(0)` parses as CallExpression(Callee=IdentifierExpression("T")). The plain
        // IdentifierExpression rewrite arm doesn't substitute names (variables vs. type
        // params are ambiguous). For a CallExpression callee that *is* a generic-param
        // identifier, swap "T" -> "Core.S64" so codegen emits the right constructor.
        if (rewrittenCallee is IdentifierExpression idCallee &&
            ctx.StringSubs.TryGetValue(key: idCallee.Name, value: out string? sub))
        {
            rewrittenCallee = idCallee with { Name = sub };
        }
        var rewritten = call with
        {
            Callee = rewrittenCallee,
            Arguments = call.Arguments
                            .Select(selector: a => RewriteExpression(expr: a, ctx: ctx))
                            .ToList(),
            TypeArguments = call.TypeArguments
                                ?.Select(selector: ta => RewriteType(type: ta, ctx: ctx))
                                 .ToList()
        };
        rewritten.ResolvedRoutine = call.ResolvedRoutine;
        rewritten.ResolvedDispatch = call.ResolvedDispatch;
        rewritten.LoweringKind = call.LoweringKind;
        rewritten.ConstructedType = call.ConstructedType;
        rewritten.IsCollectionLiteral = call.IsCollectionLiteral;
        rewritten.ResolvedType = call.ResolvedType;
        return rewritten;
    }

    private static MemberExpression CloneMember(MemberExpression me, RewriteContext ctx)
    {
        Expression newObj = RewriteExpression(expr: me.Object, ctx: ctx);
        if (newObj.ResolvedType == null && newObj is IdentifierExpression idObj &&
            ctx.ParamTypes.TryGetValue(idObj.Name, out TypeInfo? paramType))
        {
            newObj.ResolvedType = paramType;
        }
        var rewritten = me with { Object = newObj };
        rewritten.ResolvedType = me.ResolvedType;
        return rewritten;
    }

    //  BuilderService constant folding

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
            IdentifierExpression id when id.Name == "me" &&
                ctx.ParamTypes.TryGetValue(key: "me", value: out TypeInfo? meType) => meType.FullName,
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
                        TupleTypeInfo t => t.MemberVariables.Count,
                        ChoiceTypeInfo ch => ch.Cases.Count,
                        FlagsTypeInfo f => f.Members.Count,
                        RecordTypeInfo r => r.MemberVariables.Count,
                        EntityTypeInfo e => e.MemberVariables.Count,
                        CrashableTypeInfo c => c.MemberVariables.Count,
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

    private static CallExpression ReclassifyIfNeeded(CallExpression call, RewriteContext ctx)
    {
        if (call.LoweringKind != CallLoweringKind.RuntimeDispatch) return call;
        if (call.Callee is not MemberExpression { Object.ResolvedType: var rt, PropertyName: var methodName })
            return call;
        if (rt is null or GenericParameterTypeInfo or ProtocolTypeInfo) return call;

        // Const-generic values (e.g. N=4 in Array[T, 4]) are not in _routinesByOwner.
        // Resolve to the underlying numeric type (default U64) before looking up the method.
        TypeInfo lookupType = rt;
        if (rt is ConstGenericValueTypeInfo constVal)
        {
            string underlyingName = constVal.ExplicitTypeName ?? "U64";
            TypeInfo? resolvedUnderlying = ctx.Registry?.LookupType(name: underlyingName);
            if (resolvedUnderlying == null) return call;
            lookupType = resolvedUnderlying;
        }

        // Try the non-failable form first, then the failable form.
        // Numeric types like U64 only define $sub! (underflow is UB); $sub is absent.
        RoutineInfo? resolved = ctx.Registry?.LookupMethod(type: lookupType, methodName: methodName);
        if (resolved == null && !methodName.EndsWith('!'))
            resolved = ctx.Registry?.LookupMethod(type: lookupType, methodName: methodName + "!");

        return call with
        {
            LoweringKind = CallLoweringKind.DirectMemberRoutine,
            ResolvedRoutine = resolved ?? call.ResolvedRoutine
        };
    }

    private static bool CallRoutineNeedsRebinding(RoutineInfo routine)
    {
        if (routine.IsGenericDefinition ||
            routine.OwnerType is GenericParameterTypeInfo or { IsGenericDefinition: true })
        {
            return true;
        }

        return ContainsGenericPlaceholder(type: routine.OwnerType) ||
               ContainsGenericPlaceholder(type: routine.ReturnType) ||
               routine.Parameters.Any(predicate => ContainsGenericPlaceholder(type: predicate.Type));
    }

    private static bool ContainsGenericPlaceholder(TypeInfo? type)
    {
        if (type == null)
        {
            return false;
        }

        if (type is GenericParameterTypeInfo or ErrorTypeInfo)
        {
            return true;
        }

        if (type.IsGenericDefinition && type.TypeArguments is not { Count: > 0 })
        {
            return true;
        }

        if (type.TypeArguments is { Count: > 0 } &&
            type.TypeArguments.Any(ContainsGenericPlaceholder))
        {
            return true;
        }

        return type is TupleTypeInfo tuple &&
               tuple.ElementTypes.Any(ContainsGenericPlaceholder);
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

    public static Statement RewriteStatement(Statement stmt,
        IReadOnlyDictionary<string, string> subs,
        IReadOnlyDictionary<string, TypeInfo>? typeSubs,
        TypeRegistry? registry,
        RoutineInfo? enclosingRoutine = null)
    {
        var ctx = typeSubs != null && registry != null
            ? new RewriteContext(subs, typeSubs, registry)
            : new RewriteContext(subs, null, null);
        if (enclosingRoutine?.Parameters != null)
        {
            foreach (ParameterInfo p in enclosingRoutine.Parameters)
            {
                TypeInfo? resolvedParamType = ctx.ResolveType(p.Type) ?? p.Type;
                if (resolvedParamType != null) ctx.ParamTypes[p.Name] = resolvedParamType;
            }
        }
        if (enclosingRoutine?.OwnerType is { } ownerType)
        {
            TypeInfo resolvedOwner = ctx.ResolveType(ownerType) ?? ownerType;
            ctx.ParamTypes["me"] = resolvedOwner;
        }
        return RewriteStatement(stmt: stmt, ctx: ctx);
    }

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

    private static SyntaxTree.Declaration RewriteDeclaration(SyntaxTree.Declaration decl,
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
