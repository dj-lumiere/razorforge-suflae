using Compiler.Desugaring;
namespace Compiler.Instantiation.Passes;

using Resolution;
using TypeModel.Symbols;
using TypeModel.Types;
using SyntaxTree;

/// <summary>
/// Phase 6 global pass ??pre-rewrites all known generic method bodies at synthesis time
/// so the code generator never needs to search programs or perform AST substitution
/// for the common case.
///
/// <para>
/// The pass iterates every concrete generic type instance recorded in the
/// <see cref="TypeRegistry"/> during Phase 5 (e.g., <c>List[S64]</c>, <c>Maybe[Text]</c>)
/// and generates <see cref="MonomorphizedBody"/> entries for each of the generic
/// definition's methods.  Bodies are sourced from three places:
/// <list type="bullet">
///   <item><see cref="DesugaringContext.VariantBodies"/> ??WiredRoutinePass-generated
///         bodies (<c>$represent</c>, <c>$diagnose</c>) and ErrorHandlingVariantPass
///         bodies (<c>try_next</c>, etc.).</item>
///   <item><c>Registry.StdlibPrograms</c> and <c>Registry.UserPrograms</c> AST declarations ??source bodies.</item>
/// </list>
/// Pure-synthesized methods (<see cref="RoutineInfo.IsSynthesized"/> = true with no
/// body anywhere) are skipped; codegen emits those directly via
/// <c>EmitSynthesizedRoutineBody</c>.
/// </para>
///
/// <para>
/// Results are stored in <see cref="DesugaringContext.PreMonomorphizedBodies"/>,
/// keyed by the concrete routine's <see cref="RoutineInfo.RegistryKey"/>.
/// Codegen checks this map before doing its own AST search.
/// </para>
/// </summary>
public sealed class GenericMonomorphizationPass(DesugaringContext ctx)
{
    // ?€?€?€ Routine-declaration index (built once, O(1) FindInStdlib lookups) ?€?€?€?€

    // Key: routine name (e.g. "List[T].$getitem") ??list of matching declarations.
    // Built once in RunGlobal() before the fixed-point loop.
    private Dictionary<string, List<RoutineDeclaration>> _routineIndex = new();

    private void BuildRoutineIndex()
    {
        _routineIndex = new Dictionary<string, List<RoutineDeclaration>>();
        var allPrograms = ctx.Registry.StdlibPrograms.Concat(ctx.Registry.UserPrograms);
        foreach ((Program program, string _, string _) in allPrograms)
        {
            foreach (RoutineDeclaration decl in program.Declarations.OfType<RoutineDeclaration>())
            {
                if (!_routineIndex.TryGetValue(key: decl.Name, value: out List<RoutineDeclaration>? bucket))
                {
                    bucket = [];
                    _routineIndex[key: decl.Name] = bucket;
                }
                bucket.Add(item: decl);
            }
        }
    }

    // ?€?€?€ Public entry point ?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€

    public void RunGlobal()
    {
        // Pre-build the routine-declaration index so FindInStdlib is O(1) per lookup.
        BuildRoutineIndex();

        // Fixed-point loop over concrete generic type instances (M-2).
        //
        // Body rewriting via GenericAstRewriter.Rewrite calls GetOrCreateResolution for every
        // ResolvedType annotation it encounters, so rewriting SortedList[S64]'s body creates
        // BTreeListNode[S64] (and any other helper types) in the registry ??these new instances
        // appear in AllConcreteGenericInstances after each iteration.  We keep looping until no
        // new types are discovered, at which point we have pre-built bodies for every reachable
        // concrete instantiation before codegen starts.  This eliminates the non-deterministic
        // lazy-discovery order that codegen's fixed-point loop previously produced.
        //
        // Note: GMP's own ResolveSubstitutedType still uses TryGetResolution (lookup-only) when
        // building RoutineInfo signatures, so it never creates instances on its own.  All new
        // instance creation comes from GenericAstRewriter.RewriteContext.ResolveType.
        var processedTypes = new HashSet<string>();
        bool anyNew;
        int iteration = 0;
        do
        {
            anyNew = false;
            iteration++;
            TypeInfo[] instances = ctx.Registry.AllConcreteGenericInstances.ToArray();
            foreach (TypeInfo concreteType in instances)
            {
                if (processedTypes.Add(item: concreteType.FullName))
                {
                    ProcessConcreteType(concreteType);
                    anyNew = true;
                }
            }
        } while (anyNew);
    }

