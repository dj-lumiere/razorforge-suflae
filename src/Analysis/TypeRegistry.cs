using Builder.Modules;

namespace SemanticAnalysis;

using Enums;
using Scopes;
using Symbols;
using Types;
using SyntaxTree;
using TypeInfo = Types.TypeInfo;

/// <summary>
/// Central registry for all type information in a RazorForge/Suflae program.
/// Provides unified lookup for types, routines, member variables, and scopes.
/// </summary>
public sealed class TypeRegistry
{
    /// <summary>The language being built.</summary>
    public Language Language { get; }

    #region Type Storage

    /// <summary>All registered types by their full name.</summary>
    private readonly Dictionary<string, TypeInfo> _types = new();

    /// <summary>Intrinsic types by name.</summary>
    private readonly Dictionary<string, IntrinsicTypeInfo> _intrinsics = new();

    /// <summary>Generic type resolutions cache.</summary>
    private readonly Dictionary<string, TypeInfo> _resolutions = new();

    /// <summary>Whether Core module has been loaded from stdlib.</summary>
    private bool _coreModuleLoaded;

    /// <summary>The stdlib loader instance.</summary>
    private StdlibLoader? _stdlibLoader;

    /// <summary>Path to the stdlib directory.</summary>
    private readonly string? _stdlibPath;

    /// <summary>Gets the stdlib directory path.</summary>
    public string? StdlibPath => _stdlibPath;

    /// <summary>Set of loaded module paths (e.g., "Collections.List", "ErrorHandling.Maybe").</summary>
    private readonly HashSet<string> _loadedModules = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maps module IDs to their effective module names (for import tracking).</summary>
    private readonly Dictionary<string, string> _moduleNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The module resolver for finding module files.</summary>
    private ModuleResolver? _moduleResolver;

    #endregion

    #region Routine Storage

    /// <summary>All registered routines by their full name.</summary>
    private readonly Dictionary<string, RoutineInfo> _routines = new();

    /// <summary>Routines indexed by module-qualified name for unambiguous lookup.</summary>
    private readonly Dictionary<string, RoutineInfo> _routinesByQualifiedName = new();

    /// <summary>Routines indexed by owner type for fast method lookup.</summary>
    private readonly Dictionary<string, List<RoutineInfo>> _routinesByOwner = new();

    /// <summary>Generic routine resolutions cache.</summary>
    private readonly Dictionary<string, RoutineInfo> _routineResolutions = new();

    #endregion

    #region Preset Storage

    /// <summary>Module-level preset constants registered by StdlibLoader (accessible across files).</summary>
    private readonly Dictionary<string, VariableInfo> _presets = new();

    /// <summary>Presets indexed by module-qualified name for unambiguous lookup.</summary>
    private readonly Dictionary<string, VariableInfo> _presetsByQualifiedName = new();

    #endregion

    #region Scope Management

    /// <summary>The global scope.</summary>
    public Scope GlobalScope { get; }

    /// <summary>The current scope during analysis.</summary>
    private Scope _currentScope;

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="TypeRegistry"/> class.
    /// </summary>
    /// <param name="language">The language being built.</param>
    /// <param name="stdlibPath">Optional path to the stdlib directory.</param>
    public TypeRegistry(Language language, string? stdlibPath = null)
    {
        Language = language;
        GlobalScope = new Scope(kind: ScopeKind.Global);
        _currentScope = GlobalScope;
        _stdlibPath = stdlibPath ?? StdlibLoader.GetDefaultStdlibPath();

        // Register intrinsic types (always needed)
        RegisterIntrinsics();

        // Load Core module eagerly - Core types are fundamental to every program
        LoadCoreModule();

        // Register well-known error handling types
        RegisterErrorHandlingTypes();
    }

    #region Initialization

    /// <summary>
    /// Registers all well-known intrinsic types.
    /// </summary>
    private void RegisterIntrinsics()
    {
        foreach (IntrinsicTypeInfo intrinsic in IntrinsicTypeInfo.WellKnown.All)
        {
            _intrinsics[key: intrinsic.Name] = intrinsic;
            _types[key: intrinsic.Name] = intrinsic;
        }

        // Add LLVM type name aliases for convenience in stdlib
        // These allow using @intrinsic.double instead of @intrinsic.f64
        var llvmAliases = new Dictionary<string, IntrinsicTypeInfo>
        {
            ["@intrinsic.half"] = IntrinsicTypeInfo.WellKnown.F16,
            ["@intrinsic.float"] = IntrinsicTypeInfo.WellKnown.F32,
            ["@intrinsic.double"] = IntrinsicTypeInfo.WellKnown.F64,
            ["@intrinsic.fp128"] = IntrinsicTypeInfo.WellKnown.F128
        };

        foreach (var (alias, intrinsic) in llvmAliases)
        {
            _intrinsics[key: alias] = intrinsic;
            _types[key: alias] = intrinsic;
        }
    }

    /// <summary>
    /// Loads the Core module from stdlib files.
    /// Called on-demand when Core types are first used or when import Core is encountered.
    /// </summary>
    public void LoadCoreModule()
    {
        if (_coreModuleLoaded)
        {
            return;
        }

        _coreModuleLoaded = true;

        if (_stdlibPath != null && Directory.Exists(_stdlibPath))
        {
            _stdlibLoader ??= new StdlibLoader(_stdlibPath, Language);
            _stdlibLoader.LoadCoreModule(this);
        }
        else
        {
            string searchPath = _stdlibPath ?? "not specified";
            throw new InvalidOperationException(
                $"Standard library not found at '{searchPath}'. " +
                "Ensure standard/ directory exists and contains the Core module.");
        }
    }

    /// <summary>
    /// Checks if the Core module has been loaded.
    /// </summary>
    public bool IsCoreModuleLoaded => _coreModuleLoaded;

    /// <summary>
    /// Gets the parsed stdlib programs (for code generation).
    /// Returns the programs parsed by the stdlib loader, including routine bodies.
    /// </summary>
    public IReadOnlyList<(Program Program, string FilePath, string Module)> StdlibPrograms
        => _stdlibLoader?.AllLoadedPrograms ?? [];

