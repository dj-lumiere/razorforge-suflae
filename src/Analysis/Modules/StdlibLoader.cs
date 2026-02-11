namespace Compilers.Analysis.Modules;

using Enums;
using RazorForge.Lexer;
using RazorForge.Parser;
using Suflae.Lexer;
using Suflae.Parser;
using Shared.AST;
using Shared.Lexer;

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

    /// <summary>The language being compiled.</summary>
    private readonly Language _language;

    /// <summary>The file extension to scan (.rf or .sf).</summary>
    private readonly string _fileExtension;

    /// <summary>Parsed Core module programs with their file paths and module.</summary>
    private readonly List<(Program Program, string FilePath, string Module)> _corePrograms = [];

    /// <summary>Cache of parsed non-Core programs by module.</summary>
    private readonly Dictionary<string, List<(Program Program, string FilePath, string Module)>> _modulePrograms = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set of already scanned directories to avoid re-scanning.</summary>
    private bool _stdlibScanned;

    /// <summary>Gets the parsed Core module programs.</summary>
    public IReadOnlyList<(Program Program, string FilePath, string Module)> ParsedPrograms => _corePrograms;

    /// <summary>
    /// Creates a new stdlib loader for a specific language.
    /// </summary>
    /// <param name="stdlibRoot">Path to the stdlib root directory (containing razorforge/ and suflae/ subdirectories).</param>
    /// <param name="language">The language being compiled.</param>
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

        // Three-pass registration ensures protocols exist before types reference them in 'follows' clauses.
        // Pass 1a: Register all protocols first (so 'follows' clauses can resolve)
        foreach (var (program, _, ns) in _corePrograms)
        {
            RegisterProgramProtocols(registry, program, ns);
        }

        // Pass 1a.2: Resolve parent protocol hierarchies (now that all protocols are registered)
        foreach (var (program, _, _) in _corePrograms)
        {
            ResolveProtocolParents(registry, program);
        }

        // Pass 1b: Register all other types (record, entity, choice, variant)
        foreach (var (program, _, ns) in _corePrograms)
        {
            RegisterProgramTypes(registry, program, ns);
        }

        // Pass 2: Register all routines (now all types are available for return type resolution)
        foreach (var (program, _, ns) in _corePrograms)
        {
            RegisterProgramRoutines(registry, program, ns);
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
        if (_language == Language.Suflae)
        {
            var tokenizer = new SuflaeTokenizer(code, filePath);
            List<Token> tokens = tokenizer.Tokenize();
            var parser = new SuflaeParser(tokens, filePath);
            return parser.Parse();
        }
        else
        {
            var tokenizer = new RazorForgeTokenizer(code, filePath);
            List<Token> tokens = tokenizer.Tokenize();
            var parser = new RazorForgeParser(tokens, filePath);
            return parser.Parse();
        }
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

        if (isSuflaeFile)
        {
            var tokenizer = new SuflaeTokenizer(code, filePath);
            List<Token> tokens = tokenizer.Tokenize();
            var parser = new SuflaeParser(tokens, filePath);
            return parser.Parse();
        }
        else
        {
            var tokenizer = new RazorForgeTokenizer(code, filePath);
            List<Token> tokens = tokenizer.Tokenize();
            var parser = new RazorForgeParser(tokens, filePath);
            return parser.Parse();
        }
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

            // Convert directory separators to module dots
            return relativePath.Replace(Path.DirectorySeparatorChar, '.')
                               .Replace(Path.AltDirectorySeparatorChar, '.');
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

        // Three-pass registration: protocols first, then other types, then routines
        foreach (var (program, _, ns) in programs)
        {
            RegisterProgramProtocols(registry, program, ns);
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
            RegisterProgramRoutines(registry, program, ns);
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
                if (registeredProto is not Types.ProtocolTypeInfo)
                {
                    continue;
                }

                var parentProtocols = new List<Types.ProtocolTypeInfo>();
                foreach (var parentExpr in protocol.ParentProtocols)
                {
                    var parentType = ResolveSimpleType(registry, parentExpr);
                    if (parentType is Types.ProtocolTypeInfo parentProto)
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
    /// This is pass 1a — protocols must be registered before other types so 'follows' clauses can resolve.
    /// </summary>
    private static void RegisterProgramProtocols(TypeRegistry registry, Program program, string moduleName)
    {
        foreach (var node in program.Declarations)
        {
            if (node is ProtocolDeclaration protocol)
            {
                RegisterProtocolType(registry, protocol, moduleName);
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
                case ResidentDeclaration resident:
                    RegisterResidentType(registry, resident, moduleName);
                    break;
                case EntityDeclaration entity:
                    RegisterEntityType(registry, entity, moduleName);
                    break;
                case ChoiceDeclaration choice:
                    RegisterChoiceType(registry, choice, moduleName);
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
    /// Registers routine declarations from a program.
    /// This is pass 2 of module-based loading - all types are already registered.
    /// </summary>
    private static void RegisterProgramRoutines(TypeRegistry registry, Program program, string moduleName)
    {
        foreach (var node in program.Declarations)
        {
            if (node is RoutineDeclaration routine)
            {
                RegisterRoutine(registry, routine, moduleName);
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
        Types.TypeInfo? ownerType = null;
        string methodName = routineName;

        int dotIndex = routineName.IndexOf('.');
        if (dotIndex > 0)
        {
            string typeName = routineName[..dotIndex];
            methodName = routineName[(dotIndex + 1)..]; // Just the method part (e.g., "__add__")
            ownerType = registry.LookupType(typeName);
        }

        // Resolve parameter types
        var parameters = new List<Symbols.ParameterInfo>();
        foreach (var param in routine.Parameters)
        {
            var paramType = ResolveSimpleType(registry, param.Type);
            parameters.Add(new Symbols.ParameterInfo(param.Name,
                paramType ?? Types.ErrorTypeInfo.Instance));
        }

        // Resolve return type
        var returnType = routine.ReturnType != null
            ? ResolveSimpleType(registry, routine.ReturnType)
            : null;

        // Use just the method name (not "S32.__add__", just "__add__")
        var routineInfo = new Symbols.RoutineInfo(methodName)
        {
            OwnerType = ownerType,
            Parameters = parameters,
            ReturnType = returnType,
            Module = moduleName,
            IsFailable = routine.IsFailable
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

        // Build fields list upfront (TypeInfo uses init properties with IReadOnlyList)
        var fields = new List<Symbols.FieldInfo>();
        foreach (var member in record.Members)
        {
            if (member is VariableDeclaration { Type: not null } field)
            {
                var fieldType = ResolveSimpleType(registry, field.Type);
                if (fieldType != null)
                {
                    fields.Add(new Symbols.FieldInfo(field.Name, fieldType)
                    {
                        IsMutable = field.IsMutable, Visibility = field.Visibility
                    });
                }
            }
        }

        // Resolve implemented protocols (follows clause)
        var protocols = new List<Types.TypeInfo>();
        foreach (var protoExpr in record.Protocols)
        {
            var protoType = ResolveSimpleType(registry, protoExpr);
            if (protoType != null)
            {
                protocols.Add(protoType);
            }
        }

        var typeInfo = new Types.RecordTypeInfo(record.Name)
        {
            Module = moduleName,
            Visibility = record.Visibility,
            Fields = fields,
            ImplementedProtocols = protocols,
            GenericParameters = record.GenericParameters
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

        // Build fields list upfront
        var fields = new List<Symbols.FieldInfo>();
        foreach (var member in entity.Members)
        {
            if (member is VariableDeclaration { Type: not null } field)
            {
                var fieldType = ResolveSimpleType(registry, field.Type);
                if (fieldType != null)
                {
                    fields.Add(new Symbols.FieldInfo(field.Name, fieldType)
                    {
                        IsMutable = field.IsMutable, Visibility = field.Visibility
                    });
                }
            }
        }

        // Resolve implemented protocols (follows clause)
        var protocols = new List<Types.TypeInfo>();
        foreach (var protoExpr in entity.Protocols)
        {
            var protoType = ResolveSimpleType(registry, protoExpr);
            if (protoType != null)
            {
                protocols.Add(protoType);
            }
        }

        var typeInfo = new Types.EntityTypeInfo(entity.Name)
        {
            Module = moduleName,
            Visibility = entity.Visibility,
            Fields = fields,
            ImplementedProtocols = protocols,
            GenericParameters = entity.GenericParameters
        };

        registry.RegisterType(typeInfo);
    }

    /// <summary>
    /// Registers a resident type from stdlib.
    /// Residents are fixed-size reference types with stable memory addresses.
    /// </summary>
    private static void RegisterResidentType(TypeRegistry registry, ResidentDeclaration resident,
        string moduleName)
    {
        // Skip if already registered
        if (registry.LookupType(resident.Name) != null)
        {
            return;
        }

        // Build fields list upfront
        var fields = new List<Symbols.FieldInfo>();
        foreach (var member in resident.Members)
        {
            if (member is VariableDeclaration { Type: not null } field)
            {
                var fieldType = ResolveSimpleType(registry, field.Type);
                if (fieldType != null)
                {
                    fields.Add(new Symbols.FieldInfo(field.Name, fieldType)
                    {
                        IsMutable = field.IsMutable, Visibility = field.Visibility
                    });
                }
            }
        }

        // Resolve implemented protocols (follows clause)
        var protocols = new List<Types.TypeInfo>();
        foreach (var protoExpr in resident.Protocols)
        {
            var protoType = ResolveSimpleType(registry, protoExpr);
            if (protoType != null)
            {
                protocols.Add(protoType);
            }
        }

        var typeInfo = new Types.ResidentTypeInfo(resident.Name)
        {
            Module = moduleName,
            Visibility = resident.Visibility,
            Fields = fields,
            ImplementedProtocols = protocols,
            GenericParameters = resident.GenericParameters
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
        var cases = new List<Types.ChoiceCaseInfo>();
        foreach (var caseDecl in choice.Cases)
        {
            cases.Add(new Types.ChoiceCaseInfo(caseDecl.Name)
            {
                Value = null // TODO: Extract from caseDecl.Value expression
            });
        }

        var typeInfo = new Types.ChoiceTypeInfo(choice.Name)
        {
            Module = moduleName, Visibility = choice.Visibility, Cases = cases
        };

        registry.RegisterType(typeInfo);
    }

    /// <summary>
    /// Registers a variant type (tagged union) from stdlib.
    /// </summary>
    private static void RegisterVariantType(TypeRegistry registry, VariantDeclaration variant,
        string moduleName)
    {
        // Skip if already registered
        if (registry.LookupType(variant.Name) != null)
        {
            return;
        }

        // Build cases list upfront
        var cases = new List<Types.VariantCaseInfo>();
        int tagValue = 0;
        foreach (var caseDecl in variant.Cases)
        {
            // Determine payload type from AssociatedTypes if any
            Types.TypeInfo? payloadType = null;
            if (caseDecl.AssociatedTypes != null)
            {
                payloadType = ResolveSimpleType(registry, caseDecl.AssociatedTypes);
            }

            cases.Add(new Types.VariantCaseInfo(caseDecl.Name)
            {
                PayloadType = payloadType, TagValue = tagValue++
            });
        }

        var typeInfo = new Types.VariantTypeInfo(variant.Name)
        {
            Module = moduleName,
            Cases = cases,
            GenericParameters = variant.GenericParameters
        };

        registry.RegisterType(typeInfo);
    }

    /// <summary>
    /// Registers a protocol type from stdlib.
    /// </summary>
    private static void RegisterProtocolType(TypeRegistry registry, ProtocolDeclaration protocol,
        string moduleName)
    {
        // Skip if already registered
        if (registry.LookupType(protocol.Name) != null)
        {
            return;
        }

        // Build methods list upfront
        var methods = new List<Types.ProtocolMethodInfo>();
        foreach (var method in protocol.Methods)
        {
            var returnType = method.ReturnType != null
                ? ResolveSimpleType(registry, method.ReturnType)
                : null;

            var parameterTypes = new List<Types.TypeInfo>();
            var parameterNames = new List<string>();

            foreach (var param in method.Parameters)
            {
                var paramType = ResolveSimpleType(registry, param.Type);
                if (paramType != null)
                {
                    parameterTypes.Add(paramType);
                    parameterNames.Add(param.Name);
                }
            }

            methods.Add(new Types.ProtocolMethodInfo(method.Name)
            {
                ParameterTypes = parameterTypes,
                ParameterNames = parameterNames,
                ReturnType = returnType
            });
        }

        var typeInfo = new Types.ProtocolTypeInfo(protocol.Name)
        {
            Module = moduleName,
            Visibility = protocol.Visibility,
            Methods = methods,
            GenericParameters = protocol.GenericParameters
        };

        registry.RegisterType(typeInfo);
    }

    /// <summary>
    /// Resolves a simple type expression.
    /// Only handles intrinsic types and direct type references.
    /// </summary>
    private static Types.TypeInfo? ResolveSimpleType(TypeRegistry registry,
        TypeExpression? typeExpr)
    {
        if (typeExpr == null) return null;

        string typeName = typeExpr.Name;

        // Handle intrinsic types (@intrinsic.*)
        if (typeName.StartsWith("@intrinsic."))
        {
            return registry.LookupType(typeName);
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
            string stdlibPath = Path.Combine(exeDir, "standard");
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

                stdlibPath = Path.Combine(current, "standard");
                if (Directory.Exists(stdlibPath))
                {
                    return stdlibPath;
                }
            }
        }

        // Fallback to current directory
        return Path.Combine(Directory.GetCurrentDirectory(), "standard");
    }
}
