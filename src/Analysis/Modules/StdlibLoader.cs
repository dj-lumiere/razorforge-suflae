using SemanticAnalysis;
using SemanticAnalysis.Types;

namespace Builder.Modules;

using SemanticAnalysis.Enums;
using SyntaxTree;
using Compiler.Lexer;
using Compiler.Parser;

/// <summary>
/// Loads the standard library based on module declarations.
/// Files declaring "module Core" are loaded eagerly (auto-imported).
/// Other modules are loaded on-demand when imported.
/// Supports both RazorForge (.rf) and Suflae (.sf) stdlib files.
/// </summary>
public sealed class StdlibLoader
{
    /// <summary>The stdlib root directory path (e.g., stdlib/razorforge or stdlib/suflae).</summary>
    private readonly string _stdlibPath;

    /// <summary>The language being built.</summary>
    private readonly Language _language;

    /// <summary>The file extension to scan (.rf or .sf).</summary>
    private readonly string _fileExtension;

    /// <summary>Parsed Core module programs with their file paths and module.</summary>
    private readonly List<(Program Program, string FilePath, string Module)> _corePrograms = [];

    /// <summary>Cache of parsed non-Core programs by module.</summary>
    private readonly Dictionary<string, List<(Program Program, string FilePath, string Module)>> _modulePrograms = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set of already scanned directories to avoid re-scanning.</summary>
    private bool _stdlibScanned;

    /// <summary>Tracks modules that have been loaded on-demand.</summary>
    private readonly HashSet<string> _loadedModules = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the parsed Core module programs.</summary>
    public IReadOnlyList<(Program Program, string FilePath, string Module)> ParsedPrograms => _corePrograms;

    /// <summary>Gets all parsed programs (core + loaded modules) for codegen.</summary>
    public IReadOnlyList<(Program Program, string FilePath, string Module)> AllLoadedPrograms
    {
        get
        {
            var all = new List<(Program, string, string)>(_corePrograms);
            foreach (var mod in _loadedModules)
            {
                if (_modulePrograms.TryGetValue(mod, out var programs))
                    all.AddRange(programs);
            }
            return all;
        }
    }

    /// <summary>
    /// Creates a new stdlib loader for a specific language.
    /// </summary>
    /// <param name="stdlibRoot">Path to the stdlib root directory (containing razorforge/ and suflae/ subdirectories).</param>
    /// <param name="language">The language being built.</param>
    public StdlibLoader(string stdlibRoot, Language language)
    {
        _language = language;
        _fileExtension = language == Language.Suflae ? "*.sf" : "*.rf";

        // Use language-specific subdirectory
        string subdir = language == Language.Suflae ? "Suflae" : "RazorForge";
        _stdlibPath = Path.Combine(stdlibRoot, subdir);
    }

    /// <summary>
    /// Loads the Core module types into the type registry.
    /// Scans all stdlib files and loads those declaring "module Core".
    /// </summary>
    /// <param name="registry">The type registry to populate.</param>
    public void LoadCoreModule(TypeRegistry registry)
    {
        // Scan all stdlib files and categorize by module
        ScanStdlibFiles();

        // Three-pass registration ensures protocols exist before types reference them in 'obeys' clauses.
        // Pass 1a: Register all protocol type shells first (names + generic params, no methods yet)
        foreach (var (program, _, ns) in _corePrograms)
        {
            foreach (var node in program.Declarations)
            {
                if (node is ProtocolDeclaration protocol)
                    RegisterProtocolTypeShell(registry, protocol, ns);
            }
        }

        // Pass 1a.1: Fill in protocol method signatures (all protocols are now registered for cross-refs)
        foreach (var (program, _, _) in _corePrograms)
        {
            foreach (var node in program.Declarations)
            {
                if (node is ProtocolDeclaration protocol)
                    FillProtocolMethods(registry, protocol);
            }
        }

        // Pass 1a.2: Resolve parent protocol hierarchies (now that all protocols are registered)
        foreach (var (program, _, _) in _corePrograms)
        {
            ResolveProtocolParents(registry, program);
        }

        // Pass 1b: Register all type shells (record, entity, choice, variant)
        foreach (var (program, _, ns) in _corePrograms)
        {
            RegisterProgramTypes(registry, program, ns);
        }

        // Pass 1c: Re-resolve member variables now that all types are registered.
        // The initial registration may have empty member lists due to forward references
        // (e.g., Bytes needs List which needs U64, but files are processed alphabetically).
        foreach (var (program, _, ns) in _corePrograms)
        {
            ResolveProgramMemberVariables(registry, program);
        }

        // Pass 1d: Re-resolve protocol conformances now that all types are registered.
        // Protocol arguments may reference types not yet registered during Pass 1b
        // (e.g., EnumerateSequence[T] obeys Sequenceable[Tuple[S64, T]] needs S64).
        foreach (var (program, _, ns) in _corePrograms)
        {
            ResolveProgramProtocolConformances(registry, program);
        }

        // Pass 2: Register all routines (now all types are available for return type resolution)
        foreach (var (program, _, ns) in _corePrograms)
        {
            RegisterProgramRoutines(registry, program, ns);
        }

        // Pass 3: Register all presets (module-level constants accessible across files)
        foreach (var (program, _, ns) in _corePrograms)
        {
            RegisterProgramPresets(registry, program, ns);
        }
    }