    /// <summary>
    /// Loads a module on-demand by its import path.
    /// Handles both stdlib modules and project modules.
    /// </summary>
    /// <param name="importPath">The import path (e.g., "Collections/List", "ErrorHandling/Maybe").</param>
    /// <param name="currentFile">The file containing the import statement (for relative import resolution).</param>
    /// <param name="location">Source location for error reporting.</param>
    /// <param name="effectiveModule">The effective module name of the loaded module, or null on failure.</param>
    /// <returns>True if the module was loaded successfully or was already loaded, false on error.</returns>
    public bool LoadModule(string importPath, string currentFile, SourceLocation location,
        out string? effectiveModule)
    {
        // Normalize the import path to a module identifier (e.g., "Collections/List" -> "Collections.List")
        string moduleId = importPath.Replace('/', '.').Replace('\\', '.');

        // Check if already loaded
        if (_loadedModules.Contains(moduleId))
        {
            _moduleNames.TryGetValue(moduleId, out effectiveModule);
            return true;
        }

        // Core module is special - always loaded at startup
        if (moduleId.Equals("Core", StringComparison.OrdinalIgnoreCase) ||
            moduleId.StartsWith("Core.", StringComparison.OrdinalIgnoreCase))
        {
            LoadCoreModule();
            _loadedModules.Add(moduleId);
            effectiveModule = "Core";
            _moduleNames[moduleId] = "Core";
            return true;
        }

        // Ensure stdlib path is available
        if (_stdlibPath == null || !Directory.Exists(_stdlibPath))
        {
            effectiveModule = null;
            return false;
        }

        // Initialize the module resolver if needed
        _moduleResolver ??= new ModuleResolver(
            projectRoot: Path.GetDirectoryName(currentFile) ?? Directory.GetCurrentDirectory(),
            stdlibRoot: _stdlibPath);

        // Resolve the import path to a file
        string? resolvedPath = _moduleResolver.ResolveImport(importPath, currentFile, location);
        if (resolvedPath == null)
        {
            effectiveModule = null;
            return false;
        }

        // Mark as loaded before parsing to prevent infinite recursion
        _loadedModules.Add(moduleId);

        // Load the module using StdlibLoader
        _stdlibLoader ??= new StdlibLoader(_stdlibPath, Language);
        effectiveModule = _stdlibLoader.LoadModule(this, resolvedPath, moduleId);

        if (effectiveModule != null)
        {
            _moduleNames[moduleId] = effectiveModule;
        }

        return effectiveModule != null;
    }

    /// <summary>
    /// Checks if a module has been loaded.
    /// </summary>
    /// <param name="moduleId">The module identifier (e.g., "Collections.List").</param>
    /// <returns>True if the module is loaded, false otherwise.</returns>
    public bool IsModuleLoaded(string moduleId)
    {
        return _loadedModules.Contains(moduleId);
    }

    /// <summary>
    /// Gets all loaded module identifiers.
    /// </summary>
    /// <returns>An enumerable of all loaded module identifiers.</returns>
    public IEnumerable<string> GetLoadedModules()
    {
        return _loadedModules;
    }

    /// <summary>
    /// Registers all well-known error handling types (Maybe, Result, Lookup).
    /// </summary>
    private void RegisterErrorHandlingTypes()
    {
        RegisterType(type: ErrorHandlingTypeInfo.WellKnown.MaybeDefinition);
        RegisterType(type: ErrorHandlingTypeInfo.WellKnown.ResultDefinition);
        RegisterType(type: ErrorHandlingTypeInfo.WellKnown.LookupDefinition);
    }

    #endregion

    #region Type Registration and Lookup

    /// <summary>
    /// Registers a type in the registry.
    /// </summary>
    /// <param name="type">The type to register.</param>
    /// <exception cref="InvalidOperationException">Thrown if the type is already registered.</exception>
    public void RegisterType(TypeInfo type)
    {
        string key = type.FullName;

        if (_types.ContainsKey(key: key))
        {
            throw new InvalidOperationException(message: $"Type '{key}' is already registered.");
        }

        _types[key: key] = type;
    }

    /// <summary>
    /// Updates a type in the registry, replacing it with a new version.
    /// Used for updating immutable type info after additional resolution (e.g., protocol methods).
    /// </summary>
    /// <param name="oldType">The old type to replace.</param>
    /// <param name="newType">The new type to register.</param>
    public void UpdateType(TypeInfo oldType, TypeInfo newType)
    {
        string key = oldType.FullName;
        if (_types.ContainsKey(key: key))
        {
            _types[key: key] = newType;
        }
    }

    /// <summary>
    /// Updates a record type's implemented protocols.
    /// </summary>
    /// <param name="recordName">The name of the record to update.</param>
    /// <param name="protocols">The resolved protocol types.</param>
    public void UpdateRecordProtocols(string recordName, IReadOnlyList<TypeInfo> protocols)
    {
        if (!_types.TryGetValue(key: recordName, value: out TypeInfo? type))
        {
            return;
        }

        if (type is not RecordTypeInfo record)
        {
            return;
        }

        // Create updated record with protocols
        var updatedRecord = new RecordTypeInfo(name: record.Name)
        {
            MemberVariables = record.MemberVariables,
            ImplementedProtocols = protocols,
            GenericParameters = record.GenericParameters,
            GenericConstraints = record.GenericConstraints,
            TypeArguments = record.TypeArguments,
            Visibility = record.Visibility,
            Location = record.Location,
            Module = record.Module,
            BackendType = record.BackendType
        };

        _types[key: recordName] = updatedRecord;
    }

    /// <summary>
    /// Updates a record type with its resolved member variables.
    /// </summary>
    /// <param name="recordName">The name of the record to update.</param>
    /// <param name="memberVariables">The resolved member variables.</param>
    public void UpdateRecordMemberVariables(string recordName, IReadOnlyList<MemberVariableInfo> memberVariables)
    {
        if (!_types.TryGetValue(key: recordName, value: out TypeInfo? type))
        {
            return;
        }

        if (type is not RecordTypeInfo record)
        {
            return;
        }

        // Create updated record with member variables
        var updatedRecord = new RecordTypeInfo(name: record.Name)
        {
            MemberVariables = memberVariables,
            ImplementedProtocols = record.ImplementedProtocols,
            GenericParameters = record.GenericParameters,
            GenericConstraints = record.GenericConstraints,
            TypeArguments = record.TypeArguments,
            Visibility = record.Visibility,
            Location = record.Location,
            Module = record.Module,
            BackendType = record.BackendType
        };

        _types[key: recordName] = updatedRecord;
    }

