namespace Compilers.Analysis.Modules;

using Compilers.Analysis.Enums;
using Compilers.RazorForge.Lexer;
using Compilers.RazorForge.Parser;
using Compilers.Shared.AST;
using Compilers.Shared.Lexer;

/// <summary>
/// Loads the standard library Core namespace from stdlib files.
/// Core namespace is auto-imported and reserved for stdlib use only.
/// </summary>
public sealed class StdlibLoader
{
    /// <summary>The stdlib root directory path.</summary>
    private readonly string _stdlibPath;

    /// <summary>Parsed stdlib programs with their file paths.</summary>
    private readonly List<(Program Program, string FilePath)> _parsedPrograms = new();

    /// <summary>Gets the parsed stdlib programs.</summary>
    public IReadOnlyList<(Program Program, string FilePath)> ParsedPrograms => _parsedPrograms;

    /// <summary>
    /// Creates a new stdlib loader.
    /// </summary>
    /// <param name="stdlibPath">Path to the stdlib directory.</param>
    public StdlibLoader(string stdlibPath)
    {
        _stdlibPath = stdlibPath;
    }

    /// <summary>
    /// Loads the Core namespace types into the type registry.
    /// Parses stdlib files and registers their declarations.
    /// </summary>
    /// <param name="registry">The type registry to populate.</param>
    public void LoadCoreNamespace(TypeRegistry registry)
    {
        // Parse all Core namespace files in correct order:
        // 1. Root stdlib files (protocols like Integral, BinaryFP, etc.)
        // 2. Core directory files
        // 3. NativeDataTypes (depend on protocols)
        ParseStdlibRootFiles();
        ParseCoreDirectory();
        ParseNativeDataTypes();

        // Use a simplified analysis to register just the type declarations
        // Full body analysis happens later when routines are called
        foreach (var (program, filePath) in _parsedPrograms)
        {
            RegisterProgramDeclarations(registry, program, filePath, _stdlibPath);
        }
    }

