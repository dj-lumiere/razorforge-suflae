using System;
using System.Collections.Generic;
using Compiler.Resolution;
using Compiler.Targeting;
using Microsoft.Win32;
using SyntaxTree;

namespace Compiler.Instantiation;

/// <summary>
/// Shared context for Phase 6 generic instantiation work.
/// This is scaffold-only for now; existing monomorphization still lives elsewhere.
/// </summary>
public sealed class InstantiationContext
{
    /// <summary>
    /// Semantic registry used to resolve generic definitions and concrete instantiations.
    /// </summary>
    public TypeRegistry Registry { get; }

    /// <summary>
    /// User programs that seed reachable generic type and routine discovery.
    /// </summary>
    public IReadOnlyList<(Program Program, string FilePath, string Module)> UserPrograms { get; }

    /// <summary>
    /// Verified routine bodies keyed by registry key, used as source bodies for instantiation.
    /// </summary>
    public IReadOnlyDictionary<string, Statement> RoutineBodies { get; }

    /// <summary>
    /// Synthesized error-handling variant bodies that may contain reachable generic calls.
    /// </summary>
    public Dictionary<string, Statement> VariantBodies { get; }

    /// <summary>
    /// Concrete generic bodies produced by instantiation and later consumed by codegen.
    /// </summary>
    public Dictionary<string, MonomorphizedBody> InstantiatedGenericBodies { get; }

    /// <summary>
    /// Target platform used when generic expansion depends on platform constants.
    /// </summary>
    public TargetConfig Target { get; }

    /// <summary>
    /// Build mode used when generic expansion depends on compile-time configuration.
    /// </summary>
    public RfBuildMode BuildMode { get; }

    /// <summary>
    /// Canonical keys for reachable concrete generic types discovered during collection.
    /// </summary>
    public HashSet<string> ReachableGenericTypes { get; } = [];

    /// <summary>
    /// Canonical keys for reachable concrete generic routines discovered during collection.
    /// </summary>
    public HashSet<string> ReachableGenericRoutines { get; } = [];

    /// <summary>
    /// Strategy-B live routine set: <see cref="RegistryKey"/> values reachable from
    /// program entry points (<c>start()</c>, <c>@test</c>, <c>@bench</c>) via a transitive
    /// call-graph BFS. When non-empty, GMP gates body emission on membership so unreachable
    /// methods on live concrete types (e.g. <c>List[Owned[Text]].insertion_sort</c> when no caller
    /// uses it) are skipped, preventing the stdlib closure cascade from forcing emission of
    /// unused routines. Populated by <c>RoutineReachabilityPass</c>; empty disables filtering.
    /// </summary>
    public HashSet<string> LiveRoutineKeys { get; } = new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// Strategy-B live owner-type set: <see cref="TypeInfo.FullName"/> values for concrete owner
    /// types whose routines were reached by the entry-point BFS. GMP gates
    /// <c>ProcessConcreteType</c> on membership so unreachable concrete instances
    /// (e.g. <c>Array[BuildMode, 63]</c>, <c>BTreeListNode[Text]</c>) don't get monomorphized at all.
    /// Populated by <c>RoutineReachabilityPass</c>; empty disables filtering.
    /// </summary>
    public HashSet<string> LiveOwnerTypeNames { get; } = new(comparer: StringComparer.Ordinal);

    /// <summary>When true, passes print per-iteration diagnostics to stderr.</summary>
    public bool SaTiming { get; set; }

    /// <summary>
    /// Initializes shared state for Phase 6 generic reachability and monomorphization.
    /// </summary>
    public InstantiationContext(TypeRegistry registry,
        IReadOnlyList<(Program Program, string FilePath, string Module)> userPrograms,
        IReadOnlyDictionary<string, Statement> routineBodies,
        Dictionary<string, Statement>? variantBodies = null,
        Dictionary<string, MonomorphizedBody>? instantiatedGenericBodies = null,
        TargetConfig? target = null,
        RfBuildMode buildMode = RfBuildMode.Debug)
    {
        Registry = registry;
        UserPrograms = userPrograms;
        RoutineBodies = routineBodies;
        VariantBodies = variantBodies ?? [];
        InstantiatedGenericBodies = instantiatedGenericBodies ?? [];
        Target = target ?? TargetConfig.ForCurrentHost();
        BuildMode = buildMode;
    }
}
