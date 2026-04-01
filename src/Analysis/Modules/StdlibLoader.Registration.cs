using SemanticAnalysis;
using SemanticAnalysis.Types;
using SemanticAnalysis.Symbols;

namespace Builder.Modules;

using SemanticAnalysis.Enums;
using SyntaxTree;

public sealed partial class StdlibLoader
{
    private static void ResolveProtocolParents(TypeRegistry registry, Program program)
    {
        foreach (IAstNode node in program.Declarations)
        {
            if (node is ProtocolDeclaration protocol && protocol.ParentProtocols.Count > 0)
            {
                // Look up the registered protocol to get its FullName
                TypeInfo? registeredProto = registry.LookupType(name: protocol.Name);
                if (registeredProto is not ProtocolTypeInfo)
                {
                    continue;
                }

                var parentProtocols = new List<ProtocolTypeInfo>();
                foreach (TypeExpression parentExpr in protocol.ParentProtocols)
                {
                    TypeInfo? parentType =
                        ResolveSimpleType(registry: registry, typeExpr: parentExpr);
                    if (parentType is ProtocolTypeInfo parentProto)
                    {
                        parentProtocols.Add(item: parentProto);
                    }
                }

                if (parentProtocols.Count > 0)
                {
                    registry.UpdateProtocolParents(protocolName: registeredProto.FullName,
                        parentProtocols: parentProtocols);
                }
            }
        }
    }

    /// <summary>
    /// Registers protocol declarations from a program.
    /// This is pass 1a — protocols must be registered before other types so 'obeys' clauses can resolve.
    /// Uses two passes: first registers protocol type shells (names + generic params), then fills in
    /// method signatures. This ensures forward references between protocols resolve correctly
    /// (e.g., Iterable[T].$iter() → Iterator[T] where Iterator is another protocol).
    /// </summary>
    private static void RegisterProgramProtocols(TypeRegistry registry, Program program,
        string moduleName)
    {
        // Pass 1: Register protocol type shells (no methods yet)
        foreach (IAstNode node in program.Declarations)
        {
            if (node is ProtocolDeclaration protocol)
            {
                RegisterProtocolTypeShell(registry: registry,
                    protocol: protocol,
                    moduleName: moduleName);
            }
        }

        // Pass 2: Fill in method signatures (now all protocols are registered for cross-references)
        foreach (IAstNode node in program.Declarations)
        {
            if (node is ProtocolDeclaration protocol)
            {
                FillProtocolMethods(registry: registry, protocol: protocol);
            }
        }
    }

    /// <summary>
    /// Registers type declarations (record, entity, choice, variant, protocol) from a program.
    /// This is pass 1b of module-based loading. Protocols may already be registered from pass 1a.
    /// </summary>
    /// <param name="registry">The type registry to register types into.</param>
    /// <param name="program">The parsed program AST.</param>
    /// <param name="moduleName">The module for the types (from declaration or directory-derived).</param>
    private static void RegisterProgramTypes(TypeRegistry registry, Program program,
        string moduleName)
    {
        foreach (IAstNode node in program.Declarations)
        {
            switch (node)
            {
                case RecordDeclaration record:
                    RegisterRecordType(registry: registry, record: record, moduleName: moduleName);
                    break;
                case EntityDeclaration entity:
                    RegisterEntityType(registry: registry, entity: entity, moduleName: moduleName);
                    break;
                case ChoiceDeclaration choice:
                    RegisterChoiceType(registry: registry, choice: choice, moduleName: moduleName);
                    break;
                case FlagsDeclaration flags:
                    RegisterFlagsType(registry: registry, flags: flags, moduleName: moduleName);
                    break;
                case VariantDeclaration variant:
                    RegisterVariantType(registry: registry,
                        variant: variant,
                        moduleName: moduleName);
                    break;
                case ProtocolDeclaration protocol:
                    RegisterProtocolType(registry: registry,
                        protocol: protocol,
                        moduleName: moduleName);
                    break;
            }
        }
    }

