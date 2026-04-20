namespace SemanticVerification;

using Compiler.Resolution;
using Enums;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeSymbol = TypeModel.Types.TypeInfo;

/// <summary>
/// Central authority for BuilderService routine registration and import-gating.
/// Provides static name sets for identifying BS routines and methods to register
/// them on types, as standalone functions, and as module-level routines.
/// </summary>
public static class BuilderInfoProvider
{
    /// <summary>Per-type BuilderService member routines (require 'import BuilderService').</summary>
    private static readonly HashSet<string> PerTypeRoutines = new(comparer: StringComparer.Ordinal)
    {
        "type_name",
        "type_kind",
        "type_id",
        "module_name",
        "is_generic",
        "generic_args",
        "member_variable_count",
        "member_variable_info",
        "all_member_variables",
        "open_member_variables",
        "protocols",
        "protocol_info",
        "routine_names",
        "routine_info",
        "annotations",
        "data_size",
        "full_type_name",
        "dependencies",
        "member_type_id",
        "var_name"
    };

    /// <summary>Standalone BuilderService routines (require 'import BuilderService').</summary>
    private static readonly HashSet<string> StandaloneRoutines =
        new(comparer: StringComparer.Ordinal)
        {
            "source_file",
            "source_line",
            "source_column",
            "source_routine",
            "source_module",
            "source_text",
            "caller_file",
            "caller_line",
            "caller_routine",
            // Platform/build info (formerly module-level, now standalone)
            "target_os",
            "target_arch",
            "builder_version",
            "build_mode",
            "build_timestamp",
            "page_size",
            "cache_line",
            "word_size"
        };

    /// <summary>Returns true if the routine name is a per-type BuilderService member routine.</summary>
    public static bool IsBuilderServiceRoutine(string name)
    {
        return PerTypeRoutines.Contains(item: name);
    }

    /// <summary>Returns true if the routine name is a standalone BuilderService routine.</summary>
    public static bool IsBuilderServiceStandalone(string name)
    {
        return StandaloneRoutines.Contains(item: name);
    }

    /// <summary>
    /// Registers all per-type BuilderService metadata routines on a given type.
    /// </summary>
    public static void RegisterRoutinesOnType(TypeSymbol type, List<RoutineInfo> existingMethods,
        TypeRegistry registry, TypeSymbol? textType, TypeSymbol? boolType,
        TypeSymbol? u64Type, TypeSymbol? s64Type, TypeSymbol? listTextType,
        TypeSymbol? listFieldInfoType, TypeSymbol? listProtocolInfoType,
        TypeSymbol? listRoutineInfoType, TypeSymbol? dictTextDataType,
        TypeSymbol? byteSizeType = null)
    {
        // Text-returning routines
        if (textType != null)
        {
            MaybeRegister(owner: type,
                name: "type_name",
                returnType: textType,
                existingMethods: existingMethods,
                registry: registry);
            MaybeRegister(owner: type,
                name: "module_name",
                returnType: textType,
                existingMethods: existingMethods,
                registry: registry);
            MaybeRegister(owner: type,
                name: "full_type_name",
                returnType: textType,
                existingMethods: existingMethods,
                registry: registry);
            MaybeRegister(owner: type,
                name: "var_name",
                returnType: textType,
                existingMethods: existingMethods,
                registry: registry);
        }

        // U64-returning routines
        if (u64Type != null)
        {
            MaybeRegister(owner: type,
                name: "type_id",
                returnType: u64Type,
                existingMethods: existingMethods,
                registry: registry);
            // data_size is RazorForge-only (used by collection internals via Hijacked pointer arithmetic)
            // Returns ByteSize for type-safe arithmetic; falls back to U64 if ByteSize not loaded yet.
            if (registry.Language == Language.RazorForge)
            {
                MaybeRegister(owner: type,
                    name: "data_size",
                    returnType: byteSizeType ?? u64Type,
                    existingMethods: existingMethods,
                    registry: registry);
            }
        }

        // S64-returning routines
        if (s64Type != null)
        {
            MaybeRegister(owner: type,
                name: "member_variable_count",
                returnType: s64Type,
                existingMethods: existingMethods,
                registry: registry);
        }

        // type_kind returns TypeKind choice (fall back to S64 if BS not loaded)
        {
            TypeSymbol? typeKindType = registry.LookupType(name: "TypeKind") ?? s64Type;
            if (typeKindType != null)
            {
                MaybeRegister(owner: type,
                    name: "type_kind",
                    returnType: typeKindType,
                    existingMethods: existingMethods,
                    registry: registry);
            }
        }

        // Bool-returning routines
        if (boolType != null)
        {
            MaybeRegister(owner: type,
                name: "is_generic",
                returnType: boolType,
                existingMethods: existingMethods,
                registry: registry);
        }

        // List[Text]-returning routines
        if (listTextType != null)
        {
            MaybeRegister(owner: type,
                name: "protocols",
                returnType: listTextType,
                existingMethods: existingMethods,
                registry: registry);
            MaybeRegister(owner: type,
                name: "routine_names",
                returnType: listTextType,
                existingMethods: existingMethods,
                registry: registry);
            MaybeRegister(owner: type,
                name: "annotations",
                returnType: listTextType,
                existingMethods: existingMethods,
                registry: registry);
            MaybeRegister(owner: type,
                name: "generic_args",
                returnType: listTextType,
                existingMethods: existingMethods,
                registry: registry);
            MaybeRegister(owner: type,
                name: "dependencies",
                returnType: listTextType,
                existingMethods: existingMethods,
                registry: registry);
        }

        // List[FieldInfo]-returning routines
        if (listFieldInfoType != null)
        {
            MaybeRegister(owner: type,
                name: "member_variable_info",
                returnType: listFieldInfoType,
                existingMethods: existingMethods,
                registry: registry);
        }

        // List[ProtocolInfo]-returning routines
        if (listProtocolInfoType != null)
        {
            MaybeRegister(owner: type,
                name: "protocol_info",
                returnType: listProtocolInfoType,
                existingMethods: existingMethods,
                registry: registry);
        }

        // List[RoutineInfo]-returning routines
        if (listRoutineInfoType != null)
        {
            MaybeRegister(owner: type,
                name: "routine_info",
                returnType: listRoutineInfoType,
                existingMethods: existingMethods,
                registry: registry);
        }

        // Dict[Text, Data]-returning routines
        if (dictTextDataType != null)
        {
            MaybeRegister(owner: type,
                name: "all_member_variables",
                returnType: dictTextDataType,
                existingMethods: existingMethods,
                registry: registry);
            MaybeRegister(owner: type,
                name: "open_member_variables",
                returnType: dictTextDataType,
                existingMethods: existingMethods,
                registry: registry);
        }

        // member_type_id(member_name: Text) -> U64
        if (u64Type != null && textType != null)
        {
            MaybeRegisterWithParam(owner: type,
                name: "member_type_id",
                paramName: "member_name",
                paramType: textType,
                returnType: u64Type,
                existingMethods: existingMethods,
                registry: registry);
        }
    }