    // ?€?€?€ Per-type processing ?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€

    private void ProcessConcreteType(TypeInfo concreteType)
    {
        TypeInfo? genDef = concreteType switch
        {
            EntityTypeInfo { GenericDefinition: { } d } => d,
            RecordTypeInfo { GenericDefinition: { } d } => d,
            _ => null
        };

        if (genDef?.GenericParameters == null || genDef.GenericParameters.Count == 0)
            return;

        IReadOnlyList<TypeInfo>? typeArgs = concreteType.TypeArguments;
        if (typeArgs == null || typeArgs.Count != genDef.GenericParameters.Count)
            return;

        // Build type substitution maps.
        // stringSubs uses FullName (e.g. "T" ??"Core.S64") so rewritten AST type-expression
        // names are fully qualified. LookupType handles both "S64" and "Core.S64" via the
        // Core-prefix fallback, and GetOrCreateResolution stores types under both the FullName
        // key ("Hijacked[Core.Byte]") and the short-name alias ("Hijacked[Byte]").
        // typeSubs carries the resolved TypeInfo for ResolvedType annotation in GenericAstRewriter.
        var typeSubs = new Dictionary<string, TypeInfo>(capacity: genDef.GenericParameters.Count);
        var stringSubs = new Dictionary<string, string>(capacity: genDef.GenericParameters.Count);
        for (int i = 0; i < genDef.GenericParameters.Count; i++)
        {
            typeSubs[genDef.GenericParameters[i]] = typeArgs[i];
            stringSubs[genDef.GenericParameters[i]] = typeArgs[i].FullName;
        }

        foreach (RoutineInfo genMethod in ctx.Registry.GetMethodsForType(genDef))
        {
            RoutineInfo concreteInfo = BuildConcreteRoutineInfo(
                genMethod: genMethod,
                concreteOwner: concreteType,
                typeSubs: typeSubs);

            string key = concreteInfo.RegistryKey;
            if (ctx.PreMonomorphizedBodies.ContainsKey(key))
                continue;

            MonomorphizedBody? body = BuildBody(
                genMethod: genMethod,
                concreteInfo: concreteInfo,
                genDef: genDef,
                typeSubs: typeSubs,
                stringSubs: stringSubs);

            if (body != null)
                ctx.PreMonomorphizedBodies[key] = body;
        }
    }

    // ?€?€?€ Body construction ?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€

    private MonomorphizedBody? BuildBody(
        RoutineInfo genMethod,
        RoutineInfo concreteInfo,
        TypeInfo genDef,
        Dictionary<string, TypeInfo> typeSubs,
        Dictionary<string, string> stringSubs)
    {
        // ?€?€ Variant methods (try_/check_/lookup_) ?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€
        // These have OriginalName pointing back to the failable source routine.
        // Look for the body of that source routine (not the variant name).
        if (genMethod.OriginalName != null)
            return BuildVariantBody(
                genMethod: genMethod,
                concreteInfo: concreteInfo,
                genDef: genDef,
                typeSubs: typeSubs,
                stringSubs: stringSubs);

        // ?€?€ WiredRoutinePass / ErrorHandlingVariantPass body in VariantBodies ?€?€
        if (ctx.VariantBodies.TryGetValue(key: genMethod.RegistryKey, out Statement? variantBody))
        {
            Statement rewritten = GenericAstRewriter.RewriteStatement(variantBody, stringSubs);
            return new MonomorphizedBody(
                Ast: WrapInShellDecl(name: concreteInfo.Name, body: rewritten, info: concreteInfo),
                Info: concreteInfo,
                TypeSubs: typeSubs,
                VariantStatus: null,
                VariantInnerType: null,
                IsSynthesized: true);  // treat as synthesized so codegen uses EmitSynthesizedBodyFromAst
        }

        // ?€?€ Pure synthesized ??no AST anywhere ?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€
        if (genMethod.IsSynthesized)
            return null; // let codegen's EmitSynthesizedRoutineBody handle it

        // ?€?€ Regular method: search stdlib + user program ASTs ?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€
        string astName = BuildAstName(genDef: genDef, routineName: genMethod.Name);
        RoutineDeclaration? astDecl = FindInStdlib(
            genericAstName: astName,
            expectedParamCount: genMethod.Parameters.Count,
            typeSubs: typeSubs);

        if (astDecl == null)
            return null;

        RoutineDeclaration rewrittenDecl =
            GenericAstRewriter.Rewrite(
                routine: astDecl,
                subs: stringSubs,
                typeSubs: typeSubs,
                registry: ctx.Registry);

        return new MonomorphizedBody(
            Ast: rewrittenDecl,
            Info: concreteInfo,
            TypeSubs: typeSubs,
            VariantStatus: null,
            VariantInnerType: null,
            IsSynthesized: false);
    }

