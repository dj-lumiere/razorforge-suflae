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
    private readonly HashSet<TypeInfo> _liveOwnerTypes =
        new(comparer: ReferenceEqualityComparer.Instance);

    private Dictionary<string, List<RoutineDeclaration>> _userByName = new(comparer: StringComparer.Ordinal);
    private Dictionary<string, List<RoutineDeclaration>> _stdlibByName = new(comparer: StringComparer.Ordinal);

    // Per-frame local variable type map (params + var decls). Used by ResolveMemberCall and
    // ResolveCallStyleConstructor to recover receiver types in stdlib bodies where SA didn't
    // populate ResolvedType on identifier expressions.
    private Dictionary<string, TypeInfo> _localTypes = new(comparer: StringComparer.Ordinal);
    private TypeInfo? _meType;

    private readonly record struct Frame(RoutineInfo Routine, RoutineDeclaration Decl,
        Dictionary<string, TypeInfo> TypeSubs);

    public void Run()
    {
        BuildAstIndices();
        SeedFromEntryPoints();
        Drain();
        // Loop until fixed-point: every time we drain we may discover new owner types
        // (e.g. Bool first reached during synthesized-body walks of Tuple[S8, Bool].$hash).
        // The wired-routine seed must rerun on those new owners so their $eq/$hash/etc.
        // become live.
        int prevOwnerCount;
        do
        {
            prevOwnerCount = _liveOwnerTypes.Count;
            SeedWiredRoutinesOnLiveTypes();
            Drain();
        } while (_liveOwnerTypes.Count > prevOwnerCount);
        foreach (string key in _live) ctx.LiveRoutineKeys.Add(item: key);
        foreach (TypeInfo owner in _liveOwnerTypes) ctx.LiveOwnerTypeNames.Add(item: owner.FullName);

        string dumpPath = System.Environment.GetEnvironmentVariable(variable: "RF_REACHABILITY_DUMP");
        if (!string.IsNullOrEmpty(value: dumpPath))
        {
            var lines = new List<string> { "=== LIVE ROUTINES ===" };
            lines.AddRange(collection: _live.OrderBy(keySelector: s => s));
            lines.Add(item: "=== LIVE OWNER TYPES ===");
            lines.AddRange(collection: _liveOwnerTypes.Select(selector: t => t.FullName).OrderBy(keySelector: s => s));
            System.IO.File.WriteAllLines(path: dumpPath, contents: lines);
        }
    }

    /// <summary>
    /// Codegen and synthesis passes (DerivedOperatorPass, WiredRoutinePass, BuilderInfoProvider)
    /// emit wired routines for every live concrete type unconditionally — they are not driven by
    /// AST call sites. To prevent the GMP gate from stripping bodies that have downstream callers
    /// in synthesized code, force every wired routine on every live concrete type into the live set.
    /// Sibling expansion in <see cref="ExpandSyntheticSiblings"/> then handles wrapper transparency
    /// (e.g. Owned[Text].$represent -> Text.$represent).
    /// </summary>
    private void SeedWiredRoutinesOnLiveTypes()
    {
        // Snapshot — EnqueueCallee mutates _liveOwnerTypes when it marks new owners live.
        TypeInfo[] snapshot = _liveOwnerTypes.ToArray();
        foreach (TypeInfo type in snapshot)
        {
            foreach (string wiredName in WiredRoutineNames)
            {
                RoutineInfo? routine = ctx.Registry.LookupMethod(type: type, methodName: wiredName);
                if (routine != null) EnqueueCallee(callee: routine);
            }
        }
    }

    // Closed set of overloadable wired routines per RazorForge-Wiki/docs/Operators.md
    // (operator -> wired-routine table). Operator-lowering may leave ResolvedRoutine = null on
    // stdlib bodies whose receivers lack ResolvedType (e.g. CStr.$create's UTF-8 encoder uses
    // cp & 0x3F, cp >> 6) — seeding these names per live owner backstops that.
    // Excluded: $create/$destroy (driven by CreatorExpression / scope), and $getitem!/$setitem!
    // (driven by index syntax) — those have dedicated reachability paths.
    private static readonly string[] WiredRoutineNames =
    {
        // Display / hash
        "$represent", "$diagnose", "$hash", "$secure_hash",
        // Equality / comparison
        "$eq", "$ne",
        "$cmp", "$lt", "$le", "$gt", "$ge",
        // Containment
        "$contains", "$notcontains",
        // Iteration
        "$iter", "$next!", "try_next",
        // Arithmetic — standard
        "$add", "$sub", "$mul", "$truediv", "$floordiv", "$mod", "$pow", "$neg",
        // Arithmetic — wrapping
        "$add_wrap", "$sub_wrap", "$mul_wrap", "$pow_wrap",
        // Arithmetic — clamping
        "$add_clamp", "$sub_clamp", "$mul_clamp", "$truediv_clamp", "$pow_clamp",
        // Bitwise
        "$bitand", "$bitor", "$bitxor", "$bitnot",
        // Shift
        "$ashl", "$ashr", "$lshl", "$lshr",
        // In-place arithmetic
        "$iadd", "$isub", "$imul", "$itruediv", "$ifloordiv", "$imod", "$ipow",
        // In-place bitwise
        "$ibitand", "$ibitor", "$ibitxor",
        // In-place shift
        "$iashl", "$iashr", "$ilshl", "$ilshr",
        // Unwrap (Maybe / Result / Lookup)
        "$unwrap", "$unwrap!", "$unwrap_or",
    };

    private void BuildAstIndices()
    {
        foreach ((Program program, _, _) in ctx.UserPrograms)
        {
            foreach (RoutineDeclaration decl in program.Declarations.OfType<RoutineDeclaration>())
            {
                AddDecl(map: _userByName, name: decl.Name, decl: decl);
            }
        }

        foreach ((Program program, _, _) in ctx.Registry.StdlibPrograms)
        {
            foreach (RoutineDeclaration decl in program.Declarations.OfType<RoutineDeclaration>())
            {
                AddDecl(map: _stdlibByName, name: decl.Name, decl: decl);
            }
        }
    }

    private static void AddDecl(Dictionary<string, List<RoutineDeclaration>> map, string name,
        RoutineDeclaration decl)
    {
        if (!map.TryGetValue(key: name, value: out List<RoutineDeclaration>? list))
        {
            list = new List<RoutineDeclaration>();
            map[key: name] = list;
        }
        list.Add(item: decl);
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
        if (routine.OwnerType is { } owner) _liveOwnerTypes.Add(item: owner);
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
        // Set up per-frame inference scope: parameter types + receiver type for `me`.
        _localTypes = BuildLocalTypes(decl: frame.Decl, typeSubs: frame.TypeSubs);
        _meType = frame.Routine.OwnerType;

        // Walk the body and collect every CallExpression / GenericMethodCallExpression.
        var calls = new List<object>();
        CollectCalls(node: frame.Decl.Body, sink: calls);

        // Pull var-decl types into _localTypes by walking the body once.
        CollectLocalVarTypes(node: frame.Decl.Body, map: _localTypes);

        foreach (object node in calls)
        {
            if (node is ThrowStatement throwStmt)
            {
                EnqueueThrowCrashMessage(throwStmt: throwStmt, typeSubs: frame.TypeSubs);
                continue;
            }
            RoutineInfo? resolved = node switch
            {
                CallExpression ce => ce.ResolvedRoutine ?? ResolveNoArgConstructor(ce: ce) ?? ResolveCallStyleConstructor(ce: ce) ?? ResolveMemberCall(ce: ce),
                GenericMethodCallExpression gce => gce.ResolvedRoutine,
                CreatorExpression cre => ResolveCreatorRoutine(cre: cre),
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
                if (node is ThrowStatement throwStmt)
                {
                    EnqueueThrowCrashMessage(throwStmt: throwStmt, typeSubs: frame.TypeSubs);
                    continue;
                }
                RoutineInfo? resolved = node switch
                {
                    CallExpression ce => ce.ResolvedRoutine ?? ResolveNoArgConstructor(ce: ce) ?? ResolveCallStyleConstructor(ce: ce) ?? ResolveMemberCall(ce: ce),
                    GenericMethodCallExpression gce => gce.ResolvedRoutine,
                    CreatorExpression cre => ResolveCreatorRoutine(cre: cre),
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
        if (callee.OwnerType is { } liveOwner) _liveOwnerTypes.Add(item: liveOwner);
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
        if (decl == null)
        {
            // No source AST — but the routine may have a synthesized body in VariantBodies
            // (e.g. Tuple[S8, Bool].$hash, generated record/entity $eq/$cmp/$hash). Walk that
            // body directly so its calls (S8.$hash, Bool.$hash, ...) reach the live set.
            // Without this, synthesized routines are leaf nodes in the BFS and their callees
            // never become live -> linker errors.
            //
            // For concrete generic instantiations (e.g. Range[U64].$iter), the synthesized body
            // is keyed under the generic-def routine (Range[T].$iter). Fall back to that key and
            // walk with the substitution map so calls like me.is_ascending() resolve to the
            // concrete Range[U64].is_ascending and become live.
            if (!ctx.VariantBodies.TryGetValue(key: callee.RegistryKey, out Statement? synthBody))
            {
                RoutineInfo? genericDefRoutine = ResolveGenericDefRoutine(callee: callee);
                if (genericDefRoutine != null)
                    ctx.VariantBodies.TryGetValue(key: genericDefRoutine.RegistryKey, out synthBody);
            }
            if (synthBody != null)
            {
                string synthVisitKey = $"{callee.RegistryKey}|{string.Join(separator: ",", values: subs.Select(selector: kv => $"{kv.Key}={kv.Value.FullName}"))}";
                if (_visited.Add(item: synthVisitKey))
                {
                    var synthCalls = new List<object>();
                    CollectCalls(node: synthBody, sink: synthCalls);
                    foreach (object node in synthCalls)
                    {
                        if (node is ThrowStatement throwStmt)
                        {
                            EnqueueThrowCrashMessage(throwStmt: throwStmt, typeSubs: subs);
                            continue;
                        }
                        RoutineInfo? resolved = node switch
                        {
                            CallExpression ce => ce.ResolvedRoutine ?? ResolveNoArgConstructor(ce: ce) ?? ResolveCallStyleConstructor(ce: ce) ?? ResolveMemberCall(ce: ce),
                            GenericMethodCallExpression gce => gce.ResolvedRoutine,
                            CreatorExpression cre => ResolveCreatorRoutine(cre: cre),
                            _ => null
                        };
                        if (resolved == null) continue;
                        EnqueueCallee(callee: SubstituteRoutine(routine: resolved, typeSubs: subs));
                    }
                }
            }
            return;
        }

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

    /// <summary>
    /// Maps a concrete-owner routine (e.g. <c>Range[U64].$iter</c>) to its generic-def
    /// counterpart (<c>Range[T].$iter</c>). Returns null if the owner isn't a generic
    /// instantiation or the method isn't found on the def.
    /// </summary>
    private RoutineInfo? ResolveCreatorRoutine(CreatorExpression cre)
    {
        TypeInfo? ct = cre.ConstructedType;
        if (ct == null) return null;
        // Match overload by parameter count — Text() (no args) and Text(from: CStr) are
        // distinct $create overloads on the same type. LookupMethod alone returns the
        // first-registered one and misses the no-arg variant when callers use it.
        int argCount = cre.MemberVariables.Count;
        bool found = false;
        foreach (RoutineInfo m in ctx.Registry.GetMethodsForType(type: ct))
        {
            if (m.Name == "$create" && m.Parameters.Count == argCount)
            {
                EnqueueCallee(callee: m);
                found = true;
            }
        }
        TypeInfo? genDef = ct switch
        {
            RecordTypeInfo r => r.GenericDefinition,
            EntityTypeInfo e => e.GenericDefinition,
            _ => null
        };
        if (genDef != null && !ReferenceEquals(objA: genDef, objB: ct))
        {
            foreach (RoutineInfo m in ctx.Registry.GetMethodsForType(type: genDef))
            {
                if (m.Name == "$create" && m.Parameters.Count == argCount)
                {
                    EnqueueCallee(callee: m);
                    found = true;
                }
            }
        }
        if (!found && argCount == 0)
        {
            // Fallback for no-arg constructors that codegen calls by mangled name
            // <Type>.$create even when no explicit RoutineInfo exists.
            _live.Add(item: $"{ct.FullName}.$create");
        }
        return ctx.Registry.LookupMethod(type: ct, methodName: "$create");
    }

    /// <summary>
    /// SA leaves <c>CallExpression.ResolvedRoutine</c> null for no-arg type-creator calls (e.g.
    /// <c>Text()</c>) — only <c>ConstructedType</c> is set. Codegen emits a call to
    /// <c>&lt;Type&gt;.$create</c> by mangled name, so we must mark that key live ourselves.
    /// </summary>
    private RoutineInfo? ResolveNoArgConstructor(CallExpression ce)
    {
        if (ce.Arguments.Count != 0) return null;
        TypeInfo? ct = ce.ConstructedType;
        if (ct == null) return null;
        foreach (RoutineInfo m in ctx.Registry.GetMethodsForType(type: ct))
        {
            if (m.Name == "$create" && m.Parameters.Count == 0) return m;
        }
        // Method-chain constructor: text.S32!() lowers to S32.$create(receiver). The call
        // has zero positional arguments but the member-receiver is the conversion source.
        // Match the $create overload whose single parameter accepts the receiver type so
        // reachability marks the failable Text overload (not the first-registered S8 one).
        if (ce.Callee is MemberExpression chainMember)
        {
            TypeInfo? receiverType = chainMember.Object.ResolvedType
                ?? InferExpressionType(e: chainMember.Object);
            if (receiverType != null)
            {
                foreach (RoutineInfo m in ctx.Registry.GetMethodsForType(type: ct))
                {
                    if (m.Name != "$create" || m.Parameters.Count != 1) continue;
                    if (m.Parameters[index: 0].Type?.Name == receiverType.Name) return m;
                }
            }
        }
        // Fallback: codegen mangles by FullName regardless of registration.
        _live.Add(item: $"{ct.FullName}.$create");
        return null;
    }

    /// <summary>
    /// OperatorLoweringPass produces <c>receiver.$op(you: arg)</c> CallExpressions but leaves
    /// <c>ResolvedRoutine = null</c> when the method couldn't be resolved at lowering time
    /// (e.g. stdlib bodies whose receiver lacks <c>ResolvedType</c>). Retry the lookup here
    /// using the receiver's resolved type so primitive operators like <c>U32.$bitand</c>
    /// reachable through stdlib bodies (<c>CStr.$create</c>) become live.
    /// </summary>
    private RoutineInfo? ResolveMemberCall(CallExpression ce)
    {
        if (ce.Callee is not MemberExpression member) return null;
        TypeInfo? receiverType = member.Object.ResolvedType ?? InferExpressionType(e: member.Object);
        if (receiverType == null) return null;
        return ctx.Registry.LookupMethod(type: receiverType, methodName: member.PropertyName);
    }

    /// <summary>
    /// Handles call-style constructor invocations like <c>Byte(U8(cp))</c> — a CallExpression
    /// whose Callee is an IdentifierExpression naming a type, with no ResolvedRoutine. Codegen
    /// emits a call to <c>&lt;Type&gt;.$create(...)</c> by mangled name; mark the matching
    /// $create overload live by parameter count.
    /// </summary>
    private RoutineInfo? ResolveCallStyleConstructor(CallExpression ce)
    {
        if (ce.Arguments.Count == 0) return null; // ResolveNoArgConstructor handles those
        TypeInfo? ct = ce.ConstructedType;
        if (ct == null && ce.Callee is IdentifierExpression idCallee)
        {
            ct = ctx.Registry.LookupType(name: idCallee.Name);
        }
        if (ct == null) return null;
        int argCount = ce.Arguments.Count;

        // Infer arg types so we can disambiguate overloads like Byte.$create(Byte)
        // vs Byte.$create(U8).
        var argTypes = new TypeInfo?[argCount];
        for (int i = 0; i < argCount; i++)
            argTypes[i] = InferExpressionType(e: ce.Arguments[index: i]);

        RoutineInfo? PickOverload(IEnumerable<RoutineInfo> methods)
        {
            RoutineInfo? countOnly = null;
            foreach (RoutineInfo m in methods)
            {
                if (m.Name != "$create" || m.Parameters.Count != argCount) continue;
                countOnly ??= m;
                bool typesMatch = true;
                for (int i = 0; i < argCount; i++)
                {
                    TypeInfo? at = argTypes[i];
                    if (at == null) continue; // unknown — accept
                    if (m.Parameters[index: i].Type?.Name != at.Name)
                    { typesMatch = false; break; }
                }
                if (typesMatch) return m;
            }
            return countOnly;
        }

        RoutineInfo? match = PickOverload(methods: ctx.Registry.GetMethodsForType(type: ct));
        if (match == null)
        {
            TypeInfo? genDef = ct switch
            {
                RecordTypeInfo r => r.GenericDefinition,
                EntityTypeInfo e => e.GenericDefinition,
                _ => null
            };
            if (genDef != null && !ReferenceEquals(objA: genDef, objB: ct))
                match = PickOverload(methods: ctx.Registry.GetMethodsForType(type: genDef));
        }

        // Direct named-field construction has no `$create` routine — codegen synthesizes a struct
        // literal in `EmitRecordConstruction`. Without a routine to enqueue, reachability would
        // never mark the constructed type as a live owner, blocking `SeedWiredRoutinesOnLiveTypes`
        // from seeding its derived operators ($ne/$lt/...). Add the type to the live-owner set
        // directly when we recognise this construction pattern. Affects user records like Point
        // that obey Equatable/Comparable and rely on synthesised $ne/$lt/$le/$gt/$ge bodies.
        if (match == null && ct is RecordTypeInfo or EntityTypeInfo)
        {
            _liveOwnerTypes.Add(item: ct);
        }
        return match;
    }

    /// <summary>
    /// Infers an expression's type using a local-variable map plus chained method-call return
    /// types. Backstops <see cref="Expression.ResolvedType"/> for stdlib bodies where SA leaves
    /// receiver types unset (e.g. <c>buffer.offset(write_pos.bytes()).inject(...)</c>).
    /// </summary>
    private TypeInfo? InferExpressionType(Expression e)
    {
        if (e.ResolvedType != null) return e.ResolvedType;
        switch (e)
        {
            case IdentifierExpression id:
                if (id.Name == "me") return _meType;
                if (_localTypes.TryGetValue(key: id.Name, value: out TypeInfo? t)) return t;
                // Treat bare type identifier as the type itself (for `Byte(...)` callee resolution).
                return ctx.Registry.LookupType(name: id.Name);
            case CallExpression ce:
                if (ce.ResolvedRoutine?.ReturnType != null) return ce.ResolvedRoutine.ReturnType;
                if (ce.ConstructedType != null) return ce.ConstructedType;
                if (ce.Callee is MemberExpression mem)
                {
                    TypeInfo? recv = InferExpressionType(e: mem.Object);
                    if (recv == null) return null;
                    RoutineInfo? mm = ctx.Registry.LookupMethod(type: recv, methodName: mem.PropertyName);
                    return mm?.ReturnType;
                }
                if (ce.Callee is IdentifierExpression idC)
                    return ctx.Registry.LookupType(name: idC.Name);
                return null;
        }
        return null;
    }

    /// <summary>
    /// Builds the parameter portion of the local-type map. Var-decl types are added later by
    /// <see cref="CollectLocalVarTypes"/> after a full body walk.
    /// </summary>
    private Dictionary<string, TypeInfo> BuildLocalTypes(RoutineDeclaration decl,
        Dictionary<string, TypeInfo> typeSubs)
    {
        var map = new Dictionary<string, TypeInfo>(comparer: StringComparer.Ordinal);
        foreach (Parameter p in decl.Parameters)
        {
            if (p.Type == null) continue;
            TypeInfo? t = ResolveTypeExpression(typeExpr: p.Type, typeSubs: typeSubs);
            if (t != null) map[key: p.Name] = t;
        }
        return map;
    }

    /// <summary>
    /// Walks the body and registers <c>var x = ...</c> bindings into the local-type map. Uses
    /// the explicit type annotation when present, falling back to inferring from the initializer.
    /// </summary>
    private void CollectLocalVarTypes(object? node, Dictionary<string, TypeInfo> map)
    {
        if (node == null) return;
        if (node is VariableDeclaration vd)
        {
            TypeInfo? t = vd.Type != null
                ? ResolveTypeExpression(typeExpr: vd.Type, typeSubs: null)
                : (vd.Initializer != null ? InferExpressionType(e: vd.Initializer) : null);
            if (t != null) map[key: vd.Name] = t;
        }
        Type nt = node.GetType();
        if (nt.IsPrimitive || node is string || nt.IsEnum) return;

        if (node is IEnumerable e2 && node is not string)
        {
            foreach (object? item in e2)
                if (item != null) CollectLocalVarTypes(node: item, map: map);
            return;
        }

        foreach (System.Reflection.PropertyInfo prop in nt.GetProperties(
            bindingAttr: System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            object? val;
            try { val = prop.GetValue(obj: node); } catch { continue; }
            if (val == null) continue;
            if (val is Expression || val is Statement || val is SyntaxTree.Declaration)
                CollectLocalVarTypes(node: val, map: map);
            else if (val is IEnumerable list && val is not string)
            {
                foreach (object? item in list)
                    if (item != null) CollectLocalVarTypes(node: item, map: map);
            }
        }
    }

    /// <summary>
    /// Resolves a TypeExpression to a TypeInfo via the registry. Honors generic arguments by
    /// recursively resolving and looking up the instantiation. Generic-param substitutions are
    /// applied first when <paramref name="typeSubs"/> is non-null.
    /// </summary>
    private TypeInfo? ResolveTypeExpression(TypeExpression typeExpr,
        Dictionary<string, TypeInfo>? typeSubs)
    {
        if (typeExpr.ResolvedType != null) return typeExpr.ResolvedType;
        if (typeSubs != null && typeSubs.TryGetValue(key: typeExpr.Name, value: out TypeInfo? sub))
            return sub;
        return ctx.Registry.LookupType(name: typeExpr.Name);
    }

    /// <summary>
    /// EmitThrow in codegen calls <c>errorType.crash_message</c> directly to format the panic
    /// message — there is no source-AST CallExpression to drive reachability. Mark it live here
    /// for every throw site we walk.
    /// </summary>
    private void EnqueueThrowCrashMessage(ThrowStatement throwStmt, Dictionary<string, TypeInfo> typeSubs)
    {
        TypeInfo? errorType = throwStmt.Error.ResolvedType
            ?? (throwStmt.Error is CreatorExpression cre ? cre.ConstructedType : null);
        if (errorType == null) return;
        RoutineInfo? crashMsg = ctx.Registry.LookupMethod(type: errorType, methodName: "crash_message");
        if (crashMsg != null) EnqueueCallee(callee: crashMsg);
    }

    private RoutineInfo? ResolveGenericDefRoutine(RoutineInfo callee)
    {
        TypeInfo? owner = callee.OwnerType;
        if (owner == null) return null;
        TypeInfo? genDef = owner switch
        {
            RecordTypeInfo r => r.GenericDefinition,
            EntityTypeInfo e => e.GenericDefinition,
            WrapperTypeInfo w => ctx.Registry.LookupType(name: w.Name),
            _ => null
        };
        if (genDef == null || ReferenceEquals(objA: genDef, objB: owner)) return null;
        return ctx.Registry.LookupMethod(type: genDef, methodName: callee.Name);
    }

    private RoutineDeclaration? FindDecl(RoutineInfo callee)
    {
        // Standalone routine: try by bare name in user, then stdlib.
        if (callee.OwnerType == null)
        {
            if (_userByName.TryGetValue(key: callee.Name, value: out List<RoutineDeclaration>? u))
                return MatchOverload(decls: u, callee: callee);
            if (_stdlibByName.TryGetValue(key: callee.Name, value: out List<RoutineDeclaration>? s))
                return MatchOverload(decls: s, callee: callee);
            return null;
        }

        // Member routine: stdlib decls have names like "List[T].insertion_sort" or "S32.$add".
        TypeInfo owner = callee.OwnerType;
        TypeInfo? genDef = owner switch
        {
            RecordTypeInfo r => r.GenericDefinition,
            EntityTypeInfo e => e.GenericDefinition,
            WrapperTypeInfo w => ctx.Registry.LookupType(name: w.Name),
            _ => null
        };
        // Try short generic-def form first (e.g. "List[T].insertion_sort").
        if (genDef != null)
        {
            string genericKey = $"{RoutineInfo.GetTypeIdentity(type: genDef)}.{callee.Name}";
            // Stdlib decl name is the SHORT form like "List[T].insertion_sort".
            string shortKey = $"{genDef.Name}[{string.Join(separator: ", ", values: genDef.GenericParameters ?? [])}].{callee.Name}";
            if (_stdlibByName.TryGetValue(key: shortKey, value: out List<RoutineDeclaration>? gd))
                return MatchOverload(decls: gd, callee: callee);
            if (_stdlibByName.TryGetValue(key: genericKey, value: out List<RoutineDeclaration>? gd2))
                return MatchOverload(decls: gd2, callee: callee);
        }
        // Concrete owner: e.g. "S32.$add" or "Bytes.split".
        string concreteKey = $"{owner.Name}.{callee.Name}";
        if (_stdlibByName.TryGetValue(key: concreteKey, value: out List<RoutineDeclaration>? c))
            return MatchOverload(decls: c, callee: callee);
        if (_userByName.TryGetValue(key: concreteKey, value: out List<RoutineDeclaration>? cu))
            return MatchOverload(decls: cu, callee: callee);
        return null;
    }

    /// <summary>
    /// Picks the overload whose AST parameter list matches the callee's signature. Falls back
    /// to count-only match, then the first decl, so single-overload lookups stay correct.
    /// </summary>
    private static RoutineDeclaration? MatchOverload(List<RoutineDeclaration> decls, RoutineInfo callee)
    {
        if (decls.Count == 1) return decls[index: 0];
        int paramCount = callee.Parameters.Count;
        // Pass 1: match by param count + serialized param type signatures.
        foreach (RoutineDeclaration d in decls)
        {
            if (d.Parameters.Count != paramCount) continue;
            bool typesMatch = true;
            for (int i = 0; i < paramCount; i++)
            {
                string declSig = TypeExpressionSig(typeExpr: d.Parameters[index: i].Type);
                string calleeSig = callee.Parameters[index: i].Type?.Name ?? "";
                if (declSig != calleeSig) { typesMatch = false; break; }
            }
            if (typesMatch) return d;
        }
        // Pass 2: count-only.
        foreach (RoutineDeclaration d in decls)
        {
            if (d.Parameters.Count == paramCount) return d;
        }
        return decls[index: 0];
    }

    /// <summary>
    /// Serializes a TypeExpression to the same form TypeInfo.Name uses for generic instances:
    /// <c>Name[Arg1, Arg2]</c>. Used by overload matching to compare against callee parameter
    /// type names which include their generic args inline.
    /// </summary>
    private static string TypeExpressionSig(TypeExpression? typeExpr)
    {
        if (typeExpr == null) return "";
        if (typeExpr.GenericArguments is not { Count: > 0 }) return typeExpr.Name;
        var args = string.Join(separator: ", ",
            values: typeExpr.GenericArguments.Select(selector: TypeExpressionSig));
        return $"{typeExpr.Name}[{args}]";
    }

    /// <summary>
    /// Substitutes the generic-def callee owner+typeArgs through the calling frame's typeSubs
    /// to yield a concrete callee. E.g. callee = <c>List[T].insertion_sort</c>, frame subs
    /// {T -> Owned[Text]} -> <c>List[Owned[Text]].insertion_sort</c>.
    /// </summary>
    private RoutineInfo SubstituteRoutine(RoutineInfo routine, Dictionary<string, TypeInfo> typeSubs)
    {
        if (typeSubs.Count == 0) return routine;

        // Standalone generic routine (e.g. hijacked_from[T]). Build the concrete instantiation
        // and add its registry key directly to _live, so GMP picks it up via LiveRoutineKeys.
        if (routine.OwnerType == null)
        {
            // Case A: pure generic def — substitute by GenericParameters.
            if (routine.IsGenericDefinition)
            {
                IReadOnlyList<string>? rgParams = routine.GenericParameters;
                if (rgParams != null)
                {
                    var concreteTypeArgs = new List<TypeInfo>(capacity: rgParams.Count);
                    bool allOk = true;
                    foreach (string p in rgParams)
                    {
                        if (typeSubs.TryGetValue(key: p, value: out TypeInfo? sub))
                            concreteTypeArgs.Add(item: sub);
                        else { allOk = false; break; }
                    }
                    if (allOk)
                    {
                        RoutineInfo resolved = ctx.Registry.GetOrCreateRoutineResolution(
                            genericDef: routine, typeArguments: concreteTypeArgs);
                        _live.Add(item: resolved.RegistryKey);
                        return resolved;
                    }
                }
            }
            // Case B: SA produced a "resolved" instance whose TypeArguments are still
            // GenericParameterTypeInfo (e.g. hijacked_from[T] where T comes from the enclosing
            // List[T]). Substitute those through the frame.
            if (routine.TypeArguments is { Count: > 0 } tArgs
                && tArgs.Any(predicate: t => t is GenericParameterTypeInfo))
            {
                var substArgs = new List<TypeInfo>(capacity: tArgs.Count);
                bool allOk = true;
                foreach (TypeInfo a in tArgs)
                {
                    if (a is GenericParameterTypeInfo gpa)
                    {
                        if (typeSubs.TryGetValue(key: gpa.Name, value: out TypeInfo? sub))
                            substArgs.Add(item: sub);
                        else { allOk = false; break; }
                    }
                    else
                    {
                        substArgs.Add(item: a);
                    }
                }
                if (allOk)
                {
                    RoutineInfo? genDef = routine.GenericDefinition ?? routine;
                    RoutineInfo resolved = ctx.Registry.GetOrCreateRoutineResolution(
                        genericDef: genDef, typeArguments: substArgs);
                    _live.Add(item: resolved.RegistryKey);
                    return resolved;
                }
            }
            return routine;
        }

        TypeInfo owner = routine.OwnerType;

        // Owner like ListEmitter[T] or Hijacked[BTreeSetNode[T]] — stored as a resolution whose
        // TypeArguments contain GenericParameterTypeInfo, possibly nested inside another generic
        // resolution. Substitute the params (recursively, via RoutineInfo.SubstituteType) to get
        // a concrete owner.
        if (owner.TypeArguments is { Count: > 0 } ownerTArgs
            && ownerTArgs.Any(predicate: t => t is GenericParameterTypeInfo))
        {
            TypeInfo? ownerGenDef = owner switch
            {
                RecordTypeInfo r => r.GenericDefinition,
                EntityTypeInfo e => e.GenericDefinition,
                WrapperTypeInfo w => ctx.Registry.LookupType(name: w.Name),
                _ => null
            };
            if (ownerGenDef != null)
            {
                var substArgs = new List<TypeInfo>(capacity: ownerTArgs.Count);
                bool allOk = true;
                foreach (TypeInfo arg in ownerTArgs)
                {
                    if (arg is GenericParameterTypeInfo paramArg)
                    {
                        if (typeSubs.TryGetValue(key: paramArg.Name, value: out TypeInfo? sub))
                            substArgs.Add(item: sub);
                        else { allOk = false; break; }
                    }
                    else
                    {
                        substArgs.Add(item: arg);
                    }
                }
                if (allOk)
                {
                    TypeInfo concreteOwner = ctx.Registry.GetOrCreateResolution(
                        genericDef: ownerGenDef, typeArguments: substArgs);
                    _liveOwnerTypes.Add(item: concreteOwner);
                    RoutineInfo? resolved = ctx.Registry.LookupMethod(type: concreteOwner, methodName: routine.Name);
                    if (resolved != null) return TransferSubstitutedTypeArguments(input: routine, resolved: resolved, typeSubs: typeSubs);
                    // Fallback: synthesized routine, mark substituted RegistryKey live
                    var synthSubs = new Dictionary<string, TypeInfo>(comparer: StringComparer.Ordinal);
                    IReadOnlyList<string>? defParams = ownerGenDef.GenericParameters;
                    if (defParams != null)
                    {
                        for (int i = 0; i < defParams.Count && i < substArgs.Count; i++)
                            synthSubs[key: defParams[index: i]] = substArgs[index: i];
                    }
                    string concreteKey = ComputeConcreteRegistryKey(routine: routine, concreteOwner: concreteOwner, subs: synthSubs);
                    _live.Add(item: concreteKey);
                }
            }
        }
        // If owner is itself a generic param T and frame has T -> ConcreteType, substitute.
        if (owner is GenericParameterTypeInfo gp && typeSubs.TryGetValue(key: gp.Name, value: out TypeInfo? concrete))
        {
            RoutineInfo? resolved = ctx.Registry.LookupMethod(type: concrete, methodName: routine.Name);
            return resolved != null
                ? TransferSubstitutedTypeArguments(input: routine, resolved: resolved, typeSubs: typeSubs)
                : routine;
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
                _liveOwnerTypes.Add(item: concreteOwner);
                RoutineInfo? resolved = ctx.Registry.LookupMethod(type: concreteOwner, methodName: routine.Name);
                if (resolved != null) return TransferSubstitutedTypeArguments(input: routine, resolved: resolved, typeSubs: typeSubs);
                // Synthesized routines (try_next, $represent, $diagnose, wrapper forwarders) live
                // only on the generic-def. Manually mark the substituted RegistryKey live so the
                // codegen Phase B/C gates emit the concrete-form symbol that callers reference.
                string concreteKey = ComputeConcreteRegistryKey(routine: routine, concreteOwner: concreteOwner, subs: typeSubs);
                _live.Add(item: concreteKey);
            }
        }
        return routine;
    }

    private static bool ContainsAnyGenericParameter(TypeInfo type)
    {
        if (type is GenericParameterTypeInfo) return true;
        if (type.TypeArguments is { Count: > 0 } args)
        {
            foreach (TypeInfo a in args)
                if (ContainsAnyGenericParameter(type: a)) return true;
        }
        return false;
    }

    /// <summary>
    /// Propagates the substituted method-level <see cref="RoutineInfo.TypeArguments"/> from the
    /// caller's input routine (post-typeSubs) onto the routine returned by
    /// <c>LookupMethod</c>(concreteOwner). The lookup path goes through
    /// <c>SubstituteMethodForOwner</c>, which preserves <c>method.TypeArguments</c> from the
    /// registered def — that is null/empty for stdlib defs, so the resulting RegistryKey omits
    /// the method-level type args (e.g. <c>Hijacked[S64].recast_as</c> instead of
    /// <c>Hijacked[S64].recast_as[S64]</c>). Without this fix, definition emission and call-site
    /// references diverge -> linker error.
    ///
    /// We take the input routine's TypeArguments (which carry the SA-resolved method-level
    /// bindings), substitute them through <paramref name="typeSubs"/>, and graft them onto
    /// <paramref name="resolved"/> as a fresh clone. Returns <paramref name="resolved"/>
    /// unchanged when there is nothing to graft or when substitution leaves a generic parameter
    /// behind.
    /// </summary>
    private RoutineInfo TransferSubstitutedTypeArguments(RoutineInfo input,
        RoutineInfo resolved, Dictionary<string, TypeInfo> typeSubs)
    {
        if (input.TypeArguments is not { Count: > 0 } tArgs) return resolved;

        var newArgs = new List<TypeInfo>(capacity: tArgs.Count);
        foreach (TypeInfo t in tArgs)
        {
            TypeInfo subbed = RoutineInfo.SubstituteType(type: t, substitution: typeSubs);
            if (ContainsAnyGenericParameter(type: subbed)) return resolved; // partial — bail
            newArgs.Add(item: subbed);
        }

        // Already aligned (e.g. resolved.TypeArguments was already set the same way).
        if (resolved.TypeArguments is { Count: > 0 } existing
            && existing.Count == newArgs.Count
            && existing.Zip(newArgs, (a, b) => a.FullName == b.FullName).All(x => x))
        {
            return resolved;
        }

        var clone = new RoutineInfo(name: resolved.Name)
        {
            Kind = resolved.Kind,
            OwnerType = resolved.OwnerType,
            Parameters = resolved.Parameters,
            ReturnType = resolved.ReturnType,
            IsFailable = resolved.IsFailable,
            DeclaredModification = resolved.DeclaredModification,
            ModificationCategory = resolved.ModificationCategory,
            GenericParameters = resolved.GenericParameters,
            GenericConstraints = resolved.GenericConstraints,
            Visibility = resolved.Visibility,
            Location = resolved.Location,
            Module = resolved.Module,
            ModulePath = resolved.ModulePath,
            Annotations = resolved.Annotations,
            CallingConvention = resolved.CallingConvention,
            IsVariadic = resolved.IsVariadic,
            IsDangerous = resolved.IsDangerous,
            IsSynthesized = resolved.IsSynthesized,
            TypeArguments = newArgs,
            // Point GenericDefinition at `resolved` itself (the SubstituteMethodForOwner result):
            // its GenericParameters are already filtered to method-level only ([U] for
            // Hijacked[T].recast_as[U]) and its OwnerType is the concrete owner. That alignment
            // is what GenericMonomorphizationPass.BuildResolvedRoutineTypeSubstitutions expects
            // when zipping methodParams against methodTypeArgs.
            GenericDefinition = resolved,
            WrapperForwarderInnerMethod = resolved.WrapperForwarderInnerMethod,
            WrapperForwarderInnerGenericDef = resolved.WrapperForwarderInnerGenericDef,
            Storage = resolved.Storage,
            AsyncStatus = resolved.AsyncStatus,
            OriginalName = resolved.OriginalName,
            HasThrow = resolved.HasThrow,
            HasAbsent = resolved.HasAbsent,
            HasFailableCalls = resolved.HasFailableCalls,
            ThrowableTypes = resolved.ThrowableTypes,
        };
        // Register so GenericMonomorphizationPass.ProcessResolvedMethodGenericRoutines
        // (which iterates Registry.GetAllRoutineResolutions()) can build a body for this key.
        return ctx.Registry.RegisterRoutineResolution(resolvedMethod: clone);
    }

    private static bool ContainsSubstitutableParameter(TypeInfo type, Dictionary<string, TypeInfo> typeSubs)
    {
        if (type is GenericParameterTypeInfo gp) return typeSubs.ContainsKey(key: gp.Name);
        if (type.TypeArguments is { Count: > 0 } args)
        {
            foreach (TypeInfo a in args)
                if (ContainsSubstitutableParameter(type: a, typeSubs: typeSubs)) return true;
        }
        return false;
    }

    private static string ComputeConcreteRegistryKey(RoutineInfo routine, TypeInfo concreteOwner, Dictionary<string, TypeInfo> subs)
    {
        string ownerKey = concreteOwner.TypeArguments is { Count: > 0 }
            ? $"{concreteOwner.Module}.{concreteOwner.Name}[{string.Join(",", concreteOwner.TypeArguments.Select(t => t.FullName))}]"
            : string.IsNullOrEmpty(value: concreteOwner.Module)
                ? concreteOwner.Name
                : $"{concreteOwner.Module}.{concreteOwner.Name}";
        string baseName = $"{ownerKey}.{routine.Name}";
        if (routine.Parameters.Count == 0) return baseName;
        string paramTypes = string.Join(",", routine.Parameters.Select(p =>
        {
            TypeInfo? pt = p.Type;
            if (pt is GenericParameterTypeInfo gp && subs.TryGetValue(key: gp.Name, value: out TypeInfo? c)) pt = c;
            return pt?.FullName ?? "?";
        }));
        return $"{baseName}#{paramTypes}";
    }

    /// <summary>
    /// Reflectively walks an AST node tree and collects every <see cref="CallExpression"/> and
    /// <see cref="GenericMethodCallExpression"/> encountered. Robust to AST schema changes — any
    /// property of a record type that holds Statements/Expressions (or lists thereof) is traversed.
    /// </summary>
    private static void DumpAstInline(object? node, string indent, int depth, int maxDepth)
    {
        if (node == null || depth > maxDepth) return;
        Type t = node.GetType();
        if (t.IsPrimitive || node is string || t.IsEnum) return;
        Console.Error.WriteLine($"[trace]{indent}{t.Name}");
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
                Console.Error.WriteLine($"[trace]{indent}  .{prop.Name}=");
                DumpAstInline(node: value, indent: indent + "    ", depth: depth + 1, maxDepth: maxDepth);
            }
            else if (value is IEnumerable enumerable && value is not string)
            {
                int i = 0;
                foreach (object? item in enumerable)
                {
                    if (item == null) continue;
                    Type it = item.GetType();
                    if (it.IsPrimitive || item is string || it.IsEnum) continue;
                    Console.Error.WriteLine($"[trace]{indent}  .{prop.Name}[{i}]=");
                    DumpAstInline(node: item, indent: indent + "    ", depth: depth + 1, maxDepth: maxDepth);
                    i++;
                    if (i > 8) break;
                }
            }
        }
    }

    private static void CollectCalls(object? node, List<object> sink)
    {
        if (node == null) return;
        if (node is CallExpression || node is GenericMethodCallExpression || node is CreatorExpression || node is ThrowStatement) sink.Add(item: node);

        Type t = node.GetType();
        if (t.IsPrimitive || node is string || t.IsEnum) return;

        // ValueTuples expose Item1/Item2 as fields; ITuple lets us index them uniformly.
        if (node is System.Runtime.CompilerServices.ITuple tuple)
        {
            for (int i = 0; i < tuple.Length; i++)
            {
                CollectCalls(node: tuple[i], sink: sink);
            }
            return;
        }

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
                    if (item == null) continue;
                    if (item is Expression || item is Statement || item is SyntaxTree.Declaration)
                    {
                        CollectCalls(node: item, sink: sink);
                    }
                    else
                    {
                        // Walk tuple items / wrapper records — CreatorExpression.MemberVariables is
                        // List<(string, Expression)>; the Expression hides inside an unrelated struct.
                        Type it = item.GetType();
                        if (!it.IsPrimitive && item is not string && !it.IsEnum)
                        {
                            CollectCalls(node: item, sink: sink);
                        }
                    }
                }
            }
            else if (value is System.Runtime.CompilerServices.ITuple)
            {
                CollectCalls(node: value, sink: sink);
            }
        }
    }
}
