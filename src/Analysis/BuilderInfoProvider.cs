namespace SemanticAnalysis;

using Enums;
using Symbols;
using Types;
using TypeSymbol = Types.TypeInfo;

/// <summary>
/// Central authority for BuilderService routine registration and import-gating.
/// Provides static name sets for identifying BS routines and methods to register
/// them on types, as standalone functions, and as module-level routines.
/// </summary>
public static class BuilderInfoProvider
{
    /// <summary>Per-type BuilderService member routines (require 'import BuilderService').</summary>
    private static readonly HashSet<string> PerTypeRoutines = new(StringComparer.Ordinal)
    {
        "type_name", "type_kind", "type_id", "module_name",
        "is_generic", "generic_args",
        "member_variable_count", "member_variable_info",
        "all_member_variables", "open_member_variables",
        "protocols", "protocol_info",
        "routine_names", "routine_info",
        "annotations",
        "data_size",
        "origin_module", "dependencies",
        "member_type_id",
        "var_name"
    };

    /// <summary>Standalone BuilderService routines (require 'import BuilderService').</summary>
    private static readonly HashSet<string> StandaloneRoutines = new(StringComparer.Ordinal)
    {
        "source_file", "source_line", "source_column",
        "source_routine", "source_module", "source_text",
        "caller_file", "caller_line", "caller_routine",
        // Platform/build info (formerly module-level, now standalone)
        "target_os", "target_arch", "builder_version",
        "build_mode", "build_timestamp",
        "page_size", "cache_line", "word_size"
    };

    /// <summary>Returns true if the routine name is a per-type BuilderService member routine.</summary>
    public static bool IsBuilderServiceRoutine(string name) => PerTypeRoutines.Contains(name);

    /// <summary>Returns true if the routine name is a standalone BuilderService routine.</summary>
    public static bool IsBuilderServiceStandalone(string name) => StandaloneRoutines.Contains(name);

    /// <summary>
    /// Registers all per-type BuilderService metadata routines on a given type.
    /// </summary>
    public static void RegisterRoutinesOnType(
        TypeSymbol type,
        List<RoutineInfo> existingMethods,
        TypeRegistry registry,
        TypeSymbol? textType,
        TypeSymbol? boolType,
        TypeSymbol? u64Type,
        TypeSymbol? s64Type,
        TypeSymbol? listTextType,
        TypeSymbol? listFieldInfoType,
        TypeSymbol? listProtocolInfoType,
        TypeSymbol? listRoutineInfoType,
        TypeSymbol? dictTextDataType)
    {
        // Text-returning routines
        if (textType != null)
        {
            MaybeRegister(type, "type_name", textType, existingMethods, registry);
            MaybeRegister(type, "module_name", textType, existingMethods, registry);
            MaybeRegister(type, "origin_module", textType, existingMethods, registry);
            MaybeRegister(type, "var_name", textType, existingMethods, registry);
        }

        // U64-returning routines
        if (u64Type != null)
        {
            MaybeRegister(type, "type_id", u64Type, existingMethods, registry);
            // data_size is RazorForge-only (used by collection internals via Snatched pointer arithmetic)
            if (registry.Language == Language.RazorForge)
                MaybeRegister(type, "data_size", u64Type, existingMethods, registry);
        }

        // S64-returning routines
        if (s64Type != null)
        {
            MaybeRegister(type, "member_variable_count", s64Type, existingMethods, registry);
        }

        // type_kind returns S64 (choice ordinal)
        if (s64Type != null)
        {
            MaybeRegister(type, "type_kind", s64Type, existingMethods, registry);
        }

        // Bool-returning routines
        if (boolType != null)
        {
            MaybeRegister(type, "is_generic", boolType, existingMethods, registry);
        }

        // List[Text]-returning routines
        if (listTextType != null)
        {
            MaybeRegister(type, "protocols", listTextType, existingMethods, registry);
            MaybeRegister(type, "routine_names", listTextType, existingMethods, registry);
            MaybeRegister(type, "annotations", listTextType, existingMethods, registry);
            MaybeRegister(type, "generic_args", listTextType, existingMethods, registry);
            MaybeRegister(type, "dependencies", listTextType, existingMethods, registry);
        }

        // List[FieldInfo]-returning routines
        if (listFieldInfoType != null)
        {
            MaybeRegister(type, "member_variable_info", listFieldInfoType, existingMethods, registry);
        }

        // List[ProtocolInfo]-returning routines
        if (listProtocolInfoType != null)
        {
            MaybeRegister(type, "protocol_info", listProtocolInfoType, existingMethods, registry);
        }

        // List[RoutineInfo]-returning routines
        if (listRoutineInfoType != null)
        {
            MaybeRegister(type, "routine_info", listRoutineInfoType, existingMethods, registry);
        }

        // Dict[Text, Data]-returning routines
        if (dictTextDataType != null)
        {
            MaybeRegister(type, "all_member_variables", dictTextDataType, existingMethods, registry);
            MaybeRegister(type, "open_member_variables", dictTextDataType, existingMethods, registry);
        }

        // member_type_id(member_name: Text) -> U64
        if (u64Type != null && textType != null)
        {
            MaybeRegisterWithParam(type, "member_type_id", "member_name", textType, u64Type,
                existingMethods, registry);
        }
    }

