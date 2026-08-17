using System;
using System.Collections.Generic;
using System.Linq;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Resolution;

using TypeInfo = TypeInfo;

public sealed partial class TypeRegistry
{
    #region Routine Registration and Lookup

    /// <summary>
    /// Divergent cross-file duplicate constructors found during registration: two creators sharing a
    /// signature but with DIFFERENT bodies, defined in DIFFERENT files. Registration is last-wins, so
    /// one silently shadows the other — the hazard class that made <c>F64(from: F128)</c> resolve to a
    /// recursive-forwarder stub instead of the real engine impl (infinite recursion). Surfaced as a
    /// build error by <see cref="Verification.SemanticVerifier"/>. Benign identical duplicates (same
    /// body, e.g. <c>U16(from: U8)</c> in both U8.rf and U16.rf) are NOT recorded (equal BodyHash).
    /// </summary>
    public List<(RoutineInfo First, RoutineInfo Second)> DivergentDuplicateCreators { get; } = [];

    /// <summary>
    /// Location-free structural hash of a constructor body for the divergent-duplicate guard (source
    /// text, not record ToString which embeds SourceLocation — so identical logic in two files hashes
    /// equal). Null for empty / extern (PassStatement) bodies. Computed only for creators by the two
    /// registration paths (StdlibLoader, SignatureResolver).
    /// </summary>
    public static int? ComputeCreatorBodyHash(Statement? body)
    {
        if (body is null or PassStatement) return null;
        return body.Accept(visitor: new Builder.RfSyntaxTreePrinter())
                   .GetHashCode(comparisonType: StringComparison.Ordinal);
    }