    /// <summary>
    /// Updates an entity type with its resolved member variables.
    /// </summary>
    /// <param name="entityName">The name of the entity to update.</param>
    /// <param name="memberVariables">The resolved member variables.</param>
    public void UpdateEntityMemberVariables(string entityName, IReadOnlyList<MemberVariableInfo> memberVariables)
    {
        if (!_types.TryGetValue(key: entityName, value: out TypeInfo? type))
        {
            return;
        }

        if (type is not EntityTypeInfo entity)
        {
            return;
        }

        var updatedEntity = new EntityTypeInfo(name: entity.Name)
        {
            MemberVariables = memberVariables,
            ImplementedProtocols = entity.ImplementedProtocols,
            GenericParameters = entity.GenericParameters,
            GenericConstraints = entity.GenericConstraints,
            TypeArguments = entity.TypeArguments,
            Visibility = entity.Visibility,
            Location = entity.Location,
            Module = entity.Module
        };

        _types[key: entityName] = updatedEntity;
    }

    /// <summary>
    /// Updates an entity type's implemented protocols.
    /// </summary>
    /// <param name="entityName">The name of the entity to update.</param>
    /// <param name="protocols">The resolved protocol types.</param>
    public void UpdateEntityProtocols(string entityName, IReadOnlyList<TypeInfo> protocols)
    {
        if (!_types.TryGetValue(key: entityName, value: out TypeInfo? type))
        {
            return;
        }

        if (type is not EntityTypeInfo entity)
        {
            return;
        }

        var updatedEntity = new EntityTypeInfo(name: entity.Name)
        {
            MemberVariables = entity.MemberVariables,
            ImplementedProtocols = protocols,
            GenericParameters = entity.GenericParameters,
            GenericConstraints = entity.GenericConstraints,
            TypeArguments = entity.TypeArguments,
            Visibility = entity.Visibility,
            Location = entity.Location,
            Module = entity.Module
        };

        _types[key: entityName] = updatedEntity;
    }

    /// <summary>
    /// Updates a choice type's implemented protocols.
    /// </summary>
    /// <param name="choiceName">The name of the choice to update.</param>
    /// <param name="protocols">The resolved protocol types.</param>
    public void UpdateChoiceProtocols(string choiceName, IReadOnlyList<TypeInfo> protocols)
    {
        if (!_types.TryGetValue(key: choiceName, value: out TypeInfo? type))
        {
            return;
        }

        if (type is not ChoiceTypeInfo choice)
        {
            return;
        }

        var updatedChoice = new ChoiceTypeInfo(name: choice.Name)
        {
            Cases = choice.Cases,
            ImplementedProtocols = protocols,
            UnderlyingType = choice.UnderlyingType,
            Visibility = choice.Visibility,
            Location = choice.Location,
            Module = choice.Module
        };

        _types[key: choiceName] = updatedChoice;
    }

    /// <summary>
    /// Updates a flags type's implemented protocols.
    /// </summary>
    /// <param name="flagsName">The name of the flags type to update.</param>
    /// <param name="protocols">The resolved protocol types.</param>
    public void UpdateFlagsProtocols(string flagsName, IReadOnlyList<TypeInfo> protocols)
    {
        if (!_types.TryGetValue(key: flagsName, value: out TypeInfo? type))
        {
            return;
        }

        if (type is not FlagsTypeInfo flags)
        {
            return;
        }

        var updatedFlags = new FlagsTypeInfo(name: flags.Name)
        {
            Members = flags.Members,
            ImplementedProtocols = protocols,
            Visibility = flags.Visibility,
            Location = flags.Location,
            Module = flags.Module
        };

        _types[key: flagsName] = updatedFlags;
    }

    /// <summary>
    /// Updates a protocol type's parent protocols.
    /// </summary>
    /// <param name="protocolName">The name of the protocol to update.</param>
    /// <param name="parentProtocols">The resolved parent protocol types.</param>
    public void UpdateProtocolParents(string protocolName, IReadOnlyList<ProtocolTypeInfo> parentProtocols)
    {
        if (!_types.TryGetValue(key: protocolName, value: out TypeInfo? type))
        {
            return;
        }

        if (type is not ProtocolTypeInfo protocol)
        {
            return;
        }

        var updatedProtocol = new ProtocolTypeInfo(name: protocol.Name)
        {
            Methods = protocol.Methods,
            ParentProtocols = parentProtocols,
            GenericParameters = protocol.GenericParameters,
            GenericConstraints = protocol.GenericConstraints,
            TypeArguments = protocol.TypeArguments,
            Visibility = protocol.Visibility,
            Location = protocol.Location,
            Module = protocol.Module
        };

        _types[key: protocolName] = updatedProtocol;
    }

    /// <summary>
    /// Updates a choice type with its resolved cases.
    /// </summary>
    /// <param name="choiceName">The name of the choice to update.</param>
    /// <param name="cases">The resolved choice cases.</param>
    public void UpdateChoiceCases(string choiceName, IReadOnlyList<ChoiceCaseInfo> cases)
    {
        if (!_types.TryGetValue(key: choiceName, value: out TypeInfo? type))
        {
            return;
        }

        if (type is not ChoiceTypeInfo choice)
        {
            return;
        }

        // Create updated choice with cases
        var updatedChoice = new ChoiceTypeInfo(name: choice.Name)
        {
            Cases = cases,
            UnderlyingType = choice.UnderlyingType,
            GenericParameters = choice.GenericParameters,
            GenericConstraints = choice.GenericConstraints,
            Visibility = choice.Visibility,
            Location = choice.Location,
            Module = choice.Module
        };

        _types[key: choiceName] = updatedChoice;
    }

    public void UpdateFlagsMembers(string flagsName, IReadOnlyList<FlagsMemberInfo> members)
    {
        if (!_types.TryGetValue(key: flagsName, value: out TypeInfo? type))
        {
            return;
        }

        if (type is not FlagsTypeInfo flags)
        {
            return;
        }

        var updated = new FlagsTypeInfo(name: flags.Name)
        {
            Members = members,
            Visibility = flags.Visibility,
            Location = flags.Location,
            Module = flags.Module
        };

        _types[key: flagsName] = updated;
    }

