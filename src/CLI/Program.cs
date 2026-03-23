using System.Diagnostics;
using Compiler.Diagnostics;
using Compiler.Lexer;
using Compiler.Parser;
using SyntaxTree;
using SemanticAnalysis;
using SemanticAnalysis.Enums;
using SemanticAnalysis.Results;
using Compiler.CodeGen;
using Builder.Modules;

namespace Builder;

/// <summary>
/// Minimal CLI for testing the lexer and parser during the overhaul.
/// Analysis and CodeGen will be added back once the new type system is implemented.
/// </summary>
internal class Program
{
    /// <summary>
    /// Entry point for the RazorForge builder CLI.
    /// Dispatches to the appropriate command handler based on the first argument.
    /// Returns 0 on success or 1 on error.
    /// </summary>
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        string command = args[0].ToLowerInvariant().TrimStart('-');

        // Check if first arg is a command or a file
        bool isCommand = command == "parse" || command == "tokenize" || command == "codegen" || command == "emit" || command == "build" || command == "buildandrun" || command == "validate-stdlib" || command == "help";

        if (!isCommand)
        {
            // Default behavior: parse the file
            return ParseFile(sourceFile: args[0]);
        }

        switch (command)
        {
            case "parse":
                if (args.Length < 2)
                {
                    Console.WriteLine("Error: parse command requires a file path");
                    return 1;
                }
                return ParseFile(sourceFile: args[1]);

            case "tokenize":
                if (args.Length < 2)
                {
                    Console.WriteLine("Error: tokenize command requires a file path");
                    return 1;
                }
                return TokenizeFile(sourceFile: args[1]);

            case "codegen":
            case "emit":
                if (args.Length < 2)
                {
                    Console.WriteLine("Error: codegen command requires a file path");
                    return 1;
                }
                return GenerateCode(sourceFile: args[1], outputFile: args.Length > 2 ? args[2] : null);

            case "build":
                if (args.Length < 2)
                {
                    Console.WriteLine("Error: build command requires an entry file path");
                    return 1;
                }
                return BuildMultiFile(entryFile: args[1], outputFile: args.Length > 2 ? args[2] : null);

            case "buildandrun":
                if (args.Length < 2)
                {
                    Console.WriteLine("Error: buildandrun command requires an entry file path");
                    return 1;
                }
                return BuildAndRun(entryFile: args[1]);

            case "validate-stdlib":
            {
                string lang = args.Length >= 2 ? args[1].ToLowerInvariant() : "rf";
                Language stdlibLang = lang == "sf" || lang == "suflae" ? Language.Suflae : Language.RazorForge;
                return ValidateStdlib(stdlibLang);
            }

            case "help":
                PrintUsage();
                return 0;

            default:
                PrintUsage();
                return 1;
        }
    }

    /// <summary>
    /// Prints the CLI usage instructions to standard output.
    /// </summary>
    private static void PrintUsage()
    {
        Console.WriteLine("RazorForge Builder");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  RazorForge <source-file>                    - Parse file and show AST summary");
        Console.WriteLine("  RazorForge parse <source-file>              - Parse file and show AST summary");
        Console.WriteLine("  RazorForge tokenize <source-file>           - Tokenize file and show tokens");
        Console.WriteLine("  RazorForge codegen <source-file> [out.ll]   - Generate LLVM IR (single file)");
        Console.WriteLine("  RazorForge build <entry-file> [out.ll]      - Build multi-file project");
        Console.WriteLine("  RazorForge buildandrun <entry-file>          - Build and execute via lli");
        Console.WriteLine("  RazorForge validate-stdlib [rf|sf]           - Validate stdlib routine bodies");
        Console.WriteLine("  RazorForge help                             - Show this help");
        Console.WriteLine();
        Console.WriteLine("  <source-file>: .rf file for RazorForge or .sf file for Suflae");
    }

    /// <summary>Returns true if the given file path has a <c>.sf</c> extension (Suflae source file).</summary>
    private static bool IsSuflaeFile(string path) => path.EndsWith(".sf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tokenizes the given source file and prints each token with its position and text to standard output.
    /// Returns 0 on success or 1 if the file is not found or tokenization fails.
    /// </summary>
    private static int TokenizeFile(string sourceFile)
    {
        if (!File.Exists(sourceFile))
        {
            Console.WriteLine($"Error: File '{sourceFile}' not found.");
            return 1;
        }

        string code = File.ReadAllText(sourceFile);
        bool isSuflae = IsSuflaeFile(sourceFile);

        Console.WriteLine($"Tokenizing {sourceFile} as {(isSuflae ? "Suflae" : "RazorForge")}...");
        Console.WriteLine();

        try
        {
            var language = isSuflae ? Language.Suflae : Language.RazorForge;
            var tokenizer = new Tokenizer(code, sourceFile, language);
            List<Token> tokens = tokenizer.Tokenize();

            Console.WriteLine($"Generated {tokens.Count} tokens:");
            Console.WriteLine();

            foreach (Token tok in tokens)
            {
                Console.WriteLine($"  {tok.Line,4}:{tok.Column,-3} {tok.Type,-25} '{EscapeString(tok.Text)}'");
            }

            Console.WriteLine();
            Console.WriteLine("Tokenization successful!");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Tokenization failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Tokenizes and parses the given source file, then prints a summary of the resulting AST
    /// along with any warnings. Returns 0 on success or 1 if the file is not found or parsing fails.
    /// </summary>
    private static int ParseFile(string sourceFile)
    {
        if (!File.Exists(sourceFile))
        {
            Console.WriteLine($"Error: File '{sourceFile}' not found.");
            return 1;
        }

        string code = File.ReadAllText(sourceFile);
        bool isSuflae = IsSuflaeFile(sourceFile);

        Console.WriteLine($"Parsing {sourceFile} as {(isSuflae ? "Suflae" : "RazorForge")}...");
        Console.WriteLine();

        try
        {
            var language = isSuflae ? Language.Suflae : Language.RazorForge;

            // Tokenize
            Console.WriteLine("=== TOKENIZATION ===");
            var tokenizer = new Tokenizer(code, sourceFile, language);
            List<Token> tokens = tokenizer.Tokenize();
            Console.WriteLine($"Generated {tokens.Count} tokens");

            // Parse
            Console.WriteLine();
            Console.WriteLine("=== PARSING ===");
            var parser = new Parser(tokens: tokens, language: language, fileName: sourceFile);
            SyntaxTree.Program ast = parser.Parse();
            IReadOnlyList<BuildWarning> warnings = parser.GetWarnings();

            Console.WriteLine($"Successfully parsed! AST contains {ast.Declarations.Count} declarations");

            // Show warnings if any
            if (warnings.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"=== WARNINGS ({warnings.Count}) ===");
                foreach (var warning in warnings)
                {
                    Console.WriteLine($"  [{warning.Line}:{warning.Column}] {warning.Message}");
                }
            }

            // Show AST summary
            Console.WriteLine();
            Console.WriteLine("=== AST SUMMARY ===");
            foreach (var decl in ast.Declarations)
            {
                PrintDeclarationSummary(decl, indent: 0);
            }

            Console.WriteLine();
            Console.WriteLine("Parsing successful!");
            return 0;
        }
        catch (GrammarException ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// Runs the semantic analyzer over the standard library routine bodies for the given language
    /// and reports any errors found. Returns 0 if all bodies are valid, or 1 if errors were found.
    /// </summary>
    private static int ValidateStdlib(Language language)
    {
        try
        {
            string langName = language == Language.Suflae ? "Suflae" : "RazorForge";
            Console.WriteLine($"Validating {langName} stdlib routine bodies...");
            Console.WriteLine();

            var analyzer = new SemanticAnalyzer(language);
            var stdlibErrors = analyzer.ValidateStdlibBodies();

            if (stdlibErrors.Count == 0)
            {
                Console.WriteLine("All stdlib routine bodies validated successfully!");
                return 0;
            }

            // Group errors by file
            var errorsByFile = new Dictionary<string, List<SemanticError>>();
            foreach (var error in stdlibErrors)
            {
                string file = error.Location.FileName;
                if (!errorsByFile.TryGetValue(file, out var list))
                {
                    list = [];
                    errorsByFile[file] = list;
                }
                list.Add(error);
            }

            Console.WriteLine($"=== STDLIB VALIDATION ERRORS ({stdlibErrors.Count} errors in {errorsByFile.Count} files) ===");
            Console.WriteLine();

            foreach (var (file, errors) in errorsByFile.OrderBy(kvp => kvp.Key))
            {
                Console.WriteLine($"  {Path.GetFileName(file)} ({errors.Count} errors):");
                foreach (var error in errors)
                {
                    Console.WriteLine($"    {error.FormattedMessage}");
                }
                Console.WriteLine();
            }

            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Stdlib validation failed: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// Runs the full compiler pipeline (tokenize → parse → semantic analysis → LLVM IR generation)
    /// on the given source file and writes the resulting IR to <paramref name="outputFile"/>,
    /// or to a default <c>.ll</c> file if no output path is specified.
    /// Returns 0 on success or 1 if any stage fails.
    /// </summary>
    private static int GenerateCode(string sourceFile, string? outputFile)
    {
        if (!File.Exists(sourceFile))
        {
            Console.WriteLine($"Error: File '{sourceFile}' not found.");
            return 1;
        }

        string code = File.ReadAllText(sourceFile);
        bool isSuflae = IsSuflaeFile(sourceFile);

        Console.WriteLine($"Building {sourceFile} as {(isSuflae ? "Suflae" : "RazorForge")}...");
        Console.WriteLine();

        try
        {
            var language = isSuflae ? Language.Suflae : Language.RazorForge;

            // Tokenize
            Console.WriteLine("=== TOKENIZATION ===");
            var tokenizer = new Tokenizer(code, sourceFile, language);
            List<Token> tokens = tokenizer.Tokenize();
            Console.WriteLine($"Generated {tokens.Count} tokens");

            // Parse
            Console.WriteLine();
            Console.WriteLine("=== PARSING ===");
            var parser = new Parser(tokens: tokens, language: language, fileName: sourceFile);
            SyntaxTree.Program ast = parser.Parse();
            IReadOnlyList<BuildWarning> parseWarnings = parser.GetWarnings();

            Console.WriteLine($"Parsed {ast.Declarations.Count} declarations");

            // Semantic Analysis
            Console.WriteLine();
            Console.WriteLine("=== SEMANTIC ANALYSIS ===");

            var analyzer = new SemanticAnalyzer(language);
            var result = analyzer.Analyze(ast);

            Console.WriteLine($"Routines registered: {result.Registry.GetAllRoutines().Count()}");

            // Show errors and warnings
            if (result.Errors.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"=== ERRORS ({result.Errors.Count}) ===");
                foreach (var error in result.Errors)
                {
                    Console.WriteLine($"  {error.FormattedMessage}");
                }
                Console.WriteLine();
                Console.WriteLine("Code generation aborted due to errors.");
                return 1;
            }

            if (result.Warnings.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"=== WARNINGS ({result.Warnings.Count}) ===");
                foreach (var warning in result.Warnings)
                {
                    Console.WriteLine($"  {warning.FormattedMessage}");
                }
            }

            // Code Generation
            Console.WriteLine();
            Console.WriteLine("=== CODE GENERATION ===");

            // Pass stdlib programs to codegen so intrinsic routines get built
            var stdlibPrograms = result.Registry.StdlibPrograms;
            var generator = new LLVMCodeGenerator(ast, result.Registry, stdlibPrograms);
            string llvmIR = generator.Generate();

            // Output
            if (outputFile != null)
            {
                File.WriteAllText(outputFile, llvmIR);
                Console.WriteLine($"LLVM IR written to: {outputFile}");
            }
            else
            {
                // Default output file
                string defaultOutput = Path.ChangeExtension(sourceFile, ".ll");
                File.WriteAllText(defaultOutput, llvmIR);
                Console.WriteLine($"LLVM IR written to: {defaultOutput}");
            }

            Console.WriteLine();
            Console.WriteLine("Code generation successful!");
            return 0;
        }
        catch (GrammarException ex)
        {
            Console.WriteLine($"{ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Build failed: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// Runs the multi-file build pipeline: BuildDriver (parse + resolve imports + topo sort)
    /// → SemanticAnalyzer.AnalyzeMultiple → LLVMCodeGenerator with multiple user programs.
    /// Returns 0 on success or 1 if any stage fails.
    /// </summary>
    private static int BuildMultiFile(string entryFile, string? outputFile)
    {
        if (!File.Exists(entryFile))
        {
            Console.WriteLine($"Error: File '{entryFile}' not found.");
            return 1;
        }

        bool isSuflae = IsSuflaeFile(entryFile);
        var language = isSuflae ? Language.Suflae : Language.RazorForge;

        Console.WriteLine($"Building {entryFile} as {(isSuflae ? "Suflae" : "RazorForge")} (multi-file)...");
        Console.WriteLine();

        try
        {
            // Determine project root (directory containing the entry file) and stdlib path
            string projectRoot = Path.GetDirectoryName(Path.GetFullPath(entryFile)) ?? ".";
            string stdlibRoot = StdlibLoader.GetDefaultStdlibPath();

            // Phase 1: Parse all files and resolve dependencies
            Console.WriteLine("=== BUILD DRIVER ===");
            var driver = new BuildDriver(projectRoot, stdlibRoot, language);
            BuildResult buildResult = driver.CompileFile(Path.GetFullPath(entryFile));

            Console.WriteLine($"Parsed {buildResult.Units.Count} file(s)");

            if (buildResult.Errors.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"=== BUILD ERRORS ({buildResult.Errors.Count}) ===");
                foreach (var error in buildResult.Errors)
                {
                    Console.WriteLine($"  {error.FormattedMessage}");
                }
                Console.WriteLine();
                Console.WriteLine("Build aborted due to errors.");
                return 1;
            }

            if (buildResult.Warnings.Count > 0)
            {
                Console.WriteLine($"Warnings: {buildResult.Warnings.Count}");
                foreach (var warning in buildResult.Warnings)
                {
                    Console.WriteLine($"  [{warning.Line}:{warning.Column}] {warning.Message}");
                }
            }

            Console.WriteLine($"Initialization order: {string.Join(" → ", buildResult.InitializationOrder)}");

            // Filter out stdlib files — they are already loaded by TypeRegistry/StdlibLoader
            string normalizedStdlib = Path.GetFullPath(stdlibRoot);
            var userUnits = buildResult.Units
                .Where(u => !Path.GetFullPath(u.FilePath).StartsWith(normalizedStdlib, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Build file list in topological order
            var unitsByFile = new Dictionary<string, FileBuildUnit>(StringComparer.OrdinalIgnoreCase);
            foreach (var unit in userUnits)
            {
                unitsByFile[unit.FilePath] = unit;
            }

            // Map module names back to file units for ordering
            var unitsByModule = new Dictionary<string, FileBuildUnit>(StringComparer.OrdinalIgnoreCase);
            foreach (var unit in userUnits)
            {
                string moduleName = unit.Module ?? Path.GetFileNameWithoutExtension(unit.FilePath);
                unitsByModule[moduleName] = unit;
            }

            var orderedFiles = new List<(SyntaxTree.Program Program, string FilePath)>();
            foreach (string moduleName in buildResult.InitializationOrder)
            {
                if (unitsByModule.TryGetValue(moduleName, out var unit))
                {
                    orderedFiles.Add((unit.Ast, unit.FilePath));
                }
            }

            // Fallback: if init order doesn't cover all units (e.g., entry file with no module decl)
            foreach (var unit in userUnits)
            {
                if (!orderedFiles.Any(f => string.Equals(f.FilePath, unit.FilePath, StringComparison.OrdinalIgnoreCase)))
                {
                    orderedFiles.Add((unit.Ast, unit.FilePath));
                }
            }

            // Phase 2: Semantic analysis (multi-file)
            Console.WriteLine();
            Console.WriteLine("=== SEMANTIC ANALYSIS ===");

            var analyzer = new SemanticAnalyzer(language);
            var result = analyzer.AnalyzeMultiple(orderedFiles);

            Console.WriteLine($"Routines registered: {result.Registry.GetAllRoutines().Count()}");

            if (result.Errors.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"=== ERRORS ({result.Errors.Count}) ===");
                foreach (var error in result.Errors)
                {
                    Console.WriteLine($"  {error.FormattedMessage}");
                }
                Console.WriteLine();
                Console.WriteLine("Code generation aborted due to errors.");
                return 1;
            }

            if (result.Warnings.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"=== WARNINGS ({result.Warnings.Count}) ===");
                foreach (var warning in result.Warnings)
                {
                    Console.WriteLine($"  {warning.FormattedMessage}");
                }
            }

            // Phase 3: Code generation (multi-program)
            Console.WriteLine();
            Console.WriteLine("=== CODE GENERATION ===");

            var userPrograms = orderedFiles
                .Select(f =>
                {
                    string module = unitsByFile.TryGetValue(f.FilePath, out var u) ? u.Module ?? "" : "";
                    return (f.Program, f.FilePath, module);
                })
                .ToList();

            var stdlibPrograms = result.Registry.StdlibPrograms;
            var generator = new LLVMCodeGenerator(userPrograms, result.Registry, stdlibPrograms);
            string llvmIR = generator.Generate();

            // Output
            string outPath = outputFile ?? Path.ChangeExtension(entryFile, ".ll");
            File.WriteAllText(outPath, llvmIR);
            Console.WriteLine($"LLVM IR written to: {outPath}");

            Console.WriteLine();
            Console.WriteLine("Build successful!");
            return 0;
        }
        catch (GrammarException ex)
        {
            Console.WriteLine($"{ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Build failed: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// Builds a multi-file project and executes the resulting LLVM IR via lli.
    /// Returns 0 on success or 1 if build or execution fails.
    /// </summary>
    private static int BuildAndRun(string entryFile)
    {
        // Build first (to a temp .ll file)
        string llFile = Path.ChangeExtension(entryFile, ".ll");
        int buildResult = BuildMultiFile(entryFile: entryFile, outputFile: llFile);
        if (buildResult != 0)
        {
            return buildResult;
        }

        // Find the runtime library next to the executable
        string? exeDir = Path.GetDirectoryName(typeof(Program).Assembly.Location);
        string runtimeLib = "";
        if (exeDir != null)
        {
            // Try both naming conventions (non-lib-prefixed works with lli on Windows)
            string altPath = Path.Combine(exeDir, "razorforge_runtime.dll");
            string libPath = Path.Combine(exeDir, "librazorforge_runtime.dll");
            if (File.Exists(altPath))
                runtimeLib = altPath;
            else if (File.Exists(libPath))
                runtimeLib = libPath;
        }

        // Execute via lli
        Console.WriteLine();
        Console.WriteLine("=== EXECUTION ===");

        var lliArgs = new List<string>();
        if (!string.IsNullOrEmpty(runtimeLib))
        {
            lliArgs.Add($"--dlopen=\"{runtimeLib}\"");
        }
        lliArgs.Add($"\"{llFile}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "lli",
            Arguments = string.Join(" ", lliArgs),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                Console.WriteLine("Error: Failed to start lli.");
                return 1;
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (!string.IsNullOrEmpty(stdout))
                Console.Write(stdout);
            if (!string.IsNullOrEmpty(stderr))
                Console.Error.Write(stderr);

            if (process.ExitCode != 0)
            {
                Console.WriteLine($"lli exited with code {process.ExitCode}");
            }

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to execute lli: {ex.Message}");
            Console.WriteLine("Make sure LLVM is installed and 'lli' is on your PATH.");
            return 1;
        }
    }

    /// <summary>
    /// Recursively prints a one-line summary of an AST node to standard output,
    /// indented to the given depth. Used to display a human-readable AST overview after parsing.
    /// </summary>
    private static void PrintDeclarationSummary(IAstNode node, int indent)
    {
        string prefix = new string(' ', indent * 2);

        switch (node)
        {
            case RoutineDeclaration func:
                string funcModifiers = string.Join(" ", GetModifiers(func));
                Console.WriteLine($"{prefix}func {func.Name}({func.Parameters.Count} params) -> {func.ReturnType?.ToString() ?? "void"} {funcModifiers}".TrimEnd());
                break;

            case RecordDeclaration rec:
                string recModifiers = string.Join(" ", GetModifiers(rec));
                Console.WriteLine($"{prefix}record {rec.Name} ({rec.Members.Count} members) {recModifiers}".TrimEnd());
                break;

            case EntityDeclaration ent:
                string entModifiers = string.Join(" ", GetModifiers(ent));
                Console.WriteLine($"{prefix}entity {ent.Name} ({ent.Members.Count} members) {entModifiers}".TrimEnd());
                break;

            case ResidentDeclaration res:
                Console.WriteLine($"{prefix}resident {res.Name} ({res.Members.Count} members)");
                break;

            case ChoiceDeclaration choice:
                Console.WriteLine($"{prefix}choice {choice.Name} ({choice.Cases.Count} variants)");
                break;

            case VariantDeclaration variant:
                Console.WriteLine($"{prefix}variant {variant.Name} ({variant.Cases.Count} cases)");
                break;

            case ProtocolDeclaration proto:
                Console.WriteLine($"{prefix}protocol {proto.Name} ({proto.Methods.Count} methods)");
                break;

            case ImportDeclaration import:
                Console.WriteLine($"{prefix}import {import.ModulePath}");
                break;

            case ModuleDeclaration ns:
                Console.WriteLine($"{prefix}module {ns.Path}");
                break;

            default:
                Console.WriteLine($"{prefix}{node.GetType().Name}");
                break;
        }
    }

    /// <summary>Returns a list of modifier tokens (e.g., generic parameter lists) for a routine declaration.</summary>
    private static List<string> GetModifiers(RoutineDeclaration func)
    {
        var mods = new List<string>();
        if (func.GenericParameters?.Count > 0) mods.Add($"[{string.Join(", ", func.GenericParameters)}]");
        return mods;
    }

    /// <summary>Returns a list of modifier tokens (e.g., generic parameter lists) for a record declaration.</summary>
    private static List<string> GetModifiers(RecordDeclaration rec)
    {
        var mods = new List<string>();
        if (rec.GenericParameters?.Count > 0) mods.Add($"[{string.Join(", ", rec.GenericParameters)}]");
        return mods;
    }

    /// <summary>Returns a list of modifier tokens (e.g., generic parameter lists) for an entity declaration.</summary>
    private static List<string> GetModifiers(EntityDeclaration ent)
    {
        var mods = new List<string>();
        if (ent.GenericParameters?.Count > 0) mods.Add($"[{string.Join(", ", ent.GenericParameters)}]");
        return mods;
    }

    /// <summary>
    /// Escapes newline, carriage return, and tab characters in a string to their
    /// backslash-escaped equivalents for safe display on a single console line.
    /// </summary>
    private static string EscapeString(string s)
    {
        return s.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }
}
