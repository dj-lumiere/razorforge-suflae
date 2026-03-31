namespace SemanticAnalysis;

using Enums;
using Symbols;
using SyntaxTree;
using Types;
using TypeInfo = Types.TypeInfo;

public sealed partial class TypeRegistry
{
    #region Routine Registration and Lookup

    /// <summary>
    /// Registers a routine in the registry.
    /// </summary>
    /// <param name="routine">The routine to register.</param>
    public void RegisterRoutine(RoutineInfo routine)
    {
        string key = routine.FullName;

        if (!_routines.ContainsKey(key: key))
        {
            // First overload wins for unqualified lookup
            _routines[key: key] = routine;
        }

        // Also register with parameter-based disambiguation for overload resolution
        string overloadKey = GetOverloadKey(routine);
        if (overloadKey != key) // Only store if different from primary key (avoids overwriting resolved entries)
            _routines[key: overloadKey] = routine;

        // Index by module-qualified name for unambiguous lookup
        string qualifiedName = routine.QualifiedName;
        if (qualifiedName != key)
        {
            _routinesByQualifiedName.TryAdd(qualifiedName, routine);
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
    /// Builds a disambiguated key for overloaded routines: "Name#ParamType1,ParamType2"
    /// </summary>
    private static string GetOverloadKey(RoutineInfo routine)
    {
        var paramTypes = string.Join(",", routine.Parameters.Select(p => p.Type.Name));
        return $"{routine.FullName}#{paramTypes}";
    }

    /// <summary>
    /// Looks up a routine overload that matches the given argument types.
    /// Falls back to the default (first-registered) overload if no exact match.
    /// </summary>
    public RoutineInfo? LookupRoutineOverload(string fullName, IReadOnlyList<TypeInfo> argTypes)
    {
        // Try exact overload match
        var paramTypeNames = string.Join(",", argTypes.Select(t => t.Name));
        string overloadKey = $"{fullName}#{paramTypeNames}";
        if (_routines.TryGetValue(key: overloadKey, value: out RoutineInfo? overload))
        {
            return overload;
        }

        // Try matching generic overloads by reconstructing the generic parameter pattern.
        // e.g., arg SortedSet[S64] → its generic def is SortedSet with GenericParameters ["T"]
        //        → try key "List.$create#SortedSet[T]" which matches the registered generic overload.
        foreach (TypeInfo argType in argTypes)
        {
            if (!argType.IsGenericResolution) continue;
            TypeInfo? genericDef = argType switch
            {
                RecordTypeInfo r => r.GenericDefinition,
                EntityTypeInfo e => e.GenericDefinition,
                ProtocolTypeInfo p => p.GenericDefinition,
                _ => null
            };
            if (genericDef?.GenericParameters == null) continue;

            string genericArgName = $"{genericDef.Name}[{string.Join(", ", genericDef.GenericParameters)}]";
            string genericOverloadKey = $"{fullName}#{genericArgName}";
            if (_routines.TryGetValue(key: genericOverloadKey, value: out RoutineInfo? genericOverload)
                && !genericOverload.IsVariadic) // Skip variadic overloads — handled by variadic fallback
            {
                return genericOverload;
            }
        }

        // For generic instances (e.g., List[Byte].$create), try the generic definition
        // (e.g., List[T].$create#U64) since overloads are registered on the generic definition.
        int bracketIdx = fullName.IndexOf('[');
        if (bracketIdx >= 0)
        {
            int closeBracketIdx = fullName.IndexOf("].", bracketIdx);
            if (closeBracketIdx >= 0)
            {
                string genericDefName = fullName[..bracketIdx] + fullName[(closeBracketIdx + 1)..];
                string genericOverloadKey = $"{genericDefName}#{paramTypeNames}";
                if (_routines.TryGetValue(key: genericOverloadKey, value: out RoutineInfo? genericDefOverload))
                {
                    return genericDefOverload;
                }
            }
        }

        // Fall back to default lookup
        return LookupRoutine(fullName: fullName);
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
        if (_routinesByQualifiedName.TryGetValue(fullName, out RoutineInfo? qualified))
        {
            return qualified;
        }

        // Try Core module prefix (Core routines are auto-imported)
        if (!fullName.Contains('.') && _routines.TryGetValue(key: $"Core.{fullName}", value: out routine))
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
        return _routinesByQualifiedName.GetValueOrDefault(qualifiedName);
    }

    /// <summary>
    /// Looks up a routine by its short name (without module prefix).
    /// Used by codegen when the AST has "Console.show" but the registry key is "IO.show".
    /// </summary>
    public RoutineInfo? LookupRoutineByName(string name)
    {
        foreach (var routine in _routines.Values)
        {
            if (routine.Name == name && routine.OwnerType == null)
                return routine;
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
        foreach (var routine in _routines.Values)
        {
            if (routine.Name == name && routine.OwnerType == null && routine.IsGenericDefinition)
            {
                if (!routine.IsVariadic)
                    return routine;
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
        foreach (var routine in _routines.Values)
        {
            if (routine.Name == name && routine.OwnerType == null
                && routine.IsGenericDefinition && routine.IsVariadic)
                return routine;
        }
        return null;
    }

    /// <summary>
    /// Updates a routine with resolved parameters and return type.
    /// </summary>
    /// <param name="routine">The routine to update.</param>
    /// <param name="parameters">The resolved parameters.</param>
    /// <param name="returnType">The resolved return type.</param>
    /// <param name="genericParameters">Updated generic parameters (may include implicit ones from protocol-as-type).</param>
    /// <param name="genericConstraints">Updated generic constraints (may include implicit ones from protocol-as-type).</param>
    public void UpdateRoutine(
        RoutineInfo routine,
        IReadOnlyList<ParameterInfo> parameters,
        TypeInfo? returnType,
        IReadOnlyList<string>? genericParameters,
        IReadOnlyList<GenericConstraintDeclaration>? genericConstraints)
    {
        string key = routine.FullName;
        if (!_routines.ContainsKey(key: key))
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

        _routines[key: key] = updatedRoutine;

        // Register with the resolved overload key so body-matching can find it
        string resolvedOverloadKey = GetOverloadKey(updatedRoutine);
        if (resolvedOverloadKey != key)
            _routines[key: resolvedOverloadKey] = updatedRoutine;

        // Update the module-qualified name index
        string qualifiedName = updatedRoutine.QualifiedName;
        if (qualifiedName != key)
        {
            _routinesByQualifiedName[qualifiedName] = updatedRoutine;
        }

        // Update the routines-by-owner index if this is a method
        if (routine.OwnerType != null)
        {
            string ownerKey = routine.OwnerType.Name;
            if (_routinesByOwner.TryGetValue(key: ownerKey, value: out List<RoutineInfo>? list))
            {
                int index = list.FindIndex(match: r => r.FullName == key);
                if (index >= 0)
                {
                    list[index] = updatedRoutine;
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
            var protoMethod = proto.Methods.FirstOrDefault(m => m.Name == methodName);
            if (protoMethod != null)
            {
                return SynthesizeProtocolMethod(proto, protoMethod, ownerType: type);
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
            if (genericDef == null && type.Name.Contains('['))
            {
                string baseName = type.Name[..type.Name.IndexOf('[')];
                genericDef = LookupType(baseName);
                // Try module-qualified name for non-Core types
                if (genericDef == null && !string.IsNullOrEmpty(type.Module))
                    genericDef = LookupType($"{type.Module}.{baseName}");
            }

            if (genericDef != null)
            {
                RoutineInfo? genericMethod = LookupMethod(type: genericDef, methodName: methodName);
                if (genericMethod != null)
                {
                    return SubstituteMethodForOwner(genericMethod, resolvedOwner: type);
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
        foreach (var (_, ownerMethods) in _routinesByOwner)
        {
            if (ownerMethods.Count > 0 && ownerMethods[0].OwnerType is GenericParameterTypeInfo)
            {
                RoutineInfo? universalMethod = ownerMethods.FirstOrDefault(predicate: m => m.Name == methodName);
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
    private RoutineInfo SynthesizeProtocolMethod(ProtocolTypeInfo proto, ProtocolMethodInfo protoMethod, TypeInfo ownerType)
    {
        // Build substitution map for generic protocols (e.g., Iterator[S64]: T → S64)
        Dictionary<string, TypeInfo>? substitution = null;
        if (proto.TypeArguments is { Count: > 0 })
        {
            var genericDef = proto.GenericDefinition ?? proto;
            if (genericDef.GenericParameters is { Count: > 0 })
            {
                substitution = new Dictionary<string, TypeInfo>();
                for (int i = 0; i < genericDef.GenericParameters.Count
                              && i < proto.TypeArguments.Count; i++)
                    substitution[genericDef.GenericParameters[i]] = proto.TypeArguments[i];
            }
        }

        // Resolve return type with substitution
        TypeInfo? resolvedReturn = protoMethod.ReturnType;
        if (resolvedReturn != null && substitution != null)
            resolvedReturn = SubstituteTypeInProtocol(resolvedReturn, substitution);

        // Convert ProtocolMethodInfo.ParameterTypes → ParameterInfo list
        var parameters = new List<ParameterInfo>();
        for (int i = 0; i < protoMethod.ParameterTypes.Count; i++)
        {
            TypeInfo paramType = protoMethod.ParameterTypes[i];
            if (substitution != null)
                paramType = SubstituteTypeInProtocol(paramType, substitution);

            string paramName = i < protoMethod.ParameterNames.Count
                ? protoMethod.ParameterNames[i]
                : $"arg{i}";
            parameters.Add(new ParameterInfo(paramName, paramType) { Index = i });
        }

        return new RoutineInfo(protoMethod.Name)
        {
            OwnerType = ownerType,
            Parameters = parameters,
            ReturnType = resolvedReturn,
            IsFailable = protoMethod.IsFailable,
            ModificationCategory = protoMethod.Modification,
            Storage = protoMethod.IsInstanceMethod ? StorageClass.None : StorageClass.Common,
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
            return method;

        var substitution = new Dictionary<string, TypeInfo>();
        for (int i = 0; i < genericDef.GenericParameters.Count
                      && i < resolvedOwner.TypeArguments.Count; i++)
        {
            substitution[genericDef.GenericParameters[i]] = resolvedOwner.TypeArguments[i];
        }

        if (substitution.Count == 0)
            return method;

        // Substitute types in parameters
        var substitutedParams = method.Parameters
            .Select(p => RoutineInfo.SubstituteParameterType(param: p, substitution: substitution))
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
            $"{genericDef.FullName}[{string.Join(separator: ", ", values: typeArguments.Select(selector: t => t.Name))}]";

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
    private TypeInfo SubstituteTypeInProtocol(TypeInfo type, Dictionary<string, TypeInfo> substitution)
    {
        // Direct substitution for generic parameters
        if (type is GenericParameterTypeInfo && substitution.TryGetValue(type.Name, out TypeInfo? sub))
            return sub;

        // Recursive substitution in type arguments
        if (type.TypeArguments is not { Count: > 0 })
            return type;

        bool anyChanged = false;
        var newArgs = new List<TypeInfo>();
        foreach (var arg in type.TypeArguments)
        {
            var resolved = SubstituteTypeInProtocol(arg, substitution);
            newArgs.Add(resolved);
            if (!ReferenceEquals(resolved, arg)) anyChanged = true;
        }
        if (!anyChanged) return type;

        // Get the generic definition and create a new instance with substituted args
        TypeInfo? genDef = type switch
        {
            EntityTypeInfo e => e.GenericDefinition,
            RecordTypeInfo r => r.GenericDefinition,
            ProtocolTypeInfo p => p.GenericDefinition,
            _ => null
        };

        if (genDef != null)
            return GetOrCreateResolution(genDef, newArgs);

        return type;
    }

    #endregion
}
