namespace Compiler.CodeGen;

using System.Text;
using SemanticAnalysis.Enums;
using SemanticAnalysis.Symbols;
using SemanticAnalysis.Types;
using SyntaxTree;

public partial class LLVMCodeGenerator
{
    private void GenerateProtocolDispatchStubs()
    {
        foreach ((string mangledName, ProtocolDispatchInfo info) in _pendingProtocolDispatches)
        {
            // Skip if already defined (a previous iteration or other codegen path generated it)
            if (_generatedFunctionDefs.Contains(item: mangledName))
            {
                continue;
            }

            // Find the declaration to get param/return types
            // The declaration was emitted in EmitMethodCall: "declare <retType> @<mangledName>(<paramTypes>)"
            if (!_generatedFunctions.Contains(item: mangledName))
            {
                continue;
            }

            // Find concrete implementers of this protocol resolution
            string? concreteFunc =
                FindConcreteImplementer(protocol: info.Protocol, methodName: info.MethodName);
            if (concreteFunc == null || !_generatedFunctionDefs.Contains(item: concreteFunc))
            {
                continue;
            }

            // Parse the declaration to extract return type and parameter types
            string? declLine = FindDeclarationLine(mangledName: mangledName);
            if (declLine == null)
            {
                continue;
            }

            // Parse: "declare <retType> @<name>(<params>)"
            int declareIdx = declLine.IndexOf(value: "declare ");
            if (declareIdx < 0)
            {
                continue;
            }

            string afterDeclare = declLine[(declareIdx + 8)..];
            int atIdx = afterDeclare.IndexOf(value: " @");
            if (atIdx < 0)
            {
                continue;
            }

            string retType = afterDeclare[..atIdx]
               .Trim();
            int openParen = afterDeclare.IndexOf(value: '(');
            int closeParen = afterDeclare.LastIndexOf(value: ')');
            if (openParen < 0 || closeParen < 0)
            {
                continue;
            }

            string paramList = afterDeclare[(openParen + 1)..closeParen]
               .Trim();

            // Build parameter names
            var paramNames = new List<string>();
            var paramTypes = new List<string>();
            if (!string.IsNullOrEmpty(value: paramList))
            {
                // Split param types (handles types like { i64, ptr } that contain commas)
                paramTypes = SplitLlvmParams(paramList: paramList);
                for (int i = 0; i < paramTypes.Count; i++)
                {
                    paramNames.Add(item: i == 0
                        ? "%self"
                        : $"%arg{i}");
                }
            }

            // Emit forwarding stub
            StringBuilder sb = _functionDefinitions;
            string paramDefs = string.Join(separator: ", ",
                values: paramTypes.Select(selector: (t, i) => $"{t} {paramNames[index: i]}"));
            EmitLine(sb: sb, line: $"define {retType} @{mangledName}({paramDefs}) {{");
            EmitLine(sb: sb, line: "entry:");

            string callArgs = string.Join(separator: ", ",
                values: paramTypes.Select(selector: (t, i) => $"{t} {paramNames[index: i]}"));

            if (retType == "void")
            {
                EmitLine(sb: sb, line: $"  call void @{concreteFunc}({callArgs})");
                EmitLine(sb: sb, line: "  ret void");
            }
            else
            {
                EmitLine(sb: sb, line: $"  %fwd = call {retType} @{concreteFunc}({callArgs})");
                EmitLine(sb: sb, line: $"  ret {retType} %fwd");
            }

            EmitLine(sb: sb, line: "}");
            EmitLine(sb: sb, line: "");

            _generatedFunctionDefs.Add(item: mangledName);
        }
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

        // Search all entity/record types (including resolutions) for implementers
        var seen = new HashSet<string>();
        foreach (TypeInfo type in _registry.GetTypesByCategory(category: TypeCategory.Entity)
                                           .Concat(second: _registry.GetTypesByCategory(
                                                category: TypeCategory.Record)))
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
                    Q(name: $"{concreteType.FullName}.{SanitizeLLVMName(name: methodName)}");
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
                            !_pendingMonomorphizations.ContainsKey(key: candidateName))
                        {
                            // Ensure entity type struct is defined for the concrete type
                            if (concreteType is EntityTypeInfo entityType)
                            {
                                GenerateEntityType(entity: entityType);
                            }

                            RecordMonomorphization(mangledName: candidateName,
                                genericMethod: genericMethod,
                                resolvedOwnerType: concreteType);
                            triggered = true;
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a declaration line for a given mangled function name.
    /// </summary>
    private string? FindDeclarationLine(string mangledName)
    {
        string searchTarget = $"@{mangledName}(";
        foreach (string line in _functionDeclarations.ToString()
                                                     .Split(separator: '\n'))
        {
            if (line.StartsWith(value: "declare ") && line.Contains(value: searchTarget))
            {
                return line;
            }
        }

        return null;
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


    /// <summary>
    /// Emits the body for a synthesized type_name() routine.
    /// Returns the type's name as a Text constant.
    /// </summary>
}
