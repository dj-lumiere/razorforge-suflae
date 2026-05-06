using Compiler.Resolution;
using Verification;
using Verification.Enums;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using TypeSymbol = TypeModel.Types.TypeInfo;

namespace Compiler.Synthesis;

/// <summary>
/// Phase 2.55: Auto-registers builder-generated member routine signatures for all user types.
/// These are default routines that every type of a given category gets ($hash(), $eq(), etc.).
/// $represent and $diagnose are auto-registered (overridable).
/// Only registers if the user hasn't already defined the routine.
/// </summary>
internal sealed class AutoWiredRegistrationPass
{
    private readonly TypeRegistry _registry;

    public AutoWiredRegistrationPass(TypeRegistry registry)
    {
        _registry = registry;
    }

    public void Run()
    {
        // Look up required types (bail on each if not available)
        TypeSymbol? textType = _registry.LookupType(name: "Text");
        TypeSymbol? boolType = _registry.LookupType(name: "Bool");
        TypeSymbol? u64Type = _registry.LookupType(name: "U64");
        TypeSymbol? s64Type = _registry.LookupType(name: "S64");
        TypeSymbol? byteSizeType = _registry.LookupType(name: "ByteSize");

        // Look up List[T] for list-returning synthesized routines
        TypeSymbol? listDef = _registry.LookupType(name: "List");
        TypeSymbol? listTextType = listDef != null && textType != null
            ? _registry.GetOrCreateResolution(genericDef: listDef, typeArguments: [textType])
            : null;

        // Look up BuilderService helper types (from stdlib or previous registration)
        TypeSymbol? fieldInfoType = _registry.LookupType(name: "FieldInfo");
        TypeSymbol? protocolInfoType = _registry.LookupType(name: "ProtocolInfo");
        TypeSymbol? routineInfoType = _registry.LookupType(name: "RoutineInfo");

        TypeSymbol? listFieldInfoType = listDef != null && fieldInfoType != null
            ? _registry.GetOrCreateResolution(genericDef: listDef, typeArguments: [fieldInfoType])
            : null;
        TypeSymbol? listProtocolInfoType = listDef != null && protocolInfoType != null
            ? _registry.GetOrCreateResolution(genericDef: listDef,
                typeArguments: [protocolInfoType])
            : null;
        TypeSymbol? listRoutineInfoType = listDef != null && routineInfoType != null
            ? _registry.GetOrCreateResolution(genericDef: listDef,
                typeArguments: [routineInfoType])
            : null;

        // Look up Dict[Text, Data] for all_fields() / open_fields()
        TypeSymbol? dictDef = _registry.LookupType(name: "Dict");
        TypeSymbol? dataType = _registry.LookupType(name: "Data");
        TypeSymbol? dictTextDataType = dictDef != null && textType != null && dataType != null
            ? _registry.GetOrCreateResolution(genericDef: dictDef,
                typeArguments: [textType, dataType])
            : null;

        foreach (TypeSymbol type in _registry.GetTypesWithMethods())
        {
            var existingMethods = _registry.GetMethodsForType(type: type)
                                           .ToList();

            // All types: $represent(), $diagnose() — auto-generated, overridable
            if (textType != null)
            {
                MaybeRegisterWired(owner: type,
                    name: "$represent",
                    returnType: textType,
                    existingMethods: existingMethods);
                MaybeRegisterWired(owner: type,
                    name: "$diagnose",
                    returnType: textType,
                    existingMethods: existingMethods);
            }

            // All types: BuilderService metadata routines
            BuilderInfoProvider.RegisterRoutinesOnType(type: type,
                existingMethods: existingMethods,
                registry: _registry,
                textType: textType,
                boolType: boolType,
                u64Type: u64Type,
                s64Type: s64Type,
                listTextType: listTextType,
                listFieldInfoType: listFieldInfoType,
                listProtocolInfoType: listProtocolInfoType,
                listRoutineInfoType: listRoutineInfoType,
                dictTextDataType: dictTextDataType,
                byteSizeType: byteSizeType);

            switch (type.Category)
            {
                case TypeCategory.Record:
                    // Blank maps to LLVM void — it cannot appear as a parameter type.
                    // Skip comparison/hash/copy stubs; two Blanks are trivially equal.
                    // Wrapper types (Owned, Retained, Viewed, etc.) are transparent forwarders —
                    // WrapperForwardingPass lazily synthesizes their $hash/$eq/$cmp from the inner T.
                    // Don't register field-based stubs here: for zero-field wrappers (Owned[T])
                    // WiredRoutinePass would generate wrong bodies (returns 0 / returns true).
                    bool isWrapper = type is RecordTypeInfo &&
                                     WrapperForwardingPass.WrapperTypeNames.Contains(
                                         item: (type as RecordTypeInfo)?.GenericDefinition?.Name
                                               ?? type.Name);
                    if (!type.IsBlank && !isWrapper)
                    {
                        // $hash / $eq are opt-in: only auto-derived when the record explicitly
                        // declares `obeys Hashable` / `obeys Equatable`. This avoids fanning out
                        // synthesized bodies (and corresponding LLVM defs) for every record in
                        // scope, and gives users an explicit place to override semantics.
                        if (u64Type != null && ObeysProtocol(type: type, protocolName: "Hashable"))
                        {
                            MaybeRegisterWired(owner: type,
                                name: "$hash",
                                returnType: u64Type,
                                existingMethods: existingMethods);
                        }

                        if (boolType != null && ObeysProtocol(type: type, protocolName: "Equatable"))
                        {
                            MaybeRegisterWiredWithParam(owner: type,
                                name: "$eq",
                                paramName: "you",
                                paramType: type,
                                returnType: boolType,
                                existingMethods: existingMethods);
                        }

                        // Records: $copy() — field-by-field copy; body generated by WiredRoutinePass.
                        MaybeRegisterWired(owner: type,
                            name: "$copy",
                            returnType: type,
                            existingMethods: existingMethods);
                    }

                    break;

                case TypeCategory.Entity:
                    if (boolType != null && ObeysProtocol(type: type, protocolName: "Equatable"))
                    {
                        MaybeRegisterWiredWithParam(owner: type,
                            name: "$eq",
                            paramName: "you",
                            paramType: type,
                            returnType: boolType,
                            existingMethods: existingMethods);
                    }

                    // Synthesize $create(field1: T1, ...) -> EntityType for field construction.
                    // Always synthesize the all-fields overload unless an exact match already exists,
                    // so field construction inside user-defined $create overloads works too.
                    // Skip generic definitions (their resolved instances get synthesis).
                    if (type is EntityTypeInfo entityForCreate &&
                        !type.IsGenericDefinition &&
                        !existingMethods.Any(predicate: m =>
                            m.Name == "$create" &&
                            m.Parameters.Count == entityForCreate.MemberVariables.Count &&
                            entityForCreate.MemberVariables.Select(selector: mv => mv.Name)
                                           .SequenceEqual(second: m.Parameters.Select(selector: p => p.Name))))
                    {
                        _registry.RegisterRoutine(routine: new RoutineInfo(name: "$create")
                        {
                            Kind = RoutineKind.Creator,
                            OwnerType = type,
                            Parameters = entityForCreate.MemberVariables
                                                        .Select(selector: mv =>
                                                             new ParameterInfo(name: mv.Name,
                                                                 type: mv.Type))
                                                        .ToList(),
                            ReturnType = type,
                            IsFailable = false,
                            DeclaredModification = ModificationCategory.Readonly,
                            ModificationCategory = ModificationCategory.Readonly,
                            Visibility = VisibilityModifier.Open,
                            IsSynthesized = true
                        });
                    }

                    break;

                case TypeCategory.Choice:
                    // Choices/flags get $eq/$hash unconditionally — equality is unambiguous
                    // tag-compare with no field-selection design choice to make. Stdlib's
                    // ComparisonSign and BuilderService enums rely on this for $represent /
                    // $diagnose / derived comparison operators.
                    if (u64Type != null)
                    {
                        MaybeRegisterWired(owner: type,
                            name: "$hash",
                            returnType: u64Type,
                            existingMethods: existingMethods);
                    }

                    if (boolType != null)
                    {
                        MaybeRegisterWiredWithParam(owner: type,
                            name: "$eq",
                            paramName: "you",
                            paramType: type,
                            returnType: boolType,
                            existingMethods: existingMethods);
                    }

                    // S64.$create(from: ChoiceType) — choice_val.S64() desugars to S64.$create(from: choice_val)
                    if (s64Type != null && !type.IsGenericDefinition &&
                        _registry.LookupRoutineOverload(baseName: "S64.$create",
                            argTypes: [type]) == null)
                    {
                        _registry.RegisterRoutine(routine: new RoutineInfo(name: "$create")
                        {
                            Kind = RoutineKind.Creator,
                            OwnerType = s64Type,
                            Parameters = [new ParameterInfo(name: "from", type: type)],
                            ReturnType = s64Type,
                            IsFailable = false,
                            DeclaredModification = ModificationCategory.Readonly,
                            ModificationCategory = ModificationCategory.Readonly,
                            Visibility = VisibilityModifier.Open,
                            IsSynthesized = true
                        });
                    }

                    if (textType != null)
                    {
                        MaybeRegisterWiredFailable(owner: type,
                            name: "$create!",
                            returnType: type,
                            existingMethods: existingMethods,
                            param: ("from", textType),
                            kind: RoutineKind.Creator);
                    }

                    if (listDef != null)
                    {
                        TypeSymbol listMeType = _registry.GetOrCreateResolution(
                            genericDef: listDef,
                            typeArguments: [type]);
                        MaybeRegisterWired(owner: type,
                            name: "all_cases",
                            returnType: listMeType,
                            existingMethods: existingMethods);
                    }

                    break;

                case TypeCategory.Crashable:
                    // crash_title() is @generated — synthesized from type name, overridable
                    if (textType != null)
                    {
                        MaybeRegisterWired(owner: type,
                            name: "crash_title",
                            returnType: textType,
                            existingMethods: existingMethods);
                    }

                    // Synthesize $create(field1: T1, ...) -> CrashableType for construction via throw
                    if (type is CrashableTypeInfo crashableForCreate &&
                        !existingMethods.Any(predicate: m => m.Name == "$create"))
                    {
                        _registry.RegisterRoutine(routine: new RoutineInfo(name: "$create")
                        {
                            Kind = RoutineKind.Creator,
                            OwnerType = type,
                            Parameters = crashableForCreate.MemberVariables
                                                           .Select(selector: mv =>
                                                                new ParameterInfo(name: mv.Name,
                                                                    type: mv.Type))
                                                           .ToList(),
                            ReturnType = type,
                            IsFailable = false,
                            DeclaredModification = ModificationCategory.Readonly,
                            ModificationCategory = ModificationCategory.Readonly,
                            Visibility = VisibilityModifier.Open,
                            IsSynthesized = true
                        });
                    }

                    // Auto-add Crashable protocol conformance (implicit from the crashable keyword)
                    TypeSymbol? crashableProto = _registry.LookupType(name: "Crashable");
                    if (crashableProto != null && type is CrashableTypeInfo crashableInfo &&
                        crashableInfo.ImplementedProtocols.All(predicate: p =>
                            p.Name != "Crashable"))
                    {
                        var protocols = crashableInfo.ImplementedProtocols.ToList();
                        protocols.Add(item: crashableProto);
                        _registry.UpdateCrashableProtocols(typeName: type.FullName,
                            protocols: protocols);
                    }

                    break;

                case TypeCategory.Flags:
                    // See Choice case above — equality is unambiguous bit-compare; always-on.
                    if (u64Type != null)
                    {
                        MaybeRegisterWired(owner: type,
                            name: "$hash",
                            returnType: u64Type,
                            existingMethods: existingMethods);
                    }

                    if (boolType != null)
                    {
                        MaybeRegisterWiredWithParam(owner: type,
                            name: "$eq",
                            paramName: "you",
                            paramType: type,
                            returnType: boolType,
                            existingMethods: existingMethods);
                    }

                    // U64.$create(from: FlagsType) — flags_val.U64() desugars to U64.$create(from: flags_val)
                    if (u64Type != null && !type.IsGenericDefinition &&
                        _registry.LookupRoutineOverload(baseName: "U64.$create",
                            argTypes: [type]) == null)
                    {
                        _registry.RegisterRoutine(routine: new RoutineInfo(name: "$create")
                        {
                            Kind = RoutineKind.Creator,
                            OwnerType = u64Type,
                            Parameters = [new ParameterInfo(name: "from", type: type)],
                            ReturnType = u64Type,
                            IsFailable = false,
                            DeclaredModification = ModificationCategory.Readonly,
                            ModificationCategory = ModificationCategory.Readonly,
                            Visibility = VisibilityModifier.Open,
                            IsSynthesized = true
                        });
                    }

                    MaybeRegisterWired(owner: type,
                        name: "all_on",
                        returnType: type,
                        existingMethods: existingMethods);
                    MaybeRegisterWired(owner: type,
                        name: "all_off",
                        returnType: type,
                        existingMethods: existingMethods);
                    if (listDef != null)
                    {
                        TypeSymbol listMeType = _registry.GetOrCreateResolution(
                            genericDef: listDef,
                            typeArguments: [type]);
                        MaybeRegisterWired(owner: type,
                            name: "all_cases",
                            returnType: listMeType,
                            existingMethods: existingMethods);
                    }

                    break;
            }
        }

        // Source location and caller standalone routines (injected at call site by codegen)
        BuilderInfoProvider.RegisterStandaloneRoutines(registry: _registry,
            textType: textType,
            s64Type: s64Type);

        // Synthesize BuilderService record type with platform/build info member routines
        BuilderInfoProvider.RegisterModuleRoutines(registry: _registry,
            textType: textType,
            u64Type: u64Type,
            s64Type: s64Type);

        // Auto-register Text.$create(from: T) for all concrete user types
        // This makes every type structurally satisfy Representable[T]
        if (textType != null)
        {
            var textCreateMethods = _registry.GetMethodsForType(type: textType)
                                             .Where(predicate: m => m.Name == "$create")
                                             .ToList();

            foreach (TypeSymbol type in _registry.GetAllTypes())
            {
                if (type.Category is not (TypeCategory.Record or TypeCategory.Entity
                    or TypeCategory.Choice or TypeCategory.Flags or TypeCategory.Variant))
                {
                    continue;
                }

                bool alreadyDefined = textCreateMethods.Any(predicate: m =>
                    m.Parameters.Count == 1 &&
                    m.Parameters[index: 0].Type.FullName == type.FullName);
                if (alreadyDefined)
                {
                    continue;
                }

                _registry.RegisterRoutine(routine: new RoutineInfo(name: "$create")
                {
                    Kind = RoutineKind.Creator,
                    OwnerType = textType,
                    Parameters = [new ParameterInfo(name: "from", type: type)],
                    ReturnType = textType,
                    IsFailable = false,
                    DeclaredModification = ModificationCategory.Readonly,
                    ModificationCategory = ModificationCategory.Readonly,
                    Visibility = VisibilityModifier.Open,
                    IsSynthesized = true
                });
            }
        }

        // Register BS per-type routines + $represent/$diagnose as universal methods.
        // This allows T.data_size(), K.type_id(), T.$represent(), etc. to resolve in
        // generic function bodies where the receiver is a GenericParameterTypeInfo.
        var tParam = new GenericParameterTypeInfo(name: "T");
        var universalExisting = new List<RoutineInfo>();
        BuilderInfoProvider.RegisterRoutinesOnType(type: tParam,
            existingMethods: universalExisting,
            registry: _registry,
            textType: textType,
            boolType: boolType,
            u64Type: u64Type,
            s64Type: s64Type,
            listTextType: listTextType,
            listFieldInfoType: listFieldInfoType,
            listProtocolInfoType: listProtocolInfoType,
            listRoutineInfoType: listRoutineInfoType,
            dictTextDataType: dictTextDataType,
            byteSizeType: byteSizeType);
        if (textType != null)
        {
            MaybeRegisterWired(owner: tParam,
                name: "$represent",
                returnType: textType,
                existingMethods: universalExisting);
            MaybeRegisterWired(owner: tParam,
                name: "$diagnose",
                returnType: textType,
                existingMethods: universalExisting);
        }

        // Auto-register Data.$create(from: T) for all concrete storable types
        // This enables type-erased boxing: Data(42), Data(my_entity), etc.
        if (dataType != null)
        {
            var dataCreateMethods = _registry.GetMethodsForType(type: dataType)
                                             .Where(predicate: m => m.Name == "$create")
                                             .ToList();

            foreach (TypeSymbol type in _registry.GetAllTypes())
            {
                // Include concrete storable types + intrinsics
                if (type.Category is not (TypeCategory.Record or TypeCategory.Entity
                    or TypeCategory.Choice or TypeCategory.Flags or TypeCategory.Intrinsic))
                {
                    continue;
                }

                // Skip non-boxable types
                if (IsCarrierType(type: type) || type is VariantTypeInfo or WrapperTypeInfo)
                {
                    continue;
                }

                // Skip Data itself (no boxing Data in Data)
                if (type.FullName == dataType.FullName)
                {
                    continue;
                }

                // Skip Blank -- void cannot be a parameter type in LLVM IR
                if (type.IsBlank)
                {
                    continue;
                }

                bool alreadyDefined = dataCreateMethods.Any(predicate: m =>
                    m.Parameters.Count == 1 &&
                    m.Parameters[index: 0].Type.FullName == type.FullName);
                if (alreadyDefined)
                {
                    continue;
                }

                _registry.RegisterRoutine(routine: new RoutineInfo(name: "$create")
                {
                    Kind = RoutineKind.Creator,
                    OwnerType = dataType,
                    Parameters = [new ParameterInfo(name: "from", type: type)],
                    ReturnType = dataType,
                    IsFailable = false,
                    DeclaredModification = ModificationCategory.Readonly,
                    ModificationCategory = ModificationCategory.Readonly,
                    Visibility = VisibilityModifier.Open,
                    IsSynthesized = true
                });
            }
        }
    }

