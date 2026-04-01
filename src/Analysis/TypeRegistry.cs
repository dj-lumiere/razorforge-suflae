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
public sealed partial class TypeRegistry
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
    private readonly HashSet<string> _loadedModules =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>Maps module IDs to their effective module names (for import tracking).</summary>
    private readonly Dictionary<string, string> _moduleNames =
        new(comparer: StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Methods on GenericParameterTypeInfo owners, indexed by method name for O(1) universal lookup.</summary>
    private readonly Dictionary<string, RoutineInfo> _universalMethods = new();

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
            [key: "@intrinsic.half"] = IntrinsicTypeInfo.WellKnown.F16,
            [key: "@intrinsic.float"] = IntrinsicTypeInfo.WellKnown.F32,
            [key: "@intrinsic.double"] = IntrinsicTypeInfo.WellKnown.F64,
            [key: "@intrinsic.fp128"] = IntrinsicTypeInfo.WellKnown.F128
        };

        foreach ((string alias, IntrinsicTypeInfo intrinsic) in llvmAliases)
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

        if (_stdlibPath != null && Directory.Exists(path: _stdlibPath))
        {
            _stdlibLoader ??= new StdlibLoader(stdlibRoot: _stdlibPath, language: Language);
            _stdlibLoader.LoadCoreModule(registry: this);
        }
        else
        {
            string searchPath = _stdlibPath ?? "not specified";
            throw new InvalidOperationException(
                message: $"Standard library not found at '{searchPath}'. " +
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
    public IReadOnlyList<(Program Program, string FilePath, string Module)> StdlibPrograms =>
        _stdlibLoader?.AllLoadedPrograms ?? [];

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
        string moduleId = importPath.Replace(oldChar: '/', newChar: '.')
                                    .Replace(oldChar: '\\', newChar: '.');

        // Check if already loaded
        if (_loadedModules.Contains(item: moduleId))
        {
            _moduleNames.TryGetValue(key: moduleId, value: out effectiveModule);
            return true;
        }

        // Core module is special - always loaded at startup
        if (moduleId.Equals(value: "Core", comparisonType: StringComparison.OrdinalIgnoreCase) ||
            moduleId.StartsWith(value: "Core.",
                comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            LoadCoreModule();
            _loadedModules.Add(item: moduleId);
            effectiveModule = "Core";
            _moduleNames[key: moduleId] = "Core";
            return true;
        }

        // Ensure stdlib path is available
        if (_stdlibPath == null || !Directory.Exists(path: _stdlibPath))
        {
            effectiveModule = null;
            return false;
        }

        // Initialize the module resolver if needed
        _moduleResolver ??= new ModuleResolver(
            projectRoot: Path.GetDirectoryName(path: currentFile) ??
                         Directory.GetCurrentDirectory(),
            stdlibRoot: _stdlibPath);

        // Resolve the import path to a file
        string? resolvedPath = _moduleResolver.ResolveImport(importPath: importPath,
            currentFile: currentFile,
            location: location);
        if (resolvedPath == null)
        {
            effectiveModule = null;
            return false;
        }

        // Mark as loaded before parsing to prevent infinite recursion
        _loadedModules.Add(item: moduleId);

        // Load the module using StdlibLoader
        _stdlibLoader ??= new StdlibLoader(stdlibRoot: _stdlibPath, language: Language);
        effectiveModule =
            _stdlibLoader.LoadModule(registry: this, filePath: resolvedPath, moduleId: moduleId);

        if (effectiveModule != null)
        {
            _moduleNames[key: moduleId] = effectiveModule;
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
        return _loadedModules.Contains(item: moduleId);
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
            GenericDefinition = record.GenericDefinition,
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
    public void UpdateRecordMemberVariables(string recordName,
        IReadOnlyList<MemberVariableInfo> memberVariables)
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
            GenericDefinition = record.GenericDefinition,
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
    public void UpdateEntityMemberVariables(string entityName,
        IReadOnlyList<MemberVariableInfo> memberVariables)
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
            GenericDefinition = entity.GenericDefinition,
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
            GenericDefinition = entity.GenericDefinition,
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
    public void UpdateProtocolParents(string protocolName,
        IReadOnlyList<ProtocolTypeInfo> parentProtocols)
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
            GenericDefinition = protocol.GenericDefinition,
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
    /// <summary>
    /// Updates the declared member set for an already-registered flags type.
    /// </summary>

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
                ChoiceCaseInfo? caseInfo =
                    choiceType.Cases.FirstOrDefault(predicate: c => c.Name == caseName);
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
        if (!name.Contains(value: '.') && _types.TryGetValue(key: $"Core.{name}", value: out type))
        {
            return type;
        }

        // Try any module prefix (e.g., Collections.SortedSet for bare "SortedSet")
        if (!name.Contains(value: '.'))
        {
            string suffix = $".{name}";
            foreach ((string key, TypeInfo value) in _types)
            {
                if (key.EndsWith(value: suffix))
                {
                    return value;
                }
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
    public RoutineTypeInfo GetOrCreateRoutineType(IReadOnlyList<TypeInfo> parameterTypes,
        TypeInfo? returnType, bool isFailable = false)
    {
        // Build the signature key
        string paramList = string.Join(separator: ", ",
            values: parameterTypes.Select(selector: p => p.Name));
        string returnName = returnType?.Name ?? "Blank";
        string failableSuffix = isFailable
            ? "!"
            : "";
        string key = $"({paramList}) -> {returnName}{failableSuffix}";

        // Check cache
        if (_resolutions.TryGetValue(key: key, value: out TypeInfo? existing) &&
            existing is RoutineTypeInfo routineType)
        {
            return routineType;
        }

        // Create and cache
        var newType =
            new RoutineTypeInfo(parameterTypes: parameterTypes, returnType: returnType)
            {
                IsFailable = isFailable
            };
        _resolutions[key: key] = newType;

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
        string typeList = string.Join(separator: ", ",
            values: elementTypes.Select(selector: t => t.FullName));
        string key = $"Tuple[{typeList}]";

        // Check cache
        if (_resolutions.TryGetValue(key: key, value: out TypeInfo? existing) &&
            existing is TupleTypeInfo tupleType)
        {
            return tupleType;
        }

        // Create and cache
        var newType = new TupleTypeInfo(elementTypes: elementTypes);
        _resolutions[key: key] = newType;

        // Auto-register TupleType.$represent()
        TypeInfo? textType = LookupType(name: "Text");
        if (textType != null)
        {
            RegisterRoutine(routine: new RoutineInfo(name: "$represent")
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

            RegisterRoutine(routine: new RoutineInfo(name: "$diagnose")
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
        TypeInfo? boolType = LookupType(name: "Bool");
        if (boolType != null)
        {
            var youParam = new ParameterInfo(name: "you", type: newType);

            RegisterRoutine(routine: new RoutineInfo(name: "$eq")
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

            RegisterRoutine(routine: new RoutineInfo(name: "$ne")
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
        TypeInfo? u64Type = LookupType(name: "U64");
        if (u64Type != null &&
            elementTypes.All(predicate: et => LookupMethod(type: et, methodName: "$hash") != null))
        {
            RegisterRoutine(routine: new RoutineInfo(name: "$hash")
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
        TypeInfo? comparisonSignType = LookupType(name: "ComparisonSign");
        if (boolType != null && comparisonSignType != null &&
            elementTypes.All(predicate: et => LookupMethod(type: et, methodName: "$cmp") != null))
        {
            var youParam = new ParameterInfo(name: "you", type: newType);

            RegisterRoutine(routine: new RoutineInfo(name: "$cmp")
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
            foreach (string opName in new[]
                     {
                         "$lt",
                         "$le",
                         "$gt",
                         "$ge"
                     })
            {
                RegisterRoutine(routine: new RoutineInfo(name: opName)
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
    public WrapperTypeInfo GetOrCreateWrapperType(string wrapperName, TypeInfo innerType,
        bool isReadOnly)
    {
        // Build the cache key
        string key = $"{wrapperName}[{innerType.FullName}]";

        // Check cache
        if (_resolutions.TryGetValue(key: key, value: out TypeInfo? existing) &&
            existing is WrapperTypeInfo wrapperType)
        {
            return wrapperType;
        }

        // Create and cache
        var newType = new WrapperTypeInfo(wrapperName: wrapperName,
            innerType: innerType,
            isReadOnly: isReadOnly);
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
            TypeCategory.Tuple => true, // Tuples are always inline structs
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
        return type is RecordTypeInfo record
            ? record.UnderlyingIntrinsic
            : null;
    }

    /// <summary>
    /// Gets all types of a specific category.
    /// </summary>
    /// <param name="category">The category of types to retrieve.</param>
    /// <returns>An enumerable of all types in the specified category.</returns>
    public IEnumerable<TypeInfo> GetTypesByCategory(TypeCategory category)
    {
        return _types.Values
                     .Concat(second: _resolutions.Values)
                     .Where(predicate: t => t.Category == category);
    }

    /// <summary>
    /// Gets all types that can have methods (records, entities, choices, flags).
    /// </summary>
    /// <returns>An enumerable of all types that can have methods.</returns>
    public IEnumerable<TypeInfo> GetTypesWithMethods()
    {
        IEnumerable<TypeInfo> namedTypes = _types.Values.Where(predicate: t =>
            t.Category is TypeCategory.Record or TypeCategory.Entity or TypeCategory.Choice
                or TypeCategory.Flags);

        // Include tuple types from resolutions cache
        IEnumerable<TypeInfo> tupleTypes =
            _resolutions.Values.Where(predicate: t => t is TupleTypeInfo);

        return namedTypes.Concat(second: tupleTypes);
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

    // Routine registration and lookup methods are in TypeRegistry.MethodLookup.cs

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
        _currentScope = _currentScope.Parent ??
                        throw new InvalidOperationException(
                            message: "Cannot exit the global scope.");
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
            IsModifiable = !isPreset, IsPreset = isPreset
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
            IsModifiable = false, IsPreset = true, Module = module
        };

        _presets[key: name] = variable;

        // Index by module-qualified name for unambiguous lookup
        string qualifiedName = variable.QualifiedName;
        if (qualifiedName != name)
        {
            _presetsByQualifiedName.TryAdd(key: qualifiedName, value: variable);
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
        return _currentScope.LookupVariable(name: name) ?? _presets.GetValueOrDefault(key: name) ??
            _presetsByQualifiedName.GetValueOrDefault(key: name);
    }

    /// <summary>
    /// Looks up a preset by its module-qualified name (e.g., "Core.S8_MIN").
    /// </summary>
    public VariableInfo? LookupPresetByQualifiedName(string qualifiedName)
    {
        return _presetsByQualifiedName.GetValueOrDefault(key: qualifiedName);
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
}
