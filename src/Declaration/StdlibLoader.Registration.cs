using System;
using System.Collections.Generic;
using System.Linq;
using Compiler.Resolution;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Declaration;

public sealed partial class StdlibLoader
{
    private static void ResolveProtocolParents(TypeRegistry registry, Program program) // NOSONAR S3776
    {
        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            if (node is ProtocolDeclaration { ParentProtocols.Count: > 0 } protocol)
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
    /// (e.g., Iterable[T].$iter() -> Iterator[T] where Iterator is another protocol).
    /// </summary>
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
        foreach (ISyntaxTreeNode node in program.Declarations)
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
                case CrashableDeclaration crashable:
                    RegisterCrashableType(registry: registry,
                        crashable: crashable,
                        moduleName: moduleName);
                    break;
            }
        }
    }

    /// <summary>
    /// Re-resolves member variables for types that had unresolvable forward references
    /// during initial registration. Called after all type shells are registered.
    /// </summary>
    private static void ResolveProgramMemberVariables(TypeRegistry registry, Program program) // NOSONAR S3776
    {
        foreach (ISyntaxTreeNode node in program.Declarations)
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
                        genericParams: entity.GenericParameters,
                        owner: existing,
                        moduleName: existing.Module);
                    if (members.Count > existing.MemberVariables.Count)
                    {
                        existing.MemberVariables = members;
                        registry.RefreshEntityResolutions(genericDef: existing);
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
                        genericParams: record.GenericParameters,
                        owner: existing,
                        moduleName: existing.Module);
                    if (members.Count > existing.MemberVariables.Count)
                    {
                        existing.MemberVariables = members;
                    }

                    break;
                }
                case CrashableDeclaration crashable:
                {
                    var existing =
                        registry.LookupType(name: crashable.Name) as CrashableTypeInfo;
                    int expectedCount = crashable.Members.Count(predicate: m =>
                        m is VariableDeclaration { Type: not null });
                    if (existing == null || existing.MemberVariables.Count >= expectedCount)
                    {
                        continue;
                    }

                    List<MemberVariableInfo> members = ResolveMemberVariables(registry: registry,
                        members: crashable.Members,
                        genericParams: null,
                        owner: existing,
                        moduleName: existing.Module);
                    if (members.Count > existing.MemberVariables.Count)
                    {
                        registry.UpdateCrashableMemberVariables(typeName: existing.FullName,
                            memberVariables: members);
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
    private static void ResolveProgramProtocolConformances(TypeRegistry registry, Program program) // NOSONAR S3776
    {
        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            switch (node)
            {
                case EntityDeclaration { Protocols.Count: > 0 } entity:
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
                case RecordDeclaration { Protocols.Count: > 0 } record:
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
        List<TypeExpression> protoExprs, List<string>? genericParams)
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
        List<SyntaxTree.Declaration> members, List<string>? genericParams,
        TypeInfo? owner = null, string? moduleName = null)
    {
        var result = new List<MemberVariableInfo>();
        int index = 0;
        foreach (SyntaxTree.Declaration member in members)
        {
            if (member is VariableDeclaration { Type: not null } memberVariable)
            {
                TypeInfo? memberVariableType = ResolveSimpleType(registry: registry,
                    typeExpr: memberVariable.Type,
                    genericParams: genericParams,
                    moduleName: moduleName);
                if (memberVariableType != null)
                {
                    result.Add(
                        item: new MemberVariableInfo(name: memberVariable.Name,
                            type: memberVariableType)
                        {
                            Visibility = memberVariable.Visibility,
                            Index = index,
                            Owner = owner
                        });
                    index++;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Registers routine declarations from a program.
    /// This is pass 2 of module-based loading - all types are already registered.
    /// </summary>
    /// <summary>
    /// BuilderService per-type routines whose stdlib decls return
    /// <c>List[Owned[FieldInfo|ProtocolInfo|RoutineInfo]]</c> or <c>Dict[Text, Data]</c>.
    /// Registering these as universal <c>T.x()</c> routines from BuilderService.rf forces GMP to
    /// monomorphize the heavy carrier closure (BTreeListNode/Owned/Array/ArrayIterator) for every
    /// type even when the user program never imports BuilderService.
    /// AutoWiredRegistrationPass re-registers these per-type when (and only when) the user actually
    /// imports BuilderService, so skipping the stdlib decls here is safe.
    /// </summary>
    private static readonly HashSet<string> BuilderServiceClosureCascadingRoutines =
        new(comparer: StringComparer.Ordinal)
        {
            "protocol_info",
            "routine_info",
            "member_variable_info"
        };

    private static bool ShouldSkipBuilderServiceRoutineDecl(RoutineDeclaration routine,
        string moduleName)
    {
        if (!moduleName.Equals(value: "BuilderService", comparisonType: StringComparison.Ordinal))
        {
            return false;
        }

        string name = routine.Name;
        int dot = name.IndexOf(value: '.');
        string method = dot > 0 ? name[(dot + 1)..] : name;
        return BuilderServiceClosureCascadingRoutines.Contains(item: method);
    }

    private static void RegisterProgramRoutines(TypeRegistry registry, Program program,
        string moduleName) // NOSONAR S3776
    {
        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            switch (node)
            {
                case RoutineDeclaration routine:
                    if (ShouldSkipBuilderServiceRoutineDecl(routine: routine,
                            moduleName: moduleName))
                    {
                        break;
                    }
                    RegisterRoutine(registry: registry, routine: routine, moduleName: moduleName);
                    break;
                case ExternalDeclaration external:
                    RegisterExternalDeclaration(registry: registry,
                        external: external,
                        moduleName: moduleName);
                    break;
                case ExternalBlockDeclaration block:
                    foreach (SyntaxTree.Declaration decl in block.Declarations)
                    {
                        if (decl is ExternalDeclaration ext)
                        {
                            RegisterExternalDeclaration(registry: registry,
                                external: ext,
                                moduleName: moduleName);
                        }
                    }

                    break;

                case CrashableDeclaration crashable:
                    // Register routine members (e.g., crash_message synthesized from message: directive)
                    foreach (SyntaxTree.Declaration member in crashable.Members)
                    {
                        if (member is RoutineDeclaration memberRoutine)
                        {
                            // Prefix the method name with the type name so RegisterRoutine
                            // treats it as a member method (e.g., "DivisionByZeroError.crash_message")
                            var prefixed = memberRoutine with
                            {
                                Name = $"{crashable.Name}.{memberRoutine.Name}"
                            };
                            RegisterRoutine(registry: registry,
                                routine: prefixed,
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
        List<string>? genericCtx = external.GenericParameters is { Count: > 0 }
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
            ModulePath = moduleName?.Split('/').ToList(),
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
        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            if (node is PresetDeclaration preset)
            {
                TypeInfo? presetType =
                    ResolveSimpleType(registry: registry, typeExpr: preset.Type);
                if (presetType != null)
                {
                    SeedPresetValueMetadata(value: preset.Value, presetType: presetType);
                    registry.RegisterPreset(name: preset.Name,
                        type: presetType,
                        module: moduleName,
                        value: preset.Value);
                }
            }
        }
    }

    private static void SeedPresetValueMetadata(Expression value, TypeInfo presetType)
    {
        value.ResolvedType ??= presetType;

        if (value is not CallExpression call ||
            call.Callee is not IdentifierExpression identifier ||
            call.LoweringKind != CallLoweringKind.Unknown)
        {
            return;
        }

        bool matchesPresetType = identifier.Name == presetType.Name ||
                                 identifier.Name == presetType.FullName ||
                                 presetType.FullName.EndsWith(value: "." + identifier.Name,
                                     comparisonType: StringComparison.Ordinal);
        if (!matchesPresetType)
        {
            return;
        }

        call.ResolvedType ??= presetType;
        call.ConstructedType ??= presetType;

        call.LoweringKind = presetType switch
        {
            RecordTypeInfo { HasDirectBackendType: true } => CallLoweringKind.TypeConstructor,
            _ => call.LoweringKind
        };
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
                    // Generic definition: List[T] -> owner is List
                    ownerType = baseDef;
                }
                else
                {
                    // Concrete specialization: List[Byte] -> owner is List[Byte]
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
            // Filter out names that resolve to real registered types. The parser collects bracket
            // contents from owner receivers like `Iterable[Text]` and stuffs them into
            // routine.GenericParameters, but concrete args (Text) must not shadow the real type
            // — otherwise `separator: Text` resolves to GenericParameterTypeInfo("Text")
            // instead of Core.Text, breaking method lookup in the body.
            foreach (string gp in routine.GenericParameters)
            {
                if (registry.LookupType(name: gp) is null &&
                    registry.LookupType(name: $"{moduleName}.{gp}") is null)
                {
                    genericContext.Add(item: gp);
                }
            }
        }

        List<string>? ctx = genericContext.Count > 0
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
            ModulePath = moduleName?.Split('/').ToList(),
            Location = routine.Location,
            IsFailable = routine.IsFailable,
            IsVariadic = routine.Parameters.Any(predicate: p => p.IsVariadic),
            GenericParameters = routine.GenericParameters,
            GenericConstraints = routine.GenericConstraints,
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
        // Detect entity-type specializations of constrained generics
        // (e.g. `record Maybe[T] needs T is EntityType`).
        // In stdlib .rf files, `needs T is EntityType` is parsed as a ConstGeneric constraint
        // with ConstraintTypes[0].Name == "EntityType". These create a second layout specialization
        // (e.g. Maybe[Text] uses { Hijacked[T] } instead of { Bool, T }) and must be stored
        // separately so GetOrCreateResolution can select the right definition.
        string? entityConstraintParam = record.GenericConstraints?
            .Where(predicate: c =>
                c is { ConstraintType: ConstraintKind.ConstGeneric, ConstraintTypes: [{ Name: "EntityType" }] })
            .Select(selector: c => c.ParameterName)
            .FirstOrDefault();
        bool isEntitySpecialization = entityConstraintParam != null;

        // Transparent pointer wrappers (e.g. T) carry `needs T is EntityType` as a
        // contract annotation but have a single fixed LLVM layout (ptr) for all type arguments.
        // They are wrapper types — NOT entity specializations — so register them normally.
        if (isEntitySpecialization &&
            ExtractLlvmAnnotation(annotations: record.Annotations) == "ptr" &&
            !record.Members.Any(predicate: m => m is VariableDeclaration { Type: not null }))
        {
            isEntitySpecialization = false;
        }

        // Records whose member variables are all known @llvm("ptr") wrapper types (e.g.
        // Retained[T] with two Hijacked fields) have a fixed struct layout regardless of T.
        // They are NOT entity specializations — register them normally.
        if (isEntitySpecialization &&
            record.Members.Any(predicate: m => m is VariableDeclaration { Type: not null }))
        {
            bool allMembersPtrWrapper = record.Members
                .OfType<VariableDeclaration>()
                .Where(predicate: m => m.Type != null)
                .All(predicate: m =>
                {
                    string baseName = m.Type!.Name.Contains('[')
                        ? m.Type.Name[..m.Type.Name.IndexOf('[')]
                        : m.Type.Name;
                    return baseName is "Hijacked" or "Viewing" or "Modifying"
                        or "Retained" or "Tracked";
                });
            if (allMembersPtrWrapper)
            {
                isEntitySpecialization = false;
            }
        }

        // Skip if already registered (non-entity-specialization types only;
        // entity specializations need separate registration even if the base name exists)
        if (!isEntitySpecialization && registry.LookupType(name: record.Name) != null)
        {
            return;
        }

        // Build member variables list upfront (TypeInfo uses init properties with IReadOnlyList)
        var memberVariables = new List<MemberVariableInfo>();
        foreach (SyntaxTree.Declaration member in record.Members)
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
                            type: memberVariableType)
                        {
                            Visibility = memberVariable.Visibility,
                            HasDefaultValue = memberVariable.Initializer != null,
                            Location = memberVariable.Location
                        });
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

        // Inherit CarrierKind from the pre-registered generic definition shell when building
        // entity-type specializations (e.g. Maybe[T] needs T is EntityType).
        CarrierKind inheritedCarrierKind = CarrierKind.None;
        if (isEntitySpecialization &&
            registry.LookupType(name: record.Name) is RecordTypeInfo { CarrierKind: var baseKind })
        {
            inheritedCarrierKind = baseKind;
        }

        var typeInfo = new RecordTypeInfo(name: record.Name)
        {
            Module = moduleName,
            Visibility = record.Visibility,
            ImplementedProtocols = protocols,
            GenericParameters = record.GenericParameters,
            GenericConstraints = record.GenericConstraints,
            BackendType = ExtractLlvmAnnotation(annotations: record.Annotations),
            CarrierKind = inheritedCarrierKind
        };

        // Back-fill Owner + Index now that typeInfo exists (Owner is needed for module access checks)
        typeInfo.MemberVariables = memberVariables
                                   .Select(selector: (mv, i) =>
                                        new MemberVariableInfo(name: mv.Name, type: mv.Type)
                                        {
                                            Visibility = mv.Visibility,
                                            Index = i,
                                            HasDefaultValue = mv.HasDefaultValue,
                                            Location = mv.Location,
                                            Owner = typeInfo
                                        })
                                   .ToList();

        RegisterAssociatedTypeBindings(registry: registry,
            declared: record.AssociatedTypes,
            genericParams: record.GenericParameters,
            moduleName: moduleName,
            bindings: typeInfo.AssociatedTypeBindings);

        if (isEntitySpecialization)
        {
            // This is the entity-type specialization of a constrained generic
            // (e.g. Maybe[T] needs T is EntityType -> { Hijacked[T] } layout).
            // Register it so GetOrCreateResolution can select it for entity type arguments.
            registry.RegisterEntitySpecialization(type: typeInfo);
        }
        else
        {
            try
            {
                registry.RegisterType(type: typeInfo);
            }
            catch
            {
                // Ignore duplicate type registration
            }
        }
    }

    /// <summary>
    /// Registers a crashable type from stdlib.
    /// Crashable types are heap-allocated error types that implement the Crashable protocol.
    /// </summary>
    private static void RegisterCrashableType(TypeRegistry registry, CrashableDeclaration crashable,
        string moduleName)
    {
        // Skip if already registered
        if (registry.LookupType(name: crashable.Name) != null)
        {
            return;
        }

        // Resolve member variables (e.g., KeyNotFoundError.key: Text)
        var memberVariables = new List<MemberVariableInfo>();
        foreach (SyntaxTree.Declaration member in crashable.Members)
        {
            if (member is VariableDeclaration { Type: not null } field)
            {
                TypeInfo? memberType = ResolveSimpleType(registry: registry,
                    typeExpr: field.Type,
                    genericParams: null,
                    moduleName: moduleName);
                if (memberType != null)
                {
                    memberVariables.Add(
                        item: new MemberVariableInfo(name: field.Name, type: memberType)
                        {
                            Visibility = field.Visibility,
                            HasDefaultValue = field.Initializer != null,
                            Location = field.Location
                        });
                }
            }
        }

        var typeInfo = new CrashableTypeInfo(name: crashable.Name)
        {
            Module = moduleName,
            Visibility = crashable.Visibility,
            Location = crashable.Location
        };

        // Back-fill Owner + Index now that typeInfo exists (Owner is needed for module access checks)
        typeInfo.MemberVariables = memberVariables
                                   .Select(selector: (mv, i) =>
                                        new MemberVariableInfo(name: mv.Name, type: mv.Type)
                                        {
                                            Visibility = mv.Visibility,
                                            Index = i,
                                            HasDefaultValue = mv.HasDefaultValue,
                                            Location = mv.Location,
                                            Owner = typeInfo
                                        })
                                   .ToList();

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
        foreach (SyntaxTree.Declaration member in entity.Members)
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
                            type: memberVariableType)
                        {
                            Visibility = memberVariable.Visibility,
                            HasDefaultValue = memberVariable.Initializer != null,
                            Location = memberVariable.Location
                        });
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
            ImplementedProtocols = protocols,
            GenericParameters = entity.GenericParameters,
            GenericConstraints = entity.GenericConstraints
        };

        // Back-fill Owner + Index now that typeInfo exists (Owner is needed for module access checks)
        typeInfo.MemberVariables = memberVariables
                                   .Select(selector: (mv, i) =>
                                        new MemberVariableInfo(name: mv.Name, type: mv.Type)
                                        {
                                            Visibility = mv.Visibility,
                                            Index = i,
                                            HasDefaultValue = mv.HasDefaultValue,
                                            Location = mv.Location,
                                            Owner = typeInfo
                                        })
                                   .ToList();

        RegisterAssociatedTypeBindings(registry: registry,
            declared: entity.AssociatedTypes,
            genericParams: entity.GenericParameters,
            moduleName: moduleName,
            bindings: typeInfo.AssociatedTypeBindings);

        registry.RegisterType(type: typeInfo);
    }

    /// <summary>
    /// Post-registration pass: (re)resolves associated-type bindings (<c>relates Concrete as Name</c>)
    /// for all entity/record types now that every type — including iterator/emitter types defined
    /// later in their file — is registered. The inline resolution during initial registration can
    /// miss forward references (e.g. <c>List</c> binds <c>ListEmitter[T]</c> but <c>ListEmitter</c>
    /// is declared further down the file), leaving the binding empty; this fills them in.
    /// </summary>
    private static void ResolveAssociatedTypeBindings(TypeRegistry registry, Program program)
    {
        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            switch (node)
            {
                case EntityDeclaration { AssociatedTypes: { Count: > 0 } at } ed
                    when registry.LookupType(name: ed.Name) is EntityTypeInfo ent:
                    RegisterAssociatedTypeBindings(registry: registry, declared: at,
                        genericParams: ed.GenericParameters, moduleName: ent.Module ?? "",
                        bindings: ent.AssociatedTypeBindings);
                    break;
                case RecordDeclaration { AssociatedTypes: { Count: > 0 } at } rd
                    when registry.LookupType(name: rd.Name) is RecordTypeInfo rec:
                    RegisterAssociatedTypeBindings(registry: registry, declared: at,
                        genericParams: rd.GenericParameters, moduleName: rec.Module ?? "",
                        bindings: rec.AssociatedTypeBindings);
                    break;
            }
        }
    }

    /// <summary>
    /// Resolves <c>relates Concrete as Name</c> bindings from a declaration's AST and populates the
    /// target type's binding map (slot name → concrete <see cref="TypeInfo"/>). Shared by entity
    /// and record registration.
    /// </summary>
    private static void RegisterAssociatedTypeBindings(TypeRegistry registry,
        List<AssociatedTypeDeclaration>? declared, List<string>? genericParams, string moduleName,
        Dictionary<string, TypeInfo> bindings)
    {
        if (declared is not { Count: > 0 })
        {
            return;
        }

        foreach (AssociatedTypeDeclaration binding in declared)
        {
            if (binding.Binding == null)
            {
                continue;
            }

            TypeInfo? concrete = ResolveSimpleType(registry: registry,
                typeExpr: binding.Binding,
                genericParams: genericParams,
                moduleName: moduleName);
            if (concrete != null)
            {
                bindings[key: binding.Name] = concrete;
            }
        }
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
                     } &&
                     int.TryParse(s: negStr, result: out int v))
            {
                explicitValue = -v;
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
        int tag = 0;

        // First pass: find None
        foreach (VariantMember memberDecl in variant.Members)
        {
            if (memberDecl.Type.Name == "None")
            {
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
            GenericParameters = variant.GenericParameters,
            GenericConstraints = variant.GenericConstraints
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
            GenericParameters = protocol.GenericParameters,
            GenericConstraints = protocol.GenericConstraints
        };

        // Associated-type slots declared via `relates Iter obeys Iterator[T]`.
        if (protocol.AssociatedTypes is { Count: > 0 } slots)
        {
            foreach (AssociatedTypeDeclaration slot in slots)
            {
                TypeInfo? constraint = slot.Constraint != null
                    ? ResolveSimpleType(registry: registry,
                        typeExpr: slot.Constraint,
                        genericParams: protocol.GenericParameters,
                        moduleName: moduleName)
                    : null;
                typeInfo.AssociatedTypes.Add(item: new AssociatedTypeSlot(name: slot.Name)
                {
                    Constraint = constraint
                });
            }
        }

        registry.RegisterType(type: typeInfo);
    }

    /// <summary>
    /// Re-resolves protocol method return types that failed to resolve during the initial pass
    /// due to forward references (e.g., Crashable.crash_message() -> Text where Text was not
    /// yet registered when protocols were first processed).
    /// Analogous to ResolveProgramMemberVariables for record/entity member variables.
    /// </summary>
    private static void ResolveProtocolMethodReturnTypes(TypeRegistry registry, Program program) // NOSONAR S3776
    {
        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            if (node is not ProtocolDeclaration protocolDecl)
            {
                continue;
            }

            var existing = registry.LookupType(name: protocolDecl.Name) as ProtocolTypeInfo;
            if (existing == null || existing.Methods.Count == 0)
            {
                continue;
            }

            // Check if any method has a null return type where the declaration declares one
            bool needsRefresh = false;
            foreach (RoutineSignature method in protocolDecl.Methods)
            {
                string rawName = method.Name;
                bool isFailable = rawName.EndsWith(value: '!');
                string fullName = isFailable ? rawName[..^1] : rawName;
                bool isInstance = fullName.StartsWith(value: "Me.");
                string methodName = isInstance ? fullName[3..] : fullName;

                ProtocolMethodInfo? protoMethod = existing.Methods.FirstOrDefault(predicate: m =>
                    m.Name == methodName && m.IsFailable == isFailable);

                // A param whose type was a forward reference (e.g. a concrete `index: U64` before
                // U64 was registered) is silently dropped by FillProtocolMethods, leaving the proto
                // method with fewer params than declared. Detect the count mismatch and re-fill now
                // that all type shells exist, so conformance (S703) sees the real arity. This must
                // run for void methods too (e.g. `$setitem!`), so it precedes the return-type check.
                int declParamCount = method.Parameters.Count(predicate: p => p.Name != "me");
                if (protoMethod != null && protoMethod.ParameterTypes.Count != declParamCount)
                {
                    needsRefresh = true;
                    break;
                }

                if (method.ReturnType == null)
                {
                    continue; // Intentionally void — nothing more to check for return type
                }

                if (protoMethod?.ReturnType == null)
                {
                    needsRefresh = true;
                    break;
                }
            }

            if (!needsRefresh)
            {
                continue;
            }

            // Reset and re-fill with all type shells now registered
            existing.Methods = [];
            FillProtocolMethods(registry: registry, protocol: protocolDecl);
        }
    }

    /// <summary>
    /// Re-resolves routine signatures after all module types are registered.
    /// This repairs stdlib routines that were registered before a referenced return type or
    /// parameter type became available and were later finalized to Blank/Error.
    /// </summary>
    private static void ResolveRoutineSignatures(TypeRegistry registry, Program program,
        string moduleName)
    {
        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            if (node is not RoutineDeclaration routine)
            {
                continue;
            }

            if (ShouldSkipBuilderServiceRoutineDecl(routine: routine, moduleName: moduleName))
            {
                continue;
            }

            string methodName = routine.Name;
            TypeInfo? ownerType = null;
            int dotIndex = routine.Name.IndexOf(value: '.');
            if (dotIndex > 0)
            {
                string ownerName = routine.Name[..dotIndex];
                methodName = routine.Name[(dotIndex + 1)..];
                ownerType = registry.LookupType(name: ownerName) ??
                            registry.LookupType(name: $"{moduleName}.{ownerName}");
                if (ownerType == null)
                {
                    continue;
                }
            }

            var genericContext = new List<string>();
            if (ownerType?.GenericParameters != null)
            {
                genericContext.AddRange(collection: ownerType.GenericParameters);
            }

            if (routine.GenericParameters != null)
            {
                genericContext.AddRange(collection: routine.GenericParameters);
            }

            List<string>? ctx = genericContext.Count > 0
                ? genericContext
                : null;

            var parameters = new List<ParameterInfo>();
            foreach (Parameter param in routine.Parameters)
            {
                TypeInfo? paramType = ResolveSimpleType(registry: registry,
                    typeExpr: param.Type,
                    genericParams: ctx,
                    moduleName: moduleName);

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
                        DefaultValue = param.DefaultValue,
                        IsVariadicParam = param.IsVariadic
                    });
            }

            TypeInfo? resolvedReturnType = routine.ReturnType != null
                ? ResolveSimpleType(registry: registry,
                    typeExpr: routine.ReturnType,
                    genericParams: ctx,
                    moduleName: moduleName)
                : null;

            RoutineInfo? existingRoutine;
            if (ownerType != null)
            {
                string baseName = $"{ownerType.Name}.{methodName}";
                existingRoutine = parameters.Count > 0
                    ? registry.LookupRoutineOverload(baseName: baseName,
                        argTypes: parameters.Select(selector: p => p.Type).ToList())
                    : registry.LookupRoutine(fullName: baseName,
                        isFailable: routine.IsFailable);
            }
            else
            {
                string baseName = string.IsNullOrEmpty(value: moduleName)
                    ? methodName
                    : $"{moduleName}.{methodName}";
                existingRoutine = parameters.Count > 0
                    ? registry.LookupRoutineOverload(baseName: baseName,
                        argTypes: parameters.Select(selector: p => p.Type).ToList())
                    : registry.LookupRoutine(fullName: baseName,
                        isFailable: routine.IsFailable);
            }

            if (existingRoutine == null)
            {
                continue;
            }

            bool hasErrorParams = existingRoutine.Parameters.Any(
                predicate: p => p.Type is ErrorTypeInfo);
            bool hasDeclaredReturn = routine.ReturnType != null;
            bool missingReturn = hasDeclaredReturn &&
                                 (existingRoutine.ReturnType == null ||
                                  existingRoutine.ReturnType is ErrorTypeInfo ||
                                  existingRoutine.ReturnType.Name == "Blank");

            if (!hasErrorParams && !missingReturn)
            {
                continue;
            }

            registry.UpdateRoutine(routine: existingRoutine,
                parameters: parameters,
                returnType: resolvedReturnType,
                genericParameters: existingRoutine.GenericParameters,
                genericConstraints: existingRoutine.GenericConstraints);
        }
    }

    /// <summary>
    /// Fills in method signatures for a previously registered protocol type.
    /// This is the second pass — all protocols are registered, so cross-references resolve.
    /// </summary>
    private static void FillProtocolMethods(TypeRegistry registry, ProtocolDeclaration protocol) // NOSONAR S3776
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

            // For failable methods, also expose a `try_X` non-failable variant returning
            // Maybe[T] (or Bool when T is Blank), so call sites typed against the bare
            // protocol (e.g. for-loop desugaring's `iter.try_next()` where `iter: Iterator[T]`)
            // can resolve. Mirrors ErrorHandlingGenerator.GenerateTryVariant's shape.
            if (isFailable)
            {
                string tryName = "try_" + methodName.TrimStart(trimChar: '$');
                TypeInfo? tryReturnType;
                if (resolvedReturnType == null || resolvedReturnType.Name == "Blank")
                {
                    tryReturnType = registry.LookupType(name: "Bool");
                }
                else
                {
                    TypeInfo? maybeDef = registry.LookupType(name: "Maybe");
                    tryReturnType = maybeDef != null
                        ? registry.GetOrCreateResolution(
                            genericDef: maybeDef,
                            typeArguments: [resolvedReturnType])
                        : null;
                }

                methods.Add(item: new ProtocolMethodInfo(name: tryName)
                {
                    IsInstanceMethod = isInstance,
                    ParameterTypes = parameterTypes,
                    ParameterNames = parameterNames,
                    ReturnType = tryReturnType,
                    IsFailable = false,
                    IsAutoDerivedVariant = true
                });
            }
        }

        existing.Methods = methods;
    }
}
