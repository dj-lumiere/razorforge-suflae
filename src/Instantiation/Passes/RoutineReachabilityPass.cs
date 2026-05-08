using System.Collections;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using TypeInfo = TypeModel.Types.TypeInfo;

namespace Compiler.Instantiation.Passes;

/// <summary>
/// Strategy-B: builds the live-routine set by BFS from program entry points
/// (<c>start()</c>, <c>@test</c>, <c>@bench</c>) over <c>CallExpression.ResolvedRoutine</c> and
/// <c>GenericMethodCallExpression.ResolvedRoutine</c>. Tracks per-frame type substitutions so
/// generic-def call sites (e.g. <c>me.insertion_sort(...)</c> inside <c>List[T].sort</c>) resolve
/// to the correct concrete callee (<c>List[Owned[Text]].insertion_sort</c>) for the calling
/// concrete instantiation.
/// </summary>
internal sealed class RoutineReachabilityPass(InstantiationContext ctx)
{
    private readonly HashSet<string> _live = new(comparer: StringComparer.Ordinal);
    private readonly HashSet<string> _visited = new(comparer: StringComparer.Ordinal);
    private readonly Queue<Frame> _worklist = new();

    private Dictionary<string, RoutineDeclaration> _userByName = new(comparer: StringComparer.Ordinal);
    private Dictionary<string, RoutineDeclaration> _stdlibByName = new(comparer: StringComparer.Ordinal);

    private readonly record struct Frame(RoutineInfo Routine, RoutineDeclaration Decl,
        Dictionary<string, TypeInfo> TypeSubs);

    public void Run()
    {
        BuildAstIndices();
        SeedFromEntryPoints();
        Drain();
        SeedWiredRoutinesOnLiveTypes();
        Drain();
        foreach (string key in _live) ctx.LiveRoutineKeys.Add(item: key);
    }

    /// <summary>
    /// Codegen and synthesis passes (DerivedOperatorPass, WiredRoutinePass, BuilderInfoProvider)
    /// emit wired routines for every live concrete type unconditionally — they are not driven by
    /// AST call sites. To prevent the GMP gate from stripping bodies that have downstream callers
    /// in synthesized code, force every wired routine on every live concrete type into the live set.
    /// Sibling expansion in <see cref="ExpandSyntheticSiblings"/> then handles wrapper transparency
    /// (e.g. Owned[Text].$represent → Text.$represent).
    /// </summary>
    private void SeedWiredRoutinesOnLiveTypes()
    {
        // Use unfiltered to also include types TypeLivenessPass marked dead but codegen still
        // emits (e.g. Hijacked[LocalMoment], ListEmitter[Owned[Text]] forwarders).
        List<TypeInfo> liveTypes = ctx.Registry.AllConcreteGenericInstancesUnfiltered
            .Concat(second: ctx.Registry.AllConcreteWrapperInstances)
            .ToList();
        foreach (TypeInfo type in liveTypes)
        {
            foreach (string wiredName in WiredRoutineNames)
            {
                RoutineInfo? routine = ctx.Registry.LookupMethod(type: type, methodName: wiredName);
                if (routine != null) EnqueueCallee(callee: routine);
            }
        }
    }

    private static readonly string[] WiredRoutineNames =
    {
        "$represent", "$diagnose",
        "$eq", "$ne",
        "$cmp", "$lt", "$le", "$gt", "$ge",
        "$contains", "$notcontains",
        "$hash",
        "$iter", "try_next",
    };

    private void BuildAstIndices()
    {
        foreach ((Program program, _, _) in ctx.UserPrograms)
        {
            foreach (RoutineDeclaration decl in program.Declarations.OfType<RoutineDeclaration>())
            {
                _userByName[key: decl.Name] = decl;
            }
        }

        foreach ((Program program, _, _) in ctx.Registry.StdlibPrograms)
        {
            foreach (RoutineDeclaration decl in program.Declarations.OfType<RoutineDeclaration>())
            {
                _stdlibByName.TryAdd(key: decl.Name, value: decl);
            }
        }
    }

