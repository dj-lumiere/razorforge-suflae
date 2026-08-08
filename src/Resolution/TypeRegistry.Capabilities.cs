using System;
using System.Collections.Generic;
using System.Linq;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Resolution;

/// <summary>
/// Per-type capability queries. Answers "does this type support wired routine X?"
/// recursively, considering generic-def constraints on the routine in question.
/// </summary>
/// <remarks>
/// Used to gate codegen `declare` emission and reachability seeding for wired
/// routine families whose body has a built-in constraint surface that the registered
/// implementation can't satisfy for every instantiation — e.g. `Array[T, N].eq`
/// declares `needs T obeys Equatable`, so `Array[X, 64]` should NOT carry an
/// `$eq` symbol because `X` is not equatable.
///
/// Results are cached per FullName + protocol for the lifetime of the registry.
/// Cache entries are conservative — when the cache cannot decide (incomplete type
/// substitution, missing generic def), the query returns true so the caller does
/// not silently drop a needed symbol.
/// </remarks>
public sealed partial class TypeRegistry
{
    private const string ContainsMethodName = "contains";

    private readonly Dictionary<(string FullName, string Protocol), bool> _capabilityCache =
        new();

    /// <summary>
    /// Wired routine name -> (protocol it requires the owner to obey, canonical wired-routine name
    /// to look up on the owner). Derived from the single source of truth
    /// <see cref="WiredRoutineCatalog"/> (entries flagged <see cref="WiredView.Capability"/>); used
    /// by both the constraint walker (which receives a `T obeys P` constraint and must check T) and
    /// the routine-applicability gate (which receives a routine and must check its owner). To add a
    /// wired routine, edit <see cref="WiredRoutineCatalog"/>, not this projection.
    /// </summary>
    private static readonly Dictionary<string, (string Protocol, string WiredName)>
        _wiredRoutineMap = WiredRoutineCatalog.BuildCapabilityMap();

    /// <summary>
    /// Reverse map: protocol -> canonical wired routine that materialises it. Built once
    /// from <see cref="_wiredRoutineMap"/>; the protocol's "canonical" routine is the one
    /// whose wired name matches the protocol's primary operator.
    /// </summary>
    private static readonly Dictionary<string, string> _protocolToWired =
        BuildProtocolToWired();

    private static Dictionary<string, string> BuildProtocolToWired()
    {
        var result = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        foreach (var (_, (proto, wired)) in _wiredRoutineMap)
            result[key: proto] = wired;
        return result;
    }

    /// <summary>
    /// Returns true when <paramref name="type"/> can host wired routine <paramref name="wiredName"/>
    /// for this concrete instantiation — i.e. all `T obeys P` constraints on the generic-def
    /// version of that routine are satisfied by <paramref name="type"/>'s type arguments,
    /// AND the routine is either defined directly or derivable through the protocol marker.
    /// </summary>
    public bool TypeHasWiredRoutine(TypeInfo type, string wiredName)
    {
        if (!_wiredRoutineMap.TryGetValue(key: wiredName, value: out var entry))
            return true;
        return HasCapability(type: type, protocol: entry.Protocol, wiredName: entry.WiredName);
    }

    /// <summary>Returns true if the type implements <c>Equatable</c> ($eq).</summary>
    public bool TypeHasEquality(TypeInfo type) => TypeHasWiredRoutine(type: type, wiredName: "eq");
    /// <summary>Returns true if the type implements <c>Containable</c> ($contains).</summary>
    public bool TypeHasContainment(TypeInfo type) => TypeHasWiredRoutine(type: type, wiredName: ContainsMethodName);
    /// <summary>Returns true if the type implements <c>Hashable</c> ($hash).</summary>
    public bool TypeHasHashing(TypeInfo type) => TypeHasWiredRoutine(type: type, wiredName: "hash");
    /// <summary>Returns true if the type implements <c>Comparable</c> ($cmp).</summary>
    public bool TypeHasComparison(TypeInfo type) => TypeHasWiredRoutine(type: type, wiredName: "cmp");