    /// <summary>
    /// Loads a specific module on-demand.
    /// Parses the module file and registers its types and routines.
    /// Uses namespace-based imports: the import path determines the namespace.
    /// </summary>
    /// <param name="registry">The type registry to populate.</param>
    /// <param name="filePath">The resolved file path of the module.</param>
    /// <param name="moduleId">The module identifier (e.g., "Collections.List") - used as namespace.</param>
    /// <returns>True if the module was loaded successfully, false on error.</returns>
    public bool LoadModule(TypeRegistry registry, string filePath, string moduleId)
    {
        try
        {
            string code = File.ReadAllText(filePath);
            var tokenizer = new RazorForgeTokenizer(code);
            List<Token> tokens = tokenizer.Tokenize();
            var parser = new RazorForgeParser(tokens, filePath);
            Program ast = parser.Parse();

            // Extract namespace from module ID (e.g., "Collections.List" -> "Collections")
            // The module ID comes from the import path, which is namespace-based
            string importNamespace = GetNamespaceFromModuleId(moduleId);

            // Register the module's declarations using the import-derived namespace
            RegisterProgramDeclarationsWithNamespace(registry, ast, importNamespace);

            // Handle any imports within this module (recursive loading)
            foreach (var node in ast.Declarations)
            {
                if (node is ImportDeclaration import)
                {
                    // Recursively load imported modules
                    registry.LoadModule(import.ModulePath, filePath, import.Location);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to load module '{moduleId}' from {filePath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Extracts the namespace from a module ID.
    /// For "Collections.List", returns "Collections".
    /// For "Collections", returns "Collections".
    /// </summary>
    private static string GetNamespaceFromModuleId(string moduleId)
    {
        int lastDot = moduleId.LastIndexOf('.');
        return lastDot > 0 ? moduleId.Substring(0, lastDot) : moduleId;
    }

    /// <summary>
    /// Registers declarations using a specified namespace (from import path).
    /// </summary>
    private static void RegisterProgramDeclarationsWithNamespace(TypeRegistry registry, Program program, string importNamespace)
    {
        // Check if file has explicit namespace declaration
        string? fileNamespace = null;
        foreach (var node in program.Declarations)
        {
            if (node is NamespaceDeclaration ns)
            {
                fileNamespace = ns.Path;
                break;
            }
        }

        // Use import-derived namespace, but respect file's namespace if it's a sub-namespace
        // e.g., import Collections/List with file declaring "Collections" -> use "Collections"
        // e.g., import Collections/List with file declaring "Collections.Internal" -> use "Collections.Internal"
        string effectiveNamespace = fileNamespace ?? importNamespace;

        // Register each type declaration
        foreach (var node in program.Declarations)
        {
            if (node is Declaration decl)
            {
                RegisterDeclaration(registry, decl, effectiveNamespace);
            }
        }
    }

    /// <summary>
    /// Parses .rf files in the stdlib root directory (protocols, etc.).
    /// </summary>
    private void ParseStdlibRootFiles()
    {
        if (!Directory.Exists(_stdlibPath))
        {
            return;
        }

        foreach (string file in Directory.GetFiles(_stdlibPath, "*.rf"))
        {
            ParseStdlibFile(file);
        }
    }

    /// <summary>
    /// Parses native data types from stdlib/NativeDataTypes/.
    /// </summary>
    private void ParseNativeDataTypes()
    {
        string nativeTypesPath = Path.Combine(_stdlibPath, "NativeDataTypes");
        if (!Directory.Exists(nativeTypesPath))
        {
            return;
        }

        foreach (string file in Directory.GetFiles(nativeTypesPath, "*.rf"))
        {
            ParseStdlibFile(file);
        }
    }

    /// <summary>
    /// Parses Core directory types from stdlib/Core/.
    /// </summary>
    private void ParseCoreDirectory()
    {
        string corePath = Path.Combine(_stdlibPath, "Core");
        if (!Directory.Exists(corePath))
        {
            return;
        }

        foreach (string file in Directory.GetFiles(corePath, "*.rf"))
        {
            ParseStdlibFile(file);
        }
    }

    /// <summary>
    /// Parses a single stdlib file.
    /// </summary>
    private void ParseStdlibFile(string filePath)
    {
        try
        {
            string code = File.ReadAllText(filePath);
            var tokenizer = new RazorForgeTokenizer(code);
            List<Token> tokens = tokenizer.Tokenize();
            var parser = new RazorForgeParser(tokens, filePath);
            Program ast = parser.Parse();
            _parsedPrograms.Add((ast, filePath));
        }
        catch (Exception ex)
        {
            // Log but don't fail - stdlib loading should be resilient
            Console.Error.WriteLine($"Warning: Failed to parse stdlib file {filePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Registers declarations from a parsed program into the type registry.
    /// Only registers type signatures, not routine bodies.
    /// </summary>
    /// <param name="registry">The type registry to register types into.</param>
    /// <param name="program">The parsed program AST.</param>
    /// <param name="filePath">The source file path.</param>
    /// <param name="stdlibPath">The stdlib root directory path.</param>
    private static void RegisterProgramDeclarations(TypeRegistry registry, Program program, string filePath, string stdlibPath)
    {
        // Get namespace if declared, otherwise derive from file directory
        string? fileNamespace = null;
        foreach (var node in program.Declarations)
        {
            if (node is NamespaceDeclaration ns)
            {
                fileNamespace = ns.Path;
                break;
            }
        }

        // If no explicit namespace, derive from directory relative to stdlib root
        fileNamespace ??= DeriveNamespaceFromPath(filePath, stdlibPath);

        // Register each type declaration
        foreach (var node in program.Declarations)
        {
            if (node is Declaration decl)
            {
                RegisterDeclaration(registry, decl, fileNamespace);
            }
        }
    }

    /// <summary>
    /// Derives a namespace from the file path relative to the stdlib root.
    /// Example: stdlib/Text/Encoding/UTF8.rf -> Text.Encoding
    /// </summary>
    private static string DeriveNamespaceFromPath(string filePath, string stdlibPath)
    {
        try
        {
            // Get the directory of the file
            string? fileDir = Path.GetDirectoryName(filePath);
            if (fileDir == null)
            {
                return "Core";
            }

            // Normalize paths for comparison
            string normalizedFileDir = Path.GetFullPath(fileDir);
            string normalizedStdlibPath = Path.GetFullPath(stdlibPath);

            // Get relative path from stdlib root
            if (!normalizedFileDir.StartsWith(normalizedStdlibPath, StringComparison.OrdinalIgnoreCase))
            {
                return "Core";
            }

            string relativePath = normalizedFileDir.Substring(normalizedStdlibPath.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.IsNullOrEmpty(relativePath))
            {
                return "Core";
            }

            // Convert directory separators to namespace dots
            // NativeDataTypes -> Core (special case for primitive types)
            if (relativePath.Equals("NativeDataTypes", StringComparison.OrdinalIgnoreCase))
            {
                return "Core";
            }

            // Replace directory separators with dots for namespace
            return relativePath.Replace(Path.DirectorySeparatorChar, '.')
                              .Replace(Path.AltDirectorySeparatorChar, '.');
        }
        catch
        {
            return "Core";
        }
    }

    /// <summary>
    /// Registers a single declaration.
    /// </summary>
    private static void RegisterDeclaration(TypeRegistry registry, Declaration decl, string? namespaceName)
    {
        switch (decl)
        {
            case RecordDeclaration record:
                RegisterRecordType(registry, record, namespaceName);
                break;

            case EntityDeclaration entity:
                RegisterEntityType(registry, entity, namespaceName);
                break;

            case ChoiceDeclaration choice:
                RegisterChoiceType(registry, choice, namespaceName);
                break;

            case VariantDeclaration variant:
                RegisterVariantType(registry, variant, namespaceName);
                break;

            case MutantDeclaration mutant:
                RegisterMutantType(registry, mutant, namespaceName);
                break;

            case ProtocolDeclaration protocol:
                RegisterProtocolType(registry, protocol, namespaceName);
                break;

            case RoutineDeclaration routine:
                RegisterRoutine(registry, routine, namespaceName);
                break;
        }
    }

    /// <summary>
    /// Registers a routine from stdlib (including type methods like S32.__add__).
    /// </summary>
    private static void RegisterRoutine(TypeRegistry registry, RoutineDeclaration routine, string? namespaceName)
    {
        // Parse method names like "S32.__add__" or "Type.method"
        string routineName = routine.Name;
        Analysis.Types.TypeInfo? ownerType = null;
        string methodName = routineName;

        int dotIndex = routineName.IndexOf('.');
        if (dotIndex > 0)
        {
            string typeName = routineName.Substring(0, dotIndex);
            methodName = routineName.Substring(dotIndex + 1); // Just the method part (e.g., "__add__")
            ownerType = registry.LookupType(typeName);
        }

        // Resolve parameter types
        var parameters = new List<Analysis.Symbols.ParameterInfo>();
        foreach (var param in routine.Parameters)
        {
            var paramType = ResolveSimpleType(registry, param.Type);
            parameters.Add(new Analysis.Symbols.ParameterInfo(param.Name, paramType ?? Analysis.Types.ErrorTypeInfo.Instance));
        }

        // Resolve return type
        var returnType = routine.ReturnType != null
            ? ResolveSimpleType(registry, routine.ReturnType)
            : null;

        // Use just the method name (not "S32.__add__", just "__add__")
        var routineInfo = new Analysis.Symbols.RoutineInfo(methodName)
        {
            OwnerType = ownerType,
            Parameters = parameters,
            ReturnType = returnType,
            Namespace = namespaceName,
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
    private static void RegisterRecordType(TypeRegistry registry, RecordDeclaration record, string? namespaceName)
    {
        // Skip if already registered
        if (registry.LookupType(record.Name) != null)
        {
            return;
        }

        // Build fields list upfront (TypeInfo uses init properties with IReadOnlyList)
        var fields = new List<Analysis.Symbols.FieldInfo>();
        foreach (var member in record.Members)
        {
            if (member is VariableDeclaration field && field.Type != null)
            {
                var fieldType = ResolveSimpleType(registry, field.Type);
                if (fieldType != null)
                {
                    fields.Add(new Analysis.Symbols.FieldInfo(field.Name, fieldType)
                    {
                        IsMutable = field.IsMutable,
                        Visibility = field.Visibility
                    });
                }
            }
        }

        // Resolve implemented protocols (follows clause)
        var protocols = new List<Analysis.Types.TypeInfo>();
        foreach (var protoExpr in record.Protocols)
        {
            var protoType = ResolveSimpleType(registry, protoExpr);
            if (protoType != null)
            {
                protocols.Add(protoType);
            }
        }

        var typeInfo = new Analysis.Types.RecordTypeInfo(record.Name)
        {
            Namespace = namespaceName ?? "Core",
            Visibility = record.Visibility,
            Fields = fields,
            ImplementedProtocols = protocols
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
    private static void RegisterEntityType(TypeRegistry registry, EntityDeclaration entity, string? namespaceName)
    {
        // Skip if already registered
        if (registry.LookupType(entity.Name) != null)
        {
            return;
        }

        // Build fields list upfront
        var fields = new List<Analysis.Symbols.FieldInfo>();
        foreach (var member in entity.Members)
        {
            if (member is VariableDeclaration field && field.Type != null)
            {
                var fieldType = ResolveSimpleType(registry, field.Type);
                if (fieldType != null)
                {
                    fields.Add(new Analysis.Symbols.FieldInfo(field.Name, fieldType)
                    {
                        IsMutable = field.IsMutable,
                        Visibility = field.Visibility
                    });
                }
            }
        }

        var typeInfo = new Analysis.Types.EntityTypeInfo(entity.Name)
        {
            Namespace = namespaceName ?? "Core",
            Visibility = entity.Visibility,
            Fields = fields
        };

        registry.RegisterType(typeInfo);
    }

    /// <summary>
    /// Registers a choice type from stdlib.
    /// </summary>
    private static void RegisterChoiceType(TypeRegistry registry, ChoiceDeclaration choice, string? namespaceName)
    {
        // Skip if already registered
        if (registry.LookupType(choice.Name) != null)
        {
            return;
        }

        // Build cases list upfront
        var cases = new List<Analysis.Types.ChoiceCaseInfo>();
        foreach (var caseDecl in choice.Cases)
        {
            cases.Add(new Analysis.Types.ChoiceCaseInfo(caseDecl.Name)
            {
                Value = caseDecl.Value.HasValue ? (int)caseDecl.Value.Value : null
            });
        }

        var typeInfo = new Analysis.Types.ChoiceTypeInfo(choice.Name)
        {
            Namespace = namespaceName ?? "Core",
            Visibility = choice.Visibility,
            Cases = cases
        };

        registry.RegisterType(typeInfo);
    }

    /// <summary>
    /// Registers a variant type (tagged union) from stdlib.
    /// </summary>
    private static void RegisterVariantType(TypeRegistry registry, VariantDeclaration variant, string? namespaceName)
    {
        // Skip if already registered
        if (registry.LookupType(variant.Name) != null)
        {
            return;
        }

        // Build cases list upfront
        var cases = new List<Analysis.Types.VariantCaseInfo>();
        int tagValue = 0;
        foreach (var caseDecl in variant.Cases)
        {
            // Determine payload type from AssociatedTypes if any
            Analysis.Types.TypeInfo? payloadType = null;
            if (caseDecl.AssociatedTypes != null)
            {
                payloadType = ResolveSimpleType(registry, caseDecl.AssociatedTypes);
            }

            cases.Add(new Analysis.Types.VariantCaseInfo(caseDecl.Name)
            {
                PayloadType = payloadType,
                TagValue = tagValue++
            });
        }

        var typeInfo = new Analysis.Types.VariantTypeInfo(variant.Name)
        {
            Namespace = namespaceName ?? "Core",
            Cases = cases
        };

        registry.RegisterType(typeInfo);
    }

    /// <summary>
    /// Registers a mutant type (untagged union) from stdlib.
    /// </summary>
    private static void RegisterMutantType(TypeRegistry registry, MutantDeclaration mutant, string? namespaceName)
    {
        // Skip if already registered
        if (registry.LookupType(mutant.Name) != null)
        {
            return;
        }

        // Build cases list upfront
        var cases = new List<Analysis.Types.VariantCaseInfo>();
        int tagValue = 0;
        foreach (var caseDecl in mutant.Cases)
        {
            // Determine payload type from AssociatedTypes if any
            Analysis.Types.TypeInfo? payloadType = null;
            if (caseDecl.AssociatedTypes != null)
            {
                payloadType = ResolveSimpleType(registry, caseDecl.AssociatedTypes);
            }

            cases.Add(new Analysis.Types.VariantCaseInfo(caseDecl.Name)
            {
                PayloadType = payloadType,
                TagValue = tagValue++
            });
        }

        var typeInfo = new Analysis.Types.MutantTypeInfo(mutant.Name)
        {
            Namespace = namespaceName ?? "Core",
            Cases = cases
        };

        registry.RegisterType(typeInfo);
    }

    /// <summary>
    /// Registers a protocol type from stdlib.
    /// </summary>
    private static void RegisterProtocolType(TypeRegistry registry, ProtocolDeclaration protocol, string? namespaceName)
    {
        // Skip if already registered
        if (registry.LookupType(protocol.Name) != null)
        {
            return;
        }

        // Build methods list upfront
        var methods = new List<Analysis.Types.ProtocolMethodInfo>();
        foreach (var method in protocol.Methods)
        {
            var returnType = method.ReturnType != null
                ? ResolveSimpleType(registry, method.ReturnType)
                : null;

            var parameterTypes = new List<Analysis.Types.TypeInfo>();
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

            methods.Add(new Analysis.Types.ProtocolMethodInfo(method.Name)
            {
                ParameterTypes = parameterTypes,
                ParameterNames = parameterNames,
                ReturnType = returnType
            });
        }

        var typeInfo = new Analysis.Types.ProtocolTypeInfo(protocol.Name)
        {
            Namespace = namespaceName ?? "Core",
            Visibility = protocol.Visibility,
            Methods = methods
        };

        registry.RegisterType(typeInfo);
    }

    /// <summary>
    /// Resolves a simple type expression.
    /// Only handles intrinsic types and direct type references.
    /// </summary>
    private static Analysis.Types.TypeInfo? ResolveSimpleType(TypeRegistry registry, TypeExpression? typeExpr)
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
        // Try to find stdlib relative to the executable
        string? exeDir = Path.GetDirectoryName(typeof(StdlibLoader).Assembly.Location);
        if (exeDir != null)
        {
            string stdlibPath = Path.Combine(exeDir, "stdlib");
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

                stdlibPath = Path.Combine(current, "stdlib");
                if (Directory.Exists(stdlibPath))
                {
                    return stdlibPath;
                }
            }
        }

        // Fallback to current directory
        return Path.Combine(Directory.GetCurrentDirectory(), "stdlib");
    }
}