    private void SeedFromEntryPoints()
    {
        foreach ((Program program, _, _) in ctx.UserPrograms)
        {
            foreach (RoutineDeclaration decl in program.Declarations.OfType<RoutineDeclaration>())
            {
                bool isEntry = decl.Name == "start" ||
                               decl.Annotations.Any(predicate: a => a == "test" || a == "bench");
                if (!isEntry) continue;

                RoutineInfo? info = ctx.Registry.LookupRoutineByName(name: decl.Name);
                if (info == null) continue;
                Enqueue(routine: info, decl: decl, typeSubs: new Dictionary<string, TypeInfo>());
            }
        }
    }

    private void Enqueue(RoutineInfo routine, RoutineDeclaration decl,
        Dictionary<string, TypeInfo> typeSubs)
    {
        string key = routine.RegistryKey;
        if (_live.Add(item: key))
        {
            ExpandSyntheticSiblings(callee: routine);
        }
        string visitKey = $"{key}|{string.Join(separator: ",", values: typeSubs.Select(selector: kv => $"{kv.Key}={kv.Value.FullName}"))}";
        if (!_visited.Add(item: visitKey)) return;
        _worklist.Enqueue(item: new Frame(Routine: routine, Decl: decl, TypeSubs: typeSubs));
    }

    private void Drain()
    {
        while (_worklist.Count > 0)
        {
            Frame frame = _worklist.Dequeue();
            ProcessFrame(frame: frame);
        }
    }

    private void ProcessFrame(Frame frame)
    {
        // Walk the body and collect every CallExpression / GenericMethodCallExpression.
        var calls = new List<object>();
        CollectCalls(node: frame.Decl.Body, sink: calls);

        foreach (object node in calls)
        {
            RoutineInfo? resolved = node switch
            {
                CallExpression ce => ce.ResolvedRoutine,
                GenericMethodCallExpression gce => gce.ResolvedRoutine,
                _ => null
            };
            if (resolved == null) continue;

            RoutineInfo concreteCallee = SubstituteRoutine(routine: resolved, typeSubs: frame.TypeSubs);
            EnqueueCallee(callee: concreteCallee);

            // Pure synthesized $represent / try_next / wrapper-forwarders also have call sites
            // we may need to walk later; their bodies live in VariantBodies under the generic-def
            // key and are scanned via the synthesized-AST handling below.
        }

        // Variant bodies (synthesized $represent / try_next / wrapper forwarders) for this routine —
        // walk if present, using the same typeSubs.
        if (ctx.VariantBodies.TryGetValue(key: frame.Routine.RegistryKey, out Statement? variantBody))
        {
            var variantCalls = new List<object>();
            CollectCalls(node: variantBody, sink: variantCalls);
            foreach (object node in variantCalls)
            {
                RoutineInfo? resolved = node switch
                {
                    CallExpression ce => ce.ResolvedRoutine,
                    GenericMethodCallExpression gce => gce.ResolvedRoutine,
                    _ => null
                };
                if (resolved == null) continue;
                EnqueueCallee(callee: SubstituteRoutine(routine: resolved, typeSubs: frame.TypeSubs));
            }
        }
    }

    private void EnqueueCallee(RoutineInfo callee)
    {
        // Mark live first — synthesized leafs (no AST body) still need to be in the live set
        // so GMP emits them and codegen can resolve the symbol.
        bool isFirstSeen = _live.Add(item: callee.RegistryKey);
        if (isFirstSeen)
        {
            ExpandSyntheticSiblings(callee: callee);
        }

        // Compute substitution map for the callee from owner-type generics.
        var subs = new Dictionary<string, TypeInfo>(comparer: StringComparer.Ordinal);
        if (callee.OwnerType is { } owner)
        {
            TypeInfo? genDef = owner switch
            {
                RecordTypeInfo r => r.GenericDefinition,
                EntityTypeInfo e => e.GenericDefinition,
                WrapperTypeInfo w => ctx.Registry.LookupType(name: w.Name),
                _ => null
            };
            IReadOnlyList<TypeInfo>? typeArgs = owner.TypeArguments;
            IReadOnlyList<string>? gParams = genDef?.GenericParameters;
            if (gParams != null && typeArgs != null && gParams.Count == typeArgs.Count)
            {
                for (int i = 0; i < gParams.Count; i++)
                {
                    subs[key: gParams[index: i]] = typeArgs[index: i];
                }
            }
        }

        // Find AST decl by name. Routine name forms used in stdlib: "List[T].insertion_sort",
        // "S64.$add", "show". For methods, we try the owner-base + name combinations.
        RoutineDeclaration? decl = FindDecl(callee: callee);
        if (decl == null) return; // synthesized leaf — already marked live above

        // Enqueue for body walking. Use a visit-key gate to avoid re-walking under same subs.
        string visitKey = $"{callee.RegistryKey}|{string.Join(separator: ",", values: subs.Select(selector: kv => $"{kv.Key}={kv.Value.FullName}"))}";
        if (!_visited.Add(item: visitKey)) return;
        _worklist.Enqueue(item: new Frame(Routine: callee, Decl: decl, TypeSubs: subs));
    }