    private bool HasCapability(TypeInfo type, string protocol, string wiredName)
    {
        var cacheKey = (type.FullName, protocol);
        if (_capabilityCache.TryGetValue(key: cacheKey, value: out bool cached))
            return cached;
        // Seed the cache with `true` before recursing so a self-referential type
        // (record containing itself via a wrapper) terminates rather than looping.
        // The conservative seed cannot produce a false-positive cycle: if any
        // step below proves the type lacks the capability, we overwrite to false.
        _capabilityCache[key: cacheKey] = true;
        bool result = ComputeCapability(type: type,
            protocol: protocol,
            wiredName: wiredName);
        _capabilityCache[key: cacheKey] = result;
        return result;
    }

    private bool ComputeCapability(TypeInfo type, string protocol, string wiredName)
    {
        // Generic parameters, error / blank types pass through — they're either further
        // substituted downstream or already a no-op.
        if (type is GenericParameterTypeInfo or ErrorTypeInfo || type.IsNone) return true;

        // No backend-type shortcut: scalar primitives still define `$eq`/`$hash` explicitly
        // (see Core/Numerics/*.rf) and will be picked up by the LookupMethod fallback below.
        // The shortcut used to fire on every `@llvm("…")`-backed record — including aggregate-
        // backed records like `Array[T, N]` (`@llvm("[{N} x {T}]")`) — which masked the
        // generic-constraint check we need for correctness.

        // For a generic resolution G[T1, T2, ...], the generic def's wired-method may carry
        // `Ti obeys P` constraints. Each must hold for the corresponding type arg, where
        // the capability being demanded is keyed by P (not by the outer `protocol` we're
        // currently evaluating) — e.g. Array.contains demands `T obeys Equatable`, so we
        // recurse for Equatable on T, not for Container.
        TypeInfo? genericDef = type switch
        {
            RecordTypeInfo r => r.GenericDefinition,
            EntityTypeInfo e => e.GenericDefinition,
            _ => null
        };
        if (genericDef != null && type.TypeArguments is { Count: > 0 } typeArgs &&
            genericDef.GenericParameters is { Count: > 0 } gParams &&
            gParams.Count == typeArgs.Count)
        {
            RoutineInfo? defMethod = LookupMethod(type: genericDef, methodName: wiredName);
            if (defMethod is { GenericConstraints: { Count: > 0 } constraints })
            {
                foreach (GenericConstraintDeclaration c in constraints)
                {
                    if (c.ConstraintType != ConstraintKind.Obeys ||
                        c.ConstraintTypes is not { Count: > 0 } protos) continue;
                    int idx = -1;
                    for (int i = 0; i < gParams.Count; i++)
                        if (gParams[index: i] == c.ParameterName) { idx = i; break; }
                    if (idx < 0) continue;

                    TypeInfo argType = typeArgs[index: idx];
                    foreach (TypeExpression protoExpr in protos)
                    {
                        // Each `T obeys P` constraint demands that the corresponding type
                        // arg has P's underlying capability. Look up the canonical wired
                        // routine for P from the central map; unknown protocols (e.g.
                        // marker traits without a wired routine) are skipped.
                        if (!_protocolToWired.TryGetValue(key: protoExpr.Name,
                                value: out string? requiredWired))
                            continue;
                        if (!HasCapability(type: argType,
                                protocol: protoExpr.Name,
                                wiredName: requiredWired))
                            return false;
                    }
                }
            }
        }

        // Direct support: type has a CONCRETE impl of the method (explicit or synthesised).
        // A lookup that resolves to the ABSTRACT protocol method (e.g. `Equatable.eq` for a
        // plain record that neither defines `$eq` nor obeys Equatable) does NOT count — it has no
        // body, so reporting capability here would let callers emit a call to the unimplemented
        // abstract symbol (LINKERR). Genuine conformance is established by the TypeObeysProtocol
        // check below (concrete impl) or by obeying the protocol.
        RoutineInfo? direct = LookupMethod(type: type, methodName: wiredName);
        if (direct != null && direct.OwnerType is not ProtocolTypeInfo) return true;

        // Marker conformance: the type obeys the named protocol — we expect a body to
        // appear eventually (via auto-synthesis) or for it to be an abstract marker.
        if (TypeObeysProtocol(type: type, protocolName: protocol)) return true;

        return false;
    }