    /// <summary>Registers a routine by its <see cref="RoutineInfo.RegistryKey"/> (overload-exact) and <see cref="RoutineInfo.BaseName"/> (first-match unqualified).</summary>
    public void RegisterRoutine(RoutineInfo routine) // NOSONAR S3776
    {
        string registryKey = routine.RegistryKey;
        string baseName = routine.BaseName;

        // Register under RegistryKey for exact overload matching.
        // Never let a synthesized (builder-generated) routine overwrite a user-written one:
        // explicit user routines override synthetic same-signature defaults (e.g., a user
        // `T.create(field: Foo)` overrides the auto-generated record field constructor).
        bool keyExisted =
            _routines.TryGetValue(key: registryKey, value: out RoutineInfo? existingByKey);
        if (keyExisted)
        {
            // Divergent cross-file duplicate constructor guard (see DivergentDuplicateCreators):
            // same signature + SAME failability, both real (non-synthetic), different files, DIFFERENT
            // bodies. Failability must match: a checked `T!(from: X)` and a reinterpret `T(from: X)`
            // legitimately share a signature (they coexist via the owner+IsFailable index) and are NOT a
            // divergent duplicate — only same-failability same-signature different-body pairs are the bug.
            if (existingByKey is { IsSynthesized: false, BodyHash: { } h1 }
                && routine is { IsSynthesized: false, BodyHash: { } h2 }
                && existingByKey.IsFailable == routine.IsFailable
                && h1 != h2
                && existingByKey.Location?.FileName is { } f1
                && routine.Location?.FileName is { } f2
                && !string.Equals(a: f1, b: f2, comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                DivergentDuplicateCreators.Add(item: (existingByKey, routine));
            }

            bool existingIsUser = !existingByKey!.IsSynthesized;
            bool incomingIsSynthetic = routine.IsSynthesized;
            if (!(existingIsUser && incomingIsSynthetic))
                _routines[key: registryKey] = routine;
        }
        else
        {
            _routines[key: registryKey] = routine;
        }

        // Also register under base name (first overload wins for unqualified lookup)
        if (!_routines.ContainsKey(key: baseName))
        {
            _routines[key: baseName] = routine;
        }

        // Index by module-qualified name for unambiguous lookup
        string qualifiedName = routine.QualifiedName;
        if (qualifiedName != registryKey && qualifiedName != baseName)
        {
            _routinesByQualifiedName.TryAdd(key: qualifiedName, value: routine);
        }

        // Index by owner type → method name → overloads for fast O(1) `owner.method` lookup. A bare
        // generic-param owner (`routine T.m()`) is normalized to the canonical GenericOwnerKey so its
        // default-impl member routines resolve by name off this same store (no separate index) — see the
        // fallback in LookupMethod. The param name (T/E/…) is arbitrary for such a routine.
        if (routine.OwnerType != null)
        {
            string ownerKey = routine.OwnerType is GenericParameterTypeInfo
                ? GenericOwnerKey
                : routine.OwnerType.FullName;
            if (!_routinesByOwner.TryGetValue(key: ownerKey, value: out Dictionary<string, List<RoutineInfo>>? byName))
            {
                byName = new Dictionary<string, List<RoutineInfo>>(comparer: StringComparer.Ordinal);
                _routinesByOwner[key: ownerKey] = byName;
            }
            if (!byName.TryGetValue(key: routine.Name, value: out List<RoutineInfo>? list))
            {
                list = [];
                byName[key: routine.Name] = list;
            }

            // Dedup by (RegistryKey, Me-constraint set): a re-registered routine (same owner and
            // signature) REPLACES its prior list entry instead of appending. Appending duplicates
            // here let method resolution iterate stale-and-fresh copies of the same overload and pick
            // order-dependently — a non-determinism that manifested as platform-specific codegen.
            // Failability is NOT part of the identity: a name maps to at most one routine (declaring
            // both `mul` and `mul!` is a name collision, not two coexisting routines), and `!` is
            // never in the name. The dedup scan only runs when the RegistryKey was already present
            // (`keyExisted`); a key's first registration stays an O(1) append. User-written routines
            // are never replaced by a synthesized same-identity routine.
            if (keyExisted)
            {
                // Identity includes the Me-constraint set: several `needs Me is VariantType` /
                // `obeys X`-gated protocol-default bodies share a RegistryKey (same signature) yet are
                // DISTINCT overloads that must coexist so within-dispatch can pick the kind-matched
                // one. Only a truly same-signature, same-constraint re-registration replaces in place.
                int existingIdx = list.FindIndex(match: r =>
                    r.RegistryKey == registryKey && SameMeConstraintSet(a: r, b: routine));
                if (existingIdx < 0)
                    list.Add(item: routine);
                else if (!(!list[index: existingIdx].IsSynthesized && routine.IsSynthesized))
                    list[index: existingIdx] = routine;
            }
            else
            {
                list.Add(item: routine);
            }
        }


        // Free-function (owner-less) overloads: fold into _routinesByOwner under the canonical FreeOwnerKey,
        // keyed by base name → overloads (append + reference-dedup, matching the old _routineOverloads).
        if (routine.OwnerType == null)
        {
            if (!_routinesByOwner.TryGetValue(key: FreeOwnerKey, value: out Dictionary<string, List<RoutineInfo>>? freeByName))
            {
                freeByName = new Dictionary<string, List<RoutineInfo>>(comparer: StringComparer.Ordinal);
                _routinesByOwner[key: FreeOwnerKey] = freeByName;
            }
            if (!freeByName.TryGetValue(key: baseName, value: out List<RoutineInfo>? overloadList))
            {
                overloadList = [];
                freeByName[key: baseName] = overloadList;
            }
            if (!overloadList.Contains(item: routine))
                overloadList.Add(item: routine);
        }
    }

    /// <summary>
    /// Checks if a routine with the given key is registered.
    /// </summary>
    public bool HasRoutine(string key) => _routines.ContainsKey(key: key);

    /// <summary>
    /// Looks up a routine overload that matches the given argument types.
    /// Falls back to the default (first-registered) overload if no exact match.
    /// </summary>
    /// <param name="baseName">The routine's base name (e.g., "List.append", "IO.show").</param>
    /// <param name="argTypes">The argument types to match against.</param>
    public RoutineInfo? LookupRoutineOverload(string baseName, List<TypeInfo> argTypes)
    {
        // Try exact overload match by RegistryKey format.
        // Zero-arg routines register under baseName (no '#' suffix) — match that directly.
        string paramTypeNames =
            string.Join(separator: ",",
                values: argTypes.Select(RoutineInfo.GetTypeIdentity));
        string registryKey = argTypes.Count == 0 ? baseName : $"{baseName}#{paramTypeNames}";
        if (_routines.TryGetValue(key: registryKey, value: out RoutineInfo? overload))
        {
            return overload;
        }

        // Core-prefix fallback: bare callee names (e.g., "rf_allocate_dynamic_uninit") register
        // under "Core.rf_allocate_dynamic_uninit#…". Mirror LookupRoutine's behavior so overload
        // resolution can find module-qualified registrations from unqualified call sites.
        if (!baseName.Contains(value: '.'))
        {
            string coreKey = argTypes.Count == 0
                ? $"Core.{baseName}"
                : $"Core.{baseName}#{paramTypeNames}";
            if (_routines.TryGetValue(key: coreKey, value: out RoutineInfo? coreOverload))
            {
                return coreOverload;
            }
        }

        // Try matching generic overloads by reconstructing the generic parameter pattern.
        // e.g., arg SortedSet[S64] -> its generic def is SortedSet with GenericParameters ["T"]
        //        -> try key "List.create#SortedSet[T]" which matches the registered generic overload.
        foreach (TypeInfo argType in argTypes)
        {
            if (!argType.IsGenericResolution)
            {
                continue;
            }

            TypeInfo? genericDef = argType switch
            {
                RecordTypeInfo r => r.GenericDefinition,
                EntityTypeInfo e => e.GenericDefinition,
                ProtocolTypeInfo p => p.GenericDefinition,
                _ => null
            };
            if (genericDef?.GenericParameters == null)
            {
                continue;
            }

            string genericArgName = RoutineInfo.GetTypeIdentity(type: genericDef);
            string genericRegistryKey = $"{baseName}#{genericArgName}";
            if (_routines.TryGetValue(key: genericRegistryKey,
                    value: out RoutineInfo? genericOverload) &&
                !genericOverload
                   .IsVariadic) // Skip variadic overloads — handled by variadic fallback
            {
                return genericOverload;
            }
        }

        // Structural candidate search: iterate all overloads registered for this base name and
        // match positionally by full type identity (module-qualified, includes generic args).
        // Runs before the first-wins fallback so multi-overload disambiguation is type-correct.
        List<RoutineInfo>? overloadCandidates = FreeOverloads(baseName: baseName);
        if (overloadCandidates == null && !baseName.Contains(value: '.'))
        {
            overloadCandidates = FreeOverloads(baseName: $"Core.{baseName}");
        }
        if (overloadCandidates is { Count: > 1 })
        {
            foreach (RoutineInfo candidate in overloadCandidates)
            {
                if (candidate.Parameters.Count != argTypes.Count) continue;
                bool match = true;
                for (int i = 0; i < argTypes.Count; i++)
                {
                    if (RoutineInfo.GetTypeIdentity(type: candidate.Parameters[index: i].Type) !=
                        RoutineInfo.GetTypeIdentity(type: argTypes[index: i]))
                    {
                        match = false;
                        break;
                    }
                }

                if (match) return candidate;
            }
        }

        // Fall back to first-registered overload. Needed in two cases:
        // 1. Single overload — first-wins is the correct choice.
        // 2. No structural match found — SA still needs a candidate to check types against
        //    so it can report ArgumentTypeMismatch rather than silently producing no error.
        return LookupRoutine(fullName: baseName);
    }

    /// <summary>
    /// Looks up the routine registered under an exact <see cref="RoutineInfo.RegistryKey"/>.
    /// Unlike <see cref="LookupRoutineOverload"/> this applies no Core-prefix or generic
    /// fallbacks — it answers "is this precise owner+name+signature slot occupied?", which
    /// reserved-variant collision detection needs to compare a generated variant against any
    /// hand-written routine sharing its key.
    /// </summary>
    public RoutineInfo? GetRoutineByExactKey(string registryKey) =>
        _routines.TryGetValue(key: registryKey, value: out RoutineInfo? routine) ? routine : null;

    /// <summary>
    /// Looks up a routine by its full name. A routine's identity is (owner, bare-name) — the
    /// failable `!` is NOT part of the name, so a name maps to at most ONE routine and failability
    /// is an attribute read off that routine, never a lookup key. When <paramref name="isFailable"/>
    /// is given it filters the found routine (returns null on a failability mismatch) so a bare call
    /// can retry for the failable-only form; it never selects between two same-named variants
    /// (declaring both `foo` and `foo!` is a name collision, not two coexisting routines).
    /// </summary>
    /// <param name="fullName">The fully qualified name of the routine.</param>
    /// <param name="isFailable">If non-null, require the routine's failability to match.</param>
    /// <returns>The routine info if found, null otherwise.</returns>
    public RoutineInfo? LookupRoutine(string fullName, bool? isFailable = null)
    {
        RoutineInfo? routine =
            _routines.GetValueOrDefault(key: fullName)
            ?? _routineResolutions.GetValueOrDefault(key: fullName)
            ?? _routinesByQualifiedName.GetValueOrDefault(key: fullName)
            ?? (!fullName.Contains(value: '.')
                ? _routines.GetValueOrDefault(key: $"Core.{fullName}")
                : null);

        if (routine == null) return null;
        return isFailable != null && routine.IsFailable != isFailable.Value ? null : routine;
    }

    /// <summary>
    /// Looks up a routine by its module-qualified name (e.g., "Core.S8.add").
    /// </summary>
    public RoutineInfo? LookupRoutineByQualifiedName(string qualifiedName)
    {
        return _routinesByQualifiedName.GetValueOrDefault(key: qualifiedName);
    }

    /// <summary>
    /// Looks up a routine by its short name (without module prefix).
    /// Used by codegen when the AST has "Console.show" but the registry key is "IO.show".
    /// Falls back to a linear scan only when neither fast-path dictionaries find a match.
    /// </summary>
    public RoutineInfo? LookupRoutineByName(string name, bool? isFailable = null)
    {
        // Fast path: the primary _routines index covers the common case; fall back to a Core prefix
        // and finally a targeted linear scan for codegen short-name lookups.
        if (_routines.TryGetValue(key: name, value: out RoutineInfo? found) &&
            found.OwnerType == null &&
            (isFailable == null || found.IsFailable == isFailable))
            return found;

        if (_routines.TryGetValue(key: $"Core.{name}", value: out found) &&
            found.OwnerType == null &&
            (isFailable == null || found.IsFailable == isFailable))
            return found;

        // Fallback: targeted linear scan (rare; used only by codegen short-name lookups)
        return _routines.Values.FirstOrDefault(routine =>
            routine.Name == name &&
            routine.OwnerType == null &&
            (isFailable == null || routine.IsFailable == isFailable));
    }

    /// <summary>
    /// Looks up any registered routine (free or member) whose Name equals <paramref name="methodName"/>.
    /// Linear scan; intended as a last-resort fallback when name-construction mismatches obscure the
    /// canonical registry key (e.g. extension methods on concrete generic specializations).
    /// </summary>
    public RoutineInfo? LookupAnyByMethodName(string methodName, bool? isFailable = null)
    {
        return _routines.Values.FirstOrDefault(routine =>
            routine.Name == methodName &&
            (isFailable == null || routine.IsFailable == isFailable));
    }

    /// <summary>
    /// Finds a generic overload of a free function by name (e.g., show[T] for "show").
    /// Backed by <see cref="GenericFreeFunctions"/>, which scans the FreeOwnerKey store.
    /// </summary>
    /// <param name="name">The routine name (without generic params).</param>
    /// <param name="preferredArity">Expected argument count; -1 means any arity is acceptable.</param>
    public RoutineInfo? LookupGenericOverload(string name, int preferredArity = -1)
    {
        List<RoutineInfo> candidates = GenericFreeFunctions(name: name);
        if (candidates.Count == 0)
            return null;

        // Prefer non-variadic overloads matching the preferred arity first.
        RoutineInfo? arityMismatch = null;
        RoutineInfo? variadicFallback = null;
        foreach (RoutineInfo routine in candidates)
        {
            if (routine.IsVariadic) { variadicFallback ??= routine; continue; }
            if (preferredArity < 0 || routine.Parameters.Count == preferredArity) return routine;
            arityMismatch ??= routine;
        }

        return variadicFallback ?? arityMismatch;
    }

    /// <summary>
    /// Finds a variadic generic overload of a free function by name (e.g., show[T](values...: T) for "show").
    /// Backed by <see cref="GenericFreeFunctions"/>, which scans the FreeOwnerKey store.
    /// </summary>
    public RoutineInfo? LookupVariadicGenericOverload(string name)
        => GenericFreeFunctions(name: name).FirstOrDefault(routine => routine.IsVariadic);

    /// <summary>
    /// Updates a routine with resolved parameters and return type.
    /// Used for external declarations that are registered in Phase 1 without params.
    /// </summary>
    /// <param name="routine">The routine to update.</param>
    /// <param name="parameters">The resolved parameters.</param>
    /// <param name="returnType">The resolved return type.</param>
    /// <param name="genericParameters">Updated generic parameters (may include implicit ones from protocol-as-type).</param>
    /// <param name="genericConstraints">Updated generic constraints (may include implicit ones from protocol-as-type).</param>
    public void UpdateRoutine(RoutineInfo routine, List<ParameterInfo> parameters,
        TypeInfo? returnType, List<string>? genericParameters,
        List<GenericConstraintDeclaration>? genericConstraints) // NOSONAR S3776
    {
        string baseName = routine.BaseName;
        if (!_routines.ContainsKey(key: baseName))
        {
            return;
        }

        // Create updated routine with resolved signature
        var updatedRoutine = new RoutineInfo(name: routine.Name)
        {
            Kind = routine.Kind,
            OwnerType = routine.OwnerType,
            MeType = routine.MeType,
            Parameters = parameters,
            ReturnType = returnType,
            IsFailable = routine.IsFailable,
            DeclaredMutation = routine.DeclaredMutation,
            MutationCategory = routine.MutationCategory,
            GenericParameters = genericParameters,
            GenericConstraints = genericConstraints,
            Visibility = routine.Visibility,
            Location = routine.Location,
            Module = routine.Module,
            ModulePath = routine.ModulePath,
            Annotations = routine.Annotations,
            CallingConvention = routine.CallingConvention,
            IsVariadic = routine.IsVariadic,
            IsDangerous = routine.IsDangerous,
            Storage = routine.Storage,
            AsyncStatus = routine.AsyncStatus,
            FailableVariant = routine.FailableVariant
        };

        // Replace base name entry
        _routines[key: baseName] = updatedRoutine;

        // Register with resolved RegistryKey for overload-specific lookup
        string registryKey = updatedRoutine.RegistryKey;
        if (registryKey != baseName)
        {
            _routines[key: registryKey] = updatedRoutine;
        }

        // Update the module-qualified name index
        string qualifiedName = updatedRoutine.QualifiedName;
        if (qualifiedName != baseName)
        {
            _routinesByQualifiedName[key: qualifiedName] = updatedRoutine;
        }

        // Update the routines-by-owner index if this is a method
        if (routine.OwnerType != null)
        {
            string ownerKey = routine.OwnerType is GenericParameterTypeInfo
                ? GenericOwnerKey
                : routine.OwnerType.FullName;
            if (_routinesByOwner.TryGetValue(key: ownerKey, value: out Dictionary<string, List<RoutineInfo>>? byName)
                && byName.TryGetValue(key: baseName, value: out List<RoutineInfo>? list))
            {
                int index = list.FindIndex(match: r => r.BaseName == baseName);
                if (index >= 0)
                {
                    list[index: index] = updatedRoutine;
                }
            }
        }

        // Update the free-function overload entry (now under FreeOwnerKey) — replace old instance by reference
        if (updatedRoutine.OwnerType == null &&
            FreeOverloads(baseName: baseName) is { } overloadList)
        {
            int idx = overloadList.FindIndex(match: r => ReferenceEquals(r, routine));
            if (idx >= 0)
                overloadList[index: idx] = updatedRoutine;
            else if (!overloadList.Contains(item: updatedRoutine))
                overloadList.Add(item: updatedRoutine);
        }
    }

    /// <summary>
    /// Recursively unifies a specialized-receiver pattern (a method's <c>MeType</c>, e.g.
    /// <c>List[Agent[V]]</c>) against the concrete receiver (<c>List[Agent[S64]]</c>), recording
    /// each method generic parameter's binding (V → S64) into <paramref name="into"/>. Used so a
    /// member declared on a specialized generic instantiation resolves to a fully concrete method.
    /// </summary>
    private static void UnifyReceiverGenerics(TypeInfo pattern, TypeInfo concrete,
        List<string>? genericParams, Dictionary<string, TypeInfo> into)
    {
        if (genericParams is not { Count: > 0 }) return;
        if (pattern is GenericParameterTypeInfo gp)
        {
            if (genericParams.Contains(item: gp.Name) && !into.ContainsKey(key: gp.Name) &&
                concrete is not GenericParameterTypeInfo)
            {
                into[key: gp.Name] = concrete;
            }
            return;
        }
        if (pattern.TypeArguments is { Count: > 0 } pArgs &&
            concrete.TypeArguments is { Count: > 0 } cArgs)
        {
            for (int i = 0; i < pArgs.Count && i < cArgs.Count; i++)
            {
                UnifyReceiverGenerics(pattern: pArgs[index: i], concrete: cArgs[index: i],
                    genericParams: genericParams, into: into);
            }
        }
    }

    /// <summary>
    /// True when two routines carry the same set of <c>needs Me …</c> gate constraints (compared by
    /// kind + constraint-type names, order-insensitive). Differently-gated same-signature protocol
    /// defaults are DISTINCT overloads and must not dedup each other in the owner method list.
    /// </summary>
    /// <param name="a">First routine to compare.</param>
    /// <param name="b">Second routine to compare.</param>
    private static bool SameMeConstraintSet(RoutineInfo a, RoutineInfo b)
    {
        static List<string> MeGates(RoutineInfo r) =>
            (r.GenericConstraints ?? [])
            .Where(predicate: c => c.ParameterName == "Me")
            .Select(selector: c =>
                $"{c.ConstraintType}:{string.Join(separator: ",", values: (c.ConstraintTypes ?? []).Select(selector: t => t.Name))}")
            .OrderBy(keySelector: s => s, comparer: StringComparer.Ordinal)
            .ToList();

        List<string> ga = MeGates(r: a);
        List<string> gb = MeGates(r: b);
        return ga.Count == gb.Count && ga.SequenceEqual(second: gb);
    }

    /// <summary>
    /// Picks the most-specific of several same-name candidate routines for a concrete implementer:
    /// among those whose <c>needs Me …</c> constraints the implementer satisfies, the one with the
    /// MOST such constraints wins (an unconstrained body is the least-specific fallback). Ties and
    /// the no-satisfied-candidate case fall back to the first candidate.
    /// </summary>
    private RoutineInfo? SelectMostSpecificForImplementer(List<RoutineInfo> candidates,
        TypeInfo implementer)
    {
        RoutineInfo? best = null;
        int bestScore = -1;
        foreach (RoutineInfo candidate in candidates)
        {
            List<GenericConstraintDeclaration> meConstraints =
                candidate.GenericConstraints?
                    .Where(predicate: c => c.ParameterName == "Me")
                    .ToList() ?? [];
            if (!meConstraints.All(predicate: c =>
                    ImplementerSatisfiesConstraint(implementer: implementer, constraint: c)))
            {
                continue; // some Me-constraint is unmet — this body doesn't apply
            }

            if (meConstraints.Count > bestScore)
            {
                bestScore = meConstraints.Count;
                best = candidate;
            }
        }

        return best ?? candidates.FirstOrDefault();
    }

    /// <summary>
    /// True when a concrete implementer type satisfies a single <c>needs Me …</c> constraint used to
    /// gate a protocol-default body (the kind constraints <c>is variant/choice/flags/record/entity</c>
    /// plus <c>obeys P</c>).
    /// </summary>
    private bool ImplementerSatisfiesConstraint(TypeInfo implementer,
        GenericConstraintDeclaration constraint)
    {
        switch (constraint.ConstraintType)
        {
            case ConstraintKind.VariantType:
                return implementer is VariantTypeInfo;
            case ConstraintKind.ChoiceType:
                return implementer is ChoiceTypeInfo;
            case ConstraintKind.FlagsType:
                return implementer is FlagsTypeInfo;
            case ConstraintKind.TupleType:
                return implementer is TupleTypeInfo;
            case ConstraintKind.RoutineType:
                return implementer is RoutineTypeInfo;
            case ConstraintKind.Crashable:
                return implementer is CrashableTypeInfo;
            case ConstraintKind.Splittable:
                // The SoA footprint gate: the element type tears down trivially (only `@llvm`
                // primitives + raw pointers, no custom store/destroy), so its columns are
                // memcpy-movable with no per-element teardown.
                return IsTriviallyDestructible(type: implementer);
            case ConstraintKind.ZeroMemvarType:
                // A field-less aggregate: an empty record, or a scalar kind (choice/flags carry no
                // member variables). Its `memvarof` is empty, so the base field-walk is degenerate.
                return implementer switch
                {
                    RecordTypeInfo r => r.MemberVariables.Count == 0,
                    EntityTypeInfo e => e.MemberVariables.Count == 0,
                    _ => false
                };
            case ConstraintKind.ReferenceType:
                // `is EntityType` — a plain entity; a crashable is an entity subtype with its own
                // more-specific `is CrashableType` gate, so exclude it here.
                return implementer is EntityTypeInfo and not CrashableTypeInfo;
            case ConstraintKind.ValueType:
                // `is RecordType` — a plain value record; exclude the sum/enum/tuple record
                // subtypes, which have their own more-specific kind gates.
                return implementer is RecordTypeInfo
                    and not (VariantTypeInfo or ChoiceTypeInfo or FlagsTypeInfo or TupleTypeInfo);
            case ConstraintKind.Obeys:
                return constraint.ConstraintTypes?.All(predicate: p =>
                    TypeObeysProtocol(type: implementer, protocolName: p.Name)) ?? true;
            default:
                // Unknown/unsupported gate — treat as satisfied so it never wrongly excludes.
                return true;
        }
    }

    /// <summary>
    /// Registers an auto-derive template captured directly from a stdlib <c>@overridable/@override
    /// routine T.method()</c> declaration: the owner type-parameter name (<c>T</c>), the method's
    /// kind gate constraints (<c>needs T is VariantType/…</c>), and its AST body. Several
    /// same-signature templates coexist (distinguished by their gate set) because selection is
    /// per-type at SYNTHESIS time — this store never goes through the signature-keyed registry.
    /// </summary>
    /// <summary>The default-impl member routine named <paramref name="methodName"/> declared on a bare
    /// generic-param owner (`routine T.m()`), or null. Found under the canonical GenericOwnerKey in
    /// _routinesByOwner — the by-name resolution path that replaced the old separate _universalMethods index.
    /// First overload wins (matching the prior first-registration-wins TryAdd).</summary>
    private RoutineInfo? DefaultMemberRoutine(string methodName)
        => _routinesByOwner.TryGetValue(key: GenericOwnerKey,
               value: out Dictionary<string, List<RoutineInfo>>? byName)
           && byName.TryGetValue(key: methodName, value: out List<RoutineInfo>? list)
           && list.Count > 0
            ? list[index: 0]
            : null;

    /// <summary>The free-function (owner-less) overload list for <paramref name="baseName"/>, or null —
    /// stored under the canonical FreeOwnerKey in _routinesByOwner (replaced the old _routineOverloads).</summary>
    private List<RoutineInfo>? FreeOverloads(string baseName)
        => _routinesByOwner.TryGetValue(key: FreeOwnerKey, value: out Dictionary<string, List<RoutineInfo>>? byName)
           && byName.TryGetValue(key: baseName, value: out List<RoutineInfo>? list)
            ? list
            : null;

    /// <summary>Generic-definition free functions with the bare name <paramref name="name"/> — filtered off
    /// the FreeOwnerKey store (which is keyed by BaseName = Module.Name), replacing the old separate
    /// _genericFreeFunctions by-Name index. Matches the old semantics (all modules' same-named generics).</summary>
    private List<RoutineInfo> GenericFreeFunctions(string name)
        => _routinesByOwner.TryGetValue(key: FreeOwnerKey, value: out Dictionary<string, List<RoutineInfo>>? byName)
            ? OwnerMethods(byName: byName).Where(predicate: r => r.Name == name && r.IsGenericDefinition).ToList()
            : [];

    public void RegisterDeriveTemplate(string method, string ownerParam, int arity,
        List<GenericConstraintDeclaration>? constraints, Statement body)
    {
        if (!_deriveTemplates.TryGetValue(key: method,
                value: out List<(string, int, List<GenericConstraintDeclaration>, Statement)>? list))
        {
            list = [];
            _deriveTemplates[key: method] = list;
        }

        List<GenericConstraintDeclaration> gates = DeriveKindGates(constraints: constraints);
        string gateKey = DeriveGateKey(gates: gates);
        // Dedup by (arity, gate set): several same-name templates coexist — kind-gated overrides
        // (different gates) and, for `hash`, the fast `hash()` vs keyed `hash(k0, k1)` forms
        // (different arity, same gates).
        if (list.Any(predicate: e => e.Item2 == arity && DeriveGateKey(gates: e.Item3) == gateKey))
            return; // already captured (re-run across passes)
        list.Add(item: (ownerParam, arity, gates, body));
    }

    /// <summary>
    /// Selects the most-specific auto-derive template for <paramref name="forType"/>: among the
    /// candidates whose kind gates (<c>needs T is VariantType/…</c>) the type satisfies, the one
    /// with the MOST gates wins; the unconstrained base is the fallback. Returns the owner
    /// type-parameter name (for the T→type substitution) and the template body.
    /// </summary>
    public (string OwnerParam, Statement Body)? GetDeriveTemplate(string name, int arity,
        TypeInfo forType)
    {
        if (!_deriveTemplates.TryGetValue(key: name,
                value: out List<(string, int, List<GenericConstraintDeclaration>, Statement)>? list))
            return null;

        (string, Statement)? best = null;
        int bestScore = -1;
        foreach ((string ownerParam, int tArity, List<GenericConstraintDeclaration> gates,
                     Statement body) in list)
        {
            if (tArity != arity)
                continue;
            if (!gates.All(predicate: g =>
                    ImplementerSatisfiesConstraint(implementer: forType, constraint: g)))
                continue;
            if (gates.Count > bestScore)
            {
                bestScore = gates.Count;
                best = (ownerParam, body);
            }
        }

        return best;
    }

    /// <summary>The kind gate constraints (<c>is VariantType/choice/flags/…</c>) that drive
    /// per-type derive selection. Obeys/other constraints are ignored for gating.</summary>
    private static List<GenericConstraintDeclaration> DeriveKindGates(
        List<GenericConstraintDeclaration>? constraints)
        => (constraints ?? [])
            .Where(predicate: c => c.ConstraintType is ConstraintKind.VariantType
                or ConstraintKind.ChoiceType or ConstraintKind.FlagsType
                or ConstraintKind.TupleType or ConstraintKind.ValueType
                or ConstraintKind.ReferenceType or ConstraintKind.RoutineType
                or ConstraintKind.Crashable or ConstraintKind.ZeroMemvarType
                or ConstraintKind.Splittable)
            .ToList();

    private static string DeriveGateKey(List<GenericConstraintDeclaration> gates)
        => string.Join(separator: "&",
            values: gates
                .Select(selector: c => c.ConstraintType.ToString())
                .OrderBy(keySelector: s => s, comparer: StringComparer.Ordinal));

    /// <summary>Looks up a method on a type, returning a fully-resolved <see cref="RoutineInfo"/> with type parameters substituted for generic owners and protocol methods.</summary>
    /// <param name="type">The type to search.</param>
    /// <param name="methodName">The method name to look up.</param>
    /// <param name="isFailable">Filter by failability; null = accept either.</param>
    /// <param name="forImplementer">Concrete implementer for protocol method substitution.</param>
    public RoutineInfo? LookupMethod(TypeInfo type, string methodName, bool? isFailable = null,
        TypeInfo? forImplementer = null)
    {
        // Transparent-protocol unwrap: Accessing[X] / Controlling[X] are markers that
        // dispatch every method to X. If the receiver is one of these wrappers with a
        // single type argument, recurse on the inner type. Without this, for-loops over
        // `Accessing[Iterable[T]]` parameters can't resolve iter at SA time, leading
        // to "no resolved method" warnings during generic monomorphization.
        if (type is ProtocolTypeInfo { TypeArguments: { Count: 1 } markerArgs } markerProto)
        {
            string markerBase = TypeInfo.StripTypeArgs(
                name: markerProto.GenericDefinition?.Name ?? markerProto.Name);
            if (markerBase is RuntimeContract.Accessing or RuntimeContract.Controlling)
            {
                RoutineInfo? viaInner = LookupMethod(type: markerArgs[index: 0],
                    methodName: methodName, isFailable: isFailable);
                if (viaInner != null) return viaInner;
            }
        }

        // First check the type's own methods (O(1) by name via the nested store)
        if (_routinesByOwner.TryGetValue(key: type.FullName, value: out Dictionary<string, List<RoutineInfo>>? ownByName)
            && ownByName.TryGetValue(key: methodName, value: out List<RoutineInfo>? methods))
        {
            List<RoutineInfo> nameMatches = methods.Where(predicate: m =>
                isFailable == null || m.IsFailable == isFailable).ToList();

            // `within`-dispatch: when a protocol declares several same-name default bodies gated by
            // `needs Me is VariantType` / `is ChoiceType` / `obeys X`, pick the MOST-SPECIFIC one whose
            // Me-constraints the concrete implementer satisfies (an unconstrained body is the least
            // specific fallback). Only kicks in when resolving a protocol's defaults FOR a concrete
            // implementer with >1 candidate — the single-candidate common path is unchanged.
            RoutineInfo? method = nameMatches.Count > 1 && forImplementer != null
                ? SelectMostSpecificForImplementer(candidates: nameMatches,
                    implementer: forImplementer)
                : nameMatches.FirstOrDefault();
            if (method != null)
            {
                bool shouldNormalizeConcreteOwner =
                    (type.IsGenericResolution || type is WrapperTypeInfo { TypeArguments: { Count: > 0 } }) &&
                    (method.OwnerType is { IsGenericDefinition: true } ||
                     method.IsGenericDefinition);
                if (shouldNormalizeConcreteOwner)
                {
                    return SubstituteMethodForOwner(method: method, resolvedOwner: type);
                }

                return method;
            }
        }

        // For protocol types, check the protocol's method signatures
        if (type is ProtocolTypeInfo proto)
        {
            ProtocolMethodInfo? protoMethod =
                proto.Methods.FirstOrDefault(predicate: m =>
                    m.Name == methodName &&
                    (isFailable == null || m.IsFailable == isFailable));
            if (protoMethod != null)
            {
                return SynthesizeProtocolMethod(proto: proto,
                    protoMethod: protoMethod,
                    ownerType: type);
            }
        }

        // For resolved generics, check the generic definition's methods
        if (type.IsGenericResolution)
        {
            TypeInfo? genericDef = type switch
            {
                RecordTypeInfo r => r.GenericDefinition,
                EntityTypeInfo e => e.GenericDefinition,
                ProtocolTypeInfo p => p.GenericDefinition,
                // Wrapper types: methods are registered on the corresponding RecordTypeInfo
                // (e.g. _routinesByOwner["Core.Hijacked"] holds extract, offset, etc.).
                // Always look up the RecordTypeInfo by base name, regardless of whether
                // InnerType is a generic parameter — Hijacked[T] and Hijacked[Character]
                // both need to route through the generic definition's method table.
                WrapperTypeInfo wt => LookupType(name: wt.Name),
                _ => null
            };

            if (genericDef != null)
            {
                RoutineInfo? genericMethod =
                    LookupMethod(type: genericDef, methodName: methodName, isFailable: isFailable);
                // Skip the generic-def → concrete substitution path when the inner lookup
                // resolved via the universal-method fallback (e.g. `T.hijack()`). In that
                // case `genericMethod` already has its universal T baked to the generic-def
                // (e.g. `Hijacked[Retained-genericdef]`), and a second
                // SubstituteMethodForOwner with the concrete `type` only substitutes the
                // OUTER record's generic params (Retained's T → Counter) — it can't reach
                // the inner T binding any more. Fall through to the universal path below
                // so `T` binds directly to the concrete `type` (e.g. Retained[Counter])
                // and produces `Hijacked[Retained[Counter]]`.
                if (genericMethod != null &&
                    genericMethod.GenericDefinition?.OwnerType is not GenericParameterTypeInfo)
                {
                    return SubstituteMethodForOwner(method: genericMethod, resolvedOwner: type);
                }
            }
        }

        // Fallback: a default-impl member routine on a bare generic-param owner (`routine T.m()`), found by
        // name under the canonical GenericOwnerKey in the same _routinesByOwner store and substituted onto the
        // concrete receiver (derive template / access coercion / hijack / `@innate` fold-intrinsic — the
        // AST/synthesis + fold layers decide what happens next). No separate "universal methods" index.
        if (DefaultMemberRoutine(methodName: methodName) is { } defaultMember)
        {
            return SubstituteMethodForOwner(method: defaultMember, resolvedOwner: type);
        }

        // Generic parameter receivers route through caller-supplied constraints — see
        // LookupMethodViaConstraints below. The plain LookupMethod path has no routine
        // context to discover Obeys constraints, so it cannot resolve them here.

        // Check implemented protocols for default implementations
        List<TypeInfo>? protocols = type switch
        {
            RecordTypeInfo r => r.ImplementedProtocols,
            EntityTypeInfo e => e.ImplementedProtocols,
            _ => null
        };
        if (protocols != null)
        {
            // Retained/Tracked obey `Controlling[T]`. The recursive LookupMethod call on a
            // `Controlling[X]` protocol triggers the marker-protocol unwrap at the top of this
            // method, dispatching the lookup transparently to X's method. That is correct for
            // protocol-as-type parameter receivers (where the call site already holds an X-shaped
            // pointer), but WRONG for RC wrappers — their pointer addresses a `RetainController[T]`
            // struct, NOT T directly. Letting the unwrap proceed here returns the inner T method
            // (e.g. `ListNode.chain_text`), which the call dispatcher then invokes with the
            // controller pointer as `me`, reading strong+weak counts as if they were T's first
            // fields. Skip the protocols loop for Retained/Tracked records so the call dispatcher
            // falls through to the wrapper-forwarder synthesis path, which emits the correct
            // double-indirection body.
            string recBaseName = type switch
            {
                RecordTypeInfo r2 => r2.GenericDefinition?.Name ?? r2.Name,
                _ => type.Name
            };
            recBaseName = TypeInfo.StripTypeArgs(name: recBaseName);
            bool skipProtocols = recBaseName is RuntimeContract.Retained or RuntimeContract.Tracked;
            if (!skipProtocols)
            {
                foreach (var protocol in protocols)
                {
                    // Thread the concrete implementer so a protocol with several `needs`-gated
                    // default bodies dispatches to the kind-matched one (within-dispatch).
                    var res = LookupMethod(type: protocol, methodName: methodName,
                        forImplementer: forImplementer ?? type);
                    if (res != null) return res;
                }
            }
            return null;
        }

        // WrapperTypeInfo (Viewing/Modifying/Inspecting/Claiming/Shared/Watched)
        // is the parallel representation to the substituted RecordTypeInfo of the same wrapper.
        // The RecordTypeInfo path finds methods via its substituted `Controlling[InnerT]` /
        // `Accessing[InnerT]` protocol entry. WrapperTypeInfo carries no ImplementedProtocols,
        // so the protocols loop above is skipped — without this fallback, the call dispatcher
        // would then synthesize a forwarder whose body is never emitted (LINKERR). Resolve
        // directly to InnerType as a last resort. Hijacked is intentionally excluded — its
        // members must be reached via explicit extract()/as_entity().
        //
        // Retained/Tracked are also excluded: they are `@llvm("ptr")` to a `RetainController[T]`,
        // NOT to T directly. Falling through here would dispatch an inner-T method with `me` =
        // controller pointer, reading controller's strong+weak counts as if they were T's first
        // fields. The forwarder-synthesis path emits the correct double-indirection body
        // (Hijacked[RetainController[T]](me).as_entity().borrow_data().as_entity().method(...)).
        if (type is WrapperTypeInfo { Name: RuntimeContract.Viewing
                or RuntimeContract.Modifying or RuntimeContract.Inspecting or RuntimeContract.Claiming or RuntimeContract.Shared or RuntimeContract.Watched
            } forwardingWrapper)
        {
            return LookupMethod(type: forwardingWrapper.InnerType,
                methodName: methodName, isFailable: isFailable);
        }

        return null;
    }

    /// <summary>
    /// Resolves a method on a generic-parameter receiver by walking <c>Obeys</c> constraints
    /// supplied by the caller (typically the current routine + its owner type). Each constraint
    /// protocol is queried via <see cref="LookupMethod"/>, which synthesizes a <see cref="RoutineInfo"/>
    /// from the matching <see cref="ProtocolMethodInfo"/>. Returns the first hit, or null.
    /// </summary>
    public RoutineInfo? LookupMethodViaConstraints(GenericParameterTypeInfo param,
        string methodName, bool? isFailable,
        IEnumerable<GenericConstraintDeclaration> constraints)
    {
        foreach (GenericConstraintDeclaration c in constraints)
        {
            if (c.ParameterName != param.Name ||
                c.ConstraintType != ConstraintKind.Obeys ||
                c.ConstraintTypes == null)
                continue;
            foreach (TypeExpression protocolExpr in c.ConstraintTypes)
            {
                TypeInfo? proto = LookupType(name: protocolExpr.Name);
                if (proto is not ProtocolTypeInfo protoInfo)
                    continue;
                // Synthesize directly with the generic parameter as ownerType so that
                // Me-self-type slots in the protocol signature substitute to `param`
                // (e.g. `T`), not to the protocol itself. Going through LookupMethod
                // would bind Me to the protocol type, yielding signatures like
                // `combine(you: Combinable) -> Combinable` instead of `-> T`.
                ProtocolMethodInfo? protoMethod =
                    protoInfo.Methods.FirstOrDefault(predicate: m =>
                        m.Name == methodName &&
                        (isFailable == null || m.IsFailable == isFailable));
                if (protoMethod != null)
                    return SynthesizeProtocolMethod(proto: protoInfo,
                        protoMethod: protoMethod,
                        ownerType: param);

                // Extension methods (default implementations) declared as
                // `routine Iterable[T].List()` are registered against the protocol's owner
                // table, NOT in `protoInfo.Methods` (which holds only the abstract signatures).
                // Resolve them through the protocol's generic definition so a generic-parameter
                // receiver (`S obeys Iterable[T]`) can call them. The returned routine keeps the
                // protocol's element params and `Me` in its signature; the caller's member-call
                // substitution block binds them (the obeys constraint maps `Iterable[T]`'s element
                // → the receiver's element, and `Me` → the receiver `param`).
                RoutineInfo? extensionMethod =
                    LookupMethod(type: protoInfo, methodName: methodName, isFailable: isFailable);
                if (extensionMethod is { OwnerType: not GenericParameterTypeInfo })
                    return extensionMethod;
            }
        }

        return null;
    }

    /// <summary>
    /// Looks up a method overload on a type using the argument types for disambiguation.
    /// This is used for operator/member dispatch where multiple wired overloads may exist
    /// on the same owner type (for example Moment.sub(Duration) and Moment.sub(Moment)).
    /// </summary>
    public RoutineInfo? LookupMethodOverload(TypeInfo type, string methodName,
        List<TypeInfo> argTypes)
    {
        // Transparent-protocol unwrap: Accessing[X] / Controlling[X] forward every method
        // to X. Mirror the unwrap in LookupMethod so overload-driven resolution (e.g. the
        // CallOverloadResolutionPass walking f-string-lowered represent calls on a
        // `Accessing[Text]` receiver) lands on Text's method instead of synthesizing a
        // protocol-dispatch stub on Accessing that has no implementers registered.
        if (type is ProtocolTypeInfo { TypeArguments: { Count: 1 } markerArgs } markerProto)
        {
            string markerBase = TypeInfo.StripTypeArgs(
                name: markerProto.GenericDefinition?.Name ?? markerProto.Name);
            if (markerBase is RuntimeContract.Accessing or RuntimeContract.Controlling)
            {
                RoutineInfo? viaInner = LookupMethodOverload(type: markerArgs[index: 0],
                    methodName: methodName, argTypes: argTypes);
                if (viaInner != null) return viaInner;
            }
        }

        var candidates = new List<RoutineInfo>();
        CollectMemberRoutineCandidates(type: type, methodName: methodName, candidates: candidates);

        // Protocol abstract methods are never a valid dispatch target on a concrete receiver —
        // RF protocols are abstract-only (no default impls). Including them would let lookup
        // pick `Equatable.eq(Self)` for `S128 == S64`, masking the integer-promotion fallback
        // and emitting an unresolved `Core.Equatable.eq` symbol at link time.
        if (type is not ProtocolTypeInfo)
        {
            candidates.RemoveAll(match: c => c.OwnerType is ProtocolTypeInfo);
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // Prefer exact arity + exact type-name matches first.
        foreach (RoutineInfo candidate in candidates)
        {
            if (candidate.Parameters.Count != argTypes.Count)
            {
                continue;
            }

            bool exactMatch = true;
            for (int i = 0; i < argTypes.Count; i++)
            {
                TypeInfo paramType = candidate.Parameters[index: i].Type;
                if (paramType is ProtocolSelfTypeInfo)
                {
                    paramType = type;
                }

                if (paramType.Name != argTypes[index: i].Name)
                {
                    exactMatch = false;
                    break;
                }
            }

            if (exactMatch)
            {
                return candidate.OwnerType is GenericParameterTypeInfo
                    ? SubstituteMethodForOwner(method: candidate, resolvedOwner: type)
                    : candidate;
            }
        }

        // Then accept assignable matches.
        foreach (RoutineInfo candidate in candidates)
        {
            if (candidate.Parameters.Count != argTypes.Count)
            {
                continue;
            }

            bool assignableMatch = true;
            for (int i = 0; i < argTypes.Count; i++)
            {
                TypeInfo paramType = candidate.Parameters[index: i].Type;
                if (paramType is ProtocolSelfTypeInfo)
                {
                    paramType = type;
                }

                if (!IsMethodArgumentAssignable(source: argTypes[index: i], target: paramType))
                {
                    assignableMatch = false;
                    break;
                }
            }

            if (assignableMatch)
            {
                return candidate.OwnerType is GenericParameterTypeInfo
                    ? SubstituteMethodForOwner(method: candidate, resolvedOwner: type)
                    : candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Synthesizes a complete RoutineInfo from a ProtocolMethodInfo, including parameters,
    /// modification category, storage, and all other metadata. Substitutes generic type
    /// parameters for instantiated generic protocols (e.g., Iterator[S64]: T -> S64).
    /// </summary>
    private RoutineInfo SynthesizeProtocolMethod(ProtocolTypeInfo proto,
        ProtocolMethodInfo protoMethod, TypeInfo ownerType) // NOSONAR S3776
    {
        // Build substitution map for generic protocols (e.g., Iterator[S64]: T -> S64)
        Dictionary<string, TypeInfo>? substitution = null;
        if (proto.TypeArguments is { Count: > 0 })
        {
            ProtocolTypeInfo genericDef = proto.GenericDefinition ?? proto;
            if (genericDef.GenericParameters is { Count: > 0 })
            {
                substitution = new Dictionary<string, TypeInfo>();
                for (int i = 0;
                     i < genericDef.GenericParameters.Count && i < proto.TypeArguments.Count;
                     i++)
                {
                    substitution[key: genericDef.GenericParameters[index: i]] =
                        proto.TypeArguments[index: i];
                }
            }
        }

        // Resolve return type with substitution
        TypeInfo? resolvedReturn = protoMethod.ReturnType;
        if (resolvedReturn != null && substitution != null)
        {
            resolvedReturn =
                SubstituteTypeInProtocol(type: resolvedReturn, substitution: substitution);
        }

        // ProtocolSelf (Me) in protocol method signatures means the concrete implementing type.
        if (resolvedReturn is ProtocolSelfTypeInfo)
            resolvedReturn = ownerType;

        // Convert ProtocolMethodInfo.ParameterTypes -> ParameterInfo list
        var parameters = new List<ParameterInfo>();
        for (int i = 0; i < protoMethod.ParameterTypes.Count; i++)
        {
            TypeInfo paramType = protoMethod.ParameterTypes[index: i];
            if (substitution != null)
            {
                paramType = SubstituteTypeInProtocol(type: paramType, substitution: substitution);
            }

            if (paramType is ProtocolSelfTypeInfo)
                paramType = ownerType;

            string paramName = i < protoMethod.ParameterNames.Count
                ? protoMethod.ParameterNames[index: i]
                : $"arg{i}";
            parameters.Add(
                item: new ParameterInfo(name: paramName, type: paramType) { Index = i });
        }

        return new RoutineInfo(name: protoMethod.Name)
        {
            OwnerType = ownerType,
            Parameters = parameters,
            ReturnType = resolvedReturn,
            IsFailable = protoMethod.IsFailable,
            MutationCategory = protoMethod.Mutation,
            Storage = protoMethod.IsInstanceMethod
                ? StorageClass.None
                : StorageClass.Common,
            AsyncStatus = AsyncStatus.None,
            IsSynthesized = true,
            Location = protoMethod.Location
        };
    }

    /// <summary>
    /// Substitutes the owner type's generic type parameters into a method's signature.
    /// For example, List[S32].add(item: T) -> List[S32].add(item: S32).
    /// </summary>
    internal RoutineInfo? SubstituteMethodForOwner(RoutineInfo method, TypeInfo resolvedOwner)
    {
        if (method.OwnerType is GenericParameterTypeInfo universalOwner)
        {
            var substitution = new Dictionary<string, TypeInfo>
            {
                [key: universalOwner.Name] = resolvedOwner
            };

            var substitutedParams = method.Parameters
                                          .Select(selector: p =>
                                               RoutineInfo.SubstituteParameterType(param: p,
                                                   substitution: substitution))
                                          .ToList();
            TypeInfo? substitutedReturn = method.ReturnType != null
                ? RoutineInfo.SubstituteType(type: method.ReturnType, substitution: substitution)
                : null;
            List<string>? methodOnlyGenericParams = method.GenericParameters?
                .Where(gp => gp != universalOwner.Name)
                .ToList();
            if (methodOnlyGenericParams?.Count == 0)
                methodOnlyGenericParams = null;

            // Keep constraints on the method's own generic params, PLUS `in [...]` (TypeEquality)
            // constraints on the OWNER's params (e.g. `Shared[T, P].claim() needs P in [...]`). The
            // owner param is already substituted on the resolved instance, but the constraint is not
            // validated here — it is preserved so the call-site verifier can check it against the
            // receiver's bound argument (otherwise a method constraint on an inherited param vanishes
            // unchecked).
            List<GenericConstraintDeclaration>? methodOnlyConstraints = method.GenericConstraints?
                .Where(c => methodOnlyGenericParams?.Contains(c.ParameterName) == true
                    || c.ConstraintType == ConstraintKind.TypeEquality)
                .ToList();
            if (methodOnlyConstraints?.Count == 0)
                methodOnlyConstraints = null;

            var resolvedUniversalMethod = new RoutineInfo(name: method.Name)
            {
                Kind = method.Kind,
                OwnerType = resolvedOwner,
                Parameters = substitutedParams,
                ReturnType = substitutedReturn,
                IsFailable = method.IsFailable,
                DeclaredMutation = method.DeclaredMutation,
                MutationCategory = method.MutationCategory,
                GenericParameters = methodOnlyGenericParams,
                GenericConstraints = methodOnlyConstraints,
                Visibility = method.Visibility,
                Location = method.Location,
                Module = method.Module,
                ModulePath = method.ModulePath,
                Annotations = method.Annotations,
                CallingConvention = method.CallingConvention,
                IsVariadic = method.IsVariadic,
                IsDangerous = method.IsDangerous,
                IsSynthesized = method.IsSynthesized,
                TypeArguments = method.TypeArguments,
                GenericDefinition = method.GenericDefinition ?? method,
                WrapperForwarderInnerMethod = method.WrapperForwarderInnerMethod,
                WrapperForwarderInnerGenericDef = method.WrapperForwarderInnerGenericDef,
                Storage = method.Storage,
                AsyncStatus = method.AsyncStatus,
                FailableVariant = method.FailableVariant,
                OriginalName = method.OriginalName
            };

            return CacheResolvedOwnerMethod(resolvedMethod: resolvedUniversalMethod);
        }

        // Build substitution map from the resolved owner's generic definition
        TypeInfo? genericDef = resolvedOwner switch
        {
            RecordTypeInfo r => r.GenericDefinition,
            EntityTypeInfo e => e.GenericDefinition,
            ProtocolTypeInfo p => p.GenericDefinition,
            // Wrapper types (Hijacked[T], Hijacked[Byte], etc.) — look up generic def by base name
            WrapperTypeInfo => LookupType(name: resolvedOwner.Name),
            _ => null
        };

        if (genericDef?.GenericParameters == null || resolvedOwner.TypeArguments == null)
        {
            return method;
        }

        var substitution2 = new Dictionary<string, TypeInfo>();
        for (int i = 0;
             i < genericDef.GenericParameters.Count && i < resolvedOwner.TypeArguments.Count;
             i++)
        {
            substitution2[key: genericDef.GenericParameters[index: i]] =
                resolvedOwner.TypeArguments[index: i];
        }

        // Specialized-receiver member (e.g. `routine List[Agent[V]].gather!()`): the method's
        // generic param V is determined by the receiver PATTERN (MeType = List[Agent[V]]), not by a
        // call-site `[..]`. Unify MeType against the concrete owner (List[Agent[S64]]) to bind V=S64
        // and fold it into the owner substitution. This makes the resolved method FULLY CONCRETE
        // (V dropped from method generics, no `[S64]` mangle suffix), so SA, reachability, GMP and
        // codegen all key on the same name — `Core.List[Agent[S64]].gather`.
        if (method.MeType is { } mePattern)
        {
            UnifyReceiverGenerics(pattern: mePattern, concrete: resolvedOwner,
                genericParams: method.GenericParameters, into: substitution2);
        }

        if (substitution2.Count == 0)
        {
            return method;
        }

        // Wrapper-forwarder: re-resolve signature against the concrete inner method instead of
        // naive name substitution (inner-T vs wrapper-T collision: both T and List[T] use T,
        // so {T: List[Character]} would map List[T].getitem!'s T to List[Character], not Character).
        // Note: wrapper types like T may be RecordTypeInfo (declared as `record` in RF),
        // not WrapperTypeInfo, so check TypeArguments.Count rather than the runtime type.
        if (method is { IsSynthesized: true, WrapperForwarderInnerMethod: { } innerGenMethod } &&
            resolvedOwner.TypeArguments is { Count: 1 } && resolvedOwner is not GenericParameterTypeInfo)
        {
            TypeInfo concreteInner = resolvedOwner.TypeArguments![index: 0];
            RoutineInfo? concreteInnerMethod = LookupMethod(
                type: concreteInner,
                methodName: innerGenMethod.Name,
                isFailable: innerGenMethod.IsFailable);
            if (concreteInnerMethod != null)
            {
                var fwdParams = concreteInnerMethod.Parameters
                    .Select(p => p.Name == "me"
                        ? p.WithSubstitutedType(newType: resolvedOwner)
                        : p)
                    .ToList();
                var resolvedWrapperForwarder = new RoutineInfo(name: method.Name)
                {
                    Kind = method.Kind,
                    OwnerType = resolvedOwner,
                    Parameters = fwdParams,
                    ReturnType = concreteInnerMethod.ReturnType,
                    IsFailable = method.IsFailable,
                    DeclaredMutation = method.DeclaredMutation,
                    MutationCategory = method.MutationCategory,
                    Visibility = method.Visibility,
                    Location = method.Location,
                    Module = method.Module,
                    ModulePath = method.ModulePath,
                    Annotations = method.Annotations,
                    CallingConvention = method.CallingConvention,
                    IsVariadic = method.IsVariadic,
                    IsDangerous = method.IsDangerous,
                    IsSynthesized = true,
                    TypeArguments = method.TypeArguments,
                    GenericDefinition = method.GenericDefinition ?? method,
                    WrapperForwarderInnerMethod = concreteInnerMethod,
                    WrapperForwarderInnerGenericDef = method.WrapperForwarderInnerGenericDef,
                    Storage = method.Storage,
                    AsyncStatus = method.AsyncStatus,
                    FailableVariant = method.FailableVariant,
                    OriginalName = method.OriginalName,
                    // Propagate method-level generic parameters from the concrete inner method so
                    // OperatorLoweringPass can monomorphize (e.g. Text.getitem![I] -> [U64]).
                    GenericParameters = concreteInnerMethod.GenericParameters ?? method.GenericParameters,
                    GenericConstraints = concreteInnerMethod.GenericConstraints ?? method.GenericConstraints,
                };
                return CacheResolvedOwnerMethod(resolvedMethod: resolvedWrapperForwarder);
            }
            // The concrete inner type does not have this forwarded method — do not fabricate it.
            return null;
        }

        // Substitute types in parameters
        var substitutedParams2 = method.Parameters
                                      .Select(selector: p =>
                                           RoutineInfo.SubstituteParameterType(param: p,
                                               substitution: substitution2))
                                      .ToList();

        // Substitute return type
        // Special case: if return type IS the owner's generic def (e.g. Maybe.store returns Maybe_def),
        // the concrete return type is resolvedOwner itself (Maybe[ListNode[S64]], not Maybe_def).
        TypeInfo? substitutedReturn2;
        if (method.ReturnType != null && genericDef != null &&
            (ReferenceEquals(objA: method.ReturnType, objB: genericDef) ||
             method.ReturnType.Name == genericDef.Name && method.ReturnType.IsGenericDefinition))
        {
            substitutedReturn2 = resolvedOwner;
        }
        else
        {
            substitutedReturn2 = method.ReturnType != null
                ? RoutineInfo.SubstituteType(type: method.ReturnType, substitution: substitution2)
                : null;
        }

        // If return type is still a generic definition (e.g., track() -> Tracked_def when
        // Tracked[T] was declared), instantiate it using the substitution map.
        if (substitutedReturn2 is { IsGenericDefinition: true, GenericParameters: { } retGenericParams } retDef)
        {
            var retArgs = retGenericParams
                .Select(selector: p => substitution2.TryGetValue(p, out TypeInfo? subType) ?
                    subType : null)
                .ToList();
            if (retArgs.All(predicate: a => a != null))
                substitutedReturn2 = retDef.CreateInstance(typeArguments: retArgs.Select(selector: a => a!).ToList());
        }

        // Only keep method-level generic parameters (owner params are now resolved)
        List<string>? methodOnlyGenericParams2 = method.GenericParameters?
            .Where(gp => !substitution2.ContainsKey(gp))
            .ToList();
        if (methodOnlyGenericParams2?.Count == 0)
            methodOnlyGenericParams2 = null;

        // Keep method-level constraints PLUS owner-param `in [...]` (TypeEquality) constraints, so a
        // method constraint on an inherited param (e.g. `Shared[T, P].claim() needs P in [...]`)
        // survives to be validated at the call site against the receiver's bound argument.
        List<GenericConstraintDeclaration>? methodOnlyConstraints2 = method
            .GenericConstraints?
            .Where(c => methodOnlyGenericParams2?.Contains(c.ParameterName) == true
                || c.ConstraintType == ConstraintKind.TypeEquality)
            .ToList();
        if (methodOnlyConstraints2?.Count == 0)
            methodOnlyConstraints2 = null;

        var resolvedOwnerMethod = new RoutineInfo(name: method.Name)
        {
            Kind = method.Kind,
            OwnerType = resolvedOwner,
            // Carry the specialized-receiver pattern (e.g. List[Agent[V]]) unchanged: V is a method
            // generic param, not an owner param, so owner substitution leaves it intact. Receiver-
            // based method-generic inference at the call site needs this pattern to bind V. (The
            // Suflae entity `me`=Roamed[E] handle is substituted downstream in
            // GenericMonomorphizationPass, so it does not need owner substitution here — and doing it
            // here mistyped some stdlib Hijacked `me` receivers, tripping RF-S627.)
            MeType = method.MeType,
            Parameters = substitutedParams2,
            ReturnType = substitutedReturn2,
            IsFailable = method.IsFailable,
            DeclaredMutation = method.DeclaredMutation,
            MutationCategory = method.MutationCategory,
            GenericParameters = methodOnlyGenericParams2,
            GenericConstraints = methodOnlyConstraints2,
            Visibility = method.Visibility,
            Location = method.Location,
            Module = method.Module,
            ModulePath = method.ModulePath,
            Annotations = method.Annotations,
            CallingConvention = method.CallingConvention,
            IsVariadic = method.IsVariadic,
            IsDangerous = method.IsDangerous,
            IsSynthesized = method.IsSynthesized,
            TypeArguments = method.TypeArguments,
            GenericDefinition = method.GenericDefinition ?? method,
            WrapperForwarderInnerMethod = method.WrapperForwarderInnerMethod,
            WrapperForwarderInnerGenericDef = method.WrapperForwarderInnerGenericDef,
            Storage = method.Storage,
            AsyncStatus = method.AsyncStatus,
            FailableVariant = method.FailableVariant,
            OriginalName = method.OriginalName
        };
        return CacheResolvedOwnerMethod(resolvedMethod: resolvedOwnerMethod);
    }

    /// <summary>
    /// Public entry to register a fully-resolved RoutineInfo into the resolutions cache,
    /// keyed by <see cref="RoutineInfo.RegistryKey"/>. Returns the cached instance if one
    /// already exists for that key; otherwise inserts and returns <paramref name="resolvedMethod"/>.
    /// Used by reachability/instantiation when it constructs concrete routine clones (e.g.
    /// substituting method-level TypeArguments after owner monomorphization) that need to be
    /// visible to <c>GenericMonomorphizationPass</c> via <see cref="GetAllRoutineResolutions"/>.
    /// </summary>
    public RoutineInfo RegisterRoutineResolution(RoutineInfo resolvedMethod)
        => CacheResolvedOwnerMethod(resolvedMethod: resolvedMethod);

    /// <summary>
    /// Removes a routine resolution entry by its (current) registry key. Used when a
    /// resolution's parameter types have been mutated in-place (e.g.
    /// MarkerProtocolDesugarPass rewriting Accessing[T] → T) so the resolution needs to
    /// be re-inserted under its new <see cref="RoutineInfo.RegistryKey"/>.
    /// </summary>
    public bool UnregisterRoutineResolution(string oldKey)
        => _routineResolutions.Remove(key: oldKey);

    private RoutineInfo CacheResolvedOwnerMethod(RoutineInfo resolvedMethod)
    {
        // A universal method substituted onto a generic-def owner (e.g. `Node.retain()`) produces
        // the same RegistryKey as one substituted onto a concrete instantiation (`Node[T_param].retain()`)
        // because GetTypeIdentity collapses both to "Module.Name[Param]". Caching the first form
        // would then return wrongly-substituted return types (Retained[Node] instead of
        // Retained[Node[T_param]]) for subsequent lookups on the resolution. Only honor the cache
        // when the owner type is referentially the same.
        if (_routineResolutions.TryGetValue(key: resolvedMethod.RegistryKey,
                value: out RoutineInfo? cached)
            && ReferenceEquals(objA: cached.OwnerType, objB: resolvedMethod.OwnerType))
        {
            return cached;
        }

        _routineResolutions[key: resolvedMethod.RegistryKey] = resolvedMethod;
        return resolvedMethod;
    }

    internal void CollectMemberRoutineCandidates(TypeInfo type, string methodName, List<RoutineInfo> candidates)
    {
        if (_routinesByOwner.TryGetValue(key: type.FullName, value: out Dictionary<string, List<RoutineInfo>>? byName)
            && byName.TryGetValue(key: methodName, value: out List<RoutineInfo>? methods))
        {
            candidates.AddRange(methods);
        }

        if (type is ProtocolTypeInfo proto)
        {
            foreach (ProtocolMethodInfo protoMethod in proto.Methods.Where(predicate: m => m.Name == methodName))
            {
                candidates.Add(item: SynthesizeProtocolMethod(proto: proto,
                    protoMethod: protoMethod,
                    ownerType: type));
            }
        }

        if (type.IsGenericResolution)
        {
            TypeInfo? genericDef = type switch
            {
                RecordTypeInfo r => r.GenericDefinition,
                EntityTypeInfo e => e.GenericDefinition,
                ProtocolTypeInfo p => p.GenericDefinition,
                WrapperTypeInfo wt => LookupType(name: wt.Name),
                _ => null
            };

            if (genericDef != null)
            {
                var genericCandidates = new List<RoutineInfo>();
                CollectMemberRoutineCandidates(type: genericDef, methodName: methodName, candidates: genericCandidates);
                foreach (RoutineInfo genericCandidate in genericCandidates)
                {
                    if (genericCandidate.OwnerType is GenericParameterTypeInfo)
                    {
                        candidates.Add(item: genericCandidate);
                    }
                    else
                    {
                        RoutineInfo? substituted = SubstituteMethodForOwner(method: genericCandidate,
                            resolvedOwner: type);
                        if (substituted != null)
                            candidates.Add(item: substituted);
                    }
                }
            }
        }

        if (DefaultMemberRoutine(methodName: methodName) is { } defaultMember)
        {
            candidates.Add(item: SubstituteMethodForOwner(method: defaultMember,
                resolvedOwner: type)!);
        }

        List<TypeInfo>? protocols = type switch
        {
            RecordTypeInfo r => r.ImplementedProtocols,
            EntityTypeInfo e => e.ImplementedProtocols,
            _ => null
        };

        if (protocols != null)
        {
            foreach (TypeInfo protocol in protocols)
            {
                CollectMemberRoutineCandidates(type: protocol, methodName: methodName, candidates: candidates);
            }
        }
    }

    private static bool IsMethodArgumentAssignable(TypeInfo source, TypeInfo target)
    {
        // Compare by Name (includes generic args, e.g. "List[S64]") rather than FullName
        // because arg types constructed during SA may lack a module prefix while registry
        // types are always module-qualified — FullName comparison would break that pairing.
        if (source.Name == target.Name)
        {
            return true;
        }

        if (target is ProtocolTypeInfo targetProto)
        {
            // For generic-protocol targets (e.g. Accessing[Bytes]), require the type-argument
            // to match the source. Without this check, ANY type matches ANY generic protocol —
            // CStr.create(Accessing[Bytes]) "accepts" a Text arg, beating
            // CStr.create(Accessing[Text]) by source order and producing garbled output
            // when SA emits a call to the wrong overload.
            if (targetProto.TypeArguments is { Count: 1 } pTypeArgs)
            {
                return pTypeArgs[0].Name == source.Name
                    || pTypeArgs[0].FullName == source.FullName;
            }
            return true;
        }

        if (target.Name == "Me")
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets all registered routines, excluding pruned generic stubs.
    /// </summary>
    /// <returns>An enumerable of all registered routines.</returns>
    public IEnumerable<RoutineInfo> GetAllRoutines()
    {
        // `_routines` indexes each first-overload routine under BOTH its RegistryKey and its
        // (owner-qualified) BaseName, so `.Values` yields that object twice. Dedup by reference so
        // every consumer (codegen, synthesis passes, the registered-count) sees each routine once
        // instead of ~2x — the inflated count that made a trivial program look like it had 12k
        // routines when it actually has ~6.6k.
        IEnumerable<RoutineInfo> all = _prunedGenericBases.Count == 0
            ? _routines.Values.Distinct()
            : _routines.Values.Distinct().Where(r => !_prunedGenericBases.Contains(r.BaseName));
        // Exclude:
        // - @innate routines: compile-time-only stubs (type_name, module_name, etc.) that
        //   BuilderServiceInliningPass folds to literals; they have no body and must never reach codegen.
        // - Routines on generic-definition owner types: bodies are synthesised per concrete instance;
        //   emitting them for the definition produces [T]/[K,V] placeholders in LLVM.
        // - Routines on None owners: None -> LLVM void, illegal as a parameter type.
        // - Routines on non-live concrete generic owner types: phantom instantiations.
        return all.Where(r =>
                      !r.Annotations.Contains(value: "innate") &&
                      (r.OwnerType == null ||
                       (!r.OwnerType.IsNone &&
                        !r.OwnerType.IsGenericDefinition &&
                        (r.OwnerType.TypeArguments == null ||
                         r.OwnerType.TypeArguments.All(a => !a.IsNone)) &&
                        IsConcreteTypeLive(r.OwnerType))));
    }

    /// <summary>
    /// Gets all concrete generic routine resolutions created from generic routine definitions.
    /// </summary>
    public IEnumerable<RoutineInfo> GetAllRoutineResolutions()
    {
        return _routineResolutions.Values;
    }

    /// <summary>
    /// Removes generic-definition routines that were never instantiated for any concrete type.
    /// Called at the end of Phase 6 global desugaring, after all variant and wired bodies have
    /// been generated. Routines whose <c>BaseName</c> has no concrete entry in either
    /// <c>_routines</c> or <c>_routineResolutions</c> are marked as pruned and excluded from
    /// subsequent <see cref="GetAllRoutines"/> calls (codegen, AST printer, etc.).
    /// </summary>
    public void PruneUnusedGenericRoutines()
    {
        // Collect base names that have at least one concrete (non-generic) instance.
        var concreteBases = new HashSet<string>(capacity: _routines.Count + _routineResolutions.Count);
        foreach (RoutineInfo r in _routines.Values)
        {
            if (!r.IsGenericDefinition)
                concreteBases.Add(r.BaseName);
        }
        foreach (RoutineInfo r in _routineResolutions.Values)
        {
            concreteBases.Add(r.BaseName);
        }

        // Mark every generic definition whose base has no concrete instance.
        foreach (RoutineInfo r in _routines.Values)
        {
            if (r.IsGenericDefinition && !concreteBases.Contains(r.BaseName))
                _prunedGenericBases.Add(r.BaseName);
        }

        // Also prune routines with <error> in parameter or return types.
        // These arise from implicit-generic routines (e.g. `routine max!(values...: T) needs T obeys P`)
        // where the generic parameter T was never added to GenericParameters, causing type resolution
        // to fall back to ErrorTypeInfo. Such routines can never be called with valid types.
        foreach (RoutineInfo r in _routines.Values)
        {
            if (r.Parameters.Any(p => p.Type.Name.Contains(value: "<error>"))
                || (r.ReturnType?.Name.Contains(value: "<error>") ?? false))
            {
                _prunedGenericBases.Add(r.BaseName);
            }
        }
    }

    /// <summary>
    /// Returns true if the routine with the given base name was pruned as an unused generic.
    /// Used by the desugaring pipeline to also evict matching entries from the variant-body dictionary.
    /// </summary>
    public bool IsRoutinePruned(string baseName) => _prunedGenericBases.Contains(baseName);

    /// <summary>
    /// Gets the methods registered DIRECTLY on a type's own table (raw). Returns empty for a generic
    /// resolution like <c>List[S64]</c> whose concrete owner is never written into
    /// <c>_routinesByOwner</c>. Callers that need the resolved own-method set of a generic resolution
    /// (e.g. unified teardown/copy lifecycle resolution) must use
    /// <see cref="GetOwnMethodsResolved"/> instead.
    /// </summary>
    /// <param name="type">The type to get methods for.</param>
    /// <returns>An enumerable of all methods for the type.</returns>
    public IEnumerable<RoutineInfo> GetMethodsForType(TypeInfo type)
    {
        return _routinesByOwner.TryGetValue(key: type.FullName, value: out Dictionary<string, List<RoutineInfo>>? byName)
            ? OwnerMethods(byName: byName)
            : [];
    }

    private readonly Dictionary<string, List<RoutineInfo>> _methodsForTypeCache =
        new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// The unified own-method resolver: returns the methods a type provides ITSELF, including — for a
    /// generic resolution whose concrete owner is absent from <c>_routinesByOwner</c> — the generic
    /// definition's own methods substituted for this owner (via <see cref="SubstituteMethodForOwner"/>).
    /// This is the single source of truth the find-side and the lifecycle (teardown/copy) passes share,
    /// so <c>GetMethodsForType</c> (raw) and <see cref="LookupMethod"/> can no longer disagree about
    /// whether e.g. <c>Retained[Tracer]</c> has a <c>destroy</c>.
    ///
    /// <para>Plain OWN-method enumeration only — no protocol-method synthesis, no universal-method
    /// stub, no marker/wrapper unwrap (those are dispatch concerns). That keeps it from surfacing the
    /// no-owner universal <c>T.destroy</c> stub for a borrowed referent. Results are cached per
    /// <c>FullName</c>; only fully-concrete resolutions are admitted to the cache.</para>
    /// </summary>
    public IEnumerable<RoutineInfo> GetOwnMethodsResolved(TypeInfo type)
    {
        if (_routinesByOwner.TryGetValue(key: type.FullName, value: out Dictionary<string, List<RoutineInfo>>? ownByName))
            return OwnerMethods(byName: ownByName);

        if (!type.IsGenericResolution ||
            type.TypeArguments is null ||
            type.TypeArguments.Any(predicate: a => a is GenericParameterTypeInfo or ErrorTypeInfo || a.IsNone))
            return [];

        if (_methodsForTypeCache.TryGetValue(key: type.FullName, value: out List<RoutineInfo>? cached))
            return cached;

        var result = new List<RoutineInfo>();
        TypeInfo? genericDef = type switch
        {
            RecordTypeInfo r => r.GenericDefinition,
            EntityTypeInfo e => e.GenericDefinition,
            ProtocolTypeInfo p => p.GenericDefinition,
            WrapperTypeInfo wt => LookupType(name: wt.Name),
            _ => null
        };
        if (genericDef != null && !ReferenceEquals(objA: genericDef, objB: type) &&
            _routinesByOwner.TryGetValue(key: genericDef.FullName, value: out Dictionary<string, List<RoutineInfo>>? defByName))
        {
            foreach (RoutineInfo m in OwnerMethods(byName: defByName))
            {
                // Universal (T-owned) methods are not the type's OWN methods — skip them so the
                // no-owner T.destroy stub never leaks in for a borrowed referent.
                if (m.OwnerType is GenericParameterTypeInfo) continue;
                RoutineInfo? sub = SubstituteMethodForOwner(method: m, resolvedOwner: type);
                if (sub != null) result.Add(item: sub);
            }
        }

        _methodsForTypeCache[key: type.FullName] = result;
        return result;
    }

    /// <summary>The owned-value lifecycle of a type, resolved through the single unified own-method
    /// resolver (<see cref="GetOwnMethodsResolved"/>) so the teardown and copy passes agree about
    /// generic resolutions like <c>Retained[Tracer]</c> / <c>Maybe[Text]</c>.</summary>
    public readonly record struct Lifecycle(RoutineInfo? Store, RoutineInfo? Destroy, bool IsBorrow);

    /// <summary>
    /// Lifecycle and reference are governed by the four wired routines
    /// <c>create</c>/<c>refer</c>/<c>control</c>/<c>destroy</c> — the system is AGNOSTIC to
    /// specific wrapper-type names (no hardcoded Viewing/Modifying/Hijacked list). Teardown simply calls
    /// <c>destroy</c> uniformly: it is a real destructor on owning types and a no-op on the
    /// access/borrow wrappers, so firing it is always safe by construction. The only thing this gate
    /// excludes is the ABSTRACT tier — generic parameters and protocols (the latter also covering the
    /// <c>Accessing</c>/<c>Controlling</c> access markers) — which have no concrete <c>destroy</c> to
    /// resolve. The one remaining hazard, a <c>T</c> reference bound to the bare referent type via the
    /// reference primitives <c>refer</c>/<c>control</c>/<c>as_entity</c>, is excluded at the binding
    /// site by <c>ScopeTeardownLoweringPass.IsViewBinding</c> (keyed on the producing verb, since the
    /// binding's static type is the referent itself, not a borrow wrapper).
    /// </summary>
    private static bool IsBorrowTier(TypeInfo type) =>
        type is GenericParameterTypeInfo or ProtocolTypeInfo;

    /// <summary>
    /// If <paramref name="type"/> is an RC wrapper (Retained/Tracked/Shared/Watched/Roamed) — matched
    /// by its generic base name — returns that base name, else null. Used to redirect the abstract
    /// <c>store</c> hook to the wrapper's concrete refcount copy verb (see
    /// <c>RuntimeContract.RcCopyVerb</c>).
    /// </summary>
    internal static string? GetRcWrapperBaseName(TypeInfo type)
    {
        // Prefer the generic DEFINITION's name (a resolution's own Name may carry a module prefix, e.g.
        // `Core.Roamed[...]`, which would not match the bare `Roamed` allowlist). `BareName` drops the
        // `[typeargs]` suffix, so no manual bracket parsing here.
        string? baseName = type switch
        {
            RecordTypeInfo { GenericDefinition: { } gd } => gd.BareName,
            WrapperTypeInfo wt => wt.BareName,
            RecordTypeInfo r => r.BareName,
            _ => null
        };

        return baseName is not null && RuntimeContract.RcWrapperBaseNames.Contains(item: baseName)
            ? baseName
            : null;
    }

    /// <summary>
    /// Resolves a type's owned-value lifecycle: its retaining <c>store</c> (a hand-written, i.e.
    /// non-synthesized, zero-arg <c>store</c> on a record — the managed-leaf retain hook), its
    /// <c>destroy</c> (preferring the user-written one), and whether it is a borrow-tier type. The
    /// teardown and copy lowering passes both drive off THIS one decision, so a value is either both
    /// retaining-copied and balanced-destroyed, or neither — never the asymmetry that double-freed
    /// before. Resolved via <see cref="GetOwnMethodsResolved"/>, so it works for generic resolutions.
    /// </summary>
    // True iff a method's owner-level `needs <param> obeys <Protocol>` constraints HOLD for the concrete
    // owner's type args. e.g. `Array[T,N].store() needs T obeys Storable` — for `Array[Node]` the map
    // T→Node fails (an entity is not storable), so the store hook must NOT be handed to the copy-lowering
    // pass: injecting a `needs`-gated method whose constraint is unmet produces a body that can't resolve
    // its inner `element.store()` → the "declared+called but never defined" over-prune crash. Monomorph's
    // ConstraintsSatisfied deliberately trusts SA for `Obeys`, and no SA site rejects `var b = a` on a
    // container of a non-storable element, so this is the guard that keeps the injection honest.
    private bool OwnerConstraintsSatisfied(RoutineInfo method, TypeInfo ownerType)
    {
        if (method.GenericConstraints is not { Count: > 0 } constraints)
            return true;
        List<string>? paramNames =
            (ownerType as RecordTypeInfo)?.GenericDefinition?.GenericParameters
            ?? ownerType.GenericParameters;
        List<TypeInfo>? args = ownerType.TypeArguments;
        if (paramNames is null || args is null)
            return true;
        var subs = new Dictionary<string, TypeInfo>(comparer: StringComparer.Ordinal);
        for (int i = 0; i < paramNames.Count && i < args.Count; i++)
            subs[key: paramNames[i]] = args[i];
        foreach (GenericConstraintDeclaration c in constraints)
        {
            if (subs.TryGetValue(key: c.ParameterName, value: out TypeInfo? actual)
                && !ImplementerSatisfiesConstraint(implementer: actual, constraint: c))
                return false;
        }
        return true;
    }

    /// <summary>Returns the <see cref="Lifecycle"/> (store/destroy hooks) for <paramref name="type"/>, or a borrow-tier sentinel for generic/protocol types.</summary>
    public Lifecycle GetLifecycle(TypeInfo type)
    {
        if (IsBorrowTier(type: type))
            return new Lifecycle(Store: null, Destroy: null, IsBorrow: true);

        List<RoutineInfo> own = GetOwnMethodsResolved(type: type).ToList();
        RoutineInfo? destroy = own
            .Where(predicate: m => m.Name == "destroy" && m.Parameters.Count == 0)
            .OrderBy(keySelector: m => m.IsSynthesized ? 1 : 0)
            .FirstOrDefault();
        // The store-site hook: the verb the copy-lowering pass injects at each `store` point to make
        // an aliased value sound. For records/RC wrappers it is the retaining `store`; for a variant
        // with a destructible arm it is the deep `copy` (a bitwise alias would double-free the heap arm).
        RoutineInfo? store = null;
        // Variant MUST be checked before RecordTypeInfo: VariantTypeInfo is a RecordTypeInfo subclass,
        // so `type is RecordTypeInfo` would otherwise capture variants and give them the record
        // field-walk copy — but a variant is a { tag, payload } union whose deep copy needs tag
        // dispatch (BuildVariantCopyBody). Using the record copy on a variant double-frees / corrupts
        // its heap arm (the nested_serialize regression).
        // RC wrappers (Retained/Tracked/Shared/Watched/Roamed) define no literal `store` method — their
        // retaining copy IS the refcount verb (retain/track/share/watch/roam). LookupMethod redirects
        // `store`→that verb, but GetOwnMethodsResolved (below) never surfaces a `store` for them, so the
        // record branch's name=="store" filter would miss it → Store=null → no retain injected. A container
        // storing a Roamed element (`List[Roamed[E]].add_last`'s `poke(value)`) then aliases without a
        // refcount bump → the element dangles when the caller's handle releases (the List[entity] UAF).
        // Resolve the copy verb through the redirect so instantiated generic bodies get a real retaining
        // copy — checked BEFORE the RecordTypeInfo branch (RC wrappers ARE records). SUFLAE-ONLY: in SF an
        // `entity` is a `Roamed` and containers hold `Roamed[E]` elements that MUST auto-retain on store; in
        // RazorForge `Roamed`/RC handles are managed MANUALLY (`.roam()`/`.release()` in danger blocks, e.g.
        // roamed_cycle_api), so auto-retain here would double-count and leak. Gate to the SF compile.
        if (Language == TypeModel.Enums.Language.Suflae && GetRcWrapperBaseName(type: type) is not null)
        {
            // RC copy verb is `share` (the refcount-bump co-owner mint) — renamed from the STEP-3 unified
            // `store` so it reads as the explicit-share op and is distinct from value-record `store`.
            store = LookupMethod(type: type, methodName: RuntimeContract.RefCount.Share);
        }
        else if (type is VariantTypeInfo variant && VariantHasDestructibleArm(variant: variant))
        {
            // A variant with a destructible arm (an arm whose own destroy does real work — a heap
            // entity like a collection, a managed leaf like Text, or a record that transitively owns
            // one) would DOUBLE-FREE if bitwise-aliased: two copies of the variant both tear down the
            // same heap arm. Its synthesized deep `copy` (WiredRoutinePass.BuildVariantCopyBody,
            // tag-dispatch → reconstruct each destructible arm with `arm.copy()`) makes an independent
            // value. Return it as the store hook so the copy-lowering pass injects it at every store
            // point (record-ctor field-store, call-arg, assignment) — exactly where a bare alias would
            // otherwise be torn down by both owners.
            store = own.FirstOrDefault(predicate: m =>
                m.Name == "copy" && m.Parameters.Count == 0);
        }
        else if (type is RecordTypeInfo rec)
        {
            // A hand-written store is always a retaining copy (the managed-leaf retain hook,
            // e.g. Text/Decimal bumping a shared controller). Skip it when its owner-level `needs`
            // constraint is unmet for the concrete type — e.g. `Array[SomeEntity]` whose element-loop
            // store `needs T obeys Storable` (an entity is not storable): returning it would inject a
            // store whose body can't resolve → over-prune crash. Store=null ⇒ the value is not storable
            // and the implicit copy is (correctly) not injected.
            store = own.FirstOrDefault(predicate: m =>
                m.Name == "store" && m.Parameters.Count == 0 && !m.IsSynthesized
                && OwnerConstraintsSatisfied(method: m, ownerType: type));

            // The synthesized record store is field-delegating (WiredRoutinePass.
            // BuildRecordCopyBody) — symmetric with the field-delegating synthesized destroy.
            // Treat it as a retaining copy iff some field itself needs one, so it gets injected
            // at copy sites and balances the per-field destroy at teardown (else: double-free).
            if (store is null && RecordHasRetainingField(record: rec))
                store = own.FirstOrDefault(predicate: m =>
                    m.Name == "store" && m.Parameters.Count == 0);
        }
        return new Lifecycle(Store: store, Destroy: destroy, IsBorrow: false);
    }

    /// <summary>
    /// Whether a variant has at least one arm whose payload owns a real destructor — i.e. an arm type
    /// with a non-borrow <c>destroy</c> (a heap entity/collection, a managed leaf like <c>Text</c>, or
    /// a record that transitively owns one). Such an arm double-frees on bitwise alias, so the variant
    /// needs a synthesized deep <c>copy</c>. None/None/scalar arms are safe to bitwise-copy and are
    /// ignored. Drives the variant branch of <see cref="GetLifecycle"/> and the copy/Copyable synthesis.
    /// </summary>
    public bool VariantHasDestructibleArm(VariantTypeInfo variant)
    {
        if (variant.IsGenericDefinition)
            return false;

        foreach (VariantMemberInfo member in variant.Members)
        {
            if (member.IsNone || member.Type is null)
                continue;

            Lifecycle armLc = GetLifecycle(type: member.Type);
            if (!armLc.IsBorrow && armLc.Destroy is not null)
                return true;
            // An ENTITY arm is a heap reference with a destructor and double-frees on bitwise alias,
            // even when its (generic-instance) destructor isn't materialized yet at this phase — so
            // GetLifecycle reports a null Destroy. Recognize it directly by kind (mirrors the copy
            // body in WiredRoutinePass.BuildVariantCopyBody, which copies every non-borrow arm).
            if (member.Type is EntityTypeInfo)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a record transitively contains a field that needs a retaining copy — i.e. a
    /// field whose type has a hand-written <c>store</c> (a managed leaf such as <c>Text</c> or
    /// <c>Decimal</c>), or a composite record that itself contains one. Drives whether the
    /// synthesized field-delegating <c>store</c> counts as retaining in <see cref="GetLifecycle"/>.
    /// </summary>
    private bool RecordHasRetainingField(RecordTypeInfo record,
        HashSet<string>? visited = null)
    {
        if (record.HasDirectBackendType || record.MemberVariables is null)
            return false;
        visited ??= new HashSet<string>();
        if (!visited.Add(item: record.FullName ?? record.Name))
            return false; // recursive-record cycle guard

        foreach (MemberVariableInfo field in record.MemberVariables)
        {
            if (field.Type is not RecordTypeInfo fieldRec)
                continue;
            List<RoutineInfo> fieldOwn = GetOwnMethodsResolved(type: fieldRec).ToList();
            if (fieldOwn.Any(predicate: m =>
                    m.Name == "store" && m.Parameters.Count == 0 && !m.IsSynthesized))
                return true;
            if (RecordHasRetainingField(record: fieldRec, visited: visited))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when a value's <c>destroy</c> is transitively a no-op — nothing to free, no user side
    /// effect — so its scope-exit teardown call can be ELIDED. Every synthesized <c>destroy</c> is a
    /// (possibly empty) chain of field/arm destroys; when the whole tree bottoms out in scalars, the
    /// chain is pure <c>ret void</c>, yet the calls are NOT stripped by the optimizer (external linkage)
    /// and pin the value's alloca, blocking SROA. Skipping the call lets a pure-scalar record scalarize.
    ///
    /// A value is NON-trivially destructible (needs the call) when it (or, recursively, a field/arm/
    /// element) OWNS a resource or carries a user teardown: an <c>entity</c> (heap identity), an RC
    /// wrapper (refcount release), a managed leaf with a hand-written <c>store</c>/<c>destroy</c>
    /// (<c>Text</c>/<c>Decimal</c>), an RC-wrapper field (the separate <c>HasRCFields</c> teardown
    /// path), a variant with a destructible arm, or ANY user-written <c>destroy</c>. Abstract/unknown
    /// shapes return false (conservative — keep the call). Mirrors the ownership signals
    /// <see cref="GetLifecycle"/>, <see cref="VariantHasDestructibleArm"/> and
    /// <see cref="RecordTypeInfo.HasRCFields"/> already trust, so it can never disagree with what
    /// teardown/copy consider owning.
    /// </summary>
    public bool IsTriviallyDestructible(TypeInfo type, HashSet<string>? visited = null)
    {
        // Borrow/view tier owns nothing — no teardown either way.
        if (IsBorrowTier(type: type))
            return true;

        // Raw-pointer wrapper `Hijacked[T]` is USER-MANAGED: the holder frees it explicitly via
        // `invalidate()`. The compiler must NEVER auto-tear it down — its `destroy` is a no-op, and
        // auto-freeing a pointer the user also frees is a double-free. So it is trivially destructible
        // (skipped by teardown). (`CPtr` is `@llvm("ptr")` and already trivial via HasDirectBackendType.)
        if (type is WrapperTypeInfo && type.Name == RuntimeContract.Hijacked)
            return true;

        switch (type)
        {
            // Abstract types have no concrete destructor to reason about — be conservative.
            case GenericParameterTypeInfo or ProtocolTypeInfo:
                return false;
            // Heap reference with identity + destructor.
            case EntityTypeInfo:
                return false;
            // A tuple is trivial iff every element is (its fields are the elements).
            case TupleTypeInfo tuple:
                return tuple.ElementTypes.All(predicate: e => IsTriviallyDestructible(type: e, visited: visited));
        }

        // RC wrappers (Retained/Tracked/Shared/Watched/Roamed) release a refcounted controller.
        if (GetRcWrapperBaseName(type: type) is not null)
            return false;

        // A user-written (non-synthesized) destroy may have observable side effects even on a
        // pointer-free value — it must run.
        if (GetOwnMethodsResolved(type: type).Any(predicate: m =>
                m.Name == "destroy" && m.Parameters.Count == 0 && !m.IsSynthesized))
            return false;

        if (type is VariantTypeInfo variant)
            return !VariantHasDestructibleArm(variant: variant);

        if (type is RecordTypeInfo rec)
        {
            // Scalar-backed records (S64/F64/Bool/Character/CPtr/Hijacked…) have a no-op destroy.
            if (rec.HasDirectBackendType)
                return true;
            // Generic definition without concrete args — analysed via monomorphisation; be conservative.
            if (rec is { IsGenericDefinition: true, TypeArguments: null or { Count: 0 } })
                return false;
            // RC-wrapper fields tear down via the dedicated HasRCFields path.
            if (rec.HasRCFields)
                return false;
            if (rec.MemberVariables is null)
                return true;
            visited ??= new HashSet<string>(comparer: StringComparer.Ordinal);
            if (!visited.Add(item: rec.FullName ?? rec.Name))
                return true; // recursive-record cycle guard (value records can't truly recurse)
            foreach (MemberVariableInfo field in rec.MemberVariables)
                if (!IsTriviallyDestructible(type: field.Type, visited: visited))
                    return false;
            return true;
        }

        // Unknown shape — keep the teardown call.
        return false;
    }

    /// <summary>
    /// Gets or creates a resolved generic routine.
    /// </summary>
    /// <param name="genericDef">The generic routine definition.</param>
    /// <param name="typeArguments">The type arguments for resolution.</param>
    /// <returns>The resolved routine (cached if already created).</returns>
    public RoutineInfo GetOrCreateRoutineResolution(RoutineInfo genericDef,
        List<TypeInfo> typeArguments)
    {
        RoutineInfo resolved = genericDef.CreateInstance(typeArguments: typeArguments);
        string key = resolved.RegistryKey;

        if (_routineResolutions.TryGetValue(key: key, value: out RoutineInfo? existing))
        {
            return existing;
        }

        _routineResolutions[key: key] = resolved;

        return resolved;
    }

    #endregion

    #region Protocol Type Substitution

    /// <summary>
    /// Recursively substitutes generic type parameters in a type.
    /// Handles both direct parameters (T -> S64) and composite types (Iterator[T] -> Iterator[S64]).
    /// </summary>
    private TypeInfo SubstituteTypeInProtocol(TypeInfo type,
        Dictionary<string, TypeInfo> substitution)
    {
        // Direct substitution for generic parameters
        if (type is GenericParameterTypeInfo &&
            substitution.TryGetValue(key: type.Name, value: out TypeInfo? sub))
        {
            return sub;
        }

        // Recursive substitution in type arguments
        if (type.TypeArguments is not { Count: > 0 })
        {
            return type;
        }

        bool anyChanged = false;
        var newArgs = new List<TypeInfo>();
        foreach (TypeInfo arg in type.TypeArguments)
        {
            TypeInfo resolved = SubstituteTypeInProtocol(type: arg, substitution: substitution);
            newArgs.Add(item: resolved);
            if (!ReferenceEquals(objA: resolved, objB: arg))
            {
                anyChanged = true;
            }
        }

        if (!anyChanged)
        {
            return type;
        }

        // Get the generic definition and create a new instance with substituted args
        TypeInfo? genDef = type switch
        {
            EntityTypeInfo e => e.GenericDefinition,
            RecordTypeInfo r => r.GenericDefinition,
            ProtocolTypeInfo p => p.GenericDefinition,
            _ => null
        };

        if (genDef != null)
        {
            return GetOrCreateResolution(genericDef: genDef, typeArguments: newArgs);
        }

        return type;
    }

    /// <summary>
    /// Returns all methods registered for the given owner type (by FullName key).
    /// Used by SA's eager wrapper-forwarder synthesis to enumerate inner-type methods.
    /// </summary>
    public List<RoutineInfo> GetMethodsForOwner(TypeInfo ownerType)
    {
        return _routinesByOwner.TryGetValue(key: ownerType.FullName, value: out Dictionary<string, List<RoutineInfo>>? byName)
            ? OwnerMethods(byName: byName).ToList()
            : [];
    }

    /// <summary>
    /// Enumerates every registered member routine object exactly once. <c>_routinesByOwner</c> holds
    /// the full per-owner method lists (including all overloads), which is the comprehensive set the
    /// wired-ness inference pass must iterate. Deduped by reference because the same routine object can
    /// appear under multiple owner keys (e.g. a shell/canonical duplicate of a generic definition).
    /// </summary>
    public IEnumerable<RoutineInfo> EnumerateMemberRoutines()
    {
        var seen = new HashSet<RoutineInfo>(comparer: ReferenceEqualityComparer.Instance);
        foreach ((string ownerKey, Dictionary<string, List<RoutineInfo>> byName) in _routinesByOwner)
        {
            if (ownerKey == FreeOwnerKey) continue; // free functions are not member routines
            foreach (RoutineInfo r in OwnerMethods(byName: byName))
                if (seen.Add(item: r))
                    yield return r;
        }
    }

    #endregion
}
