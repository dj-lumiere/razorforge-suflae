using Compiler.Postprocessing;

namespace Compiler.CodeGen;

using System.Text;
using Desugaring;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

public partial class LlvmCodeGenerator
{
    private void GenerateProtocolDispatchStubs()
    {
        foreach ((string mangledName, ProtocolDispatchInfo info) in _pendingProtocolDispatches)
        {
            // Skip if already defined
            if (_generatedFunctionDefs.Contains(item: mangledName))
            {
                continue;
            }

            // Skip if no declare was emitted yet
            if (!_generatedFunctions.Contains(item: mangledName))
            {
                continue;
            }

            // Determine the return type from the protocol method declaration
            ProtocolMethodInfo? protoMethod = info.Protocol.Methods
               .FirstOrDefault(predicate: m => m.Name == info.MethodName && !m.IsFailable);
            protoMethod ??= info.Protocol.Methods
               .FirstOrDefault(predicate: m => m.Name == info.MethodName);

            string retType = protoMethod?.ReturnType != null
                ? GetLlvmType(type: protoMethod.ReturnType)
                : "void";

            // Trigger compilation of every uncompiled implementer, then defer if any were added.
            // This ensures the switch table covers all concrete types, not just the first compiled one.
            int triggered = TriggerAllImplementerCompilations(
                protocol: info.Protocol, methodName: info.MethodName);
            if (triggered > 0)
            {
                continue; // Some declared-but-not-compiled implementations queued — revisit next pass
            }

            // Find all compiled concrete implementers (type + mangled function name)
            List<(TypeInfo ConcreteType, string FuncName)> implementers =
                FindAllCompiledImplementers(protocol: info.Protocol, methodName: info.MethodName);

            // Nothing compiled (and nothing left to trigger) — defer stub
            if (implementers.Count == 0)
            {
                continue;
            }

            // Generate a switch-based dispatch stub: branch on type_id to the right concrete method
            StringBuilder sb = _functionDefinitions;
            string defaultLabel = NextLabel(prefix: "dispatch_default");
            var caseLabels = implementers
               .Select(selector: (_, i) => NextLabel(prefix: $"dispatch_{i}_"))
               .ToList();

            EmitLine(sb: sb, line: $"define {retType} @{mangledName}(ptr %self, i64 %type_id) {{");
            EmitLine(sb: sb, line: "entry:");

            // Build switch: switch i64 %type_id, label %default [ i64 X, label %dN ... ]
            var switchSb = new StringBuilder();
            switchSb.Append($"  switch i64 %type_id, label %{defaultLabel} [");
            for (int i = 0; i < implementers.Count; i++)
            {
                ulong typeId = TypeIdHelper.ComputeTypeId(fullName: implementers[i].ConcreteType.FullName);
                switchSb.Append($"\n    i64 {typeId}, label %{caseLabels[index: i]}");
            }

            switchSb.Append("\n  ]");
            EmitLine(sb: sb, line: switchSb.ToString());

            // Emit one dispatch basic block per implementer
            for (int i = 0; i < implementers.Count; i++)
            {
                TypeInfo concreteType = implementers[i].ConcreteType;
                string funcName = implementers[i].FuncName;

                EmitLine(sb: sb, line: $"{caseLabels[index: i]}:");

                if (concreteType is RecordTypeInfo)
                {
                    // Record methods take the struct by value — load it from the pointer
                    string llvmType = GetLlvmType(type: concreteType);
                    string loaded = NextTemp();
                    EmitLine(sb: sb, line: $"  {loaded} = load {llvmType}, ptr %self");

                    if (retType == "void")
                    {
                        EmitLine(sb: sb, line: $"  call void @{funcName}({llvmType} {loaded})");
                        EmitLine(sb: sb, line: "  ret void");
                    }
                    else
                    {
                        string result = NextTemp();
                        EmitLine(sb: sb,
                            line: $"  {result} = call {retType} @{funcName}({llvmType} {loaded})");
                        EmitLine(sb: sb, line: $"  ret {retType} {result}");
                    }
                }
                else
                {
                    // Entity methods take ptr directly
                    if (retType == "void")
                    {
                        EmitLine(sb: sb, line: $"  call void @{funcName}(ptr %self)");
                        EmitLine(sb: sb, line: "  ret void");
                    }
                    else
                    {
                        string result = NextTemp();
                        EmitLine(sb: sb,
                            line: $"  {result} = call {retType} @{funcName}(ptr %self)");
                        EmitLine(sb: sb, line: $"  ret {retType} {result}");
                    }
                }
            }

            // Default case: unknown type_id — should never be reached in correct code
            EmitLine(sb: sb, line: $"{defaultLabel}:");
            EmitLine(sb: sb, line: "  unreachable");
            EmitLine(sb: sb, line: "}");
            EmitLine(sb: sb, line: "");

            _generatedFunctionDefs.Add(item: mangledName);
        }
    }

