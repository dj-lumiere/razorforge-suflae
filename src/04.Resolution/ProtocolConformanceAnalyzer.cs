using System.Collections.Generic;
using System.Linq;
using Verification;
using TypeModel.Enums;
using TypeModel.Types;

namespace Compiler.Resolution;

using TypeSymbol = TypeInfo;

/// <summary>
/// Handles implicit marker protocol conformance for the semantic analyzer.
/// </summary>
internal sealed class ProtocolConformanceAnalyzer
{
    private readonly SemanticVerifier _sa;

    internal ProtocolConformanceAnalyzer(SemanticVerifier sa)
    {
        _sa = sa;
    }

    #region Phase 4.2: Implicit Marker Protocol Conformance

    /// <summary>
    /// Automatically adds marker protocol conformance based on type category.
    /// Records implicitly conform to RecordType, entities to EntityType, etc.
    /// Also adds all transitive protocols from the marker's obeys chain.
    /// </summary>
    internal void ApplyImplicitMarkerConformance() // NOSONAR S3776
    {
        foreach (TypeSymbol type in _sa._registry.GetTypesWithMemberRoutines())
        {
            // Skip generic definitions — their resolutions inherit conformance
            if (type.IsGenericDefinition)
            {
                continue;
            }

            // Determine the marker protocol name for this type category
            string? markerName = type.Category switch
            {
                TypeCategory.Record => "RecordType",
                TypeCategory.Entity => "EntityType",
                TypeCategory.Choice => "ChoiceType",
                TypeCategory.Flags => "FlagsType",
                TypeCategory.Crashable => "Crashable",
                _ => null
            };

            if (markerName == null)
            {
                continue;
            }

            TypeSymbol? markerType = _sa._registry.LookupType(name: markerName);
            if (markerType is not ProtocolTypeInfo marker)
            {
                continue;
            }

            // Collect all transitive protocols from the marker's obeys chain
            var transitiveProtocols = new List<TypeSymbol>();
            CollectTransitiveProtocols(protocol: marker, result: transitiveProtocols);

            // Merge with existing user-declared protocols
            List<TypeSymbol> existing = GetImplementedProtocols(type: type);
            var merged = new List<TypeSymbol>(collection: existing);

            // Add transitive protocols first, then the marker itself
            // Track implicitly-added protocols so validation skips them
            foreach (TypeSymbol proto in transitiveProtocols)
            {
                if (merged.All(predicate: p => p.Name != proto.Name))
                {
                    merged.Add(item: proto);
                    _sa._implicitProtocolConformances.Add(item: (type.FullName, proto.Name));
                }
            }

            if (merged.All(predicate: p => p.Name != marker.Name))
            {
                merged.Add(item: marker);
                _sa._implicitProtocolConformances.Add(item: (type.FullName, marker.Name));
            }

            // Only update if we actually added something
            if (merged.Count > existing.Count)
            {
                UpdateTypeProtocols(type: type, protocols: merged);
            }
        }

        ApplyAutoAssignableConformance();
        ApplyAutoAssignableCascadeConformance();
        ApplyEverywhereConformance();
    }

