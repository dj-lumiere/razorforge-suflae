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
public sealed partial class StdlibLoader
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
    private readonly Dictionary<string, List<(Program Program, string FilePath, string Module)>>
        _modulePrograms = new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>Set of already scanned directories to avoid re-scanning.</summary>
    private bool _stdlibScanned;

    /// <summary>Tracks modules that have been loaded on-demand.</summary>
    private readonly HashSet<string> _loadedModules =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the parsed Core module programs.</summary>
    public IReadOnlyList<(Program Program, string FilePath, string Module)> ParsedPrograms =>
        _corePrograms;

    /// <summary>Gets all parsed programs (core + loaded modules) for codegen.</summary>
    public IReadOnlyList<(Program Program, string FilePath, string Module)> AllLoadedPrograms
    {
        get
        {
            var all = new List<(Program, string, string)>(collection: _corePrograms);
            foreach (string mod in _loadedModules)
            {
                if (_modulePrograms.TryGetValue(key: mod,
                        value: out
                        List<(Program Program, string FilePath, string Module)>? programs))
                {
                    all.AddRange(collection: programs);
                }
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
        _fileExtension = language == Language.Suflae
            ? "*.sf"
            : "*.rf";

        // Use language-specific subdirectory
        string subdir = language == Language.Suflae
            ? "Suflae"
            : "RazorForge";
        _stdlibPath = Path.Combine(path1: stdlibRoot, path2: subdir);
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
        foreach ((Program program, string _, string ns) in _corePrograms)
        {
            foreach (IAstNode node in program.Declarations)
            {
                if (node is ProtocolDeclaration protocol)
                {
                    RegisterProtocolTypeShell(registry: registry,
                        protocol: protocol,
                        moduleName: ns);
                }
            }
        }

        // Pass 1a.1: Fill in protocol method signatures (all protocols are now registered for cross-refs)
        foreach ((Program program, string _, string _) in _corePrograms)
        {
            foreach (IAstNode node in program.Declarations)
            {
                if (node is ProtocolDeclaration protocol)
                {
                    FillProtocolMethods(registry: registry, protocol: protocol);
                }
            }
        }

        // Pass 1a.2: Resolve parent protocol hierarchies (now that all protocols are registered)
        foreach ((Program program, string _, string _) in _corePrograms)
        {
            ResolveProtocolParents(registry: registry, program: program);
        }

        // Pass 1b: Register all type shells (record, entity, choice, variant)
        foreach ((Program program, string _, string ns) in _corePrograms)
        {
            RegisterProgramTypes(registry: registry, program: program, moduleName: ns);
        }

        // Pass 1b.1: Load modules imported by Core files so their types are available
        // for member variable resolution (e.g., Set imports Collections.SortedSet).
        var importedModules = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        foreach ((Program program, string _, string _) in _corePrograms)
        {
            foreach (IAstNode decl in program.Declarations)
            {
                if (decl is ImportDeclaration import)
                {
                    // Extract top-level module name (e.g., "Collections" from "Collections.SortedSet")
                    string moduleName = import.ModulePath.Replace(oldChar: '/', newChar: '.');
                    int dotIndex = moduleName.IndexOf(value: '.');
                    if (dotIndex > 0)
                    {
                        moduleName = moduleName[..dotIndex];
                    }

                    if (!moduleName.Equals(value: "Core",
                            comparisonType: StringComparison.OrdinalIgnoreCase) &&
                        !_loadedModules.Contains(item: moduleName))
                    {
                        importedModules.Add(item: moduleName);
                    }
                }
            }
        }

        foreach (string mod in importedModules)
        {
            LoadModule(registry: registry, moduleName: mod);
        }

        // Pass 1c: Re-resolve member variables now that all types are registered.
        // The initial registration may have empty member lists due to forward references
        // (e.g., Bytes needs List which needs U64, but files are processed alphabetically).
        foreach ((Program program, string _, string ns) in _corePrograms)
        {
            ResolveProgramMemberVariables(registry: registry, program: program);
        }

        // Pass 1d: Re-resolve protocol conformances now that all types are registered.
        // Protocol arguments may reference types not yet registered during Pass 1b
        // (e.g., EnumerateIterator[T] obeys Iterable[Tuple[S64, T]] needs S64).
        foreach ((Program program, string _, string ns) in _corePrograms)
        {
            ResolveProgramProtocolConformances(registry: registry, program: program);
        }

        // Pass 2: Register all routines (now all types are available for return type resolution)
        foreach ((Program program, string _, string ns) in _corePrograms)
        {
            RegisterProgramRoutines(registry: registry, program: program, moduleName: ns);
        }

        // Pass 3: Register all presets (module-level constants accessible across files)
        foreach ((Program program, string _, string ns) in _corePrograms)
        {
            RegisterProgramPresets(registry: registry, program: program, moduleName: ns);
        }
    }

    /// <summary>
    /// Scans all stdlib files recursively and categorizes them by module.
    /// Files with "module Core" go to _corePrograms.
    /// Other modules are cached in _modulePrograms for on-demand loading.
    /// </summary>
    private void ScanStdlibFiles()
    {
        if (_stdlibScanned || !Directory.Exists(path: _stdlibPath))
        {
            return;
        }

        _stdlibScanned = true;

        // Recursively find all files with the appropriate extension
        foreach (string filePath in Directory.GetFiles(path: _stdlibPath,
                     searchPattern: _fileExtension,
                     searchOption: SearchOption.AllDirectories))
        {
            try
            {
                string code = File.ReadAllText(path: filePath);
                Program ast = ParseFile(code: code, filePath: filePath);

                // Find module declaration, or derive from directory
                string? fileModule = GetDeclaredModule(program: ast);
                fileModule ??= DeriveModuleFromPath(filePath: filePath);

                // Categorize by module
                if (fileModule.Equals(value: "Core",
                        comparisonType: StringComparison.OrdinalIgnoreCase))
                {
                    _corePrograms.Add(item: (ast, filePath, fileModule));
                }
                else
                {
                    // Cache for on-demand loading
                    if (!_modulePrograms.TryGetValue(key: fileModule,
                            value: out
                            List<(Program Program, string FilePath, string Module)>? programs))
                    {
                        programs = [];
                        _modulePrograms[key: fileModule] = programs;
                    }

                    programs.Add(item: (ast, filePath, fileModule));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    value: $"Warning: Failed to parse stdlib file {filePath}: {ex.Message}");
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
        var tokenizer = new Tokenizer(source: code, fileName: filePath, language: _language);
        List<Token> tokens = tokenizer.Tokenize();
        var parser = new Parser(tokens: tokens, language: _language, fileName: filePath);
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
        bool isSuflaeFile = filePath.EndsWith(value: ".sf",
            comparisonType: StringComparison.OrdinalIgnoreCase);
        Language language = isSuflaeFile
            ? Language.Suflae
            : Language.RazorForge;
        var tokenizer = new Tokenizer(source: code, fileName: filePath, language: language);
        List<Token> tokens = tokenizer.Tokenize();
        var parser = new Parser(tokens: tokens, language: language, fileName: filePath);
        return parser.Parse();
    }

    /// <summary>
    /// Gets the declared module from a program AST.
    /// </summary>
    private static string? GetDeclaredModule(Program program)
    {
        foreach (IAstNode node in program.Declarations)
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
            string? fileDir = Path.GetDirectoryName(path: filePath);
            if (fileDir == null)
            {
                return "Core";
            }

            string normalizedFileDir = Path.GetFullPath(path: fileDir);
            string normalizedStdlibPath = Path.GetFullPath(path: _stdlibPath);

            if (!normalizedFileDir.StartsWith(value: normalizedStdlibPath,
                    comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                return "Core";
            }

            string relativePath = normalizedFileDir[normalizedStdlibPath.Length..]
               .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.IsNullOrEmpty(value: relativePath))
            {
                return "Core";
            }

            // Convert directory separators to module path separators
            return relativePath.Replace(oldChar: Path.DirectorySeparatorChar, newChar: '/')
                               .Replace(oldChar: Path.AltDirectorySeparatorChar, newChar: '/');
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
        if (moduleName.Equals(value: "Core", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check if we have files for this module
        if (!_modulePrograms.TryGetValue(key: moduleName,
                value: out List<(Program Program, string FilePath, string Module)>? programs) ||
            programs.Count == 0)
        {
            return false;
        }

        _loadedModules.Add(item: moduleName);

        // Three-pass registration: protocols first, then other types, then routines
        // Register protocol shells across all files first, then fill in methods
        foreach ((Program program, string _, string ns) in programs)
        {
            foreach (IAstNode node in program.Declarations)
            {
                if (node is ProtocolDeclaration protocol)
                {
                    RegisterProtocolTypeShell(registry: registry,
                        protocol: protocol,
                        moduleName: ns);
                }
            }
        }

        foreach ((Program program, string _, string _) in programs)
        {
            foreach (IAstNode node in program.Declarations)
            {
                if (node is ProtocolDeclaration protocol)
                {
                    FillProtocolMethods(registry: registry, protocol: protocol);
                }
            }
        }

        foreach ((Program program, string _, string _) in programs)
        {
            ResolveProtocolParents(registry: registry, program: program);
        }

        foreach ((Program program, string _, string ns) in programs)
        {
            RegisterProgramTypes(registry: registry, program: program, moduleName: ns);
        }

        // Re-resolve member variables now that all type shells in this module are registered.
        // Initial registration may have empty member lists due to forward references
        // (e.g., Set needs SortedSet which may not be registered yet during alphabetical processing).
        foreach ((Program program, string _, string ns) in programs)
        {
            ResolveProgramMemberVariables(registry: registry, program: program);
        }

        foreach ((Program program, string _, string ns) in programs)
        {
            ResolveProgramProtocolConformances(registry: registry, program: program);
        }

        foreach ((Program program, string _, string ns) in programs)
        {
            RegisterProgramRoutines(registry: registry, program: program, moduleName: ns);
        }

        // Register presets for the module
        foreach ((Program program, string _, string ns) in programs)
        {
            RegisterProgramPresets(registry: registry, program: program, moduleName: ns);
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
            string code = File.ReadAllText(path: filePath);
            // Detect file type from extension and use appropriate parser
            Program ast = ParseFileByExtension(code: code, filePath: filePath);

            // Get module from file declaration, or derive from directory structure
            string? fileModule = GetDeclaredModule(program: ast);
            string effectiveModule = fileModule ?? DeriveModuleFromPath(filePath: filePath);

            // Track loaded program for codegen
            _loadedModules.Add(item: moduleId);
            if (!_modulePrograms.TryGetValue(key: moduleId,
                    value: out List<(Program Program, string FilePath, string Module)>? progs))
            {
                progs = [];
                _modulePrograms[key: moduleId] = progs;
            }

            progs.Add(item: (ast, filePath, effectiveModule));

            // Two-pass registration for single module
            RegisterProgramTypes(registry: registry, program: ast, moduleName: effectiveModule);
            RegisterProgramRoutines(registry: registry, program: ast, moduleName: effectiveModule);

            // Handle any imports within this module (recursive loading)
            foreach (IAstNode node in ast.Declarations)
            {
                if (node is ImportDeclaration import)
                {
                    // Recursively load imported modules
                    registry.LoadModule(importPath: import.ModulePath,
                        currentFile: filePath,
                        location: import.Location,
                        effectiveModule: out _);
                }
            }

            return effectiveModule;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                value:
                $"Warning: Failed to load module '{moduleId}' from {filePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Resolves parent protocol relationships for all protocols in a program.
    /// Must run after all protocols are registered (pass 1a) so parent lookups succeed.
    /// </summary>
    private static TypeInfo? ResolveSimpleType(TypeRegistry registry, TypeExpression? typeExpr,
        IReadOnlyList<string>? genericParams = null, string? moduleName = null)
    {
        if (typeExpr == null)
        {
            return null;
        }

        string typeName = typeExpr.Name;

        // Handle intrinsic types (@intrinsic.*)
        if (typeName.StartsWith(value: "@intrinsic."))
        {
            return registry.LookupType(name: typeName);
        }

        // Generic parameter name (T, K, V) → placeholder for substitution
        if (genericParams != null && genericParams.Contains(value: typeName))
        {
            return new GenericParameterTypeInfo(name: typeName);
        }

        // Parameterized type like List[Letter], Dict[Text, S32]
        if (typeExpr.GenericArguments is { Count: > 0 })
        {
            // Tuple types are not registered as generic definitions — handle specially
            if (typeName is "Tuple")
            {
                var elemTypes = new List<TypeInfo>();
                foreach (TypeExpression argExpr in typeExpr.GenericArguments)
                {
                    TypeInfo? argType = ResolveSimpleType(registry: registry,
                        typeExpr: argExpr,
                        genericParams: genericParams,
                        moduleName: moduleName);
                    if (argType == null)
                    {
                        return null;
                    }

                    elemTypes.Add(item: argType);
                }

                return new TupleTypeInfo(elementTypes: elemTypes);
            }

            TypeInfo? genericDef = registry.LookupType(name: typeName) ?? (moduleName != null
                ? registry.LookupType(name: $"{moduleName}.{typeName}")
                : null);
            if (genericDef is { IsGenericDefinition: true } &&
                genericDef.GenericParameters!.Count == typeExpr.GenericArguments.Count)
            {
                var typeArgs = new List<TypeInfo>();
                foreach (TypeExpression argExpr in typeExpr.GenericArguments)
                {
                    TypeInfo? argType = ResolveSimpleType(registry: registry,
                        typeExpr: argExpr,
                        genericParams: genericParams,
                        moduleName: moduleName);
                    if (argType == null)
                    {
                        return null;
                    }

                    typeArgs.Add(item: argType);
                }

                return registry.GetOrCreateResolution(genericDef: genericDef,
                    typeArguments: typeArgs);
            }
        }

        // Try to look up existing type, with module-qualified fallback
        return registry.LookupType(name: typeName) ?? (moduleName != null
            ? registry.LookupType(name: $"{moduleName}.{typeName}")
            : null);
    }

    /// <summary>
    /// Gets the default stdlib path relative to the application.
    /// </summary>
    public static string GetDefaultStdlibPath()
    {
        // Try to find standard library relative to the executable
        string? exeDir = Path.GetDirectoryName(path: typeof(StdlibLoader).Assembly.Location);
        if (exeDir != null)
        {
            string stdlibPath = Path.Combine(path1: exeDir, path2: "Standard");
            if (Directory.Exists(path: stdlibPath))
            {
                return stdlibPath;
            }

            // Try parent directories (for development)
            string? current = exeDir;
            for (int i = 0; i < 5; i++)
            {
                current = Path.GetDirectoryName(path: current);
                if (current == null)
                {
                    break;
                }

                stdlibPath = Path.Combine(path1: current, path2: "Standard");
                if (Directory.Exists(path: stdlibPath))
                {
                    return stdlibPath;
                }
            }
        }

        // Fallback to current directory
        return Path.Combine(path1: Directory.GetCurrentDirectory(), path2: "Standard");
    }

    /// <summary>
    /// Extracts the LLVM type from an @llvm("type") annotation.
    /// Returns null if no @llvm annotation is present.
    /// </summary>
    private static string? ExtractLlvmAnnotation(List<string>? annotations)
    {
        if (annotations == null)
        {
            return null;
        }

        foreach (string ann in annotations)
        {
            if (ann.StartsWith(value: "llvm(") && ann.EndsWith(value: ")"))
            {
                return ann[5..^1]
                   .Trim(trimChar: '"');
            }
        }

        return null;
    }
}
