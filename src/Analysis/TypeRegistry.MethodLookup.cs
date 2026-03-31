namespace SemanticAnalysis;

using Symbols;
using SyntaxTree;
using Types;
using TypeInfo = Types.TypeInfo;

public sealed partial class TypeRegistry
{
    #region Routine Registration and Lookup

    /// <summary>
    /// Registers a routine in the registry.
    /// Uses RegistryKey (BaseName + param types) as the primary key for overload-specific lookup,
    /// and BaseName for first-overload-wins unqualified lookup.
    /// </summary>
    /// <param name="routine">The routine to register.</param>
    public void RegisterRoutine(RoutineInfo routine)
    {
        string registryKey = routine.RegistryKey;
        string baseName = routine.BaseName;

        // Register under RegistryKey for exact overload matching
        _routines[key: registryKey] = routine;

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

        // Index by owner type for fast method lookup
        if (routine.OwnerType != null)
        {
            string ownerKey = routine.OwnerType.Name;
            if (!_routinesByOwner.TryGetValue(key: ownerKey, value: out List<RoutineInfo>? list))
            {
                list = [];
                _routinesByOwner[key: ownerKey] = list;
            }

            list.Add(item: routine);
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
    public RoutineInfo? LookupRoutineOverload(string baseName, IReadOnlyList<TypeInfo> argTypes)
    {
        // Try exact overload match by RegistryKey format
        string paramTypeNames =
            string.Join(separator: ",", values: argTypes.Select(selector: t => t.Name));
        string registryKey = $"{baseName}#{paramTypeNames}";
        if (_routines.TryGetValue(key: registryKey, value: out RoutineInfo? overload))
        {
            return overload;
        }

        // Try matching generic overloads by reconstructing the generic parameter pattern.
        // e.g., arg SortedSet[S64] → its generic def is SortedSet with GenericParameters ["T"]
        //        → try key "List.$create#SortedSet[T]" which matches the registered generic overload.
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

            string genericArgName =
                $"{genericDef.Name}[{string.Join(separator: ", ", values: genericDef.GenericParameters)}]";
            string genericRegistryKey = $"{baseName}#{genericArgName}";
            if (_routines.TryGetValue(key: genericRegistryKey,
                    value: out RoutineInfo? genericOverload) &&
                !genericOverload
                   .IsVariadic) // Skip variadic overloads — handled by variadic fallback
            {
                return genericOverload;
            }
        }

        // For generic instances (e.g., List[Byte].$create), try the generic definition
        // (e.g., List[T].$create#U64) since overloads are registered on the generic definition.
        int bracketIdx = baseName.IndexOf(value: '[');
        if (bracketIdx >= 0)
        {
            int closeBracketIdx = baseName.IndexOf(value: "].", startIndex: bracketIdx);
            if (closeBracketIdx >= 0)
            {
                string genericDefName = baseName[..bracketIdx] + baseName[(closeBracketIdx + 1)..];
                string genericRegistryKey = $"{genericDefName}#{paramTypeNames}";
                if (_routines.TryGetValue(key: genericRegistryKey,
                        value: out RoutineInfo? genericDefOverload))
                {
                    return genericDefOverload;
                }
            }
        }

        // Fall back to default lookup
        return LookupRoutine(fullName: baseName);
    }

    /// <summary>
    /// Looks up a routine by its full name.
    /// </summary>
    /// <param name="fullName">The fully qualified name of the routine.</param>
    /// <returns>The routine info if found, null otherwise.</returns>
    public RoutineInfo? LookupRoutine(string fullName)
    {
        if (_routines.TryGetValue(key: fullName, value: out RoutineInfo? routine))
        {
            return routine;
        }

        if (_routineResolutions.TryGetValue(key: fullName, value: out RoutineInfo? resolution))
        {
            return resolution;
        }

        // Fall back to module-qualified name lookup
        if (_routinesByQualifiedName.TryGetValue(key: fullName, value: out RoutineInfo? qualified))
        {
            return qualified;
        }

        // Try Core module prefix (Core routines are auto-imported)
        if (!fullName.Contains(value: '.') &&
            _routines.TryGetValue(key: $"Core.{fullName}", value: out routine))
        {
            return routine;
        }

        return null;
    }

    /// <summary>
    /// Looks up a routine by its module-qualified name (e.g., "Core.S8.$add").
    /// </summary>
    public RoutineInfo? LookupRoutineByQualifiedName(string qualifiedName)
    {
        return _routinesByQualifiedName.GetValueOrDefault(key: qualifiedName);
    }

    /// <summary>
    /// Looks up a routine by its short name (without module prefix).
    /// Used by codegen when the AST has "Console.show" but the registry key is "IO.show".
    /// </summary>
    public RoutineInfo? LookupRoutineByName(string name)
    {
        foreach (RoutineInfo routine in _routines.Values)
        {
            if (routine.Name == name && routine.OwnerType == null)
            {
                return routine;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a generic overload of a free function by name (e.g., show[T] for "show").
    /// </summary>
    public RoutineInfo? LookupGenericOverload(string name)
    {
        // Prefer non-variadic generic overloads (e.g., show[T](value: T) over show[T](values...: T))
        RoutineInfo? fallback = null;
        foreach (RoutineInfo routine in _routines.Values)
        {
            if (routine.Name == name && routine.OwnerType == null && routine.IsGenericDefinition)
            {
                if (!routine.IsVariadic)
                {
                    return routine;
                }

                fallback ??= routine;
            }
        }

        return fallback;
    }

    /// <summary>
    /// Finds a variadic generic overload of a free function by name (e.g., show[T](values...: T) for "show").
    /// </summary>
    public RoutineInfo? LookupVariadicGenericOverload(string name)
    {
        foreach (RoutineInfo routine in _routines.Values)
        {
            if (routine.Name == name && routine.OwnerType == null && routine.IsGenericDefinition &&
                routine.IsVariadic)
            {
                return routine;
            }
        }

        return null;
    }

    /// <summary>
    /// Updates a routine with resolved parameters and return type.
    /// Used for external declarations that are registered in Phase 1 without params.
    /// </summary>
    /// <param name="routine">The routine to update.</param>
    /// <param name="parameters">The resolved parameters.</param>
    /// <param name="returnType">The resolved return type.</param>
    /// <param name="genericParameters">Updated generic parameters (may include implicit ones from protocol-as-type).</param>
    /// <param name="genericConstraints">Updated generic constraints (may include implicit ones from protocol-as-type).</param>
    public void UpdateRoutine(RoutineInfo routine, IReadOnlyList<ParameterInfo> parameters,
        TypeInfo? returnType, IReadOnlyList<string>? genericParameters,
        IReadOnlyList<GenericConstraintDeclaration>? genericConstraints)
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
            Parameters = parameters,
            ReturnType = returnType,
            IsFailable = routine.IsFailable,
            DeclaredModification = routine.DeclaredModification,
            ModificationCategory = routine.ModificationCategory,
            GenericParameters = genericParameters,
            GenericConstraints = genericConstraints,
            Visibility = routine.Visibility,
            Location = routine.Location,
            Module = routine.Module,
            Annotations = routine.Annotations,
            CallingConvention = routine.CallingConvention,
            IsVariadic = routine.IsVariadic,
            IsDangerous = routine.IsDangerous,
            Storage = routine.Storage,
            AsyncStatus = routine.AsyncStatus
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
            string ownerKey = routine.OwnerType.Name;
            if (_routinesByOwner.TryGetValue(key: ownerKey, value: out List<RoutineInfo>? list))
            {
                int index = list.FindIndex(match: r => r.BaseName == baseName);
                if (index >= 0)
                {
                    list[index: index] = updatedRoutine;
                }
            }
        }
    }

    /// <summary>
    /// Looks up a method on a type. Returns a fully-resolved RoutineInfo with type parameters
    /// substituted for generic owners and protocol methods.
    /// </summary>
    /// <param name="type">The type to search for the method.</param>
    /// <param name="methodName">The name of the method to look up.</param>
    /// <returns>The routine info if found, null otherwise.</returns>
    public RoutineInfo? LookupMethod(TypeInfo type, string methodName)
    {
        // First check the type's own methods
        if (_routinesByOwner.TryGetValue(key: type.Name, value: out List<RoutineInfo>? methods))
        {
            RoutineInfo? method = methods.FirstOrDefault(predicate: m => m.Name == methodName);
            if (method != null)
            {
                return method;
            }
        }

        // For protocol types, check the protocol's method signatures
        if (type is ProtocolTypeInfo proto)
        {
            ProtocolMethodInfo? protoMethod =
                proto.Methods.FirstOrDefault(predicate: m => m.Name == methodName);
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
                _ => null
            };
            // Fallback: strip type arguments from name to find generic definition
            if (genericDef == null && type.Name.Contains(value: '['))
            {
                string baseName = type.Name[..type.Name.IndexOf(value: '[')];
                genericDef = LookupType(name: baseName);
                // Try module-qualified name for non-Core types
                if (genericDef == null && !string.IsNullOrEmpty(value: type.Module))
                {
                    genericDef = LookupType(name: $"{type.Module}.{baseName}");
                }
            }

            if (genericDef != null)
            {
                RoutineInfo? genericMethod =
                    LookupMethod(type: genericDef, methodName: methodName);
                if (genericMethod != null)
                {
                    // Universal methods on bare type params (e.g., T.get_address(), T.snatch())
                    // must keep their GenericParameterTypeInfo owner so codegen can record
                    // the correct monomorphization (T → concrete receiver type).
                    if (genericMethod.OwnerType is GenericParameterTypeInfo)
                    {
                        return genericMethod;
                    }

                    return SubstituteMethodForOwner(method: genericMethod, resolvedOwner: type);
                }
            }
        }

        // Check implemented protocols for default implementations
        IReadOnlyList<TypeInfo>? protocols = type switch
        {
            RecordTypeInfo r => r.ImplementedProtocols,
            EntityTypeInfo e => e.ImplementedProtocols,
            _ => null
        };

        if (protocols != null)
        {
            foreach (TypeInfo protocol in protocols)
            {
                RoutineInfo? protocolMethod = LookupMethod(type: protocol, methodName: methodName);
                if (protocolMethod != null)
                {
                    return protocolMethod;
                }
            }
        }

        // Fallback: check methods registered on generic type parameters (e.g., routine T.view())
        // These methods are available on all types
        foreach ((string _, List<RoutineInfo> ownerMethods) in _routinesByOwner)
        {
            if (ownerMethods.Count > 0 &&
                ownerMethods[index: 0].OwnerType is GenericParameterTypeInfo)
            {
                RoutineInfo? universalMethod =
                    ownerMethods.FirstOrDefault(predicate: m => m.Name == methodName);
                if (universalMethod != null)
                {
                    return universalMethod;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Synthesizes a complete RoutineInfo from a ProtocolMethodInfo, including parameters,
    /// modification category, storage, and all other metadata. Substitutes generic type
    /// parameters for instantiated generic protocols (e.g., Iterator[S64]: T → S64).
    /// </summary>
    private RoutineInfo SynthesizeProtocolMethod(ProtocolTypeInfo proto,
        ProtocolMethodInfo protoMethod, TypeInfo ownerType)
    {
        // Build substitution map for generic protocols (e.g., Iterator[S64]: T → S64)
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

        // Convert ProtocolMethodInfo.ParameterTypes → ParameterInfo list
        var parameters = new List<ParameterInfo>();
        for (int i = 0; i < protoMethod.ParameterTypes.Count; i++)
        {
            TypeInfo paramType = protoMethod.ParameterTypes[index: i];
            if (substitution != null)
            {
                paramType = SubstituteTypeInProtocol(type: paramType, substitution: substitution);
            }

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
            ModificationCategory = protoMethod.Modification,
            Storage = protoMethod.IsInstanceMethod
                ? StorageClass.None
                : StorageClass.Common,
            AsyncStatus = protoMethod.Name == "$next"
                ? AsyncStatus.Emitting
                : AsyncStatus.None,
            IsSynthesized = true,
            Location = protoMethod.Location
        };
    }

    /// <summary>
    /// Substitutes the owner type's generic type parameters into a method's signature.
    /// For example, List[S32].$add(item: T) → List[S32].$add(item: S32).
    /// </summary>
    private RoutineInfo SubstituteMethodForOwner(RoutineInfo method, TypeInfo resolvedOwner)
    {
        // Build substitution map from the resolved owner's generic definition
        TypeInfo? genericDef = resolvedOwner switch
        {
            RecordTypeInfo r => r.GenericDefinition,
            EntityTypeInfo e => e.GenericDefinition,
            ProtocolTypeInfo p => p.GenericDefinition,
            _ => null
        };

        if (genericDef?.GenericParameters == null || resolvedOwner.TypeArguments == null)
        {
            return method;
        }

        var substitution = new Dictionary<string, TypeInfo>();
        for (int i = 0;
             i < genericDef.GenericParameters.Count && i < resolvedOwner.TypeArguments.Count;
             i++)
        {
            substitution[key: genericDef.GenericParameters[index: i]] =
                resolvedOwner.TypeArguments[index: i];
        }

        if (substitution.Count == 0)
        {
            return method;
        }

        // Substitute types in parameters
        var substitutedParams = method.Parameters
                                      .Select(selector: p =>
                                           RoutineInfo.SubstituteParameterType(param: p,
                                               substitution: substitution))
                                      .ToList();

        // Substitute return type
        TypeInfo? substitutedReturn = method.ReturnType != null
            ? RoutineInfo.SubstituteType(type: method.ReturnType, substitution: substitution)
            : null;

        return new RoutineInfo(name: method.Name)
        {
            Kind = method.Kind,
            OwnerType = resolvedOwner,
            Parameters = substitutedParams,
            ReturnType = substitutedReturn,
            IsFailable = method.IsFailable,
            DeclaredModification = method.DeclaredModification,
            ModificationCategory = method.ModificationCategory,
            GenericParameters = method.GenericParameters,
            GenericConstraints = method.GenericConstraints,
            Visibility = method.Visibility,
            Location = method.Location,
            Module = method.Module,
            Annotations = method.Annotations,
            CallingConvention = method.CallingConvention,
            IsVariadic = method.IsVariadic,
            IsDangerous = method.IsDangerous,
            IsSynthesized = method.IsSynthesized,
            Storage = method.Storage,
            AsyncStatus = method.AsyncStatus
        };
    }

    /// <summary>
    /// Gets all registered routines.
    /// </summary>
    /// <returns>An enumerable of all registered routines.</returns>
    public IEnumerable<RoutineInfo> GetAllRoutines()
    {
        return _routines.Values;
    }

    /// <summary>
    /// Gets all methods for a type.
    /// </summary>
    /// <param name="type">The type to get methods for.</param>
    /// <returns>An enumerable of all methods for the type.</returns>
    public IEnumerable<RoutineInfo> GetMethodsForType(TypeInfo type)
    {
        if (_routinesByOwner.TryGetValue(key: type.Name, value: out List<RoutineInfo>? methods))
        {
            return methods;
        }

        return [];
    }

    /// <summary>
    /// Gets or creates a resolved generic routine.
    /// </summary>
    /// <param name="genericDef">The generic routine definition.</param>
    /// <param name="typeArguments">The type arguments for resolution.</param>
    /// <returns>The resolved routine (cached if already created).</returns>
    public RoutineInfo GetOrCreateRoutineResolution(RoutineInfo genericDef,
        IReadOnlyList<TypeInfo> typeArguments)
    {
        string name =
            $"{genericDef.BaseName}[{string.Join(separator: ", ", values: typeArguments.Select(selector: t => t.Name))}]";

        if (_routineResolutions.TryGetValue(key: name, value: out RoutineInfo? existing))
        {
            return existing;
        }

        RoutineInfo resolved = genericDef.CreateInstance(typeArguments: typeArguments);
        _routineResolutions[key: name] = resolved;

        return resolved;
    }

    #endregion

    #region Protocol Type Substitution

    /// <summary>
    /// Recursively substitutes generic type parameters in a type.
    /// Handles both direct parameters (T → S64) and composite types (Iterator[T] → Iterator[S64]).
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

    #endregion
}
