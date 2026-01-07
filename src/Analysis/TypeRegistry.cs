namespace Compilers.Analysis;

using Compilers.Analysis.Enums;
using Compilers.Analysis.Modules;
using Compilers.Analysis.Scopes;
using Compilers.Analysis.Symbols;
using Compilers.Analysis.Types;
using Compilers.Shared.AST;
using TypeInfo = Compilers.Analysis.Types.TypeInfo;

/// <summary>
/// Central registry for all type information in a RazorForge/Suflae program.
/// Provides unified lookup for types, routines, fields, and scopes.
/// </summary>
public sealed class TypeRegistry
{
    /// <summary>The language being compiled.</summary>
    public Language Language { get; }

    #region Type Storage

    /// <summary>All registered types by their full name.</summary>
    private readonly Dictionary<string, TypeInfo> _types = new();

    /// <summary>Intrinsic types by name.</summary>
    private readonly Dictionary<string, IntrinsicTypeInfo> _intrinsics = new();

    /// <summary>Generic type instantiations cache.</summary>
    private readonly Dictionary<string, TypeInfo> _instantiations = new();

    /// <summary>Whether Core namespace has been loaded from stdlib.</summary>
    private bool _coreNamespaceLoaded;

    /// <summary>The stdlib loader instance.</summary>
    private StdlibLoader? _stdlibLoader;

    /// <summary>Path to the stdlib directory.</summary>
    private readonly string? _stdlibPath;

    /// <summary>Set of loaded module paths (e.g., "Collections.List", "ErrorHandling.Maybe").</summary>
    private readonly HashSet<string> _loadedModules = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The module resolver for finding module files.</summary>
    private ModuleResolver? _moduleResolver;

    #endregion

    #region Routine Storage

    /// <summary>All registered routines by their full name.</summary>
    private readonly Dictionary<string, RoutineInfo> _routines = new();

    /// <summary>Routines indexed by owner type for fast method lookup.</summary>
    private readonly Dictionary<string, List<RoutineInfo>> _routinesByOwner = new();