    /// <summary>
    /// Re-resolves member variables for types that had unresolvable forward references
    /// during initial registration. Called after all type shells are registered.
    /// </summary>
    private static void ResolveProgramMemberVariables(TypeRegistry registry, Program program)
    {
        foreach (IAstNode node in program.Declarations)
        {
            switch (node)
            {
                case EntityDeclaration entity:
                {
                    var existing = registry.LookupType(name: entity.Name) as EntityTypeInfo;
                    int expectedCount = entity.Members.Count(predicate: m =>
                        m is VariableDeclaration { Type: not null });
                    if (existing == null || existing.MemberVariables.Count >= expectedCount)
                    {
                        continue;
                    }

                    List<MemberVariableInfo> members = ResolveMemberVariables(registry: registry,
                        members: entity.Members,
                        genericParams: entity.GenericParameters);
                    if (members.Count > existing.MemberVariables.Count)
                    {
                        existing.MemberVariables = members;
                    }

                    break;
                }
                case RecordDeclaration record:
                {
                    var existing = registry.LookupType(name: record.Name) as RecordTypeInfo;
                    int expectedCount = record.Members.Count(predicate: m =>
                        m is VariableDeclaration { Type: not null });
                    if (existing == null || existing.MemberVariables.Count >= expectedCount)
                    {
                        continue;
                    }

                    List<MemberVariableInfo> members = ResolveMemberVariables(registry: registry,
                        members: record.Members,
                        genericParams: record.GenericParameters);
                    if (members.Count > existing.MemberVariables.Count)
                    {
                        existing.MemberVariables = members;
                    }

                    break;
                }
            }
        }
    }

    /// <summary>
    /// Re-resolves protocol conformances for types whose protocol arguments contain
    /// forward-referenced types (e.g., EnumerateIterator[T] obeys Iterable[Tuple[S64, T]]
    /// where S64 wasn't registered during initial entity registration).
    /// Called after all type shells are registered.
    /// </summary>
    private static void ResolveProgramProtocolConformances(TypeRegistry registry, Program program)
    {
        foreach (IAstNode node in program.Declarations)
        {
            switch (node)
            {
                case EntityDeclaration entity when entity.Protocols.Count > 0:
                {
                    var existing = registry.LookupType(name: entity.Name) as EntityTypeInfo;
                    if (existing == null ||
                        existing.ImplementedProtocols.Count >= entity.Protocols.Count)
                    {
                        continue;
                    }

                    List<TypeInfo> protocols = ResolveProtocolList(registry: registry,
                        protoExprs: entity.Protocols,
                        genericParams: entity.GenericParameters);
                    if (protocols.Count > existing.ImplementedProtocols.Count)
                    {
                        existing.ImplementedProtocols = protocols;
                    }

                    break;
                }
                case RecordDeclaration record when record.Protocols.Count > 0:
                {
                    var existing = registry.LookupType(name: record.Name) as RecordTypeInfo;
                    if (existing == null ||
                        existing.ImplementedProtocols.Count >= record.Protocols.Count)
                    {
                        continue;
                    }

                    List<TypeInfo> protocols = ResolveProtocolList(registry: registry,
                        protoExprs: record.Protocols,
                        genericParams: record.GenericParameters);
                    if (protocols.Count > existing.ImplementedProtocols.Count)
                    {
                        existing.ImplementedProtocols = protocols;
                    }

                    break;
                }
            }
        }
    }