    /// <summary>
    /// Codegen synthesizes related routines unconditionally for a type that has any of its methods
    /// emitted. To keep the linker happy without forcing all-or-nothing emission, when we mark a
    /// routine live we also enqueue:
    /// — comparison-op group siblings ($cmp ↔ $lt/$le/$gt/$ge, $eq ↔ $ne, $contains ↔ $notcontains)
    /// — display routines on the owner ($represent, $diagnose) — emitted unconditionally per type
    /// — wrapper transparency: same-named method on the inner T of wrapper owners
    /// </summary>
    private void ExpandSyntheticSiblings(RoutineInfo callee)
    {
        TypeInfo? owner = callee.OwnerType;
        string name = callee.Name;

        // (1) Comparison op group on same owner.
        if (owner != null)
        {
            string[]? siblings = name switch
            {
                "$cmp" => new[] { "$lt", "$le", "$gt", "$ge" },
                "$lt" or "$le" or "$gt" or "$ge" => new[] { "$cmp" },
                "$eq" => new[] { "$ne" },
                "$ne" => new[] { "$eq" },
                "$contains" => new[] { "$notcontains" },
                "$notcontains" => new[] { "$contains" },
                _ => null
            };
            if (siblings != null)
            {
                foreach (string sib in siblings)
                {
                    RoutineInfo? sibInfo = ctx.Registry.LookupMethod(type: owner, methodName: sib);
                    if (sibInfo != null) EnqueueCallee(callee: sibInfo);
                }
            }

            // (2) Display routines: codegen emits $represent/$diagnose for every owner that has any
            // method emitted. Force them live so transitive callers of show()/alert() resolve.
            if (name != "$represent" && name != "$diagnose")
            {
                RoutineInfo? rep = ctx.Registry.LookupMethod(type: owner, methodName: "$represent");
                if (rep != null) EnqueueCallee(callee: rep);
                RoutineInfo? diag = ctx.Registry.LookupMethod(type: owner, methodName: "$diagnose");
                if (diag != null) EnqueueCallee(callee: diag);
            }

            // (3) Wrapper transparency: forward to inner T's same-named method.
            if (owner is WrapperTypeInfo wrapper && wrapper.InnerType != null)
            {
                RoutineInfo? inner = ctx.Registry.LookupMethod(type: wrapper.InnerType, methodName: name);
                if (inner != null) EnqueueCallee(callee: inner);
            }
        }
    }