    /// <summary>
    /// Returns all concrete types that implement the given protocol and have the named method
    /// already compiled (present in _generatedFunctionDefs).
    /// </summary>
    private List<(TypeInfo ConcreteType, string FuncName)> FindAllCompiledImplementers(
        ProtocolTypeInfo protocol, string methodName)
    {
        var result = new List<(TypeInfo, string)>();
        ProtocolTypeInfo protocolDef = protocol.GenericDefinition ?? protocol;
        string protocolBaseName = protocolDef.Name;

        var seen = new HashSet<string>();
        foreach (TypeInfo type in _registry.GetTypesByCategory(category: TypeCategory.Entity)
                                           .Concat(second: _registry.GetTypesByCategory(
                                                category: TypeCategory.Record))
                                           .Concat(second: _registry.GetTypesByCategory(
                                                category: TypeCategory.Crashable)))
        {
            if (type.IsGenericDefinition)
            {
                continue;
            }

            if (!seen.Add(item: type.FullName))
            {
                continue;
            }

            IReadOnlyList<TypeInfo>? protocols = type switch
            {
                EntityTypeInfo e => e.ImplementedProtocols,
                RecordTypeInfo r => r.ImplementedProtocols,
                CrashableTypeInfo c => c.ImplementedProtocols,
                _ => null
            };
            if (protocols == null)
            {
                continue;
            }

            bool implements = protocols.Any(predicate: p =>
                (GetGenericBaseName(type: p) ?? p.Name) == protocolBaseName);
            if (!implements)
            {
                continue;
            }

            string candidateName =
                Q(name: $"{type.FullName}.{SanitizeLlvmName(name: methodName)}");
            if (_generatedFunctionDefs.Contains(item: candidateName))
            {
                result.Add(item: (type, candidateName));
            }
        }

        return result;
    }


