namespace SemanticAnalysis;

using Enums;
using Symbols;
using Types;
using TypeSymbol = Types.TypeInfo;

public sealed partial class SemanticAnalyzer
{
    private static TypeInfo SubstituteTypeParams(TypeInfo type,
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
        TypeInfo? genericDef = type switch
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
        if (type is TupleTypeInfo tuple)
        {
            return new TupleTypeInfo(elementTypes: newArgs);
        }

        return type;
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> implements the named protocol.
    /// Checks explicit protocol declarations, parent protocol chains, and structural conformance
    /// (i.e., whether the type has all required methods of the protocol).
    /// </summary>
    private bool ImplementsProtocol(TypeSymbol type, string protocolName)
    {
        // Get the protocol type
        TypeSymbol? protocol = LookupTypeWithImports(name: protocolName);
        if (protocol is not { Category: TypeCategory.Protocol })
        {
            return false;
        }

        // Get the list of implemented protocols for this type
        IReadOnlyList<TypeSymbol>? implementedProtocols = type switch
        {
            RecordTypeInfo record => record.ImplementedProtocols,
            EntityTypeInfo entity => entity.ImplementedProtocols,
            _ => null
        };

        if (implementedProtocols == null)
        {
            return false;
        }

        // Check if the protocol is directly declared
        foreach (TypeSymbol implemented in implementedProtocols)
        {
            if (implemented.Name == protocolName ||
                GetBaseTypeName(typeName: implemented.Name) == protocolName)
            {
                return true;
            }

            // Check parent protocols recursively
            if (implemented is ProtocolTypeInfo proto &&
                CheckParentProtocols(proto: proto, targetName: protocolName))
            {
                return true;
            }
        }

        // Check if the type has all required methods of the protocol (structural conformance)
        if (protocol is ProtocolTypeInfo protoType)
        {
            return CheckStructuralConformance(type: type, protocol: protoType);
        }

        return false;
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> explicitly declares conformance to the named protocol
    /// via <c>obeys</c>. Unlike <see cref="ImplementsProtocol"/>, this does NOT fall back to
    /// structural conformance, making it suitable for marker protocols like ConstCompatible.
    /// </summary>
    private bool ExplicitlyImplementsProtocol(TypeSymbol type, string protocolName)
    {
        IReadOnlyList<TypeSymbol>? implementedProtocols = type switch
        {
            RecordTypeInfo record => record.ImplementedProtocols,
            EntityTypeInfo entity => entity.ImplementedProtocols,
            _ => null
        };

        if (implementedProtocols == null)
        {
            return false;
        }

        foreach (TypeSymbol implemented in implementedProtocols)
        {
            if (implemented.Name == protocolName ||
                GetBaseTypeName(typeName: implemented.Name) == protocolName)
            {
                return true;
            }

            if (implemented is ProtocolTypeInfo proto &&
                CheckParentProtocols(proto: proto, targetName: protocolName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if any parent protocol matches the target.
    /// </summary>
    private bool CheckParentProtocols(ProtocolTypeInfo proto, string targetName)
    {
        foreach (ProtocolTypeInfo parent in proto.ParentProtocols)
        {
            if (parent.Name == targetName || GetBaseTypeName(typeName: parent.Name) == targetName)
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
    /// Checks if a type structurally conforms to a protocol by having all required methods.
    /// </summary>
    private bool CheckStructuralConformance(TypeSymbol type, ProtocolTypeInfo protocol)
    {
        foreach (ProtocolMethodInfo requiredMethod in protocol.Methods)
        {
            // Skip methods with default implementations
            if (requiredMethod.HasDefaultImplementation)
            {
                continue;
            }

            // Look for the method on the type
            RoutineInfo? typeMethod =
                _registry.LookupMethod(type: type, methodName: requiredMethod.Name);
            if (typeMethod == null)
            {
                // Also check with failable suffix
                if (requiredMethod.IsFailable)
                {
                    typeMethod = _registry.LookupMethod(type: type,
                        methodName: requiredMethod.Name + "!");
                }

                if (typeMethod == null)
                {
                    return false;
                }
            }

            // Verify method signature matches (basic check)
            if (!MethodSignatureMatches(typeMethod: typeMethod, protoMethod: requiredMethod))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks if a type's method signature matches a protocol method signature.
    /// </summary>
    private bool MethodSignatureMatches(RoutineInfo typeMethod, ProtocolMethodInfo protoMethod)
    {
        // Check failable matches
        if (typeMethod.IsFailable != protoMethod.IsFailable)
        {
            return false;
        }

        // Check parameter count (excluding 'me' parameter if present)
        // In-body methods have explicit 'me' as first parameter
        // Extension methods don't include 'me' in the parameter list
        int expectedParamCount = protoMethod.ParameterTypes.Count;
        bool hasMeParam = typeMethod.Parameters.Count > 0 &&
                          typeMethod.Parameters[index: 0].Name == "me";
        int actualParamCount = typeMethod.Parameters.Count - (hasMeParam
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
            TypeSymbol expectedType = protoMethod.ParameterTypes[index: i];
            TypeSymbol actualType = typeMethod.Parameters[index: startIndex + i].Type;

            // Handle protocol self type (Me) - should match the implementing type
            if (expectedType is ProtocolSelfTypeInfo)
            {
                // 'Me' in protocol should match the owner type of the method
                if (typeMethod.OwnerType != null &&
                    !TypesMatch(actual: actualType, expected: typeMethod.OwnerType))
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
        if (protoMethod.ReturnType != null && typeMethod.ReturnType != null)
        {
            if (!IsAssignableTo(source: typeMethod.ReturnType, target: protoMethod.ReturnType))
            {
                return false;
            }
        }
        else if (protoMethod.ReturnType == null != (typeMethod.ReturnType == null))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if two types match for protocol signature comparison.
    /// </summary>
    private bool TypesMatch(TypeSymbol actual, TypeSymbol expected)
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
            string baseName = GetBaseTypeName(typeName: actual.Name);
            if (baseName == expected.Name)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a hijacking source expression would result in nested hijacking.
    /// Nested hijacking occurs when trying to hijack a member of an already-hijacked object.
    /// </summary>
    /// <param name="source">The source expression for the hijacking statement.</param>
    /// <returns>True if this would be a nested hijacking, false otherwise.</returns>
}