    /// <summary>
    /// Registers standalone BuilderService routines (source_*, caller_*).
    /// </summary>
    public static void RegisterStandaloneRoutines(TypeRegistry registry, TypeSymbol? textType,
        TypeSymbol? s64Type)
    {
        if (textType != null)
        {
            foreach (string name in new[]
                     {
                         "source_file",
                         "source_routine",
                         "source_module",
                         "source_text",
                         "caller_file",
                         "caller_routine"
                     })
            {
                RegisterStandalone(registry: registry, name: name, returnType: textType);
            }
        }

        if (s64Type != null)
        {
            foreach (string name in new[]
                     {
                         "source_line",
                         "source_column",
                         "caller_line"
                     })
            {
                RegisterStandalone(registry: registry, name: name, returnType: s64Type);
            }
        }
    }

    /// <summary>
    /// Registers the BuilderService platform/build info routines as standalone functions.
    /// </summary>
    public static void RegisterModuleRoutines(TypeRegistry registry, TypeSymbol? textType,
        TypeSymbol? u64Type, TypeSymbol? s64Type)
    {
        if (textType == null || u64Type == null)
        {
            return;
        }

        // Text-returning standalone routines
        foreach (string name in new[]
                 {
                     "target_os",
                     "target_arch",
                     "builder_version",
                     "build_timestamp"
                 })
        {
            RegisterStandalone(registry: registry, name: name, returnType: textType);
        }

        // build_mode returns BuildMode choice (fall back to S64 if BS not loaded)
        {
            TypeSymbol? buildModeType = registry.LookupType(name: "BuildMode") ?? s64Type;
            if (buildModeType != null)
            {
                RegisterStandalone(registry: registry,
                    name: "build_mode",
                    returnType: buildModeType);
            }
        }

        // ByteSize-returning standalone routines (fall back to U64 if ByteSize not yet loaded)
        {
            TypeSymbol? byteSizeType = registry.LookupType(name: "ByteSize") ?? u64Type;
            foreach (string name in new[]
                     {
                         "page_size",
                         "cache_line",
                         "word_size"
                     })
            {
                RegisterStandalone(registry: registry, name: name, returnType: byteSizeType);
            }
        }
    }

    /// <summary>
    /// Registers a no-parameter readonly synthesized routine if not already defined.
    /// </summary>
    private static void MaybeRegister(TypeSymbol owner, string name, TypeSymbol returnType,
        List<RoutineInfo> existingMethods, TypeRegistry registry)
    {
        if (existingMethods.Any(predicate: m => m.Name == name))
        {
            return;
        }

        registry.RegisterRoutine(routine: new RoutineInfo(name: name)
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
    /// Registers a single-parameter readonly synthesized routine if not already defined.
    /// </summary>
    private static void MaybeRegisterWithParam(TypeSymbol owner, string name, string paramName,
        TypeSymbol paramType, TypeSymbol returnType, List<RoutineInfo> existingMethods,
        TypeRegistry registry)
    {
        if (existingMethods.Any(predicate: m => m.Name == name))
        {
            return;
        }

        registry.RegisterRoutine(routine: new RoutineInfo(name: name)
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
    /// Registers a standalone (no owner type) synthesized routine if not already defined.
    /// </summary>
    private static void RegisterStandalone(TypeRegistry registry, string name,
        TypeSymbol returnType)
    {
        if (registry.LookupRoutine(fullName: name) != null)
        {
            return;
        }

        registry.RegisterRoutine(routine: new RoutineInfo(name: name)
        {
            Kind = RoutineKind.Function,
            OwnerType = null,
            Parameters = [],
            ReturnType = returnType,
            IsFailable = false,
            DeclaredModification = ModificationCategory.Readonly,
            ModificationCategory = ModificationCategory.Readonly,
            Visibility = VisibilityModifier.Open,
            IsSynthesized = true
        });
    }
}
