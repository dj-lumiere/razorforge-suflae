using System;
using System.Collections.Generic;
using System.Linq;
using Compiler.Resolution;
using SyntaxTree;
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
    private const string EquatableProtocolName = "Equatable";
    private const string CreateMethodName = "$create";

    private readonly TypeRegistry _registry;

    public AutoWiredRegistrationPass(TypeRegistry registry)
    {
        _registry = registry;
    }

    public void Run(bool builderServiceImported = true)
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

        // BuilderService helper-type closures (List[FieldInfo], List[ProtocolInfo],
        // List[RoutineInfo], Dict[Text,Data]) are only resolved when the user program
        // actually imports BuilderService. Otherwise GMP would drag in the full
        // BTreeListNode/Owned/Array/ArrayIterator closure for every type via the
        // metadata routines registered on each type.
        // Data itself is needed unconditionally for the Data.$create auto-registration below.
        TypeSymbol? dataType = _registry.LookupType(name: "Data");
        TypeSymbol? listFieldInfoType = null;
        TypeSymbol? listProtocolInfoType = null;
        TypeSymbol? listRoutineInfoType = null;
        if (builderServiceImported)
        {
            TypeSymbol? fieldInfoType = _registry.LookupType(name: "FieldInfo");
            TypeSymbol? protocolInfoType = _registry.LookupType(name: "ProtocolInfo");
            TypeSymbol? routineInfoType = _registry.LookupType(name: "RoutineInfo");

            listFieldInfoType = listDef != null && fieldInfoType != null
                ? _registry.GetOrCreateResolution(genericDef: listDef, typeArguments: [fieldInfoType])
                : null;
            listProtocolInfoType = listDef != null && protocolInfoType != null
                ? _registry.GetOrCreateResolution(genericDef: listDef,
                    typeArguments: [protocolInfoType])
                : null;
            listRoutineInfoType = listDef != null && routineInfoType != null
                ? _registry.GetOrCreateResolution(genericDef: listDef,
                    typeArguments: [routineInfoType])
                : null;
        }

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
                byteSizeType: byteSizeType);

            switch (type.Category)
            {
                case TypeCategory.Record:
                    // Blank maps to LLVM void — it cannot appear as a parameter type.
                    // Skip comparison/hash/copy stubs; two Blanks are trivially equal.
                    // Wrapper types (Owned, Retained, Viewed, etc.) are transparent forwarders —
                    // WrapperForwardingPass lazily synthesizes their $hash/$eq/$cmp from the inner T.
                    // Don't register field-based stubs here: for zero-field wrappers (T)
                    // WiredRoutinePass would generate wrong bodies (returns 0 / returns true).
                    bool isWrapper = type is RecordTypeInfo &&
                                     WrapperForwardingPass.WrapperTypeNames.Contains(
                                         item: (type as RecordTypeInfo)?.GenericDefinition?.Name
                                               ?? type.Name);
                    // @llvm("ptr")-backed opaque handles (BigIntHandle, BigDecHandle, etc.)
                    // have no semantic equality / hash — they're raw pointers. Skip auto-derivation.
                    bool isOpaqueBackend = type is RecordTypeInfo { HasDirectBackendType: true };
                    if (!type.IsBlank && !isWrapper && !isOpaqueBackend)
                    {
                        // $hash / $eq are opt-in: only auto-derived when the record explicitly
                        // declares `obeys Hashable` / `obeys Equatable`. This avoids fanning out
                        // synthesized bodies (and corresponding LLVM defs) for every record in
                        // scope, and gives users an explicit place to override semantics.
                        if (u64Type != null && ObeysProtocol(type: type, protocolName: "FastHashable"))
                        {
                            MaybeRegisterWired(owner: type,
                                name: "$hash",
                                returnType: u64Type,
                                existingMethods: existingMethods);
                        }

                        if (u64Type != null && ObeysProtocol(type: type, protocolName: "Hashable"))
                        {
                            MaybeRegisterKeyedHash(owner: type, u64Type: u64Type,
                                existingMethods: existingMethods);
                        }

                        if (boolType != null && ObeysProtocol(type: type, protocolName: EquatableProtocolName))
                        {
                            MaybeRegisterWiredWithParam(owner: type,
                                name: "$eq",
                                paramName: "you",
                                paramType: type,
                                returnType: boolType,
                                existingMethods: existingMethods);
                        }

                        // Synthesize `$copy() -> Me` for Assignable records (auto-derived when the
                        // @llvm layout contains no `ptr`, or explicit via `obeys Assignable`).
                        // Body is `return me`, which lowers to a structural bitwise copy via the
                        // return value. Records with custom $copy (raw-ptr wrappers, opt-in records
                        // with managed-pointer fields) keep their user-written body.
                        if (ObeysProtocol(type: type, protocolName: "Assignable"))
                        {
                            MaybeRegisterWired(owner: type,
                                name: "$copy",
                                returnType: type,
                                existingMethods: existingMethods);
                            // Assignable obeys Cloneable: any type with auto-$copy also gets
                            // `clone() -> ?Me`. Return type uses the rvalue mark; body is `return me`.
                            MaybeRegisterWired(owner: type,
                                name: "clone",
                                returnType: type,
                                existingMethods: existingMethods);
                        }
                    }

                    break;

                case TypeCategory.Entity:
                    // Only synthesize `$eq` when every member field's type also has an
                    // accessible `$eq`. Otherwise the auto-derived body would emit calls
                    // like `me.children == you.children` for non-equatable element types
                    // (e.g. `Array[T, 64]`), and the cascade dead-ends at link time.
                    // Entities that genuinely need equality but hold non-equatable fields
                    // should declare `$eq` explicitly with the right semantics.
                    // Only synthesize `$eq` when every member field's type also has an
                    // accessible `$eq`. Otherwise the auto-derived body would emit calls
                    // like `me.children == you.children` for non-equatable element types
                    // (e.g. `Array[T, 64]`), and the cascade dead-ends at link time.
                    if (boolType != null && ObeysProtocol(type: type, protocolName: EquatableProtocolName) &&
                        AllFieldsHaveEquality(type: type))
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
                            m.Name == CreateMethodName &&
                            m.Parameters.Count == entityForCreate.MemberVariables.Count &&
                            entityForCreate.MemberVariables.Select(selector: mv => mv.Name)
                                           .SequenceEqual(second: m.Parameters.Select(selector: p => p.Name))))
                    {
                        _registry.RegisterRoutine(routine: new RoutineInfo(name: CreateMethodName)
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
                        MaybeRegisterKeyedHash(owner: type, u64Type: u64Type,
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

                    // Choices auto-derive Assignable (scalar tag layout).
                    MaybeRegisterWired(owner: type,
                        name: "$copy",
                        returnType: type,
                        existingMethods: existingMethods);
                    MaybeRegisterWired(owner: type,
                        name: "clone",
                        returnType: type,
                        existingMethods: existingMethods);

                    // S64.$create(from: ChoiceType) — choice_val.S64() desugars to S64.$create(from: choice_val)
                    if (s64Type != null && !type.IsGenericDefinition &&
                        _registry.LookupRoutineOverload(baseName: "S64.$create",
                            argTypes: [type]) == null)
                    {
                        _registry.RegisterRoutine(routine: new RoutineInfo(name: CreateMethodName)
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
                        !existingMethods.Any(predicate: m => m.Name == CreateMethodName))
                    {
                        _registry.RegisterRoutine(routine: new RoutineInfo(name: CreateMethodName)
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
                        MaybeRegisterKeyedHash(owner: type, u64Type: u64Type,
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

                    // Flags auto-derive Assignable (scalar bitset layout).
                    MaybeRegisterWired(owner: type,
                        name: "$copy",
                        returnType: type,
                        existingMethods: existingMethods);
                    MaybeRegisterWired(owner: type,
                        name: "clone",
                        returnType: type,
                        existingMethods: existingMethods);

                    // U64.$create(from: FlagsType) — flags_val.U64() desugars to U64.$create(from: flags_val)
                    if (u64Type != null && !type.IsGenericDefinition &&
                        _registry.LookupRoutineOverload(baseName: "U64.$create",
                            argTypes: [type]) == null)
                    {
                        _registry.RegisterRoutine(routine: new RoutineInfo(name: CreateMethodName)
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
                                             .Where(predicate: m => m.Name == CreateMethodName)
                                             .ToList();

            foreach (TypeSymbol type in _registry.GetAllTypes())
            {
                if (type.Category is not (TypeCategory.Record or TypeCategory.Entity
                    or TypeCategory.Choice or TypeCategory.Flags or TypeCategory.Variant))
                {
                    continue;
                }

                // Skip generic-definition types (T, Retained[T], List[T], etc. without
                // concrete arg) and WrapperTypeInfo definitions — registering a $create(from: T)
                // for the bare wrapper produces a phantom Text.$create(Core.Owned) symbol that
                // overload-resolution can drift onto, then linker fails (no definition emitted).
                if (type.IsGenericDefinition || type is WrapperTypeInfo)
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

                _registry.RegisterRoutine(routine: new RoutineInfo(name: CreateMethodName)
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
                                             .Where(predicate: m => m.Name == CreateMethodName)
                                             .ToList();

            foreach (TypeSymbol type in _registry.GetAllTypes())
            {
                // Include concrete storable types
                if (type.Category is not (TypeCategory.Record or TypeCategory.Entity
                    or TypeCategory.Choice or TypeCategory.Flags))
                {
                    continue;
                }

                // Skip non-boxable types
                if (IsCarrierType(type: type) || type is VariantTypeInfo or WrapperTypeInfo)
                {
                    continue;
                }

                // Skip generic-definition types (same reason as Text loop above)
                if (type.IsGenericDefinition)
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

                _registry.RegisterRoutine(routine: new RoutineInfo(name: CreateMethodName)
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
    /// Registers the keyed `$hash(k0: U64, k1: U64) -> U64` overload if not already defined.
    /// Distinct from the unkeyed `$hash()` by parameter count, so both can coexist.
    /// </summary>
    private void MaybeRegisterKeyedHash(TypeSymbol owner, TypeSymbol u64Type,
        List<RoutineInfo> existingMethods)
    {
        if (existingMethods.Any(predicate: m => m is { Name: "$hash", Parameters.Count: 2 }))
        {
            return;
        }

        _registry.RegisterRoutine(routine: new RoutineInfo(name: "$hash")
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = owner,
            Parameters =
            [
                new ParameterInfo(name: "k0", type: u64Type),
                new ParameterInfo(name: "k1", type: u64Type)
            ],
            ReturnType = u64Type,
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
    /// Registers a failable wired routine if not already defined (for clone, $create!).
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
    /// <summary>
    /// Returns true when every member-variable type on <paramref name="type"/> supports `$eq`
    /// — either it obeys `Equatable`, has an explicit `$eq` method, or is a primitive /
    /// `@llvm("...")`-backed record (whose equality is a built-in instruction). Used to gate
    /// auto-derivation of `$eq` so entities holding non-equatable fields (e.g. `Array[T, N]`)
    /// don't get a synthesised body whose recursion dead-ends at link time.
    /// </summary>
    private bool AllFieldsHaveEquality(TypeSymbol type)
    {
        List<MemberVariableInfo>? members = type switch
        {
            RecordTypeInfo r => r.MemberVariables,
            EntityTypeInfo e => e.MemberVariables,
            _ => null
        };
        if (members == null) return true;

        return members.All(m => TypeHasEquality(type: m.Type));
    }

    private bool TypeHasEquality(TypeSymbol type)
    {
        return TypeHasEquality(type: type, seen: new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Recursive check for whether <paramref name="type"/> supports `$eq`. Handles three layers:
    /// (1) primitives / `@llvm` records — built-in IR equality;
    /// (2) explicit `$eq` method or obeys `Equatable` — registered conformance;
    /// (3) generic resolution like `Array[T, N]` — looks up the generic def's `$eq` method
    /// and recursively verifies every `T obeys Equatable` constraint against the substituted
    /// type args. Without (3), `Array[X, 64]` passes the check (because `Array.$eq` exists
    /// on the generic def) even though the body's recursion into `X.$ne` link-errors.
    /// </summary>
    private bool TypeHasEquality(TypeSymbol type, HashSet<string> seen)
    {
        // Treat generic parameters and error / blank types as permissive (the constraint
        // either narrows them later or they're already a no-op).
        if (type is GenericParameterTypeInfo or ErrorTypeInfo || type.IsBlank) return true;

        // @llvm-backed records (numeric primitives, Bool, Character, Byte, Hijacked[T])
        // get equality from the underlying IR instruction.
        if (type is RecordTypeInfo { HasDirectBackendType: true }) return true;

        // Cycle guard — recursive record / entity types must not loop here.
        if (!seen.Add(item: type.FullName)) return true;

        // For a generic resolution, the generic def's `$eq` method may carry
        // `T obeys Equatable` constraints. Each such constraint must hold for the
        // corresponding type argument.
        TypeSymbol? genericDef = type switch
        {
            RecordTypeInfo r => r.GenericDefinition,
            EntityTypeInfo e => e.GenericDefinition,
            _ => null
        };
        if (genericDef != null && type.TypeArguments is { Count: > 0 } typeArgs &&
            genericDef.GenericParameters is { Count: > 0 } gParams &&
            gParams.Count == typeArgs.Count)
        {
            RoutineInfo? defEq = _registry.LookupMethod(type: genericDef, methodName: "$eq");
            if (defEq is { GenericConstraints: { } constraints })
            {
                foreach (GenericConstraintDeclaration c in constraints)
                {
                    if (c.ConstraintType != ConstraintKind.Obeys ||
                        c.ConstraintTypes is not { Count: > 0 }) continue;
                    int idx = -1;
                    for (int i = 0; i < gParams.Count; i++)
                    {
                        if (gParams[index: i] == c.ParameterName) { idx = i; break; }
                    }
                    if (idx < 0) continue;
                    TypeSymbol argType = typeArgs[index: idx];
                    foreach (TypeExpression protoExpr in c.ConstraintTypes)
                    {
                        if (protoExpr.Name == EquatableProtocolName && !TypeHasEquality(type: argType, seen: seen))
                            return false;
                    }
                }
            }
        }

        // Explicit `$eq` method on the type (either user-defined or already auto-derived).
        if (_registry.LookupMethod(type: type, methodName: "$eq") != null) return true;

        // Type declares obeys Equatable — we expect a `$eq` will eventually be synthesised.
        if (ObeysProtocol(type: type, protocolName: EquatableProtocolName)) return true;

        return false;
    }

    private bool ObeysProtocol(TypeSymbol type, string protocolName)
    {
        List<TypeSymbol>? implemented = type switch
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
        return implemented.Any(p => CheckProtocol(p, protocolName, seen));
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
            return proto.ParentProtocols.Any(parent => CheckProtocol(parent, targetName, seen));
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