    /// <summary>Generic routine instantiations cache.</summary>
    private readonly Dictionary<string, RoutineInfo> _routineInstantiations = new();

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
    /// <param name="language">The language being compiled.</param>
    /// <param name="stdlibPath">Optional path to the stdlib directory.</param>
    public TypeRegistry(Language language, string? stdlibPath = null)
    {
        Language = language;
        GlobalScope = new Scope(kind: ScopeKind.Global);
        _currentScope = GlobalScope;
        _stdlibPath = stdlibPath ?? StdlibLoader.GetDefaultStdlibPath();

        // Register intrinsic types (always needed)
        RegisterIntrinsics();

        // Load Core namespace eagerly - Core types are fundamental to every program
        LoadCoreNamespace();

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
    /// Loads the Core namespace from stdlib files.
    /// Called on-demand when Core types are first used or when import Core is encountered.
    /// </summary>
    public void LoadCoreNamespace()
    {
        if (_coreNamespaceLoaded)
        {
            return;
        }

        _coreNamespaceLoaded = true;

        if (_stdlibPath != null && Directory.Exists(_stdlibPath))
        {
            _stdlibLoader ??= new StdlibLoader(_stdlibPath);
            _stdlibLoader.LoadCoreNamespace(this);
        }
        else
        {
            // Fallback: register minimal Core types if stdlib is not available
            RegisterFallbackCoreTypes();
        }
    }

    /// <summary>
    /// Checks if the Core namespace has been loaded.
    /// </summary>
    public bool IsCoreNamespaceLoaded => _coreNamespaceLoaded;

    /// <summary>
    /// Gets the parsed stdlib programs (for code generation).
    /// Returns the programs parsed by the stdlib loader, including routine bodies.
    /// </summary>
    public IReadOnlyList<(Program Program, string FilePath)> StdlibPrograms
        => _stdlibLoader?.ParsedPrograms ?? Array.Empty<(Program, string)>();

    /// <summary>
    /// Loads a module on-demand by its import path.
    /// Handles both stdlib modules and project modules.
    /// </summary>
    /// <param name="importPath">The import path (e.g., "Collections/List", "ErrorHandling/Maybe").</param>
    /// <param name="currentFile">The file containing the import statement (for relative import resolution).</param>
    /// <param name="location">Source location for error reporting.</param>
    /// <returns>True if the module was loaded successfully or was already loaded, false on error.</returns>
    public bool LoadModule(string importPath, string currentFile, SourceLocation location)
    {
        // Normalize the import path to a module identifier (e.g., "Collections/List" -> "Collections.List")
        string moduleId = importPath.Replace('/', '.').Replace('\\', '.');

        // Check if already loaded
        if (_loadedModules.Contains(moduleId))
        {
            return true;
        }

        // Core namespace is special - always loaded at startup
        if (moduleId.Equals("Core", StringComparison.OrdinalIgnoreCase) ||
            moduleId.StartsWith("Core.", StringComparison.OrdinalIgnoreCase) ||
            moduleId.Equals("NativeDataTypes", StringComparison.OrdinalIgnoreCase) ||
            moduleId.StartsWith("NativeDataTypes.", StringComparison.OrdinalIgnoreCase))
        {
            LoadCoreNamespace();
            _loadedModules.Add(moduleId);
            return true;
        }

        // Ensure stdlib path is available
        if (_stdlibPath == null || !Directory.Exists(_stdlibPath))
        {
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
            return false;
        }

        // Mark as loaded before parsing to prevent infinite recursion
        _loadedModules.Add(moduleId);

        // Load the module using StdlibLoader
        _stdlibLoader ??= new StdlibLoader(_stdlibPath);
        return _stdlibLoader.LoadModule(this, resolvedPath, moduleId);
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
    /// Registers minimal fallback Core types when stdlib is not available.
    /// This is only used for testing or when stdlib cannot be found.
    /// </summary>
    private void RegisterFallbackCoreTypes()
    {
        // Signed integers
        RegisterFallbackRecordType(name: "S8", underlying: IntrinsicTypeInfo.WellKnown.I8);
        RegisterFallbackRecordType(name: "S16", underlying: IntrinsicTypeInfo.WellKnown.I16);
        RegisterFallbackRecordType(name: "S32", underlying: IntrinsicTypeInfo.WellKnown.I32);
        RegisterFallbackRecordType(name: "S64", underlying: IntrinsicTypeInfo.WellKnown.I64);
        RegisterFallbackRecordType(name: "S128", underlying: IntrinsicTypeInfo.WellKnown.I128);

        // Unsigned integers
        RegisterFallbackRecordType(name: "U8", underlying: IntrinsicTypeInfo.WellKnown.I8);
        RegisterFallbackRecordType(name: "U16", underlying: IntrinsicTypeInfo.WellKnown.I16);
        RegisterFallbackRecordType(name: "U32", underlying: IntrinsicTypeInfo.WellKnown.I32);
        RegisterFallbackRecordType(name: "U64", underlying: IntrinsicTypeInfo.WellKnown.I64);
        RegisterFallbackRecordType(name: "U128", underlying: IntrinsicTypeInfo.WellKnown.I128);

        // Pointer-sized integers
        RegisterFallbackRecordType(name: "SAddr", underlying: IntrinsicTypeInfo.WellKnown.Iptr);
        RegisterFallbackRecordType(name: "UAddr", underlying: IntrinsicTypeInfo.WellKnown.Uptr);

        // Floating-point types
        RegisterFallbackRecordType(name: "F16", underlying: IntrinsicTypeInfo.WellKnown.F16);
        RegisterFallbackRecordType(name: "F32", underlying: IntrinsicTypeInfo.WellKnown.F32);
        RegisterFallbackRecordType(name: "F64", underlying: IntrinsicTypeInfo.WellKnown.F64);
        RegisterFallbackRecordType(name: "F128", underlying: IntrinsicTypeInfo.WellKnown.F128);

        // Boolean
        RegisterFallbackRecordType(name: "Bool", underlying: IntrinsicTypeInfo.WellKnown.I1);

        // Text (placeholder)
        var textType = new RecordTypeInfo(name: "Text")
        {
            Namespace = "Core",
            Visibility = VisibilityModifier.Public
        };
        _types[key: "Text"] = textType;

        // Blank (unit type)
        var blankType = new RecordTypeInfo(name: "Blank")
        {
            Namespace = "Core",
            Visibility = VisibilityModifier.Public
        };
        _types[key: "Blank"] = blankType;
    }

    /// <summary>
    /// Registers a fallback Core record type that wraps an intrinsic.
    /// </summary>
    private void RegisterFallbackRecordType(string name, IntrinsicTypeInfo underlying)
    {
        var valueField = new FieldInfo(name: "value", type: underlying);
        var recordType = new RecordTypeInfo(name: name)
        {
            Fields = [valueField],
            Namespace = "Core",
            Visibility = VisibilityModifier.Public
        };
        _types[key: name] = recordType;
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
            Fields = record.Fields,
            ImplementedProtocols = protocols,
            GenericParameters = record.GenericParameters,
            GenericConstraints = record.GenericConstraints,
            TypeArguments = record.TypeArguments,
            Visibility = record.Visibility,
            Location = record.Location,
            Namespace = record.Namespace
        };

        _types[key: recordName] = updatedRecord;
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

        // Try instantiation cache
        if (_instantiations.TryGetValue(key: name, value: out TypeInfo? instantiation))
        {
            return instantiation;
        }

        // Try Core namespace prefix (Core types are auto-imported)
        if (!name.Contains('.') && _types.TryGetValue(key: $"Core.{name}", value: out type))
        {
            return type;
        }

        return null;
    }

    /// <summary>
    /// Gets or creates an instantiated generic type.
    /// </summary>
    /// <param name="genericDef">The generic type definition.</param>
    /// <param name="typeArguments">The type arguments for instantiation.</param>
    /// <returns>The instantiated type (cached if already created).</returns>
    public TypeInfo GetOrCreateInstantiation(TypeInfo genericDef,
        IReadOnlyList<TypeInfo> typeArguments)
    {
        // Build the instantiation name
        string name =
            $"{genericDef.Name}<{string.Join(separator: ", ", values: typeArguments.Select(selector: t => t.Name))}>";

        // Check cache
        if (_instantiations.TryGetValue(key: name, value: out TypeInfo? existing))
        {
            return existing;
        }

        // Create and cache
        TypeInfo instantiated = genericDef.Instantiate(typeArguments: typeArguments);
        _instantiations[key: name] = instantiated;

        return instantiated;
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
        if (_instantiations.TryGetValue(key, out TypeInfo? existing) && existing is RoutineTypeInfo routineType)
        {
            return routineType;
        }

        // Create and cache
        var newType = new RoutineTypeInfo(parameterTypes, returnType) { IsFailable = isFailable };
        _instantiations[key] = newType;

        return newType;
    }

    /// <summary>
    /// Checks if a type is a single-field record wrapping an intrinsic.
    /// </summary>
    /// <param name="type">The type to check.</param>
    /// <returns>True if the type is a single-field wrapper, false otherwise.</returns>
    public bool IsSingleFieldWrapper(TypeInfo type)
    {
        return type is RecordTypeInfo record && record.IsSingleFieldWrapper;
    }

    /// <summary>
    /// Gets the underlying intrinsic type for a single-field wrapper.
    /// </summary>
    /// <param name="type">The type to extract the intrinsic from.</param>
    /// <returns>The underlying intrinsic type, or null if not a single-field wrapper.</returns>
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
        return _types.Values.Where(predicate: t => t.Category == category);
    }

