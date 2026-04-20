namespace Compiler.Resolution;

using SemanticVerification;
using TypeModel.Enums;
using TypeModel.Types;
using TypeSymbol = TypeModel.Types.TypeInfo;

/// <summary>
/// Handles implicit marker protocol conformance for the semantic analyzer.
/// </summary>
internal sealed class ProtocolConformanceAnalyzer
{
    private readonly SemanticAnalyzer _sa;

    internal ProtocolConformanceAnalyzer(SemanticAnalyzer sa)
    {
        _sa = sa;
    }

    #region Phase 2.54: Implicit Marker Protocol Conformance

    /// <summary>
    /// Automatically adds marker protocol conformance based on type category.
    /// Records implicitly conform to RecordType, entities to EntityType, etc.
    /// Also adds all transitive protocols from the marker's obeys chain.
    /// </summary>
    internal void ApplyImplicitMarkerConformance()
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
            IReadOnlyList<TypeSymbol> existing = GetImplementedProtocols(type: type);
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
    private static IReadOnlyList<TypeSymbol> GetImplementedProtocols(TypeSymbol type)
    {
        return type switch
        {
            RecordTypeInfo r => r.ImplementedProtocols,
            EntityTypeInfo e => e.ImplementedProtocols,
            ChoiceTypeInfo c => c.ImplementedProtocols,
            FlagsTypeInfo f => f.ImplementedProtocols,
            CrashableTypeInfo cr => cr.ImplementedProtocols,
            _ => []
        };
    }

    /// <summary>
    /// Updates the implemented protocols for any type that supports them.
    /// </summary>
    private void UpdateTypeProtocols(TypeSymbol type, IReadOnlyList<TypeSymbol> protocols)
    {
        switch (type)
        {
            case RecordTypeInfo:
                _sa._registry.UpdateRecordProtocols(recordName: type.FullName, protocols: protocols);
                break;
            case EntityTypeInfo:
                _sa._registry.UpdateEntityProtocols(entityName: type.FullName, protocols: protocols);
                break;
            case ChoiceTypeInfo:
                _sa._registry.UpdateChoiceProtocols(choiceName: type.FullName, protocols: protocols);
                break;
            case FlagsTypeInfo:
                _sa._registry.UpdateFlagsProtocols(flagsName: type.FullName, protocols: protocols);
                break;
            case CrashableTypeInfo:
                _sa._registry.UpdateCrashableProtocols(typeName: type.FullName, protocols: protocols);
                break;
        }
    }

    #endregion
}