    /// <summary>
    /// Finds the concrete implementation function for a protocol method.
    /// Searches all entity/record types for one that implements the given protocol
    /// and has a generated function body for the method.
    /// </summary>
    private string? FindConcreteImplementer(ProtocolTypeInfo protocol, string methodName)
    {
        ProtocolTypeInfo protocolDef = protocol.GenericDefinition ?? protocol;
        string protocolBaseName = protocolDef.Name;

        // Track whether we've already triggered a monomorphization for an uncompiled candidate
        bool triggered = false;

        // Search all entity/record/crashable types (including resolutions) for implementers
        var seen = new HashSet<string>();
        foreach (TypeInfo type in _registry.GetTypesByCategory(category: TypeCategory.Entity)
                                           .Concat(second: _registry.GetTypesByCategory(
                                                category: TypeCategory.Record))
                                           .Concat(second: _registry.GetTypesByCategory(
                                                category: TypeCategory.Crashable)))
        {
            if (type.IsGenericDefinition && protocol.TypeArguments == null)
            {
                continue;
            }

            if (!seen.Add(item: type.Name))
            {
                continue;
            }

            IReadOnlyList<TypeInfo>? protocols = type switch
            {
                EntityTypeInfo e => e.ImplementedProtocols,
                RecordTypeInfo r => r.ImplementedProtocols,
                CrashableTypeInfo c => c.ImplementedProtocols,
                _ => null
            };
            if (protocols == null)
            {
                continue;
            }

            foreach (TypeInfo impl in protocols)
            {
                // Check if this type implements the matching protocol
                // For generic types: List[T] obeys Iterable[T] → when T=S64, List[S64] obeys Iterable[S64]
                string implBaseName = GetGenericBaseName(type: impl) ?? impl.Name;
                if (implBaseName != protocolBaseName)
                {
                    continue;
                }

                // For resolved (non-generic-definition) types, verify the protocol type arguments match exactly.
                // Without this, EnumerateEmitter[S64] (obeys Iterator[Tuple[S64, S64]]) would
                // incorrectly match a search for Iterator[S64].
                if (!type.IsGenericDefinition && protocol.TypeArguments is { Count: > 0 } &&
                    impl.TypeArguments is { Count: > 0 })
                {
                    if (protocol.TypeArguments.Count != impl.TypeArguments.Count)
                    {
                        continue;
                    }

                    bool argsMatch = true;
                    for (int i = 0; i < protocol.TypeArguments.Count; i++)
                    {
                        if (protocol.TypeArguments[index: i].FullName !=
                            impl.TypeArguments[index: i].FullName)
                        {
                            argsMatch = false;
                            break;
                        }
                    }

                    if (!argsMatch)
                    {
                        continue;
                    }
                }

                // Match: now determine the concrete type resolution
                TypeInfo concreteType = type;

                // If the type is a generic definition, resolve it using the protocol's type args
                if (type.IsGenericDefinition && protocol.TypeArguments is { Count: > 0 })
                {
                    // The protocol on the generic def has the same generic params (e.g., Iterable[T])
                    // We need to map protocol's T to the concrete type arg (e.g., S64)
                    // Then resolve the generic def with those args
                    ProtocolTypeInfo protocolGenDef = protocol.GenericDefinition ?? protocol;
                    if (protocolGenDef.GenericParameters is { Count: > 0 } &&
                        type.GenericParameters is { Count: > 0 })
                    {
                        // Build mapping: protocol param → concrete type arg
                        var mapping = new Dictionary<string, TypeInfo>();
                        for (int i = 0;
                             i < protocolGenDef.GenericParameters.Count &&
                             i < protocol.TypeArguments.Count;
                             i++)
                        {
                            mapping[key: protocolGenDef.GenericParameters[index: i]] =
                                protocol.TypeArguments[index: i];
                        }

                        // Map type's generic params using the impl protocol's type args
                        // e.g., List[T] with Iterable[T]: T maps to protocol param T → S64
                        var typeArgs = new List<TypeInfo>();
                        if (impl.TypeArguments is { Count: > 0 })
                        {
                            foreach (TypeInfo implArg in impl.TypeArguments)
                            {
                                if (implArg is GenericParameterTypeInfo gp &&
                                    mapping.TryGetValue(key: gp.Name,
                                        value: out TypeInfo? concrete))
                                {
                                    typeArgs.Add(item: concrete);
                                }
                                else if (mapping.TryGetValue(key: implArg.Name,
                                             value: out TypeInfo? concrete2))
                                {
                                    typeArgs.Add(item: concrete2);
                                }
                                else
                                {
                                    typeArgs.Add(item: implArg);
                                }
                            }
                        }
                        else
                        {
                            // If impl has no type args, use protocol's type args directly
                            typeArgs.AddRange(collection: protocol.TypeArguments);
                        }

                        if (typeArgs.Count == type.GenericParameters.Count)
                        {
                            concreteType = _registry.GetOrCreateResolution(genericDef: type,
                                typeArguments: typeArgs);
                        }
                        else
                        {
                            continue;
                        }
                    }
                    else
                    {
                        continue;
                    }
                }
                else if (type.IsGenericDefinition)
                {
                    continue; // Can't resolve without type args
                }

                // Check if the concrete method exists in generated functions
                string candidateName =
                    Q(name: $"{concreteType.FullName}.{SanitizeLlvmName(name: methodName)}");
                if (_generatedFunctionDefs.Contains(item: candidateName))
                {
                    return candidateName;
                }

                // Method not compiled yet — trigger monomorphization for ONE candidate so it
                // will be available in a subsequent iteration of the multi-pass loop.
                // Only trigger once (first match) to avoid cascading monomorphization of
                // all implementers (e.g., SetIterator, SkipEmitter, etc.) that aren't needed.
                if (!triggered)
                {
                    TypeInfo? genericDef = concreteType switch
                    {
                        EntityTypeInfo e => e.GenericDefinition,
                        RecordTypeInfo r => r.GenericDefinition,
                        _ => null
                    };
                    if (genericDef != null)
                    {
                        RoutineInfo? genericMethod =
                            _registry.LookupMethod(type: genericDef, methodName: methodName);
                        if (genericMethod != null &&
                            !_planner.HasEntry(mangledName: candidateName))
                        {
                            // Ensure entity type struct is defined for the concrete type
                            if (concreteType is EntityTypeInfo entityType)
                            {
                                GenerateEntityType(entity: entityType);
                            }

                            // Declare the concrete implementer so EmitFromPreMonomorphizedBodies
                            // can emit its body (GMP pre-builds bodies for all generic resolutions).
                            if (!_generatedFunctions.Contains(item: candidateName))
                                GenerateFunctionDeclaration(routine: genericMethod,
                                    nameOverride: candidateName);
                            triggered = true;
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Triggers compilation (monomorphization) of every uncompiled concrete implementer
    /// of the given protocol method. Returns the number of new compilations triggered.
    /// Call before generating the dispatch stub so the switch table is complete.
    /// </summary>
    private int TriggerAllImplementerCompilations(ProtocolTypeInfo protocol, string methodName)
    {
        ProtocolTypeInfo protocolDef = protocol.GenericDefinition ?? protocol;
        string protocolBaseName = protocolDef.Name;
        int count = 0;

        var seen = new HashSet<string>();
        foreach (TypeInfo type in _registry.GetTypesByCategory(category: TypeCategory.Entity)
                                           .Concat(second: _registry.GetTypesByCategory(
                                                category: TypeCategory.Record))
                                           .Concat(second: _registry.GetTypesByCategory(
                                                category: TypeCategory.Crashable)))
        {
            if (type.IsGenericDefinition && protocol.TypeArguments == null)
                continue;
            if (!seen.Add(item: type.Name))
                continue;

            IReadOnlyList<TypeInfo>? protocols = type switch
            {
                EntityTypeInfo e => e.ImplementedProtocols,
                RecordTypeInfo r => r.ImplementedProtocols,
                CrashableTypeInfo c => c.ImplementedProtocols,
                _ => null
            };
            if (protocols == null)
                continue;

            foreach (TypeInfo impl in protocols)
            {
                string implBaseName = GetGenericBaseName(type: impl) ?? impl.Name;
                if (implBaseName != protocolBaseName)
                    continue;

                // Verify type-argument match for resolved types (same logic as FindConcreteImplementer)
                if (!type.IsGenericDefinition && protocol.TypeArguments is { Count: > 0 } &&
                    impl.TypeArguments is { Count: > 0 })
                {
                    if (protocol.TypeArguments.Count != impl.TypeArguments.Count)
                        continue;
                    bool argsMatch = true;
                    for (int i = 0; i < protocol.TypeArguments.Count; i++)
                    {
                        if (protocol.TypeArguments[index: i].FullName !=
                            impl.TypeArguments[index: i].FullName)
                        {
                            argsMatch = false;
                            break;
                        }
                    }

                    if (!argsMatch)
                        continue;
                }

                // Resolve concrete type (handle generic definitions)
                TypeInfo concreteType = type;
                if (type.IsGenericDefinition && protocol.TypeArguments is { Count: > 0 })
                {
                    ProtocolTypeInfo protocolGenDef = protocol.GenericDefinition ?? protocol;
                    if (protocolGenDef.GenericParameters is { Count: > 0 } &&
                        type.GenericParameters is { Count: > 0 })
                    {
                        var mapping = new Dictionary<string, TypeInfo>();
                        for (int i = 0;
                             i < protocolGenDef.GenericParameters.Count &&
                             i < protocol.TypeArguments.Count;
                             i++)
                        {
                            mapping[key: protocolGenDef.GenericParameters[index: i]] =
                                protocol.TypeArguments[index: i];
                        }

                        var typeArgs = new List<TypeInfo>();
                        if (impl.TypeArguments is { Count: > 0 })
                        {
                            foreach (TypeInfo implArg in impl.TypeArguments)
                            {
                                if (implArg is GenericParameterTypeInfo gp &&
                                    mapping.TryGetValue(key: gp.Name, value: out TypeInfo? concrete))
                                    typeArgs.Add(item: concrete);
                                else if (mapping.TryGetValue(key: implArg.Name,
                                             value: out TypeInfo? concrete2))
                                    typeArgs.Add(item: concrete2);
                                else
                                    typeArgs.Add(item: implArg);
                            }
                        }
                        else
                        {
                            typeArgs.AddRange(collection: protocol.TypeArguments);
                        }

                        if (typeArgs.Count == type.GenericParameters.Count)
                            concreteType = _registry.GetOrCreateResolution(genericDef: type,
                                typeArguments: typeArgs);
                        else
                            continue;
                    }
                    else
                    {
                        continue;
                    }
                }
                else if (type.IsGenericDefinition)
                {
                    continue;
                }

                string candidateName =
                    Q(name: $"{concreteType.FullName}.{SanitizeLlvmName(name: methodName)}");

                // Skip if already compiled
                if (_generatedFunctionDefs.Contains(item: candidateName))
                    continue;

                // Skip if already queued
                if (_planner.HasEntry(mangledName: candidateName))
                    continue;

                // Trigger monomorphization for this implementer
                TypeInfo? genericDef = concreteType switch
                {
                    EntityTypeInfo e => e.GenericDefinition,
                    RecordTypeInfo r => r.GenericDefinition,
                    _ => null
                };
                TypeInfo lookupType = genericDef ?? concreteType;
                RoutineInfo? genericMethod =
                    _registry.LookupMethod(type: lookupType, methodName: methodName);
                if (genericMethod == null)
                    continue;

                // Force-declare if not yet declared: protocol dispatch routes to ANY conforming
                // type at runtime via type_id, so all implementations must appear in the switch.
                if (!_generatedFunctions.Contains(item: candidateName))
                    GenerateFunctionDeclaration(routine: genericMethod, nameOverride: candidateName);

                if (concreteType is EntityTypeInfo entityType)
                    GenerateEntityType(entity: entityType);
                else if (concreteType is CrashableTypeInfo crashableType)
                    GenerateCrashableType(crashable: crashableType);

                // Declaration already emitted at line 537; body handled by EmitFromPreMonomorphizedBodies.
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Finds a declaration line for a given mangled function name.
    /// </summary>
    private string? FindDeclarationLine(string mangledName)
    {
        return _rfFunctionDeclarations.TryGetValue(key: mangledName, value: out string? line)
            ? line
            : null;
    }

    /// <summary>
    /// Splits LLVM parameter types, handling nested braces (e.g., "{ i64, ptr }").
    /// </summary>
    private static List<string> SplitLlvmParams(string paramList)
    {
        var result = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < paramList.Length; i++)
        {
            if (paramList[index: i] == '{')
            {
                depth++;
            }
            else if (paramList[index: i] == '}')
            {
                depth--;
            }
            else if (paramList[index: i] == ',' && depth == 0)
            {
                result.Add(item: paramList[start..i]
                   .Trim());
                start = i + 1;
            }
        }

        if (start < paramList.Length)
        {
            result.Add(item: paramList[start..]
               .Trim());
        }

        return result;
    }
}