    private MonomorphizedBody? BuildVariantBody(
        RoutineInfo genMethod,
        RoutineInfo concreteInfo,
        TypeInfo genDef,
        Dictionary<string, TypeInfo> typeSubs,
        Dictionary<string, string> stringSubs)
    {
        // Compute carrier-unwrapping metadata (same logic as MonomorphizationPlanner)
        AsyncStatus? variantStatus = null;
        TypeInfo? variantInnerType = null;

        if (concreteInfo.ReturnType?.TypeArguments is { Count: > 0 })
        {
            string? baseName = GetGenericBaseName(concreteInfo.ReturnType);
            if (baseName == "Lookup")
            {
                variantStatus = AsyncStatus.LookupVariant;
                variantInnerType = concreteInfo.ReturnType.TypeArguments[0];
            }
            else if (baseName == "Result")
            {
                variantStatus = AsyncStatus.CheckVariant;
                variantInnerType = concreteInfo.ReturnType.TypeArguments[0];
            }
        }

        // When there is a carrier, the RoutineInfo.ReturnType is the inner type T,
        // not the carrier ??same convention as MonomorphizationPlanner.
        RoutineInfo emitInfo = concreteInfo;
        if (variantStatus != null && variantInnerType != null)
        {
            emitInfo = new RoutineInfo(name: concreteInfo.Name)
            {
                Kind = concreteInfo.Kind,
                OwnerType = concreteInfo.OwnerType,
                Parameters = concreteInfo.Parameters,
                ReturnType = variantInnerType,
                IsFailable = concreteInfo.IsFailable,
                DeclaredModification = concreteInfo.DeclaredModification,
                ModificationCategory = concreteInfo.ModificationCategory,
                Visibility = concreteInfo.Visibility,
                Location = concreteInfo.Location,
                Module = concreteInfo.Module,
                Annotations = concreteInfo.Annotations,
                CallingConvention = concreteInfo.CallingConvention,
                IsVariadic = concreteInfo.IsVariadic,
                IsDangerous = concreteInfo.IsDangerous,
                Storage = concreteInfo.Storage,
                AsyncStatus = variantStatus.Value,
                OriginalName = concreteInfo.OriginalName
            };
        }

        // Pre-built variant body from ErrorHandlingVariantPass (keyed by generic method RegistryKey)
        if (ctx.VariantBodies.TryGetValue(key: genMethod.RegistryKey, out Statement? prebuiltVariant))
        {
            Statement rewritten = GenericAstRewriter.RewriteStatement(prebuiltVariant, stringSubs);
            return new MonomorphizedBody(
                Ast: WrapInShellDecl(name: emitInfo.Name, body: rewritten, info: emitInfo),
                Info: emitInfo,
                TypeSubs: typeSubs,
                VariantStatus: variantStatus,
                VariantInnerType: variantInnerType,
                IsSynthesized: false);
        }

        // Fallback: search for the original failable routine's AST and compile it as a variant
        string fallbackAstName = BuildAstName(genDef: genDef, routineName: genMethod.OriginalName!);
        RoutineDeclaration? astDecl = FindInStdlib(
            genericAstName: fallbackAstName,
            expectedParamCount: genMethod.Parameters.Count);

        if (astDecl == null)
            return null;

        RoutineDeclaration rewrittenDecl =
            GenericAstRewriter.Rewrite(
                routine: astDecl,
                subs: stringSubs,
                typeSubs: typeSubs,
                registry: ctx.Registry);

        return new MonomorphizedBody(
            Ast: rewrittenDecl,
            Info: emitInfo,
            TypeSubs: typeSubs,
            VariantStatus: variantStatus,
            VariantInnerType: variantInnerType,
            IsSynthesized: false);
    }

    // ?€?€?€ Helpers ?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€