    /// <summary>
    /// Generic <c>needs P everywhere</c> gate (④ standard-impl eligibility): for every protocol that declares
    /// an <c>everywhere</c> self-constraint, auto-derive conformance to any concrete member-bearing type all
    /// of whose members obey P (<see cref="TypeRegistry.EverywhereObeys"/>). This is NOT a per-protocol
    /// bespoke pass — it reads the rule from the stdlib protocol declaration, so Copyable (and later Assignable/
    /// Equatable/…) opt in by writing <c>needs P everywhere</c> rather than growing new C# passes. The BASE
    /// case is the leaf types' own explicit/auto conformance (scalars bitwise, Text/Integer declare Copyable,
    /// <c>@llvm</c> aggregates cascade to their element via EverywhereObeys); this gate is the INDUCTIVE step
    /// over composition. Runs LAST so leaf + Assignable-cascade conformances are already in place for the member
    /// walk to see. A type already declaring P is skipped (idempotent).
    /// </summary>
    private void ApplyEverywhereConformance()
    {
        // Collect protocols carrying an `everywhere` self-constraint (subject `Me`, ConstraintKind.Everywhere).
        var everywhereProtocols = new List<ProtocolTypeInfo>();
        foreach (TypeSymbol type in _sa._registry.GetAllTypes())
        {
            if (type is ProtocolTypeInfo proto && ProtocolHasEverywhereSelfConstraint(proto: proto))
            {
                everywhereProtocols.Add(item: proto);
            }
        }
        if (everywhereProtocols.Count == 0)
        {
            return;
        }

        foreach (ProtocolTypeInfo proto in everywhereProtocols)
        {
            foreach (TypeSymbol type in _sa._registry.GetTypesWithMemberRoutines())
            {
                if (type.IsGenericDefinition)
                {
                    continue;
                }

                // Entities stay OPT-IN for Copyable (STEP 4: "entity is NOT always copyable") — a simple
                // `entity Point{x,y}` must not silently become copyable. Entity auto-derive is a separate,
                // deliberate increment; this gate covers value composition (record/tuple/variant/…) only.
                if (type is EntityTypeInfo)
                {
                    continue;
                }

                List<TypeSymbol> existing = GetImplementedProtocols(type: type);
                if (existing.Any(predicate: p => p.Name == proto.Name))
                {
                    continue;
                }

                if (!_sa._registry.EverywhereObeys(type: type, protocol: proto.Name))
                {
                    continue;
                }

                var merged = new List<TypeSymbol>(collection: existing) { proto };
                _sa._implicitProtocolConformances.Add(item: (type.FullName, proto.Name));
                UpdateTypeProtocols(type: type, protocols: merged);
            }
        }
    }

    /// <summary>
    /// True when a protocol declares an <c>everywhere</c> self-constraint (<c>needs P everywhere</c>, which
    /// the parser records as a <see cref="ConstraintKind.Everywhere"/> constraint with subject <c>Me</c> and
    /// the protocol's own name as the constraint target) — the opt-in that makes
    /// <see cref="ApplyEverywhereConformance"/> structurally cascade the protocol over composition.
    /// </summary>
    private static bool ProtocolHasEverywhereSelfConstraint(ProtocolTypeInfo proto)
    {
        return proto.GenericConstraints is { } cs &&
               cs.Any(predicate: c => c.ConstraintType == SyntaxTree.ConstraintKind.Everywhere);
    }

    /// <summary>
    /// Auto-derives <c>Assignable</c> (NOT <c>Copyable</c>) for a value aggregate whose every field/element is
    /// itself Assignable — even when some are MANAGED (Text/Integer/RC wrapper), which <see
    /// cref="ApplyAutoAssignableConformance"/> cannot handle (a managed field has a ptr → not bitwise
    /// Copyable). Its <c>store</c> is a FIELD-WALK (each field's <c>store</c>), synthesized by
    /// <c>WiredRoutinePass.BuildRecordCopyBody</c>. This completes storability so <c>.assign()</c> resolves for
    /// every identity-less type — the store-based replacement for the arbitrary <c>IsTriviallyAssignable</c>
    /// heuristic. Entities (identity) and the borrow/access tokens (scope-bound) are correctly left out —
    /// <see cref="TypeRegistry.CanMemberVariableWalkAssignable"/> excludes them.
    /// </summary>
    private void ApplyAutoAssignableCascadeConformance()
    {
        if (_sa._registry.LookupType(name: "Assignable") is not ProtocolTypeInfo Assignable)
        {
            return;
        }

        foreach (TypeSymbol type in _sa._registry.GetTypesWithMemberRoutines())
        {
            if (type.IsGenericDefinition)
            {
                continue;
            }

            List<TypeSymbol> existing = GetImplementedProtocols(type: type);
            if (existing.Any(predicate: p => p.Name is "Assignable" or "Copyable"))
            {
                continue;
            }

            if (!_sa._registry.CanMemberVariableWalkAssignable(type: type))
            {
                continue;
            }

            var merged = new List<TypeSymbol>(collection: existing) { Assignable };
            _sa._implicitProtocolConformances.Add(item: (type.FullName, Assignable.Name));
            UpdateTypeProtocols(type: type, protocols: merged);
        }
    }