    /// <summary>
    /// Looks up a choice case by name across all choice types.
    /// </summary>
    /// <param name="caseName">The name of the choice case to look up.</param>
    /// <returns>A tuple of the choice type and case info if found, null otherwise.</returns>
    public (ChoiceTypeInfo ChoiceType, ChoiceCaseInfo CaseInfo)? LookupChoiceCase(string caseName)
    {
        foreach (TypeInfo type in _types.Values)
        {
            if (type is ChoiceTypeInfo choiceType)
            {
                ChoiceCaseInfo? caseInfo = choiceType.Cases.FirstOrDefault(predicate: c => c.Name == caseName);
                if (caseInfo != null)
                {
                    return (choiceType, caseInfo);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Looks up a type by name.
    /// </summary>
    /// <param name="name">The name of the type to look up.</param>
    /// <returns>The type info if found, null otherwise.</returns>
    public TypeInfo? LookupType(string name)
    {
        // Try exact match first
        if (_types.TryGetValue(key: name, value: out TypeInfo? type))
        {
            return type;
        }

        // Try intrinsic prefix
        if (_intrinsics.TryGetValue(key: name, value: out IntrinsicTypeInfo? intrinsic))
        {
            return intrinsic;
        }

        // Try resolution cache
        if (_resolutions.TryGetValue(key: name, value: out TypeInfo? resolution))
        {
            return resolution;
        }

        // Try Core module prefix (Core types are auto-imported)
        if (!name.Contains('.') && _types.TryGetValue(key: $"Core.{name}", value: out type))
        {
            return type;
        }

        // Try any module prefix (e.g., Collections.SortedSet for bare "SortedSet")
        if (!name.Contains('.'))
        {
            string suffix = $".{name}";
            foreach (var (key, value) in _types)
            {
                if (key.EndsWith(suffix))
                    return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets or creates a resolved generic type.
    /// </summary>
    /// <param name="genericDef">The generic type definition.</param>
    /// <param name="typeArguments">The type arguments for resolution.</param>
    /// <returns>The resolved type (cached if already created).</returns>
    public TypeInfo GetOrCreateResolution(TypeInfo genericDef,
        IReadOnlyList<TypeInfo> typeArguments)
    {
        // Build the resolution name
        string name =
            $"{genericDef.Name}[{string.Join(separator: ", ", values: typeArguments.Select(selector: t => t.Name))}]";

        // Check cache
        if (_resolutions.TryGetValue(key: name, value: out TypeInfo? existing))
        {
            return existing;
        }

        // Create and cache
        TypeInfo resolved = genericDef.CreateInstance(typeArguments: typeArguments);
        _resolutions[key: name] = resolved;

        return resolved;
    }

    /// <summary>
    /// Gets or creates a function type with the given parameter and return types.
    /// Function types are cached by their signature.
    /// </summary>
    /// <param name="parameterTypes">The parameter types.</param>
    /// <param name="returnType">The return type (null for Blank/void).</param>
    /// <param name="isFailable">Whether the function can throw/absent.</param>
    /// <returns>The cached or newly created function type.</returns>
    public RoutineTypeInfo GetOrCreateRoutineType(
        IReadOnlyList<TypeInfo> parameterTypes,
        TypeInfo? returnType,
        bool isFailable = false)
    {
        // Build the signature key
        string paramList = string.Join(", ", parameterTypes.Select(p => p.Name));
        string returnName = returnType?.Name ?? "Blank";
        string failableSuffix = isFailable ? "!" : "";
        string key = $"({paramList}) -> {returnName}{failableSuffix}";

        // Check cache
        if (_resolutions.TryGetValue(key, out TypeInfo? existing) && existing is RoutineTypeInfo routineType)
        {
            return routineType;
        }

        // Create and cache
        var newType = new RoutineTypeInfo(parameterTypes, returnType) { IsFailable = isFailable };
        _resolutions[key] = newType;

        return newType;
    }

    /// <summary>
    /// Gets or creates a tuple type with the given element types.
    /// Tuple types are cached by their element type signature.
    /// </summary>
    /// <param name="elementTypes">The types of each element in the tuple.</param>
    /// <returns>The cached or newly created tuple type.</returns>
    public TupleTypeInfo GetOrCreateTupleType(IReadOnlyList<TypeInfo> elementTypes)
    {
        // Build the cache key
        string typeList = string.Join(separator: ", ", values: elementTypes.Select(selector: t => t.FullName));
        string key = $"Tuple[{typeList}]";

        // Check cache
        if (_resolutions.TryGetValue(key: key, value: out TypeInfo? existing) && existing is TupleTypeInfo tupleType)
        {
            return tupleType;
        }

        // Create and cache
        var newType = new TupleTypeInfo(elementTypes: elementTypes);
        _resolutions[key: key] = newType;

        // Auto-register TupleType.$represent()
        TypeInfo? textType = LookupType("Text");
        if (textType != null)
        {
            RegisterRoutine(new RoutineInfo(name: "$represent")
            {
                Kind = RoutineKind.MemberRoutine,
                OwnerType = newType,
                Parameters = [],
                ReturnType = textType,
                IsFailable = false,
                DeclaredModification = ModificationCategory.Readonly,
                ModificationCategory = ModificationCategory.Readonly,
                Visibility = VisibilityModifier.Open,
                IsSynthesized = true
            });

            RegisterRoutine(new RoutineInfo(name: "$diagnose")
            {
                Kind = RoutineKind.MemberRoutine,
                OwnerType = newType,
                Parameters = [],
                ReturnType = textType,
                IsFailable = false,
                DeclaredModification = ModificationCategory.Readonly,
                ModificationCategory = ModificationCategory.Readonly,
                Visibility = VisibilityModifier.Open,
                IsSynthesized = true
            });
        }

        // Auto-register $eq and $ne (always — element-wise equality)
        TypeInfo? boolType = LookupType("Bool");
        if (boolType != null)
        {
            var youParam = new ParameterInfo(name: "you", type: newType);

            RegisterRoutine(new RoutineInfo(name: "$eq")
            {
                Kind = RoutineKind.MemberRoutine,
                OwnerType = newType,
                Parameters = [youParam],
                ReturnType = boolType,
                IsFailable = false,
                DeclaredModification = ModificationCategory.Readonly,
                ModificationCategory = ModificationCategory.Readonly,
                Visibility = VisibilityModifier.Open,
                IsSynthesized = true
            });

            RegisterRoutine(new RoutineInfo(name: "$ne")
            {
                Kind = RoutineKind.MemberRoutine,
                OwnerType = newType,
                Parameters = [youParam],
                ReturnType = boolType,
                IsFailable = false,
                DeclaredModification = ModificationCategory.Readonly,
                ModificationCategory = ModificationCategory.Readonly,
                Visibility = VisibilityModifier.Open,
                IsSynthesized = true
            });
        }

        // Auto-register $hash if ALL element types support $hash
        TypeInfo? u64Type = LookupType("U64");
        if (u64Type != null && elementTypes.All(et => LookupMethod(et, "$hash") != null))
        {
            RegisterRoutine(new RoutineInfo(name: "$hash")
            {
                Kind = RoutineKind.MemberRoutine,
                OwnerType = newType,
                Parameters = [],
                ReturnType = u64Type,
                IsFailable = false,
                DeclaredModification = ModificationCategory.Readonly,
                ModificationCategory = ModificationCategory.Readonly,
                Visibility = VisibilityModifier.Open,
                IsSynthesized = true
            });
        }

        // Auto-register $cmp + derived operators if ALL element types support $cmp
        TypeInfo? comparisonSignType = LookupType("ComparisonSign");
        if (boolType != null && comparisonSignType != null &&
            elementTypes.All(et => LookupMethod(et, "$cmp") != null))
        {
            var youParam = new ParameterInfo(name: "you", type: newType);

            RegisterRoutine(new RoutineInfo(name: "$cmp")
            {
                Kind = RoutineKind.MemberRoutine,
                OwnerType = newType,
                Parameters = [youParam],
                ReturnType = comparisonSignType,
                IsFailable = false,
                DeclaredModification = ModificationCategory.Readonly,
                ModificationCategory = ModificationCategory.Readonly,
                Visibility = VisibilityModifier.Open,
                IsSynthesized = true
            });

            // Derived: $lt, $le, $gt, $ge
            foreach (string opName in new[] { "$lt", "$le", "$gt", "$ge" })
            {
                RegisterRoutine(new RoutineInfo(name: opName)
                {
                    Kind = RoutineKind.MemberRoutine,
                    OwnerType = newType,
                    Parameters = [youParam],
                    ReturnType = boolType,
                    IsFailable = false,
                    DeclaredModification = ModificationCategory.Readonly,
                    ModificationCategory = ModificationCategory.Readonly,
                    Visibility = VisibilityModifier.Open,
                    IsSynthesized = true
                });
            }
        }

        return newType;
    }

    /// <summary>
    /// Gets or creates a synthesized wrapper type (Hijacked, Inspected, Seized, Viewed).
    /// These are builder-intrinsic types that don't need to be defined in the program.
    /// </summary>
    /// <param name="wrapperName">The name of the wrapper type (e.g., "Hijacked").</param>
    /// <param name="innerType">The type being wrapped.</param>
    /// <param name="isReadOnly">Whether this is a read-only wrapper (Viewed, Inspected).</param>
    /// <returns>The cached or newly created wrapper type.</returns>
    public WrapperTypeInfo GetOrCreateWrapperType(
        string wrapperName,
        TypeInfo innerType,
        bool isReadOnly)
    {
        // Build the cache key
        string key = $"{wrapperName}[{innerType.FullName}]";

        // Check cache
        if (_resolutions.TryGetValue(key: key, value: out TypeInfo? existing) && existing is WrapperTypeInfo wrapperType)
        {
            return wrapperType;
        }

        // Create and cache
        var newType = new WrapperTypeInfo(wrapperName: wrapperName, innerType: innerType, isReadOnly: isReadOnly);
        _resolutions[key: key] = newType;

        return newType;
    }

    /// <summary>
    /// Determines if a type is a value type (has copy semantics).
    /// Value types include: Record, Choice, Tuple, and Variant.
    /// </summary>
    /// <param name="type">The type to check.</param>
    /// <returns>True if the type is a value type, false otherwise.</returns>
    public static bool IsValueType(TypeInfo type)
    {
        return type.Category switch
        {
            TypeCategory.Record => true,
            TypeCategory.Choice => true,
            TypeCategory.Variant => true, // Variants are value types (stack-allocated)
            TypeCategory.Tuple => true,   // Tuples are always inline structs
            _ => false
        };
    }

    /// <summary>
    /// Checks if a type is a single-member-variable record wrapping an intrinsic.
    /// </summary>
    /// <param name="type">The type to check.</param>
    /// <returns>True if the type is a single-member-variable wrapper, false otherwise.</returns>
    public bool IsSingleMemberVariableWrapper(TypeInfo type)
    {
        return type is RecordTypeInfo { IsSingleMemberVariableWrapper: true };
    }

    /// <summary>
    /// Gets the underlying intrinsic type for a single-member-variable wrapper.
    /// </summary>
    /// <param name="type">The type to extract the intrinsic from.</param>
    /// <returns>The underlying intrinsic type, or null if not a single-member-variable wrapper.</returns>
    public IntrinsicTypeInfo? GetUnderlyingIntrinsic(TypeInfo type)
    {
        return type is RecordTypeInfo record ? record.UnderlyingIntrinsic : null;
    }

    /// <summary>
    /// Gets all types of a specific category.
    /// </summary>
    /// <param name="category">The category of types to retrieve.</param>
    /// <returns>An enumerable of all types in the specified category.</returns>
    public IEnumerable<TypeInfo> GetTypesByCategory(TypeCategory category)
    {
        return _types.Values
            .Concat(_resolutions.Values)
            .Where(predicate: t => t.Category == category);
    }

    /// <summary>
    /// Gets all types that can have methods (records, entities, choices, flags).
    /// </summary>
    /// <returns>An enumerable of all types that can have methods.</returns>
    public IEnumerable<TypeInfo> GetTypesWithMethods()
    {
        IEnumerable<TypeInfo> namedTypes = _types.Values.Where(predicate: t =>
            t.Category is TypeCategory.Record or TypeCategory.Entity or
                TypeCategory.Choice or TypeCategory.Flags);

        // Include tuple types from resolutions cache
        IEnumerable<TypeInfo> tupleTypes = _resolutions.Values
            .Where(predicate: t => t is TupleTypeInfo);

        return namedTypes.Concat(tupleTypes);
    }

    /// <summary>
    /// Gets all registered types.
    /// </summary>
    /// <returns>An enumerable of all types.</returns>
    public IEnumerable<TypeInfo> GetAllTypes()
    {
        return _types.Values;
    }

    #endregion

    #region Routine Registration and Lookup

    /// <summary>
    /// Registers a routine in the registry.
    /// </summary>
    /// <param name="routine">The routine to register.</param>
    public void RegisterRoutine(RoutineInfo routine)
    {
        string key = routine.FullName;

        if (!_routines.ContainsKey(key: key))
        {
            // First overload wins for unqualified lookup
            _routines[key: key] = routine;
        }

        // Also register with parameter-based disambiguation for overload resolution
        string overloadKey = GetOverloadKey(routine);
        if (overloadKey != key) // Only store if different from primary key (avoids overwriting resolved entries)
            _routines[key: overloadKey] = routine;

        // Index by module-qualified name for unambiguous lookup
        string qualifiedName = routine.QualifiedName;
        if (qualifiedName != key)
        {
            _routinesByQualifiedName.TryAdd(qualifiedName, routine);
        }

        // Index by owner type for fast method lookup
        if (routine.OwnerType != null)
        {
            string ownerKey = routine.OwnerType.Name;
            if (!_routinesByOwner.TryGetValue(key: ownerKey, value: out List<RoutineInfo>? list))
            {
                list = [];
                _routinesByOwner[key: ownerKey] = list;
            }

            list.Add(item: routine);
        }
    }

    /// <summary>
    /// Builds a disambiguated key for overloaded routines: "Name#ParamType1,ParamType2"
    /// </summary>
    private static string GetOverloadKey(RoutineInfo routine)
    {
        var paramTypes = string.Join(",", routine.Parameters.Select(p => p.Type.Name));
        return $"{routine.FullName}#{paramTypes}";
    }

    /// <summary>
    /// Looks up a routine overload that matches the given argument types.
    /// Falls back to the default (first-registered) overload if no exact match.
    /// </summary>
    public RoutineInfo? LookupRoutineOverload(string fullName, IReadOnlyList<TypeInfo> argTypes)
    {
        // Try exact overload match
        var paramTypeNames = string.Join(",", argTypes.Select(t => t.Name));
        string overloadKey = $"{fullName}#{paramTypeNames}";
        if (_routines.TryGetValue(key: overloadKey, value: out RoutineInfo? overload))
        {
            return overload;
        }

        // Fall back to default lookup
        return LookupRoutine(fullName: fullName);
    }

    /// <summary>
    /// Looks up a routine by its full name.
    /// </summary>
    /// <param name="fullName">The fully qualified name of the routine.</param>
    /// <returns>The routine info if found, null otherwise.</returns>
    public RoutineInfo? LookupRoutine(string fullName)
    {
        if (_routines.TryGetValue(key: fullName, value: out RoutineInfo? routine))
        {
            return routine;
        }

        if (_routineResolutions.TryGetValue(key: fullName, value: out RoutineInfo? resolution))
        {
            return resolution;
        }

        // Fall back to module-qualified name lookup
        if (_routinesByQualifiedName.TryGetValue(fullName, out RoutineInfo? qualified))
        {
            return qualified;
        }

        // Try Core module prefix (Core routines are auto-imported)
        if (!fullName.Contains('.') && _routines.TryGetValue(key: $"Core.{fullName}", value: out routine))
        {
            return routine;
        }

        return null;
    }

    /// <summary>
    /// Looks up a routine by its module-qualified name (e.g., "Core.S8.$add").
    /// </summary>
    public RoutineInfo? LookupRoutineByQualifiedName(string qualifiedName)
    {
        return _routinesByQualifiedName.GetValueOrDefault(qualifiedName);
    }

    /// <summary>
    /// Looks up a routine by its short name (without module prefix).
    /// Used by codegen when the AST has "Console.show" but the registry key is "IO.show".
    /// </summary>
    public RoutineInfo? LookupRoutineByName(string name)
    {
        foreach (var routine in _routines.Values)
        {
            if (routine.Name == name && routine.OwnerType == null)
                return routine;
        }
        return null;
    }

    /// <summary>
    /// Finds a generic overload of a free function by name (e.g., show[T] for "show").
    /// </summary>
    public RoutineInfo? LookupGenericOverload(string name)
    {
        foreach (var routine in _routines.Values)
        {
            if (routine.Name == name && routine.OwnerType == null && routine.IsGenericDefinition)
                return routine;
        }
        return null;
    }

    /// <summary>
    /// Updates a routine with resolved parameters and return type.
    /// </summary>
    /// <param name="routine">The routine to update.</param>
    /// <param name="parameters">The resolved parameters.</param>
    /// <param name="returnType">The resolved return type.</param>
    /// <param name="genericParameters">Updated generic parameters (may include implicit ones from protocol-as-type).</param>
    /// <param name="genericConstraints">Updated generic constraints (may include implicit ones from protocol-as-type).</param>
    public void UpdateRoutine(
        RoutineInfo routine,
        IReadOnlyList<ParameterInfo> parameters,
        TypeInfo? returnType,
        IReadOnlyList<string>? genericParameters,
        IReadOnlyList<GenericConstraintDeclaration>? genericConstraints)
    {
        string key = routine.FullName;
        if (!_routines.ContainsKey(key: key))
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
            Annotations = routine.Annotations,
            CallingConvention = routine.CallingConvention,
            IsVariadic = routine.IsVariadic,
            IsDangerous = routine.IsDangerous,
            Storage = routine.Storage,
            AsyncStatus = routine.AsyncStatus
        };

        _routines[key: key] = updatedRoutine;

        // Update the module-qualified name index
        string qualifiedName = updatedRoutine.QualifiedName;
        if (qualifiedName != key)
        {
            _routinesByQualifiedName[qualifiedName] = updatedRoutine;
        }

        // Update the routines-by-owner index if this is a method
        if (routine.OwnerType != null)
        {
            string ownerKey = routine.OwnerType.Name;
            if (_routinesByOwner.TryGetValue(key: ownerKey, value: out List<RoutineInfo>? list))
            {
                int index = list.FindIndex(match: r => r.FullName == key);
                if (index >= 0)
                {
                    list[index] = updatedRoutine;
                }
            }
        }
    }

    /// <summary>
    /// Looks up a method on a type.
    /// </summary>
    /// <param name="type">The type to search for the method.</param>
    /// <param name="methodName">The name of the method to look up.</param>
    /// <returns>The routine info if found, null otherwise.</returns>
    public RoutineInfo? LookupMethod(TypeInfo type, string methodName)
    {
        // First check the type's own methods
        if (_routinesByOwner.TryGetValue(key: type.Name, value: out List<RoutineInfo>? methods))
        {
            RoutineInfo? method = methods.FirstOrDefault(predicate: m => m.Name == methodName);
            if (method != null)
            {
                return method;
            }
        }

        // For protocol types, check the protocol's method signatures
        if (type is ProtocolTypeInfo proto)
        {
            var protoMethod = proto.Methods.FirstOrDefault(m => m.Name == methodName);
            if (protoMethod != null)
            {
                // Resolve return type: for generic protocols (Iterator[S64]),
                // substitute generic params in the return type (T → S64)
                TypeInfo? resolvedReturn = protoMethod.ReturnType;
                if (resolvedReturn != null && proto.TypeArguments is { Count: > 0 })
                {
                    var genericDef2 = proto.GenericDefinition ?? proto;
                    if (genericDef2.GenericParameters is { Count: > 0 })
                    {
                        // Build substitution map: T → S64, etc.
                        var substitution = new Dictionary<string, TypeInfo>();
                        for (int i = 0; i < genericDef2.GenericParameters.Count
                                      && i < proto.TypeArguments.Count; i++)
                            substitution[genericDef2.GenericParameters[i]] = proto.TypeArguments[i];

                        // Recursively substitute in return type (handles both T and Iterator[T])
                        resolvedReturn = SubstituteTypeInProtocol(resolvedReturn, substitution);
                    }
                }
                // Create a RoutineInfo wrapper so callers get standard ReturnType/OwnerType
                return new RoutineInfo(protoMethod.Name)
                {
                    OwnerType = type,
                    ReturnType = resolvedReturn,
                    IsFailable = protoMethod.IsFailable,
                    AsyncStatus = protoMethod.Name == "$next"
                        ? AsyncStatus.Emitting
                        : AsyncStatus.None
                };
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
                _ => null
            };
            // Fallback: strip type arguments from name to find generic definition
            if (genericDef == null && type.Name.Contains('['))
            {
                string baseName = type.Name[..type.Name.IndexOf('[')];
                genericDef = LookupType(baseName);
                // Try module-qualified name for non-Core types
                if (genericDef == null && !string.IsNullOrEmpty(type.Module))
                    genericDef = LookupType($"{type.Module}.{baseName}");
            }

            if (genericDef != null)
            {
                RoutineInfo? genericMethod = LookupMethod(type: genericDef, methodName: methodName);
                if (genericMethod != null)
                {
                    // Need to resolve the method with the type's type arguments
                    return genericMethod;
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
        // These methods are available on all types
        foreach (var (_, ownerMethods) in _routinesByOwner)
        {
            if (ownerMethods.Count > 0 && ownerMethods[0].OwnerType is GenericParameterTypeInfo)
            {
                RoutineInfo? universalMethod = ownerMethods.FirstOrDefault(predicate: m => m.Name == methodName);
                if (universalMethod != null)
                {
                    return universalMethod;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets all registered routines.
    /// </summary>
    /// <returns>An enumerable of all registered routines.</returns>
    public IEnumerable<RoutineInfo> GetAllRoutines()
    {
        return _routines.Values;
    }

    /// <summary>
    /// Gets all methods for a type.
    /// </summary>
    /// <param name="type">The type to get methods for.</param>
    /// <returns>An enumerable of all methods for the type.</returns>
    public IEnumerable<RoutineInfo> GetMethodsForType(TypeInfo type)
    {
        if (_routinesByOwner.TryGetValue(key: type.Name, value: out List<RoutineInfo>? methods))
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
            $"{genericDef.FullName}[{string.Join(separator: ", ", values: typeArguments.Select(selector: t => t.Name))}]";

        if (_routineResolutions.TryGetValue(key: name, value: out RoutineInfo? existing))
        {
            return existing;
        }

        RoutineInfo resolved = genericDef.CreateInstance(typeArguments: typeArguments);
        _routineResolutions[key: name] = resolved;

        return resolved;
    }

    #endregion

    #region Scope Management

    /// <summary>
    /// Gets the current scope.
    /// </summary>
    public Scope CurrentScope => _currentScope;

    /// <summary>
    /// Enters a new child scope.
    /// </summary>
    /// <param name="kind">The kind of scope to enter.</param>
    /// <param name="name">Optional name for the scope.</param>
    /// <returns>The newly created child scope.</returns>
    public Scope EnterScope(ScopeKind kind, string? name = null)
    {
        _currentScope = _currentScope.CreateChildScope(kind: kind, name: name);
        return _currentScope;
    }

    /// <summary>
    /// Exits the current scope and returns to the parent.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if attempting to exit the global scope.</exception>
    public void ExitScope()
    {
        _currentScope = _currentScope.Parent ?? throw new InvalidOperationException(message: "Cannot exit the global scope.");
    }

    /// <summary>
    /// Declares a variable in the current scope.
    /// </summary>
    /// <param name="name">The name of the variable.</param>
    /// <param name="type">The type of the variable.</param>
    /// <param name="isPreset">Whether this is a preset (build-time constant).</param>
    /// <returns>True if successful, false if already declared in this scope.</returns>
    public bool DeclareVariable(string name, TypeInfo type, bool isPreset = false)
    {
        var variable = new VariableInfo(name: name, type: type)
        {
            IsModifiable = !isPreset,
            IsPreset = isPreset
        };

        return _currentScope.DeclareVariable(variable: variable);
    }

    /// <summary>
    /// Registers a module-level preset constant (from StdlibLoader).
    /// Presets registered here are accessible across files within the same module.
    /// </summary>
    /// <param name="name">The preset name.</param>
    /// <param name="type">The type of the preset.</param>
    /// <param name="module">The module this preset belongs to.</param>
    public void RegisterPreset(string name, TypeInfo type, string? module = null)
    {
        var variable = new VariableInfo(name: name, type: type)
        {
            IsModifiable = false,
            IsPreset = true,
            Module = module
        };

        _presets[name] = variable;

        // Index by module-qualified name for unambiguous lookup
        string qualifiedName = variable.QualifiedName;
        if (qualifiedName != name)
        {
            _presetsByQualifiedName.TryAdd(qualifiedName, variable);
        }
    }

    /// <summary>
    /// Looks up a variable by name in the current scope chain,
    /// falling back to module-level presets if not found in local scopes.
    /// </summary>
    /// <param name="name">The name of the variable to look up.</param>
    /// <returns>The variable info if found, null otherwise.</returns>
    public VariableInfo? LookupVariable(string name)
    {
        return _currentScope.LookupVariable(name: name)
            ?? _presets.GetValueOrDefault(name)
            ?? _presetsByQualifiedName.GetValueOrDefault(name);
    }

    /// <summary>
    /// Looks up a preset by its module-qualified name (e.g., "Core.S8_MIN").
    /// </summary>
    public VariableInfo? LookupPresetByQualifiedName(string qualifiedName)
    {
        return _presetsByQualifiedName.GetValueOrDefault(qualifiedName);
    }

    /// <summary>
    /// Narrows the type of a variable in the current scope.
    /// Used for type narrowing after pattern checks (e.g., after "unless x is None").
    /// </summary>
    /// <param name="name">The variable name to narrow.</param>
    /// <param name="narrowedType">The narrowed type.</param>
    public void NarrowVariable(string name, TypeInfo narrowedType)
    {
        _currentScope.NarrowVariable(name: name, narrowedType: narrowedType);
    }

    /// <summary>
    /// Gets the narrowed type for a variable in the current scope chain.
    /// </summary>
    /// <param name="name">The variable name to look up.</param>
    /// <returns>The narrowed type if found, null otherwise.</returns>
    public TypeInfo? GetNarrowedType(string name)
    {
        return _currentScope.GetNarrowedType(name: name);
    }

    /// <summary>
    /// Gets all variables visible in the current scope as a dictionary.
    /// Used for lambda capture analysis to track which variables from enclosing scopes are captured.
    /// </summary>
    /// <returns>A dictionary of variable names to their info.</returns>
    public IReadOnlyDictionary<string, VariableInfo> GetAllVariablesInScope()
    {
        var variables = new Dictionary<string, VariableInfo>();

        foreach (VariableInfo variable in _currentScope.GetAllVisibleVariables())
        {
            variables.TryAdd(key: variable.Name, value: variable);
        }

        return variables;
    }

    /// <summary>
    /// Gets variables from local (function-level) scopes only, stopping at Global/Module/Type boundaries.
    /// Variables from these scopes are truly "captured" by lambdas and require 'given' declarations.
    /// </summary>
    public IReadOnlyDictionary<string, VariableInfo> GetLocalScopeVariables()
    {
        var variables = new Dictionary<string, VariableInfo>();

        Scope? current = _currentScope;
        while (current != null)
        {
            // Stop at non-local scope boundaries
            if (current.Kind is ScopeKind.Global or ScopeKind.Module or ScopeKind.Type)
            {
                break;
            }

            foreach (VariableInfo variable in current.GetLocalVariables())
            {
                variables.TryAdd(key: variable.Name, value: variable);
            }

            current = current.Parent;
        }

        return variables;
    }

    #endregion

    #region Language-Specific Validation

    /// <summary>
    /// Validates that a type is allowed for the current language.
    /// </summary>
    /// <param name="type">The type to validate.</param>
    /// <returns>True if the type is allowed for the current language, false otherwise.</returns>
    public bool IsTypeAllowedForLanguage(TypeInfo type)
    {
        // Memory wrapper types are RazorForge only
        if (IsMemoryWrapperType(typeName: type.Name) && Language == Language.Suflae)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if a type name is a RazorForge-specific memory wrapper type.
    /// </summary>
    /// <param name="typeName">The name of the type to check.</param>
    /// <returns>True if the type is a memory wrapper, false otherwise.</returns>
    private static bool IsMemoryWrapperType(string typeName)
    {
        return typeName is "Viewed" or "Hijacked" or "Inspected" or "Seized" or "Snatched" or "Shared" or "Tracked";
    }

    #endregion

    #region Protocol Type Substitution

    /// <summary>
    /// Recursively substitutes generic type parameters in a type.
    /// Handles both direct parameters (T → S64) and composite types (Iterator[T] → Iterator[S64]).
    /// </summary>
    private TypeInfo SubstituteTypeInProtocol(TypeInfo type, Dictionary<string, TypeInfo> substitution)
    {
        // Direct substitution for generic parameters
        if (type is GenericParameterTypeInfo && substitution.TryGetValue(type.Name, out TypeInfo? sub))
            return sub;

        // Recursive substitution in type arguments
        if (type.TypeArguments is not { Count: > 0 })
            return type;

        bool anyChanged = false;
        var newArgs = new List<TypeInfo>();
        foreach (var arg in type.TypeArguments)
        {
            var resolved = SubstituteTypeInProtocol(arg, substitution);
            newArgs.Add(resolved);
            if (!ReferenceEquals(resolved, arg)) anyChanged = true;
        }
        if (!anyChanged) return type;

        // Get the generic definition and create a new instance with substituted args
        TypeInfo? genericDef = type switch
        {
            EntityTypeInfo e => e.GenericDefinition,
            RecordTypeInfo r => r.GenericDefinition,
            ProtocolTypeInfo p => p.GenericDefinition,
            _ => null
        };

        if (genericDef != null)
            return GetOrCreateResolution(genericDef, newArgs);

        return type;
    }

    #endregion
}