    /// <summary>
    /// Builds a concrete <see cref="RoutineInfo"/> by substituting type parameters.
    /// Mirrors <c>MonomorphizationPlanner.BuildRoutineInfo</c>.
    /// </summary>
    private RoutineInfo BuildConcreteRoutineInfo(
        RoutineInfo genMethod,
        TypeInfo concreteOwner,
        Dictionary<string, TypeInfo> typeSubs)
    {
        // Wrapper-forwarder special case: the generic forwarder's signature came from the
        // inner-generic-def method (e.g. List[T].$getitem! returning T). Naive name-based
        // substitution using the wrapper's typeSubs would map List[T]'s T to the wrapper's
        // T-substitution (the whole inner type), not the inner's own T. Re-resolve the
        // signature against the concrete inner method instead.
        if (genMethod is { IsSynthesized: true, WrapperForwarderInnerMethod: { } innerGenMethod }
            && concreteOwner.TypeArguments is { Count: 1 } wrapperArgs)
        {
            TypeInfo concreteInner = wrapperArgs[0];
            RoutineInfo? concreteInnerMethod = ctx.Registry.LookupMethod(
                type: concreteInner,
                methodName: innerGenMethod.Name,
                isFailable: innerGenMethod.IsFailable);
            if (concreteInnerMethod != null)
            {
                var fwdParams = concreteInnerMethod.Parameters
                    .Select(p => p.Name == "me"
                        ? p.WithSubstitutedType(newType: concreteOwner)
                        : p)
                    .ToList();
                return new RoutineInfo(name: genMethod.Name)
                {
                    Kind = genMethod.Kind,
                    OwnerType = concreteOwner,
                    Parameters = fwdParams,
                    ReturnType = concreteInnerMethod.ReturnType,
                    IsFailable = genMethod.IsFailable,
                    DeclaredModification = genMethod.DeclaredModification,
                    ModificationCategory = genMethod.ModificationCategory,
                    Visibility = genMethod.Visibility,
                    Location = genMethod.Location,
                    Module = genMethod.Module,
                    Annotations = genMethod.Annotations,
                    CallingConvention = genMethod.CallingConvention,
                    IsVariadic = genMethod.IsVariadic,
                    IsDangerous = genMethod.IsDangerous,
                    IsSynthesized = true,
                    WrapperForwarderInnerMethod = concreteInnerMethod,
                    WrapperForwarderInnerGenericDef = genMethod.WrapperForwarderInnerGenericDef,
                    Storage = genMethod.Storage,
                    AsyncStatus = genMethod.AsyncStatus,
                    OriginalName = genMethod.OriginalName
                };
            }
        }

        var resolvedParams = genMethod.Parameters
            .Select(p =>
            {
                TypeInfo resolved = ResolveSubstitutedType(p.Type, typeSubs);
                // Final sweep: if ResolveSubstitutedType couldn't resolve a generic parameter
                // (e.g., TryGetResolution returned null for a wrapper type), fall back to a direct
                // name-based lookup in typeSubs. Post-GMP there must be no GenericParameterTypeInfo.
                if (resolved is GenericParameterTypeInfo gp &&
                    typeSubs.TryGetValue(key: gp.Name, value: out TypeInfo? directSub))
                    resolved = directSub;
                return p.WithSubstitutedType(newType: resolved);
            })
            .ToList();

        TypeInfo? resolvedReturn = genMethod.ReturnType != null
            ? ResolveSubstitutedType(genMethod.ReturnType, typeSubs)
            : null;
        if (resolvedReturn is GenericParameterTypeInfo retGp &&
            typeSubs.TryGetValue(key: retGp.Name, value: out TypeInfo? directRetSub))
            resolvedReturn = directRetSub;

        return new RoutineInfo(name: genMethod.Name)
        {
            Kind = genMethod.Kind,
            OwnerType = concreteOwner,
            Parameters = resolvedParams,
            ReturnType = resolvedReturn,
            IsFailable = genMethod.IsFailable,
            DeclaredModification = genMethod.DeclaredModification,
            ModificationCategory = genMethod.ModificationCategory,
            Visibility = genMethod.Visibility,
            Location = genMethod.Location,
            Module = genMethod.Module,
            Annotations = genMethod.Annotations,
            CallingConvention = genMethod.CallingConvention,
            IsVariadic = genMethod.IsVariadic,
            IsDangerous = genMethod.IsDangerous,
            Storage = genMethod.Storage,
            AsyncStatus = genMethod.AsyncStatus,
            OriginalName = genMethod.OriginalName
        };
    }

