namespace SemanticVerification;

using Enums;
using Symbols;
using Types;
using TypeSymbol = Types.TypeInfo;

public sealed partial class SemanticAnalyzer
{
    #region Phase 2.55: Auto-Register Builder-Generated Member Routines

    /// <summary>
    /// Auto-registers builder-generated member routine signatures for all user types.
    /// These are default routines that every type of a given category gets ($hash(), $eq(), etc.).
    /// $represent and $diagnose are auto-registered (overridable).
    /// Only registers if the user hasn't already defined the routine.
    /// </summary>
    private void AutoRegisterWiredRoutines()
    {
        // Look up required types (bail on each if not available)
        TypeSymbol? textType = _registry.LookupType(name: "Text");
        TypeSymbol? boolType = _registry.LookupType(name: "Bool");
        TypeSymbol? u64Type = _registry.LookupType(name: "U64");
        TypeSymbol? s64Type = _registry.LookupType(name: "S64");
        TypeSymbol? byteSizeType = _registry.LookupType(name: "ByteSize");

        // Look up protocols for auto-conformance
        TypeSymbol? hashableProtocol = _registry.LookupType(name: "Hashable");

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

                    // Records: $copy() — field-by-field copy; body generated by WiredRoutinePass.
                    MaybeRegisterWired(owner: type,
                        name: "$copy",
                        returnType: type,
                        existingMethods: existingMethods);

                    // #27: Records with all-Hashable fields auto-add Hashable conformance
                    if (hashableProtocol != null && type is RecordTypeInfo record)
                    {
                        if (record.ImplementedProtocols.All(predicate: p =>
                                p.Name != "Hashable") && AllFieldsHashable(record: record))
                        {
                            var protocols = record.ImplementedProtocols.ToList();
                            protocols.Add(item: hashableProtocol);
                            _registry.UpdateRecordProtocols(recordName: type.FullName,
                                protocols: protocols);
                        }
                    }

                    break;

                case TypeCategory.Entity:
                    if (s64Type != null)
                    {
                        MaybeRegisterWired(owner: type,
                            name: "id",
                            returnType: s64Type,
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
                        MaybeRegisterWiredWithParam(owner: type,
                            name: "$same",
                            paramName: "you",
                            paramType: type,
                            returnType: boolType,
                            existingMethods: existingMethods);
                        // $notsame is generated from $same in GenerateDerivedOperators (Phase 3)
                    }

                    MaybeRegisterWiredFailable(owner: type,
                        name: "copy!",
                        returnType: type,
                        existingMethods: existingMethods);
                    break;

                case TypeCategory.Choice:
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
                        _registry.LookupRoutineOverload(
                            baseName: "S64.$create", argTypes: [type]) == null)
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
                                .Select(selector: mv => new ParameterInfo(name: mv.Name, type: mv.Type))
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
                        crashableInfo.ImplementedProtocols.All(predicate: p => p.Name != "Crashable"))
                    {
                        var protocols = crashableInfo.ImplementedProtocols.ToList();
                        protocols.Add(item: crashableProto);
                        _registry.UpdateCrashableProtocols(typeName: type.FullName,
                            protocols: protocols);
                    }

                    break;

                case TypeCategory.Flags:
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
                        _registry.LookupRoutineOverload(
                            baseName: "U64.$create", argTypes: [type]) == null)
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
        {
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
    /// Checks whether all member variables of a record implement Hashable.
    /// Primitives (S32, U64, Text, Bool, etc.), choices, flags, and other records
    /// that are Hashable are considered hashable. Entities, variants, and
    /// generic parameters are not.
    /// </summary>
    private bool AllFieldsHashable(RecordTypeInfo record)
    {
        IReadOnlyList<MemberVariableInfo> fields = record.MemberVariables;
        if (fields.Count == 0)
        {
            return true; // empty records are trivially hashable
        }

        foreach (MemberVariableInfo field in fields)
        {
            TypeSymbol fieldType = field.Type;

            // Intrinsic types (primitives) are always hashable
            if (fieldType is IntrinsicTypeInfo)
            {
                continue;
            }

            // Choices and flags are always hashable
            if (fieldType is ChoiceTypeInfo or FlagsTypeInfo)
            {
                continue;
            }

            // Records are hashable if they implement Hashable (recursive)
            if (fieldType is RecordTypeInfo fieldRecord)
            {
                if (fieldRecord.ImplementedProtocols.Any(predicate: p => p.Name == "Hashable"))
                {
                    continue;
                }

                // Check structurally: does it have hash()?
                if (_registry.LookupMethod(type: fieldType, methodName: "$hash") != null)
                {
                    continue;
                }

                return false;
            }

            // Entities, variants, generic parameters, etc. — not hashable
            return false;
        }

        return true;
    }

    #endregion
}
