namespace Compiler.Resolution;

using SemanticVerification.Symbols;
using SyntaxTree;
using SemanticVerification.Types;
using TypeInfo = SemanticVerification.Types.TypeInfo;

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
            string ownerKey = routine.OwnerType.FullName;
            if (!_routinesByOwner.TryGetValue(key: ownerKey, value: out List<RoutineInfo>? list))
            {
                list = [];
                _routinesByOwner[key: ownerKey] = list;
            }

            list.Add(item: routine);

            // Index universal methods (on GenericParameterTypeInfo owners) by name for O(1) lookup
            if (routine.OwnerType is GenericParameterTypeInfo)
            {
                _universalMethods.TryAdd(key: routine.Name, value: routine);
            }
        }

        // Index generic free functions (no owner, has generic parameters) for O(1) generic overload lookup
        if (routine.OwnerType == null && routine.IsGenericDefinition)
        {
            if (!_genericFreeFunctions.TryGetValue(key: routine.Name, value: out List<RoutineInfo>? list))
            {
                list = [];
                _genericFreeFunctions[key: routine.Name] = list;
            }

            if (!list.Contains(item: routine))
            {
                list.Add(item: routine);
            }
        }

        // Secondary (name, failability) index for O(1) isFailable-aware lookup.
        // First-registration wins per (BaseName, IsFailable) and (QualifiedName, IsFailable).
        var nameFailKey = (routine.BaseName, routine.IsFailable);
        _routinesByNameAndFailability.TryAdd(key: nameFailKey, value: routine);
        var qualFailKey = (routine.QualifiedName, routine.IsFailable);
        if (qualFailKey != nameFailKey)
            _routinesByNameAndFailability.TryAdd(key: qualFailKey, value: routine);
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

        // Fall back to default lookup
        return LookupRoutine(fullName: baseName);
    }

    /// <summary>
    /// Looks up a routine by its full name.
    /// </summary>
    /// <param name="fullName">The fully qualified name of the routine.</param>
    /// <returns>The routine info if found, null otherwise.</returns>
    public RoutineInfo? LookupRoutine(string fullName, bool? isFailable = null)
    {
        if (isFailable == null)
        {
            if (_routines.TryGetValue(key: fullName, value: out RoutineInfo? routine)) return routine;
            if (_routineResolutions.TryGetValue(key: fullName, value: out routine)) return routine;
            if (_routinesByQualifiedName.TryGetValue(key: fullName, value: out routine)) return routine;
            if (!fullName.Contains(value: '.') &&
                _routines.TryGetValue(key: $"Core.{fullName}", value: out routine))
                return routine;
            return null;
        }

        // isFailable != null: SA is disambiguating between failable and non-failable variants of
        // the same logical name. The parser strips '!' from routine names and tracks failability
        // separately, so fullName is always without '!' here (e.g., "parse", "List.$getitem").
        // Use the (BaseName, IsFailable) secondary index for O(1) lookup.
        bool wantsFailable = isFailable.Value;
        var nameFailKey = (fullName, wantsFailable);
        if (_routinesByNameAndFailability.TryGetValue(key: nameFailKey, value: out RoutineInfo? found))
            return found;

        // Also try resolution cache (monomorphized instances) with failability check
        if (_routineResolutions.TryGetValue(key: fullName, value: out found) &&
            found.IsFailable == wantsFailable)
            return found;

        // Core prefix: try "Core.{name}" (auto-imported Core routines looked up bare)
        if (!fullName.Contains(value: '.'))
        {
            var coreFailKey = ($"Core.{fullName}", wantsFailable);
            if (_routinesByNameAndFailability.TryGetValue(key: coreFailKey, value: out found))
                return found;
        }

        // Last resort: check if the non-qualified fast path already has a matching-failability entry
        if (_routines.TryGetValue(key: fullName, value: out found) &&
            found.IsFailable == wantsFailable)
            return found;

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
    /// Falls back to a linear scan only when neither fast-path dictionaries find a match.
    /// </summary>
    public RoutineInfo? LookupRoutineByName(string name, bool? isFailable = null)
    {
        // Fast path: _genericFreeFunctions covers the generic-definition case callers commonly need.
        // For non-generic free functions, try Core prefix and module-qualified name index.
        if (_routines.TryGetValue(key: name, value: out RoutineInfo? found) &&
            found.OwnerType == null &&
            (isFailable == null || found.IsFailable == isFailable))
            return found;

        if (_routines.TryGetValue(key: $"Core.{name}", value: out found) &&
            found.OwnerType == null &&
            (isFailable == null || found.IsFailable == isFailable))
            return found;

        // Fallback: targeted linear scan (rare; used only by codegen short-name lookups)
        foreach (RoutineInfo routine in _routines.Values)
        {
            if (routine.Name == name &&
                routine.OwnerType == null &&
                (isFailable == null || routine.IsFailable == isFailable))
            {
                return routine;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a generic overload of a free function by name (e.g., show[T] for "show").
    /// O(1): backed by <see cref="_genericFreeFunctions"/> index populated in <see cref="RegisterRoutine"/>.
    /// </summary>
    public RoutineInfo? LookupGenericOverload(string name)
    {
        if (!_genericFreeFunctions.TryGetValue(key: name, value: out List<RoutineInfo>? candidates))
            return null;

        // Prefer non-variadic overloads (e.g., show[T](value: T) over show[T](values...: T))
        RoutineInfo? fallback = null;
        foreach (RoutineInfo routine in candidates)
        {
            if (!routine.IsVariadic) return routine;
            fallback ??= routine;
        }

        return fallback;
    }

    /// <summary>
    /// Finds a variadic generic overload of a free function by name (e.g., show[T](values...: T) for "show").
    /// O(1): backed by <see cref="_genericFreeFunctions"/> index populated in <see cref="RegisterRoutine"/>.
    /// </summary>
    public RoutineInfo? LookupVariadicGenericOverload(string name)
    {
        if (!_genericFreeFunctions.TryGetValue(key: name, value: out List<RoutineInfo>? candidates))
            return null;

        foreach (RoutineInfo routine in candidates)
        {
            if (routine.IsVariadic) return routine;
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
            ModulePath = routine.ModulePath,
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
            string ownerKey = routine.OwnerType.FullName;
            if (_routinesByOwner.TryGetValue(key: ownerKey, value: out List<RoutineInfo>? list))
            {
                int index = list.FindIndex(match: r => r.BaseName == baseName);
                if (index >= 0)
                {
                    list[index: index] = updatedRoutine;
                }
            }
        }

        // Update the generic free functions index if this routine is/became a generic definition
        if (updatedRoutine.OwnerType == null && updatedRoutine.IsGenericDefinition)
        {
            if (!_genericFreeFunctions.TryGetValue(key: updatedRoutine.Name,
                    value: out List<RoutineInfo>? genericList))
            {
                genericList = [];
                _genericFreeFunctions[key: updatedRoutine.Name] = genericList;
            }

            // Replace stale entry for this base name
            int idx = genericList.FindIndex(match: r => r.BaseName == baseName);
            if (idx >= 0)
                genericList[index: idx] = updatedRoutine;
            else
                genericList.Add(item: updatedRoutine);
        }

        // Update (name, failability) index with the resolved version
        var updatedNameFailKey = (updatedRoutine.BaseName, updatedRoutine.IsFailable);
        _routinesByNameAndFailability[key: updatedNameFailKey] = updatedRoutine;
        var updatedQualFailKey = (updatedRoutine.QualifiedName, updatedRoutine.IsFailable);
        if (updatedQualFailKey != updatedNameFailKey)
            _routinesByNameAndFailability[key: updatedQualFailKey] = updatedRoutine;
    }

    /// <summary>
    /// Looks up a method on a type. Returns a fully-resolved RoutineInfo with type parameters
    /// substituted for generic owners and protocol methods.
    /// </summary>
    /// <param name="type">The type to search for the method.</param>
    /// <param name="methodName">The name of the method to look up.</param>
    /// <returns>The routine info if found, null otherwise.</returns>
    public RoutineInfo? LookupMethod(TypeInfo type, string methodName, bool? isFailable = null)
    {
        // First check the type's own methods
        if (_routinesByOwner.TryGetValue(key: type.FullName, value: out List<RoutineInfo>? methods))
        {
            RoutineInfo? method = methods.FirstOrDefault(predicate: m =>
                m.Name == methodName &&
                (isFailable == null || m.IsFailable == isFailable));
            if (method != null)
            {
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
                // Wrapper types (Snatched[Byte], Viewed[T], etc.) — look up by base name
                WrapperTypeInfo => LookupType(name: type.Name),
                _ => null
            };

            if (genericDef != null)
            {
                RoutineInfo? genericMethod =
                    LookupMethod(type: genericDef, methodName: methodName, isFailable: isFailable);
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
        // These methods are available on all types — O(1) lookup via _universalMethods index
        if (_universalMethods.TryGetValue(key: methodName, value: out RoutineInfo? universalMethod))
        {
            return universalMethod;
        }

        return null;
    }

    /// <summary>
    /// Looks up a method overload on a type using the argument types for disambiguation.
    /// This is used for operator/member dispatch where multiple wired overloads may exist
    /// on the same owner type (for example Moment.$sub(Duration) and Moment.$sub(Moment)).
    /// </summary>
    public RoutineInfo? LookupMethodOverload(TypeInfo type, string methodName,
        IReadOnlyList<TypeInfo> argTypes)
    {
        var candidates = new List<RoutineInfo>();
        CollectMethodCandidates(type: type, methodName: methodName, candidates: candidates);

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
                return candidate;
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
                return candidate;
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
            AsyncStatus = AsyncStatus.None,
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
            // Wrapper types (Snatched[Byte], etc.) — look up generic def by base name
            WrapperTypeInfo => LookupType(name: resolvedOwner.Name),
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

        // Only keep method-level generic parameters (owner params are now resolved)
        IReadOnlyList<string>? methodOnlyGenericParams = method.GenericParameters?
            .Where(gp => !substitution.ContainsKey(gp))
            .ToList();
        if (methodOnlyGenericParams?.Count == 0)
            methodOnlyGenericParams = null;

        // Only keep constraints on method-level generic parameters
        IReadOnlyList<GenericConstraintDeclaration>? methodOnlyConstraints = method.GenericConstraints?
            .Where(c => methodOnlyGenericParams?.Contains(c.ParameterName) == true)
            .ToList();
        if (methodOnlyConstraints?.Count == 0)
            methodOnlyConstraints = null;

        return new RoutineInfo(name: method.Name)
        {
            Kind = method.Kind,
            OwnerType = resolvedOwner,
            Parameters = substitutedParams,
            ReturnType = substitutedReturn,
            IsFailable = method.IsFailable,
            DeclaredModification = method.DeclaredModification,
            ModificationCategory = method.ModificationCategory,
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
            Storage = method.Storage,
            AsyncStatus = method.AsyncStatus,
            OriginalName = method.OriginalName
        };
    }

    private void CollectMethodCandidates(TypeInfo type, string methodName, List<RoutineInfo> candidates)
    {
        if (_routinesByOwner.TryGetValue(key: type.FullName, value: out List<RoutineInfo>? methods))
        {
            candidates.AddRange(methods.Where(predicate: m => m.Name == methodName));
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
                _ => null
            };

            if (genericDef != null)
            {
                var genericCandidates = new List<RoutineInfo>();
                CollectMethodCandidates(type: genericDef, methodName: methodName, candidates: genericCandidates);
                foreach (RoutineInfo genericCandidate in genericCandidates)
                {
                    if (genericCandidate.OwnerType is GenericParameterTypeInfo)
                    {
                        candidates.Add(item: genericCandidate);
                    }
                    else
                    {
                        candidates.Add(item: SubstituteMethodForOwner(method: genericCandidate,
                            resolvedOwner: type));
                    }
                }
            }
        }

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
                CollectMethodCandidates(type: protocol, methodName: methodName, candidates: candidates);
            }
        }

        if (_universalMethods.TryGetValue(key: methodName, value: out RoutineInfo? universalMethod))
        {
            candidates.Add(item: universalMethod);
        }
    }

    private static bool IsMethodArgumentAssignable(TypeInfo source, TypeInfo target)
    {
        if (source.Name == target.Name)
        {
            return true;
        }

        if (target is ProtocolTypeInfo)
        {
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
        if (_prunedGenericBases.Count == 0)
            return _routines.Values;
        return _routines.Values.Where(r => !_prunedGenericBases.Contains(r.BaseName));
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
    /// Gets all methods for a type.
    /// </summary>
    /// <param name="type">The type to get methods for.</param>
    /// <returns>An enumerable of all methods for the type.</returns>
    public IEnumerable<RoutineInfo> GetMethodsForType(TypeInfo type)
    {
        if (_routinesByOwner.TryGetValue(key: type.FullName, value: out List<RoutineInfo>? methods))
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