    /// <summary>
    /// Resolves a type by applying generic substitutions.
    /// Mirrors <c>MonomorphizationPlanner.ResolveSubstitutedType</c>.
    /// Also converts <see cref="WrapperTypeInfo"/> to the concrete <see cref="RecordTypeInfo"/>
    /// so method lookup and LLVM name mangling use the correct module-qualified type name.
    /// </summary>
    private TypeInfo ResolveSubstitutedType(TypeInfo type, Dictionary<string, TypeInfo> subs)
    {
        if (subs.TryGetValue(key: type.Name, value: out TypeInfo? sub))
            return sub;

        // WrapperTypeInfo (e.g., Hijacked[T] or Hijacked[Core.Byte]) must always be resolved
        // to the real RecordTypeInfo so LookupMethod and LLVM mangled names work correctly.
        // Use TryGetResolution (lookup-only) ??GMP must not grow AllConcreteGenericInstances.
        // Any unresolved generic parameter left in a RoutineInfo is handled at emit-time via
        // GetLLVMType consulting _typeSubstitutions.
        if (type is WrapperTypeInfo wrapper)
        {
            TypeInfo? wrapperDef = ctx.Registry.LookupType(name: wrapper.Name);
            if (wrapperDef is { IsGenericDefinition: true } &&
                wrapper.TypeArguments is { Count: > 0 })
            {
                var resolvedInnerArgs = wrapper.TypeArguments
                    .Select(a => ResolveSubstitutedType(a, subs))
                    .ToList();
                return ctx.Registry.TryGetResolution(genericDef: wrapperDef,
                    typeArguments: resolvedInnerArgs) ?? type;
            }
        }

        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            bool anySubstituted = false;
            var substitutedArgs = new List<TypeInfo>();
            foreach (TypeInfo arg in type.TypeArguments)
            {
                TypeInfo resolved = ResolveSubstitutedType(arg, subs);
                substitutedArgs.Add(resolved);
                if (!ReferenceEquals(resolved, arg)) anySubstituted = true;
            }
            if (anySubstituted)
            {
                TypeInfo? genericBase = GetGenericBase(type);
                if (genericBase != null)
                    return ctx.Registry.TryGetResolution(
                        genericDef: genericBase,
                        typeArguments: substitutedArgs) ?? type;
            }
        }

        if (type is { IsGenericDefinition: true, GenericParameters: not null } &&
            type.TypeArguments == null)
        {
            var typeArgs = type.GenericParameters
                .Select(gp => subs.TryGetValue(key: gp, value: out TypeInfo? s)
                    ? s
                    : ctx.Registry.LookupType(name: gp))
                .Where(t => t != null)
                .ToList();
            if (typeArgs.Count == type.GenericParameters.Count)
                return ctx.Registry.TryGetResolution(
                    genericDef: type,
                    typeArguments: typeArgs!) ?? type;
        }