    /// <summary>
    /// Resolves a list of protocol type expressions into TypeInfo instances.
    /// </summary>
    private static List<TypeInfo> ResolveProtocolList(TypeRegistry registry,
        IReadOnlyList<TypeExpression> protoExprs, IReadOnlyList<string>? genericParams)
    {
        var result = new List<TypeInfo>();
        foreach (TypeExpression protoExpr in protoExprs)
        {
            TypeInfo? protoType = ResolveSimpleType(registry: registry,
                typeExpr: protoExpr,
                genericParams: genericParams);
            if (protoType != null)
            {
                result.Add(item: protoType);
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves member variable types from a list of member declarations.
    /// </summary>
    private static List<MemberVariableInfo> ResolveMemberVariables(TypeRegistry registry,
        IReadOnlyList<Declaration> members, IReadOnlyList<string>? genericParams)
    {
        var result = new List<MemberVariableInfo>();
        foreach (Declaration member in members)
        {
            if (member is VariableDeclaration { Type: not null } memberVariable)
            {
                TypeInfo? memberVariableType = ResolveSimpleType(registry: registry,
                    typeExpr: memberVariable.Type,
                    genericParams: genericParams);
                if (memberVariableType != null)
                {
                    result.Add(
                        item: new MemberVariableInfo(name: memberVariable.Name,
                            type: memberVariableType) { Visibility = memberVariable.Visibility });
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Registers routine declarations from a program.
    /// This is pass 2 of module-based loading - all types are already registered.
    /// </summary>
    private static void RegisterProgramRoutines(TypeRegistry registry, Program program,
        string moduleName)
    {
        foreach (IAstNode node in program.Declarations)
        {
            switch (node)
            {
                case RoutineDeclaration routine:
                    RegisterRoutine(registry: registry, routine: routine, moduleName: moduleName);
                    break;
                case ExternalDeclaration external:
                    RegisterExternalDeclaration(registry: registry,
                        external: external,
                        moduleName: moduleName);
                    break;
                case ExternalBlockDeclaration block:
                    foreach (Declaration decl in block.Declarations)
                    {
                        if (decl is ExternalDeclaration ext)
                        {
                            RegisterExternalDeclaration(registry: registry,
                                external: ext,
                                moduleName: moduleName);
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Registers an external("C") declaration from stdlib (e.g., NativeDeclarations.rf).
    /// </summary>
    private static void RegisterExternalDeclaration(TypeRegistry registry,
        ExternalDeclaration external, string moduleName)
    {
        // Build generic context for type resolution (e.g., T, To, From)
        IReadOnlyList<string>? genericCtx = external.GenericParameters is { Count: > 0 }
            ? external.GenericParameters
            : null;

        // Resolve parameter types
        var parameters = new List<ParameterInfo>();
        foreach (Parameter param in external.Parameters)
        {
            TypeInfo? paramType = ResolveSimpleType(registry: registry,
                typeExpr: param.Type,
                genericParams: genericCtx,
                moduleName: moduleName);
            parameters.Add(
                item: new ParameterInfo(name: param.Name,
                    type: paramType ?? ErrorTypeInfo.Instance)
                {
                    DefaultValue = param.DefaultValue, IsVariadicParam = param.IsVariadic
                });
        }

        // Resolve return type
        TypeInfo? returnType = external.ReturnType != null
            ? ResolveSimpleType(registry: registry,
                typeExpr: external.ReturnType,
                genericParams: genericCtx,
                moduleName: moduleName)
            : null;

        var routineInfo = new RoutineInfo(name: external.Name)
        {
            Kind = RoutineKind.External,
            CallingConvention = external.CallingConvention ?? "C",
            IsVariadic = external.IsVariadic,
            Parameters = parameters,
            ReturnType = returnType,
            Module = moduleName,
            Location = external.Location,
            IsDangerous = external.IsDangerous,
            GenericParameters = external.GenericParameters,
            Annotations = external.Annotations ?? []
        };

        try
        {
            registry.RegisterRoutine(routine: routineInfo);
        }
        catch
        {
            // Ignore duplicate routine registration
        }
    }

    /// <summary>
    /// Registers preset (build-time constant) declarations from a program.
    /// Presets are module-level constants accessible across files within the same module.
    /// </summary>
    private static void RegisterProgramPresets(TypeRegistry registry, Program program,
        string moduleName)
    {
        foreach (IAstNode node in program.Declarations)
        {
            if (node is PresetDeclaration preset)
            {
                TypeInfo? presetType =
                    ResolveSimpleType(registry: registry, typeExpr: preset.Type);
                if (presetType != null)
                {
                    registry.RegisterPreset(name: preset.Name,
                        type: presetType,
                        module: moduleName);
                }
            }
        }
    }

    /// <summary>
    /// Registers a routine from stdlib (including type methods like S32.$add).
    /// </summary>
    private static void RegisterRoutine(TypeRegistry registry, RoutineDeclaration routine,
        string moduleName)
    {
        // Parse method names like "S32.$add" or "Type.method"
        string routineName = routine.Name;
        TypeInfo? ownerType = null;
        string methodName = routineName;

        int dotIndex = routineName.IndexOf(value: '.');
        if (dotIndex > 0)
        {
            string typeName = routineName[..dotIndex];
            methodName = routineName[(dotIndex + 1)..]; // Just the method part (e.g., "$add")

            int bracketIndex = typeName.IndexOf(value: '[');
            if (bracketIndex > 0)
            {
                // Check if the bracket content is concrete types (e.g., List[Byte])
                // vs generic params (e.g., List[T], Dict[K, V])
                string bracketContent = typeName[(bracketIndex + 1)..]
                   .TrimEnd(trimChar: ']');
                string baseName = typeName[..bracketIndex];
                TypeInfo? baseDef = registry.LookupType(name: baseName) ??
                                    registry.LookupType(name: $"{moduleName}.{baseName}");

                // If the base is a generic definition, check if bracket args are its own params
                bool isGenericDef = false;
                if (baseDef?.GenericParameters != null)
                {
                    var args = bracketContent.Split(separator: ',')
                                             .Select(selector: a => a.Trim())
                                             .ToList();
                    isGenericDef =
                        args.All(predicate: a => baseDef.GenericParameters.Contains(value: a));
                }

                if (isGenericDef)
                {
                    // Generic definition: List[T] → owner is List
                    ownerType = baseDef;
                }
                else
                {
                    // Concrete specialization: List[Byte] → owner is List[Byte]
                    ownerType = registry.LookupType(name: typeName) ?? baseDef;
                }
            }
            else
            {
                ownerType = registry.LookupType(name: typeName) ??
                            registry.LookupType(name: $"{moduleName}.{typeName}");

                // If type not found, treat as a generic type parameter (e.g., T in "routine T.view()")
                if (ownerType == null)
                {
                    ownerType = new GenericParameterTypeInfo(name: typeName);
                }
            }
        }

        // Collect generic params from owner type + routine itself for type resolution context
        var genericContext = new List<string>();
        // If owner is a generic parameter itself (e.g., T in "routine T.view()"),
        // add it to the generic context so return/param types can reference it
        if (ownerType is GenericParameterTypeInfo genParam)
        {
            genericContext.Add(item: genParam.Name);
        }

        if (ownerType?.GenericParameters != null)
        {
            genericContext.AddRange(collection: ownerType.GenericParameters);
        }

        if (routine.GenericParameters != null)
        {
            genericContext.AddRange(collection: routine.GenericParameters);
        }

        IReadOnlyList<string>? ctx = genericContext.Count > 0
            ? genericContext
            : null;

        // Resolve parameter types
        var parameters = new List<ParameterInfo>();
        foreach (Parameter param in routine.Parameters)
        {
            TypeInfo? paramType = ResolveSimpleType(registry: registry,
                typeExpr: param.Type,
                genericParams: ctx,
                moduleName: moduleName);

            // Wrap variadic params as List[T] (mirrors SA Phase 2 wrapping)
            if (param.IsVariadic && paramType != null)
            {
                TypeInfo? listDef = registry.LookupType(name: "List");
                if (listDef != null)
                {
                    paramType = registry.GetOrCreateResolution(genericDef: listDef,
                        typeArguments: [paramType]);
                }
            }

            parameters.Add(
                item: new ParameterInfo(name: param.Name,
                    type: paramType ?? ErrorTypeInfo.Instance)
                {
                    DefaultValue = param.DefaultValue, IsVariadicParam = param.IsVariadic
                });
        }

        // Resolve return type
        TypeInfo? returnType = routine.ReturnType != null
            ? ResolveSimpleType(registry: registry,
                typeExpr: routine.ReturnType,
                genericParams: ctx,
                moduleName: moduleName)
            : null;

        // Use just the method name (not "S32.$add", just "$add")
        var routineInfo = new RoutineInfo(name: methodName)
        {
            OwnerType = ownerType,
            Parameters = parameters,
            ReturnType = returnType,
            Module = moduleName,
            Location = routine.Location,
            IsFailable = routine.IsFailable,
            IsVariadic = routine.Parameters.Any(predicate: p => p.IsVariadic),
            GenericParameters = routine.GenericParameters,
            AsyncStatus = routine.Async,
            Annotations = routine.Annotations,
            IsDangerous = routine.IsDangerous,
            Storage = routine.Storage
        };

        try
        {
            registry.RegisterRoutine(routine: routineInfo);
        }
        catch
        {
            // Ignore duplicate routine registration
        }
    }

    /// <summary>
    /// Registers a record type from stdlib.
    /// </summary>
    private static void RegisterRecordType(TypeRegistry registry, RecordDeclaration record,
        string moduleName)
    {
        // Skip if already registered
        if (registry.LookupType(name: record.Name) != null)
        {
            return;
        }

        // Build member variables list upfront (TypeInfo uses init properties with IReadOnlyList)
        var memberVariables = new List<MemberVariableInfo>();
        foreach (Declaration member in record.Members)
        {
            if (member is VariableDeclaration { Type: not null } memberVariable)
            {
                TypeInfo? memberVariableType = ResolveSimpleType(registry: registry,
                    typeExpr: memberVariable.Type,
                    genericParams: record.GenericParameters,
                    moduleName: moduleName);
                if (memberVariableType != null)
                {
                    memberVariables.Add(
                        item: new MemberVariableInfo(name: memberVariable.Name,
                            type: memberVariableType) { Visibility = memberVariable.Visibility });
                }
            }
        }

        // Resolve implemented protocols (obeys clause)
        var protocols = new List<TypeInfo>();
        foreach (TypeExpression protoExpr in record.Protocols)
        {
            TypeInfo? protoType = ResolveSimpleType(registry: registry,
                typeExpr: protoExpr,
                genericParams: record.GenericParameters,
                moduleName: moduleName);
            if (protoType != null)
            {
                protocols.Add(item: protoType);
            }
        }

        var typeInfo = new RecordTypeInfo(name: record.Name)
        {
            Module = moduleName,
            Visibility = record.Visibility,
            MemberVariables = memberVariables,
            ImplementedProtocols = protocols,
            GenericParameters = record.GenericParameters,
            BackendType = ExtractLlvmAnnotation(annotations: record.Annotations)
        };

        try
        {
            registry.RegisterType(type: typeInfo);
        }
        catch
        {
            // Ignore duplicate type registration
        }
    }

    /// <summary>
    /// Registers an entity type from stdlib.
    /// </summary>
    private static void RegisterEntityType(TypeRegistry registry, EntityDeclaration entity,
        string moduleName)
    {
        // Skip if already registered
        if (registry.LookupType(name: entity.Name) != null)
        {
            return;
        }

        // Build member variables list upfront
        var memberVariables = new List<MemberVariableInfo>();
        foreach (Declaration member in entity.Members)
        {
            if (member is VariableDeclaration { Type: not null } memberVariable)
            {
                TypeInfo? memberVariableType = ResolveSimpleType(registry: registry,
                    typeExpr: memberVariable.Type,
                    genericParams: entity.GenericParameters,
                    moduleName: moduleName);
                if (memberVariableType != null)
                {
                    memberVariables.Add(
                        item: new MemberVariableInfo(name: memberVariable.Name,
                            type: memberVariableType) { Visibility = memberVariable.Visibility });
                }
            }
        }

        // Resolve implemented protocols (obeys clause)
        var protocols = new List<TypeInfo>();
        foreach (TypeExpression protoExpr in entity.Protocols)
        {
            TypeInfo? protoType = ResolveSimpleType(registry: registry,
                typeExpr: protoExpr,
                genericParams: entity.GenericParameters,
                moduleName: moduleName);
            if (protoType != null)
            {
                protocols.Add(item: protoType);
            }
        }

        var typeInfo = new EntityTypeInfo(name: entity.Name)
        {
            Module = moduleName,
            Visibility = entity.Visibility,
            MemberVariables = memberVariables,
            ImplementedProtocols = protocols,
            GenericParameters = entity.GenericParameters
        };

        registry.RegisterType(type: typeInfo);
    }

    /// <summary>
    /// Registers a choice type from stdlib.
    /// </summary>
    private static void RegisterChoiceType(TypeRegistry registry, ChoiceDeclaration choice,
        string moduleName)
    {
        // Skip if already registered
        if (registry.LookupType(name: choice.Name) != null)
        {
            return;
        }

        // Build cases list upfront
        var cases = new List<ChoiceCaseInfo>();
        int autoValue = 0;
        foreach (ChoiceCase caseDecl in choice.Cases)
        {
            int? explicitValue = null;
            if (caseDecl.Value is LiteralExpression { Value: string valStr })
            {
                if (int.TryParse(s: valStr, result: out int v))
                {
                    explicitValue = v;
                }
            }
            else if (caseDecl.Value is UnaryExpression
                     {
                         Operator: UnaryOperator.Minus,
                         Operand: LiteralExpression { Value: string negStr }
                     })
            {
                if (int.TryParse(s: negStr, result: out int v))
                {
                    explicitValue = -v;
                }
            }

            int computedValue;
            if (explicitValue.HasValue)
            {
                computedValue = explicitValue.Value;
                autoValue = computedValue + 1;
            }
            else
            {
                computedValue = autoValue;
                autoValue++;
            }

            cases.Add(item: new ChoiceCaseInfo(name: caseDecl.Name)
            {
                Value = explicitValue, ComputedValue = computedValue
            });
        }

        var typeInfo = new ChoiceTypeInfo(name: choice.Name)
        {
            Module = moduleName, Visibility = choice.Visibility, Cases = cases
        };

        registry.RegisterType(type: typeInfo);
    }

    /// <summary>
    /// Registers a flags type from stdlib.
    /// </summary>
    private static void RegisterFlagsType(TypeRegistry registry, FlagsDeclaration flags,
        string moduleName)
    {
        if (registry.LookupType(name: flags.Name) != null)
        {
            return;
        }

        var members = new List<FlagsMemberInfo>();
        for (int i = 0; i < flags.Members.Count; i++)
        {
            members.Add(item: new FlagsMemberInfo(Name: flags.Members[index: i], BitPosition: i));
        }

        var typeInfo = new FlagsTypeInfo(name: flags.Name)
        {
            Module = moduleName, Visibility = flags.Visibility, Members = members
        };

        registry.RegisterType(type: typeInfo);
    }

    /// <summary>
    /// Registers a variant type (type-based tagged union) from stdlib.
    /// </summary>
    private static void RegisterVariantType(TypeRegistry registry, VariantDeclaration variant,
        string moduleName)
    {
        // Skip if already registered
        if (registry.LookupType(name: variant.Name) != null)
        {
            return;
        }

        // Build members list: None = tag 0, others sequential from 1
        var members = new List<VariantMemberInfo>();
        bool hasNone = false;
        int tag = 0;

        // First pass: find None
        foreach (VariantMember memberDecl in variant.Members)
        {
            if (memberDecl.Type.Name == "None")
            {
                hasNone = true;
                members.Add(item: VariantMemberInfo.CreateNone(tagValue: 0, location: null));
                tag = 1;
                break;
            }
        }

        // Second pass: all non-None members
        foreach (VariantMember memberDecl in variant.Members)
        {
            if (memberDecl.Type.Name == "None")
            {
                continue;
            }

            TypeInfo? memberType =
                ResolveSimpleType(registry: registry, typeExpr: memberDecl.Type);
            if (memberType != null)
            {
                members.Add(item: new VariantMemberInfo(type: memberType) { TagValue = tag++ });
            }
        }

        var typeInfo = new VariantTypeInfo(name: variant.Name)
        {
            Module = moduleName,
            Members = members,
            GenericParameters = variant.GenericParameters
        };

        registry.RegisterType(type: typeInfo);
    }

    /// <summary>
    /// Registers a protocol type from stdlib (single-pass: registers type and methods together).
    /// Used by RegisterProgramTypes (pass 1b) for protocols encountered outside the two-pass path.
    /// </summary>
    private static void RegisterProtocolType(TypeRegistry registry, ProtocolDeclaration protocol,
        string moduleName)
    {
        RegisterProtocolTypeShell(registry: registry, protocol: protocol, moduleName: moduleName);
        FillProtocolMethods(registry: registry, protocol: protocol);
    }

    /// <summary>
    /// Registers a protocol type shell (name, generic params) without method signatures.
    /// This is the first pass of protocol registration — ensures all protocol types exist
    /// before method signatures are resolved (which may reference other protocols).
    /// </summary>
    private static void RegisterProtocolTypeShell(TypeRegistry registry,
        ProtocolDeclaration protocol, string moduleName)
    {
        // Skip if already registered
        if (registry.LookupType(name: protocol.Name) != null)
        {
            return;
        }

        var typeInfo = new ProtocolTypeInfo(name: protocol.Name)
        {
            Module = moduleName,
            Visibility = protocol.Visibility,
            Methods = [], // Filled in by FillProtocolMethods
            GenericParameters = protocol.GenericParameters
        };

        registry.RegisterType(type: typeInfo);
    }

    /// <summary>
    /// Fills in method signatures for a previously registered protocol type.
    /// This is the second pass — all protocols are registered, so cross-references resolve.
    /// </summary>
    private static void FillProtocolMethods(TypeRegistry registry, ProtocolDeclaration protocol)
    {
        var existing = registry.LookupType(name: protocol.Name) as ProtocolTypeInfo;
        if (existing == null || existing.Methods.Count > 0)
        {
            return; // Already has methods or not found
        }

        var methods = new List<ProtocolMethodInfo>();
        foreach (RoutineSignature method in protocol.Methods)
        {
            string rawName = method.Name;
            bool isFailable = rawName.EndsWith(value: '!');
            string fullName = isFailable
                ? rawName[..^1]
                : rawName;
            bool isInstance = fullName.StartsWith(value: "Me.");
            string methodName = isInstance
                ? fullName[3..]
                : fullName;

            TypeInfo? returnType = method.ReturnType != null
                ? ResolveSimpleType(registry: registry,
                    typeExpr: method.ReturnType,
                    genericParams: protocol.GenericParameters)
                : null;

            var parameterTypes = new List<TypeInfo>();
            var parameterNames = new List<string>();

            foreach (Parameter param in method.Parameters)
            {
                if (param.Name == "me")
                {
                    continue;
                }

                TypeInfo? paramType = param.Type?.Name == "Me"
                    ? ProtocolSelfTypeInfo.Instance
                    : ResolveSimpleType(registry: registry,
                        typeExpr: param.Type,
                        genericParams: protocol.GenericParameters);
                if (paramType != null)
                {
                    parameterTypes.Add(item: paramType);
                    parameterNames.Add(item: param.Name);
                }
            }

            TypeInfo? resolvedReturnType = method.ReturnType?.Name == "Me"
                ? ProtocolSelfTypeInfo.Instance
                : returnType;

            methods.Add(item: new ProtocolMethodInfo(name: methodName)
            {
                IsInstanceMethod = isInstance,
                ParameterTypes = parameterTypes,
                ParameterNames = parameterNames,
                ReturnType = resolvedReturnType,
                IsFailable = isFailable
            });
        }

        existing.Methods = methods;
    }
}