    private RoutineDeclaration? FindDecl(RoutineInfo callee)
    {
        // Standalone routine: try by bare name in user, then stdlib.
        if (callee.OwnerType == null)
        {
            if (_userByName.TryGetValue(key: callee.Name, value: out RoutineDeclaration? u)) return u;
            if (_stdlibByName.TryGetValue(key: callee.Name, value: out RoutineDeclaration? s)) return s;
            return null;
        }

        // Member routine: stdlib decls have names like "List[T].insertion_sort" or "S32.$add".
        TypeInfo owner = callee.OwnerType;
        TypeInfo? genDef = owner switch
        {
            RecordTypeInfo r => r.GenericDefinition,
            EntityTypeInfo e => e.GenericDefinition,
            _ => null
        };
        // Try short generic-def form first (e.g. "List[T].insertion_sort").
        if (genDef != null)
        {
            string genericKey = $"{RoutineInfo.GetTypeIdentity(type: genDef)}.{callee.Name}";
            // Stdlib decl name is the SHORT form like "List[T].insertion_sort".
            string shortKey = $"{genDef.Name}[{string.Join(separator: ", ", values: genDef.GenericParameters ?? [])}].{callee.Name}";
            if (_stdlibByName.TryGetValue(key: shortKey, value: out RoutineDeclaration? gd)) return gd;
            if (_stdlibByName.TryGetValue(key: genericKey, value: out RoutineDeclaration? gd2)) return gd2;
        }
        // Concrete owner: e.g. "S32.$add" or "Bytes.split".
        string concreteKey = $"{owner.Name}.{callee.Name}";
        if (_stdlibByName.TryGetValue(key: concreteKey, value: out RoutineDeclaration? c)) return c;
        if (_userByName.TryGetValue(key: concreteKey, value: out RoutineDeclaration? cu)) return cu;
        return null;
    }

    /// <summary>
    /// Substitutes the generic-def callee owner+typeArgs through the calling frame's typeSubs
    /// to yield a concrete callee. E.g. callee = <c>List[T].insertion_sort</c>, frame subs
    /// {T → Owned[Text]} → <c>List[Owned[Text]].insertion_sort</c>.
    /// </summary>
    private RoutineInfo SubstituteRoutine(RoutineInfo routine, Dictionary<string, TypeInfo> typeSubs)
    {
        if (typeSubs.Count == 0 || routine.OwnerType == null) return routine;

        TypeInfo owner = routine.OwnerType;
        // If owner is itself a generic param T and frame has T → ConcreteType, substitute.
        if (owner is GenericParameterTypeInfo gp && typeSubs.TryGetValue(key: gp.Name, value: out TypeInfo? concrete))
        {
            RoutineInfo? resolved = ctx.Registry.LookupMethod(type: concrete, methodName: routine.Name);
            return resolved ?? routine;
        }
        // If owner is a generic def (e.g. List[T]) referencing T from the frame, build the
        // concrete instantiation List[ConcreteT] and look up the method on it.
        IReadOnlyList<string>? gParams = owner.GenericParameters;
        if (gParams != null && gParams.Count > 0 && owner.IsGenericDefinition)
        {
            var concreteArgs = new List<TypeInfo>(capacity: gParams.Count);
            bool allResolved = true;
            foreach (string p in gParams)
            {
                if (!typeSubs.TryGetValue(key: p, value: out TypeInfo? a))
                {
                    allResolved = false;
                    break;
                }
                concreteArgs.Add(item: a);
            }
            if (allResolved)
            {
                TypeInfo concreteOwner = ctx.Registry.GetOrCreateResolution(
                    genericDef: owner, typeArguments: concreteArgs);
                RoutineInfo? resolved = ctx.Registry.LookupMethod(type: concreteOwner, methodName: routine.Name);
                if (resolved != null) return resolved;
            }
        }
        return routine;
    }

    /// <summary>
    /// Reflectively walks an AST node tree and collects every <see cref="CallExpression"/> and
    /// <see cref="GenericMethodCallExpression"/> encountered. Robust to AST schema changes — any
    /// property of a record type that holds Statements/Expressions (or lists thereof) is traversed.
    /// </summary>
    private static void CollectCalls(object? node, List<object> sink)
    {
        if (node == null) return;
        if (node is CallExpression || node is GenericMethodCallExpression) sink.Add(item: node);

        Type t = node.GetType();
        if (t.IsPrimitive || node is string || t.IsEnum) return;

        foreach (System.Reflection.PropertyInfo prop in t.GetProperties(
            bindingAttr: System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            object? value;
            try { value = prop.GetValue(obj: node); }
            catch { continue; }
            if (value == null) continue;

            if (value is Expression || value is Statement || value is SyntaxTree.Declaration)
            {
                CollectCalls(node: value, sink: sink);
            }
            else if (value is IEnumerable enumerable && value is not string)
            {
                foreach (object? item in enumerable)
                {
                    if (item is Expression || item is Statement || item is SyntaxTree.Declaration)
                    {
                        CollectCalls(node: item, sink: sink);
                    }
                }
            }
        }
    }
}