    /// <summary>
    /// Registers standalone BuilderService routines (source_*, caller_*).
    /// </summary>
    public static void RegisterStandaloneRoutines(
        TypeRegistry registry,
        TypeSymbol? textType,
        TypeSymbol? s64Type)
    {
        if (textType != null)
        {
            foreach (string name in new[]
                     {
                         "source_file", "source_routine", "source_module",
                         "source_text",
                         "caller_file", "caller_routine"
                     })
            {
                RegisterStandalone(registry, name, textType);
            }
        }

        if (s64Type != null)
        {
            foreach (string name in new[] { "source_line", "source_column", "caller_line" })
            {
                RegisterStandalone(registry, name, s64Type);
            }
        }
    }

    /// <summary>
    /// Registers the BuilderService platform/build info routines as standalone functions.
    /// </summary>
    public static void RegisterModuleRoutines(
        TypeRegistry registry,
        TypeSymbol? textType,
        TypeSymbol? u64Type,
        TypeSymbol? s64Type)
    {
        if (textType == null || u64Type == null)
            return;

        // Text-returning standalone routines
        foreach (string name in new[] { "target_os", "target_arch", "builder_version", "build_timestamp" })
            RegisterStandalone(registry, name, textType);

        // build_mode returns S64 (choice ordinal)
        if (s64Type != null)
            RegisterStandalone(registry, "build_mode", s64Type);

        // U64-returning standalone routines
        foreach (string name in new[] { "page_size", "cache_line", "word_size" })
            RegisterStandalone(registry, name, u64Type);
    }

    /// <summary>
    /// Registers a no-parameter readonly synthesized routine if not already defined.
    /// </summary>
    private static void MaybeRegister(
        TypeSymbol owner, string name, TypeSymbol returnType,
        List<RoutineInfo> existingMethods, TypeRegistry registry)
    {
        if (existingMethods.Any(predicate: m => m.Name == name))
            return;

        registry.RegisterRoutine(routine: new RoutineInfo(name: name)
        {
            Kind = Enums.RoutineKind.MemberRoutine,
            OwnerType = owner,
            Parameters = [],
            ReturnType = returnType,
            IsFailable = false,
            DeclaredModification = Enums.ModificationCategory.Readonly,
            ModificationCategory = Enums.ModificationCategory.Readonly,
            Visibility = VisibilityModifier.Open,
            IsSynthesized = true
        });
    }

    /// <summary>
    /// Registers a single-parameter readonly synthesized routine if not already defined.
    /// </summary>
    private static void MaybeRegisterWithParam(
        TypeSymbol owner, string name, string paramName, TypeSymbol paramType,
        TypeSymbol returnType, List<RoutineInfo> existingMethods, TypeRegistry registry)
    {
        if (existingMethods.Any(predicate: m => m.Name == name))
            return;

        registry.RegisterRoutine(routine: new RoutineInfo(name: name)
        {
            Kind = Enums.RoutineKind.MemberRoutine,
            OwnerType = owner,
            Parameters = [new ParameterInfo(name: paramName, type: paramType)],
            ReturnType = returnType,
            IsFailable = false,
            DeclaredModification = Enums.ModificationCategory.Readonly,
            ModificationCategory = Enums.ModificationCategory.Readonly,
            Visibility = VisibilityModifier.Open,
            IsSynthesized = true
        });
    }

    /// <summary>
    /// Registers a standalone (no owner type) synthesized routine if not already defined.
    /// </summary>
    private static void RegisterStandalone(TypeRegistry registry, string name, TypeSymbol returnType)
    {
        if (registry.LookupRoutine(fullName: name) != null)
            return;

        registry.RegisterRoutine(routine: new RoutineInfo(name: name)
        {
            Kind = Enums.RoutineKind.Function,
            OwnerType = null,
            Parameters = [],
            ReturnType = returnType,
            IsFailable = false,
            DeclaredModification = Enums.ModificationCategory.Readonly,
            ModificationCategory = Enums.ModificationCategory.Readonly,
            Visibility = VisibilityModifier.Open,
            IsSynthesized = true
        });
    }
}
