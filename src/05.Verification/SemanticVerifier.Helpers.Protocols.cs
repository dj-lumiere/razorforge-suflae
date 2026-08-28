using System.Collections.Generic;
using System.Linq;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Verification.Enums;

namespace Verification;

using TypeSymbol = TypeInfo;

public sealed partial class SemanticVerifier
{
    private static TypeSymbol SubstituteTypeParams(TypeSymbol type,
        Dictionary<string, TypeSymbol> substitution)
    {
        // Direct substitution for generic parameters
        if (type is GenericParameterTypeInfo &&
            substitution.TryGetValue(key: type.Name, value: out TypeSymbol? sub))
        {
            return sub;
        }

        // Recursive substitution in type arguments
        if (type.TypeArguments is not { Count: > 0 })
        {
            return type;
        }

        var newArgs = type.TypeArguments
                          .Select(selector: arg =>
                               SubstituteTypeParams(type: arg, substitution: substitution))
                          .ToList();

        // Check if anything actually changed
        bool changed = false;
        for (int i = 0; i < newArgs.Count; i++)
        {
            if (!ReferenceEquals(objA: newArgs[index: i], objB: type.TypeArguments[index: i]))
            {
                changed = true;
                break;
            }
        }

        if (!changed)
        {
            return type;
        }

        // Get the generic definition and create a new instance with substituted args
        TypeSymbol? genericDef = type switch
        {
            RecordTypeInfo r => r.GenericDefinition,
            EntityTypeInfo e => e.GenericDefinition,
            ProtocolTypeInfo p => p.GenericDefinition,
            _ => null
        };

        if (genericDef != null)
        {
            return genericDef.CreateInstance(typeArguments: newArgs);
        }

        // TupleTypeInfo doesn't have a GenericDefinition — create a new tuple directly
        if (type is TupleTypeInfo)
        {
            return new TupleTypeInfo(elementTypes: newArgs);
        }

        return type;
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> implements the named protocol.
    /// Checks explicit protocol declarations, parent protocol chains, and structural conformance
    /// (i.e., whether the type has all required memberRoutines of the protocol).
    /// </summary>
    internal bool ImplementsProtocol(TypeSymbol type, string protocolName)
    {
        // Get the protocol type. Parameterised protocol names like "Controlling[List[S64]]"
        // won't resolve directly (only the generic definition is registered) — strip brackets
        // and fall back to the base name so conformance checks see the protocol either way.
        TypeSymbol? protocol = LookupTypeWithImports(name: protocolName);
        if (protocol is not { Category: TypeCategory.Protocol } &&
            protocolName.Contains(value: '['))
        {
            string baseProtocolName = BareTypeName(typeName: protocolName);
            protocol = LookupTypeWithImports(name: baseProtocolName);
        }
        if (protocol is not { Category: TypeCategory.Protocol })
        {
            return false;
        }

        // Type-category protocols are satisfied by category membership itself: a record obeys
        // RecordType by BEING a record — no declaration needed (their parent protocols like
        // Equatable/Hashable are auto-derived for these categories). Without this, a
        // `needs M obeys RecordType` constraint can only be met by the wrong-spelling
        // workaround `M is RecordType`.
        bool categoryMatch = protocolName switch
        {
            "RecordType" => type.Category == TypeCategory.Record,
            "EntityType" => type.Category == TypeCategory.Entity,
            "ChoiceType" => type.Category == TypeCategory.Choice,
            "VariantType" => type.Category == TypeCategory.Variant,
            "FlagsType" => type.Category == TypeCategory.Flags,
            "Crashable" => type.Category == TypeCategory.Crashable,
            _ => false
        };
        if (categoryMatch)
        {
            return true;
        }

        // Generic parameter: check current routine/owner type constraints for obeys declarations.
        // e.g., needs T obeys Equatable means T satisfies Equatable inside this routine's body.
        if (type is GenericParameterTypeInfo)
        {
            if (_currentRoutine?.GenericConstraints != null &&
                _currentRoutine.GenericConstraints.Any(c =>
                    c.ParameterName == type.Name && c is { ConstraintType: ConstraintKind.Obeys, ConstraintTypes: not null } &&
                    c.ConstraintTypes.Any(ct => ct.Name == protocolName)))
                return true;

            TypeSymbol? ownerType = _currentRoutine?.OwnerType;
            if (ownerType?.GenericConstraints == null)
            {
                return false;
            }

            return ownerType.GenericConstraints.Any(c =>
                c.ParameterName == type.Name && c is { ConstraintType: ConstraintKind.Obeys, ConstraintTypes: not null } &&
                c.ConstraintTypes.Any(ct => ct.Name == protocolName));
        }

        // Get the list of implemented protocols for this type
        List<TypeSymbol>? implementedProtocols = type switch
        {
            RecordTypeInfo record => record.ImplementedProtocols,
            EntityTypeInfo entity => entity.ImplementedProtocols,
            _ => null
        };

        if (implementedProtocols == null)
        {
            return false;
        }

        // Check if the protocol is directly declared (or via parent protocols recursively)
        if (implementedProtocols.Any(implemented =>
                implemented.Name == protocolName ||
                implemented.BareName == protocolName ||
                (implemented is ProtocolTypeInfo proto &&
                 CheckParentProtocols(proto: proto, targetName: protocolName))))
            return true;

        // Check if the type has all required memberRoutines of the protocol (structural conformance)
        if (protocol is ProtocolTypeInfo protoType)
        {
            // Entity T implicitly satisfies Accessing[T] and Controlling[T]
            if (type.Category == TypeCategory.Entity &&
                protoType.TypeArguments is { Count: 1 } args &&
                args[index: 0].Name == type.Name)
            {
                string baseProto = (protoType.GenericDefinition ?? protoType).BareName;
                if (baseProto is Compiler.Resolution.RuntimeContract.Accessing or Compiler.Resolution.RuntimeContract.Controlling)
                {
                    return true;
                }
            }

            // Transparent relay for Accessing[T] / Controlling[T]:
            // A wrapper type satisfies any readonly protocol that its inner entity type satisfies.
            // All @readonly protocol memberRoutines are safe to delegate through both read-only
            // (Accessing) and read-write (Controlling) wrappers.
            if (IsAllReadOnlyProtocol(protoType))
            {
                TypeSymbol? innerT = GetReferringControllingInnerType(protocols: implementedProtocols);
                if (innerT != null && ImplementsProtocol(type: innerT, protocolName: protocolName))
                    return true;
            }

            return CheckStructuralConformance(type: type, protocol: protoType);
        }

        return false;
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> explicitly declares conformance to the named protocol
    /// via <c>obeys</c>. Unlike <see cref="ImplementsProtocol"/>, this does NOT fall back to
    /// structural conformance, making it suitable for marker protocols like ConstCompatible.
    /// </summary>
    internal bool ExplicitlyImplementsProtocol(TypeSymbol type, string protocolName)
    {
        List<TypeSymbol>? implementedProtocols = type switch
        {
            RecordTypeInfo record => record.ImplementedProtocols,
            EntityTypeInfo entity => entity.ImplementedProtocols,
            _ => null
        };

        if (implementedProtocols == null)
        {
            return false;
        }

        return implementedProtocols.Any(implemented =>
            implemented.Name == protocolName ||
            implemented.BareName == protocolName ||
            (implemented is ProtocolTypeInfo proto &&
             CheckParentProtocols(proto: proto, targetName: protocolName)));
    }

    /// <summary>
    /// Checks if any parent protocol matches the target.
    /// </summary>
    internal bool CheckParentProtocols(ProtocolTypeInfo proto, string targetName)
    {
        foreach (ProtocolTypeInfo parent in proto.ParentProtocols)
        {
            if (parent.Name == targetName || parent.BareName == targetName)
            {
                return true;
            }

            // Re-lookup parent from registry to get the latest version with populated ParentProtocols,
            // since immutable type updates may leave stale references in the hierarchy.
            ProtocolTypeInfo latestParent = parent;
            if (parent.ParentProtocols.Count == 0)
            {
                TypeSymbol? looked = _registry.LookupType(name: parent.Name);
                if (looked is ProtocolTypeInfo latest)
                {
                    latestParent = latest;
                }
            }

            if (CheckParentProtocols(proto: latestParent, targetName: targetName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a type structurally conforms to a protocol by having all required memberRoutines.
    /// </summary>
    private bool CheckStructuralConformance(TypeSymbol type, ProtocolTypeInfo protocol)
    {
        // Marker protocols (no memberRoutines) require explicit conformance — never structurally satisfied
        if (protocol.MemberRoutines.Count == 0)
        {
            return false;
        }

        foreach (ProtocolMemberRoutineInfo requiredMemberRoutine in protocol.MemberRoutines)
        {
            // Skip memberRoutines with default implementations
            if (requiredMemberRoutine.HasDefaultImplementation)
            {
                continue;
            }

            // Look for the memberRoutine on the type
            RoutineInfo? typeMemberRoutine =
                _registry.LookupMemberRoutine(type: type, memberRoutineName: requiredMemberRoutine.Name);
            if (typeMemberRoutine == null)
            {
                // memberRoutine names are bare; the failable `!` is a structured flag. Retry matching a
                // same-named failable implementation via the isFailable filter.
                if (requiredMemberRoutine.IsFailable)
                {
                    typeMemberRoutine = _registry.LookupMemberRoutine(type: type,
                        memberRoutineName: requiredMemberRoutine.Name, isFailable: true);
                }

                if (typeMemberRoutine == null)
                {
                    return false;
                }
            }

            // Verify memberRoutine signature matches (basic check)
            if (!memberRoutineSignatureMatches(typeMemberRoutine: typeMemberRoutine, protoMemberRoutine: requiredMemberRoutine))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks if a type's memberRoutine signature matches a protocol memberRoutine signature.
    /// </summary>
    private bool memberRoutineSignatureMatches(RoutineInfo typeMemberRoutine, ProtocolMemberRoutineInfo protoMemberRoutine) // NOSONAR S3776
    {
        // Check failable matches
        if (typeMemberRoutine.IsFailable != protoMemberRoutine.IsFailable)
        {
            return false;
        }

        // Check parameter count (excluding 'me' parameter if present)
        // In-body memberRoutines have explicit 'me' as first parameter
        // Extension memberRoutines don't include 'me' in the parameter list
        int expectedParamCount = protoMemberRoutine.ParameterTypes.Count;
        bool hasMeParam = typeMemberRoutine.Parameters.Count > 0 &&
                          typeMemberRoutine.Parameters[index: 0].Name == "me";
        int actualParamCount = typeMemberRoutine.Parameters.Count - (hasMeParam
            ? 1
            : 0);

        if (actualParamCount != expectedParamCount)
        {
            return false;
        }

        // Check parameter types - skip 'me' if present
        int startIndex = hasMeParam
            ? 1
            : 0;
        for (int i = 0; i < expectedParamCount; i++)
        {
            TypeSymbol expectedType = protoMemberRoutine.ParameterTypes[index: i];
            TypeSymbol actualType = typeMemberRoutine.Parameters[index: startIndex + i].Type;

            // Handle protocol self type (Me) - should match the implementing type
            if (expectedType is ProtocolSelfTypeInfo)
            {
                // 'Me' in protocol should match the owner type of the memberRoutine
                if (typeMemberRoutine.OwnerType != null &&
                    !TypesMatch(actual: actualType, expected: typeMemberRoutine.OwnerType))
                {
                    return false;
                }
            }
            else if (!TypesMatch(actual: actualType, expected: expectedType))
            {
                return false;
            }
        }

        // Check return type (if specified)
        if (protoMemberRoutine.ReturnType != null && typeMemberRoutine.ReturnType != null)
        {
            if (!IsAssignableTo(source: typeMemberRoutine.ReturnType, target: protoMemberRoutine.ReturnType))
            {
                return false;
            }
        }
        else if (protoMemberRoutine.ReturnType == null != (typeMemberRoutine.ReturnType == null))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns true if all required memberRoutines in <paramref name="protocol"/> and its parent chain
    /// have <see cref="MutationCategory.Readonly"/> mutation. Marker protocols with no
    /// memberRoutines return false — they require explicit declaration, not relay.
    /// </summary>
    private bool IsAllReadOnlyProtocol(ProtocolTypeInfo protocol)
    {
        if (protocol.MemberRoutines.Count == 0)
            return false;

        foreach (ProtocolMemberRoutineInfo memberRoutine in protocol.MemberRoutines)
        {
            if (memberRoutine.Mutation != MutationCategory.Readonly)
                return false;
        }

        foreach (ProtocolTypeInfo parent in protocol.ParentProtocols)
        {
            // Re-lookup to get a fully-populated parent (same pattern as CheckParentProtocols).
            ProtocolTypeInfo resolved = parent;
            if (_registry.LookupType(name: parent.Name) is ProtocolTypeInfo latest)
                resolved = latest;

            if (!IsAllReadOnlyProtocol(resolved))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Extracts the inner type T from the first <c>Accessing[T]</c> or <c>Controlling[T]</c>
    /// entry in <paramref name="protocols"/>. Returns null if neither is present.
    /// </summary>
    private static TypeSymbol? GetReferringControllingInnerType(List<TypeSymbol> protocols)
    {
        foreach (TypeSymbol proto in protocols)
        {
            string baseName = proto.BareName;
            if (baseName is Compiler.Resolution.RuntimeContract.Accessing or Compiler.Resolution.RuntimeContract.Controlling && proto.TypeArguments is { Count: 1 })
                return proto.TypeArguments[index: 0];
        }

        return null;
    }

    /// <summary>
    /// Checks if two types match for protocol signature comparison.
    /// </summary>
    private static bool TypesMatch(TypeSymbol actual, TypeSymbol expected)
    {
        // Exact name match
        if (actual.Name == expected.Name)
        {
            return true;
        }

        // Handle ProtocolSelfTypeInfo in expected position
        if (expected is ProtocolSelfTypeInfo)
        {
            // 'Me' matches the owner type - handled by caller
            return true;
        }

        // Handle generic resolutions
        if (expected.IsGenericDefinition && actual.IsGenericResolution)
        {
            string baseName = actual.BareName;
            if (baseName == expected.Name)
            {
                return true;
            }
        }

        return false;
    }
}
