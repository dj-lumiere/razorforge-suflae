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

    #region Phase 2.54: Implicit Marker Protocol Conformance

    /// <summary>
    /// Automatically adds marker protocol conformance based on type category.
    /// Records implicitly conform to RecordType, entities to EntityType, etc.
    /// Also adds all transitive protocols from the marker's obeys chain.
    /// </summary>
    internal void ApplyImplicitMarkerConformance() // NOSONAR S3776
    {
        foreach (TypeSymbol type in _sa._registry.GetTypesWithMethods())
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
        TypeSymbol? assignableType = _sa._registry.LookupType(name: "Assignable");
        if (assignableType is not ProtocolTypeInfo assignable)
        {
            return;
        }

        foreach (TypeSymbol type in _sa._registry.GetTypesWithMethods())
        {
            if (type.IsGenericDefinition)
            {
                continue;
            }

            List<TypeSymbol> existing = GetImplementedProtocols(type: type);
            if (existing.Any(predicate: p => p.Name == "Assignable"))
            {
                continue;
            }

            if (!_sa._registry.CanAutoDeriveAssignable(type: type))
            {
                continue;
            }

            var merged = new List<TypeSymbol>(collection: existing) { assignable };
            _sa._implicitProtocolConformances.Add(item: (type.FullName, assignable.Name));
            UpdateTypeProtocols(type: type, protocols: merged);
        }
    }

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
            CrashableTypeInfo cr => cr.ImplementedProtocols,
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
            case EntityTypeInfo:
                _sa._registry.UpdateEntityProtocols(entityName: type.FullName, protocols: protocols);
                break;
            case CrashableTypeInfo:
                _sa._registry.UpdateCrashableProtocols(typeName: type.FullName, protocols: protocols);
                break;
        }
    }

    #endregion
}