    /// <summary>
    /// Registers a no-parameter readonly wired routine if not already defined.
    /// </summary>
    private void MaybeRegisterWired(TypeSymbol owner, string name, TypeSymbol returnType,
        List<RoutineInfo> existingMethods)
    {
        if (existingMethods.Any(predicate: m => m.Name == name))
        {
            return;
        }

        _registry.RegisterRoutine(routine: new RoutineInfo(name: name)
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = owner,
            Parameters = [],
            ReturnType = returnType,
            IsFailable = false,
            DeclaredModification = ModificationCategory.Readonly,
            ModificationCategory = ModificationCategory.Readonly,
            Visibility = VisibilityModifier.Open,
            IsSynthesized = true
        });
    }

    /// <summary>
    /// Registers a single-parameter readonly wired routine if not already defined.
    /// </summary>
    private void MaybeRegisterWiredWithParam(TypeSymbol owner, string name, string paramName,
        TypeSymbol paramType, TypeSymbol returnType, List<RoutineInfo> existingMethods)
    {
        if (existingMethods.Any(predicate: m => m.Name == name))
        {
            return;
        }

        _registry.RegisterRoutine(routine: new RoutineInfo(name: name)
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = owner,
            Parameters = [new ParameterInfo(name: paramName, type: paramType)],
            ReturnType = returnType,
            IsFailable = false,
            DeclaredModification = ModificationCategory.Readonly,
            ModificationCategory = ModificationCategory.Readonly,
            Visibility = VisibilityModifier.Open,
            IsSynthesized = true
        });
    }

    /// <summary>
    /// Registers a failable wired routine if not already defined (for copy!, $create!).
    /// </summary>
    private void MaybeRegisterWiredFailable(TypeSymbol owner, string name, TypeSymbol returnType,
        List<RoutineInfo> existingMethods, (string name, TypeSymbol type)? param = null,
        RoutineKind kind = RoutineKind.MemberRoutine)
    {
        if (existingMethods.Any(predicate: m => m.Name == name))
        {
            return;
        }

        _registry.RegisterRoutine(routine: new RoutineInfo(name: name)
        {
            Kind = kind,
            OwnerType = owner,
            Parameters = param.HasValue
                ? [new ParameterInfo(name: param.Value.name, type: param.Value.type)]
                : [],
            ReturnType = returnType,
            IsFailable = true,
            DeclaredModification = ModificationCategory.Readonly,
            ModificationCategory = ModificationCategory.Readonly,
            Visibility = VisibilityModifier.Open,
            IsSynthesized = true
        });
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> declares conformance to the named protocol
    /// via <c>obeys</c>, either directly or transitively through a parent protocol.
    /// Used to gate auto-derivation of <c>$eq</c> / <c>$hash</c> on records, entities,
    /// choices, and flags — these are now opt-in rather than universal.
    /// </summary>
    private bool ObeysProtocol(TypeSymbol type, string protocolName)
    {
        IReadOnlyList<TypeSymbol>? implemented = type switch
        {
            ChoiceTypeInfo c => c.ImplementedProtocols,
            FlagsTypeInfo f => f.ImplementedProtocols,
            RecordTypeInfo r => r.ImplementedProtocols,
            EntityTypeInfo e => e.ImplementedProtocols,
            _ => null
        };
        if (implemented == null)
        {
            return false;
        }

        var seen = new HashSet<string>();
        foreach (TypeSymbol p in implemented)
        {
            if (CheckProtocol(p, protocolName, seen))
            {
                return true;
            }
        }
        return false;
    }

    private bool CheckProtocol(TypeSymbol candidate, string targetName, HashSet<string> seen)
    {
        if (!seen.Add(candidate.Name))
        {
            return false;
        }
        if (candidate.Name == targetName)
        {
            return true;
        }
        // Resolve the latest version from the registry — ImplementedProtocols entries
        // can be stale (immutable type updates). The fully-populated parent list lives
        // on the registry's current ProtocolTypeInfo.
        TypeSymbol latest = _registry.LookupType(name: candidate.Name) ?? candidate;
        if (latest is ProtocolTypeInfo proto)
        {
            foreach (ProtocolTypeInfo parent in proto.ParentProtocols)
            {
                if (CheckProtocol(parent, targetName, seen))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Returns the base name ("Maybe", "Result", or "Lookup") for a carrier type,
    /// or null if the type is not a carrier type.
    /// </summary>
    private static string? GetCarrierBaseName(TypeSymbol type)
    {
        if (type is not RecordTypeInfo r)
        {
            return null;
        }

        string baseName = r.GenericDefinition?.Name ?? r.Name;
        return baseName is "Maybe" or "Result" or "Lookup"
            ? baseName
            : null;
    }

    /// <summary>
    /// Returns true if the type is a carrier type (Maybe, Result, or Lookup).
    /// </summary>
    private static bool IsCarrierType(TypeSymbol type) => GetCarrierBaseName(type: type) != null;
}