    /// <summary>
    /// Scans all stdlib files recursively and categorizes them by module.
    /// Files with "module Core" go to _corePrograms.
    /// Other modules are cached in _modulePrograms for on-demand loading.
    /// </summary>
    private void ScanStdlibFiles()
    {
        if (_stdlibScanned || !Directory.Exists(_stdlibPath))
        {
            return;
        }

        _stdlibScanned = true;

        // Recursively find all files with the appropriate extension
        foreach (string filePath in Directory.GetFiles(_stdlibPath, _fileExtension, SearchOption.AllDirectories))
        {
            try
            {
                string code = File.ReadAllText(filePath);
                Program ast = ParseFile(code, filePath);

                // Find module declaration, or derive from directory
                string? fileModule = GetDeclaredModule(ast);
                fileModule ??= DeriveModuleFromPath(filePath);

                // Categorize by module
                if (fileModule.Equals("Core", StringComparison.OrdinalIgnoreCase))
                {
                    _corePrograms.Add((ast, filePath, fileModule));
                }
                else
                {
                    // Cache for on-demand loading
                    if (!_modulePrograms.TryGetValue(fileModule, out var programs))
                    {
                        programs = [];
                        _modulePrograms[fileModule] = programs;
                    }
                    programs.Add((ast, filePath, fileModule));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: Failed to parse stdlib file {filePath}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Parses a file using the appropriate tokenizer/parser for the current language.
    /// Used for scanning stdlib files where extension matches language.
    /// </summary>
    /// <param name="code">The source code to parse.</param>
    /// <param name="filePath">The file path for error reporting.</param>
    /// <returns>The parsed program AST.</returns>
    private Program ParseFile(string code, string filePath)
    {
        var tokenizer = new Tokenizer(code, filePath, _language);
        List<Token> tokens = tokenizer.Tokenize();
        var parser = new Parser(tokens, _language, filePath);
        return parser.Parse();
    }

    /// <summary>
    /// Parses a file using the tokenizer/parser determined by file extension.
    /// Used for cross-language imports where a Suflae file imports a RazorForge module.
    /// </summary>
    /// <param name="code">The source code to parse.</param>
    /// <param name="filePath">The file path (extension determines parser choice).</param>
    /// <returns>The parsed program AST.</returns>
    private static Program ParseFileByExtension(string code, string filePath)
    {
        bool isSuflaeFile = filePath.EndsWith(".sf", StringComparison.OrdinalIgnoreCase);
        var language = isSuflaeFile ? Language.Suflae : Language.RazorForge;
        var tokenizer = new Tokenizer(code, filePath, language);
        List<Token> tokens = tokenizer.Tokenize();
        var parser = new Parser(tokens, language, filePath);
        return parser.Parse();
    }

    /// <summary>
    /// Gets the declared module from a program AST.
    /// </summary>
    private static string? GetDeclaredModule(Program program)
    {
        foreach (var node in program.Declarations)
        {
            if (node is ModuleDeclaration ns)
            {
                return ns.Path;
            }
        }
        return null;
    }

    /// <summary>
    /// Derives a module from the file path relative to the standard library root.
    /// Example: standard/razorforge/Collections/List.rf -> Collections
    /// Example: standard/razorforge/Text/Encoding/UTF8.rf -> Text.Encoding
    /// Files directly in the language root default to Core.
    /// </summary>
    private string DeriveModuleFromPath(string filePath)
    {
        try
        {
            string? fileDir = Path.GetDirectoryName(filePath);
            if (fileDir == null)
            {
                return "Core";
            }

            string normalizedFileDir = Path.GetFullPath(fileDir);
            string normalizedStdlibPath = Path.GetFullPath(_stdlibPath);

            if (!normalizedFileDir.StartsWith(normalizedStdlibPath, StringComparison.OrdinalIgnoreCase))
            {
                return "Core";
            }

            string relativePath = normalizedFileDir[normalizedStdlibPath.Length..]
                                                   .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.IsNullOrEmpty(relativePath))
            {
                return "Core";
            }

            // Convert directory separators to module path separators
            return relativePath.Replace(Path.DirectorySeparatorChar, '/')
                               .Replace(Path.AltDirectorySeparatorChar, '/');
        }
        catch
        {
            return "Core";
        }
    }

    /// <summary>
    /// Loads a specific module on-demand.
    /// </summary>
    /// <param name="registry">The type registry to populate.</param>
    /// <param name="moduleName">The module to load (e.g., "Collections").</param>
    /// <returns>True if the module was loaded successfully, false if not found.</returns>
    public bool LoadModule(TypeRegistry registry, string moduleName)
    {
        // Ensure stdlib is scanned
        ScanStdlibFiles();

        // Core is already loaded
        if (moduleName.Equals("Core", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check if we have files for this module
        if (!_modulePrograms.TryGetValue(moduleName, out var programs) || programs.Count == 0)
        {
            return false;
        }

        _loadedModules.Add(moduleName);

        // Three-pass registration: protocols first, then other types, then routines
        // Register protocol shells across all files first, then fill in methods
        foreach (var (program, _, ns) in programs)
        {
            foreach (var node in program.Declarations)
            {
                if (node is ProtocolDeclaration protocol)
                    RegisterProtocolTypeShell(registry, protocol, ns);
            }
        }
        foreach (var (program, _, _) in programs)
        {
            foreach (var node in program.Declarations)
            {
                if (node is ProtocolDeclaration protocol)
                    FillProtocolMethods(registry, protocol);
            }
        }

        foreach (var (program, _, _) in programs)
        {
            ResolveProtocolParents(registry, program);
        }

        foreach (var (program, _, ns) in programs)
        {
            RegisterProgramTypes(registry, program, ns);
        }

        foreach (var (program, _, ns) in programs)
        {
            ResolveProgramProtocolConformances(registry, program);
        }

        foreach (var (program, _, ns) in programs)
        {
            RegisterProgramRoutines(registry, program, ns);
        }

        // Register presets for the module
        foreach (var (program, _, ns) in programs)
        {
            RegisterProgramPresets(registry, program, ns);
        }

        return true;
    }

    /// <summary>
    /// Loads a specific module on-demand.
    /// Parses the module file and registers its types and routines.
    /// Uses module-based imports: the import path determines the module.
    /// </summary>
    /// <param name="registry">The type registry to populate.</param>
    /// <param name="filePath">The resolved file path of the module.</param>
    /// <param name="moduleId">The module identifier (e.g., "Collections.List").</param>
    /// <returns>The effective module of the loaded module, or null on failure.</returns>
    public string? LoadModule(TypeRegistry registry, string filePath, string moduleId)
    {
        try
        {
            string code = File.ReadAllText(filePath);
            // Detect file type from extension and use appropriate parser
            Program ast = ParseFileByExtension(code, filePath);

            // Get module from file declaration, or derive from directory structure
            string? fileModule = GetDeclaredModule(ast);
            string effectiveModule = fileModule ?? DeriveModuleFromPath(filePath);

            // Track loaded program for codegen
            _loadedModules.Add(moduleId);
            if (!_modulePrograms.TryGetValue(moduleId, out var progs))
            {
                progs = [];
                _modulePrograms[moduleId] = progs;
            }
            progs.Add((ast, filePath, effectiveModule));

            // Two-pass registration for single module
            RegisterProgramTypes(registry, ast, effectiveModule);
            RegisterProgramRoutines(registry, ast, effectiveModule);

            // Handle any imports within this module (recursive loading)
            foreach (var node in ast.Declarations)
            {
                if (node is ImportDeclaration import)
                {
                    // Recursively load imported modules
                    registry.LoadModule(import.ModulePath, filePath, import.Location, out _);
                }
            }

            return effectiveModule;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to load module '{moduleId}' from {filePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Resolves parent protocol relationships for all protocols in a program.
    /// Must run after all protocols are registered (pass 1a) so parent lookups succeed.
    /// </summary>
    private static void ResolveProtocolParents(TypeRegistry registry, Program program)
    {
        foreach (var node in program.Declarations)
        {
            if (node is ProtocolDeclaration protocol && protocol.ParentProtocols.Count > 0)
            {
                // Look up the registered protocol to get its FullName
                var registeredProto = registry.LookupType(protocol.Name);
                if (registeredProto is not ProtocolTypeInfo)
                {
                    continue;
                }

                var parentProtocols = new List<ProtocolTypeInfo>();
                foreach (var parentExpr in protocol.ParentProtocols)
                {
                    var parentType = ResolveSimpleType(registry, parentExpr);
                    if (parentType is ProtocolTypeInfo parentProto)
                    {
                        parentProtocols.Add(parentProto);
                    }
                }

                if (parentProtocols.Count > 0)
                {
                    registry.UpdateProtocolParents(registeredProto.FullName, parentProtocols);
                }
            }
        }
    }

    /// <summary>
    /// Registers protocol declarations from a program.
    /// This is pass 1a — protocols must be registered before other types so 'obeys' clauses can resolve.
    /// Uses two passes: first registers protocol type shells (names + generic params), then fills in
    /// method signatures. This ensures forward references between protocols resolve correctly
    /// (e.g., Sequenceable[T].__seq__() → SequenceEmitter[T] where SequenceEmitter is another protocol).
    /// </summary>
    private static void RegisterProgramProtocols(TypeRegistry registry, Program program, string moduleName)
    {
        // Pass 1: Register protocol type shells (no methods yet)
        foreach (var node in program.Declarations)
        {
            if (node is ProtocolDeclaration protocol)
            {
                RegisterProtocolTypeShell(registry, protocol, moduleName);
            }
        }

        // Pass 2: Fill in method signatures (now all protocols are registered for cross-references)
        foreach (var node in program.Declarations)
        {
            if (node is ProtocolDeclaration protocol)
            {
                FillProtocolMethods(registry, protocol);
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
    private static void RegisterProgramTypes(TypeRegistry registry, Program program, string moduleName)
    {
        foreach (var node in program.Declarations)
        {
            switch (node)
            {
                case RecordDeclaration record:
                    RegisterRecordType(registry, record, moduleName);
                    break;
                case EntityDeclaration entity:
                    RegisterEntityType(registry, entity, moduleName);
                    break;
                case ChoiceDeclaration choice:
                    RegisterChoiceType(registry, choice, moduleName);
                    break;
                case FlagsDeclaration flags:
                    RegisterFlagsType(registry, flags, moduleName);
                    break;
                case VariantDeclaration variant:
                    RegisterVariantType(registry, variant, moduleName);
                    break;
                case ProtocolDeclaration protocol:
                    RegisterProtocolType(registry, protocol, moduleName);
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
        foreach (var node in program.Declarations)
        {
            switch (node)
            {
                case EntityDeclaration entity:
                {
                    var existing = registry.LookupType(entity.Name) as EntityTypeInfo;
                    int expectedCount = entity.Members.Count(m => m is VariableDeclaration { Type: not null });
                    if (existing == null || existing.MemberVariables.Count >= expectedCount)
                        continue;

                    var members = ResolveMemberVariables(registry, entity.Members, entity.GenericParameters);
                    if (members.Count > existing.MemberVariables.Count)
                        existing.MemberVariables = members;
                    break;
                }
                case RecordDeclaration record:
                {
                    var existing = registry.LookupType(record.Name) as RecordTypeInfo;
                    int expectedCount = record.Members.Count(m => m is VariableDeclaration { Type: not null });
                    if (existing == null || existing.MemberVariables.Count >= expectedCount)
                        continue;

                    var members = ResolveMemberVariables(registry, record.Members, record.GenericParameters);
                    if (members.Count > existing.MemberVariables.Count)
                        existing.MemberVariables = members;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Re-resolves protocol conformances for types whose protocol arguments contain
    /// forward-referenced types (e.g., EnumerateSequence[T] obeys Sequenceable[Tuple[S64, T]]
    /// where S64 wasn't registered during initial entity registration).
    /// Called after all type shells are registered.
    /// </summary>
    private static void ResolveProgramProtocolConformances(TypeRegistry registry, Program program)
    {
        foreach (var node in program.Declarations)
        {
            switch (node)
            {
                case EntityDeclaration entity when entity.Protocols.Count > 0:
                {
                    var existing = registry.LookupType(entity.Name) as EntityTypeInfo;
                    if (existing == null || existing.ImplementedProtocols.Count >= entity.Protocols.Count)
                        continue;

                    var protocols = ResolveProtocolList(registry, entity.Protocols, entity.GenericParameters);
                    if (protocols.Count > existing.ImplementedProtocols.Count)
                        existing.ImplementedProtocols = protocols;
                    break;
                }
                case RecordDeclaration record when record.Protocols.Count > 0:
                {
                    var existing = registry.LookupType(record.Name) as RecordTypeInfo;
                    if (existing == null || existing.ImplementedProtocols.Count >= record.Protocols.Count)
                        continue;

                    var protocols = ResolveProtocolList(registry, record.Protocols, record.GenericParameters);
                    if (protocols.Count > existing.ImplementedProtocols.Count)
                        existing.ImplementedProtocols = protocols;
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
        foreach (var protoExpr in protoExprs)
        {
            var protoType = ResolveSimpleType(registry, protoExpr, genericParams);
            if (protoType != null)
                result.Add(protoType);
        }
        return result;
    }

    /// <summary>
    /// Resolves member variable types from a list of member declarations.
    /// </summary>
    private static List<SemanticAnalysis.Symbols.MemberVariableInfo> ResolveMemberVariables(
        TypeRegistry registry, IReadOnlyList<Declaration> members,
        IReadOnlyList<string>? genericParams)
    {
        var result = new List<SemanticAnalysis.Symbols.MemberVariableInfo>();
        foreach (var member in members)
        {
            if (member is VariableDeclaration { Type: not null } memberVariable)
            {
                var memberVariableType = ResolveSimpleType(registry, memberVariable.Type, genericParams);
                if (memberVariableType != null)
                {
                    result.Add(new SemanticAnalysis.Symbols.MemberVariableInfo(memberVariable.Name, memberVariableType)
                    {
                        Visibility = memberVariable.Visibility
                    });
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Registers routine declarations from a program.
    /// This is pass 2 of module-based loading - all types are already registered.
    /// </summary>
    private static void RegisterProgramRoutines(TypeRegistry registry, Program program, string moduleName)
    {
        foreach (var node in program.Declarations)
        {
            switch (node)
            {
                case RoutineDeclaration routine:
                    RegisterRoutine(registry, routine, moduleName);
                    break;
                case ExternalDeclaration external:
                    RegisterExternalDeclaration(registry, external, moduleName);
                    break;
                case ExternalBlockDeclaration block:
                    foreach (var decl in block.Declarations)
                    {
                        if (decl is ExternalDeclaration ext)
                            RegisterExternalDeclaration(registry, ext, moduleName);
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Registers an external("C") declaration from stdlib (e.g., NativeDeclarations.rf).
    /// </summary>
    private static void RegisterExternalDeclaration(TypeRegistry registry, ExternalDeclaration external,
        string moduleName)
    {
        // Build generic context for type resolution (e.g., T, To, From)
        IReadOnlyList<string>? genericCtx = external.GenericParameters is { Count: > 0 }
            ? external.GenericParameters
            : null;

        // Resolve parameter types
        var parameters = new List<SemanticAnalysis.Symbols.ParameterInfo>();
        foreach (var param in external.Parameters)
        {
            var paramType = ResolveSimpleType(registry, param.Type, genericCtx);
            parameters.Add(new SemanticAnalysis.Symbols.ParameterInfo(param.Name,
                paramType ?? ErrorTypeInfo.Instance)
            {
                DefaultValue = param.DefaultValue,
                IsVariadicParam = param.IsVariadic
            });
        }

        // Resolve return type
        var returnType = external.ReturnType != null
            ? ResolveSimpleType(registry, external.ReturnType, genericCtx)
            : null;

        var routineInfo = new SemanticAnalysis.Symbols.RoutineInfo(external.Name)
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
            registry.RegisterRoutine(routineInfo);
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
    private static void RegisterProgramPresets(TypeRegistry registry, Program program, string moduleName)
    {
        foreach (var node in program.Declarations)
        {
            if (node is PresetDeclaration preset)
            {
                var presetType = ResolveSimpleType(registry, preset.Type);
                if (presetType != null)
                {
                    registry.RegisterPreset(preset.Name, presetType, moduleName);
                }
            }
        }
    }

    /// <summary>
    /// Registers a routine from stdlib (including type methods like S32.__add__).
    /// </summary>
    private static void RegisterRoutine(TypeRegistry registry, RoutineDeclaration routine,
        string moduleName)
    {
        // Parse method names like "S32.__add__" or "Type.method"
        string routineName = routine.Name;
        TypeInfo? ownerType = null;
        string methodName = routineName;

        int dotIndex = routineName.IndexOf('.');
        if (dotIndex > 0)
        {
            string typeName = routineName[..dotIndex];
            methodName = routineName[(dotIndex + 1)..]; // Just the method part (e.g., "__add__")

            int bracketIndex = typeName.IndexOf('[');
            if (bracketIndex > 0)
            {
                // Check if the bracket content is concrete types (e.g., List[Byte])
                // vs generic params (e.g., List[T], Dict[K, V])
                string bracketContent = typeName[(bracketIndex + 1)..].TrimEnd(']');
                string baseName = typeName[..bracketIndex];
                var baseDef = registry.LookupType(baseName);

                // If the base is a generic definition, check if bracket args are its own params
                bool isGenericDef = false;
                if (baseDef?.GenericParameters != null)
                {
                    var args = bracketContent.Split(',').Select(a => a.Trim()).ToList();
                    isGenericDef = args.All(a => baseDef.GenericParameters.Contains(a));
                }

                if (isGenericDef)
                {
                    // Generic definition: List[T] → owner is List
                    ownerType = baseDef;
                }
                else
                {
                    // Concrete specialization: List[Byte] → owner is List[Byte]
                    ownerType = registry.LookupType(typeName) ?? baseDef;
                }
            }
            else
            {
                ownerType = registry.LookupType(typeName);

                // If type not found, treat as a generic type parameter (e.g., T in "routine T.view()")
                if (ownerType == null)
                {
                    ownerType = new GenericParameterTypeInfo(typeName);
                }
            }
        }

        // Collect generic params from owner type + routine itself for type resolution context
        var genericContext = new List<string>();
        // If owner is a generic parameter itself (e.g., T in "routine T.view()"),
        // add it to the generic context so return/param types can reference it
        if (ownerType is GenericParameterTypeInfo genParam) genericContext.Add(genParam.Name);
        if (ownerType?.GenericParameters != null) genericContext.AddRange(ownerType.GenericParameters);
        if (routine.GenericParameters != null) genericContext.AddRange(routine.GenericParameters);
        IReadOnlyList<string>? ctx = genericContext.Count > 0 ? genericContext : null;

        // Resolve parameter types
        var parameters = new List<SemanticAnalysis.Symbols.ParameterInfo>();
        foreach (var param in routine.Parameters)
        {
            var paramType = ResolveSimpleType(registry, param.Type, ctx);
            parameters.Add(new SemanticAnalysis.Symbols.ParameterInfo(param.Name,
                paramType ?? ErrorTypeInfo.Instance)
            {
                DefaultValue = param.DefaultValue,
                IsVariadicParam = param.IsVariadic
            });
        }

        // Resolve return type
        var returnType = routine.ReturnType != null
            ? ResolveSimpleType(registry, routine.ReturnType, ctx)
            : null;

        // Use just the method name (not "S32.__add__", just "__add__")
        var routineInfo = new SemanticAnalysis.Symbols.RoutineInfo(methodName)
        {
            OwnerType = ownerType,
            Parameters = parameters,
            ReturnType = returnType,
            Module = moduleName,
            Location = routine.Location,
            IsFailable = routine.IsFailable,
            GenericParameters = routine.GenericParameters,
            AsyncStatus = routine.Async,
            Annotations = routine.Annotations,
            IsDangerous = routine.IsDangerous,
            Storage = routine.Storage
        };

        try
        {
            registry.RegisterRoutine(routineInfo);
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
        if (registry.LookupType(record.Name) != null)
        {
            return;
        }

        // Build member variables list upfront (TypeInfo uses init properties with IReadOnlyList)
        var memberVariables = new List<SemanticAnalysis.Symbols.MemberVariableInfo>();
        foreach (var member in record.Members)
        {
            if (member is VariableDeclaration { Type: not null } memberVariable)
            {
                var memberVariableType = ResolveSimpleType(registry, memberVariable.Type, record.GenericParameters);
                if (memberVariableType != null)
                {
                    memberVariables.Add(new SemanticAnalysis.Symbols.MemberVariableInfo(memberVariable.Name, memberVariableType)
                    {
                        Visibility = memberVariable.Visibility
                    });
                }
            }
        }

        // Resolve implemented protocols (obeys clause)
        var protocols = new List<TypeInfo>();
        foreach (var protoExpr in record.Protocols)
        {
            var protoType = ResolveSimpleType(registry, protoExpr, record.GenericParameters);
            if (protoType != null)
            {
                protocols.Add(protoType);
            }
        }

        var typeInfo = new RecordTypeInfo(record.Name)
        {
            Module = moduleName,
            Visibility = record.Visibility,
            MemberVariables = memberVariables,
            ImplementedProtocols = protocols,
            GenericParameters = record.GenericParameters,
            BackendType = ExtractLlvmAnnotation(record.Annotations)
        };

        try
        {
            registry.RegisterType(typeInfo);
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
        if (registry.LookupType(entity.Name) != null)
        {
            return;
        }

        // Build member variables list upfront
        var memberVariables = new List<SemanticAnalysis.Symbols.MemberVariableInfo>();
        foreach (var member in entity.Members)
        {
            if (member is VariableDeclaration { Type: not null } memberVariable)
            {
                var memberVariableType = ResolveSimpleType(registry, memberVariable.Type, entity.GenericParameters);
                if (memberVariableType != null)
                {
                    memberVariables.Add(new SemanticAnalysis.Symbols.MemberVariableInfo(memberVariable.Name, memberVariableType)
                    {
                        Visibility = memberVariable.Visibility
                    });
                }
            }
        }

        // Resolve implemented protocols (obeys clause)
        var protocols = new List<TypeInfo>();
        foreach (var protoExpr in entity.Protocols)
        {
            var protoType = ResolveSimpleType(registry, protoExpr, entity.GenericParameters);
            if (protoType != null)
            {
                protocols.Add(protoType);
            }
        }

        var typeInfo = new EntityTypeInfo(entity.Name)
        {
            Module = moduleName,
            Visibility = entity.Visibility,
            MemberVariables = memberVariables,
            ImplementedProtocols = protocols,
            GenericParameters = entity.GenericParameters
        };

        registry.RegisterType(typeInfo);
    }

    /// <summary>
    /// Registers a choice type from stdlib.
    /// </summary>
    private static void RegisterChoiceType(TypeRegistry registry, ChoiceDeclaration choice,
        string moduleName)
    {
        // Skip if already registered
        if (registry.LookupType(choice.Name) != null)
        {
            return;
        }

        // Build cases list upfront
        var cases = new List<ChoiceCaseInfo>();
        long autoValue = 0;
        foreach (var caseDecl in choice.Cases)
        {
            long? explicitValue = null;
            if (caseDecl.Value is LiteralExpression { Value: string valStr })
            {
                if (long.TryParse(valStr, out long v))
                    explicitValue = v;
            }
            else if (caseDecl.Value is UnaryExpression { Operator: UnaryOperator.Minus, Operand: LiteralExpression { Value: string negStr } })
            {
                if (long.TryParse(negStr, out long v))
                    explicitValue = -v;
            }

            long computedValue;
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

            cases.Add(new ChoiceCaseInfo(caseDecl.Name)
            {
                Value = explicitValue,
                ComputedValue = computedValue
            });
        }

        var typeInfo = new ChoiceTypeInfo(choice.Name)
        {
            Module = moduleName, Visibility = choice.Visibility, Cases = cases
        };

        registry.RegisterType(typeInfo);
    }

    /// <summary>
    /// Registers a flags type from stdlib.
    /// </summary>
    private static void RegisterFlagsType(TypeRegistry registry, FlagsDeclaration flags,
        string moduleName)
    {
        if (registry.LookupType(flags.Name) != null)
        {
            return;
        }

        var members = new List<FlagsMemberInfo>();
        for (int i = 0; i < flags.Members.Count; i++)
        {
            members.Add(new FlagsMemberInfo(flags.Members[i], i));
        }

        var typeInfo = new FlagsTypeInfo(flags.Name)
        {
            Module = moduleName, Visibility = flags.Visibility, Members = members
        };

        registry.RegisterType(typeInfo);
    }

    /// <summary>
    /// Registers a variant type (type-based tagged union) from stdlib.
    /// </summary>
    private static void RegisterVariantType(TypeRegistry registry, VariantDeclaration variant,
        string moduleName)
    {
        // Skip if already registered
        if (registry.LookupType(variant.Name) != null)
        {
            return;
        }

        // Build members list: None = tag 0, others sequential from 1
        var members = new List<VariantMemberInfo>();
        bool hasNone = false;
        int tag = 0;

        // First pass: find None
        foreach (var memberDecl in variant.Members)
        {
            if (memberDecl.Type.Name == "None")
            {
                hasNone = true;
                members.Add(VariantMemberInfo.CreateNone(tagValue: 0, location: null));
                tag = 1;
                break;
            }
        }

        // Second pass: all non-None members
        foreach (var memberDecl in variant.Members)
        {
            if (memberDecl.Type.Name == "None") continue;

            TypeInfo? memberType = ResolveSimpleType(registry, memberDecl.Type);
            if (memberType != null)
            {
                members.Add(new VariantMemberInfo(memberType) { TagValue = tag++ });
            }
        }

        var typeInfo = new VariantTypeInfo(variant.Name)
        {
            Module = moduleName,
            Members = members,
            GenericParameters = variant.GenericParameters
        };

        registry.RegisterType(typeInfo);
    }

    /// <summary>
    /// Registers a protocol type from stdlib (single-pass: registers type and methods together).
    /// Used by RegisterProgramTypes (pass 1b) for protocols encountered outside the two-pass path.
    /// </summary>
    private static void RegisterProtocolType(TypeRegistry registry, ProtocolDeclaration protocol,
        string moduleName)
    {
        RegisterProtocolTypeShell(registry, protocol, moduleName);
        FillProtocolMethods(registry, protocol);
    }

    /// <summary>
    /// Registers a protocol type shell (name, generic params) without method signatures.
    /// This is the first pass of protocol registration — ensures all protocol types exist
    /// before method signatures are resolved (which may reference other protocols).
    /// </summary>
    private static void RegisterProtocolTypeShell(TypeRegistry registry, ProtocolDeclaration protocol,
        string moduleName)
    {
        // Skip if already registered
        if (registry.LookupType(protocol.Name) != null)
            return;

        var typeInfo = new ProtocolTypeInfo(protocol.Name)
        {
            Module = moduleName,
            Visibility = protocol.Visibility,
            Methods = [], // Filled in by FillProtocolMethods
            GenericParameters = protocol.GenericParameters
        };

        registry.RegisterType(typeInfo);
    }

    /// <summary>
    /// Fills in method signatures for a previously registered protocol type.
    /// This is the second pass — all protocols are registered, so cross-references resolve.
    /// </summary>
    private static void FillProtocolMethods(TypeRegistry registry, ProtocolDeclaration protocol)
    {
        var existing = registry.LookupType(protocol.Name) as ProtocolTypeInfo;
        if (existing == null || existing.Methods.Count > 0)
            return; // Already has methods or not found

        var methods = new List<ProtocolMethodInfo>();
        foreach (var method in protocol.Methods)
        {
            string rawName = method.Name;
            bool isFailable = rawName.EndsWith('!');
            string fullName = isFailable ? rawName[..^1] : rawName;
            bool isInstance = fullName.StartsWith("Me.");
            string methodName = isInstance ? fullName[3..] : fullName;

            var returnType = method.ReturnType != null
                ? ResolveSimpleType(registry, method.ReturnType, protocol.GenericParameters)
                : null;

            var parameterTypes = new List<TypeInfo>();
            var parameterNames = new List<string>();

            foreach (var param in method.Parameters)
            {
                if (param.Name == "me") continue;

                var paramType = param.Type?.Name == "Me"
                    ? ProtocolSelfTypeInfo.Instance
                    : ResolveSimpleType(registry, param.Type, protocol.GenericParameters);
                if (paramType != null)
                {
                    parameterTypes.Add(paramType);
                    parameterNames.Add(param.Name);
                }
            }

            var resolvedReturnType = method.ReturnType?.Name == "Me"
                ? ProtocolSelfTypeInfo.Instance
                : returnType;

            methods.Add(new ProtocolMethodInfo(methodName)
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

    /// <summary>
    /// Resolves a simple type expression.
    /// Handles intrinsic types, direct type references, generic parameter names,
    /// and parameterized types like List[Letter] or Dict[Text, S32].
    /// </summary>
    /// <param name="registry">The type registry to look up types in.</param>
    /// <param name="typeExpr">The type expression to resolve.</param>
    /// <param name="genericParams">Optional list of generic parameter names in scope (e.g., T, K, V).</param>
    private static TypeInfo? ResolveSimpleType(TypeRegistry registry,
        TypeExpression? typeExpr, IReadOnlyList<string>? genericParams = null)
    {
        if (typeExpr == null) return null;

        string typeName = typeExpr.Name;

        // Handle intrinsic types (@intrinsic.*)
        if (typeName.StartsWith("@intrinsic."))
        {
            return registry.LookupType(typeName);
        }

        // Generic parameter name (T, K, V) → placeholder for substitution
        if (genericParams != null && genericParams.Contains(typeName))
        {
            return new GenericParameterTypeInfo(typeName);
        }

        // Parameterized type like List[Letter], Dict[Text, S32]
        if (typeExpr.GenericArguments is { Count: > 0 })
        {
            // Tuple types are not registered as generic definitions — handle specially
            if (typeName is "Tuple")
            {
                var elemTypes = new List<TypeInfo>();
                foreach (var argExpr in typeExpr.GenericArguments)
                {
                    var argType = ResolveSimpleType(registry, argExpr, genericParams);
                    if (argType == null) return null;
                    elemTypes.Add(argType);
                }
                return new TupleTypeInfo(elemTypes);
            }

            var genericDef = registry.LookupType(typeName);
            if (genericDef is { IsGenericDefinition: true }
                && genericDef.GenericParameters!.Count == typeExpr.GenericArguments.Count)
            {
                var typeArgs = new List<TypeInfo>();
                foreach (var argExpr in typeExpr.GenericArguments)
                {
                    var argType = ResolveSimpleType(registry, argExpr, genericParams);
                    if (argType == null) return null;
                    typeArgs.Add(argType);
                }
                return registry.GetOrCreateResolution(genericDef, typeArgs);
            }
        }

        // Try to look up existing type
        return registry.LookupType(typeName);
    }

    /// <summary>
    /// Gets the default stdlib path relative to the application.
    /// </summary>
    public static string GetDefaultStdlibPath()
    {
        // Try to find standard library relative to the executable
        string? exeDir = Path.GetDirectoryName(typeof(StdlibLoader).Assembly.Location);
        if (exeDir != null)
        {
            string stdlibPath = Path.Combine(exeDir, "Standard");
            if (Directory.Exists(stdlibPath))
            {
                return stdlibPath;
            }

            // Try parent directories (for development)
            string? current = exeDir;
            for (int i = 0; i < 5; i++)
            {
                current = Path.GetDirectoryName(current);
                if (current == null) break;

                stdlibPath = Path.Combine(current, "Standard");
                if (Directory.Exists(stdlibPath))
                {
                    return stdlibPath;
                }
            }
        }

        // Fallback to current directory
        return Path.Combine(Directory.GetCurrentDirectory(), "Standard");
    }

    /// <summary>
    /// Extracts the LLVM type from an @llvm("type") annotation.
    /// Returns null if no @llvm annotation is present.
    /// </summary>
    private static string? ExtractLlvmAnnotation(List<string>? annotations)
    {
        if (annotations == null) return null;
        foreach (var ann in annotations)
        {
            if (ann.StartsWith("llvm(") && ann.EndsWith(")"))
                return ann[5..^1].Trim('"');
        }
        return null;
    }
}