    /// <summary>
    /// Gets all types that can have methods (records, entities, residents, choices).
    /// </summary>
    /// <returns>An enumerable of all types that can have methods.</returns>
    public IEnumerable<TypeInfo> GetTypesWithMethods()
    {
        return _types.Values.Where(predicate: t =>
            t.Category is TypeCategory.Record or TypeCategory.Entity or
                TypeCategory.Resident or TypeCategory.Choice);
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

        // For overloaded routines, we might need a different key
        // For now, assume unique names
        _routines[key: key] = routine;

        // Index by owner type for fast method lookup
        if (routine.OwnerType != null)
        {
            string ownerKey = routine.OwnerType.Name;
            if (!_routinesByOwner.TryGetValue(key: ownerKey, value: out List<RoutineInfo>? list))
            {
                list = new List<RoutineInfo>();
                _routinesByOwner[key: ownerKey] = list;
            }

            list.Add(item: routine);
        }
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

        if (_routineInstantiations.TryGetValue(key: fullName, value: out RoutineInfo? instantiation))
        {
            return instantiation;
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
            DeclaredMutation = routine.DeclaredMutation,
            MutationCategory = routine.MutationCategory,
            GenericParameters = genericParameters,
            GenericConstraints = genericConstraints,
            Visibility = routine.Visibility,
            Location = routine.Location,
            Namespace = routine.Namespace,
            Attributes = routine.Attributes,
            CallingConvention = routine.CallingConvention,
            IsVariadic = routine.IsVariadic
        };

        _routines[key: key] = updatedRoutine;

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

        // For instantiated generics, check the generic definition's methods
        if (type.IsGenericInstantiation)
        {
            TypeInfo? genericDef = type switch
            {
                RecordTypeInfo r => r.GenericDefinition,
                EntityTypeInfo e => e.GenericDefinition,
                ResidentTypeInfo res => res.GenericDefinition,
                _ => null
            };

            if (genericDef != null)
            {
                RoutineInfo? genericMethod = LookupMethod(type: genericDef, methodName: methodName);
                if (genericMethod != null)
                {
                    // Need to instantiate the method with the type's type arguments
                    return genericMethod;
                }
            }
        }

        // Check implemented protocols for default implementations
        IReadOnlyList<TypeInfo>? protocols = type switch
        {
            RecordTypeInfo r => r.ImplementedProtocols,
            EntityTypeInfo e => e.ImplementedProtocols,
            ResidentTypeInfo res => res.ImplementedProtocols,
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

        return Enumerable.Empty<RoutineInfo>();
    }

    /// <summary>
    /// Gets or creates an instantiated generic routine.
    /// </summary>
    /// <param name="genericDef">The generic routine definition.</param>
    /// <param name="typeArguments">The type arguments for instantiation.</param>
    /// <returns>The instantiated routine (cached if already created).</returns>
    public RoutineInfo GetOrCreateRoutineInstantiation(RoutineInfo genericDef,
        IReadOnlyList<TypeInfo> typeArguments)
    {
        string name =
            $"{genericDef.FullName}<{string.Join(separator: ", ", values: typeArguments.Select(selector: t => t.Name))}>";

        if (_routineInstantiations.TryGetValue(key: name, value: out RoutineInfo? existing))
        {
            return existing;
        }

        RoutineInfo instantiated = genericDef.Instantiate(typeArguments: typeArguments);
        _routineInstantiations[key: name] = instantiated;

        return instantiated;
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
        if (_currentScope.Parent == null)
        {
            throw new InvalidOperationException(message: "Cannot exit the global scope.");
        }

        _currentScope = _currentScope.Parent;
    }

    /// <summary>
    /// Declares a variable in the current scope.
    /// </summary>
    /// <param name="name">The name of the variable.</param>
    /// <param name="type">The type of the variable.</param>
    /// <param name="isMutable">Whether the variable is mutable (var) or immutable (let).</param>
    /// <param name="isPreset">Whether this is a preset (compile-time constant).</param>
    /// <returns>True if successful, false if already declared in this scope.</returns>
    public bool DeclareVariable(string name, TypeInfo type, bool isMutable, bool isPreset = false)
    {
        var variable = new VariableInfo(name: name, type: type)
        {
            IsMutable = isMutable,
            IsPreset = isPreset
        };

        return _currentScope.DeclareVariable(variable: variable);
    }

    /// <summary>
    /// Looks up a variable by name in the current scope chain.
    /// </summary>
    /// <param name="name">The name of the variable to look up.</param>
    /// <returns>The variable info if found, null otherwise.</returns>
    public VariableInfo? LookupVariable(string name)
    {
        return _currentScope.LookupVariable(name: name);
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

    #endregion

    #region Language-Specific Validation

    /// <summary>
    /// Validates that a type is allowed for the current language.
    /// </summary>
    /// <param name="type">The type to validate.</param>
    /// <returns>True if the type is allowed for the current language, false otherwise.</returns>
    public bool IsTypeAllowedForLanguage(TypeInfo type)
    {
        // Residents are RazorForge only
        if (type.Category == TypeCategory.Resident && Language == Language.Suflae)
        {
            return false;
        }

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
}