        return type;
    }

    /// <summary>Builds the expected AST name for a routine on a generic type definition.</summary>
    private static string BuildAstName(TypeInfo genDef, string routineName)
    {
        if (genDef.GenericParameters is { Count: > 0 })
        {
            string paramList = string.Join(", ", genDef.GenericParameters);
            return $"{genDef.Name}[{paramList}].{routineName}";
        }
        return $"{genDef.Name}.{routineName}";
    }

    /// <summary>
    /// Searches all stdlib and user program ASTs for a routine declaration matching the given name.
    /// Uses a pre-built index for O(1) name lookup.
    /// When <paramref name="typeSubs"/> is provided, routines whose generic constraints are not
    /// satisfied by the concrete type arguments are skipped ??this prevents the record-layout
    /// overload of e.g. <c>Maybe[T]</c> from being selected when T is an entity type.
    /// </summary>
    private RoutineDeclaration? FindInStdlib(string genericAstName, int expectedParamCount = -1,
        Dictionary<string, TypeInfo>? typeSubs = null)
    {
        bool requireGenericSuffix = genericAstName.EndsWith("[generic]");
        string baseName = requireGenericSuffix
            ? genericAstName[..genericAstName.IndexOf("[generic]", StringComparison.Ordinal)]
            : genericAstName;

        if (!_routineIndex.TryGetValue(key: baseName, value: out List<RoutineDeclaration>? candidates))
        {
            // Failable methods are indexed under their name WITHOUT '!' (parser strips it,
            // sets IsFailable=true). When baseName ends with '!', retry without it and
            // keep only the failable overloads.
            if (!baseName.EndsWith('!') ||
                !_routineIndex.TryGetValue(key: baseName[..^1], value: out candidates))
                return null;
            candidates = candidates!.Where(d => d.IsFailable).ToList();
            if (candidates.Count == 0) return null;
        }

        RoutineDeclaration? firstMatch = null;
        foreach (RoutineDeclaration decl in candidates)
        {
            if (requireGenericSuffix && decl.GenericParameters is not { Count: > 0 }) continue;
            if (!ConstraintsSatisfied(routine: decl, subs: typeSubs)) continue;

            if (expectedParamCount >= 0 && decl.Parameters.Count != expectedParamCount)
            {
                firstMatch ??= decl;
                continue;
            }

            return decl;
        }

        return firstMatch;
    }

    /// <summary>
    /// Returns true if all explicit generic constraints on <paramref name="routine"/> are
    /// satisfied by the concrete type substitutions in <paramref name="subs"/>.
    /// Mirrors <see cref="MonomorphizationPlanner.ConstraintsSatisfied"/>.
    /// </summary>
    private static bool ConstraintsSatisfied(RoutineDeclaration routine,
        Dictionary<string, TypeInfo>? subs)
    {
        if (routine.GenericConstraints is not { Count: > 0 }) return true;
        if (subs == null || subs.Count == 0) return true;

        foreach (GenericConstraintDeclaration c in routine.GenericConstraints)
        {
            if (!subs.TryGetValue(key: c.ParameterName, value: out TypeInfo? actual)) continue;
            bool ok = c.ConstraintType switch
            {
                ConstraintKind.ValueType     => actual is RecordTypeInfo,
                ConstraintKind.ReferenceType => actual is EntityTypeInfo,
                ConstraintKind.ChoiceType    => actual is ChoiceTypeInfo,
                ConstraintKind.FlagsType     => actual is FlagsTypeInfo,
                ConstraintKind.VariantType   => actual is VariantTypeInfo,
                ConstraintKind.Crashable     => actual is CrashableTypeInfo,
                ConstraintKind.ConstGeneric  => CheckStructuralConstGeneric(c: c, actual: actual),
                _                            => true, // Obeys/TypeEquality: trust SA
            };
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// Checks a <see cref="ConstraintKind.ConstGeneric"/> constraint that may encode a structural
    /// type-category requirement. Mirrors <see cref="MonomorphizationPlanner.CheckStructuralConstGeneric"/>.
    /// </summary>
    private static bool CheckStructuralConstGeneric(GenericConstraintDeclaration c, TypeInfo actual)
    {
        string? typeName = c.ConstraintTypes is { Count: > 0 } ? c.ConstraintTypes[0].Name : null;
        return typeName switch
        {
            "RecordType"  => actual is RecordTypeInfo,
            "EntityType"  => actual is EntityTypeInfo,
            "ChoiceType"  => actual is ChoiceTypeInfo,
            "FlagsType"   => actual is FlagsTypeInfo,
            "VariantType" => actual is VariantTypeInfo,
            "Crashable"   => actual is CrashableTypeInfo,
            _             => true
        };
    }

    /// <summary>Wraps a pre-built body statement in a minimal shell RoutineDeclaration.</summary>
    private static RoutineDeclaration WrapInShellDecl(
        string name, Statement body, RoutineInfo info)
    {
        return new RoutineDeclaration(
            Name: name,
            Parameters: [],
            ReturnType: null,
            Body: body,
            Visibility: VisibilityModifier.Open,
            Annotations: [],
            Location: info.Location ?? new SourceLocation("", 0, 0, 0));
    }

    // ?€?€?€ Type helpers (no codegen dependency) ?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€?€

    private static TypeInfo? GetGenericBase(TypeInfo type) => type switch
    {
        RecordTypeInfo { GenericDefinition: { } d } => d,
        EntityTypeInfo { GenericDefinition: { } d } => d,
        ProtocolTypeInfo { GenericDefinition: { } d } => d,
        VariantTypeInfo { GenericDefinition: { } d } => d,
        _ => null
    };

    private static string? GetGenericBaseName(TypeInfo type) => GetGenericBase(type)?.Name;
}