    /// <summary>
    /// Auto-derives <c>Assignable</c> conformance for any type whose LLVM layout
    /// contains no <c>ptr</c>. Runs after marker-protocol conformance so the new
    /// entry sits alongside <c>RecordType</c>/<c>EntityType</c>/etc. in the
    /// type's <c>ImplementedProtocols</c>. Types that already declare
    /// <c>obeys Assignable</c> (whether user-written for opt-in records, or
    /// trivially for raw-pointer wrappers like <c>Hijacked[T]</c>/<c>CPtr</c>)
    /// are left untouched — <see cref="TypeRegistry.CanAutoDeriveAssignable"/>
    /// is only consulted when the type does not already obey the protocol.
    /// </summary>
    private void ApplyAutoAssignableConformance()
    {
        // A no-ptr layout is a bitwise duplicate — which is BOTH a valid cheap `store` AND a valid
        // deep `copy` (nothing heap is shared). `Assignable` and `Copyable` are ORTHOGONAL (no hierarchy),
        // so derive BOTH explicitly. Raw-pointer opt-in types (Hijacked/CPtr) have a ptr, so
        // CanAutoDeriveAssignable is false and they keep their hand-written `obeys Assignable` only.
        if (_sa._registry.LookupType(name: "Copyable") is not ProtocolTypeInfo copyable
            || _sa._registry.LookupType(name: "Assignable") is not ProtocolTypeInfo Assignable)
        {
            return;
        }

        foreach (TypeSymbol type in _sa._registry.GetTypesWithMemberRoutines())
        {
            if (type.IsGenericDefinition)
            {
                continue;
            }

            List<TypeSymbol> existing = GetImplementedProtocols(type: type);
            if (existing.Any(predicate: p => p.Name is "Copyable" or "Assignable"))
            {
                continue;
            }

            if (!_sa._registry.CanAutoDeriveAssignable(type: type))
            {
                continue;
            }

            var merged = new List<TypeSymbol>(collection: existing) { Assignable, copyable };
            _sa._implicitProtocolConformances.Add(item: (type.FullName, Assignable.Name));
            _sa._implicitProtocolConformances.Add(item: (type.FullName, copyable.Name));
            UpdateTypeProtocols(type: type, protocols: merged);
        }
    }

    // RC wrappers (Retained/Tracked/Shared/Watched/Roamed) deliberately do NOT obey `Assignable` — an RC
    // handle is not implicitly copyable (that would silently mint a co-owner). Duplication is the explicit
    // `.share()` member routine, and a bare `var b = rc` is rejected (RF-S420). A record that HOLDS an RC
    // field is likewise NON-Assignable (its MemberVariableAssignable fails on the RC field) — copying it would silently
    // share the handle, so it must be reconstructed explicitly (WithBaseNotAssignable). (The former
    // `ApplyAutoAssignableConformance` that stamped `Assignable` on the 5 RC wrappers is removed.)

    /// <summary>
    /// Recursively collects all transitive parent protocols from a protocol's obeys chain.
    /// </summary>
    private static void CollectTransitiveProtocols(ProtocolTypeInfo protocol,
        List<TypeSymbol> result)
    {
        foreach (ProtocolTypeInfo parent in protocol.ParentProtocols)
        {
            if (result.Any(p => p.Name == parent.Name))
            {
                continue;
            }

            result.Add(item: parent);
            CollectTransitiveProtocols(protocol: parent, result: result);
        }
    }

    /// <summary>
    /// Gets the implemented protocols for any type that supports them.
    /// </summary>
    private static List<TypeSymbol> GetImplementedProtocols(TypeSymbol type)
    {
        return type switch
        {
            RecordTypeInfo r => r.ImplementedProtocols,
            EntityTypeInfo e => e.ImplementedProtocols,
            _ => []
        };
    }

    /// <summary>
    /// Updates the implemented protocols for any type that supports them.
    /// </summary>
    private void UpdateTypeProtocols(TypeSymbol type, List<TypeSymbol> protocols)
    {
        switch (type)
        {
            case ChoiceTypeInfo:
                _sa._registry.UpdateChoiceProtocols(choiceName: type.FullName, protocols: protocols);
                break;
            case FlagsTypeInfo:
                _sa._registry.UpdateFlagsProtocols(flagsName: type.FullName, protocols: protocols);
                break;
            case RecordTypeInfo:
                _sa._registry.UpdateRecordProtocols(recordName: type.FullName, protocols: protocols);
                break;
            case CrashableTypeInfo:
                _sa._registry.UpdateCrashableProtocols(typeName: type.FullName, protocols: protocols);
                break;
            case EntityTypeInfo:
                _sa._registry.UpdateEntityProtocols(entityName: type.FullName, protocols: protocols);
                break;
        }
    }

    #endregion
}