    /// <summary>
    /// Returns true iff <paramref name="type"/> can auto-derive <c>Assignable</c>:
    /// its LLVM layout contains no <c>ptr</c> (and is not zero-sized in a way that
    /// indicates a managed reference). Concretely:
    /// <list type="bullet">
    ///   <item><description>Records: <see cref="RecordTypeInfo.LlvmType"/> contains no "ptr" substring.</description></item>
    ///   <item><description>Choice/Flags: always (tag-only layout).</description></item>
    ///   <item><description>Tuples: every element is auto-deriveable.</description></item>
    ///   <item><description>Entities, wrappers, variants, crashables, protocols, routines: never (always ptr-shaped).</description></item>
    ///   <item><description>Generic parameters: false (decision deferred to instantiation).</description></item>
    /// </list>
    /// Raw-pointer types like <c>Hijacked[T]</c> and <c>CPtr</c> are ptr-shaped and
    /// therefore must opt in manually with a trivial <c>$store() -> Me  return me</c>.
    /// </summary>
    public bool CanAutoDeriveAssignable(TypeInfo type)
    {
        return type switch
        {
            ChoiceTypeInfo => true,
            FlagsTypeInfo => true,
            TupleTypeInfo tuple => tuple.ElementTypes.All(predicate: CanAutoDeriveAssignable),
            RecordTypeInfo record => !record.IsGenericDefinition && !LayoutContainsPtr(layout: record.LlvmType),
            _ => false
        };
    }

    private static bool LayoutContainsPtr(string layout)
    {
        // "void" (zero-sized) and any layout without "ptr" auto-derives.
        // Substring check is safe: LLVM type syntax uses "ptr" only as a literal type
        // token; no primitive scalar contains the substring (i8, i64, f32, [N x T], etc.).
        return layout.Contains(value: "ptr", comparisonType: StringComparison.Ordinal);
    }

    /// <summary>
    /// Public conformance probe by protocol NAME — backs the comptime <c>m.obeys(Protocol)</c> expand
    /// projection. Returns true when <paramref name="type"/> either DECLARES the protocol (walks
    /// <c>ImplementedProtocols</c> transitively) OR STRUCTURALLY satisfies it by supplying a concrete
    /// (non-abstract) impl of every method the protocol requires. The structural arm matters for
    /// synthesized capabilities that aren't spelled as an explicit <c>obeys</c>: e.g. every
    /// Record/Entity/Variant gets a synthesized <c>serialize()</c>, so a scalar field like <c>S32</c>
    /// satisfies <c>Serializable</c> (it has the method) even though it lists only numeric protocols.
    /// A positive result therefore guarantees the protocol's wired method resolves to a real body —
    /// safe for a derive template to gate a <c>me.field.serialize()</c> call on (else fall back to
    /// <c>represent</c>), and the comptime-if prune then drops the untaken branch before codegen.
    /// </summary>
    public bool DoesTypeObeyProtocol(TypeInfo type, string protocolName)
    {
        if (TypeObeysProtocol(type: type, protocolName: protocolName)) return true;

        // Structural satisfaction: the type has a concrete impl of every method the protocol declares.
        if (LookupType(name: protocolName) is not ProtocolTypeInfo proto) return false;
        List<RoutineInfo> required = GetMethodsForType(type: proto)
                                    .Where(predicate: m => m.OwnerType is ProtocolTypeInfo)
                                    .ToList();
        return required.Count > 0 && required.All(predicate: m =>
            LookupMethod(type: type, methodName: m.Name) is { OwnerType: not ProtocolTypeInfo });
    }

    private bool TypeObeysProtocol(TypeInfo type, string protocolName) // NOSONAR S3776
    {
        List<TypeInfo>? implemented = type switch
        {
            ChoiceTypeInfo c => c.ImplementedProtocols,
            FlagsTypeInfo f => f.ImplementedProtocols,
            RecordTypeInfo r => r.ImplementedProtocols,
            EntityTypeInfo e => e.ImplementedProtocols,
            _ => null
        };
        if (implemented == null) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return implemented.Any(p => Walk(p, protocolName, seen));

        bool Walk(TypeInfo candidate, string target, HashSet<string> seenSet)
        {
            if (!seenSet.Add(item: candidate.Name)) return false;
            if (candidate.Name == target) return true;
            TypeInfo latest = LookupType(name: candidate.Name) ?? candidate;
            if (latest is ProtocolTypeInfo proto)
                return proto.ParentProtocols.Any(parent => Walk(parent, target, seenSet));
            return false;
        }
    }
}
